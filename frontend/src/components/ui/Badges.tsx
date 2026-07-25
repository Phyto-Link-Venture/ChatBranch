import type { ReactNode } from 'react'
import type { ContactStatus } from '@/lib/mocks'
import { HEALTH_META, type HealthStatus } from '@/lib/channelHealth'

// ── Status badge (contact status) ────────────────────────────────────────────
const STATUS: Record<
  string,
  { lbl: string; bg: string; c: string; dot: string }
> = {
  active: { lbl: 'Active', bg: 'rgba(59,130,246,0.15)', c: '#93C5FD', dot: '#3B82F6' },
  pending: { lbl: 'Pending', bg: 'rgba(245,158,11,0.13)', c: '#FCD34D', dot: '#F59E0B' },
  'message-sent': { lbl: 'Msg Sent', bg: 'rgba(139,92,246,0.15)', c: '#C4B5FD', dot: '#8B5CF6' },
  resolved: { lbl: 'Resolved', bg: 'rgba(16,185,129,0.13)', c: '#6EE7B7', dot: '#10B981' },
  failed: { lbl: 'Failed', bg: 'rgba(239,68,68,0.13)', c: '#FCA5A5', dot: '#EF4444' },
}

export function StatusBadge({ status }: { status: ContactStatus }) {
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
      {s.lbl}
    </span>
  )
}

// ── Call-log status badge ─────────────────────────────────────────────────────
const LOG_STATUS: Record<string, { lbl: string; c: string; bg: string }> = {
  completed: { lbl: 'Completed', c: '#6EE7B7', bg: 'rgba(16,185,129,0.13)' },
  active: { lbl: 'Live', c: '#93C5FD', bg: 'rgba(59,130,246,0.15)' },
  voicemail: { lbl: 'Voicemail', c: '#FCD34D', bg: 'rgba(245,158,11,0.13)' },
  'no-answer': { lbl: 'No Answer', c: '#FCA5A5', bg: 'rgba(239,68,68,0.13)' },
  'callback-requested': { lbl: 'Callback Req.', c: '#C4B5FD', bg: 'rgba(139,92,246,0.15)' },
  read: { lbl: 'Read', c: '#6EE7B7', bg: 'rgba(16,185,129,0.13)' },
  delivered: { lbl: 'Delivered', c: '#93C5FD', bg: 'rgba(59,130,246,0.13)' },
}

export function LogStatusBadge({ status }: { status: string }) {
  const st = LOG_STATUS[status] || LOG_STATUS.delivered
  return (
    <span
      className="rounded-full px-2 py-0.5 text-[11px] font-semibold whitespace-nowrap"
      style={{ color: st.c, background: st.bg }}
    >
      {st.lbl}
    </span>
  )
}

// ── Channel-account health badge (dashboard card + Settings) ─────────────────
export function HealthBadge({ health, detail }: { health: HealthStatus | string; detail?: string }) {
  const m = HEALTH_META[health as HealthStatus] || HEALTH_META.unknown
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
      {m.lbl}
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

const ROLE_CHIP: Record<AccountRole, { lbl: string; cls: string; title: string }> = {
  reminder: {
    lbl: 'Reminder',
    cls: 'bg-cyan/15 text-cyan-300',
    title: 'Reminder (business) — sends templates and hands contacts to conversation numbers',
  },
  conversation: {
    lbl: 'Conversation',
    cls: 'bg-brand-400/10 text-brand-300',
    title: 'Conversation (personal) — AI chat, customer-initiated only',
  },
}

export function RoleChip({ role }: { role: AccountRole }) {
  const m = ROLE_CHIP[role]
  return (
    <span
      className={`shrink-0 rounded-full px-1.5 py-px text-[9px] font-bold uppercase ${m.cls}`}
      title={m.title}
    >
      {m.lbl}
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
