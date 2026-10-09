'use client'

import { useState } from 'react'
import { Bot, Hand, Loader2, UserCheck } from 'lucide-react'
import { handoverAction, handoverBy, type UiHandover } from '@/lib/aiControls'
import { timeAgo } from './inboxTypes'
import { useFormat, useT } from '@/i18n'

/**
 * "The AI handed this over" — shown above the composer while a handover is
 * open: why, by whom and when, with "Take it" (assign to me) and "Let AI
 * continue" (closes the handover and turns the chatbot back on). A person's
 * reply or resolving the conversation closes it too (server side).
 */
export default function HandoverBanner({
  channel,
  threadKey,
  handover,
  assignee,
  me,
  canAct,
  onChanged,
}: {
  channel: string
  threadKey: string
  handover: UiHandover | null | undefined
  assignee?: string
  /** the signed-in username (null until mounted) */
  me: string | null
  canAct: boolean
  onChanged: () => void
}) {
  const t = useT()
  const f = useFormat()
  const [busy, setBusy] = useState<'take' | 'continue' | null>(null)
  const [err, setErr] = useState<string | null>(null)
  if (!handover?.open) return null

  const act = async (action: 'take' | 'continue') => {
    setBusy(action)
    setErr(null)
    try {
      await handoverAction(channel, threadKey, action)
      onChanged()
    } catch (e) {
      setErr(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(null)
    }
  }

  const who = handover.by === 'ai' ? t('chats.handover.theAi') : t('chats.handover.byWho', { who: handoverBy(handover.by, t) })
  const mine = !!me && assignee === me
  const btn =
    'inline-flex h-8 items-center gap-1.5 rounded-lg px-3 text-[12.5px] font-semibold transition-colors disabled:cursor-not-allowed disabled:opacity-50'

  return (
    <div
      role="status"
      className="mx-3 mb-2 flex flex-wrap items-center gap-x-3 gap-y-2 rounded-xl border border-amber/40 bg-amber/10 px-3 py-2.5 sm:mx-4"
    >
      <span className="grid h-8 w-8 shrink-0 place-items-center rounded-full bg-amber/20 text-[color:var(--amber-ink)]">
        <Hand size={15} strokeWidth={1.9} aria-hidden />
      </span>
      <div className="min-w-0 flex-1 basis-[200px]">
        <div className="text-[13px] font-semibold text-text-heading">
          {handover.by === 'ai' ? t('chats.handover.aiHandedOver') : who}
          <span className="font-normal text-text-muted"> · {timeAgo(handover.at, f)}</span>
        </div>
        <div className="text-[12.5px] text-text-mid">
          {handover.reason}
          {mine && <span className="text-text-muted">{t('chats.handover.yours')}</span>}
        </div>
        {err && (
          <div role="alert" className="mt-1 text-[12px] text-[color:var(--red-ink)]">
            {err}
          </div>
        )}
      </div>
      {canAct && (
        <div className="flex shrink-0 flex-wrap gap-1.5">
          {!mine && (
            <button
              type="button"
              onClick={() => act('take')}
              disabled={!!busy}
              className={`${btn} bg-primary text-on-primary hover:opacity-90`}
            >
              {busy === 'take' ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <UserCheck size={13} aria-hidden />}
              {t('chats.handover.takeIt')}
            </button>
          )}
          <button
            type="button"
            onClick={() => act('continue')}
            disabled={!!busy}
            className={`${btn} border border-line-strong bg-card text-text-high hover:bg-fill-hover`}
            title={t('chats.handover.letAiContinueHint')}
          >
            {busy === 'continue' ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Bot size={13} aria-hidden />}
            {t('chats.handover.letAiContinue')}
          </button>
        </div>
      )}
    </div>
  )
}
