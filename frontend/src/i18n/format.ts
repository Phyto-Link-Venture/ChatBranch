'use client'

import { useMemo } from 'react'
import { useI18n } from './I18nProvider'
import { intlTag } from './locales'
import { useDisplayTimeZone } from '@/lib/timeZone'

type DateInput = Date | string | number | null | undefined

function toDate(d: DateInput): Date | null {
  if (d == null || d === '') return null
  const v = d instanceof Date ? d : new Date(d)
  return Number.isNaN(v.getTime()) ? null : v
}

/**
 * "2026-10-12" or "2026-10-12T15:00" — no "Z", no offset: a WALL-CLOCK value (a
 * calendar day, a booking's local start), shown exactly as written in any zone.
 * Every timestamp the API sends carries its "Z", so only these are offset-less.
 */
const WALL = /^(\d{4}-\d{2}-\d{2})(?:[T ](\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?))?$/

/** The instant to format and the zone to read it in (a wall-clock value reads as UTC = as written). */
function resolve(d: DateInput, zone: string | undefined): { v: Date; zone: string | undefined } | null {
  if (typeof d === 'string') {
    const m = WALL.exec(d.trim())
    if (m) {
      const v = new Date(`${m[1]}T${m[2] ?? '00:00'}Z`)
      return Number.isNaN(v.getTime()) ? null : { v, zone: 'UTC' }
    }
  }
  const v = toDate(d)
  return v ? { v, zone } : null
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
 * Times show in the person's chosen zone (Settings → Time zone, `lib/timeZone.ts`:
 * this device's own by default, or the business time zone, or one they picked).
 */
export function useFormat() {
  const { locale } = useI18n()
  const displayZone = useDisplayTimeZone()
  return useMemo(() => {
    const tag = intlTag(locale)
    // `timeZone` (an IANA name, e.g. a calendar's) formats in that zone instead of the display zone
    const df = (o: Intl.DateTimeFormatOptions, timeZone?: string) =>
      new Intl.DateTimeFormat(tag, timeZone ? { ...o, timeZone } : o)
    const fmt = (d: DateInput, o: Intl.DateTimeFormatOptions, timeZone?: string) => {
      const r = resolve(d, timeZone ?? displayZone)
      if (!r) return ''
      try {
        return df(o, r.zone).format(r.v)
      } catch {
        return df(o).format(r.v) // an unknown zone name: the browser's own
      }
    }
    /** "2026-10-12" — the calendar day of a time in the zone times are shown in. */
    const dayKey = (d: DateInput): string => {
      const r = resolve(d, displayZone)
      if (!r) return ''
      let parts: Intl.DateTimeFormatPart[]
      try {
        parts = new Intl.DateTimeFormat('en-CA', { year: 'numeric', month: '2-digit', day: '2-digit', timeZone: r.zone }).formatToParts(r.v)
      } catch {
        parts = new Intl.DateTimeFormat('en-CA', { year: 'numeric', month: '2-digit', day: '2-digit' }).formatToParts(r.v)
      }
      const p = (t: string) => parts.find((x) => x.type === t)?.value ?? ''
      return `${p('year')}-${p('month')}-${p('day')}`
    }
    /** Whole calendar days from `d` to `now` in the zone times are shown in (0 = today, 1 = yesterday). */
    const daysAgo = (d: DateInput, now: DateInput = Date.now()): number | null => {
      const a = dayKey(d)
      const b = dayKey(now)
      if (!a || !b) return null
      return Math.round((Date.parse(`${b}T00:00:00Z`) - Date.parse(`${a}T00:00:00Z`)) / 86_400_000)
    }
    return {
      /** 9 Oct 2026 */
      date: (d: DateInput, timeZone?: string) => fmt(d, { day: 'numeric', month: 'short', year: 'numeric' }, timeZone),
      /** Oct 2026 — billing months */
      monthYear: (d: DateInput, timeZone?: string) => fmt(d, { month: 'short', year: 'numeric' }, timeZone),
      /** October 2026 — a calendar's month title */
      monthYearLong: (d: DateInput, timeZone?: string) => fmt(d, { month: 'long', year: 'numeric' }, timeZone),
      /** 9 Oct — chart axes, lists within one year */
      dayMonth: (d: DateInput, timeZone?: string) => fmt(d, { day: 'numeric', month: 'short' }, timeZone),
      /** 9 am / 14 — a time grid's hour labels */
      hour: (d: DateInput, timeZone?: string) => fmt(d, { hour: 'numeric' }, timeZone),
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
      /** Thursday */
      weekdayLong: (d: DateInput, timeZone?: string) => fmt(d, { weekday: 'long' }, timeZone),
      dayKey,
      daysAgo,
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
      /** The zone times are shown in (undefined = this device's own). */
      timeZone: displayZone,
    }
  }, [locale, displayZone])
}
