'use client'

import { useEffect, useMemo, useState } from 'react'
import { ArrowLeft, ChevronLeft, ChevronRight, Search, X } from 'lucide-react'
import { Select } from '@/components/ui/Select'
import { describeStep } from '@/lib/flow/describe'
import { fetchRun, fetchRuns, type RunsPage } from '@/lib/flow/api'
import type { FlowNode, FlowRunPathStep, FlowRunRow, FlowStats } from '@/lib/flow/types'
import DailyBars from './DailyBars'
import { useFlowView } from './FlowViewContext'
import { inputCls } from './fields'

/**
 * The Runs panel: is this flow doing what it should? Totals for the period,
 * people entering per day, every step with how many reached it and the
 * drop-off from the step before, why runs failed — and, a click away, each
 * contact's own path through the flow.
 */

const STATUS_FILTERS = [
  { value: '', label: 'Everyone' },
  { value: 'running,waiting', label: 'In progress' },
  { value: 'done', label: 'Finished' },
  { value: 'failed', label: 'Failed' },
]

const STATUS_COPY: Record<string, { label: string; cls: string }> = {
  running: { label: 'Running', cls: 'text-[color:var(--blue-ink)] border-blue/40' },
  waiting: { label: 'Waiting', cls: 'text-[color:var(--amber-ink)] border-amber/40' },
  done: { label: 'Finished', cls: 'text-[color:var(--green-ink)] border-green/40' },
  failed: { label: 'Failed', cls: 'text-[color:var(--red-ink)] border-red/40' },
  cancelled: { label: 'Cancelled', cls: 'text-text-muted border-line-strong' },
}

const KIND_COPY: Record<string, string> = {
  entered: 'Reached',
  completed: 'Done',
  waiting: 'Waiting',
  failed: 'Failed',
  skipped: 'Skipped',
  followup: 'Follow-up',
}

const when = (iso: string | null) =>
  iso
    ? new Date(iso).toLocaleString('en-GB', { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' })
    : ''

function StatusPill({ status }: { status: string }) {
  const s = STATUS_COPY[status] ?? { label: status, cls: 'text-text-muted border-line-strong' }
  return <span className={`shrink-0 rounded-full border px-2 py-px text-[10.5px] font-bold uppercase tracking-[0.05em] ${s.cls}`}>{s.label}</span>
}

export default function RunsPanel({
  flowId,
  nodes,
  stats,
  days,
  periodLabel,
  onClose,
  onSelectStep,
}: {
  flowId: string
  nodes: FlowNode[]
  stats: FlowStats | null
  days: number
  periodLabel: string
  onClose: () => void
  onSelectStep: (id: string) => void
}) {
  const [tab, setTab] = useState<'overview' | 'people'>('overview')
  const [nodeFilter, setNodeFilter] = useState<string>('')
  const [runId, setRunId] = useState<number | null>(null)

  const showPeople = (node = '') => {
    setNodeFilter(node)
    setRunId(null)
    setTab('people')
  }

  return (
    <aside className="flex h-full w-full flex-col bg-card" aria-label="Runs">
      <header className="flex items-start gap-3 border-b border-line px-4 py-3">
        <div className="min-w-0 flex-1">
          <div className="eyebrow">Runs · {periodLabel}</div>
          <div className="display mt-0.5 text-[17px] leading-snug text-text-heading">Is it working?</div>
        </div>
        <button
          type="button"
          onClick={onClose}
          aria-label="Close runs"
          className="grid h-8 w-8 shrink-0 place-items-center rounded-lg text-text-muted hover:bg-fill-hover hover:text-text-high"
        >
          <X size={16} aria-hidden />
        </button>
      </header>
      <div className="flex gap-1 border-b border-line px-4 pt-2">
        {(
          [
            ['overview', 'Overview'],
            ['people', 'Each contact'],
          ] as const
        ).map(([id, label]) => (
          <button
            key={id}
            type="button"
            onClick={() => (id === 'people' ? showPeople(nodeFilter) : setTab(id))}
            className={`-mb-px border-b-2 px-2 pb-2 text-[12.5px] font-semibold ${
              tab === id ? 'border-text-primary text-text-heading' : 'border-transparent text-text-muted hover:text-text-high'
            }`}
          >
            {label}
          </button>
        ))}
      </div>
      <div className="min-h-0 flex-1 overflow-y-auto px-4 py-4">
        {tab === 'overview' ? (
          <Overview stats={stats} nodes={nodes} days={days} onPeople={showPeople} onSelectStep={onSelectStep} />
        ) : runId !== null ? (
          <RunDetail id={runId} nodes={nodes} onBack={() => setRunId(null)} />
        ) : (
          <People flowId={flowId} nodes={nodes} days={days} node={nodeFilter} onClearNode={() => setNodeFilter('')} onOpen={setRunId} />
        )}
      </div>
    </aside>
  )
}

function Tile({ label, value, tone }: { label: string; value: number | undefined; tone?: string }) {
  return (
    <div className="rounded-lg border border-line bg-well px-3 py-2">
      <div className="text-[11px] text-text-muted">{label}</div>
      <div className={`mt-0.5 text-[20px] font-semibold leading-none ${tone ?? 'text-text-heading'}`}>{(value ?? 0).toLocaleString()}</div>
    </div>
  )
}

function Overview({
  stats,
  nodes,
  days,
  onPeople,
  onSelectStep,
}: {
  stats: FlowStats | null
  nodes: FlowNode[]
  days: number
  onPeople: (node?: string) => void
  onSelectStep: (id: string) => void
}) {
  const { describeCtx } = useFlowView()
  const byId = useMemo(() => new Map(nodes.map((n) => [n.id, n])), [nodes])
  const depth = useMemo(() => {
    const d = new Map<string, number>()
    for (const o of stats?.order ?? []) d.set(o.nodeId, o.parent ? (d.get(o.parent) ?? 0) + 1 : 0)
    return d
  }, [stats])

  if (!stats) {
    return (
      <p className="text-[12.5px] text-text-muted">
        No numbers yet. Once the flow is saved and live, every step counts the people who reach it, day by day.
      </p>
    )
  }
  const t = stats.totals
  const hasFollowUps = nodes.some((n) => n.type === 'followup')
  return (
    <div className="flex flex-col gap-5">
      <div className="grid grid-cols-2 gap-2">
        <Tile label="Started" value={t.started} />
        <Tile label="Finished" value={t.completed} />
        <Tile label="Waiting now" value={t.waitingNow} />
        <Tile label="Failed" value={t.failed} tone={t.failed ? 'text-[color:var(--red-ink)]' : undefined} />
      </div>

      {days > 1 && (
        <DailyBars data={stats.series.map((s) => ({ day: s.day, value: s.started }))} label="People who entered, per day" />
      )}

      <section>
        <div className="eyebrow mb-2">Each step</div>
        <div className="overflow-x-auto rounded-lg border border-line">
          <table className="w-full min-w-[340px] text-[12px]">
            <thead>
              <tr className="border-b border-line bg-well text-left text-[10.5px] uppercase tracking-[0.05em] text-text-muted">
                <th className="px-2.5 py-1.5 font-bold">Step</th>
                <th className="px-2 py-1.5 text-right font-bold">Reached</th>
                <th className="px-2 py-1.5 text-right font-bold">Drop-off</th>
                <th className="px-2.5 py-1.5 text-right font-bold">Now</th>
              </tr>
            </thead>
            <tbody>
              {stats.order.map((o) => {
                const node = byId.get(o.nodeId)
                const s = stats.nodes[o.nodeId]
                const title = node ? describeStep(node, describeCtx).title : o.label || o.nodeId
                return (
                  <tr key={o.nodeId} className="border-b border-line last:border-0 hover:bg-fill-subtle">
                    <td className="max-w-0 px-2.5 py-1.5">
                      <button
                        type="button"
                        onClick={() => onSelectStep(o.nodeId)}
                        className="block w-full truncate text-left text-text-high hover:underline"
                        style={{ paddingLeft: Math.min(4, depth.get(o.nodeId) ?? 0) * 8 }}
                        title={title}
                      >
                        {title}
                      </button>
                    </td>
                    <td className="px-2 py-1.5 text-right tabular-nums">
                      <button type="button" className="font-semibold text-text-high hover:underline" onClick={() => onPeople(o.nodeId)} title="See who reached this step">
                        {o.entered.toLocaleString()}
                      </button>
                    </td>
                    <td className={`px-2 py-1.5 text-right tabular-nums ${o.dropPct && o.dropPct >= 50 ? 'text-[color:var(--amber-ink)]' : 'text-text-muted'}`}>
                      {o.dropPct === null ? '—' : `${o.dropPct}%`}
                    </td>
                    <td className="px-2.5 py-1.5 text-right tabular-nums text-text-muted">
                      {s?.waitingNow ? <span className="text-[color:var(--amber-ink)]">{s.waitingNow} waiting</span> : s?.failed ? <span className="text-[color:var(--red-ink)]">{s.failed} failed</span> : '—'}
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
        <div className="mt-1.5 text-[11px] text-text-muted">Drop-off = people who reached the step before but not this one. On a branch it is the share that went the other way.</div>
      </section>

      {hasFollowUps && (
        <section>
          <div className="eyebrow mb-1">Follow-ups</div>
          <div className="text-[12.5px] text-text-mid">
            {t.followupsSent.toLocaleString()} sent · {t.repliedAfterFollowUp.toLocaleString()} replied after a follow-up
          </div>
        </section>
      )}

      {(stats.failures.length > 0 || stats.skips.length > 0) && (
        <section>
          <div className="eyebrow mb-2">What went wrong</div>
          <ul className="flex flex-col gap-1.5">
            {[...stats.failures.map((f) => ({ ...f, fatal: true })), ...stats.skips.map((f) => ({ ...f, fatal: false }))].map((f, i) => (
              <li key={i} className="rounded-lg border border-line px-3 py-2">
                <div className="flex items-start justify-between gap-2">
                  <span className={`text-[12.5px] ${f.fatal ? 'text-[color:var(--red-ink)]' : 'text-text-high'}`}>{f.reason}</span>
                  <span className="shrink-0 text-[12px] font-semibold tabular-nums text-text-high">×{f.count}</span>
                </div>
                <div className="mt-0.5 text-[11px] text-text-muted">
                  {f.fatal ? 'Stopped the run' : 'Skipped, run continued'} · at {byId.get(f.nodeId) ? describeStep(byId.get(f.nodeId)!, describeCtx).title : f.label || f.nodeId}
                </div>
              </li>
            ))}
          </ul>
        </section>
      )}

      <button
        type="button"
        onClick={() => onPeople('')}
        className="rounded-lg border border-line-strong bg-card px-3 py-2 text-[12.5px] font-semibold text-text-high hover:bg-fill-hover"
      >
        See each contact&apos;s path
      </button>
    </div>
  )
}

function People({
  flowId,
  nodes,
  days,
  node,
  onClearNode,
  onOpen,
}: {
  flowId: string
  nodes: FlowNode[]
  days: number
  node: string
  onClearNode: () => void
  onOpen: (id: number) => void
}) {
  const { describeCtx } = useFlowView()
  const [status, setStatus] = useState('')
  const [q, setQ] = useState('')
  const [debounced, setDebounced] = useState('')
  const [page, setPage] = useState(1)
  const [data, setData] = useState<RunsPage | null>(null)
  const [loading, setLoading] = useState(false)

  useEffect(() => {
    const t = setTimeout(() => setDebounced(q.trim()), 300)
    return () => clearTimeout(t)
  }, [q])
  useEffect(() => setPage(1), [status, debounced, node, days])
  useEffect(() => {
    let alive = true
    setLoading(true)
    fetchRuns({ flow: flowId, status, contact: debounced, node, page, days }).then((r) => {
      if (!alive) return
      setData(r)
      setLoading(false)
    })
    return () => {
      alive = false
    }
  }, [flowId, status, debounced, node, page, days])

  const nodeTitle = (id: string) => {
    const n = nodes.find((x) => x.id === id)
    return n ? describeStep(n, describeCtx).title : id
  }

  return (
    <div className="flex flex-col gap-3">
      <div className="flex flex-col gap-2">
        <div className="relative">
          <Search size={13} className="pointer-events-none absolute left-2.5 top-1/2 -translate-y-1/2 text-text-faint" aria-hidden />
          <input type="search" value={q} onChange={(e) => setQ(e.target.value)} placeholder="Find a contact…" aria-label="Find a contact" className={`${inputCls} pl-8`} />
        </div>
        <Select value={status} onChange={setStatus} options={STATUS_FILTERS} size="md" ariaLabel="Status" />
        {node && (
          <span className="inline-flex w-fit max-w-full items-center gap-1 rounded-md border border-line-strong bg-well py-0.5 pl-2 pr-1 text-[11.5px] text-text-high">
            <span className="truncate">Reached: {nodeTitle(node)}</span>
            <button type="button" aria-label="Clear step filter" onClick={onClearNode} className="grid h-4 w-4 place-items-center rounded hover:bg-fill-hover">
              <X size={11} aria-hidden />
            </button>
          </span>
        )}
      </div>

      {loading && !data ? (
        <div className="text-[12.5px] text-text-muted">Loading…</div>
      ) : !data || data.runs.length === 0 ? (
        <div className="text-[12.5px] text-text-muted">No one matches in this period.</div>
      ) : (
        <>
          <ul className="flex flex-col divide-y divide-line rounded-lg border border-line">
            {data.runs.map((r: FlowRunRow) => (
              <li key={r.id}>
                <button type="button" onClick={() => onOpen(r.id)} className="flex w-full flex-col gap-0.5 px-3 py-2 text-left hover:bg-fill-subtle">
                  <span className="flex items-center justify-between gap-2">
                    <span className="truncate text-[13px] font-semibold text-text-high">{r.contactName || `Contact #${r.contactId}`}</span>
                    <StatusPill status={r.status} />
                  </span>
                  <span className="truncate text-[11.5px] text-text-muted">
                    {r.status === 'done' || r.status === 'failed'
                      ? `${r.detail || r.status} · ${when(r.finishedAt)}`
                      : `At ${r.currentLabel || nodeTitle(r.currentNode)}${r.waitFor === 'reply' ? ' · waiting for a reply' : ''} · since ${when(r.startedAt)}`}
                  </span>
                </button>
              </li>
            ))}
          </ul>
          {data.pages > 1 && (
            <div className="flex items-center justify-between text-[12px] text-text-muted">
              <button type="button" disabled={page <= 1} onClick={() => setPage(page - 1)} className="inline-flex items-center gap-1 disabled:opacity-40">
                <ChevronLeft size={13} aria-hidden /> Newer
              </button>
              <span>
                Page {data.page} of {data.pages} · {data.count.toLocaleString()} runs
              </span>
              <button type="button" disabled={page >= data.pages} onClick={() => setPage(page + 1)} className="inline-flex items-center gap-1 disabled:opacity-40">
                Older <ChevronRight size={13} aria-hidden />
              </button>
            </div>
          )}
        </>
      )}
    </div>
  )
}

function RunDetail({ id, nodes, onBack }: { id: number; nodes: FlowNode[]; onBack: () => void }) {
  const { describeCtx } = useFlowView()
  const stepTitle = (p: FlowRunPathStep) => {
    const n = nodes.find((x) => x.id === p.nodeId)
    return n ? describeStep(n, describeCtx).title : p.label || p.nodeId
  }
  const [data, setData] = useState<Awaited<ReturnType<typeof fetchRun>>>(null)
  const [loading, setLoading] = useState(true)
  const [showLog, setShowLog] = useState(false)
  useEffect(() => {
    let alive = true
    setLoading(true)
    fetchRun(id).then((r) => {
      if (!alive) return
      setData(r)
      setLoading(false)
    })
    return () => {
      alive = false
    }
  }, [id])

  return (
    <div className="flex flex-col gap-3">
      <button type="button" onClick={onBack} className="inline-flex w-fit items-center gap-1 text-[12px] font-semibold text-text-muted hover:text-text-high">
        <ArrowLeft size={13} aria-hidden /> All contacts
      </button>
      {loading ? (
        <div className="text-[12.5px] text-text-muted">Loading…</div>
      ) : !data ? (
        <div className="text-[12.5px] text-text-muted">This run is not available.</div>
      ) : (
        <>
          <div className="flex items-start justify-between gap-2">
            <div className="min-w-0">
              <div className="display truncate text-[17px] text-text-heading">{data.run.contactName || `Contact #${data.run.contactId}`}</div>
              <div className="text-[11.5px] text-text-muted">
                Started {when(data.run.startedAt)}
                {data.run.finishedAt && ` · ended ${when(data.run.finishedAt)}`}
              </div>
            </div>
            <StatusPill status={data.run.status} />
          </div>
          {data.run.detail && (
            <div className={`rounded-lg border px-3 py-2 text-[12.5px] ${data.run.status === 'failed' ? 'border-red/40 text-[color:var(--red-ink)]' : 'border-line text-text-high'}`}>
              {data.run.status === 'failed' ? 'Why it failed: ' : 'Outcome: '}
              {data.run.detail}
            </div>
          )}
          {data.run.lastReply && (
            <div className="rounded-lg border border-line bg-well px-3 py-2 text-[12.5px] text-text-mid">
              <span className="eyebrow mr-1">Last reply</span> {data.run.lastReply}
            </div>
          )}
          <ol className="relative ml-1.5 border-l border-line-strong">
            {data.path.map((p: FlowRunPathStep, i) => (
              <li key={i} className="mb-3 ml-4 last:mb-0">
                <span
                  className={`absolute -left-[5px] mt-1.5 h-2.5 w-2.5 rounded-full border-2 border-card ${
                    p.kind === 'failed' ? 'bg-red' : p.kind === 'waiting' ? 'bg-amber' : p.kind === 'skipped' ? 'bg-text-faint' : p.kind === 'followup' ? 'bg-blue' : 'bg-text-primary'
                  }`}
                  aria-hidden
                />
                <div className="flex flex-wrap items-baseline gap-x-2">
                  <span className="text-[12.5px] font-semibold text-text-high">{stepTitle(p)}</span>
                  <span className="text-[11px] text-text-muted">
                    {KIND_COPY[p.kind] ?? p.kind} · {when(p.at)}
                  </span>
                </div>
                {p.detail && <div className="text-[11.5px] text-text-muted">{p.detail}</div>}
              </li>
            ))}
            {data.path.length === 0 && <li className="ml-4 text-[12px] text-text-muted">No step events recorded for this run (it predates tracking).</li>}
          </ol>
          <button type="button" onClick={() => setShowLog(!showLog)} className="w-fit text-[12px] font-semibold text-text-muted hover:text-text-high">
            {showLog ? 'Hide' : 'Show'} the engine log ({data.log.length})
          </button>
          {showLog && (
            <pre className="max-h-[260px] overflow-auto whitespace-pre-wrap rounded-lg border border-line bg-well p-2.5 text-[11px] leading-relaxed text-text-mid">
              {data.log.map((l) => `${when(l.t)}  ${l.node}  ${l.evt}`).join('\n')}
            </pre>
          )}
        </>
      )}
    </div>
  )
}
