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
import { useFormat, useT } from '@/i18n'
import {
  KIND_LABEL,
  STATUS_META,
  draftBookingVerb,
  parseUtc,
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
  const t = useT()
  const m = STATUS_META[status] ?? STATUS_META.pending
  return (
    <span className={`inline-flex shrink-0 items-center rounded-full px-2 py-0.5 text-[11px] font-semibold ${TONE[m.tone]}`}>
      {t(m.label)}
    </span>
  )
}

export function displayName(d: Draft): string {
  return d.contact?.name || d.thread?.name || d.thread_key
}

export function chatHref(d: Draft): string {
  // + thread_id: a customer has one conversation per company under that channel + key
  const id = d.thread?.id
  return `/chats?channel=${encodeURIComponent(d.channel)}&key=${encodeURIComponent(d.thread_key)}${id ? `&thread_id=${id}` : ''}`
}

/** Contact · channel logo · account · entity · kind · age */
export function DraftHeader({ d, right }: { d: Draft; right?: React.ReactNode }) {
  const t = useT()
  const f = useFormat()
  const created = parseUtc(d.created_at)
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
        {KIND_LABEL[d.kind] ? t(KIND_LABEL[d.kind]) : d.kind}
      </span>
      <span className="text-[11.5px] text-text-faint" title={f.dateTime(created)}>
        {f.relative(created)}
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
  const t = useT()
  const c = d.context || {}
  const tools = Array.isArray(c.tools) ? c.tools : []
  const checks = Array.isArray(c.checks) ? c.checks : []
  const tokens = (c.prompt_tokens ?? 0) + (c.completion_tokens ?? 0)
  const chips: React.ReactNode[] = []
  tools.forEach((tool, i) =>
    chips.push(
      <Chip
        key={`t${i}`}
        Icon={Wrench}
        tone={tool.ok === false ? 'red' : 'muted'}
        title={[
          t(tool.ok === false ? 'ai.draft.toolFailedTitle' : 'ai.draft.toolTitle', { name: tool.name ?? '' }),
          tool.result ? t('ai.draft.resultLine', { result: asText(tool.result).slice(0, 400) }) : '',
        ]
          .filter(Boolean)
          .join('\n')}
      >
        {tool.name ?? t('ai.draft.tool')}
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
          title={[t(failed ? 'ai.draft.checkFailedTitle' : 'ai.draft.checkPassedTitle', { name: k.name ?? '' }), why ?? '']
            .filter(Boolean)
            .join('\n')}
        >
          {k.name ?? t('ai.draft.check')}
        </Chip>,
      )
    })
  if (c.action === 'block' || c.action === 'handover')
    chips.push(
      <Chip
        key="act"
        Icon={AlertTriangle}
        tone="red"
        title={
          c.action === 'block'
            ? c.reason
              ? t('ai.draft.blockedReasonTitle', { reason: String(c.reason) })
              : t('ai.draft.blockedTitle')
            : c.reason
              ? t('ai.draft.handoverAskReasonTitle', { reason: String(c.reason) })
              : t('ai.draft.handoverAskTitle')
        }
      >
        {c.action === 'block' ? t('ai.draft.blockedChip') : t('ai.draft.askedChip')}
      </Chip>,
    )
  if (typeof c.retries === 'number' && c.retries > 0)
    chips.push(
      <Chip key="r" Icon={RotateCcw} tone="amber" title={t('ai.draft.retriesTitle')}>
        {t('ai.draft.retries', { count: c.retries })}
      </Chip>,
    )
  if (c.handover && c.action !== 'handover')
    chips.push(
      <Chip key="h" Icon={UserRound} tone="amber" title={t('ai.draft.handoverTitle')}>
        {t('ai.draft.handoverChip')}
      </Chip>,
    )
  if (c.model)
    chips.push(
      <Chip
        key="m"
        Icon={Cpu}
        title={[
          c.provider
            ? t('ai.draft.modelProviderTitle', { model: String(c.model), provider: String(c.provider) })
            : t('ai.draft.modelTitle', { model: String(c.model) }),
          tokens ? t('ai.draft.tokensLine', { prompt: c.prompt_tokens ?? 0, completion: c.completion_tokens ?? 0 }) : '',
        ]
          .filter(Boolean)
          .join('\n')}
      >
        {c.model}
        {tokens ? ` · ${t('ai.draft.tokensShort', { count: tokens })}` : ''}
      </Chip>,
    )
  if (c.flow_run_id)
    chips.push(
      <Chip
        key="f"
        Icon={GitBranch}
        title={
          c.flow_node
            ? t('ai.draft.flowStepTitle', { id: String(c.flow_run_id), step: String(c.flow_node) })
            : t('ai.draft.flowTitle', { id: String(c.flow_run_id) })
        }
      >
        {t('ai.draft.flowChip', { id: String(c.flow_run_id) })}
      </Chip>,
    )
  if (c.template)
    chips.push(
      <Chip key="tpl" title={t('ai.draft.templateTitle')}>
        {t('ai.draft.templateChip', { name: String(c.template) })}
      </Chip>,
    )
  if (c.acknowledgement)
    chips.push(
      <Chip key="ack" Icon={Bot} title={t('ai.draft.ackTitle')}>
        {t('ai.draft.ackChip')}
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
 * The human-like touches a reply carries: the emoji reaction it makes on a
 * customer message ("Will react 👍 on “ok thanks”" while held, "Reacted 👍"
 * once delivered) and the message its first bubble quotes. Nothing renders for
 * a reply without either.
 */
export function DraftTouches({
  d,
  compact = false,
}: {
  d: Pick<Draft, 'reactions' | 'quote' | 'content'> & Partial<Pick<Draft, 'voice' | 'voice_error'>>
  /** No box — for the chat's review banner. */
  compact?: boolean
}) {
  const t = useT()
  const reactions = Array.isArray(d.reactions) ? d.reactions : []
  const voice = d.voice === 'planned' || d.voice === 'sent' || d.voice === 'text' ? d.voice : null
  if (!reactions.length && !d.quote && !voice) return null
  const clip = (s: string) => (s.length > 80 ? `${s.slice(0, 80)}…` : s)
  return (
    <div
      aria-label={t('ai.draft.touch.label')}
      className={`flex flex-col gap-1 text-[12.5px] leading-snug ${
        compact ? '' : 'rounded-lg border border-line bg-fill-subtle px-2.5 py-1.5'
      }`}
    >
      {reactions.map((r) => (
        <span key={`${r.message_id}:${r.emoji}`} className="flex flex-wrap items-baseline gap-x-1.5">
          <span
            className={`inline-flex items-center gap-1 rounded-full border px-2 py-px font-semibold ${
              r.error
                ? 'border-red/30 text-[color:var(--red-ink)]'
                : r.done
                  ? 'border-green/30 text-[color:var(--green-ink)]'
                  : 'border-line-strong text-text-high'
            }`}
            title={r.error || undefined}
          >
            {r.error
              ? t('ai.draft.touch.reactionFailed', { emoji: r.emoji })
              : r.done
                ? t('ai.draft.touch.reacted', { emoji: r.emoji })
                : t('ai.draft.touch.willReact', { emoji: r.emoji })}
          </span>
          {r.text && <span className="min-w-0 break-words text-text-muted">{t('ai.draft.touch.onMessage', { text: clip(r.text) })}</span>}
        </span>
      ))}
      {reactions.length > 0 && !d.content.trim() && (
        <span className="text-[11.5px] text-text-muted">{t('ai.draft.touch.reactionOnly')}</span>
      )}
      {voice && (
        <span
          className={`inline-flex w-fit items-center rounded-full border px-2 py-px font-semibold ${
            voice === 'sent'
              ? 'border-green/30 text-[color:var(--green-ink)]'
              : voice === 'text'
                ? 'border-amber/30 text-[color:var(--amber-ink)]'
                : 'border-line-strong text-text-high'
          }`}
          title={voice === 'text' ? d.voice_error || undefined : undefined}
        >
          {voice === 'sent'
            ? t('ai.draft.touch.voiceSent')
            : voice === 'text'
              ? t('ai.draft.touch.voiceText')
              : t('ai.draft.touch.voicePlanned')}
        </span>
      )}
      {d.quote && (
        <span className="min-w-0 break-words border-l-2 border-line-strong pl-2 text-text-mid">
          {d.quote.text ? t('ai.draft.touch.quotes', { text: clip(d.quote.text) }) : t('ai.draft.touch.quotesMessage')}
        </span>
      )}
    </div>
  )
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
  const t = useT()
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
              <span className={`font-semibold ${BOOKING_INK[v.tone]}`}>{t('ai.draft.bookingVerb', { verb: t(v.verb) })}</span>{' '}
              {b.label || `#${b.booking_id}`}
            </span>
          )
        })}
      </span>
    )
  return (
    <ul aria-label={t('ai.draft.calendarActionsLabel')} className="flex flex-col gap-1">
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
              <b className={`font-semibold ${BOOKING_INK[v.tone]}`}>{t('ai.draft.bookingVerb', { verb: t(v.verb) })}</b>{' '}
              {b.label || t('ai.draft.bookingFallback', { id: String(b.booking_id) })}
              {b.calendar_label && <span className="text-text-muted"> · {b.calendar_label}</span>}
              {held && v.tone === 'pending' && (
                <span className="text-text-muted">
                  {' '}
                  {b.type === 'cancel' ? t('ai.draft.approvingCancels') : t('ai.draft.approvingBooks')}
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
  const t = useT()
  if (!reasons.length) return null
  // the reasons themselves are the server's words (stored with the draft)
  return (
    <ul className="flex flex-wrap gap-1" aria-label={t('ai.draft.holdLabel')}>
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
  const t = useT()
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
                  {m.source === 'ai' ? t('ai.common.ai') : m.source === 'agent' ? t('ai.common.team') : m.source}
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
  const t = useT()
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), 1000)
    return () => window.clearInterval(timer)
  }, [])
  if (secondsLeft == null) return null
  const left = Math.max(0, Math.round(secondsLeft - (now - fetchedAt) / 1000))
  const m = Math.floor(left / 60)
  const s = left % 60
  return (
    <span
      className="inline-flex items-center gap-1 rounded-md bg-amber/15 px-1.5 py-0.5 text-[11px] font-semibold text-amber-300"
      title={t('ai.draft.countdownTitle')}
    >
      <Clock size={11} strokeWidth={2.2} aria-hidden />
      {left === 0 ? t('ai.draft.sending') : t('ai.draft.sendsIn', { time: `${m}:${String(s).padStart(2, '0')}` })}
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
