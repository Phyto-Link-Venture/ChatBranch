'use client'

import { useMemo } from 'react'
import { useI18n } from './I18nProvider'
import { intlTag } from './locales'

type DateInput = Date | string | number | null | undefined

function toDate(d: DateInput): Date | null {
  if (d == null || d === '') return null
  const v = d instanceof Date ? d : new Date(d)
  return Number.isNaN(v.getTime()) ? null : v
}

const UNITS: [Intl.RelativeTimeFormatUnit, number][] = [
  ['year', 365 * 24 * 3600],
  ['month', 30 * 24 * 3600],
  ['week', 7 * 24 * 3600],
  ['day', 24 * 3600],
  ['hour', 3600],
  ['minute', 60],
  ['second', 1],
]

/**
 * Locale-aware formatting for the current language — use these instead of
 * `toLocaleString()` / hand-built strings so dates, numbers and "5 min ago"
 * follow the chosen language. Every function returns "" for an empty value.
 */
export function useFormat() {
  const { locale } = useI18n()
  return useMemo(() => {
    const tag = intlTag(locale)
    // `timeZone` (an IANA name, e.g. a calendar's) formats in that zone instead of the browser's
    const df = (o: Intl.DateTimeFormatOptions, timeZone?: string) =>
      new Intl.DateTimeFormat(tag, timeZone ? { ...o, timeZone } : o)
    const fmt = (d: DateInput, o: Intl.DateTimeFormatOptions, timeZone?: string) => {
      const v = toDate(d)
      if (!v) return ''
      try {
        return df(o, timeZone).format(v)
      } catch {
        return df(o).format(v) // an unknown zone name: the browser's own
      }
    }
    return {
      /** 9 Oct 2026 */
      date: (d: DateInput, timeZone?: string) => fmt(d, { day: 'numeric', month: 'short', year: 'numeric' }, timeZone),
      /** 9 Oct — chart axes, lists within one year */
      dayMonth: (d: DateInput, timeZone?: string) => fmt(d, { day: 'numeric', month: 'short' }, timeZone),
      /** 14:05 */
      time: (d: DateInput, timeZone?: string) => fmt(d, { hour: '2-digit', minute: '2-digit' }, timeZone),
      /** 14:05:09 — transcripts, logs */
      timeSeconds: (d: DateInput, timeZone?: string) =>
        fmt(d, { hour: '2-digit', minute: '2-digit', second: '2-digit' }, timeZone),
      /** 9 Oct 2026, 14:05 */
      dateTime: (d: DateInput, timeZone?: string) =>
        fmt(d, { day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' }, timeZone),
      /** Thu */
      weekday: (d: DateInput, timeZone?: string) => fmt(d, { weekday: 'short' }, timeZone),
      /** "5 minutes ago" / "in 2 days" (now = the reference time) */
      relative: (d: DateInput, now: number = Date.now()) => {
        const v = toDate(d)
        if (!v) return ''
        const secs = Math.round((v.getTime() - now) / 1000)
        const rtf = new Intl.RelativeTimeFormat(tag, { numeric: 'auto' })
        for (const [unit, size] of UNITS)
          if (Math.abs(secs) >= size || unit === 'second') return rtf.format(Math.round(secs / size), unit)
        return ''
      },
      /** 1,234.5 */
      number: (n: number | null | undefined, o?: Intl.NumberFormatOptions) =>
        n == null || Number.isNaN(n) ? '' : new Intl.NumberFormat(tag, o).format(n),
      /** RM 12.50 */
      /** `maxDigits` widens the fraction (e.g. 4 for sub-cent amounts); the minimum stays the currency's own. */
      currency: (n: number | null | undefined, currency = 'MYR', maxDigits?: number) =>
        n == null || Number.isNaN(n)
          ? ''
          : new Intl.NumberFormat(tag, maxDigits ? { style: 'currency', currency, maximumFractionDigits: maxDigits } : { style: 'currency', currency }).format(n),
      /** 42% (pass 0.42) */
      percent: (n: number | null | undefined, digits = 0) =>
        n == null || Number.isNaN(n)
          ? ''
          : new Intl.NumberFormat(tag, { style: 'percent', maximumFractionDigits: digits }).format(n),
      /** "A, B and C" */
      list: (items: string[]) => new Intl.ListFormat(tag, { style: 'long', type: 'conjunction' }).format(items),
    }
  }, [locale])
}
