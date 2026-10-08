/**
 * Client for the per-user saved list views (`/api/views`). Server-stored so a
 * view follows the operator across browsers; every call only ever sees the
 * signed-in user's own views.
 */
import { authHeaders, expireSessionOn401 } from './auth'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

export type ViewScope = 'chats' | 'contacts'

export interface SavedView<F = Record<string, unknown>> {
  id: number
  scope: ViewScope
  name: string
  filters: F
  position: number
  created_at: string
}

async function call<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}/${path}`, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...authHeaders(), ...(init?.headers ?? {}) },
  })
  const data = await res.json().catch(() => ({}))
  if (expireSessionOn401(res)) throw new Error('Session expired — redirecting to login…')
  if (!res.ok) throw new Error((data as { detail?: string }).detail || `HTTP ${res.status}`)
  return data as T
}

export const listViews = <F,>(scope: ViewScope) =>
  call<{ views: SavedView<F>[] }>(`views?scope=${scope}`).then((d) => d.views ?? [])

export const createView = <F,>(scope: ViewScope, name: string, filters: F) =>
  call<SavedView<F>>('views', { method: 'POST', body: JSON.stringify({ scope, name, filters }) })

export const updateView = <F,>(id: number, patch: { name?: string; filters?: F; position?: number }) =>
  call<SavedView<F>>(`views/${id}`, { method: 'PATCH', body: JSON.stringify(patch) })

export const deleteView = (id: number) => call<{ ok: boolean }>(`views/${id}`, { method: 'DELETE' })

export const reorderViews = <F,>(scope: ViewScope, ids: number[]) =>
  call<{ views: SavedView<F>[] }>('views/reorder', { method: 'POST', body: JSON.stringify({ scope, ids }) }).then(
    (d) => d.views ?? [],
  )
