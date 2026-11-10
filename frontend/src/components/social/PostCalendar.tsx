'use client'

/**
 * /social: a simple week / month calendar of posts. Days are calendar days in
 * the zone the dashboard shows times in (`useFormat().dayKey`); a post sits on
 * its scheduled day, else the day it was made. Clicking a post opens it.
 */

import { useMemo } from 'react'
import { ChevronLeft, ChevronRight } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { ChannelLogo } from '@/components/ui/ChannelLogo'
import { platformId, type SocialAccount, type SocialPost } from '@/lib/social'
import { useFormat, useT } from '@/i18n'
import { postTime, statusMeta } from './PostParts'

/** "YYYY-MM-DD" ± days (pure calendar arithmetic, no zone). */
export const addDays = (ymd: string, n: number) => {
  const [y, m, d] = ymd.split('-').map(Number)
  const v = new Date(Date.UTC(y, m - 1, d + n))
  return v.toISOString().slice(0, 10)
}
/** Monday-based weekday 0..6 of a "YYYY-MM-DD". */
const weekdayMon = (ymd: string) => (new Date(`${ymd}T00:00:00Z`).getUTCDay() + 6) % 7

/** The first and last day ("YYYY-MM-DD", inclusive) the grid shows around `anchor`. */
export function calendarRange(view: 'week' | 'month', anchor: string): { start: string; end: string } {
  if (view === 'week') {
    const start = addDays(anchor, -weekdayMon(anchor))
    return { start, end: addDays(start, 6) }
  }
  const first = `${anchor.slice(0, 7)}-01`
  const start = addDays(first, -weekdayMon(first))
  return { start, end: addDays(start, 41) }
}

/** Move the anchor one week / month. */
export function shiftAnchor(view: 'week' | 'month', anchor: string, dir: 1 | -1): string {
  if (view === 'week') return addDays(anchor, 7 * dir)
  const [y, m] = anchor.split('-').map(Number)
  const v = new Date(Date.UTC(y, m - 1 + dir, 1))
  return v.toISOString().slice(0, 10)
}

export function PostCalendar({
  view,
  anchor,
  today,
  posts,
  accounts,
  onAnchor,
  onOpen,
}: {
  view: 'week' | 'month'
  anchor: string
  today: string
  posts: SocialPost[]
  accounts: SocialAccount[]
  onAnchor: (ymd: string) => void
  onOpen: (p: SocialPost) => void
}) {
  const t = useT()
  const f = useFormat()
  const { start, end } = calendarRange(view, anchor)
  const days = useMemo(() => {
    const out: string[] = []
    for (let d = start; d <= end; d = addDays(d, 1)) out.push(d)
    return out
  }, [start, end])

  const byDay = useMemo(() => {
    const map: Record<string, SocialPost[]> = {}
    for (const p of posts) {
      const ts = postTime(p)
      if (!ts) continue
      const k = f.dayKey(ts)
      ;(map[k] ||= []).push(p)
    }
    for (const k of Object.keys(map)) map[k].sort((a, b) => postTime(a).localeCompare(postTime(b)))
    return map
  }, [posts, f])

  const platformOf = (p: SocialPost) => {
    const first = p.platforms?.[0] ?? accounts.find((a) => a.id === p.account_ids?.[0])?.platform
    return first ? platformId(first) : 'social'
  }

  // weekday headers from a known Monday (2024-01-01)
  const heads = Array.from({ length: 7 }, (_, i) => f.weekday(`${addDays('2024-01-01', i)}T12:00:00Z`, 'UTC'))
  const title =
    view === 'month'
      ? f.monthYearLong(`${anchor.slice(0, 7)}-15T12:00:00Z`, 'UTC')
      : `${f.dayMonth(`${start}T12:00:00Z`, 'UTC')} – ${f.date(`${end}T12:00:00Z`, 'UTC')}`
  const maxPerDay = view === 'month' ? 3 : 12

  return (
    <div data-tour="social-calendar" className="flex flex-col gap-2">
      <div className="flex flex-wrap items-center gap-2">
        <Button variant="secondary" size="sm" onClick={() => onAnchor(shiftAnchor(view, anchor, -1))} aria-label={t('social.calendar.prev')}>
          <ChevronLeft size={14} aria-hidden />
        </Button>
        <Button variant="secondary" size="sm" onClick={() => onAnchor(today)}>
          {t('common.time.today')}
        </Button>
        <Button variant="secondary" size="sm" onClick={() => onAnchor(shiftAnchor(view, anchor, 1))} aria-label={t('social.calendar.next')}>
          <ChevronRight size={14} aria-hidden />
        </Button>
        <span className="display ml-1 text-[17px] text-text-heading">{title}</span>
      </div>
      <div className="overflow-x-auto">
        <div className="grid min-w-[640px] grid-cols-7 overflow-hidden rounded-xl border border-line bg-line" style={{ gap: 1 }}>
          {heads.map((h) => (
            <div key={h} className="bg-well px-2 py-1 text-[11px] font-semibold text-text-muted">
              {h}
            </div>
          ))}
          {days.map((d) => {
            const items = byDay[d] ?? []
            const outside = view === 'month' && d.slice(0, 7) !== anchor.slice(0, 7)
            return (
              <div
                key={d}
                className={`flex flex-col gap-1 bg-card p-1.5 ${view === 'month' ? 'min-h-[92px]' : 'min-h-[220px]'} ${outside ? 'opacity-50' : ''}`}
              >
                <span
                  className={`self-start rounded-full px-1.5 text-[11px] font-semibold ${
                    d === today ? 'bg-primary text-on-primary' : 'text-text-mid'
                  }`}
                >
                  {Number(d.slice(8))}
                </span>
                {items.slice(0, maxPerDay).map((p) => (
                  <button
                    key={String(p.id)}
                    type="button"
                    onClick={() => onOpen(p)}
                    title={p.content}
                    className="flex min-w-0 cursor-pointer items-center gap-1 rounded-md border border-line px-1 py-0.5 text-left text-[11px] text-text-high hover:bg-fill-hover"
                  >
                    <span className={`h-1.5 w-1.5 shrink-0 rounded-full ${statusMeta(String(p.status)).dot}`} aria-hidden />
                    <ChannelLogo channel={platformOf(p)} size={10} title="" />
                    <span className="shrink-0 text-text-muted">{f.time(postTime(p))}</span>
                    <span className="truncate">{p.content || '—'}</span>
                  </button>
                ))}
                {items.length > maxPerDay && (
                  <span className="text-[10.5px] text-text-muted">{t('social.calendar.more', { count: items.length - maxPerDay })}</span>
                )}
              </div>
            )
          })}
        </div>
      </div>
    </div>
  )
}
