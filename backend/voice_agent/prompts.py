"""Prompt builders: the voice agent's persona + the end-of-call summary extraction.

Vertical-neutral by design. The agent knows three things about a call: WHO it
is talking to (`contact_name`, `company_name`, `language`), WHAT the call is
about (a `Record` — order, shipment, invoice, booking, ticket — carried in the
`record_*` metadata keys), and WHAT the operator wants out of it (`objective`).
A deployment customises behaviour through the dispatch metadata and the
Settings → AI Persona override, never by editing this file.
"""

from datetime import datetime


def _today() -> str:
    """Human-readable current date. The model otherwise assumes its training-cutoff
    date (~2023), which breaks any date reasoning ('next Friday', due dates, etc.)."""
    return datetime.now().strftime("%A, %d %B %Y")


# Demographic language hint -> human-readable name for the prompt. Adding a
# language = one entry here (agent.py derives its STT-hint whitelist from
# this map) + keep crm.Contact.SUPPORTED_LANGUAGES and frontend
# lib/languages.ts in step — the Django side can't import this file's twin
# and vice versa (the agent process is Django-free). "auto" is deliberately
# NOT in this map: an auto-detect contact carries no hint, which routes to
# the detect-and-follow branch below and leaves STT on auto-detect too.
LANGUAGE_NAMES = {
    "en": "English",
    "ms": "Malay (Bahasa Malaysia)",
    "zh": "Mandarin Chinese",
}
SUPPORTED_LANGUAGES = tuple(LANGUAGE_NAMES)


def _language_directive(contact: dict) -> str:
    """Tell the agent which language to open in and that it may switch.

    The switch rule is deliberately forceful: a soft "follow them" version
    left the model with English inertia — it once ANNOUNCED "I'll switch to
    Malay" in English and kept drifting back, while the person repeated
    "saya tak faham" (real call, 2026-07-21)."""
    hint = contact.get("language")
    hint_name = LANGUAGE_NAMES.get(hint)
    names = list(LANGUAGE_NAMES.values())
    spoken_list = ", ".join(names[:-1]) + f", or {names[-1]}"
    switch_rule = (
        "LANGUAGE RULE — apply on EVERY turn: reply in the language of the caller's MOST RECENT "
        "sentence, no matter what language earlier turns used. Switch instantly and completely — "
        "never announce a switch, and NEVER announce it in the old language; just answer in theirs. "
        "Phone transcription garbles words, so judge the language from whatever fragments you get "
        "(e.g. anything with 'boleh/tak/saya/faham' is Malay — answer in Malay). If they say they "
        "don't understand you (in any language), your language is wrong: repeat the point in THEIR "
        "language, simpler and shorter. NEVER mix languages or scripts inside one reply (no Chinese "
        "characters in a Malay sentence, no stray English mid-Malay) — each reply is ONE language, "
        "start to finish; light Manglish particles are the only exception. Chinese replies must be "
        "written in Chinese characters (汉字), never pinyin romanization."
    )
    # This is a SPOKEN call: whatever you write is read aloud by TTS, and TTS
    # mangles digits and currency codes — "RM5,000" came out garbled mid-Malay
    # on a real call (2026-07-23). Numbers must be written the way a person
    # would SAY them, in the language of the reply.
    number_rule = (
        " NUMBER RULE — you are being spoken aloud, never read on screen. Write EVERY number as "
        "words in the language you are replying in, never as digits or a currency code. Money: "
        "Malay 'lima ribu ringgit' / 'lima juta ringgit', English 'five thousand ringgit', "
        "Chinese '五千令吉' — NOT 'RM5,000', 'RM5000' or '5,000'. The same applies to days, dates "
        "and times: Malay 'sepuluh hari', 'pukul dua belas tengah hari'; English 'ten days', "
        "'twelve noon'. Round naturally when speaking — say 'lima ribu ringgit' rather than "
        "spelling out stray cents, unless the exact sen genuinely matters. Reference numbers "
        "(order ids, tracking numbers) are read out digit by digit, slowly."
    )
    switch_rule += number_rule
    if hint_name:
        return (
            f"The caller's preferred language is {hint_name}. Conduct the whole call in {hint_name} "
            f"— open in it and keep using it by default. They may also speak {spoken_list}. {switch_rule}"
        )
    return (
        f"Begin in English. The person may speak any of: {spoken_list}. {switch_rule}"
    )


def _memory_directive(memory: dict | None) -> str:
    """Render the file-backed MEMORY.md subset (language/best_contact/notes)
    into a short prompt block — what the CRM can't derive, carried forward from
    prior contact."""
    if not memory:
        return ""
    lines = []
    if memory.get("best_contact"):
        lines.append(f"They are usually best reached: {memory['best_contact']}.")
    notes = memory.get("notes") or []
    if notes:
        lines.append("Known context from prior contact:")
        lines.extend(f"- {n}" for n in notes)
    if not lines:
        return ""
    return "\n\nWhat you already know about this person (from past contact):\n" + "\n".join(lines)


def _metadata_memory_block(contact: dict) -> str:
    """The cross-channel contact memory (aicore/memory.build_context output),
    arriving as a PLAIN STRING in the dispatch metadata — this process stays
    Django-free, so the block is composed on the Django side and passed in."""
    mem = str(contact.get("memory") or "").strip()
    if not mem:
        return ""
    return (
        "\n\nCONTACT MEMORY — what previous conversations (calls and chats) taught us "
        "about this person. Use it naturally in the conversation; never recite it or "
        "mention that you keep records:\n" + mem
    )


def _metadata_knowledge_block(contact: dict) -> str:
    """COMPANY KNOWLEDGE — the operator-authored knowledge base
    (aicore/knowledge.build_knowledge_context output), arriving as a PLAIN
    STRING in the dispatch metadata for the same reason as the memory block:
    this process is Django-free, so the block is assembled and budgeted on the
    Django side. Hours, policies, FAQs, live promotions and sudden notices —
    the same facts the chat bot answers from."""
    block = str(contact.get("knowledge") or "").strip()
    if not block:
        return ""
    return (
        "\n\nCOMPANY KNOWLEDGE — facts, policies and current notices the business has "
        "published for you. Treat them as authoritative and answer from them directly. "
        "Anything NOT listed here you do not know: say the team will confirm it rather "
        "than guessing.\n" + block
    )


# What the record's `type` is called out loud, so the agent doesn't have to
# guess a noun for a vertical it has never seen.
RECORD_NOUNS = {
    "order": "order",
    "shipment": "delivery",
    "invoice": "invoice",
    "booking": "booking",
    "ticket": "support ticket",
    "custom": "request",
}


def _record_block(contact: dict) -> str:
    """The business object this call is about, rendered as prompt context.
    Empty when the call carries no record (a pure identity/greeting call)."""
    rec_type = str(contact.get("record_type") or "").strip()
    if not rec_type and not contact.get("record_id"):
        return ""
    noun = RECORD_NOUNS.get(rec_type, "request")
    lines = [f"What this call is about — a {noun}:"]
    if contact.get("record_id"):
        lines.append(f"- Reference: {contact['record_id']}")
    if contact.get("record_title"):
        lines.append(f"- Description: {contact['record_title']}")
    if contact.get("record_status"):
        lines.append(f"- Current status: {contact['record_status']}")
    if contact.get("record_amount"):
        lines.append(f"- Amount: {contact['record_amount']}")
    if contact.get("record_due_date"):
        lines.append(f"- Key date: {contact['record_due_date']}")
    data = contact.get("record_data")
    if isinstance(data, dict) and data:
        detail = "; ".join(f"{k}: {v}" for k, v in list(data.items())[:8])
        lines.append(f"- Details: {detail[:400]}")
    return "\n".join(lines)


DEFAULT_OBJECTIVE = (
    "Confirm you are speaking with the right person, give them the update this "
    "call is about, answer whatever they ask about it, and capture a clear next "
    "step (a confirmed date, a decision, or a request you must pass on)."
)


def _objective_directive(contact: dict) -> str:
    """The operator-supplied goal for this call. Set per call through the
    dispatch metadata `objective` key (flow builder / dial API); falls back to
    a neutral confirm-and-update goal."""
    return str(contact.get("objective") or "").strip() or DEFAULT_OBJECTIVE


# Deterministic dispositions — pick exactly one, based on what actually
# happened by the end of the call:
#   resolved       — the reason for the call was fully handled on this call
#   scheduled      — a concrete follow-up (date/time/action) was agreed
#   info_provided  — the person was informed; nothing further was needed
#   declined       — the person refused, opted out, or would not engage
#   unreachable    — wrong person, voicemail, or the call ended with nothing said
OUTCOME_LABELS = [
    "resolved",
    "scheduled",
    "info_provided",
    "declined",
    "unreachable",
]

# Fixed sentiment buckets — keeps the field aggregatable across calls.
SENTIMENT_LABELS = ["Cooperative", "Neutral", "Defensive", "Avoidant"]


def agent_instructions(contact: dict, memory: dict | None = None) -> str:
    """System instructions for the realtime voice agent."""
    record_block = _record_block(contact)
    record_section = f"\n{record_block}\n" if record_block else "\n"
    return f"""You are a polite assistant calling on behalf of {contact['company_name']}.
You are on a live phone-style voice call. Keep every reply SHORT — ideally ONE brief sentence, like a real phone call. Never monologue or over-explain.

Speaking style — sound like a real person, not a script:
- The MOMENT they finish talking, START your reply with a short, natural filler so they never hear dead air while you gather the answer — e.g. "Mm-hmm,", "Right,", "Okay,", "I see,", "Got it," "Let me see," — then give your one short answer. This is important: lead with the filler, always.
- Use contractions and everyday words ("I'm", "you've", "no worries").
- Vary your sentence length and rhythm; don't sound robotic or read a list.
- Keep it to one thought per turn; don't stack multiple sentences or explain more than asked.
- Warm, calm, friendly tone — like a helpful person, never a machine.
- Occasional light filler is fine ("so", "alright", "just checking") but don't overdo it.
- React to what they actually say instead of pushing a fixed sequence.
- Never spell out URLs awkwardly or read symbols literally; speak naturally.
- Mirror the caller's register. When they're casual, be casual the LOCAL way: \
Manglish / bahasa pasar is welcome ("bro", "boss", "boleh", "lah"), and light \
English-Malay code-switching is natural. When they're formal, stay formal. \
Match them; never force slang on a formal caller.

Today's date is {_today()}. Use it for any date reasoning (e.g. interpreting "next Friday" or "end of the month").

Who you are calling:
- Name: {contact['contact_name']}
{record_section}
Your objective for this call:
{_objective_directive(contact)}

How to run the call:
1. Confirm you are speaking with {contact['contact_name']}. If the person answering says they are not
   that person, apologize briefly, do NOT discuss any details of the matter, and end the call.
2. Say plainly why you are calling, in one short sentence.
3. Answer their questions using ONLY the facts above. If you don't have a fact, say you'll have the
   team confirm it — never guess a number, date, price or policy.
4. Capture the next step: a confirmed date/time, a decision, or what they want us to do.
5. Only escalate to a human if they explicitly ask for one, or are clearly angry, distressed or
   complaining. Do NOT reflexively offer "let me get a human" just because a question is awkward —
   handle it yourself, calmly, like a real person would.
6. Once the next step is clear, say ONE brief natural goodbye ("Thank you, have a good day") and call
   the `end_call` tool to hang up. Do not keep talking after the next step is clear.
7. If they try to end the call BEFORE a next step is confirmed, make ONE more direct attempt to get a
   real answer ("just to confirm, is there anything you'd like us to do?"). Only end once you have a
   next step, or they make clear they won't engage further.
8. NEVER end a conversation with only spoken words — whenever the call is over, for ANY reason, you
   MUST call the `end_call` tool. IMPORTANT: end_call is a FUNCTION you invoke, not a word — never say
   or write "end_call" out loud, and NEVER announce the mechanics of hanging up ("let me end the call
   now", "I'll end the call") — they should only ever hear a normal human farewell.

Strict rules — never break these:
- If asked directly whether you are a human, say plainly that you are an automated assistant.
- Do NOT threaten, shame, harass or pressure.
- Do NOT ask for passwords, OTPs, card numbers or any sensitive financial detail.
- Do NOT invent commitments, prices, discounts, dates or policies. Only state what you were given.
- Do NOT contact or discuss this matter with family, employers or third parties.
- If they ask to stop being contacted, acknowledge it, confirm it will be actioned, and end the call.

Speak warmly and briefly. Do not monologue.

Language:
{_language_directive(contact)}{_memory_directive(memory)}{_metadata_memory_block(contact)}{_metadata_knowledge_block(contact)}"""


def opening_line(contact: dict) -> str:
    """Instruction for the agent's very first spoken turn."""
    hint_name = LANGUAGE_NAMES.get(contact.get("language"), "English")
    return (
        f"Greet the person warmly in {hint_name}, say you're calling from {contact['company_name']}, "
        f"and politely ask to confirm you're speaking with {contact['contact_name']}. "
        "Keep it to one short, friendly sentence. If they reply in a different language "
        "(English, Malay, or Chinese), switch to it."
    )


# Speakable greeting per language — said via TTS directly (no LLM turn) so the
# first sentence lands ~0.2s after answer instead of waiting ~3-4s for the LLM
# to write it. The greeting is fully scriptable (company + confirm name); the
# LLM takes over from the person's first reply and handles any language switch.
_OPENING_TEMPLATES = {
    "en": "Hi, this is a call from {company}. May I confirm I'm speaking with {name}?",
    "ms": "Hai, ini panggilan daripada {company}. Boleh saya sahkan saya bercakap dengan {name}?",
    "zh": "您好，这里是{company}的来电。请问您是{name}吗？",
}


def opening_text(contact: dict) -> str:
    """The agent's first spoken sentence as ready-to-speak text (localized),
    for session.say() — skips the LLM round-trip that opening_line() incurs."""
    tmpl = _OPENING_TEMPLATES.get(contact.get("language"), _OPENING_TEMPLATES["en"])
    return tmpl.format(company=contact["company_name"], name=contact["contact_name"])


def summary_prompt(contact: dict, transcript: str) -> str:
    """Prompt for the structured post-call summary (expects a JSON object back)."""
    record_block = _record_block(contact)
    context = f"\n{record_block}\n" if record_block else "\n"
    return f"""You are analyzing a phone call made on behalf of {contact['company_name']}.
Person called: {contact['contact_name']}.
{context}
Today's date is {_today()}. Convert any relative date they gave (e.g. "next Friday", "next month",
"today", "now" -> today's actual date above, "tomorrow" -> the day after) to an absolute YYYY-MM-DD
using this. Pay close attention to WHICH commitment each relative date belongs to when several dates
are discussed.

Here is the call transcript:
---
{transcript or "(no transcript captured)"}
---

They may have spoken English, Malay, or Chinese. ALWAYS write every field in ENGLISH,
translating anything they said (including any agreed date) into English.

Return a JSON object with EXACTLY these keys:
- "summary": a 1-2 sentence plain-English summary of what happened.
- "outcome": one of {OUTCOME_LABELS}. Pick the single best fit.
- "followup_date": the date they FINALLY, CONFIRMED agreed to for the next step, IN ENGLISH.
  Translate from the spoken language (e.g. Chinese "下个月" -> "next month", Malay "minggu depan" ->
  "next week"). If they gave a concrete calendar date, use ISO format YYYY-MM-DD. Something floated
  mid-conversation and later dropped does NOT count as confirmed — only the actual final agreement
  does. If there was no final confirmed commitment, null.
- "followup_action": a SHORT English phrase for WHAT was agreed to happen on that date (e.g.
  "contact will collect the parcel", "we call back with the quote"). null when followup_date is null.
- "needs_human": true if they asked for a human, were angry/distressed, complained, or the situation
  needs escalation; otherwise false.
- "sentiment_score": their overall demeanor across the call — one of {SENTIMENT_LABELS}.
  Judge from how they engaged, not just their words.
- "objection": a SHORT English phrase for the main objection or blocker they raised, or null if
  there was none.
- "language_used": the language they actually spoke in, as a code — "en", "ms", or "zh". If they
  code-switched, the one they used MOST. Default "en" if unclear. (Used to open the NEXT call in
  their language.)
- "best_contact": if they said anything about a better time or way to reach them (e.g. "call after
  6pm", "evenings", "WhatsApp is easier"), a SHORT English phrase capturing it; otherwise null.
- "memory_notes": an array of 0-3 SHORT durable facts worth recalling on the NEXT call — attitude,
  their situation (prefix unverified claims with "claims "), what worked or didn't, and any flag
  (do-not-call request, active dispute, vulnerability → human only). Each a terse phrase, not a full
  sentence. [] if nothing durable came up.

Rules:
- "resolved": the reason for the call was fully handled during this call — nothing is outstanding.
- "scheduled": a concrete follow-up was agreed (a date, a time, or an action either side will take).
- "info_provided": they were informed and had no further need — no follow-up was required or agreed.
- "declined": they refused, opted out, or would not engage with the reason for the call.
- "unreachable": wrong person, voicemail, or the call ended with effectively nothing said.
- If they asked for a human, were angry/distressed, or complained, set needs_human=true regardless
  of which outcome above applies — needs_human is independent of the call outcome.
Output ONLY the JSON object, nothing else."""
