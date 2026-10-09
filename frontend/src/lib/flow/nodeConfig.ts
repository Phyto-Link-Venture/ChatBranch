import type { MessageKey, TFunction } from '@/i18n'
import type { FlowConfig, NodeKind } from './types'

/**
 * The flow builder's vocabulary — every `value` here is a CONTRACT with the
 * backend engine (backend-csharp/src/ChatBranch.Data/Flows/FlowEngine.cs),
 * which reads the same config keys and values. Change one side, change both.
 *
 * Values are stored in the flow JSON in English and are NEVER translated; what
 * the dashboard shows is a separate display layer — a translation key (`lbl`,
 * `hint`) or a brand name shown verbatim (`label`) — resolved at render with
 * `choiceLabel(t, c)` / `choiceOptions(t, list)`.
 */

/** A stored config value and how it is shown. */
export interface Choice {
  value: string
  /** translation key of the label (preferred) */
  lbl?: MessageKey
  /** a brand name shown as is (WhatsApp, Telegram…) */
  label?: string
  /** translation key of a one-line explanation */
  hint?: MessageKey
}

export const choiceLabel = (t: TFunction, c: Choice): string => (c.lbl ? t(c.lbl) : (c.label ?? c.value))

/** `Select` options for a choice list, translated. */
export const choiceOptions = (t: TFunction, list: readonly Choice[], withHints = false) =>
  list.map((c) => ({ value: c.value, label: choiceLabel(t, c), hint: withHints && c.hint ? t(c.hint) : undefined }))

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

export type IntegrationAction = (typeof INTEGRATION_ACTIONS)[number]

/** What each integration action is CALLED on screen (the stored value stays English). */
export const INTEGRATION_ACTION_LABELS: Record<IntegrationAction, MessageKey> = {
  'Create calendar event': 'flows.appActions.createCalendarEvent',
  'Find free slot': 'flows.appActions.findFreeSlot',
  'Upload file to Drive': 'flows.appActions.uploadToDrive',
  'Append row to Sheet': 'flows.appActions.appendRowToSheet',
  'Send via Gmail': 'flows.appActions.sendViaGmail',
  'Update order status': 'flows.appActions.updateOrderStatus',
  'Create Stripe payment link': 'flows.appActions.createStripeLink',
}

// ---- triggers -----------------------------------------------------------------

export const TRIGGERS: Choice[] = [
  { value: 'Contact created', lbl: 'flows.trigger.contactCreated', hint: 'flows.trigger.contactCreatedHint' },
  { value: 'Tag added', lbl: 'flows.trigger.tagAdded', hint: 'flows.trigger.tagAddedHint' },
  { value: 'Message received', lbl: 'flows.trigger.messageReceived', hint: 'flows.trigger.messageReceivedHint' },
  { value: 'Date field', lbl: 'flows.trigger.dateField', hint: 'flows.trigger.dateFieldHint' },
  { value: 'Scheduled', lbl: 'flows.trigger.scheduled', hint: 'flows.trigger.scheduledHint' },
  { value: 'Manual / API', lbl: 'flows.trigger.manual', hint: 'flows.trigger.manualHint' },
]

/** Trigger `when` values from the debt-collection era — still executed by the
 * engine, never offered for new flows. Stored value → label key. */
export const LEGACY_TRIGGERS: Record<string, MessageKey> = {
  'On the due date': 'flows.legacyTrigger.onDueDate',
  'Days before due date': 'flows.legacyTrigger.daysBeforeDue',
  'Days after due date': 'flows.legacyTrigger.daysAfterDue',
  'Days after due date (exactly)': 'flows.legacyTrigger.daysAfterDueExactly',
  'Record status is': 'flows.legacyTrigger.recordStatusIs',
  'Contact has tag': 'flows.legacyTrigger.contactHasTag',
}

export const REPEAT_OPTIONS: Choice[] = [
  { value: 'Once', lbl: 'flows.repeat.once' },
  { value: 'Every time', lbl: 'flows.repeat.everyTime' },
  { value: 'Once a day', lbl: 'flows.repeat.onceADay' },
]

export const LEGACY_REPEATS: Record<string, MessageKey> = {
  'Daily until answered': 'flows.repeat.dailyLegacy',
  'Every 2 days': 'flows.repeat.every2DaysLegacy',
}

export const MESSAGE_CHANNELS: Choice[] = [
  { value: '', lbl: 'flows.channels.any' },
  { value: 'whatsapp', label: 'WhatsApp' },
  { value: 'email', lbl: 'common.fields.email' },
  { value: 'telegram', label: 'Telegram' },
  { value: 'facebook', label: 'Messenger' },
  { value: 'instagram', label: 'Instagram' },
  { value: 'website', lbl: 'flows.channels.websiteChat' },
  { value: 'sms', label: 'SMS' },
  { value: 'line', label: 'LINE' },
  { value: 'viber', label: 'Viber' },
  { value: 'wechat', label: 'WeChat' },
  { value: 'zalo', label: 'Zalo' },
  { value: 'tiktok', label: 'TikTok' },
  { value: 'douyin', label: 'Douyin' },
]

export const WEEKDAYS: Choice[] = [
  { value: 'mon', lbl: 'flows.weekdays.mon' },
  { value: 'tue', lbl: 'flows.weekdays.tue' },
  { value: 'wed', lbl: 'flows.weekdays.wed' },
  { value: 'thu', lbl: 'flows.weekdays.thu' },
  { value: 'fri', lbl: 'flows.weekdays.fri' },
  { value: 'sat', lbl: 'flows.weekdays.sat' },
  { value: 'sun', lbl: 'flows.weekdays.sun' },
]

// ---- action channels ----------------------------------------------------------

/** The composition marker: the step's text is written by the AI (using its
 * skill) and sent on the step's other channels. */
export const AI_MESSAGE = 'AI message'

/** Channels a message can be SENT on (to the customer). */
export const SEND_CHANNELS: Choice[] = [
  { value: 'WhatsApp', label: 'WhatsApp' },
  { value: 'Email', lbl: 'common.fields.email' },
  { value: 'Telegram', label: 'Telegram' },
  { value: 'SMS', lbl: 'flows.channels.smsSandbox' },
]

/** Team-facing channel (an in-app notification to staff). */
export const TEAM_PUSH = 'In-app push'

export const CALL_CHANNELS = ['AI voice call', 'Script call'] as const

export const CALL_CHANNEL_LABELS: Record<(typeof CALL_CHANNELS)[number], MessageKey> = {
  'AI voice call': 'flows.call.aiVoiceCall',
  'Script call': 'flows.call.scriptCall',
}

/** How one stored action channel reads on screen ("Email" → "E-mel"…). */
export function actionChannelLabel(t: TFunction, c: string): string {
  if (c === TEAM_PUSH) return t('flows.channels.teamPush')
  if (c === AI_MESSAGE) return t('flows.stepChoice.ai')
  if (c === 'Email') return t('common.fields.email')
  if (c in CALL_CHANNEL_LABELS) return t(CALL_CHANNEL_LABELS[c as keyof typeof CALL_CHANNEL_LABELS])
  if (c in INTEGRATION_ACTION_LABELS) return t(INTEGRATION_ACTION_LABELS[c as IntegrationAction])
  return c
}

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

/**
 * "Ask & branch" kinds. Adding one: (1) a row here — the engine's English
 * `value` + `lbl`/`hint` keys under `flows.decision.*` in messages/{en,ms,zh}/
 * flows.ts; (2) its inputs in ConfigPanel `DecisionForm`; (3) optionally a
 * summary case in describe.ts (without one the card shows the `lbl`); (4) its
 * "unfinished" check in validate.ts; (5) the case in FlowEngine.cs.
 */
export const DECISIONS: Choice[] = [
  { value: 'They replied', lbl: 'flows.decision.replied', hint: 'flows.decision.repliedHint' },
  { value: 'Reply contains', lbl: 'flows.decision.replyContains', hint: 'flows.decision.replyContainsHint' },
  { value: 'They said yes', lbl: 'flows.decision.saidYes', hint: 'flows.decision.saidYesHint' },
  { value: 'Has tag', lbl: 'flows.decision.hasTag', hint: 'flows.decision.hasTagHint' },
  { value: 'Custom field equals', lbl: 'flows.decision.fieldEquals', hint: 'flows.decision.fieldEqualsHint' },
]

export const LEGACY_DECISIONS: Record<string, MessageKey> = {
  "Contact's answer": 'flows.decision.legacyContactsAnswer',
  'Record status': 'flows.legacyTrigger.recordStatusIs',
  'Contact tag': 'flows.decision.hasTag',
}

// ---- "Set chatbot on/off" (an Update step) -----------------------------------------

/** Update-step op the engine reads as "set the per-chat AI switch" (the spec's alias `updateType: "chatbot"` works too). */
export const CHATBOT_OP = 'chatbot'

/** Conversation channels a chatbot step can be limited to — THREAD channel ids, as the engine matches them. */
export const CHATBOT_CHANNELS: Choice[] = [
  { value: 'whatsapp', label: 'WhatsApp' },
  { value: 'email', lbl: 'common.fields.email' },
  { value: 'telegram', label: 'Telegram' },
  { value: 'facebook', label: 'Messenger' },
  { value: 'instagram', label: 'Instagram' },
  { value: 'website', lbl: 'flows.channels.websiteChat' },
]

export const isChatbotStep = (cfg: FlowConfig | undefined) =>
  String(cfg?.op ?? '') === CHATBOT_OP || String(cfg?.updateType ?? '') === CHATBOT_OP

// ---- durations ------------------------------------------------------------------

export const DURATION_UNITS: Choice[] = [
  { value: 'minutes', lbl: 'flows.units.minutes' },
  { value: 'hours', lbl: 'flows.units.hours' },
  { value: 'days', lbl: 'flows.units.days' },
  { value: 'weeks', lbl: 'flows.units.weeks' },
]

const DURATION_KEYS: Record<string, MessageKey> = {
  minutes: 'flows.duration.minutes',
  hours: 'flows.duration.hours',
  days: 'flows.duration.days',
  weeks: 'flows.duration.weeks',
}

/** "3 days" in the current language ("" when there is no amount). */
export function durationText(t: TFunction, amount: unknown, unit: unknown): string {
  const n = Number(amount)
  if (!amount || !Number.isFinite(n) || n <= 0) return ''
  return t(DURATION_KEYS[String(unit || 'days')] ?? DURATION_KEYS.days, { count: n })
}

// ---- the step palette (insert menu + canvas palette) -----------------------------

/**
 * Adding a step choice: a row here (`title`/`hint` keys under
 * `flows.stepChoice.*`), its icon in components/flow/stepIcons.tsx, its form in
 * ConfigPanel, its summary in describe.ts and its checks in validate.ts. A new
 * node KIND also needs types.ts `NodeKind`, `DEFAULT_CONFIG` below and
 * nodes.tsx `nodeTypes` (+ the engine).
 */

export interface StepChoice {
  id: string
  kind: NodeKind
  /** translation keys */
  title: MessageKey
  hint: MessageKey
  config: FlowConfig
}

export const STEP_CHOICES: StepChoice[] = [
  { id: 'send', kind: 'action', title: 'flows.stepChoice.send', hint: 'flows.stepChoice.sendHint',
    config: { channels: ['WhatsApp'], waMode: 'text', script: '' } },
  { id: 'ai', kind: 'action', title: 'flows.stepChoice.ai', hint: 'flows.stepChoice.aiHint',
    config: { channels: [AI_MESSAGE, 'WhatsApp'], skill: '', script: '' } },
  { id: 'wait', kind: 'wait', title: 'flows.stepChoice.wait', hint: 'flows.stepChoice.waitHint',
    config: { mode: 'reply', timeoutAmount: 2, timeoutUnit: 'days', amount: 1, unit: 'days' } },
  { id: 'followup', kind: 'followup', title: 'flows.stepChoice.followup', hint: 'flows.stepChoice.followupHint',
    config: { waitAmount: 1, waitUnit: 'days', maxFollowUps: 2, channels: ['WhatsApp'], waMode: 'auto', script: '', stopOnResolved: true } },
  { id: 'branch', kind: 'decision', title: 'flows.stepChoice.branch', hint: 'flows.stepChoice.branchHint',
    config: { basedOn: 'They replied' } },
  { id: 'call', kind: 'action', title: 'flows.stepChoice.call', hint: 'flows.stepChoice.callHint',
    config: { channels: ['AI voice call'], callVia: 'Phone (SIP)', script: '' } },
  { id: 'handover', kind: 'handover', title: 'flows.stepChoice.handover', hint: 'flows.stepChoice.handoverHint',
    config: { team: '', priority: 'Normal', reason: '' } },
  { id: 'update', kind: 'update', title: 'flows.stepChoice.update', hint: 'flows.stepChoice.updateHint',
    config: { op: 'add_tag', tag: '' } },
  // an Update step with op "chatbot" (FlowEngine.SetChatbot): the per-chat AI switch
  { id: 'chatbot', kind: 'update', title: 'flows.stepChoice.chatbot', hint: 'flows.stepChoice.chatbotHint',
    config: { op: CHATBOT_OP, value: 'off', scope: 'all', channels: [] } },
  { id: 'app', kind: 'action', title: 'flows.stepChoice.app', hint: 'flows.stepChoice.appHint',
    config: { channels: ['Create calendar event'], script: '' } },
  { id: 'end', kind: 'end', title: 'flows.stepChoice.end', hint: 'flows.stepChoice.endHint',
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
  end: STEP_CHOICES.find((c) => c.id === 'end')!.config,
  stage: {},
}

/** Branch outcomes: the words STORED on each side's edge label (English — the
 * engine also follows an un-handled edge whose label starts with yes/no). */
export function branchLabels(kind: string): { yes: string; no: string } {
  return kind === 'followup' ? { yes: 'Replied', no: 'No reply' } : { yes: 'Yes', no: 'No' }
}

/** The same outcomes as they are SHOWN (translation keys). */
export function branchLabelKeys(kind: string): { yes: MessageKey; no: MessageKey } {
  return kind === 'followup'
    ? { yes: 'flows.branch.replied', no: 'flows.branch.noReply' }
    : { yes: 'common.status.yes', no: 'common.status.no' }
}

const STORED_EDGE_LABELS: Record<string, MessageKey> = {
  yes: 'common.status.yes',
  no: 'common.status.no',
  replied: 'flows.branch.replied',
  'no reply': 'flows.branch.noReply',
}

/** An edge label for display: the stored branch words translated, anything an
 * operator typed shown as typed. */
export function edgeLabelText(t: TFunction, label: unknown): string {
  const raw = typeof label === 'string' ? label : ''
  const key = STORED_EDGE_LABELS[raw.trim().toLowerCase()]
  return key ? t(key) : raw
}

export const isBranchKind = (kind: string | undefined) => kind === 'decision' || kind === 'followup'
