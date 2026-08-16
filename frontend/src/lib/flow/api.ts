import { authHeaders } from '@/lib/auth'
import type { AgentSkill, WorkflowDef } from './types'

/**
 * Server persistence for the flow builder (backend/flows).
 *
 * The builder syncs its WHOLE library (workflows + agent skills) to
 * /api/flows/library — the backend stores each item as a row and the flow
 * engine executes the workflows marked `active` automatically (time triggers
 * + inbound-message events). localStorage stays as the offline fallback, so
 * the builder still works with the API down; edits push up on reconnect.
 */
const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

export interface FlowLibrary {
  workflows: WorkflowDef[]
  agentSkills: AgentSkill[]
}

/** null = API unreachable or not authenticated (caller falls back to local). */
export async function fetchFlowLibrary(): Promise<FlowLibrary | null> {
  try {
    const res = await fetch(`${API}/flows/library`, {
      headers: authHeaders(),
      cache: 'no-store',
    })
    if (!res.ok) return null
    const data = await res.json()
    return {
      workflows: Array.isArray(data.workflows) ? data.workflows : [],
      agentSkills: Array.isArray(data.agentSkills) ? data.agentSkills : [],
    }
  } catch {
    return null
  }
}

export interface AgentVoice {
  id: string
  name: string
  provider: string
}

/** ElevenLabs agent voices for the Action node's voice picker (same source as
 * the /chats manual call picker). [] on any failure — the picker still offers
 * the native default. */
export async function fetchAgentVoices(): Promise<AgentVoice[]> {
  try {
    const res = await fetch(`${API}/voices`, { headers: authHeaders(), cache: 'no-store' })
    if (!res.ok) return []
    const data = await res.json()
    const voices: AgentVoice[] = Array.isArray(data.voices) ? data.voices : []
    return voices.filter((v) => v.provider === 'elevenlabs')
  } catch {
    return []
  }
}

export interface ScriptTemplateOpt {
  id: number
  name: string
}

/** crm.ScriptTemplate list for the Action node's "Script call" template
 * picker (Settings → Script Templates manages the actual content). []
 * on any failure — the picker just shows "(missing)" for a stale id. */
export async function fetchScriptTemplates(): Promise<ScriptTemplateOpt[]> {
  try {
    const res = await fetch(`${API}/calls/script-templates`, { headers: authHeaders(), cache: 'no-store' })
    if (!res.ok) return []
    const data = await res.json()
    const templates: ScriptTemplateOpt[] = Array.isArray(data.templates) ? data.templates : []
    return templates.map((t) => ({ id: t.id, name: t.name }))
  } catch {
    return []
  }
}

export type PushResult = 'saved' | 'forbidden' | 'error'

/** Full-library sync (admin+ on the server). 'forbidden' = agent role/expired
 * token — the builder keeps working locally and shows why. */
export async function pushFlowLibrary(library: FlowLibrary): Promise<PushResult> {
  try {
    const res = await fetch(`${API}/flows/library`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json', ...authHeaders() },
      body: JSON.stringify(library),
    })
    if (res.ok) return 'saved'
    return res.status === 401 || res.status === 403 ? 'forbidden' : 'error'
  } catch {
    return 'error'
  }
}
