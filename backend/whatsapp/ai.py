"""AI auto-reply for WhatsApp text chats — thin delegate.

The assistant "brain" (system prompt, hard rules, CRM enrichment, provider
selection) lives in aicore/assistant.py so every text channel behaves
identically. This module keeps the old public surface — generate_reply(thread)
and HISTORY_LIMIT — so whatsapp/views.py and messaging/views.py (which adapts
its threads into this shape) need zero changes.
"""
from __future__ import annotations

from aicore import assistant

HISTORY_LIMIT = 12  # last N thread messages forwarded to the model

# Back-compat re-export — the prompt itself now lives with the brain.
SYSTEM_PROMPT = assistant.SYSTEM_PROMPT


def generate_reply(thread: dict) -> str:
    """One AI reply for the thread's latest state. Raises on provider errors."""
    history: list[dict] = []
    for m in thread.get("messages", [])[-HISTORY_LIMIT:]:
        if m.get("source") == "system":
            continue
        history.append({
            "role": "user" if m.get("dir") == "in" else "assistant",
            "content": m.get("text", ""),
        })
    return assistant.generate_reply(
        history,
        contact_name=thread.get("name") or "",
        phone=thread.get("phone") or thread.get("wa_id") or "",
        channel="whatsapp",
        # identity link beats phone matching — social channels (telegram/IG)
        # have chat ids, not phone numbers, so this is their ONLY CRM join
        contact_id=thread.get("contact_id"),
    )
