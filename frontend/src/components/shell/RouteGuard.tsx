'use client'

import Link from 'next/link'
import { Lock } from 'lucide-react'
import type { ReactNode } from 'react'
import type { AuthUser } from '@/lib/auth'
import { useT } from '@/i18n'
import { canSee, firstVisibleHref, routeDef, visiblePages } from './nav'

/**
 * Pages mount only for people allowed to open them. A typed or bookmarked URL
 * for a page outside the caller's capabilities renders this panel INSTEAD of
 * the page — so its data requests never fire — and nothing at all renders
 * until the session has been read (the first client paint has no user yet).
 * The server still refuses the data on its own; this keeps the screen honest.
 */
export function RouteGuard({
  user,
  ready,
  pathname,
  children,
}: {
  user: AuthUser | null
  ready: boolean
  pathname: string
  children: ReactNode
}) {
  const t = useT()
  if (!ready) return <div className="min-h-0 flex-1" aria-busy="true" />
  const def = routeDef(pathname)
  if (!def || canSee(user, def)) return <>{children}</>

  const next = firstVisibleHref(user)
  const nextDef = visiblePages(user).find((p) => p.href === next)
  const nextLabel = t(nextDef?.lbl ?? 'shell.nav.settings')
  const need = def.cap ? ([] as string[]).concat(def.cap) : []
  return (
    <div className="flex h-full min-h-[50vh] flex-1 items-center justify-center p-6">
      <div role="alert" className="w-full max-w-md rounded-2xl border border-line bg-card p-6 text-center">
        <span className="mx-auto grid h-11 w-11 place-items-center rounded-full bg-fill">
          <Lock size={18} className="text-text-muted" aria-hidden />
        </span>
        <h1 className="display mt-3 text-[22px] text-text-heading">{t('shell.guard.title', { page: t(def.lbl) })}</h1>
        <p className="mt-2 text-[13px] leading-relaxed text-text-muted">
          {t('shell.guard.body', { caps: need.join(' / ') || '—' })}
        </p>
        <Link
          href={next}
          className="mt-4 inline-flex items-center justify-center rounded-lg bg-primary px-4 py-2 text-[13px] font-semibold text-on-primary hover:opacity-90"
        >
          {t('shell.guard.goTo', { page: nextLabel })}
        </Link>
      </div>
    </div>
  )
}
