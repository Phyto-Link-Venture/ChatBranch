'use client'

import { Spline, Trash2, X } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Select } from '@/components/ui/Select'
import { Trans, useT } from '@/i18n'
import { choiceOptions, type Choice } from '@/lib/flow/nodeConfig'
import type { FlowConfig, FlowEdge } from '@/lib/flow/types'
import { Field, Notice, NumberInput, TextInput } from './fields'

/**
 * Settings for a LINE on the Advanced canvas. Most flows never need these —
 * a Wait step is the plain way to pause — but older flows put timing on the
 * line itself (wait N days / until a reply / until a record changes), and a
 * "No answer / timeout" line is the fallback a reply wait takes when nothing
 * arrives in time. The engine executes all of them.
 */

/** Stored timing values (engine contract) → what they are called. */
const TIMING_OPTIONS: Choice[] = [
  { value: 'Immediately', lbl: 'flows.edge.timingImmediately' },
  { value: 'Wait N days', lbl: 'flows.edge.timingWaitDays' },
  { value: 'On contact reply', lbl: 'flows.edge.timingOnReply' },
  { value: 'On record update', lbl: 'flows.edge.timingOnRecord' },
  { value: 'No answer / timeout', lbl: 'flows.edge.timingTimeout' },
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
  const t = useT()
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
    <aside className="flex h-full w-full flex-col bg-card" aria-label={t('flows.edge.settings')}>
      <header className="flex items-start gap-3 border-b border-line px-4 py-3">
        <span className="mt-0.5 grid h-8 w-8 shrink-0 place-items-center rounded-lg border border-line-strong bg-well text-text-high">
          <Spline size={15} aria-hidden />
        </span>
        <div className="min-w-0 flex-1">
          <div className="eyebrow">{t('flows.edge.eyebrow')}</div>
          <div className="display mt-0.5 text-[17px] leading-snug text-text-heading">{t('flows.edge.title')}</div>
        </div>
        <button
          type="button"
          onClick={onClose}
          aria-label={t('flows.edge.close')}
          className="grid h-8 w-8 shrink-0 place-items-center rounded-lg text-text-muted hover:bg-fill-hover hover:text-text-high"
        >
          <X size={16} aria-hidden />
        </button>
      </header>
      <div className="flex min-h-0 flex-1 flex-col gap-4 overflow-y-auto px-4 py-4">
        <Field label={t('flows.edge.label')}>
          <TextInput
            value={String(cfg.label ?? (typeof edge.label === 'string' ? edge.label : ''))}
            onChange={(v) => apply({ label: v })}
            placeholder={t('flows.edge.labelPlaceholder')}
            ariaLabel={t('flows.edge.label')}
          />
        </Field>
        <Field label={t('flows.edge.moveOn')}>
          <Select value={timing} onChange={(v) => apply({ timing: v })} options={choiceOptions(t, TIMING_OPTIONS)} size="md" ariaLabel={t('flows.edge.moveOn')} />
        </Field>
        {(timing === 'Wait N days' || timing === 'No answer / timeout') && (
          <Field label={timing === 'Wait N days' ? t('flows.edge.daysToWait') : t('flows.edge.giveUpAfterDays')} help={t('flows.edge.decimalsHint')}>
            <NumberInput
              value={cfg.waitDays as number}
              onChange={(v) => apply({ waitDays: v })}
              min={0}
              step={0.5}
              ariaLabel={timing === 'Wait N days' ? t('flows.edge.daysToWait') : t('flows.edge.giveUpAfterDays')}
            />
          </Field>
        )}
        {timing !== 'Immediately' && (
          <Notice>
            <Trans k="flows.edge.preferWait" values={{ wait: <strong>{t('flows.stepChoice.wait')}</strong> }} />
          </Notice>
        )}
        <Field label={t('flows.edge.lineStyle')}>
          <Select
            value={String(cfg.style ?? 'Solid')}
            onChange={(v) => apply({ style: v })}
            options={[
              { value: 'Solid', label: t('flows.edge.solid') },
              { value: 'Dashed', label: t('flows.edge.dashed') },
            ]}
            size="md"
            ariaLabel={t('flows.edge.lineStyle')}
          />
        </Field>
        <Field label={t('flows.edge.movingDashes')} help={t('flows.edge.movingDashesHint')}>
          <Select
            value={String(cfg.animated ?? 'No')}
            onChange={(v) => apply({ animated: v })}
            options={[
              { value: 'No', label: t('common.status.off') },
              { value: 'Yes', label: t('common.status.on') },
            ]}
            size="md"
            ariaLabel={t('flows.edge.movingDashes')}
          />
        </Field>
      </div>
      <footer className="flex items-center justify-end border-t border-line bg-surface px-4 py-2.5">
        <Button variant="danger" size="sm" onClick={onDelete}>
          <Trash2 size={13} aria-hidden /> {t('flows.edge.deleteLine')}
        </Button>
      </footer>
    </aside>
  )
}
