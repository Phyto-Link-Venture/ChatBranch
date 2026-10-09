'use client'

import { useCallback } from 'react'
import { useFormat, useT, type TFunction } from '@/i18n'
import type { AnalyticsSummary } from './types'

/** 95 -> "1m 35s", 3610 -> "1h 0m", 42 -> "42s" — in the UI language. */
export function useDuration() {
  const t = useT()
  return useCallback(
    (secs: number) => {
      if (secs >= 3600)
        return t('analytics.units.hoursMinutes', { h: Math.floor(secs / 3600), m: Math.round((secs % 3600) / 60) })
      if (secs >= 60) return t('analytics.units.minutesSeconds', { m: Math.floor(secs / 60), s: Math.round(secs % 60) })
      return t('analytics.units.seconds', { s: Math.round(secs) })
    },
    [t],
  )
}

type Tone = 'good' | 'bad' | 'flat'

interface Kpi {
  label: string
  value: string
  sub: string
  tone: Tone
}

type Fmt = ReturnType<typeof useFormat>

/** "▲ 8.2% vs prior" — direction × whether up is good decides the ink. */
function change(t: TFunction, f: Fmt, cur: number, prev: number, upIsGood = true): { text: string; tone: Tone } | null {
  if (!Number.isFinite(cur) || !Number.isFinite(prev) || prev <= 0) return null
  const d = ((cur - prev) / prev) * 100
  if (Math.abs(d) < 0.05) return { text: t('analytics.kpi.noChange'), tone: 'flat' }
  const up = d > 0
  return {
    text: t('analytics.kpi.changePct', { arrow: up ? '▲' : '▼', value: f.percent(Math.abs(d) / 100, 1) }),
    tone: up === upIsGood ? 'good' : 'bad',
  }
}

/** Rate change in percentage points (a % of a % reads wrong). */
function points(t: TFunction, f: Fmt, cur: number, prev: number): { text: string; tone: Tone } | null {
  if (!Number.isFinite(cur) || !Number.isFinite(prev)) return null
  const d = (cur - prev) * 100
  if (Math.abs(d) < 0.05) return { text: t('analytics.kpi.noChange'), tone: 'flat' }
  return {
    text: t('analytics.kpi.changePts', {
      arrow: d > 0 ? '▲' : '▼',
      value: f.number(Math.abs(d), { maximumFractionDigits: 1 }),
    }),
    tone: d > 0 ? 'good' : 'bad',
  }
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
  const t = useT()
  const f = useFormat()
  const duration = useDuration()
  const fm = data.funnel
  const prior = data.prior
  const fr = data.first_response
  const noSends = fm.sent === 0

  const sentChange = prior ? change(t, f, fm.sent, prior.sent) : null
  const replyChange = prior && !noSends && prior.sent > 0 ? points(t, f, fm.reply_rate, prior.reply_rate) : null
  const frPrev = prior?.first_response_median_seconds
  const frChange =
    fr?.median_seconds != null && frPrev != null && frPrev > 0 ? change(t, f, fr.median_seconds, frPrev, false) : null

  const kpis: Kpi[] = [
    {
      label: t('analytics.kpi.sent'),
      value: f.number(fm.sent),
      sub: sentChange?.text ?? t('analytics.kpi.lastDays', { count: data.days }),
      tone: sentChange?.tone ?? 'flat',
    },
    {
      label: t('analytics.kpi.readRate'),
      value: fm.delivered > 0 ? f.percent(fm.read / fm.delivered, 1) : '—',
      sub: fm.delivered > 0 ? t('analytics.kpi.ofDelivered') : t('analytics.kpi.nothingDelivered'),
      tone: 'flat',
    },
    {
      label: t('analytics.kpi.replied'),
      value: noSends ? '—' : f.percent(fm.reply_rate, 1),
      sub:
        replyChange?.text ??
        (noSends ? t('analytics.kpi.noSends') : t('analytics.kpi.conversations', { count: fm.replied })),
      tone: replyChange?.tone ?? 'flat',
    },
    {
      label: t('analytics.kpi.firstResponse'),
      value: fr?.median_seconds != null ? duration(fr.median_seconds) : '—',
      sub:
        frChange?.text ??
        (fr && fr.samples > 0 ? t('analytics.kpi.medianOf', { count: fr.samples }) : t('analytics.kpi.noReplies')),
      tone: frChange?.tone ?? 'flat',
    },
  ]

  // 2×2 on phones, one row from md up; hairline dividers between cells.
  const cellBorder = ['border-b border-r md:border-b-0', 'border-b md:border-b-0 md:border-r', 'border-r', '']

  return (
    <div className="grid grid-cols-2 rounded-xl border border-line bg-card md:grid-cols-4">
      {kpis.map((k, i) => (
        <div key={i} className={`min-w-0 border-line px-4 py-4 sm:px-5 sm:py-[18px] ${cellBorder[i]}`}>
          <div className="truncate text-[12px] font-semibold text-text-muted">{k.label}</div>
          <div className="display mt-1.5 truncate text-[28px] leading-none text-text-heading sm:text-[36px]">{k.value}</div>
          <div className={`mt-1.5 truncate text-[12px] ${TONE[k.tone]}`} title={k.sub}>
            {k.sub}
          </div>
        </div>
      ))}
    </div>
  )
}
