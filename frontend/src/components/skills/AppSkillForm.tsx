'use client'

import { useEffect, useRef, useState, type ReactNode } from 'react'
import Link from 'next/link'
import { AlertCircle, CalendarCheck, CalendarSearch, Clock, Info, Link2, Loader2, Plus, Video, X } from 'lucide-react'
import { Advanced } from '@/components/ui/Advanced'
import { Button } from '@/components/ui/Button'
import { ChannelLogo } from '@/components/ui/ChannelLogo'
import { Select, type SelectOption } from '@/components/ui/Select'
import { Toggle } from '@/components/ui/Toggle'
import {
  CALENDAR_DEFAULTS,
  WEEKDAYS,
  actionLabel,
  calendarTargetLabel,
  previewAvailability,
  sourceLabel,
  type AppAction,
  type AppCatalogEntry,
  type AppTestResponse,
  type CalendarAppConfig,
  type HoursWindow,
  type MeetingKind,
  type Weekday,
} from './skillsApi'
import { Trans, useFormat, useT, type MessageKey, type TFunction } from '@/i18n'

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

const ACTION_HELP: Record<AppAction, MessageKey> = {
  availability: 'ai.app.actionHelp.availability',
  book: 'ai.app.actionHelp.book',
  cancel: 'ai.app.actionHelp.cancel',
}

const EVENT_VARIABLES = ['name', 'phone', 'email', 'company'] as const
const EVENT_VARIABLE_LABEL: Record<(typeof EVENT_VARIABLES)[number], MessageKey> = {
  name: 'ai.app.vars.name',
  phone: 'ai.app.vars.phone',
  email: 'ai.app.vars.email',
  company: 'ai.app.vars.company',
}

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

/** One weekday's problem (a message key) — or null when its windows are fine. */
export function dayProblem(wins: HoursWindow[]): MessageKey | null {
  const spans = wins.map(([from, to]) => [toMin(from), toMin(to)] as const)
  if (spans.some(([from, to]) => !Number.isFinite(from) || !Number.isFinite(to))) return 'ai.app.problems.hhmm'
  if (spans.some(([from, to]) => from >= to)) return 'ai.app.problems.endAfterStart'
  const sorted = [...spans].sort((x, y) => x[0] - y[0])
  for (let i = 1; i < sorted.length; i++) if (sorted[i][0] < sorted[i - 1][1]) return 'ai.app.problems.overlap'
  return null
}

/** The whole config's first problem (shown in the wizard's alert), or null. */
export function calendarConfigProblem(c: CalendarAppConfig, t: TFunction): string | null {
  if (!c.actions.length) return t('ai.app.problems.pickAction')
  for (const d of WEEKDAYS) {
    const p = dayProblem(c.hours[d.key] ?? [])
    if (p) return t('ai.app.problems.day', { day: t(d.label), problem: t(p) })
  }
  if (!WEEKDAYS.some((d) => (c.hours[d.key] ?? []).length > 0)) return t('ai.app.problems.openDay')
  const int = (v: number) => Number.isInteger(v)
  if (!int(c.duration_minutes) || c.duration_minutes < 5 || c.duration_minutes > 480) return t('ai.app.problems.length')
  if (c.max_units > 1) {
    // one booking of N × unit — the default length must be one of them
    const unit = c.duration_unit_minutes
    if (c.duration_minutes % unit !== 0) return t('ai.app.problems.unitMultiple', { unit })
    if (c.duration_minutes > unit * c.max_units)
      return t('ai.app.problems.longest', { length: c.duration_minutes, longest: unit * c.max_units })
  }
  if (!int(c.slot_step_minutes) || c.slot_step_minutes < 5 || c.slot_step_minutes > 480) return t('ai.app.problems.step')
  if (!int(c.buffer_minutes) || c.buffer_minutes < 0 || c.buffer_minutes > 240) return t('ai.app.problems.buffer')
  if (!int(c.min_notice_minutes) || c.min_notice_minutes < 0 || c.min_notice_minutes > 60 * 24 * 30)
    return t('ai.app.problems.notice')
  if (!int(c.max_days_ahead) || c.max_days_ahead < 1 || c.max_days_ahead > 90) return t('ai.app.problems.ahead')
  if (!int(c.max_active_per_contact) || c.max_active_per_contact < 1 || c.max_active_per_contact > 20)
    return t('ai.app.problems.perCustomer')
  if (!c.event_title.trim()) return t('ai.app.problems.eventTitle')
  if (c.meeting === 'link' && !isMeetingLink(c.meeting_link)) return t('ai.app.problems.meetingLink')
  if (c.meeting === 'google_meet' && c.source !== 'google') return t('ai.app.problems.meetingMeet')
  if (c.meeting && c.meeting_message.trim() && !c.meeting_message.includes('{{link}}'))
    return t('ai.app.problems.meetingMessage')
  return null
}

/** A full https:// link (what the server accepts as a booking's own meeting link). */
export function isMeetingLink(raw: string): boolean {
  try {
    const u = new URL(raw.trim())
    return u.protocol === 'https:' && u.hostname.includes('.')
  } catch {
    return false
  }
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
  const t = useT()
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

  const followsGroup = t('ai.app.calendarOptions.followsGroup')
  const specificGroup = t('ai.app.calendarOptions.specificGroup')
  const calendarOptions: SelectOption[] = [
    {
      value: 'assignee',
      label: t('ai.app.calendarOptions.assignee'),
      hint: t('ai.app.calendarOptions.assigneeHint'),
      group: followsGroup,
    },
    {
      value: 'shared',
      label: t('ai.app.calendarOptions.shared'),
      hint: t('ai.app.calendarOptions.sharedHint'),
      group: followsGroup,
    },
    ...connections.map((x) => {
      const owner = t(
        x.owner === 'user'
          ? 'ai.app.calendarOptions.ownerUser'
          : x.owner === 'entity'
            ? 'ai.app.calendarOptions.ownerEntity'
            : 'ai.app.calendarOptions.ownerOrg',
      )
      const named = x.owner_name && !/^organi[sz]ation$/i.test(x.owner_name)
      return {
        value: String(x.id),
        label: x.label || t('ai.app.connectionN', { id: String(x.id) }),
        hint: named
          ? t('ai.app.calendarOptions.ownerNameHint', { owner, name: x.owner_name })
          : t('ai.app.calendarOptions.ownerHint', { owner }),
        group: specificGroup,
      }
    }),
    // a stored id that is no longer offered stays visible (the server re-validates it)
    ...(!['assignee', 'shared'].includes(c.calendar) && !connections.some((x) => String(x.id) === c.calendar)
      ? [
          {
            value: c.calendar,
            label: calendarTargetLabel(c.calendar, connections, t),
            hint: t('ai.app.calendarOptions.notAvailable'),
            group: specificGroup,
          },
        ]
      : []),
  ]

  // What sits under Advanced, counted when it differs from the app's defaults
  // (the server's when known) — those sections then open by themselves.
  const defs = { ...CALENDAR_DEFAULTS, ...app.defaults }
  const calendarCustom = [c.calendar !== defs.calendar, (c.calendar_id || 'primary') !== (defs.calendar_id || 'primary')].filter(
    Boolean,
  ).length
  const rulesCustom = [
    c.max_units > 1,
    c.max_units > 1 && c.duration_unit_minutes !== c.duration_minutes,
    c.slot_step_minutes !== c.duration_minutes && c.slot_step_minutes !== defs.slot_step_minutes,
    c.buffer_minutes !== defs.buffer_minutes,
    c.min_notice_minutes !== defs.min_notice_minutes,
    c.max_days_ahead !== defs.max_days_ahead,
    c.max_active_per_contact !== defs.max_active_per_contact,
    // an empty title is an error — never hide it
    !c.event_title.trim() || c.event_title !== defs.event_title,
    c.event_description !== defs.event_description,
  ].filter(Boolean).length

  return (
    <div className="flex flex-col gap-6">
      {/* ---- calendar source ---------------------------------------------------- */}
      <Section title={t('ai.app.calendar')} hint={t('ai.app.calendarHint')}>
        <div role="radiogroup" aria-label={t('ai.app.sourceAria')} className="grid grid-cols-1 gap-2 sm:grid-cols-2">
          <SourceCard
            on={c.source === 'google'}
            disabled={readOnly}
            onPick={() => set({ source: 'google' })}
            icon={<ChannelLogo channel="google_calendar" size={18} title="" />}
            title={sourceLabel('google', t, google?.label)}
            body={
              appsState === 'loading' ? (
                <span className="inline-flex items-center gap-1 text-text-muted">
                  <Loader2 size={11} className="animate-spin" aria-hidden /> {t('ai.app.checkingConnection')}
                </span>
              ) : appsState === 'offline' ? (
                <span className="text-text-muted">{t('ai.app.couldntCheck')}</span>
              ) : google?.connected ? (
                <span className="inline-flex items-center gap-1.5 text-[color:var(--green-ink)]">
                  <span className="h-1.5 w-1.5 rounded-full bg-green" aria-hidden /> {t('common.status.connected')}
                  {connections.length > 0 && (
                    <span className="text-text-muted">{t('ai.app.calendars', { count: connections.length })}</span>
                  )}
                </span>
              ) : (
                <span className="inline-flex flex-wrap items-center gap-1 text-[color:var(--amber-ink)]">
                  <span className="h-1.5 w-1.5 rounded-full bg-amber" aria-hidden />
                  <Trans
                    k="ai.app.notConnected"
                    values={{
                      link: (
                        <Link href="/integrations" className="font-semibold underline-offset-2 hover:underline">
                          {t('ai.app.connectInIntegrations')}
                        </Link>
                      ),
                    }}
                  />
                </span>
              )
            }
          />
          <SourceCard
            on={c.source === 'chatbranch'}
            disabled={readOnly}
            onPick={() => set({ source: 'chatbranch' })}
            icon={<CalendarCheck size={17} className="text-text-high" aria-hidden />}
            title={sourceLabel('chatbranch', t)}
            body={<span className="text-text-muted">{t('ai.app.chatbranchBody')}</span>}
          />
        </div>

        {c.source === 'google' && (
          <Advanced className="mt-3" count={calendarCustom} forceOpen={calendarCustom > 0}>
            <div className="grid grid-cols-1 gap-4 md:grid-cols-[minmax(0,1fr)_200px]">
              <Field label={t('ai.app.whoseCalendar')}>
                <Select
                  value={c.calendar}
                  disabled={readOnly}
                  onChange={(v) => set({ calendar: v })}
                  options={calendarOptions}
                  size="md"
                  ariaLabel={t('ai.app.whoseCalendar')}
                />
              </Field>
              <Field label={t('ai.app.calendarId')} hint={t('ai.app.calendarIdHint')}>
                <input
                  value={c.calendar_id}
                  disabled={readOnly}
                  onChange={(e) => set({ calendar_id: e.target.value })}
                  placeholder="primary"
                  className={input}
                />
              </Field>
            </div>
          </Advanced>
        )}
        {c.source === 'google' && c.calendar === 'assignee' && (
          <p className="mt-2 text-[11.5px] leading-snug text-text-muted">
            <Trans
              k="ai.app.assigneeNote"
              values={{
                link: (
                  <Link href="/settings/connections" className="font-semibold text-text-high hover:underline">
                    {t('ai.app.myConnections')}
                  </Link>
                ),
              }}
            />
          </p>
        )}
        {c.source === 'google' && appsState !== 'loading' && !google?.connected && (
          <Note tone="amber">{t('ai.app.untilConnected')}</Note>
        )}
      </Section>

      {/* ---- actions ---------------------------------------------------------- */}
      <Section title={t('ai.app.mayDo')}>
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
                    <span className="block text-[12.5px] font-semibold text-text-high">{actionLabel(a, t)}</span>
                    <span className="block text-[11.5px] leading-snug text-text-muted">
                      {ACTION_HELP[a.id] ? t(ACTION_HELP[a.id]) : ''}
                    </span>
                  </span>
                </label>
              </li>
            )
          })}
        </ul>
        <p className="mt-2 text-[11.5px] leading-relaxed text-text-muted">{t('ai.app.perReply')}</p>
      </Section>

      {/* ---- working hours ------------------------------------------------------ */}
      <Section
        title={t('ai.app.workingHours')}
        hint={t('ai.app.workingHoursHint')}
        right={
          <Select
            value={c.timezone}
            disabled={readOnly}
            onChange={(v) => set({ timezone: v })}
            searchable
            ariaLabel={t('ai.app.timezoneAria')}
            className="w-full min-w-[220px] sm:w-auto"
            options={[
              { value: '', label: t('ai.app.orgTimezone'), hint: orgTimezone ? `(${orgTimezone})` : undefined },
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
            const day = t(d.label)
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
                    label={open ? t('ai.app.dayOpen', { day }) : t('ai.app.dayClosed', { day })}
                  />
                  <span className={`min-w-0 truncate text-[12.5px] font-semibold ${open ? 'text-text-high' : 'text-text-muted'}`}>
                    {day}
                  </span>
                </div>
                {!open ? (
                  <span className="flex h-8 items-center text-[12px] text-text-muted">{t('ai.app.closed')}</span>
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
                            ariaLabel={t('ai.app.windowFrom', { day, n: wi + 1 })}
                            className="w-[92px]"
                          />
                          <span className="text-[11.5px] text-text-muted">–</span>
                          <Select
                            value={w[1]}
                            disabled={readOnly}
                            onChange={(v) => setDay(d.key, wins.map((x, j) => (j === wi ? [x[0], v] : x)))}
                            options={timeOptions(w[1])}
                            searchable
                            ariaLabel={t('ai.app.windowTo', { day, n: wi + 1 })}
                            className="w-[92px]"
                          />
                          {wins.length > 1 && !readOnly && (
                            <button
                              type="button"
                              onClick={() => setDay(d.key, wins.filter((_, j) => j !== wi))}
                              aria-label={t('ai.app.removeWindow', { day, n: wi + 1 })}
                              title={t('ai.app.removeWindowTitle')}
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
                          <Plus size={12} aria-hidden /> {t('ai.app.addBreak')}
                        </button>
                      )}
                    </div>
                    {problem && <span className="text-[11.5px] text-[color:var(--red-ink)]">{t(problem)}</span>}
                  </div>
                )}
              </div>
            )
          })}
        </div>
        <p className="mt-1.5 text-[11px] text-text-muted">{t('ai.app.hours24')}</p>
      </Section>

      {/* ---- appointment rules --------------------------------------------------- */}
      <Section title={t('ai.app.appointments')}>
        <div className="grid grid-cols-1 gap-x-4 gap-y-3 sm:grid-cols-2 lg:grid-cols-3">
          <NumField
            label={t('ai.app.length')}
            suffix={t('ai.units.min')}
            value={c.duration_minutes}
            min={5}
            max={480}
            disabled={readOnly}
            hint={c.max_units > 1 ? t('ai.app.lengthDefaultHint') : undefined}
            onChange={(v) =>
              set({
                duration_minutes: v,
                // a step that tracked the length keeps tracking it
                slot_step_minutes: c.slot_step_minutes === c.duration_minutes ? v : c.slot_step_minutes,
                // one fixed length: the unit IS the length (the server ignores it then)
                duration_unit_minutes:
                  c.max_units <= 1 && c.duration_unit_minutes === c.duration_minutes ? v : c.duration_unit_minutes,
              })
            }
          />
        </div>
        {/* the rest has working defaults; the event is part of it */}
        <Advanced className="mt-3" count={rulesCustom} forceOpen={rulesCustom > 0}>
          <div className="grid grid-cols-1 gap-x-4 gap-y-3 sm:grid-cols-2 lg:grid-cols-3">
            <LengthFields config={c} disabled={readOnly} onChange={set} />
            <NumField
              label={t('ai.app.startEvery')}
              suffix={t('ai.units.min')}
              value={c.slot_step_minutes}
              min={5}
              max={480}
              disabled={readOnly}
              hint={t('ai.app.startHint')}
              onChange={(v) => set({ slot_step_minutes: v })}
            />
            <NumField
              label={t('ai.app.buffer')}
              suffix={t('ai.units.min')}
              value={c.buffer_minutes}
              min={0}
              max={240}
              disabled={readOnly}
              onChange={(v) => set({ buffer_minutes: v })}
            />
            <NoticeField value={c.min_notice_minutes} disabled={readOnly} onChange={(v) => set({ min_notice_minutes: v })} />
            <NumField
              label={t('ai.app.bookUpTo')}
              suffix={t('ai.app.daysAhead')}
              value={c.max_days_ahead}
              min={1}
              max={90}
              disabled={readOnly}
              onChange={(v) => set({ max_days_ahead: v })}
            />
            <NumField
              label={t('ai.app.perCustomer')}
              value={c.max_active_per_contact}
              min={1}
              max={20}
              disabled={readOnly}
              hint={t('ai.app.perCustomerHint')}
              onChange={(v) => set({ max_active_per_contact: v })}
            />
          </div>
          <div className="border-t border-line pt-4">
            <h3 className="eyebrow mb-2.5">{t('ai.app.event')}</h3>
            <EventTitleField value={c.event_title} disabled={readOnly} onChange={(v) => set({ event_title: v })} />
            <div className="mt-3">
              <Field label={t('ai.app.eventNotes')} hint={t('ai.app.eventNotesHint')}>
                <textarea
                  rows={2}
                  value={c.event_description}
                  disabled={readOnly}
                  onChange={(e) => set({ event_description: e.target.value })}
                  placeholder={t('ai.app.eventNotesPlaceholder')}
                  className={`${input} resize-y leading-[1.55]`}
                />
              </Field>
            </div>
          </div>
        </Advanced>
      </Section>

      {/* ---- video meeting: a link sent with every booking ------------------------- */}
      <MeetingSection config={c} app={app} appsState={appsState} readOnly={readOnly} onChange={set} />

      <AvailabilityPreview config={c} />
    </div>
  )
}

/**
 * The video meeting each booking gets — a Zoom meeting of its own (a Zoom connection on the
 * Integrations page), a Google Meet on the Google event, or the same link every time — and
 * the message its link is sent in. ChatBranch sends the link right after the AI's
 * confirmation; the AI never writes one.
 */
function MeetingSection({
  config: c,
  app,
  appsState,
  readOnly,
  onChange,
}: {
  config: CalendarAppConfig
  app: AppCatalogEntry
  appsState: 'loading' | 'ok' | 'offline'
  readOnly: boolean
  onChange: (p: Partial<CalendarAppConfig>) => void
}) {
  const t = useT()
  const zoom = app.meetings?.find((m) => m.id === 'zoom')
  const options: SelectOption[] = [
    { value: '', label: t('ai.app.meeting.none'), hint: t('ai.app.meeting.noneHint') },
    { value: 'zoom', label: t('ai.app.meeting.zoom'), hint: t('ai.app.meeting.zoomHint') },
    {
      value: 'google_meet',
      label: t('ai.app.meeting.meet'),
      hint: c.source === 'google' ? t('ai.app.meeting.meetHint') : t('ai.app.meeting.meetNeedsGoogle'),
    },
    { value: 'link', label: t('ai.app.meeting.link'), hint: t('ai.app.meeting.linkHint') },
  ]
  const linkBad = c.meeting === 'link' && c.meeting_link.trim() !== '' && !isMeetingLink(c.meeting_link)
  const messageBad = c.meeting_message.trim() !== '' && !c.meeting_message.includes('{{link}}')
  return (
    <Section title={t('ai.app.meeting.title')} hint={t('ai.app.meeting.hint')}>
      <div data-tour="skill-meeting" className="flex flex-col gap-3">
        <Field label={t('ai.app.meeting.label')}>
          <Select
            value={c.meeting}
            disabled={readOnly}
            onChange={(v) => onChange({ meeting: v as MeetingKind })}
            options={options}
            size="md"
            ariaLabel={t('ai.app.meeting.label')}
            className="w-full sm:max-w-[360px]"
          />
        </Field>

        {c.meeting === 'zoom' &&
          appsState === 'ok' &&
          (zoom?.connected ? (
            <span className="inline-flex items-center gap-1.5 text-[12px] text-[color:var(--green-ink)]">
              <span className="h-1.5 w-1.5 rounded-full bg-green" aria-hidden /> {t('ai.app.meeting.zoomConnected')}
            </span>
          ) : (
            <Note tone="amber">
              <Trans
                k="ai.app.meeting.zoomNotConnected"
                values={{
                  link: (
                    <Link href="/integrations" className="font-semibold underline-offset-2 hover:underline">
                      {t('ai.app.meeting.connectZoom')}
                    </Link>
                  ),
                }}
              />
            </Note>
          ))}
        {c.meeting === 'google_meet' && c.source !== 'google' && <Note tone="amber">{t('ai.app.meeting.meetNotGoogle')}</Note>}

        {c.meeting === 'link' && (
          <Field label={t('ai.app.meeting.linkLabel')} hint={t('ai.app.meeting.linkFieldHint')}>
            <div className="relative">
              <Link2 size={13} className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-text-muted" aria-hidden />
              <input
                value={c.meeting_link}
                disabled={readOnly}
                onChange={(e) => onChange({ meeting_link: e.target.value })}
                placeholder="https://zoom.us/j/…"
                inputMode="url"
                aria-invalid={linkBad || undefined}
                className={`${input} pl-8 ${linkBad ? 'border-red-400' : ''}`}
              />
            </div>
            {linkBad && <span className="mt-1 block text-[11.5px] text-[color:var(--red-ink)]">{t('ai.app.problems.meetingLink')}</span>}
          </Field>
        )}

        {c.meeting && (
          <>
            <p className="flex items-start gap-1.5 text-[11.5px] leading-relaxed text-text-muted">
              <Video size={13} className="mt-px shrink-0" aria-hidden />
              <span>{t(c.meeting === 'zoom' ? 'ai.app.meeting.howZoom' : 'ai.app.meeting.how')}</span>
            </p>
            <Advanced count={c.meeting_message.trim() ? 1 : 0} forceOpen={messageBad}>
              <Field label={t('ai.app.meeting.message')} hint={t('ai.app.meeting.messageHint')}>
                <textarea
                  rows={2}
                  value={c.meeting_message}
                  disabled={readOnly}
                  onChange={(e) => onChange({ meeting_message: e.target.value })}
                  placeholder={t('ai.app.meeting.messagePlaceholder')}
                  aria-invalid={messageBad || undefined}
                  className={`${input} resize-y leading-[1.55]`}
                />
              </Field>
              {messageBad && (
                <span className="text-[11.5px] text-[color:var(--red-ink)]">{t('ai.app.problems.meetingMessage')}</span>
              )}
            </Advanced>
          </>
        )}
      </div>
    </Section>
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

const LENGTH_UNITS = [15, 30, 60]

/** "Length unit" + "Longest booking": a customer may book N × unit (N = 1 … max_units) as ONE
 *  appointment. max_units 1 = one fixed length (the unit is then that length). */
function LengthFields({
  config: c,
  disabled,
  onChange,
}: {
  config: CalendarAppConfig
  disabled?: boolean
  onChange: (patch: Partial<CalendarAppConfig>) => void
}) {
  const t = useT()
  const unit = c.duration_unit_minutes
  const units = LENGTH_UNITS.includes(unit) ? LENGTH_UNITS : [...LENGTH_UNITS, unit].sort((a, b) => a - b)
  const unitOptions: SelectOption[] = units.map((u) => ({ value: String(u), label: `${u} ${t('ai.units.min')}` }))
  const longestOptions: SelectOption[] = Array.from({ length: 16 }, (_, i) => i + 1).map((n) => ({
    value: String(n),
    label:
      n === 1
        ? t('ai.app.longestFixed', { length: c.duration_minutes })
        : t('ai.app.longestOption', { n, unit, total: n * unit }),
  }))
  return (
    <>
      <Field label={t('ai.app.lengthUnit')} hint={t('ai.app.lengthUnitHint')}>
        <Select
          value={String(unit)}
          disabled={disabled}
          onChange={(v) => onChange({ duration_unit_minutes: Number(v) })}
          options={unitOptions}
          size="md"
          ariaLabel={t('ai.app.lengthUnit')}
        />
      </Field>
      <Field label={t('ai.app.longest')} hint={t('ai.app.longestHint')}>
        <Select
          value={String(Math.max(1, c.max_units))}
          disabled={disabled}
          onChange={(v) => onChange({ max_units: Number(v) })}
          options={longestOptions}
          size="md"
          ariaLabel={t('ai.app.longest')}
        />
      </Field>
    </>
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
  const t = useT()
  const [unit, setUnit] = useState<'hours' | 'minutes'>(value % 60 === 0 && value > 0 ? 'hours' : 'minutes')
  const shown = unit === 'hours' ? value / 60 : value
  const [text, setText] = useState(String(shown))
  useEffect(() => setText(String(unit === 'hours' ? value / 60 : value)), [value, unit])
  return (
    <Field label={t('ai.app.notice')} hint={t('ai.app.noticeHint')}>
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
          aria-label={t('ai.app.notice')}
        />
        <Select
          value={unit}
          disabled={disabled}
          onChange={(u) => setUnit(u as 'hours' | 'minutes')}
          options={[
            { value: 'minutes', label: t('ai.app.minutes') },
            { value: 'hours', label: t('ai.app.hours') },
          ]}
          ariaLabel={t('ai.app.noticeUnit')}
          size="md"
          className="w-[116px]"
        />
      </div>
    </Field>
  )
}

/** The event title with {{variable}} chips that insert at the cursor. */
function EventTitleField({ value, onChange, disabled }: { value: string; onChange: (v: string) => void; disabled?: boolean }) {
  const t = useT()
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
    <Field label={t('ai.app.eventTitle')}>
      <input
        ref={ref}
        value={value}
        disabled={disabled}
        onChange={(e) => onChange(e.target.value)}
        placeholder={t('ai.app.eventTitlePlaceholder')}
        className={input}
      />
      {!disabled && (
        <div className="mt-1.5 flex flex-wrap items-center gap-1">
          <span className="mr-0.5 text-[10.5px] text-text-muted">{t('ai.app.insert')}</span>
          {EVENT_VARIABLES.map((v) => (
            <button
              key={v}
              type="button"
              onClick={() => insert(v)}
              title={t('ai.app.insertTitle', { token: `{{${v}}}`, field: t(EVENT_VARIABLE_LABEL[v]) })}
              className="inline-flex cursor-pointer items-center rounded-full border border-line bg-fill px-2 py-0.5 font-mono text-[10.5px] text-text-mid transition-colors hover:border-line-strong hover:text-text-high"
            >
              {`{{${v}}}`}
            </button>
          ))}
        </div>
      )}
    </Field>
  )
}

/** The dry run: the free times the AI would offer with these (unsaved) settings. */
function AvailabilityPreview({ config }: { config: CalendarAppConfig }) {
  const t = useT()
  const f = useFormat()
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
          <h3 className="eyebrow">{t('ai.app.previewTitle')}</h3>
          <p className="mt-0.5 text-[11.5px] text-text-muted">{t('ai.app.previewBody')}</p>
        </div>
        <Button variant="secondary" size="sm" onClick={run} disabled={busy}>
          {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <CalendarSearch size={13} aria-hidden />}
          {result ? t('ai.app.checkAgain') : t('ai.app.previewTitle')}
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
            {result.source && <span>· {sourceLabel(result.source, t)}</span>}
            {stale && <span className="text-[color:var(--amber-ink)]">{t('ai.app.settingsChanged')}</span>}
          </div>
          {!result.ok ? (
            <p className="text-[12.5px] text-[color:var(--amber-ink)]">{result.detail || t('ai.app.couldNotRead')}</p>
          ) : days.length === 0 ? (
            <p className="text-[12.5px] text-text-muted">{result.detail || t('ai.app.noFree')}</p>
          ) : (
            <ul className="flex flex-col gap-2">
              {days.map((d) => (
                <li key={d.date} className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
                  <span className="w-[150px] shrink-0 text-[12px] font-semibold text-text-high">
                    {dayLabel(d.date, d.slots[0]?.label, t, f)}
                  </span>
                  <span className="flex min-w-0 flex-1 flex-wrap gap-1">
                    {d.slots.map((s) => (
                      <span
                        key={s.start}
                        title={slotDate(s.start) ? f.dateTime(slotDate(s.start)) : s.label}
                        className="rounded-full border border-line-strong bg-card px-2 py-0.5 text-[11.5px] font-medium text-text-high"
                      >
                        {timeLabel(s, f)}
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

type Fmt = ReturnType<typeof useFormat>

/** "Tue, 14 Oct 2026" from the date key (falls back to the label's own day part). */
function dayLabel(date: string, label: string | undefined, t: TFunction, f: Fmt): string {
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(date)
  if (m) {
    const d = new Date(Number(m[1]), Number(m[2]) - 1, Number(m[3]))
    return t('ai.app.dayLabel', { weekday: f.weekday(d), date: f.date(d) })
  }
  return label?.split(',')[0] ?? date
}

/** A slot's local wall time ("2026-10-14T15:00", no offset) as a Date at that wall time, or null. */
function slotDate(start: string): Date | null {
  const m = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})/.exec(start || '')
  return m ? new Date(Number(m[1]), Number(m[2]) - 1, Number(m[3]), Number(m[4]), Number(m[5])) : null
}

/** "15:00" — the slot's time in the reader's format (the server label's time part when start can't be read). */
function timeLabel(s: { start: string; label: string }, f: Fmt): string {
  const d = slotDate(s.start)
  if (d) return f.time(d)
  const i = s.label?.lastIndexOf(', ') ?? -1
  return i >= 0 ? s.label.slice(i + 2) : s.label
}
