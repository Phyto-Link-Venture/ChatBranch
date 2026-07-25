import { initialNodes, initialEdges } from './initialFlow'
import type { FlowEdge, FlowNode, WorkflowDef } from './types'

/**
 * The DEFAULT workflow library the builder ships with — one flow per common
 * scenario. The client can create as many workflows as they want on top
 * of these (the "+ New workflow" button); the defaults just make sure they
 * never start from a blank canvas.
 *
 * Workflows are NOT agent skills: a workflow is the visual flow of steps;
 * an agent skill (lib/flow/defaultAgentSkills.ts) is the instruction module
 * a step runs with — each Action's settings point at one via `config.skill`.
 *
 * Each entry: { id, name, hint, nodes, edges } — nodes/edges are the same
 * JSON shape as Export/Import. All defaults except the master journey ship
 * `active: false` — a client arms the ones they actually want live.
 */

/* ---- Pre-due reminders — prevent the record going past due ---- */
const predueNodes: FlowNode[] = [
  { id: 't1', type: 'trigger', position: { x: 300, y: 0 },
    data: { label: 'Record due in 7 days (client-tunable in day-config)',
      config: { when: 'Days before due date', days: 7, window: 'Any allowed hours', repeat: 'Once' } } },
  { id: 'a1', type: 'action', position: { x: 300, y: 170 },
    data: { label: '7 days before due — friendly reminder + link',
      config: { skill: '02 · Order / record status update', channels: ['WhatsApp', 'In-app push'], model: 'Haiku 4.5', script: 'Hi {{name}}! Your {{business_name}} {{record_title}} is due on {{due_date}}. View it here: {{link}}', maxAttempts: 1 } } },
  { id: 'a2', type: 'action', position: { x: 300, y: 380 },
    data: { label: '3 days & 1 day before due — nudge + final reminder',
      config: { skill: '02 · Order / record status update', channels: ['SMS', 'In-app push'], model: 'Haiku 4.5', script: 'Reminder: {{record_title}} due tomorrow ({{due_date}}). View it here: {{link}}', maxAttempts: 2 } } },
  { id: 'a3', type: 'action', position: { x: 300, y: 590 },
    data: { label: 'No response yet? Offer an easier way to confirm',
      config: { skill: '02 · Order / record status update', channels: ['WhatsApp'], model: 'Haiku 4.5', script: 'Confirm in one tap so you never miss an update: {{link}}', maxAttempts: 1 } } },
  { id: 'd1', type: 'decision', position: { x: 300, y: 800 },
    data: { label: 'Confirmed before due date?',
      config: { basedOn: 'Record status', status: 'confirmed' } } },
  { id: 'e1', type: 'end', position: { x: 80, y: 990 },
    data: { label: 'Confirmed on time — thank-you note, no case opened',
      config: { logCode: 'RESOLVED', updateMemory: 'Yes' } } },
  { id: 'e2', type: 'end', position: { x: 520, y: 990 },
    data: { label: 'Still open on day 1 → in-progress workflow takes over',
      config: { logCode: 'OPEN', updateMemory: 'Yes' } } },
]
const predueEdges: FlowEdge[] = [
  { id: 'e-t1-a1', source: 't1', target: 'a1', animated: true },
  { id: 'e-a1-a2', source: 'a1', target: 'a2' },
  { id: 'e-a2-a3', source: 'a2', target: 'a3' },
  { id: 'e-a3-d1', source: 'a3', target: 'd1' },
  { id: 'e-d1-e1', source: 'd1', sourceHandle: 'yes', target: 'e1', label: 'Yes' },
  { id: 'e-d1-e2', source: 'd1', sourceHandle: 'no', target: 'e2', label: 'No' },
]

/* ---- First follow-up call — standalone, one stage lane ---- */
const earlyNodes: FlowNode[] = [
  { id: 's1', type: 'stage', position: { x: 0, y: 100 }, width: 1160, height: 1500,
    data: { label: 'In progress — first contact & follow-up',
      config: { stage: 'Normal', rangeType: 'Days after due date', fromDay: 1, toDay: 7 } } },
  { id: 't1', type: 'trigger', position: { x: 380, y: -80 },
    data: { label: 'Record hits day 1 past due (from day-config)',
      config: { when: 'Days after due date', days: 1, window: '9:00–12:00', repeat: 'Daily until answered' } } },
  { id: 'a1', type: 'action', position: { x: 560, y: 70 }, parentId: 's1',
    data: { label: 'Call the contact — verify identity first',
      config: { skill: '00 · Greeting & identity check', channels: ['AI voice call'], model: 'Sonnet 5', script: 'Hello, may I speak to {{name}}? Just confirming a few details before we continue.', maxAttempts: 3 } } },
  { id: 'd1', type: 'decision', position: { x: 560, y: 260 }, parentId: 's1',
    data: { label: 'Right person on the line?',
      config: { basedOn: "Contact's answer" } } },
  { id: 'e1', type: 'end', position: { x: 860, y: 420 }, parentId: 's1',
    data: { label: 'Wrong person — end call, never discuss details with a third party',
      config: { logCode: 'WRONG-PERSON', updateMemory: 'Yes' } } },
  { id: 'a2', type: 'action', position: { x: 360, y: 420 }, parentId: 's1',
    data: { label: 'Load contact memory → tailor tone',
      config: { skill: '01 · Qualification', channels: ['Internal log'], model: 'Haiku 4.5', script: 'Pull contact memory; confirm what they need before proceeding.' } } },
  { id: 'd2', type: 'decision', position: { x: 360, y: 600 }, parentId: 's1',
    data: { label: 'Resolved on this call?',
      config: { basedOn: "Contact's answer" } } },
  { id: 'e2', type: 'end', position: { x: 90, y: 760 }, parentId: 's1',
    data: { label: 'Resolved → thank & close',
      config: { logCode: 'RESOLVED', updateMemory: 'Yes' } } },
  { id: 'a3', type: 'action', position: { x: 580, y: 760 }, parentId: 's1',
    data: { label: 'Offer the next step — reschedule, more info, or escalate',
      config: { skill: '03 · Scheduling & rescheduling', channels: ['AI voice call'], model: 'Sonnet 5', script: 'Offer to reschedule, share more information, or escalate — whichever the contact needs.', maxAttempts: 1 } } },
  { id: 'd3', type: 'decision', position: { x: 580, y: 950 }, parentId: 's1',
    data: { label: 'Needs a human?',
      config: { basedOn: "Contact's answer" } } },
  { id: 'a4', type: 'action', position: { x: 390, y: 1110 }, parentId: 's1',
    data: { label: 'Confirm follow-up: read-back + written confirmation',
      config: { skill: '03 · Scheduling & rescheduling', channels: ['WhatsApp', 'SMS'], model: 'Haiku 4.5', script: 'Confirming: {{record_title}} follow-up set for {{due_date}}.', maxAttempts: 1 } } },
  { id: 'e3', type: 'end', position: { x: 390, y: 1300 }, parentId: 's1',
    data: { label: 'Log outcome + update memory',
      config: { logCode: 'FOLLOW-UP-SET', updateMemory: 'Yes' } } },
  { id: 'h1', type: 'handover', position: { x: 1240, y: 1050 },
    data: { label: 'Escalate — needs a human decision',
      config: { team: 'Senior agent', priority: 'High', reason: "Contact needs a decision outside the agent's scope." } } },
]
const earlyEdges: FlowEdge[] = [
  { id: 'e-t1-a1', source: 't1', target: 'a1', animated: true },
  { id: 'e-a1-d1', source: 'a1', target: 'd1' },
  { id: 'e-d1-e1', source: 'd1', sourceHandle: 'no', target: 'e1', label: 'No' },
  { id: 'e-d1-a2', source: 'd1', sourceHandle: 'yes', target: 'a2', label: 'Yes' },
  { id: 'e-a2-d2', source: 'a2', target: 'd2' },
  { id: 'e-d2-e2', source: 'd2', sourceHandle: 'yes', target: 'e2', label: 'Yes' },
  { id: 'e-d2-a3', source: 'd2', sourceHandle: 'no', target: 'a3', label: 'No' },
  { id: 'e-a3-d3', source: 'a3', target: 'd3' },
  { id: 'e-d3-h1', source: 'd3', sourceHandle: 'yes', target: 'h1', label: 'Yes' },
  { id: 'e-d3-a4', source: 'd3', sourceHandle: 'no', target: 'a4', label: 'No' },
  { id: 'e-a4-e3', source: 'a4', target: 'e3' },
]

/* ---- Unresponsive-contact protocol — no answer / gone quiet ---- */
const ghostNodes: FlowNode[] = [
  { id: 't1', type: 'trigger', position: { x: 300, y: 0 },
    data: { label: 'No answer ×3 across 2+ channels (any stage)',
      config: { when: 'Days after due date', days: 3, window: 'Any allowed hours', repeat: 'Once' } } },
  { id: 'a1', type: 'action', position: { x: 300, y: 170 },
    data: { label: 'Step 1 — vary contact times & channels (never spam)',
      config: { skill: '03 · Scheduling & rescheduling', channels: ['AI voice call', 'WhatsApp'], model: 'Haiku 4.5', script: 'Try {{best_contact_time}} and the preferred channel ({{preferred_channel}}); rotate morning / lunch / evening slots.', maxAttempts: 3 } } },
  { id: 'd1', type: 'decision', position: { x: 300, y: 380 },
    data: { label: 'Reconnected?',
      config: { basedOn: 'Contact tag', tag: 'unresponsive' } } },
  { id: 'eR', type: 'end', position: { x: 40, y: 570 },
    data: { label: 'Back in contact — return to the normal flow',
      config: { logCode: 'RECONNECTED', updateMemory: 'Yes' } } },
  { id: 'a2', type: 'action', position: { x: 560, y: 570 },
    data: { label: 'Step 2 — writing-only check-in',
      config: { skill: '03 · Scheduling & rescheduling', channels: ['WhatsApp'], model: 'Haiku 4.5', script: 'Writing only: quick check-in on {{record_title}}. Reply on any channel: {{link}}', maxAttempts: 1 } } },
  { id: 'd2', type: 'decision', position: { x: 560, y: 780 },
    data: { label: 'Any reply by day 21?',
      config: { basedOn: 'Contact tag', tag: 'unresponsive' } } },
  { id: 'a3', type: 'action', position: { x: 560, y: 970 },
    data: { label: 'Step 3 — final written notice before escalation',
      config: { skill: '05 · Escalation to a human', channels: ['Email', 'WhatsApp'], model: 'Haiku 4.5', script: 'Final notice to {{name}}: without a reply, this will be handed to a human for follow-up. Support: {{support_number}}.', maxAttempts: 1 } } },
  { id: 'e2', type: 'end', position: { x: 560, y: 1180 },
    data: { label: 'Escalated without them by day 30 — a human takes it from here',
      config: { logCode: 'UNRESPONSIVE', updateMemory: 'Yes' } } },
]
const ghostEdges: FlowEdge[] = [
  { id: 'e-t1-a1', source: 't1', target: 'a1', animated: true },
  { id: 'e-a1-d1', source: 'a1', target: 'd1' },
  { id: 'e-d1-eR', source: 'd1', sourceHandle: 'yes', target: 'eR', label: 'Yes' },
  { id: 'e-d1-a2', source: 'd1', sourceHandle: 'no', target: 'a2', label: 'No' },
  { id: 'e-a2-d2', source: 'a2', target: 'd2' },
  { id: 'e-d2-eR', source: 'd2', sourceHandle: 'yes', target: 'eR', label: 'Yes — reconnected' },
  { id: 'e-d2-a3', source: 'd2', sourceHandle: 'no', target: 'a3', label: 'No' },
  { id: 'e-a3-e2', source: 'a3', target: 'e2' },
]

/* ---- Order confirmation — simple trigger → action → decision → end ---- */
const orderConfirmationNodes: FlowNode[] = [
  { id: 't1', type: 'trigger', position: { x: 300, y: 0 },
    data: { label: 'New order placed',
      config: { when: 'Record status is', status: 'placed', window: 'Any allowed hours', repeat: 'Once' } } },
  { id: 'a1', type: 'action', position: { x: 300, y: 170 },
    data: { label: 'Send order confirmation + tracking link',
      config: { skill: '02 · Order / record status update', channels: ['WhatsApp', 'Email'], model: 'Haiku 4.5', script: 'Hi {{name}}, thanks for your order! {{record_title}} is confirmed. Track it here: {{tracking_url}}', maxAttempts: 1 } } },
  { id: 'd1', type: 'decision', position: { x: 300, y: 380 },
    data: { label: 'Details correct?',
      config: { basedOn: "Contact's answer" } } },
  { id: 'e1', type: 'end', position: { x: 80, y: 570 },
    data: { label: 'Confirmed — no further action needed',
      config: { logCode: 'RESOLVED', updateMemory: 'Yes' } } },
  { id: 'h1', type: 'handover', position: { x: 520, y: 570 },
    data: { label: 'Needs a correction — human review',
      config: { team: 'Manager', priority: 'Normal', reason: 'Contact flagged an issue with order details.' } } },
]
const orderConfirmationEdges: FlowEdge[] = [
  { id: 'e-t1-a1', source: 't1', target: 'a1', animated: true },
  { id: 'e-a1-d1', source: 'a1', target: 'd1', label: 'On contact reply', data: { config: { timing: 'On contact reply' } } },
  { id: 'e-d1-e1', source: 'd1', sourceHandle: 'yes', target: 'e1', label: 'Yes' },
  { id: 'e-d1-h1', source: 'd1', sourceHandle: 'no', target: 'h1', label: 'No' },
]

/* ---- Delivery update — simple trigger → action → decision → action/end ---- */
const deliveryUpdateNodes: FlowNode[] = [
  { id: 't1', type: 'trigger', position: { x: 300, y: 0 },
    data: { label: 'Shipment marked out for delivery',
      config: { when: 'Record status is', status: 'out_for_delivery', window: 'Any allowed hours', repeat: 'Once' } } },
  { id: 'a1', type: 'action', position: { x: 300, y: 170 },
    data: { label: 'Send delivery-day update',
      config: { skill: '02 · Order / record status update', channels: ['WhatsApp', 'SMS'], model: 'Haiku 4.5', script: 'Hi {{name}}, {{record_title}} is out for delivery today. Track it here: {{tracking_url}}', maxAttempts: 1 } } },
  { id: 'd1', type: 'decision', position: { x: 300, y: 380 },
    data: { label: 'Delivered successfully?',
      config: { basedOn: 'Record status', status: 'delivered' } } },
  { id: 'e1', type: 'end', position: { x: 80, y: 570 },
    data: { label: 'Delivered — case closed',
      config: { logCode: 'RESOLVED', updateMemory: 'Yes' } } },
  { id: 'a2', type: 'action', position: { x: 520, y: 570 },
    data: { label: 'Delivery exception — offer to reschedule',
      config: { skill: '03 · Scheduling & rescheduling', channels: ['WhatsApp', 'AI voice call'], model: 'Haiku 4.5', script: "Sorry {{name}}, delivery didn't go through. Would tomorrow at the same time work?", maxAttempts: 1 } } },
  { id: 'e2', type: 'end', position: { x: 520, y: 760 },
    data: { label: 'Redelivery scheduled',
      config: { logCode: 'FOLLOW-UP-SET', updateMemory: 'Yes' } } },
]
const deliveryUpdateEdges: FlowEdge[] = [
  { id: 'e-t1-a1', source: 't1', target: 'a1', animated: true },
  { id: 'e-a1-d1', source: 'a1', target: 'd1', label: 'On record update', data: { config: { timing: 'On record update' } } },
  { id: 'e-d1-e1', source: 'd1', sourceHandle: 'yes', target: 'e1', label: 'Yes' },
  { id: 'e-d1-a2', source: 'd1', sourceHandle: 'no', target: 'a2', label: 'No' },
  { id: 'e-a2-e2', source: 'a2', target: 'e2' },
]

/* ---- Appointment reminder — simple trigger → action → decision → action/end ---- */
const appointmentReminderNodes: FlowNode[] = [
  { id: 't1', type: 'trigger', position: { x: 300, y: 0 },
    data: { label: 'Appointment is 1 day away',
      config: { when: 'Days before due date', days: 1, window: 'Any allowed hours', repeat: 'Once' } } },
  { id: 'a1', type: 'action', position: { x: 300, y: 170 },
    data: { label: 'Send appointment reminder',
      config: { skill: '03 · Scheduling & rescheduling', channels: ['WhatsApp', 'SMS'], model: 'Haiku 4.5', script: 'Hi {{name}}, a reminder about your {{record_title}} on {{due_date}}. See you then!', maxAttempts: 1 } } },
  { id: 'd1', type: 'decision', position: { x: 300, y: 380 },
    data: { label: 'Will they attend?',
      config: { basedOn: "Contact's answer" } } },
  { id: 'e1', type: 'end', position: { x: 80, y: 570 },
    data: { label: 'Confirmed — no further action needed',
      config: { logCode: 'RESOLVED', updateMemory: 'Yes' } } },
  { id: 'a2', type: 'action', position: { x: 520, y: 570 },
    data: { label: 'Offer to reschedule',
      config: { skill: '03 · Scheduling & rescheduling', channels: ['WhatsApp', 'AI voice call'], model: 'Haiku 4.5', script: 'No problem — what date and time would work better for you?', maxAttempts: 1 } } },
  { id: 'e2', type: 'end', position: { x: 520, y: 760 },
    data: { label: 'Rescheduled',
      config: { logCode: 'FOLLOW-UP-SET', updateMemory: 'Yes' } } },
]
const appointmentReminderEdges: FlowEdge[] = [
  { id: 'e-t1-a1', source: 't1', target: 'a1', animated: true },
  { id: 'e-a1-d1', source: 'a1', target: 'd1', label: 'On contact reply', data: { config: { timing: 'On contact reply' } } },
  { id: 'e-d1-e1', source: 'd1', sourceHandle: 'yes', target: 'e1', label: 'Yes' },
  { id: 'e-d1-a2', source: 'd1', sourceHandle: 'no', target: 'a2', label: 'No' },
  { id: 'e-a2-e2', source: 'a2', target: 'e2' },
]

export const DEFAULT_WORKFLOWS: WorkflowDef[] = [
  { id: 'master-journey', name: 'Order journey — master',
    hint: 'The whole playbook: Confirmation → In progress → Follow-up lanes, unresponsive branch, handovers, human exit',
    nodes: initialNodes, edges: initialEdges },
  { id: 'wf-predue', name: 'Pre-due reminders',
    hint: 'Prevent it going past due: reminders 7/3/1 days before due',
    active: false,
    nodes: predueNodes, edges: predueEdges },
  { id: 'wf-first-followup', name: 'First follow-up call',
    hint: 'Day 1–7 past due: verify, tailor tone, offer next step, follow up',
    active: false,
    nodes: earlyNodes, edges: earlyEdges },
  { id: 'wf-ghost', name: 'Unresponsive-contact protocol',
    hint: 'No answer / gone quiet: 3 steps, then escalate without them',
    active: false,
    nodes: ghostNodes, edges: ghostEdges },
  { id: 'wf-order-confirmation', name: 'Order confirmation',
    hint: 'New order placed → send confirmation → confirm details or escalate a correction',
    active: false,
    nodes: orderConfirmationNodes, edges: orderConfirmationEdges },
  { id: 'wf-delivery-update', name: 'Delivery update',
    hint: 'Out for delivery → send tracking update → confirm delivered or reschedule',
    active: false,
    nodes: deliveryUpdateNodes, edges: deliveryUpdateEdges },
  { id: 'wf-appointment-reminder', name: 'Appointment reminder',
    hint: '1 day before → send reminder → confirm attendance or reschedule',
    active: false,
    nodes: appointmentReminderNodes, edges: appointmentReminderEdges },
]
