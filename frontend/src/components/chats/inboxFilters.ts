/**
 * The inbox filter model: what the filter modal edits, what a saved view
 * stores, and how it turns into GET /api/inbox/threads query parameters.
 * Everything is filtered on the SERVER — this file only describes the request.
 */
import { CHANNEL_META, statusLabel, type Channel, type ThreadStatus } from './inboxTypes'

export type DatePreset = '' | 'today' | '24h' | '7d' | '30d' | '90d' | 'custom'

export interface DateRange {
  preset: DatePreset
  /** custom range, local calendar dates (YYYY-MM-DD), both inclusive */
  from?: string
  to?: string
}

export type Sort = 'newest' | 'oldest_waiting' | 'unread'
export type TriState = '' | 'yes' | 'no'

export interface InboxFilters {
  status: ThreadStatus[]
  channels: Channel[]
  /** sending-account ids; 'none' = conversations with no account bound */
  accounts: string[]
  /** 'me' | 'unassigned' | usernames — empty = anyone */
  assignees: string[]
  /** team ids; 'none' = no owning team */
  teams: string[]
  /** entity slugs; null = follow the top-bar entity */
  entities: string[] | null
  labels: string[]
  labelsMode: 'any' | 'all'
  /** contact tags (any of) */
  tags: string[]
  ai: '' | 'on' | 'off'
  window: '' | 'open' | 'closed'
  optedOut: TriState
  waiting: boolean
  call: '' | 'any' | 'live'
  linked: TriState
  /** human handover: open = waiting for a person · any = ever handed over · none = never */
  handover: '' | 'open' | 'any' | 'none'
  /** handover kinds (any of) — check_failed, asked_for_human, … */
  handoverKinds: string[]
  active: DateRange
  created: DateRange
  sort: Sort
}

export const EMPTY_FILTERS: InboxFilters = {
  status: [],
  channels: [],
  accounts: [],
  assignees: [],
  teams: [],
  entities: null,
  labels: [],
  labelsMode: 'any',
  tags: [],
  ai: '',
  window: '',
  optedOut: '',
  waiting: false,
  call: '',
  linked: '',
  handover: '',
  handoverKinds: [],
  active: { preset: '' },
  created: { preset: '' },
  sort: 'newest',
}

/** Fill in any keys a saved view (possibly from an older build) lacks. */
export function normalizeFilters(raw: unknown): InboxFilters {
  const r = (raw && typeof raw === 'object' ? raw : {}) as Partial<InboxFilters>
  const arr = <T,>(v: unknown): T[] => (Array.isArray(v) ? (v as T[]) : [])
  const range = (v: unknown): DateRange =>
    v && typeof v === 'object' && 'preset' in (v as object) ? { ...(v as DateRange) } : { preset: '' }
  return {
    ...EMPTY_FILTERS,
    ...r,
    status: arr(r.status),
    channels: arr(r.channels),
    accounts: arr(r.accounts),
    assignees: arr(r.assignees),
    teams: arr(r.teams),
    entities: Array.isArray(r.entities) ? r.entities : null,
    labels: arr(r.labels),
    tags: arr(r.tags),
    handover: r.handover === 'open' || r.handover === 'any' || r.handover === 'none' ? r.handover : '',
    handoverKinds: arr(r.handoverKinds),
    labelsMode: r.labelsMode === 'all' ? 'all' : 'any',
    waiting: r.waiting === true,
    active: range(r.active),
    created: range(r.created),
    sort: r.sort === 'oldest_waiting' || r.sort === 'unread' ? r.sort : 'newest',
  }
}

/** Order-insensitive equality (saved view vs what's on screen). */
export function sameFilters(a: InboxFilters, b: InboxFilters): boolean {
  const canon = (f: InboxFilters) =>
    JSON.stringify(
      Object.fromEntries(
        Object.entries(f)
          .map(([k, v]) => [k, Array.isArray(v) ? [...v].sort() : v])
          .sort(([x], [y]) => String(x).localeCompare(String(y))),
      ),
    )
  return canon(a) === canon(b)
}

export const DATE_PRESETS: { value: DatePreset; label: string }[] = [
  { value: '', label: 'Any time' },
  { value: 'today', label: 'Today' },
  { value: '24h', label: 'Last 24 hours' },
  { value: '7d', label: 'Last 7 days' },
  { value: '30d', label: 'Last 30 days' },
  { value: '90d', label: 'Last 90 days' },
  { value: 'custom', label: 'Custom range…' },
]

export const SORTS: { value: Sort; label: string }[] = [
  { value: 'newest', label: 'Newest activity' },
  { value: 'oldest_waiting', label: 'Oldest waiting' },
  { value: 'unread', label: 'Unread first' },
]

/** [from, to) in ISO for a range; undefined bounds are open. */
export function rangeBounds(r: DateRange, now = new Date()): { from?: string; to?: string } {
  const hours = (h: number) => new Date(now.getTime() - h * 3_600_000).toISOString()
  const localDay = (ymd: string, addDays = 0) => {
    const [y, m, d] = ymd.split('-').map(Number)
    if (!y || !m || !d) return undefined
    return new Date(y, m - 1, d + addDays).toISOString()
  }
  switch (r.preset) {
    case 'today': {
      const start = new Date(now)
      start.setHours(0, 0, 0, 0)
      return { from: start.toISOString() }
    }
    case '24h':
      return { from: hours(24) }
    case '7d':
      return { from: hours(24 * 7) }
    case '30d':
      return { from: hours(24 * 30) }
    case '90d':
      return { from: hours(24 * 90) }
    case 'custom':
      return {
        from: r.from ? localDay(r.from) : undefined,
        // inclusive end date → exclusive bound at the next local midnight
        to: r.to ? localDay(r.to, 1) : undefined,
      }
    default:
      return {}
  }
}

const rangeActive = (r: DateRange) => r.preset !== '' && (r.preset !== 'custom' || !!r.from || !!r.to)

/** GET /api/inbox/threads parameters for these filters. */
export function filtersToParams(f: InboxFilters, opts: { q?: string; entity?: string | null } = {}): URLSearchParams {
  const p = new URLSearchParams()
  const list = (k: string, v: string[]) => v.length && p.set(k, v.join(','))
  if (opts.q?.trim()) p.set('q', opts.q.trim())
  list('status', f.status)
  list('channel', f.channels)
  list('account', f.accounts)
  list('assignee', f.assignees)
  list('team', f.teams)
  list('entity', f.entities ?? (opts.entity ? [opts.entity] : []))
  if (f.labels.length) {
    list('labels', f.labels)
    if (f.labelsMode === 'all') p.set('labels_mode', 'all')
  }
  list('tags', f.tags)
  if (f.ai) p.set('ai', f.ai)
  if (f.window) p.set('window', f.window)
  if (f.optedOut) p.set('opted_out', f.optedOut === 'yes' ? '1' : '0')
  if (f.waiting) p.set('waiting', '1')
  if (f.call) p.set('call', f.call)
  if (f.linked) p.set('linked', f.linked === 'yes' ? '1' : '0')
  if (f.handover) p.set('handover', f.handover)
  list('handover_kind', f.handoverKinds)
  if (rangeActive(f.active)) {
    const b = rangeBounds(f.active)
    if (b.from) p.set('active_from', b.from)
    if (b.to) p.set('active_to', b.to)
  }
  if (rangeActive(f.created)) {
    const b = rangeBounds(f.created)
    if (b.from) p.set('created_from', b.from)
    if (b.to) p.set('created_to', b.to)
  }
  if (f.sort !== 'newest') p.set('sort', f.sort)
  return p
}

export interface FilterChip {
  key: string
  label: string
  clear: (f: InboxFilters) => InboxFilters
}

export interface ChipLookups {
  accountName: (id: string) => string
  agentName: (username: string) => string
  teamName: (id: string) => string
  entityName: (slug: string) => string
  labelName: (slug: string) => string
}

const joinNames = (xs: string[], max = 2) =>
  xs.length <= max ? xs.join(', ') : `${xs.slice(0, max).join(', ')} +${xs.length - max}`

const rangeLabel = (r: DateRange) =>
  r.preset === 'custom'
    ? `${r.from || '…'} – ${r.to || '…'}`
    : (DATE_PRESETS.find((p) => p.value === r.preset)?.label ?? '').toLowerCase()

/** One removable chip per active filter dimension (sort is not a filter). */
export function activeChips(f: InboxFilters, l: ChipLookups): FilterChip[] {
  const chips: FilterChip[] = []
  const add = (key: string, label: string, clear: (f: InboxFilters) => InboxFilters) =>
    chips.push({ key, label, clear })
  if (f.status.length) add('status', `Status: ${joinNames(f.status.map(statusLabel))}`, (x) => ({ ...x, status: [] }))
  if (f.channels.length)
    add('channels', joinNames(f.channels.map((c) => CHANNEL_META[c]?.lbl ?? c)), (x) => ({ ...x, channels: [] }))
  if (f.accounts.length)
    add('accounts', `Via ${joinNames(f.accounts.map((a) => (a === 'none' ? 'no account' : l.accountName(a))))}`, (x) => ({
      ...x,
      accounts: [],
    }))
  if (f.assignees.length)
    add(
      'assignees',
      `Assignee: ${joinNames(f.assignees.map((a) => (a === 'me' ? 'me' : a === 'unassigned' ? 'unassigned' : l.agentName(a))))}`,
      (x) => ({ ...x, assignees: [] }),
    )
  if (f.teams.length)
    add('teams', `Team: ${joinNames(f.teams.map((t) => (t === 'none' ? 'none' : l.teamName(t))))}`, (x) => ({ ...x, teams: [] }))
  if (f.entities)
    add('entities', f.entities.length ? `Entity: ${joinNames(f.entities.map(l.entityName))}` : 'All entities', (x) => ({
      ...x,
      entities: null,
    }))
  if (f.labels.length)
    add(
      'labels',
      `${f.labelsMode === 'all' ? 'All of' : 'Label'}: ${joinNames(f.labels.map(l.labelName))}`,
      (x) => ({ ...x, labels: [], labelsMode: 'any' }),
    )
  if (f.tags.length) add('tags', `Tag: ${joinNames(f.tags)}`, (x) => ({ ...x, tags: [] }))
  if (f.waiting) add('waiting', 'Waiting for reply', (x) => ({ ...x, waiting: false }))
  if (f.ai) add('ai', f.ai === 'on' ? 'AI on' : 'AI off', (x) => ({ ...x, ai: '' }))
  if (f.window) add('window', f.window === 'open' ? '24h window open' : '24h window closed', (x) => ({ ...x, window: '' }))
  if (f.optedOut) add('optedOut', f.optedOut === 'yes' ? 'Opted out' : 'Not opted out', (x) => ({ ...x, optedOut: '' }))
  if (f.call) add('call', f.call === 'live' ? 'Call in progress' : 'Has a call', (x) => ({ ...x, call: '' }))
  if (f.linked) add('linked', f.linked === 'yes' ? 'Linked to a contact' : 'Not linked', (x) => ({ ...x, linked: '' }))
  if (f.handover)
    add(
      'handover',
      f.handover === 'open' ? 'Needs a human' : f.handover === 'any' ? 'Ever handed over' : 'Never handed over',
      (x) => ({ ...x, handover: '' }),
    )
  if (f.handoverKinds.length)
    add('handoverKinds', `Handover: ${joinNames(f.handoverKinds.map((k) => k.replace(/_/g, ' ')))}`, (x) => ({
      ...x,
      handoverKinds: [],
    }))
  if (rangeActive(f.active)) add('active', `Active ${rangeLabel(f.active)}`, (x) => ({ ...x, active: { preset: '' } }))
  if (rangeActive(f.created)) add('created', `Created ${rangeLabel(f.created)}`, (x) => ({ ...x, created: { preset: '' } }))
  return chips
}
