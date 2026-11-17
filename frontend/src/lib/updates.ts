'use client'

/**
 * Versions and each company's update choice (GET/PUT /api/updates,
 * UpdatesEndpoints.cs). `mine` is the version the signed-in person's company
 * runs; while its admin holds new features, a release's gated features
 * (`feature` in content/releases.ts) stay hidden — wrap such UI in useFeature.
 */

import { useCallback, useEffect, useState } from 'react'
import { API } from '@/lib/apiBase'
import { authHeaders } from '@/lib/auth'
import { featureVersion } from '@/content/releases'

export interface CompanyUpdate {
  id: number
  name: string
  version: string
  latest_in_major: string
  pinned_major: number
  minor_mode: 'auto' | 'hold'
  held_version: string
  updated_by: string
  updated_at: string | null
}

export interface UpdatesStatus {
  current: string
  mine: string
  mine_company: string | null
  releases: { version: string; date: string }[]
  can_manage: boolean
  can_overview: boolean
  companies: CompanyUpdate[]
}

export interface UpdatesOverview {
  current: string
  releases: { version: string; date: string }[]
  companies: CompanyUpdate[]
  changes: { at: string; by: string; company: string; change: string }[]
}

/** -1, 0 or 1, comparing MAJOR.MINOR.PATCH numerically. */
export function compareVersions(a: string, b: string): number {
  const pa = a.split('.').map((n) => parseInt(n, 10) || 0)
  const pb = b.split('.').map((n) => parseInt(n, 10) || 0)
  for (let i = 0; i < 3; i++) if ((pa[i] ?? 0) !== (pb[i] ?? 0)) return (pa[i] ?? 0) < (pb[i] ?? 0) ? -1 : 1
  return 0
}

export const majorOf = (v: string) => parseInt(v.split('.')[0] ?? '0', 10) || 0

let cached: Promise<UpdatesStatus | null> | null = null

export function fetchUpdates(force = false): Promise<UpdatesStatus | null> {
  if (!cached || force) {
    cached = fetch(`${API}/updates`, { headers: authHeaders() })
      .then((r) => (r.ok ? (r.json() as Promise<UpdatesStatus>) : null))
      .catch(() => null)
  }
  return cached
}

export function useUpdates(): { status: UpdatesStatus | null; loaded: boolean; reload: () => void } {
  const [status, setStatus] = useState<UpdatesStatus | null>(null)
  const [loaded, setLoaded] = useState(false)
  const load = useCallback((force: boolean) => {
    void fetchUpdates(force).then((s) => {
      setStatus(s)
      setLoaded(true)
    })
  }, [])
  useEffect(() => load(false), [load])
  return { status, loaded, reload: () => load(true) }
}

/** Change one company's choice. Resolves to the saved company, or throws with the server's message. */
export async function saveUpdates(body: {
  company: number
  minor_mode?: 'auto' | 'hold'
  approve?: string
  pinned_major?: number
}): Promise<CompanyUpdate> {
  const r = await fetch(`${API}/updates`, {
    method: 'PUT',
    headers: { ...authHeaders(), 'Content-Type': 'application/json' },
    body: JSON.stringify({ ...body, company: String(body.company) }),
  })
  const data = await r.json().catch(() => ({}))
  if (!r.ok) throw new Error((data as { detail?: string }).detail || `HTTP ${r.status}`)
  cached = null
  return data as CompanyUpdate
}

export async function fetchOverview(): Promise<UpdatesOverview | null> {
  try {
    const r = await fetch(`${API}/updates/overview`, { headers: authHeaders() })
    return r.ok ? ((await r.json()) as UpdatesOverview) : null
  } catch {
    return null
  }
}

/** True when the caller's company has the release that introduced `key`
 *  (always true for ungated keys, and while the version is still loading). */
export function useFeature(key: string): boolean {
  const { status } = useUpdates()
  const since = featureVersion(key)
  if (!since || !status) return true
  return compareVersions(status.mine, since) >= 0
}
