'use client'

/**
 * "AI activity" — everything the AI sent or tried: replies that went straight
 * out, held ones and what people decided, follow-ups, flow messages, shadow
 * drafts and the ones a guard stopped. Filter row above, newest first, each
 * row expands to the full context (what was answered, tools, checks, model,
 * the AI's original text when a person edited it).
 */

import Link from 'next/link'
import { useCallback, useEffect, useState } from 'react'
import { ChevronDown, ChevronRight, ExternalLink, Loader2, Search } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Card } from '@/components/ui/Card'
import { Select } from '@/components/ui/Select'
import { channelLabel } from '@/components/ui/ChannelLogo'
import { subscribeChatStream } from '@/lib/chatStream'
import {
  KIND_LABEL,
  STATUS_META,
  aiApi,
  decide,
  decisionFromError,
  type Draft,
  type DraftKind,
  type DraftList,
  type DraftStatus,
} from '@/lib/aiMonitor'
import {
  ContextChips,
  ConversationSnippet,
  DraftBookings,
  DraftHeader,
  HoldReasons,
  StatusBadge,
  chatHref,
} from './DraftBits'

const PAGE_SIZE = 25
const CHANNELS = ['whatsapp', 'telegram', 'facebook', 'instagram', 'email', 'website']

const inputCls =
  'h-8 w-full rounded-lg border border-line-strong bg-card pl-8 pr-3 text-[12.5px] text-text-primary outline-none transition-colors focus:border-brand-400'

function when(iso: string | null): string {
  if (!iso) return ''
  const d = new Date(iso.endsWith('Z') || /[+-]\d\d:\d\d$/.test(iso) ? iso : `${iso}Z`)
  return d.toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })
}

export function ActivityList({ entityQuery, canDecide }: { entityQuery: string; canDecide: boolean }) {
  const [status, setStatus] = useState('')
  const [kind, setKind] = useState('')
  const [channel, setChannel] = useState('')
  const [q, setQ] = useState('')
  const [query, setQuery] = useState('')
  const [page, setPage] = useState(1)
  const [data, setData] = useState<DraftList | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [open, setOpen] = useState<Set<number>>(new Set())
  const [rowMsg, setRowMsg] = useState<Record<number, string>>({})

  // debounce the search box
  useEffect(() => {
    const t = window.setTimeout(() => setQuery(q.trim()), 300)
    return () => window.clearTimeout(t)
  }, [q])
  useEffect(() => setPage(1), [status, kind, channel, query, entityQuery])

  const load = useCallback(async () => {
    setLoading(true)
    try {
      const p = new URLSearchParams({ page: String(page), page_size: String(PAGE_SIZE), sort: 'newest' })
      if (status) p.set('status', status)
      if (kind) p.set('kind', kind)
      if (channel) p.set('channel', channel)
      if (query) p.set('q', query)
      setData(await aiApi<DraftList>(`ai/monitor?${p.toString()}${entityQuery ? `&${entityQuery}` : ''}`))
      setError(null)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setLoading(false)
    }
  }, [page, status, kind, channel, query, entityQuery])

  useEffect(() => {
    void load()
    let t: number | undefined
    const unsub = subscribeChatStream(() => {
      window.clearTimeout(t)
      t = window.setTimeout(() => void load(), 600)
    })
    return () => {
      unsub()
      window.clearTimeout(t)
    }
  }, [load])

  const toggle = (id: number) =>
    setOpen((cur) => {
      const n = new Set(cur)
      if (n.has(id)) n.delete(id)
      else n.add(id)
      return n
    })

  const act = async (d: Draft, action: 'approve' | 'reject') => {
    try {
      const r = action === 'approve' ? await decide.approve(d.id) : await decide.reject(d.id, '')
      setRowMsg((m) => ({ ...m, [d.id]: r.detail }))
      await load()
    } catch (e) {
      const r = decisionFromError(e)
      setRowMsg((m) => ({ ...m, [d.id]: r?.detail ?? (e instanceof Error ? e.message : String(e)) }))
    }
  }

  const total = data?.total ?? 0
  const pages = Math.max(1, Math.ceil(total / PAGE_SIZE))
  const counts = data?.counts

  const statusOptions = [
    { value: '', label: 'Any status' },
    ...(Object.keys(STATUS_META) as DraftStatus[]).map((s) => ({
      value: s,
      label: STATUS_META[s].label,
      hint: counts ? String(counts[s] ?? 0) : undefined,
    })),
  ]

  return (
    <div className="flex flex-col gap-3">
      {/* filter row — scopes the list below */}
      <div className="flex flex-wrap items-center gap-2">
        <div className="relative min-w-[200px] flex-1 basis-[220px]">
          <Search size={14} className="pointer-events-none absolute left-2.5 top-1/2 -translate-y-1/2 text-text-faint" aria-hidden />
          <input
            value={q}
            onChange={(e) => setQ(e.target.value)}
            placeholder="Search replies, names, numbers"
            className={inputCls}
            aria-label="Search AI activity"
          />
        </div>
        <Select value={status} onChange={setStatus} options={statusOptions} ariaLabel="Status" className="w-[190px]" />
        <Select
          value={kind}
          onChange={setKind}
          ariaLabel="Kind"
          className="w-[150px]"
          options={[
            { value: '', label: 'Any kind' },
            ...(Object.keys(KIND_LABEL) as DraftKind[]).map((k) => ({ value: k, label: KIND_LABEL[k] })),
          ]}
        />
        <Select
          value={channel}
          onChange={setChannel}
          ariaLabel="Channel"
          className="w-[150px]"
          options={[{ value: '', label: 'Any channel' }, ...CHANNELS.map((c) => ({ value: c, label: channelLabel(c) }))]}
        />
        {loading && <Loader2 size={14} className="animate-spin text-text-faint" aria-label="Loading" />}
      </div>

      {error && (
        <div role="alert" className="text-[12.5px] text-[color:var(--red-ink)]">
          {error}
        </div>
      )}

      <Card className={`overflow-hidden transition-opacity ${loading && data ? 'opacity-70' : ''}`}>
        {data && data.drafts.length === 0 && (
          <div className="px-5 py-8 text-[13px] text-text-muted">
            No AI activity matches. Everything the AI writes — sent, held, rejected or stopped by a guard — is listed here.
          </div>
        )}
        <ul className="divide-y divide-line">
          {(data?.drafts ?? []).map((d) => {
            const expanded = open.has(d.id)
            return (
              <li key={d.id}>
                <button
                  type="button"
                  onClick={() => toggle(d.id)}
                  aria-expanded={expanded}
                  className="flex w-full items-start gap-3 px-4 py-3 text-left transition-colors hover:bg-fill-subtle"
                >
                  <span className="mt-0.5 text-text-faint">
                    {expanded ? <ChevronDown size={15} aria-hidden /> : <ChevronRight size={15} aria-hidden />}
                  </span>
                  <span className="min-w-0 flex-1">
                    <DraftHeader d={d} right={<StatusBadge status={d.status} />} />
                    <span className="mt-1 line-clamp-2 block whitespace-pre-wrap break-words text-[13px] leading-snug text-text-high">
                      {d.content}
                    </span>
                    <DraftBookings d={d} inline />
                    {(d.decided_by && d.decided_by !== 'auto') || d.error ? (
                      <span className="mt-1 block text-[11.5px] text-text-muted">
                        {d.decided_by && d.decided_by !== 'auto' && d.decided_by !== 'system' ? `${d.decided_by}` : ''}
                        {d.reject_reason ? ` — “${d.reject_reason}”` : ''}
                        {d.error ? `${d.decided_by && d.decided_by !== 'auto' && d.decided_by !== 'system' ? ' · ' : ''}${d.error}` : ''}
                      </span>
                    ) : null}
                  </span>
                </button>
                {expanded && (
                  <div className="grid gap-4 border-t border-line bg-well/60 px-4 py-4 md:grid-cols-2 md:pl-11">
                    <section className="min-w-0">
                      <div className="eyebrow mb-1.5">Answering</div>
                      {d.recent?.length ? (
                        <ConversationSnippet messages={d.recent} />
                      ) : (d.context.answering ?? []).length ? (
                        <ul className="flex flex-col gap-1">
                          {(d.context.answering ?? []).map((a) => (
                            <li key={a.id} className="rounded-lg border border-line bg-card px-2.5 py-1.5 text-[12.5px] text-text-high">
                              {a.text}
                            </li>
                          ))}
                        </ul>
                      ) : (
                        <p className="text-[12.5px] text-text-muted">
                          {d.kind === 'follow_up' ? 'Nothing — the customer went quiet.' : 'No customer message recorded.'}
                        </p>
                      )}
                    </section>
                    <section className="min-w-0">
                      <div className="eyebrow mb-1.5">{d.original_content ? 'Sent (after a person edited it)' : 'The AI wrote'}</div>
                      <p className="whitespace-pre-wrap break-words rounded-lg border border-line bg-card px-3 py-2 text-[13px] leading-relaxed text-text-high">
                        {d.content}
                      </p>
                      {d.original_content && (
                        <>
                          <div className="eyebrow mb-1.5 mt-3">The AI’s original</div>
                          <p className="whitespace-pre-wrap break-words rounded-lg border border-dashed border-line-strong px-3 py-2 text-[12.5px] leading-relaxed text-text-muted">
                            {d.original_content}
                          </p>
                        </>
                      )}
                      <div className="mt-3 flex flex-col gap-2">
                        <HoldReasons reasons={d.reasons} />
                        {!d.reasons.length && d.flags.length > 0 && (
                          <p className="text-[11.5px] text-text-muted">Signals noticed (not held): {d.flags.join('; ')}</p>
                        )}
                        <DraftBookings d={d} />
                        <ContextChips d={d} />
                        <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-0.5 text-[11.5px] text-text-muted">
                          <dt>Written</dt>
                          <dd>{when(d.created_at)}</dd>
                          {d.decided_at && d.decided_by !== 'auto' && (
                            <>
                              <dt>Decided</dt>
                              <dd>
                                {when(d.decided_at)} by {d.decided_by}
                              </dd>
                            </>
                          )}
                          {d.sent_at && (
                            <>
                              <dt>Delivered</dt>
                              <dd>{when(d.sent_at)}</dd>
                            </>
                          )}
                        </dl>
                        <div className="flex flex-wrap items-center gap-2">
                          {canDecide && (d.status === 'pending' || d.status === 'shadow') && (
                            <>
                              <Button size="sm" variant="secondary" onClick={() => void act(d, 'approve')}>
                                {d.status === 'shadow' ? 'Use this reply' : 'Approve & send'}
                              </Button>
                              <Button size="sm" variant="ghost" onClick={() => void act(d, 'reject')}>
                                {d.status === 'shadow' ? 'Mark as wrong' : 'Reject'}
                              </Button>
                            </>
                          )}
                          <Link
                            href={chatHref(d)}
                            className="inline-flex items-center gap-1 text-[12.5px] font-semibold text-text-mid hover:text-text-high hover:underline"
                          >
                            Open chat <ExternalLink size={12} aria-hidden />
                          </Link>
                        </div>
                        {rowMsg[d.id] && <p className="text-[12px] text-text-mid">{rowMsg[d.id]}</p>}
                      </div>
                    </section>
                  </div>
                )}
              </li>
            )
          })}
        </ul>
      </Card>

      {total > PAGE_SIZE && (
        <div className="flex items-center justify-between gap-2 text-[12.5px] text-text-muted">
          <span>
            {(page - 1) * PAGE_SIZE + 1}–{Math.min(total, page * PAGE_SIZE)} of {total}
          </span>
          <span className="flex gap-2">
            <Button size="sm" variant="secondary" disabled={page <= 1} onClick={() => setPage((p) => p - 1)}>
              Previous
            </Button>
            <Button size="sm" variant="secondary" disabled={page >= pages} onClick={() => setPage((p) => p + 1)}>
              Next
            </Button>
          </span>
        </div>
      )}
    </div>
  )
}
