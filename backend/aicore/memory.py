"""The AI Memory Manager — one contact memory shared by every channel.

Two halves:

  READ  — build_context(contact) composes a compact, deterministic prompt
          block (CRM facts, learned preferences, open records, the last N
          conversation summaries). The chat brain (assistant.py) injects it as
          its "CONTACT MEMORY" system block, and the voice dispatch path
          (whatsapp/baileys_calls.py) passes the same string in the agent
          dispatch metadata — so a WhatsApp chat and a voice call remember the
          same things.

  WRITE — summarize_conversation() turns a finished conversation into one
          crm.ConversationSummary row via a single LLM call (same lazy
          OpenRouter > OpenAI > Google provider resolution as the chat brain,
          via wa_config.chat_provider), and applies any learned profile
          updates (preferred_contact_time / communication_style /
          ai_profile_summary / sentiment_trend) onto the Contact.
          Triggered by the call-transcript hook (whatsapp/wa_calls.py) and by
          the idle-chat ticker below (aicore/apps.py).

Sandbox rule: no AI provider key -> summarize_conversation returns None
silently. Both public functions NEVER raise into callers — memory is a
nice-to-have; the conversation that triggered it already happened.
"""
from __future__ import annotations

import json
import logging
import re
import threading
import time
from datetime import timedelta

from whatsapp import wa_config

logger = logging.getLogger(__name__)

CONTEXT_BUDGET = 1500   # chars — keep the memory block prompt-cheap
SUMMARY_LINE_MAX = 240  # chars per "[date source] summary" line

LANGUAGE_NAMES = {"en": "English", "ms": "Bahasa Malaysia", "zh": "Chinese"}


# ---- READ: the prompt block ---------------------------------------------------

def build_context(contact, channel=None, max_summaries=5) -> str:
    """Compact deterministic memory block for prompts: profile facts, learned
    preferences, open records, then the newest `max_summaries` conversation
    summaries. Budgeted to ~CONTEXT_BUDGET chars — oldest summaries are
    dropped first. Returns "" for no contact / on any failure; never raises."""
    try:
        return _build_context(contact, channel, max_summaries)
    except Exception:
        logger.exception("aicore.memory: build_context failed")
        return ""


def _build_context(contact, channel, max_summaries) -> str:
    if contact is None:
        return ""
    from aicore import assistant  # lazy: assistant also imports this module's callers

    # Profile: reuse the assistant's CRM enrichment (name, status, open
    # records, attributes) rather than re-gathering it here.
    head: list[str] = [assistant._crm_facts(contact)]
    language = LANGUAGE_NAMES.get(contact.language)
    if language:
        head.append(f"Preferred language: {language}.")
    if contact.preferred_contact_time:
        head.append(f"Preferred contact time: {contact.preferred_contact_time}.")
    if contact.communication_style:
        head.append(f"Communication style: {contact.communication_style}.")
    if contact.sentiment_trend:
        head.append(f"Recent sentiment trend: {contact.sentiment_trend}.")
    if contact.ai_profile_summary.strip():
        head.append(f"Profile notes: {contact.ai_profile_summary.strip()}")

    # Newest first (ConversationSummary.Meta.ordering = -created_at).
    summaries = list(contact.conversation_summaries.all()[:max_summaries])
    lines = [
        f"- [{s.created_at:%Y-%m-%d} {s.source}] {s.summary.strip()}"[:SUMMARY_LINE_MAX]
        for s in summaries
    ]

    def compose(summary_lines: list[str]) -> str:
        parts = list(head)
        if summary_lines:
            parts.append("Recent conversations (newest first):")
            parts.extend(summary_lines)
        return "\n".join(parts)

    text = compose(lines)
    while len(text) > CONTEXT_BUDGET and lines:
        lines.pop()  # list is newest-first, so this drops the OLDEST summary
        text = compose(lines)
    return text[:CONTEXT_BUDGET]


# ---- WRITE: one LLM call per finished conversation ------------------------------

_SENTIMENTS = ("positive", "neutral", "negative")


def _provider() -> tuple[str, str, str]:
    """(base_url, api_key, model) — the SAME Setting-over-env resolution the
    chat brain uses (wa_config.chat_provider): OpenRouter > OpenAI > Google."""
    return wa_config.chat_provider()


def _chat_text(base_url: str, api_key: str, model: str, prompt: str, *,
               contact=None, channel: str = "", ref: str = "") -> str:
    """One chat completion, raw text back. Lazy openai import — same rule as
    assistant.generate_reply / calls.lk: keep cheap request paths fast.
    Records the tokens into the usage ledger (feature "summary")."""
    from openai import OpenAI

    client = OpenAI(base_url=base_url, api_key=api_key)
    resp = client.chat.completions.create(
        model=model,
        messages=[{"role": "user", "content": prompt}],
        max_tokens=500,
        temperature=0.2,
    )
    from aicore import usage as usage_ledger  # lazy + exception-safe

    usage_ledger.record("summary",
                        provider=usage_ledger.provider_from_base_url(base_url),
                        model=model, usage=getattr(resp, "usage", None),
                        contact=contact, channel=channel, ref=ref)
    return (resp.choices[0].message.content or "").strip()


def _summary_prompt(contact, source: str, channel: str, transcript_text: str) -> str:
    return f"""You maintain the long-term memory of a business's AI assistant. Below is a \
finished {source} conversation ({channel or source}) with {contact.name}.

Extract what is worth remembering for FUTURE conversations. Write every field in English.

Return ONLY a JSON object with exactly these keys:
- "summary": 1-3 factual sentences — what was asked, what was agreed (what + when), objections raised.
- "facts": a list of short durable facts worth carrying forward (may be empty), e.g. "only home after 7pm".
- "sentiment": the contact's overall attitude — exactly one of "positive", "neutral", "negative".
- "profile_updates": an object with any of these keys, ONLY when the conversation clearly supports them:
    "preferred_contact_time": when the contact prefers to be contacted (short phrase),
    "communication_style": how this contact prefers to be spoken to (short phrase),
    "ai_profile_summary": a 1-2 sentence standing profile of this contact that REPLACES the previous one.

Conversation transcript:
---
{transcript_text}
---
Output ONLY the JSON object, nothing else."""


def _parse_json_object(text: str) -> dict | None:
    """Defensive JSON extraction — models love ```json fences and prose."""
    if not text:
        return None
    cleaned = re.sub(r"^\s*```[a-zA-Z0-9]*\s*", "", text.strip())
    cleaned = re.sub(r"\s*```\s*$", "", cleaned)
    start, end = cleaned.find("{"), cleaned.rfind("}")
    if start < 0 or end <= start:
        return None
    try:
        data = json.loads(cleaned[start:end + 1])
    except ValueError:
        return None
    return data if isinstance(data, dict) else None


def _apply_profile_updates(contact, updates, sentiment: str) -> None:
    """Persist learned prefs onto the Contact. Only non-empty values apply;
    ai_profile_summary REPLACES the previous one; sentiment_trend always
    follows the newest conversation's sentiment."""
    fields: list[str] = []
    if isinstance(updates, dict):
        for key, max_len in (("preferred_contact_time", 64), ("communication_style", 120)):
            value = str(updates.get(key) or "").strip()
            if value:
                setattr(contact, key, value[:max_len])
                fields.append(key)
        profile = str(updates.get("ai_profile_summary") or "").strip()
        if profile:
            contact.ai_profile_summary = profile
            fields.append("ai_profile_summary")
    if sentiment:
        contact.sentiment_trend = sentiment
        fields.append("sentiment_trend")
    if fields:
        contact.save(update_fields=fields + ["updated_at"])


def summarize_conversation(contact, source: str, channel: str, ref: str,
                           transcript_text: str):
    """One LLM call -> one crm.ConversationSummary row (+ profile updates).

    Returns the created row, or None on ANY failure: no contact, empty
    transcript, no AI provider key (sandbox rule), LLM/network error, or
    unparseable output. Never raises into callers."""
    try:
        if contact is None or not str(transcript_text or "").strip():
            return None
        base_url, api_key, model = _provider()
        if not api_key:
            return None  # sandbox: no AI provider configured
        text = _chat_text(base_url, api_key, model,
                          _summary_prompt(contact, source, channel, transcript_text),
                          contact=contact, channel=channel, ref=source)
        data = _parse_json_object(text)
        if not data:
            return None
        summary = str(data.get("summary") or "").strip()
        if not summary:
            return None
        sentiment = str(data.get("sentiment") or "").strip().lower()
        if sentiment not in _SENTIMENTS:
            sentiment = ""
        raw_facts = data.get("facts")
        facts = ([str(f).strip()[:200] for f in raw_facts if str(f).strip()][:12]
                 if isinstance(raw_facts, list) else [])

        from crm.models import ConversationSummary
        row = ConversationSummary.objects.create(
            contact=contact, source=source, channel=(channel or "")[:12],
            ref=str(ref or "")[:190], summary=summary, facts=facts,
            sentiment=sentiment,
        )
        _apply_profile_updates(contact, data.get("profile_updates"), sentiment)
        return row
    except Exception:
        logger.exception("aicore.memory: summarize_conversation failed")
        return None


# ---- the idle-chat summarizer (ticker) --------------------------------------------
# Calls get summarized when they end (wa_calls hook). Chats have no "end", so a
# conversation burst counts as finished once it has been quiet for
# AICORE_SUMMARY_IDLE_MIN minutes — the ticker below sweeps for those.

def _idle_minutes() -> int:
    # Read LIVE each sweep (Setting-over-env) so a dashboard change to the idle
    # threshold applies without a restart.
    from core import app_settings
    return app_settings.aicore_summary_idle_min()


def thread_ref(thread) -> str:
    """ConversationSummary.ref for a chat thread — channel-qualified so the
    same key on two channels never collides."""
    return f"{thread.channel}:{thread.key}"[:190]


def summarize_idle_chats(now=None) -> int:
    """One sweep: summarize every conversation burst that has gone idle.

    A thread qualifies when it has a linked contact AND its newest
    non-system message is at least AICORE_SUMMARY_IDLE_MIN minutes old (the
    burst is over) AND newer than the contact's latest chat summary for this
    thread ref (there is actually something new). The already-summarized check
    is one created_at comparison — unchanged threads cost two cheap queries
    and no LLM call, and a burst is summarized exactly once. Only the messages
    since the last summary (capped at 40) are sent to the model."""
    from django.db.models import Max, Q
    from django.utils import timezone as dj_tz

    from crm.models import ConversationSummary, Thread

    now = now or dj_tz.now()
    cutoff = now - timedelta(minutes=_idle_minutes())
    made = 0
    threads = (
        Thread.objects.filter(contact__isnull=False)
        .annotate(last_msg_at=Max("messages__created_at",
                                  filter=~Q(messages__source="system")))
        .filter(last_msg_at__isnull=False, last_msg_at__lte=cutoff)
        .select_related("contact")
    )
    for thread in threads:
        ref = thread_ref(thread)
        last_summary_at = (
            ConversationSummary.objects
            .filter(contact=thread.contact, source="chat", ref=ref)
            .order_by("-created_at")
            .values_list("created_at", flat=True)
            .first()
        )
        if last_summary_at is not None and last_summary_at >= thread.last_msg_at:
            continue  # burst already summarized, nothing new since
        messages = thread.messages.exclude(source="system")
        if last_summary_at is not None:
            messages = messages.filter(created_at__gt=last_summary_at)
        batch = list(messages.order_by("-created_at")[:40])
        batch.reverse()  # chronological for the transcript
        if not batch:
            continue
        transcript = "\n".join(
            f"{'Contact' if m.direction == 'in' else 'Agent'}: {m.text}" for m in batch
        )
        if summarize_conversation(thread.contact, source="chat", channel=thread.channel,
                                  ref=ref, transcript_text=transcript) is not None:
            made += 1
    return made


_ticker_lock = threading.Lock()
_ticker_started = False


def start_ticker() -> bool:
    """Start the in-process idle-chat summarizer thread (idempotent) — same
    pattern as flows.engine.start_ticker. Interval: AICORE_SUMMARY_INTERVAL
    seconds (default 600)."""
    global _ticker_started
    with _ticker_lock:
        if _ticker_started:
            return False
        _ticker_started = True

    # Interval is read once here (at thread start); a change applies on restart.
    from core import app_settings
    interval = app_settings.aicore_summary_interval()

    def loop():
        from django.db import close_old_connections

        while True:
            time.sleep(interval)
            try:
                summarize_idle_chats()
            except Exception:
                logger.exception("aicore.memory: idle-summary tick failed")
            finally:
                close_old_connections()

    threading.Thread(target=loop, daemon=True, name="aicore-summarizer").start()
    logger.info("aicore: idle-chat summarizer started (every %ss)", interval)
    return True


def ticker_alive() -> bool:
    return _ticker_started
