'use client'

/** KPI stat tile (dataviz "the number is the chart"): label, semibold value
 *  in proportional figures, optional context line. Chrome uses semantic
 *  tokens only so both themes work. */
export function StatTile({ label, value, sub }: { label: string; value: string; sub?: string }) {
  return (
    <div className="rounded-2xl border border-line bg-card px-4 py-3.5">
      <div className="mb-1 text-[11px] font-medium text-text-muted">{label}</div>
      <div className="text-[22px] font-semibold leading-none tracking-[-0.5px] text-text-primary">
        {value}
      </div>
      {sub && <div className="mt-1.5 truncate text-[10.5px] text-text-faint">{sub}</div>}
    </div>
  )
}
