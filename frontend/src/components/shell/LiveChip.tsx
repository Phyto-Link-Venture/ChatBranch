'use client'

import Link from 'next/link'
import { useEffect, useRef, useState } from 'react'
import { usePathname, useRouter } from 'next/navigation'
import { ArrowUpRight, PhoneCall } from 'lucide-react'
import { useEntityQuery } from '@/lib/EntityContext'
import { ChannelLogo } from '@/components/ui/ChannelLogo'
import { secondsSince, shortAge, useLive, useStreamStatus } from './liveStore'
import { AiQuickToggle } from '@/components/ai-controls/AiQuickToggle'

const DOT: Record<string, { cls: string; label: string }> = {
  connected: { cls: 'bg-green', label: 'Live — connected' },
  reconnecting: { cls: 'bg-amber', label: 'Live — reconnecting' },
  offline: { cls: 'bg-text-faint', label: 'Offline — updates paused' },
}

const CALL_STATUS: Record<string, string> = {
  connecting: 'Connecting',
  ringing: 'Ringing',
  in_call: 'In call',
}

/**
 * The top bar's Live chip. The dot is the realtime connection itself (the
 * inbox SSE stream — green connected, amber reconnecting, grey offline); the
 * text is what's live right now ("2 calls · 5 waiting"). Click for the
 * popover: active calls, conversations waiting for a reply (oldest first)
 * and a short feed of recent events, each a link into the right screen.
 */
export function LiveChip() {
  const entityQ = useEntityQuery()
  const { data } = useLive(entityQ)
  const status = useStreamStatus()
  const [open, setOpen] = useState(false)
  const wrap = useRef<HTMLDivElement>(null)
  const router = useRouter()
  const pathname = usePathname()

  // Tick so "waiting 4 min" ages while the popover is open.
  const [, setNow] = useState(0)
  useEffect(() => {
    if (!open) return
    const t = setInterval(() => setNow((n) => n + 1), 30_000)
    return () => clearInterval(t)
  }, [open])

  useEffect(() => {
    if (!open) return
    const onDoc = (e: MouseEvent) => {
      if (!wrap.current?.contains(e.target as Node)) setOpen(false)
    }
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && setOpen(false)
    document.addEventListener('mousedown', onDoc)
    document.addEventListener('keydown', onKey)
    return () => {
      document.removeEventListener('mousedown', onDoc)
      document.removeEventListener('keydown', onKey)
    }
  }, [open])

  useEffect(() => setOpen(false), [pathname])

  const calls = data?.active_call_count ?? 0
  const waiting = data?.waiting_count ?? 0
  const dot = DOT[status] ?? DOT.offline
  const summary = [
    calls > 0 ? `${calls} ${calls === 1 ? 'call' : 'calls'}` : null,
    `${waiting} waiting`,
  ]
    .filter(Boolean)
    .join(' · ')

  // The inbox only reads its ?channel=&key= deep link on mount, so a jump to
  // another chat while already on /chats needs a real navigation.
  const go = (url: string) => {
    setOpen(false)
    if (pathname === '/chats' && url.startsWith('/chats?')) window.location.assign(url)
    else router.push(url)
  }

  return (
    <div ref={wrap} className="relative">
      <button
        type="button"
        onClick={() => setOpen((v) => !v)}
        aria-expanded={open}
        aria-haspopup="dialog"
        aria-label={`${dot.label}. ${summary}`}
        title={dot.label}
        className="flex h-9 cursor-pointer items-center gap-2 rounded-full border border-line bg-card px-3 text-[13px] font-semibold text-text-high transition-colors hover:border-line-strong"
      >
        <span className={`relative h-2 w-2 shrink-0 rounded-full ${dot.cls}`} aria-hidden>
          {status === 'connected' && (
            <span className={`absolute inset-0 animate-ping rounded-full ${dot.cls} opacity-40`} />
          )}
        </span>
        Live
        {data && <span className="hidden font-normal text-text-muted sm:inline">· {summary}</span>}
      </button>

      {open && (
        <div
          role="dialog"
          aria-label="Live activity"
          className="fixed inset-x-3 top-[64px] z-50 max-h-[78vh] animate-slideDown overflow-y-auto rounded-xl border border-line bg-card shadow-pop sm:absolute sm:inset-x-auto sm:right-0 sm:top-[44px] sm:w-[380px]"
        >
          <div className="flex items-baseline justify-between gap-3 border-b border-line px-4 py-3">
            <span className="display text-[18px] text-text-heading">Live</span>
            <span className="text-[12px] text-text-muted">{dot.label}</span>
          </div>
          <AiQuickToggle />

          <Section title="Active calls" count={calls}>
            {(data?.active_calls ?? []).length === 0 ? (
              <Empty>No calls in progress.</Empty>
            ) : (
              data!.active_calls.map((c) => (
                <RowButton key={`${c.channel}:${c.key}`} onClick={() => go(c.url)}>
                  <span className="grid h-7 w-7 shrink-0 place-items-center rounded-full bg-green/15 text-[color:var(--green-ink)]">
                    <PhoneCall size={13} strokeWidth={1.9} aria-hidden />
                  </span>
                  <span className="min-w-0 flex-1">
                    <span className="block truncate font-semibold text-text-high">{c.name}</span>
                    <span className="flex items-center gap-1.5 text-[12px] text-text-muted">
                      <ChannelLogo channel={c.channel} size={12} /> {CALL_STATUS[c.status] ?? c.status}
                    </span>
                  </span>
                  <span className="shrink-0 text-[12px] tabular-nums text-text-muted">
                    {shortAge(secondsSince(c.since))}
                  </span>
                </RowButton>
              ))
            )}
          </Section>

          <Section title="Waiting for a reply" count={waiting}>
            {(data?.waiting ?? []).length === 0 ? (
              <Empty>Nobody is waiting — every conversation has an answer.</Empty>
            ) : (
              data!.waiting.map((w) => {
                const secs = secondsSince(w.waiting_since)
                return (
                  <RowButton key={`${w.channel}:${w.key}`} onClick={() => go(w.url)}>
                    <ChannelLogo channel={w.channel} size={16} />
                    <span className="min-w-0 flex-1">
                      <span className="block truncate font-semibold text-text-high">{w.name}</span>
                      {w.preview && <span className="block truncate text-[12px] text-text-muted">{w.preview}</span>}
                    </span>
                    <span
                      className={`shrink-0 text-[12px] font-semibold tabular-nums ${
                        secs >= 600 ? 'text-[color:var(--brand-ink)]' : 'text-text-muted'
                      }`}
                    >
                      {shortAge(secs)}
                    </span>
                  </RowButton>
                )
              })
            )}
            {waiting > (data?.waiting.length ?? 0) && (
              <div className="px-4 pb-1 pt-1 text-[12px] text-text-muted">
                + {waiting - (data?.waiting.length ?? 0)} more in the inbox
              </div>
            )}
          </Section>

          <Section title="Recent">
            {(data?.recent ?? []).length === 0 ? (
              <Empty>Nothing yet.</Empty>
            ) : (
              data!.recent.map((r) => (
                <RowButton key={r.id} onClick={() => go(r.url)} dense>
                  <span className="mt-[3px] w-4 shrink-0">
                    {r.kind === 'message' && r.channel ? (
                      <ChannelLogo channel={r.channel} size={13} />
                    ) : (
                      <span className="mx-auto block h-1.5 w-1.5 rounded-full bg-text-faint" aria-hidden />
                    )}
                  </span>
                  <span className="min-w-0 flex-1">
                    <span className="block truncate text-[12.5px] text-text-high">
                      <span className="font-semibold">{r.title}</span>
                      {r.text && <span className="text-text-muted"> — {r.text}</span>}
                    </span>
                  </span>
                  <span className="shrink-0 text-[11.5px] tabular-nums text-text-faint">
                    {shortAge(secondsSince(r.at))}
                  </span>
                </RowButton>
              ))
            )}
          </Section>

          <div className="flex items-center justify-between border-t border-line px-4 py-2.5 text-[12.5px]">
            <span className="text-text-muted">Updates as it happens</span>
            <Link
              href="/chats"
              onClick={() => setOpen(false)}
              className="inline-flex items-center gap-1 font-semibold text-[color:var(--brand-ink)] hover:underline"
            >
              Open chats <ArrowUpRight size={13} aria-hidden />
            </Link>
          </div>
        </div>
      )}
    </div>
  )
}

function Section({ title, count, children }: { title: string; count?: number; children: React.ReactNode }) {
  return (
    <div className="border-b border-line py-2 last:border-b-0">
      <div className="flex items-center justify-between px-4 pb-1 pt-1">
        <span className="eyebrow">{title}</span>
        {count !== undefined && <span className="text-[12px] tabular-nums text-text-muted">{count}</span>}
      </div>
      {children}
    </div>
  )
}

function RowButton({ children, onClick, dense }: { children: React.ReactNode; onClick: () => void; dense?: boolean }) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={`flex w-full cursor-pointer gap-2.5 px-4 text-left text-[13px] transition-colors hover:bg-fill-hover ${
        dense ? 'items-start py-1.5' : 'items-center py-2'
      }`}
    >
      {children}
    </button>
  )
}

function Empty({ children }: { children: React.ReactNode }) {
  return <div className="px-4 py-2 text-[12.5px] text-text-muted">{children}</div>
}
