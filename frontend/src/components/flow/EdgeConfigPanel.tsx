'use client'

import { Spline, Trash2, X } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Select } from '@/components/ui/Select'
import type { FlowConfig, FlowEdge } from '@/lib/flow/types'
import { Field, Notice, NumberInput, TextInput } from './fields'

/**
 * Settings for a LINE on the Advanced canvas. Most flows never need these —
 * a Wait step is the plain way to pause — but older flows put timing on the
 * line itself (wait N days / until a reply / until a record changes), and a
 * "No answer / timeout" line is the fallback a reply wait takes when nothing
 * arrives in time. The engine executes all of them.
 */

const TIMING_OPTIONS = [
  { value: 'Immediately', label: 'Straight away' },
  { value: 'Wait N days', label: 'After waiting some days' },
  { value: 'On contact reply', label: 'When the contact replies' },
  { value: 'On record update', label: 'When their record changes' },
  { value: 'No answer / timeout', label: 'If no reply comes in time' },
]

export default function EdgeConfigPanel({
  edge,
  onChange,
  onDelete,
  onClose,
}: {
  edge: FlowEdge
  onChange: (patch: Partial<FlowEdge>) => void
  onDelete: () => void
  onClose: () => void
}) {
  const cfg: FlowConfig = edge.data?.config ?? {
    label: typeof edge.label === 'string' ? edge.label : '',
    timing: 'Immediately',
  }
  const timing = String(cfg.timing || 'Immediately')

  const apply = (patch: FlowConfig) => {
    const next = { ...cfg, ...patch }
    onChange({
      data: { ...edge.data, config: next },
      label: String(next.label ?? '') || undefined,
      animated: next.animated === 'Yes',
      style: next.style === 'Dashed' ? { ...edge.style, strokeDasharray: '7 5' } : { ...edge.style, strokeDasharray: undefined },
    })
  }

  return (
    <aside className="flex h-full w-full flex-col bg-card" aria-label="Line settings">
      <header className="flex items-start gap-3 border-b border-line px-4 py-3">
        <span className="mt-0.5 grid h-8 w-8 shrink-0 place-items-center rounded-lg border border-line-strong bg-well text-text-high">
          <Spline size={15} aria-hidden />
        </span>
        <div className="min-w-0 flex-1">
          <div className="eyebrow">Line</div>
          <div className="display mt-0.5 text-[17px] leading-snug text-text-heading">When does the flow move on?</div>
        </div>
        <button
          type="button"
          onClick={onClose}
          aria-label="Close line settings"
          className="grid h-8 w-8 shrink-0 place-items-center rounded-lg text-text-muted hover:bg-fill-hover hover:text-text-high"
        >
          <X size={16} aria-hidden />
        </button>
      </header>
      <div className="flex min-h-0 flex-1 flex-col gap-4 overflow-y-auto px-4 py-4">
        <Field label="Label on the line">
          <TextInput value={String(cfg.label ?? (typeof edge.label === 'string' ? edge.label : ''))} onChange={(v) => apply({ label: v })} placeholder="e.g. Yes" ariaLabel="Label" />
        </Field>
        <Field label="Move on">
          <Select value={timing} onChange={(v) => apply({ timing: v })} options={TIMING_OPTIONS} size="md" ariaLabel="Timing" />
        </Field>
        {(timing === 'Wait N days' || timing === 'No answer / timeout') && (
          <Field label={timing === 'Wait N days' ? 'Days to wait' : 'Give up after (days)'} help="Decimals work: 0.5 = 12 hours.">
            <NumberInput value={cfg.waitDays as number} onChange={(v) => apply({ waitDays: v })} min={0} step={0.5} ariaLabel="Days" />
          </Field>
        )}
        {timing !== 'Immediately' && (
          <Notice>
            Prefer a <strong>Wait</strong> step for new flows — it reads more clearly in the Steps view and does the same
            thing.
          </Notice>
        )}
        <Field label="Line style">
          <Select
            value={String(cfg.style ?? 'Solid')}
            onChange={(v) => apply({ style: v })}
            options={[
              { value: 'Solid', label: 'Solid' },
              { value: 'Dashed', label: 'Dashed' },
            ]}
            size="md"
            ariaLabel="Line style"
          />
        </Field>
        <Field label="Moving dashes" help="Use sparingly — e.g. to highlight the main path.">
          <Select
            value={String(cfg.animated ?? 'No')}
            onChange={(v) => apply({ animated: v })}
            options={[
              { value: 'No', label: 'Off' },
              { value: 'Yes', label: 'On' },
            ]}
            size="md"
            ariaLabel="Moving dashes"
          />
        </Field>
      </div>
      <footer className="flex items-center justify-end border-t border-line bg-surface px-4 py-2.5">
        <Button variant="danger" size="sm" onClick={onDelete}>
          <Trash2 size={13} aria-hidden /> Delete line
        </Button>
      </footer>
    </aside>
  )
}
