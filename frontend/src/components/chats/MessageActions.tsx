'use client'

/**
 * One bubble's actions in /chats: hover (or long-press on a phone) shows a bar
 * with the quick reactions + "more", Reply and Copy text; reaction chips sit
 * under the bubble. Wraps the bubble the page renders; carries the
 * `msg-<pk>` anchor a quote's "jump to original" scrolls to.
 *
 * On a message the SHOP sent, over a channel that allows it, the bar also
 * carries Edit and "Delete for everyone" — WhatsApp's own two, and the page
 * decides whether they apply (direction, channel, and WhatsApp's time
 * windows). Editing happens inline, in place of the bubble, so the operator
 * sees the wording they are changing rather than a dialog over it.
 */
import { useEffect, useRef, useState, type ReactNode } from 'react'
import { Check, Copy, Pencil, Reply, Trash2 } from 'lucide-react'
import { useFormat, useT } from '@/i18n'
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
  canEdit = false,
  canDelete = false,
  onReply,
  onReact,
  onEdit,
  onDelete,
  children,
}: {
  m: UiMessage
  contactName: string
  meName: string | null
  canReply: boolean
  canReact: boolean
  /** Our own text message, on a channel that can edit, inside WhatsApp's window. */
  canEdit?: boolean
  /** Our own message, on a channel that can delete for everyone, inside its window. */
  canDelete?: boolean
  onReply: (m: UiMessage) => void
  onReact: (m: UiMessage, emoji: string) => void
  onEdit?: (m: UiMessage, text: string) => Promise<void>
  onDelete?: (m: UiMessage) => Promise<void>
  children: ReactNode
}) {
  const t = useT()
  const f = useFormat()
  const [open, setOpen] = useState(false) // long-press / keyboard-opened bar
  const [picker, setPicker] = useState(false)
  const [copied, setCopied] = useState(false)
  const [editing, setEditing] = useState(false)
  const [editText, setEditText] = useState('')
  const [working, setWorking] = useState(false)
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

  const editable = canEdit && hasPk && !m.private && !!onEdit
  const deletable = canDelete && hasPk && !m.private && !!onDelete
  const anyAction = reactable || replyable || copyable || editable || deletable

  const saveEdit = async () => {
    const next = editText.trim()
    if (!onEdit || next.length === 0 || next === m.text.trim()) {
      setEditing(false)
      return
    }
    setWorking(true)
    try {
      await onEdit(m, next)
      setEditing(false)
    } finally {
      // the page reports why; the editor stays open so the wording is not lost
      setWorking(false)
    }
  }

  const remove = async () => {
    if (!onDelete) return
    setOpen(false)
    if (!window.confirm(t('bubble.sent.deleteConfirm'))) return
    setWorking(true)
    try {
      await onDelete(m)
    } finally {
      setWorking(false)
    }
  }

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
        {editing ? (
          <div className="w-full max-w-[85%] rounded-xl border border-line-strong bg-card p-2">
            <textarea
              autoFocus
              rows={2}
              value={editText}
              onChange={(e) => setEditText(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) {
                  e.preventDefault()
                  void saveEdit()
                }
                if (e.key === 'Escape') setEditing(false)
              }}
              aria-label={t('bubble.sent.editAria')}
              className="w-full resize-none bg-transparent text-[13px] text-text-primary outline-none"
            />
            <div className="mt-1 flex items-center justify-between gap-2">
              <span className="text-[10.5px] text-text-faint">{t('bubble.sent.editHint')}</span>
              <span className="flex shrink-0 gap-1">
                <button
                  type="button"
                  onClick={() => setEditing(false)}
                  className="rounded-md px-2 py-1 text-[11.5px] font-semibold text-text-mid transition-colors hover:bg-fill-hover"
                >
                  {t('bubble.sent.editCancel')}
                </button>
                <button
                  type="button"
                  onClick={() => void saveEdit()}
                  disabled={working || editText.trim().length === 0}
                  className="rounded-md bg-primary px-2 py-1 text-[11.5px] font-semibold text-on-primary transition-opacity hover:opacity-90 disabled:opacity-40"
                >
                  {t('bubble.sent.editSave')}
                </button>
              </span>
            </div>
          </div>
        ) : (
          children
        )}
        {anyAction && !editing && (
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
            {editable && (
              <button
                type="button"
                onClick={() => {
                  setOpen(false)
                  setEditText(m.text)
                  setEditing(true)
                }}
                title={t('bubble.sent.edit')}
                aria-label={t('bubble.sent.edit')}
                className="grid h-8 w-8 place-items-center rounded-full text-text-mid transition-colors hover:bg-fill-hover hover:text-text-high"
              >
                <Pencil size={14} aria-hidden />
              </button>
            )}
            {deletable && (
              <button
                type="button"
                onClick={() => void remove()}
                disabled={working}
                title={working ? t('bubble.sent.deleting') : t('bubble.sent.delete')}
                aria-label={t('bubble.sent.delete')}
                className="grid h-8 w-8 place-items-center rounded-full text-text-mid transition-colors hover:bg-fill-hover hover:text-red-300 disabled:opacity-40"
              >
                <Trash2 size={14} aria-hidden />
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
      {m.edited_at && !editing && (
        <span
          className="mt-0.5 px-1 text-[10px] italic text-text-faint"
          title={t('bubble.sent.editedAt', { time: f.time(m.edited_at) })}
        >
          {t('bubble.sent.edited')}
        </span>
      )}
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
