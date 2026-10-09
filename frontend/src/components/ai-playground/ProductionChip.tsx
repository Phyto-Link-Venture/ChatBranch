'use client'

import { Ban, Check, Hand, PauseCircle, PowerOff, type LucideIcon } from 'lucide-react'
import { useT, type TFunction } from '@/i18n'
import { handoverKindLabel } from '@/lib/aiControls'
import type { PlaygroundReply } from '@/lib/aiPlayground'

const TONE = {
  green: 'bg-green/10 text-green-300',
  amber: 'bg-amber/15 text-amber-300',
  red: 'bg-red/10 text-red-300',
  purple: 'bg-purple/10 text-purple-300',
  muted: 'bg-fill text-text-muted',
} as const

/** The production verdict in words (the reasons are the server's own text). */
export function verdictText(t: TFunction, p: PlaygroundReply['production']): string {
  switch (p.verdict) {
    case 'send':
      return t('playground.verdict.send')
    case 'hold':
      return p.shadow ? t('playground.verdict.shadow') : t('playground.verdict.hold', { reason: p.why })
    case 'handover':
      return t('playground.verdict.handover', { kind: handoverKindLabel(t, p.kind ?? '', p.why) })
    case 'blocked':
      return t('playground.verdict.blocked', { reason: p.why })
    case 'ai_off':
      return t('playground.verdict.aiOff', { reason: p.why })
    default:
      return p.why
  }
}

const META: Record<string, { Icon: LucideIcon; tone: keyof typeof TONE }> = {
  send: { Icon: Check, tone: 'green' },
  hold: { Icon: PauseCircle, tone: 'amber' },
  handover: { Icon: Hand, tone: 'purple' },
  blocked: { Icon: Ban, tone: 'red' },
  ai_off: { Icon: PowerOff, tone: 'muted' },
}

/** "Would send" · "Held for review: …" · "Hand over" · "Blocked" · "AI is off here: …". */
export function ProductionChip({ production, full = false }: { production: PlaygroundReply['production']; full?: boolean }) {
  const t = useT()
  const m = META[production.verdict] ?? META.send
  const text = verdictText(t, production)
  return (
    <span
      title={text}
      className={`inline-flex max-w-full items-start gap-1 rounded-full px-2 py-0.5 text-[11px] font-semibold leading-[1.45] ${TONE[m.tone]}`}
    >
      <m.Icon size={12} strokeWidth={2.2} className="mt-[2px] shrink-0" aria-hidden />
      <span className={full ? 'whitespace-normal' : 'truncate'}>{text}</span>
    </span>
  )
}
