import type { MessageKey, Vars } from '@/i18n'
import type { DecisionOption, FlowCondition, FlowConfig } from './types'

/**
 * The decision step's vocabulary — a CONTRACT with the engine
 * (backend-csharp/src/ChatBranch.Data/Flows/FlowEngine.Conditions.cs and
 * FlowEngine.AiDecisions.cs). Subjects, operators and config keys are stored
 * in English and never translated; only the labels below are.
 *
 * A decision's `basedOn` picks its kind:
 *   'Conditions'       — `match` all | any + `conditions` [{subject, op, …}]
 *   'AI yes/no'        — `question` + the history window + `threshold` + `unsure`
 *   'AI picks a path'  — the same + `options` [{id, label, description}]
 * Older single-check kinds (They replied · Reply contains · They said yes ·
 * Has tag · Custom field equals) still run as stored; the editor shows them as
 * the equivalent condition and saves the new shape once the step is edited.
 */

export const KIND_CONDITIONS = 'Conditions'
export const KIND_AI_YES_NO = 'AI yes/no'
export const KIND_AI_PICK = 'AI picks a path'

export type DecisionKind = 'conditions' | 'ai_yes_no' | 'ai_pick' | 'legacy'

/** Single-check kinds the condition builder replaced — read as one condition. */
export const CONVERTIBLE_LEGACY = ['They replied', 'Reply contains', 'They said yes', 'Has tag', 'Contact tag', 'Custom field equals']

export function decisionKind(cfg: FlowConfig | undefined): DecisionKind {
  const b = String(cfg?.basedOn ?? '').trim()
  if (b === KIND_CONDITIONS) return 'conditions'
  if (b === KIND_AI_YES_NO) return 'ai_yes_no'
  if (b === KIND_AI_PICK) return 'ai_pick'
  if (CONVERTIBLE_LEGACY.includes(b)) return 'conditions'
  return 'legacy'
}

export const isAiDecision = (cfg: FlowConfig | undefined) => {
  const k = decisionKind(cfg)
  return k === 'ai_yes_no' || k === 'ai_pick'
}

/** The legacy single check as the condition it always meant (null = not one). */
export function legacyToConditions(cfg: FlowConfig | undefined): FlowCondition[] | null {
  const s = (k: string) => String(cfg?.[k] ?? '').trim()
  switch (s('basedOn')) {
    case 'They replied':
      return [{ subject: 'reply', op: 'replied' }]
    case 'Reply contains':
      return [{ subject: 'reply', op: 'contains_any', value: s('keyword') }]
    case 'They said yes':
      return [{ subject: 'reply', op: 'said_yes' }]
    case 'Has tag':
    case 'Contact tag':
      return [{ subject: 'tag', op: 'has', value: s('tag') }]
    case 'Custom field equals':
      return s('value')
        ? [{ subject: 'field', op: 'equals', field: s('field'), value: s('value') }]
        : [{ subject: 'field', op: 'not_empty', field: s('field') }]
    default:
      return null
  }
}

/** The conditions a step holds (a legacy single check read as one). */
export function conditionsOf(cfg: FlowConfig | undefined): FlowCondition[] {
  if (String(cfg?.basedOn ?? '') === KIND_CONDITIONS)
    return Array.isArray(cfg?.conditions) ? (cfg!.conditions as FlowCondition[]).filter((c) => c && typeof c === 'object') : []
  return legacyToConditions(cfg) ?? []
}

export const matchOf = (cfg: FlowConfig | undefined): 'all' | 'any' => (cfg?.match === 'any' ? 'any' : 'all')

// ---- the checks -----------------------------------------------------------------

/** What value a check needs, and so which input the editor shows. */
export type ValueInput =
  | 'none'
  | 'text'
  | 'words'
  | 'number'
  | 'date'
  | 'days'
  | 'hours'
  | 'count'
  | 'tag'
  | 'language'
  | 'entity'
  | 'optedScope'
  | 'status'
  | 'team'
  | 'channel'
  | 'fieldOption'
  | 'fieldOptions'

export interface CheckDef {
  subject: string
  op: string
  /** label of the check ("Has tag", "Conversation status is") */
  lbl: MessageKey
  input: ValueInput
}

export interface CheckGroup {
  subject: string
  lbl: MessageKey
  checks: CheckDef[]
}

const c = (subject: string, op: string, lbl: MessageKey, input: ValueInput = 'none'): CheckDef => ({ subject, op, lbl, input })

/** Every non-field check, grouped as the "what to check" picker shows them. */
export const CHECK_GROUPS: CheckGroup[] = [
  {
    subject: 'tag',
    lbl: 'flows.cond.group.tag',
    checks: [c('tag', 'has', 'flows.cond.tag.has', 'tag'), c('tag', 'not_has', 'flows.cond.tag.notHas', 'tag')],
  },
  {
    subject: 'contact',
    lbl: 'flows.cond.group.contact',
    checks: [
      c('contact', 'language_is', 'flows.cond.contact.languageIs', 'language'),
      c('contact', 'has_phone', 'flows.cond.contact.hasPhone'),
      c('contact', 'has_email', 'flows.cond.contact.hasEmail'),
      c('contact', 'in_entity', 'flows.cond.contact.inEntity', 'entity'),
      c('contact', 'opted_out', 'flows.cond.contact.optedOut', 'optedScope'),
    ],
  },
  {
    subject: 'conversation',
    lbl: 'flows.cond.group.conversation',
    checks: [
      c('conversation', 'status_is', 'flows.cond.conversation.statusIs', 'status'),
      c('conversation', 'assigned', 'flows.cond.conversation.assigned'),
      c('conversation', 'unassigned', 'flows.cond.conversation.unassigned'),
      c('conversation', 'team_is', 'flows.cond.conversation.teamIs', 'team'),
      c('conversation', 'open_handover', 'flows.cond.conversation.openHandover'),
      c('conversation', 'ai_on', 'flows.cond.conversation.aiOn'),
      c('conversation', 'channel_is', 'flows.cond.conversation.channelIs', 'channel'),
      c('conversation', 'last_message_within_hours', 'flows.cond.conversation.lastMessageWithin', 'hours'),
      c('conversation', 'messages_at_least', 'flows.cond.conversation.messagesAtLeast', 'count'),
    ],
  },
  {
    subject: 'reply',
    lbl: 'flows.cond.group.reply',
    checks: [
      c('reply', 'replied', 'flows.cond.reply.replied'),
      c('reply', 'contains_any', 'flows.cond.reply.containsAny', 'words'),
      c('reply', 'contains_all', 'flows.cond.reply.containsAll', 'words'),
      c('reply', 'said_yes', 'flows.cond.reply.saidYes'),
      c('reply', 'said_no', 'flows.cond.reply.saidNo'),
    ],
  },
  {
    subject: 'booking',
    lbl: 'flows.cond.group.booking',
    checks: [
      c('booking', 'upcoming', 'flows.cond.booking.upcoming'),
      c('booking', 'booked_within_days', 'flows.cond.booking.bookedWithin', 'days'),
    ],
  },
]

export interface FieldOpDef {
  op: string
  lbl: MessageKey
  input: ValueInput
}

const o = (op: string, lbl: MessageKey, input: ValueInput = 'none'): FieldOpDef => ({ op, lbl, input })

/** Custom-field operators by the field's kind (dates live in text fields). */
export const FIELD_OPS: Record<string, FieldOpDef[]> = {
  text: [
    o('equals', 'flows.cond.op.equals', 'text'),
    o('not_equals', 'flows.cond.op.notEquals', 'text'),
    o('contains', 'flows.cond.op.contains', 'text'),
    o('any_of', 'flows.cond.op.anyOf', 'words'),
    o('is_empty', 'flows.cond.op.isEmpty'),
    o('not_empty', 'flows.cond.op.notEmpty'),
    o('before', 'flows.cond.op.before', 'date'),
    o('after', 'flows.cond.op.after', 'date'),
    o('within_last_days', 'flows.cond.op.withinLast', 'days'),
    o('within_next_days', 'flows.cond.op.withinNext', 'days'),
  ],
  number: [
    o('equals', 'flows.cond.op.equals', 'number'),
    o('not_equals', 'flows.cond.op.notEquals', 'number'),
    o('gt', 'flows.cond.op.gt', 'number'),
    o('lt', 'flows.cond.op.lt', 'number'),
    o('is_empty', 'flows.cond.op.isEmpty'),
    o('not_empty', 'flows.cond.op.notEmpty'),
  ],
  boolean: [o('is_true', 'flows.cond.op.isTrue'), o('is_false', 'flows.cond.op.isFalse'), o('is_empty', 'flows.cond.op.isEmpty')],
  select: [
    o('equals', 'flows.cond.op.equals', 'fieldOption'),
    o('not_equals', 'flows.cond.op.notEquals', 'fieldOption'),
    o('any_of', 'flows.cond.op.anyOf', 'fieldOptions'),
    o('is_empty', 'flows.cond.op.isEmpty'),
    o('not_empty', 'flows.cond.op.notEmpty'),
  ],
  multiselect: [
    o('contains', 'flows.cond.op.includes', 'fieldOption'),
    o('any_of', 'flows.cond.op.anyOf', 'fieldOptions'),
    o('is_empty', 'flows.cond.op.isEmpty'),
    o('not_empty', 'flows.cond.op.notEmpty'),
  ],
}

/** Every field operator (a field whose kind is unknown offers all of them). */
export const ALL_FIELD_OPS: FieldOpDef[] = (() => {
  const seen = new Set<string>()
  const out: FieldOpDef[] = []
  for (const list of [FIELD_OPS.text, FIELD_OPS.number, FIELD_OPS.boolean, FIELD_OPS.select])
    for (const d of list) if (!seen.has(d.op)) (seen.add(d.op), out.push(d))
  return out
})()

export const fieldOpsFor = (kind: string | undefined) => (kind && FIELD_OPS[kind]) || ALL_FIELD_OPS

export function checkDef(cond: FlowCondition): CheckDef | undefined {
  for (const g of CHECK_GROUPS) for (const d of g.checks) if (d.subject === cond.subject && d.op === cond.op) return d
  return undefined
}

/** The input a condition's value needs (field operators depend on the kind). */
export function inputFor(cond: FlowCondition, fieldKind?: string): ValueInput {
  if (cond.subject === 'field') return fieldOpsFor(fieldKind).find((d) => d.op === cond.op)?.input ?? ALL_FIELD_OPS.find((d) => d.op === cond.op)?.input ?? 'text'
  return checkDef(cond)?.input ?? 'none'
}

/** Is the condition filled in enough to mean something? */
export function conditionComplete(cond: FlowCondition, fieldKind?: string): boolean {
  if (cond.subject === 'field' && !String(cond.field ?? '').trim()) return false
  const input = inputFor(cond, fieldKind)
  const v = String(cond.value ?? '').trim()
  switch (input) {
    case 'none':
    case 'optedScope':
      return true
    case 'days':
    case 'hours':
    case 'count':
      return Number(cond.amount) > 0
    case 'fieldOptions':
      return (cond.values?.length ?? 0) > 0 || !!v
    default:
      return !!v
  }
}

// ---- AI decisions -------------------------------------------------------------------

export const MAX_OPTIONS = 6

export function aiOptionsOf(cfg: FlowConfig | undefined): DecisionOption[] {
  const raw = Array.isArray(cfg?.options) ? (cfg!.options as unknown[]) : []
  return raw
    .filter((x): x is DecisionOption => !!x && typeof x === 'object' && typeof (x as DecisionOption).id === 'string')
    .slice(0, MAX_OPTIONS)
}

/** The next unused option handle (opt-1…opt-6). */
export function nextOptionId(options: DecisionOption[]): string {
  for (let i = 1; i <= MAX_OPTIONS + 6; i++) if (!options.some((x) => x.id === `opt-${i}`)) return `opt-${i}`
  return `opt-${options.length + 1}`
}

export const HISTORY_SCOPES = ['all', 'channel', 'conversation'] as const
export type HistoryScope = (typeof HISTORY_SCOPES)[number]

export const historyOf = (cfg: FlowConfig | undefined) => {
  const n = Number(cfg?.historyMessages)
  const scope = String(cfg?.historyScope ?? '')
  return {
    messages: Number.isFinite(n) && n > 0 ? Math.min(100, Math.max(5, Math.round(n))) : 20,
    scope: (HISTORY_SCOPES as readonly string[]).includes(scope) ? (scope as HistoryScope) : 'all',
    sinceEntered: cfg?.sinceEntered === true || cfg?.sinceEntered === 'true',
  }
}

/** "when unsure" for a kind (defaults: NO / none of these). */
export function unsureOf(cfg: FlowConfig | undefined): string {
  const pick = decisionKind(cfg) === 'ai_pick'
  const u = String(cfg?.unsure ?? '')
  const allowed = pick ? ['none', 'wait', 'end'] : ['yes', 'no', 'wait', 'end']
  return allowed.includes(u) ? u : pick ? 'none' : 'no'
}

export const thresholdOf = (cfg: FlowConfig | undefined) => {
  const n = Number(cfg?.threshold)
  return Number.isFinite(n) && cfg?.threshold !== '' && cfg?.threshold !== undefined ? Math.min(1, Math.max(0, n)) : 0.6
}

/** A fresh config for each decision kind (keys the other kinds use are kept). */
export const AI_DEFAULTS: FlowConfig = {
  question: '',
  historyMessages: 20,
  historyScope: 'all',
  sinceEntered: false,
  threshold: 0.6,
  maxAsks: 2,
  waitHours: 24,
}

// ---- outcomes (branches) -----------------------------------------------------------------

export interface Outcome {
  /** the edge's sourceHandle */
  handle: string
  /** the words stored on the edge label (English for yes/no; the option's own label) */
  stored: string
  /** shown: a translation key, or the operator's own text */
  key?: MessageKey
  vars?: Vars
  text?: string
  tone: 'yes' | 'no' | 'option' | 'none'
}

/** A decision's outcomes: YES / NO, or one per option + "None of these". */
export function decisionOutcomes(cfg: FlowConfig | undefined): Outcome[] {
  if (decisionKind(cfg) === 'ai_pick') {
    const opts = aiOptionsOf(cfg)
    return [
      ...opts.map(
        (x, i): Outcome => ({
          handle: x.id,
          stored: x.label.trim() || `Option ${i + 1}`,
          text: x.label.trim() || undefined,
          key: x.label.trim() ? undefined : 'flows.branch.option',
          vars: { n: i + 1 },
          tone: 'option',
        }),
      ),
      { handle: 'none', stored: 'None of these', key: 'flows.branch.none', tone: 'none' },
    ]
  }
  return [
    { handle: 'yes', stored: 'Yes', key: 'common.status.yes', tone: 'yes' },
    { handle: 'no', stored: 'No', key: 'common.status.no', tone: 'no' },
  ]
}
