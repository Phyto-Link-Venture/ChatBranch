import type { FlowConfig, FlowEdge, FlowNode, NodeKind, WorkflowDef } from './types'
import { AI_MESSAGE } from './nodeConfig'
import { autoLayout } from './steps'

/**
 * "Start from a template" — industry-neutral starter flows. Each is a plain
 * graph built from the same step types the builder offers, laid out by the
 * same auto-layout, and shipped PAUSED: the operator reads it, adjusts the
 * words, and goes live. Node ids only need to be unique within a flow.
 */

export interface StarterTemplate {
  id: string
  name: string
  hint: string
  /** the steps in one line, for the gallery card */
  outline: string[]
  build: () => { nodes: FlowNode[]; edges: FlowEdge[] }
}

const n = (id: string, type: NodeKind, config: FlowConfig, label = ''): FlowNode => ({
  id,
  type,
  position: { x: 0, y: 0 },
  data: { label, config },
})

let k = 0
const e = (source: string, target: string, handle?: 'yes' | 'no', followup = false): FlowEdge => ({
  id: `e${++k}-${source}-${target}`,
  source,
  target,
  ...(handle
    ? { sourceHandle: handle, label: followup ? (handle === 'yes' ? 'Replied' : 'No reply') : handle === 'yes' ? 'Yes' : 'No' }
    : {}),
  data: { config: { timing: 'Immediately' } },
})

const laidOut = (nodes: FlowNode[], edges: FlowEdge[]) => autoLayout({ nodes, edges })

export const STARTER_TEMPLATES: StarterTemplate[] = [
  {
    id: 'welcome-series',
    name: 'Welcome series',
    hint: 'Greet every new contact, then send a getting-started email two days later.',
    outline: ['Contact created', 'WhatsApp welcome', 'Wait 2 days', 'Email guide'],
    build: () =>
      laidOut(
        [
          n('t1', 'trigger', { when: 'Contact created', repeat: 'Once' }),
          n('a1', 'action', {
            channels: ['WhatsApp'], waMode: 'text', humanize: true,
            script: 'Hi {{first_name}}, welcome to {{business_name}}! Reply here any time — we usually answer within minutes.',
          }),
          n('w1', 'wait', { mode: 'time', amount: 2, unit: 'days' }),
          n('a2', 'action', {
            channels: ['Email'], subject: 'Getting started with {{business_name}}',
            script: "Hi {{first_name}},\n\nHere's a quick guide to getting the most from us: {{website}}\n\nQuestions? Just reply to this email.",
          }),
          n('e1', 'end', { logCode: 'WELCOMED', updateMemory: 'Yes' }),
        ],
        [e('t1', 'a1'), e('a1', 'w1'), e('w1', 'a2'), e('a2', 'e1')],
      ),
  },
  {
    id: 'appointment-reminder',
    name: 'Appointment reminder',
    hint: 'The day before an appointment: remind, ask to confirm, and hand anyone unsure to the team.',
    outline: ['1 day before the appointment', 'Reminder', 'Wait for a reply', 'If they said yes'],
    build: () =>
      laidOut(
        [
          n('t1', 'trigger', { when: 'Date field', field: 'appointment_date', dateWhen: 'before', days: 1, repeat: 'Every time' }),
          n('a1', 'action', {
            channels: ['WhatsApp'], waMode: 'text',
            script: 'Hi {{first_name}}, a reminder about your appointment with {{business_name}} tomorrow. Reply YES to confirm, or tell us a time that suits you better.',
          }),
          n('w1', 'wait', { mode: 'reply', timeoutAmount: 12, timeoutUnit: 'hours' }),
          n('d1', 'decision', { basedOn: 'They said yes' }),
          n('u1', 'update', { op: 'add_tag', tag: 'confirmed' }),
          n('e1', 'end', { logCode: 'CONFIRMED', updateMemory: 'Yes' }),
          n('h1', 'handover', { team: '', priority: 'High', reason: '{{first_name}} has not confirmed tomorrow’s appointment — please check in or reschedule.' }),
          n('e2', 'end', { logCode: 'NEEDS_FOLLOW_UP', updateMemory: 'Yes' }),
        ],
        [e('t1', 'a1'), e('a1', 'w1'), e('w1', 'd1'), e('d1', 'u1', 'yes'), e('u1', 'e1'), e('d1', 'h1', 'no'), e('h1', 'e2')],
      ),
  },
  {
    id: 're-engage',
    name: 'Re-engage quiet contacts',
    hint: 'Weekday mornings, the AI checks in with contacts tagged “quiet” and follows up once.',
    outline: ['Weekdays at 10:00', 'AI check-in', 'Follow up if no reply'],
    build: () =>
      laidOut(
        [
          n('t1', 'trigger', { when: 'Scheduled', time: '10:00', weekdays: ['mon', 'tue', 'wed', 'thu', 'fri'], onlyTag: 'quiet', repeat: 'Once' }),
          n('a1', 'action', {
            channels: [AI_MESSAGE, 'WhatsApp'], skill: '', waMode: 'text', humanize: true,
            script: 'Write a short, friendly check-in. Mention what they last asked about if you know it. No pressure, no discounts.',
          }),
          n('f1', 'followup', {
            waitAmount: 3, waitUnit: 'days', maxFollowUps: 1, channels: ['WhatsApp'], waMode: 'text',
            script: 'Just floating this back up, {{first_name}} — anything we can help with?', stopOnResolved: true,
          }),
          n('u1', 'update', { op: 'remove_tag', tag: 'quiet' }),
          n('e1', 'end', { logCode: 'RE_ENGAGED', updateMemory: 'Yes' }),
          n('e2', 'end', { logCode: 'STILL_QUIET', updateMemory: 'No' }),
        ],
        [e('t1', 'a1'), e('a1', 'f1'), e('f1', 'u1', 'yes', true), e('u1', 'e1'), e('f1', 'e2', 'no', true)],
      ),
  },
  {
    id: 'lead-qualification',
    name: 'Lead qualification',
    hint: 'A new lead gets an AI greeting; buying signals go straight to the team, the rest are nurtured.',
    outline: ['Tagged “lead”', 'AI greeting', 'Wait for a reply', 'If they mention buying'],
    build: () =>
      laidOut(
        [
          n('t1', 'trigger', { when: 'Tag added', tag: 'lead', repeat: 'Once' }),
          n('a1', 'action', {
            channels: [AI_MESSAGE, 'WhatsApp'], skill: '', waMode: 'text',
            script: 'Greet the lead warmly, introduce {{business_name}} in one sentence, and ask what they are looking for and by when.',
          }),
          n('w1', 'wait', { mode: 'reply', timeoutAmount: 1, timeoutUnit: 'days' }),
          n('d1', 'decision', { basedOn: 'They replied' }),
          n('d2', 'decision', { basedOn: 'Reply contains', keyword: 'price, quote, buy, demo, today, now' }),
          n('h1', 'handover', { team: '', priority: 'High', reason: 'Hot lead — {{first_name}} is asking about buying.' }),
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
    id: 'feedback-request',
    name: 'Feedback request',
    hint: 'A day after a job is done, ask for a 1–5 rating; happy customers get a review link, others reach a person.',
    outline: ['Tagged “completed”', 'Wait 1 day', 'Ask for a rating', 'If it is a 4 or 5'],
    build: () =>
      laidOut(
        [
          n('t1', 'trigger', { when: 'Tag added', tag: 'completed', repeat: 'Once' }),
          n('w0', 'wait', { mode: 'time', amount: 1, unit: 'days' }),
          n('a1', 'action', {
            channels: ['WhatsApp'], waMode: 'text',
            script: 'Hi {{first_name}}, how did we do? Reply with a number from 1 (poor) to 5 (great).',
          }),
          n('w1', 'wait', { mode: 'reply', timeoutAmount: 3, timeoutUnit: 'days' }),
          n('d1', 'decision', { basedOn: 'Reply contains', keyword: '4, 5, great, good, love' }),
          n('a2', 'action', {
            channels: ['WhatsApp'], waMode: 'text',
            script: 'Thank you, {{first_name}}! Would you mind leaving us a short review? {{website}}',
          }),
          n('e1', 'end', { logCode: 'HAPPY', updateMemory: 'Yes' }),
          n('h1', 'handover', { team: '', priority: 'Normal', reason: 'Low rating or no answer to the feedback request — worth a personal follow-up.' }),
          n('e2', 'end', { logCode: 'FOLLOW_UP', updateMemory: 'Yes' }),
        ],
        [
          e('t1', 'w0'), e('w0', 'a1'), e('a1', 'w1'), e('w1', 'd1'),
          e('d1', 'a2', 'yes'), e('a2', 'e1'), e('d1', 'h1', 'no'), e('h1', 'e2'),
        ],
      ),
  },
]

/** A fresh, paused workflow from a template (or a blank one). */
export function workflowFromTemplate(t: StarterTemplate | null, id: string): WorkflowDef {
  if (!t) {
    const g = autoLayout({
      nodes: [n('t1', 'trigger', { when: 'Tag added', tag: '', repeat: 'Once' })],
      edges: [],
    })
    return { id, name: 'New flow', hint: '', active: false, nodes: g.nodes, edges: g.edges }
  }
  const g = t.build()
  return { id, name: t.name, hint: t.hint, active: false, nodes: structuredClone(g.nodes), edges: structuredClone(g.edges) }
}
