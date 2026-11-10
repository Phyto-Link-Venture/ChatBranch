'use client'

/** /social: a post's status chip and its card (text, media, networks, per-account results, Retry / Delete / Edit). */

import { AlertCircle, CalendarClock, ExternalLink, Loader2, Pencil, RotateCcw, Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { ChannelLogo } from '@/components/ui/ChannelLogo'
import { platformId, platformLabel, targetAccountId, targetName, type SocialAccount, type SocialPost } from '@/lib/social'
import { useFormat, useT, type MessageKey } from '@/i18n'

export const STATUS_META: Record<string, { lbl: MessageKey; cls: string; dot: string }> = {
  draft: { lbl: 'social.status.draft', cls: 'bg-fill text-text-mid', dot: 'bg-text-faint' },
  scheduled: { lbl: 'social.status.scheduled', cls: 'bg-blue/15 text-blue-300', dot: 'bg-blue' },
  publishing: { lbl: 'social.status.publishing', cls: 'bg-cyan/15 text-cyan-300', dot: 'bg-cyan' },
  published: { lbl: 'social.status.published', cls: 'bg-green/15 text-green-300', dot: 'bg-green' },
  partial: { lbl: 'social.status.partial', cls: 'bg-amber/15 text-amber-300', dot: 'bg-amber' },
  failed: { lbl: 'social.status.failed', cls: 'bg-red/15 text-red-300', dot: 'bg-red' },
  deleted: { lbl: 'social.status.deleted', cls: 'bg-fill text-text-faint', dot: 'bg-text-faint' },
}

export const statusMeta = (s: string) => STATUS_META[s] ?? STATUS_META.draft

export function PostStatusChip({ status }: { status: string }) {
  const t = useT()
  const m = statusMeta(status)
  return <span className={`shrink-0 rounded-full px-2 py-px text-[10.5px] font-bold ${m.cls}`}>{STATUS_META[status] ? t(m.lbl) : status}</span>
}

/** When a post goes / went out: its schedule, else when it was made. */
export const postTime = (p: SocialPost) => p.scheduled_at || p.created_at || ''

export function PostCard({
  post,
  accounts,
  canPublish,
  busy,
  onRetry,
  onDelete,
  onEdit,
}: {
  post: SocialPost
  accounts: SocialAccount[]
  canPublish: boolean
  busy: string
  onRetry: (p: SocialPost) => void
  onDelete: (p: SocialPost) => void
  onEdit: (p: SocialPost) => void
}) {
  const t = useT()
  const f = useFormat()
  const status = String(post.status)
  const platforms = [
    ...new Set(
      (post.platforms && post.platforms.length
        ? post.platforms
        : (post.account_ids ?? []).map((id) => accounts.find((a) => a.id === targetAccountId(id))?.platform ?? '')
      )
        .filter(Boolean)
        .map(platformId),
    ),
  ]
  const results = post.results ?? []
  const media = post.media ?? []
  const accountName = (id?: string | null, pageId?: string | null) => (id ? targetName(accounts, id, pageId) : null)

  return (
    <article className="flex flex-col gap-2 rounded-xl border border-line bg-card p-3.5">
      <div className="flex flex-wrap items-center gap-2">
        <PostStatusChip status={status} />
        <span className="flex items-center gap-1">
          {platforms.map((p) => (
            <ChannelLogo key={p} channel={p} size={14} title={platformLabel(p)} />
          ))}
        </span>
        <span className="ml-auto inline-flex items-center gap-1 text-[11.5px] text-text-muted">
          {post.scheduled_at && <CalendarClock size={11} aria-hidden />}
          {postTime(post) ? f.dateTime(postTime(post)) : ''}
        </span>
      </div>

      {post.content && <p className="line-clamp-4 whitespace-pre-wrap text-[13px] leading-relaxed text-text-primary">{post.content}</p>}

      {media.length > 0 && (
        <div className="flex flex-wrap gap-1.5">
          {media.slice(0, 4).map((m, i) =>
            m.type?.startsWith('video') ? (
              <span key={i} className="grid h-12 w-12 place-items-center rounded-md border border-line bg-fill text-[10px] text-text-muted">
                {t('social.posts.video')}
              </span>
            ) : (
              // eslint-disable-next-line @next/next/no-img-element
              <img key={i} src={m.url} alt="" className="h-12 w-12 rounded-md border border-line object-cover" />
            ),
          )}
          {media.length > 4 && <span className="self-center text-[11px] text-text-muted">+{media.length - 4}</span>}
        </div>
      )}

      {post.error && (
        <div className="flex items-start gap-1.5 text-[12px]" style={{ color: 'var(--red-ink)' }}>
          <AlertCircle size={12} className="mt-0.5 shrink-0" aria-hidden /> {post.error}
        </div>
      )}

      {results.length > 0 && (
        <ul className="flex flex-col gap-1 border-t border-line pt-2">
          {results.map((r, i) => (
            <li key={`${r.account_id ?? i}`} className="flex flex-wrap items-center gap-1.5 text-[12px]">
              <ChannelLogo channel={platformId(r.platform) || 'social'} size={12} title="" />
              <span className="font-semibold text-text-high">{accountName(r.account_id, r.page_id) ?? platformLabel(r.platform)}</span>
              {r.status && <PostStatusChip status={r.status} />}
              {r.url && (
                <a href={r.url} target="_blank" rel="noreferrer" className="inline-flex items-center gap-0.5 font-semibold text-brand-300 hover:underline">
                  <ExternalLink size={11} aria-hidden /> {t('social.posts.view')}
                </a>
              )}
              {r.error && <span className="basis-full text-[11.5px]" style={{ color: 'var(--red-ink)' }}>{r.error}</span>}
            </li>
          ))}
        </ul>
      )}

      {canPublish && status !== 'deleted' && (
        <div className="flex flex-wrap items-center justify-end gap-1.5">
          {(status === 'draft' || status === 'scheduled') && (
            <Button variant="ghost" size="sm" onClick={() => onEdit(post)} disabled={busy !== ''}>
              <Pencil size={12} aria-hidden /> {t('common.actions.edit')}
            </Button>
          )}
          {(status === 'failed' || status === 'partial') && (
            <Button variant="secondary" size="sm" onClick={() => onRetry(post)} disabled={busy !== ''}>
              {busy === `retry-${post.id}` ? <Loader2 size={12} className="animate-spin" aria-hidden /> : <RotateCcw size={12} aria-hidden />}
              {t('social.posts.retry')}
            </Button>
          )}
          <Button variant="ghost" size="sm" onClick={() => onDelete(post)} disabled={busy !== ''}>
            <Trash2 size={12} aria-hidden /> {t('common.actions.delete')}
          </Button>
        </div>
      )}
    </article>
  )
}
