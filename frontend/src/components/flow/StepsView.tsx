'use client'

import { useLayoutEffect, useMemo, useRef } from 'react'
import { AlertCircle, ArrowDown, ArrowUp, CornerDownRight, Plus, Trash2 } from 'lucide-react'
import { Trans, useT } from '@/i18n'
import { describeStep } from '@/lib/flow/describe'
import { buildStepTree, canMove, timingText, type BranchTree, type Graph, type StepTree } from '@/lib/flow/steps'
import type { FlowEdge, FlowNode } from '@/lib/flow/types'
import { useFlowView } from './FlowViewContext'
import { StepCount } from './nodes'
import { StepIcon } from './stepIcons'

/**
 * The Steps view — the default way to read and build a flow: a vertical list
 * of plain-language cards with a "+" between every two steps, branch steps
 * splitting into side-by-side columns (YES / NO, Replied / No reply, or one
 * per AI option + "None of these"). It is
 * a projection of the same nodes/edges graph the canvas draws; structural
 * edits go through lib/flow/steps and re-lay the canvas out.
 */

export interface InsertAt {
  source: string
  handle?: string
  edgeId?: string | null
  hasContinuation: boolean
}

export default function StepsView({
  graph,
  centerKey,
  onSelect,
  onInsert,
  onDelete,
  onMove,
}: {
  graph: Graph
  /** re-centre the main path on the first step when this changes */
  centerKey: string
  onSelect: (id: string) => void
  onInsert: (at: InsertAt) => void
  onDelete: (id: string) => void
  onMove: (id: string, dir: 'up' | 'down') => void
}) {
  const t = useT()
  const forest = useMemo(() => buildStepTree(graph.nodes, graph.edges), [graph])
  const { describeCtx } = useFlowView()
  const titleOf = (n: FlowNode) => describeStep(t, n, describeCtx).title

  const ctx = { graph, onSelect, onInsert, onDelete, onMove, titleOf }

  // a wide branch tree scrolls sideways inside this box — open it with the
  // starting point in the middle rather than the far-left column
  const scroller = useRef<HTMLDivElement>(null)
  useLayoutEffect(() => {
    const box = scroller.current
    const first = box?.querySelector<HTMLElement>('[data-step-root]')
    if (!box || !first) return
    const boxRect = box.getBoundingClientRect()
    const r = first.getBoundingClientRect()
    box.scrollLeft += r.left + r.width / 2 - (boxRect.left + boxRect.width / 2)
  }, [centerKey])

  return (
    <div ref={scroller} className="h-full overflow-auto">
      <div className="mx-auto flex w-max min-w-full max-w-none flex-col items-center px-4 pb-24 pt-6 sm:px-8">
        {forest.roots.length === 0 && (
          <div className="w-full max-w-[min(560px,calc(100vw-2rem))] rounded-xl border border-dashed border-line-strong bg-card px-5 py-6 text-center">
            <div className="display text-[18px] text-text-heading">{t('flows.stepsView.noStartTitle')}</div>
            <p className="mt-1 text-[13px] text-text-muted">{t('flows.stepsView.noStartBody')}</p>
          </div>
        )}
        {forest.roots.map((root, i) => (
          <div
            key={root.node.id}
            className="flex w-full flex-col items-center"
            data-step-root={i === 0 ? '' : undefined}
          >
            {i > 0 && <div className="eyebrow my-6">{t('flows.stepsView.anotherStart')}</div>}
            <Chain tree={root} ctx={ctx} />
          </div>
        ))}
        {forest.orphans.length > 0 && (
          <div className="mt-10 w-full max-w-[min(560px,calc(100vw-2rem))]">
            <div className="eyebrow mb-1">{t('flows.stepsView.notConnected')}</div>
            <p className="mb-3 text-[12.5px] text-text-muted">{t('flows.stepsView.notConnectedBody')}</p>
            <div className="flex flex-col gap-2">
              {forest.orphans.map((n) => (
                <StepCard key={n.id} node={n} ctx={ctx} canUp={false} canDown={false} />
              ))}
            </div>
          </div>
        )}
      </div>
    </div>
  )
}

interface Ctx {
  graph: Graph
  onSelect: (id: string) => void
  onInsert: (at: InsertAt) => void
  onDelete: (id: string) => void
  onMove: (id: string, dir: 'up' | 'down') => void
  titleOf: (n: FlowNode) => string
}

function Chain({ tree, ctx }: { tree: StepTree; ctx: Ctx }) {
  if (tree.ref) {
    return (
      <button
        type="button"
        onClick={() => ctx.onSelect(tree.node.id)}
        className="flex w-full max-w-[min(560px,calc(100vw-2rem))] items-center gap-2 rounded-xl border border-dashed border-line-strong bg-fill-subtle px-4 py-2.5 text-left text-[12.5px] text-text-mid hover:border-text-primary"
      >
        <CornerDownRight size={14} className="shrink-0 text-text-muted" aria-hidden />
        <span className="min-w-0 truncate">
          <Trans
            k="flows.stepsView.continuesAt"
            values={{ step: <strong className="font-semibold text-text-high">{ctx.titleOf(tree.node)}</strong> }}
          />
        </span>
      </button>
    )
  }
  const isEnd = tree.node.type === 'end'
  return (
    <>
      <StepCard
        node={tree.node}
        ctx={ctx}
        canUp={canMove(ctx.graph, tree.node.id, 'up')}
        canDown={canMove(ctx.graph, tree.node.id, 'down')}
      />
      {tree.branches ? (
        <Branches branches={tree.branches} ctx={ctx} />
      ) : isEnd ? null : (
        <>
          <Connector
            edge={tree.nextEdge}
            onAdd={() =>
              ctx.onInsert({
                source: tree.node.id,
                edgeId: tree.nextEdge?.id ?? null,
                hasContinuation: !!tree.nextEdge,
              })
            }
          />
          {tree.next ? <Chain tree={tree.next} ctx={ctx} /> : <PathEnd />}
        </>
      )}
    </>
  )
}

function Branches({ branches, ctx }: { branches: BranchTree[]; ctx: Ctx }) {
  return (
    <div className="flex w-full flex-col items-center">
      <div className="h-4 w-px bg-line-strong" aria-hidden />
      <div className="flex items-start justify-center">
        {branches.map((b, i) => (
          <div
            key={b.key}
            className="relative flex min-w-[284px] shrink-0 flex-col items-center px-2 sm:min-w-[316px] sm:px-2.5"
          >
            {branches.length > 1 && (
              <div
                className="absolute top-0 h-px bg-line-strong"
                style={{
                  left: i === 0 ? '50%' : 0,
                  right: i === branches.length - 1 ? '50%' : 0,
                }}
                aria-hidden
              />
            )}
            <div className="h-4 w-px bg-line-strong" aria-hidden />
            <BranchPill branch={b} />
            <Connector
              edge={b.edge}
              onAdd={() =>
                ctx.onInsert({
                  source: b.source,
                  handle: b.handle,
                  edgeId: b.edge?.id ?? null,
                  hasContinuation: !!b.edge,
                })
              }
            />
            {b.child ? <Chain tree={b.child} ctx={ctx} /> : <PathEnd />}
          </div>
        ))}
      </div>
    </div>
  )
}

function BranchPill({ branch }: { branch: BranchTree }) {
  const t = useT()
  const tone =
    branch.tone === 'yes'
      ? 'border-green/40 text-[color:var(--green-ink)]'
      : branch.tone === 'no'
        ? 'border-red/40 text-[color:var(--red-ink)]'
        : branch.tone === 'timeout'
          ? 'border-amber/40 text-[color:var(--amber-ink)]'
          : branch.tone === 'option'
            ? 'border-blue/40 text-[color:var(--blue-ink)]'
            : 'border-line-strong text-text-mid'
  // an operator's own option label reads as written (no shouting caps)
  const own = branch.labelText !== undefined
  return (
    <span
      className={`max-w-[260px] truncate rounded-full border bg-card px-2.5 py-0.5 text-[11px] font-bold ${
        own ? '' : 'uppercase tracking-[0.06em]'
      } ${tone}`}
      title={own ? branch.labelText : undefined}
    >
      {own ? branch.labelText : branch.labelKey ? t(branch.labelKey, branch.labelVars) : branch.key}
    </span>
  )
}

function Connector({ edge, onAdd }: { edge: FlowEdge | null; onAdd: () => void }) {
  const t = useT()
  const timing = timingText(t, edge ?? undefined)
  return (
    <div className="flex flex-col items-center">
      <div className="h-3 w-px bg-line-strong" aria-hidden />
      {timing && (
        <>
          <span className="rounded-md border border-line bg-well px-2 py-0.5 text-[11px] text-text-mid">{timing}</span>
          <div className="h-2 w-px bg-line-strong" aria-hidden />
        </>
      )}
      <button
        type="button"
        onClick={onAdd}
        title={t('flows.stepsView.addStepHere')}
        aria-label={t('flows.stepsView.addStepHere')}
        className="grid h-7 w-7 place-items-center rounded-full border border-line-strong bg-card text-text-mid transition-colors hover:border-text-primary hover:bg-primary hover:text-on-primary"
      >
        <Plus size={14} strokeWidth={2.25} aria-hidden />
      </button>
      <div className="h-3 w-px bg-line-strong" aria-hidden />
    </div>
  )
}

function PathEnd() {
  const t = useT()
  return <div className="pb-2 text-[11.5px] text-text-faint">{t('flows.stepsView.pathEnds')}</div>
}

function StepCard({ node, ctx, canUp, canDown }: { node: FlowNode; ctx: Ctx; canUp: boolean; canDown: boolean }) {
  const t = useT()
  const { describeCtx, selectedId, issues } = useFlowView()
  const text = describeStep(t, node, describeCtx)
  const selected = selectedId === node.id
  const isTrigger = node.type === 'trigger'
  const warn = issues[node.id] ?? []
  const deletable = !isTrigger || ctx.graph.nodes.filter((n) => n.type === 'trigger').length > 1
  return (
    <div
      role="button"
      tabIndex={0}
      aria-pressed={selected}
      onClick={() => ctx.onSelect(node.id)}
      onKeyDown={(e) => {
        if (e.key === 'Enter' || e.key === ' ') {
          e.preventDefault()
          ctx.onSelect(node.id)
        }
      }}
      className={`group relative w-full max-w-[min(560px,calc(100vw-2rem))] cursor-pointer rounded-xl border bg-card px-4 py-3 text-left outline-none transition-[border-color,box-shadow] focus-visible:border-text-primary ${
        selected
          ? 'border-text-primary shadow-[0_0_0_1px_var(--text-primary)]'
          : isTrigger
            ? 'border-accent hover:shadow-[0_0_0_1px_var(--accent)]'
            : 'border-line-strong hover:border-text-primary'
      }`}
    >
      <div className="flex items-start gap-3">
        <span
          className={`mt-0.5 grid h-8 w-8 shrink-0 place-items-center rounded-lg border bg-well ${
            isTrigger ? 'border-accent text-[color:var(--brand-ink)]' : 'border-line text-text-high'
          }`}
        >
          <StepIcon kind={String(node.type)} config={node.data.config} />
        </span>
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-x-2 gap-y-0.5 pr-16 sm:pr-0">
            <span className="eyebrow" style={isTrigger ? { color: 'var(--brand-ink)' } : undefined}>
              {text.eyebrow}
            </span>
            {warn.length > 0 && (
              <span
                className="inline-flex min-w-0 items-center gap-1 text-[11px] font-semibold text-[color:var(--amber-ink)]"
                title={warn.map((w) => t(w)).join('\n')}
              >
                <AlertCircle size={12} className="shrink-0" aria-hidden /> {t(warn[0])}
              </span>
            )}
          </div>
          <div className="mt-0.5 text-[14px] font-semibold leading-snug text-text-heading">{text.title}</div>
          {node.data.label && <div className="mt-0.5 text-[12px] text-text-mid">{node.data.label}</div>}
          {text.detail && (
            <div className="mt-1 line-clamp-2 text-[12.5px] leading-snug text-text-muted">{text.detail}</div>
          )}
          <div className="mt-2">
            <StepCount id={node.id} />
          </div>
        </div>
      </div>
      <div className="absolute right-2 top-2 flex items-center gap-0.5 opacity-100 transition-opacity sm:opacity-0 sm:group-focus-within:opacity-100 sm:group-hover:opacity-100">
        {canUp && (
          <IconBtn label={t('flows.stepsView.moveUp')} onClick={() => ctx.onMove(node.id, 'up')}>
            <ArrowUp size={13} aria-hidden />
          </IconBtn>
        )}
        {canDown && (
          <IconBtn label={t('flows.stepsView.moveDown')} onClick={() => ctx.onMove(node.id, 'down')}>
            <ArrowDown size={13} aria-hidden />
          </IconBtn>
        )}
        {deletable && (
          <IconBtn label={t('flows.config.deleteStep')} onClick={() => ctx.onDelete(node.id)} danger>
            <Trash2 size={13} aria-hidden />
          </IconBtn>
        )}
      </div>
    </div>
  )
}

function IconBtn({
  label,
  onClick,
  children,
  danger,
}: {
  label: string
  onClick: () => void
  children: React.ReactNode
  danger?: boolean
}) {
  return (
    <button
      type="button"
      title={label}
      aria-label={label}
      onClick={(e) => {
        e.stopPropagation()
        onClick()
      }}
      className={`grid h-7 w-7 place-items-center rounded-md text-text-muted hover:bg-fill-hover ${danger ? 'hover:text-[color:var(--red-ink)]' : 'hover:text-text-high'}`}
    >
      {children}
    </button>
  )
}
