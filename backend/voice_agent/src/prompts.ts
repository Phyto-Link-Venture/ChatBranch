/**
 * Prompt builders: the voice agent's persona + the end-of-call summary extraction.
 *
 * Vertical-neutral by design. The agent knows three things about a call: WHO it
 * is talking to (`contact_name`, `company_name`, `language`), WHAT the call is
 * about (a Record — order, shipment, invoice, booking, ticket — carried in the
 * `record_*` metadata keys), and WHAT the operator wants out of it (`objective`).
 * A deployment customises behaviour through the dispatch metadata (persona,
 * skills, knowledge), never by editing this file.
 */
import type { CallContact } from './metadata.js';
import type { ContactMemory } from './storage.js';

const DAYS = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];
const MONTHS = [
  'January', 'February', 'March', 'April', 'May', 'June',
  'July', 'August', 'September', 'October', 'November', 'December',
];

/**
 * Human-readable current date ("Thursday, 08 October 2026"). The model otherwise
 * assumes its training-cutoff date, which breaks any date reasoning ("next
 * Friday", due dates, etc.).
 */
export function today(now: Date = new Date()): string {
  const day = String(now.getDate()).padStart(2, '0');
  return `${DAYS[now.getDay()]}, ${day} ${MONTHS[now.getMonth()]} ${now.getFullYear()}`;
}

/**
 * Demographic language hint -> human-readable name for the prompt. Adding a
 * language = one entry here (session.ts derives its STT-hint whitelist from
 * this map) + keep the API's Contact.Language values and frontend
 * lib/languages.ts in step. "auto" is deliberately NOT here: an auto-detect
 * contact carries no hint, which routes to the detect-and-follow branch below
 * and leaves STT on auto-detect too.
 */
export const LANGUAGE_NAMES: Readonly<Record<string, string>> = {
  en: 'English',
  ms: 'Malay (Bahasa Malaysia)',
  zh: 'Mandarin Chinese',
};
export const SUPPORTED_LANGUAGES: readonly string[] = Object.keys(LANGUAGE_NAMES);

/** True when `lang` is one of the languages the prompts and STT hints know. */
export function isSupportedLanguage(lang: string | undefined): lang is string {
  return lang !== undefined && SUPPORTED_LANGUAGES.includes(lang);
}

/**
 * Tell the agent which language to open in and that it may switch.
 *
 * The switch rule is deliberately forceful: a soft "follow them" version left
 * the model with English inertia — it once ANNOUNCED "I'll switch to Malay" in
 * English and kept drifting back, while the person repeated "saya tak faham".
 */
export function languageDirective(contact: Pick<CallContact, 'language'>): string {
  const hintName = contact.language ? LANGUAGE_NAMES[contact.language] : undefined;
  const names = Object.values(LANGUAGE_NAMES);
  const spokenList = `${names.slice(0, -1).join(', ')}, or ${names[names.length - 1]}`;
  let switchRule =
    "LANGUAGE RULE — apply on EVERY turn: reply in the language of the caller's MOST RECENT " +
    'sentence, no matter what language earlier turns used. Switch instantly and completely — ' +
    'never announce a switch, and NEVER announce it in the old language; just answer in theirs. ' +
    'Phone transcription garbles words, so judge the language from whatever fragments you get ' +
    "(e.g. anything with 'boleh/tak/saya/faham' is Malay — answer in Malay). If they say they " +
    "don't understand you (in any language), your language is wrong: repeat the point in THEIR " +
    'language, simpler and shorter. NEVER mix languages or scripts inside one reply (no Chinese ' +
    'characters in a Malay sentence, no stray English mid-Malay) — each reply is ONE language, ' +
    'start to finish; light Manglish particles are the only exception. Chinese replies must be ' +
    'written in Chinese characters (汉字), never pinyin romanization.';
  // This is a SPOKEN call: whatever is written is read aloud by TTS, and TTS
  // mangles digits and currency codes ("RM5,000" came out garbled mid-Malay).
  // Numbers must be written the way a person would SAY them.
  switchRule +=
    ' NUMBER RULE — you are being spoken aloud, never read on screen. Write EVERY number as ' +
    'words in the language you are replying in, never as digits or a currency code. Money: ' +
    "Malay 'lima ribu ringgit' / 'lima juta ringgit', English 'five thousand ringgit', " +
    "Chinese '五千令吉' — NOT 'RM5,000', 'RM5000' or '5,000'. The same applies to days, dates " +
    "and times: Malay 'sepuluh hari', 'pukul dua belas tengah hari'; English 'ten days', " +
    "'twelve noon'. Round naturally when speaking — say 'lima ribu ringgit' rather than " +
    'spelling out stray cents, unless the exact sen genuinely matters. Reference numbers ' +
    '(order ids, tracking numbers) are read out digit by digit, slowly.';
  if (hintName) {
    return (
      `The caller's preferred language is ${hintName}. Conduct the whole call in ${hintName} ` +
      `— open in it and keep using it by default. They may also speak ${spokenList}. ${switchRule}`
    );
  }
  return `Begin in English. The person may speak any of: ${spokenList}. ${switchRule}`;
}

/**
 * Render the file-backed memory/<contact_id>.md subset (best_contact/notes)
 * into a short prompt block — what the CRM can't derive, carried forward from
 * prior calls.
 */
export function memoryDirective(memory: ContactMemory | null): string {
  if (!memory) return '';
  const lines: string[] = [];
  if (memory.best_contact) lines.push(`They are usually best reached: ${memory.best_contact}.`);
  if (memory.notes.length) {
    lines.push('Known context from prior contact:');
    lines.push(...memory.notes.map((n) => `- ${n}`));
  }
  if (!lines.length) return '';
  return '\n\nWhat you already know about this person (from past contact):\n' + lines.join('\n');
}

/**
 * The cross-channel contact memory, arriving as a PLAIN STRING in the dispatch
 * metadata — this process never touches the database, so the block is composed
 * API-side and passed in.
 */
export function metadataMemoryBlock(contact: Pick<CallContact, 'memory'>): string {
  const mem = String(contact.memory ?? '').trim();
  if (!mem) return '';
  return (
    '\n\nCONTACT MEMORY — what previous conversations (calls and chats) taught us ' +
    'about this person. Use it naturally in the conversation; never recite it or ' +
    'mention that you keep records:\n' +
    mem
  );
}

/**
 * COMPANY KNOWLEDGE — the operator-authored knowledge base, assembled and
 * budgeted API-side for the same reason as the memory block. Hours, policies,
 * FAQs, live promotions and sudden notices — the same facts the chat bot
 * answers from.
 */
export function metadataKnowledgeBlock(contact: Pick<CallContact, 'knowledge'>): string {
  const block = String(contact.knowledge ?? '').trim();
  if (!block) return '';
  return (
    '\n\nCOMPANY KNOWLEDGE — facts, policies and current notices the business has ' +
    'published for you. Treat them as authoritative and answer from them directly. ' +
    'Anything NOT listed here you do not know: say the team will confirm it rather ' +
    'than guessing.\n' +
    block
  );
}

/**
 * SKILLS — the operator's playbooks (Settings → AI Skills), the same section
 * every chat channel receives. Each entry says when it applies and what to do.
 */
export function metadataSkillsBlock(contact: Pick<CallContact, 'skills'>): string {
  const block = String(contact.skills ?? '').trim();
  if (!block) return '';
  return (
    '\n\nSKILLS — playbooks the business wrote for situations that come up on calls. ' +
    'When one applies, follow it; the strict rules above still win.\n' +
    block
  );
}

/**
 * What the record's `type` is called out loud, so the agent doesn't have to
 * guess a noun for a vertical it has never seen.
 */
export const RECORD_NOUNS: Readonly<Record<string, string>> = {
  order: 'order',
  shipment: 'delivery',
  invoice: 'invoice',
  booking: 'booking',
  ticket: 'support ticket',
  custom: 'request',
};

/**
 * The business object this call is about, rendered as prompt context. Empty
 * when the call carries no record (a pure identity/greeting call).
 */
export function recordBlock(contact: CallContact): string {
  const recType = String(contact.record_type ?? '').trim();
  if (!recType && !contact.record_id) return '';
  const noun = RECORD_NOUNS[recType] ?? 'request';
  const lines = [`What this call is about — a ${noun}:`];
  if (contact.record_id) lines.push(`- Reference: ${contact.record_id}`);
  if (contact.record_title) lines.push(`- Description: ${contact.record_title}`);
  if (contact.record_status) lines.push(`- Current status: ${contact.record_status}`);
  if (contact.record_amount) lines.push(`- Amount: ${contact.record_amount}`);
  if (contact.record_due_date) lines.push(`- Key date: ${contact.record_due_date}`);
  const data = contact.record_data;
  if (data && typeof data === 'object' && !Array.isArray(data) && Object.keys(data).length) {
    const detail = Object.entries(data)
      .slice(0, 8)
      .map(([k, v]) => `${k}: ${pyStr(v)}`)
      .join('; ');
    lines.push(`- Details: ${detail.slice(0, 400)}`);
  }
  return lines.join('\n');
}

/** Scalar record details read as they always have to the model (None/True/False, Python's str()). */
function pyStr(v: unknown): string {
  if (v === null || v === undefined) return 'None';
  if (typeof v === 'boolean') return v ? 'True' : 'False';
  return typeof v === 'object' ? JSON.stringify(v) : String(v);
}

export const DEFAULT_OBJECTIVE =
  'Confirm you are speaking with the right person, give them the update this ' +
  'call is about, answer whatever they ask about it, and capture a clear next ' +
  'step (a confirmed date, a decision, or a request you must pass on).';

/**
 * The operator-supplied goal for this call (dispatch metadata `objective`, set
 * by the flow builder / dial API); falls back to a neutral confirm-and-update goal.
 */
export function objectiveDirective(contact: Pick<CallContact, 'objective'>): string {
  return String(contact.objective ?? '').trim() || DEFAULT_OBJECTIVE;
}

/**
 * Deterministic dispositions — pick exactly one, based on what actually
 * happened by the end of the call:
 *   resolved       — the reason for the call was fully handled on this call
 *   scheduled      — a concrete follow-up (date/time/action) was agreed
 *   info_provided  — the person was informed; nothing further was needed
 *   declined       — the person refused, opted out, or would not engage
 *   unreachable    — wrong person, voicemail, or the call ended with nothing said
 */
export const OUTCOME_LABELS = ['resolved', 'scheduled', 'info_provided', 'declined', 'unreachable'] as const;
export type Outcome = (typeof OUTCOME_LABELS)[number];

/** Fixed sentiment buckets — keeps the field aggregatable across calls. */
export const SENTIMENT_LABELS = ['Cooperative', 'Neutral', 'Defensive', 'Avoidant'] as const;

/** A Python-style list literal, the form the summary prompt has always shown the model. */
const listLiteral = (items: readonly string[]): string => `[${items.map((i) => `'${i}'`).join(', ')}]`;

/** System instructions for the voice agent. */
export function agentInstructions(contact: CallContact, memory: ContactMemory | null = null): string {
  const block = recordBlock(contact);
  const recordSection = block ? `\n${block}\n` : '\n';
  return `You are a polite assistant calling on behalf of ${contact.company_name}.
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
- Mirror the caller's register. When they're casual, be casual the LOCAL way: Manglish / bahasa pasar is welcome ("bro", "boss", "boleh", "lah"), and light English-Malay code-switching is natural. When they're formal, stay formal. Match them; never force slang on a formal caller.

Today's date is ${today()}. Use it for any date reasoning (e.g. interpreting "next Friday" or "end of the month").

Who you are calling:
- Name: ${contact.contact_name}
${recordSection}
Your objective for this call:
${objectiveDirective(contact)}

How to run the call:
1. Confirm you are speaking with ${contact.contact_name}. If the person answering says they are not
   that person, apologize briefly, do NOT discuss any details of the matter, and end the call.
2. Say plainly why you are calling, in one short sentence.
3. Answer their questions using ONLY the facts above. If you don't have a fact, say you'll have the
   team confirm it — never guess a number, date, price or policy.
4. Capture the next step: a confirmed date/time, a decision, or what they want us to do.
5. Only escalate to a human if they explicitly ask for one, or are clearly angry, distressed or
   complaining. Do NOT reflexively offer "let me get a human" just because a question is awkward —
   handle it yourself, calmly, like a real person would.
6. Once the next step is clear, say ONE brief natural goodbye ("Thank you, have a good day") and call
   the \`end_call\` tool to hang up. Do not keep talking after the next step is clear.
7. If they try to end the call BEFORE a next step is confirmed, make ONE more direct attempt to get a
   real answer ("just to confirm, is there anything you'd like us to do?"). Only end once you have a
   next step, or they make clear they won't engage further.
8. NEVER end a conversation with only spoken words — whenever the call is over, for ANY reason, you
   MUST call the \`end_call\` tool. IMPORTANT: end_call is a FUNCTION you invoke, not a word — never say
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
${languageDirective(contact)}${memoryDirective(memory)}${metadataMemoryBlock(contact)}${metadataKnowledgeBlock(contact)}${metadataSkillsBlock(contact)}`;
}

/** Instruction for the agent's very first spoken turn (LLM-generated opening). */
export function openingLine(contact: CallContact): string {
  const hintName = (contact.language && LANGUAGE_NAMES[contact.language]) || 'English';
  return (
    `Greet the person warmly in ${hintName}, say you're calling from ${contact.company_name}, ` +
    `and politely ask to confirm you're speaking with ${contact.contact_name}. ` +
    'Keep it to one short, friendly sentence. If they reply in a different language ' +
    '(English, Malay, or Chinese), switch to it.'
  );
}

/**
 * Speakable greeting per language — said via TTS directly (no LLM turn) so the
 * first sentence lands ~0.2s after answer instead of waiting ~3-4s for the LLM
 * to write it. The LLM takes over from the person's first reply.
 */
const OPENING_TEMPLATES: Readonly<Record<string, string>> = {
  en: "Hi, this is a call from {company}. May I confirm I'm speaking with {name}?",
  ms: 'Hai, ini panggilan daripada {company}. Boleh saya sahkan saya bercakap dengan {name}?',
  zh: '您好，这里是{company}的来电。请问您是{name}吗？',
};

/** The agent's first sentence as ready-to-speak text (localized), for session.say(). */
export function openingText(contact: CallContact): string {
  const template = (contact.language && OPENING_TEMPLATES[contact.language]) || OPENING_TEMPLATES.en!;
  return template.replace(/\{(company|name)\}/g, (_, key: string) =>
    key === 'company' ? contact.company_name : contact.contact_name,
  );
}

/** Prompt for the structured post-call summary (expects a JSON object back). */
export function summaryPrompt(contact: CallContact, transcript: string): string {
  const block = recordBlock(contact);
  const context = block ? `\n${block}\n` : '\n';
  return `You are analyzing a phone call made on behalf of ${contact.company_name}.
Person called: ${contact.contact_name}.
${context}
Today's date is ${today()}. Convert any relative date they gave (e.g. "next Friday", "next month",
"today", "now" -> today's actual date above, "tomorrow" -> the day after) to an absolute YYYY-MM-DD
using this. Pay close attention to WHICH commitment each relative date belongs to when several dates
are discussed.

Here is the call transcript:
---
${transcript || '(no transcript captured)'}
---

They may have spoken English, Malay, or Chinese. ALWAYS write every field in ENGLISH,
translating anything they said (including any agreed date) into English.

Return a JSON object with EXACTLY these keys:
- "summary": a 1-2 sentence plain-English summary of what happened.
- "outcome": one of ${listLiteral(OUTCOME_LABELS)}. Pick the single best fit.
- "followup_date": the date they FINALLY, CONFIRMED agreed to for the next step, IN ENGLISH.
  Translate from the spoken language (e.g. Chinese "下个月" -> "next month", Malay "minggu depan" ->
  "next week"). If they gave a concrete calendar date, use ISO format YYYY-MM-DD. Something floated
  mid-conversation and later dropped does NOT count as confirmed — only the actual final agreement
  does. If there was no final confirmed commitment, null.
- "followup_action": a SHORT English phrase for WHAT was agreed to happen on that date (e.g.
  "contact will collect the parcel", "we call back with the quote"). null when followup_date is null.
- "needs_human": true if they asked for a human, were angry/distressed, complained, or the situation
  needs escalation; otherwise false.
- "sentiment_score": their overall demeanor across the call — one of ${listLiteral(SENTIMENT_LABELS)}.
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
Output ONLY the JSON object, nothing else.`;
}
