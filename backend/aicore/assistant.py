"""The assistant "brain" — channel-agnostic AI replies for contact chats.

Extracted from whatsapp/ai.py so every text channel (WhatsApp, the messaging
app's email/social channels) drives the SAME conversation: one system prompt, one
CRM-enrichment step, one provider choice. Channel modules adapt their thread
shapes into a plain {"role","content"} history and delegate here.

Pattern: system prompt + recent conversation history -> one chat completion.
Provider selection mirrors the voice agent's summary step: OpenRouter when
OPENROUTER_API_KEY is set, else direct OpenAI.

The system prompt is persona.HARD_RULES (fixed safety floor) + the active
persona, so the text agent behaves like the voice agent's chat twin and a
vertical customises both from one place.
"""
from __future__ import annotations

import re

# The AI provider keys + chat model are GLOBAL app settings that happen to
# live in the whatsapp config module (Setting rows override backend/.env,
# edited from Settings -> App Keys). They are not WhatsApp-specific — every
# text channel shares them — so we import the accessors rather than duplicate
# the Setting-over-env plumbing.
from whatsapp import wa_config

from . import persona

# Back-compat re-export (whatsapp/ai.py mirrors it): the DEFAULT composition.
# The live prompt is composed per reply via persona.system_prompt(), so a
# Setting-row persona override applies without a restart.
SYSTEM_PROMPT = persona.HARD_RULES + "\n\n" + persona.DEFAULT_PERSONA


# Escalation triggers (human handover). Word-boundary patterns, so e.g.
# "polish" does not match "polis".
_HANDOFF_PATTERNS = tuple(re.compile(p) for p in (
    r"\blawyers?\b", r"\bpeguam\b", r"\bpolice\b", r"\bpolis\b",
    r"\bdisput\w*\b",                       # dispute / disputed / disputing
    r"\bhuman\b", r"\bagent\b", r"\bmanusia\b",
    r"\bcomplain\w*\b", r"\bharass\w*\b", r"\brefund\b",
    r"\bcancel\s+(?:my\s+)?(?:order|booking|account)\b",
    r"\bwrong\s+(?:person|number|order|item)\b",
))


def detect_handoff(text: str) -> bool:
    """True when the message should be escalated to a human agent — keyword
    scan used by callers to hand the conversation off (the prompt already
    makes the model back off; this flags the workflow side)."""
    low = (text or "").lower()
    return any(p.search(low) for p in _HANDOFF_PATTERNS)


def _provider() -> tuple[str, str, str]:
    """-> (base_url, api_key, model) — OpenRouter > OpenAI > Google Gemini."""
    return wa_config.chat_provider()


# ---- CRM enrichment ---------------------------------------------------------

def _digits(value: str) -> str:
    return re.sub(r"\D", "", str(value or ""))


def _skills_block() -> str:
    """The operator's editable playbook (crm.AgentSkill) as a SKILLS prompt
    section. Included in every reply so a skill edit actually changes bot
    behavior — not just flow-node runs. Owned by Settings → AI Skills; see
    aicore/knowledge.py `skills_block`."""
    from . import knowledge

    return knowledge.skills_block()


def _find_contact(phone: str):
    """crm.Contact lookup by normalized phone digits. Contact.phone is
    E.164 ("+60…"); a suffix match catches local formats (01x…) and numbers
    that arrive without the country prefix."""
    from crm.models import Contact

    digits = _digits(phone)
    if not digits:
        return None
    contact = Contact.objects.filter(phone=f"+{digits}").first()
    if contact is None and len(digits) >= 7:
        # stored phones carry formatting ("+60 18-445 6677") — compare digits
        contact = next((c for c in Contact.objects.exclude(phone="")
                         if _digits(c.phone).endswith(digits[-9:])), None)
    return contact


# Record statuses that mean "nothing outstanding" — kept in step with
# flows.engine.CLOSED_RECORD_STATUSES.
_CLOSED_RECORD_STATUSES = ("closed", "done", "resolved", "cancelled", "completed")


def _record_line(record) -> str:
    bits = [f"{record.get_type_display().lower()} {record.external_id or record.pk}"]
    if record.title:
        bits.append(record.title)
    bits.append(f"status {record.status}")
    if record.amount is not None:
        bits.append(f"{record.currency} {record.amount}")
    if record.due_date:
        bits.append(f"key date {record.due_date.isoformat()}")
    return " — ".join(bits)


def _crm_facts(contact) -> str:
    """The contact's CRM facts + open records, facts only — shared by the chat
    prompt (via _crm_context) and the memory manager (aicore/memory.py's
    build_context), so both channels quote the same record."""
    line = f"CRM record for this contact — name: {contact.name}, status: {contact.status}"
    if contact.company_name:
        line += f", company: {contact.company_name}"
    if contact.tags:
        line += f", tags: {', '.join(str(t) for t in contact.tags[:8])}"
    line += "."
    try:
        records = list(contact.records.all()[:5])
    except Exception:
        records = []
    open_records = [r for r in records
                    if r.status.strip().lower() not in _CLOSED_RECORD_STATUSES]
    if open_records:
        line += " Open records: " + "; ".join(_record_line(r) for r in open_records[:3]) + "."
    elif records:
        line += " Most recent record: " + _record_line(records[0]) + "."
    attrs = contact.attributes or {}
    if attrs:
        detail = ", ".join(f"{k}: {v}" for k, v in list(attrs.items())[:6])
        line += f" Attributes — {detail[:300]}."
    return line


def _crm_context(contact) -> str:
    """One system line with the contact's live record state — context only;
    the prompt still forbids quoting figures as final."""
    return _crm_facts(contact) + (
        " Use these details as background only — exact figures and dates must "
        "still be confirmed by the team before being stated as final."
    )


# ---- the reply --------------------------------------------------------------

def generate_reply(history: list[dict], *, contact_name: str, phone: str, channel: str,
                   contact_id=None) -> str:
    """One AI reply for the conversation so far. `history` items are
    {"role": "user"|"assistant", "content": str}, oldest first.
    `contact_id` (crm.Contact pk, from the thread's identity link) beats
    phone matching — social channels have chat ids, not phone numbers.
    Raises on provider errors — callers decide how to surface them."""
    from openai import OpenAI  # lazy — keep cheap endpoints fast

    base_url, api_key, model = _provider()
    if not api_key:
        raise RuntimeError("No OPENAI_API_KEY / OPENROUTER_API_KEY configured.")

    # Composed per reply: hard compliance rules (fixed) + the active persona
    # (crm.Setting "aicore_persona" override, else the built-in default).
    messages: list[dict] = [{"role": "system", "content": persona.system_prompt()}]
    skills = _skills_block()
    if skills:
        messages.append({"role": "system", "content": skills})
    context = (
        f"Contact name on file: {contact_name or 'unknown'} · reachable at {phone or 'unknown'} · "
        f"channel: {channel}. "
        "You do not have live business data in this chat unless a CRM record follows; "
        "if asked for anything you cannot see, say the team will confirm it."
    )
    messages.append({"role": "system", "content": context})

    contact = None
    if contact_id:
        from crm.models import Contact
        contact = Contact.objects.filter(pk=contact_id).first()
    if contact is None:
        contact = _find_contact(phone)
    if contact is not None:
        # CONTACT MEMORY: the CRM facts plus everything the memory manager
        # carries across conversations (learned prefs, the last few
        # conversation summaries). build_context reuses _crm_facts, so this
        # block REPLACES the bare CRM line — one source of figures.
        from aicore import memory  # lazy — memory pulls nothing heavy, but avoid load cycles

        mem = memory.build_context(contact, channel=channel)
        if mem:
            messages.append({"role": "system", "content": (
                "CONTACT MEMORY — the CRM record plus what previous conversations "
                "(any channel) taught us about this person. Use it naturally; never "
                "recite it or mention keeping records. Exact figures and dates must "
                "still be confirmed by the team before being stated as final.\n" + mem
            )})
        else:  # memory manager unavailable — fall back to the plain CRM line
            messages.append({"role": "system", "content": _crm_context(contact)})

    # COMPANY KNOWLEDGE: what the business itself published (hours, policies,
    # FAQs, live promotions, sudden notices). Injected right after the memory
    # block — the SAME seam, so chat and voice see one knowledge base. Bounded
    # and exception-safe: a failure here degrades to answering without it.
    # Note this runs even without a linked contact: a policy is not personal.
    from aicore import knowledge as knowledge_mod  # lazy, same rule as memory

    last_user = next((str(m.get("content", "")) for m in reversed(history)
                      if m.get("role") != "assistant"), "")
    block = knowledge_mod.prompt_block(contact, channel=channel, text=last_user)
    if block:
        messages.append({"role": "system", "content": block})

    for m in history:
        role = "assistant" if m.get("role") == "assistant" else "user"
        messages.append({"role": role, "content": str(m.get("content", ""))})

    client = OpenAI(base_url=base_url, api_key=api_key)
    kwargs = {}
    if "ilmu" in base_url:
        # ilmu-v3.1 is a reasoning model; "low" turns extended thinking OFF
        # (ILMU docs) so internal deliberation can never leak into a contact
        # message. The voice agent applies the same setting (_pipeline_llm).
        kwargs["reasoning_effort"] = "low"
    resp = client.chat.completions.create(
        model=model,
        messages=messages,
        max_tokens=300,
        temperature=0.4,
        **kwargs,
    )
    text = (resp.choices[0].message.content or "").strip()
    # Belt and braces for reasoning models: strip any leaked think-block —
    # a whole <think>…</think> section, or everything before a stray closing
    # tag (observed on a live call: reasoning streamed with NO opening tag).
    if "</think>" in text:
        text = text.rsplit("</think>", 1)[-1].strip()
    text = re.sub(r"<think>.*?(?:</think>|$)", "", text, flags=re.S).strip()
    if contact is not None and "{{" in text:
        # Models copy {{link}}-style placeholders verbatim out of the
        # persona/skill instructions (observed live on a Telegram reply) —
        # fill them from the same variable catalog the flow builder uses.
        # Unknown keys stay visible on purpose, same rule as the engine.
        from flows.engine import render, variables_for
        text = render(text, variables_for(contact))
    return text
