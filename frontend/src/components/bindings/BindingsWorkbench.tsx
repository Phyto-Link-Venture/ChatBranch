'use client'

import { useCallback, useEffect, useMemo, useState } from 'react'
import { ArrowLeftRight, Check, Link2, Loader2, Search, X } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Select } from '@/components/ui/Select'
import { authHeaders, hasCap } from '@/lib/auth'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

/**
 * One screen for every many-to-many binding: contacts on an entity, members of
 * a team, entity access for a user, customers of a service provider — and each
 * of those from the other side too.
 *
 * The point is BATCH. Ticking one box at a time in five different screens does
 * not survive a few hundred contacts, so this page filters first and then acts
 * on everything that matched: "select all matching", "clear all matching",
 * bind or unbind in one apply.
 *
 * The apply sends a DIFF, never "here is the new member list" — the operator is
 * usually looking at a filtered subset, and replacing the whole list from a
 * filtered view would silently unbind everyone they had filtered out.
 */

interface PoolItem {
  id: number
  label: string
  sub: string
}

interface Kind {
  id: string
  label: string
  target: string
  candidate: string
  hint: string
}

interface Catalog {
  kinds: Kind[]
  pools: {
    entities: PoolItem[]
    teams: PoolItem[]
    users: PoolItem[]
    providers: PoolItem[]
    contacts: PoolItem[]
  }
}

const POOL_OF: Record<string, keyof Catalog['pools']> = {
  entity: 'entities',
  team: 'teams',
  user: 'users',
  provider: 'providers',
  contact: 'contacts',
}

const SIDE_LABEL: Record<string, string> = {
  entity: 'Entity',
  team: 'Team',
  user: 'User',
  provider: 'Service provider',
  contact: 'Contact',
}

async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}/${path}`, {
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
    ...init,
  })
  const data = await res.json().catch(() => ({}))
  if (!res.ok) {
    const d = data as { detail?: string; error?: { message?: string } }
    throw new Error(d.detail || d.error?.message || `HTTP ${res.status}`)
  }
  return data as T
}

export function BindingsWorkbench() {
  const [catalog, setCatalog] = useState<Catalog | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [kindId, setKindId] = useState('entity-contacts')
  const [targetId, setTargetId] = useState<number | null>(null)
  /** Saved membership from the server — the baseline the diff is measured against. */
  const [saved, setSaved] = useState<number[]>([])
  /** The working set the operator is editing. */
  const [draft, setDraft] = useState<number[]>([])
  const [filter, setFilter] = useState('')
  const [onlyBound, setOnlyBound] = useState<'all' | 'bound' | 'unbound'>('all')
  const [busy, setBusy] = useState(false)
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)
  const [canManage, setCanManage] = useState(false)
  useEffect(() => setCanManage(hasCap('users.manage')), [])

  useEffect(() => {
    api<Catalog>('bindings/catalog')
      .then(setCatalog)
      .catch((e) => setLoadError(e instanceof Error ? e.message : String(e)))
  }, [])

  const kind = catalog?.kinds.find((k) => k.id === kindId) ?? null
  // Memoized: both feed effect dependency arrays, and a fresh [] every render
  // would re-run them forever.
  const targets = useMemo(() => (kind ? catalog!.pools[POOL_OF[kind.target]] : []), [kind, catalog])
  const candidates = useMemo(() => (kind ? catalog!.pools[POOL_OF[kind.candidate]] : []), [kind, catalog])

  // Pick a sensible target as soon as the kind changes, so the page is never
  // sitting there asking the operator to make two choices before showing work.
  useEffect(() => {
    if (!kind) return
    setTargetId((cur) => (targets.some((t) => t.id === cur) ? cur : (targets[0]?.id ?? null)))
    setFilter('')
    setMsg(null)
  }, [kindId, kind, targets])

  const loadMembers = useCallback(async () => {
    if (!kind || targetId == null) return
    try {
      const d = await api<{ member_ids: number[] }>(`bindings/${kind.id}?target=${targetId}`)
      setSaved(d.member_ids ?? [])
      setDraft(d.member_ids ?? [])
    } catch (e) {
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    }
  }, [kind, targetId])

  useEffect(() => {
    loadMembers()
  }, [loadMembers])

  const shown = useMemo(() => {
    const needle = filter.trim().toLowerCase()
    return candidates.filter((c) => {
      if (needle && !`${c.label} ${c.sub}`.toLowerCase().includes(needle)) return false
      if (onlyBound === 'bound' && !draft.includes(c.id)) return false
      if (onlyBound === 'unbound' && draft.includes(c.id)) return false
      return true
    })
  }, [candidates, filter, onlyBound, draft])

  const add = draft.filter((id) => !saved.includes(id))
  const remove = saved.filter((id) => !draft.includes(id))
  const dirty = add.length > 0 || remove.length > 0

  const toggle = (id: number) =>
    setDraft((cur) => (cur.includes(id) ? cur.filter((x) => x !== id) : [...cur, id]))

  /** Batch ops act on WHAT IS SHOWN — that is the whole point of filtering first. */
  const selectShown = () => setDraft((cur) => [...new Set([...cur, ...shown.map((c) => c.id)])])
  const clearShown = () => {
    const ids = new Set(shown.map((c) => c.id))
    setDraft((cur) => cur.filter((x) => !ids.has(x)))
  }

  const apply = async () => {
    if (!kind || targetId == null || !dirty) return
    setBusy(true)
    setMsg(null)
    try {
      const d = await api<{ added: number; removed: number; member_ids: number[] }>(
        `bindings/${kind.id}`,
        { method: 'POST', body: JSON.stringify({ target: targetId, add, remove }) },
      )
      setSaved(d.member_ids)
      setDraft(d.member_ids)
      setMsg({
        ok: true,
        text: `Applied — ${d.added} bound, ${d.removed} unbound.`,
      })
    } catch (e) {
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    } finally {
      setBusy(false)
    }
  }

  const reset = () => {
    setDraft(saved)
    setMsg(null)
  }

  if (loadError) {
    return (
      <div className="py-6 text-[12px] text-text-muted">Bindings unavailable — {loadError}</div>
    )
  }
  if (!catalog || !kind) {
    return (
      <div className="flex items-center gap-1.5 py-6 text-[12px] text-text-muted">
        <Loader2 size={13} className="animate-spin" aria-hidden /> Loading…
      </div>
    )
  }

  const targetName = targets.find((t) => t.id === targetId)?.label ?? '—'

  return (
    <div className="flex flex-col gap-4 pt-3.5">
      <p className="max-w-[760px] text-[11.5px] leading-relaxed text-text-mid">
        Bind things in bulk. Pick what you are binding, choose one side, then filter and tick — the
        select-all buttons act on <b>whatever the filter is showing</b>, so a few hundred contacts is the
        same amount of work as three. Nothing is saved until you apply, and applying sends only what
        changed.
      </p>

      {/* what to bind */}
      <div className="flex flex-wrap gap-2">
        {catalog.kinds.map((k) => {
          const on = k.id === kindId
          return (
            <button
              key={k.id}
              onClick={() => setKindId(k.id)}
              className={`rounded-lg border px-3 py-1.5 text-[12px] font-semibold transition-colors ${
                on
                  ? 'border-brand-400/50 bg-brand-400/15 text-brand-200'
                  : 'border-line bg-card text-text-muted hover:bg-fill-hover'
              }`}
            >
              {k.label}
            </button>
          )
        })}
      </div>

      <div className="rounded-xl border border-line bg-card p-4">
        <div className="mb-3 flex flex-wrap items-end gap-3">
          <label className="min-w-[220px] flex-1">
            <span className="mb-1 block text-[10.5px] font-bold text-text-high">
              {SIDE_LABEL[kind.target]}
            </span>
            <Select
              value={targetId == null ? '' : String(targetId)}
              onChange={(v) => setTargetId(v ? Number(v) : null)}
              size="md"
              ariaLabel={`${SIDE_LABEL[kind.target]} to bind`}
              options={
                targets.length === 0
                  ? [{ value: '', label: `No ${kind.target}s yet` }]
                  : targets.map((t) => ({ value: String(t.id), label: t.label, hint: t.sub }))
              }
            />
          </label>
          <div className="flex items-center gap-1.5 pb-2 text-text-faint">
            <ArrowLeftRight size={14} aria-hidden />
          </div>
          <div className="min-w-[220px] flex-1 pb-2">
            <span className="block text-[10.5px] font-bold text-text-high">
              {SIDE_LABEL[kind.candidate]}s
            </span>
            <span className="text-[11px] text-text-muted">
              {draft.length} of {candidates.length} bound
              {dirty && (
                <span className="ml-1 font-semibold text-amber-300">
                  · {add.length > 0 && `+${add.length}`} {remove.length > 0 && `−${remove.length}`} unsaved
                </span>
              )}
            </span>
          </div>
        </div>

        <p className="mb-3 text-[11px] leading-relaxed text-text-muted">{kind.hint}</p>

        {/* filter + batch controls */}
        <div className="mb-3 flex flex-wrap items-center gap-2">
          <label className="flex min-w-[200px] flex-1 items-center gap-2 rounded-lg border border-line-strong bg-well px-2.5 py-1.5">
            <Search size={13} className="shrink-0 text-text-muted" aria-hidden />
            <input
              value={filter}
              onChange={(e) => setFilter(e.target.value)}
              placeholder={`Filter ${kind.candidate}s…`}
              aria-label={`Filter ${kind.candidate}s`}
              className="w-full bg-transparent text-[12.5px] text-text-primary outline-none placeholder:text-text-faint"
            />
            {filter && (
              <button onClick={() => setFilter('')} aria-label="Clear filter" className="text-text-muted">
                <X size={12} aria-hidden />
              </button>
            )}
          </label>
          <Select
            value={onlyBound}
            onChange={(v) => setOnlyBound(v as 'all' | 'bound' | 'unbound')}
            size="md"
            ariaLabel="Show"
            className="w-[150px]"
            options={[
              { value: 'all', label: 'Show all' },
              { value: 'bound', label: 'Bound only' },
              { value: 'unbound', label: 'Unbound only' },
            ]}
          />
          {canManage && (
            <>
              <button
                onClick={selectShown}
                disabled={shown.length === 0}
                className="rounded-lg border border-line bg-fill px-2.5 py-1.5 text-[11.5px] font-semibold text-text-mid transition-colors hover:bg-fill-hover hover:text-text-high disabled:opacity-40"
              >
                Select {shown.length} shown
              </button>
              <button
                onClick={clearShown}
                disabled={shown.length === 0}
                className="rounded-lg border border-line bg-fill px-2.5 py-1.5 text-[11.5px] font-semibold text-text-mid transition-colors hover:bg-fill-hover hover:text-text-high disabled:opacity-40"
              >
                Clear shown
              </button>
            </>
          )}
        </div>

        {/* candidates */}
        {targetId == null ? (
          <p className="py-6 text-center text-[12px] text-text-muted">
            Create a {kind.target} first — there is nothing to bind to.
          </p>
        ) : shown.length === 0 ? (
          <p className="py-6 text-center text-[12px] text-text-muted">
            {candidates.length === 0
              ? `No ${kind.candidate}s exist yet.`
              : 'Nothing matches this filter.'}
          </p>
        ) : (
          <div className="max-h-[420px] overflow-y-auto rounded-lg border border-line">
            {shown.map((c) => {
              const on = draft.includes(c.id)
              const changed = on !== saved.includes(c.id)
              return (
                <button
                  key={c.id}
                  onClick={() => canManage && toggle(c.id)}
                  disabled={!canManage}
                  className={`flex w-full items-center gap-2.5 border-b border-line px-3 py-2 text-left transition-colors last:border-b-0 ${
                    canManage ? 'cursor-pointer hover:bg-fill-hover' : 'cursor-default'
                  } ${changed ? 'bg-amber-400/5' : ''}`}
                >
                  <span
                    className={`grid h-4 w-4 shrink-0 place-items-center rounded border ${
                      on ? 'border-brand-400 bg-brand-400 text-white' : 'border-line-strong bg-card'
                    }`}
                    aria-hidden
                  >
                    {on && <Check size={11} strokeWidth={3} />}
                  </span>
                  <span className="min-w-0 flex-1 truncate text-[12.5px] font-semibold text-text-high">
                    {c.label}
                  </span>
                  {c.sub && (
                    <span className="shrink-0 truncate text-[11px] text-text-muted">{c.sub}</span>
                  )}
                  {changed && (
                    <span className="shrink-0 text-[10px] font-bold text-amber-300">
                      {on ? 'will bind' : 'will unbind'}
                    </span>
                  )}
                </button>
              )
            })}
          </div>
        )}

        {/* apply */}
        {canManage && (
          <div className="mt-3 flex flex-wrap items-center gap-2">
            <Button variant="primary" onClick={apply} disabled={busy || !dirty}>
              {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Link2 size={13} aria-hidden />}{' '}
              Apply to {targetName}
            </Button>
            {dirty && (
              <Button variant="ghost" onClick={reset} disabled={busy}>
                Discard
              </Button>
            )}
            {msg && (
              <span className={`text-[12px] font-semibold ${msg.ok ? 'text-green-300' : 'text-red-300'}`}>
                {msg.text}
              </span>
            )}
          </div>
        )}
      </div>
    </div>
  )
}
