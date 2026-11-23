'use client'

import { API } from '@/lib/apiBase'
import { useCallback, useEffect, useState } from 'react'
import { getAuth, setAuth, userHasCap, type AuthUser, type Role } from './auth'
import { readPolicy, type ChannelPolicy } from './channelAccess'

type Whoami = { capabilities?: unknown; channel_access?: unknown } | null

/** The whoami read in flight, shared by every useAccess() mounted in the same
 *  render (a page, its cards and the shell's widgets mount together) — one
 *  request instead of one per hook. Only an UNSETTLED read is shared: the next
 *  mount after it answers asks again, exactly as before. */
let inflight: { token: string; p: Promise<Whoami> } | null = null
function readWhoami(token: string): Promise<Whoami> {
  if (inflight && inflight.token === token) return inflight.p
  const p = fetch(`${API}/auth/whoami`, { headers: { Authorization: `Bearer ${token}` } }).then(
    (r) => (r.ok ? (r.json() as Promise<Whoami>) : null),
  )
  const entry = { token, p }
  inflight = entry
  const done = () => {
    if (inflight === entry) inflight = null
  }
  p.then(done, done)
  return p
}

/**
 * Hydration-safe capability gate. Like useRole(): nothing is read from
 * localStorage until after mount, so the server render and the first client
 * render agree (`ready` false, every `can()` false except un-gated items).
 *
 * On mount it also re-asks whoami for the caller's capabilities and refreshes
 * the cached session — capabilities added to the catalog after someone signed
 * in would otherwise stay invisible to them until their next login.
 */
export function useAccess(): {
  ready: boolean
  role: Role
  user: AuthUser | null
  can: (cap?: string) => boolean
  /** The role's channel policy (null = every channel). */
  channelAccess: ChannelPolicy
} {
  const [user, setUser] = useState<AuthUser | null>(null)
  const [ready, setReady] = useState(false)

  useEffect(() => {
    const cached = getAuth()
    setUser(cached)
    setReady(true)
    if (!cached) return
    let alive = true
    readWhoami(cached.token)
      .then((d) => {
        if (!alive || !d || !Array.isArray(d.capabilities)) return
        const caps = d.capabilities.filter((c): c is string => typeof c === 'string')
        const next = { ...cached, capabilities: caps, channel_access: readPolicy(d.channel_access) }
        setAuth(next)
        setUser(next)
      })
      .catch(() => {
        /* offline: keep the cached list */
      })
    return () => {
      alive = false
    }
  }, [])

  const can = useCallback(
    (cap?: string) => {
      if (!cap) return true
      if (!user) return false
      if (user.role === 'superadmin') return true // always allowed, in code — like the server
      return userHasCap(user, cap)
    },
    [user],
  )

  const channelAccess = user?.role === 'superadmin' ? null : (user?.channel_access ?? null)
  return { ready, role: user?.role ?? 'agent', user, can, channelAccess }
}
