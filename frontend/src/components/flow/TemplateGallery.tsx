'use client'

import { ArrowRight, Plus } from 'lucide-react'
import { STARTER_TEMPLATES, type StarterTemplate } from '@/lib/flow/templates'

/**
 * "Start from a template": the five starter flows plus a blank one. Used by the
 * builder's empty state and by its "New flow" dialog.
 */
export default function TemplateGallery({ onPick }: { onPick: (t: StarterTemplate | null) => void }) {
  return (
    <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
      {STARTER_TEMPLATES.map((t) => (
        <button
          key={t.id}
          type="button"
          onClick={() => onPick(t)}
          className="group flex flex-col rounded-xl border border-line-strong bg-card p-4 text-left transition-colors hover:border-text-primary"
        >
          <span className="display text-[17px] leading-tight text-text-heading">{t.name}</span>
          <span className="mt-1.5 text-[12.5px] leading-snug text-text-muted">{t.hint}</span>
          <span className="mt-3 flex flex-wrap items-center gap-1 text-[11px] text-text-mid">
            {t.outline.map((step, i) => (
              <span key={step} className="inline-flex items-center gap-1">
                {i > 0 && <ArrowRight size={10} className="text-text-faint" aria-hidden />}
                <span className="rounded-md border border-line bg-well px-1.5 py-0.5">{step}</span>
              </span>
            ))}
          </span>
        </button>
      ))}
      <button
        type="button"
        onClick={() => onPick(null)}
        className="flex flex-col items-start justify-center rounded-xl border border-dashed border-line-strong bg-fill-subtle p-4 text-left transition-colors hover:border-text-primary"
      >
        <span className="grid h-8 w-8 place-items-center rounded-lg border border-line-strong bg-card text-text-high">
          <Plus size={15} aria-hidden />
        </span>
        <span className="mt-2 text-[14px] font-semibold text-text-heading">Blank flow</span>
        <span className="mt-0.5 text-[12.5px] text-text-muted">Start with just a trigger and build it step by step.</span>
      </button>
    </div>
  )
}
