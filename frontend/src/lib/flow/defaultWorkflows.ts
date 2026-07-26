import { initialNodes, initialEdges } from './initialFlow'
import type { FlowEdge, FlowNode, WorkflowDef } from './types'

/**
 * The DEFAULT workflow library the builder ships with — a small set of clean,
 * INDUSTRY-NEUTRAL starters so a client never begins from a blank canvas. Swap
 * the copy for orders, bookings, tickets, invoices, renewals or leads and the
 * shapes still fit. The client adds as many of their own workflows as they want
 * on top of these (the "+ New workflow" button).
 *
 * Workflows are NOT agent skills: a workflow is the visual flow of steps;
 * an agent skill (lib/flow/defaultAgentSkills.ts) is the instruction module
 * a step runs with — each Action's settings point at one via `config.skill`.
 *
 * Each entry: { id, name, hint, active, nodes, edges } — nodes/edges are the
 * same JSON shape as Export/Import and the same shape the backend flow engine
 * (backend/flows/engine.py) executes. ALL starters ship `active: false` — the
 * client arms the ones they actually want the engine to run live.
 */

/* ---- Appointment reminder — remind before the date, offer reschedule on reply ---- */
const appointmentNodes: FlowNode[] = [
  { id: 't1', type: 'trigger', position: { x: 300, y: 0 },
    data: { label: 'Appointment is 1 day away',
      config: { when: 'Days before due date', days: 1, window: 'Any allowed hours', repeat: 'Once' } } },
  { id: 'a1', type: 'action', position: { x: 300, y: 170 },
    data: { label: 'Send the reminder',
      config: { skill: '03 · Scheduling & rescheduling', channels: ['WhatsApp', 'SMS'], model: 'Haiku 4.5', script: 'Hi {{first_name}}, a friendly reminder about your {{record_title}} on {{due_date}}. Reply here if you need to change the time — otherwise, see you then!', maxAttempts: 1 } } },
  { id: 'd1', type: 'decision', position: { x: 300, y: 380 },
    data: { label: 'Will the customer attend?',
      config: { basedOn: "Contact's answer" } } },
  { id: 'e1', type: 'end', position: { x: 80, y: 570 },
    data: { label: 'Confirmed — see you then',
      config: { logCode: 'RESOLVED', updateMemory: 'Yes' } } },
  { id: 'a2', type: 'action', position: { x: 520, y: 570 },
    data: { label: 'AI offers to reschedule',
      config: { skill: '03 · Scheduling & rescheduling', channels: ['WhatsApp', 'AI voice call'], model: 'Sonnet 5', script: 'No problem, {{first_name}} — what date and time would suit you better for your {{record_title}}? I can lock it in right away.', maxAttempts: 1 } } },
  { id: 'e2', type: 'end', position: { x: 520, y: 760 },
    data: { label: 'Rescheduled',
      config: { logCode: 'FOLLOW-UP-SET', updateMemory: 'Yes' } } },
]
const appointmentEdges: FlowEdge[] = [
  { id: 'e-t1-a1', source: 't1', target: 'a1', animated: true },
  { id: 'e-a1-d1', source: 'a1', target: 'd1', label: 'On contact reply', data: { config: { timing: 'On contact reply' } } },
  { id: 'e-d1-e1', source: 'd1', sourceHandle: 'yes', target: 'e1', label: 'Yes' },
  { id: 'e-d1-a2', source: 'd1', sourceHandle: 'no', target: 'a2', label: 'No' },
  { id: 'e-a2-e2', source: 'a2', target: 'e2' },
]

/* ---- Re-engagement — a contact went quiet, nudge to reconnect ---- */
const reengageNodes: FlowNode[] = [
  { id: 't1', type: 'trigger', position: { x: 300, y: 0 },
    data: { label: "Contact went quiet (tagged 'no_reply')",
      config: { when: 'Contact has tag', tag: 'no_reply', window: 'Any allowed hours', repeat: 'Once' } } },
  { id: 'a1', type: 'action', position: { x: 300, y: 170 },
    data: { label: 'Friendly check-in nudge',
      config: { skill: '02 · Order / record status update', channels: ['WhatsApp', 'Email'], model: 'Haiku 4.5', script: "Hi {{first_name}}, just checking in about {{record_title}} — anything I can help with? You can reply right here, or take a look: {{link}}", maxAttempts: 1 } } },
  { id: 'd1', type: 'decision', position: { x: 300, y: 380 },
    data: { label: 'Did they reply?',
      config: { basedOn: "Contact's answer" } } },
  { id: 'a2', type: 'action', position: { x: 80, y: 570 },
    data: { label: 'Back in touch — pick up where they left off',
      config: { skill: '01 · Qualification', channels: ['WhatsApp'], model: 'Sonnet 5', script: 'Great to hear from you, {{first_name}}! How can I help with {{record_title}} today?', maxAttempts: 1 } } },
  { id: 'e1', type: 'end', position: { x: 80, y: 760 },
    data: { label: 'Re-engaged — conversation continues',
      config: { logCode: 'RESOLVED', updateMemory: 'Yes' } } },
  { id: 'a3', type: 'action', position: { x: 520, y: 570 },
    data: { label: 'One more nudge on their preferred channel',
      config: { skill: '03 · Scheduling & rescheduling', channels: ['SMS', 'Email'], model: 'Haiku 4.5', script: "Still here whenever you're ready, {{first_name}}. Reach us anytime at {{support_number}} or {{link}}.", maxAttempts: 1 } } },
  { id: 'e2', type: 'end', position: { x: 520, y: 760 },
    data: { label: 'No response — leave the door open',
      config: { logCode: 'NO-RESPONSE', updateMemory: 'Yes' } } },
]
const reengageEdges: FlowEdge[] = [
  { id: 'e-t1-a1', source: 't1', target: 'a1', animated: true },
  { id: 'e-a1-d1', source: 'a1', target: 'd1', label: 'On contact reply', data: { config: { timing: 'On contact reply' } } },
  { id: 'e-d1-a2', source: 'd1', sourceHandle: 'yes', target: 'a2', label: 'Yes' },
  { id: 'e-a2-e1', source: 'a2', target: 'e1' },
  { id: 'e-d1-a3', source: 'd1', sourceHandle: 'no', target: 'a3', label: 'No' },
  { id: 'e-a3-e2', source: 'a3', target: 'e2' },
]

/* ---- New-lead welcome — greet + qualify a freshly tagged lead ---- */
const newLeadNodes: FlowNode[] = [
  { id: 't1', type: 'trigger', position: { x: 300, y: 0 },
    data: { label: "New lead added (tagged 'new_lead')",
      config: { when: 'Contact has tag', tag: 'new_lead', window: 'Any allowed hours', repeat: 'Once' } } },
  { id: 'a1', type: 'action', position: { x: 300, y: 170 },
    data: { label: 'Send a warm welcome',
      config: { skill: '00 · Greeting & identity check', channels: ['WhatsApp', 'Email'], model: 'Haiku 4.5', script: "Hi {{first_name}}, welcome to {{business_name}}! I'm {{agent_name}}. What can I help you with today?", maxAttempts: 1 } } },
  { id: 'd1', type: 'decision', position: { x: 300, y: 380 },
    data: { label: 'Did the lead reply?',
      config: { basedOn: "Contact's answer" } } },
  { id: 'a2', type: 'action', position: { x: 80, y: 570 },
    data: { label: 'Qualify — understand what they need',
      config: { skill: '01 · Qualification', channels: ['WhatsApp'], model: 'Sonnet 5', script: 'Thanks {{first_name}}! To point you the right way — what are you looking for today? I can share options and pricing, or connect you with the team.', maxAttempts: 1 } } },
  { id: 'd2', type: 'decision', position: { x: 80, y: 760 },
    data: { label: 'Ready to move forward?',
      config: { basedOn: "Contact's answer" } } },
  { id: 'h1', type: 'handover', position: { x: 80, y: 950 },
    data: { label: 'Warm lead — hand to the team',
      config: { team: 'Senior agent', priority: 'Normal', reason: 'New lead qualified and ready to speak with a person.' } } },
  { id: 'e1', type: 'end', position: { x: 300, y: 950 },
    data: { label: 'Not ready yet — nurture later',
      config: { logCode: 'NURTURE', updateMemory: 'Yes' } } },
  { id: 'a3', type: 'action', position: { x: 520, y: 570 },
    data: { label: 'No reply yet — gentle follow-up',
      config: { skill: '02 · Order / record status update', channels: ['Email'], model: 'Haiku 4.5', script: "Hi {{first_name}}, just making sure you got my note. Whenever you're ready, I'm here to help — {{website}}.", maxAttempts: 1 } } },
  { id: 'e2', type: 'end', position: { x: 520, y: 760 },
    data: { label: 'Awaiting the lead',
      config: { logCode: 'NO-RESPONSE', updateMemory: 'Yes' } } },
]
const newLeadEdges: FlowEdge[] = [
  { id: 'e-t1-a1', source: 't1', target: 'a1', animated: true },
  { id: 'e-a1-d1', source: 'a1', target: 'd1', label: 'On contact reply', data: { config: { timing: 'On contact reply' } } },
  { id: 'e-d1-a2', source: 'd1', sourceHandle: 'yes', target: 'a2', label: 'Yes' },
  { id: 'e-a2-d2', source: 'a2', target: 'd2', label: 'On contact reply', data: { config: { timing: 'On contact reply' } } },
  { id: 'e-d2-h1', source: 'd2', sourceHandle: 'yes', target: 'h1', label: 'Yes' },
  { id: 'e-d2-e1', source: 'd2', sourceHandle: 'no', target: 'e1', label: 'No' },
  { id: 'e-d1-a3', source: 'd1', sourceHandle: 'no', target: 'a3', label: 'No' },
  { id: 'e-a3-e2', source: 'a3', target: 'e2' },
]

export const DEFAULT_WORKFLOWS: WorkflowDef[] = [
  { id: 'wf-order-updates', name: 'Order confirmation & delivery updates',
    hint: 'New order placed → confirm → out-for-delivery update → delivered, or reschedule a missed delivery',
    active: false,
    nodes: initialNodes, edges: initialEdges },
  { id: 'wf-appointment-reminder', name: 'Appointment reminder',
    hint: '1 day before the date → send a reminder → confirm attendance or offer to reschedule',
    active: false,
    nodes: appointmentNodes, edges: appointmentEdges },
  { id: 'wf-reengagement', name: 'Re-engagement / follow-up',
    hint: 'A contact goes quiet → friendly nudge → reconnect, or a final soft follow-up',
    active: false,
    nodes: reengageNodes, edges: reengageEdges },
  { id: 'wf-new-lead-welcome', name: 'New-lead welcome',
    hint: 'New lead tagged → warm welcome → qualify and hand a ready lead to the team',
    active: false,
    nodes: newLeadNodes, edges: newLeadEdges },
]
