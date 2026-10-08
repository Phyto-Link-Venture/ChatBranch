/**
 * Pure text helpers for the conversation stream: call-ending cues, carrier
 * announcements, reasoning-leak scrubbing and what the TTS may speak. No
 * LiveKit imports, so the offline selftest exercises them directly.
 */
import type { PronunciationTerm } from './storage.js';

/** ISO-8601 UTC timestamp in the "+00:00" form the call records have always used. */
export const nowIso = (): string => new Date().toISOString().replace('Z', '+00:00');

/**
 * Bucket a call's timestamp into a coarse time-of-day label. Heuristic on the
 * UTC hour (no per-tenant timezone concept exists yet) — good enough for
 * reporting, not a precise local-time classification.
 */
export function callWindow(isoTs: string): string {
  const ms = Date.parse(isoTs);
  if (Number.isNaN(ms)) return 'unknown';
  const hour = new Date(ms).getUTCHours();
  if (hour < 9) return 'before_work';
  if (hour < 12 || (hour >= 14 && hour < 18)) return 'working_hour';
  if (hour < 14) return 'lunch_time';
  if (hour < 21) return 'after_work';
  return 'before_10pm';
}

// "end_call" as a standalone token (letters/digits/underscore on neither side,
// Unicode-aware like Python's \b), optionally followed only by non-word chars.
const END_CALL_BRACES = /\{\s*end_call\s*\}/u;
const END_CALL_TRAILING = /(?<![\p{L}\p{N}_])end_call(?![\p{L}\p{N}_])[^\p{L}\p{N}_]*$/u;
const END_CALL_SCRUB = /\{?\s*end_call\s*\}?/gu;

/**
 * The model sometimes SAYS the tool name instead of invoking it ("Goodbye!
 * {end_call}"). Trigger ONLY on the braces form or a trailing bare token: a
 * plain substring match once killed a call mid-identity-check when the brain
 * merely mentioned the token mid-sentence.
 */
export function hasEndCallMarker(text: string): boolean {
  const lower = text.toLowerCase();
  return END_CALL_BRACES.test(lower) || END_CALL_TRAILING.test(lower);
}

/** Remove the marker so operators never see it in the transcript. */
export const scrubEndCallMarker = (text: string): string => text.replace(END_CALL_SCRUB, '').trim() || 'Goodbye!';

/**
 * A clear closing from the AGENT. When the model wraps up naturally but forgets
 * to call end_call, the silence watchdog would otherwise RE-ASK and re-open a
 * call the agent already closed. Terminal phrases only (EN + a little BM/ZH);
 * bare "bye" is excluded to avoid mid-word false hits.
 */
export const GOODBYE_RE =
  /\b(?:have a (?:good|great|nice|lovely|wonderful|blessed) day|have a good one|good\s?bye|take care|selamat tinggal|jumpa lagi)\b|再见|拜拜/iu;

/**
 * Carrier/network system messages — voicemail prompts, "number not in service"
 * — that some routes answer WITH before (or instead of) ringing the callee. SIP
 * still reports the leg "active", so the agent would converse with a recording.
 * Matched only against the FIRST user turn (a real caller could say these words
 * later) — a fixed carrier phrase prefix, not a general filter.
 */
export const CARRIER_MSG_RE =
  /您所拨打的|无法接通|请稍后再拨|留下您的信息|留下您的讯息|暂时无法|number you (?:have )?dialed|is not in service|please leave a message|nombor yang anda dail/iu;

const THINK_OPEN = '<think>';
const THINK_CLOSE = '</think>';

/**
 * Stateful stream scrubber for reasoning leaks: drops <think>…</think> blocks
 * (and bare tag fragments, incl. tags split across stream chunks) before the
 * text reaches TTS. ILMU still occasionally emits thinking even with
 * reasoning_effort=low (the greeting once spoke '…from D</think>').
 */
export function makeThinkFilter(): (piece: string) => string {
  let inThink = false;
  let carry = '';
  return (piece: string): string => {
    let text = carry + piece;
    carry = '';
    const out: string[] = [];
    while (text) {
      const tag = inThink ? THINK_CLOSE : THINK_OPEN;
      const idx = text.indexOf(tag);
      if (idx === -1) {
        // also scrub a stray closing tag outside a think block (the
        // missing-open-tag leak shape) so it is never spoken
        if (!inThink) {
          const stray = text.indexOf(THINK_CLOSE);
          if (stray !== -1) {
            out.push(text.slice(0, stray));
            text = text.slice(stray + THINK_CLOSE.length);
            continue;
          }
        }
        // hold back a tail that could be the start of a split tag
        let hold = 0;
        for (let k = Math.min(text.length, THINK_CLOSE.length - 1); k > 0; k--) {
          const tail = text.slice(-k);
          if (THINK_OPEN.startsWith(tail) || THINK_CLOSE.startsWith(tail)) {
            hold = k;
            break;
          }
        }
        if (!inThink) out.push(text.slice(0, text.length - hold));
        carry = hold ? text.slice(text.length - hold) : '';
        break;
      }
      if (inThink) {
        text = text.slice(idx + THINK_CLOSE.length);
        inThink = false;
      } else {
        out.push(text.slice(0, idx));
        text = text.slice(idx + THINK_OPEN.length);
        inThink = true;
      }
    }
    return out.join('');
  };
}

const escapeRegExp = (s: string): string => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

/**
 * Apply the pronunciation lexicon: every whole-word, case-insensitive
 * occurrence of a term is replaced by its respelling. Longer terms win, so
 * "Duit Gini Sdn Bhd" is respelled before "Duit Gini".
 */
export function applyPronunciation(text: string, terms: readonly PronunciationTerm[]): string {
  if (!terms.length || !text) return text;
  const byTerm = new Map(terms.map((t) => [t.term.toLowerCase(), t.alias]));
  const pattern = [...byTerm.keys()]
    .sort((a, b) => b.length - a.length)
    .map(escapeRegExp)
    .join('|');
  const re = new RegExp(`(?<![\\p{L}\\p{N}_])(?:${pattern})(?![\\p{L}\\p{N}_])`, 'giu');
  return text.replace(re, (match) => byTerm.get(match.toLowerCase()) ?? match);
}

/** What the TTS may say: never the end_call marker, and lexicon respellings applied. */
export const speakable = (text: string, terms: readonly PronunciationTerm[]): string =>
  applyPronunciation(text.replace(END_CALL_SCRUB, ''), terms);

// The marker and lexicon terms must be seen whole: either could be split across
// two stream chunks. Text is therefore released at clause ends.
const CLAUSE_END = /[.,!?;:。，！？；：\n]/gu;

/**
 * Rewrite a streamed TTS input through speakable(), releasing text at clause
 * boundaries (TTS engines synthesize per clause/sentence anyway, so this adds
 * no audible latency) and flushing the remainder when the stream ends.
 */
export async function* speakableStream(
  text: AsyncIterable<string>,
  terms: readonly PronunciationTerm[],
): AsyncGenerator<string> {
  let pending = '';
  for await (const chunk of text) {
    pending += chunk;
    let cut = -1;
    for (const m of pending.matchAll(CLAUSE_END)) cut = m.index + m[0].length;
    if (cut > 0) {
      const out = speakable(pending.slice(0, cut), terms);
      if (out.trim()) yield out;
      pending = pending.slice(cut);
    }
  }
  const rest = speakable(pending, terms);
  if (rest.trim()) yield rest;
}
