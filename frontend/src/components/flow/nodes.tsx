'use client'

import {
  BaseEdge,
  EdgeLabelRenderer,
  Handle,
  NodeResizer,
  Position,
  getSmoothStepPath,
  type EdgeProps,
  type NodeProps,
} from '@xyflow/react'
import { AlertCircle, Plus } from 'lucide-react'
import { describeStep } from '@/lib/flow/describe'
import { branchLabels, isBranchKind } from '@/lib/flow/nodeConfig'
import { timingText } from '@/lib/flow/steps'
import type { FlowEdge, FlowNode } from '@/lib/flow/types'
import { useFlowView } from './FlowViewContext'
import { StepIcon } from './stepIcons'

/**
 * The Advanced canvas's node + edge renderers (Paper & Ink): every step is a
 * card with an eyebrow naming its type, an ink border, the same plain-language
 * summary the Steps view prints, and the selected period's count. The trigger
 * carries the tangerine accent. Branch steps (Ask & branch, Follow up) have
 * two outputs, YES / NO (Replied / No reply). Every line has a "+" to insert
 * a step into it.
 */

type FlowNodeProps = NodeProps<FlowNode>

export function StepCount({ id, compact = false }: { id: string; compact?: boolean }) {
  const { stats } = useFlowView()
  const s = stats?.nodes[id]
  if (!stats) return null
  const reached = s?.entered ?? 0
  return (
    <span className={`inline-flex flex-wrap items-center gap-x-2 gap-y-0.5 ${compact ? 'text-[10.5px]' : 'text-[11.5px]'}`}>
      <span className={`tabular-nums ${reached ? 'font-semibold text-text-high' : 'text-text-faint'}`}>{reached.toLocaleString()} reached</span>
      {!!s?.waitingNow && <span className="tabular-nums text-[color:var(--amber-ink)]">{s.waitingNow.toLocaleString()} waiting</span>}
      {!!s?.failed && <span className="tabular-nums text-[color:var(--red-ink)]">{s.failed.toLocaleString()} failed</span>}
    </span>
  )
}

function StepNodeCard({ id, data, type, selected }: FlowNodeProps) {
  const { describeCtx, issues } = useFlowView()
  const node = { id, data, type, position: { x: 0, y: 0 } } as FlowNode
  const text = describeStep(node, describeCtx)
  const isTrigger = type === 'trigger'
  const branch = isBranchKind(type)
  const labels = branchLabels(String(type))
  const warn = (issues[id] ?? []).length > 0
  return (
    <div className={`fb-node${isTrigger ? ' is-trigger' : ''}${selected ? ' is-selected' : ''}`}>
      {!isTrigger && <Handle type="target" position={Position.Top} />}
      <div className="flex items-start gap-2.5">
        <span className="fb-node-icon">
          <StepIcon kind={String(type)} config={data.config} size={14} />
        </span>
        <div className="min-w-0 flex-1">
          <div className="flex items-center gap-1.5">
            <span className="eyebrow fb-node-eyebrow">{text.eyebrow}</span>
            {warn && <AlertCircle size={12} className="text-[color:var(--amber-ink)]" aria-label="Needs attention" />}
          </div>
          <div className="mt-0.5 text-[13px] font-semibold leading-snug text-text-heading">{text.title}</div>
          {data.label && <div className="mt-0.5 truncate text-[11.5px] text-text-mid">{data.label}</div>}
          {text.detail && <div className="mt-1 line-clamp-2 text-[11.5px] leading-snug text-text-muted">{text.detail}</div>}
          <div className="mt-1.5">
            <StepCount id={id} compact />
          </div>
        </div>
      </div>
      {branch ? (
        <>
          <Handle id="yes" type="source" position={Position.Bottom} className="fb-handle-yes" style={{ left: '28%' }} />
          <Handle id="no" type="source" position={Position.Bottom} className="fb-handle-no" style={{ left: '72%' }} />
          <div className="fb-branch-legend">
            <span>{labels.yes}</span>
            <span>{labels.no}</span>
          </div>
        </>
      ) : (
        type !== 'end' && <Handle type="source" position={Position.Bottom} />
      )}
    </div>
  )
}

/** LEGACY day-range section — drawn as a labelled box, executed as a pass-through. */
function SectionNode({ data, selected }: FlowNodeProps) {
  return (
    <div className={`fb-section${selected ? ' is-selected' : ''}`}>
      <NodeResizer isVisible={selected} minWidth={260} minHeight={120} />
      <Handle type="target" position={Position.Top} />
      <div className="fb-section-head">
        <span className="eyebrow">Section (legacy)</span>
        <span className="truncate text-[12.5px] font-semibold text-text-high">{data.label}</span>
      </div>
      <Handle type="source" position={Position.Bottom} />
    </div>
  )
}

export const nodeTypes = {
  trigger: StepNodeCard,
  action: StepNodeCard,
  wait: StepNodeCard,
  followup: StepNodeCard,
  decision: StepNodeCard,
  handover: StepNodeCard,
  update: StepNodeCard,
  end: StepNodeCard,
  stage: SectionNode,
}

/** A line with its label, its old-style timing, and a "+" to insert a step. */
function PlusEdge({
  id,
  sourceX,
  sourceY,
  targetX,
  targetY,
  sourcePosition,
  targetPosition,
  markerEnd,
  style,
  label,
  data,
  selected,
}: EdgeProps<FlowEdge>) {
  const { onInsertOnEdge } = useFlowView()
  const [path, labelX, labelY] = getSmoothStepPath({ sourceX, sourceY, targetX, targetY, sourcePosition, targetPosition, borderRadius: 10 })
  const timing = timingText({ id, source: '', target: '', data } as FlowEdge)
  const isTimeout = data?.config?.timing === 'No answer / timeout'
  const words = [typeof label === 'string' ? label : '', timing, isTimeout ? 'if no reply in time' : ''].filter(Boolean).join(' · ')
  return (
    <>
      <BaseEdge id={id} path={path} markerEnd={markerEnd} style={style} className={selected ? 'is-selected' : ''} />
      <EdgeLabelRenderer>
        <div
          className="fb-edge-label nodrag nopan"
          style={{ transform: `translate(-50%, -50%) translate(${labelX}px, ${labelY}px)` }}
        >
          {words && <span className="fb-edge-text">{words}</span>}
          <button
            type="button"
            className="fb-edge-plus"
            title="Insert a step here"
            aria-label="Insert a step here"
            onClick={(e) => {
              e.stopPropagation()
              onInsertOnEdge(id)
            }}
          >
            <Plus size={12} strokeWidth={2.25} aria-hidden />
          </button>
        </div>
      </EdgeLabelRenderer>
    </>
  )
}

export const edgeTypes = { plus: PlusEdge }
