'use client'

/**
 * One poller for the signed-in user's notifications, shared by everything that
 * shows them (the top-bar bell and the sidebar's Notifications counter) so the
 * two never disagree and the API is asked once per interval, not once per
 * consumer. Ref-counted: it polls while at least one component is subscribed.
 */

import { useCallback, useEffect, useState } from 'react'
import { authHeaders, getAuth } from '@/lib/auth'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'
const POLL_MS = 20_000

export interface Notif {
  id: number
  kind: 'message' | 'call' | 'system'
  title: string
  body: string
  link: string
  read: boolean
  created_at: string
}

interface State {
  unread: number
  items: Notif[]
  loaded: boolean
}

let state: State = { unread: 0, items: [], loaded: false }
const listeners = new Set<(s: State) => void>()
let timer: ReturnType<typeof setInterval> | null = null

function emit(next: State) {
  state = next
  listeners.forEach((fn) => fn(state))
}

export async function refreshNotifications(): Promise<void> {
  if (!getAuth()) return
  try {
    const res = await fetch(`${API}/auth/notifications`, { headers: authHeaders(), cache: 'no-store' })
    if (!res.ok) return
    const data = (await res.json()) as { unread?: number; notifications?: Notif[] }
    emit({ unread: data.unread ?? 0, items: data.notifications ?? [], loaded: true })
  } catch {
    /* API down — keep the last good state */
  }
}

async function post(body: unknown) {
  await fetch(`${API}/auth/notifications`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
    body: JSON.stringify(body),
  }).catch(() => {})
}

/** Forget the previous user's notifications (sign-out / user switch). */
export function resetNotifications(): void {
  emit({ unread: 0, items: [], loaded: false })
}

export async function markAllNotificationsRead(): Promise<void> {
  emit({ ...state, unread: 0, items: state.items.map((n) => ({ ...n, read: true })) })
  await post({ id: 'all' })
  void refreshNotifications()
}

export function markNotificationRead(n: Notif): void {
  if (n.read) return
  emit({
    ...state,
    unread: Math.max(0, state.unread - 1),
    items: state.items.map((x) => (x.id === n.id ? { ...x, read: true } : x)),
  })
  void post({ id: n.id })
}

function subscribe(fn: (s: State) => void): () => void {
  listeners.add(fn)
  if (!timer) {
    void refreshNotifications()
    timer = setInterval(() => void refreshNotifications(), POLL_MS)
  }
  return () => {
    listeners.delete(fn)
    if (!listeners.size && timer) {
      clearInterval(timer)
      timer = null
    }
  }
}

export function useNotifications() {
  const [s, setS] = useState<State>(state)
  useEffect(() => subscribe(setS), [])
  const refresh = useCallback(() => void refreshNotifications(), [])
  return { ...s, refresh }
}
