/**
 * File IPC with the C# API — the agent never touches the database, ON PURPOSE.
 * Everything lives under config.DATA_DIR (CHATBRANCH_DATA_DIR), and every
 * shape here is read or written by ChatBranch.Data/Sidecars/VoiceAgentBridge.cs:
 *
 *   live/{room}.json        this call's transcript, rewritten per turn (API polls it)
 *   inbox/{room}.json       operator guide/takeover instructions (consume-once)
 *   calls.json              the array of finished call records
 *   memory/{contact}.md     per-contact durable notes, round-tripped with the API
 *   pronunciation.json      TTS lexicon (term → alias) + optional ElevenLabs dictionary
 *   recordings/{room}.wav   stereo call recording (recorder.ts)
 *
 * Writes are atomic (temp file + rename) so a reader never sees half a file.
 */
import { randomBytes } from 'node:crypto';
import { mkdirSync, readFileSync, renameSync, rmSync, writeFileSync, existsSync } from 'node:fs';
import path from 'node:path';
import * as config from './config.js';

/** One persisted conversation turn. `ts` is epoch seconds (per-turn time labels in /chats). */
export interface LiveTurn {
  role: 'user' | 'assistant';
  text: string;
  ts: number;
  /** Knowledge reference the turn was answered from; rendered by the dashboard when present. */
  kb?: string;
}

/** Token counts accumulated over one call, folded into the API's LlmUsage billing ledger. */
export interface LlmUsageTotals {
  prompt: number;
  completion: number;
  audio_prompt: number;
  audio_completion: number;
  model: string;
  provider: string;
  /** The end-of-call summary completion, billed separately. */
  summary?: { model: string; prompt: number; completion: number };
}

export interface LivePayload {
  turns: LiveTurn[];
  done: boolean;
  /** Who ended the call: agent | contact | system:silence | system:max_duration | … */
  ended_by?: string;
  llm_usage?: LlmUsageTotals;
}

/** An operator instruction pushed mid-call: guide (steer the next reply) or takeover (say verbatim). */
export interface Instruction {
  mode: 'guide' | 'takeover';
  text: string;
}

/** Room ids are server-generated tokens; keep only filename-safe characters. */
export const safeName = (callId: string): string => callId.replace(/[^A-Za-z0-9_-]/g, '');

const dataPath = (...parts: string[]): string => path.join(config.DATA_DIR, ...parts);
const livePath = (callId: string): string => dataPath('live', `${safeName(callId)}.json`);
const inboxPath = (callId: string): string => dataPath('inbox', `${safeName(callId)}.json`);
const callsPath = (): string => dataPath('calls.json');
const memoryPath = (contactId: number | string): string => dataPath('memory', `${safeName(String(contactId))}.md`);

/** Where this call's stereo recording goes (recordings/{room}.wav). */
export const recordingPath = (callId: string): string =>
  dataPath('recordings', `${callId.replace(/[^A-Za-z0-9_-]/g, '_')}.wav`);

/** Write via a sibling temp file + rename: readers see the old file or the new one, never half. */
export function writeAtomic(file: string, contents: string | Uint8Array): void {
  mkdirSync(path.dirname(file), { recursive: true });
  const tmp = `${file}.${process.pid}.${randomBytes(4).toString('hex')}.tmp`;
  writeFileSync(tmp, contents);
  renameSync(tmp, file);
}

function readJson(file: string): unknown {
  try {
    return JSON.parse(readFileSync(file, 'utf8'));
  } catch {
    return undefined; // missing or corrupt — callers degrade, never crash the call
  }
}

/**
 * Overwrite the live transcript for an in-progress call. The final write
 * (done=true) also carries `ended_by` (WHO ended it) and `llm_usage`.
 */
export function saveLive(
  callId: string,
  turns: LiveTurn[],
  final: { done?: boolean; endedBy?: string; llmUsage?: LlmUsageTotals } = {},
): void {
  const payload: LivePayload = { turns, done: final.done ?? false };
  if (final.endedBy) payload.ended_by = final.endedBy;
  if (final.llmUsage) payload.llm_usage = final.llmUsage;
  writeAtomic(livePath(callId), JSON.stringify(payload));
}

/**
 * The API drops this marker (live/{room}.operator-end) right before an
 * operator's End button deletes the room, so the caller-disconnect that
 * follows is recorded as the operator's hang-up, not the contact's.
 */
const operatorEndPath = (callId: string): string => dataPath('live', `${safeName(callId)}.operator-end`);

export function operatorEnded(callId: string): boolean {
  return existsSync(operatorEndPath(callId));
}

export function getLive(callId: string): LivePayload | null {
  const data = readJson(livePath(callId));
  return data && typeof data === 'object' ? (data as LivePayload) : null;
}

function loadCalls(): Record<string, unknown>[] {
  const data = readJson(callsPath());
  return Array.isArray(data) ? (data as Record<string, unknown>[]) : []; // corrupt → start fresh
}

/** Append one call record to calls.json (read-modify-write, atomically replaced). */
export function saveCall(record: Record<string, unknown>): void {
  const calls = loadCalls();
  calls.push(record);
  writeAtomic(callsPath(), JSON.stringify(calls, null, 2));
}

export function getCall(callId: string): Record<string, unknown> | null {
  return loadCalls().find((c) => c.call_id === callId) ?? null;
}

/**
 * Append one operator instruction (the API's writer, mirrored here for the
 * selftest and local tooling).
 */
export function pushInstruction(callId: string, instruction: Instruction): void {
  const existing = readJson(inboxPath(callId));
  const items = Array.isArray(existing) ? existing : existing ? [existing] : [];
  items.push(instruction);
  writeAtomic(inboxPath(callId), JSON.stringify(items));
}

function toInstruction(raw: unknown): Instruction | null {
  if (!raw || typeof raw !== 'object') return null;
  const item = raw as Record<string, unknown>;
  const text = typeof item.text === 'string' ? item.text : '';
  // `mode` is the documented key; `kind` is accepted from older API builds.
  const mode = item.mode ?? item.kind;
  return { mode: mode === 'takeover' ? 'takeover' : 'guide', text };
}

/**
 * Return all pending instructions and clear the inbox (consume-once). The file
 * is claimed by renaming it first, so an API write racing this read lands in a
 * fresh inbox instead of being deleted unread. Accepts a list or a single object.
 */
export function popInstructions(callId: string): Instruction[] {
  const file = inboxPath(callId);
  const claimed = `${file}.${process.pid}.claimed`;
  try {
    renameSync(file, claimed);
  } catch {
    return []; // nothing pending
  }
  const data = readJson(claimed);
  rmSync(claimed, { force: true });
  const items = Array.isArray(data) ? data : data ? [data] : [];
  return items.map(toInstruction).filter((i): i is Instruction => i !== null);
}

// ── Per-contact memory (memory/{contact_id}.md) ─────────────────────────────
// Lean frontmatter + "## Notes" bullets. preferred_voice / preferred_voice_provider
// are API-side fields this process never sets, but they MUST round-trip unchanged
// through parse → save → render, or a call here wipes the contact's remembered voice.

export interface ContactMemory {
  language: string | null;
  best_contact: string | null;
  preferred_voice: string | null;
  preferred_voice_provider: string | null;
  updated: string | null;
  notes: string[];
}

const FRONTMATTER_KEYS = ['language', 'best_contact', 'preferred_voice', 'preferred_voice_provider', 'updated'] as const;

const emptyMemory = (): ContactMemory => ({
  language: null,
  best_contact: null,
  preferred_voice: null,
  preferred_voice_provider: null,
  updated: null,
  notes: [],
});

export function parseMemoryMd(text: string): ContactMemory {
  const memory = emptyMemory();
  let inFrontmatter = false;
  let inNotes = false;
  for (const line of text.split(/\r?\n/)) {
    const stripped = line.trim();
    if (stripped === '---') {
      inFrontmatter = !inFrontmatter;
      continue;
    }
    if (inFrontmatter) {
      const idx = stripped.indexOf(':');
      if (idx !== -1) {
        const key = stripped.slice(0, idx).trim();
        if ((FRONTMATTER_KEYS as readonly string[]).includes(key)) {
          memory[key as (typeof FRONTMATTER_KEYS)[number]] = stripped.slice(idx + 1).trim() || null;
        }
      }
      continue;
    }
    if (stripped === '## Notes') {
      inNotes = true;
      continue;
    }
    if (inNotes && stripped.startsWith('- ')) memory.notes.push(stripped.slice(2).trim());
  }
  return memory;
}

export function renderMemoryMd(memory: ContactMemory): string {
  const lines = ['---'];
  for (const key of FRONTMATTER_KEYS) {
    if (memory[key]) lines.push(`${key}: ${memory[key]}`);
  }
  lines.push('---');
  if (memory.notes.length) {
    lines.push('', '## Notes', ...memory.notes.map((n) => `- ${n}`));
  }
  return lines.join('\n') + '\n';
}

export function loadMemory(contactId: number | string): ContactMemory | null {
  try {
    return parseMemoryMd(readFileSync(memoryPath(contactId), 'utf8'));
  } catch {
    return null;
  }
}

/** Fold one call's learnings into the contact's memory file (last 20 notes kept). */
export function saveMemory(
  contactId: number | string,
  update: { language?: string | null; bestContact?: string | null; newNotes?: string[] },
): void {
  const memory = loadMemory(contactId) ?? emptyMemory();
  if (update.language) memory.language = update.language;
  if (update.bestContact) memory.best_contact = update.bestContact;
  memory.updated = new Date().toISOString().slice(0, 10);
  for (const raw of update.newNotes ?? []) {
    const note = String(raw).trim();
    if (note && !memory.notes.includes(note)) memory.notes.push(note);
  }
  memory.notes = memory.notes.slice(-20);
  writeAtomic(memoryPath(contactId), renderMemoryMd(memory));
}

// ── Pronunciation (pronunciation.json) ──────────────────────────────────────

export interface PronunciationTerm {
  term: string;
  alias: string;
}

export interface Pronunciation {
  /** term → respelling, applied to the text every cascade TTS speaks. */
  terms: PronunciationTerm[];
  /** An ElevenLabs pronunciation dictionary, when one has been synced upstream. */
  dictionary?: { dictionaryId: string; versionId: string };
}

/**
 * Read the pronunciation lexicon saved from Settings → Pronunciation. Missing
 * or malformed file = no overrides.
 */
export function loadPronunciation(): Pronunciation {
  const data = readJson(dataPath('pronunciation.json'));
  const state = data && typeof data === 'object' ? (data as Record<string, unknown>) : {};
  const terms = Array.isArray(state.terms)
    ? state.terms
        .map((t) => (t && typeof t === 'object' ? (t as Record<string, unknown>) : {}))
        .map((t) => ({ term: String(t.term ?? '').trim(), alias: String(t.alias ?? '').trim() }))
        .filter((t) => t.term && t.alias)
    : [];
  const dictionaryId = typeof state.dictionary_id === 'string' ? state.dictionary_id : '';
  const versionId = typeof state.version_id === 'string' ? state.version_id : '';
  return dictionaryId && versionId ? { terms, dictionary: { dictionaryId, versionId } } : { terms };
}
