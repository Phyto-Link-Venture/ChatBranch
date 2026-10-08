'use client'

import type { AnalyticsSummary } from './types'

/** 95 -> "1m 35s", 3610 -> "1h 0m", 42 -> "42s" */
export const duration = (secs: number) => {
  if (secs >= 3600) return `${Math.floor(secs / 3600)}h ${Math.round((secs % 3600) / 60)}m`
  if (secs >= 60) return `${Math.floor(secs / 60)}m ${Math.round(secs % 60)}s`
  return `${Math.round(secs)}s`
}

const pct = (rate: number) => `${Math.round(rate * 1000) / 10}%`

type Tone = 'good' | 'bad' | 'flat'

interface Kpi {
  label: string
  value: string
  sub: string
  tone: Tone
}

/** "▲ 8.2% vs prior" — direction × whether up is good decides the ink. */
function change(cur: number, prev: number, upIsGood = true): { text: string; tone: Tone } | null {
  if (!Number.isFinite(cur) || !Number.isFinite(prev) || prev <= 0) return null
  const d = ((cur - prev) / prev) * 100
  if (Math.abs(d) < 0.05) return { text: 'no change vs prior', tone: 'flat' }
  const up = d > 0
  return {
    text: `${up ? '▲' : '▼'} ${Math.abs(Math.round(d * 10) / 10)}% vs prior`,
    tone: up === upIsGood ? 'good' : 'bad',
  }
}

/** Rate change in percentage points (a % of a % reads wrong). */
function points(cur: number, prev: number): { text: string; tone: Tone } | null {
  if (!Number.isFinite(cur) || !Number.isFinite(prev)) return null
  const d = (cur - prev) * 100
  if (Math.abs(d) < 0.05) return { text: 'no change vs prior', tone: 'flat' }
  return { text: `${d > 0 ? '▲' : '▼'} ${Math.abs(Math.round(d * 10) / 10)} pts vs prior`, tone: d > 0 ? 'good' : 'bad' }
}

const TONE: Record<Tone, string> = {
  good: 'text-[color:var(--green-ink)]',
  bad: 'text-[color:var(--red-ink)]',
  flat: 'text-text-muted',
}

/** The headline strip: four figures in one ruled card (Paper & Ink). Numbers
 *  sit in the display face, labels in the sans, deltas against the window of
 *  equal length just before this one. */
export function KpiStrip({ data }: { data: AnalyticsSummary }) {
  const f = data.funnel
  const prior = data.prior
  const fr = data.first_response
  const noSends = f.sent === 0

  const sentChange = prior ? change(f.sent, prior.sent) : null
  const replyChange = prior && !noSends && prior.sent > 0 ? points(f.reply_rate, prior.reply_rate) : null
  const frPrev = prior?.first_response_median_seconds
  const frChange =
    fr?.median_seconds != null && frPrev != null && frPrev > 0 ? change(fr.median_seconds, frPrev, false) : null

  const kpis: Kpi[] = [
    {
      label: 'Messages sent',
      value: f.sent.toLocaleString('en-US'),
      sub: sentChange?.text ?? `last ${data.days} days`,
      tone: sentChange?.tone ?? 'flat',
    },
    {
      label: 'Read rate',
      value: f.delivered > 0 ? pct(f.read / f.delivered) : '—',
      sub: f.delivered > 0 ? 'of delivered' : 'nothing delivered yet',
      tone: 'flat',
    },
    {
      label: 'Replied',
      value: noSends ? '—' : pct(f.reply_rate),
      sub: replyChange?.text ?? (noSends ? 'no sends yet' : `${f.replied.toLocaleString('en-US')} conversations`),
      tone: replyChange?.tone ?? 'flat',
    },
    {
      label: 'First response',
      value: fr?.median_seconds != null ? duration(fr.median_seconds) : '—',
      sub: frChange?.text ?? (fr && fr.samples > 0 ? `median of ${fr.samples.toLocaleString('en-US')}` : 'no replies yet'),
      tone: frChange?.tone ?? 'flat',
    },
  ]

  // 2×2 on phones, one row from md up; hairline dividers between cells.
  const cellBorder = ['border-b border-r md:border-b-0', 'border-b md:border-b-0 md:border-r', 'border-r', '']

  return (
    <div className="grid grid-cols-2 rounded-xl border border-line bg-card md:grid-cols-4">
      {kpis.map((k, i) => (
        <div key={k.label} className={`min-w-0 border-line px-4 py-4 sm:px-5 sm:py-[18px] ${cellBorder[i]}`}>
          <div className="text-[12px] font-semibold text-text-muted">{k.label}</div>
          <div className="display mt-1.5 truncate text-[28px] leading-none text-text-heading sm:text-[36px]">{k.value}</div>
          <div className={`mt-1.5 truncate text-[12px] ${TONE[k.tone]}`}>{k.sub}</div>
        </div>
      ))}
    </div>
  )
}
