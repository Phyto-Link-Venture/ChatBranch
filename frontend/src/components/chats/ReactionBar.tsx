'use client'

/**
 * Reactions on a bubble: the chips under it (one per emoji, a count, who
 * reacted in the tooltip; clicking yours removes it, clicking another adds
 * that one) and the quick-reaction row the action bar shows.
 */
import { Plus } from 'lucide-react'
import { useT } from '@/i18n'
import { QUICK_REACTIONS } from '@/lib/emoji/data'
import type { UiReaction } from './inboxTypes'
import './messageExtras.css'

/** A reaction the business made — a person (`user:<name>`) or the AI (`ai`). */
export const isOurReaction = (actor: string) => actor === 'ai' || actor.startsWith('user:')

/** Our current reaction on a message (the business is one sender: any `user:` or `ai` row). */
export const myReaction = (reactions: UiReaction[] | undefined, meName: string | null): string =>
  reactions?.find((r) => meName && r.actor === `user:${meName}`)?.emoji ??
  reactions?.find((r) => isOurReaction(r.actor))?.emoji ??
  ''

export function ReactionChips({
  reactions,
  meName,
  contactName,
  align,
  onReact,
}: {
  reactions: UiReaction[]
  meName: string | null
  contactName: string
  align: 'start' | 'end'
  /** absent = read-only (no permission, or a channel without reactions) */
  onReact?: (emoji: string) => void
}) {
  const t = useT()
  if (reactions.length === 0) return null
  const groups = new Map<string, UiReaction[]>()
  for (const r of reactions) groups.set(r.emoji, [...(groups.get(r.emoji) ?? []), r])
  const mine = myReaction(reactions, meName)
  const who = (r: UiReaction) =>
    r.actor === 'contact'
      ? contactName || t('bubble.reactions.customer')
      : r.actor === 'ai'
        ? t('bubble.reactions.ai')
        : meName && r.actor === `user:${meName}`
          ? t('bubble.reactions.you')
          : r.actor.replace(/^user:/, '')
  return (
    <div className={`cb-reactions ${align === 'end' ? 'justify-end' : 'justify-start'}`}>
      {[...groups.entries()].map(([emoji, rs]) => {
        const ours = rs.some((r) => isOurReaction(r.actor))
        const label = t('bubble.reactions.by', { names: rs.map(who).join(', '), emoji })
        const hint = onReact ? (ours && mine === emoji ? t('bubble.reactions.removeYours') : t('bubble.reactions.addSame', { emoji })) : ''
        return (
          <button
            key={emoji}
            type="button"
            disabled={!onReact}
            title={hint ? `${label}\n${hint}` : label}
            aria-label={label}
            onClick={() => onReact?.(mine === emoji ? '' : emoji)}
            className={`cb-reaction-chip ${ours ? 'cb-reaction-mine' : ''}`}
          >
            <span className="text-[13px] leading-none">{emoji}</span>
            {rs.length > 1 && <span className="text-[10.5px] font-semibold tabular-nums">{rs.length}</span>}
          </button>
        )
      })}
    </div>
  )
}

/** 👍 ❤️ 😂 😮 😢 🙏 + "more" — the current one is marked; picking it again removes it. */
export function QuickReactions({
  mine,
  onPick,
  onMore,
}: {
  mine: string
  onPick: (emoji: string) => void
  onMore: () => void
}) {
  const t = useT()
  return (
    <div className="flex items-center gap-0.5">
      {QUICK_REACTIONS.map((e) => (
        <button
          key={e}
          type="button"
          onClick={() => onPick(mine === e ? '' : e)}
          title={mine === e ? t('bubble.reactions.removeYours') : t('bubble.actions.react')}
          aria-label={`${t('bubble.actions.react')} ${e}`}
          aria-pressed={mine === e}
          className={`grid h-8 w-8 place-items-center rounded-full text-[18px] leading-none transition-transform hover:scale-110 ${
            mine === e ? 'bg-fill' : ''
          }`}
        >
          {e}
        </button>
      ))}
      <button
        type="button"
        onClick={onMore}
        title={t('bubble.actions.moreEmoji')}
        aria-label={t('bubble.actions.moreEmoji')}
        className="grid h-8 w-8 place-items-center rounded-full text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
      >
        <Plus size={15} aria-hidden />
      </button>
    </div>
  )
}
