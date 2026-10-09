import type { FlowConfig, NodeKind } from './types'

/**
 * The flow builder's vocabulary — every string here is a CONTRACT with the
 * backend engine (backend-csharp/src/ChatBranch.Data/Flows/FlowEngine.cs),
 * which reads the same config keys and values. Change one side, change both.
 */

/**
 * Third-party action steps. These labels are a CONTRACT with the backend:
 * IntegrationActions.Providers maps exactly these strings to handlers, and the
 * engine routes any Action channel found in that map to the integration.
 */
export const INTEGRATION_ACTIONS = [
  'Create calendar event',
  'Find free slot',
  'Upload file to Drive',
  'Append row to Sheet',
  'Send via Gmail',
  'Update order status',
  'Create Stripe payment link',
] as const

// ---- triggers -----------------------------------------------------------------

export const TRIGGERS = [
  { value: 'Contact created', label: 'A contact is created', hint: 'New contacts, from any source, after the flow goes live' },
  { value: 'Tag added', label: 'A contact is tagged', hint: 'Once a contact carries a tag you choose' },
  { value: 'Message received', label: 'A message arrives', hint: 'A customer writes in — optionally only with a keyword' },
  { value: 'Date field', label: 'A date comes round', hint: 'Days before, on or after a date field (birthday, renewal…)' },
  { value: 'Scheduled', label: 'On a schedule', hint: 'Every day at a set time, optionally only some weekdays' },
  { value: 'Manual / API', label: 'Started by hand or by the API', hint: 'You pick the contacts; integrations call the API' },
] as const

/** Trigger `when` values from the debt-collection era — still executed by the
 * engine, never offered for new flows. */
export const LEGACY_TRIGGERS: Record<string, string> = {
  'On the due date': "On the record's due date",
  'Days before due date': 'Days before the due date',
  'Days after due date': 'Days after the due date',
  'Days after due date (exactly)': 'Exactly N days after the due date',
  'Record status is': 'Record status is…',
  'Contact has tag': 'Contact has a tag',
}

export const REPEAT_OPTIONS = [
  { value: 'Once', label: 'Once per contact' },
  { value: 'Every time', label: 'Every time it happens' },
  { value: 'Once a day', label: 'At most once a day' },
]

export const LEGACY_REPEATS: Record<string, string> = {
  'Daily until answered': 'Daily (legacy)',
  'Every 2 days': 'Every 2 days (legacy)',
}

export const MESSAGE_CHANNELS = [
  { value: '', label: 'Any channel' },
  { value: 'whatsapp', label: 'WhatsApp' },
  { value: 'email', label: 'Email' },
  { value: 'telegram', label: 'Telegram' },
  { value: 'facebook', label: 'Messenger' },
  { value: 'instagram', label: 'Instagram' },
  { value: 'website', label: 'Website chat' },
]

export const WEEKDAYS = [
  { value: 'mon', label: 'Mon' },
  { value: 'tue', label: 'Tue' },
  { value: 'wed', label: 'Wed' },
  { value: 'thu', label: 'Thu' },
  { value: 'fri', label: 'Fri' },
  { value: 'sat', label: 'Sat' },
  { value: 'sun', label: 'Sun' },
]

// ---- action channels ----------------------------------------------------------

/** The composition marker: the step's text is written by the AI (using its
 * skill) and sent on the step's other channels. */
export const AI_MESSAGE = 'AI message'

/** Channels a message can be SENT on (to the customer). */
export const SEND_CHANNELS = [
  { value: 'WhatsApp', label: 'WhatsApp' },
  { value: 'Email', label: 'Email' },
  { value: 'Telegram', label: 'Telegram' },
  { value: 'SMS', label: 'SMS (logged only — no SMS provider yet)' },
]

/** Team-facing channel (an in-app notification to staff). */
export const TEAM_PUSH = 'In-app push'

export const CALL_CHANNELS = ['AI voice call', 'Script call'] as const

export type ActionType = 'send' | 'ai' | 'call' | 'app'

/** What an Action node does, derived from its channels. */
export function actionType(cfg: FlowConfig | undefined): ActionType {
  const ch = channelsOf(cfg)
  if (ch.includes(AI_MESSAGE)) return 'ai'
  const messaging = ch.some((c) => SEND_CHANNELS.some((s) => s.value === c) || c === TEAM_PUSH)
  if (!messaging && ch.some((c) => (CALL_CHANNELS as readonly string[]).includes(c))) return 'call'
  if (!messaging && ch.some((c) => (INTEGRATION_ACTIONS as readonly string[]).includes(c))) return 'app'
  return 'send'
}

export function channelsOf(cfg: FlowConfig | undefined): string[] {
  const raw = cfg?.channels
  return Array.isArray(raw) ? raw.map(String) : raw ? [String(raw)] : []
}

// ---- decisions ------------------------------------------------------------------

export const DECISIONS = [
  { value: 'They replied', label: 'They replied', hint: 'A reply arrived during the last wait' },
  { value: 'Reply contains', label: 'Their reply contains…', hint: 'Any of the words you list' },
  { value: 'They said yes', label: 'They said yes', hint: 'Yes / ok / sure / boleh… (English + Malay)' },
  { value: 'Has tag', label: 'They have a tag', hint: 'The contact carries a tag' },
  { value: 'Custom field equals', label: 'A field has a value', hint: 'A custom field equals what you type' },
]

export const LEGACY_DECISIONS: Record<string, string> = {
  "Contact's answer": "Contact's answer is yes (waits for a reply)",
  'Record status': 'Record status is…',
  'Contact tag': 'They have a tag',
}

// ---- durations ------------------------------------------------------------------

export const DURATION_UNITS = [
  { value: 'minutes', label: 'minutes' },
  { value: 'hours', label: 'hours' },
  { value: 'days', label: 'days' },
  { value: 'weeks', label: 'weeks' },
]

export function durationText(amount: unknown, unit: unknown): string {
  const n = Number(amount)
  if (!amount || !Number.isFinite(n) || n <= 0) return ''
  const u = String(unit || 'days')
  const singular = u.replace(/s$/, '')
  return `${n} ${n === 1 ? singular : u}`
}

// ---- the step palette (insert menu + canvas palette) -----------------------------

export interface StepChoice {
  id: string
  kind: NodeKind
  title: string
  hint: string
  config: FlowConfig
}

export const STEP_CHOICES: StepChoice[] = [
  { id: 'send', kind: 'action', title: 'Send message', hint: 'WhatsApp, email or Telegram — your words or a template',
    config: { channels: ['WhatsApp'], waMode: 'text', script: '' } },
  { id: 'ai', kind: 'action', title: 'AI message', hint: 'The AI writes the message, guided by a skill',
    config: { channels: [AI_MESSAGE, 'WhatsApp'], skill: '', script: '' } },
  { id: 'wait', kind: 'wait', title: 'Wait', hint: 'A set time, or until they reply',
    config: { mode: 'reply', timeoutAmount: 2, timeoutUnit: 'days', amount: 1, unit: 'days' } },
  { id: 'followup', kind: 'followup', title: 'Follow up if no reply', hint: 'Nudge again, up to 5 times, until they answer',
    config: { waitAmount: 1, waitUnit: 'days', maxFollowUps: 2, channels: ['WhatsApp'], waMode: 'auto', script: '', stopOnResolved: true } },
  { id: 'branch', kind: 'decision', title: 'Ask & branch', hint: 'Split into YES / NO paths',
    config: { basedOn: 'They replied' } },
  { id: 'call', kind: 'action', title: 'Call', hint: 'An AI voice call or a scripted call',
    config: { channels: ['AI voice call'], callVia: 'Phone (SIP)', script: '' } },
  { id: 'handover', kind: 'handover', title: 'Hand over', hint: 'Pass the conversation to a team',
    config: { team: '', priority: 'Normal', reason: '' } },
  { id: 'update', kind: 'update', title: 'Update contact', hint: 'Add or remove a tag, set a field',
    config: { op: 'add_tag', tag: '' } },
  { id: 'app', kind: 'action', title: 'App action', hint: 'Calendar, Sheets, Drive, Gmail, Shopify, Stripe',
    config: { channels: ['Create calendar event'], script: '' } },
  { id: 'end', kind: 'end', title: 'End', hint: 'Finish with an outcome',
    config: { logCode: 'DONE', updateMemory: 'Yes' } },
]

/** Values a freshly created node of each kind starts with. */
export const DEFAULT_CONFIG: Record<NodeKind, FlowConfig> = {
  trigger: { when: 'Tag added', tag: '', repeat: 'Once' },
  action: STEP_CHOICES[0].config,
  wait: STEP_CHOICES[2].config,
  followup: STEP_CHOICES[3].config,
  decision: STEP_CHOICES[4].config,
  handover: STEP_CHOICES[6].config,
  update: STEP_CHOICES[7].config,
  end: STEP_CHOICES[9].config,
  stage: {},
}

/** Branch outcomes: the handle id on the node and the words on each side. */
export function branchLabels(kind: string): { yes: string; no: string } {
  return kind === 'followup' ? { yes: 'Replied', no: 'No reply' } : { yes: 'Yes', no: 'No' }
}

export const isBranchKind = (kind: string | undefined) => kind === 'decision' || kind === 'followup'
