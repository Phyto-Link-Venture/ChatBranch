"""LiveKit voice agent: joins a room, runs the OpenAI Realtime model, and on call
end generates a structured summary and saves it to data/calls.json.

Run:  python voice_agent/agent.py dev  (or `python agent.py dev` from voice_agent/)
"""
import asyncio
import json
import logging
import os
import re
import math
import time
import sys
from datetime import datetime, timezone

from openai import AsyncOpenAI

from livekit import api as lk_api
from livekit.agents import (
    NOT_GIVEN,
    APIError,
    Agent,
    AgentSession,
    ConversationItemAddedEvent,
    JobContext,
    MetricsCollectedEvent,
    RoomInputOptions,
    RunContext,
    WorkerOptions,
    cli,
    function_tool,
    get_job_context,
)
from livekit.agents import tts as agent_tts
from livekit.agents.llm import ChatMessage
from livekit.agents.metrics import EOUMetrics, LLMMetrics, RealtimeModelMetrics, STTMetrics, TTSMetrics
from livekit.plugins import elevenlabs as lk_elevenlabs
from livekit.plugins import google as lk_google
from livekit.plugins import openai as lk_openai
from livekit.plugins import silero
from livekit.plugins.turn_detector.multilingual import MultilingualModel

# Krisp background-noise / background-voice cancellation on the INBOUND mic
# audio (before STT/VAD/model) — cleans phone-line hiss and background talkers
# on every call transport (SIP + WhatsApp), which the ElevenLabs cascade
# otherwise has no noise reduction for (the OpenAI Realtime path has its own).
# Optional dependency: if the plugin isn't installed the agent still runs.
try:
    from livekit.plugins import noise_cancellation as _noise_cancellation
except ImportError:
    _noise_cancellation = None
from openai.types.beta.realtime.session import InputAudioTranscription, TurnDetection
from google.genai import types as genai_types

# livekit-agents' CLI logging uses a lazy rich.Console singleton. On Windows,
# when stdout is a pipe (e.g. `concurrently` in `npm run dev:all`), rich still
# detects "legacy windows" and routes through a raw WriteConsole path that
# hard-encodes to cp1252 -- any Chinese/Malay non-Latin1 log content (call
# transcripts, contact names) raises UnicodeEncodeError and cascades into
# repeated "Logging error" spam that buries real output. Force rich off that
# path; a piped stream isn't a tty anyway so this doesn't add color codes.
if sys.platform == "win32":
    import rich.console as _rich_console
    _rich_console.detect_legacy_windows = lambda: False
    # With the legacy-console path disabled, rich/print() now write through
    # normal sys.stdout/stderr -- but a piped Windows stream can still report
    # its encoding as cp1252, which silently mangles curly quotes/CJK into
    # "�" (visible as the corrupted You've -> "You�ve" in logs)
    # instead of erroring. Force real UTF-8 so it renders correctly.
    for _stream_name in ("stdout", "stderr"):
        _stream = getattr(sys, _stream_name)
        if hasattr(_stream, "reconfigure"):
            _stream.reconfigure(encoding="utf-8", errors="replace")

import config
from prompts import SUPPORTED_LANGUAGES, agent_instructions, opening_line, opening_text, summary_prompt
from storage import save_call, save_live, pop_instructions
from recorder import CallRecorder

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger("repayment-agent")


def _now() -> str:
    return datetime.now(timezone.utc).isoformat()


def _call_window(iso_ts: str) -> str:
    """Bucket a call's timestamp into a coarse time-of-day label. Heuristic on
    UTC hour (no per-tenant timezone concept exists yet) — good enough for
    reporting, not a precise local-time classification."""
    try:
        hour = datetime.fromisoformat(iso_ts).hour
    except (ValueError, TypeError):
        return "unknown"
    if hour < 9:
        return "before_work"
    if hour < 12 or (14 <= hour < 18):
        return "working_hour"
    if hour < 14:
        return "lunch_time"
    if hour < 21:
        return "after_work"
    return "before_10pm"


def _parse_metadata(ctx: JobContext) -> dict:
    """Parse the agent dispatch metadata (contact info + voice selection)."""
    try:
        return json.loads(ctx.job.metadata) if ctx.job.metadata else {}
    except (json.JSONDecodeError, AttributeError) as e:
        logger.warning("Could not parse job metadata: %s", e)
        return {}


async def _generate_summary(contact: dict, transcript: str) -> dict:
    """Call a chat model (OpenRouter / OpenAI / Gemini) to extract the summary."""
    kwargs, model = config.summary_config()
    if kwargs is None:
        raise RuntimeError(
            "No summary key set (need one of OPENROUTER_API_KEY / OPENAI_API_KEY / GOOGLE_API_KEY)."
        )
    client = AsyncOpenAI(**kwargs)
    resp = await client.chat.completions.create(
        model=model,
        messages=[{"role": "user", "content": summary_prompt(contact, transcript)}],
        response_format={"type": "json_object"},
        temperature=0.2,
    )
    return json.loads(resp.choices[0].message.content)


_CONTACT_META_KEYS = (
    "contact_name", "company_name", "language", "memory", "objective", "tags",
    "record_type", "record_id", "record_title", "record_status",
    "record_amount", "record_currency", "record_due_date", "record_data",
)


def _contact_from_metadata(md: dict) -> dict:
    """Start from the fallback contact context, override with dispatch metadata."""
    contact = dict(config.CONTACT)
    for key in _CONTACT_META_KEYS:
        if md.get(key) is not None:
            contact[key] = md[key]
    if md.get("phone"):
        contact["phone"] = md["phone"]
    if md.get("contact_id") is not None:
        contact["contact_id"] = md["contact_id"]
    return contact


def _memory_path(contact_id):
    return config.DATA_DIR / "memory" / f"{contact_id}.md"


def _parse_memory_md(text: str) -> dict:
    """Same lean frontmatter shape as calls/memory.py -- duplicated here (not
    imported) because this process stays Django-free, same reasoning as
    _pronunciation_locator above. preferred_voice/preferred_voice_provider are
    Django-side-only fields (this process never sets them) but MUST round-trip
    unchanged through parse -> _save_memory -> render, or a call here wipes the
    contact's remembered voice the next time Django writes/reads this file."""
    memory = {"language": None, "best_contact": None, "preferred_voice": None,
              "preferred_voice_provider": None, "updated": None, "notes": []}
    in_frontmatter = False
    in_notes = False
    for line in text.splitlines():
        stripped = line.strip()
        if stripped == "---":
            in_frontmatter = not in_frontmatter
            continue
        if in_frontmatter:
            if ":" in stripped:
                key, _, value = stripped.partition(":")
                key = key.strip()
                if key in memory:
                    memory[key] = value.strip() or None
            continue
        if stripped == "## Notes":
            in_notes = True
            continue
        if in_notes and stripped.startswith("- "):
            memory["notes"].append(stripped[2:].strip())
    return memory


def _render_memory_md(memory: dict) -> str:
    lines = ["---"]
    if memory.get("language"):
        lines.append(f"language: {memory['language']}")
    if memory.get("best_contact"):
        lines.append(f"best_contact: {memory['best_contact']}")
    if memory.get("preferred_voice"):
        lines.append(f"preferred_voice: {memory['preferred_voice']}")
    if memory.get("preferred_voice_provider"):
        lines.append(f"preferred_voice_provider: {memory['preferred_voice_provider']}")
    if memory.get("updated"):
        lines.append(f"updated: {memory['updated']}")
    lines.append("---")
    notes = memory.get("notes") or []
    if notes:
        lines.append("")
        lines.append("## Notes")
        lines.extend(f"- {n}" for n in notes)
    return "\n".join(lines) + "\n"


def _load_memory(contact_id) -> dict | None:
    """Read backend/data/memory/{contact_id}.md, written/read by calls.memory
    on the Django side -- cross-process file IPC, same pattern as pronunciation."""
    try:
        with open(_memory_path(contact_id), "r", encoding="utf-8") as f:
            return _parse_memory_md(f.read())
    except FileNotFoundError:
        return None


def _save_memory(contact_id, *, language: str | None, best_contact: str | None, new_notes: list[str]) -> None:
    import os
    memory = _load_memory(contact_id) or {"language": None, "best_contact": None, "preferred_voice": None,
                                           "preferred_voice_provider": None, "updated": None, "notes": []}
    if language:
        memory["language"] = language
    if best_contact:
        memory["best_contact"] = best_contact
    memory["updated"] = _now()[:10]
    for note in new_notes:
        note = note.strip()
        if note and note not in memory["notes"]:
            memory["notes"].append(note)
    memory["notes"] = memory["notes"][-20:]

    path = _memory_path(contact_id)
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = str(path) + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        f.write(_render_memory_md(memory))
    os.replace(tmp, path)


def _pronunciation_locator() -> lk_elevenlabs.PronunciationDictionaryLocator | None:
    """Read the pronunciation dictionary saved via Settings -> Pronunciation
    (backend/data/pronunciation.json, written by calls.pronunciation) -- the
    voice agent is a separate process, so this reads the shared file rather
    than importing the Django app, same IPC pattern as the rest of the app."""
    try:
        with open(config.DATA_DIR / "pronunciation.json", "r", encoding="utf-8") as f:
            state = json.load(f)
    except (FileNotFoundError, json.JSONDecodeError):
        return None
    dictionary_id, version_id = state.get("dictionary_id"), state.get("version_id")
    if not dictionary_id or not version_id:
        return None
    return lk_elevenlabs.PronunciationDictionaryLocator(pronunciation_dictionary_id=dictionary_id, version_id=version_id)


def _pipeline_llm(md: dict):
    """Conversation brain for the ElevenLabs pipeline. The provider comes per
    call from dispatch metadata (Settings → AI selector, read at dial time —
    same live-wire as stt_provider), falling back to PIPELINE_LLM_PROVIDER.
    All four options speak the OpenAI chat-completions shape, so only
    base_url/key/model change; the voice never does. A provider whose key is
    missing falls back to OpenAI so a config gap can't silence calls."""
    provider = (md.get("llm_provider") or config.PIPELINE_LLM_PROVIDER).lower()
    table = {
        "ilmu": (config.ILMU_BASE_URL, config.ILMU_API_KEY, config.ILMU_LLM_MODEL),
        "openrouter": ("https://openrouter.ai/api/v1", config.OPENROUTER_API_KEY,
                       config.PIPELINE_OPENROUTER_MODEL),
        "gemini": ("https://generativelanguage.googleapis.com/v1beta/openai/",
                   config.GOOGLE_API_KEY, config.PIPELINE_GEMINI_MODEL),
    }
    entry = table.get(provider)
    if entry and entry[1]:
        base_url, api_key, model = entry
        logger.info("Pipeline LLM: %s (%s)", provider, model)
        extra = {}
        if provider == "ilmu":
            # ilmu-v3.1 is a reasoning model; "low" turns thinking OFF (ILMU
            # docs). Without this a Guide instruction once made it stream its
            # whole internal monologue (Chinese deliberation + </think>) into
            # the TTS — the contact heard the model thinking. Also faster.
            extra["reasoning_effort"] = "low"
        return lk_openai.LLM(model=model, api_key=api_key, base_url=base_url, **extra)
    if entry:
        logger.warning("Pipeline LLM %s has no API key — falling back to OpenAI", provider)
    return lk_openai.LLM(model=config.PIPELINE_LLM_MODEL, api_key=config.OPENAI_API_KEY)


def _pipeline_shared_kwargs(call_language: str | None) -> dict:
    """VAD + turn-taking tuning shared by BOTH cascade pipelines (elevenlabs
    and ilmu) — one copy so knob changes apply to each. Realtime providers do
    their own VAD and never touch this."""
    kwargs = {
        # Preemptive generation: the LLM + TTS start on the PRELIMINARY
        # transcript while end-of-turn is still being confirmed, then the
        # result is either used (typical) or discarded (false endpoint).
        # Audio-measured 2026-07-21: median contact-stop -> agent-speak was
        # 2.2s without it; this claws back most of the endpointing wait.
        "preemptive_generation": True,
        # activation/min-speech raised via SILERO_* (config.py) so line
        # hiss and low murmurs aren't treated as speech (STT hallucinates
        # a transcript otherwise). min_silence_duration: 0.55 default ->
        # 0.3s. Pure-silence tail before VAD calls end-of-speech; the
        # semantic turn detector below is what protects mid-sentence
        # thinking pauses from being cut off.
        "vad": silero.VAD.load(
            activation_threshold=config.SILERO_ACTIVATION,
            min_speech_duration=config.SILERO_MIN_SPEECH_S,
            min_silence_duration=float(
                os.getenv("PIPELINE_VAD_SILENCE") or 0.3),
        ),
        "turn_handling": {
            # Endpointing floor once the turn detector thinks the user is
            # done (default 0.5s). With the semantic model deciding "done
            # vs still thinking", the floor can sit lower without clipping.
            "endpointing": {"min_delay": float(
                os.getenv("PIPELINE_ENDPOINT_MIN_DELAY") or 0.2)},
            # Barge-in ON (reverted 2026-07-22): interruptions OFF made the
            # agent monologue straight over the caller and never listen — the
            # contact spoke several times but only one fragment was heard
            # (field case). Backchannel guard: a real utterance (>=0.4s AND
            # >=2 words) interrupts; a bare "mm-hmm"/"okay" does not cut the
            # agent mid-sentence.
            "interruption": {"min_duration": 0.4, "min_words": 2},
        },
    }
    # Semantic end-of-turn model: reads the live transcript and predicts
    # whether the speaker is actually done (vs pausing mid-thought), so
    # complete sentences endpoint fast and "I can pay... uh..." isn't cut
    # off. The multilingual model covers en + zh but NOT ms -- Malay calls
    # stay on plain VAD endpointing.
    if call_language in ("en", "zh"):
        kwargs["turn_handling"]["turn_detection"] = MultilingualModel()
    return kwargs


# Bias for language=auto calls: an unconstrained detector grabs ANY language
# on short utterances — a bare "hello? hello!" transcribed as Russian "Алло"
# on a real call. OpenAI-shape STT (incl. ILMU ASR + the realtime
# transcription) accepts a prompt; this pins detection to Malaysia's three.
# The example phrases anchor the decoder's vocabulary — without them short
# Malay lines came back mangled ("boleh dengar tak" -> "oleh delta"), which
# then wrecked the LLM's language detection downstream.
_AUTO_LANG_BIAS = ("Malaysian phone call. The speaker speaks English, Bahasa Malaysia "
                   "(Malay), or Mandarin Chinese — possibly code-switching between them. "
                   "Transcribe only in those languages. Common phrases on these calls: "
                   "'boleh dengar tak', 'saya tak faham', 'bila boleh bayar', "
                   "'berapa jumlah tertunggak', '你说什么', '我什么时候可以还钱', 'hello, who is this'.")

# Collection-domain words misheard most over compressed phone audio — biasing
# the recognizer toward these is the cheapest accuracy win there is.
_DOMAIN_TERMS = ["ringgit", "RM", "bayar", "bayaran", "tertunggak", "diskaun",
                 "settle", "resit", "还钱", "付款"]


def _stt_domain_bias(md: dict, auto: bool) -> str:
    """Vocabulary prompt for OpenAI-shape STT (gpt-4o-transcribe + ILMU ASR):
    contact + company names and the domain terms, so exactly the tokens that
    matter survive phone-grade audio. auto=True prepends the language
    constraint for detect mode."""
    terms = [t for t in (md.get("company_name"), md.get("contact_name")) if t]
    vocab = "Key terms: " + ", ".join(dict.fromkeys(terms + _DOMAIN_TERMS)) + "."
    if auto:
        return _AUTO_LANG_BIAS + " " + vocab
    return "Malaysian debt-collection phone call. " + vocab


def _domain_keyterms(md: dict) -> list[str]:
    """Scribe's keyterm biasing — same idea in list form (Scribe has no free
    prompt, only keyterms)."""
    names = [t for t in (md.get("contact_name"), md.get("company_name")) if t]
    return names + ["ringgit", "bayar", "tertunggak", "diskaun", "settle"]


def _build_session_kwargs(md: dict) -> dict:
    """Build the AgentSession kwargs for the call's chosen voice provider.

    `md["voice_provider"]` (from dial-time dispatch metadata, Task 1) selects
    between a realtime speech-to-speech model (llm=RealtimeModel — OpenAI or
    Gemini, unchanged from before) and a decoupled STT->LLM->TTS cascade:
    "elevenlabs" (ElevenLabs STT/TTS + OpenAI LLM) or "ilmu" (every leg on
    YTL ILMU's OpenAI-compatible platform — api.ilmu.ai). Falls back to the
    REALTIME_PROVIDER env var when a call doesn't specify voice_provider, so
    existing dispatches behave exactly as before.
    """
    provider = md.get("voice_provider") or config.REALTIME_PROVIDER

    if provider == "ilmu":
        if not config.ILMU_API_KEY:
            raise RuntimeError("voice_provider=ilmu requires ILMU_API_KEY in backend/.env.")
        voice = md.get("voice") or config.ILMU_TTS_VOICE
        call_language = md.get("language")
        logger.info("Voice provider: ILMU pipeline (voice=%s)", voice)
        stt_kwargs = {"model": config.ILMU_STT_MODEL, "api_key": config.ILMU_API_KEY,
                      "base_url": config.ILMU_BASE_URL}
        if call_language in SUPPORTED_LANGUAGES:
            stt_kwargs["language"] = call_language
            stt_kwargs["prompt"] = _stt_domain_bias(md, auto=False)
        else:
            stt_kwargs["detect_language"] = True
            stt_kwargs["prompt"] = _stt_domain_bias(md, auto=True)
        return {
            "stt": lk_openai.STT(**stt_kwargs),
            # reasoning_effort="low" turns ilmu-v3.1's extended thinking OFF —
            # required for voice (leaked think-blocks get spoken) + faster
            "llm": lk_openai.LLM(model=config.ILMU_LLM_MODEL, api_key=config.ILMU_API_KEY,
                                 base_url=config.ILMU_BASE_URL, reasoning_effort="low"),
            # response_format MUST be "pcm": the plugin's streaming request is
            # rejected 400 (tts.streaming_format_unsupported) for every other
            # format — the agent then goes silent while the transcript still
            # logs text. ILMU streams pcm at 24kHz, matching the plugin.
            "tts": lk_openai.TTS(model=config.ILMU_TTS_MODEL, voice=voice,
                                 api_key=config.ILMU_API_KEY, base_url=config.ILMU_BASE_URL,
                                 response_format="pcm"),
            **_pipeline_shared_kwargs(call_language),
        }

    if provider == "elevenlabs":
        voice_id = md.get("voice") or ""
        if not voice_id:
            raise RuntimeError(
                "voice_provider=elevenlabs requires a 'voice' (ElevenLabs voice_id) in dispatch metadata."
            )
        logger.info("Voice provider: ElevenLabs pipeline (voice_id=%s)", voice_id)
        locator = _pronunciation_locator()
        tts_kwargs = {"voice_id": voice_id, "api_key": config.ELEVENLABS_API_KEY}
        if locator:
            tts_kwargs["pronunciation_dictionary_locators"] = [locator]
        # Hint the known call language; auto-detect otherwise so code-switching
        # mid-call isn't locked to the wrong language. Forcing the wrong
        # language (or OpenAI STT's "en" default when omitted) badly garbles
        # zh/ms transcription.
        call_language = md.get("language")
        # Read per-call from dispatch metadata (Django's dial view sources this
        # fresh from the Settings-page toggle) so switching providers doesn't
        # need a worker restart; PIPELINE_STT_PROVIDER only backstops calls
        # dispatched outside that path (e.g. selftest scripts).
        stt_provider = md.get("stt_provider") or config.PIPELINE_STT_PROVIDER
        if stt_provider == "elevenlabs" and call_language not in SUPPORTED_LANGUAGES:
            # language=auto: Scribe has no way to restrict detection to our
            # three languages (no candidate list, no bias prompt) — OpenAI STT
            # does, so auto calls ride it. Fixed-language calls keep Scribe's
            # ~150ms fast path with a hard language_code lock.
            logger.info("STT: language=auto — OpenAI STT with language-bias prompt instead of Scribe")
            stt_provider = "openai"
        if stt_provider == "ilmu" and not config.ILMU_API_KEY:
            logger.warning("STT engine ilmu selected but ILMU_API_KEY unset — using OpenAI STT")
            stt_provider = "openai"
        if stt_provider == "elevenlabs":
            logger.info("STT provider: ElevenLabs Scribe v2 Realtime")
            # Bias the model toward the contact's real name + collection
            # vocabulary -- short phrases like "I'm Beckham" are easily
            # misheard as unrelated near-sounding words over compressed phone
            # audio otherwise.
            stt = lk_elevenlabs.STT(
                model_id="scribe_v2_realtime",
                api_key=config.ELEVENLABS_API_KEY,
                language_code=call_language if call_language in SUPPORTED_LANGUAGES else NOT_GIVEN,
                keyterms=_domain_keyterms(md) or NOT_GIVEN,
                # server_vad=None (commit_strategy="manual") was tried to avoid
                # double-VAD wait, but nothing in this pipeline sends the
                # required manual commit signal -- transcripts stalled
                # unfinalized for 10-20+s with garbled repeated partials, far
                # worse than the default's steady ~1.5-2s. Keep server VAD
                # (reliable finalization) but shrink its silence threshold
                # below the 1.5s default to cut the redundant wait instead.
                # Scribe finalization is the cascade's dominant latency (~1s);
                # this is the main knob. Lower = snappier, higher = fewer
                # premature mid-sentence finalizations. Tune per line quality.
                # CLAMPED to ElevenLabs' accepted 0.3-3.0s range on purpose.
                # Out of range it does not fall back to a default — it answers
                # 1008 invalid_request and CLOSES the STT socket, so the agent
                # greets and then never hears a single word (field case
                # 2026-07-23: SCRIBE_VAD_SILENCE_SECS=0.25 silently broke every
                # ElevenLabs-voice call; the caller talked to a deaf agent that
                # hung up as system:no_answer). A stray env value must degrade
                # latency, never take speech recognition down.
                server_vad={"vad_silence_threshold_secs": min(3.0, max(0.3, float(
                    os.getenv("SCRIBE_VAD_SILENCE_SECS") or 0.35)))},
            )
        else:
            # openai and ilmu speak the same /audio/transcriptions shape —
            # only host + model differ. ILMU's ASR is Malaysian-trained; A/B
            # it against Scribe from Settings → AI → Call STT Engine.
            if stt_provider == "ilmu":
                logger.info("STT provider: ILMU ASR (%s)", config.ILMU_STT_MODEL)
                stt_kwargs = {"model": config.ILMU_STT_MODEL, "api_key": config.ILMU_API_KEY,
                              "base_url": config.ILMU_BASE_URL}
            else:
                stt_kwargs = {"model": config.PIPELINE_STT_MODEL, "api_key": config.OPENAI_API_KEY}
            if call_language in SUPPORTED_LANGUAGES:
                stt_kwargs["language"] = call_language
                stt_kwargs["prompt"] = _stt_domain_bias(md, auto=False)
            else:
                stt_kwargs["detect_language"] = True
                stt_kwargs["prompt"] = _stt_domain_bias(md, auto=True)
            stt = lk_openai.STT(**stt_kwargs)
        # Wrap ElevenLabs TTS in a fallback so a transient ElevenLabs failure
        # ("no audio frames were pushed") doesn't leave the contact hearing
        # SILENCE for a turn. The backup is a SECOND ElevenLabs instance (fresh
        # connection, SAME voice_id/settings) — the voice never changes, and
        # happy-path latency is unchanged (the backup only runs when the primary
        # yields no audio, retrying on a clean websocket).
        tts_engine = lk_elevenlabs.TTS(**tts_kwargs)
        try:
            backup_tts = lk_elevenlabs.TTS(**tts_kwargs)
            tts_engine = agent_tts.FallbackAdapter([tts_engine, backup_tts])
        except Exception as e:  # never block the call on fallback setup
            logger.warning("TTS fallback unavailable: %s", e)
        return {
            "stt": stt,
            "llm": _pipeline_llm(md),
            "tts": tts_engine,
            **_pipeline_shared_kwargs(call_language),
        }

    if provider == "gemini":
        logger.info("Voice provider: Gemini Live (%s)", config.GEMINI_MODEL)
        realtime_input_config = genai_types.RealtimeInputConfig(
            automatic_activity_detection=genai_types.AutomaticActivityDetection(
                start_of_speech_sensitivity=genai_types.StartSensitivity.START_SENSITIVITY_HIGH,
                end_of_speech_sensitivity=genai_types.EndSensitivity.END_SENSITIVITY_HIGH,
                prefix_padding_ms=config.VAD_PREFIX_MS,
                silence_duration_ms=config.VAD_SILENCE_MS,
            )
        )
        return {
            "llm": lk_google.realtime.RealtimeModel(
                model=config.GEMINI_MODEL,
                voice=config.GEMINI_VOICE,
                api_key=config.GOOGLE_API_KEY,
                realtime_input_config=realtime_input_config,
                temperature=0.8,
            ),
            # Barge-in ON (reverted 2026-07-22): let real caller speech
            # interrupt the agent so it actually listens instead of monologuing.
            "turn_handling": {"interruption": {"min_duration": 0.4, "min_words": 2}},
        }

    # openai realtime (default)
    voice = md.get("voice") or config.REALTIME_VOICE
    logger.info("Voice provider: OpenAI Realtime (%s, voice=%s)", config.REALTIME_MODEL, voice)
    turn_detection = TurnDetection(
        type="server_vad",
        threshold=config.VAD_THRESHOLD,
        prefix_padding_ms=300,
        silence_duration_ms=config.VAD_SILENCE_MS,
        create_response=True,
        # Barge-in ON (reverted 2026-07-22): the server cancels the in-progress
        # response when the caller speaks, so the agent stops and listens
        # instead of talking over them (field case: agent monologued, only one
        # of many caller utterances was heard).
        interrupt_response=True,
    )
    realtime_kwargs = dict(
        model=config.REALTIME_MODEL,
        voice=voice,
        api_key=config.OPENAI_API_KEY,
        turn_detection=turn_detection,
        temperature=0.8,
    )
    # Same fix as the ElevenLabs pipeline's STT: without a language hint the
    # Realtime API's own transcription auto-detects and badly garbles zh/ms
    # speech (random-script hallucinations) whenever it's unsure. Hint the
    # known call language; leave auto-detect for code-switching mid-call.
    # `model` is REQUIRED here: the plugin serializes only explicitly-set
    # fields, so a language-only override reaches OpenAI without a
    # transcription model and input transcription silently never runs — the
    # call still works (the realtime model hears audio natively) but every
    # user turn vanishes from the transcript. The model below is the same
    # default the plugin uses when this override is absent.
    call_language = md.get("language")
    if call_language in SUPPORTED_LANGUAGES:
        realtime_kwargs["input_audio_transcription"] = InputAudioTranscription(
            model="gpt-4o-mini-transcribe", language=call_language)
    else:
        # language=auto (or unset): unconstrained detection transcribes short
        # utterances in random languages ("hello" -> Russian "Алло") — bias it
        # to Malaysia's three without locking any single one.
        realtime_kwargs["input_audio_transcription"] = InputAudioTranscription(
            model="gpt-4o-mini-transcribe", prompt=_AUTO_LANG_BIAS)
    # Noise reduction is what stops the model hallucinating "speech" out of
    # phone-line hiss (random-language turns when the caller is silent).
    nr = config.REALTIME_NOISE_REDUCTION
    if nr and nr not in ("off", "none"):
        realtime_kwargs["input_audio_noise_reduction"] = nr
        logger.info("OpenAI Realtime input noise reduction: %s", nr)
    return {
        "llm": lk_openai.realtime.RealtimeModel(**realtime_kwargs),
        # Barge-in ON (reverted 2026-07-22): the client stops playout when the
        # caller speaks (pairs with interrupt_response=True above). Backchannel
        # guard keeps a bare "mm-hmm"/"okay" from cutting the agent off.
        "turn_handling": {"interruption": {"min_duration": 0.4, "min_words": 2}},
    }


async def _await_answer(ctx: JobContext, participant, timeout: float = 60.0):
    """Block until an outbound SIP callee actually picks up.

    On outbound SIP the participant object joins the room while the line is still
    RINGING — its `sip.callStatus` attribute reads "dialing"/"ringing" and only
    flips to "active" on pickup. Greeting before "active" plays the opening into a
    dead audio path, so the human hears nothing after they answer (the classic
    "AI says nothing after I accept the call" bug). For non-SIP participants (no
    `sip.*` attributes, e.g. a browser tester) this returns immediately.
    """
    # SIP publishes sip.callStatus; the WhatsApp bridges (Baileys gateway +
    # Cloud bridge) publish wa.callStatus with the same ringing→active
    # lifecycle so they can join EARLY (warm room during ringing) without the
    # agent greeting a ringing line. No attribute at all = browser tester or
    # a legacy bridge that only joins on answer — greet immediately.
    status_key = next((k for k in ("sip.callStatus", "wa.callStatus")
                       if participant.attributes.get(k) is not None), None)
    if status_key is None:
        return "active"  # nothing to wait for (browser tester) — treat as answered
    answered = asyncio.Event()

    def _check(p):
        st = p.attributes.get(status_key)
        if st in ("active", "hangup"):  # picked up, or gone — either way stop waiting
            answered.set()

    @ctx.room.on("participant_attributes_changed")
    def _on_attrs(_changed, p):
        if p.identity == participant.identity:
            _check(p)

    _check(participant)  # may already be "active" before we subscribed
    result = "timeout"
    try:
        await asyncio.wait_for(answered.wait(), timeout=timeout)
        result = participant.attributes.get(status_key) or "active"
        logger.info("Call answered (%s=%s)", status_key, result)
    except asyncio.TimeoutError:
        logger.warning("SIP call not answered within %ss; greeting anyway", timeout)
    if result == "hangup":
        # Callee declined / hung up during ringing — don't settle or greet a
        # dead line; the caller-side end is handled by the entrypoint.
        return "hangup"
    # Human beat after pickup: real callers don't speak the instant the line
    # opens — give the person a moment to bring the phone to their ear and
    # say hello (also lets the media path fully settle). ANSWER_SETTLE_MS
    # tunes it; keep within ~1-2s or the caller thinks the line is dead.
    settle = float(os.getenv("ANSWER_SETTLE_MS", "1500") or 1500) / 1000
    await asyncio.sleep(settle)
    return result


# Who ended each call, keyed by room name — written at every hangup site
# (end_call tool / silence watchdog / max-duration failsafe / caller
# disconnect) and folded into the final live-transcript write so the persisted
# transcript can say who hung up. setdefault everywhere: first cause wins.
_END_CAUSE: dict[str, str] = {}

# A clear closing/goodbye from the AGENT. When the model wraps up naturally
# ("thanks, have a good day") but forgets to call end_call, the silence watchdog
# would otherwise sit ~12s then RE-ASK — re-opening a call the agent already
# closed. Detecting this ends the call gracefully instead. Terminal phrases only
# (EN + a little BM/ZH); bare "bye" is excluded to avoid mid-word false hits.
_GOODBYE_RE = re.compile(
    r"\b(have a (?:good|great|nice|lovely|wonderful|blessed) day|have a good one|"
    r"good\s?bye|take care|selamat tinggal|jumpa lagi)\b|再见|拜拜",
    re.IGNORECASE)

# Carrier/network system messages — voicemail prompts, "number not in service",
# "cannot be connected" — that some telcos/SIP routes answer WITH before (or
# instead of) actually ringing the callee. SIP still reports the leg "active",
# so the agent greets and STT dutifully "converses" with a recording, never a
# person (observed 2026-07-23: the phone never rang; the transcript showed the
# agent politely re-asking a Chinese decline announcement to confirm identity).
# Matched only against the FIRST user-attributed turn (a real caller could say
# these words later in conversation) — a fixed carrier phrase prefix, not a
# general profanity/gibberish filter.
_CARRIER_MSG_RE = re.compile(
    r"您所拨打的|无法接通|请稍后再拨|留下您的信息|留下您的讯息|暂时无法|"
    r"number you (?:have )?dialed|is not in service|please leave a message|"
    r"nombor yang anda dail",
    re.IGNORECASE)


async def _hangup(room_name: str):
    """Delete the room, which disconnects the phone participant (hangs up)."""
    api = lk_api.LiveKitAPI()
    try:
        await api.room.delete_room(lk_api.DeleteRoomRequest(room=room_name))
    finally:
        await api.aclose()


async def _gated_audio(audio):
    """Universal dBFS noise gate on the contact's audio, transport-agnostic:
    runs inside the agent so SIP, Cloud-bridge and Baileys calls all share ONE
    implementation (the gateway's BAILEYS_GATE_DB is its transport-local
    ancestor — keep that at 0 when this is on). Frames below VOICE_GATE_DB are
    replaced with silence; the gate holds open VOICE_GATE_HOLD_MS after the
    last loud frame. Pure-Python RMS over ~10ms frames is cheap (~50k ops/s)."""
    import array as _array

    from livekit import rtc

    threshold = config.VOICE_GATE_DB
    hold_s = config.VOICE_GATE_HOLD_MS / 1000
    open_until = 0.0
    async for frame in audio:
        raw = bytes(frame.data)
        samples = _array.array("h")
        samples.frombytes(raw)
        if len(samples):
            acc = 0
            for s in samples:
                acc += s * s
            rms = math.sqrt(acc / len(samples))
            db = 20 * math.log10(rms / 32768) if rms > 0 else -120.0
            now = time.time()
            if db >= threshold:
                open_until = now + hold_s
            elif now > open_until:
                frame = rtc.AudioFrame(
                    data=b"\x00" * len(raw),
                    sample_rate=frame.sample_rate,
                    num_channels=frame.num_channels,
                    samples_per_channel=frame.samples_per_channel,
                )
        yield frame


def _make_think_filter():
    """Stateful stream scrubber for reasoning leaks: drops <think>…</think>
    blocks (and bare tag fragments, incl. tags split across stream chunks)
    before the text reaches TTS. Needed because ILMU still occasionally emits
    thinking even with reasoning_effort=low (field case: the greeting spoke
    '…from D</think>' mid-call, 2026-07-21)."""
    state = {"in_think": False, "carry": ""}
    OPEN, CLOSE = "<think>", "</think>"

    def feed(piece: str) -> str:
        text = state["carry"] + piece
        state["carry"] = ""
        out: list[str] = []
        while text:
            tag = CLOSE if state["in_think"] else OPEN
            idx = text.find(tag)
            if idx == -1:
                # also scrub a stray closing tag outside a think block (the
                # missing-open-tag leak shape) so it is never spoken
                if not state["in_think"]:
                    stray = text.find(CLOSE)
                    if stray != -1:
                        out.append(text[:stray])
                        text = text[stray + len(CLOSE):]
                        continue
                # hold back a tail that could be the start of a split tag
                hold = 0
                probe = text[-(len(OPEN) + 1):]
                for k in range(min(len(text), len(CLOSE) - 1), 0, -1):
                    if OPEN.startswith(text[-k:]) or CLOSE.startswith(text[-k:]):
                        hold = k
                        break
                if not state["in_think"]:
                    out.append(text[: len(text) - hold])
                state["carry"] = text[len(text) - hold:] if hold else ""
                break
            if state["in_think"]:
                text = text[idx + len(CLOSE):]
                state["in_think"] = False
            else:
                out.append(text[:idx])
                text = text[idx + len(OPEN):]
                state["in_think"] = True
        return "".join(out)

    return feed


class CallAgent(Agent):
    """The repayment-reminder agent, with a tool to hang up when the call is done."""

    def __init__(self, instructions: str):
        super().__init__(instructions=instructions)

    async def stt_node(self, audio, model_settings):
        """Default STT node behind the universal noise gate (VOICE_GATE_DB) —
        one filtering implementation for every call transport."""
        source = _gated_audio(audio) if config.VOICE_GATE_DB < 0 else audio
        async for event in Agent.default.stt_node(self, source, model_settings):
            yield event

    async def llm_node(self, chat_ctx, tools, model_settings):
        """Default LLM node + the think-block scrubber on every text delta —
        internal monologue must never reach the voice."""
        scrub = _make_think_filter()
        async for chunk in Agent.default.llm_node(self, chat_ctx, tools, model_settings):
            delta = getattr(chunk, "delta", None)
            content = getattr(delta, "content", None) if delta else None
            if isinstance(content, str) and content:
                cleaned = scrub(content)
                if cleaned != content:
                    delta.content = cleaned or None
            yield chunk

    @function_tool()
    async def end_call(self, ctx: RunContext):
        """Hang up the phone call. Call this right AFTER speaking your own brief
        goodbye. Use it once a clear next step is captured — a promise-to-pay
        date, a refusal, a wrong-person call, or a human follow-up arranged."""
        # The goodbye is the model's own last utterance (rule 7) — generating a
        # second one here made the agent say farewell twice and narrate the
        # hangup. Just let the current speech finish playing, then drop.
        try:
            cur = getattr(ctx.session, "current_speech", None)
            if cur is not None:
                await cur.wait_for_playout()
        except Exception:  # never block the hangup on a playout hiccup
            pass
        # Let the goodbye's tail drain over PSTN before dropping the call.
        await asyncio.sleep(max(config.HANGUP_GRACE_MS, 1200) / 1000)
        try:
            room_name = get_job_context().room.name
            _END_CAUSE.setdefault(room_name, "agent")
            await _hangup(room_name)
        except Exception as e:
            logger.warning("delete_room on hangup failed: %s", e)
        return "Call ended."


async def _warm_llm(session) -> None:
    """Prewarm the pipeline LLM during ring so the FIRST real turn isn't cold
    (~1.8s ttft on turn 1 vs ~0.7s after — first request pays TLS + model
    routing). Fires a throwaway 1-token completion on the session's OWN llm, so
    it warms exactly the client/endpoint the pipeline will use. Best-effort:
    realtime providers (no text .chat) and any error just skip. Only the LLM leg
    — STT/TTS warm on first use."""
    from livekit.agents import llm as _llm
    obj = getattr(session, "llm", None)
    if not isinstance(obj, _llm.LLM):  # realtime model → different warmup, skip
        return
    try:
        ctx = _llm.ChatContext.empty()
        ctx.add_message(role="user", content="hi")
        stream = obj.chat(chat_ctx=ctx)
        try:
            async for _ in stream:  # first token establishes the connection
                break
        finally:
            await stream.aclose()
        logger.info("LLM prewarmed")
    except Exception as e:
        logger.debug("LLM warmup skipped: %s", e)


async def entrypoint(ctx: JobContext):
    config.validate()
    await ctx.connect()

    md = _parse_metadata(ctx)
    contact = _contact_from_metadata(md)
    # Script-call mode: read a fixed script verbatim, then hang up — no
    # listening at all. Computed early so session.start() can disable audio
    # INPUT entirely below: allow_interruptions=False on say() only stops the
    # framework from cutting our speech short, it does nothing about the
    # underlying Agent's own turn-taking, which still reacts to any detected
    # user speech with an automatic reply once our forced utterance ends
    # (observed 2026-07-23: the script would finish, then the agent carried on
    # a normal conversation anyway). With no input audio there is nothing for
    # VAD/STT/turn-detection to ever see, so neither problem can happen.
    script_text = (md.get("script") or "").strip()
    contact_id = contact.get("contact_id")
    memory = _load_memory(contact_id) if contact_id is not None else None
    call_id = ctx.room.name
    started_at = _now()
    transcript_lines: list[str] = []
    live_turns: list[dict[str, str]] = []
    timeout_task: asyncio.Task | None = None
    inbox_task: asyncio.Task | None = None
    silence_task: asyncio.Task | None = None
    sync_task: asyncio.Task | None = None
    seen_item_ids: set[str] = set()  # turns already persisted (event or sync path)
    # Silence watchdog state: the model sometimes says its goodbye without
    # calling end_call (wrong-person calls especially) and the line then sits
    # open until someone hangs up by hand. Track last activity from either
    # side; a nudge fires first, then a forced hangup.
    activity = {"ts": time.time(), "nudges": 0}
    recorder = None  # in-process SIP call recorder, started at pickup, flushed on shutdown
    logger.info("Call started: %s (contact=%s)", call_id, contact.get("contact_name"))

    session = AgentSession(**_build_session_kwargs(md))
    # Warm the LLM connection NOW (runs through the ring wait) so the contact's
    # first reply isn't answered by a cold model. Referenced so it isn't GC'd.
    warm_task = asyncio.create_task(_warm_llm(session))

    # Capture every finalized conversation turn (both user and agent).
    @session.on("conversation_item_added")
    def _on_item(event: ConversationItemAddedEvent):
        if isinstance(event.item, ChatMessage) and event.item.text_content:
            text = event.item.text_content
            # Any turn means the line is live: reset the silence CLOCK on both
            # sides, so the contact gets a fresh window to answer after each
            # agent turn (an agent that just asked a question must not trigger a
            # hangup a second later). Only CONTACT speech clears the nudge COUNT
            # — an agent still talking to a dead line keeps escalating to hangup.
            activity["ts"] = time.time()
            if event.item.role == "user" and not activity.get("heard_user") \
                    and _CARRIER_MSG_RE.search(text):
                # First "user" turn is actually a carrier system announcement,
                # not the callee — nobody picked up. End the call instead of
                # letting the agent try to hold a conversation with a recording.
                logger.info("Carrier/network message detected, not a person — "
                            "ending %s (text=%r)", call_id, text[:120])
                _END_CAUSE.setdefault(call_id, "system:no_answer")

                async def _carrier_msg_hangup():
                    try:
                        await _hangup(call_id)
                    except Exception as e:
                        logger.warning("carrier-message hangup failed: %s", e)

                asyncio.create_task(_carrier_msg_hangup())
            elif event.item.role == "user":
                activity["nudges"] = 0
                activity["heard_user"] = True  # greeting-retry no longer needed
            elif re.search(r"\{\s*end_call\s*\}", text.lower()) or re.search(
                    r"\bend_call\b\W*$", text.lower()):
                # The model sometimes SAYS the tool name instead of invoking it
                # ("Goodbye! {end_call}") — honor the intent: short drain for
                # the goodbye audio, then hang up. Trigger ONLY on the braces
                # form or a trailing bare token: a plain substring match once
                # killed a call mid-identity-check when the pipeline brain
                # merely mentioned the token mid-sentence (2026-07-21). Scrub
                # the marker so operators never see it in the transcript.
                logger.info("end_call marker in assistant text — hanging up %s (text=%r)",
                            call_id, text[:120])
                text = re.sub(r"\{?\s*end_call\s*\}?", "", text).strip() or "Goodbye!"
                _END_CAUSE.setdefault(call_id, "agent")

                async def _marker_hangup():
                    await asyncio.sleep(2.0)
                    try:
                        await _hangup(call_id)
                    except Exception as e:
                        logger.warning("marker hangup failed: %s", e)

                asyncio.create_task(_marker_hangup())
            elif event.item.role == "assistant" and activity.get("heard_user") \
                    and not activity.get("closing") and _GOODBYE_RE.search(text):
                # Agent gave a clear closing after a real exchange but didn't call
                # end_call — the call is done, so hang up instead of letting the
                # silence watchdog re-ask and re-open it. Gated on heard_user so a
                # premature turn-1 "goodbye" can't kill a fresh call; `closing`
                # guards against scheduling more than one hangup.
                activity["closing"] = True
                _END_CAUSE.setdefault(call_id, "agent")
                logger.info("Agent closing line — hanging up %s (text=%r)", call_id, text[:120])

                async def _closing_hangup():
                    await asyncio.sleep(3.0)  # let the goodbye audio play out
                    try:
                        await _hangup(call_id)
                    except Exception as e:
                        logger.warning("closing hangup failed: %s", e)

                asyncio.create_task(_closing_hangup())
            seen_item_ids.add(getattr(event.item, "id", "") or f"evt-{len(transcript_lines)}")
            transcript_lines.append(f"{event.item.role}: {text}")
            # ts (epoch seconds) rides into the live store AND the persisted
            # call transcript (wa_calls copies turns verbatim) — the chat UI
            # uses it for per-turn time labels + audio-position highlighting.
            live_turns.append({"role": event.item.role, "text": text, "ts": time.time()})
            logger.info("TURN [%s]: %s", event.item.role, text)
            # Push to the live store so the website can show turns during the call.
            try:
                save_live(call_id, live_turns)
            except Exception as e:
                logger.warning("save_live failed: %s", e)

    # Live signal that the agent is receiving/processing the caller's audio.
    @session.on("user_input_transcribed")
    def _on_user_audio(event):
        activity["ts"] = time.time()
        logger.info("USER AUDIO transcribed: %r (final=%s)",
                    getattr(event, "transcript", None), getattr(event, "is_final", None))

    # Per-turn stage timings -- answers "which step is slow" directly from
    # logs instead of guessing. EOU = gap between you stopping talking and the
    # agent deciding your turn is over; STT/LLM/TTS (decoupled ElevenLabs
    # pipeline) or RealtimeModel (native speech-to-speech) cover the rest.
    @session.on("metrics_collected")
    def _on_metrics(event: MetricsCollectedEvent):
        m = event.metrics
        if isinstance(m, EOUMetrics):
            logger.info("LATENCY eou: end_of_utterance_delay=%.2fs transcription_delay=%.2fs",
                        m.end_of_utterance_delay, m.transcription_delay)
        elif isinstance(m, STTMetrics):
            logger.info("LATENCY stt: duration=%.2fs audio_duration=%.2fs", m.duration, m.audio_duration)
        elif isinstance(m, LLMMetrics):
            logger.info("LATENCY llm: ttft=%.2fs duration=%.2fs", m.ttft, m.duration)
        elif isinstance(m, RealtimeModelMetrics):
            logger.info("LATENCY realtime: ttft=%.2fs duration=%.2fs", m.ttft, m.duration)
        elif isinstance(m, TTSMetrics):
            logger.info("LATENCY tts: ttfb=%.2fs duration=%.2fs", m.ttfb, m.duration)

    def _sync_history_once() -> bool:
        """Fold session.history turns the event handler missed into the live
        store. Realtime models commit conversation items whose AUDIO transcript
        fills in AFTER conversation_item_added fires — text_content is empty at
        event time, so native-voice calls persisted ZERO turns while the agent
        audibly spoke (field case 2026-07-22, monitor stuck on 'Waiting for the
        agent to speak…'). session.history is the source of truth; this
        re-reads it and adds whatever the event path skipped, keyed by item id."""
        added = False
        try:
            for item in session.history.items:
                if not isinstance(item, ChatMessage) or item.role not in ("user", "assistant"):
                    continue
                text = item.text_content
                item_id = getattr(item, "id", "") or ""
                if not text or not item_id or item_id in seen_item_ids:
                    continue
                seen_item_ids.add(item_id)
                if item.role == "user":
                    activity["ts"] = time.time()
                    activity["nudges"] = 0
                    activity["heard_user"] = True
                transcript_lines.append(f"{item.role}: {text}")
                live_turns.append({"role": item.role, "text": text, "ts": time.time()})
                logger.info("TURN [%s] (history sync): %s", item.role, text)
                added = True
        except Exception as e:
            logger.warning("history sync failed: %s", e)
        if added:
            try:
                save_live(call_id, live_turns)
            except Exception as e:
                logger.warning("save_live failed: %s", e)
        return added

    async def _history_sync():
        while True:
            await asyncio.sleep(2)
            _sync_history_once()

    # On call end: build transcript, summarize, save, print.
    async def _on_shutdown():
        if timeout_task and not timeout_task.done():
            timeout_task.cancel()  # call ended on its own; stop the failsafe timer
        if inbox_task and not inbox_task.done():
            inbox_task.cancel()
        if silence_task and not silence_task.done():
            silence_task.cancel()
        if sync_task and not sync_task.done():
            sync_task.cancel()
        if warm_task and not warm_task.done():
            warm_task.cancel()
        _sync_history_once()  # late-arriving realtime transcripts land in the record
        rec_name = None
        if recorder is not None:
            try:
                rec_name = await recorder.stop()  # flush the stereo WAV
            except Exception as e:
                logger.warning("recorder.stop failed: %s", e)
        ended_at = _now()
        transcript = "\n".join(transcript_lines)

        summary_data = {
            "summary": "",
            "outcome": "unreachable",
            "followup_date": None,
            "followup_action": None,
            "needs_human": False,
            "objection": None,
            "sentiment_score": None,
        }
        user_lines = [line.split(":", 1)[1].strip()
                      for line in transcript_lines if line.startswith("user:")]
        user_lines = [t for t in user_lines if t]
        user_words = sum(len(t.split()) for t in user_lines)
        user_turns = len(user_lines)
        # Summarize when EITHER the person said enough words OR replied across
        # enough turns. The word-only guard used to drop real-but-terse calls: a
        # caller who answers "Yes." / "Next week." / "28th of July." across 5
        # turns totals <15 words yet clearly agreed a follow-up — that was being
        # lost as "not enough said" (real case 2026-07-22). A multi-turn
        # back-and-forth is genuine content, not the near-empty transcript the
        # threshold guards against.
        if user_words < config.MIN_SUMMARY_WORDS and user_turns < 3:
            # Too little was said (nothing at all, or callee picked up and hung
            # up after a word or two) -- asking the LLM to summarize a near-empty
            # transcript makes it hallucinate a plausible-sounding but fictional
            # conversation from contact_name/record alone.
            summary_data["summary"] = (
                "No conversation captured (call ended before any speech was recorded)."
                if not transcript.strip() else
                "Not enough was said to summarize (call ended almost immediately)."
            )
        else:
            try:
                summary_data = {**summary_data, **await _generate_summary(contact, transcript)}
            except Exception as e:  # keep the record even if summarization fails
                logger.error("Summary generation failed: %s", e)
                summary_data["summary"] = f"(summary failed: {e})"

        record = {
            "call_id": call_id,
            "started_at": started_at,
            "ended_at": ended_at,
            "contact": contact,
            "transcript": transcript,
            "recording": rec_name,
            # structured turns with per-turn epoch ts — the /chats SIP call
            # card uses these for time labels (transcript above is the flat
            # string the summarizer consumed; kept for back-compat)
            "turns": live_turns,
            "summary": summary_data.get("summary", ""),
            "outcome": summary_data.get("outcome", "unreachable"),
            "followup_date": summary_data.get("followup_date"),
            "followup_action": summary_data.get("followup_action"),
            "needs_human": bool(summary_data.get("needs_human", False)),
            "objection": summary_data.get("objection"),
            "sentiment_score": summary_data.get("sentiment_score"),
            # Code-computed metadata — no LLM guessing involved.
            "call_executed_at": started_at,
            "executed_call_window": _call_window(started_at),
            "persona_utilized": contact.get("persona"),
            # Not implemented: needs a real signal this codebase doesn't have yet
            # (per-utterance ASR confidence) — left null rather than having an
            # LLM fabricate a number.
            "confidence_score": None,
        }
        save_call(record)
        try:
            # done=True stops the website's live polling; ended_by tells the
            # persisted transcript who hung up
            save_live(call_id, live_turns, done=True,
                      ended_by=_END_CAUSE.pop(call_id, None))
        except Exception as e:
            logger.warning("save_live(done) failed: %s", e)

        if contact_id is not None:
            try:
                _save_memory(
                    contact_id,
                    language=summary_data.get("language_used"),
                    best_contact=summary_data.get("best_contact"),
                    new_notes=summary_data.get("memory_notes") or [],
                )
            except Exception as e:
                logger.warning("_save_memory failed: %s", e)

        logger.info("==== CALL SUMMARY (%s) ====", call_id)
        try:
            print(json.dumps(record, indent=2, ensure_ascii=False))
        except UnicodeEncodeError:
            # Console codepage can't render the contact's script (e.g. cp1252
            # on a piped Windows terminal) -- the record is already persisted
            # via save_call() above; this print is debug visibility only.
            print(json.dumps(record, indent=2, ensure_ascii=True))

    ctx.add_shutdown_callback(_on_shutdown)

    # Krisp noise/background-voice cancellation on the inbound mic audio. Runs
    # before STT/VAD, so it helps the ElevenLabs cascade (which has no other
    # noise reduction) on both SIP and WhatsApp calls. Best-effort: a missing
    # plugin or unknown mode just skips it — never blocks the call.
    start_kwargs = {
        "agent": CallAgent(instructions=agent_instructions(contact, memory=memory)),
        "room": ctx.room,
    }
    if script_text:
        # No listening at all: with audio input off, VAD/STT/turn-detection
        # never see anything, so the underlying Agent can neither auto-reply
        # nor "interrupt" our forced read — the only reliable way to guarantee
        # a verbatim, uninterrupted, non-conversational read (see the note
        # above script_text's definition).
        start_kwargs["room_input_options"] = RoomInputOptions(audio_enabled=False)
        logger.info("Script-call %s — audio input disabled (no listening)", call_id)
    else:
        # Krisp noise/background-voice cancellation on the inbound mic audio. Runs
        # before STT/VAD, so it helps the ElevenLabs cascade (which has no other
        # noise reduction) on both SIP and WhatsApp calls. Best-effort: a missing
        # plugin or unknown mode just skips it — never blocks the call.
        nc_mode = config.NOISE_CANCELLATION
        if _noise_cancellation is not None and nc_mode not in ("off", "none", ""):
            try:
                nc = (_noise_cancellation.BVC() if nc_mode == "bvc"
                      else _noise_cancellation.BVCTelephony())
                start_kwargs["room_input_options"] = RoomInputOptions(noise_cancellation=nc)
                logger.info("Inbound noise cancellation: %s", nc_mode)
            except Exception as e:  # model load / version mismatch — run without it
                logger.warning("Noise cancellation unavailable (%s) — continuing without", e)

    await session.start(**start_kwargs)
    sync_task = asyncio.create_task(_history_sync())

    # Detect caller hang-up: when the phone (SIP) participant leaves the room,
    # end the call at once — save the summary and stop the live feed — instead of
    # letting the agent talk to a dead line until the max-duration failsafe fires.
    hangup_started = False

    @ctx.room.on("participant_disconnected")
    def _on_participant_left(p):
        nonlocal hangup_started
        if hangup_started:
            return
        hangup_started = True
        _END_CAUSE.setdefault(call_id, "contact")
        logger.info("Participant %s disconnected — caller hung up, ending call %s", p.identity, call_id)

        async def _end():
            try:
                await _hangup(call_id)
            except Exception as e:  # room may already be closing — shutdown still fires
                logger.warning("hangup on caller-disconnect failed: %s", e)

        asyncio.create_task(_end())

    # Wait until the caller/callee is actually in the room before speaking.
    # On phone (SIP) the agent joins before the call is answered; greeting too
    # early plays into a dead audio path and is never heard.
    try:
        participant = await ctx.wait_for_participant()
        logger.info("Participant joined: %s — waiting for pickup", participant.identity)
        # …but "joined" != "answered" on SIP. Hold the greeting until pickup.
        answered = await _await_answer(ctx, participant)
        if answered == "hangup":
            # Callee declined / hung up during ringing — end now (writes done via
            # shutdown) so the website's live view doesn't sit on "Ringing…".
            logger.info("Callee hung up during ringing — ending %s", call_id)
            _END_CAUSE.setdefault(call_id, "contact")
            try:
                await _hangup(call_id)
            except Exception as e:
                logger.warning("ring-hangup end failed: %s", e)
            return
        # Record from pickup (not from ring) so the WAV holds only the live call.
        try:
            safe = re.sub(r"[^A-Za-z0-9_-]", "_", call_id)
            out = config.DATA_DIR / "recordings" / f"{safe}.wav"
            recorder = CallRecorder(out)
            recorder.start(ctx.room, participant)
            logger.info("Recording call %s -> %s", call_id, out.name)
        except Exception as e:  # recording is best-effort, never block the call
            logger.warning("recorder start failed: %s", e)
    except Exception as e:
        logger.warning("wait_for_participant failed (%s); greeting anyway", e)

    # Script-call mode (script_text computed at entrypoint start, above): read
    # it verbatim then hang up. No STT/LLM turn, no silence watchdog, no
    # listening at all — a one-way broadcast, distinct from the normal
    # conversational call built below.
    if script_text:
        logger.info("Script-call %s — reading fixed script, no conversation", call_id)
        try:
            # allow_interruptions=False: this is a monologue by design — line
            # noise/breathing must not trigger the normal barge-in logic and
            # cut the read off mid-sentence (observed 2026-07-23: the script
            # stopped partway through with no error, just an early return
            # from wait_for_playout() once "interrupted").
            await session.say(script_text, add_to_chat_ctx=True, allow_interruptions=False)
        except RuntimeError:  # no standalone TTS (realtime path) — read it via the LLM
            await session.generate_reply(
                instructions=f"Read the following text EXACTLY as written, word for "
                             f"word, in a natural voice, then stop talking: {script_text}",
                allow_interruptions=False)
        except APIError as e:
            logger.error("TTS unavailable — ending script-call %s: %s", call_id, e)
            _END_CAUSE.setdefault(call_id, "system:tts_unavailable")
            try:
                await _hangup(call_id)
            except Exception as hangup_err:
                logger.warning("hangup after TTS failure failed: %s", hangup_err)
            return
        await asyncio.sleep(1.5)  # let the audio finish draining before hangup
        _END_CAUSE.setdefault(call_id, "system:script_complete")
        try:
            await _hangup(call_id)
        except Exception as e:
            logger.warning("script-call hangup failed: %s", e)
        return

    # AI speaks first with a polite opening. On the pipeline path (ElevenLabs /
    # ILMU) say it VERBATIM via TTS — the greeting is fully scriptable, and
    # skipping the LLM turn drops first-word latency from ~4s to the TTS ttfb
    # (~0.2s). Realtime providers (openai/gemini native speech-to-speech) have NO
    # standalone TTS, so session.say() can't synthesize there — fall back to an
    # LLM-generated opening. The LLM takes over from the contact's first reply
    # (language switch included) either way.
    logger.info("Sending opening for %s", call_id)
    try:
        await session.say(opening_text(contact), add_to_chat_ctx=True)
    except RuntimeError:  # no standalone TTS (realtime path) — let the model speak it
        await session.generate_reply(instructions=opening_line(contact))
    except APIError as e:
        # The voice provider refused to synthesize (quota, auth, or outage). On
        # the pipeline path NOTHING the agent says can reach the contact — the
        # FallbackAdapter's backup engine shares the same key, so it fails the
        # same way — and staying on the line only burns the contact's patience:
        # the operator sees a healthy "live" call, STT keeps transcribing, and
        # the contact hears dead air. Hang up with a distinct cause instead of
        # letting this kill the entrypoint mid-greeting, which is what left three
        # calls mute for their whole duration with no error anywhere in the UI
        # (2026-07-23: the ElevenLabs key hit its per-key character cap → 401 on
        # every synthesize; account quota was untouched, so nothing looked wrong).
        logger.error("TTS unavailable — ending %s without an opening: %s", call_id, e)
        _END_CAUSE.setdefault(call_id, "system:tts_unavailable")
        try:
            await _hangup(call_id)
        except Exception as hangup_err:
            logger.warning("hangup after TTS failure failed: %s", hangup_err)
        return

    # Failsafe: hard cap on call length in case end_call never fires (silent caller).
    if config.MAX_CALL_SECONDS > 0:
        async def _failsafe():
            await asyncio.sleep(config.MAX_CALL_SECONDS)
            logger.warning("Max call duration (%ss) reached — hanging up %s",
                           config.MAX_CALL_SECONDS, call_id)
            _END_CAUSE.setdefault(call_id, "system:max_duration")
            try:
                await _hangup(call_id)
            except Exception as e:
                logger.warning("failsafe hangup failed: %s", e)
        timeout_task = asyncio.create_task(_failsafe())

    # Silence watchdog: every SILENCE_NUDGE_SECONDS of dead air, RE-ASK the
    # contact (up to SILENCE_MAX_NUDGES times) instead of sitting on a quiet
    # line — "are you still there?" and so on. After the last unanswered re-ask
    # (or the SILENCE_HANGUP_SECONDS absolute cap), hang up. The nudge counter is
    # rearmed to 0 by any contact speech (conversation_item_added handler).
    nudge_gap = float(os.getenv("SILENCE_NUDGE_SECONDS", "12") or 12)
    max_nudges = int(os.getenv("SILENCE_MAX_NUDGES", "2") or 2)
    hangup_after = float(os.getenv("SILENCE_HANGUP_SECONDS", "120") or 120)
    # Greeting cadence for a silent line (client spec, 2026-07-21): the first
    # greeting plays ANSWER_SETTLE_MS after pickup; if the contact says
    # NOTHING, repeat it every GREETING_RETRY_SECONDS up to GREETING_RETRIES
    # times (3 greetings total), then hang up — nobody is on the line.
    # WhatsApp reports "active" while the callee's phone is still switching
    # into the call UI, so the first greeting is often unheard — the repeats
    # are what actually land.
    regreet_after = float(os.getenv("GREETING_RETRY_SECONDS", "3") or 3)
    max_regreets = int(os.getenv("GREETING_RETRIES", "2") or 2)
    activity["ts"] = time.time()  # clock starts at the greeting, not job start

    def _end_silence():
        logger.warning("Silence — forcing hangup of %s after %d re-asks", call_id, activity["nudges"])
        _END_CAUSE.setdefault(call_id, "system:silence")
        return asyncio.create_task(_hangup(call_id))

    async def _silence_watchdog():
        while True:
            await asyncio.sleep(1)
            idle = time.time() - activity["ts"]
            if not activity.get("heard_user") and idle >= regreet_after:
                done = activity.get("regreets", 0)
                if done >= max_regreets:
                    logger.info("No response after %d greetings — hanging up %s",
                                done + 1, call_id)
                    _END_CAUSE.setdefault(call_id, "system:no_answer")
                    try:
                        await _hangup(call_id)
                    except Exception as e:
                        logger.warning("no-response hangup failed: %s", e)
                    return
                activity["regreets"] = done + 1
                activity["ts"] = time.time()  # next window starts at this repeat
                logger.info("Silent line — greeting again (%d/%d) on %s",
                            done + 1, max_regreets, call_id)
                try:
                    await session.generate_reply(instructions=(
                        "The person hasn't responded — they may not have heard "
                        "you (their phone may still be connecting). Say the SAME "
                        "short greeting again, naturally — do not mention "
                        "repeating yourself."))
                except Exception as e:
                    logger.warning("greeting retry failed: %s", e)
                continue
            if idle >= hangup_after:
                logger.warning("Silence for %.0fs — forcing hangup of %s", idle, call_id)
                _END_CAUSE.setdefault(call_id, "system:silence")
                try:
                    await _end_silence()
                except Exception as e:
                    logger.warning("silence hangup failed: %s", e)
                return
            # contact silent for a full gap since anyone last spoke
            if idle < nudge_gap:
                continue
            if activity["nudges"] >= max_nudges:
                # already re-asked the max number of times, still nothing — close
                try:
                    await _end_silence()
                except Exception as e:
                    logger.warning("silence hangup failed: %s", e)
                return
            activity["nudges"] += 1
            activity["ts"] = time.time()  # fresh window for the contact to answer
            last = activity["nudges"] >= max_nudges
            logger.info("Silence %.0fs — re-ask #%d for %s", idle, activity["nudges"], call_id)
            try:
                await session.generate_reply(instructions=(
                    "The person has gone quiet and hasn't replied. If the call "
                    "already reached its natural end (goodbyes exchanged, wrong "
                    "person, or a next step captured), call the end_call tool NOW. "
                    "Otherwise ask ONE short question in their language to check "
                    "they are still there and can hear you"
                    + (", and say you'll have to end the call if there's no answer."
                       if last else ".")))
            except Exception as e:
                logger.warning("silence re-ask failed: %s", e)

    silence_task = asyncio.create_task(_silence_watchdog())

    # Poll the inbox for operator instructions (guide / take-over / escalate) sent
    # mid-call from the web API, and act on them in real time.
    async def _poll_inbox():
        while True:
            await asyncio.sleep(1.0)
            try:
                for ins in pop_instructions(call_id):
                    text = (ins.get("text") or "").strip()
                    if not text:
                        continue
                    mode = ins.get("mode", "guide")
                    if mode == "takeover":
                        # Speak the operator's words verbatim to the caller.
                        try:
                            session.say(text)
                        except Exception:
                            session.generate_reply(
                                instructions=f"Say this to the caller, verbatim: {text}"
                            )
                    else:
                        # guide / escalate: steer the AI's next reply.
                        session.generate_reply(instructions=text)
            except asyncio.CancelledError:
                raise
            except Exception as e:
                logger.warning("inbox poll error: %s", e)

    inbox_task = asyncio.create_task(_poll_inbox())


if __name__ == "__main__":
    # agent_name enables EXPLICIT dispatch (required for outbound phone via
    # make_call.py). The browser token server also requests this same agent_name,
    # so both paths dispatch this one worker.
    cli.run_app(WorkerOptions(entrypoint_fnc=entrypoint, agent_name=config.AGENT_NAME))
