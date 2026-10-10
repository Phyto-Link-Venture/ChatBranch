'use client'

/**
 * "Install ChatBranch" — the nudge to put the app on the home screen.
 *
 * - Android / desktop Chrome & Edge: the browser's own install prompt, behind
 *   an Install button (`beforeinstallprompt`, parked in pwa.ts).
 * - iPhone / iPad: browsers there never offer to install, so the card shows the
 *   two Share-sheet steps instead.
 * Shown once a person is signed in, never inside the installed app; "Not now"
 * hides it for 30 days (per browser). The sidebar's "Install app" reopens it
 * any time (`window` event `pwa:show-install`).
 * It never stacks with a page guide: it waits while a guide (or its replay tip)
 * is on screen, and does not open by itself on the first visit to a page whose
 * guide starts by itself. Only its buttons take touches — a swipe over the card
 * still scrolls the page under it.
 */

import { useEffect, useState } from 'react'
import { Download, Share, SquarePlus, X } from 'lucide-react'
import { useT } from '@/i18n'
import { getAuth } from '@/lib/auth'
import { tourSeen, toursAuto } from '@/lib/tours'
import { useTour } from '@/components/tour/TourProvider'
import { useInstallPrompt } from './pwa'
import { BranchMark } from './Logo'

const KEY = 'pwa-install-dismissed'
const SNOOZE_MS = 30 * 24 * 3600 * 1000
export const SHOW_INSTALL_EVENT = 'pwa:show-install'

function snoozed(): boolean {
  try {
    const at = Number(localStorage.getItem(KEY) || 0)
    return at > 0 && Date.now() - at < SNOOZE_MS
  } catch {
    return false
  }
}

export function InstallHint() {
  const t = useT()
  const { canInstall, iosHint, install } = useInstallPrompt()
  const [open, setOpen] = useState(false)
  const { busy, pageTour } = useTour()

  // show by itself once the browser can install (or on iOS), unless snoozed —
  // and not on the first visit to a page whose guide is about to start
  useEffect(() => {
    if (!(canInstall || iosHint) || snoozed()) return
    const user = getAuth()?.username
    if (pageTour && user && toursAuto(user) && !tourSeen(user, pageTour.id)) return
    const timer = window.setTimeout(() => setOpen(true), 4000) // let the page settle first
    return () => window.clearTimeout(timer)
  }, [canInstall, iosHint, pageTour])

  useEffect(() => {
    const show = () => setOpen(true)
    window.addEventListener(SHOW_INSTALL_EVENT, show)
    return () => window.removeEventListener(SHOW_INSTALL_EVENT, show)
  }, [])

  if (!open || busy || (!canInstall && !iosHint)) return null

  const dismiss = () => {
    setOpen(false)
    try {
      localStorage.setItem(KEY, String(Date.now()))
    } catch {
      /* storage blocked — it simply shows again next visit */
    }
  }

  return (
    <div
      role="dialog"
      aria-label={t('shell.install.title')}
      className="print-hide pointer-events-none fixed inset-x-3 bottom-[calc(76px+env(safe-area-inset-bottom))] z-[70] rounded-2xl border border-line-strong bg-card p-4 shadow-pop lg:inset-x-auto lg:bottom-4 lg:right-4 lg:w-[360px] [&_button]:pointer-events-auto"
    >
      <div className="flex items-start gap-3">
        <span className="grid h-11 w-11 shrink-0 place-items-center rounded-xl bg-fill">
          <BranchMark size={26} />
        </span>
        <div className="min-w-0 flex-1">
          <div className="text-[14px] font-semibold text-text-heading">{t('shell.install.title')}</div>
          <p className="mt-0.5 text-[12.5px] leading-snug text-text-mid">
            {iosHint && !canInstall ? t('shell.install.iosBody') : t('shell.install.body')}
          </p>
        </div>
        <button
          type="button"
          onClick={dismiss}
          aria-label={t('shell.install.notNow')}
          className="-mr-1 -mt-1 grid h-8 w-8 shrink-0 place-items-center rounded-lg text-text-muted hover:bg-fill-hover hover:text-text-high"
        >
          <X size={15} aria-hidden />
        </button>
      </div>

      {iosHint && !canInstall ? (
        <ol className="mt-3 flex flex-col gap-2 text-[12.5px] text-text-high">
          <li className="flex items-center gap-2 rounded-lg bg-fill-subtle px-3 py-2">
            <span className="font-semibold">1.</span>
            <span className="min-w-0 flex-1">{t('shell.install.iosStep1')}</span>
            <Share size={16} className="shrink-0 text-[var(--blue-ink)]" aria-hidden />
          </li>
          <li className="flex items-center gap-2 rounded-lg bg-fill-subtle px-3 py-2">
            <span className="font-semibold">2.</span>
            <span className="min-w-0 flex-1">{t('shell.install.iosStep2')}</span>
            <SquarePlus size={16} className="shrink-0 text-text-high" aria-hidden />
          </li>
          <li className="flex items-center gap-2 rounded-lg bg-fill-subtle px-3 py-2">
            <span className="font-semibold">3.</span>
            <span className="min-w-0 flex-1">{t('shell.install.iosStep3')}</span>
          </li>
        </ol>
      ) : (
        <div className="mt-3 flex justify-end gap-2">
          <button
            type="button"
            onClick={dismiss}
            className="h-9 rounded-lg px-3 text-[12.5px] font-semibold text-text-muted hover:bg-fill-hover hover:text-text-high"
          >
            {t('shell.install.notNow')}
          </button>
          <button
            type="button"
            onClick={async () => {
              await install()
              setOpen(false)
            }}
            className="inline-flex h-9 items-center gap-1.5 rounded-lg bg-primary px-3.5 text-[12.5px] font-semibold text-on-primary hover:opacity-90"
          >
            <Download size={14} aria-hidden /> {t('shell.install.install')}
          </button>
        </div>
      )}
    </div>
  )
}
