'use client'

/**
 * A quoted message — above the text of a reply bubble (click = jump to the
 * original) and above the composer while you write a reply (✕ = cancel).
 */
import { X } from 'lucide-react'
import { useT, type MessageKey } from '@/i18n'
import type { UiMessage, UiQuote } from './inboxTypes'
import './messageExtras.css'

const MEDIA = ['image', 'video', 'audio', 'document']

/** Who wrote the quoted message, in the reader's words. */
function useAuthor() {
  const t = useT()
  return (q: { dir?: string; source?: string; missing?: boolean }, contactName: string) =>
    q.missing
      ? t('bubble.quote.missing')
      : q.dir === 'out'
        ? q.source === 'ai'
          ? t('bubble.quote.ai')
          : t('bubble.quote.you')
        : contactName || t('bubble.quote.customer')
}

function useSnippet() {
  const t = useT()
  return (q: UiQuote) => {
    const text = (q.text || '').trim()
    // "[image]"-style placeholders read as the media word
    const placeholder = /^\[(image|video|audio|document)\]$/.exec(text)?.[1]
    if (placeholder) return t(`bubble.quote.media.${placeholder}` as MessageKey)
    if (!text && q.media_type && MEDIA.includes(q.media_type)) return t(`bubble.quote.media.${q.media_type}` as MessageKey)
    return text
  }
}

export function QuotedBlock({
  q,
  contactName,
  onJump,
}: {
  q: UiQuote
  contactName: string
  onJump?: (pk: number) => void
}) {
  const t = useT()
  const author = useAuthor()
  const snippet = useSnippet()
  const canJump = !q.missing && typeof q.pk === 'number' && !!onJump
  const body = (
    <>
      <span className="cb-quote-author">{author(q, contactName)}</span>
      {!q.missing && <span className="cb-quote-text">{snippet(q)}</span>}
    </>
  )
  return canJump ? (
    <button
      type="button"
      className="cb-quote"
      title={t('bubble.quote.jump')}
      aria-label={t('bubble.quote.jump')}
      onClick={(e) => {
        e.stopPropagation()
        onJump!(q.pk!)
      }}
    >
      {body}
    </button>
  ) : (
    <span className="cb-quote">{body}</span>
  )
}

/** The composer's "Replying to …" strip. */
export function ReplyPreview({
  m,
  contactName,
  onCancel,
}: {
  m: UiMessage
  contactName: string
  onCancel: () => void
}) {
  const t = useT()
  const author = useAuthor()
  const snippet = useSnippet()
  const who = author(m, contactName)
  return (
    <div className="mb-2 flex items-center gap-2 rounded-lg border border-line bg-card px-3 py-1.5" data-tour="reply-preview">
      <div className="cb-quote cb-quote-composer min-w-0 flex-1">
        <span className="cb-quote-author">{t('bubble.quote.replyingTo', { who })}</span>
        <span className="cb-quote-text">
          {snippet({ id: m.id, text: m.text, media_type: m.media?.type ?? null, dir: m.dir, source: m.source })}
        </span>
      </div>
      <button
        type="button"
        onClick={onCancel}
        title={t('bubble.quote.cancel')}
        aria-label={t('bubble.quote.cancel')}
        className="grid h-6 w-6 shrink-0 place-items-center rounded-md text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
      >
        <X size={12} aria-hidden />
      </button>
    </div>
  )
}

/** Scroll the conversation to a message and flash it. */
export function jumpToMessage(pk: number) {
  const el = typeof document !== 'undefined' ? document.getElementById(`msg-${pk}`) : null
  if (!el) return
  el.scrollIntoView({ behavior: 'smooth', block: 'center' })
  el.classList.remove('cb-msg-flash')
  // restart the animation on a repeat click
  void el.offsetWidth
  el.classList.add('cb-msg-flash')
  window.setTimeout(() => el.classList.remove('cb-msg-flash'), 1800)
}
