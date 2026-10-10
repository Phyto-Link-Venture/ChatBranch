'use client'

import { useEffect, useState } from 'react'
import { authHeaders } from './auth'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

// one whoami read per session token, shared by every caller
let pending: Promise<boolean> | null = null
let pendingFor = ''

/**
 * Is the signed-in person a COMPANY admin — an admin bound to entities, who
 * sees and manages only those companies (and their branches)? Read from
 * `GET /api/v1/whoami` (`company_admin`) after mount, so it is hydration-safe;
 * false until known. The server is the control — this only shapes the UI
 * (no "Everyone (global)" choice, no admin role to hand out).
 */
export function useCompanyAdmin(): boolean {
  const [value, setValue] = useState(false)
  useEffect(() => {
    let live = true
    const headers = authHeaders()
    const key = JSON.stringify(headers)
    if (key !== pendingFor) {
      pendingFor = key
      pending = null
    }
    pending ??= fetch(`${API}/v1/whoami`, { headers })
      .then((r) => (r.ok ? r.json() : null))
      .then((d) => !!d?.company_admin)
      .catch(() => false)
    pending.then((v) => live && setValue(v))
    return () => {
      live = false
    }
  }, [])
  return value
}

/** Forget the cached answer (sign-out / sign-in as someone else). */
export function resetCompanyAdmin() {
  pending = null
  pendingFor = ''
}
