import type { ReactNode } from 'react'
import type { ContactStatus } from '@/lib/mocks'
import { HEALTH_META, type HealthStatus } from '@/lib/channelHealth'

// ── Status badge (contact status) ────────────────────────────────────────────
const STATUS: Record<
  string,
  { lbl: string; bg: string; c: string; dot: string }
> = {
  active: { lbl: 'Active', bg: 'rgba(255,90,31,0.15)', c: 'var(--brand-ink)', dot: 'rgb(var(--c-brand))' },
  pending: { lbl: 'Pending', bg: 'rgba(200,137,26,0.13)', c: 'var(--amber-ink)', dot: 'rgb(var(--c-amber))' },
  'message-sent': { lbl: 'Msg Sent', bg: 'rgba(122,78,140,0.15)', c: 'var(--purple-ink)', dot: 'rgb(var(--c-purple))' },
  resolved: { lbl: 'Resolved', bg: 'rgba(47,125,79,0.13)', c: 'var(--green-ink)', dot: 'rgb(var(--c-green))' },
  failed: { lbl: 'Failed', bg: 'rgba(192,57,43,0.13)', c: 'var(--red-ink)', dot: 'rgb(var(--c-red))' },
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
  completed: { lbl: 'Completed', c: 'var(--green-ink)', bg: 'rgba(47,125,79,0.13)' },
  active: { lbl: 'Live', c: 'var(--brand-ink)', bg: 'rgba(255,90,31,0.15)' },
  voicemail: { lbl: 'Voicemail', c: 'var(--amber-ink)', bg: 'rgba(200,137,26,0.13)' },
  'no-answer': { lbl: 'No Answer', c: 'var(--red-ink)', bg: 'rgba(192,57,43,0.13)' },
  'callback-requested': { lbl: 'Callback Req.', c: 'var(--purple-ink)', bg: 'rgba(122,78,140,0.15)' },
  read: { lbl: 'Read', c: 'var(--green-ink)', bg: 'rgba(47,125,79,0.13)' },
  delivered: { lbl: 'Delivered', c: 'var(--brand-ink)', bg: 'rgba(255,90,31,0.13)' },
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
