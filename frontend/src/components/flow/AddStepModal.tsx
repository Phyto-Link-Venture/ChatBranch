'use client'

import { Modal } from '@/components/ui/Modal'
import { useT } from '@/i18n'
import { STEP_CHOICES, type StepChoice } from '@/lib/flow/nodeConfig'
import { StepIcon } from './stepIcons'

/**
 * "Add a step here" — the step palette as a list of plain-language choices.
 * Used by the "+" between steps (Steps view) and the "+" on a line (canvas).
 * End is only offered where nothing follows: ending mid-path would strand
 * the rest of it.
 */
export default function AddStepModal({
  open,
  onClose,
  onPick,
  hasContinuation,
}: {
  open: boolean
  onClose: () => void
  onPick: (choice: StepChoice) => void
  hasContinuation: boolean
}) {
  const t = useT()
  return (
    <Modal open={open} onClose={onClose} title={t('flows.addStep.title')} subtitle={t('flows.addStep.subtitle')} size="md">
      <div className="grid grid-cols-1 gap-2 sm:grid-cols-2">
        {STEP_CHOICES.map((c) => {
          const blocked = c.kind === 'end' && hasContinuation
          return (
            <button
              key={c.id}
              type="button"
              disabled={blocked}
              title={blocked ? t('flows.addStep.endBlocked') : undefined}
              onClick={() => onPick(c)}
              className="flex items-start gap-3 rounded-xl border border-line bg-card px-3 py-2.5 text-left transition-colors hover:border-text-primary disabled:cursor-not-allowed disabled:opacity-45 disabled:hover:border-line"
            >
              <span className="mt-0.5 grid h-7 w-7 shrink-0 place-items-center rounded-lg border border-line bg-well text-text-high">
                <StepIcon kind={c.kind === 'action' ? c.id : c.kind} config={c.config} />
              </span>
              <span className="min-w-0">
                <span className="block text-[13px] font-semibold text-text-heading">{t(c.title)}</span>
                <span className="mt-0.5 block text-[11.5px] leading-snug text-text-muted">{t(c.hint)}</span>
              </span>
            </button>
          )
        })}
      </div>
    </Modal>
  )
}
