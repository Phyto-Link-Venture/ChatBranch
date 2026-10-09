import { translate, type MessageKey, type TFunction } from '@/i18n'
import { en } from '@/i18n/messages/en'
import type { FlowCondition, FlowConfig, FlowEdge, FlowNode, NodeKind, WorkflowDef } from './types'
import { AI_MESSAGE } from './nodeConfig'
import { AI_DEFAULTS, KIND_AI_PICK, KIND_CONDITIONS } from './conditions'
import { autoLayout } from './steps'

/**
 * "Start from a template" — industry-neutral starter journeys. Each is a plain
 * graph built from the same step types the builder offers, laid out by the
 * same auto-layout, and shipped PAUSED: the operator reads it, adjusts the
 * words, and goes live. Node ids only need to be unique within a flow.
 *
 * Names, descriptions and the starter WORDING (messages, AI briefs, handover
 * notes, reply keywords, AI questions and options) come from
 * `flows.starters.*`, so a journey started in Malay or Chinese reads in that
 * language. Config VALUES (trigger names, channels, decision kinds, condition
 * subjects/operators), tags and outcome codes stay English — the engine
 * matches them. Keyword lists keep ASCII commas: the engine splits on them.
 */

export interface StarterTemplate {
  id: string
  /** translation keys */
  name: MessageKey
  hint: MessageKey
  /** the steps in one line, for the gallery card (translation keys) */
  outline: MessageKey[]
  build: (t: TFunction) => { nodes: FlowNode[]; edges: FlowEdge[] }
}

const n = (id: string, type: NodeKind, config: FlowConfig, label = ''): FlowNode => ({
  id,
  type,
  position: { x: 0, y: 0 },
  data: { label, config },
})

let k = 0
/** A line; on a branch it carries its handle and stored label (English yes/no
 * words — the engine reads them, the dashboard translates them — or an AI
 * option's own label). */
const e = (source: string, target: string, handle?: string, label?: string): FlowEdge => ({
  id: `e${++k}-${source}-${target}`,
  source,
  target,
  ...(handle ? { sourceHandle: handle, label: label ?? (handle === 'yes' ? 'Yes' : handle === 'no' ? 'No' : handle) } : {}),
  data: { config: { timing: 'Immediately' } },
})

/** A "Check conditions" decision (all of). */
const check = (...conditions: FlowCondition[]): FlowConfig => ({ basedOn: KIND_CONDITIONS, match: 'all', conditions })

const laidOut = (nodes: FlowNode[], edges: FlowEdge[]) => autoLayout({ nodes, edges })

export const STARTER_TEMPLATES: StarterTemplate[] = [
  {
    id: 'welcome-series',
    name: 'flows.starters.welcome.name',
    hint: 'flows.starters.welcome.hint',
    outline: [
      'flows.starters.outline.contactCreated',
      'flows.starters.outline.whatsappWelcome',
      'flows.starters.outline.wait2Days',
      'flows.starters.outline.emailGuide',
    ],
    build: (t) =>
      laidOut(
        [
          n('t1', 'trigger', { when: 'Contact created', repeat: 'Once' }),
          n('a1', 'action', {
            channels: ['WhatsApp'], waMode: 'text', humanize: true,
            script: t('flows.starters.welcome.message'),
          }),
          n('w1', 'wait', { mode: 'time', amount: 2, unit: 'days' }),
          n('a2', 'action', {
            channels: ['Email'], subject: t('flows.starters.welcome.emailSubject'),
            script: t('flows.starters.welcome.emailBody'),
          }),
          n('e1', 'end', { logCode: 'WELCOMED', updateMemory: 'Yes' }),
        ],
        [e('t1', 'a1'), e('a1', 'w1'), e('w1', 'a2'), e('a2', 'e1')],
      ),
  },
  {
    id: 'appointment-reminder',
    name: 'flows.starters.appointment.name',
    hint: 'flows.starters.appointment.hint',
    outline: [
      'flows.starters.outline.dayBefore',
      'flows.starters.outline.reminder',
      'flows.starters.outline.waitForReply',
      'flows.starters.outline.ifSaidYes',
    ],
    build: (t) =>
      laidOut(
        [
          n('t1', 'trigger', { when: 'Date field', field: 'appointment_date', dateWhen: 'before', days: 1, repeat: 'Every time' }),
          n('a1', 'action', {
            channels: ['WhatsApp'], waMode: 'text',
            script: t('flows.starters.appointment.reminder'),
          }),
          n('w1', 'wait', { mode: 'reply', timeoutAmount: 12, timeoutUnit: 'hours' }),
          n('d1', 'decision', check({ subject: 'reply', op: 'said_yes' })),
          n('u1', 'update', { op: 'add_tag', tag: 'confirmed' }),
          n('e1', 'end', { logCode: 'CONFIRMED', updateMemory: 'Yes' }),
          n('h1', 'handover', { team: '', priority: 'High', reason: t('flows.starters.appointment.handoverReason') }),
          n('e2', 'end', { logCode: 'NEEDS_FOLLOW_UP', updateMemory: 'Yes' }),
        ],
        [e('t1', 'a1'), e('a1', 'w1'), e('w1', 'd1'), e('d1', 'u1', 'yes'), e('u1', 'e1'), e('d1', 'h1', 'no'), e('h1', 'e2')],
      ),
  },
  {
    id: 're-engage',
    name: 'flows.starters.reengage.name',
    hint: 'flows.starters.reengage.hint',
    outline: [
      'flows.starters.outline.weekdaysAt10',
      'flows.starters.outline.aiCheckIn',
      'flows.stepChoice.followup',
    ],
    build: (t) =>
      laidOut(
        [
          n('t1', 'trigger', { when: 'Scheduled', time: '10:00', weekdays: ['mon', 'tue', 'wed', 'thu', 'fri'], onlyTag: 'quiet', repeat: 'Once' }),
          n('a1', 'action', {
            channels: [AI_MESSAGE, 'WhatsApp'], skill: '', waMode: 'text', humanize: true,
            script: t('flows.starters.reengage.aiBrief'),
          }),
          n('f1', 'followup', {
            waitAmount: 3, waitUnit: 'days', maxFollowUps: 1, channels: ['WhatsApp'], waMode: 'text',
            script: t('flows.starters.reengage.followUp'), stopOnResolved: true,
          }),
          n('u1', 'update', { op: 'remove_tag', tag: 'quiet' }),
          n('e1', 'end', { logCode: 'RE_ENGAGED', updateMemory: 'Yes' }),
          n('e2', 'end', { logCode: 'STILL_QUIET', updateMemory: 'No' }),
        ],
        [e('t1', 'a1'), e('a1', 'f1'), e('f1', 'u1', 'yes', 'Replied'), e('u1', 'e1'), e('f1', 'e2', 'no', 'No reply')],
      ),
  },
  {
    id: 'lead-qualification',
    name: 'flows.starters.lead.name',
    hint: 'flows.starters.lead.hint',
    outline: [
      'flows.starters.outline.taggedLead',
      'flows.starters.outline.aiGreeting',
      'flows.starters.outline.waitForReply',
      'flows.starters.outline.ifBuying',
    ],
    build: (t) =>
      laidOut(
        [
          n('t1', 'trigger', { when: 'Tag added', tag: 'lead', repeat: 'Once' }),
          n('a1', 'action', {
            channels: [AI_MESSAGE, 'WhatsApp'], skill: '', waMode: 'text',
            script: t('flows.starters.lead.aiBrief'),
          }),
          n('w1', 'wait', { mode: 'reply', timeoutAmount: 1, timeoutUnit: 'days' }),
          n('d1', 'decision', check({ subject: 'reply', op: 'replied' })),
          n('d2', 'decision', check({ subject: 'reply', op: 'contains_any', value: t('flows.starters.lead.buyingWords') })),
          n('h1', 'handover', { team: '', priority: 'High', reason: t('flows.starters.lead.handoverReason') }),
          n('e1', 'end', { logCode: 'QUALIFIED', updateMemory: 'Yes' }),
          n('u1', 'update', { op: 'add_tag', tag: 'nurture' }),
          n('e2', 'end', { logCode: 'NURTURE', updateMemory: 'Yes' }),
          n('u2', 'update', { op: 'add_tag', tag: 'no-response' }),
          n('e3', 'end', { logCode: 'NO_RESPONSE', updateMemory: 'No' }),
        ],
        [
          e('t1', 'a1'), e('a1', 'w1'), e('w1', 'd1'),
          e('d1', 'd2', 'yes'), e('d2', 'h1', 'yes'), e('h1', 'e1'), e('d2', 'u1', 'no'), e('u1', 'e2'),
          e('d1', 'u2', 'no'), e('u2', 'e3'),
        ],
      ),
  },
  {
    id: 'ai-lead-qualification',
    name: 'flows.starters.aiLead.name',
    hint: 'flows.starters.aiLead.hint',
    outline: [
      'flows.trigger.messageReceived',
      'flows.starters.outline.aiPicksPath',
      'flows.starters.outline.buyerQuestionComplaint',
    ],
    build: (t) => {
      const options = [
        { id: 'opt-1', label: t('flows.starters.aiLead.buyer'), description: t('flows.starters.aiLead.buyerHint') },
        { id: 'opt-2', label: t('flows.starters.aiLead.question'), description: t('flows.starters.aiLead.questionHint') },
        { id: 'opt-3', label: t('flows.starters.aiLead.complaint'), description: t('flows.starters.aiLead.complaintHint') },
      ]
      return laidOut(
        [
          n('t1', 'trigger', { when: 'Message received', channel: '', keyword: '', repeat: 'Once a day' }),
          n('d1', 'decision', {
            basedOn: KIND_AI_PICK,
            ...AI_DEFAULTS,
            question: t('flows.starters.aiLead.aiQuestion'),
            options,
            historyMessages: 20,
            historyScope: 'conversation',
            unsure: 'none',
          }),
          n('u1', 'update', { op: 'add_tag', tag: 'hot-lead' }),
          n('h1', 'handover', { team: '', priority: 'High', reason: t('flows.starters.aiLead.buyerHandover') }),
          n('e1', 'end', { logCode: 'BUYER', updateMemory: 'Yes' }),
          n('a2', 'action', {
            channels: [AI_MESSAGE, 'WhatsApp'], skill: '', waMode: 'text', humanize: true,
            script: t('flows.starters.aiLead.answerBrief'),
          }),
          n('e2', 'end', { logCode: 'QUESTION', updateMemory: 'Yes' }),
          n('h2', 'handover', { team: '', priority: 'Urgent', reason: t('flows.starters.aiLead.complaintHandover') }),
          n('e3', 'end', { logCode: 'COMPLAINT', updateMemory: 'Yes' }),
          n('e4', 'end', { logCode: 'OTHER', updateMemory: 'No' }),
        ],
        [
          e('t1', 'd1'),
          e('d1', 'u1', 'opt-1', options[0].label), e('u1', 'h1'), e('h1', 'e1'),
          e('d1', 'a2', 'opt-2', options[1].label), e('a2', 'e2'),
          e('d1', 'h2', 'opt-3', options[2].label), e('h2', 'e3'),
          e('d1', 'e4', 'none', 'None of these'),
        ],
      )
    },
  },
  {
    id: 'feedback-request',
    name: 'flows.starters.feedback.name',
    hint: 'flows.starters.feedback.hint',
    outline: [
      'flows.starters.outline.taggedCompleted',
      'flows.starters.outline.wait1Day',
      'flows.starters.outline.askRating',
      'flows.starters.outline.ifFourOrFive',
    ],
    build: (t) =>
      laidOut(
        [
          n('t1', 'trigger', { when: 'Tag added', tag: 'completed', repeat: 'Once' }),
          n('w0', 'wait', { mode: 'time', amount: 1, unit: 'days' }),
          n('a1', 'action', {
            channels: ['WhatsApp'], waMode: 'text',
            script: t('flows.starters.feedback.ask'),
          }),
          n('w1', 'wait', { mode: 'reply', timeoutAmount: 3, timeoutUnit: 'days' }),
          n('d1', 'decision', check({ subject: 'reply', op: 'contains_any', value: t('flows.starters.feedback.happyWords') })),
          n('a2', 'action', {
            channels: ['WhatsApp'], waMode: 'text',
            script: t('flows.starters.feedback.reviewAsk'),
          }),
          n('e1', 'end', { logCode: 'HAPPY', updateMemory: 'Yes' }),
          n('h1', 'handover', { team: '', priority: 'Normal', reason: t('flows.starters.feedback.handoverReason') }),
          n('e2', 'end', { logCode: 'FOLLOW_UP', updateMemory: 'Yes' }),
        ],
        [
          e('t1', 'w0'), e('w0', 'a1'), e('a1', 'w1'), e('w1', 'd1'),
          e('d1', 'a2', 'yes'), e('a2', 'e1'), e('d1', 'h1', 'no'), e('h1', 'e2'),
        ],
      ),
  },
]

/** English, for callers outside a component (DEFAULT_WORKFLOWS). */
const english: TFunction = (key, vars) => translate(en, 'en', key, vars)

/** A fresh, paused workflow from a template (or a blank one), worded in the
 * language `t` speaks. */
export function workflowFromTemplate(tpl: StarterTemplate | null, id: string, t: TFunction = english): WorkflowDef {
  if (!tpl) {
    const g = autoLayout({
      nodes: [n('t1', 'trigger', { when: 'Tag added', tag: '', repeat: 'Once' })],
      edges: [],
    })
    return { id, name: t('flows.builder.newFlowName'), hint: '', active: false, nodes: g.nodes, edges: g.edges }
  }
  const g = tpl.build(t)
  return { id, name: t(tpl.name), hint: t(tpl.hint), active: false, nodes: structuredClone(g.nodes), edges: structuredClone(g.edges) }
}
