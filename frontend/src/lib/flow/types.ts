import type { Edge, Node } from '@xyflow/react'

/**
 * The node KINDS a flow is built from — the same strings the backend engine
 * (FlowEngine.cs) switches on:
 *   trigger   WHEN — what starts a run
 *   action    Send message · AI message · Call · App action (by its channels)
 *   wait      a set time, or until the contact replies (with a timeout)
 *   followup  "Follow up if no reply" — N follow-ups, two outcomes
 *   decision  "Ask & branch" — YES / NO outcomes
 *   handover  hand the conversation to a team
 *   update    update the contact (tag / field)
 *   end       finish with an outcome code
 *   stage     LEGACY day-range section — still rendered and executed as a
 *             pass-through, no longer offered in the palette
 */
export type NodeKind =
  | 'trigger'
  | 'action'
  | 'wait'
  | 'followup'
  | 'decision'
  | 'handover'
  | 'update'
  | 'end'
  | 'stage'

/** Per-node settings — written by the step editor (components/flow/ConfigPanel)
 * and executed verbatim by the engine.
 *
 * Action / follow-up nodes that send on WhatsApp carry the template contract:
 *   waMode:            'text' | 'template' | 'auto' (text inside the 24h
 *                      window, the template once it has closed)
 *   templateName / templateLanguage
 *   templateVariables: {"1": "{{name}}"} — slot → value ({{placeholders}}
 *                      rendered from the CRM when the engine sends)
 *   allLinked:         send to every linked identity, not just the CRM primary
 *                      (waAllNumbers = legacy WhatsApp-only spelling)
 *   humanize:          "Send like a person" — the queue paces + splits it
 *   skill:             the AI skill id (legacy configs hold its NAME)
 * templateVariables is the reason for the object member in the union. */
export type FlowConfig = Record<
  string,
  string | number | boolean | string[] | Record<string, string> | undefined
>

/** Must stay a type alias (not interface) so it satisfies Node's data constraint. */
export type FlowNodeData = {
  label: string
  config?: FlowConfig
  [key: string]: unknown
}

export type FlowNode = Node<FlowNodeData>
export type FlowEdgeData = { config?: FlowConfig; [key: string]: unknown }
export type FlowEdge = Edge<FlowEdgeData>

/** An AI skill as GET /api/aicore/skills returns it. `id` is the skill's
 * string id ("as-…") — what an Action node's `skill` config stores. */
export interface AgentSkill {
  id: string
  name: string
  kind?: string
  enabled?: boolean
  useWhen?: string
  instructions?: string
}

/** A visual flow in the workflow library — same JSON shape as Export/Import.
 * `active` arms the backend flow engine: live workflows run automatically. */
export interface WorkflowDef {
  id: string
  name: string
  hint?: string
  active?: boolean
  nodes: FlowNode[]
  edges: FlowEdge[]
}

// ---- statistics (GET /api/flows/{flowId}/stats) ------------------------------

export interface NodeStats {
  entered: number
  completed: number
  waiting: number
  waitingNow: number
  failed: number
  skipped: number
  followups: number
}

export interface FlowStats {
  flowId: string
  name: string
  active: boolean
  days: number
  from: string
  to: string
  totals: {
    started: number
    completed: number
    failed: number
    waitingNow: number
    runningNow: number
    followupsSent: number
    repliedAfterFollowUp: number
  }
  nodes: Record<string, NodeStats>
  order: { nodeId: string; label: string; type: string; parent: string | null; entered: number; dropPct: number | null }[]
  dropoff: { from: string; to: string; branch: string; fromCount: number; toCount: number; dropPct: number | null }[]
  series: { day: string; started: number; completed: number; failed: number; nodes: Record<string, number> }[]
  failures: { nodeId: string; label: string; reason: string; count: number }[]
  skips: { nodeId: string; label: string; reason: string; count: number }[]
}

export interface FlowRunRow {
  id: number
  workflowId: number
  flowId: string
  flowName: string
  contactId: number | null
  contactName: string
  contactPhone: string
  status: 'running' | 'waiting' | 'done' | 'failed' | 'cancelled' | string
  currentNode: string
  currentLabel: string
  waitUntil: string | null
  waitFor: string
  startedAt: string | null
  updatedAt: string | null
  finishedAt: string | null
  detail: string
}

export interface FlowRunPathStep {
  at: string
  day: string
  nodeId: string
  label: string
  kind: 'entered' | 'completed' | 'waiting' | 'failed' | 'skipped' | 'followup' | string
  detail: string
}
