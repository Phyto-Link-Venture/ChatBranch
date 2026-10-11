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
import { HelpCircle, Link2, Loader2, UserPlus } from 'lucide-react'
import { cApi, errText, identifyPath, type IdentifyState } from '@/lib/contactIdentity'
import { CreateFromChatModal } from '@/components/contacts/CreateFromChatModal'
import { useT } from '@/i18n'

export default function IdentityBanner({
  channel,
  threadKey,
  threadId,
  refreshToken,
  onCreated,
}: {
  channel: string
  threadKey: string
  /** which company's conversation (a customer has one per company) */
  threadId?: number | null
  /** Any value that changes when the conversation updates (refetch trigger). */
  refreshToken?: unknown
  onCreated: (contactId: number) => void
}) {
  const t = useT()
  const [state, setState] = useState<IdentifyState | null>(null)
  const [open, setOpen] = useState(false)
  const [linking, setLinking] = useState<number | null>(null)
  const [linkError, setLinkError] = useState<string | null>(null)

  // one of the matching contacts — the same scope-checked route as "Create
  // contact", with the contact's id
  const link = async (contactId: number) => {
    setLinking(contactId)
    setLinkError(null)
    try {
      await cApi(identifyPath(channel, threadKey, threadId, '/contact'), {
        method: 'POST',
        body: JSON.stringify({ contact_id: contactId }),
      })
      onCreated(contactId)
      void load()
    } catch (e) {
      setLinkError(errText(e))
    } finally {
      setLinking(null)
    }
  }

  const load = useCallback(async () => {
    try {
      setState(await cApi<IdentifyState>(identifyPath(channel, threadKey, threadId)))
    } catch {
      setState(null) // no chats.view, or the API is down — no banner
    }
  }, [channel, threadKey, threadId])

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
      {req.state === 'ambiguous' && state.can_create && (state.candidates?.length ?? 0) > 0 && (
        <span className="flex w-full flex-wrap items-center gap-1.5">
          <span className="text-[11.5px] font-semibold">{t('chats.identity.linkOne')}</span>
          {state.candidates!.map((c) => (
            <button
              key={c.id}
              type="button"
              disabled={linking !== null}
              onClick={() => void link(c.id)}
              title={[c.phone, c.email].filter(Boolean).join(' · ')}
              className="inline-flex h-7 max-w-full items-center gap-1 rounded-lg border border-line-strong bg-card px-2.5 text-[11.5px] font-semibold text-text-high transition-colors hover:bg-fill-hover disabled:opacity-50"
            >
              {linking === c.id ? <Loader2 size={12} className="animate-spin" aria-hidden /> : <Link2 size={12} aria-hidden />}
              <span className="truncate">
                {c.name || c.phone || c.email}
                {c.phone || c.email ? <span className="ml-1 font-normal text-text-muted">{c.phone || c.email}</span> : null}
              </span>
            </button>
          ))}
          {linkError && (
            <span role="alert" className="text-[11.5px] text-[var(--red-ink)]">
              {linkError}
            </span>
          )}
        </span>
      )}
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
        threadId={threadId}
        onCreated={(id) => {
          onCreated(id)
          void load()
        }}
      />
    </div>
  )
}
