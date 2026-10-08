'use client'

/**
 * "What is happening right now" — GET /api/live, shared by every consumer in
 * the tab (the top-bar Live chip, the sidebar's Chats counter, Analytics'
 * "Waiting for a reply" card).
 *
 * Refreshes every 15 s AND immediately on every inbox `change` event from the
 * SSE stream (lib/chatStream — one connection per tab, shared with /chats).
 * Keyed by the entity scope so switching outlets never shows the previous
 * outlet's queue; each key polls only while something is subscribed to it.
 */

import { useEffect, useState } from 'react'
import { authHeaders, getAuth } from '@/lib/auth'
import { subscribeChatStream, subscribeStreamStatus, type StreamStatus } from '@/lib/chatStream'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'
const POLL_MS = 15_000

export interface LiveCall {
  channel: string
  key: string
  name: string
  status: string
  room: string | null
  since: string
  url: string
}

export interface LiveWaiting {
  channel: string
  key: string
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
  if (e.inflight || !getAuth()) return
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

/** "just now" · "4 min" · "2 h" · "3 d" — compact waits and ages. */
export function shortAge(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds < 60) return 'just now'
  const m = Math.floor(seconds / 60)
  if (m < 60) return `${m} min`
  const h = Math.floor(m / 60)
  if (h < 24) return `${h} h`
  return `${Math.floor(h / 24)} d`
}

export function secondsSince(iso: string): number {
  const t = new Date(iso).getTime()
  return Number.isFinite(t) ? Math.max(0, (Date.now() - t) / 1000) : 0
}
