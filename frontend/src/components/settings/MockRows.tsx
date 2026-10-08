'use client'

import { useState, type ReactNode } from 'react'

/** Shared building blocks for settings rows. Row/SectionLabel are plain
 * presentational layout used by real tabs too; Toggle/MockSelect hold local
 * mock state and only remain on the demo-only rows of AI Configuration. */

export function Toggle({ defaultOn }: { defaultOn: boolean }) {
  const [on, setOn] = useState(defaultOn)
  return (
    <div
      onClick={() => setOn(!on)}
      className="relative h-[22px] w-[38px] cursor-pointer rounded-full transition-colors"
      style={{ background: on ? 'rgb(var(--c-brand))' : 'var(--fill-hover)' }}
    >
      <div
        className="absolute top-[3px] h-4 w-4 rounded-full bg-white transition-all"
        style={{ left: on ? 18 : 3 }}
      />
    </div>
  )
}

export function Row({ label, sub, control }: { label: string; sub?: string | null; control: ReactNode }) {
  return (
    <div className="flex items-center justify-between gap-3 border-b border-line py-3.5">
      <div>
        <div className="mb-0.5 text-[13px] font-semibold text-text-high">{label}</div>
        {sub && <div className="text-[11px] text-text-muted">{sub}</div>}
      </div>
      {control}
    </div>
  )
}

export function MockSelect({ opts, val }: { opts: string[]; val: string }) {
  const [v, setV] = useState(val)
  return (
    <select
      value={v}
      onChange={(e) => setV(e.target.value)}
      className="cursor-pointer rounded-[7px] border border-line-strong bg-surface px-2.5 py-1.5 text-[12px] font-semibold text-text-high outline-none"
    >
      {opts.map((o) => (
        <option key={o} value={o}>
          {o}
        </option>
      ))}
    </select>
  )
}

/** Controlled variants — value + onChange lifted to the parent so a tab can
 * stage edits and Save/Discard them via SettingsDirtyContext. */
export function ControlledToggle({ on, onChange }: { on: boolean; onChange: (v: boolean) => void }) {
  return (
    <div
      onClick={() => onChange(!on)}
      className="relative h-[22px] w-[38px] cursor-pointer rounded-full transition-colors"
      style={{ background: on ? 'rgb(var(--c-brand))' : 'var(--fill-hover)' }}
    >
      <div className="absolute top-[3px] h-4 w-4 rounded-full bg-white transition-all" style={{ left: on ? 18 : 3 }} />
    </div>
  )
}

export function ControlledSelect({
  opts,
  value,
  onChange,
}: {
  opts: string[]
  value: string
  onChange: (v: string) => void
}) {
  return (
    <select
      value={value}
      onChange={(e) => onChange(e.target.value)}
      className="cursor-pointer rounded-[7px] border border-line-strong bg-well px-2.5 py-1.5 text-[12px] font-semibold text-text-high outline-none"
    >
      {opts.map((o) => (
        <option key={o} value={o}>
          {o}
        </option>
      ))}
    </select>
  )
}

export function SectionLabel({ t, cls = '' }: { t: string; cls?: string }) {
  return (
    <div className={`text-[11px] font-semibold uppercase tracking-[0.06em] text-text-muted ${cls}`}>
      {t}
    </div>
  )
}
