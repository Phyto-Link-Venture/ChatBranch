import { API } from '@/lib/apiBase'
import { authHeaders } from '@/lib/auth'
import type { AgentSkill, FlowEdge, FlowNode, FlowRunPathStep, FlowRunRow, FlowStats, WorkflowDef } from './types'

/**
 * Server persistence + read models for the flow builder.
 *
 * The builder syncs its WHOLE workflow library to /api/flows/library (PUT
 * prunes flows missing from the payload); the engine executes the ones marked
 * `active`. On the wire a workflow's string id is `flowId` (the numeric `id`
 * is the row pk). localStorage stays the offline fallback.
 */

async function getJson<T>(path: string): Promise<T | null> {
  try {
    const res = await fetch(`${API}${path}`, { headers: authHeaders(), cache: 'no-store' })
    if (!res.ok) return null
    return (await res.json()) as T
  } catch {
    return null
  }
}

interface WireWorkflow {
  id?: number | string
  flowId?: string
  name?: string
  hint?: string
  active?: boolean
  nodes?: FlowNode[]
  edges?: FlowEdge[]
}

/** null = API unreachable or not allowed (caller falls back to local). */
export async function fetchFlowLibrary(): Promise<{ workflows: WorkflowDef[] } | null> {
  const data = await getJson<{ workflows?: WireWorkflow[] }>('/flows/library')
  if (!data) return null
  const workflows = (Array.isArray(data.workflows) ? data.workflows : []).map((w) => ({
    id: String(w.flowId || w.id),
    name: w.name || 'Untitled flow',
    hint: w.hint ?? '',
    active: !!w.active,
    // a flow written outside the editor (Test lab, API, MCP, an import) may carry
    // nodes with no position — give them one so the canvas and the next save work
    nodes: Array.isArray(w.nodes) ? w.nodes.map((n, i) => (n.position ? n : { ...n, position: { x: 0, y: i * 120 } })) : [],
    edges: Array.isArray(w.edges) ? w.edges : [],
  }))
  return { workflows }
}

/** Only the keys the graph needs — React Flow's runtime bits (selected,
 * measured, dragging…) never reach the server. */
export function cleanNodes(nodes: FlowNode[]): FlowNode[] {
  return nodes.map(({ id, type, position, data, width, height, parentId }) => ({
    id,
    type,
    position: { x: Math.round(position?.x ?? 0), y: Math.round(position?.y ?? 0) },
    data,
    ...(width ? { width } : {}),
    ...(height ? { height } : {}),
    ...(parentId ? { parentId } : {}),
  }))
}

export function cleanEdges(edges: FlowEdge[]): FlowEdge[] {
  return edges.map(({ id, source, sourceHandle, target, label, animated, style, data }) => ({
    id,
    source,
    ...(sourceHandle ? { sourceHandle } : {}),
    target,
    ...(label ? { label } : {}),
    ...(animated ? { animated: true } : {}),
    ...(style?.strokeDasharray ? { style } : {}),
    ...(data?.config ? { data } : {}),
  }))
}

export type PushResult = 'saved' | 'forbidden' | 'error'

/** Full-library sync (flows.manage on the server). */
export async function pushFlowLibrary(workflows: WorkflowDef[]): Promise<PushResult> {
  try {
    const res = await fetch(`${API}/flows/library`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json', ...authHeaders() },
      body: JSON.stringify({
        workflows: workflows.map((w, i) => ({
          flowId: w.id,
          name: w.name,
          hint: w.hint ?? '',
          active: !!w.active,
          position: i,
          nodes: cleanNodes(w.nodes),
          edges: cleanEdges(w.edges),
        })),
      }),
    })
    if (res.ok) return 'saved'
    return res.status === 401 || res.status === 403 ? 'forbidden' : 'error'
  } catch {
    return 'error'
  }
}

// ---- pickers --------------------------------------------------------------------

/** The AI skills (Skills page is their only editor). [] on failure. */
export async function fetchSkills(): Promise<AgentSkill[]> {
  const data = await getJson<{ skills?: (AgentSkill & { use_when?: string })[] }>('/aicore/skills')
  const rows = Array.isArray(data?.skills) ? data!.skills : []
  return rows.map((s) => ({
    id: String(s.id),
    name: s.name || String(s.id),
    kind: s.kind,
    enabled: s.enabled,
    useWhen: s.useWhen ?? s.use_when ?? '',
  }))
}

export interface TeamOpt {
  id: number
  name: string
}

export async function fetchTeams(): Promise<TeamOpt[]> {
  const data = await getJson<{ teams?: TeamOpt[] }>('/teams')
  return Array.isArray(data?.teams) ? data!.teams.map((t) => ({ id: t.id, name: t.name })) : []
}

export interface AgentVoice {
  id: string
  name: string
  provider: string
}

/** ElevenLabs voices for the call step's voice picker. [] on failure. */
export async function fetchAgentVoices(): Promise<AgentVoice[]> {
  const data = await getJson<{ voices?: AgentVoice[] }>('/voices')
  const voices = Array.isArray(data?.voices) ? data!.voices : []
  return voices.filter((v) => v.provider === 'elevenlabs')
}

export interface ScriptTemplateOpt {
  id: number
  name: string
}

export async function fetchScriptTemplates(): Promise<ScriptTemplateOpt[]> {
  const data = await getJson<{ templates?: ScriptTemplateOpt[] }>('/calls/script-templates')
  return Array.isArray(data?.templates) ? data!.templates.map((t) => ({ id: t.id, name: t.name })) : []
}

// ---- statistics + runs ------------------------------------------------------------

export const fetchFlowStats = (flowId: string, days: number) =>
  getJson<FlowStats>(`/flows/${encodeURIComponent(flowId)}/stats?days=${days}`)

export interface RunsPage {
  runs: FlowRunRow[]
  count: number
  page: number
  pageSize: number
  pages: number
}

export function fetchRuns(q: { flow: string; status?: string; contact?: string; node?: string; page?: number; days?: number }) {
  const p = new URLSearchParams({ flow: q.flow, page: String(q.page ?? 1), page_size: '25' })
  if (q.status) p.set('status', q.status)
  if (q.contact) p.set('contact', q.contact)
  if (q.node) p.set('node', q.node)
  if (q.days) p.set('days', String(q.days))
  return getJson<RunsPage>(`/flows/runs?${p.toString()}`)
}

export const fetchRun = (id: number) =>
  getJson<{ run: FlowRunRow & { lastReply: string; answered: boolean }; path: FlowRunPathStep[]; log: { t: string; node: string; evt: string }[] }>(
    `/flows/runs/${id}`,
  )

/** Start a "Manual / API" flow for some contacts. */
export async function startFlow(flowId: string, contactIds: number[]): Promise<{ started: number; skipped: number } | string> {
  try {
    const res = await fetch(`${API}/flows/${encodeURIComponent(flowId)}/start`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', ...authHeaders() },
      body: JSON.stringify({ contactIds }),
    })
    const body = await res.json().catch(() => ({}))
    if (!res.ok) return (body as { detail?: string }).detail || `HTTP ${res.status}`
    return body as { started: number; skipped: number }
  } catch (e) {
    return e instanceof Error ? e.message : String(e)
  }
}

export interface FlowsOverview {
  days: number
  from: string
  to: string
  flows: {
    flowId: string
    name: string
    active: boolean
    entered: number
    reachedEnd: number
    completed: number
    failed: number
    inProgress: number
    series: { day: string; entered: number; completed: number; failed: number }[]
  }[]
}

export const fetchFlowsOverview = (days: number, entity?: string) =>
  getJson<FlowsOverview>(`/analytics/flows?days=${days}${entity ? `&entity=${encodeURIComponent(entity)}` : ''}`)
