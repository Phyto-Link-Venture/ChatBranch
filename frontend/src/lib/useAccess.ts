'use client'

import { useCallback, useEffect, useState } from 'react'
import { getAuth, setAuth, userHasCap, type AuthUser, type Role } from './auth'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

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
} {
  const [user, setUser] = useState<AuthUser | null>(null)
  const [ready, setReady] = useState(false)

  useEffect(() => {
    const cached = getAuth()
    setUser(cached)
    setReady(true)
    if (!cached) return
    let alive = true
    fetch(`${API}/auth/whoami`, { headers: { Authorization: `Bearer ${cached.token}` } })
      .then((r) => (r.ok ? r.json() : null))
      .then((d: { capabilities?: unknown } | null) => {
        if (!alive || !d || !Array.isArray(d.capabilities)) return
        const caps = d.capabilities.filter((c): c is string => typeof c === 'string')
        const next = { ...cached, capabilities: caps }
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

  return { ready, role: user?.role ?? 'agent', user, can }
}
