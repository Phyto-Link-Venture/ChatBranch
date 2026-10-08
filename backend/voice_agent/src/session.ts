/**
 * Builds the AgentSession models for a call's chosen voice provider.
 *
 * Dispatch metadata `voice_provider` selects between a realtime speech-to-speech
 * model ("openai" | "gemini") and a decoupled STT→LLM→TTS cascade:
 * "elevenlabs" (ElevenLabs voice, OpenAI-shape brain) or "ilmu" (every leg on
 * YTL ILMU's OpenAI-compatible platform). Unset = REALTIME_PROVIDER, so
 * dispatches that never chose a voice behave exactly as before.
 */
import { EndSensitivity, StartSensitivity } from '@google/genai';
import { inference, type llm, tts, type VAD, type voice } from '@livekit/agents';
import * as elevenlabs from '@livekit/agents-plugin-elevenlabs';
import * as google from '@livekit/agents-plugin-google';
import * as openai from '@livekit/agents-plugin-openai';
import * as config from './config.js';
import { logger } from './log.js';
import type { DispatchMetadata } from './metadata.js';
import { isSupportedLanguage } from './prompts.js';
import type { Pronunciation } from './storage.js';

/** The AgentSession options this module decides; the rest are the framework's defaults. */
export type SessionModels = Pick<voice.AgentSessionOptions, 'llm' | 'stt' | 'tts' | 'vad' | 'turnHandling'>;

/** The resolved provider for a call: metadata choice, else the worker default. */
export const voiceProvider = (md: DispatchMetadata): string =>
  (md.voice_provider || config.REALTIME_PROVIDER).toLowerCase();

/** The cascade brain for a call: metadata choice (Settings → AI, read at dial time), else env. */
export const llmProvider = (md: DispatchMetadata): string =>
  (md.llm_provider || config.PIPELINE_LLM_PROVIDER).toLowerCase();

/**
 * Bias for language=auto calls: an unconstrained detector grabs ANY language on
 * short utterances — a bare "hello? hello!" came back as Russian "Алло". OpenAI-
 * shape STT (incl. ILMU ASR + realtime transcription) accepts a prompt; this
 * pins detection to Malaysia's three. The example phrases anchor the decoder's
 * vocabulary — without them short Malay lines came back mangled.
 */
export const AUTO_LANG_BIAS =
  'Malaysian phone call. The speaker speaks English, Bahasa Malaysia ' +
  '(Malay), or Mandarin Chinese — possibly code-switching between them. ' +
  'Transcribe only in those languages. Common phrases on these calls: ' +
  "'boleh dengar tak', 'saya tak faham', 'bila boleh bayar', " +
  "'berapa jumlah tertunggak', '你说什么', '我什么时候可以还钱', 'hello, who is this'.";

/** Collection-domain words misheard most over compressed phone audio. */
const DOMAIN_TERMS = ['ringgit', 'RM', 'bayar', 'bayaran', 'tertunggak', 'diskaun', 'settle', 'resit', '还钱', '付款'];

/**
 * Vocabulary prompt for OpenAI-shape STT: contact + company names and the
 * domain terms, so exactly the tokens that matter survive phone-grade audio.
 * `auto` prepends the language constraint for detect mode.
 */
export function sttDomainBias(md: DispatchMetadata, auto: boolean): string {
  const names = [md.company_name, md.contact_name].filter((t): t is string => Boolean(t));
  const vocab = `Key terms: ${[...new Set([...names, ...DOMAIN_TERMS])].join(', ')}.`;
  return auto ? `${AUTO_LANG_BIAS} ${vocab}` : `Malaysian debt-collection phone call. ${vocab}`;
}

/**
 * Scribe's keyterm biasing — the same idea in list form (Scribe has no free
 * prompt). Its realtime model accepts at most 50 terms of ≤20 characters, and
 * an out-of-spec request closes the socket (a deaf agent), so longer names are
 * dropped rather than sent.
 */
export function domainKeyterms(md: DispatchMetadata): string[] {
  const names = [md.contact_name, md.company_name].filter((t): t is string => Boolean(t));
  return [...names, 'ringgit', 'bayar', 'tertunggak', 'diskaun', 'settle'].filter((t) => t.length <= 20).slice(0, 50);
}

/**
 * OpenAI-shape transcription options (OpenAI or ILMU ASR): a hard language lock
 * plus the vocabulary prompt for a known language, auto-detect biased to our
 * three otherwise (forcing the wrong language badly garbles zh/ms).
 */
function openaiShapeStt(md: DispatchMetadata, base: { model: string; apiKey: string; baseURL?: string }): openai.STT {
  const language = md.language;
  return new openai.STT({
    ...base,
    useRealtime: false,
    ...(isSupportedLanguage(language)
      ? { language, prompt: sttDomainBias(md, false) }
      : { detectLanguage: true, prompt: sttDomainBias(md, true) }),
  });
}

/**
 * Conversation brain for the cascade. All four options speak the OpenAI
 * chat-completions shape, so only baseURL/key/model change; the voice never
 * does. A provider whose key is missing falls back to OpenAI so a config gap
 * can't silence calls.
 */
export function pipelineLlm(md: DispatchMetadata): llm.LLM {
  const provider = llmProvider(md);
  const table: Record<string, { baseURL: string; apiKey: string; model: string }> = {
    ilmu: { baseURL: config.ILMU_BASE_URL, apiKey: config.ILMU_API_KEY, model: config.ILMU_LLM_MODEL },
    openrouter: {
      baseURL: 'https://openrouter.ai/api/v1',
      apiKey: config.OPENROUTER_API_KEY,
      model: config.PIPELINE_OPENROUTER_MODEL,
    },
    gemini: {
      baseURL: 'https://generativelanguage.googleapis.com/v1beta/openai/',
      apiKey: config.GOOGLE_API_KEY,
      model: config.PIPELINE_GEMINI_MODEL,
    },
  };
  const entry = table[provider];
  if (entry?.apiKey) {
    logger().info(`Pipeline LLM: ${provider} (${entry.model})`);
    // ilmu-v3.1 is a reasoning model: "low" turns thinking OFF — a Guide
    // instruction once made it stream its internal monologue into the TTS.
    // Gemini 2.5 thinks by default and its hidden reasoning shares the output
    // budget, which can starve or delay the spoken reply. (A thinking_budget
    // extra_body 400s on Gemini's OpenAI endpoint — reasoning_effort is the knob.)
    return new openai.LLM({ ...entry, reasoningEffort: 'low' });
  }
  if (entry) logger().warn(`Pipeline LLM ${provider} has no API key — falling back to OpenAI`);
  return new openai.LLM({ model: config.PIPELINE_LLM_MODEL, apiKey: config.OPENAI_API_KEY });
}

/**
 * VAD + turn-taking shared by BOTH cascades (elevenlabs and ilmu) — one copy so
 * knob changes apply to each. Realtime providers do their own VAD.
 */
function pipelineShared(md: DispatchMetadata, vad: VAD): Pick<SessionModels, 'vad' | 'turnHandling'> {
  return {
    // Silero, loaded once per worker process with SILERO_* + PIPELINE_VAD_SILENCE
    // (prewarm in agent.ts): line hiss and low murmurs aren't treated as speech,
    // and a short silence tail ends the turn — the turn detector below is what
    // protects mid-sentence thinking pauses.
    vad,
    turnHandling: {
      // Semantic end-of-turn on the audio (on-device v1-mini model): complete
      // sentences endpoint fast and "I can pay... uh..." isn't cut off. It
      // covers en + zh but NOT ms — Malay/auto calls stay on VAD endpointing.
      turnDetection: md.language === 'en' || md.language === 'zh' ? new inference.TurnDetector({ version: 'v1-mini' }) : 'vad',
      // Endpointing floor once the detector thinks the user is done; with the
      // detector deciding "done vs still thinking" it can sit low.
      endpointing: { minDelay: config.PIPELINE_ENDPOINT_MIN_DELAY_S * 1000 },
      // Barge-in ON: interruptions OFF made the agent monologue over the caller.
      // Backchannel guard: a real utterance (>=0.4s AND >=2 words) interrupts; a
      // bare "mm-hmm" does not cut the agent mid-sentence.
      interruption: { mode: 'vad', minDuration: 400, minWords: 2 },
      // Preemptive generation: LLM starts on the PRELIMINARY transcript while
      // end-of-turn is still being confirmed — claws back most of the
      // endpointing wait (median contact-stop → agent-speak was 2.2s without it).
      preemptiveGeneration: { enabled: true },
    },
  };
}

function ilmuPipeline(md: DispatchMetadata, vad: VAD): SessionModels {
  if (!config.ILMU_API_KEY) throw new Error('voice_provider=ilmu requires ILMU_API_KEY in backend/.env.');
  const voiceId = md.voice || config.ILMU_TTS_VOICE;
  logger().info(`Voice provider: ILMU pipeline (voice=${voiceId})`);
  const ilmu = { apiKey: config.ILMU_API_KEY, baseURL: config.ILMU_BASE_URL };
  return {
    stt: openaiShapeStt(md, { ...ilmu, model: config.ILMU_STT_MODEL }),
    // reasoning "low" turns ilmu-v3.1's extended thinking OFF — required for
    // voice (leaked think-blocks get spoken) and faster.
    llm: new openai.LLM({ ...ilmu, model: config.ILMU_LLM_MODEL, reasoningEffort: 'low' }),
    // The plugin streams PCM, the only format ILMU's streaming TTS accepts.
    // ILMU's voices (voice_1/2/3) are outside OpenAI's voice union.
    tts: new openai.TTS({ ...ilmu, model: config.ILMU_TTS_MODEL, voice: voiceId as openai.TTSVoices }),
    ...pipelineShared(md, vad),
  };
}

function elevenlabsPipeline(md: DispatchMetadata, vad: VAD, pronunciation: Pronunciation): SessionModels {
  const voiceId = md.voice || '';
  if (!voiceId) {
    throw new Error("voice_provider=elevenlabs requires a 'voice' (ElevenLabs voice_id) in dispatch metadata.");
  }
  logger().info(`Voice provider: ElevenLabs pipeline (voice_id=${voiceId})`);
  const ttsOptions: elevenlabs.TTSOptions = { voiceId, apiKey: config.ELEVENLABS_API_KEY };
  if (pronunciation.dictionary) {
    ttsOptions.pronunciationDictionaryLocators = [
      {
        pronunciation_dictionary_id: pronunciation.dictionary.dictionaryId,
        version_id: pronunciation.dictionary.versionId,
      },
    ];
  }
  // The STT engine is read per call from dispatch metadata (the dashboard's
  // setting at dial time) so switching needs no worker restart.
  const language = md.language;
  let sttProvider = (md.stt_provider || config.PIPELINE_STT_PROVIDER).toLowerCase();
  if (sttProvider === 'elevenlabs' && !isSupportedLanguage(language)) {
    // language=auto: Scribe can't restrict detection to our three languages
    // (no candidate list, no bias prompt) — OpenAI STT can, so auto calls ride
    // it. Fixed-language calls keep Scribe's fast path with a hard lock.
    logger().info('STT: language=auto — OpenAI STT with language-bias prompt instead of Scribe');
    sttProvider = 'openai';
  }
  if (sttProvider === 'ilmu' && !config.ILMU_API_KEY) {
    logger().warn('STT engine ilmu selected but ILMU_API_KEY unset — using OpenAI STT');
    sttProvider = 'openai';
  }
  let stt: SessionModels['stt'];
  if (sttProvider === 'elevenlabs') {
    logger().info('STT provider: ElevenLabs Scribe v2 Realtime');
    stt = new elevenlabs.STT({
      model: 'scribe_v2_realtime',
      apiKey: config.ELEVENLABS_API_KEY,
      ...(isSupportedLanguage(language) ? { languageCode: language } : {}),
      // Bias toward the contact's real name + domain vocabulary: short phrases
      // like "I'm Beckham" are easily misheard over compressed phone audio.
      keyterms: domainKeyterms(md),
      // Keep server VAD (manual commits stalled transcripts 10-20s) but shrink
      // its silence threshold — Scribe finalization is the cascade's dominant
      // latency. CLAMPED to ElevenLabs' accepted 0.3-3.0s: out of range it does
      // not fall back, it answers 1008 and CLOSES the STT socket, so the agent
      // greets and never hears a word. A stray env value must degrade latency,
      // never take speech recognition down.
      serverVad: { vadSilenceThresholdSecs: Math.min(3.0, Math.max(0.3, config.SCRIBE_VAD_SILENCE_SECS)) },
    });
  } else if (sttProvider === 'ilmu') {
    // openai and ilmu speak the same /audio/transcriptions shape — only host +
    // model differ. ILMU's ASR is Malaysian-trained.
    logger().info(`STT provider: ILMU ASR (${config.ILMU_STT_MODEL})`);
    stt = openaiShapeStt(md, { model: config.ILMU_STT_MODEL, apiKey: config.ILMU_API_KEY, baseURL: config.ILMU_BASE_URL });
  } else {
    stt = openaiShapeStt(md, { model: config.PIPELINE_STT_MODEL, apiKey: config.OPENAI_API_KEY });
  }
  // A transient ElevenLabs failure ("no audio frames were pushed") must not
  // leave the contact hearing SILENCE for a turn: the backup is a SECOND
  // instance (fresh connection, SAME voice), so the voice never changes and the
  // happy path is untouched — the backup only runs when the primary fails.
  const voiceEngine = new tts.FallbackAdapter({
    ttsInstances: [new elevenlabs.TTS(ttsOptions), new elevenlabs.TTS(ttsOptions)],
  });
  return { stt, llm: pipelineLlm(md), tts: voiceEngine, ...pipelineShared(md, vad) };
}

function geminiRealtime(): SessionModels {
  logger().info(`Voice provider: Gemini Live (${config.GEMINI_MODEL})`);
  return {
    llm: new google.realtime.RealtimeModel({
      model: config.GEMINI_MODEL,
      voice: config.GEMINI_VOICE,
      apiKey: config.GOOGLE_API_KEY,
      temperature: 0.8,
      realtimeInputConfig: {
        automaticActivityDetection: {
          startOfSpeechSensitivity: StartSensitivity.START_SENSITIVITY_HIGH,
          endOfSpeechSensitivity: EndSensitivity.END_SENSITIVITY_HIGH,
          prefixPaddingMs: config.VAD_PREFIX_MS,
          silenceDurationMs: config.VAD_SILENCE_MS,
        },
      },
    }),
    vad: null,
    // Barge-in ON: real caller speech interrupts the agent so it listens.
    turnHandling: {
      turnDetection: 'realtime_llm',
      interruption: { mode: 'vad', minDuration: 400, minWords: 2 },
      preemptiveGeneration: { enabled: false },
    },
  };
}

function openaiRealtime(md: DispatchMetadata): SessionModels {
  const voiceId = md.voice || config.REALTIME_VOICE;
  logger().info(`Voice provider: OpenAI Realtime (${config.REALTIME_MODEL}, voice=${voiceId})`);
  const nr = config.REALTIME_NOISE_REDUCTION;
  // Noise reduction stops the model hallucinating "speech" out of line hiss.
  const noiseReduction: { type: 'near_field' | 'far_field' } | null =
    nr === 'near_field' || nr === 'far_field' ? { type: nr } : null;
  if (noiseReduction) logger().info(`OpenAI Realtime input noise reduction: ${nr}`);
  return {
    llm: new openai.realtime.RealtimeModel({
      model: config.REALTIME_MODEL,
      voice: voiceId,
      apiKey: config.OPENAI_API_KEY,
      turnDetection: {
        type: 'server_vad',
        threshold: config.VAD_THRESHOLD,
        prefix_padding_ms: 300,
        silence_duration_ms: config.VAD_SILENCE_MS,
        create_response: true,
        // Barge-in ON: the server cancels the in-progress response when the
        // caller speaks, so the agent stops and listens instead of talking over.
        interrupt_response: true,
      },
      // Without a language hint the transcription auto-detects and garbles
      // zh/ms (random-script hallucinations). `model` is REQUIRED alongside the
      // hint or input transcription silently never runs and every user turn
      // vanishes from the transcript. Auto calls get the three-language bias.
      inputAudioTranscription: isSupportedLanguage(md.language)
        ? { model: 'gpt-4o-mini-transcribe', language: md.language }
        : { model: 'gpt-4o-mini-transcribe', prompt: AUTO_LANG_BIAS },
      inputAudioNoiseReduction: noiseReduction,
    }),
    vad: null,
    turnHandling: {
      turnDetection: 'realtime_llm',
      // Backchannel guard pairs with interrupt_response above — raise
      // INTERRUPT_MIN_DURATION/_WORDS if background voices still barge in.
      interruption: {
        mode: 'vad',
        minDuration: config.INTERRUPT_MIN_DURATION_S * 1000,
        minWords: config.INTERRUPT_MIN_WORDS,
      },
      preemptiveGeneration: { enabled: false },
    },
  };
}

/** Build the session's models for this call. `vad` is the worker's prewarmed Silero instance. */
export function buildSessionModels(md: DispatchMetadata, vad: VAD, pronunciation: Pronunciation): SessionModels {
  const provider = voiceProvider(md);
  if (provider === 'ilmu') return ilmuPipeline(md, vad);
  if (provider === 'elevenlabs') return elevenlabsPipeline(md, vad, pronunciation);
  if (provider === 'gemini') return geminiRealtime();
  return openaiRealtime(md);
}
