'use client'

import { useCallback, useEffect, useState } from 'react'
import { AlertCircle, CheckCircle, Loader2, Lock, RefreshCw, Save } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { roleHeaders, getRole } from '@/lib/role'
import { expireSessionOn401 } from '@/lib/auth'
import { useEntity, useEntityQuery } from '@/lib/EntityContext'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8000/api'

type DayHours = [number, number] | null
type Schedule = Record<string, DayHours>

const DAYS: { key: string; lbl: string }[] = [
  { key: 'mon', lbl: 'Monday' },
  { key: 'tue', lbl: 'Tuesday' },
  { key: 'wed', lbl: 'Wednesday' },
  { key: 'thu', lbl: 'Thursday' },
  { key: 'fri', lbl: 'Friday' },
  { key: 'sat', lbl: 'Saturday' },
  { key: 'sun', lbl: 'Sunday' },
]

/** 0..24 as "12 AM" … "12 AM (next day)" for the hour dropdowns. */
function hourLabel(h: number): string {
  if (h === 0) return '12 AM'
  if (h === 12) return '12 PM'
  if (h === 24) return 'Midnight'
  return h < 12 ? `${h} AM` : `${h - 12} PM`
}

async function hoursApi<T>(init?: RequestInit, entityQ = ''): Promise<T> {
  const res = await fetch(`${API}/aicore/booking-hours${entityQ && `?${entityQ}`}`, {
    headers: { 'Content-Type': 'application/json', ...roleHeaders() },
    ...init,
  })
  if (expireSessionOn401(res)) throw new Error('Session expired — redirecting to login…')
  const data = await res.json().catch(() => ({}))
  if (!res.ok) {
    throw new Error(
      (data as { detail?: string }).detail ||
        (res.status === 404 ? 'Booking-hours API not available on this backend yet.' : `HTTP ${res.status}`),
    )
  }
  return data as T
}

function normalize(raw: Record<string, unknown> | undefined): Schedule {
  const out: Schedule = {}
  for (const { key } of DAYS) {
    const v = raw?.[key]
    out[key] = Array.isArray(v) && v.length >= 2 ? [Number(v[0]), Number(v[1])] : null
  }
  return out
}

/** One-line digest of a schedule, for the "what you would fall back to" hint. */
function summarize(sched: Schedule): string {
  const open = DAYS.filter((d) => sched[d.key])
  if (!open.length) return 'closed every day'
  const first = sched[open[0].key]!
  const uniform = open.every((d) => {
    const v = sched[d.key]!
    return v[0] === first[0] && v[1] === first[1]
  })
  if (uniform && open.length === DAYS.length) {
    return `${hourLabel(first[0])}–${hourLabel(first[1])} daily`
  }
  if (uniform) {
    return `${hourLabel(first[0])}–${hourLabel(first[1])} on ${open.length} day${open.length === 1 ? '' : 's'}`
  }
  return `${open.length} day${open.length === 1 ? '' : 's'} open, varying hours`
}

/** Settings → Booking Hours: the per-day appointment schedule the AI uses to
 *  offer slots AND the hard guard that refuses bookings outside these hours.
 *  Admin+ edits via PUT /api/aicore/booking-hours; others see it read-only. */
export function BookingHoursSettings() {
  // Entity scope. With an outlet selected this page edits THAT outlet's
  // schedule; with "All entities" it edits the organization default every
  // outlet inherits. `scope` tells us which of the two is in force.
  const entityQ = useEntityQuery()
  const { activeEntity } = useEntity()
  const [scope, setScope] = useState<'entity' | 'global' | ''>('')
  const [inherited, setInherited] = useState<Schedule>({})
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [server, setServer] = useState<Schedule>({})
  const [hours, setHours] = useState<Schedule>({})
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [canEdit, setCanEdit] = useState(false)

  useEffect(() => setCanEdit(['admin', 'superadmin'].includes(getRole())), [])

  const load = useCallback(async () => {
    setLoading(true)
    setLoadError(null)
    try {
      const data = await hoursApi<{
        hours: Record<string, unknown>
        scope?: 'entity' | 'global' | ''
        global_hours?: Record<string, unknown>
      }>(undefined, entityQ)
      const norm = normalize(data.hours)
      setServer(norm)
      setHours(norm)
      setScope(data.scope ?? '')
      setInherited(normalize(data.global_hours))
    } catch (e) {
      setLoadError(e instanceof Error ? e.message : String(e))
    } finally {
      setLoading(false)
    }
  }, [entityQ])

  useEffect(() => {
    load()
  }, [load])

  const dirty = JSON.stringify(hours) !== JSON.stringify(server)

  const setDay = (key: string, val: DayHours) =>
    setHours((h) => ({ ...h, [key]: val }))

  const toggleClosed = (key: string, closed: boolean) =>
    setDay(key, closed ? null : [9, 18])

  const setBound = (key: string, idx: 0 | 1, value: number) =>
    setHours((h) => {
      const cur = h[key] ?? [9, 18]
      const next: [number, number] = idx === 0 ? [value, cur[1]] : [cur[0], value]
      return { ...h, [key]: next }
    })

  /** Drop this outlet's override so it follows the organization hours again.
   *  Only offered when an override actually exists — otherwise there is
   *  nothing to remove and the button would be a no-op. */
  const revertToInherited = async () => {
    if (!activeEntity) return
    if (!window.confirm(
      `Remove ${activeEntity.name}'s own booking hours and follow the organization hours instead?`,
    )) return
    setSaving(true)
    setError(null)
    setNotice(null)
    try {
      await hoursApi({ method: 'DELETE' }, entityQ)
      setNotice(`${activeEntity.name} now follows the organization booking hours.`)
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setSaving(false)
    }
  }

  const save = async () => {
    // Client-side sanity: open < close on every open day.
    for (const { key, lbl } of DAYS) {
      const v = hours[key]
      if (v && v[0] >= v[1]) {
        setError(`${lbl}: opening time must be before closing time.`)
        return
      }
    }
    setSaving(true)
    setError(null)
    setNotice(null)
    try {
      await hoursApi({ method: 'PUT', body: JSON.stringify({ hours }) }, entityQ)
      setNotice(
        activeEntity
          ? `Saved for ${activeEntity.name} — this outlet now overrides the organization hours.`
          : 'Organization booking hours saved — every entity without its own hours follows them.',
      )
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setSaving(false)
    }
  }

  return (
    <div className="pt-3">
      <div className="mb-1.5 text-[13px] font-bold text-text-high">Booking Hours</div>
      <p className="mb-4 max-w-[640px] text-[11.5px] leading-[1.6] text-text-muted">
        The per-day hours the assistant may book appointments in. It only offers free
        slots inside these windows, and a request outside them is refused — for both
        the customer chat and the calendar guard. Set a day to <em>Closed</em> to block it.
      </p>

      {/* Scope banner — which schedule you are editing, and what it inherits.
          Without this it is impossible to tell an outlet's own hours from the
          organization default you are about to change for every branch. */}
      <div className="mb-4 flex flex-wrap items-center gap-x-3 gap-y-2 rounded-lg border border-line bg-fill-subtle px-3 py-2.5">
        <span className="text-[12px] font-semibold text-text-high">
          {activeEntity ? `Editing: ${activeEntity.name}` : 'Editing: organization default'}
        </span>
        {activeEntity ? (
          scope === 'entity' ? (
            <>
              <span className="rounded-md bg-brand-500/15 px-1.5 py-0.5 text-[10px] font-semibold" style={{ color: 'var(--brand-ink)' }}>
                Own hours
              </span>
              <span className="text-[11px] text-text-muted">
                This outlet overrides the organization hours ({summarize(inherited)}).
              </span>
              {canEdit && (
                <button
                  onClick={revertToInherited}
                  disabled={saving}
                  className="ml-auto cursor-pointer rounded-lg border border-line px-2.5 py-1 text-[11px] font-semibold text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high disabled:opacity-50"
                >
                  Use organization hours
                </button>
              )}
            </>
          ) : (
            <>
              <span className="rounded-md bg-fill px-1.5 py-0.5 text-[10px] font-semibold text-text-muted">
                Inherited
              </span>
              <span className="text-[11px] text-text-muted">
                Following the organization hours. Saving here creates an override for
                this outlet only.
              </span>
            </>
          )
        ) : (
          <span className="text-[11px] text-text-muted">
            Every entity without its own hours follows these. Pick an entity in the top
            bar to give one outlet different hours.
          </span>
        )}
      </div>

      {error && (
        <div
          className="mb-3 flex items-start gap-2 rounded-lg border border-red-500/20 bg-red-500/10 px-3 py-2 text-[12px]"
          style={{ color: 'var(--red-ink)' }}
        >
          <AlertCircle size={13} className="mt-0.5 shrink-0" aria-hidden /> {error}
        </div>
      )}
      {notice && (
        <div
          className="mb-3 flex items-start gap-2 rounded-lg border border-brand-400/20 bg-brand-400/10 px-3 py-2 text-[12px]"
          style={{ color: 'var(--green-ink)' }}
        >
          <CheckCircle size={13} className="mt-0.5 shrink-0" aria-hidden /> {notice}
        </div>
      )}

      {loading ? (
        <div className="flex items-center gap-2 py-6 text-[12px] text-text-muted">
          <Loader2 size={14} className="animate-spin" aria-hidden /> Loading booking hours…
        </div>
      ) : loadError ? (
        <div>
          <div
            className="mb-3 flex items-start gap-2 rounded-lg border border-red-500/20 bg-red-500/10 px-3 py-2 text-[12px]"
            style={{ color: 'var(--red-ink)' }}
          >
            <AlertCircle size={13} className="mt-0.5 shrink-0" aria-hidden /> {loadError}
          </div>
          <Button variant="ghost" onClick={load}>
            <RefreshCw size={13} aria-hidden /> Retry
          </Button>
        </div>
      ) : (
        <div>
          {!canEdit && (
            <div className="mb-2 flex items-center gap-1.5 text-[11px] font-semibold text-text-muted">
              <Lock size={11} aria-hidden /> Read-only — only an admin can edit booking hours.
            </div>
          )}
          <div className="max-w-[560px] overflow-hidden rounded-xl border border-line">
            {DAYS.map(({ key, lbl }, i) => {
              const v = hours[key]
              const closed = v === null
              return (
                <div
                  key={key}
                  className={`flex items-center gap-3 px-3.5 py-2.5 ${
                    i > 0 ? 'border-t border-line' : ''
                  }`}
                >
                  <span className="w-[92px] text-[12px] font-semibold text-text-high">{lbl}</span>
                  <label className="flex cursor-pointer items-center gap-1.5 text-[11.5px] text-text-muted">
                    <input
                      type="checkbox"
                      checked={!closed}
                      disabled={!canEdit}
                      onChange={(e) => toggleClosed(key, !e.target.checked)}
                      className="accent-brand-500"
                    />
                    Open
                  </label>
                  {closed ? (
                    <span className="text-[11.5px] font-semibold" style={{ color: 'var(--text-muted)' }}>
                      Closed
                    </span>
                  ) : (
                    <div className="flex items-center gap-1.5 text-[11.5px] text-text-muted">
                      <select
                        value={v?.[0] ?? 9}
                        disabled={!canEdit}
                        onChange={(e) => setBound(key, 0, Number(e.target.value))}
                        className="rounded-lg border border-line-strong bg-card px-2 py-1 text-[11.5px] text-text-high outline-none focus:border-brand-400 disabled:cursor-default disabled:bg-fill-subtle"
                      >
                        {Array.from({ length: 24 }, (_, h) => (
                          <option key={h} value={h}>
                            {hourLabel(h)}
                          </option>
                        ))}
                      </select>
                      <span>to</span>
                      <select
                        value={v?.[1] ?? 18}
                        disabled={!canEdit}
                        onChange={(e) => setBound(key, 1, Number(e.target.value))}
                        className="rounded-lg border border-line-strong bg-card px-2 py-1 text-[11.5px] text-text-high outline-none focus:border-brand-400 disabled:cursor-default disabled:bg-fill-subtle"
                      >
                        {Array.from({ length: 24 }, (_, h) => h + 1).map((h) => (
                          <option key={h} value={h}>
                            {hourLabel(h)}
                          </option>
                        ))}
                      </select>
                    </div>
                  )}
                </div>
              )
            })}
          </div>

          {canEdit && (
            <div className="mt-3 flex items-center gap-2">
              <Button onClick={save} disabled={!dirty || saving}>
                {saving ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Save size={13} aria-hidden />}
                {saving ? 'Saving…' : 'Save booking hours'}
              </Button>
              {dirty && (
                <span className="text-[11px] font-semibold" style={{ color: 'var(--amber-ink)' }}>
                  Unsaved changes
                </span>
              )}
            </div>
          )}
        </div>
      )}
    </div>
  )
}
