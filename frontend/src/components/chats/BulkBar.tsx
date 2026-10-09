'use client'

import { useState } from 'react'
import { Bot, BotOff, CheckCircle2, Loader2, X } from 'lucide-react'
import { Select } from '@/components/ui/Select'
import { bulkThreads, paramsToFilter, type BulkAction } from '@/lib/aiControls'
import { roleLabel, type UiAgent } from './inboxTypes'
import { useT } from '@/i18n'

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
  const t = useT()
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
          ? t('chats.bulk.aiTurnedOn')
          : action === 'ai_off'
            ? t('chats.bulk.aiTurnedOff')
            : action === 'resolve'
              ? t('chats.bulk.resolved')
              : action === 'reopen'
                ? t('chats.bulk.reopened')
                : value
                  ? t('chats.bulk.assignedTo', { name: agents.find((a) => a.username === value)?.name || value })
                  : t('chats.bulk.unassigned')
      const text = t('chats.bulk.result', { what, count: r.matched })
      setMsg({
        ok: true,
        text: r.skipped.length ? t('chats.bulk.skipped', { text, count: r.skipped.length }) : text,
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
    <div className="shrink-0 border-t border-line-strong bg-surface px-3 pb-3 pt-2.5" role="region" aria-label={t('chats.bulk.regionAria')}>
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
        <span className="shrink-0 font-semibold text-text-heading">{t('common.counts.selected', { count })}</span>
        {total !== null && total > selected.length && (
          <button
            type="button"
            onClick={() => onAllMatching(!allMatching)}
            className="truncate font-semibold text-[color:var(--brand-ink)] hover:underline"
          >
            {allMatching ? t('chats.bulk.onlyPicked') : t('chats.bulk.selectAllN', { count: total })}
          </button>
        )}
        <button
          type="button"
          onClick={onClear}
          aria-label={t('chats.bulk.clearSelection')}
          className="ml-auto grid h-7 w-7 shrink-0 place-items-center rounded-md text-text-muted hover:bg-fill-hover hover:text-text-high"
        >
          <X size={14} aria-hidden />
        </button>
      </div>
      {allMatching && total !== null && total > 1000 && (
        <div className="mt-1 text-[11.5px] text-[color:var(--amber-ink)]">
          {t('chats.bulk.max1000')}
        </div>
      )}
      <div className="mt-2 flex flex-wrap items-center gap-1.5">
        <button type="button" className={btn} disabled={disabled} onClick={() => run('ai_on')}>
          {busy === 'ai_on' ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Bot size={13} aria-hidden />} {t('chats.bulk.aiOn')}
        </button>
        <button type="button" className={btn} disabled={disabled} onClick={() => run('ai_off')}>
          {busy === 'ai_off' ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <BotOff size={13} aria-hidden />} {t('chats.bulk.aiOff')}
        </button>
        <button type="button" className={btn} disabled={disabled} onClick={() => run('resolve')}>
          {busy === 'resolve' ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <CheckCircle2 size={13} aria-hidden />}{' '}
          {t('chats.bulk.resolve')}
        </button>
        <div className="flex min-w-[180px] flex-1 items-center gap-1.5">
          <Select
            value={assignee}
            onChange={setAssignee}
            options={[
              { value: '', label: t('chats.bulk.assignTo') },
              { value: '__none__', label: t('chats.bulk.nobody') },
              ...agents.map((a) => ({ value: a.username, label: a.name || a.username, hint: roleLabel(t, a.role) })),
            ]}
            size="sm"
            ariaLabel={t('chats.bulk.assignAria')}
            className="min-w-0 flex-1"
            searchable={agents.length > 6}
          />
          <button
            type="button"
            className={btn}
            disabled={disabled || !assignee}
            onClick={() => run('assign', assignee === '__none__' ? '' : assignee)}
          >
            {busy === 'assign' && <Loader2 size={13} className="animate-spin" aria-hidden />} {t('chats.bulk.assign')}
          </button>
        </div>
      </div>
    </div>
  )
}
