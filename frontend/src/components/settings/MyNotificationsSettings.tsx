'use client'

import { useEffect, useState } from 'react'
import Link from 'next/link'
import { AlertCircle, ArrowRight, Inbox } from 'lucide-react'
import { Card, CardTitle } from '@/components/ui/Card'
import { Toggle } from '@/components/ui/Toggle'
import { tint } from '@/lib/color'
import { NOTIF_CATEGORIES, notifApi, type NotificationPrefs } from '@/lib/notifications'

/**
 * Settings → Personal → My notifications: the caller's own in-app switch per
 * category, plus "only conversations assigned to me or my teams" for customer
 * messages. Stored on the user's profile; the central notifier applies them
 * after the organization-wide defaults (a category muted there stays muted).
 */
export function MyNotificationsSettings() {
  const [prefs, setPrefs] = useState<NotificationPrefs | null>(null)
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    notifApi<NotificationPrefs>('notifications/preferences')
      .then(setPrefs)
      .catch(() => setError('Your notification preferences are unavailable — is the API running?'))
  }, [])

  const save = async (patch: { user?: Record<string, boolean>; only_assigned?: boolean }) => {
    if (!prefs) return
    const prev = prefs
    setPrefs({
      ...prefs,
      user: { ...prefs.user, ...(patch.user ?? {}) },
      only_assigned: patch.only_assigned ?? prefs.only_assigned,
    })
    setError('')
    setBusy(true)
    try {
      setPrefs(await notifApi<NotificationPrefs>('notifications/preferences', { method: 'PUT', body: JSON.stringify(patch) }))
    } catch (e) {
      setPrefs(prev)
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  if (error && prefs === null) return <p className="py-6 text-[12.5px] text-text-muted">{error}</p>

  return (
    <div className="flex flex-col gap-5">
      <Card className="p-4 sm:p-5">
        <CardTitle right={<span className="text-text-muted">Bell + inbox</span>}>Alerts I receive</CardTitle>
        <p className="mt-1 text-[12.5px] text-text-muted">Turn off what you never need to see. Nothing here affects anyone else.</p>
        <div className="mt-3 divide-y divide-line border-t border-line">
          {NOTIF_CATEGORIES.map((c) => {
            const orgOn = prefs?.org?.[c.key] !== false
            const on = orgOn && (prefs?.user?.[c.key] ?? true)
            return (
              <div key={c.key} className="flex items-center gap-3 py-3">
                <span
                  className="grid h-8 w-8 shrink-0 place-items-center rounded-lg"
                  style={{ background: tint(c.ink, 0.12), color: c.ink }}
                >
                  <c.Icon size={15} aria-hidden />
                </span>
                <div className="min-w-0 flex-1">
                  <div className="text-[13.5px] font-semibold text-text-high">{c.label}</div>
                  <div className="text-[12px] leading-snug text-text-muted">
                    {orgOn ? c.description : 'Muted for the whole organization by an admin.'}
                  </div>
                </div>
                <Toggle
                  on={on}
                  disabled={!prefs || busy || !orgOn}
                  onChange={(v) => void save({ user: { [c.key]: v } })}
                  label={c.label}
                />
              </div>
            )
          })}
        </div>
      </Card>

      <Card className="p-4 sm:p-5">
        <CardTitle>Customer messages scope</CardTitle>
        <div className="mt-3 flex items-center gap-3 border-t border-line pt-3">
          <div className="min-w-0 flex-1">
            <div className="text-[13.5px] font-semibold text-text-high">Only conversations assigned to me or my teams</div>
            <div className="text-[12px] leading-snug text-text-muted">
              New-message alerts go to the conversation&rsquo;s assignee, else its team, else the admins. Switch this on to skip
              unassigned conversations that land on you only because you are an admin.
            </div>
          </div>
          <Toggle
            on={prefs?.only_assigned ?? false}
            disabled={!prefs || busy || prefs.user?.messages === false || prefs.org?.messages === false}
            onChange={(v) => void save({ only_assigned: v })}
            label="Only conversations assigned to me or my teams"
          />
        </div>
      </Card>

      {error && prefs !== null && (
        <div role="alert" className="flex items-start gap-2 rounded-lg border border-red/25 bg-red/10 px-3 py-2 text-[12.5px] text-[color:var(--red-ink)]">
          <AlertCircle size={14} className="mt-0.5 shrink-0" aria-hidden /> {error}
        </div>
      )}

      <div className="flex flex-wrap items-center gap-x-4 gap-y-2 text-[12.5px]">
        <Link href="/notifications" className="inline-flex items-center gap-1.5 font-semibold text-text-high hover:underline">
          <Inbox size={14} aria-hidden /> Open my notification inbox <ArrowRight size={13} aria-hidden />
        </Link>
        {prefs?.can_manage_org && (
          <Link href="/settings/notifications" className="text-text-muted hover:text-text-high hover:underline">
            Organization defaults
          </Link>
        )}
      </div>
    </div>
  )
}
