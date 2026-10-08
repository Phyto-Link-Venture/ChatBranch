'use client'

import { useEffect, useState } from 'react'
import { PhoneCall } from 'lucide-react'
import { useApp } from '@/lib/AppContext'
import { initials } from '@/lib/mocks'

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
    <div className="no-scrollbar flex items-center gap-2 overflow-x-auto rounded-lg border border-line bg-card px-2.5 py-2">
      <span className="flex shrink-0 items-center gap-1.5 pr-1 text-[11px] font-bold uppercase tracking-[0.06em] text-text-muted">
        <PhoneCall size={12} aria-hidden /> {active.length} live
      </span>
      {active.map((c) => {
        const openable = c.contactId != null
        const isActive = openable && c.contactId === activeContactId
        return (
          <button
            key={c.room}
            onClick={() => openable && onOpenContact(c.contactId as number)}
            disabled={!openable}
            title={openable ? 'Open this chat' : 'Manual call — no linked chat'}
            className="flex shrink-0 items-center gap-2 rounded-lg px-2.5 py-1.5 text-left transition-colors disabled:cursor-default"
            style={{
              background: isActive ? 'rgba(255,90,31,0.12)' : 'var(--fill-subtle)',
              border: `1px solid ${isActive ? 'rgba(255,90,31,0.35)' : 'var(--line)'}`,
              cursor: openable ? 'pointer' : 'default',
            }}
          >
            <span
              className="relative grid h-7 w-7 shrink-0 place-items-center rounded-full text-[10px] font-bold text-white"
              style={{ background: 'linear-gradient(135deg,#1E40AF,rgb(var(--c-brand)))' }}
            >
              {initials(c.name || 'Contact')}
              <span
                className="absolute -bottom-px -right-px h-2.5 w-2.5 rounded-full"
                style={{ background: STATE_DOT[c.status], border: '2px solid var(--card)' }}
              />
            </span>
            <span className="min-w-0">
              <span className="block max-w-[120px] truncate text-[12px] font-bold text-text-high">
                {c.name || c.phone || 'Contact'}
              </span>
              <span className="block text-[10.5px] font-semibold text-text-mid">
                {c.status === 'live' ? timer(c.startedAt) : c.statusText}
              </span>
            </span>
          </button>
        )
      })}
    </div>
  )
}
