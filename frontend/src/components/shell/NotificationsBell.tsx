'use client'

import Link from 'next/link'
import { useEffect, useRef, useState } from 'react'
import { useRouter } from 'next/navigation'
import { Bell, CheckCheck, MessageSquare, Phone, Settings2 } from 'lucide-react'
import {
  markAllNotificationsRead,
  markNotificationRead,
  useNotifications,
  type Notif,
} from './notificationsStore'
import { useFormat, useT } from '@/i18n'

const KIND_ICON = { message: MessageSquare, call: Phone, system: Settings2 }
const KIND_TINT = {
  message: 'bg-green/15 text-[color:var(--green-ink)]',
  call: 'bg-brand/15 text-[color:var(--brand-ink)]',
  system: 'bg-fill text-text-mid',
}

/** Per-user notification bell: a tangerine dot when something is unread, a
 *  dropdown of the latest, and "View all" into /notifications. Shares its data
 *  with the sidebar's Notifications counter (notificationsStore). */
export function NotificationsBell() {
  const t = useT()
  const f = useFormat()
  const router = useRouter()
  const { unread, items } = useNotifications()
  const [open, setOpen] = useState(false)
  const wrapRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!open) return
    const onClick = (e: MouseEvent) => {
      if (!wrapRef.current?.contains(e.target as Node)) setOpen(false)
    }
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && setOpen(false)
    document.addEventListener('mousedown', onClick)
    document.addEventListener('keydown', onKey)
    return () => {
      document.removeEventListener('mousedown', onClick)
      document.removeEventListener('keydown', onKey)
    }
  }, [open])

  const openItem = (n: Notif) => {
    setOpen(false)
    markNotificationRead(n)
    if (n.link) router.push(n.link)
  }

  return (
    <div ref={wrapRef} className="relative">
      <button
        onClick={() => setOpen(!open)}
        aria-label={unread ? t('shell.bell.ariaUnread', { count: unread }) : t('shell.nav.notifications')}
        aria-expanded={open}
        aria-haspopup="dialog"
        className="relative grid h-10 w-10 cursor-pointer place-items-center rounded-lg text-text-high transition-colors hover:bg-fill-hover"
      >
        <Bell size={18} strokeWidth={1.5} />
        {unread > 0 && (
          <span
            className="absolute right-[9px] top-[9px] h-2 w-2 rounded-full bg-accent ring-2 ring-[var(--app)]"
            aria-hidden
          />
        )}
      </button>

      {open && (
        <div
          role="dialog"
          aria-label={t('shell.nav.notifications')}
          className="fixed inset-x-3 top-[64px] z-50 animate-slideDown overflow-hidden rounded-xl border border-line bg-card shadow-pop sm:absolute sm:inset-x-auto sm:right-0 sm:top-[46px] sm:w-[360px]"
        >
          <div className="flex items-center justify-between gap-3 border-b border-line px-4 py-3">
            {/* the unread count drops under the title when the row is tight (long languages) */}
            <span className="display flex min-w-0 flex-wrap items-baseline gap-x-2 text-[18px] text-text-heading">
              <span>{t('shell.nav.notifications')}</span>
              {unread > 0 && (
                <span className="whitespace-nowrap font-sans text-[12px] font-semibold text-text-muted">
                  {t('shell.bell.unread', { count: unread })}
                </span>
              )}
            </span>
            <button
              onClick={() => void markAllNotificationsRead()}
              disabled={!unread}
              className="flex shrink-0 cursor-pointer items-center gap-1 text-[12px] font-semibold text-text-muted transition-colors hover:text-text-high disabled:cursor-default disabled:opacity-40"
            >
              <CheckCheck size={13} aria-hidden /> {t('shell.bell.markAllRead')}
            </button>
          </div>
          <div className="max-h-[min(380px,60vh)] overflow-y-auto">
            {items.length === 0 && (
              <div className="px-4 py-8 text-center text-[12.5px] text-text-muted">
                {t('shell.bell.empty')}
              </div>
            )}
            {items.map((n) => {
              const Icon = KIND_ICON[n.kind] ?? Settings2
              return (
                <button
                  key={n.id}
                  onClick={() => openItem(n)}
                  className="flex w-full cursor-pointer items-start gap-3 border-b border-line px-4 py-2.5 text-left transition-colors last:border-b-0 hover:bg-fill-hover"
                >
                  <span className={`mt-0.5 grid h-7 w-7 shrink-0 place-items-center rounded-full ${KIND_TINT[n.kind] ?? KIND_TINT.system}`}>
                    <Icon size={13} aria-hidden />
                  </span>
                  <span className="min-w-0 flex-1">
                    <span className="flex items-baseline justify-between gap-2">
                      <span className={`truncate text-[13px] ${n.read ? 'text-text-mid' : 'font-semibold text-text-high'}`}>
                        {n.title}
                      </span>
                      {!n.read && <span className="h-1.5 w-1.5 shrink-0 rounded-full bg-accent" aria-label={t('shell.bell.unreadDot')} />}
                    </span>
                    {n.body && <span className="block truncate text-[12px] text-text-muted">{n.body}</span>}
                    <span className="block text-[11px] text-text-faint">{f.dateTime(n.created_at)}</span>
                  </span>
                </button>
              )
            })}
          </div>
          <Link
            href="/notifications"
            onClick={() => setOpen(false)}
            className="block border-t border-line px-4 py-2.5 text-center text-[12.5px] font-semibold text-[color:var(--brand-ink)] transition-colors hover:bg-fill-hover"
          >
            {t('shell.bell.viewAll')}
          </Link>
        </div>
      )}
    </div>
  )
}
