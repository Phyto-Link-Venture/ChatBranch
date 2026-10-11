/**
 * Client + types for the Test lab (superadmin only; the whole API is 404 when
 * the server runs with TEST_LAB=0):
 *   GET  /api/testlab/scenarios          the catalog, grouped by category
 *   GET  /api/testlab/status             is there fenced test data, how much
 *   POST /api/testlab/run {ids, run_id}  run scenarios; results append to run_id
 *   GET  /api/testlab/runs[/{id}]        the last 50 runs
 *   POST /api/testlab/reset              delete the fenced data (nothing else)
 *   POST /api/testlab/demo               load the demo dataset inside the fence
 */
import { API } from '@/lib/apiBase'
import { authHeaders, expireSessionOn401 } from './auth'

export interface LabScenario {
  id: string
  title: string
  proves: string
  steps: string[]
  expect: string[]
  needs_ai_key: boolean
}

export interface LabCategory {
  id: string
  label: string
  covers: string
  scenarios: LabScenario[]
}

export interface LabCatalog {
  total: number
  has_ai_key: boolean
  categories: LabCategory[]
}

export interface LabCheck {
  name: string
  ok: boolean
  detail: string
}

export interface LabLink {
  label: string
  url: string
}

export interface LabResult {
  id: string
  title: string
  category: string
  passed: boolean
  skipped: boolean
  skip_reason: string
  error: string
  checks: LabCheck[]
  links: LabLink[]
  duration_ms: number
  /** "lab" = the deterministic test brain, "configured" = the real model, "" = no AI involved. */
  brain: string
  ran_at: string
}

export interface LabRun {
  id: number
  started_by: string
  source: 'page' | 'cli' | 'api' | 'test' | string
  total: number
  passed: number
  failed: number
  skipped: number
  duration_ms: number
  created_at: string
  updated_at: string
}

export interface LabStatus {
  exists: boolean
  entity: string | null
  entity_name: string | null
  demo_loaded: boolean
  counts: { entities: number; accounts: number; users: number; contacts: number; conversations: number }
}

export class LabError extends Error {
  status: number
  constructor(message: string, status: number) {
    super(message)
    this.status = status
  }
}

async function call<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}/testlab${path}`, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...authHeaders(), ...(init?.headers ?? {}) },
  })
  if (expireSessionOn401(res)) throw new LabError('session expired', 401)
  const body = await res.json().catch(() => ({}))
  if (!res.ok) throw new LabError(String(body?.detail ?? `HTTP ${res.status}`), res.status)
  return body as T
}

export const getCatalog = () => call<LabCatalog>('/scenarios')
export const getStatus = () => call<LabStatus>('/status')
export const getRuns = () => call<{ runs: LabRun[] }>('/runs').then((r) => r.runs)
export const getRun = (id: number) => call<{ run: LabRun; results: LabResult[] }>(`/runs/${id}`)
export const runScenarios = (ids: string[], runId?: number) =>
  call<{ run: LabRun; results: LabResult[] }>('/run', {
    method: 'POST',
    body: JSON.stringify({ ids, run_id: runId, source: 'page' }),
  })
export const resetLab = () => call<{ ok: boolean; deleted: Record<string, number> }>('/reset', { method: 'POST', body: '{}' })
export const loadDemo = () =>
  call<{ ok: boolean; already_loaded: boolean; created: Record<string, number>; links: LabLink[] }>('/demo', {
    method: 'POST',
    body: '{}',
  })
