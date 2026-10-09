'use client'

import { useEffect, useState } from 'react'
import { PhoneCall } from 'lucide-react'
import { useApp } from '@/lib/AppContext'
import { initials } from '@/lib/mocks'
import { useT } from '@/i18n'

const STATE_DOT: Record<string, string> = {
  dialing: 'rgb(var(--c-amber))',
  ringing: 'rgb(var(--c-amber))',
  live: 'rgb(var(--c-green))',
}

/** Fleet strip: one chip per concurrent SIP call, always visible above the
 *  inbox so several live calls can be watched at once (the /live-calls monitor
 *  grid, folded into Chats). Click a contact chip to jump to that thread — where
 *  the info panel's VoiceCallPanel shows the live transcript + steer controls. */
export default function ActiveCallsStrip({
  onOpenContact,
  activeContactId,
}: {
  onOpenContact: (contactId: number) => void
  activeContactId?: number | null
}) {
  const t = useT()
  const { calls } = useApp()
  const [now, setNow] = useState(0)

  const active = Object.values(calls)
    .filter((c) => ['dialing', 'ringing', 'live'].includes(c.status))
    .sort((a, b) => a.startedAt - b.startedAt)

  useEffect(() => {
    if (!active.length) return
    const id = setInterval(() => setNow(Date.now()), 1000)
    return () => clearInterval(id)
  }, [active.length])

  if (!active.length) return null

  const timer = (startedAt: number) => {
    const s = Math.max(0, Math.floor((now - startedAt) / 1000))
    return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`
  }

  return (
    <div className="no-scrollbar flex shrink-0 items-center gap-2 overflow-x-auto border-b border-line bg-surface px-3 py-2">
      <span className="eyebrow flex shrink-0 items-center gap-1.5 pr-1">
        <PhoneCall size={12} aria-hidden /> {t('chats.call.liveN', { count: active.length })}
      </span>
      {active.map((c) => {
        const openable = c.contactId != null
        const isActive = openable && c.contactId === activeContactId
        return (
          <button
            key={c.room}
            onClick={() => openable && onOpenContact(c.contactId as number)}
            disabled={!openable}
            title={t(openable ? 'chats.call.openChat' : 'chats.call.manualNoChat')}
            className={`flex shrink-0 items-center gap-2 rounded-lg border px-2.5 py-1.5 text-left transition-colors ${
              isActive ? 'border-line-strong bg-[var(--nav-active-bg)]' : 'border-line bg-card hover:bg-fill'
            } ${openable ? 'cursor-pointer' : 'cursor-default'}`}
          >
            <span className="relative grid h-7 w-7 shrink-0 place-items-center rounded-full bg-primary text-[10px] font-semibold text-on-primary">
              {initials(c.name || t('chats.call.contactFallback'))}
              <span
                className="absolute -bottom-px -right-px h-2.5 w-2.5 rounded-full"
                style={{ background: STATE_DOT[c.status], border: '2px solid var(--card)' }}
              />
            </span>
            <span className="min-w-0">
              <span className="block max-w-[120px] truncate text-[12px] font-bold text-text-high">
                {c.name || c.phone || t('chats.call.contactFallback')}
              </span>
              <span className="block text-[11px] text-text-muted">
                {c.status === 'live'
                  ? timer(c.startedAt)
                  : c.status === 'dialing'
                    ? t('chats.call.dialing')
                    : c.status === 'ringing'
                      ? t('chats.call.ringingPhone')
                      : c.statusText}
              </span>
            </span>
          </button>
        )
      })}
    </div>
  )
}
