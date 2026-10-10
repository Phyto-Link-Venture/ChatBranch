'use client'

/**
 * "The AI has a reply waiting for review" — shown above the composer when the
 * AI monitor is holding a draft for this conversation (review_all /
 * review_flagged / shadow modes). The operator can approve it as written,
 * reject it, or open it in the AI monitor to edit — without leaving the chat.
 *
 * Reads `GET /api/ai/monitor?thread=<channel>:<key>&status=pending`; refreshes
 * when the conversation changes (the page re-renders on stream `change`
 * events, which the monitor bumps when a draft is held or decided).
 */

import { useCallback, useEffect, useState } from 'react'
import Link from 'next/link'
import { Check, Loader2, ScanEye, X } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { DraftBookings } from '@/components/ai-monitor/DraftBits'
import { DraftDocuments } from '@/components/ai-monitor/DraftDocuments'
import { aiApi, decide, decisionFromError, type Draft, type DraftList } from '@/lib/aiMonitor'
import { useT } from '@/i18n'

export default function PendingDraftBanner({
  channel,
  threadKey,
  refreshToken,
}: {
  channel: string
  threadKey: string
  /** Any value that changes when the conversation updates (refetch trigger). */
  refreshToken?: unknown
}) {
  const t = useT()
  const [draft, setDraft] = useState<Draft | null>(null)
  const [busy, setBusy] = useState<'approve' | 'reject' | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(async () => {
    try {
      const thread = encodeURIComponent(`${channel}:${threadKey}`)
      const d = await aiApi<DraftList>(`ai/monitor?thread=${thread}&status=pending&page_size=1&sort=oldest`)
      setDraft(d.drafts[0] ?? null)
    } catch {
      // no ai.monitor capability, or the API is down — no banner
      setDraft(null)
    }
  }, [channel, threadKey])

  useEffect(() => {
    setError(null)
    void load()
  }, [load, refreshToken])

  if (!draft) return null

  const act = async (kind: 'approve' | 'reject') => {
    setBusy(kind)
    setError(null)
    try {
      if (kind === 'approve') await decide.approve(draft.id)
      else await decide.reject(draft.id, 'Rejected from the chat')
      await load()
    } catch (e) {
      const r = decisionFromError(e)
      setError(r?.detail || (e instanceof Error ? e.message : t('chats.draft.couldNotUpdate')))
    } finally {
      setBusy(null)
    }
  }

  return (
    <div role="status" className="mx-3 mb-2 rounded-xl border border-amber/40 bg-amber/10 px-3.5 py-2.5 sm:mx-4">
      <div className="flex flex-wrap items-center gap-2">
        <ScanEye size={15} className="shrink-0 text-amber-300" aria-hidden />
        <span className="text-[12.5px] font-semibold text-text-heading">{t('chats.draft.title')}</span>
        {draft.reasons.length > 0 && (
          <span className="min-w-0 truncate text-[12px] text-text-muted">· {draft.reasons.join(' · ')}</span>
        )}
        <span className="ml-auto flex flex-wrap items-center gap-1.5">
          <Button size="sm" variant="primary" disabled={!!busy} onClick={() => void act('approve')}>
            {busy === 'approve' ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Check size={13} aria-hidden />}
            {t('chats.draft.approveSend')}
          </Button>
          <Button size="sm" variant="ghost" disabled={!!busy} onClick={() => void act('reject')}>
            {busy === 'reject' ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <X size={13} aria-hidden />}
            {t('chats.draft.reject')}
          </Button>
          <Link
            href="/ai-monitor"
            className="inline-flex h-8 items-center rounded-lg px-2.5 text-[12.5px] font-semibold text-text-mid hover:bg-fill-hover hover:text-text-high"
          >
            {t('chats.draft.editInMonitor')}
          </Link>
        </span>
      </div>
      <p className="mt-1.5 line-clamp-3 whitespace-pre-wrap text-[13px] leading-relaxed text-text-high">{draft.content}</p>
      {/* a held reply has only RESERVED its calendar actions — approving makes them */}
      {draft.bookings && draft.bookings.length > 0 && (
        <div className="mt-1.5">
          <DraftBookings d={draft} compact />
        </div>
      )}
      {/* …and the files it will send after its text (Documents PDFs, QR codes) */}
      {draft.documents && draft.documents.length > 0 && (
        <div className="mt-1.5">
          <DraftDocuments d={draft} compact />
        </div>
      )}
      {error && (
        <p role="alert" className="mt-1.5 text-[12px] text-[color:var(--red-ink)]">
          {error}
        </p>
      )}
    </div>
  )
}
