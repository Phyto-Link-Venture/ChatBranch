'use client'

import type { Dispatch, SetStateAction } from 'react'
import { Spline, X } from 'lucide-react'
import type { FlowConfig, FlowEdge } from '@/lib/flow/types'

/**
 * Settings drawer for a LINE (edge) — same n8n pattern as the node panel.
 * Click a line to open it: rename the label, say when the transition fires
 * (immediately / wait N days / on reply / on record update / no answer), switch
 * solid ↔ dashed, toggle the animated marching-ants, or delete the line.
 *
 * Raw values live in edge.data.config; the label shown on the canvas is
 * recomputed from them (e.g. "No · wait 2d"), and everything exports with
 * the JSON.
 */

const TIMING_OPTIONS = [
  'Immediately',
  'Wait N days',
  'On contact reply',
  'On record update',
  'No answer / timeout',
]

const timingSuffix = (cfg: FlowConfig): string =>
  cfg.timing === 'Wait N days'
    ? `wait ${cfg.waitDays === '' || cfg.waitDays === undefined ? '?' : cfg.waitDays}d`
    : cfg.timing === 'On contact reply'
      ? 'on reply'
      : cfg.timing === 'On record update'
        ? 'on update'
        : cfg.timing === 'No answer / timeout'
          ? 'no answer'
          : ''

export default function EdgeConfigPanel({
  edge,
  setEdges,
  onClose,
}: {
  edge: FlowEdge
  setEdges: Dispatch<SetStateAction<FlowEdge[]>>
  onClose: () => void
}) {
  /** Seed from the edge so lines made before this feature still open cleanly. */
  const cfg: FlowConfig = edge.data?.config ?? {
    label: typeof edge.label === 'string' ? edge.label : '',
    timing: 'Immediately',
    waitDays: 2,
    animated: edge.animated ? 'Yes' : 'No',
    style: edge.style?.strokeDasharray ? 'Dashed' : 'Solid',
  }

  const apply = (patch: FlowConfig) => {
    const next = { ...cfg, ...patch }
    setEdges((eds) =>
      eds.map((e) => {
        if (e.id !== edge.id) return e
        const suffix = timingSuffix(next)
        return {
          ...e,
          data: { ...e.data, config: next },
          label: [next.label, suffix].filter(Boolean).join(' · ') || undefined,
          animated: next.animated === 'Yes',
          style:
            next.style === 'Dashed'
              ? { ...e.style, strokeDasharray: '7 5' }
              : { ...e.style, strokeDasharray: undefined },
        }
      }),
    )
  }

  const deleteEdge = () => {
    setEdges((eds) => eds.filter((e) => e.id !== edge.id))
    onClose()
  }

  return (
    <aside className="config-panel" style={{ '--kind': 'var(--text-muted)' } as React.CSSProperties}>
      <header className="config-header">
        <span className="config-icon">
          <Spline size={15} aria-hidden />
        </span>
        <div className="config-title">
          <span className="config-kind">Line</span>
          <span className="config-hint">When does the flow move along it?</span>
        </div>
        <button className="config-close" onClick={onClose} title="Close" aria-label="Close settings">
          <X size={16} aria-hidden />
        </button>
      </header>

      <div className="config-body">
        <label className="config-field">
          <span>Label on the line</span>
          <input
            type="text"
            value={String(cfg.label ?? '')}
            placeholder="e.g. Yes / No / Unpaid at D+8"
            onChange={(e) => apply({ label: e.target.value })}
          />
        </label>

        <label className="config-field">
          <span>When does it fire?</span>
          <select value={String(cfg.timing ?? 'Immediately')} onChange={(e) => apply({ timing: e.target.value })}>
            {TIMING_OPTIONS.map((opt) => (
              <option key={opt} value={opt}>
                {opt}
              </option>
            ))}
          </select>
        </label>

        {cfg.timing === 'Wait N days' && (
          <label className="config-field">
            <span>Days to wait</span>
            <input
              type="number"
              min="0"
              value={String(cfg.waitDays ?? '')}
              onChange={(e) => apply({ waitDays: e.target.value === '' ? '' : Number(e.target.value) })}
            />
            <small>Shown on the line as &quot;wait Nd&quot;. Day values are client-set defaults (skill 00).</small>
          </label>
        )}

        <label className="config-field">
          <span>Line style</span>
          <select value={String(cfg.style ?? 'Solid')} onChange={(e) => apply({ style: e.target.value })}>
            <option value="Solid">Solid</option>
            <option value="Dashed">Dashed</option>
          </select>
        </label>

        <label className="config-field">
          <span>Animated (moving dashes)</span>
          <select value={String(cfg.animated ?? 'No')} onChange={(e) => apply({ animated: e.target.value })}>
            <option value="No">No</option>
            <option value="Yes">Yes</option>
          </select>
          <small>Use sparingly — e.g. to highlight the main path in a walkthrough.</small>
        </label>

        <button className="fb-btn fb-btn-danger" onClick={deleteEdge}>
          Delete this line
        </button>
      </div>

      <footer className="config-footer">Changes save automatically and travel with Export JSON.</footer>
    </aside>
  )
}
