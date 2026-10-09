/**
 * Contacts: identities, invite links, unknown customers and contact-book
 * import — the API shapes and small helpers shared by ChannelHub,
 * InviteModal, the chat's IdentityBanner / CreateFromChat, the inbox drawer's
 * "Unknown customers" field and ContactImportModal.
 */
import { authHeaders } from './auth'

export const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

/** JSON fetch against the API; throws the server's `detail` on failure. */
export async function cApi<T>(path: string, init?: RequestInit): Promise<T> {
  const isForm = typeof FormData !== 'undefined' && init?.body instanceof FormData
  const res = await fetch(`${API}/${path}`, {
    ...init,
    headers: { ...(isForm ? {} : { 'Content-Type': 'application/json' }), ...authHeaders(), ...(init?.headers ?? {}) },
  })
  const data = await res.json().catch(() => ({}))
  if (!res.ok) {
    const d = data as { detail?: string; error?: string }
    throw Object.assign(new Error(d.detail || d.error || `HTTP ${res.status}`), { status: res.status, data })
  }
  return data as T
}

export function errText(e: unknown): string {
  return e instanceof Error ? e.message : String(e)
}

// ── identities ───────────────────────────────────────────────────────────────

export type Verdict = 'pending' | 'verified' | 'not_on_whatsapp' | 'unknown'

/** Apps a phone number cannot be looked up on (the hub explains: send an invite link). */
export const NO_LOOKUP_LABELS: Record<string, string> = {
  facebook: 'Messenger',
  instagram: 'Instagram',
  telegram: 'Telegram',
  line: 'LINE',
  viber: 'Viber',
  wechat: 'WeChat',
  tiktok: 'TikTok',
  douyin: 'Douyin',
  zalo: 'Zalo',
}

export interface DiscoverResult {
  contact_id: number
  added: number
  removed: number
  queued: number
  lookup_available: boolean
  lookup_note: string | null
}

// ── invites ──────────────────────────────────────────────────────────────────

export interface InviteLink {
  channel: string
  label: string
  url: string | null
  text: string
  code: string
  account_id: number | null
  account_name: string | null
  note: string | null
  qr_svg: string | null
}

export interface InviteResult {
  code: string
  channel: string
  expires_at: string
  links: InviteLink[]
}

export const INVITE_CHANNELS: { value: string; label: string }[] = [
  { value: 'whatsapp', label: 'WhatsApp' },
  { value: 'telegram', label: 'Telegram' },
  { value: 'facebook', label: 'Messenger' },
  { value: 'instagram', label: 'Instagram' },
  { value: 'email', label: 'Email' },
]

/** A QR SVG as an <img> source — never injected as markup. */
export function svgDataUri(svg: string): string {
  return `data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg)}`
}

// ── unknown customers ────────────────────────────────────────────────────────

export type IdentifyMode = 'off' | 'ask' | 'ask_and_create'

export interface IdentityRequestState {
  id: number
  state: 'armed' | 'asking' | 'linked' | 'created' | 'needs_contact' | 'ambiguous' | 'gave_up' | 'cancelled'
  mode: string
  attempts: number
  max_asks: number
  language: string
  collected: { name: string; phone: string; email: string }
  contact_id: number | null
  note: string
  open: boolean
  last_asked_at: string | null
}

export interface IdentifyState {
  channel: string
  key: string
  linked: boolean
  contact_id: number | null
  mode: IdentifyMode
  request: IdentityRequestState | null
  suggested: { name: string; phone: string; email: string }
  can_create: boolean
}

export interface InboxIdentify {
  account_id: number
  identify_unknown: '' | IdentifyMode
  identify_message: string
  effective: IdentifyMode
  default_messages: { en: string; ms: string }
}

export function threadPath(channel: string, key: string): string {
  return `${encodeURIComponent(channel)}/${encodeURIComponent(key)}`
}

// ── import ───────────────────────────────────────────────────────────────────

export interface ImportColumn {
  header: string
  samples: string[]
  target: string
}

export interface ImportTarget {
  value: string
  label: string
  group: string
  multi: boolean
}

export interface ImportRowIssue {
  row: number
  reason: string
  name: string
  phone: string
  email: string
}

export interface ImportResult {
  preview: boolean
  source: string
  total: number
  created: number
  updated: number
  unchanged: number
  skipped: number
  merged: number
  duplicates: number
  queued_checks: number
  mode: string
  headers: string[]
  columns: ImportColumn[]
  column_mapping: Record<string, string>
  targets: ImportTarget[]
  errors: ImportRowIssue[]
  errors_total: number
  warnings: ImportRowIssue[]
  warnings_total: number
  max_rows: number
  /** Google Contacts preview only. */
  rows?: Record<string, string>[]
  connected?: boolean
  warning?: string | null
}

/** Download the row issues as a CSV file (quoted fields). */
export function downloadIssuesCsv(issues: ImportRowIssue[], fileName: string): void {
  const q = (v: unknown) => `"${String(v ?? '').replace(/"/g, '""')}"`
  const lines = [
    ['row', 'reason', 'name', 'phone', 'email'].join(','),
    ...issues.map((i) => [i.row, i.reason, i.name, i.phone, i.email].map(q).join(',')),
  ]
  const blob = new Blob([`﻿${lines.join('\r\n')}`], { type: 'text/csv;charset=utf-8' })
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = fileName
  document.body.appendChild(a)
  a.click()
  a.remove()
  setTimeout(() => URL.revokeObjectURL(url), 1000)
}
