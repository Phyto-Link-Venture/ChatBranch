/**
 * LiveKit voice agent worker: joins a dispatched room, waits for the callee to
 * answer, runs the conversation (realtime model or STT→LLM→TTS cascade), and on
 * call end generates a structured summary and saves it to calls.json.
 *
 * It shares state with the C# API through FILES only (storage.ts) — no database,
 * no HTTP. Run: `npm run dev` (development) or `npm start` (production build).
 */
import * as config from './config.js'; // first: loads backend/.env before anything reads process.env
import { cli, defineAgent, type JobContext, type JobProcess, llm, ServerOptions, type VAD, voice } from '@livekit/agents';
import * as silero from '@livekit/agents-plugin-silero';
import type { NoiseCancellationOptions } from '@livekit/rtc-node';
import { type AudioFrame, type Participant, type Room, RoomEvent } from '@livekit/rtc-node';
import path from 'node:path';
import type { ReadableStream } from 'node:stream/web';
import { TransformStream } from 'node:stream/web';
import { setTimeout as sleep } from 'node:timers/promises';
import { fileURLToPath } from 'node:url';
import { gateAudio } from './audioGate.js';
import { hangup } from './livekit.js';
import { logger } from './log.js';
import { contactFromMetadata, parseMetadata } from './metadata.js';
import { agentInstructions, openingLine, openingText } from './prompts.js';
import { CallRecorder } from './recorder.js';
import { buildSessionModels, llmProvider, type SessionModels, voiceProvider } from './session.js';
import {
  type LiveTurn,
  type LlmUsageTotals,
  loadMemory,
  loadPronunciation,
  popInstructions,
  type PronunciationTerm,
  recordingPath,
  saveCall,
  saveLive,
  saveMemory,
  operatorEnded,
} from './storage.js';
import { type CallSummary, generateSummary } from './summary.js';
import {
  callWindow,
  CARRIER_MSG_RE,
  GOODBYE_RE,
  hasEndCallMarker,
  makeThinkFilter,
  nowIso,
  scrubEndCallMarker,
  speakableStream,
} from './transcript.js';

/**
 * Who ended each call, keyed by room — written at every hangup site (end_call
 * tool / silence watchdog / max-duration failsafe / caller disconnect) and
 * folded into the final live-transcript write as `ended_by`. First cause wins.
 */
const END_CAUSE = new Map<string, string>();
const endCause = (room: string, cause: string): void => {
  // An operator's End button deletes the room, which looks like the contact
  // leaving; the API's marker file says who really pressed it.
  if (!END_CAUSE.has(room)) END_CAUSE.set(room, operatorEnded(room) ? 'operator' : cause);
};

/** Delete the room (hang up), logging instead of throwing — the room may already be closing. */
async function hangupQuietly(room: string, what: string): Promise<void> {
  try {
    await hangup(room);
  } catch (err) {
    logger().warn(`${what} failed: ${String(err)}`);
  }
}

const seconds = (ms: number): string => (ms / 1000).toFixed(2);

/**
 * Block until an outbound callee actually picks up.
 *
 * On outbound SIP the participant joins while the line is still RINGING — its
 * `sip.callStatus` reads "dialing"/"ringing" and flips to "active" on pickup.
 * Greeting before "active" plays the opening into a dead audio path ("the AI
 * says nothing after I accept the call"). The Baileys bridge publishes
 * `wa.callStatus` with the same lifecycle so it can join EARLY (warm room during
 * ringing). No attribute at all (browser tester) = greet immediately.
 */
async function awaitAnswer(room: Room, participant: Participant, timeoutMs = 60_000): Promise<string> {
  const statusKey = ['sip.callStatus', 'wa.callStatus'].find((k) => participant.attributes[k] !== undefined);
  if (!statusKey) return 'active';
  const outcome = await new Promise<string>((resolve) => {
    const timer = setTimeout(() => finish('timeout'), timeoutMs);
    function finish(status: string): void {
      clearTimeout(timer);
      room.off(RoomEvent.ParticipantAttributesChanged, onAttributes);
      resolve(status);
    }
    function check(p: Participant): void {
      const status = p.attributes[statusKey!];
      if (status === 'active' || status === 'hangup') finish(status); // picked up, or gone
    }
    function onAttributes(_changed: Record<string, string>, p: Participant): void {
      if (p.identity === participant.identity) check(p);
    }
    room.on(RoomEvent.ParticipantAttributesChanged, onAttributes);
    check(participant); // may already be "active" before we subscribed
  });
  if (outcome === 'timeout') logger().warn(`Call not answered within ${timeoutMs / 1000}s; greeting anyway`);
  else logger().info(`Call answered (${statusKey}=${outcome})`);
  // Callee declined / hung up during ringing — don't settle or greet a dead line.
  if (outcome === 'hangup') return outcome;
  // Human beat after pickup: real callers don't speak the instant the line
  // opens — give them a moment to bring the phone to their ear.
  await sleep(config.ANSWER_SETTLE_MS);
  return outcome;
}

/** Krisp noise/background-voice cancellation on inbound audio; best-effort, never blocks a call. */
async function noiseCancellation(): Promise<NoiseCancellationOptions | undefined> {
  const mode = config.NOISE_CANCELLATION;
  if (mode === 'off' || mode === 'none') return undefined;
  try {
    const nc = await import('@livekit/noise-cancellation-node');
    const options = mode === 'bvc' ? nc.BackgroundVoiceCancellation() : nc.TelephonyBackgroundVoiceCancellation();
    logger().info(`Inbound noise cancellation: ${mode}`);
    return options;
  } catch (err) {
    logger().warn(`Noise cancellation unavailable (${String(err)}) — continuing without`);
    return undefined;
  }
}

/**
 * Prewarm the cascade LLM during ringing so the FIRST real turn isn't cold
 * (~1.8s ttft on turn 1 vs ~0.7s after: the first request pays model routing on
 * top of TLS). A throwaway completion on the session's OWN llm, cut after the
 * first token. Realtime models are skipped.
 */
async function warmLlm(model: SessionModels['llm']): Promise<void> {
  if (!(model instanceof llm.LLM)) return;
  const chatCtx = llm.ChatContext.empty();
  chatCtx.addMessage({ role: 'user', content: 'hi' });
  const stream = model.chat({ chatCtx });
  try {
    await stream[Symbol.asyncIterator]().next(); // first token establishes the connection
  } finally {
    stream.close();
  }
  logger().info('LLM prewarmed');
}

/**
 * The call agent: default nodes behind three guards — the inbound speech gate
 * (sttNode), the reasoning-leak scrubber (llmNode) and the spoken-text filter
 * (ttsNode: end_call marker + pronunciation lexicon). Every transport shares
 * them because they run here.
 */
class CallAgent extends voice.Agent {
  constructor(
    instructions: string,
    tools: llm.ToolContextLike,
    private readonly lexicon: readonly PronunciationTerm[],
  ) {
    super({ instructions, tools });
  }

  override async sttNode(
    audio: ReadableStream<AudioFrame> | AsyncIterable<AudioFrame>,
    modelSettings: voice.ModelSettings,
  ) {
    return voice.Agent.default.sttNode(this, await gateAudio(audio), modelSettings);
  }

  override async llmNode(chatCtx: llm.ChatContext, toolCtx: llm.ToolContext, modelSettings: voice.ModelSettings) {
    const stream = await voice.Agent.default.llmNode(this, chatCtx, toolCtx, modelSettings);
    if (!stream) return null;
    // Internal monologue must never reach the voice.
    const scrub = makeThinkFilter();
    return stream.pipeThrough(
      new TransformStream<llm.ChatChunk | string, llm.ChatChunk | string>({
        transform(chunk, controller) {
          if (typeof chunk === 'string') {
            controller.enqueue(scrub(chunk));
            return;
          }
          const content = chunk.delta?.content;
          if (chunk.delta && content) {
            const cleaned = scrub(content);
            if (cleaned !== content) chunk.delta.content = cleaned || undefined;
          }
          controller.enqueue(chunk);
        },
      }),
    );
  }

  override async ttsNode(text: ReadableStream<string> | AsyncIterable<string>, modelSettings: voice.ModelSettings) {
    // A spoken "{end_call}" is scrubbed here too, not only from the transcript.
    return voice.Agent.default.ttsNode(this, speakableStream(text, this.lexicon), modelSettings);
  }
}

async function runCall(ctx: JobContext): Promise<void> {
  config.validate();
  await ctx.connect();

  const md = parseMetadata(ctx.job.metadata);
  const contact = contactFromMetadata(md);
  // Script-call mode: read a fixed script verbatim, then hang up — no listening
  // at all. Decided up front so session.start() can disable audio INPUT: with
  // no input there is nothing for VAD/STT/turn-detection to react to, so the
  // agent can neither auto-reply after the read nor be "interrupted" by line noise.
  const scriptText = (md.script ?? '').trim();
  const contactId = contact.contact_id;
  const memory = contactId !== undefined ? loadMemory(contactId) : null;
  const callId = ctx.room.name ?? '';
  const startedAt = nowIso();
  const transcriptLines: string[] = [];
  const liveTurns: LiveTurn[] = [];
  const seenItemIds = new Set<string>(); // turns already persisted (event or history-sync path)
  // Silence-watchdog state. `ts` = last activity from either side; only CONTACT
  // speech clears `nudges` — an agent talking to a dead line keeps escalating.
  const activity = { ts: Date.now(), nudges: 0, regreets: 0, heardUser: false, closing: false };
  let recorder: CallRecorder | null = null;
  let ttsDead = false;
  // Token accumulator for the billing ledger — persisted in the final live write
  // (llm_usage) and folded into LlmUsage API-side.
  const usage: LlmUsageTotals = { prompt: 0, completion: 0, audio_prompt: 0, audio_completion: 0, model: '', provider: '' };
  const stopped = new AbortController(); // ends the watchdog loops at shutdown
  logger().info(`Call started: ${callId} (contact=${contact.contact_name})`);

  const pronunciation = loadPronunciation();
  const models = buildSessionModels(md, ctx.proc.userData.vad as VAD, pronunciation);
  const session = new voice.AgentSession(models);
  // Warm the LLM NOW so it runs through the ring wait.
  warmLlm(models.llm).catch((err: unknown) => logger().debug(`LLM warmup skipped: ${String(err)}`));

  const persistLive = (): void => {
    try {
      saveLive(callId, liveTurns);
    } catch (err) {
      logger().warn(`save_live failed: ${String(err)}`);
    }
  };

  /** say() needs a standalone TTS; realtime models have none, so the model voices it instead. */
  const speak = (text: string, fallbackInstructions: string, allowInterruptions?: boolean): voice.SpeechHandle =>
    models.tts
      ? session.say(text, { addToChatCtx: true, allowInterruptions })
      : session.generateReply({ instructions: fallbackInstructions, allowInterruptions });

  // Capture every finalized conversation turn (both user and agent).
  session.on(voice.AgentSessionEventTypes.ConversationItemAdded, ({ item }) => {
    if (item.type !== 'message' || (item.role !== 'user' && item.role !== 'assistant')) return;
    let text = item.textContent;
    if (!text) return; // realtime audio transcripts fill in later — history sync catches them
    // Any turn means the line is live: reset the silence CLOCK, so the contact
    // gets a fresh window to answer after each agent turn.
    activity.ts = Date.now();
    if (item.role === 'user' && !activity.heardUser && CARRIER_MSG_RE.test(text)) {
      // The first "user" turn is a carrier announcement, not the callee —
      // nobody picked up. End instead of conversing with a recording.
      logger().info(`Carrier/network message detected, not a person — ending ${callId} (text=${JSON.stringify(text.slice(0, 120))})`);
      endCause(callId, 'system:no_answer');
      void hangupQuietly(callId, 'carrier-message hangup');
    } else if (item.role === 'user') {
      activity.nudges = 0;
      activity.heardUser = true; // greeting-retry no longer needed
    } else if (hasEndCallMarker(text)) {
      // The model SAID the tool name instead of invoking it — honor the
      // intent: short drain for the goodbye audio, then hang up.
      logger().info(`end_call marker in assistant text — hanging up ${callId} (text=${JSON.stringify(text.slice(0, 120))})`);
      text = scrubEndCallMarker(text);
      endCause(callId, 'agent');
      void sleep(2000).then(() => hangupQuietly(callId, 'marker hangup'));
    } else if (activity.heardUser && !activity.closing && GOODBYE_RE.test(text)) {
      // A clear closing after a real exchange, without end_call: hang up rather
      // than let the silence watchdog re-ask and re-open the call. Gated on
      // heardUser so a premature turn-1 "goodbye" can't kill a fresh call.
      activity.closing = true;
      endCause(callId, 'agent');
      logger().info(`Agent closing line — hanging up ${callId} (text=${JSON.stringify(text.slice(0, 120))})`);
      void sleep(3000).then(() => hangupQuietly(callId, 'closing hangup')); // let the goodbye play out
    }
    seenItemIds.add(item.id);
    transcriptLines.push(`${item.role}: ${text}`);
    // ts (epoch seconds) rides into the live store AND the persisted call
    // transcript — the chat UI uses it for per-turn time labels.
    liveTurns.push({ role: item.role, text, ts: Date.now() / 1000 });
    logger().info(`TURN [${item.role}]: ${text}`);
    persistLive();
  });

  // Live signal that the agent is receiving/processing the caller's audio.
  session.on(voice.AgentSessionEventTypes.UserInputTranscribed, (ev) => {
    activity.ts = Date.now();
    logger().info(`USER AUDIO transcribed: ${JSON.stringify(ev.transcript)} (final=${ev.isFinal})`);
  });

  // An unrecoverable TTS failure (quota, auth, outage) means NOTHING the agent
  // says reaches the contact — the fallback engine shares the same key. Staying
  // on the line only shows the operator a healthy "live" call over dead air.
  session.on(voice.AgentSessionEventTypes.Error, ({ error }) => {
    if (error.type !== 'tts_error' || error.recoverable || ttsDead) return;
    ttsDead = true;
    logger().error(`TTS unavailable — ending ${callId}: ${String(error.error)}`);
    endCause(callId, 'system:tts_unavailable');
    void hangupQuietly(callId, 'hangup after TTS failure');
  });

  // Per-turn stage timings ("which step is slow") and the token ledger.
  session.on(voice.AgentSessionEventTypes.MetricsCollected, ({ metrics: m }) => {
    switch (m.type) {
      case 'eou_metrics':
        logger().info(
          `LATENCY eou: end_of_utterance_delay=${seconds(m.endOfUtteranceDelayMs)}s transcription_delay=${seconds(m.transcriptionDelayMs)}s`,
        );
        break;
      case 'stt_metrics':
        logger().info(`LATENCY stt: duration=${seconds(m.durationMs)}s audio_duration=${seconds(m.audioDurationMs)}s`);
        break;
      case 'llm_metrics':
        logger().info(`LATENCY llm: ttft=${seconds(m.ttftMs)}s duration=${seconds(m.durationMs)}s`);
        usage.prompt += m.promptTokens;
        usage.completion += m.completionTokens;
        if (!usage.model) {
          // The ILMU voice runs ILMU's own brain whatever llm_provider says.
          const provider = voiceProvider(md) === 'ilmu' ? 'ilmu' : llmProvider(md);
          usage.provider = provider;
          usage.model = m.metadata?.modelName || (provider === 'ilmu' ? config.ILMU_LLM_MODEL : config.PIPELINE_LLM_MODEL);
        }
        break;
      case 'realtime_model_metrics':
        logger().info(`LATENCY realtime: ttft=${seconds(m.ttftMs)}s duration=${seconds(m.durationMs)}s`);
        // Realtime models bill text and audio tokens separately — keep them apart.
        usage.prompt += m.inputTokenDetails.textTokens;
        usage.audio_prompt += m.inputTokenDetails.audioTokens;
        usage.completion += m.outputTokenDetails.textTokens;
        usage.audio_completion += m.outputTokenDetails.audioTokens;
        if (!usage.model) {
          const gemini = voiceProvider(md) === 'gemini';
          usage.provider = gemini ? 'gemini' : 'openai';
          usage.model = m.metadata?.modelName || (gemini ? config.GEMINI_MODEL : config.REALTIME_MODEL);
        }
        break;
      case 'tts_metrics':
        logger().info(`LATENCY tts: ttfb=${seconds(m.ttfbMs)}s duration=${seconds(m.durationMs)}s`);
        break;
      default:
        break;
    }
  });

  /**
   * Fold session.history turns the event handler missed into the live store.
   * Realtime models commit items whose AUDIO transcript fills in AFTER
   * conversation_item_added fires (empty text at event time), so native-voice
   * calls would persist ZERO turns while the agent audibly spoke. History is the
   * source of truth; this adds whatever the event path skipped, keyed by item id.
   */
  const syncHistoryOnce = (): void => {
    let added = false;
    try {
      for (const item of session.history.items) {
        if (item.type !== 'message' || (item.role !== 'user' && item.role !== 'assistant')) continue;
        const text = item.textContent;
        if (!text || seenItemIds.has(item.id)) continue;
        seenItemIds.add(item.id);
        if (item.role === 'user') {
          activity.ts = Date.now();
          activity.nudges = 0;
          activity.heardUser = true;
        }
        transcriptLines.push(`${item.role}: ${text}`);
        liveTurns.push({ role: item.role, text, ts: Date.now() / 1000 });
        logger().info(`TURN [${item.role}] (history sync): ${text}`);
        added = true;
      }
    } catch (err) {
      logger().warn(`history sync failed: ${String(err)}`);
    }
    if (added) persistLive();
  };

  /**
   * Run `tick` every `everyMs` until shutdown; a tick returning true ends the
   * loop. A failing tick is logged and the loop carries on.
   */
  const every = async (
    label: string,
    everyMs: number,
    tick: () => Promise<boolean | void> | boolean | void,
  ): Promise<void> => {
    while (!stopped.signal.aborted) {
      try {
        await sleep(everyMs, undefined, { signal: stopped.signal });
      } catch {
        return; // shutdown
      }
      try {
        if ((await tick()) === true) return;
      } catch (err) {
        logger().warn(`${label} failed: ${String(err)}`);
      }
    }
  };

  // On call end: final sync, recording flush, summary, persistence.
  ctx.addShutdownCallback(async () => {
    stopped.abort();
    syncHistoryOnce(); // late-arriving realtime transcripts land in the record
    let recording: string | null = null;
    if (recorder) {
      try {
        recording = await recorder.stop(); // flush the stereo WAV
      } catch (err) {
        logger().warn(`recorder.stop failed: ${String(err)}`);
      }
    }
    const endedAt = nowIso();
    const transcript = transcriptLines.join('\n');

    let summary: CallSummary = {
      summary: '',
      outcome: 'unreachable',
      followup_date: null,
      followup_action: null,
      needs_human: false,
      objection: null,
      sentiment_score: null,
    };
    const userLines = transcriptLines
      .filter((line) => line.startsWith('user:'))
      .map((line) => line.slice('user:'.length).trim())
      .filter(Boolean);
    const userWords = userLines.reduce((n, t) => n + t.split(/\s+/).filter(Boolean).length, 0);
    // Summarize when EITHER enough words OR enough turns: a caller answering
    // "Yes." / "Next week." / "28th of July." across 5 turns is real content.
    if (userWords < config.MIN_SUMMARY_WORDS && userLines.length < 3) {
      // Near-empty transcript: an LLM would hallucinate a plausible but
      // fictional conversation from the contact/record alone.
      summary.summary = transcript.trim()
        ? 'Not enough was said to summarize (call ended almost immediately).'
        : 'No conversation captured (call ended before any speech was recorded).';
    } else {
      try {
        const result = await generateSummary(contact, transcript);
        summary = { ...summary, ...result.summary };
        if (result.usage.prompt || result.usage.completion) usage.summary = result.usage;
      } catch (err) {
        // keep the record even if summarization fails
        const message = err instanceof Error ? err.message : String(err);
        logger().error(`Summary generation failed: ${message}`);
        summary.summary = `(summary failed: ${message})`;
      }
    }

    // The prompt blocks are company context, not contact data — keep them out of calls.json.
    const { knowledge: _knowledge, skills: _skills, ...contactRecord } = contact;
    const record = {
      call_id: callId,
      started_at: startedAt,
      ended_at: endedAt,
      contact: contactRecord,
      transcript,
      recording,
      // structured turns with per-turn epoch ts (transcript above is the flat
      // string the summarizer consumed; kept for back-compat)
      turns: liveTurns,
      summary: summary.summary ?? '',
      outcome: summary.outcome ?? 'unreachable',
      followup_date: summary.followup_date ?? null,
      followup_action: summary.followup_action ?? null,
      needs_human: Boolean(summary.needs_human),
      objection: summary.objection ?? null,
      sentiment_score: summary.sentiment_score ?? null,
      // Code-computed metadata — no LLM guessing involved.
      call_executed_at: startedAt,
      executed_call_window: callWindow(startedAt),
      persona_utilized: contact.persona ?? null,
      // Needs a real signal (per-utterance ASR confidence) this codebase doesn't
      // have — left null rather than having an LLM fabricate a number.
      confidence_score: null,
    };
    try {
      saveCall(record);
    } catch (err) {
      logger().error(`save_call failed: ${String(err)}`);
    }
    try {
      // done stops the dashboard's live polling; ended_by says who hung up;
      // llm_usage carries the call's tokens into the API's billing ledger.
      const hasUsage = Boolean(usage.prompt || usage.completion || usage.audio_prompt || usage.audio_completion || usage.summary);
      saveLive(callId, liveTurns, { done: true, endedBy: END_CAUSE.get(callId), llmUsage: hasUsage ? usage : undefined });
    } catch (err) {
      logger().warn(`save_live(done) failed: ${String(err)}`);
    }
    END_CAUSE.delete(callId);

    if (contactId !== undefined) {
      try {
        saveMemory(contactId, {
          language: summary.language_used,
          bestContact: summary.best_contact,
          newNotes: Array.isArray(summary.memory_notes) ? summary.memory_notes : [],
        });
      } catch (err) {
        logger().warn(`save_memory failed: ${String(err)}`);
      }
    }
    logger().info(`==== CALL SUMMARY (${callId}) ====\n${JSON.stringify(record, null, 2)}`);
  });

  const agent = new CallAgent(
    agentInstructions(contact, memory),
    {
      end_call: llm.tool({
        description:
          'Hang up the phone call. Call this right AFTER speaking your own brief goodbye. Use it once a ' +
          'clear next step is captured — a promise-to-pay date, a refusal, a wrong-person call, or a human ' +
          'follow-up arranged.',
        execute: async (_args, { ctx: run }) => {
          // The goodbye is the model's own last utterance — generating another
          // here made the agent say farewell twice. Let it finish playing, then
          // let its tail drain over PSTN before dropping the call.
          await run.waitForPlayout().catch(() => undefined);
          await sleep(Math.max(config.HANGUP_GRACE_MS, 1200));
          endCause(callId, 'agent');
          await hangupQuietly(callId, 'delete_room on hangup');
          return 'Call ended.';
        },
      }),
    },
    pronunciation.terms,
  );

  if (scriptText) logger().info(`Script-call ${callId} — audio input disabled (no listening)`);
  await session.start({
    agent,
    room: ctx.room,
    inputOptions: scriptText ? { audioEnabled: false } : { noiseCancellation: await noiseCancellation() },
  });
  void every('history sync', 2000, syncHistoryOnce);

  // Caller hang-up: when the phone participant leaves, end the call at once
  // (summary + stop the live feed) instead of talking to a dead line.
  let hangupStarted = false;
  ctx.room.on(RoomEvent.ParticipantDisconnected, (p) => {
    if (hangupStarted) return;
    hangupStarted = true;
    endCause(callId, 'contact');
    logger().info(`Participant ${p.identity} disconnected — caller hung up, ending call ${callId}`);
    void hangupQuietly(callId, 'hangup on caller-disconnect');
  });

  // Wait until the callee is in the room — and has ANSWERED — before speaking.
  try {
    const participant = await ctx.waitForParticipant();
    logger().info(`Participant joined: ${participant.identity} — waiting for pickup`);
    if ((await awaitAnswer(ctx.room, participant)) === 'hangup') {
      // Declined during ringing — end now so the live view doesn't sit on "Ringing…".
      logger().info(`Callee hung up during ringing — ending ${callId}`);
      endCause(callId, 'contact');
      await hangupQuietly(callId, 'ring-hangup end');
      return;
    }
    // Record from pickup (not from ring) so the WAV holds only the live call.
    try {
      recorder = new CallRecorder(recordingPath(callId));
      recorder.start(ctx.room, participant);
      logger().info(`Recording call ${callId} -> ${path.basename(recordingPath(callId))}`);
    } catch (err) {
      logger().warn(`recorder start failed: ${String(err)}`); // best-effort, never block the call
    }
  } catch (err) {
    logger().warn(`wait_for_participant failed (${String(err)}); greeting anyway`);
  }

  if (scriptText) {
    // A one-way broadcast: read verbatim, no STT/LLM turn, no watchdog. Not
    // interruptible by design — line noise must not cut the read short.
    logger().info(`Script-call ${callId} — reading fixed script, no conversation`);
    await speak(
      scriptText,
      `Read the following text EXACTLY as written, word for word, in a natural voice, then stop talking: ${scriptText}`,
      false,
    ).waitForPlayout();
    if (ttsDead) return; // the error handler already hung up
    await sleep(1500); // let the audio finish draining before hangup
    endCause(callId, 'system:script_complete');
    await hangupQuietly(callId, 'script-call hangup');
    return;
  }

  // The agent speaks first. On a cascade the greeting is said VERBATIM via TTS
  // (no LLM turn: first-word latency drops from ~4s to the TTS ttfb); realtime
  // models have no standalone TTS, so the model generates the opening.
  logger().info(`Sending opening for ${callId}`);
  await speak(openingText(contact), openingLine(contact)).waitForPlayout();
  if (ttsDead) return;

  // Failsafe: hard cap on call length in case end_call never fires.
  if (config.MAX_CALL_SECONDS > 0) {
    void every('max-duration failsafe', config.MAX_CALL_SECONDS * 1000, async () => {
      logger().warn(`Max call duration (${config.MAX_CALL_SECONDS}s) reached — hanging up ${callId}`);
      endCause(callId, 'system:max_duration');
      await hangupQuietly(callId, 'failsafe hangup');
      return true;
    });
  }

  // Silence watchdog. A silent line first gets the greeting repeated (every
  // GREETING_RETRY_SECONDS, up to GREETING_RETRIES times — WhatsApp reports
  // "active" while the callee's phone is still switching into the call UI, so
  // the first greeting is often unheard), then hangs up: nobody is there. Once
  // the contact has spoken, every SILENCE_NUDGE_SECONDS of dead air RE-ASKS
  // (up to SILENCE_MAX_NUDGES times) before hanging up; SILENCE_HANGUP_SECONDS
  // is the absolute cap. Any contact speech rearms the nudge counter.
  activity.ts = Date.now(); // the clock starts at the greeting, not at job start
  const endForSilence = async (): Promise<boolean> => {
    logger().warn(`Silence — forcing hangup of ${callId} after ${activity.nudges} re-asks`);
    endCause(callId, 'system:silence');
    await hangupQuietly(callId, 'silence hangup');
    return true;
  };
  void every('silence watchdog', 1000, async () => {
    const idleS = (Date.now() - activity.ts) / 1000;
    if (!activity.heardUser && idleS >= config.GREETING_RETRY_SECONDS) {
      if (activity.regreets >= config.GREETING_RETRIES) {
        logger().info(`No response after ${activity.regreets + 1} greetings — hanging up ${callId}`);
        endCause(callId, 'system:no_answer');
        await hangupQuietly(callId, 'no-response hangup');
        return true;
      }
      activity.regreets += 1;
      activity.ts = Date.now(); // next window starts at this repeat
      logger().info(`Silent line — greeting again (${activity.regreets}/${config.GREETING_RETRIES}) on ${callId}`);
      await session
        .generateReply({
          instructions:
            "The person hasn't responded — they may not have heard you (their phone may still be " +
            'connecting). Say the SAME short greeting again, naturally — do not mention repeating yourself.',
        })
        .waitForPlayout();
      return false;
    }
    if (idleS >= config.SILENCE_HANGUP_SECONDS) return endForSilence();
    if (idleS < config.SILENCE_NUDGE_SECONDS) return false; // contact still within the window
    if (activity.nudges >= config.SILENCE_MAX_NUDGES) return endForSilence(); // re-asked enough, still nothing
    activity.nudges += 1;
    activity.ts = Date.now(); // fresh window for the contact to answer
    const last = activity.nudges >= config.SILENCE_MAX_NUDGES;
    logger().info(`Silence ${idleS.toFixed(0)}s — re-ask #${activity.nudges} for ${callId}`);
    await session
      .generateReply({
        instructions:
          'The person has gone quiet and hasn\'t replied. If the call already reached its natural end ' +
          '(goodbyes exchanged, wrong person, or a next step captured), call the end_call tool NOW. ' +
          'Otherwise ask ONE short question in their language to check they are still there and can hear you' +
          (last ? ", and say you'll have to end the call if there's no answer." : '.'),
      })
      .waitForPlayout();
    return false;
  });

  // Operator instructions sent mid-call from the dashboard (guide / take over /
  // escalate), polled from the file inbox and acted on in real time.
  void every('inbox poll', 1000, () => {
    for (const instruction of popInstructions(callId)) {
      const text = instruction.text.trim();
      if (!text) continue;
      if (instruction.mode === 'takeover') {
        speak(text, `Say this to the caller, verbatim: ${text}`); // the operator's words, verbatim
      } else {
        session.generateReply({ instructions: text }); // guide / escalate: steer the next reply
      }
    }
  });
}

export default defineAgent({
  // Silero is the cascade's VAD; its settings are worker-wide, so it loads once
  // per job process instead of on every call.
  prewarm: async (proc: JobProcess) => {
    proc.userData.vad = await silero.VAD.load({
      activationThreshold: config.SILERO_ACTIVATION,
      minSpeechDuration: config.SILERO_MIN_SPEECH_S * 1000,
      minSilenceDuration: config.PIPELINE_VAD_SILENCE_S * 1000,
    });
  },
  entry: runCall,
});

// The framework re-imports this file inside each job process; only the CLI
// invocation (`dev` / `start` / `download-files`) starts a worker.
if (path.resolve(process.argv[1] ?? '') === fileURLToPath(import.meta.url)) {
  const command = process.argv[2];
  if (command === 'dev' || command === 'start') {
    try {
      config.validate();
    } catch (err) {
      console.error(err instanceof Error ? err.message : String(err));
      process.exit(1);
    }
  }
  // agentName enables EXPLICIT dispatch: the API dispatches this worker by name.
  cli.runApp(new ServerOptions({ agent: fileURLToPath(import.meta.url), agentName: config.AGENT_NAME }));
}
