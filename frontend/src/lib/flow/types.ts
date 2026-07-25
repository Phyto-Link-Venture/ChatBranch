import type { Edge, Node } from '@xyflow/react'

/** The node KINDS a flow can be built from (see components/flow/nodes.tsx). */
export type NodeKind = 'trigger' | 'stage' | 'action' | 'decision' | 'handover' | 'end'

/** Per-node settings — keys come from CONFIG_SCHEMAS (lib/flow/nodeConfig.ts).
 *
 * Action nodes sending on the WhatsApp channel additionally carry the
 * template-mode contract the backend flow engine executes verbatim:
 *   waMode:            'text' | 'template'  (absent = 'text' — legacy flows)
 *   templateName:      approved template's name
 *   templateLanguage:  its language code, e.g. 'en' / 'ms'
 *   templateVariables: {"1": "{{name}}", "2": "{{amount}}"} — position →
 *                      value; values may hold {{placeholders}} the engine
 *                      renders from the CRM (flows/engine.variables_for).
 *   allLinked:         true = send to every identity linked to the contact on
 *                      the step's channels (all WhatsApp numbers, all email
 *                      addresses — engine dedupes); absent = primary CRM
 *                      contact only. waAllNumbers is the legacy WhatsApp-only
 *                      spelling of the same switch — still honoured.
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

/** An instruction module the AI caller loads. */
export interface AgentSkill {
  id: string
  name: string
  useWhen?: string
  instructions?: string
}

/** A visual flow in the workflow library — same JSON shape as Export/Import.
 * `active` arms the backend flow engine: live workflows run automatically
 * (time triggers + inbound-message events). */
export interface WorkflowDef {
  id: string
  name: string
  hint?: string
  active?: boolean
  nodes: FlowNode[]
  edges: FlowEdge[]
}

export type ConfigFieldType =
  | 'text'
  | 'number'
  | 'textarea'
  | 'select'
  | 'multiselect'
  | 'skillselect'
  | 'voiceselect'
  /** Multi-pick from the live contact book. Empty = every contact (the default);
   *  a non-empty list scopes the trigger to just those contact ids. */
  | 'contactselect'
  | 'scripttemplateselect'

export interface ConfigField {
  key: string
  label: string
  type: ConfigFieldType
  options?: string[]
  help?: string
  /** textarea only: show the {{variable}} picker underneath. */
  variables?: boolean
}
