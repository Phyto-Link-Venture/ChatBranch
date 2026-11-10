'use client'

/**
 * Inbox pieces for social threads (channel "social", via Zernio): the kind chip
 * (Comment / Review ★n / DM) and the pinned context card above the messages —
 * the post a comment is on, or the review's stars and reviewer — with Hide /
 * Unhide and "Reply privately" for comments.
 *
 * Actions: POST /api/social/threads/{key}/hide {hidden}, POST
 * /api/social/threads/{key}/private-reply {text}.
 */

import { useState } from 'react'
import { AlertCircle, Eye, EyeOff, ExternalLink, Globe, Loader2, MessageCircle, Mail, Star } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { ChannelLogo } from '@/components/ui/ChannelLogo'
import { Modal } from '@/components/ui/Modal'
import { errMessage, isNotSetUp, platformId, platformLabel, socialApi, socialThreadPath, type SocialMeta } from '@/lib/social'
import { useT } from '@/i18n'

/** 1..5 stars, filled up to `rating`. Static hex: `var(--…)` in an SVG prop renders invisible. */
export function Stars({ rating, size = 12 }: { rating: number; size?: number }) {
  const t = useT()
  const n = Math.max(0, Math.min(5, Math.round(rating)))
  return (
    <span className="inline-flex items-center gap-px" role="img" aria-label={t('social.inbox.stars', { count: n })}>
      {[1, 2, 3, 4, 5].map((i) => (
        <Star key={i} size={size} fill={i <= n ? '#E8A317' : 'none'} className={i <= n ? 'text-amber-300' : 'text-text-faint'} aria-hidden />
      ))}
    </span>
  )
}

/** "Comment" / "Review ★4" / "DM" chip for a social thread. */
export function SocialKindChip({ social, className = '' }: { social: SocialMeta; className?: string }) {
  const t = useT()
  const base = `inline-flex shrink-0 items-center gap-0.5 rounded-full border border-line-strong px-1.5 text-[10px] font-semibold leading-[16px] text-text-mid ${className}`
  if (social.kind === 'review')
    return (
      <span className={base} title={t('social.inbox.reviewHint')}>
        <Star size={9} aria-hidden /> {t('social.inbox.kindReview')}
        {social.rating ? ` ★${social.rating}` : ''}
      </span>
    )
  if (social.kind === 'dm')
    return (
      <span className={base} title={t('social.inbox.dmHint')}>
        <Mail size={9} aria-hidden /> {t('social.inbox.kindDm')}
      </span>
    )
  return (
    <span className={base} title={t('social.inbox.commentHint')}>
      <MessageCircle size={9} aria-hidden /> {t('social.inbox.kindComment')}
      {social.hidden ? (
        <>
          {' · '}
          <EyeOff size={9} aria-hidden />
        </>
      ) : null}
    </span>
  )
}

/**
 * The context card pinned above a social thread's messages. Comments: the post
 * (thumbnail, text, link) + Hide / Unhide + Reply privately. Reviews: stars and
 * the reviewer. DMs: just the account it came to.
 */
export function SocialContextCard({
  threadKey,
  threadId,
  social,
  canAct,
  onChanged,
}: {
  threadKey: string
  threadId?: number | null
  social: SocialMeta
  canAct: boolean
  onChanged: () => void
}) {
  const t = useT()
  const [busy, setBusy] = useState<'' | 'hide' | 'dm'>('')
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [privateOpen, setPrivateOpen] = useState(false)
  const [privateText, setPrivateText] = useState('')
  const [privateError, setPrivateError] = useState<string | null>(null)

  const platform = platformId(social.platform)
  const post = social.post ?? null
  const author = social.author ?? null

  const toggleHidden = async () => {
    setBusy('hide')
    setError(null)
    setNotice(null)
    try {
      await socialApi(socialThreadPath(threadKey, 'hide', threadId), {
        method: 'POST',
        body: JSON.stringify({ hidden: !social.hidden }),
      })
      setNotice(t(social.hidden ? 'social.inbox.unhidden' : 'social.inbox.hiddenDone'))
      onChanged()
    } catch (e) {
      setError(isNotSetUp(e) ? t('social.zernio.notSetUpBody') : errMessage(e))
    } finally {
      setBusy('')
    }
  }

  const sendPrivate = async () => {
    if (!privateText.trim()) return
    setBusy('dm')
    setPrivateError(null)
    try {
      await socialApi(socialThreadPath(threadKey, 'private-reply', threadId), {
        method: 'POST',
        body: JSON.stringify({ text: privateText.trim() }),
      })
      setPrivateOpen(false)
      setPrivateText('')
      setNotice(t('social.inbox.privateSent'))
      onChanged()
    } catch (e) {
      setPrivateError(isNotSetUp(e) ? t('social.zernio.notSetUpBody') : errMessage(e))
    } finally {
      setBusy('')
    }
  }

  return (
    <div data-tour="chats-social-context" className="shrink-0 border-b border-line bg-well px-4 py-2.5 sm:px-5">
      <div className="flex items-start gap-3">
        {social.kind === 'comment' && post?.media_url && (
          // eslint-disable-next-line @next/next/no-img-element
          <img src={post.media_url} alt="" className="h-14 w-14 shrink-0 rounded-lg border border-line object-cover" />
        )}
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-x-2 gap-y-1 text-[11.5px] text-text-muted">
            <ChannelLogo channel={platform || 'social'} size={13} />
            <span className="font-semibold text-text-high">
              {social.kind === 'review'
                ? t('social.inbox.reviewOn', { platform: platformLabel(social.platform) })
                : social.kind === 'dm'
                  ? t('social.inbox.dmOn', { platform: platformLabel(social.platform) })
                  : t('social.inbox.commentOn', { platform: platformLabel(social.platform) })}
            </span>
            {social.account_username && (
              <span>{t('social.inbox.toAccount', { account: social.account_username.startsWith('@') ? social.account_username : `@${social.account_username}` })}</span>
            )}
            {social.kind === 'comment' && social.hidden && (
              <span className="inline-flex items-center gap-1 rounded-full bg-amber/15 px-2 py-px text-[10.5px] font-semibold text-amber-300">
                <EyeOff size={10} aria-hidden /> {t('social.inbox.hiddenBadge')}
              </span>
            )}
          </div>

          {social.kind === 'review' && (
            <div className="mt-1 flex flex-wrap items-center gap-2 text-[12.5px] text-text-high">
              {social.rating ? <Stars rating={social.rating} size={13} /> : null}
              {author?.name && <span className="font-semibold">{author.name}</span>}
            </div>
          )}

          {social.kind === 'comment' && post && (post.text || post.permalink) && (
            <div className="mt-1 text-[12.5px] leading-snug text-text-mid">
              {post.text && <span className="line-clamp-2">{post.text}</span>}
              {post.permalink && (
                <a
                  href={post.permalink}
                  target="_blank"
                  rel="noreferrer"
                  className="mt-0.5 inline-flex items-center gap-1 text-[11.5px] font-semibold text-brand-300 hover:underline"
                >
                  <ExternalLink size={11} aria-hidden /> {t('social.inbox.openPost')}
                </a>
              )}
            </div>
          )}

          {(social.kind === 'comment' || social.kind === 'review') && (
            <div className="mt-1 flex items-center gap-1 text-[11px] text-text-muted">
              <Globe size={10} aria-hidden />
              {t('social.inbox.publicNote')}
            </div>
          )}

          {error && (
            <div role="alert" className="mt-1.5 flex items-center gap-1.5 text-[12px]" style={{ color: 'var(--red-ink)' }}>
              <AlertCircle size={12} aria-hidden /> {error}
            </div>
          )}
          {notice && (
            <div role="status" className="mt-1.5 text-[12px] font-semibold" style={{ color: 'var(--green-ink)' }}>
              {notice}
            </div>
          )}
        </div>

        {social.kind === 'comment' && canAct && (
          <div className="flex shrink-0 flex-wrap items-center justify-end gap-1.5">
            <Button variant="secondary" size="sm" onClick={toggleHidden} disabled={busy !== ''} title={t('social.inbox.hideHint')}>
              {busy === 'hide' ? (
                <Loader2 size={12} className="animate-spin" aria-hidden />
              ) : social.hidden ? (
                <Eye size={12} aria-hidden />
              ) : (
                <EyeOff size={12} aria-hidden />
              )}
              {social.hidden ? t('social.inbox.unhide') : t('social.inbox.hide')}
            </Button>
            <Button
              variant="secondary"
              size="sm"
              onClick={() => {
                setPrivateError(null)
                setPrivateOpen(true)
              }}
              disabled={busy !== ''}
              title={t('social.inbox.privateHint')}
            >
              <Mail size={12} aria-hidden /> {t('social.inbox.replyPrivately')}
            </Button>
          </div>
        )}
      </div>

      <Modal
        open={privateOpen}
        onClose={() => busy === '' && setPrivateOpen(false)}
        title={t('social.inbox.privateTitle', { name: author?.name || author?.handle || t('social.inbox.theCommenter') })}
        subtitle={t('social.inbox.privateSubtitle')}
        size="md"
        footer={
          <>
            <Button variant="ghost" onClick={() => setPrivateOpen(false)} disabled={busy !== ''}>
              {t('common.actions.cancel')}
            </Button>
            <Button onClick={sendPrivate} disabled={busy !== '' || !privateText.trim()}>
              {busy === 'dm' && <Loader2 size={13} className="animate-spin" aria-hidden />}
              {t('common.actions.send')}
            </Button>
          </>
        }
      >
        {privateError && (
          <div
            role="alert"
            className="mb-3 rounded-lg border border-red/35 bg-red/10 px-3 py-2 text-[12.5px] font-semibold"
            style={{ color: 'var(--red-ink)' }}
          >
            {privateError}
          </div>
        )}
        <textarea
          value={privateText}
          onChange={(e) => setPrivateText(e.target.value)}
          rows={4}
          autoFocus
          aria-label={t('social.inbox.replyPrivately')}
          placeholder={t('social.inbox.privatePlaceholder')}
          className="w-full resize-y rounded-lg border border-line-strong bg-card px-3 py-2 text-[13px] text-text-primary outline-none focus:border-brand-400"
        />
      </Modal>
    </div>
  )
}
