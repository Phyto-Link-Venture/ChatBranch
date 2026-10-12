'use client'

import Link from 'next/link'
import { useRouter } from 'next/navigation'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  AlertCircle,
  CheckCheck,
  Loader2,
  Mail,
  MailOpen,
  RefreshCw,
  Search,
  Settings2,
  Trash2,
  X,
} from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Modal } from '@/components/ui/Modal'
import { refreshNotifications } from '@/components/shell/notificationsStore'
import { tint } from '@/lib/color'
import { useFormat, useT, type TFunction } from '@/i18n'
import { dayStartIso } from '@/lib/timeZone'
import {
  NOTIF_CATEGORIES,
  categoryMeta,
  notifApi,
  type NotificationItem,
  type NotificationPage,
} from '@/lib/notifications'

const PAGE_SIZE = 30

type ReadFilter = 'all' | 'unread' | 'read'

/** Day bucket label: Today / Yesterday / weekday (this week) / 12 Mar 2026 — in the UI language
 *  and the zone times are shown in (Settings → Time zone). */
function dayLabel(d: Date, now: Date, t: TFunction, f: ReturnType<typeof useFormat>): string {
  const days = f.daysAgo(d, now)
  if (days === 0) return t('common.time.today')
  if (days === 1) return t('common.time.yesterday')
  if (days != null && days > 1 && days < 7) return f.weekdayLong(d)
  return f.date(d)
}


/**
 * The full notification inbox: every alert the caller ever received, newest
 * first, grouped by day. Filters (category, read state, date range, text),
 * per-row read/unread/delete, bulk selection, mark-all-read, delete-read, and
 * click-through to the screen the alert is about. Infinite scroll in pages of
 * 30 (with a Load more fallback).
 */
export function NotificationsInbox() {
  const t = useT()
  const f = useFormat()
  const router = useRouter()
  const [items, setItems] = useState<NotificationItem[]>([])
  const [page, setPage] = useState(1)
  const [total, setTotal] = useState(0)
  const [hasMore, setHasMore] = useState(false)
  const [unread, setUnread] = useState(0)
  const [byCat, setByCat] = useState<Record<string, number>>({})
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')

  const [category, setCategory] = useState<string>('')
  const [readFilter, setReadFilter] = useState<ReadFilter>('all')
  const [query, setQuery] = useState('')
  const [debounced, setDebounced] = useState('')
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')

  const [selected, setSelected] = useState<Set<number>>(new Set())
  const [confirm, setConfirm] = useState<null | 'read' | 'selected'>(null)
  const [busy, setBusy] = useState(false)
  const reqId = useRef(0)

  useEffect(() => {
    const t = window.setTimeout(() => setDebounced(query.trim()), 300)
    return () => window.clearTimeout(t)
  }, [query])

  const qs = useCallback(
    (p: number) => {
      const params = new URLSearchParams({ page: String(p), page_size: String(PAGE_SIZE) })
      if (category) params.set('category', category)
      if (readFilter !== 'all') params.set('unread', readFilter === 'unread' ? 'true' : 'false')
      if (debounced) params.set('q', debounced)
      if (from) params.set('from', dayStartIso(from) ?? '')
      if (to) params.set('to', dayStartIso(to, 1) ?? '')
      return params.toString()
    },
    [category, readFilter, debounced, from, to],
  )

  const load = useCallback(
    async (p: number) => {
      const id = ++reqId.current
      setLoading(true)
      setError('')
      try {
        const d = await notifApi<NotificationPage>(`notifications?${qs(p)}`)
        if (id !== reqId.current) return // a newer filter won
        setItems((prev) => (p === 1 ? d.items : [...prev, ...d.items.filter((n) => !prev.some((x) => x.id === n.id))]))
        setPage(d.page)
        setTotal(d.total)
        setHasMore(d.has_more)
        setUnread(d.unread)
        setByCat(d.unread_by_category ?? {})
      } catch (e) {
        if (id === reqId.current) setError(e instanceof Error ? e.message : String(e))
      } finally {
        if (id === reqId.current) setLoading(false)
      }
    },
    [qs],
  )

  // Filters changed → start over from page 1.
  useEffect(() => {
    setSelected(new Set())
    void load(1)
  }, [load])

  // Infinite scroll: fetch the next page as the sentinel comes into view.
  const sentinel = useRef<HTMLDivElement>(null)
  useEffect(() => {
    const el = sentinel.current
    if (!el || !hasMore) return
    const io = new IntersectionObserver((entries) => {
      if (entries.some((e) => e.isIntersecting) && !loading) void load(page + 1)
    })
    io.observe(el)
    return () => io.disconnect()
  }, [hasMore, loading, page, load])

  const groups = useMemo(() => {
    const now = new Date()
    const out: { label: string; items: NotificationItem[] }[] = []
    for (const n of items) {
      const label = dayLabel(new Date(n.created_at), now, t, f)
      const last = out[out.length - 1]
      if (last && last.label === label) last.items.push(n)
      else out.push({ label, items: [n] })
    }
    return out
  }, [items, t, f])

  // ---- mutations (optimistic where safe) ----------------------------------

  const patchLocal = (ids: number[], read: boolean) =>
    setItems((xs) =>
      readFilter === 'all'
        ? xs.map((x) => (ids.includes(x.id) ? { ...x, read } : x))
        : xs.filter((x) => !(ids.includes(x.id) && (readFilter === 'unread') === read)),
    )

  const setRead = async (n: NotificationItem, read: boolean) => {
    try {
      const r = await notifApi<{ unread: number }>(`notifications/${n.id}/${read ? 'read' : 'unread'}`, { method: 'POST' })
      patchLocal([n.id], read)
      setUnread(r.unread)
      void refreshNotifications() // keep the bell + sidebar counter in step
      setByCat((m) => ({ ...m, [n.category]: Math.max(0, (m[n.category] ?? 0) + (read ? -1 : 1)) }))
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  const open = (n: NotificationItem) => {
    if (!n.read) void setRead(n, true)
    if (n.link) router.push(n.link)
  }

  const remove = async (n: NotificationItem) => {
    try {
      const r = await notifApi<{ unread: number }>(`notifications/${n.id}`, { method: 'DELETE' })
      setItems((xs) => xs.filter((x) => x.id !== n.id))
      setTotal((t) => Math.max(0, t - 1))
      setUnread(r.unread)
      void refreshNotifications()
      setSelected((s) => {
        const next = new Set(s)
        next.delete(n.id)
        return next
      })
      if (!n.read) setByCat((m) => ({ ...m, [n.category]: Math.max(0, (m[n.category] ?? 0) - 1) }))
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  const bulk = async (action: 'read' | 'unread' | 'delete') => {
    const ids = [...selected]
    if (ids.length === 0) return
    setBusy(true)
    try {
      if (action === 'delete') {
        await notifApi('notifications/delete', { method: 'POST', body: JSON.stringify({ ids }) })
      } else {
        await notifApi('notifications/mark', { method: 'POST', body: JSON.stringify({ ids, read: action === 'read' }) })
      }
      setSelected(new Set())
      await load(1)
      void refreshNotifications()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
      setConfirm(null)
    }
  }

  const markAll = async () => {
    setBusy(true)
    try {
      await notifApi('notifications/read-all', {
        method: 'POST',
        body: JSON.stringify(category ? { category } : {}),
      })
      await load(1)
      void refreshNotifications()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  const deleteRead = async () => {
    setBusy(true)
    try {
      await notifApi('notifications/delete', { method: 'POST', body: JSON.stringify({ read: true }) })
      await load(1)
      void refreshNotifications()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
      setConfirm(null)
    }
  }

  const toggleSel = (id: number) =>
    setSelected((s) => {
      const next = new Set(s)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })
  const allSelected = items.length > 0 && items.every((n) => selected.has(n.id))
  const filtered = !!(category || readFilter !== 'all' || debounced || from || to)
  const totalUnread = Object.values(byCat).reduce((s, n) => s + n, 0)

  return (
    <div className="flex flex-col gap-4">
      {/* Category filter */}
      <div data-tour="notif-categories" className="-mx-1 flex flex-wrap gap-1.5 px-1" role="tablist" aria-label={t('notifications.inbox.category')}>
        <CategoryChip
          active={category === ''}
          onClick={() => setCategory('')}
          label={t('common.status.all')}
          count={totalUnread}
        />
        {NOTIF_CATEGORIES.map((c) => (
          <CategoryChip
            key={c.key}
            active={category === c.key}
            onClick={() => setCategory(c.key)}
            label={t(c.labelKey)}
            count={byCat[c.key] ?? 0}
            ink={c.ink}
          />
        ))}
      </div>

      {/* Toolbar */}
      <div data-tour="notif-toolbar" className="flex flex-wrap items-center gap-2">
        <label className="flex h-9 min-w-[180px] flex-1 items-center gap-2 rounded-lg border border-line-strong bg-card px-2.5 sm:max-w-[320px]">
          <Search size={14} className="shrink-0 text-text-muted" aria-hidden />
          <input
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder={t('notifications.inbox.searchPlaceholder')}
            aria-label={t('notifications.inbox.search')}
            className="min-w-0 flex-1 bg-transparent text-[13px] text-text-primary outline-none"
          />
          {query && (
            <button
              type="button"
              onClick={() => setQuery('')}
              aria-label={t('notifications.inbox.clearSearch')}
              className="text-text-muted hover:text-text-high"
            >
              <X size={13} />
            </button>
          )}
        </label>
        <div
          role="radiogroup"
          aria-label={t('notifications.inbox.readState')}
          className="inline-flex rounded-lg border border-line-strong bg-card p-0.5"
        >
          {(['all', 'unread', 'read'] as ReadFilter[]).map((v) => (
            <button
              key={v}
              type="button"
              role="radio"
              aria-checked={readFilter === v}
              onClick={() => setReadFilter(v)}
              className={`h-8 rounded-md px-3 text-[12.5px] font-semibold transition-colors ${
                readFilter === v ? 'bg-primary text-on-primary' : 'text-text-mid hover:bg-fill-hover hover:text-text-high'
              }`}
            >
              {v === 'all'
                ? t('common.status.all')
                : v === 'unread'
                  ? t('notifications.inbox.unread')
                  : t('notifications.inbox.read')}
            </button>
          ))}
        </div>
        <div className="flex items-center gap-1.5">
          <input
            type="date"
            value={from}
            max={to || undefined}
            onChange={(e) => setFrom(e.target.value)}
            aria-label={t('notifications.inbox.fromDate')}
            className="h-9 rounded-lg border border-line-strong bg-card px-2 text-[12.5px] text-text-primary outline-none"
          />
          <span className="text-[12px] text-text-faint">{t('notifications.inbox.to')}</span>
          <input
            type="date"
            value={to}
            min={from || undefined}
            onChange={(e) => setTo(e.target.value)}
            aria-label={t('notifications.inbox.toDate')}
            className="h-9 rounded-lg border border-line-strong bg-card px-2 text-[12.5px] text-text-primary outline-none"
          />
        </div>
        {filtered && (
          <Button
            variant="ghost"
            size="sm"
            onClick={() => {
              setCategory('')
              setReadFilter('all')
              setQuery('')
              setFrom('')
              setTo('')
            }}
          >
            {t('notifications.inbox.clearFilters')}
          </Button>
        )}
        <span data-tour="notif-actions" className="flex flex-wrap items-center gap-1.5 sm:ml-auto">
          <Button
            variant="ghost"
            size="sm"
            onClick={() => void load(1)}
            aria-label={t('common.actions.refresh')}
            title={t('common.actions.refresh')}
          >
            <RefreshCw size={13} aria-hidden />
          </Button>
          <Button variant="secondary" size="sm" onClick={() => void markAll()} disabled={busy || unread === 0}>
            <CheckCheck size={13} aria-hidden /> {t('notifications.inbox.markAllRead')}
          </Button>
          <Button variant="ghost" size="sm" onClick={() => setConfirm('read')} disabled={busy}>
            <Trash2 size={13} aria-hidden /> {t('notifications.inbox.deleteRead')}
          </Button>
          <Link
            href="/settings/my-notifications"
            data-tour="notif-settings"
            className="grid h-8 w-8 place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
            aria-label={t('notifications.inbox.settings')}
            title={t('notifications.inbox.settings')}
          >
            <Settings2 size={15} aria-hidden />
          </Link>
        </span>
      </div>

      {/* Selection bar */}
      {items.length > 0 && (
        <div
          className={`sticky top-0 z-10 flex flex-wrap items-center gap-2 rounded-xl border px-3 py-2 ${
            selected.size ? 'border-line-strong bg-card shadow-pop' : 'border-transparent'
          }`}
        >
          <label className="flex cursor-pointer items-center gap-2 text-[12.5px] text-text-mid">
            <input
              type="checkbox"
              checked={allSelected}
              onChange={() => setSelected(allSelected ? new Set() : new Set(items.map((n) => n.id)))}
              className="h-4 w-4 cursor-pointer accent-[color:var(--primary)]"
              aria-label={t('notifications.inbox.selectAll')}
            />
            {selected.size
              ? t('common.counts.selected', { count: selected.size })
              : unread
                ? t('notifications.inbox.totalUnread', {
                    total: t('notifications.inbox.total', { count: total }),
                    unread: f.number(unread),
                  })
                : t('notifications.inbox.total', { count: total })}
          </label>
          {selected.size > 0 && (
            <span className="ml-auto flex flex-wrap gap-1.5">
              <Button variant="secondary" size="sm" disabled={busy} onClick={() => void bulk('read')}>
                {t('notifications.inbox.markRead')}
              </Button>
              <Button variant="secondary" size="sm" disabled={busy} onClick={() => void bulk('unread')}>
                {t('notifications.inbox.markUnread')}
              </Button>
              <Button variant="danger" size="sm" disabled={busy} onClick={() => setConfirm('selected')}>
                <Trash2 size={13} aria-hidden /> {t('common.actions.delete')}
              </Button>
              <Button variant="ghost" size="sm" onClick={() => setSelected(new Set())}>
                {t('common.actions.cancel')}
              </Button>
            </span>
          )}
        </div>
      )}

      {error && (
        <div role="alert" className="flex items-start gap-2 rounded-lg border border-red/25 bg-red/10 px-3 py-2 text-[12.5px] text-[color:var(--red-ink)]">
          <AlertCircle size={14} className="mt-0.5 shrink-0" aria-hidden /> {error}
        </div>
      )}

      {/* The list */}
      {items.length === 0 && !loading ? (
        <div data-tour="notif-list" className="rounded-xl border border-dashed border-line-strong px-6 py-14 text-center">
          <div className="display text-[20px] text-text-heading">
            {filtered ? t('notifications.inbox.noMatchTitle') : t('notifications.inbox.emptyTitle')}
          </div>
          <p className="mx-auto mt-1 max-w-[380px] text-[12.5px] text-text-muted">
            {filtered ? t('notifications.inbox.noMatchBody') : t('notifications.inbox.emptyBody')}
          </p>
        </div>
      ) : (
        <div data-tour="notif-list" className="flex flex-col gap-5">
          {groups.map((g) => (
            <section key={g.label}>
              <div className="mb-1.5 flex items-baseline gap-2 border-b border-line pb-1">
                <span className="eyebrow">{g.label}</span>
                <span className="text-[11.5px] text-text-faint">{f.number(g.items.length)}</span>
              </div>
              <ul className="flex flex-col">
                {g.items.map((n) => (
                  <Row
                    key={n.id}
                    n={n}
                    selected={selected.has(n.id)}
                    onSelect={() => toggleSel(n.id)}
                    onOpen={() => open(n)}
                    onToggleRead={() => void setRead(n, !n.read)}
                    onDelete={() => void remove(n)}
                  />
                ))}
              </ul>
            </section>
          ))}
        </div>
      )}

      <div ref={sentinel} className="flex justify-center py-2">
        {loading ? (
          <span className="flex items-center gap-2 text-[12.5px] text-text-muted">
            <Loader2 size={14} className="animate-spin" aria-hidden /> {t('common.status.loading')}
          </span>
        ) : hasMore ? (
          <Button variant="secondary" size="sm" onClick={() => void load(page + 1)}>
            {t('notifications.inbox.loadMore')}
          </Button>
        ) : items.length > 0 ? (
          <span className="text-[11.5px] text-text-faint">{t('notifications.inbox.everything')}</span>
        ) : null}
      </div>

      <Modal
        open={confirm !== null}
        onClose={() => setConfirm(null)}
        size="sm"
        title={
          confirm === 'read'
            ? t('notifications.inbox.deleteReadTitle')
            : t('notifications.inbox.deleteSelectedTitle', { count: selected.size })
        }
        subtitle={t('notifications.inbox.deleteSubtitle')}
        footer={
          <>
            <Button variant="ghost" onClick={() => setConfirm(null)}>
              {t('common.actions.cancel')}
            </Button>
            <Button
              variant="danger"
              disabled={busy}
              onClick={() => void (confirm === 'read' ? deleteRead() : bulk('delete'))}
            >
              {busy && <Loader2 size={13} className="animate-spin" aria-hidden />}
              {t('common.actions.delete')}
            </Button>
          </>
        }
      >
        <p className="text-[13px] text-text-mid">
          {confirm === 'read' ? t('notifications.inbox.deleteReadBody') : t('notifications.inbox.deleteSelectedBody')}
        </p>
      </Modal>
    </div>
  )
}

function CategoryChip({
  active,
  onClick,
  label,
  count,
  ink,
}: {
  active: boolean
  onClick: () => void
  label: string
  count: number
  ink?: string
}) {
  const f = useFormat()
  return (
    <button
      type="button"
      role="tab"
      aria-selected={active}
      onClick={onClick}
      className={`inline-flex h-8 items-center gap-1.5 rounded-full border px-3 text-[12.5px] transition-colors ${
        active
          ? 'border-transparent bg-primary font-semibold text-on-primary'
          : 'border-line-strong bg-card text-text-mid hover:text-text-high'
      }`}
    >
      {ink && !active && <span className="h-2 w-2 rounded-full" style={{ background: ink }} aria-hidden />}
      {label}
      {count > 0 && (
        <span
          className={`min-w-[18px] rounded-full px-1.5 text-center text-[10.5px] font-bold ${
            active ? 'text-on-primary' : 'bg-fill text-text-high'
          }`}
          style={active ? { background: tint('var(--on-primary)', 0.2) } : undefined}
        >
          {count > 99 ? `${f.number(99)}+` : f.number(count)}
        </span>
      )}
    </button>
  )
}

function Row({
  n,
  selected,
  onSelect,
  onOpen,
  onToggleRead,
  onDelete,
}: {
  n: NotificationItem
  selected: boolean
  onSelect: () => void
  onOpen: () => void
  onToggleRead: () => void
  onDelete: () => void
}) {
  const t = useT()
  const f = useFormat()
  const meta = categoryMeta(n.category)
  return (
    <li
      className={`group relative flex items-start gap-3 border-b border-line py-3 pl-3 pr-1 transition-colors hover:bg-fill-subtle ${
        selected ? 'bg-fill-subtle' : ''
      }`}
    >
      {/* unread rule */}
      <span
        aria-hidden
        className="absolute bottom-2 left-0 top-2 w-[3px] rounded-full"
        style={{ background: n.read ? 'transparent' : 'var(--accent)' }}
      />
      <input
        type="checkbox"
        checked={selected}
        onChange={onSelect}
        aria-label={t('notifications.inbox.selectOne', { title: n.title })}
        className="mt-1.5 h-4 w-4 shrink-0 cursor-pointer accent-[color:var(--primary)]"
      />
      <span
        className="mt-0.5 grid h-8 w-8 shrink-0 place-items-center rounded-lg"
        style={{ background: tint(meta.ink, 0.12), color: meta.ink }}
        title={t(meta.labelKey)}
      >
        <meta.Icon size={15} aria-hidden />
      </span>
      <button type="button" onClick={onOpen} className="min-w-0 flex-1 cursor-pointer text-left">
        <span className="flex items-baseline gap-2">
          <span
            className={`line-clamp-2 text-[13.5px] sm:line-clamp-1 ${n.read ? 'font-medium text-text-mid' : 'font-bold text-text-heading'}`}
          >
            {n.title}
          </span>
          <span className="ml-auto shrink-0 text-[11.5px] tabular-nums text-text-faint">{f.time(n.created_at)}</span>
        </span>
        {n.body && (
          <span className={`mt-0.5 line-clamp-2 block text-[12.5px] leading-snug ${n.read ? 'text-text-muted' : 'text-text-mid'}`}>
            {n.body}
          </span>
        )}
        <span className="mt-1 block text-[11px] text-text-faint">
          {t(meta.labelKey)}
          {n.link ? ` · ${t('common.actions.open')}` : ''}
        </span>
      </button>
      <span className="flex shrink-0 flex-col gap-0.5 sm:flex-row sm:opacity-60 sm:group-hover:opacity-100">
        <button
          type="button"
          onClick={onToggleRead}
          aria-label={n.read ? t('notifications.inbox.markAsUnread') : t('notifications.inbox.markAsRead')}
          title={n.read ? t('notifications.inbox.markAsUnread') : t('notifications.inbox.markAsRead')}
          className="grid h-8 w-8 place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
        >
          {n.read ? <Mail size={14} aria-hidden /> : <MailOpen size={14} aria-hidden />}
        </button>
        <button
          type="button"
          onClick={onDelete}
          aria-label={t('notifications.inbox.deleteOne')}
          title={t('common.actions.delete')}
          className="grid h-8 w-8 place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-[color:var(--red-ink)]"
        >
          <Trash2 size={14} aria-hidden />
        </button>
      </span>
    </li>
  )
}
