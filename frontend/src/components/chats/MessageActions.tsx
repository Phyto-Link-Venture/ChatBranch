'use client'

/**
 * One bubble's actions in /chats: hover (or long-press on a phone) shows a bar
 * with the quick reactions + "more", Reply and Copy text; reaction chips sit
 * under the bubble. Wraps the bubble the page renders; carries the
 * `msg-<pk>` anchor a quote's "jump to original" scrolls to.
 */
import { useEffect, useRef, useState, type ReactNode } from 'react'
import { Check, Copy, Reply } from 'lucide-react'
import { useT } from '@/i18n'
import { EmojiPicker } from './EmojiPicker'
import { QuickReactions, ReactionChips, myReaction } from './ReactionBar'
import type { UiMessage } from './inboxTypes'
import './messageExtras.css'

const LONG_PRESS_MS = 450

export default function MessageActions({
  m,
  contactName,
  meName,
  canReply,
  canReact,
  onReply,
  onReact,
  children,
}: {
  m: UiMessage
  contactName: string
  meName: string | null
  canReply: boolean
  canReact: boolean
  onReply: (m: UiMessage) => void
  onReact: (m: UiMessage, emoji: string) => void
  children: ReactNode
}) {
  const t = useT()
  const [open, setOpen] = useState(false) // long-press / keyboard-opened bar
  const [picker, setPicker] = useState(false)
  const [copied, setCopied] = useState(false)
  const rootRef = useRef<HTMLDivElement>(null)
  const pressTimer = useRef<number | null>(null)
  const align = m.dir === 'out' ? 'end' : 'start'
  const hasPk = typeof m.pk === 'number'
  const reactable = canReact && hasPk && !m.private
  const replyable = canReply && hasPk && !m.private
  const copyable = !!m.text && !/^\[(image|video|audio|document|sticker)\]$/.test(m.text)
  const mine = myReaction(m.reactions, meName)

  // a tap outside closes a long-press-opened bar
  useEffect(() => {
    if (!open) return
    const onDown = (e: PointerEvent) => {
      if (rootRef.current && !rootRef.current.contains(e.target as Node)) setOpen(false)
    }
    document.addEventListener('pointerdown', onDown)
    return () => document.removeEventListener('pointerdown', onDown)
  }, [open])

  useEffect(() => () => {
    if (pressTimer.current) window.clearTimeout(pressTimer.current)
  }, [])

  const cancelPress = () => {
    if (pressTimer.current) window.clearTimeout(pressTimer.current)
    pressTimer.current = null
  }

  const react = (emoji: string) => {
    setOpen(false)
    setPicker(false)
    onReact(m, emoji)
  }

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(m.text)
      setCopied(true)
      window.setTimeout(() => setCopied(false), 1200)
    } catch {
      /* clipboard blocked (insecure origin) — nothing to do */
    }
  }

  const anyAction = reactable || replyable || copyable

  return (
    <div
      ref={rootRef}
      id={hasPk ? `msg-${m.pk}` : undefined}
      className={`cb-msg flex w-full flex-col ${align === 'end' ? 'items-end' : 'items-start'}`}
      onTouchStart={() => {
        if (!anyAction) return
        cancelPress()
        pressTimer.current = window.setTimeout(() => setOpen(true), LONG_PRESS_MS)
      }}
      onTouchEnd={cancelPress}
      onTouchMove={cancelPress}
      onTouchCancel={cancelPress}
      onKeyDown={(e) => {
        if (e.key === 'Escape') {
          setOpen(false)
          setPicker(false)
        }
      }}
    >
      <div className={`relative flex w-full ${align === 'end' ? 'justify-end' : 'justify-start'}`}>
        {children}
        {anyAction && (
          <div
            role="toolbar"
            aria-label={t('bubble.actions.aria')}
            className={`cb-msg-tools ${open || picker ? 'is-open' : ''} ${align === 'end' ? 'right-0' : 'left-0'}`}
          >
            {reactable && <QuickReactions mine={mine} onPick={react} onMore={() => setPicker(true)} />}
            {reactable && (replyable || copyable) && <span className="mx-0.5 h-5 w-px bg-line" aria-hidden />}
            {replyable && (
              <button
                type="button"
                onClick={() => {
                  setOpen(false)
                  onReply(m)
                }}
                title={t('bubble.actions.reply')}
                aria-label={t('bubble.actions.reply')}
                className="grid h-8 w-8 place-items-center rounded-full text-text-mid transition-colors hover:bg-fill-hover hover:text-text-high"
              >
                <Reply size={15} aria-hidden />
              </button>
            )}
            {copyable && (
              <button
                type="button"
                onClick={copy}
                title={copied ? t('bubble.actions.copied') : t('bubble.actions.copy')}
                aria-label={t('bubble.actions.copy')}
                className="grid h-8 w-8 place-items-center rounded-full text-text-mid transition-colors hover:bg-fill-hover hover:text-text-high"
              >
                {copied ? <Check size={14} aria-hidden /> : <Copy size={14} aria-hidden />}
              </button>
            )}
            {picker && (
              <EmojiPicker
                align={align === 'end' ? 'right' : 'left'}
                placement="below"
                onPick={react}
                onClose={() => setPicker(false)}
              />
            )}
          </div>
        )}
      </div>
      {m.reactions && m.reactions.length > 0 && (
        <ReactionChips
          reactions={m.reactions}
          meName={meName}
          contactName={contactName}
          align={align}
          onReact={reactable ? (e) => onReact(m, e) : undefined}
        />
      )}
    </div>
  )
}
