'use client'

import { useEffect, useRef, useState, type ReactNode } from 'react'
import Link from 'next/link'
import { AlertCircle, CalendarCheck, CalendarSearch, Clock, Info, Loader2, Plus, X } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { ChannelLogo } from '@/components/ui/ChannelLogo'
import { Select, type SelectOption } from '@/components/ui/Select'
import { Toggle } from '@/components/ui/Toggle'
import {
  WEEKDAYS,
  calendarTargetLabel,
  previewAvailability,
  type AppAction,
  type AppCatalogEntry,
  type AppTestResponse,
  type CalendarAppConfig,
  type HoursWindow,
  type Weekday,
} from './skillsApi'

/**
 * The "Connected app" skill's settings — Calendar booking. Configured directly
 * (no AI drafting, no code): where bookings go (Google Calendar or ChatBranch's
 * own bookings), whose calendar, what the AI may do, the weekly working hours,
 * the appointment rules and the event it creates. "Preview availability" is a
 * DRY RUN of the availability tool with the unsaved settings — nothing is
 * booked.
 *
 * Booking itself is two-phase on the server: the AI only RESERVES a time while
 * it writes; the booking is made when the reply is delivered (so a reply held
 * in the AI monitor books on approval, and a rejected one releases it).
 */

const field =
  'rounded-lg border border-line-strong bg-card px-3 py-2 text-[13px] text-text-primary outline-none transition-colors focus:border-brand-400 disabled:opacity-70'
const input = `w-full ${field}`
/** Short numbers — a fixed width so the unit stays beside the box. */
const numInput = `w-[88px] shrink-0 ${field}`

const ACTION_HELP: Record<AppAction, string> = {
  availability: 'Read the free times inside your working hours.',
  book: 'Reserve the time the customer picked — made when the reply is sent.',
  cancel: 'Only this customer’s own bookings made through ChatBranch. Rescheduling = cancel + book.',
}

const EVENT_VARIABLES = ['name', 'phone', 'email', 'company'] as const

// ---- time helpers --------------------------------------------------------------

const toMin = (hhmm: string) => {
  const m = /^(\d{1,2}):(\d{2})$/.exec(hhmm.trim())
  return m ? Number(m[1]) * 60 + Number(m[2]) : NaN
}
const fmt = (min: number) => `${String(Math.floor(min / 60)).padStart(2, '0')}:${String(min % 60).padStart(2, '0')}`
const LAST = 23 * 60 + 45

/** Every quarter hour, 00:00 → 23:45 (plus any odd stored value, kept selectable). */
function timeOptions(extra: string): SelectOption[] {
  const out: string[] = []
  for (let m = 0; m <= LAST; m += 15) out.push(fmt(m))
  if (extra && !out.includes(extra) && Number.isFinite(toMin(extra))) out.push(extra)
  return out.sort((a, b) => toMin(a) - toMin(b)).map((v) => ({ value: v, label: v }))
}

/** "Add break": split the last window around its middle hour; a short one gets a later window instead. */
function addBreak(wins: HoursWindow[]): HoursWindow[] {
  const last = wins[wins.length - 1]
  if (!last) return [['09:00', '17:00']]
  const a = toMin(last[0])
  const b = toMin(last[1])
  if (Number.isFinite(a) && Number.isFinite(b) && b - a >= 180) {
    const mid = Math.round((a + (b - a) / 2 - 60) / 15) * 15
    return [...wins.slice(0, -1), [last[0], fmt(mid)], [fmt(mid + 60), last[1]]]
  }
  const s = (Number.isFinite(b) ? b : 17 * 60) + 60
  const e = Math.min(s + 120, LAST)
  return s < e ? [...wins, [fmt(s), fmt(e)]] : wins
}

/** One weekday's problem, in words — or null when its windows are fine. */
export function dayProblem(wins: HoursWindow[]): string | null {
  const spans = wins.map(([f, t]) => [toMin(f), toMin(t)] as const)
  if (spans.some(([f, t]) => !Number.isFinite(f) || !Number.isFinite(t))) return 'use HH:MM times'
  if (spans.some(([f, t]) => f >= t)) return 'each window must end after it starts'
  const sorted = [...spans].sort((x, y) => x[0] - y[0])
  for (let i = 1; i < sorted.length; i++) if (sorted[i][0] < sorted[i - 1][1]) return 'windows overlap'
  return null
}

/** The whole config's first problem (shown in the wizard's alert), or null. */
export function calendarConfigProblem(c: CalendarAppConfig): string | null {
  if (!c.actions.length) return 'Pick at least one thing the AI may do with the calendar.'
  for (const d of WEEKDAYS) {
    const p = dayProblem(c.hours[d.key] ?? [])
    if (p) return `Working hours — ${d.label}: ${p}.`
  }
  if (!WEEKDAYS.some((d) => (c.hours[d.key] ?? []).length > 0)) return 'Open at least one day in the working hours.'
  const int = (v: number) => Number.isInteger(v)
  if (!int(c.duration_minutes) || c.duration_minutes < 5 || c.duration_minutes > 480)
    return 'The appointment length must be between 5 and 480 minutes.'
  if (!int(c.slot_step_minutes) || c.slot_step_minutes < 5 || c.slot_step_minutes > 480)
    return 'Start times must be between 5 and 480 minutes apart.'
  if (!int(c.buffer_minutes) || c.buffer_minutes < 0 || c.buffer_minutes > 240)
    return 'The buffer must be between 0 and 240 minutes.'
  if (!int(c.min_notice_minutes) || c.min_notice_minutes < 0 || c.min_notice_minutes > 60 * 24 * 30)
    return 'The minimum notice must be between 0 minutes and 30 days.'
  if (!int(c.max_days_ahead) || c.max_days_ahead < 1 || c.max_days_ahead > 90)
    return 'Bookings can be made 1 to 90 days ahead.'
  if (!int(c.max_active_per_contact) || c.max_active_per_contact < 1 || c.max_active_per_contact > 20)
    return 'Active bookings per customer must be between 1 and 20.'
  if (!c.event_title.trim()) return 'Give the calendar event a title.'
  return null
}

// ---- the form ------------------------------------------------------------------

export function AppSkillForm({
  config: c,
  onChange,
  app,
  appsState,
  orgTimezone,
  readOnly = false,
}: {
  config: CalendarAppConfig
  onChange: (next: CalendarAppConfig) => void
  /** The calendar entry of GET /skills/apps (or the offline fallback). */
  app: AppCatalogEntry
  appsState: 'loading' | 'ok' | 'offline'
  orgTimezone?: string
  readOnly?: boolean
}) {
  const set = (p: Partial<CalendarAppConfig>) => onChange({ ...c, ...p })
  const google = app.sources.find((s) => s.source === 'google')
  const connections = google?.connections ?? []

  // timezone list after mount — Intl data differs between server and browser
  const [zones, setZones] = useState<string[]>([])
  useEffect(() => {
    const intl = Intl as unknown as { supportedValuesOf?: (k: string) => string[] }
    setZones(intl.supportedValuesOf?.('timeZone') ?? [])
  }, [])

  const setDay = (day: Weekday, wins: HoursWindow[]) => set({ hours: { ...c.hours, [day]: wins } })
  const toggleAction = (a: AppAction, on: boolean) =>
    set({ actions: (['availability', 'book', 'cancel'] as AppAction[]).filter((x) => (x === a ? on : c.actions.includes(x))) })

  const calendarOptions: SelectOption[] = [
    {
      value: 'assignee',
      label: 'Conversation’s assigned agent',
      hint: '— their own Google grant, else the shared one',
      group: 'Follows the conversation',
    },
    {
      value: 'shared',
      label: 'This outlet’s shared calendar',
      hint: '— else the organisation’s',
      group: 'Follows the conversation',
    },
    ...connections.map((x) => {
      const owner = x.owner === 'user' ? 'personal' : x.owner === 'entity' ? 'outlet' : 'organisation'
      const name = x.owner_name && !/^organi[sz]ation$/i.test(x.owner_name) ? ` · ${x.owner_name}` : ''
      return {
        value: String(x.id),
        label: x.label || `Connection #${x.id}`,
        hint: `— ${owner}${name}`,
        group: 'A specific connection',
      }
    }),
    // a stored id that is no longer offered stays visible (the server re-validates it)
    ...(!['assignee', 'shared'].includes(c.calendar) && !connections.some((x) => String(x.id) === c.calendar)
      ? [{ value: c.calendar, label: calendarTargetLabel(c.calendar, connections), hint: '— not available', group: 'A specific connection' }]
      : []),
  ]

  return (
    <div className="flex flex-col gap-6">
      {/* ---- calendar source ---------------------------------------------------- */}
      <Section title="Calendar" hint="Where the AI reads free times and puts bookings.">
        <div role="radiogroup" aria-label="Calendar source" className="grid grid-cols-1 gap-2 sm:grid-cols-2">
          <SourceCard
            on={c.source === 'google'}
            disabled={readOnly}
            onPick={() => set({ source: 'google' })}
            icon={<ChannelLogo channel="google_calendar" size={18} title="" />}
            title={google?.label ?? 'Google Calendar'}
            body={
              appsState === 'loading' ? (
                <span className="inline-flex items-center gap-1 text-text-muted">
                  <Loader2 size={11} className="animate-spin" aria-hidden /> Checking the connection…
                </span>
              ) : appsState === 'offline' ? (
                <span className="text-text-muted">Couldn’t check the connection right now.</span>
              ) : google?.connected ? (
                <span className="inline-flex items-center gap-1.5 text-[color:var(--green-ink)]">
                  <span className="h-1.5 w-1.5 rounded-full bg-green" aria-hidden /> Connected
                  {connections.length > 0 && (
                    <span className="text-text-muted">
                      · {connections.length} calendar{connections.length === 1 ? '' : 's'}
                    </span>
                  )}
                </span>
              ) : (
                <span className="inline-flex flex-wrap items-center gap-1 text-[color:var(--amber-ink)]">
                  <span className="h-1.5 w-1.5 rounded-full bg-amber" aria-hidden /> Not connected —
                  <Link href="/integrations" className="font-semibold underline-offset-2 hover:underline">
                    connect it in Integrations
                  </Link>
                </span>
              )
            }
          />
          <SourceCard
            on={c.source === 'chatbranch'}
            disabled={readOnly}
            onPick={() => set({ source: 'chatbranch' })}
            icon={<CalendarCheck size={17} className="text-text-high" aria-hidden />}
            title={app.sources.find((s) => s.source === 'chatbranch')?.label ?? 'ChatBranch bookings'}
            body={<span className="text-text-muted">Works without Google — bookings live on the Bookings page.</span>}
          />
        </div>

        {c.source === 'google' && (
          <div className="mt-3 grid grid-cols-1 gap-4 md:grid-cols-[minmax(0,1fr)_200px]">
            <Field label="Whose calendar">
              <Select
                value={c.calendar}
                disabled={readOnly}
                onChange={(v) => set({ calendar: v })}
                options={calendarOptions}
                size="md"
                ariaLabel="Whose calendar"
              />
            </Field>
            <Field label="Calendar ID" hint="primary = the account’s main calendar">
              <input
                value={c.calendar_id}
                disabled={readOnly}
                onChange={(e) => set({ calendar_id: e.target.value })}
                placeholder="primary"
                className={input}
              />
            </Field>
          </div>
        )}
        {c.source === 'google' && appsState !== 'loading' && !google?.connected && (
          <Note tone="amber">
            Until Google Calendar is connected, the AI is told the calendar is unavailable and offers a teammate — it
            never quietly books into ChatBranch instead.
          </Note>
        )}
      </Section>

      {/* ---- actions ---------------------------------------------------------- */}
      <Section title="What the AI may do">
        <ul className="flex flex-col divide-y divide-line rounded-lg border border-line">
          {app.actions.map((a) => {
            const on = c.actions.includes(a.id)
            return (
              <li key={a.id}>
                <label className={`flex items-start gap-2.5 px-3 py-2.5 ${readOnly ? '' : 'cursor-pointer hover:bg-fill-subtle'}`}>
                  <input
                    type="checkbox"
                    checked={on}
                    disabled={readOnly}
                    onChange={(e) => toggleAction(a.id, e.target.checked)}
                    className="mt-0.5 h-3.5 w-3.5 shrink-0"
                    style={{ accentColor: 'var(--primary)' }}
                  />
                  <span className="min-w-0">
                    <span className="block text-[12.5px] font-semibold text-text-high">{a.label}</span>
                    <span className="block text-[11.5px] leading-snug text-text-muted">{ACTION_HELP[a.id]}</span>
                  </span>
                </label>
              </li>
            )
          })}
        </ul>
        <p className="mt-2 text-[11.5px] leading-relaxed text-text-muted">
          At most one booking and one cancellation per reply, only for the customer in the conversation. A booking
          reserved while the AI writes is made when the reply is sent — a reply held for review books on approval.
        </p>
      </Section>

      {/* ---- working hours ------------------------------------------------------ */}
      <Section
        title="Working hours"
        hint="The AI only offers times inside these windows."
        right={
          <Select
            value={c.timezone}
            disabled={readOnly}
            onChange={(v) => set({ timezone: v })}
            searchable
            ariaLabel="Timezone"
            className="w-full min-w-[220px] sm:w-auto"
            options={[
              { value: '', label: 'Organisation’s timezone', hint: orgTimezone ? `(${orgTimezone})` : undefined },
              ...(c.timezone && !zones.includes(c.timezone) ? [{ value: c.timezone, label: c.timezone }] : []),
              ...zones.map((z) => ({ value: z, label: z })),
            ]}
          />
        }
      >
        <div className="overflow-hidden rounded-lg border border-line">
          {WEEKDAYS.map((d, i) => {
            const wins = c.hours[d.key] ?? []
            const open = wins.length > 0
            const problem = open ? dayProblem(wins) : null
            return (
              <div
                key={d.key}
                className={`flex flex-wrap items-start gap-x-3 gap-y-2 px-3 py-2.5 ${i > 0 ? 'border-t border-line' : ''} ${
                  open ? '' : 'bg-fill-subtle'
                }`}
              >
                {/* on a phone an open day's windows drop below its name */}
                <div className={`flex h-8 shrink-0 items-center gap-2 ${open ? 'w-full sm:w-[118px]' : 'w-[118px]'}`}>
                  <Toggle
                    on={open}
                    disabled={readOnly}
                    onChange={(v) => setDay(d.key, v ? [['09:00', '17:00']] : [])}
                    label={`${d.label} ${open ? 'open' : 'closed'}`}
                  />
                  <span className={`text-[12.5px] font-semibold ${open ? 'text-text-high' : 'text-text-muted'}`}>{d.label}</span>
                </div>
                {!open ? (
                  <span className="flex h-8 items-center text-[12px] text-text-muted">Closed</span>
                ) : (
                  <div className="flex min-w-0 flex-1 flex-col gap-1.5">
                    <div className="flex flex-wrap items-center gap-x-3 gap-y-1.5">
                      {wins.map((w, wi) => (
                        <div key={wi} className="flex items-center gap-1">
                          <Select
                            value={w[0]}
                            disabled={readOnly}
                            onChange={(v) => setDay(d.key, wins.map((x, j) => (j === wi ? [v, x[1]] : x)))}
                            options={timeOptions(w[0])}
                            searchable
                            ariaLabel={`${d.label} window ${wi + 1} from`}
                            className="w-[92px]"
                          />
                          <span className="text-[11.5px] text-text-muted">–</span>
                          <Select
                            value={w[1]}
                            disabled={readOnly}
                            onChange={(v) => setDay(d.key, wins.map((x, j) => (j === wi ? [x[0], v] : x)))}
                            options={timeOptions(w[1])}
                            searchable
                            ariaLabel={`${d.label} window ${wi + 1} to`}
                            className="w-[92px]"
                          />
                          {wins.length > 1 && !readOnly && (
                            <button
                              type="button"
                              onClick={() => setDay(d.key, wins.filter((_, j) => j !== wi))}
                              aria-label={`Remove ${d.label} window ${wi + 1}`}
                              title="Remove this window"
                              className="grid h-7 w-7 place-items-center rounded-md text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
                            >
                              <X size={12} aria-hidden />
                            </button>
                          )}
                        </div>
                      ))}
                      {!readOnly && wins.length < 4 && (
                        <button
                          type="button"
                          onClick={() => setDay(d.key, addBreak(wins))}
                          className="inline-flex h-7 items-center gap-1 rounded-md px-1.5 text-[11.5px] font-semibold text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
                        >
                          <Plus size={12} aria-hidden /> Add break
                        </button>
                      )}
                    </div>
                    {problem && (
                      <span className="text-[11.5px] text-[color:var(--red-ink)]">
                        {problem.charAt(0).toUpperCase() + problem.slice(1)}.
                      </span>
                    )}
                  </div>
                )}
              </div>
            )
          })}
        </div>
        <p className="mt-1.5 text-[11px] text-text-muted">24-hour times, in the timezone above.</p>
      </Section>

      {/* ---- appointment rules --------------------------------------------------- */}
      <Section title="Appointments">
        <div className="grid grid-cols-1 gap-x-4 gap-y-3 sm:grid-cols-2 lg:grid-cols-3">
          <NumField
            label="Appointment length"
            suffix="min"
            value={c.duration_minutes}
            min={5}
            max={480}
            disabled={readOnly}
            onChange={(v) =>
              set({
                duration_minutes: v,
                // a step that tracked the length keeps tracking it
                slot_step_minutes: c.slot_step_minutes === c.duration_minutes ? v : c.slot_step_minutes,
              })
            }
          />
          <NumField
            label="Start times every"
            suffix="min"
            value={c.slot_step_minutes}
            min={5}
            max={480}
            disabled={readOnly}
            hint="Usually the same as the length."
            onChange={(v) => set({ slot_step_minutes: v })}
          />
          <NumField
            label="Buffer around busy times"
            suffix="min"
            value={c.buffer_minutes}
            min={0}
            max={240}
            disabled={readOnly}
            onChange={(v) => set({ buffer_minutes: v })}
          />
          <NoticeField value={c.min_notice_minutes} disabled={readOnly} onChange={(v) => set({ min_notice_minutes: v })} />
          <NumField
            label="Book up to"
            suffix="days ahead"
            value={c.max_days_ahead}
            min={1}
            max={90}
            disabled={readOnly}
            onChange={(v) => set({ max_days_ahead: v })}
          />
          <NumField
            label="Active bookings per customer"
            value={c.max_active_per_contact}
            min={1}
            max={20}
            disabled={readOnly}
            hint="More are refused — the AI offers to reschedule."
            onChange={(v) => set({ max_active_per_contact: v })}
          />
        </div>
      </Section>

      {/* ---- the event ------------------------------------------------------------- */}
      <Section title="Calendar event">
        <EventTitleField value={c.event_title} disabled={readOnly} onChange={(v) => set({ event_title: v })} />
        <div className="mt-3">
          <Field label="Event notes" hint="Optional — the same {{variables}} work here.">
            <textarea
              rows={2}
              value={c.event_description}
              disabled={readOnly}
              onChange={(e) => set({ event_description: e.target.value })}
              placeholder="Booked by the ChatBranch assistant."
              className={`${input} resize-y leading-[1.55]`}
            />
          </Field>
        </div>
      </Section>

      <AvailabilityPreview config={c} />
    </div>
  )
}

// ---- pieces ----------------------------------------------------------------------

function Section({
  title,
  hint,
  right,
  children,
}: {
  title: string
  hint?: string
  right?: ReactNode
  children: ReactNode
}) {
  return (
    <section className="min-w-0">
      <div className="mb-2.5 flex flex-wrap items-end gap-x-4 gap-y-2">
        <div className="min-w-0">
          <h3 className="eyebrow">{title}</h3>
          {hint && <p className="mt-0.5 text-[11.5px] text-text-muted">{hint}</p>}
        </div>
        {right && <div className="w-full sm:ml-auto sm:w-auto">{right}</div>}
      </div>
      {children}
    </section>
  )
}

function Field({ label, hint, children }: { label: string; hint?: string; children: ReactNode }) {
  return (
    <div className="min-w-0">
      <div className="mb-1.5 text-[12px] font-semibold text-text-high">{label}</div>
      {children}
      {hint && <div className="mt-1 text-[11px] leading-snug text-text-muted">{hint}</div>}
    </div>
  )
}

function Note({ tone, children }: { tone: 'amber' | 'muted'; children: ReactNode }) {
  return (
    <div
      className={`mt-3 flex items-start gap-2 rounded-lg border px-3 py-2 text-[12px] leading-relaxed ${
        tone === 'amber' ? 'border-amber/40 bg-amber/10 text-text-high' : 'border-line bg-fill-subtle text-text-mid'
      }`}
    >
      <Info size={13} className={`mt-0.5 shrink-0 ${tone === 'amber' ? 'text-amber-300' : 'text-text-muted'}`} aria-hidden />
      <span>{children}</span>
    </div>
  )
}

function SourceCard({
  on,
  disabled,
  onPick,
  icon,
  title,
  body,
}: {
  on: boolean
  disabled?: boolean
  onPick: () => void
  icon: ReactNode
  title: string
  body: ReactNode
}) {
  return (
    <div
      role="radio"
      aria-checked={on}
      aria-disabled={disabled || undefined}
      tabIndex={disabled ? -1 : 0}
      onClick={() => !disabled && onPick()}
      onKeyDown={(e) => {
        if (!disabled && (e.key === ' ' || e.key === 'Enter')) {
          e.preventDefault()
          onPick()
        }
      }}
      className={`flex items-start gap-2.5 rounded-lg border px-3 py-2.5 text-left outline-none transition-colors focus-visible:border-text-muted ${
        on ? 'border-primary bg-fill' : 'border-line hover:border-line-strong hover:bg-fill-subtle'
      } ${disabled ? 'cursor-default' : 'cursor-pointer'}`}
    >
      <span
        className={`mt-0.5 grid h-4 w-4 shrink-0 place-items-center rounded-full border ${on ? 'border-primary' : 'border-line-strong'}`}
        aria-hidden
      >
        {on && <span className="h-2 w-2 rounded-full bg-primary" />}
      </span>
      <span className="min-w-0 flex-1">
        <span className="flex items-center gap-1.5 text-[12.5px] font-semibold text-text-heading">
          {icon} {title}
        </span>
        <span className="mt-0.5 block text-[11.5px] leading-snug">{body}</span>
      </span>
    </div>
  )
}

function NumField({
  label,
  value,
  onChange,
  min,
  max,
  suffix,
  hint,
  disabled,
}: {
  label: string
  value: number
  onChange: (v: number) => void
  min: number
  max: number
  suffix?: string
  hint?: string
  disabled?: boolean
}) {
  // keep the typed text while editing so an empty box doesn't snap to 0
  const [text, setText] = useState(String(value))
  useEffect(() => setText(String(value)), [value])
  return (
    <Field label={label} hint={hint}>
      <div className="flex items-center gap-2">
        <input
          type="number"
          inputMode="numeric"
          min={min}
          max={max}
          value={text}
          disabled={disabled}
          onChange={(e) => {
            setText(e.target.value)
            if (e.target.value.trim() !== '' && Number.isFinite(Number(e.target.value))) onChange(Math.round(Number(e.target.value)))
          }}
          onBlur={() => setText(String(value))}
          className={numInput}
          aria-label={label}
        />
        {suffix && <span className="whitespace-nowrap text-[12px] text-text-muted">{suffix}</span>}
      </div>
    </Field>
  )
}

/** Minimum notice, typed in hours or minutes, stored in minutes. */
function NoticeField({ value, onChange, disabled }: { value: number; onChange: (v: number) => void; disabled?: boolean }) {
  const [unit, setUnit] = useState<'hours' | 'minutes'>(value % 60 === 0 && value > 0 ? 'hours' : 'minutes')
  const shown = unit === 'hours' ? value / 60 : value
  const [text, setText] = useState(String(shown))
  useEffect(() => setText(String(unit === 'hours' ? value / 60 : value)), [value, unit])
  return (
    <Field label="Minimum notice" hint="No same-moment bookings — the earliest time offered.">
      <div className="flex items-center gap-2">
        <input
          type="number"
          inputMode="decimal"
          min={0}
          value={text}
          disabled={disabled}
          onChange={(e) => {
            setText(e.target.value)
            const n = Number(e.target.value)
            if (e.target.value.trim() !== '' && Number.isFinite(n)) onChange(Math.round(unit === 'hours' ? n * 60 : n))
          }}
          onBlur={() => setText(String(shown))}
          className={numInput}
          aria-label="Minimum notice"
        />
        <Select
          value={unit}
          disabled={disabled}
          onChange={(u) => setUnit(u as 'hours' | 'minutes')}
          options={[
            { value: 'minutes', label: 'minutes' },
            { value: 'hours', label: 'hours' },
          ]}
          ariaLabel="Notice unit"
          size="md"
          className="w-[116px]"
        />
      </div>
    </Field>
  )
}

/** The event title with {{variable}} chips that insert at the cursor. */
function EventTitleField({ value, onChange, disabled }: { value: string; onChange: (v: string) => void; disabled?: boolean }) {
  const ref = useRef<HTMLInputElement>(null)
  const insert = (token: string) => {
    const el = ref.current
    const text = `{{${token}}}`
    const at = el?.selectionStart ?? value.length
    const end = el?.selectionEnd ?? at
    onChange(value.slice(0, at) + text + value.slice(end))
    requestAnimationFrame(() => {
      el?.focus()
      el?.setSelectionRange(at + text.length, at + text.length)
    })
  }
  return (
    <Field label="Event title">
      <input
        ref={ref}
        value={value}
        disabled={disabled}
        onChange={(e) => onChange(e.target.value)}
        placeholder="{{name}} — appointment"
        className={input}
      />
      {!disabled && (
        <div className="mt-1.5 flex flex-wrap items-center gap-1">
          <span className="mr-0.5 text-[10.5px] text-text-muted">Insert:</span>
          {EVENT_VARIABLES.map((t) => (
            <button
              key={t}
              type="button"
              onClick={() => insert(t)}
              title={`Insert {{${t}}} — the customer’s ${t}`}
              className="inline-flex cursor-pointer items-center rounded-full border border-line bg-fill px-2 py-0.5 font-mono text-[10.5px] text-text-mid transition-colors hover:border-line-strong hover:text-text-high"
            >
              {`{{${t}}}`}
            </button>
          ))}
        </div>
      )}
    </Field>
  )
}

/** The dry run: the free times the AI would offer with these (unsaved) settings. */
function AvailabilityPreview({ config }: { config: CalendarAppConfig }) {
  const [busy, setBusy] = useState(false)
  const [result, setResult] = useState<AppTestResponse | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [stale, setStale] = useState(false)
  const key = JSON.stringify(config)
  const ranFor = useRef<string | null>(null)
  useEffect(() => setStale(ranFor.current !== null && ranFor.current !== key), [key])

  const run = async () => {
    setBusy(true)
    setError(null)
    try {
      const r = await previewAvailability(config)
      setResult({ ...r, slots: Array.isArray(r.slots) ? r.slots : [] })
      ranFor.current = key
      setStale(false)
    } catch (e) {
      setResult(null)
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  // group by local date ("2026-10-14T15:00" → "2026-10-14"), keeping server order
  const days: { date: string; slots: AppTestResponse['slots'] }[] = []
  for (const s of result?.slots ?? []) {
    const date = (s.start || '').slice(0, 10)
    const g = days.find((d) => d.date === date)
    if (g) g.slots.push(s)
    else days.push({ date, slots: [s] })
  }

  return (
    <section className="min-w-0 rounded-lg border border-line bg-well p-3.5">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
        <div className="min-w-0 flex-1 basis-[240px]">
          <h3 className="eyebrow">Preview availability</h3>
          <p className="mt-0.5 text-[11.5px] text-text-muted">
            The free times the AI would offer right now with these settings — a dry run, nothing is booked.
          </p>
        </div>
        <Button variant="secondary" size="sm" onClick={run} disabled={busy}>
          {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <CalendarSearch size={13} aria-hidden />}
          {result ? 'Check again' : 'Preview availability'}
        </Button>
      </div>

      {error && (
        <div role="alert" className="mt-3 flex items-start gap-2 text-[12px] text-[color:var(--red-ink)]">
          <AlertCircle size={13} className="mt-0.5 shrink-0" aria-hidden /> <span className="min-w-0">{error}</span>
        </div>
      )}

      {result && (
        <div className={`mt-3 ${stale ? 'opacity-60' : ''}`}>
          <div className="mb-2 flex flex-wrap items-center gap-x-2 gap-y-1 text-[11.5px] text-text-muted">
            <Clock size={11} aria-hidden />
            {result.timezone && <span className="font-semibold text-text-high">{result.timezone}</span>}
            {result.source && <span>· {result.source === 'google' ? 'Google Calendar' : result.source === 'chatbranch' ? 'ChatBranch bookings' : result.source}</span>}
            {stale && <span className="text-[color:var(--amber-ink)]">· settings changed — check again</span>}
          </div>
          {!result.ok ? (
            <p className="text-[12.5px] text-[color:var(--amber-ink)]">{result.detail || 'The calendar could not be read.'}</p>
          ) : days.length === 0 ? (
            <p className="text-[12.5px] text-text-muted">{result.detail || 'No free times in the next few days with these settings.'}</p>
          ) : (
            <ul className="flex flex-col gap-2">
              {days.map((d) => (
                <li key={d.date} className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
                  <span className="w-[92px] shrink-0 text-[12px] font-semibold text-text-high">{dayLabel(d.date, d.slots[0]?.label)}</span>
                  <span className="flex min-w-0 flex-1 flex-wrap gap-1">
                    {d.slots.map((s) => (
                      <span
                        key={s.start}
                        title={s.label}
                        className="rounded-full border border-line-strong bg-card px-2 py-0.5 text-[11.5px] font-medium text-text-high"
                      >
                        {timeLabel(s)}
                      </span>
                    ))}
                  </span>
                </li>
              ))}
            </ul>
          )}
          {result.ok && days.length > 0 && result.detail && <p className="mt-2 text-[11.5px] text-text-muted">{result.detail}</p>}
        </div>
      )}
    </section>
  )
}

/** "Tue 14 Oct" from the date key (falls back to the label's own day part). */
function dayLabel(date: string, label?: string): string {
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(date)
  if (m) {
    const d = new Date(Number(m[1]), Number(m[2]) - 1, Number(m[3]))
    return d.toLocaleDateString('en-GB', { weekday: 'short', day: 'numeric', month: 'short' })
  }
  return label?.split(',')[0] ?? date
}

/** "3:00 PM" — the label's time part ("Tue 14 Oct, 3:00 PM"), else HH:MM from start. */
function timeLabel(s: { start: string; label: string }): string {
  const i = s.label?.lastIndexOf(', ') ?? -1
  if (i >= 0) return s.label.slice(i + 2)
  return s.start.slice(11, 16) || s.label
}
