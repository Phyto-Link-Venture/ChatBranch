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

import json
import logging
import re
import uuid

logger = logging.getLogger(__name__)

# Hard stop on the tool-calling loop — a runaway model that keeps emitting tool
# calls can execute at most this many rounds before we force a plain reply.
MAX_TOOL_ROUNDS = 4

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


def _skills_block(entity=None) -> str:
    """The operator's editable playbook (crm.AgentSkill) as a SKILLS prompt
    section. Included in every reply so a skill edit actually changes bot
    behavior — not just flow-node runs. Owned by Settings → AI Skills; see
    aicore/knowledge.py `skills_block`. `entity` adds that outlet's own skills
    on top of the organization-wide ones (its version of a same-named skill
    wins)."""
    from . import knowledge

    return knowledge.skills_block(entity)


def _entity_of(contact):
    """Which outlet a contact belongs to — drives persona/skills/knowledge
    selection. Exception-safe: an unresolvable entity degrades to the
    organization-wide configuration, never to another outlet's."""
    if contact is None:
        return None
    try:
        from crm import scoping

        return scoping.thread_entity(contact=contact)
    except Exception:
        logger.exception("aicore: entity resolution failed — using global config")
        return None


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

    # Resolve the contact FIRST: which outlet (entity) this person belongs to
    # decides the persona, skills and knowledge below, so a Rimbayu customer
    # gets Rimbayu's assistant rather than the organization default.
    contact = None
    if contact_id:
        from crm.models import Contact
        contact = Contact.objects.filter(pk=contact_id).first()
    if contact is None:
        contact = _find_contact(phone)
    entity = _entity_of(contact)

    # Composed per reply: hard compliance rules (fixed) + the persona governing
    # this outlet (its own "aicore_persona" override, else the organization's,
    # else the built-in default).
    messages: list[dict] = [{"role": "system", "content": persona.system_prompt(entity)}]
    skills = _skills_block(entity)
    if skills:
        messages.append({"role": "system", "content": skills})
    context = (
        f"Contact name on file: {contact_name or 'unknown'} · reachable at {phone or 'unknown'} · "
        f"channel: {channel}. "
        "You do not have live business data in this chat unless a CRM record follows; "
        "if asked for anything you cannot see, say the team will confirm it."
    )
    messages.append({"role": "system", "content": context})

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

    # TOOLS: only the tools whose integration is actually connected (empty in
    # the zero-credential sandbox). Computed here so the guidance line joins the
    # other system messages; when empty, NOTHING below changes and this reply is
    # byte-for-byte the single completion it has always been. Tools are only
    # reached via generate_reply, which the channel layers already skip for
    # opted-out / do-not-contact threads — so they sit inside that gate.
    tools = _tool_specs(contact, channel)
    if tools:
        messages.append({"role": "system", "content": (
            "You can take real actions using the provided tools when it helps the "
            "customer. Any tool that creates, changes or cancels something real "
            "requires the customer's confirmation first: state exactly what you "
            "are about to do (with the specific details) and ask them to confirm, "
            "then wait for their reply. Only after they clearly agree, call the "
            "tool again with confirmed=true. For appointments, offer the open "
            "slots and get their explicit choice of one specific time — never "
            "book a slot they have not agreed to."
        )})

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
    base_create = {"model": model, "messages": messages,
                   "max_tokens": 300, "temperature": 0.4, **kwargs}
    usage_acc: list = []  # every completion's resp.usage — summed into the ledger
    if tools and _tools_supported(base_url):
        # Additive tool-calling loop. Falls back to a plain completion if the
        # provider rejects `tools=`, so this never breaks a reply.
        text = _run_tool_loop(client, base_create, tools, contact=contact,
                              channel=channel, provider=_provider_name(base_url),
                              usage_acc=usage_acc).strip()
    else:
        # No tools (sandbox / disabled / unsupported provider): the EXACT single
        # completion — identical kwargs — that generate_reply has always made.
        resp = client.chat.completions.create(**base_create)
        usage_acc.append(getattr(resp, "usage", None))
        text = (resp.choices[0].message.content or "").strip()
    # Usage ledger (billing) — exception-safe, records nothing when the
    # provider returned no usage payload.
    from aicore import usage as usage_ledger  # lazy, same rule as memory

    _pt, _ct = usage_ledger.sum_usages(usage_acc)
    usage_ledger.record(
        "chat_reply", provider=usage_ledger.provider_from_base_url(base_url),
        model=model, prompt_tokens=_pt, completion_tokens=_ct,
        contact=contact, channel=channel, ref=phone or "",
        detail={"rounds": sum(1 for u in usage_acc if u is not None)})
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


# ---- tool-calling loop ------------------------------------------------------

def _tool_specs(contact, channel: str) -> list:
    """The connected-integration tool schemas, or [] — exception-safe so a
    registry error can never break a reply (degrades to today's no-tools path)."""
    try:
        from aicore import tools as tools_mod

        return tools_mod.tool_specs(contact, channel)
    except Exception:
        logger.exception("aicore: tool_specs failed — replying without tools")
        return []


def _provider_name(base_url: str) -> str:
    """Canonical provider key ('openai'/'openrouter'/'gemini'/'ilmu') for the
    tool loop's per-provider branches. Reuses the usage ledger's detector so
    provider naming stays in ONE place."""
    from aicore import usage
    return usage.provider_from_base_url(base_url)


def _tools_supported(base_url: str) -> bool:
    """Offer tools only to OpenAI-compatible chat providers that support them.
    ilmu runs the reasoning_effort=low path and is NOT verified for tools, so it
    is excluded. Any other provider that actually rejects `tools=` is caught by
    the one-shot fallback in _run_tool_loop."""
    return "ilmu" not in (base_url or "")


def _tool_call_payload(tc, provider: str) -> dict:
    """One tool call rebuilt as a plain dict for the NEXT request. Some providers
    require provider-specific fields echoed back on the tool-call turn — this is
    the single place that quirk lives, so a new model = one more branch here."""
    payload = {
        "id": getattr(tc, "id", ""),
        "type": "function",
        "function": {"name": tc.function.name, "arguments": tc.function.arguments},
    }
    if provider == "gemini":
        # Gemini's OpenAI-compat endpoint returns a `thought_signature` on each
        # tool call and REQUIRES it echoed back verbatim, or the follow-up call
        # 400s ("Function call is missing a thought_signature"). The OpenAI SDK
        # parks unknown response fields in `model_extra["extra_content"]`.
        extra = (getattr(tc, "model_extra", None) or {}).get("extra_content")
        if extra:
            payload["extra_content"] = extra
    # elif provider == "<next model>":  # add the model's own tool-turn quirk here
    return payload


def _assistant_tool_msg(message, tool_calls, provider: str = "openai") -> dict:
    """Rebuild the model's tool-call turn as a plain dict to feed back."""
    return {
        "role": "assistant",
        "content": getattr(message, "content", None) or "",
        "tool_calls": [_tool_call_payload(tc, provider) for tc in tool_calls],
    }


def _execute_tool(tool_call, *, contact, channel: str, session=None) -> dict:
    """Run one tool call via the registry. Never raises — the registry turns any
    failure into an `ok: False` result the model reads back."""
    from aicore import tools as tools_mod

    name = getattr(tool_call.function, "name", "") or ""
    try:
        args = json.loads(tool_call.function.arguments or "{}")
        if not isinstance(args, dict):
            args = {}
    except Exception:
        args = {}
    return tools_mod.dispatch(name, args, contact=contact, channel=channel,
                              session=session)


def _run_tool_loop(client, base_create: dict, tools: list, *, contact, channel: str,
                   provider: str = "openai", usage_acc: list | None = None) -> str:
    """Bounded function-calling loop. Passes tools + tool_choice="auto"; while
    the model returns tool_calls we execute them, feed each result back and
    re-call, up to MAX_TOOL_ROUNDS. The first call is retried ONCE without tools
    if the provider rejects the `tools=` param, so tool support never breaks a
    reply. Returns the final assistant prose (unstripped — the caller strips).
    Every completion's resp.usage is appended to `usage_acc` (billing ledger)."""
    convo = base_create["messages"]  # live list; tool turns are appended to it
    create = {**base_create, "tools": tools, "tool_choice": "auto"}
    acc = usage_acc if usage_acc is not None else []
    # One id per reply generation: the confirmation gate uses it to refuse
    # state-changing tools in the same round that asked the customer to confirm.
    session = uuid.uuid4().hex

    try:
        resp = client.chat.completions.create(**create)
        acc.append(getattr(resp, "usage", None))
    except Exception as exc:
        # Provider that 400s on an unknown `tools` param — one plain retry.
        logger.warning("aicore: provider rejected tools= (%s); replying without tools",
                       type(exc).__name__)
        resp = client.chat.completions.create(**base_create)
        acc.append(getattr(resp, "usage", None))
        return resp.choices[0].message.content or ""

    for _round in range(MAX_TOOL_ROUNDS):
        message = resp.choices[0].message
        tool_calls = getattr(message, "tool_calls", None) or []
        if not tool_calls:
            return message.content or ""
        convo.append(_assistant_tool_msg(message, tool_calls, provider))
        for tc in tool_calls:
            result = _execute_tool(tc, contact=contact, channel=channel,
                                   session=session)
            convo.append({"role": "tool", "tool_call_id": getattr(tc, "id", ""),
                          "content": json.dumps(result, default=str)})
        resp = client.chat.completions.create(**create)
        acc.append(getattr(resp, "usage", None))

    # Cap hit while the model still wants tools: force a final tool-free reply.
    message = resp.choices[0].message
    if getattr(message, "tool_calls", None):
        logger.warning("aicore: tool loop hit MAX_TOOL_ROUNDS; forcing a plain reply")
        resp = client.chat.completions.create(**base_create)
        acc.append(getattr(resp, "usage", None))
        return resp.choices[0].message.content or ""
    return message.content or ""
