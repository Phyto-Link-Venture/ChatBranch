'use client'

import { useMemo, useState } from 'react'
import { Modal } from '@/components/ui/Modal'
import { ChannelLogo } from '@/components/ui/ChannelLogo'
import { useT } from '@/i18n'
import { threadIdentity, type UiThreadSummary } from './inboxTypes'

/**
 * Pick the chat to forward a message into.
 *
 * Only chats on the SAME channel are offered, because that is the only thing a
 * forward can do — WhatsApp forwards within WhatsApp. Offering the rest and
 * then refusing would waste the operator's click, so the reason is shown once
 * at the top instead.
 *
 * The list is the conversations the page already loaded, so this opens with no
 * fetch; the search box filters by name, address and label.
 */
export function ForwardPicker({
  open,
  channel,
  currentIdentity,
  threads,
  busy,
  onPick,
  onClose,
}: {
  open: boolean
  channel: string
  /** The conversation the message is in — never a forward target. */
  currentIdentity: string
  threads: UiThreadSummary[]
  busy?: boolean
  onPick: (thread: UiThreadSummary) => void
  onClose: () => void
}) {
  const t = useT()
  const [query, setQuery] = useState('')

  const candidates = useMemo(() => {
    const needle = query.trim().toLowerCase()
    return threads
      .filter((x) => x.channel === channel && threadIdentity(x) !== currentIdentity && !x.pending)
      .filter((x) =>
        needle === ''
          ? true
          : `${x.name} ${x.sub} ${x.contact_name ?? ''}`.toLowerCase().includes(needle),
      )
      .slice(0, 50)
  }, [threads, channel, currentIdentity, query])

  return (
    <Modal open={open} onClose={onClose} title={t('bubble.forward.title')} subtitle={t('bubble.forward.sameChannel')} size="sm">
      <input
        value={query}
        onChange={(e) => setQuery(e.target.value)}
        placeholder={t('bubble.forward.search')}
        aria-label={t('bubble.forward.search')}
        className="mb-2 w-full rounded-md border border-line bg-surface px-2 py-1.5 text-[12.5px] text-text-primary outline-none focus:border-line-strong"
      />
      {candidates.length === 0 ? (
        <p className="py-4 text-center text-[12px] text-text-muted">{t('bubble.forward.none')}</p>
      ) : (
        <ul className="max-h-72 overflow-y-auto">
          {candidates.map((x) => (
            <li key={threadIdentity(x)}>
              <button
                onClick={() => onPick(x)}
                disabled={busy}
                className="flex w-full items-center gap-2 rounded-md px-2 py-2 text-left transition-colors hover:bg-fill-hover disabled:opacity-50"
              >
                <ChannelLogo channel={x.channel} size={15} />
                <span className="min-w-0 flex-1">
                  <span className="block truncate text-[12.5px] font-semibold text-text-high">
                    {x.contact_name || x.name}
                  </span>
                  <span className="block truncate text-[11px] text-text-muted">{x.sub}</span>
                </span>
                <span className="shrink-0 text-[11px] font-semibold text-primary">{t('bubble.forward.send')}</span>
              </button>
            </li>
          ))}
        </ul>
      )}
    </Modal>
  )
}
