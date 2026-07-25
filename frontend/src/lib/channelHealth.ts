/**
 * Channel-health shared layer — types for GET /api/whatsapp/channels/health,
 * the single status → color/label mapping used everywhere a health badge is
 * drawn (dashboard card, Settings account badges, assignment tiles), and a
 * relative "last OK" formatter. Colors follow the app scheme: healthy =
 * emerald, degraded/unknown = amber, disconnected/banned = red. Text colors
 * use the *-ink vars so they flip with light/dark.
 */
import { authHeaders } from './auth'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8000/api'

export type HealthStatus = 'unknown' | 'healthy' | 'degraded' | 'disconnected' | 'banned'

export interface HealthAccount {
  id: string
  name: string
  channel: string // whatsapp | facebook | instagram | telegram | email | …
  provider: string // 'cloud' | 'baileys' for whatsapp; other channels use their own (e.g. 'smtp')
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

export const HEALTH_META: Record<HealthStatus, { lbl: string; dot: string; ink: string; bg: string }> = {
  healthy: { lbl: 'Healthy', dot: '#10B981', ink: 'var(--green-ink)', bg: 'rgba(16,185,129,0.12)' },
  degraded: { lbl: 'Degraded', dot: '#F59E0B', ink: 'var(--amber-ink)', bg: 'rgba(245,158,11,0.12)' },
  unknown: { lbl: 'Unknown', dot: '#F59E0B', ink: 'var(--amber-ink)', bg: 'rgba(245,158,11,0.12)' },
  disconnected: { lbl: 'Disconnected', dot: '#EF4444', ink: 'var(--red-ink)', bg: 'rgba(239,68,68,0.13)' },
  banned: { lbl: 'Banned', dot: '#EF4444', ink: 'var(--red-ink)', bg: 'rgba(239,68,68,0.13)' },
}

/** An account in this state can no longer send — treat as an alarm. */
export const isDown = (health: string) => health === 'disconnected' || health === 'banned'

/** "just now" / "5m ago" / "3h ago" / "2d ago" — for last-OK style stamps. */
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

export async function fetchChannelHealth(): Promise<ChannelHealthPayload> {
  const res = await fetch(`${API}/whatsapp/channels/health`, { headers: authHeaders() })
  if (!res.ok) throw new Error(`HTTP ${res.status}`)
  return (await res.json()) as ChannelHealthPayload
}

/** Probe every account NOW (admin+) and return the refreshed payload — the
 *  periodic checker otherwise only runs every ~5 minutes. */
export async function checkChannelHealth(): Promise<ChannelHealthPayload> {
  const res = await fetch(`${API}/whatsapp/channels/health`, {
    method: 'POST',
    headers: authHeaders(),
  })
  if (!res.ok) throw new Error(`HTTP ${res.status}`)
  return (await res.json()) as ChannelHealthPayload
}
