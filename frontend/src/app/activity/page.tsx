'use client'

import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import {
  Activity,
  Check,
  ChevronLeft,
  ChevronRight,
  Copy,
  Download,
  Loader2,
  RefreshCw,
  Search,
  SlidersHorizontal,
  X,
} from 'lucide-react'
import { authHeaders, expireSessionOn401 } from '@/lib/auth'
import {
  SOURCES,
  sourceMeta,
  statusTone,
  type AuditEntry,
  type AuditFacets,
  type AuditPage,
} from '@/components/activity/audit'
import { Button } from '@/components/ui/Button'
import { PageHeader } from '@/components/ui/PageHeader'
import { Select } from '@/components/ui/Select'
import { EntityPicker } from '@/components/ui/EntityPicker'
import { useFormat, useT, type MessageKey } from '@/i18n'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

/**
 * Activity — the audit trail of everything that happened: people in the
 * dashboard, scripts on API keys, AI apps over MCP, the in-app assistant,
 * automations, flows, webhooks and background work. Server-side filtering and
 * paging over GET /api/audit; filters live in the URL so a filtered view can be
 * shared ("here's what happened to that chat"). Admins see their tenant;
 * everyone else sees their own rows.
 */

type Preset = '24h' | '7d' | '30d' | '90d' | 'all' | 'custom'

const PRESETS: { id: Preset; label: MessageKey; ms?: number }[] = [
  { id: '24h', label: 'review.activity.presets.h24', ms: 864e5 },
  { id: '7d', label: 'review.activity.presets.d7', ms: 7 * 864e5 },
  { id: '30d', label: 'review.activity.presets.d30', ms: 30 * 864e5 },
  { id: '90d', label: 'review.activity.presets.d90', ms: 90 * 864e5 },
  { id: 'all', label: 'review.activity.presets.all' },
  { id: 'custom', label: 'review.activity.presets.custom' },
]

const PAGE_SIZES = [25, 50, 100]

const inputCls =
  'h-8 w-full rounded-lg border border-line-strong bg-card px-2.5 text-[12.5px] text-text-primary outline-none transition-colors placeholder:text-text-faint focus:border-text-muted'

interface Filters {
  preset: Preset
  from: string
  to: string
  actor: string
  sources: string[]
  action: string
  entity: string | null
  q: string
}

const EMPTY: Filters = { preset: '7d', from: '', to: '', actor: '', sources: [], action: '', entity: null, q: '' }

function readUrl(): { filters: Filters; page: number; size: number } {
  if (typeof window === 'undefined') return { filters: EMPTY, page: 1, size: 50 }
  const p = new URLSearchParams(window.location.search)
  const preset = (p.get('range') as Preset) || '7d'
  return {
    filters: {
      preset: PRESETS.some((x) => x.id === preset) ? preset : '7d',
      from: p.get('from') || '',
      to: p.get('to') || '',
      actor: p.get('actor') || '',
      sources: (p.get('source') || '').split(',').filter(Boolean),
      action: p.get('action') || '',
      entity: p.get('entity') || null,
      q: p.get('q') || '',
    },
    page: Math.max(1, Number(p.get('page')) || 1),
    size: PAGE_SIZES.includes(Number(p.get('size'))) ? Number(p.get('size')) : 50,
  }
}

/** Filters → the API query (dates resolved in the viewer's own timezone). */
function apiQuery(f: Filters): URLSearchParams {
  const p = new URLSearchParams()
  const preset = PRESETS.find((x) => x.id === f.preset)
  if (preset?.ms) p.set('from', new Date(Date.now() - preset.ms).toISOString())
  if (f.preset === 'custom') {
    if (f.from) p.set('from', new Date(`${f.from}T00:00:00`).toISOString())
    if (f.to) {
      const end = new Date(`${f.to}T00:00:00`)
      end.setDate(end.getDate() + 1)
      p.set('to', end.toISOString())
    }
  }
  if (f.actor) p.set('actor', f.actor)
  if (f.sources.length) p.set('source', f.sources.join(','))
  if (f.action) p.set('action', f.action)
  if (f.entity) p.set('entity', f.entity)
  if (f.q.trim()) p.set('q', f.q.trim())
  return p
}

function urlQuery(f: Filters, page: number, size: number): string {
  const p = new URLSearchParams()
  if (f.preset !== '7d') p.set('range', f.preset)
  if (f.preset === 'custom') {
    if (f.from) p.set('from', f.from)
    if (f.to) p.set('to', f.to)
  }
  if (f.actor) p.set('actor', f.actor)
  if (f.sources.length) p.set('source', f.sources.join(','))
  if (f.action) p.set('action', f.action)
  if (f.entity) p.set('entity', f.entity)
  if (f.q.trim()) p.set('q', f.q.trim())
  if (page > 1) p.set('page', String(page))
  if (size !== 50) p.set('size', String(size))
  const s = p.toString()
  return s ? `?${s}` : ''
}

/** "5 minutes ago" within a week, else the date and time — in the UI language. */
function useAgo() {
  const f = useFormat()
  return (iso: string) => {
    const s = (Date.now() - new Date(iso).getTime()) / 1000
    return s < 7 * 86400 ? f.relative(iso) : f.dateTime(iso)
  }
}

function SourceBadge({ source }: { source: string }) {
  const t = useT()
  const m = sourceMeta(source)
  return (
    <span
      title={t(m.hint)}
      className={`inline-flex items-center gap-1 whitespace-nowrap rounded-full px-2 py-0.5 text-[10.5px] font-semibold ${m.cls}`}
    >
      <span className={`h-[5px] w-[5px] rounded-full ${m.dot}`} aria-hidden />
      {t(m.label)}
    </span>
  )
}

function Status({ status }: { status: number | null }) {
  return <span className={`font-mono text-[11px] tabular-nums ${statusTone(status)}`}>{status ?? '—'}</span>
}

export default function ActivityPage() {
  const t = useT()
  const f = useFormat()
  const ago = useAgo()
  // the loader reads the latest t without re-running on a language switch
  const tRef = useRef(t)
  tRef.current = t
  const [filters, setFilters] = useState<Filters>(EMPTY)
  const [page, setPage] = useState(1)
  const [size, setSize] = useState(50)
  const [ready, setReady] = useState(false)
  const [searchDraft, setSearchDraft] = useState('')

  const [data, setData] = useState<AuditPage | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [facets, setFacets] = useState<AuditFacets | null>(null)
  const [selected, setSelected] = useState<AuditEntry | null>(null)
  const [exporting, setExporting] = useState(false)
  const [reloadTick, setReloadTick] = useState(0)
  const [moreFilters, setMoreFilters] = useState(false) // phones: actor/action/entity/source fold away
  const seq = useRef(0)

  // Filters come from the URL so a filtered view survives refresh and can be shared.
  useEffect(() => {
    const u = readUrl()
    setFilters(u.filters)
    setSearchDraft(u.filters.q)
    setPage(u.page)
    setSize(u.size)
    setReady(true)
  }, [])

  const update = useCallback((patch: Partial<Filters>) => {
    setFilters((f) => ({ ...f, ...patch }))
    setPage(1)
  }, [])

  // debounce the free-text search
  useEffect(() => {
    if (!ready) return
    const timer = window.setTimeout(() => {
      if (searchDraft !== filters.q) update({ q: searchDraft })
    }, 350)
    return () => window.clearTimeout(timer)
  }, [searchDraft, filters.q, ready, update])

  const query = useMemo(() => apiQuery(filters), [filters])

  useEffect(() => {
    if (!ready) return
    window.history.replaceState(null, '', `${window.location.pathname}${urlQuery(filters, page, size)}`)
    const id = ++seq.current
    setLoading(true)
    const p = new URLSearchParams(query)
    p.set('page', String(page))
    p.set('page_size', String(size))
    fetch(`${API}/audit?${p}`, { headers: authHeaders() })
      .then(async (r) => {
        if (expireSessionOn401(r)) throw new Error(tRef.current('common.errors.sessionExpired'))
        const d = await r.json().catch(() => ({}))
        if (!r.ok) throw new Error((d as { detail?: string }).detail || `HTTP ${r.status}`)
        return d as AuditPage
      })
      .then((d) => {
        if (id !== seq.current) return
        setData(d)
        setError(null)
        if (d.page !== page) setPage(d.page)
      })
      .catch((e) => {
        if (id !== seq.current) return
        setError(e instanceof Error ? e.message : String(e))
      })
      .finally(() => {
        if (id === seq.current) setLoading(false)
      })
  }, [query, page, size, ready, reloadTick, filters])

  useEffect(() => {
    fetch(`${API}/audit/facets`, { headers: authHeaders() })
      .then((r) => (r.ok ? r.json() : null))
      .then((d) => d && setFacets(d))
      .catch(() => {})
  }, [reloadTick])

  const exportCsv = async () => {
    setExporting(true)
    try {
      const r = await fetch(`${API}/audit.csv?${query}`, { headers: authHeaders() })
      if (!r.ok) throw new Error(`HTTP ${r.status}`)
      const blob = await r.blob()
      const a = document.createElement('a')
      a.href = URL.createObjectURL(blob)
      a.download = `activity-${new Date().toISOString().slice(0, 10)}.csv`
      a.click()
      URL.revokeObjectURL(a.href)
    } catch (e) {
      setError(t('review.activity.exportFailed', { error: e instanceof Error ? e.message : String(e) }))
    } finally {
      setExporting(false)
    }
  }

  const sourceCount = (s: string) => facets?.sources.find((x) => x.value === s)?.count ?? 0
  const actorOptions = useMemo(
    () => [
      { value: '', label: t('review.activity.anyone') },
      ...(facets?.actors ?? []).map((a) => ({
        value: a.value,
        label: a.value || t('review.activity.none'),
        hint: f.number(a.count),
      })),
    ],
    [facets, t, f],
  )
  const actionOptions = useMemo(() => {
    const groups = new Map<string, number>()
    for (const a of facets?.actions ?? []) {
      const dot = a.value.indexOf('.')
      if (dot > 0 && !a.value.includes(' ')) {
        const g = a.value.slice(0, dot)
        groups.set(g, (groups.get(g) ?? 0) + a.count)
      }
    }
    return [
      { value: '', label: t('review.activity.anyAction') },
      ...Array.from(groups.entries())
        .filter(([, n]) => n > 0)
        .map(([g, n]) => ({ value: `${g}.*`, label: `${g}.*`, hint: f.number(n), group: t('review.activity.groups') })),
      ...(facets?.actions ?? []).map((a) => ({
        value: a.value,
        label: a.value,
        hint: f.number(a.count),
        group: a.value.includes(' ') ? t('review.activity.apiRequests') : t('review.activity.actions'),
      })),
    ]
  }, [facets, t, f])

  const active =
    filters.actor || filters.sources.length || filters.action || filters.entity || filters.q || filters.preset !== '7d'

  const advancedCount =
    (filters.actor ? 1 : 0) + (filters.action ? 1 : 0) + (filters.entity ? 1 : 0) + filters.sources.length

  const toggleSource = (s: string) =>
    update({ sources: filters.sources.includes(s) ? filters.sources.filter((x) => x !== s) : [...filters.sources, s] })

  return (
    <div className="flex flex-col gap-4">
      <PageHeader
        eyebrow={
          <span className="inline-flex items-center gap-1.5">
            <Activity size={12} strokeWidth={2.2} aria-hidden /> {t('shell.groups.review')}
          </span>
        }
        title={t('shell.nav.activity')}
        subtitle={t('review.activity.subtitle')}
        actions={
          <>
            <Button
              variant="ghost"
              size="sm"
              onClick={() => setReloadTick((n) => n + 1)}
              aria-label={t('common.actions.refresh')}
            >
              <RefreshCw size={13} className={loading ? 'animate-spin' : ''} aria-hidden /> {t('common.actions.refresh')}
            </Button>
            <Button data-tour="activity-export" variant="secondary" size="sm" onClick={exportCsv} disabled={exporting || !data?.total}>
              {exporting ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Download size={13} aria-hidden />}
              {t('review.activity.exportCsv')}
            </Button>
          </>
        }
      />

      {/* ---- filters ------------------------------------------------------ */}
      <section data-tour="activity-filters" className="flex flex-col gap-3 rounded-xl border border-line bg-card p-3 sm:p-4" aria-label={t('review.activity.filters')}>
        <div className="flex flex-wrap items-center gap-2">
          <div data-tour="activity-range" className="flex flex-wrap gap-1 rounded-lg bg-well p-0.5" role="group" aria-label={t('review.activity.timeRange')}>
            {PRESETS.map((p) => (
              <button
                key={p.id}
                type="button"
                onClick={() => update({ preset: p.id })}
                aria-pressed={filters.preset === p.id}
                className={`h-7 rounded-md px-2.5 text-[12px] font-semibold transition-colors ${
                  filters.preset === p.id
                    ? 'bg-card text-text-heading shadow-[0_0_0_1px_var(--line-strong)]'
                    : 'text-text-muted hover:text-text-high'
                }`}
              >
                {t(p.label)}
              </button>
            ))}
          </div>
          {filters.preset === 'custom' && (
            <div className="flex flex-wrap items-center gap-1.5 text-[12px] text-text-muted">
              <input
                type="date"
                value={filters.from}
                max={filters.to || undefined}
                onChange={(e) => update({ from: e.target.value })}
                className={`${inputCls} w-[150px]`}
                aria-label={t('notifications.inbox.fromDate')}
              />
              <span>{t('notifications.inbox.to')}</span>
              <input
                type="date"
                value={filters.to}
                min={filters.from || undefined}
                onChange={(e) => update({ to: e.target.value })}
                className={`${inputCls} w-[150px]`}
                aria-label={t('notifications.inbox.toDate')}
              />
            </div>
          )}
          <label className="relative ml-auto min-w-[200px] flex-1 sm:max-w-[320px]">
            <Search size={13} className="pointer-events-none absolute left-2.5 top-1/2 -translate-y-1/2 text-text-faint" aria-hidden />
            <input
              value={searchDraft}
              onChange={(e) => setSearchDraft(e.target.value)}
              placeholder={t('review.activity.searchPlaceholder')}
              className={`${inputCls} pl-8`}
              aria-label={t('review.activity.search')}
            />
          </label>
        </div>

        <button
          type="button"
          onClick={() => setMoreFilters((v) => !v)}
          aria-expanded={moreFilters}
          className="inline-flex items-center gap-1.5 self-start text-[12.5px] font-semibold text-text-mid sm:hidden"
        >
          <SlidersHorizontal size={13} aria-hidden />
          {moreFilters ? t('review.activity.fewerFilters') : t('review.activity.moreFilters')}
          {advancedCount > 0 && (
            <span className="rounded-full bg-primary px-1.5 text-[10.5px] leading-[16px] text-on-primary">{advancedCount}</span>
          )}
        </button>

        <div className={`${moreFilters ? 'grid' : 'hidden'} grid-cols-1 gap-2 sm:grid sm:grid-cols-3`}>
          <Select
            value={filters.actor}
            onChange={(v) => update({ actor: v })}
            options={actorOptions}
            ariaLabel={t('review.activity.actor')}
            searchable
          />
          <Select
            value={filters.action}
            onChange={(v) => update({ action: v })}
            options={actionOptions}
            ariaLabel={t('review.activity.action')}
            searchable
          />
          <EntityPicker
            value={filters.entity}
            onChange={(slug) => update({ entity: slug })}
            globalLabel={t('analytics.page.allEntities')}
            size="sm"
            ariaLabel={t('common.fields.entity')}
          />
        </div>

        <div className={`${moreFilters ? 'flex' : 'hidden'} flex-wrap items-center gap-1.5 sm:flex`} role="group" aria-label={t('review.activity.source')}>
          <span className="eyebrow mr-1">{t('review.activity.source')}</span>
          {SOURCES.map((s) => {
            const m = sourceMeta(s)
            const on = filters.sources.includes(s)
            const n = sourceCount(s)
            return (
              <button
                key={s}
                type="button"
                title={t(m.hint)}
                aria-pressed={on}
                onClick={() => toggleSource(s)}
                className={`inline-flex h-7 items-center gap-1.5 rounded-full border px-2.5 text-[11.5px] font-semibold transition-colors ${
                  on ? `border-transparent ${m.cls}` : 'border-line text-text-muted hover:border-line-strong hover:text-text-high'
                }`}
              >
                <span className={`h-[6px] w-[6px] rounded-full ${m.dot}`} aria-hidden />
                {t(m.label)}
                {facets && <span className="font-normal tabular-nums opacity-70">{f.number(n)}</span>}
              </button>
            )
          })}
          {active && (
            <button
              type="button"
              onClick={() => {
                setFilters(EMPTY)
                setSearchDraft('')
                setPage(1)
              }}
              className="ml-auto inline-flex items-center gap-1 text-[12px] font-semibold text-text-muted hover:text-text-high"
            >
              <X size={12} aria-hidden /> {t('notifications.inbox.clearFilters')}
            </button>
          )}
        </div>
      </section>

      {/* ---- results ------------------------------------------------------- */}
      {error && !data ? (
        <div className="rounded-xl border border-line bg-card px-5 py-8 text-center text-[13px] text-text-muted">
          {/activity\.view|403/.test(error)
            ? t('review.activity.needsPermission')
            : t('review.activity.unavailable', { error })}
        </div>
      ) : (
        <section data-tour="activity-entries" className="overflow-hidden rounded-xl border border-line bg-card" aria-label={t('review.activity.entriesAria')}>
          <div className="flex flex-wrap items-baseline justify-between gap-2 border-b border-line px-4 py-2.5">
            <span className="text-[12.5px] text-text-muted">
              {data
                ? data.pages > 1
                  ? t('review.activity.entriesPaged', {
                      entries: t('review.activity.entries', { count: data.total }),
                      page: data.page,
                      pages: data.pages,
                    })
                  : t('review.activity.entries', { count: data.total })
                : t('common.status.loading')}
            </span>
            {error && <span className="text-[12px] text-red-300">{error}</span>}
          </div>

          {data && data.entries.length === 0 && !loading ? (
            <div className="px-5 py-12 text-center">
              <div className="display text-[20px] text-text-heading">{t('review.activity.emptyTitle')}</div>
              <p className="mt-1 text-[13px] text-text-muted">
                {active ? t('review.activity.noMatch') : t('review.activity.emptyBody')}
              </p>
            </div>
          ) : (
            <>
              {/* desktop table */}
              <div className="hidden overflow-x-auto md:block">
                <table className="w-full min-w-[860px] border-collapse text-left">
                  <thead>
                    <tr className="eyebrow border-b border-line">
                      <th className="px-4 py-2 font-semibold">{t('review.activity.when')}</th>
                      <th className="px-3 py-2 font-semibold">{t('review.activity.who')}</th>
                      <th className="px-3 py-2 font-semibold">{t('review.activity.source')}</th>
                      <th className="px-3 py-2 font-semibold">{t('review.activity.action')}</th>
                      <th className="px-3 py-2 font-semibold">{t('review.activity.target')}</th>
                      <th className="px-3 py-2 font-semibold">{t('common.fields.entity')}</th>
                      <th className="px-4 py-2 text-right font-semibold">{t('common.fields.status')}</th>
                    </tr>
                  </thead>
                  <tbody className={loading ? 'opacity-60 transition-opacity' : ''}>
                    {(data?.entries ?? []).map((e) => (
                      <tr
                        key={e.id}
                        onClick={() => setSelected(e)}
                        className="cursor-pointer border-b border-line last:border-b-0 hover:bg-fill-subtle"
                      >
                        <td className="whitespace-nowrap px-4 py-2.5 text-[12px] text-text-muted" title={f.dateTime(e.created_at)}>
                          {ago(e.created_at)}
                        </td>
                        <td className="max-w-[140px] truncate px-3 py-2.5 text-[12.5px] font-semibold text-text-high">{e.actor}</td>
                        <td className="px-3 py-2.5">
                          <SourceBadge source={e.source} />
                        </td>
                        <td className="max-w-[260px] px-3 py-2.5">
                          <span className="block truncate font-mono text-[11.5px] text-text-primary" title={e.action}>
                            {e.action}
                          </span>
                        </td>
                        <td className="max-w-[320px] px-3 py-2.5 text-[12.5px] text-text-mid">
                          <span className="block truncate" title={`${e.target}${e.detail ? ` — ${e.detail}` : ''}`}>
                            {e.target || <span className="text-text-faint">—</span>}
                            {e.detail && <span className="text-text-muted"> — {e.detail}</span>}
                          </span>
                        </td>
                        <td className="max-w-[140px] truncate px-3 py-2.5 text-[12px] text-text-muted">
                          {e.entity_name || <span className="text-text-faint">—</span>}
                        </td>
                        <td className="px-4 py-2.5 text-right">
                          <Status status={e.status} />
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>

              {/* phone list */}
              <ul className={`divide-y divide-line md:hidden ${loading ? 'opacity-60' : ''}`}>
                {(data?.entries ?? []).map((e) => (
                  <li key={e.id}>
                    <button type="button" onClick={() => setSelected(e)} className="flex w-full flex-col gap-1 px-4 py-3 text-left hover:bg-fill-subtle">
                      <div className="flex items-center gap-2">
                        <span className="truncate text-[13px] font-semibold text-text-high">{e.actor}</span>
                        <SourceBadge source={e.source} />
                        <span className="ml-auto whitespace-nowrap text-[11.5px] text-text-muted">{ago(e.created_at)}</span>
                      </div>
                      <div className="flex items-center gap-2">
                        <span className="min-w-0 flex-1 truncate font-mono text-[11.5px] text-text-primary">{e.action}</span>
                        <Status status={e.status} />
                      </div>
                      {(e.target || e.detail) && (
                        <span className="truncate text-[12px] text-text-muted">
                          {e.target}
                          {e.detail && ` — ${e.detail}`}
                        </span>
                      )}
                    </button>
                  </li>
                ))}
              </ul>

              {!data && loading && (
                <div className="flex items-center justify-center gap-2 px-5 py-12 text-[13px] text-text-muted">
                  <Loader2 size={14} className="animate-spin" aria-hidden /> {t('review.activity.loading')}
                </div>
              )}
            </>
          )}

          {data && data.total > 0 && (
            <div className="flex flex-wrap items-center justify-between gap-2 border-t border-line px-4 py-2.5">
              <div className="flex items-center gap-2 text-[12px] text-text-muted">
                {t('review.activity.rowsPerPage')}
                <div className="w-[76px]">
                  <Select
                    value={String(size)}
                    onChange={(v) => {
                      setSize(Number(v))
                      setPage(1)
                    }}
                    options={PAGE_SIZES.map((n) => ({ value: String(n), label: String(n) }))}
                    ariaLabel={t('review.activity.rowsPerPage')}
                  />
                </div>
              </div>
              <div className="flex items-center gap-1.5">
                <Button variant="ghost" size="sm" disabled={page <= 1 || loading} onClick={() => setPage((p) => p - 1)}>
                  <ChevronLeft size={14} aria-hidden /> {t('review.activity.newer')}
                </Button>
                <span className="text-[12px] tabular-nums text-text-muted">
                  {page} / {data.pages}
                </span>
                <Button variant="ghost" size="sm" disabled={page >= data.pages || loading} onClick={() => setPage((p) => p + 1)}>
                  {t('review.activity.older')} <ChevronRight size={14} aria-hidden />
                </Button>
              </div>
            </div>
          )}
        </section>
      )}

      <DetailDrawer
        entry={selected}
        onClose={() => setSelected(null)}
        onFilter={(patch) => {
          setSelected(null)
          if (patch.q !== undefined) setSearchDraft(patch.q)
          update({ ...patch, preset: patch.q ? 'all' : filters.preset })
        }}
      />
    </div>
  )
}

// ---- detail drawer ---------------------------------------------------------

function CopyText({ value }: { value: string }) {
  const t = useT()
  const [done, setDone] = useState(false)
  if (!value) return null
  return (
    <button
      type="button"
      onClick={async () => {
        try {
          await navigator.clipboard.writeText(value)
          setDone(true)
          setTimeout(() => setDone(false), 1200)
        } catch {
          /* clipboard blocked */
        }
      }}
      aria-label={t('common.actions.copy')}
      className="grid h-6 w-6 shrink-0 place-items-center rounded-md text-text-faint hover:bg-fill-hover hover:text-text-high"
    >
      {done ? <Check size={12} aria-hidden /> : <Copy size={12} aria-hidden />}
    </button>
  )
}

function Field({ label, value, mono, copy }: { label: string; value: React.ReactNode; mono?: boolean; copy?: string }) {
  return (
    <div className="grid grid-cols-[110px_1fr] items-start gap-3 border-b border-line py-2.5 last:border-b-0">
      <dt className="eyebrow pt-0.5">{label}</dt>
      <dd className={`flex min-w-0 items-start gap-1 text-[12.5px] text-text-primary ${mono ? 'font-mono text-[11.5px]' : ''}`}>
        <span className="min-w-0 flex-1 break-words">{value || <span className="text-text-faint">—</span>}</span>
        {copy && <CopyText value={copy} />}
      </dd>
    </div>
  )
}

function DetailDrawer({
  entry,
  onClose,
  onFilter,
}: {
  entry: AuditEntry | null
  onClose: () => void
  onFilter: (patch: Partial<Filters>) => void
}) {
  const t = useT()
  const f = useFormat()
  const closeRef = useRef<HTMLButtonElement>(null)
  useEffect(() => {
    if (!entry) return
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && onClose()
    window.addEventListener('keydown', onKey)
    closeRef.current?.focus()
    return () => window.removeEventListener('keydown', onKey)
  }, [entry, onClose])

  if (!entry || typeof document === 'undefined') return null
  const m = sourceMeta(entry.source)
  return createPortal(
    <div className="fixed inset-0 z-[60] flex justify-end" style={{ background: 'var(--scrim)' }} onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <aside
        role="dialog"
        aria-modal="true"
        aria-labelledby="activity-detail-title"
        className="flex h-full w-full max-w-[480px] animate-fadeIn flex-col border-l border-line-strong bg-card shadow-pop"
      >
        <header className="flex items-start gap-3 border-b border-line px-5 py-4">
          <div className="min-w-0 flex-1">
            <div className="eyebrow">{t('review.activity.detailEyebrow', { id: entry.id })}</div>
            <h2 id="activity-detail-title" className="display mt-0.5 break-words text-[20px] leading-tight text-text-heading">
              {entry.action}
            </h2>
            <div className="mt-1.5 flex flex-wrap items-center gap-2 text-[12px] text-text-muted">
              <SourceBadge source={entry.source} />
              <span>{f.dateTime(entry.created_at)}</span>
            </div>
          </div>
          <button
            ref={closeRef}
            onClick={onClose}
            aria-label={t('common.actions.close')}
            className="grid h-8 w-8 shrink-0 place-items-center rounded-lg text-text-muted hover:bg-fill-hover hover:text-text-high"
          >
            <X size={15} aria-hidden />
          </button>
        </header>
        <dl className="min-h-0 flex-1 overflow-y-auto px-5 py-2">
          <Field
            label={t('review.activity.who')}
            value={
              <>
                {entry.actor}
                {entry.user_id != null && (
                  <span className="text-text-faint"> · {t('review.activity.userId', { id: entry.user_id })}</span>
                )}
              </>
            }
          />
          <Field label={t('review.activity.how')} value={t('review.activity.howValue', { label: t(m.label), hint: t(m.hint) })} />
          <Field label={t('review.activity.action')} value={entry.action} mono copy={entry.action} />
          <Field label={t('review.activity.target')} value={entry.target} />
          <Field label={t('review.activity.detail')} value={entry.detail} />
          <Field label={t('common.fields.entity')} value={entry.entity_name ? `${entry.entity_name} (${entry.entity})` : ''} />
          <Field label={t('review.activity.request')} value={entry.method ? `${entry.method} ${entry.path}` : ''} mono />
          <Field label={t('common.fields.status')} value={entry.status != null ? <Status status={entry.status} /> : ''} />
          <Field label={t('review.activity.ip')} value={entry.ip} mono copy={entry.ip} />
          <Field label={t('review.activity.userAgent')} value={entry.user_agent} mono />
          <Field label={t('review.activity.requestId')} value={entry.request_id} mono copy={entry.request_id} />
          <Field label={t('review.activity.timeUtc')} value={entry.created_at} mono />
        </dl>
        <footer className="flex flex-wrap gap-2 border-t border-line bg-surface px-5 py-3">
          <Button variant="secondary" size="sm" onClick={() => onFilter({ actor: entry.actor })}>
            {t('review.activity.moreBy', { actor: entry.actor.length > 18 ? `${entry.actor.slice(0, 18)}…` : entry.actor })}
          </Button>
          {entry.request_id && (
            <Button variant="ghost" size="sm" onClick={() => onFilter({ q: entry.request_id })}>
              {t('review.activity.sameRequest')}
            </Button>
          )}
        </footer>
      </aside>
    </div>,
    document.body,
  )
}
