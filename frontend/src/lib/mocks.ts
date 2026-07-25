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

// ── Contacts ─────────────────────────────────────────────────────────────────
export const CONTACTS: Contact[] = [
  {
    id: 1, name: 'Beckham', phone: '+601168382039', email: 'beckham@example.com', company: '',
    status: 'active', method: 'ai-call', language: 'en', tags: ['vip'], attributes: {},
    records: [
      { id: 101, type: 'order', external_id: 'ORD-6225', title: 'Order #6225', status: 'processing', amount: 6225, currency: 'MYR', due_date: '2026-07-29', data: {} },
    ],
    history: [
      { type: 'ai-call', date: '5 Jul, 10:12', text: 'AI call — contact confirmed identity.' },
      { type: 'message', date: '2 Jul, 09:00', text: 'WhatsApp update sent.' },
    ],
  },
  {
    id: 2, name: 'Aisyah Rahman', phone: '+60 13-221 8890', email: 'aisyah@example.com', company: 'Rahman Trading',
    status: 'active', method: 'bot-call', language: 'ms', tags: [], attributes: {},
    records: [
      { id: 102, type: 'invoice', external_id: 'INV-3400', title: 'Invoice #3400', status: 'partially_paid', amount: 12800, currency: 'MYR', due_date: '2026-06-20', data: {} },
    ],
    history: [{ type: 'bot-call', date: '4 Jul, 15:40', text: 'Bot call — confirmed partial payment.' }],
  },
  {
    id: 3, name: 'Lim Wei Jie', phone: '+60 16-778 2201', email: '', company: '',
    status: 'pending', method: 'message', language: 'en', tags: [], attributes: {},
    records: [
      { id: 103, type: 'shipment', external_id: 'SHP-4200', title: 'Shipment #4200', status: 'in_transit', amount: null, currency: 'MYR', due_date: '2026-07-15', data: {} },
    ],
    history: [],
  },
  {
    id: 4, name: 'Ravi Kumar', phone: '+60 11-908 5567', email: 'ravi@example.com', company: '',
    status: 'active', method: 'ai-call', language: 'en', tags: ['escalate'], attributes: {},
    records: [
      { id: 104, type: 'ticket', external_id: 'TCK-2150', title: 'Support ticket #2150', status: 'open', amount: null, currency: 'MYR', due_date: null, data: {} },
    ],
    history: [
      { type: 'ai-call', date: '5 Jul, 11:02', text: 'AI call — requested human follow-up.' },
    ],
  },
  {
    id: 5, name: 'Nurul Huda', phone: '+60 19-334 1120', email: '', company: '',
    status: 'pending', method: 'message', language: 'ms', tags: [], attributes: {},
    records: [
      { id: 105, type: 'booking', external_id: 'BKG-3600', title: 'Booking #3600', status: 'awaiting_confirmation', amount: 3600, currency: 'MYR', due_date: '2026-07-18', data: {} },
    ],
    history: [],
  },
  {
    id: 6, name: 'Chong Mei Ling', phone: '+60 12-556 7788', email: 'mei.ling@example.com', company: '',
    status: 'resolved', method: 'ai-call', language: 'zh', tags: [], attributes: {},
    records: [
      { id: 106, type: 'order', external_id: 'ORD-9800', title: 'Order #9800', status: 'delivered', amount: 9800, currency: 'MYR', due_date: '2026-07-03', data: {} },
    ],
    history: [{ type: 'resolved', date: '3 Jul, 14:20', text: 'Order delivered. Case closed.' }],
  },
  {
    id: 7, name: 'Daniel Ong', phone: '+60 14-220 9981', email: '', company: 'Ong Logistics',
    status: 'message-sent', method: 'message', language: 'en', tags: [], attributes: {},
    records: [
      { id: 107, type: 'invoice', external_id: 'INV-5200', title: 'Invoice #5200', status: 'open', amount: 15200, currency: 'MYR', due_date: '2026-06-05', data: {} },
    ],
    history: [{ type: 'message', date: '4 Jul, 08:15', text: 'Reminder message delivered.' }],
  },
  {
    id: 8, name: 'Farah Idris', phone: '+60 17-889 3345', email: '', company: '',
    status: 'pending', method: 'bot-call', language: 'en', tags: [], attributes: {},
    records: [
      { id: 108, type: 'booking', external_id: 'BKG-5400', title: 'Booking #5400', status: 'confirmed', amount: 5400, currency: 'MYR', due_date: '2026-07-21', data: {} },
    ],
    history: [],
  },
  {
    id: 9, name: 'Tan Ah Kow', phone: '+60 18-445 6677', email: '', company: '',
    status: 'failed', method: 'ai-call', language: 'zh', tags: [], attributes: {},
    records: [
      { id: 109, type: 'order', external_id: 'ORD-3000', title: 'Order #3000', status: 'on_hold', amount: 30000, currency: 'MYR', due_date: '2026-04-10', data: {} },
    ],
    history: [{ type: 'ai-call', date: '1 Jul, 16:00', text: 'Call failed — number unreachable.' }],
  },
  {
    id: 10, name: 'Grace Lee', phone: '+60 12-101 2020', email: 'grace@example.com', company: '',
    status: 'message-sent', method: 'message', language: 'en', tags: [], attributes: {},
    records: [
      { id: 110, type: 'shipment', external_id: 'SHP-2750', title: 'Shipment #2750', status: 'out_for_delivery', amount: null, currency: 'MYR', due_date: '2026-07-25', data: {} },
    ],
    history: [{ type: 'message', date: '5 Jul, 07:30', text: 'First update sent.' }],
  },
]

// ── Dashboard live-activity feed ───────────────────────────────────────────────
export const FEED: FeedItem[] = [
  { id: 1, type: 'ai-call', text: 'AI call in progress', contact: 'Ravi Kumar', status: 'active', amount: '', time: 'now' },
  { id: 2, type: 'resolved', text: 'Order delivered', contact: 'Chong Mei Ling', status: 'success', amount: 'RM 9,800', time: '2m ago' },
  { id: 3, type: 'bot-call', text: 'Bot call completed', contact: 'Aisyah Rahman', status: 'success', amount: '', time: '14m ago' },
  { id: 4, type: 'message', text: 'Update delivered', contact: 'Daniel Ong', status: 'sent', amount: '', time: '31m ago' },
  { id: 5, type: 'ai-call', text: 'AI call — callback requested', contact: 'Sky Tan', status: 'sent', amount: '', time: '1h ago' },
]

// ── /activity page feed ────────────────────────────────────────────────────────
export const ACTIVITY: ActivityRow[] = [
  { id: 1, type: 'call', date: 'Today', time: '11:02', title: 'AI call completed', detail: 'Ravi Kumar requested a human agent follow-up.', actor: 'AI Agent', target: 4 },
  { id: 2, type: 'record', date: 'Today', time: '10:40', title: 'Order delivered', detail: 'Order #9800 marked delivered.', actor: 'System', target: 6 },
  { id: 3, type: 'call', date: 'Today', time: '10:12', title: 'AI update call', detail: 'Sky Tan confirmed identity, will review.', actor: 'AI Agent', target: 1 },
  { id: 4, type: 'message', date: 'Today', time: '08:15', title: 'Update message sent', detail: 'WhatsApp update delivered to Daniel Ong.', actor: 'System', target: 7 },
  { id: 5, type: 'campaign', date: 'Yesterday', time: '17:30', title: 'Campaign launched', detail: '5 contacts queued for AI voice calls.', actor: 'Operator', target: null },
  { id: 6, type: 'escalation', date: 'Yesterday', time: '16:00', title: 'Case escalated', detail: 'Tan Ah Kow marked unreachable after 3 attempts.', actor: 'System', target: 9 },
  { id: 7, type: 'config', date: 'Yesterday', time: '09:20', title: 'Flow updated', detail: 'Delivery-update workflow edited.', actor: 'Operator', target: null },
]

// ── Call logs ──────────────────────────────────────────────────────────────────
export const CALL_LOGS: CallLog[] = [
  { id: 1, type: 'ai-call', contact: 'Ravi Kumar', status: 'callback-requested', phone: '+60 11-908 5567', datetime: '5 Jul 2026, 11:02', duration: '3:41', summary: 'Contact asked to speak with a human agent about their support ticket.' },
  { id: 2, type: 'ai-call', contact: 'Sky Tan', status: 'completed', phone: '+60 12-345 6789', datetime: '5 Jul 2026, 10:12', duration: '2:07', summary: 'Confirmed identity, will review and reply next week.' },
  { id: 3, type: 'bot-call', contact: 'Aisyah Rahman', status: 'completed', phone: '+60 13-221 8890', datetime: '4 Jul 2026, 15:40', duration: '1:55', summary: 'Confirmed partial payment of RM 3,000 by 10 Jul.' },
  { id: 4, type: 'message', contact: 'Daniel Ong', status: 'delivered', phone: '+60 14-220 9981', datetime: '4 Jul 2026, 08:15', summary: 'Update message delivered.' },
  { id: 5, type: 'ai-call', contact: 'Tan Ah Kow', status: 'no-answer', phone: '+60 18-445 6677', datetime: '1 Jul 2026, 16:00', duration: '0:12', summary: 'No answer — number appears unreachable.' },
]

export const CALL_TRANSCRIPTS: Record<number, TranscriptLine[]> = {
  1: [
    { who: 'ai', t: 'Hello, am I speaking with Ravi? I\'m calling from ChatBranch Support.' },
    { who: 'contact', t: 'Yes, this is Ravi.' },
    { who: 'ai', t: 'This is a follow-up on your open support ticket. How can I help today?', kb: 'Support follow-up script' },
    { who: 'contact', t: 'Can I talk to a real person about this?' },
    { who: 'ai', t: 'Of course — I\'ll arrange for a human agent to follow up with you shortly.', kb: 'Human handoff policy' },
  ],
  2: [
    { who: 'ai', t: 'Hi, is this Sky? Calling from ChatBranch Support.' },
    { who: 'contact', t: 'Yes, speaking.' },
    { who: 'ai', t: 'Just a gentle check-in on your order. Everything looking good so far?' },
    { who: 'contact', t: 'Yes, all good, thanks.' },
    { who: 'ai', t: 'Great, noted — I\'ll send you the tracking link.' },
  ],
  3: [
    { who: 'ai', t: 'Hello Aisyah, regarding your open invoice — are you able to settle it today?' },
    { who: 'contact', t: 'I can pay RM 3,000 by the 10th.' },
    { who: 'ai', t: 'Thank you, I\'ve recorded a follow-up for RM 3,000 by 10 July.', kb: 'Follow-up commitment handling' },
  ],
}

// ── Knowledge base ─────────────────────────────────────────────────────────────
export const KB: KbArticle[] = [
  { title: 'Order status script', cat: 'Scripts', snippet: 'Polite opening, confirm identity, state the current status, offer next steps.' },
  { title: 'Human handoff policy', cat: 'Policy', snippet: 'If the contact requests a person or is distressed, offer human follow-up and mark needs_human.' },
  { title: 'Follow-up commitment handling', cat: 'Process', snippet: 'Capture a specific date. Confirm the details and send any relevant link.' },
  { title: 'Do & Don\'t', cat: 'Compliance', snippet: 'Never threaten, shame, mention legal action, or ask for card details.' },
]

// ── Voices ──────────────────────────────────────────────────────────────────────
export const VOICES: Voice[] = [
  { id: 'marin', name: 'Marin', gender: 'Female', accent: 'Neutral EN', language: 'en', provider: 'openai', persona: 'advisor', status: 'ready', custom: false, tag: 'Default' },
  { id: 'cedar', name: 'Cedar', gender: 'Male', accent: 'Neutral EN', language: 'en', provider: 'openai', persona: 'negotiator', status: 'ready', custom: false },
  { id: 'alloy', name: 'Alloy', gender: 'Neutral', accent: 'US EN', language: 'en', provider: 'openai', persona: 'officer', status: 'ready', custom: false },
  { id: 'puck', name: 'Puck', gender: 'Male', accent: 'US EN', language: 'en', provider: 'gemini', persona: 'counsellor', status: 'ready', custom: false },
]

// ── Scheduled outbound calls ─────────────────────────────────────────────────
// Each entry references a contact by id (i.e. drawn from the contact list). The
// Live Calls page joins these against `contacts` and offers a "Call now" action.
export const SCHEDULED: ScheduledCall[] = [
  { id: 1, contactId: 5, at: 'Today · 3:30 PM', language: 'ms' },
  { id: 2, contactId: 3, at: 'Today · 4:15 PM', language: 'en' },
  { id: 3, contactId: 8, at: 'Tomorrow · 10:00 AM', language: 'zh' },
  { id: 4, contactId: 7, at: 'Tomorrow · 2:00 PM', language: 'en' },
]

// Seed transcript for the live-calls screen.
export const LIVE_TRANSCRIPT: TranscriptLine[] = [
  { who: 'ai', t: 'Hello, am I speaking with Ravi? I\'m calling from ChatBranch Support.' },
  { who: 'contact', t: 'Yes, that\'s me.' },
  { who: 'ai', t: 'This is a courtesy check-in on your open ticket. How are things looking?', kb: 'Support follow-up script' },
  { who: 'contact', t: 'Still waiting on a reply, actually.' },
  { who: 'ai', t: 'Understood. Let me get a human agent to follow up with you directly.', kb: 'Human handoff policy' },
]
