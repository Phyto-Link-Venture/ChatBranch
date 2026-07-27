"""Central config: env vars + fallback contact context. Loaded once, imported everywhere."""
import os
from pathlib import Path

from dotenv import load_dotenv

# Load .env from the backend dir (parent of voice_agent/)
ROOT = Path(__file__).resolve().parent.parent
load_dotenv(ROOT / ".env")

# --- LiveKit ---
LIVEKIT_URL = os.getenv("LIVEKIT_URL", "")
LIVEKIT_API_KEY = os.getenv("LIVEKIT_API_KEY", "")
LIVEKIT_API_SECRET = os.getenv("LIVEKIT_API_SECRET", "")

# --- Realtime voice provider: "openai" or "gemini" ---
REALTIME_PROVIDER = os.getenv("REALTIME_PROVIDER", "openai").lower()

# OpenAI Realtime (must be a real OpenAI key)
OPENAI_API_KEY = os.getenv("OPENAI_API_KEY", "")
REALTIME_MODEL = os.getenv("REALTIME_MODEL", "gpt-realtime")
REALTIME_VOICE = os.getenv("REALTIME_VOICE", "marin")
# Server-VAD tuning (OpenAI realtime). Higher threshold rejects phone-line noise;
# lower silence_ms replies sooner after you stop talking.
VAD_THRESHOLD = float(os.getenv("VAD_THRESHOLD") or 0.6)
VAD_SILENCE_MS = int(os.getenv("VAD_SILENCE_MS") or 500)
# Input noise reduction — CRITICAL on phone/WhatsApp calls. Without it the model
# transcribes line hiss as random-language "speech" and argues with itself.
# "far_field" (speaker/handset at a distance) | "near_field" (phone to the ear) |
# "off" to disable. Default far_field is the most aggressive against line noise.
REALTIME_NOISE_REDUCTION = (os.getenv("REALTIME_NOISE_REDUCTION") or "far_field").strip().lower()

# Krisp background-voice cancellation on inbound mic audio (all transports).
# "telephony" (BVCTelephony, tuned for phone/SIP narrowband — default) |
# "bvc" (wideband BVC) | "off" to disable. Applied via RoomInputOptions in
# agent.py; needs the livekit-plugins-noise-cancellation package.
NOISE_CANCELLATION = (os.getenv("NOISE_CANCELLATION") or "telephony").strip().lower()

# Gemini Live native-audio
GOOGLE_API_KEY = os.getenv("GOOGLE_API_KEY", "")
GEMINI_MODEL = os.getenv("GEMINI_MODEL", "gemini-2.5-flash-native-audio-preview-12-2025")
GEMINI_VOICE = os.getenv("GEMINI_VOICE", "Puck")
# Turn-detection tuning. Lower silence = the agent replies sooner after you stop
# talking (cuts the "waiting 4-5s" feeling). Too low = it may cut you off.
VAD_SILENCE_MS = int(os.getenv("VAD_SILENCE_MS") or 400)
VAD_PREFIX_MS = int(os.getenv("VAD_PREFIX_MS") or 100)

# ElevenLabs pipeline mode (voice_provider="elevenlabs" in dispatch metadata):
# a decoupled STT->LLM->TTS pipeline, used instead of a realtime speech-to-speech
# model. LLM stays on OpenAI (reuses OPENAI_API_KEY, zero new keys) — only the
# TTS voice is ElevenLabs. STT provider is switchable per-call from the
# Settings page (calls/voice_settings.py); this is just the fallback default
# for calls dispatched outside that path. "elevenlabs" (Scribe v2 Realtime,
# ~150ms first-partial vs OpenAI's 500-1500ms) draws on the SAME
# ELEVENLABS_API_KEY/quota as TTS -- watch credits if both are active.
ELEVENLABS_API_KEY = os.getenv("ELEVENLABS_API_KEY", "")
PIPELINE_STT_PROVIDER = os.getenv("PIPELINE_STT_PROVIDER", "elevenlabs").lower()
PIPELINE_STT_MODEL = os.getenv("PIPELINE_STT_MODEL", "gpt-4o-transcribe")
PIPELINE_LLM_MODEL = os.getenv("PIPELINE_LLM_MODEL", "gpt-4o-mini")
# LLM brain for the ElevenLabs pipeline — "openai" | "ilmu" | "openrouter" |
# "gemini". The ElevenLabs voice stays; only the conversation model changes
# (ILMU speaks Malay/Manglish natively). This env value is the FALLBACK: the
# live setting comes per call from Settings → AI (calls/voice_settings.py)
# via dispatch metadata, so operators switch brains with no worker restart.
PIPELINE_LLM_PROVIDER = os.getenv("PIPELINE_LLM_PROVIDER", "openai").lower()
PIPELINE_OPENROUTER_MODEL = os.getenv("PIPELINE_OPENROUTER_MODEL", "openai/gpt-4o-mini")
PIPELINE_GEMINI_MODEL = os.getenv("PIPELINE_GEMINI_MODEL", "gemini-2.5-flash")
# YTL ILMU pipeline mode (voice_provider="ilmu" in dispatch metadata): the
# same decoupled STT->LLM->TTS pipeline, but every leg on ILMU's Malaysian
# OpenAI-compatible platform (data residency + native BM). Voices are ILMU's
# three stock voices (voice_1 female, voice_2/voice_3 male) — no cloning.
ILMU_API_KEY = os.getenv("ILMU_API_KEY", "")
ILMU_BASE_URL = os.getenv("ILMU_BASE_URL", "https://api.ilmu.ai/v1")
ILMU_STT_MODEL = os.getenv("ILMU_STT_MODEL", "ilmu-asr-v4.2")
ILMU_LLM_MODEL = os.getenv("ILMU_LLM_MODEL", "ilmu-v3.1")
ILMU_TTS_MODEL = os.getenv("ILMU_TTS_MODEL", "ilmu-tts-v2")
ILMU_TTS_VOICE = os.getenv("ILMU_TTS_VOICE", "voice_1")

# Universal inbound-audio noise gate — applies to CASCADE calls on EVERY transport
# (SIP, Cloud bridge, Baileys) because it runs in the agent's stt_node, not in
# any one gateway. Frames quieter than VOICE_GATE_DB (dBFS, negative; 0 = off)
# are muted before STT; the gate stays open VOICE_GATE_HOLD_MS after the last
# loud frame so mid-sentence dips aren't chopped. Successor to the
# Baileys-only BAILEYS_GATE_DB (leave that at 0 so audio isn't gated twice).
# Realtime calls use OpenAI's own noise reduction instead (REALTIME_NOISE_REDUCTION).
VOICE_GATE_DB = float(os.getenv("VOICE_GATE_DB") or 0)
VOICE_GATE_HOLD_MS = int(os.getenv("VOICE_GATE_HOLD_MS") or 700)
# Per-second inbound level meter in the agent log (peak/median dBFS + how much
# the gate passed). Works on cascade calls even with the gate OFF (meter-only),
# so you can watch a real call's levels before choosing a VOICE_GATE_DB.
# Set VOICE_GATE_DEBUG=0 to silence it.
VOICE_GATE_DEBUG = (os.getenv("VOICE_GATE_DEBUG") or "1").strip().lower() \
    not in ("0", "false", "no", "off")

# Barge-in guard (both call modes): how much detected speech it takes to cut
# the agent off mid-sentence. Raise these if background voices/TV keep
# interrupting the agent — e.g. 0.8 / 3 makes short blips and backchannels
# ("mm-hmm", one loud word behind the caller) not count as an interruption.
INTERRUPT_MIN_DURATION = float(os.getenv("INTERRUPT_MIN_DURATION") or 0.4)
INTERRUPT_MIN_WORDS = int(os.getenv("INTERRUPT_MIN_WORDS") or 2)

# Silero VAD tuning (pipeline mode only — realtime providers do their own VAD).
# The defaults (0.5 activation, 50ms min speech) treat phone-line hiss and low
# murmurs as speech; STT then hallucinates a transcript and the LLM answers it.
# Raise activation / min-speech to make the pipeline harder of hearing.
SILERO_ACTIVATION = float(os.getenv("SILERO_ACTIVATION") or 0.6)
SILERO_MIN_SPEECH_S = float(os.getenv("SILERO_MIN_SPEECH_S") or 0.2)

# --- Summary step (plain chat completion; may use OpenRouter) ---
OPENROUTER_API_KEY = os.getenv("OPENROUTER_API_KEY", "")
SUMMARY_MODEL = os.getenv("SUMMARY_MODEL", "openai/gpt-4o-mini")
# Model used for the summary when the Gemini/Google path is chosen.
# NOTE: gemini-2.0-flash has a 0-request free tier; 2.5-flash has a usable free tier.
GEMINI_SUMMARY_MODEL = os.getenv("GEMINI_SUMMARY_MODEL", "gemini-2.5-flash")

# --- Agent dispatch name (used by both browser token + outbound phone dispatch) ---
AGENT_NAME = os.getenv("AGENT_NAME", "chatbranch-agent")

# Hard cap on call length. Failsafe hang-up if the agent's own end_call never fires
# (e.g. the person goes silent forever). Set 0 to disable.
MAX_CALL_SECONDS = int(os.getenv("MAX_CALL_SECONDS") or 300)

# Grace delay after the goodbye finishes "playing" before actually hanging up.
# Over PSTN there is jitter-buffer + network lag, so hanging up the instant local
# playout ends can cut off the last words. ~1.5s lets the tail drain to the phone.
HANGUP_GRACE_MS = int(os.getenv("HANGUP_GRACE_MS") or 1500)

# Below this many words, the transcript is too thin to summarize safely -- the
# LLM will invent a plausible-sounding conversation from contact_name/record
# alone rather than admit nothing happened (e.g. callee picked up, said nothing,
# hung up). Skip the LLM call and record a flat "not enough said" summary instead.
MIN_SUMMARY_WORDS = int(os.getenv("MIN_SUMMARY_WORDS") or 15)

# --- Outbound phone (Twilio via LiveKit SIP) ---
SIP_OUTBOUND_TRUNK_ID = os.getenv("SIP_OUTBOUND_TRUNK_ID", "")  # set after creating the trunk
CALLER_ID = os.getenv("CALLER_ID", "")  # your Twilio number, E.164 e.g. +1...
# Only needed once, to CREATE the LiveKit outbound trunk (setup_trunk.py):
TWILIO_TERMINATION_URI = os.getenv("TWILIO_TERMINATION_URI", "")  # e.g. your-trunk.pstn.twilio.com
TWILIO_SIP_USERNAME = os.getenv("TWILIO_SIP_USERNAME", "")
TWILIO_SIP_PASSWORD = os.getenv("TWILIO_SIP_PASSWORD", "")

# --- Token server ---
TOKEN_SERVER_PORT = int(os.getenv("TOKEN_SERVER_PORT", "8080"))
# Bind loopback by default so the (unauthenticated) test tool + call trigger are
# not reachable from the network. Set to 0.0.0.0 only behind real auth.
TOKEN_SERVER_HOST = os.getenv("TOKEN_SERVER_HOST", "127.0.0.1")
# Optional shared secret. If set, /call, /token and /calls require an X-API-Key header.
# Strongly recommended before binding to anything other than loopback.
CALL_API_KEY = os.getenv("CALL_API_KEY", "")
# Serve the built-in HTML test page at "/". Turn OFF in any non-local deployment.
ENABLE_TEST_PAGE = os.getenv("ENABLE_TEST_PAGE", "1").lower() in ("1", "true", "yes")
# CORS origins allowed to call the server (browser frontends). Comma-separated.
CORS_ORIGINS = [
    o.strip()
    for o in os.getenv(
        "CORS_ORIGINS", "http://localhost:3000,http://localhost:5173,http://localhost:8080"
    ).split(",")
    if o.strip()
]

# --- Storage ---
DATA_DIR = ROOT / "data"
CALLS_FILE = DATA_DIR / "calls.json"

# --- Fallback contact context (used when dispatch metadata omits a field) ---
CONTACT = {
    "contact_name": "there",
    "company_name": "our team",
    "record_type": "",
    "record_id": "",
    "record_title": "",
    "record_status": "",
    "record_amount": "",
    "record_due_date": "",
    "record_data": {},
    "objective": "",
}


def summary_config():
    """Pick which chat backend generates the end-of-call summary.

    Priority: OpenRouter -> OpenAI -> Google (Gemini via OpenAI-compat endpoint).
    Returns (client_kwargs, model) or (None, None) if no key is available.
    """
    if OPENROUTER_API_KEY:
        model = SUMMARY_MODEL if SUMMARY_MODEL else "openai/gpt-4o-mini"
        return {"api_key": OPENROUTER_API_KEY, "base_url": "https://openrouter.ai/api/v1"}, model
    if OPENAI_API_KEY:
        model = SUMMARY_MODEL.split("/", 1)[-1] if SUMMARY_MODEL else "gpt-4o-mini"
        return {"api_key": OPENAI_API_KEY}, model
    if GOOGLE_API_KEY:
        # Gemini exposes an OpenAI-compatible endpoint, so the same client works.
        return (
            {
                "api_key": GOOGLE_API_KEY,
                "base_url": "https://generativelanguage.googleapis.com/v1beta/openai/",
            },
            GEMINI_SUMMARY_MODEL,
        )
    return None, None


def validate():
    """Fail fast with a clear message if required env is missing."""
    required = {
        "LIVEKIT_URL": LIVEKIT_URL,
        "LIVEKIT_API_KEY": LIVEKIT_API_KEY,
        "LIVEKIT_API_SECRET": LIVEKIT_API_SECRET,
    }
    if REALTIME_PROVIDER == "gemini":
        required["GOOGLE_API_KEY"] = GOOGLE_API_KEY
    elif REALTIME_PROVIDER == "openai":
        required["OPENAI_API_KEY"] = OPENAI_API_KEY
    else:
        raise RuntimeError(
            f"REALTIME_PROVIDER must be 'openai' or 'gemini', got '{REALTIME_PROVIDER}'."
        )

    missing = [name for name, val in required.items() if not val]
    if missing:
        raise RuntimeError(
            "Missing required env vars: "
            + ", ".join(missing)
            + ". Copy .env.example to .env and fill them in."
        )


def require_sip_dial():
    """Ensure env needed to place an outbound call is present."""
    missing = [
        n
        for n, v in {
            "SIP_OUTBOUND_TRUNK_ID": SIP_OUTBOUND_TRUNK_ID,
            "LIVEKIT_URL": LIVEKIT_URL,
            "LIVEKIT_API_KEY": LIVEKIT_API_KEY,
            "LIVEKIT_API_SECRET": LIVEKIT_API_SECRET,
        }.items()
        if not v
    ]
    if missing:
        raise RuntimeError(
            "Cannot dial — missing: "
            + ", ".join(missing)
            + ". Create the trunk with setup_trunk.py first, then set SIP_OUTBOUND_TRUNK_ID."
        )


def require_sip_setup():
    """Ensure env needed to CREATE the outbound trunk is present."""
    missing = [
        n
        for n, v in {
            "TWILIO_TERMINATION_URI": TWILIO_TERMINATION_URI,
            "TWILIO_SIP_USERNAME": TWILIO_SIP_USERNAME,
            "TWILIO_SIP_PASSWORD": TWILIO_SIP_PASSWORD,
            "CALLER_ID": CALLER_ID,
        }.items()
        if not v
    ]
    if missing:
        raise RuntimeError("Cannot create trunk — missing: " + ", ".join(missing))
