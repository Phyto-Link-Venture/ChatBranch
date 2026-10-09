'use client'

import { Copy, Plus, X } from 'lucide-react'
import { useFormat, useT } from '@/i18n'
import { WEEKDAYS, weekdayDate, type AiSchedule, type Weekday } from '@/lib/aiControls'

const timeCls =
  'h-8 w-[122px] rounded-md border border-line-strong bg-card px-2 text-[12.5px] tabular-nums text-text-primary outline-none focus:border-text-primary disabled:opacity-60'

/**
 * Per-weekday time windows for the AI schedule (org timezone). A day with no
 * windows is "never" for that day; 00:00–00:00 is the whole day; a window that
 * ends before it starts runs past midnight.
 */
export function AiScheduleEditor({
  value,
  onChange,
  disabled,
}: {
  value: AiSchedule
  onChange: (next: AiSchedule) => void
  disabled?: boolean
}) {
  const t = useT()
  const f = useFormat()
  const day = (d: Weekday) => f.weekday(weekdayDate(d))
  const setDay = (d: Weekday, windows: [string, string][]) => onChange({ ...value, [d]: windows })
  const copyToWeekdays = (from: Weekday) => {
    const next = { ...value }
    for (const d of ['mon', 'tue', 'wed', 'thu', 'fri'] as Weekday[]) next[d] = value[from].map(([a, b]) => [a, b])
    onChange(next)
  }

  return (
    <div className="flex flex-col divide-y divide-line rounded-lg border border-line">
      {WEEKDAYS.map((d) => {
        const windows = value[d] ?? []
        return (
          <div key={d} className="flex flex-wrap items-center gap-x-3 gap-y-1.5 px-3 py-2">
            <span className="w-9 shrink-0 text-[12.5px] font-semibold text-text-high">{day(d)}</span>
            <div className="order-last flex min-w-0 basis-full flex-wrap items-center gap-2 sm:order-none sm:basis-0 sm:flex-1">
              {windows.length === 0 && <span className="text-[12.5px] text-text-faint">{t('chats.aiControls.editor.noHours')}</span>}
              {windows.map(([start, end], i) => (
                <span key={i} className="inline-flex items-center gap-1">
                  <input
                    type="time"
                    value={start}
                    disabled={disabled}
                    aria-label={t('chats.aiControls.editor.from', { day: day(d) })}
                    onChange={(e) => setDay(d, windows.map((w, j) => (j === i ? [e.target.value, w[1]] : w)))}
                    className={timeCls}
                  />
                  <span className="text-text-muted">–</span>
                  <input
                    type="time"
                    value={end}
                    disabled={disabled}
                    aria-label={t('chats.aiControls.editor.to', { day: day(d) })}
                    onChange={(e) => setDay(d, windows.map((w, j) => (j === i ? [w[0], e.target.value] : w)))}
                    className={timeCls}
                  />
                  {!disabled && (
                    <button
                      type="button"
                      onClick={() => setDay(d, windows.filter((_, j) => j !== i))}
                      aria-label={t('chats.aiControls.editor.removeWindow', { day: day(d) })}
                      className="grid h-7 w-7 place-items-center rounded-md text-text-muted hover:bg-fill-hover hover:text-text-high"
                    >
                      <X size={13} aria-hidden />
                    </button>
                  )}
                </span>
              ))}
            </div>
            {!disabled && (
              <div className="ml-auto flex shrink-0 items-center gap-1">
                {d === 'mon' && windows.length > 0 && (
                  <button
                    type="button"
                    onClick={() => copyToWeekdays('mon')}
                    className="inline-flex h-7 items-center gap-1 rounded-md px-2 text-[12px] font-semibold text-text-muted hover:bg-fill-hover hover:text-text-high"
                    title={t('chats.aiControls.editor.copyHint')}
                  >
                    <Copy size={12} aria-hidden /> {t('chats.aiControls.editor.copyLabel', { from: day('mon'), to: day('fri') })}
                  </button>
                )}
                {windows.length < 4 && (
                  <button
                    type="button"
                    onClick={() => setDay(d, [...windows, windows.length ? ['18:00', '23:59'] : ['09:00', '18:00']])}
                    className="inline-flex h-7 items-center gap-1 rounded-md px-2 text-[12px] font-semibold text-[color:var(--brand-ink)] hover:bg-fill-hover"
                  >
                    <Plus size={12} aria-hidden /> {t('chats.aiControls.editor.addHours')}
                  </button>
                )}
              </div>
            )}
          </div>
        )
      })}
    </div>
  )
}
