/**
 * Central config: env vars + fallback contact context. Loaded once, imported
 * everywhere — and imported FIRST by every entry file, so backend/.env is in
 * process.env before any other module reads it.
 */
import { config as loadDotenv } from 'dotenv';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

/** backend/voice_agent — resolved from this file, so it holds for src/ (tsx) and dist/ (node). */
const AGENT_DIR = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
/** backend/ — where the shared .env and (by default) the shared data dir live. */
export const BACKEND_DIR = path.dirname(AGENT_DIR);

// Shell-exported variables win over the file (dotenv never overrides).
loadDotenv({ path: path.join(BACKEND_DIR, '.env') });

const str = (name: string, fallback = ''): string => process.env[name] ?? fallback;
/** Python's `os.getenv(X) or default`: an empty value means "use the default". */
const strOr = (name: string, fallback: string): string => process.env[name] || fallback;
const num = (name: string, fallback: number): number => {
  const n = Number(process.env[name] || Number.NaN);
  return Number.isFinite(n) ? n : fallback;
};
const lower = (name: string, fallback: string): string => strOr(name, fallback).trim().toLowerCase();

// --- LiveKit ---
export const LIVEKIT_URL = str('LIVEKIT_URL');
export const LIVEKIT_API_KEY = str('LIVEKIT_API_KEY');
export const LIVEKIT_API_SECRET = str('LIVEKIT_API_SECRET');

// --- Realtime voice provider: "openai" or "gemini" ---
export const REALTIME_PROVIDER = lower('REALTIME_PROVIDER', 'openai');

// OpenAI Realtime (must be a real OpenAI key — OpenRouter has no Realtime API)
export const OPENAI_API_KEY = str('OPENAI_API_KEY');
export const REALTIME_MODEL = str('REALTIME_MODEL', 'gpt-realtime');
export const REALTIME_VOICE = str('REALTIME_VOICE', 'marin');
// Server-VAD tuning (OpenAI realtime + Gemini Live). Higher threshold rejects
// phone-line noise. VAD_SILENCE_MS is the pause that ends a turn: ~900ms keeps
// speakers who pause mid-sentence from being cut off; lower replies sooner.
export const VAD_THRESHOLD = num('VAD_THRESHOLD', 0.6);
export const VAD_SILENCE_MS = num('VAD_SILENCE_MS', 900);
export const VAD_PREFIX_MS = num('VAD_PREFIX_MS', 100);
// Input noise reduction — CRITICAL on phone/WhatsApp calls. Without it the model
// transcribes line hiss as random-language "speech" and argues with itself.
// "far_field" (speaker/handset at a distance) | "near_field" (phone to the ear) |
// "off" to disable. Default far_field is the most aggressive against line noise.
export const REALTIME_NOISE_REDUCTION = lower('REALTIME_NOISE_REDUCTION', 'far_field');

// Krisp background-voice cancellation on inbound mic audio (all transports).
// "telephony" (tuned for phone/SIP narrowband — default) | "bvc" (wideband) |
// "off". Applied through the session's room input options in agent.ts.
export const NOISE_CANCELLATION = lower('NOISE_CANCELLATION', 'telephony');

// Gemini Live native-audio
export const GOOGLE_API_KEY = str('GOOGLE_API_KEY');
export const GEMINI_MODEL = str('GEMINI_MODEL', 'gemini-2.5-flash-native-audio-preview-12-2025');
export const GEMINI_VOICE = str('GEMINI_VOICE', 'Puck');

// ElevenLabs pipeline mode (voice_provider="elevenlabs" in dispatch metadata):
// a decoupled STT->LLM->TTS pipeline instead of a realtime speech-to-speech
// model. Only the TTS voice is ElevenLabs; the LLM defaults to OpenAI. The STT
// engine is switchable per call (dispatch metadata `stt_provider`); this is the
// fallback for calls dispatched without one. "elevenlabs" (Scribe v2 Realtime)
// draws on the SAME ELEVENLABS_API_KEY/quota as TTS — watch credits.
export const ELEVENLABS_API_KEY = str('ELEVENLABS_API_KEY');
export const PIPELINE_STT_PROVIDER = lower('PIPELINE_STT_PROVIDER', 'elevenlabs');
export const PIPELINE_STT_MODEL = str('PIPELINE_STT_MODEL', 'gpt-4o-transcribe');
export const PIPELINE_LLM_MODEL = str('PIPELINE_LLM_MODEL', 'gpt-4o-mini');
// LLM brain for the cascade — "openai" | "ilmu" | "openrouter" | "gemini". The
// voice stays; only the conversation model changes. This env value is the
// FALLBACK: the live choice arrives per call as dispatch metadata
// `llm_provider`, so operators switch brains with no worker restart.
export const PIPELINE_LLM_PROVIDER = lower('PIPELINE_LLM_PROVIDER', 'openai');
export const PIPELINE_OPENROUTER_MODEL = str('PIPELINE_OPENROUTER_MODEL', 'openai/gpt-4o-mini');
export const PIPELINE_GEMINI_MODEL = str('PIPELINE_GEMINI_MODEL', 'gemini-2.5-flash');
// Cascade turn-taking: pure-silence tail before VAD calls end-of-speech, and the
// endpointing floor once the turn detector thinks the user is done (seconds).
export const PIPELINE_VAD_SILENCE_S = num('PIPELINE_VAD_SILENCE', 0.3);
export const PIPELINE_ENDPOINT_MIN_DELAY_S = num('PIPELINE_ENDPOINT_MIN_DELAY', 0.2);
// Scribe's server-VAD silence threshold (seconds). See session.ts for why it is clamped.
export const SCRIBE_VAD_SILENCE_SECS = num('SCRIBE_VAD_SILENCE_SECS', 0.35);

// YTL ILMU pipeline mode (voice_provider="ilmu"): the same cascade with every
// leg on ILMU's Malaysian OpenAI-compatible platform (data residency + native
// BM). Voices are ILMU's three stock voices — no cloning.
export const ILMU_API_KEY = str('ILMU_API_KEY');
export const ILMU_BASE_URL = str('ILMU_BASE_URL', 'https://api.ilmu.ai/v1');
export const ILMU_STT_MODEL = str('ILMU_STT_MODEL', 'ilmu-asr-v4.2');
export const ILMU_LLM_MODEL = str('ILMU_LLM_MODEL', 'ilmu-v3.1');
export const ILMU_TTS_MODEL = str('ILMU_TTS_MODEL', 'ilmu-tts-v2');
export const ILMU_TTS_VOICE = str('ILMU_TTS_VOICE', 'voice_1');

// Universal inbound-audio gate — applies to CASCADE calls on EVERY transport
// (SIP, Baileys) because it runs in the agent's sttNode, not in any gateway.
// Realtime calls use the provider's own noise reduction instead. Modes:
//   vad (default) — WebRTC VAD (a GMM speech classifier, libfvad via WASM):
//     only frames that CLASSIFY as speech open the gate, so loud non-speech
//     (TV, traffic, line noise) stays muted while quiet speech still passes.
//     If the WASM module can't load it falls back to "db" behaviour.
//   db  — the legacy energy gate: frames quieter than VOICE_GATE_DB (dBFS,
//     negative; 0 = never mute / meter-only) are muted before STT.
//   off — no gating at all (the meter still logs when VOICE_GATE_DEBUG).
// Both gating modes hold open VOICE_GATE_HOLD_MS after the last speech/loud
// frame so mid-sentence dips aren't chopped. Successor to the Baileys-only
// BAILEYS_GATE_DB (leave that at 0 so audio isn't gated twice).
export const VOICE_GATE_MODE = lower('VOICE_GATE_MODE', 'vad');
// WebRTC VAD aggressiveness 0-3: higher = stricter about calling audio "speech"
// (3 mutes the most noise but can clip soft speakers). 2 is the phone sweet spot.
export const VAD_AGGRESSIVENESS = num('VAD_AGGRESSIVENESS', 2);
export const VOICE_GATE_DB = num('VOICE_GATE_DB', 0);
export const VOICE_GATE_HOLD_MS = num('VOICE_GATE_HOLD_MS', 700);
// Per-second inbound level meter in the agent log (peak/median dBFS + how much
// the gate passed). Set VOICE_GATE_DEBUG=0 to silence it.
export const VOICE_GATE_DEBUG = !['0', 'false', 'no', 'off'].includes(lower('VOICE_GATE_DEBUG', '1'));

// Barge-in guard (both call modes): how much detected speech it takes to cut
// the agent off mid-sentence. Raise these if background voices/TV keep
// interrupting the agent (e.g. 0.8 / 3).
export const INTERRUPT_MIN_DURATION_S = num('INTERRUPT_MIN_DURATION', 0.4);
export const INTERRUPT_MIN_WORDS = num('INTERRUPT_MIN_WORDS', 2);

// Silero VAD tuning (cascade only — realtime providers do their own VAD). The
// defaults (0.5 activation, 50ms min speech) treat phone-line hiss and low
// murmurs as speech; STT then hallucinates a transcript and the LLM answers it.
export const SILERO_ACTIVATION = num('SILERO_ACTIVATION', 0.6);
export const SILERO_MIN_SPEECH_S = num('SILERO_MIN_SPEECH_S', 0.2);

// --- Summary step (plain chat completion; may use OpenRouter) ---
export const OPENROUTER_API_KEY = str('OPENROUTER_API_KEY');
export const SUMMARY_MODEL = str('SUMMARY_MODEL', 'openai/gpt-4o-mini');
// Model used for the summary when the Gemini/Google path is chosen.
export const GEMINI_SUMMARY_MODEL = str('GEMINI_SUMMARY_MODEL', 'gemini-2.5-flash');

// --- Agent dispatch name — must match on the worker and every dial site ---
export const AGENT_NAME = str('AGENT_NAME', 'chatbranch-agent');

// Hard cap on call length: failsafe hang-up if end_call never fires. 0 disables.
export const MAX_CALL_SECONDS = num('MAX_CALL_SECONDS', 300);
// Grace after the goodbye finishes "playing" before hanging up: PSTN jitter
// buffers lag local playout, so dropping instantly clips the last words.
export const HANGUP_GRACE_MS = num('HANGUP_GRACE_MS', 1500);
// Human beat after pickup before the greeting (keep within ~1-2s).
export const ANSWER_SETTLE_MS = num('ANSWER_SETTLE_MS', 1500);
// Contact-silence watchdog: re-ask after SILENCE_NUDGE_SECONDS of dead air (up
// to SILENCE_MAX_NUDGES times), hard hang-up after SILENCE_HANGUP_SECONDS.
export const SILENCE_NUDGE_SECONDS = num('SILENCE_NUDGE_SECONDS', 12);
export const SILENCE_MAX_NUDGES = num('SILENCE_MAX_NUDGES', 2);
export const SILENCE_HANGUP_SECONDS = num('SILENCE_HANGUP_SECONDS', 120);
// Silent-line greeting cadence: repeat the greeting every GREETING_RETRY_SECONDS
// up to GREETING_RETRIES times (3 greetings total), then hang up.
export const GREETING_RETRY_SECONDS = num('GREETING_RETRY_SECONDS', 3);
export const GREETING_RETRIES = num('GREETING_RETRIES', 2);

// Below this many contact words (and fewer than 3 contact turns) the transcript
// is too thin to summarize safely — the LLM would invent a conversation.
export const MIN_SUMMARY_WORDS = num('MIN_SUMMARY_WORDS', 15);

// --- Outbound phone (Twilio via LiveKit SIP) ---
export const SIP_OUTBOUND_TRUNK_ID = str('SIP_OUTBOUND_TRUNK_ID'); // set after creating the trunk
export const CALLER_ID = str('CALLER_ID'); // your Twilio number, E.164
// Only needed once, to CREATE the LiveKit outbound trunk (npm run setup-trunk):
export const TWILIO_TERMINATION_URI = str('TWILIO_TERMINATION_URI');
export const TWILIO_SIP_USERNAME = str('TWILIO_SIP_USERNAME');
export const TWILIO_SIP_PASSWORD = str('TWILIO_SIP_PASSWORD');

// --- Storage ---
// The directory this worker shares with the C# API (live transcripts, operator
// inbox, calls.json, memory, recordings). CHATBRANCH_DATA_DIR is the ONE knob
// both processes read; the default is backend/data, next to the Baileys session.
export const DATA_DIR = path.resolve(process.env.CHATBRANCH_DATA_DIR || path.join(BACKEND_DIR, 'data'));

/** Fallback contact context, used when dispatch metadata omits a field. */
export const CONTACT = {
  contact_name: 'there',
  company_name: 'our team',
  record_type: '',
  record_id: '',
  record_title: '',
  record_status: '',
  record_amount: '',
  record_due_date: '',
  record_data: {} as Record<string, unknown>,
  objective: '',
};

export interface ChatBackend {
  apiKey: string;
  baseURL?: string;
  model: string;
}

/**
 * Which chat backend generates the end-of-call summary.
 * Priority: OpenRouter -> OpenAI -> Google (Gemini's OpenAI-compatible endpoint).
 * Returns null when no key is available.
 */
export function summaryConfig(): ChatBackend | null {
  if (OPENROUTER_API_KEY) {
    return {
      apiKey: OPENROUTER_API_KEY,
      baseURL: 'https://openrouter.ai/api/v1',
      model: SUMMARY_MODEL || 'openai/gpt-4o-mini',
    };
  }
  if (OPENAI_API_KEY) {
    // OpenRouter-style "openai/gpt-4o-mini" → the bare OpenAI model name.
    return { apiKey: OPENAI_API_KEY, model: SUMMARY_MODEL ? SUMMARY_MODEL.split('/').slice(-1)[0]! : 'gpt-4o-mini' };
  }
  if (GOOGLE_API_KEY) {
    return {
      apiKey: GOOGLE_API_KEY,
      baseURL: 'https://generativelanguage.googleapis.com/v1beta/openai/',
      model: GEMINI_SUMMARY_MODEL,
    };
  }
  return null;
}

function requireAll(vars: Record<string, string>, message: (missing: string) => string): void {
  const missing = Object.entries(vars)
    .filter(([, value]) => !value)
    .map(([name]) => name);
  if (missing.length) throw new Error(message(missing.join(', ')));
}

/** Fail fast with a clear message if the worker's required env is missing. */
export function validate(): void {
  const required: Record<string, string> = {
    LIVEKIT_URL,
    LIVEKIT_API_KEY,
    LIVEKIT_API_SECRET,
  };
  if (REALTIME_PROVIDER === 'gemini') required.GOOGLE_API_KEY = GOOGLE_API_KEY;
  else if (REALTIME_PROVIDER === 'openai') required.OPENAI_API_KEY = OPENAI_API_KEY;
  else throw new Error(`REALTIME_PROVIDER must be 'openai' or 'gemini', got '${REALTIME_PROVIDER}'.`);
  requireAll(
    required,
    (missing) => `Missing required env vars: ${missing}. Copy .env.example to .env and fill them in.`,
  );
}

/** Ensure the env needed to place an outbound SIP call is present. */
export function requireSipDial(): void {
  requireAll(
    { SIP_OUTBOUND_TRUNK_ID, LIVEKIT_URL, LIVEKIT_API_KEY, LIVEKIT_API_SECRET },
    (missing) =>
      `Cannot dial — missing: ${missing}. Create the trunk with \`npm run setup-trunk\` first, then set SIP_OUTBOUND_TRUNK_ID.`,
  );
}

/** Ensure the env needed to CREATE the outbound trunk is present. */
export function requireSipSetup(): void {
  requireAll(
    { TWILIO_TERMINATION_URI, TWILIO_SIP_USERNAME, TWILIO_SIP_PASSWORD, CALLER_ID, LIVEKIT_URL, LIVEKIT_API_KEY, LIVEKIT_API_SECRET },
    (missing) => `Cannot create trunk — missing: ${missing}`,
  );
}
