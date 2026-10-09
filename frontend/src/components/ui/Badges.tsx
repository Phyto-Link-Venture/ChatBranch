'use client'

import type { ReactNode } from 'react'
import type { ContactStatus } from '@/lib/mocks'
import { HEALTH_META, type HealthStatus } from '@/lib/channelHealth'
import { useT, type MessageKey } from '@/i18n'

// ── Status badge (contact status) ────────────────────────────────────────────
const STATUS: Record<
  string,
  { lbl: MessageKey; bg: string; c: string; dot: string }
> = {
  active: { lbl: 'ui.contactStatus.active', bg: 'rgba(255,90,31,0.15)', c: 'var(--brand-ink)', dot: 'rgb(var(--c-brand))' },
  pending: { lbl: 'ui.contactStatus.pending', bg: 'rgba(200,137,26,0.13)', c: 'var(--amber-ink)', dot: 'rgb(var(--c-amber))' },
  'message-sent': { lbl: 'ui.contactStatus.messageSent', bg: 'rgba(122,78,140,0.15)', c: 'var(--purple-ink)', dot: 'rgb(var(--c-purple))' },
  resolved: { lbl: 'ui.contactStatus.resolved', bg: 'rgba(47,125,79,0.13)', c: 'var(--green-ink)', dot: 'rgb(var(--c-green))' },
  failed: { lbl: 'ui.contactStatus.failed', bg: 'rgba(192,57,43,0.13)', c: 'var(--red-ink)', dot: 'rgb(var(--c-red))' },
}

export function StatusBadge({ status }: { status: ContactStatus }) {
  const t = useT()
  const s = STATUS[status] || STATUS.pending
  return (
    <span
      className="inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-[11px] font-semibold whitespace-nowrap"
      style={{ color: s.c, background: s.bg }}
    >
      <span
        className="inline-block h-[5px] w-[5px] shrink-0 rounded-full"
        style={{ background: s.dot }}
      />
      {t(s.lbl)}
    </span>
  )
}

// ── Call-log status badge ─────────────────────────────────────────────────────
const LOG_STATUS: Record<string, { lbl: MessageKey; c: string; bg: string }> = {
  completed: { lbl: 'ui.callStatus.completed', c: 'var(--green-ink)', bg: 'rgba(47,125,79,0.13)' },
  active: { lbl: 'ui.callStatus.live', c: 'var(--brand-ink)', bg: 'rgba(255,90,31,0.15)' },
  voicemail: { lbl: 'ui.callStatus.voicemail', c: 'var(--amber-ink)', bg: 'rgba(200,137,26,0.13)' },
  'no-answer': { lbl: 'ui.callStatus.noAnswer', c: 'var(--red-ink)', bg: 'rgba(192,57,43,0.13)' },
  'callback-requested': { lbl: 'ui.callStatus.callbackRequested', c: 'var(--purple-ink)', bg: 'rgba(122,78,140,0.15)' },
  read: { lbl: 'ui.callStatus.read', c: 'var(--green-ink)', bg: 'rgba(47,125,79,0.13)' },
  delivered: { lbl: 'ui.callStatus.delivered', c: 'var(--brand-ink)', bg: 'rgba(255,90,31,0.13)' },
}

export function LogStatusBadge({ status }: { status: string }) {
  const t = useT()
  const st = LOG_STATUS[status] || LOG_STATUS.delivered
  return (
    <span
      className="rounded-full px-2 py-0.5 text-[11px] font-semibold whitespace-nowrap"
      style={{ color: st.c, background: st.bg }}
    >
      {t(st.lbl)}
    </span>
  )
}

// ── Channel-account health badge (dashboard card + Settings) ─────────────────
// Colours come from HEALTH_META; the visible label is translated here.
const HEALTH_LBL: Record<HealthStatus, MessageKey> = {
  healthy: 'ui.health.healthy',
  degraded: 'ui.health.degraded',
  unknown: 'ui.health.unknown',
  disconnected: 'ui.health.disconnected',
  banned: 'ui.health.banned',
}

export function HealthBadge({ health, detail }: { health: HealthStatus | string; detail?: string }) {
  const t = useT()
  const known = (Object.prototype.hasOwnProperty.call(HEALTH_LBL, health) ? health : 'unknown') as HealthStatus
  const m = HEALTH_META[known]
  return (
    <span
      className="inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-[10px] font-bold whitespace-nowrap"
      style={{ color: m.ink, background: m.bg }}
      title={detail || undefined}
    >
      <span
        className="inline-block h-[5px] w-[5px] shrink-0 rounded-full"
        style={{ background: m.dot }}
      />
      {t(HEALTH_LBL[known])}
    </span>
  )
}

// ── WhatsApp account role chip (client operating model) ──────────────────────
// reminder = business number: template cannon, hands contacts to conversation
// numbers. conversation = personal (usually Baileys) number: AI negotiation,
// customer-initiated only.
export type AccountRole = 'reminder' | 'conversation'

/** Effective role for an account row — backends mid-flight may omit `role`,
 *  so default cloud → reminder and baileys → conversation (the contract's
 *  server-side defaults). */
export function accountRole(a: { role?: string | null; provider?: string | null }): AccountRole {
  if (a.role === 'reminder' || a.role === 'conversation') return a.role
  return a.provider === 'baileys' ? 'conversation' : 'reminder'
}

const ROLE_CHIP: Record<AccountRole, { lbl: MessageKey; cls: string; title: MessageKey }> = {
  reminder: {
    lbl: 'ui.accountRole.reminder',
    cls: 'bg-cyan/15 text-cyan-300',
    title: 'ui.accountRole.reminderHint',
  },
  conversation: {
    lbl: 'ui.accountRole.conversation',
    cls: 'bg-brand-400/10 text-brand-300',
    title: 'ui.accountRole.conversationHint',
  },
}

export function RoleChip({ role }: { role: AccountRole }) {
  const t = useT()
  const m = ROLE_CHIP[role]
  return (
    <span
      className={`shrink-0 whitespace-nowrap rounded-full px-1.5 py-px text-[9px] font-bold uppercase ${m.cls}`}
      title={t(m.title)}
    >
      {t(m.lbl)}
    </span>
  )
}

// ── Simple tinted pill (used for tags, day counts, etc.) ─────────────────────
export function Pill({
  children,
  color,
  bg,
}: {
  children: ReactNode
  color: string
  bg: string
}) {
  return (
    <span
      className="rounded-full px-2 py-0.5 text-[11px] font-semibold whitespace-nowrap"
      style={{ color, background: bg }}
    >
      {children}
    </span>
  )
}
