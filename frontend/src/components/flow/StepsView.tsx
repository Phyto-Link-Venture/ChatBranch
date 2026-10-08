'use client'

import { useMemo } from 'react'
import { AlertCircle, ArrowDown, ArrowUp, CornerDownRight, Plus, Trash2 } from 'lucide-react'
import { describeStep } from '@/lib/flow/describe'
import { buildStepTree, canMove, timingText, type BranchTree, type Graph, type StepTree } from '@/lib/flow/steps'
import type { FlowEdge, FlowNode } from '@/lib/flow/types'
import { useFlowView } from './FlowViewContext'
import { StepCount } from './nodes'
import { StepIcon } from './stepIcons'

/**
 * The Steps view — the default way to read and build a flow: a vertical list
 * of plain-language cards with a "+" between every two steps, branch steps
 * splitting into side-by-side columns (YES / NO, Replied / No reply). It is
 * a projection of the same nodes/edges graph the canvas draws; structural
 * edits go through lib/flow/steps and re-lay the canvas out.
 */

export interface InsertAt {
  source: string
  handle?: 'yes' | 'no'
  edgeId?: string | null
  hasContinuation: boolean
}

export default function StepsView({
  graph,
  onSelect,
  onInsert,
  onDelete,
  onMove,
}: {
  graph: Graph
  onSelect: (id: string) => void
  onInsert: (at: InsertAt) => void
  onDelete: (id: string) => void
  onMove: (id: string, dir: 'up' | 'down') => void
}) {
  const forest = useMemo(() => buildStepTree(graph.nodes, graph.edges), [graph])
  const { describeCtx } = useFlowView()
  const titleOf = (n: FlowNode) => describeStep(n, describeCtx).title

  const ctx = { graph, onSelect, onInsert, onDelete, onMove, titleOf }

  return (
    <div className="mx-auto flex w-max min-w-full max-w-none flex-col items-center px-4 pb-24 pt-6 sm:px-8">
      {forest.roots.length === 0 && (
        <div className="w-full max-w-[560px] rounded-xl border border-dashed border-line-strong bg-card px-5 py-6 text-center">
          <div className="display text-[18px] text-text-heading">No starting point</div>
          <p className="mt-1 text-[13px] text-text-muted">
            Every flow begins with a “When…” step. This one has none, so it never runs.
          </p>
        </div>
      )}
      {forest.roots.map((root, i) => (
        <div key={root.node.id} className="flex w-full flex-col items-center">
          {i > 0 && <div className="eyebrow my-6">Another starting point</div>}
          <Chain tree={root} ctx={ctx} />
        </div>
      ))}
      {forest.orphans.length > 0 && (
        <div className="mt-10 w-full max-w-[560px]">
          <div className="eyebrow mb-1">Not connected</div>
          <p className="mb-3 text-[12.5px] text-text-muted">No path reaches these steps, so they never run. Delete them, or connect them in the Advanced canvas.</p>
          <div className="flex flex-col gap-2">
            {forest.orphans.map((n) => (
              <StepCard key={n.id} node={n} ctx={ctx} canUp={false} canDown={false} />
            ))}
          </div>
        </div>
      )}
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
        className="flex w-full max-w-[560px] items-center gap-2 rounded-xl border border-dashed border-line-strong bg-fill-subtle px-4 py-2.5 text-left text-[12.5px] text-text-mid hover:border-text-primary"
      >
        <CornerDownRight size={14} className="shrink-0 text-text-muted" aria-hidden />
        <span className="min-w-0 truncate">
          Continues at <strong className="font-semibold text-text-high">{ctx.titleOf(tree.node)}</strong>
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
              ctx.onInsert({ source: tree.node.id, edgeId: tree.nextEdge?.id ?? null, hasContinuation: !!tree.nextEdge })
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
      <div className="relative flex items-start justify-center gap-4 sm:gap-6">
        {branches.length > 1 && (
          <div className="absolute left-[140px] right-[140px] top-0 h-px bg-line-strong sm:left-[150px] sm:right-[150px]" aria-hidden />
        )}
        {branches.map((b) => (
          <div key={b.key} className="flex w-[280px] shrink-0 flex-col items-center sm:w-[300px]">
            <div className="h-4 w-px bg-line-strong" aria-hidden />
            <BranchPill branch={b} />
            <Connector
              edge={b.edge}
              onAdd={() =>
                ctx.onInsert({ source: b.source, handle: b.handle, edgeId: b.edge?.id ?? null, hasContinuation: !!b.edge })
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
  const tone =
    branch.key === 'yes'
      ? 'border-green/40 text-[color:var(--green-ink)]'
      : branch.key === 'no'
        ? 'border-red/40 text-[color:var(--red-ink)]'
        : branch.key === 'timeout'
          ? 'border-amber/40 text-[color:var(--amber-ink)]'
          : 'border-line-strong text-text-mid'
  return (
    <span className={`rounded-full border bg-card px-2.5 py-0.5 text-[11px] font-bold uppercase tracking-[0.06em] ${tone}`}>
      {branch.label}
    </span>
  )
}

function Connector({ edge, onAdd }: { edge: FlowEdge | null; onAdd: () => void }) {
  const timing = timingText(edge ?? undefined)
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
        title="Add a step here"
        aria-label="Add a step here"
        className="grid h-7 w-7 place-items-center rounded-full border border-line-strong bg-card text-text-mid transition-colors hover:border-text-primary hover:bg-primary hover:text-on-primary"
      >
        <Plus size={14} strokeWidth={2.25} aria-hidden />
      </button>
      <div className="h-3 w-px bg-line-strong" aria-hidden />
    </div>
  )
}

function PathEnd() {
  return <div className="pb-2 text-[11.5px] text-text-faint">Path ends</div>
}

function StepCard({ node, ctx, canUp, canDown }: { node: FlowNode; ctx: Ctx; canUp: boolean; canDown: boolean }) {
  const { describeCtx, selectedId, issues } = useFlowView()
  const text = describeStep(node, describeCtx)
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
      className={`group relative w-full max-w-[560px] cursor-pointer rounded-xl border bg-card px-4 py-3 text-left outline-none transition-[border-color,box-shadow] focus-visible:border-text-primary ${
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
              <span className="inline-flex items-center gap-1 text-[11px] font-semibold text-[color:var(--amber-ink)]" title={warn.join('\n')}>
                <AlertCircle size={12} aria-hidden /> {warn[0]}
              </span>
            )}
          </div>
          <div className="mt-0.5 text-[14px] font-semibold leading-snug text-text-heading">{text.title}</div>
          {node.data.label && <div className="mt-0.5 text-[12px] text-text-mid">{node.data.label}</div>}
          {text.detail && <div className="mt-1 line-clamp-2 text-[12.5px] leading-snug text-text-muted">{text.detail}</div>}
          <div className="mt-2">
            <StepCount id={node.id} />
          </div>
        </div>
      </div>
      <div className="absolute right-2 top-2 flex items-center gap-0.5 opacity-100 transition-opacity sm:opacity-0 sm:group-focus-within:opacity-100 sm:group-hover:opacity-100">
        {canUp && (
          <IconBtn label="Move up" onClick={() => ctx.onMove(node.id, 'up')}>
            <ArrowUp size={13} aria-hidden />
          </IconBtn>
        )}
        {canDown && (
          <IconBtn label="Move down" onClick={() => ctx.onMove(node.id, 'down')}>
            <ArrowDown size={13} aria-hidden />
          </IconBtn>
        )}
        {deletable && (
          <IconBtn label="Delete step" onClick={() => ctx.onDelete(node.id)} danger>
            <Trash2 size={13} aria-hidden />
          </IconBtn>
        )}
      </div>
    </div>
  )
}

function IconBtn({ label, onClick, children, danger }: { label: string; onClick: () => void; children: React.ReactNode; danger?: boolean }) {
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
