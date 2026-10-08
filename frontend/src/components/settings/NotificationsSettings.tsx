'use client'

import { useEffect, useState } from 'react'
import Link from 'next/link'
import { AlertCircle } from 'lucide-react'
import { Card } from '@/components/ui/Card'
import { Toggle } from '@/components/ui/Toggle'
import { tint } from '@/lib/color'
import { NOTIF_CATEGORIES, notifApi } from '@/lib/notifications'
import { useAccess } from '@/lib/useAccess'

type Prefs = Record<string, boolean>

/**
 * Settings → Organization → Notification defaults: the organization-wide mute
 * switch per category (Setting rows notif_<category>). The central notifier
 * reads them before anything else — a muted category reaches NOBODY, whatever
 * their personal choice. Writes need the notifications.manage capability.
 */
export function NotificationsSettings() {
  const { ready, can } = useAccess()
  const canEdit = ready && can('notifications.manage')
  const [prefs, setPrefs] = useState<Prefs | null>(null)
  const [error, setError] = useState('')
  const [busy, setBusy] = useState<string | null>(null)

  useEffect(() => {
    notifApi<Prefs>('auth/notification-prefs')
      .then(setPrefs)
      .catch(() => setError('Notification defaults are unavailable — is the API running?'))
  }, [])

  const save = async (key: string, value: boolean) => {
    if (!prefs) return
    const prev = prefs
    setPrefs({ ...prefs, [key]: value }) // optimistic; rolled back on failure
    setError('')
    setBusy(key)
    try {
      setPrefs(await notifApi<Prefs>('auth/notification-prefs', { method: 'PUT', body: JSON.stringify({ [key]: value }) }))
    } catch (e) {
      setPrefs(prev)
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(null)
    }
  }

  if (error && prefs === null) return <p className="py-6 text-[12.5px] text-text-muted">{error}</p>

  const muted = prefs ? NOTIF_CATEGORIES.filter((c) => prefs[c.key] === false).length : 0

  return (
    <div className="flex flex-col gap-4">
      <Card className="divide-y divide-line">
        {NOTIF_CATEGORIES.map((c) => {
          const on = prefs?.[c.key] ?? true
          return (
            <div key={c.key} className="flex items-center gap-3 px-4 py-3.5">
              <span
                className="grid h-8 w-8 shrink-0 place-items-center rounded-lg"
                style={{ background: tint(c.ink, 0.12), color: c.ink }}
              >
                <c.Icon size={15} aria-hidden />
              </span>
              <div className="min-w-0 flex-1">
                <div className="text-[13.5px] font-semibold text-text-high">{c.label}</div>
                <div className="text-[12px] leading-snug text-text-muted">{c.description}</div>
              </div>
              <span className={`hidden text-[11.5px] font-semibold sm:inline ${on ? 'text-text-muted' : 'text-[color:var(--amber-ink)]'}`}>
                {on ? 'On for everyone' : 'Muted for everyone'}
              </span>
              <Toggle
                on={on}
                disabled={prefs === null || !canEdit || busy === c.key}
                onChange={(v) => void save(c.key, v)}
                label={`${c.label} for the whole organization`}
              />
            </div>
          )
        })}
      </Card>

      {error && prefs !== null && (
        <div role="alert" className="flex items-start gap-2 rounded-lg border border-red/25 bg-red/10 px-3 py-2 text-[12.5px] text-[color:var(--red-ink)]">
          <AlertCircle size={14} className="mt-0.5 shrink-0" aria-hidden /> {error}
        </div>
      )}

      <p className="text-[12px] leading-relaxed text-text-muted">
        {muted > 0
          ? `${muted} categor${muted === 1 ? 'y is' : 'ies are'} muted for the whole organization. `
          : 'Every category is on. '}
        Each person can still turn categories off for themselves under{' '}
        <Link href="/settings/my-notifications" className="font-semibold text-text-high underline underline-offset-2">
          My notifications
        </Link>
        ; a muted category stays silent for everyone. Changes apply to new alerts immediately — alerts already delivered stay
        in each inbox.
      </p>
    </div>
  )
}
