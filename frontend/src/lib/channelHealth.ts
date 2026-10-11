/**
 * Channel-health shared layer — types for GET /api/whatsapp/channels/health,
 * the single status → color/label mapping used everywhere a health badge is
 * drawn (dashboard card, Settings account badges, assignment tiles), and a
 * relative "last OK" formatter. Colors follow the app scheme: healthy =
 * emerald, degraded/unknown = amber, disconnected/banned = red. Text colors
 * use the *-ink vars so they flip with light/dark.
 */
import { authHeaders } from './auth'
import type { MessageKey } from '@/i18n'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

export type HealthStatus = 'unknown' | 'healthy' | 'degraded' | 'disconnected' | 'banned'

export interface HealthAccount {
  id: string
  name: string
  channel: string // whatsapp | facebook | instagram | telegram | email | …
  provider: string // 'cloud' | 'baileys' | 'ycloud' | 'twilio' for whatsapp; other channels use their own (e.g. 'smtp')
  /** Owning outlet slug; null = a global account the organization shares. */
  entity?: string | null
  entity_name?: string
  configured: boolean
  health: HealthStatus
  detail: string
  last_ok_at: string | null
  last_inbound_at: string | null
  fail_streak: number
  changed_at: string | null
  assigned_contacts: number
}

export interface EmailHealth {
  configured: boolean
  health: 'healthy' | 'disconnected' | 'unknown'
  detail: string
}

export interface VoiceHealth {
  configured: boolean
  health: HealthStatus
  detail: string
  /** True when Twilio REST creds are present and the status is live-checked. */
  live_check: boolean
}

export interface ChannelHealthPayload {
  accounts: HealthAccount[]
  email: EmailHealth
  /** Voice trunk (LiveKit SIP → Twilio). Absent on older backends. */
  voice?: VoiceHealth
  checked_at: string
}

/** `key` is the translated label — render `t(HEALTH_META[s].key)`. `lbl` is the
 *  English word, kept for callers not yet localised. */
export const HEALTH_META: Record<HealthStatus, { key: MessageKey; lbl: string; dot: string; ink: string; bg: string }> = {
  healthy: { key: 'channels.health.healthy', lbl: 'Healthy', dot: 'rgb(var(--c-green))', ink: 'var(--green-ink)', bg: 'rgba(47,125,79,0.12)' },
  degraded: { key: 'channels.health.degraded', lbl: 'Degraded', dot: 'rgb(var(--c-amber))', ink: 'var(--amber-ink)', bg: 'rgba(200,137,26,0.12)' },
  unknown: { key: 'channels.health.unknown', lbl: 'Unknown', dot: 'rgb(var(--c-amber))', ink: 'var(--amber-ink)', bg: 'rgba(200,137,26,0.12)' },
  disconnected: { key: 'channels.health.disconnected', lbl: 'Disconnected', dot: 'rgb(var(--c-red))', ink: 'var(--red-ink)', bg: 'rgba(192,57,43,0.13)' },
  banned: { key: 'channels.health.banned', lbl: 'Banned', dot: 'rgb(var(--c-red))', ink: 'var(--red-ink)', bg: 'rgba(192,57,43,0.13)' },
}

/** An account in this state can no longer send — treat as an alarm. */
export const isDown = (health: string) => health === 'disconnected' || health === 'banned'

/** "just now" / "5m ago" / "3h ago" / "2d ago" — for last-OK style stamps.
 *  English only: localised screens use `useFormat().relative(iso)` instead. */
export function relTime(iso: string | null): string {
  if (!iso) return 'never'
  const ms = Date.now() - new Date(iso).getTime()
  if (!Number.isFinite(ms)) return 'never'
  const m = Math.floor(ms / 60000)
  if (m < 1) return 'just now'
  if (m < 60) return `${m}m ago`
  const h = Math.floor(m / 60)
  if (h < 24) return `${h}h ago`
  return `${Math.floor(h / 24)}d ago`
}

/** `entityQ` is the `entity=<slug>` string from useEntityQuery() — "" (the
 *  default) reports every account across the organization. Scoping the card
 *  matters: an outlet must not be shown a sibling branch's outage as its own. */
export async function fetchChannelHealth(entityQ = ''): Promise<ChannelHealthPayload> {
  const res = await fetch(`${API}/whatsapp/channels/health${entityQ && `?${entityQ}`}`,
                          { headers: authHeaders() })
  if (!res.ok) throw new Error(`HTTP ${res.status}`)
  return (await res.json()) as ChannelHealthPayload
}

/** Probe every account NOW (channels.manage) and return the refreshed payload — the
 *  periodic checker otherwise only runs every ~5 minutes. */
export async function checkChannelHealth(entityQ = ''): Promise<ChannelHealthPayload> {
  const res = await fetch(`${API}/whatsapp/channels/health${entityQ && `?${entityQ}`}`, {
    method: 'POST',
    headers: authHeaders(),
  })
  if (!res.ok) throw new Error(`HTTP ${res.status}`)
  return (await res.json()) as ChannelHealthPayload
}
