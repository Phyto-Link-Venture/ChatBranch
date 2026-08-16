'use client'

import { useEffect, useState } from 'react'
import { Row, SectionLabel } from '@/components/settings/MockRows'
import { authHeaders } from '@/lib/auth'
import { useRole } from '@/lib/useRole'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

/** Mute groups for the in-app bell — server-side gate in crm.auth.notify
 * (Setting rows notif_<key>). Global: they apply to every user's bell. */
const CATEGORIES = [
  {
    key: 'messages',
    label: 'Inbound Message Alerts',
    sub: 'New customer messages and WhatsApp opt-outs, on every chat channel',
  },
  {
    key: 'account_health',
    label: 'Channel Health Alerts',
    sub: 'Account down/degraded, routing failover and recovery prompts',
  },
  {
    key: 'flows',
    label: 'Flow Alerts',
    sub: 'Flow-builder in-app pushes and human handovers',
  },
  {
    key: 'admin',
    label: 'Admin & Settings Alerts',
    sub: 'User management, app-key and account configuration changes',
  },
] as const

type Prefs = Record<string, boolean>

function Toggle({ on, disabled, onChange }: { on: boolean; disabled?: boolean; onChange: (v: boolean) => void }) {
  return (
    <div
      role="switch"
      aria-checked={on}
      onClick={() => !disabled && onChange(!on)}
      className={`relative h-[22px] w-[38px] rounded-full transition-colors ${
        disabled ? 'cursor-not-allowed opacity-50' : 'cursor-pointer'
      }`}
      style={{ background: on ? '#3B82F6' : 'var(--fill-hover)' }}
    >
      <div
        className="absolute top-[3px] h-4 w-4 rounded-full bg-white transition-all"
        style={{ left: on ? 18 : 3 }}
      />
    </div>
  )
}

/**
 * Notifications preferences — global mute switches for the in-app bell.
 * Promoted to its own top-level route (upgrade §1) but the logic is unchanged
 * from the old Settings → Notifications tab.
 */
export function NotificationsSettings() {
  const [prefs, setPrefs] = useState<Prefs | null>(null)
  const [error, setError] = useState('')
  const { role } = useRole()
  const canEdit = ['admin', 'superadmin'].includes(role)

  useEffect(() => {
    fetch(`${API}/auth/notification-prefs`, { headers: authHeaders() })
      .then((r) => (r.ok ? r.json() : Promise.reject(new Error(`HTTP ${r.status}`))))
      .then(setPrefs)
      .catch(() => setError('Notification settings unavailable — is the backend API running?'))
  }, [])

  const save = async (key: string, value: boolean) => {
    if (!prefs) return
    const prev = prefs
    setPrefs({ ...prefs, [key]: value }) // optimistic; revert on failure
    setError('')
    try {
      const res = await fetch(`${API}/auth/notification-prefs`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json', ...authHeaders() },
        body: JSON.stringify({ [key]: value }),
      })
      const data = await res.json().catch(() => ({}))
      if (!res.ok) throw new Error((data as { detail?: string }).detail || `HTTP ${res.status}`)
      setPrefs(data as Prefs)
    } catch (e) {
      setPrefs(prev)
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  if (error && prefs === null) {
    return <div className="py-6 text-[12px] text-text-muted">{error}</div>
  }

  return (
    <div>
      <SectionLabel t="In-app Bell" cls="my-4" />
      {CATEGORIES.map((c) => (
        <Row
          key={c.key}
          label={c.label}
          sub={c.sub}
          control={
            <Toggle
              on={prefs?.[c.key] ?? true}
              disabled={prefs === null || !canEdit}
              onChange={(v) => save(c.key, v)}
            />
          }
        />
      ))}
      <p className="mt-3 text-[11px] leading-relaxed text-text-muted">
        These switches mute whole alert categories for <strong>every user&apos;s</strong> notification
        bell{canEdit ? '' : ' — admin role required to change them'}. Changes apply to new events
        immediately; alerts already in the bell stay.
      </p>
      {error && prefs !== null && (
        <p className="mt-2 text-[12px] font-semibold text-red-300">{error}</p>
      )}
    </div>
  )
}
