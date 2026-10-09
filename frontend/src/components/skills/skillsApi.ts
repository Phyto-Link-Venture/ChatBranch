import { authHeaders, expireSessionOn401 } from '@/lib/auth'

/**
 * Client for /api/aicore/skills — the SOLE writer of AgentSkill rows — and the
 * code-skill authoring endpoints (draft / test / builtins), plus the
 * connected-app catalog (/skills/apps) and the app dry run (/skills/test with
 * `kind: "app"`).
 *
 * The PUT replaces ONE scope's library (organization-wide, or one entity's):
 * rows of that scope missing from the payload are deleted. So every write here
 * re-reads the scope first and changes only the row it means to — a stale tab
 * can never prune someone else's skills.
 */

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

export type SkillKind = 'prompt' | 'tool' | 'check' | 'app'
export type OnFail = 'retry' | 'handover' | 'block'

// ---- connected-app skills (kind "app") ----------------------------------------

export type CalendarSource = 'google' | 'chatbranch'
export type AppAction = 'availability' | 'book' | 'cancel'
export type Weekday = 'mon' | 'tue' | 'wed' | 'thu' | 'fri' | 'sat' | 'sun'
/** One open window of a weekday, local "HH:MM" times — e.g. ["09:00", "12:00"]. */
export type HoursWindow = [string, string]

/**
 * An app skill's `config` (wire + stored, snake_case). Missing keys take the
 * server's defaults (`GET /skills/apps` → `defaults`); `calendar` is
 * "assignee" | "shared" | a google_calendar integration id as a string.
 */
export interface CalendarAppConfig {
  app: 'calendar'
  source: CalendarSource
  calendar: string
  calendar_id: string
  actions: AppAction[]
  /** "" = the organisation's timezone. */
  timezone: string
  hours: Record<Weekday, HoursWindow[]>
  duration_minutes: number
  slot_step_minutes: number
  buffer_minutes: number
  min_notice_minutes: number
  max_days_ahead: number
  max_active_per_contact: number
  event_title: string
  event_description: string
}

export type AppConfig = CalendarAppConfig

export const WEEKDAYS: { key: Weekday; short: string; label: string }[] = [
  { key: 'mon', short: 'Mon', label: 'Monday' },
  { key: 'tue', short: 'Tue', label: 'Tuesday' },
  { key: 'wed', short: 'Wed', label: 'Wednesday' },
  { key: 'thu', short: 'Thu', label: 'Thursday' },
  { key: 'fri', short: 'Fri', label: 'Friday' },
  { key: 'sat', short: 'Sat', label: 'Saturday' },
  { key: 'sun', short: 'Sun', label: 'Sunday' },
]

/** The spec's defaults — used when `/skills/apps` is unreachable, and to fill
 *  any key a stored config lacks. Hours: Mon–Fri 09:00–17:00. */
export const CALENDAR_DEFAULTS: CalendarAppConfig = {
  app: 'calendar',
  source: 'chatbranch',
  calendar: 'assignee',
  calendar_id: 'primary',
  actions: ['availability', 'book', 'cancel'],
  timezone: '',
  hours: {
    mon: [['09:00', '17:00']],
    tue: [['09:00', '17:00']],
    wed: [['09:00', '17:00']],
    thu: [['09:00', '17:00']],
    fri: [['09:00', '17:00']],
    sat: [],
    sun: [],
  },
  duration_minutes: 30,
  slot_step_minutes: 30,
  buffer_minutes: 0,
  min_notice_minutes: 120,
  max_days_ahead: 14,
  max_active_per_contact: 1,
  event_title: '{{name}} — appointment',
  event_description: '',
}

/** Prefilled "Use when" + "Instructions" of a new calendar skill. */
export const APP_USE_WHEN = 'The customer wants to book, reschedule or cancel an appointment'
export const APP_INSTRUCTIONS =
  'Offer two or three free times from the calendar — never invent one. Book only after the customer picks a ' +
  'specific time, then confirm it. Always restate the weekday, date, time and timezone. To reschedule, cancel the ' +
  'old booking and book the new time in the same reply. If the calendar is unavailable, say a teammate will ' +
  'confirm the appointment.'

const isObj = (v: unknown): v is Record<string, unknown> => !!v && typeof v === 'object' && !Array.isArray(v)

function pickDefined<T extends object>(o: T): Partial<T> {
  const out: Partial<T> = {}
  for (const [k, v] of Object.entries(o)) if (v !== undefined && v !== null) (out as Record<string, unknown>)[k] = v
  return out
}

/** A stored/partial config → a complete one (defaults for anything missing or malformed). */
export function calendarConfig(raw: unknown, defaults: Partial<CalendarAppConfig> = {}): CalendarAppConfig {
  const base: CalendarAppConfig = { ...CALENDAR_DEFAULTS, ...pickDefined(defaults) }
  const r = isObj(raw) ? raw : {}
  const has = (k: string) => r[k] !== undefined && r[k] !== null && r[k] !== ''
  const num = (k: keyof CalendarAppConfig) =>
    has(k) && Number.isFinite(Number(r[k])) ? Number(r[k]) : (base[k] as number)
  const str = (k: keyof CalendarAppConfig) => (typeof r[k] === 'string' ? (r[k] as string) : (base[k] as string))
  const hoursRaw = isObj(r.hours) ? r.hours : null
  const baseHours = isObj(base.hours) ? (base.hours as Record<string, unknown>) : {}
  const hours = {} as Record<Weekday, HoursWindow[]>
  for (const { key } of WEEKDAYS) {
    // a stored schedule without this day = closed; no schedule at all = defaults
    const day = hoursRaw ? hoursRaw[key] : baseHours[key]
    hours[key] = Array.isArray(day)
      ? day
          .filter((w): w is unknown[] => Array.isArray(w) && w.length >= 2)
          .map((w) => [String(w[0]), String(w[1])] as HoursWindow)
      : []
  }
  const actions = Array.isArray(r.actions)
    ? (r.actions.filter((a) => a === 'availability' || a === 'book' || a === 'cancel') as AppAction[])
    : [...base.actions]
  return {
    app: 'calendar',
    source: r.source === 'google' || r.source === 'chatbranch' ? r.source : base.source,
    calendar: has('calendar') ? String(r.calendar) : base.calendar,
    calendar_id: str('calendar_id') || 'primary',
    actions,
    timezone: str('timezone'),
    hours,
    duration_minutes: num('duration_minutes'),
    slot_step_minutes: num('slot_step_minutes'),
    buffer_minutes: num('buffer_minutes'),
    min_notice_minutes: num('min_notice_minutes'),
    max_days_ahead: num('max_days_ahead'),
    max_active_per_contact: num('max_active_per_contact'),
    event_title: str('event_title'),
    event_description: str('event_description'),
  }
}

/** One Google Calendar grant a skill may name explicitly. */
export interface AppConnection {
  id: number | string
  label: string
  owner: 'user' | 'entity' | 'org'
  owner_name: string
}

export interface AppSourceInfo {
  source: CalendarSource
  label: string
  connected: boolean
  connections?: AppConnection[]
}

/** GET /api/aicore/skills/apps — the connected apps a skill can use. */
export interface AppCatalogEntry {
  app: 'calendar'
  label: string
  sources: AppSourceInfo[]
  actions: { id: AppAction; label: string }[]
  defaults: Partial<CalendarAppConfig>
}

export interface AppsPayload {
  apps: AppCatalogEntry[]
}

/** What the page shows when /skills/apps can't be reached: Google's status is
 *  unknown (reported as not connected); ChatBranch bookings always work. */
export const FALLBACK_CALENDAR_APP: AppCatalogEntry = {
  app: 'calendar',
  label: 'Calendar booking',
  sources: [
    { source: 'google', label: 'Google Calendar', connected: false, connections: [] },
    { source: 'chatbranch', label: 'ChatBranch bookings', connected: true },
  ],
  actions: [
    { id: 'availability', label: 'Check availability' },
    { id: 'book', label: 'Book appointments' },
    { id: 'cancel', label: 'Cancel / reschedule' },
  ],
  defaults: {},
}

/** The calendar app from a catalog payload (or the offline fallback). */
export function calendarApp(apps: AppCatalogEntry[] | null | undefined): AppCatalogEntry {
  return apps?.find((a) => a.app === 'calendar') ?? FALLBACK_CALENDAR_APP
}

/** One free slot from the dry-run preview: local times + a human label. */
export interface AppSlot {
  start: string
  end: string
  label: string
}

/** POST /skills/test with `{kind:"app", config, app_action:"availability"}` — a DRY RUN, never books. */
export interface AppTestResponse {
  ok: boolean
  slots: AppSlot[]
  timezone: string
  source: string
  detail: string
}

/** "Assignee's calendar" / "Shared calendar" / the named connection's label. */
export function calendarTargetLabel(calendar: string, connections: AppConnection[] = []): string {
  if (calendar === 'assignee') return 'Assignee’s calendar'
  if (calendar === 'shared') return 'Shared calendar'
  const c = connections.find((x) => String(x.id) === String(calendar))
  return c ? c.label || c.owner_name || `Connection #${c.id}` : `Connection #${calendar}`
}

/** The skills list's subtitle: "Connected app · Google Calendar (Assignee's calendar)". */
export function appSkillSubtitle(config: unknown, apps: AppCatalogEntry[] | null): string {
  const cfg = calendarConfig(config)
  const src = calendarApp(apps).sources.find((s) => s.source === cfg.source)
  const srcLabel = src?.label ?? (cfg.source === 'google' ? 'Google Calendar' : 'ChatBranch bookings')
  return cfg.source === 'google'
    ? `Connected app · ${srcLabel} (${calendarTargetLabel(cfg.calendar, src?.connections)})`
    : `Connected app · ${srcLabel}`
}

export interface Skill {
  id: string
  name: string
  useWhen: string
  instructions: string
  position: number
  kind: SkillKind
  description: string
  code: string
  parameters: unknown | null
  tests: unknown[]
  enabled: boolean
  channels: string[]
  onFail: OnFail
  updatedAt?: string | null
  tool_name?: string | null
  /** Connected-app settings (kind "app"); {} or absent for the other kinds. */
  config?: Partial<AppConfig> | Record<string, unknown> | null
  /** Owning entity slug; null = organization-wide (inherited by every entity). */
  entity: string | null
  entity_name: string
  scope: 'global' | 'entity'
}

export interface Builtin {
  name: string
  label: string
  description: string
  parameters: unknown
  example_args: unknown
  example: unknown
}

export interface BuiltinsPayload {
  timezone: string
  now: string
  builtins: Builtin[]
}

export interface DraftSkill {
  name: string
  kind: SkillKind
  use_when: string
  description: string
  parameters: unknown
  code: string
  tests: unknown[]
  instructions: string
  on_fail: OnFail
}

export interface DraftResponse {
  status: 'questions' | 'draft'
  questions: string[]
  source: 'ai' | 'template'
  note: string
  skill: DraftSkill | null
}

export interface TestResult {
  name: string
  pass: boolean
  got: unknown
  expected: unknown
  error: string | null
  ms: number
}

export interface TestResponse {
  valid: boolean
  error: string | null
  total: number
  passed: number
  failed: number
  results: TestResult[]
}

export async function aicore<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}/aicore/${path}`, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...authHeaders(), ...(init?.headers ?? {}) },
  })
  if (expireSessionOn401(res)) throw new Error('Session expired — redirecting to login…')
  const data = await res.json().catch(() => ({}))
  if (!res.ok) throw new Error((data as { detail?: string }).detail || `HTTP ${res.status}`)
  return data as T
}

/** The connected-app catalog; null when it can't be reached (callers fall back
 *  to FALLBACK_CALENDAR_APP — the page never breaks over it). */
export async function fetchApps(): Promise<AppCatalogEntry[] | null> {
  try {
    const d = await aicore<AppsPayload>('skills/apps')
    return Array.isArray(d.apps) ? d.apps : null
  } catch {
    return null
  }
}

/** Dry-run the availability tool with an unsaved config — never books. */
export function previewAvailability(config: AppConfig, range: { from_date?: string; to_date?: string } = {}) {
  return aicore<AppTestResponse>('skills/test', {
    method: 'POST',
    body: JSON.stringify({ kind: 'app', config, app_action: 'availability', ...range }),
  })
}

/** The wire shape PUT accepts (every field — absent fields would be kept). */
export function toWire(s: Skill) {
  return {
    id: s.id,
    name: s.name,
    useWhen: s.useWhen,
    instructions: s.instructions,
    position: s.position,
    kind: s.kind,
    description: s.description,
    code: s.code,
    parameters: (s.parameters ?? null) as unknown,
    tests: s.tests ?? [],
    enabled: s.enabled,
    channels: s.channels ?? [],
    onFail: s.onFail,
    // Only sent when the row has one — the PUT keeps a config it isn't sent,
    // and a re-read app skill must carry its settings back unchanged.
    ...(s.config && typeof s.config === 'object' ? { config: s.config as Record<string, unknown> } : {}),
  }
}

export type WireSkill = ReturnType<typeof toWire>

const scopeQuery = (scope: string | null) => (scope ? `?entity=${encodeURIComponent(scope)}` : '')

/** One scope's OWN rows, freshly read. */
export async function readScope(scope: string | null): Promise<Skill[]> {
  const d = await aicore<{ skills: Skill[] }>(`skills${scopeQuery(scope)}`)
  return d.skills.filter((s) => (s.entity ?? null) === scope).sort((a, b) => a.position - b.position)
}

/** Re-read a scope, apply `mutate` to its own rows, write it back. */
export async function writeScope(scope: string | null, mutate: (rows: WireSkill[]) => WireSkill[]) {
  const own = (await readScope(scope)).map(toWire)
  await aicore('skills', { method: 'PUT', body: JSON.stringify({ entity: scope, skills: mutate(own) }) })
}

/** Insert or replace one skill in `scope`; when it moved, remove it from `from`. */
export async function saveSkill(skill: WireSkill, scope: string | null, from?: string | null) {
  await writeScope(scope, (rows) =>
    rows.some((r) => r.id === skill.id) ? rows.map((r) => (r.id === skill.id ? skill : r)) : [...rows, skill],
  )
  if (from !== undefined && from !== scope) await writeScope(from, (rows) => rows.filter((r) => r.id !== skill.id))
}

export async function deleteSkill(id: string, scope: string | null) {
  await writeScope(scope, (rows) => rows.filter((r) => r.id !== id))
}

export const newSkillId = () => `as-${Math.random().toString(36).slice(2, 9)}`

export const CHANNEL_OPTIONS = [
  { value: 'whatsapp', label: 'WhatsApp' },
  { value: 'email', label: 'Email' },
  { value: 'telegram', label: 'Telegram' },
  { value: 'facebook', label: 'Messenger' },
  { value: 'instagram', label: 'Instagram' },
  { value: 'website', label: 'Website widget' },
]

export const KIND_META: Record<SkillKind, { label: string; plural: string; blurb: string }> = {
  prompt: {
    label: 'Instruction',
    plural: 'Instructions',
    blurb: 'Plain-language guidance the AI follows when its trigger applies.',
  },
  tool: {
    label: 'Tool',
    plural: 'Tools',
    blurb: 'JavaScript the AI calls for an exact answer — hours, dates, lookups, validations.',
  },
  check: {
    label: 'Reply check',
    plural: 'Reply checks',
    blurb: 'JavaScript that validates every AI reply before it is sent.',
  },
  app: {
    label: 'Connected app',
    plural: 'Connected apps',
    blurb: 'Let the AI use a connected app — e.g. check your calendar and book appointments.',
  },
}

export const ON_FAIL_OPTIONS: { value: OnFail; label: string; hint: string }[] = [
  { value: 'retry', label: 'Rewrite, then hand over', hint: 'the AI rewrites up to 2×; still failing → a human answers' },
  { value: 'handover', label: 'Hand over at once', hint: 'no rewrite — the chat is marked pending for a human' },
  { value: 'block', label: 'Rewrite, then send nothing', hint: 'the AI rewrites up to 2×; still failing → nothing is sent' },
]

export const pretty = (v: unknown) => {
  if (v === undefined) return ''
  try {
    return JSON.stringify(v, null, 2)
  } catch {
    return String(v)
  }
}
