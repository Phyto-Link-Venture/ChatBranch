'use client'

/** Small pieces the Connected list's cards share (inbox, managed connection, shop). */

import { BadgeCheck, Store } from 'lucide-react'
import { HEALTH_META, type HealthStatus } from '@/lib/channelHealth'
import { useT, type MessageKey } from '@/i18n'

export function HealthDot({ status, detail }: { status: HealthStatus; detail?: string }) {
  const t = useT()
  const m = HEALTH_META[status] ?? HEALTH_META.unknown
  return (
    <span className="inline-flex items-center gap-1.5" title={detail || t(m.key)}>
      <span className="h-2 w-2 shrink-0 rounded-full" style={{ background: m.dot }} aria-hidden />
      <span style={{ color: m.ink }} className="font-semibold">
        {t(m.key)}
      </span>
    </span>
  )
}

/** How an inbox is connected: "Cloud API", "YCloud", "QR", "via Zernio"… */
export function MethodBadge({ label, zernio }: { label: MessageKey; zernio?: boolean }) {
  const t = useT()
  return (
    <span
      className={`shrink-0 rounded-full border px-1.5 py-px text-[9.5px] font-bold uppercase tracking-[0.06em] ${
        zernio ? 'border-[color:var(--text-primary)] text-text-primary' : 'border-line-strong text-text-mid'
      }`}
      title={t('channels.card.methodTitle', { method: t(label) })}
    >
      {t(label)}
    </span>
  )
}

/** A read-only outlet chip. */
export function OutletChip({ name }: { name: string }) {
  return (
    <span className="inline-flex max-w-full items-center gap-1 truncate rounded-full border border-line-strong px-2 py-px text-[10.5px] font-semibold text-text-mid">
      <Store size={10} aria-hidden /> {name}
    </span>
  )
}

/** A capability chip ("Posts", "Comments", "DMs"…). */
export function CapChip({ label }: { label: string }) {
  return (
    <span className="rounded-full border border-line-strong px-2 py-px text-[10.5px] font-semibold text-text-mid">{label}</span>
  )
}

export const cardBtn =
  'inline-flex h-8 cursor-pointer items-center gap-1.5 rounded-lg border border-line-strong bg-card px-3 text-[12.5px] font-semibold text-text-high transition-colors hover:bg-fill-hover disabled:cursor-not-allowed disabled:opacity-50'

/** "Requires platform approval" — TikTok / Douyin only open their messaging APIs to approved apps. */
export function ApprovalChip({ compact = false }: { compact?: boolean }) {
  const t = useT()
  return (
    <span
      className="inline-flex items-center gap-1 rounded-full bg-amber/15 px-1.5 py-px text-[9.5px] font-bold uppercase tracking-[0.06em]"
      style={{ color: 'var(--amber-ink)' }}
      title={t('channels.chips.approvalTitle')}
    >
      <BadgeCheck size={10} aria-hidden />
      {compact ? t('channels.chips.approval') : t('channels.chips.requiresApproval')}
    </span>
  )
}

export function KindBadge({ badge }: { badge: 'Beta' | 'Demo' }) {
  const t = useT()
  return (
    <span
      className={`rounded-full px-1.5 py-px text-[9.5px] font-bold uppercase tracking-[0.06em] ${
        badge === 'Demo' ? 'bg-amber/15' : 'bg-fill text-text-mid'
      }`}
      style={badge === 'Demo' ? { color: 'var(--amber-ink)' } : undefined}
      title={badge === 'Demo' ? t('channels.chips.demoTitle') : t('channels.chips.betaTitle')}
    >
      {badge === 'Demo' ? t('channels.chips.demo') : t('channels.chips.beta')}
    </span>
  )
}
