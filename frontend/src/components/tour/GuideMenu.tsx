'use client'

import { useEffect, useRef, useState } from 'react'
import { BookOpenText, CircleHelp, Compass, ListChecks, PlayCircle, Sparkles } from 'lucide-react'
import Link from 'next/link'
import { usePathname } from 'next/navigation'
import { Toggle } from '@/components/ui/Toggle'
import { getAuth } from '@/lib/auth'
import { setToursAuto, toursAuto, tourKey } from '@/lib/tours'
import { guideKey, guidesFor } from '@/lib/guides'
import { useT } from '@/i18n'
import { useTour } from './TourProvider'
import { useUpdates } from '@/lib/updates'

/**
 * The top bar's Guide button: replay this page's guide, open the step-by-step
 * how-to guides done on this page, take the tour of the app, open the manual,
 * or stop guides from starting by themselves.
 */
export function GuideMenu() {
  const t = useT()
  const { pageTour, start } = useTour()
  const pathname = usePathname() || '/'
  const howTos = guidesFor(pathname).slice(0, 6)
  const [open, setOpen] = useState(false)
  const [auto, setAuto] = useState(true)
  const ref = useRef<HTMLDivElement>(null)
  const { status: updates } = useUpdates()

  useEffect(() => {
    const user = getAuth()?.username
    if (user) setAuto(toursAuto(user))
  }, [])

  useEffect(() => {
    if (!open) return
    const onDown = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false)
    }
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && setOpen(false)
    document.addEventListener('mousedown', onDown)
    document.addEventListener('keydown', onKey)
    return () => {
      document.removeEventListener('mousedown', onDown)
      document.removeEventListener('keydown', onKey)
    }
  }, [open])

  const item =
    'flex w-full items-center gap-2.5 rounded-lg px-2.5 py-2 text-left text-[13px] text-text-high hover:bg-fill-hover'

  return (
    <div ref={ref} className="relative" data-tour="guide">
      <button
        type="button"
        onClick={() => setOpen((v) => !v)}
        aria-label={t('guide.ui.button')}
        aria-expanded={open}
        title={t('guide.ui.button')}
        className={`grid h-10 w-10 place-items-center rounded-lg transition-colors ${
          open ? 'bg-fill-hover text-text-heading' : 'text-text-high hover:bg-fill-hover'
        }`}
      >
        <CircleHelp size={18} strokeWidth={1.7} aria-hidden />
      </button>
      {open && (
        <div className="absolute right-0 top-[calc(100%+6px)] z-[60] w-[300px] rounded-xl border border-line-strong bg-card p-1.5 shadow-pop">
          {pageTour && (
            <button
              type="button"
              className={item}
              onClick={() => {
                setOpen(false)
                start()
              }}
            >
              <PlayCircle size={15} className="shrink-0 text-[var(--brand-ink)]" aria-hidden />
              <span className="min-w-0">
                <span className="block font-semibold">{t('guide.ui.thisPage')}</span>
                <span className="block truncate text-[11.5px] text-text-muted">{t(tourKey(pageTour.id, 'title'))}</span>
              </span>
            </button>
          )}
          {howTos.length > 0 && (
            <div className="my-1 border-y border-line py-1">
              <div className="px-2.5 pb-1 pt-1.5 text-[10.5px] font-bold uppercase tracking-[0.06em] text-text-muted">
                {t('manual.ui.forThisPage')}
              </div>
              {howTos.map((g) => (
                <a
                  key={g.id}
                  href={`/help/manual#${g.id}`}
                  className={item}
                  onClick={() => setOpen(false)}
                >
                  <ListChecks size={15} className="shrink-0 text-text-muted" aria-hidden />
                  <span className="min-w-0 truncate">{t(guideKey(g.id, 'title'))}</span>
                </a>
              ))}
            </div>
          )}
          <button
            type="button"
            className={item}
            onClick={() => {
              setOpen(false)
              start('shell')
            }}
          >
            <Compass size={15} className="shrink-0 text-text-muted" aria-hidden />
            {t('guide.ui.appTour')}
          </button>
          <Link href="/help/manual" className={item} onClick={() => setOpen(false)}>
            <BookOpenText size={15} className="shrink-0 text-text-muted" aria-hidden />
            {t('guide.ui.manual')}
          </Link>
          <Link href="/whats-new" className={item} onClick={() => setOpen(false)}>
            <Sparkles size={15} className="shrink-0 text-text-muted" aria-hidden />
            <span className="min-w-0">
              <span className="block">{t('updates.menu')}</span>
              {updates && (
                <span className="block text-[11.5px] text-text-muted">{t('updates.menuVersion', { version: updates.mine })}</span>
              )}
            </span>
          </Link>
          <div className="mt-1 flex items-center justify-between gap-3 border-t border-line px-2.5 pb-1 pt-2.5">
            <span className="text-[12px] text-text-mid">{t('guide.ui.autoStart')}</span>
            <Toggle
              on={auto}
              onChange={(v) => {
                setAuto(v)
                const user = getAuth()?.username
                if (user) setToursAuto(user, v)
              }}
              label={t('guide.ui.autoStart')}
            />
          </div>
        </div>
      )}
    </div>
  )
}
