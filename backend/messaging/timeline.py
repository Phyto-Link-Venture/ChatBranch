"""Per-contact cross-channel timeline + AI summary.

Aggregates every communication surface the app has for one contact:
  - AI voice calls   backend/data/calls.json (voice agent records: summary,
                     outcome, transcript)
  - WhatsApp         whatsapp store thread (matched by phone -> wa_id)
  - Email/social     messaging store threads (email matched by address;
                     Messenger/Instagram have no phone linkage, so they're
                     included only when their key is explicitly passed)

The AI summary is generated with the same provider config as chat auto-replies
(whatsapp/wa_config) and cached to data/messaging/summaries/{key}.json.
Without an AI key a deterministic heuristic summary is produced so the
feature still demos in sandbox.
"""
from __future__ import annotations

import json
import re
from datetime import datetime, timezone
from pathlib import Path

from whatsapp import store as wa_store
from whatsapp import wa_config

from . import store as msg_store

DATA_DIR = Path(__file__).resolve().parent.parent / "data"
CALLS_PATH = DATA_DIR / "calls.json"
SUMMARIES_DIR = DATA_DIR / "messaging" / "summaries"


def _now() -> str:
    return datetime.now(timezone.utc).isoformat()


def _norm_phone(phone: str) -> str:
    return re.sub(r"\D", "", phone or "")


def collect_events(phone: str = "", email: str = "") -> list[dict]:
    """All communications for this contact, oldest first.
    Event: {channel, dir, text, ts, source, extra?}."""
    events: list[dict] = []
    digits = _norm_phone(phone)

    # voice calls (agent-written records)
    try:
        calls = json.loads(CALLS_PATH.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        calls = []
    for c in calls if isinstance(calls, list) else []:
        called = c.get("contact") or c.get("borrower") or {}
        if digits and _norm_phone(str(called.get("phone", ""))) != digits:
            continue
        outcome = c.get("outcome", "unclear")
        text = c.get("summary") or f"Voice call — outcome: {outcome}."
        events.append({
            "channel": "voice",
            "dir": "out",
            "source": "ai",
            "text": text,
            "ts": c.get("ended_at") or c.get("started_at") or "",
            "extra": {
                "outcome": outcome,
                "followup_date": c.get("followup_date"),
                "needs_human": c.get("needs_human", False),
                "call_id": c.get("call_id"),
            },
        })

    # Chat channels — CONTACT-CENTRIC: the contact's linked threads
    # (crm.Thread.contact FK, any channel — the hub can bind WhatsApp numbers
    # or addresses that differ from the CRM phone) are the source of truth;
    # exact phone/email key lookups remain as the fallback for unlinked
    # threads. Deduped by (channel, key).
    contact = None
    try:
        from crm.models import Contact
        if digits:
            # stored phones are formatted ("+60 18-445 6677") — compare digits
            contact = next((c for c in Contact.objects.exclude(phone="")
                             if _norm_phone(c.phone).endswith(digits[-9:])), None)
        if contact is None and email:
            contact = Contact.objects.filter(email__iexact=email).first()
    except Exception:
        contact = None

    seen: set[tuple[str, str]] = set()

    def _add_thread(channel: str, key: str) -> None:
        key = str(key or "").strip()
        if not key or (channel, key) in seen:
            return
        seen.add((channel, key))
        thread = (wa_store.load_thread(key) if channel == "whatsapp"
                  else msg_store.load_thread(channel, key))
        for m in (thread or {}).get("messages", []):
            if m.get("private"):
                continue  # internal notes stay off the contact timeline
            event = {
                "channel": channel,
                "dir": m.get("dir"),
                "source": m.get("source"),
                "text": m.get("text", ""),
                "ts": m.get("ts", ""),
            }
            if m.get("media"):
                event["media"] = m["media"]
            events.append(event)

    if contact is not None:
        try:
            for t in contact.threads.all():
                _add_thread(t.channel, t.key)
        except Exception:
            pass
        if contact.email:
            _add_thread("email", contact.email)
    if digits:
        _add_thread("whatsapp", digits)
    if email:
        _add_thread("email", email)

    events.sort(key=lambda e: e.get("ts") or "")
    return events


# ---- cross-channel engagement counters ----------------------------------------------


# Playbook baseline recovery rates per stage (demo defaults — client-tunable
# in spirit of skill 00; used for the "potential returns" estimate).
def engagement_stats(entity=None) -> dict:
    """Live communication stats across every thread and call. `entity` narrows
    to one outlet (call counters stay organization-wide — the file-based call
    store carries no entity)."""
    awaiting_reply = 0   # last message is ours, contact hasn't answered
    replied = 0          # contact has answered at least once
    threads_total = 0

    def scan(thread: dict | None):
        nonlocal awaiting_reply, replied, threads_total
        if not thread:
            return
        msgs = [m for m in thread.get("messages", []) if m.get("source") != "system"]
        if not msgs:
            return
        threads_total += 1
        if any(m.get("dir") == "in" for m in msgs):
            replied += 1
        if msgs[-1].get("dir") == "out":
            awaiting_reply += 1

    for t in wa_store.list_threads(entity=entity):
        scan(wa_store.load_thread(t["wa_id"]))
    for t in msg_store.list_threads(entity=entity):
        scan(msg_store.load_thread(t["channel"], t["key"]))

    try:
        calls = json.loads(CALLS_PATH.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        calls = []
    calls = calls if isinstance(calls, list) else []
    needs_human = sum(1 for c in calls if c.get("needs_human"))
    followups_set = sum(1 for c in calls if c.get("followup_date"))

    return {
        "threads_total": threads_total,
        "awaiting_reply": awaiting_reply,
        "replied": replied,
        "calls_total": len(calls),
        "needs_human": needs_human,
        "followups_set": followups_set,
    }




def _delta(cached: dict | None, items: list[dict], count_key: str) -> tuple[str, list[dict] | None]:
    """(previous_summary, fresh_items) for an incremental update — or ("", None)
    when a FULL pass is required: no trusted (non-heuristic) previous summary,
    no `as_of` anchor (pre-rolling cache file), nothing new (operator forced a
    regenerate — give them a genuinely fresh take), or the counts don't
    reconcile (a backdated item — late webhook with provider_ts — slipped
    BEHIND the anchor and an incremental pass would silently miss it)."""
    if not cached or cached.get("heuristic") or not str(cached.get("summary") or "").strip():
        return "", None
    as_of = str(cached.get("as_of") or "")
    if not as_of:
        return "", None
    fresh = [m for m in items if str(m.get("ts") or "") > as_of]
    if not fresh or len(fresh) >= len(items):
        return "", None
    try:
        expected = int(cached.get(count_key) or 0) + len(fresh)
    except (TypeError, ValueError):
        return "", None
    if len(items) != expected:
        return "", None
    return str(cached["summary"]).strip(), fresh


def _media_note(m: dict) -> str:
    """Transcript marker for an attachment so the model knows media arrived."""
    media = m.get("media")
    if isinstance(media, dict) and media:
        return f"[{media.get('type') or 'media'} attachment] "
    return ""


def _thread_lines(msgs: list[dict]) -> list[str]:
    return [
        f"[{m.get('ts', '')[:16]}] "
        + ("[internal note] " if m.get("private")
           else "←contact: " if m.get("dir") == "in" else f"→{m.get('source', 'agent')}: ")
        + _media_note(m)
        + m.get("text", "")[:300]
        for m in msgs
    ]


def _event_lines(events: list[dict]) -> list[str]:
    return [
        f"[{e['ts'][:16]}] {e['channel']}/{e.get('source', '')} "
        f"{'←contact' if e.get('dir') == 'in' else '→outbound'}: "
        + _media_note(e)
        + e["text"][:300]
        for e in events
    ]


def _summary_key(phone: str, email: str) -> str:
    return _norm_phone(phone) or msg_store.safe_key(email) or "unknown"


def _summary_path(phone: str, email: str) -> Path:
    return SUMMARIES_DIR / f"{_summary_key(phone, email)}.json"


def cached_summary(phone: str = "", email: str = "") -> dict | None:
    try:
        return json.loads(_summary_path(phone, email).read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return None


def _heuristic_summary(name: str, events: list[dict], context: dict) -> str:
    if not events:
        return f"No recorded communications with {name or 'this contact'} yet."
    by_channel: dict[str, int] = {}
    replies = 0
    last_in = None
    for e in events:
        by_channel[e["channel"]] = by_channel.get(e["channel"], 0) + 1
        if e.get("dir") == "in" and e.get("source") == "customer":
            replies += 1
            last_in = e
    channels = ", ".join(f"{n}× {c}" for c, n in by_channel.items())
    parts = [f"{len(events)} contacts on record ({channels}); {replies} contact replies."]
    voice_outcomes = [e["extra"]["outcome"] for e in events
                      if e["channel"] == "voice" and e.get("extra", {}).get("outcome")]
    if voice_outcomes:
        parts.append(f"Last call outcome: {voice_outcomes[-1]}.")
    if last_in:
        parts.append(f'Last contact message: "{last_in["text"][:120]}".')
    parts.append("(Heuristic summary — add an OpenAI/OpenRouter key in Settings → App Keys for AI analysis.)")
    return " ".join(parts)


# ---- per-thread summary (pinned in the chat header) -----------------------------------

def _thread_summary_path(channel: str, key: str) -> Path:
    return SUMMARIES_DIR / f"thread-{channel}-{msg_store.safe_key(key)}.json"


def _load_any_thread(channel: str, key: str) -> dict | None:
    """Thread loader across BOTH stores (whatsapp lives in its own app)."""
    if channel == "whatsapp":
        return wa_store.load_thread(wa_store.normalize_wa_id(key))
    return msg_store.load_thread(channel, key)


def cached_thread_summary(channel: str, key: str) -> dict | None:
    try:
        return json.loads(_thread_summary_path(channel, key).read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return None


def thread_summary_state(channel: str, key: str) -> dict | None:
    """Cached summary + a staleness flag (messages newer than the summary).
    None when the thread doesn't exist."""
    thread = _load_any_thread(channel, key)
    if thread is None:
        return None
    msgs = [m for m in thread.get("messages", []) if m.get("source") != "system"]
    cached = cached_thread_summary(channel, key)
    last_ts = msgs[-1].get("ts", "") if msgs else ""
    stale = bool(msgs) and (cached is None or (cached.get("as_of") or "") < last_ts)
    return {"summary": cached, "stale": stale, "message_count": len(msgs)}


def _heuristic_thread_summary(thread: dict, msgs: list[dict]) -> str:
    if not msgs:
        return "No messages yet in this conversation."
    inbound = [m for m in msgs if m.get("dir") == "in"]
    notes = [m for m in msgs if m.get("private")]
    parts = [f"{len(msgs)} messages; {len(inbound)} from the contact."]
    if inbound:
        parts.append(f'Last contact message: "{inbound[-1].get("text", "")[:120]}".')
    else:
        parts.append("Contact has not replied yet.")
    if notes:
        parts.append(f'Latest internal note: "{notes[-1].get("text", "")[:100]}".')
    parts.append("(Heuristic — add an OpenAI/OpenRouter key in Settings → App Keys for AI summaries.)")
    return " ".join(parts)


def generate_thread_summary(channel: str, key: str) -> dict | None:
    """Summarize ONE conversation (any channel) and cache it. Used by the
    always-on summary card at the top of the chat. Rolling: when a previous AI
    summary exists, only the messages newer than its `as_of` anchor go to the
    model together with that summary (see _delta)."""
    thread = _load_any_thread(channel, key)
    if thread is None:
        return None
    msgs = [m for m in thread.get("messages", []) if m.get("source") != "system"]

    context = ""
    contact_id = thread.get("contact_id")
    if contact_id:
        from crm.models import Contact  # lazy — keeps module import-light

        c = Contact.objects.filter(pk=contact_id).first()
        if c:
            record = c.records.first()
            context = f"Contact: {c.name}, status {c.status}"
            if c.tags:
                context += f", tags {', '.join(str(t) for t in c.tags[:5])}"
            if record is not None:
                context += (f". Record: {record.get_type_display().lower()} "
                            f"{record.external_id or record.pk}, status {record.status}")
                if record.due_date:
                    context += f", key date {record.due_date.isoformat()}"
            context += ".\n"

    incremental = False
    if wa_config.ai_available() and msgs:
        from openai import OpenAI  # lazy

        base, api_key, model = wa_config.chat_provider()
        cached = cached_thread_summary(channel, key)
        prev, fresh = _delta(cached, msgs, count_key="message_count")
        incremental = fresh is not None
        if incremental:
            prompt = (
                f"{context}Channel: {channel}.\n"
                f"Current summary of this conversation (covers messages up to "
                f"{str(cached.get('as_of') or '')[:16]}):\n{prev}\n\n"
                "New messages since then (oldest→newest):\n"
                + "\n".join(_thread_lines(fresh[-40:]))
                + "\n\nUpdate the summary for the agent working this chat: keep still-valid "
                "facts from the current summary (commitments, complaints, opt-outs), drop what "
                "the new messages supersede, and reflect the contact's current position, "
                "sentiment, and ONE next action. 2-4 sentences. "
                "Factual — no speculation presented as fact."
            )
        else:
            prompt = (
                f"{context}Channel: {channel}. Conversation log (oldest→newest):\n"
                + "\n".join(_thread_lines(msgs[-40:]))
                + "\n\nWrite a 2-4 sentence summary for the agent working this chat: what the "
                "contact wants/said, any commitment, complaint or opt-out, current sentiment, "
                "and ONE next action. Factual — no speculation presented as fact."
            )
        try:
            client = OpenAI(base_url=base, api_key=api_key)
            resp = client.chat.completions.create(
                model=model,
                messages=[
                    {"role": "system",
                     "content": "You are a contact-conversation analyst. Be concise and factual."},
                    {"role": "user", "content": prompt},
                ],
                max_tokens=250,
                temperature=0.2,
            )
            text = (resp.choices[0].message.content or "").strip()
            heuristic = False
            from aicore import usage as usage_ledger  # lazy + exception-safe

            usage_ledger.record("timeline",
                                provider=usage_ledger.provider_from_base_url(base),
                                model=model, usage=getattr(resp, "usage", None),
                                contact_id=contact_id, channel=channel, ref=key)
        except Exception as e:
            # A quota/network hiccup must not destroy the last GOOD summary —
            # keep serving it and surface a short, transient error instead.
            cached = cached_thread_summary(channel, key)
            err = str(e).split("\n")[0][:140]
            if cached and not cached.get("heuristic"):
                return {**cached, "error": err}
            text = _heuristic_thread_summary(thread, msgs) + f" [AI unavailable: {err}]"
            heuristic = True
    else:
        text = _heuristic_thread_summary(thread, msgs)
        heuristic = True

    result = {
        "summary": text,
        "heuristic": heuristic,
        "incremental": incremental,  # rolling update (prev summary + delta) vs full pass
        "message_count": len(msgs),
        # staleness anchor: the newest message this summary has seen
        "as_of": msgs[-1].get("ts", "") if msgs else "",
        "generated_at": _now(),
    }
    SUMMARIES_DIR.mkdir(parents=True, exist_ok=True)
    path = _thread_summary_path(channel, key)
    tmp = path.with_suffix(".tmp")
    tmp.write_text(json.dumps(result, ensure_ascii=False, indent=1), encoding="utf-8")
    tmp.replace(path)
    return result


def generate_summary(name: str, phone: str = "", email: str = "", context: dict | None = None) -> dict:
    """Build (and cache) the contact summary from the full timeline. Rolling
    like the thread summary: previous AI summary + only the events newer than
    its `as_of` anchor when that's safe (see _delta)."""
    context = context or {}
    events = collect_events(phone, email)

    incremental = False
    if wa_config.ai_available() and events:
        from openai import OpenAI  # lazy

        base, key, model = wa_config.chat_provider()
        cached = cached_summary(phone, email)
        prev, fresh = _delta(cached, events, count_key="event_count")
        incremental = fresh is not None
        header = (
            f"Contact: {name} · phone {phone}"
            + (f" · {context['record']}" if context.get("record") else "")
            + ".\n\n"
        )
        if incremental:
            prompt = (
                header
                + f"Current relationship summary (covers communications up to "
                f"{str(cached.get('as_of') or '')[:16]}):\n{prev}\n\n"
                "New communications since then (oldest→newest):\n"
                + "\n".join(_event_lines(fresh[-40:]))
                + "\n\nUpdate the 4-6 sentence relationship summary: keep still-valid facts "
                "(commitments, complaints, engagement per channel), fold in the new "
                "communications and current sentiment, and end with ONE recommended next "
                "action. Factual — no speculation presented as fact."
            )
        else:
            prompt = (
                header
                + "Communication log (oldest→newest):\n" + "\n".join(_event_lines(events[-40:]))
                + "\n\nWrite a 4-6 sentence relationship summary: engagement level per channel, "
                "any commitments or complaints, contact sentiment, and ONE recommended next "
                "action. Factual — no speculation presented as fact."
            )
        try:
            client = OpenAI(base_url=base, api_key=key)
            resp = client.chat.completions.create(
                model=model,
                messages=[
                    {"role": "system",
                     "content": "You are a contact-conversation analyst. Be concise and factual."},
                    {"role": "user", "content": prompt},
                ],
                max_tokens=350,
                temperature=0.2,
            )
            summary_text = (resp.choices[0].message.content or "").strip()
            heuristic = False
            from aicore import usage as usage_ledger  # lazy + exception-safe

            usage_ledger.record("timeline",
                                provider=usage_ledger.provider_from_base_url(base),
                                model=model, usage=getattr(resp, "usage", None),
                                ref=phone or email or name)
        except Exception as e:
            # keep serving the last GOOD summary through a quota/network hiccup
            err = str(e).split("\n")[0][:140]
            if cached and not cached.get("heuristic"):
                return {**cached, "error": err}
            summary_text = _heuristic_summary(name, events, context) + f" [AI error: {err}]"
            heuristic = True
    else:
        summary_text = _heuristic_summary(name, events, context)
        heuristic = True

    result = {
        "summary": summary_text,
        "heuristic": heuristic,
        "incremental": incremental,  # rolling update (prev summary + delta) vs full pass
        "event_count": len(events),
        # staleness anchor: the newest event this summary has seen
        "as_of": events[-1].get("ts", "") if events else "",
        "generated_at": _now(),
    }
    SUMMARIES_DIR.mkdir(parents=True, exist_ok=True)
    path = _summary_path(phone, email)
    tmp = path.with_suffix(".tmp")
    tmp.write_text(json.dumps(result, ensure_ascii=False, indent=1), encoding="utf-8")
    tmp.replace(path)
    return result
