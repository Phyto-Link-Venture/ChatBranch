'use client'

import { useState } from 'react'
import { Handle, Position, NodeResizer, useReactFlow, type NodeProps } from '@xyflow/react'
import { Flag, Layers, PhoneCall, Split, UserRound, Zap, type LucideIcon } from 'lucide-react'
import { CONFIG_SCHEMAS } from '@/lib/flow/nodeConfig'
import type { FlowConfig, FlowNode, NodeKind } from '@/lib/flow/types'

/**
 * The node KINDS a client can build flows with.
 * To add a new kind: add one entry here + (optionally) a default label.
 * Everything else — palette, colors, drag & drop, minimap tint — picks it
 * up automatically.
 *
 * 'stage' is special: it renders as a SECTION (a resizable day-range lane,
 * like the stage lanes in Diagram 01). Blocks dropped inside it become its
 * children and move with it.
 *
 * Kind colors reuse the app's accent scales (tailwind.config) so the canvas
 * reads as part of the dashboard in both themes.
 */
export interface PaletteEntry {
  kind: NodeKind
  title: string
  color: string
  Icon: LucideIcon
  hint: string
}

export const PALETTE: PaletteEntry[] = [
  { kind: 'trigger', title: 'Trigger', color: '#8B5CF6', Icon: Zap, hint: 'Date-based event that starts a flow (e.g. 1 day after due)' },
  { kind: 'stage', title: 'Stage section', color: '#3B82F6', Icon: Layers, hint: 'Day-range lane (e.g. Normal D+1 → D+7) — drop steps inside it' },
  { kind: 'action', title: 'Action', color: '#10B981', Icon: PhoneCall, hint: 'Something the caller does (verify, offer, confirm, log)' },
  { kind: 'decision', title: 'Decision', color: '#F59E0B', Icon: Split, hint: 'Yes / No branch point' },
  { kind: 'handover', title: 'Handover', color: '#EF4444', Icon: UserRound, hint: 'Transfer the case to a human' },
  { kind: 'end', title: 'End', color: '#64748B', Icon: Flag, hint: 'Close the call / stop the flow' },
]

export const DEFAULT_LABELS: Record<NodeKind, string> = {
  trigger: 'New trigger — e.g. record is 8 days past due',
  stage: 'New stage — drop steps inside',
  action: 'New action — what does the caller do?',
  decision: 'New decision?',
  handover: 'Hand over to human — why?',
  end: 'End — close & log',
}

export const paletteByKind = Object.fromEntries(PALETTE.map((p) => [p.kind, p])) as Record<
  NodeKind,
  PaletteEntry
>

export function KindIcon({ kind, size = 14 }: { kind: NodeKind; size?: number }) {
  const Icon = paletteByKind[kind].Icon
  return <Icon size={size} strokeWidth={2} aria-hidden />
}

/** Double-click any node to edit its text. Saves on blur or Enter. */
function EditableLabel({ id, label }: { id: string; label: string }) {
  const { updateNodeData } = useReactFlow()
  const [editing, setEditing] = useState(false)

  if (editing) {
    return (
      <textarea
        className="node-label-input"
        autoFocus
        defaultValue={label}
        aria-label="Node text"
        onFocus={(e) => e.target.select()}
        onBlur={(e) => {
          updateNodeData(id, { label: e.target.value.trim() || label })
          setEditing(false)
        }}
        onKeyDown={(e) => {
          if (e.key === 'Enter' && !e.shiftKey) (e.target as HTMLTextAreaElement).blur()
          if (e.key === 'Escape') setEditing(false)
        }}
      />
    )
  }
  return (
    <div className="node-label" onDoubleClick={() => setEditing(true)} title="Double-click to edit">
      {label}
    </div>
  )
}

/** Short display value for a config chip. */
function chipText(fieldKey: string, value: unknown): string {
  if (Array.isArray(value)) return value.join(' + ')
  if (fieldKey === 'maxAttempts') return `max ×${value}`
  return String(value)
}

/** A config value counts as "set" unless it's empty ('' or []). */
const hasValue = (v: unknown) =>
  v !== undefined && v !== '' && !(Array.isArray(v) && v.length === 0)

/**
 * Display text for a stage's day range, built from its structured config
 * (rangeType + fromDay + toDay) — "day 1–7 past due", "day 31+ past due",
 * "last 7 days before due". Falls back to a legacy free-text dayRange.
 */
function stageRangeText(cfg: FlowConfig | undefined): string {
  const from = cfg?.fromDay
  if (from === undefined || from === '') return String(cfg?.dayRange ?? '')
  const to = cfg?.toDay === undefined || cfg?.toDay === '' ? null : cfg?.toDay
  if (cfg?.rangeType === 'Days before due date') {
    return to === 0 || to === null ? `last ${from} days before due` : `${from}–${to} days before due`
  }
  return to === null ? `day ${from}+ past due` : `day ${from}–${to} past due`
}

/** Friendly text for WHEN a trigger fires — "7 days before due", "day 3 after due". */
function triggerDayText(config: FlowConfig | undefined): string {
  const d = config?.days
  if (config?.when === 'On the due date') return 'on due date'
  if (config?.when === 'Record status is') return config?.status ? `status: ${config.status}` : ''
  if (config?.when === 'Contact has tag') return config?.tag ? `tag: ${config.tag}` : ''
  if (d === undefined || d === '') return ''
  if (config?.when === 'Days before due date') return `${d} day${Number(d) === 1 ? '' : 's'} before due`
  if (config?.when === 'Days after due date') return `day ${d} after due`
  if (config?.when === 'Days after due date (exactly)') return `exactly day ${d} after due`
  return ''
}

/**
 * n8n-style summary inside the card: the configured values as small chips
 * (day timing, channels, model…) plus one truncated line for the script text.
 * The trigger's when+days pair merges into a single plain-words chip.
 */
function ConfigSummary({ kind, config }: { kind: NodeKind; config?: FlowConfig }) {
  const schema = CONFIG_SCHEMAS[kind] ?? []
  const set = schema.filter((f) => hasValue(config?.[f.key]))
  const items: { key: string; text: string; title: string; cls?: string }[] = []
  if (kind === 'trigger') {
    const t = triggerDayText(config)
    if (t) items.push({ key: '_day', text: t, title: 'When it fires' })
  }
  // Template-mode WhatsApp send (waMode contract) — badge it so flows that
  // message outside the 24h window are readable at a glance.
  if (kind === 'action' && config?.waMode === 'template') {
    const name = typeof config.templateName === 'string' ? config.templateName : ''
    items.push({
      key: '_waTemplate',
      text: name ? `Template · ${name}` : 'Template',
      title: 'Sends an approved WhatsApp template — deliverable outside the 24h window',
      cls: 'chip-template',
    })
  }
  for (const f of set) {
    if (f.type === 'textarea') continue
    if (kind === 'trigger' && (f.key === 'when' || f.key === 'days')) continue
    items.push({ key: f.key, text: chipText(f.key, config?.[f.key]), title: f.label })
  }
  const chips = items.slice(0, 3)
  const long = set.find((f) => f.type === 'textarea')
  if (!chips.length && !long) return null
  return (
    <div className="node-config-summary">
      {chips.length > 0 && (
        <div className="node-chips">
          {chips.map((c) => (
            <span key={c.key} className={`chip${c.cls ? ` ${c.cls}` : ''}`} title={c.title}>
              {c.text}
            </span>
          ))}
        </div>
      )}
      {long && (
        <div className="node-script" title={String(config?.[long.key])}>
          <span>{long.label}:</span> {String(config?.[long.key])}
        </div>
      )}
    </div>
  )
}

type FlowNodeProps = NodeProps<FlowNode>

/**
 * Shared card look for every kind; handles differ per kind below.
 * The kind color travels as a CSS variable so hover/selected rings,
 * icon chip, and border tint all derive from one value in CSS.
 */
function NodeCard({
  id,
  kind,
  data,
  children,
}: {
  id: string
  kind: NodeKind
  data: FlowNode['data']
  children?: React.ReactNode
}) {
  const meta = paletteByKind[kind]
  return (
    <div className="node-card" style={{ '--kind': meta.color } as React.CSSProperties}>
      <div className="node-head">
        <span className="node-icon">
          <KindIcon kind={kind} />
        </span>
        <span className="node-kind">{meta.title}</span>
      </div>
      <EditableLabel id={id} label={data.label} />
      <ConfigSummary kind={kind} config={data.config} />
      {children}
    </div>
  )
}

function TriggerNode({ id, data }: FlowNodeProps) {
  return (
    <NodeCard id={id} kind="trigger" data={data}>
      <Handle type="source" position={Position.Bottom} />
    </NodeCard>
  )
}

/**
 * The Stage SECTION: a resizable day-range lane (mirrors the stage lanes in
 * Diagram 01). Drop blocks inside it — they become children and move with it.
 * The stage name + day range come from its config (client-set, skill 00).
 */
function StageSectionNode({ id, data, selected }: FlowNodeProps) {
  const meta = paletteByKind.stage
  const cfg = data.config ?? {}
  const chip = [cfg.stage, stageRangeText(cfg)].filter(Boolean).join(' · ')
  return (
    <div className="section-node" style={{ '--kind': meta.color } as React.CSSProperties}>
      <NodeResizer isVisible={selected} minWidth={320} minHeight={220} color={meta.color} />
      <Handle type="target" position={Position.Top} />
      <div className="section-head">
        <span className="node-icon">
          <KindIcon kind="stage" />
        </span>
        <EditableLabel id={id} label={data.label} />
        {chip && <span className="section-chip">{chip}</span>}
      </div>
      <Handle type="source" position={Position.Bottom} />
    </div>
  )
}

function ActionNode({ id, data }: FlowNodeProps) {
  return (
    <NodeCard id={id} kind="action" data={data}>
      <Handle type="target" position={Position.Top} />
      <Handle type="source" position={Position.Bottom} />
    </NodeCard>
  )
}

/** Decision has TWO source handles: drag from "Yes" or "No" — the edge labels itself. */
function DecisionNode({ id, data }: FlowNodeProps) {
  return (
    <NodeCard id={id} kind="decision" data={data}>
      <Handle type="target" position={Position.Top} />
      <Handle id="yes" type="source" position={Position.Bottom} className="handle-yes" style={{ left: '25%' }} />
      <Handle id="no" type="source" position={Position.Bottom} className="handle-no" style={{ left: '75%' }} />
      <div className="decision-legend">
        <span className="yes">Yes</span>
        <span className="no">No</span>
      </div>
    </NodeCard>
  )
}

function HandoverNode({ id, data }: FlowNodeProps) {
  return (
    <NodeCard id={id} kind="handover" data={data}>
      <Handle type="target" position={Position.Top} />
      <Handle type="source" position={Position.Bottom} />
    </NodeCard>
  )
}

function EndNode({ id, data }: FlowNodeProps) {
  return (
    <NodeCard id={id} kind="end" data={data}>
      <Handle type="target" position={Position.Top} />
    </NodeCard>
  )
}

/** Registered once with <ReactFlow nodeTypes={nodeTypes}>. */
export const nodeTypes = {
  trigger: TriggerNode,
  stage: StageSectionNode,
  action: ActionNode,
  decision: DecisionNode,
  handover: HandoverNode,
  end: EndNode,
}
