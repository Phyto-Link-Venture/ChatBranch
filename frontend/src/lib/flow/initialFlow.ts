import type { FlowEdge, FlowNode } from './types'

/**
 * The sample flow the builder loads on first open: a generic, industry-neutral
 * order-lifecycle journey across THREE stage lanes —
 * Confirmation (order received) → Out for delivery → Delivered / follow-up —
 * with a decision on every lane and a human handover when a delivery problem
 * needs a person. Day ranges are client-tunable defaults.
 *
 * Sections are the day-range lanes. Steps inside a section carry `parentId`
 * (position relative to the section) and move with it. Decision edges cross
 * between lanes to show progression from one stage to the next.
 *
 * Every node carries a `config` object (the n8n-style settings you see
 * when clicking a node) — fields are defined in lib/flow/nodeConfig.ts.
 * This is exactly the JSON shape the Export button produces, so a
 * client-built flow can be saved and reloaded the same way. Nothing here is
 * tied to any one industry — swap the copy for orders, bookings, tickets,
 * invoices or renewals and the shape still fits.
 */
export const initialNodes: FlowNode[] = [
  /* ---- stage sections (parents come first) ---- */
  { id: 's0', type: 'stage', position: { x: 0, y: 60 }, width: 940, height: 560,
    data: { label: 'Confirmation — order received',
      config: { stage: 'Pre-due', rangeType: 'Days before due date', fromDay: 3, toDay: 0 } } },
  { id: 's1', type: 'stage', position: { x: 0, y: 680 }, width: 940, height: 560,
    data: { label: 'Out for delivery',
      config: { stage: 'Normal', rangeType: 'Days after due date', fromDay: 0, toDay: 1 } } },
  { id: 's2', type: 'stage', position: { x: 0, y: 1300 }, width: 940, height: 720,
    data: { label: 'Delivered — follow-up',
      config: { stage: 'Normal', rangeType: 'Days after due date', fromDay: 1, toDay: 7 } } },

  /* ---- trigger (above the lanes) ---- */
  { id: 't1', type: 'trigger', position: { x: 440, y: -90 },
    data: { label: 'New order placed',
      config: { when: 'Record status is', status: 'placed', window: 'Any allowed hours', repeat: 'Once' } } },

  /* ---- Confirmation lane ---- */
  { id: 'a0', type: 'action', position: { x: 355, y: 80 }, parentId: 's0',
    data: { label: 'Send order confirmation + tracking link',
      config: { skill: '02 · Order / record status update', channels: ['WhatsApp', 'Email'], model: 'Haiku 4.5', script: 'Hi {{first_name}}, thanks for your order! {{record_title}} is confirmed and being prepared. Track it anytime here: {{tracking_url}}', maxAttempts: 1 } } },
  { id: 'd0', type: 'decision', position: { x: 355, y: 290 }, parentId: 's0',
    data: { label: 'Order details correct?',
      config: { basedOn: "Contact's answer" } } },
  { id: 'e0', type: 'end', position: { x: 60, y: 450 }, parentId: 's0',
    data: { label: 'Confirmed — nothing more needed',
      config: { logCode: 'RESOLVED', updateMemory: 'Yes' } } },

  /* ---- Out for delivery lane ---- */
  { id: 'a1', type: 'action', position: { x: 355, y: 80 }, parentId: 's1',
    data: { label: 'Send out-for-delivery update',
      config: { skill: '02 · Order / record status update', channels: ['WhatsApp', 'SMS'], model: 'Haiku 4.5', script: 'Good news {{first_name}} — {{record_title}} is out for delivery today. Follow it live here: {{tracking_url}}', maxAttempts: 1 } } },
  { id: 'd1', type: 'decision', position: { x: 355, y: 290 }, parentId: 's1',
    data: { label: 'Delivered successfully?',
      config: { basedOn: 'Record status', status: 'delivered' } } },
  { id: 'e1', type: 'end', position: { x: 60, y: 450 }, parentId: 's1',
    data: { label: 'Delivered — thank the customer',
      config: { logCode: 'RESOLVED', updateMemory: 'Yes' } } },

  /* ---- Delivered / follow-up lane ---- */
  { id: 'a2', type: 'action', position: { x: 355, y: 80 }, parentId: 's2',
    data: { label: 'Delivery issue — offer to reschedule',
      config: { skill: '03 · Scheduling & rescheduling', channels: ['WhatsApp', 'AI voice call'], model: 'Sonnet 5', script: "Hi {{first_name}}, it looks like {{record_title}} couldn't be delivered. What day works best to try again?", maxAttempts: 1 } } },
  { id: 'd2', type: 'decision', position: { x: 355, y: 290 }, parentId: 's2',
    data: { label: 'Redelivery arranged?',
      config: { basedOn: "Contact's answer" } } },
  { id: 'a3', type: 'action', position: { x: 60, y: 460 }, parentId: 's2',
    data: { label: 'Confirm the new slot in writing',
      config: { skill: '06 · Closing & confirmation', channels: ['WhatsApp', 'SMS'], model: 'Haiku 4.5', script: 'All set, {{first_name}}! {{record_title}} will be redelivered on {{due_date}}. Track it here: {{tracking_url}}', maxAttempts: 1 } } },
  { id: 'e2', type: 'end', position: { x: 60, y: 620 }, parentId: 's2',
    data: { label: 'Redelivery booked',
      config: { logCode: 'FOLLOW-UP-SET', updateMemory: 'Yes' } } },

  /* ---- handover (outside the lanes — leaving a lane = leaving the AI) ---- */
  { id: 'h1', type: 'handover', position: { x: 1040, y: 1400 },
    data: { label: 'Delivery problem — human takes over',
      config: { team: 'Manager', priority: 'Normal', reason: 'Customer reported a delivery problem that needs manual handling.' } } },
]

export const initialEdges: FlowEdge[] = [
  /* confirmation lane */
  { id: 'edge-t1-a0', source: 't1', target: 'a0', animated: true },
  { id: 'edge-a0-d0', source: 'a0', target: 'd0', label: 'On order reply', data: { config: { timing: 'On contact reply' } } },
  { id: 'edge-d0-e0', source: 'd0', sourceHandle: 'yes', target: 'e0', label: 'Yes' },
  { id: 'edge-d0-a1', source: 'd0', sourceHandle: 'no', target: 'a1', label: 'Send delivery updates' },
  /* out for delivery lane */
  { id: 'edge-a1-d1', source: 'a1', target: 'd1' },
  { id: 'edge-d1-e1', source: 'd1', sourceHandle: 'yes', target: 'e1', label: 'Yes' },
  { id: 'edge-d1-a2', source: 'd1', sourceHandle: 'no', target: 'a2', label: 'Not yet delivered' },
  /* delivered / follow-up lane */
  { id: 'edge-a2-d2', source: 'a2', target: 'd2', label: 'On contact reply', data: { config: { timing: 'On contact reply' } } },
  { id: 'edge-d2-a3', source: 'd2', sourceHandle: 'yes', target: 'a3', label: 'Yes' },
  { id: 'edge-a3-e2', source: 'a3', target: 'e2' },
  { id: 'edge-d2-h1', source: 'd2', sourceHandle: 'no', target: 'h1', label: 'Needs a human' },
]
