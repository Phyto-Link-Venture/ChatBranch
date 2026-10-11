'use client'

/**
 * "What is happening right now" — GET /api/live, shared by every consumer in
 * the tab (the top-bar Live chip, the sidebar's Chats counter, the Dashboard's
 * "Waiting for a reply" card).
 *
 * Refreshes every 15 s AND immediately on every inbox `change` event from the
 * SSE stream (lib/chatStream — one connection per tab, shared with /chats).
 * Keyed by the entity scope so switching outlets never shows the previous
 * outlet's queue; each key polls only while something is subscribed to it.
 */

import { API } from '@/lib/apiBase'
import { useEffect, useState } from 'react'
import { authHeaders, hasAppSession } from '@/lib/auth'
import { subscribeChatStream, subscribeStreamStatus, type StreamStatus } from '@/lib/chatStream'
import { useT, type TFunction } from '@/i18n'

const POLL_MS = 15_000

export interface LiveCall {
  channel: string
  key: string
  /** which company's conversation (a customer has one per company) */
  thread_id?: number | null
  name: string
  status: string
  room: string | null
  since: string
  url: string
}

export interface LiveWaiting {
  channel: string
  key: string
  thread_id?: number | null
  name: string
  preview: string
  assignee: string
  waiting_since: string
  waiting_seconds: number
  url: string
}

export interface LiveEvent {
  kind: 'message' | 'audit'
  id: string
  title: string
  text: string
  direction?: 'in' | 'out'
  channel?: string
  actor?: string
  action?: string
  at: string
  url: string
}

export interface LiveData {
  generated_at: string
  active_call_count: number
  active_calls: LiveCall[]
  waiting_count: number
  waiting: LiveWaiting[]
  /** conversations handed to a person and not yet answered (Ai/Handover) */
  needs_human_count?: number
  recent: LiveEvent[]
}

interface Entry {
  data: LiveData | null
  failed: boolean
  listeners: Set<() => void>
  timer: ReturnType<typeof setInterval> | null
  unsubStream: (() => void) | null
  inflight: boolean
}

const entries = new Map<string, Entry>()

async function load(q: string, e: Entry) {
  if (e.inflight || !hasAppSession()) return
  e.inflight = true
  try {
    const res = await fetch(`${API}/live${q ? `?${q}` : ''}`, { headers: authHeaders(), cache: 'no-store' })
    if (!res.ok) throw new Error(`HTTP ${res.status}`)
    e.data = (await res.json()) as LiveData
    e.failed = false
  } catch {
    e.failed = true
  } finally {
    e.inflight = false
    e.listeners.forEach((fn) => fn())
  }
}

function subscribe(q: string, fn: () => void): () => void {
  let e = entries.get(q)
  if (!e) {
    e = { data: null, failed: false, listeners: new Set(), timer: null, unsubStream: null, inflight: false }
    entries.set(q, e)
  }
  const entry = e
  entry.listeners.add(fn)
  if (!entry.timer) {
    void load(q, entry)
    entry.timer = setInterval(() => void load(q, entry), POLL_MS)
    entry.unsubStream = subscribeChatStream(() => void load(q, entry))
  }
  return () => {
    entry.listeners.delete(fn)
    if (!entry.listeners.size) {
      if (entry.timer) clearInterval(entry.timer)
      entry.timer = null
      entry.unsubStream?.()
      entry.unsubStream = null
    }
  }
}

/** Live counts + lists for the given `entity=<slug>` query ("" = all). */
export function useLive(entityQ: string): { data: LiveData | null; failed: boolean } {
  const [, force] = useState(0)
  useEffect(() => subscribe(entityQ, () => force((n) => n + 1)), [entityQ])
  const e = entries.get(entityQ)
  return { data: e?.data ?? null, failed: e?.failed ?? false }
}

/** The realtime connection's state (green / amber / grey dot). */
export function useStreamStatus(): StreamStatus {
  const [s, setS] = useState<StreamStatus>('offline')
  useEffect(() => subscribeStreamStatus(setS), [])
  return s
}

/**
 * "just now" · "4 min" · "2 h" · "3 d" — compact waits and ages, in the
 * dashboard language when given the translate function (`useShortAge()` binds
 * it); without one it stays English for callers not yet localised.
 */
export function shortAge(seconds: number, t?: TFunction): string {
  const m = Math.floor(seconds / 60)
  const h = Math.floor(m / 60)
  if (typeof t === 'function') {
    if (!Number.isFinite(seconds) || seconds < 60) return t('shell.live.age.justNow')
    if (m < 60) return t('shell.live.age.minutes', { count: m })
    if (h < 24) return t('shell.live.age.hours', { count: h })
    return t('shell.live.age.days', { count: Math.floor(h / 24) })
  }
  if (!Number.isFinite(seconds) || seconds < 60) return 'just now'
  if (m < 60) return `${m} min`
  if (h < 24) return `${h} h`
  return `${Math.floor(h / 24)} d`
}

/** `shortAge` bound to the current language: `const age = useShortAge(); age(secs)`. */
export function useShortAge(): (seconds: number) => string {
  const t = useT()
  return (seconds: number) => shortAge(seconds, t)
}

export function secondsSince(iso: string): number {
  const t = new Date(iso).getTime()
  return Number.isFinite(t) ? Math.max(0, (Date.now() - t) / 1000) : 0
}
