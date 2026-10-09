'use client'

/**
 * The unknown-customer strip under the chat header: "asking who they are"
 * while the exchange runs, what the customer said when it needs a person
 * (no matching contact, several matches, no answer), and the "Create contact"
 * quick action. Reads GET /api/contacts/identify/{channel}/{key}; refreshes
 * when the conversation changes. Renders nothing for linked chats or chats
 * the exchange never touched.
 */

import { useCallback, useEffect, useState } from 'react'
import { HelpCircle, Loader2, UserPlus } from 'lucide-react'
import { cApi, threadPath, type IdentifyState } from '@/lib/contactIdentity'
import { CreateFromChatModal } from '@/components/contacts/CreateFromChatModal'
import { useT } from '@/i18n'

export default function IdentityBanner({
  channel,
  threadKey,
  refreshToken,
  onCreated,
}: {
  channel: string
  threadKey: string
  /** Any value that changes when the conversation updates (refetch trigger). */
  refreshToken?: unknown
  onCreated: (contactId: number) => void
}) {
  const t = useT()
  const [state, setState] = useState<IdentifyState | null>(null)
  const [open, setOpen] = useState(false)

  const load = useCallback(async () => {
    try {
      setState(await cApi<IdentifyState>(`contacts/identify/${threadPath(channel, threadKey)}`))
    } catch {
      setState(null) // no chats.view, or the API is down — no banner
    }
  }, [channel, threadKey])

  useEffect(() => {
    void load()
  }, [load, refreshToken])

  const req = state?.request
  if (!state || state.linked || !req || req.state === 'cancelled' || req.state === 'linked' || req.state === 'created')
    return null

  const said = [req.collected.name, req.collected.phone, req.collected.email].filter(Boolean).join(' · ')
  const asking = req.state === 'asking' || req.state === 'armed'
  const text = asking
    ? req.attempts > 0
      ? t('chats.identity.askingN', { count: req.attempts, max: req.max_asks })
      : t('chats.identity.asking')
    : req.state === 'ambiguous'
      ? said
        ? t('chats.identity.ambiguousSaid', { said })
        : t('chats.identity.ambiguous')
      : req.state === 'needs_contact'
        ? said
          ? t('chats.identity.needsContactSaid', { said })
          : t('chats.identity.needsContact')
        : said
          ? t('chats.identity.gaveUpSaid', { said })
          : t('chats.identity.gaveUp')

  return (
    <div
      role="status"
      className={`flex shrink-0 flex-wrap items-center gap-x-3 gap-y-1.5 border-b px-4 py-2 text-[12px] sm:px-5 ${
        asking ? 'border-line bg-fill-subtle text-text-mid' : 'border-amber/30 bg-amber/10 text-amber-300'
      }`}
    >
      <span className="flex min-w-0 flex-1 items-center gap-1.5">
        {asking ? (
          <Loader2 size={12} className="shrink-0 animate-spin" aria-hidden />
        ) : (
          <HelpCircle size={13} className="shrink-0" aria-hidden />
        )}
        <span className="min-w-0">{text}</span>
      </span>
      {state.can_create && req.state !== 'ambiguous' && (
        <button
          type="button"
          onClick={() => setOpen(true)}
          className="inline-flex h-7 shrink-0 items-center gap-1 rounded-lg border border-line-strong bg-card px-2.5 text-[11.5px] font-semibold text-text-high transition-colors hover:bg-fill-hover"
        >
          <UserPlus size={12} aria-hidden /> {t('chats.identity.createContact')}
        </button>
      )}
      <CreateFromChatModal
        open={open}
        onClose={() => setOpen(false)}
        channel={channel}
        threadKey={threadKey}
        onCreated={(id) => {
          onCreated(id)
          void load()
        }}
      />
    </div>
  )
}
