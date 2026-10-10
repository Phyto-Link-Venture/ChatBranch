'use client'

import { useEffect, useMemo, useState } from 'react'
import { ArrowLeft, ChevronLeft, ChevronRight, Search, X } from 'lucide-react'
import { Select } from '@/components/ui/Select'
import { useFormat, useT, type MessageKey, type TFunction } from '@/i18n'
import { describeStep } from '@/lib/flow/describe'
import { fetchRun, fetchRuns, type RunsPage } from '@/lib/flow/api'
import { aiDecisionText, isAiWaitingNote } from '@/lib/flow/runText'
import type { FlowNode, FlowRunPathStep, FlowRunRow, FlowStats } from '@/lib/flow/types'
import DailyBars from './DailyBars'
import { useFlowView } from './FlowViewContext'
import { inputCls } from './fields'

/**
 * The Runs panel: is this flow doing what it should? Totals for the period,
 * people entering per day, every step with how many reached it and the
 * drop-off from the step before, why runs failed — and, a click away, each
 * contact's own path through the flow. Reasons and details the engine wrote
 * (failure reasons, outcome codes, the engine log) are shown as stored — except
 * an AI decision's verdict ("AI decided YES (0.82): …"), which is said in the
 * dashboard language with the model's reason as written (lib/flow/runText).
 */

const STATUS_FILTERS: { value: string; lbl: MessageKey }[] = [
  { value: '', lbl: 'flows.runs.filterEveryone' },
  { value: 'running,waiting', lbl: 'flows.runs.filterInProgress' },
  { value: 'done', lbl: 'flows.runs.statusDone' },
  { value: 'failed', lbl: 'flows.runs.statusFailed' },
]

const STATUS_COPY: Record<string, { lbl: MessageKey; cls: string }> = {
  running: { lbl: 'flows.runs.statusRunning', cls: 'text-[color:var(--blue-ink)] border-blue/40' },
  waiting: { lbl: 'flows.runs.statusWaiting', cls: 'text-[color:var(--amber-ink)] border-amber/40' },
  done: { lbl: 'flows.runs.statusDone', cls: 'text-[color:var(--green-ink)] border-green/40' },
  failed: { lbl: 'flows.runs.statusFailed', cls: 'text-[color:var(--red-ink)] border-red/40' },
  cancelled: { lbl: 'flows.runs.statusCancelled', cls: 'text-text-muted border-line-strong' },
}

type StepStatus = 'reached' | 'done' | 'waiting' | 'failed'
const STATUS_WORD: Record<StepStatus, MessageKey> = {
  reached: 'flows.runs.stepReached',
  done: 'flows.runs.stepDone',
  waiting: 'flows.runs.statusWaiting',
  failed: 'flows.runs.statusFailed',
}

/** One row per arrival at a step: its events folded together (reached →
 * waiting → follow-ups → done), with every detail worth reading. */
function groupPath(t: TFunction, pct: (n: number) => string, path: FlowRunPathStep[]) {
  const groups: { first: FlowRunPathStep; status: StepStatus; details: string[] }[] = []
  for (const p of path) {
    let g = groups[groups.length - 1]
    if (!g || p.kind === 'entered' || g.first.nodeId !== p.nodeId) {
      g = { first: p, status: 'reached', details: [] }
      groups.push(g)
    }
    if (p.kind === 'failed') g.status = 'failed'
    else if (p.kind === 'completed' && g.status !== 'failed') g.status = 'done'
    else if (p.kind === 'waiting' && g.status === 'reached') g.status = 'waiting'
    const d = p.detail
    if (d && !g.details.includes(d)) g.details.push(d)
  }
  for (const g of groups) {
    // a step finished later drops its stale "waiting …" note (the engine writes it in English)
    if (g.status === 'done') g.details = g.details.filter((d) => !d.startsWith('waiting') && !isAiWaitingNote(d))
    g.details = g.details.map((d) =>
      d === 'yes' || d === 'no'
        ? t('flows.runs.answer', { answer: t(d === 'yes' ? 'common.status.yes' : 'common.status.no') })
        : (aiDecisionText(t, pct, d) ?? d),
    )
  }
  return groups
}

function StatusPill({ status }: { status: string }) {
  const t = useT()
  const s = STATUS_COPY[status]
  return (
    <span
      className={`shrink-0 rounded-full border px-2 py-px text-[10.5px] font-bold uppercase tracking-[0.05em] ${s ? s.cls : 'text-text-muted border-line-strong'}`}
    >
      {s ? t(s.lbl) : status}
    </span>
  )
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
  const t = useT()
  const [tab, setTab] = useState<'overview' | 'people'>('overview')
  const [nodeFilter, setNodeFilter] = useState<string>('')
  const [runId, setRunId] = useState<number | null>(null)

  const showPeople = (node = '') => {
    setNodeFilter(node)
    setRunId(null)
    setTab('people')
  }

  return (
    <aside className="flex h-full w-full flex-col bg-card" aria-label={t('flows.builder.runs')}>
      <header className="flex items-start gap-3 border-b border-line px-4 py-3">
        <div className="min-w-0 flex-1">
          <div className="eyebrow">{t('flows.runs.eyebrow', { period: periodLabel })}</div>
          <div className="display mt-0.5 text-[17px] leading-snug text-text-heading">{t('flows.runs.title')}</div>
        </div>
        <button
          type="button"
          onClick={onClose}
          aria-label={t('flows.runs.close')}
          className="grid h-8 w-8 shrink-0 place-items-center rounded-lg text-text-muted hover:bg-fill-hover hover:text-text-high"
        >
          <X size={16} aria-hidden />
        </button>
      </header>
      <div className="flex gap-1 border-b border-line px-4 pt-2">
        {(
          [
            ['overview', t('flows.runs.tabOverview')],
            ['people', t('flows.runs.tabPeople')],
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
  const f = useFormat()
  return (
    <div className="min-w-0 rounded-lg border border-line bg-well px-3 py-2">
      <div className="truncate text-[11px] text-text-muted">{label}</div>
      <div className={`mt-0.5 text-[20px] font-semibold leading-none ${tone ?? 'text-text-heading'}`}>{f.number(value ?? 0)}</div>
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
  const t = useT()
  const f = useFormat()
  const { describeCtx } = useFlowView()
  const byId = useMemo(() => new Map(nodes.map((n) => [n.id, n])), [nodes])
  // a step right under a branch step (decision / follow-up) gets only the people
  // who took its outcome
  const branchParents = new Set((stats?.order ?? []).filter((x) => x.type === 'decision' || x.type === 'followup').map((x) => x.nodeId))
  const branchChild = (o: { parent: string | null }) => !!o.parent && branchParents.has(o.parent)
  const depth = useMemo(() => {
    const d = new Map<string, number>()
    for (const o of stats?.order ?? []) d.set(o.nodeId, o.parent ? (d.get(o.parent) ?? 0) + 1 : 0)
    return d
  }, [stats])

  if (!stats) {
    return <p className="text-[12.5px] text-text-muted">{t('flows.runs.noNumbers')}</p>
  }
  const tot = stats.totals
  const hasFollowUps = nodes.some((n) => n.type === 'followup')
  return (
    <div className="flex flex-col gap-5">
      <div className="grid grid-cols-2 gap-2">
        <Tile label={t('flows.runs.started')} value={tot.started} />
        <Tile label={t('flows.runs.statusDone')} value={tot.completed} />
        <Tile label={t('flows.runs.waitingNow')} value={tot.waitingNow} />
        <Tile label={t('flows.runs.statusFailed')} value={tot.failed} tone={tot.failed ? 'text-[color:var(--red-ink)]' : undefined} />
      </div>

      {days > 1 && (
        <DailyBars data={stats.series.map((s) => ({ day: s.day, value: s.started }))} label={t('flows.runs.enteredPerDay')} />
      )}

      <section>
        <div className="eyebrow mb-2">{t('flows.runs.eachStep')}</div>
        <div className="overflow-x-auto rounded-lg border border-line">
          <table className="w-full min-w-[320px] table-fixed text-[12px]">
            <colgroup>
              <col />
              <col className="w-[70px]" />
              <col className="w-[66px]" />
              <col className="w-[92px]" />
            </colgroup>
            <thead>
              <tr className="border-b border-line bg-well text-left text-[10.5px] uppercase tracking-[0.05em] text-text-muted">
                <th className="px-2.5 py-1.5 font-bold">{t('flows.runs.colStep')}</th>
                <th className="px-2 py-1.5 text-right font-bold">{t('flows.runs.colReached')}</th>
                <th className="px-2 py-1.5 text-right font-bold">{t('flows.runs.colDrop')}</th>
                <th className="px-2.5 py-1.5 text-right font-bold">{t('flows.runs.colNow')}</th>
              </tr>
            </thead>
            <tbody>
              {stats.order.map((o) => {
                const node = byId.get(o.nodeId)
                const s = stats.nodes[o.nodeId]
                const title = node ? describeStep(t, node, describeCtx).title : o.label || o.nodeId
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
                      <button
                        type="button"
                        className="font-semibold text-text-high hover:underline"
                        onClick={() => onPeople(o.nodeId)}
                        title={t('flows.runs.seeWhoReached')}
                      >
                        {f.number(o.entered)}
                      </button>
                    </td>
                    {/* a step on one outcome of a branch: people going the other way is not a
                        drop-off — an untaken path reads "Not taken", neutrally */}
                    <td
                      className={`px-2 py-1.5 text-right tabular-nums ${
                        !branchChild(o) && o.dropPct && o.dropPct >= 50 ? 'text-[color:var(--amber-ink)]' : 'text-text-muted'
                      }`}
                    >
                      {branchChild(o) && o.entered === 0
                        ? t('flows.runs.notTaken')
                        : o.dropPct === null || branchChild(o)
                          ? '—'
                          : f.percent(o.dropPct / 100)}
                    </td>
                    <td className="px-2.5 py-1.5 text-right tabular-nums text-text-muted">
                      {s?.waitingNow ? (
                        <span className="text-[color:var(--amber-ink)]">{t('flows.stats.waiting', { count: s.waitingNow })}</span>
                      ) : s?.failed ? (
                        <span className="text-[color:var(--red-ink)]">{t('flows.stats.failed', { count: s.failed })}</span>
                      ) : (
                        '—'
                      )}
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
        <div className="mt-1.5 text-[11px] text-text-muted">{t('flows.runs.dropExplainer')}</div>
      </section>

      {hasFollowUps && (
        <section>
          <div className="eyebrow mb-1">{t('flows.runs.followUps')}</div>
          <div className="text-[12.5px] text-text-mid">
            {t('flows.runs.followUpsLine', { sent: f.number(tot.followupsSent), replied: f.number(tot.repliedAfterFollowUp) })}
          </div>
        </section>
      )}

      {(stats.failures.length > 0 || stats.skips.length > 0) && (
        <section>
          <div className="eyebrow mb-2">{t('flows.runs.whatWentWrong')}</div>
          <ul className="flex flex-col gap-1.5">
            {[...stats.failures.map((x) => ({ ...x, fatal: true })), ...stats.skips.map((x) => ({ ...x, fatal: false }))].map((x, i) => {
              const at = byId.get(x.nodeId) ? describeStep(t, byId.get(x.nodeId)!, describeCtx).title : x.label || x.nodeId
              return (
                <li key={i} className="rounded-lg border border-line px-3 py-2">
                  <div className="flex items-start justify-between gap-2">
                    <span className={`min-w-0 text-[12.5px] ${x.fatal ? 'text-[color:var(--red-ink)]' : 'text-text-high'}`}>{x.reason}</span>
                    <span className="shrink-0 text-[12px] font-semibold tabular-nums text-text-high">×{f.number(x.count)}</span>
                  </div>
                  <div className="mt-0.5 text-[11px] text-text-muted">
                    {x.fatal ? t('flows.runs.stoppedAt', { step: at }) : t('flows.runs.skippedAt', { step: at })}
                  </div>
                </li>
              )
            })}
          </ul>
        </section>
      )}

      <button
        type="button"
        onClick={() => onPeople('')}
        className="rounded-lg border border-line-strong bg-card px-3 py-2 text-[12.5px] font-semibold text-text-high hover:bg-fill-hover"
      >
        {t('flows.runs.seeEachPath')}
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
  const t = useT()
  const f = useFormat()
  const { describeCtx } = useFlowView()
  const [status, setStatus] = useState('')
  const [q, setQ] = useState('')
  const [debounced, setDebounced] = useState('')
  const [page, setPage] = useState(1)
  const [data, setData] = useState<RunsPage | null>(null)
  const [loading, setLoading] = useState(false)

  useEffect(() => {
    const timer = setTimeout(() => setDebounced(q.trim()), 300)
    return () => clearTimeout(timer)
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
    return n ? describeStep(t, n, describeCtx).title : id
  }

  const rowLine = (r: FlowRunRow) => {
    if (r.status === 'done' || r.status === 'failed') {
      const s = STATUS_COPY[r.status]
      const detail = r.detail ? (aiDecisionText(t, f.percent, r.detail) ?? r.detail) : s ? t(s.lbl) : r.status
      return t('flows.runs.endedLine', { detail, when: f.dateTime(r.finishedAt) })
    }
    const at = r.currentLabel || nodeTitle(r.currentNode)
    const since = f.dateTime(r.startedAt)
    return r.waitFor === 'reply' ? t('flows.runs.atWaitingReply', { step: at, since }) : t('flows.runs.atSince', { step: at, since })
  }

  return (
    <div className="flex flex-col gap-3">
      <div className="flex flex-col gap-2">
        <div className="relative">
          <Search size={13} className="pointer-events-none absolute left-2.5 top-1/2 -translate-y-1/2 text-text-faint" aria-hidden />
          <input
            type="search"
            value={q}
            onChange={(e) => setQ(e.target.value)}
            placeholder={t('flows.runs.findContact')}
            aria-label={t('flows.runs.findContact')}
            className={`${inputCls} pl-8`}
          />
        </div>
        <Select
          value={status}
          onChange={setStatus}
          options={STATUS_FILTERS.map((o) => ({ value: o.value, label: t(o.lbl) }))}
          size="md"
          ariaLabel={t('common.fields.status')}
        />
        {node && (
          <span className="inline-flex w-fit max-w-full items-center gap-1 rounded-md border border-line-strong bg-well py-0.5 pl-2 pr-1 text-[11.5px] text-text-high">
            <span className="truncate">{t('flows.runs.reachedFilter', { step: nodeTitle(node) })}</span>
            <button type="button" aria-label={t('flows.runs.clearStepFilter')} onClick={onClearNode} className="grid h-4 w-4 place-items-center rounded hover:bg-fill-hover">
              <X size={11} aria-hidden />
            </button>
          </span>
        )}
      </div>

      {loading && !data ? (
        <div className="text-[12.5px] text-text-muted">{t('common.status.loading')}</div>
      ) : !data || data.runs.length === 0 ? (
        <div className="text-[12.5px] text-text-muted">{t('flows.runs.noOne')}</div>
      ) : (
        <>
          <ul className="flex flex-col divide-y divide-line rounded-lg border border-line">
            {data.runs.map((r: FlowRunRow) => (
              <li key={r.id}>
                <button type="button" onClick={() => onOpen(r.id)} className="flex w-full flex-col gap-0.5 px-3 py-2 text-left hover:bg-fill-subtle">
                  <span className="flex items-center justify-between gap-2">
                    <span className="truncate text-[13px] font-semibold text-text-high">
                      {r.contactName || t('flows.runs.contactNumber', { id: String(r.contactId ?? '') })}
                    </span>
                    <StatusPill status={r.status} />
                  </span>
                  <span className="truncate text-[11.5px] text-text-muted">{rowLine(r)}</span>
                </button>
              </li>
            ))}
          </ul>
          {data.pages > 1 && (
            <div className="flex items-center justify-between gap-2 text-[12px] text-text-muted">
              <button type="button" disabled={page <= 1} onClick={() => setPage(page - 1)} className="inline-flex shrink-0 items-center gap-1 disabled:opacity-40">
                <ChevronLeft size={13} aria-hidden /> {t('flows.runs.newer')}
              </button>
              <span className="min-w-0 text-center">
                {t('flows.runs.pageOf', { page: data.page, pages: data.pages, count: data.count })}
              </span>
              <button type="button" disabled={page >= data.pages} onClick={() => setPage(page + 1)} className="inline-flex shrink-0 items-center gap-1 disabled:opacity-40">
                {t('flows.runs.older')} <ChevronRight size={13} aria-hidden />
              </button>
            </div>
          )}
        </>
      )}
    </div>
  )
}

function RunDetail({ id, nodes, onBack }: { id: number; nodes: FlowNode[]; onBack: () => void }) {
  const t = useT()
  const f = useFormat()
  const { describeCtx } = useFlowView()
  const stepTitle = (p: FlowRunPathStep) => {
    const n = nodes.find((x) => x.id === p.nodeId)
    return n ? describeStep(t, n, describeCtx).title : p.label || p.nodeId
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
        <ArrowLeft size={13} aria-hidden /> {t('flows.runs.allContacts')}
      </button>
      {loading ? (
        <div className="text-[12.5px] text-text-muted">{t('common.status.loading')}</div>
      ) : !data ? (
        <div className="text-[12.5px] text-text-muted">{t('flows.runs.runUnavailable')}</div>
      ) : (
        <>
          <div className="flex items-start justify-between gap-2">
            <div className="min-w-0">
              <div className="display truncate text-[17px] text-text-heading">
                {data.run.contactName || t('flows.runs.contactNumber', { id: String(data.run.contactId ?? '') })}
              </div>
              <div className="text-[11.5px] text-text-muted">
                {data.run.finishedAt
                  ? t('flows.runs.startedEnded', { started: f.dateTime(data.run.startedAt), ended: f.dateTime(data.run.finishedAt) })
                  : t('flows.runs.startedAt', { started: f.dateTime(data.run.startedAt) })}
              </div>
            </div>
            <StatusPill status={data.run.status} />
          </div>
          {data.run.detail && (
            <div className={`rounded-lg border px-3 py-2 text-[12.5px] ${data.run.status === 'failed' ? 'border-red/40 text-[color:var(--red-ink)]' : 'border-line text-text-high'}`}>
              {data.run.status === 'failed'
                ? t('flows.runs.whyFailed', { detail: data.run.detail })
                : t('flows.runs.outcome', { detail: aiDecisionText(t, f.percent, data.run.detail) ?? data.run.detail })}
            </div>
          )}
          {data.run.lastReply && (
            <div className="rounded-lg border border-line bg-well px-3 py-2 text-[12.5px] text-text-mid">
              <span className="eyebrow mr-1">{t('flows.runs.lastReply')}</span> {data.run.lastReply}
            </div>
          )}
          <ol className="flex flex-col">
            {groupPath(t, f.percent, data.path).map((g, i, all) => (
              <li key={i} className="relative flex gap-3 pb-3 last:pb-0">
                {i < all.length - 1 && <span className="absolute left-[4.5px] top-3 h-full w-px bg-line-strong" aria-hidden />}
                <span
                  className={`relative mt-[5px] h-2.5 w-2.5 shrink-0 rounded-full ${
                    g.status === 'failed' ? 'bg-red' : g.status === 'waiting' ? 'bg-amber' : g.status === 'done' ? 'bg-text-primary' : 'border border-text-muted bg-card'
                  }`}
                  aria-hidden
                />
                <div className="min-w-0">
                  <div className="flex flex-wrap items-baseline gap-x-2">
                    <span className="text-[12.5px] font-semibold text-text-high">{stepTitle(g.first)}</span>
                    <span className="text-[11px] text-text-muted">
                      {t(STATUS_WORD[g.status])} · {f.dateTime(g.first.at)}
                    </span>
                  </div>
                  {g.details.length > 0 && (
                    <div className={`text-[11.5px] ${g.status === 'failed' ? 'text-[color:var(--red-ink)]' : 'text-text-muted'}`}>{g.details.join(' · ')}</div>
                  )}
                </div>
              </li>
            ))}
            {data.path.length === 0 && <li className="text-[12px] text-text-muted">{t('flows.runs.noEvents')}</li>}
          </ol>
          <button type="button" onClick={() => setShowLog(!showLog)} className="w-fit text-[12px] font-semibold text-text-muted hover:text-text-high">
            {showLog ? t('flows.runs.hideLog', { count: data.log.length }) : t('flows.runs.showLog', { count: data.log.length })}
          </button>
          {showLog && (
            <pre className="max-h-[260px] overflow-auto whitespace-pre-wrap rounded-lg border border-line bg-well p-2.5 text-[11px] leading-relaxed text-text-mid">
              {data.log.map((l) => `${f.dateTime(l.t)}  ${l.node}  ${l.evt}`).join('\n')}
            </pre>
          )}
        </>
      )}
    </div>
  )
}
