// Mock data + helpers for the ChatBranch conversation-platform dashboard POC.
// NOTE: this file was reconstructed after it was lost to a `.gitignore` rule.
// It intentionally uses in-memory sample data — no backend required.
// `.gitignore` explicitly un-ignores `frontend/src/lib/` so this file survives.

// ── Types ────────────────────────────────────────────────────────────────────
export type ContactStatus = 'active' | 'pending' | 'message-sent' | 'resolved' | 'failed'

export type HistoryEntry = {
  type: 'ai-call' | 'bot-call' | 'message' | 'resolved'
  date: string
  text: string
}

/** The business object a conversation is about — an order, a shipment, an
 *  invoice, a booking, a support ticket. Mirrors backend `crm.Record`. */
export type RecordDto = {
  id: number
  type: 'order' | 'shipment' | 'invoice' | 'booking' | 'ticket' | 'custom'
  external_id: string
  title: string
  status: string
  amount: number | null
  currency: string
  due_date: string | null
  data: Record<string, unknown>
}

export type Contact = {
  id: number
  name: string
  phone: string
  email?: string
  company?: string
  status: ContactStatus
  method?: string
  language?: string
  tags: string[]
  attributes: Record<string, unknown>
  /**
   * Entities this contact belongs to (0..many). Two shapes on the wire:
   * /api/contacts sends slugs, /api/v1/contacts/<id> sends {slug,name} —
   * normalize with contactEntityRefs() rather than assuming either.
   */
  entities?: (string | { slug: string; name: string })[]
  records: RecordDto[]
  history: HistoryEntry[]
}

// Activity feed shown on the /activity page.
export type ActivityRow = {
  id: number
  type: 'call' | 'message' | 'record' | 'resolve' | 'campaign' | 'escalation' | 'config'
  date: string
  time: string
  title: string
  detail: string
  actor: string
  target?: number | null
}

// Live-activity feed shown on the dashboard (different shape from ActivityRow).
export type FeedItem = {
  id: number
  type: 'ai-call' | 'bot-call' | 'message' | 'resolved'
  text: string
  contact: string
  status: string
  amount: string
  time: string
}

/** Outcome enum mirrors the backend call-summary contract
 *  (`resolved | scheduled | info_provided | declined | unreachable`). */
export type CallOutcome = 'resolved' | 'scheduled' | 'info_provided' | 'declined' | 'unreachable'

export type CallLog = {
  id: number
  type: 'ai-call' | 'bot-call' | 'message'
  contact: string
  status: string
  phone: string
  datetime: string
  duration?: string
  summary: string
}

export type TranscriptLine = { who: 'ai' | 'contact'; t: string; kb?: string }
export type KbArticle = { title: string; cat: string; snippet: string }
export type Voice = {
  id: string
  name: string
  gender: string
  accent: string
  language: string
  provider: string
  persona: string
  status: 'ready' | 'processing'
  custom: boolean
  tag?: string
}

// A queued outbound call. `contactId` references a contact in the contact list
// (loaded from the contacts API) — the panel joins on it to show name/phone, and
// "Call now" dials that contact. `at` is a human display label; `language` is
// the hint passed to the agent.
export type ScheduledCall = {
  id: number
  contactId: number
  at: string
  language: string // a lib/languages.ts code (en/ms/zh/ta/id)
}

// ── Helpers ──────────────────────────────────────────────────────────────────
export function fmt(n: number): string {
  return 'RM ' + Math.round(n).toLocaleString('en-US')
}

export function initials(name: string): string {
  return name
    .trim()
    .split(/\s+/)
    .slice(0, 2)
    .map((w) => w[0]?.toUpperCase() ?? '')
    .join('')
}

const METHOD_LABELS: Record<string, string> = {
  'ai-call': 'AI Voice',
  'bot-call': 'Bot Call',
  message: 'WhatsApp',
  sms: 'SMS',
  email: 'Email',
}
export function methodLabel(method?: string): string {
  if (!method) return ''
  return METHOD_LABELS[method] ?? method
}

/** First (primary) record on a contact, if any — used wherever the UI shows
 *  "the thing this conversation is about" (list rows, detail header, chips). */
export function primaryRecord(c: Pick<Contact, 'records'>): RecordDto | null {
  return c.records[0] ?? null
}

// ── Sample datasets removed ──────────────────────────────────────────────────
// These arrays used to hold invented contacts, call logs, activity and KB rows
// so the POC rendered with no backend. In production that meant fake people
// (Beckham, Aisyah Rahman…) flashing over a live database on any cold load or
// failed fetch. They are now empty: every screen sources its data from the API,
// and an unreachable API shows an honest empty state, never invented records.
export const CONTACTS: Contact[] = []

export const FEED: FeedItem[] = []

export const ACTIVITY: ActivityRow[] = []

export const CALL_LOGS: CallLog[] = []

export const CALL_TRANSCRIPTS: Record<number, TranscriptLine[]> = {}

export const KB: KbArticle[] = []

// ── Voices ──────────────────────────────────────────────────────────────────────
export const VOICES: Voice[] = [
  { id: 'marin', name: 'Marin', gender: 'Female', accent: 'Neutral EN', language: 'en', provider: 'openai', persona: 'advisor', status: 'ready', custom: false, tag: 'Default' },
  { id: 'cedar', name: 'Cedar', gender: 'Male', accent: 'Neutral EN', language: 'en', provider: 'openai', persona: 'negotiator', status: 'ready', custom: false },
  { id: 'alloy', name: 'Alloy', gender: 'Neutral', accent: 'US EN', language: 'en', provider: 'openai', persona: 'officer', status: 'ready', custom: false },
  { id: 'puck', name: 'Puck', gender: 'Male', accent: 'US EN', language: 'en', provider: 'gemini', persona: 'counsellor', status: 'ready', custom: false },
]

// ── Scheduled outbound calls ─────────────────────────────────────────────────
// Real scheduled calls come from the backend; no invented rows in production.
export const SCHEDULED: ScheduledCall[] = []

// The live-calls screen starts with an empty transcript until a real call runs.
export const LIVE_TRANSCRIPT: TranscriptLine[] = []
