'use client'

import { useSyncExternalStore } from 'react'
import { API } from './apiBase'
import { authHeaders } from './auth'

/**
 * Which time zone the dashboard SHOWS times in. Every date and time goes through
 * `useFormat()`, which reads `useDisplayTimeZone()` here — so one choice moves
 * the whole app.
 *
 *   - choice ''      → this device's own zone (the default)
 *   - choice 'org'   → the organization's BUSINESS time zone (Settings → Time zone,
 *                      Setting `org_timezone` — the AI's clock, bookings, journeys)
 *   - choice '<IANA>' → that zone, e.g. 'Asia/Kuala_Lumpur'
 *
 * The choice lives on the account (`PUT /api/auth/preferences {time_zone}`), is
 * copied here once per sign-in (`syncTimeZoneFromServer`, called by the Shell)
 * and cached per browser (localStorage `ui-timezone`) so the next visit paints in
 * it at once. Server render and hydration use the device's zone (no mismatch);
 * the stored choice applies right after.
 */
export interface TimeZoneState {
  choice: string
  /** The business time zone (IANA id) as the server last said. */
  org: string
}

const KEY = 'ui-timezone'
export const ORG_CHOICE = 'org'

let state: TimeZoneState = { choice: '', org: '' }
let loaded = false
const listeners = new Set<() => void>()

function load() {
  if (loaded || typeof window === 'undefined') return
  loaded = true
  try {
    const raw = window.localStorage.getItem(KEY)
    if (!raw) return
    const v = JSON.parse(raw) as Partial<TimeZoneState>
    state = { choice: typeof v.choice === 'string' ? v.choice : '', org: typeof v.org === 'string' ? v.org : '' }
  } catch {
    /* blocked or corrupt storage: the device's zone */
  }
}

function emit() {
  listeners.forEach((l) => l())
}

/** Change the cached state (and tell every useFormat()). */
export function setTimeZoneState(next: Partial<TimeZoneState>): void {
  load()
  state = { ...state, ...next }
  try {
    window.localStorage.setItem(KEY, JSON.stringify(state))
  } catch {
    /* storage blocked: this tab only */
  }
  emit()
}

export function getTimeZoneState(): TimeZoneState {
  load()
  return state
}

/** A zone this browser can format in. */
export function validZone(zone: string | null | undefined): zone is string {
  if (!zone) return false
  try {
    new Intl.DateTimeFormat('en', { timeZone: zone })
    return true
  } catch {
    return false
  }
}

/** This device's own zone, e.g. "Asia/Kuala_Lumpur". */
export function deviceTimeZone(): string {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC'
  } catch {
    return 'UTC'
  }
}

/** The zone a choice means; undefined = this device's own. */
export function zoneFor(s: TimeZoneState): string | undefined {
  const z = s.choice === ORG_CHOICE ? s.org : s.choice
  return validZone(z) ? z : undefined
}

/** How far `zone`'s wall clock is ahead of UTC at instant `t` (ms). */
function offsetMs(zone: string, t: number): number {
  const parts = new Intl.DateTimeFormat('en-US', {
    timeZone: zone,
    hourCycle: 'h23',
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  }).formatToParts(t)
  const n = (type: string) => Number(parts.find((p) => p.type === type)?.value ?? 0)
  return Date.UTC(n('year'), n('month') - 1, n('day'), n('hour') % 24, n('minute'), n('second')) - Math.floor(t / 1000) * 1000
}

/**
 * The instant a calendar day ("2026-10-12", plus `addDays`) begins in the zone times
 * are shown in — what a date filter's "from" / "to" means. ISO string, or undefined
 * for a malformed day. With no chosen zone: this device's midnight.
 */
export function dayStartIso(ymd: string, addDays = 0, zone: string | undefined = zoneFor(getTimeZoneState())): string | undefined {
  const [y, m, d] = ymd.split('-').map(Number)
  if (!y || !m || !d) return undefined
  if (!zone) return new Date(y, m - 1, d + addDays).toISOString()
  const wall = Date.UTC(y, m - 1, d + addDays)
  try {
    let t = wall - offsetMs(zone, wall)
    t = wall - offsetMs(zone, t) // across a DST change, measure at the answer itself
    return new Date(t).toISOString()
  } catch {
    return new Date(y, m - 1, d + addDays).toISOString()
  }
}

/** Today's calendar day ("2026-10-12") in the zone times are shown in. */
export function todayKey(now: number = Date.now(), zone: string | undefined = zoneFor(getTimeZoneState())): string {
  const parts = new Intl.DateTimeFormat('en-CA', {
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    ...(zone ? { timeZone: zone } : {}),
  }).formatToParts(now)
  const p = (type: string) => parts.find((x) => x.type === type)?.value ?? ''
  return `${p('year')}-${p('month')}-${p('day')}`
}

function subscribe(cb: () => void) {
  listeners.add(cb)
  const onStorage = (e: StorageEvent) => {
    if (e.key !== KEY) return
    loaded = false // another tab changed it
    load()
    emit()
  }
  window.addEventListener('storage', onStorage)
  return () => {
    listeners.delete(cb)
    window.removeEventListener('storage', onStorage)
  }
}

/** The zone the dashboard shows times in (undefined = the device's own). */
export function useDisplayTimeZone(): string | undefined {
  return useSyncExternalStore(
    subscribe,
    () => zoneFor(getTimeZoneState()),
    () => undefined,
  )
}

/** The cached state itself, for the Settings page. */
export function useTimeZoneState(): TimeZoneState {
  return useSyncExternalStore(subscribe, getTimeZoneState, () => state)
}

/** Once per sign-in: the person's own choice + the business zone, from their account. */
export async function syncTimeZoneFromServer(): Promise<void> {
  try {
    const res = await fetch(`${API}/auth/preferences`, { headers: authHeaders() })
    if (!res.ok) return
    const data = (await res.json()) as { time_zone?: unknown; org_time_zone?: unknown }
    setTimeZoneState({
      choice: typeof data.time_zone === 'string' ? data.time_zone : '',
      org: typeof data.org_time_zone === 'string' ? data.org_time_zone : '',
    })
  } catch {
    /* offline: keep what this browser had */
  }
}

/** Save the person's "show times in" choice ('' = this device). */
export async function saveTimeZoneChoice(choice: string): Promise<void> {
  const res = await fetch(`${API}/auth/preferences`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
    body: JSON.stringify({ time_zone: choice || null }),
  })
  if (!res.ok) throw new Error(await errorText(res))
  setTimeZoneState({ choice })
}

export interface BusinessTimeZone {
  time_zone: string
  /** setting = chosen in Settings · env = the server's ORG_TIMEZONE / TZ · server = nobody chose one */
  time_zone_source: 'setting' | 'env' | 'server'
  can_manage: boolean
}

export async function getBusinessTimeZone(): Promise<BusinessTimeZone> {
  const res = await fetch(`${API}/settings/appearance`, { headers: authHeaders() })
  if (!res.ok) throw new Error(await errorText(res))
  const data = (await res.json()) as BusinessTimeZone
  setTimeZoneState({ org: data.time_zone })
  return data
}

/** Set the organization's business time zone (null = clear it). Needs settings.appearance.org. */
export async function saveBusinessTimeZone(zone: string | null): Promise<BusinessTimeZone> {
  const res = await fetch(`${API}/settings/appearance`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
    body: JSON.stringify({ time_zone: zone }),
  })
  if (!res.ok) throw new Error(await errorText(res))
  const data = (await res.json()) as BusinessTimeZone
  setTimeZoneState({ org: data.time_zone })
  return data
}

async function errorText(res: Response): Promise<string> {
  try {
    const body = (await res.json()) as { detail?: string }
    if (body.detail) return body.detail
  } catch {
    /* not JSON */
  }
  return `HTTP ${res.status}`
}
