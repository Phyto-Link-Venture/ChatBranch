# backend/calls/playground.py
"""Text-only simulator for the voice agent's own system prompt -- type a
contact reply, see the AI's reply, no real call. Reuses voice_agent/
prompts.py + config.py directly (both have zero LiveKit dependency) via the
same sys.path bridge as lk.py, but skips lk.py itself so this stays a cheap
endpoint (importing lk.py pulls in the LiveKit SDK, seconds of cost).
"""
import json
import os
import sys
from datetime import datetime, timezone

_AGENT_DIR = os.path.join(os.path.dirname(os.path.dirname(__file__)), "voice_agent")
if _AGENT_DIR not in sys.path:
    sys.path.insert(0, _AGENT_DIR)

import prompts as agent_prompts  # noqa: E402
import config as agent_config  # noqa: E402

# Kept separate from voice_agent/data/calls.json (real dialed calls) so simulated
# Playground runs never mix with real call history / the call-logs dashboard.
PLAYGROUND_LOG = agent_config.DATA_DIR / "playground_calls.json"

END_CALL_TOOL = {
    "type": "function",
    "function": {
        "name": "end_call",
        "description": (
            "Call once a clear next step is captured (an agreed date or action, "
            "a decline, or a human follow-up arranged) "
            "and the conversation should end. Your message content for THIS "
            "reply must include a brief, warm goodbye — never call this tool "
            "with empty or missing message content."
        ),
    },
}


def merge_contact(contact: dict) -> dict:
    """Same contact shape the real agent uses: config.CONTACT as a base, with
    the real contact's dial_meta() fields layered on top."""
    return {**agent_config.CONTACT, **{k: v for k, v in contact.items() if v is not None}}


def generate_reply(contact: dict, history: list[dict], text: str | None,
                    memory: dict | None = None) -> tuple[str, bool]:
    """Returns (ai_reply, ended). `text` is None for the very first turn
    (produces the opening line); otherwise it's the contact's typed reply,
    and `history` is the prior turns as [{"role": "user"|"assistant", "content": str}].
    `memory` is the contact's MEMORY.md subset (calls.memory.load_memory)."""
    from openai import OpenAI  # lazy -- keep cheap endpoints fast

    merged = merge_contact(contact)
    kwargs, model = agent_config.summary_config()
    if kwargs is None:
        raise RuntimeError("No chat key set (need one of OPENROUTER_API_KEY / OPENAI_API_KEY / GOOGLE_API_KEY).")

    messages = [{"role": "system", "content": agent_prompts.agent_instructions(merged, memory=memory)}]
    if text is None:
        messages.append({"role": "system", "content": f"(Directive) {agent_prompts.opening_line(merged)}"})
    else:
        messages.extend(history)
        messages.append({"role": "user", "content": text})

    client = OpenAI(**kwargs)
    resp = client.chat.completions.create(
        model=model, messages=messages, tools=[END_CALL_TOOL], temperature=0.4, max_tokens=300,
    )
    msg = resp.choices[0].message
    return (msg.content or "").strip(), bool(msg.tool_calls)


def render_transcript(turns: list[dict]) -> str:
    """Render turns as the plain 'role: content' text summary_prompt()/agent.py's
    real-call path expects. No new exchange appended -- used as-is when the
    contact ends the call themselves (see end_scenario() below)."""
    return "\n".join(f"{t.get('role', 'user')}: {t.get('content', '')}" for t in turns)


def build_transcript(history: list[dict], text: str | None, reply: str) -> str:
    """Render the turns (prior history + this exchange) — the normal case
    where the AI just produced a new reply (possibly ending the call itself)."""
    turns = list(history)
    if text is not None:
        turns.append({"role": "user", "content": text})
    turns.append({"role": "assistant", "content": reply})
    return render_transcript(turns)


def generate_outcome(contact: dict, transcript: str) -> dict:
    """End-of-scenario summary/outcome extraction -- the same summary_prompt()
    used by voice_agent/agent.py's _generate_summary() for real calls, so a
    Playground run's outcome is directly comparable to a real one."""
    from openai import OpenAI  # lazy -- keep cheap endpoints fast

    kwargs, model = agent_config.summary_config()
    if kwargs is None:
        raise RuntimeError("No chat key set (need one of OPENROUTER_API_KEY / OPENAI_API_KEY / GOOGLE_API_KEY).")
    client = OpenAI(**kwargs)
    resp = client.chat.completions.create(
        model=model,
        messages=[{"role": "user", "content": agent_prompts.summary_prompt(contact, transcript)}],
        response_format={"type": "json_object"},
        temperature=0.2,
    )
    return json.loads(resp.choices[0].message.content)


def _call_window(iso_ts: str) -> str:
    """Bucket a timestamp into a coarse time-of-day label. Heuristic on UTC
    hour (no per-tenant timezone concept exists yet) -- good enough for
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


def finalize_outcome(contact: dict, transcript: str) -> dict:
    """Runs generate_outcome() (the LLM extraction) then layers on
    code-computed metadata + fields that are explicitly left unimplemented --
    see voice_agent/prompts.py's summary_prompt() docstring for what's
    actually derivable from a transcript vs what needs a real signal (ASR
    confidence, a trained recovery model) that doesn't exist in this codebase."""
    outcome = generate_outcome(contact, transcript)
    now = datetime.now(timezone.utc).isoformat()
    return {
        **outcome,
        "call_executed_at": now,
        "executed_call_window": _call_window(now),
        "persona_utilized": contact.get("persona"),
        "confidence_score": None,
        "payout_window_match": None,
        "estimated_recovery_probability": None,
    }


def end_scenario(contact_id: int, contact: dict, history: list[dict]) -> dict:
    """The contact hangs up mid-conversation (simulated via the Playground's
    End Call button) -- no new AI turn, just finalize + save whatever was
    actually said so far. Returns the outcome dict."""
    merged = merge_contact(contact)
    transcript = render_transcript(history)
    outcome = finalize_outcome(merged, transcript)
    save_playground_result(contact_id, merged, transcript, outcome)
    return outcome


def save_playground_result(contact_id: int, contact: dict, transcript: str, outcome: dict) -> None:
    """Append one simulated-scenario record to data/playground_calls.json so
    you can refer back to past Playground runs (mirrors storage.save_call()'s
    append-a-JSON-array pattern, but kept in a separate file -- see PLAYGROUND_LOG)."""
    PLAYGROUND_LOG.parent.mkdir(parents=True, exist_ok=True)
    records = []
    if PLAYGROUND_LOG.exists():
        try:
            records = json.loads(PLAYGROUND_LOG.read_text(encoding="utf-8"))
            if not isinstance(records, list):
                records = []
        except (json.JSONDecodeError, OSError):
            records = []
    records.append({
        "contact_id": contact_id,
        "contact": contact,
        "transcript": transcript,
        "ended_at": datetime.now(timezone.utc).isoformat(),
        **outcome,
    })
    PLAYGROUND_LOG.write_text(json.dumps(records, indent=2, ensure_ascii=False), encoding="utf-8")
