'use client'

/**
 * Small presentational pieces shared by the AI monitor tabs: status badge,
 * the who/where header of a draft, the context chips (tools, checks, model…),
 * the "why it was held" list, a conversation snippet and the auto-send
 * countdown. Token-only colours, so every theme and mode renders correctly.
 */

import { useEffect, useState } from 'react'
import {
  AlertTriangle,
  Bot,
  CalendarPlus,
  CalendarX,
  Check,
  Clock,
  Cpu,
  GitBranch,
  RotateCcw,
  UserRound,
  Wrench,
  X,
  type LucideIcon,
} from 'lucide-react'
import { ChannelLogo, channelLabel } from '@/components/ui/ChannelLogo'
import { tint } from '@/lib/color'
import {
  KIND_LABEL,
  STATUS_META,
  ago,
  draftBookingVerb,
  type Draft,
  type DraftStatus,
  type RecentMessage,
} from '@/lib/aiMonitor'

const TONE: Record<string, string> = {
  brand: 'bg-brand-400/10 text-brand-300',
  green: 'bg-green/10 text-green-300',
  amber: 'bg-amber/15 text-amber-300',
  red: 'bg-red/10 text-red-300',
  purple: 'bg-purple/10 text-purple-300',
  muted: 'bg-fill text-text-muted',
}

export function StatusBadge({ status }: { status: DraftStatus }) {
  const m = STATUS_META[status] ?? STATUS_META.pending
  return (
    <span className={`inline-flex shrink-0 items-center rounded-full px-2 py-0.5 text-[11px] font-semibold ${TONE[m.tone]}`}>
      {m.label}
    </span>
  )
}

export function displayName(d: Draft): string {
  return d.contact?.name || d.thread?.name || d.thread_key
}

export function chatHref(d: Draft): string {
  return `/chats?channel=${encodeURIComponent(d.channel)}&key=${encodeURIComponent(d.thread_key)}`
}

/** Contact · channel logo · account · entity · kind · age */
export function DraftHeader({ d, right }: { d: Draft; right?: React.ReactNode }) {
  return (
    <div className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-1">
      <ChannelLogo channel={d.channel} size={16} />
      <span className="truncate text-[13.5px] font-semibold text-text-heading">{displayName(d)}</span>
      <span className="text-[12px] text-text-muted">
        {channelLabel(d.channel)}
        {d.account ? ` · ${d.account.name}` : ''}
        {d.entity ? ` · ${d.entity.name}` : ''}
      </span>
      <span className="rounded-md border border-line px-1.5 py-px text-[10.5px] font-semibold uppercase tracking-wide text-text-muted">
        {KIND_LABEL[d.kind] ?? d.kind}
      </span>
      <span className="text-[11.5px] text-text-faint" title={d.created_at}>
        {ago(d.created_at)} ago
      </span>
      {right && <span className="ml-auto flex items-center gap-2">{right}</span>}
    </div>
  )
}

function Chip({
  children,
  title,
  tone = 'muted',
  Icon,
}: {
  children: React.ReactNode
  title?: string
  tone?: keyof typeof TONE
  Icon?: LucideIcon
}) {
  return (
    <span
      title={title}
      className={`inline-flex max-w-full items-center gap-1 truncate rounded-md px-1.5 py-0.5 text-[11px] font-medium ${TONE[tone]}`}
    >
      {Icon && <Icon size={11} strokeWidth={2} aria-hidden />}
      <span className="truncate">{children}</span>
    </span>
  )
}

function asText(v: unknown): string {
  if (v == null) return ''
  return typeof v === 'string' ? v : JSON.stringify(v)
}

/** Tools called, checks run, model + tokens, retries, flow refs — compact, details on hover. */
export function ContextChips({ d }: { d: Draft }) {
  const c = d.context || {}
  const tools = Array.isArray(c.tools) ? c.tools : []
  const checks = Array.isArray(c.checks) ? c.checks : []
  const tokens = (c.prompt_tokens ?? 0) + (c.completion_tokens ?? 0)
  const chips: React.ReactNode[] = []
  tools.forEach((t, i) =>
    chips.push(
      <Chip
        key={`t${i}`}
        Icon={Wrench}
        tone={t.ok === false ? 'red' : 'muted'}
        title={`Tool ${t.name ?? ''}${t.ok === false ? ' failed' : ''}${t.result ? `\nResult: ${asText(t.result).slice(0, 400)}` : ''}`}
      >
        {t.name ?? 'tool'}
      </Chip>,
    ),
  )
  // the checks run on the final draft (earlier attempts were retried)
  const lastAttempt = checks.reduce((m, k) => Math.max(m, k.attempt ?? 0), 0)
  checks
    .filter((k) => (k.attempt ?? 0) === lastAttempt)
    .forEach((k, i) => {
      const failed = k.ok === false || k.passed === false
      const why = k.reason || k.detail
      chips.push(
        <Chip
          key={`c${i}`}
          Icon={failed ? X : Check}
          tone={failed ? 'red' : 'green'}
          title={`Check ${k.name ?? ''}: ${failed ? 'failed' : 'passed'}${why ? `\n${why}` : ''}`}
        >
          {k.name ?? 'check'}
        </Chip>,
      )
    })
  if (c.action === 'block' || c.action === 'handover')
    chips.push(
      <Chip
        key="act"
        Icon={AlertTriangle}
        tone="red"
        title={`The assistant ${c.action === 'block' ? 'blocked this reply' : 'asked for a person'}${c.reason ? `: ${String(c.reason)}` : ''}`}
      >
        {c.action === 'block' ? 'blocked by a check' : 'asked for a person'}
      </Chip>,
    )
  if (typeof c.retries === 'number' && c.retries > 0)
    chips.push(
      <Chip key="r" Icon={RotateCcw} tone="amber" title="The AI rewrote its answer after a check">
        {c.retries} retr{c.retries === 1 ? 'y' : 'ies'}
      </Chip>,
    )
  if (c.handover && c.action !== 'handover')
    chips.push(
      <Chip key="h" Icon={UserRound} tone="amber" title="The AI asked for a person to take over">
        handover
      </Chip>,
    )
  if (c.model)
    chips.push(
      <Chip
        key="m"
        Icon={Cpu}
        title={`Model ${c.model}${c.provider ? ` (${c.provider})` : ''}${tokens ? `\n${c.prompt_tokens ?? 0} prompt + ${c.completion_tokens ?? 0} completion tokens` : ''}`}
      >
        {c.model}
        {tokens ? ` · ${tokens} tok` : ''}
      </Chip>,
    )
  if (c.flow_run_id)
    chips.push(
      <Chip key="f" Icon={GitBranch} title={`Flow run #${c.flow_run_id}${c.flow_node ? `, step ${c.flow_node}` : ''}`}>
        flow #{String(c.flow_run_id)}
      </Chip>,
    )
  if (c.template)
    chips.push(
      <Chip key="tpl" title="Sent as an approved WhatsApp template (the 24-hour window had closed)">
        template {String(c.template)}
      </Chip>,
    )
  if (c.acknowledgement)
    chips.push(
      <Chip key="ack" Icon={Bot} title="The customer only acknowledged — this is the short acknowledgement text">
        acknowledgement
      </Chip>,
    )
  if (chips.length === 0) return null
  return <div className="flex flex-wrap gap-1">{chips}</div>
}

const BOOKING_INK: Record<string, string> = {
  pending: 'text-[color:var(--blue-ink)]',
  done: 'text-[color:var(--green-ink)]',
  off: 'text-text-muted',
  failed: 'text-[color:var(--red-ink)]',
}

/**
 * "Will book: Tue 14 Oct, 3:00 PM · Google Calendar" — the calendar actions a
 * reply carries. A held reply has only RESERVED them: approving makes the
 * booking (or the cancellation), rejecting releases it — so the reviewer must
 * see them next to the text. After a decision the same lines read "Booked",
 * "Not booked", "Booking failed"…
 */
export function DraftBookings({
  d,
  compact = false,
  inline = false,
}: {
  d: Pick<Draft, 'bookings' | 'status'>
  /** No boxes — for the chat's review banner. */
  compact?: boolean
  /** Phrasing content only (inside a <button> row): one muted line. */
  inline?: boolean
}) {
  const list = Array.isArray(d.bookings) ? d.bookings : []
  if (!list.length) return null
  const held = d.status === 'pending'
  if (inline)
    return (
      <span className="mt-1 flex flex-wrap gap-x-3 gap-y-0.5 text-[11.5px]">
        {list.map((b) => {
          const v = draftBookingVerb(b)
          const Icon = b.type === 'cancel' ? CalendarX : CalendarPlus
          return (
            <span key={`${b.type}:${b.booking_id}`} className="inline-flex items-center gap-1 text-text-muted">
              <Icon size={11} className={BOOKING_INK[v.tone]} aria-hidden />
              <span className={`font-semibold ${BOOKING_INK[v.tone]}`}>{v.verb}:</span> {b.label || `#${b.booking_id}`}
            </span>
          )
        })}
      </span>
    )
  return (
    <ul aria-label="Calendar actions in this reply" className="flex flex-col gap-1">
      {list.map((b) => {
        const v = draftBookingVerb(b)
        const Icon = b.type === 'cancel' ? CalendarX : CalendarPlus
        return (
          <li
            key={`${b.type}:${b.booking_id}`}
            className={`flex items-start gap-1.5 text-[12.5px] leading-snug ${
              compact ? '' : 'rounded-lg border border-line bg-fill-subtle px-2.5 py-1.5'
            }`}
          >
            <Icon size={13} className={`mt-px shrink-0 ${BOOKING_INK[v.tone]}`} aria-hidden />
            <span className="min-w-0 text-text-high">
              <b className={`font-semibold ${BOOKING_INK[v.tone]}`}>{v.verb}:</b> {b.label || `booking #${b.booking_id}`}
              {b.calendar_label && <span className="text-text-muted"> · {b.calendar_label}</span>}
              {held && v.tone === 'pending' && (
                <span className="text-text-muted">
                  {' '}
                  — {b.type === 'cancel' ? 'approving cancels it' : 'approving books it'}
                </span>
              )}
            </span>
          </li>
        )
      })}
    </ul>
  )
}

/** "Why it was held" — the gate's reasons, plain language. */
export function HoldReasons({ reasons }: { reasons: string[] }) {
  if (!reasons.length) return null
  return (
    <ul className="flex flex-wrap gap-1" aria-label="Why this was held">
      {reasons.map((r) => (
        <li key={r}>
          <Chip Icon={AlertTriangle} tone="amber">
            {r}
          </Chip>
        </li>
      ))}
    </ul>
  )
}

/** The recent conversation, oldest first — customer left, the business right. */
export function ConversationSnippet({ messages, highlight }: { messages: RecentMessage[]; highlight?: Set<number> }) {
  if (!messages.length) return null
  return (
    <ol className="flex flex-col gap-1.5">
      {messages.map((m) => {
        const mine = m.direction === 'out'
        return (
          <li key={m.id} className={`flex ${mine ? 'justify-end' : 'justify-start'}`}>
            <div
              className={`max-w-[88%] whitespace-pre-wrap break-words rounded-xl px-2.5 py-1.5 text-[12.5px] leading-snug ${
                mine ? 'bg-fill text-text-mid' : 'border border-line bg-card text-text-high'
              } ${highlight?.has(m.id) ? 'ring-1 ring-brand-400/50' : ''}`}
            >
              {mine && (
                <span className="mb-0.5 block text-[10px] font-semibold uppercase tracking-wide text-text-faint">
                  {m.source === 'ai' ? 'AI' : m.source === 'agent' ? 'Team' : m.source}
                </span>
              )}
              {m.text}
            </div>
          </li>
        )
      })}
    </ol>
  )
}

/** Live countdown to the auto-send deadline. Counts down from the server's own
 *  seconds_left at fetch time, so a skewed client clock can't misreport it. */
export function Countdown({ secondsLeft, fetchedAt }: { secondsLeft: number | null; fetchedAt: number }) {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    const t = window.setInterval(() => setNow(Date.now()), 1000)
    return () => window.clearInterval(t)
  }, [])
  if (secondsLeft == null) return null
  const left = Math.max(0, Math.round(secondsLeft - (now - fetchedAt) / 1000))
  const m = Math.floor(left / 60)
  const s = left % 60
  return (
    <span
      className="inline-flex items-center gap-1 rounded-md bg-amber/15 px-1.5 py-0.5 text-[11px] font-semibold text-amber-300"
      title="Nobody has acted yet — it will be sent automatically at the deadline"
    >
      <Clock size={11} strokeWidth={2.2} aria-hidden />
      {left === 0 ? 'Sending…' : `Sends itself in ${m}:${String(s).padStart(2, '0')}`}
    </span>
  )
}

/** A keyboard-shortcut hint. `inverse` sits on a primary (ink) button. */
export function Kbd({ children, inverse, className = '' }: { children: React.ReactNode; inverse?: boolean; className?: string }) {
  return (
    <kbd
      className={`rounded border px-1 font-mono text-[10px] font-semibold leading-[15px] ${
        inverse ? 'text-[color:var(--on-primary)]' : 'border-line-strong bg-card text-text-muted'
      } ${className}`}
      style={inverse ? { borderColor: tint('var(--on-primary)', 0.45) } : undefined}
    >
      {children}
    </kbd>
  )
}
