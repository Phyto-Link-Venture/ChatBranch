import type { FlowEdge, FlowNode } from './types'

/**
 * The sample flow the builder loads on first open: a generic order-lifecycle
 * journey across THREE stage sections — Confirmation (before the due/delivery
 * date) → In progress (active handling) → Follow-up (resolving open issues) —
 * with an unresponsive-contact branch, both handover types, and a human-owned
 * exit for long-running cases. Day ranges are client-tunable defaults.
 *
 * Sections are the day-range lanes. Steps inside a section carry `parentId`
 * (position relative to the section) and move with it. Stage→stage edges
 * connect the sections themselves: escalation is by days, never by feelings.
 *
 * Every node carries a `config` object (the n8n-style settings you see
 * when clicking a node) — fields are defined in lib/flow/nodeConfig.ts.
 * This is exactly the JSON shape the Export button produces,
 * so a client-built flow can be saved and reloaded the same way.
 */
export const initialNodes: FlowNode[] = [
  /* ---- stage sections (parents come first) ---- */
  { id: 's0', type: 'stage', position: { x: 0, y: 60 }, width: 940, height: 600,
    data: { label: 'Confirmation — before the due date',
      config: { stage: 'Pre-due', rangeType: 'Days before due date', fromDay: 7, toDay: 0 } } },
  { id: 's1', type: 'stage', position: { x: 0, y: 760 }, width: 940, height: 1520,
    data: { label: 'In progress — active handling',
      config: { stage: 'Normal', rangeType: 'Days after due date', fromDay: 1, toDay: 7 } } },
  { id: 's2', type: 'stage', position: { x: 0, y: 2380 }, width: 940, height: 840,
    data: { label: 'Follow-up — resolving open issues',
      config: { stage: 'Serious', rangeType: 'Days after due date', fromDay: 8, toDay: 30 } } },

  /* ---- trigger (above the lanes) ---- */
  { id: 't1', type: 'trigger', position: { x: 440, y: -90 },
    data: { label: 'Record due soon — 7 days before due (all day values are client-tunable)',
      config: { when: 'Days before due date', days: 7, window: 'Any allowed hours', repeat: 'Once' } } },

  /* ---- Confirmation lane ---- */
  { id: 'a0', type: 'action', position: { x: 355, y: 70 }, parentId: 's0',
    data: { label: 'Send a friendly reminder + tracking link (7 / 3 / 1 days before due)',
      config: { skill: '02 · Order / record status update', channels: ['WhatsApp', 'SMS', 'In-app push'], model: 'Haiku 4.5', script: 'Hi {{name}}! Your {{business_name}} {{record_title}} is due on {{due_date}}. Track it here: {{tracking_url}}', maxAttempts: 3 } } },
  { id: 'd0', type: 'decision', position: { x: 355, y: 280 }, parentId: 's0',
    data: { label: 'Confirmed before the due date?',
      config: { basedOn: 'Record status', status: 'confirmed' } } },
  { id: 'e0', type: 'end', position: { x: 60, y: 440 }, parentId: 's0',
    data: { label: 'Confirmed on time — no follow-up needed',
      config: { logCode: 'RESOLVED', updateMemory: 'Yes' } } },

  /* ---- In progress lane (first contact) ---- */
  { id: 'a1', type: 'action', position: { x: 355, y: 70 }, parentId: 's1',
    data: { label: 'Call the contact — verify identity first',
      config: { skill: '00 · Greeting & identity check', channels: ['AI voice call'], model: 'Sonnet 5', script: 'Hello, may I speak to {{name}}? Just confirming a few details before we continue.', maxAttempts: 3 } } },
  { id: 'd1', type: 'decision', position: { x: 355, y: 260 }, parentId: 's1',
    data: { label: 'Right person on the line?',
      config: { basedOn: "Contact's answer" } } },
  { id: 'a2', type: 'action', position: { x: 60, y: 410 }, parentId: 's1',
    data: { label: 'Load contact memory → tailor tone',
      config: { skill: '01 · Qualification', channels: ['Internal log'], model: 'Haiku 4.5', script: 'Pull contact memory; confirm what they need before proceeding.' } } },
  { id: 'e1', type: 'end', position: { x: 655, y: 410 }, parentId: 's1',
    data: { label: 'Wrong person — end call, try again later',
      config: { logCode: 'WRONG-PERSON', updateMemory: 'Yes' } } },
  { id: 'd2', type: 'decision', position: { x: 60, y: 600 }, parentId: 's1',
    data: { label: 'Resolved on this call?',
      config: { basedOn: "Contact's answer" } } },
  { id: 'e2', type: 'end', position: { x: 60, y: 790 }, parentId: 's1',
    data: { label: 'Resolved → thank & close',
      config: { logCode: 'RESOLVED', updateMemory: 'Yes' } } },
  { id: 'a3', type: 'action', position: { x: 355, y: 790 }, parentId: 's1',
    data: { label: 'Offer the next step — reschedule, more info, or escalate',
      config: { skill: '03 · Scheduling & rescheduling', channels: ['AI voice call'], model: 'Sonnet 5', script: 'Offer to reschedule, share more information, or escalate — whichever the contact needs.', maxAttempts: 1 } } },
  { id: 'd3', type: 'decision', position: { x: 355, y: 980 }, parentId: 's1',
    data: { label: 'Needs a human?',
      config: { basedOn: "Contact's answer" } } },
  { id: 'a4', type: 'action', position: { x: 100, y: 1150 }, parentId: 's1',
    data: { label: 'Confirm follow-up: read-back + written confirmation',
      config: { skill: '03 · Scheduling & rescheduling', channels: ['WhatsApp', 'SMS'], model: 'Haiku 4.5', script: 'Confirming: {{record_title}} follow-up set for {{due_date}}.', maxAttempts: 1 } } },
  { id: 'e3', type: 'end', position: { x: 100, y: 1340 }, parentId: 's1',
    data: { label: 'Log outcome + update memory',
      config: { logCode: 'FOLLOW-UP-SET', updateMemory: 'Yes' } } },

  /* ---- Follow-up lane (extended handling) ---- */
  { id: 'a5', type: 'action', position: { x: 355, y: 70 }, parentId: 's2',
    data: { label: 'Follow-up call — calm and factual',
      config: { skill: '04 · Objection & complaint handling', channels: ['AI voice call'], model: 'Sonnet 5', script: "Following up on {{record_title}}, currently {{record_status}}. Let's find the best next step.", maxAttempts: 2 } } },
  { id: 'd4', type: 'decision', position: { x: 355, y: 280 }, parentId: 's2',
    data: { label: 'No answer ×3 across 2+ channels?',
      config: { basedOn: 'Contact tag', tag: 'unresponsive' } } },
  { id: 'a6', type: 'action', position: { x: 60, y: 450 }, parentId: 's2',
    data: { label: 'Try another channel and time before escalating',
      config: { skill: '03 · Scheduling & rescheduling', channels: ['WhatsApp', 'Email', 'In-app push'], model: 'Haiku 4.5', script: 'Try {{best_contact_time}} on {{preferred_channel}}. One more attempt before escalating.', maxAttempts: 1 } } },
  { id: 'e4', type: 'end', position: { x: 60, y: 640 }, parentId: 's2',
    data: { label: 'Logged unreachable — escalate without them',
      config: { logCode: 'UNREACHABLE', updateMemory: 'Yes' } } },
  { id: 'd5', type: 'decision', position: { x: 655, y: 450 }, parentId: 's2',
    data: { label: 'Contact frustrated after repeated attempts?',
      config: { basedOn: "Contact's answer" } } },
  { id: 'e5', type: 'end', position: { x: 655, y: 640 }, parentId: 's2',
    data: { label: 'On a plan — monitor, tone stays calm',
      config: { logCode: 'RESOLVED', updateMemory: 'Yes' } } },

  /* ---- handovers (outside the lanes — leaving a lane = leaving the AI) ---- */
  { id: 'h1', type: 'handover', position: { x: 1040, y: 1880 },
    data: { label: 'Escalate — needs a human decision',
      config: { team: 'Senior agent', priority: 'High', reason: "Contact needs a decision outside the agent's scope." } } },
  { id: 'h2', type: 'handover', position: { x: 1040, y: 2900 },
    data: { label: 'Frustrated after repeated attempts — human takes over',
      config: { team: 'Senior agent', priority: 'Urgent', reason: 'Contact frustrated after repeated attempts — human judgment call.' } } },
  { id: 'h3', type: 'handover', position: { x: 355, y: 3320 },
    data: { label: 'Long-running open case — humans own it',
      config: { team: 'Manager', priority: 'Urgent', reason: 'Long-running open case — every next step human-approved.' } } },
]

export const initialEdges: FlowEdge[] = [
  /* confirmation */
  { id: 'edge-t1-a0', source: 't1', target: 'a0', animated: true },
  { id: 'edge-a0-d0', source: 'a0', target: 'd0' },
  { id: 'edge-d0-e0', source: 'd0', sourceHandle: 'yes', target: 'e0', label: 'Yes' },
  /* stage escalation — by days, never by feelings */
  { id: 'edge-d0-s1', source: 'd0', sourceHandle: 'no', target: 's1', label: 'No — still open on day 1' },
  { id: 'edge-s1-s2', source: 's1', target: 's2', label: 'Still open on day 8' },
  { id: 'edge-s2-h3', source: 's2', target: 'h3', label: 'Still open on day 31' },
  /* in-progress lane */
  { id: 'edge-a1-d1', source: 'a1', target: 'd1' },
  { id: 'edge-d1-e1', source: 'd1', sourceHandle: 'no', target: 'e1', label: 'No' },
  { id: 'edge-d1-a2', source: 'd1', sourceHandle: 'yes', target: 'a2', label: 'Yes' },
  { id: 'edge-a2-d2', source: 'a2', target: 'd2' },
  { id: 'edge-d2-e2', source: 'd2', sourceHandle: 'yes', target: 'e2', label: 'Yes' },
  { id: 'edge-d2-a3', source: 'd2', sourceHandle: 'no', target: 'a3', label: 'No' },
  { id: 'edge-a3-d3', source: 'a3', target: 'd3' },
  { id: 'edge-d3-h1', source: 'd3', sourceHandle: 'yes', target: 'h1', label: 'Yes' },
  { id: 'edge-d3-a4', source: 'd3', sourceHandle: 'no', target: 'a4', label: 'No' },
  { id: 'edge-a4-e3', source: 'a4', target: 'e3' },
  /* follow-up lane */
  { id: 'edge-a5-d4', source: 'a5', target: 'd4' },
  { id: 'edge-d4-a6', source: 'd4', sourceHandle: 'yes', target: 'a6', label: 'Yes' },
  { id: 'edge-a6-e4', source: 'a6', target: 'e4' },
  { id: 'edge-d4-d5', source: 'd4', sourceHandle: 'no', target: 'd5', label: 'No' },
  { id: 'edge-d5-h2', source: 'd5', sourceHandle: 'yes', target: 'h2', label: 'Yes' },
  { id: 'edge-d5-e5', source: 'd5', sourceHandle: 'no', target: 'e5', label: 'No' },
]
