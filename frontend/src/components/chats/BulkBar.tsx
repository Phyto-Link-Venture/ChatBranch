'use client'

import { useState } from 'react'
import { Bot, BotOff, CheckCircle2, Loader2, X } from 'lucide-react'
import { Select } from '@/components/ui/Select'
import { bulkThreads, paramsToFilter, type BulkAction } from '@/lib/aiControls'
import type { UiAgent } from './inboxTypes'

/**
 * The chat list's bulk bar (select mode): AI on · AI off · Resolve · Assign
 * over the picked conversations — or, with "Select all", everything the
 * current filter matches (the server re-runs the same filter, scoped like the
 * list, at most 1000). POST /api/inbox/threads/bulk; needs chats.manage.
 */
export default function BulkBar({
  selected,
  total,
  allMatching,
  onAllMatching,
  listParams,
  agents,
  onClear,
  onDone,
}: {
  /** picked rows as `channel:key` */
  selected: string[]
  /** how many conversations the current filter matches */
  total: number | null
  /** true = act on everything the filter matches, not just the picked rows */
  allMatching: boolean
  onAllMatching: (v: boolean) => void
  /** the list request's query string (same filters as GET /api/inbox/threads) */
  listParams: string
  agents: UiAgent[]
  onClear: () => void
  onDone: () => void
}) {
  const [busy, setBusy] = useState<BulkAction | null>(null)
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)
  const [assignee, setAssignee] = useState('')

  const count = allMatching ? (total ?? 0) : selected.length
  const disabled = !!busy || count === 0

  const run = async (action: BulkAction, value?: string) => {
    setBusy(action)
    setMsg(null)
    try {
      const target = allMatching
        ? { filter: paramsToFilter(new URLSearchParams(listParams)) }
        : { threads: selected }
      const r = await bulkThreads(target, action, value)
      const what =
        action === 'ai_on'
          ? 'AI turned on'
          : action === 'ai_off'
            ? 'AI turned off'
            : action === 'resolve'
              ? 'Resolved'
              : action === 'reopen'
                ? 'Reopened'
                : value
                  ? `Assigned to ${agents.find((a) => a.username === value)?.name || value}`
                  : 'Unassigned'
      setMsg({
        ok: true,
        text: `${what} · ${r.matched} conversation${r.matched === 1 ? '' : 's'}${r.skipped.length ? ` (${r.skipped.length} skipped)` : ''}`,
      })
      onDone()
    } catch (e) {
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    } finally {
      setBusy(null)
    }
  }

  const btn =
    'inline-flex h-8 items-center gap-1.5 rounded-lg border border-line-strong bg-card px-2.5 text-[12.5px] font-semibold text-text-high transition-colors hover:bg-fill-hover disabled:cursor-not-allowed disabled:opacity-50'

  return (
    <div className="shrink-0 border-t border-line-strong bg-surface px-3 pb-3 pt-2.5" role="region" aria-label="Bulk actions">
      {msg && (
        <div
          role={msg.ok ? 'status' : 'alert'}
          className={`mb-2 rounded-md px-2.5 py-1.5 text-[12px] ${
            msg.ok ? 'bg-green/10 text-[color:var(--green-ink)]' : 'bg-red/10 text-[color:var(--red-ink)]'
          }`}
        >
          {msg.text}
        </div>
      )}
      <div className="flex items-center gap-2 text-[12.5px]">
        <span className="font-semibold text-text-heading">
          {count} selected
        </span>
        {total !== null && total > selected.length && (
          <button
            type="button"
            onClick={() => onAllMatching(!allMatching)}
            className="truncate font-semibold text-[color:var(--brand-ink)] hover:underline"
          >
            {allMatching ? 'Only the ones I picked' : `Select all ${total} in this filter`}
          </button>
        )}
        <button
          type="button"
          onClick={onClear}
          aria-label="Clear selection"
          className="ml-auto grid h-7 w-7 shrink-0 place-items-center rounded-md text-text-muted hover:bg-fill-hover hover:text-text-high"
        >
          <X size={14} aria-hidden />
        </button>
      </div>
      {allMatching && total !== null && total > 1000 && (
        <div className="mt-1 text-[11.5px] text-[color:var(--amber-ink)]">
          Bulk actions take at most 1000 conversations — narrow the filter first.
        </div>
      )}
      <div className="mt-2 flex flex-wrap items-center gap-1.5">
        <button type="button" className={btn} disabled={disabled} onClick={() => run('ai_on')}>
          {busy === 'ai_on' ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Bot size={13} aria-hidden />} AI on
        </button>
        <button type="button" className={btn} disabled={disabled} onClick={() => run('ai_off')}>
          {busy === 'ai_off' ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <BotOff size={13} aria-hidden />} AI off
        </button>
        <button type="button" className={btn} disabled={disabled} onClick={() => run('resolve')}>
          {busy === 'resolve' ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <CheckCircle2 size={13} aria-hidden />}{' '}
          Resolve
        </button>
        <div className="flex min-w-[180px] flex-1 items-center gap-1.5">
          <Select
            value={assignee}
            onChange={setAssignee}
            options={[
              { value: '', label: 'Assign to…' },
              { value: '__none__', label: 'Nobody (unassign)' },
              ...agents.map((a) => ({ value: a.username, label: a.name || a.username, hint: a.role })),
            ]}
            size="sm"
            ariaLabel="Assign to"
            className="min-w-0 flex-1"
            searchable={agents.length > 6}
          />
          <button
            type="button"
            className={btn}
            disabled={disabled || !assignee}
            onClick={() => run('assign', assignee === '__none__' ? '' : assignee)}
          >
            {busy === 'assign' && <Loader2 size={13} className="animate-spin" aria-hidden />} Assign
          </button>
        </div>
      </div>
    </div>
  )
}
