/**
 * The dispatch-metadata contract: the JSON the dial site (the C# API's
 * LiveKit control plane, or `npm run dial`) attaches to the agent dispatch.
 * Every key is optional — a missing field falls back to config.CONTACT or to
 * the worker's env defaults, so an empty dispatch still produces a sane call.
 *
 * Keep in step with ChatBranch.Data/Sidecars/VoiceDispatch.cs, which builds it.
 */
import * as config from './config.js';

export interface DispatchMetadata {
  // --- who the call is to ---
  phone?: string;
  contact_id?: number | string;
  /** The outlet (entity id) the call is placed for — echoed into calls.json so the API files
   *  the call record under that company's contact views. */
  entity_id?: number | string;
  /** false = the contact is shared by several companies: never read or write the per-contact
   *  memory file (memory/{contact_id}.md) on this call — one company's notes never reach another. */
  file_memory?: boolean;
  contact_name?: string;
  company_name?: string;
  /** Demographic language hint: "en" | "ms" | "zh"; anything else = auto-detect. */
  language?: string;
  tags?: string[];
  /** Voice persona label (advisor/negotiator/…), recorded as persona_utilized. */
  persona?: string;

  // --- what the call is about (the contact's primary Record) ---
  record_type?: string;
  record_id?: string;
  record_title?: string;
  record_status?: string;
  record_amount?: string | number;
  record_currency?: string;
  record_due_date?: string;
  record_data?: Record<string, unknown>;
  /** Operator goal for this call; falls back to prompts.DEFAULT_OBJECTIVE. */
  objective?: string;

  // --- prompt blocks composed API-side (this process never touches the DB) ---
  /** Cross-channel CONTACT MEMORY block. */
  memory?: string;
  /** Bounded COMPANY KNOWLEDGE block. */
  knowledge?: string;
  /** SKILLS block (Settings → AI Skills). */
  skills?: string;

  // --- voice / brain selection ---
  /** "openai" | "gemini" (realtime) or "elevenlabs" | "ilmu" (cascade); empty = REALTIME_PROVIDER. */
  voice_provider?: string;
  /** Provider voice id (ElevenLabs voice_id, OpenAI realtime voice, ILMU voice_1/2/3). */
  voice?: string;
  /** Cascade LLM brain: "openai" | "ilmu" | "openrouter" | "gemini". */
  llm_provider?: string;
  /** Cascade STT engine: "elevenlabs" | "openai" | "ilmu". */
  stt_provider?: string;

  // --- script-call mode ---
  /** When set: read this text verbatim, never listen, hang up. */
  script?: string;
}

/** The contact context the prompts and the call record are built from. */
export interface CallContact {
  contact_name: string;
  company_name: string;
  record_type: string;
  record_id: string;
  record_title: string;
  record_status: string;
  record_amount: string | number;
  record_due_date: string;
  record_data: Record<string, unknown>;
  objective: string;
  language?: string;
  memory?: string;
  knowledge?: string;
  skills?: string;
  tags?: string[];
  persona?: string;
  record_currency?: string;
  phone?: string;
  contact_id?: number | string;
  entity_id?: number | string;
}

/** Parse the job's dispatch metadata; malformed JSON degrades to {} (a generic call). */
export function parseMetadata(raw: string | undefined): DispatchMetadata {
  if (!raw) return {};
  try {
    const parsed: unknown = JSON.parse(raw);
    return parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? (parsed as DispatchMetadata) : {};
  } catch {
    return {};
  }
}

const CONTACT_META_KEYS = [
  'contact_name',
  'company_name',
  'language',
  'memory',
  'knowledge',
  'skills',
  'objective',
  'tags',
  'persona',
  'record_type',
  'record_id',
  'record_title',
  'record_status',
  'record_amount',
  'record_currency',
  'record_due_date',
  'record_data',
] as const satisfies readonly (keyof DispatchMetadata & keyof CallContact)[];

/** Start from the fallback contact context, override with dispatch metadata. */
export function contactFromMetadata(md: DispatchMetadata): CallContact {
  const contact: CallContact = { ...config.CONTACT, record_data: { ...config.CONTACT.record_data } };
  const target = contact as unknown as Record<string, unknown>;
  for (const key of CONTACT_META_KEYS) {
    if (md[key] !== undefined && md[key] !== null) target[key] = md[key];
  }
  if (md.phone) contact.phone = md.phone;
  if (md.contact_id !== undefined && md.contact_id !== null) contact.contact_id = md.contact_id;
  if (md.entity_id !== undefined && md.entity_id !== null) contact.entity_id = md.entity_id;
  return contact;
}
