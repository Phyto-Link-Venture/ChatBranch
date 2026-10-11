/**
 * The inbox filter model: what the filter modal edits, what a saved view
 * stores, and how it turns into GET /api/inbox/threads query parameters.
 * Everything is filtered on the SERVER — this file only describes the request.
 */
import type { MessageKey, TFunction } from '@/i18n'
import { channelName, statusLabel, type Channel, type ThreadStatus } from './inboxTypes'

export type DatePreset = '' | 'today' | '24h' | '7d' | '30d' | '90d' | 'custom'

export interface DateRange {
  preset: DatePreset
  /** custom range, local calendar dates (YYYY-MM-DD), both inclusive */
  from?: string
  to?: string
}

/** newest = last activity · customer_newest = the customer's last message (chats they
 *  never wrote in last) · oldest_waiting · unread — GET /api/inbox/threads `sort`. */
export type Sort = 'newest' | 'customer_newest' | 'oldest_waiting' | 'unread'
export type TriState = '' | 'yes' | 'no'

/** The two orders the list header's switch offers — remembered per person
 *  (`chats.sort` in /api/auth/preferences; SettingsEndpoints.ChatListSorts). */
export type HeaderSort = 'newest' | 'customer_newest'
export const isHeaderSort = (s: unknown): s is HeaderSort => s === 'newest' || s === 'customer_newest'

/**
 * A view (built-in tab or saved view) whose sort is the default `newest` follows
 * the person's chosen list order instead — the header switch is their order, a
 * view only overrides it when it was saved with a different one.
 */
export const withSortPref = (f: InboxFilters, pref: HeaderSort): InboxFilters =>
  f.sort === 'newest' && pref !== 'newest' ? { ...f, sort: pref } : f

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
    sort: r.sort === 'customer_newest' || r.sort === 'oldest_waiting' || r.sort === 'unread' ? r.sort : 'newest',
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

/** Labels are translation keys — render with `t(p.label)`. */
export const DATE_PRESETS: { value: DatePreset; label: MessageKey }[] = [
  { value: '', label: 'chats.dates.any' },
  { value: 'today', label: 'common.time.today' },
  { value: '24h', label: 'chats.dates.last24h' },
  { value: '7d', label: 'chats.dates.last7d' },
  { value: '30d', label: 'chats.dates.last30d' },
  { value: '90d', label: 'chats.dates.last90d' },
  { value: 'custom', label: 'chats.dates.custom' },
]

export const SORTS: { value: Sort; label: MessageKey }[] = [
  { value: 'newest', label: 'chats.sorts.newest' },
  { value: 'customer_newest', label: 'chats.sorts.customerNewest' },
  { value: 'oldest_waiting', label: 'chats.sorts.oldestWaiting' },
  { value: 'unread', label: 'chats.sorts.unread' },
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
/** `entities` = the top bar's ticked entities, used when the filters pick none. */
export function filtersToParams(f: InboxFilters, opts: { q?: string; entities?: string[] } = {}): URLSearchParams {
  const p = new URLSearchParams()
  const list = (k: string, v: string[]) => v.length && p.set(k, v.join(','))
  if (opts.q?.trim()) p.set('q', opts.q.trim())
  list('status', f.status)
  list('channel', f.channels)
  list('account', f.accounts)
  list('assignee', f.assignees)
  list('team', f.teams)
  list('entity', f.entities ?? opts.entities ?? [])
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

const HANDOVER_KIND_KEYS = new Set([
  'check_failed',
  'asked_for_human',
  'send_blocked',
  'booking_failed',
  'low_confidence',
  'rule',
  'manual',
  'unknown_customer',
  'quota',
])

/** One removable chip per active filter dimension (sort is not a filter). */
export function activeChips(f: InboxFilters, l: ChipLookups, t: TFunction): FilterChip[] {
  const joinNames = (xs: string[], max = 2) =>
    xs.length <= max ? xs.join(', ') : t('chats.chips.more', { list: xs.slice(0, max).join(', '), count: xs.length - max })
  const rangeLabel = (r: DateRange) =>
    r.preset === 'custom'
      ? t('chats.chips.customRange', { from: r.from || '…', to: r.to || '…' })
      : t(DATE_PRESETS.find((p) => p.value === r.preset)?.label ?? 'chats.dates.any')
  const chips: FilterChip[] = []
  const add = (key: string, label: string, clear: (f: InboxFilters) => InboxFilters) =>
    chips.push({ key, label, clear })
  if (f.status.length)
    add('status', t('chats.chips.status', { list: joinNames(f.status.map((s) => statusLabel(t, s))) }), (x) => ({ ...x, status: [] }))
  if (f.channels.length) add('channels', joinNames(f.channels.map((c) => channelName(t, c))), (x) => ({ ...x, channels: [] }))
  if (f.accounts.length)
    add(
      'accounts',
      t('chats.chips.via', { list: joinNames(f.accounts.map((a) => (a === 'none' ? t('chats.chips.noAccount') : l.accountName(a)))) }),
      (x) => ({ ...x, accounts: [] }),
    )
  if (f.assignees.length)
    add(
      'assignees',
      t('chats.chips.assignee', {
        list: joinNames(
          f.assignees.map((a) =>
            a === 'me' ? t('chats.chips.me') : a === 'unassigned' ? t('chats.chips.unassigned') : l.agentName(a),
          ),
        ),
      }),
      (x) => ({ ...x, assignees: [] }),
    )
  if (f.teams.length)
    add(
      'teams',
      t('chats.chips.team', { list: joinNames(f.teams.map((id) => (id === 'none' ? t('common.status.none') : l.teamName(id)))) }),
      (x) => ({ ...x, teams: [] }),
    )
  if (f.entities)
    add(
      'entities',
      f.entities.length ? t('chats.chips.entity', { list: joinNames(f.entities.map(l.entityName)) }) : t('chats.chips.allEntities'),
      (x) => ({ ...x, entities: null }),
    )
  if (f.labels.length)
    add(
      'labels',
      t(f.labelsMode === 'all' ? 'chats.chips.allOf' : 'chats.chips.label', { list: joinNames(f.labels.map(l.labelName)) }),
      (x) => ({ ...x, labels: [], labelsMode: 'any' }),
    )
  if (f.tags.length) add('tags', t('chats.chips.tag', { list: joinNames(f.tags) }), (x) => ({ ...x, tags: [] }))
  if (f.waiting) add('waiting', t('chats.chips.waiting'), (x) => ({ ...x, waiting: false }))
  if (f.ai) add('ai', t(f.ai === 'on' ? 'chats.chips.aiOn' : 'chats.chips.aiOff'), (x) => ({ ...x, ai: '' }))
  if (f.window)
    add('window', t(f.window === 'open' ? 'chats.chips.windowOpen' : 'chats.chips.windowClosed'), (x) => ({ ...x, window: '' }))
  if (f.optedOut)
    add('optedOut', t(f.optedOut === 'yes' ? 'chats.list.optedOut' : 'chats.chips.notOptedOut'), (x) => ({ ...x, optedOut: '' }))
  if (f.call) add('call', t(f.call === 'live' ? 'chats.chips.callLive' : 'chats.chips.hasCall'), (x) => ({ ...x, call: '' }))
  if (f.linked) add('linked', t(f.linked === 'yes' ? 'chats.chips.linked' : 'chats.chips.notLinked'), (x) => ({ ...x, linked: '' }))
  if (f.handover)
    add(
      'handover',
      t(
        f.handover === 'open'
          ? 'chats.chips.needsHuman'
          : f.handover === 'any'
            ? 'chats.chips.everHandedOver'
            : 'chats.chips.neverHandedOver',
      ),
      (x) => ({ ...x, handover: '' }),
    )
  if (f.handoverKinds.length)
    add(
      'handoverKinds',
      t('chats.chips.handover', {
        list: joinNames(
          f.handoverKinds.map((k) =>
            HANDOVER_KIND_KEYS.has(k) ? t(`chats.handoverKinds.${k}` as MessageKey) : k.replace(/_/g, ' '),
          ),
        ),
      }),
      (x) => ({ ...x, handoverKinds: [] }),
    )
  if (rangeActive(f.active)) add('active', t('chats.chips.active', { range: rangeLabel(f.active) }), (x) => ({ ...x, active: { preset: '' } }))
  if (rangeActive(f.created))
    add('created', t('chats.chips.created', { range: rangeLabel(f.created) }), (x) => ({ ...x, created: { preset: '' } }))
  return chips
}
