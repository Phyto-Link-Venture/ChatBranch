'use client'

import { useCallback, useEffect, useRef, useState } from 'react'
import { useRouter } from 'next/navigation'
import { Bell, CheckCheck, MessageSquare, Phone, Settings2 } from 'lucide-react'
import { authHeaders } from '@/lib/auth'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8000/api'

interface Notif {
  id: number
  kind: 'message' | 'call' | 'system'
  title: string
  body: string
  link: string
  read: boolean
  created_at: string
}

const KIND_ICON = { message: MessageSquare, call: Phone, system: Settings2 }

const timeShort = (ts: string) =>
  new Date(ts).toLocaleString([], { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' })

/** Per-user notification bell: unread badge + dropdown, polled every 20 s. */
export function NotificationsBell() {
  const router = useRouter()
  const [open, setOpen] = useState(false)
  const [unread, setUnread] = useState(0)
  const [items, setItems] = useState<Notif[]>([])
  const wrapRef = useRef<HTMLDivElement>(null)

  const refresh = useCallback(async () => {
    try {
      const res = await fetch(`${API}/auth/notifications`, { headers: authHeaders() })
      if (!res.ok) return
      const data = await res.json()
      setUnread(data.unread ?? 0)
      setItems(data.notifications ?? [])
    } catch {
      /* API down — badge just stays stale */
    }
  }, [])

  useEffect(() => {
    refresh()
    const t = setInterval(refresh, 20000)
    return () => clearInterval(t)
  }, [refresh])

  // close on outside click
  useEffect(() => {
    if (!open) return
    const onClick = (e: MouseEvent) => {
      if (!wrapRef.current?.contains(e.target as Node)) setOpen(false)
    }
    document.addEventListener('mousedown', onClick)
    return () => document.removeEventListener('mousedown', onClick)
  }, [open])

  const markAll = async () => {
    await fetch(`${API}/auth/notifications`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', ...authHeaders() },
      body: JSON.stringify({ id: 'all' }),
    }).catch(() => {})
    refresh()
  }

  const openItem = async (n: Notif) => {
    setOpen(false)
    fetch(`${API}/auth/notifications`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', ...authHeaders() },
      body: JSON.stringify({ id: n.id }),
    }).catch(() => {})
    setItems((xs) => xs.map((x) => (x.id === n.id ? { ...x, read: true } : x)))
    setUnread((u) => Math.max(0, u - (n.read ? 0 : 1)))
    if (n.link) router.push(n.link)
  }

  return (
    <div ref={wrapRef} className="relative">
      <button
        onClick={() => setOpen(!open)}
        aria-label={`Notifications${unread ? ` (${unread} unread)` : ''}`}
        aria-expanded={open}
        className="relative flex h-[34px] w-[34px] cursor-pointer items-center justify-center rounded-lg border border-line bg-fill text-text-muted transition-colors hover:text-text-high"
      >
        <Bell size={15} strokeWidth={1.75} />
        {unread > 0 && (
          <span
            className="absolute -right-1 -top-1 flex h-[16px] min-w-[16px] items-center justify-center rounded-full px-1 text-[9px] font-bold text-white"
            style={{ background: '#EF4444', border: '1.5px solid var(--surface)' }}
          >
            {unread > 9 ? '9+' : unread}
          </span>
        )}
      </button>

      {open && (
        <div className="absolute right-0 top-[42px] z-50 w-[340px] overflow-hidden rounded-xl border border-line bg-card shadow-2xl">
          <div className="flex items-center justify-between border-b border-line px-3.5 py-2.5">
            <span className="text-[12.5px] font-bold text-text-high">Notifications</span>
            <button
              onClick={markAll}
              disabled={!unread}
              className="flex cursor-pointer items-center gap-1 text-[11px] font-semibold text-text-muted transition-colors hover:text-brand-300 disabled:opacity-40"
            >
              <CheckCheck size={12} aria-hidden /> Mark all read
            </button>
          </div>
          <div className="max-h-[380px] overflow-y-auto">
            {items.length === 0 && (
              <div className="px-4 py-8 text-center text-[12px] text-text-muted">
                Nothing yet — customer replies and system events land here.
              </div>
            )}
            {items.map((n) => {
              const Icon = KIND_ICON[n.kind] ?? Settings2
              return (
                <button
                  key={n.id}
                  onClick={() => openItem(n)}
                  className={`flex w-full cursor-pointer items-start gap-2.5 border-b border-line px-3.5 py-2.5 text-left transition-colors hover:bg-fill ${
                    n.read ? 'opacity-60' : ''
                  }`}
                >
                  <span
                    className={`mt-0.5 grid h-7 w-7 shrink-0 place-items-center rounded-lg ${
                      n.kind === 'message' ? 'bg-green/15 text-green-300'
                      : n.kind === 'call' ? 'bg-brand/15 text-brand-300'
                      : 'bg-purple/15 text-purple-300'
                    }`}
                  >
                    <Icon size={13} aria-hidden />
                  </span>
                  <span className="min-w-0 flex-1">
                    <span className="flex items-baseline justify-between gap-2">
                      <span className="truncate text-[12px] font-semibold text-text-high">{n.title}</span>
                      {!n.read && <span className="h-1.5 w-1.5 shrink-0 rounded-full bg-brand-400" />}
                    </span>
                    {n.body && <span className="block truncate text-[11px] text-text-muted">{n.body}</span>}
                    <span className="block text-[10px] text-text-faint">{timeShort(n.created_at)}</span>
                  </span>
                </button>
              )
            })}
          </div>
        </div>
      )}
    </div>
  )
}
