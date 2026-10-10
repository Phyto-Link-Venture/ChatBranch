'use client'

/**
 * The page guide: a spotlight over one element at a time with a short card —
 * Back / Next / Skip, "2 of 5". Tours are defined in lib/tours.ts.
 *
 * - Starts by itself the FIRST time someone opens a page that has a tour
 *   (remembered per user in localStorage; the Guide menu can switch that off).
 * - The top bar's Guide button replays the current page's tour any time.
 * - `?tour=<id>` starts a tour explicitly — the public manual's "Show me".
 * Steps whose target isn't on screen are skipped; nothing here ever blocks the
 * page (Esc or a click on the dimmed area closes it).
 */

import { createContext, useCallback, useContext, useEffect, useLayoutEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { usePathname } from 'next/navigation'
import { CircleHelp, X } from 'lucide-react'
import { getAuth } from '@/lib/auth'
import { markTourSeen, stepKey, tourById, tourFor, tourSeen, toursAuto, type TourDef } from '@/lib/tours'
import { useT } from '@/i18n'

interface TourState {
  /** The current page's tour, if it has one. */
  pageTour: TourDef | undefined
  start: (id?: string) => void
  running: boolean
  /** A guide or its "replay it from ?" tip is on screen (other nudges wait). */
  busy: boolean
}

const Ctx = createContext<TourState>({ pageTour: undefined, start: () => {}, running: false, busy: false })
export const useTour = () => useContext(Ctx)

const PAD = 6

function findTarget(target?: string): HTMLElement | null {
  if (!target) return null
  const all = Array.from(document.querySelectorAll<HTMLElement>(`[data-tour="${target}"]`))
  // the first one actually on screen (a hidden mobile/desktop twin doesn't count)
  return all.find((el) => el.getClientRects().length > 0 && el.offsetWidth + el.offsetHeight > 0) ?? null
}

export function TourProvider({ children }: { children: ReactNode }) {
  const pathname = usePathname() || '/'
  const pageTour = useMemo(() => tourFor(pathname), [pathname])
  const [tour, setTour] = useState<TourDef | null>(null)
  const [index, setIndex] = useState(0)
  // after a guide closes: a short "replay it any time from the ? button" reminder
  const [replayTip, setReplayTip] = useState(false)
  useEffect(() => {
    if (!replayTip) return
    const timer = window.setTimeout(() => setReplayTip(false), 6000)
    return () => window.clearTimeout(timer)
  }, [replayTip])

  const stop = useCallback(() => {
    if (tour) {
      const user = getAuth()?.username
      if (user) markTourSeen(user, tour.id)
      // the reminder only for the first few guides — after that people know
      try {
        const n = Number(localStorage.getItem('tour-replay-tip') || 0)
        if (n < 3) {
          localStorage.setItem('tour-replay-tip', String(n + 1))
          setReplayTip(true)
        }
      } catch {
        /* storage blocked: the last step already says it */
      }
    }
    setTour(null)
  }, [tour])

  const start = useCallback(
    (id?: string) => {
      const def = id ? tourById(id) : pageTour
      if (!def) return
      setIndex(0)
      setTour(def)
    },
    [pageTour],
  )

  // ?tour=<id> (the manual's "Show me") — once per navigation
  useEffect(() => {
    const id = new URLSearchParams(window.location.search).get('tour')
    if (!id) return
    const def = id === '1' ? tourFor(pathname) : tourById(id)
    if (!def) return
    const timer = window.setTimeout(() => start(def.id), 900)
    return () => window.clearTimeout(timer)
  }, [pathname, start])

  // first visit: start by itself, a moment after the page has drawn
  useEffect(() => {
    setTour(null)
    if (!pageTour) return
    if (new URLSearchParams(window.location.search).get('tour')) return
    const user = getAuth()?.username
    if (!user || !toursAuto(user) || tourSeen(user, pageTour.id)) return
    const timer = window.setTimeout(() => {
      // the sign-in sync may have brought this guide's "seen" from another device meanwhile
      if (!toursAuto(user) || tourSeen(user, pageTour.id)) return
      if (pageTour.steps.some((s) => !s.target || findTarget(s.target))) start(pageTour.id)
    }, 1600)
    return () => window.clearTimeout(timer)
  }, [pageTour, start])

  const value = useMemo(
    () => ({ pageTour, start, running: tour != null, busy: tour != null || replayTip }),
    [pageTour, start, tour, replayTip],
  )

  return (
    <Ctx.Provider value={value}>
      {children}
      {tour && <TourOverlay tour={tour} index={index} setIndex={setIndex} onClose={stop} />}
      {!tour && replayTip && <ReplayTip onClose={() => setReplayTip(false)} />}
    </Ctx.Provider>
  )
}

function TourOverlay({
  tour,
  index,
  setIndex,
  onClose,
}: {
  tour: TourDef
  index: number
  setIndex: (n: number) => void
  onClose: () => void
}) {
  const t = useT()
  const cardRef = useRef<HTMLDivElement>(null)
  const [rect, setRect] = useState<DOMRect | null>(null)
  const [cardPos, setCardPos] = useState<{ top: number; left: number } | null>(null)

  // the steps that can show right now (targets on screen, or centred cards)
  const visible = useMemo(
    () => tour.steps.map((s, i) => ({ s, n: i + 1 })).filter(({ s }) => !s.target || findTarget(s.target)),
    // re-evaluated whenever the step changes (a step can reveal the next one's target)
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [tour, index],
  )
  const current = visible[Math.min(index, visible.length - 1)]
  const last = index >= visible.length - 1

  // measure the target (and follow it while the page scrolls or resizes)
  useLayoutEffect(() => {
    if (!current) return
    const el = findTarget(current.s.target)
    if (el) el.scrollIntoView({ block: 'nearest', inline: 'nearest' })
    const measure = () => setRect(el ? el.getBoundingClientRect() : null)
    measure()
    const id = window.setInterval(measure, 250)
    window.addEventListener('resize', measure)
    return () => {
      window.clearInterval(id)
      window.removeEventListener('resize', measure)
    }
  }, [current])

  // place the card beside the spotlight, inside the viewport
  useLayoutEffect(() => {
    const card = cardRef.current
    if (!card) return
    const cw = card.offsetWidth
    const ch = card.offsetHeight
    const vw = window.innerWidth
    const vh = window.innerHeight
    if (!rect) {
      setCardPos({ top: Math.max(16, (vh - ch) / 2), left: Math.max(16, (vw - cw) / 2) })
      return
    }
    const below = rect.bottom + PAD + 10
    const above = rect.top - PAD - 10 - ch
    let top = below + ch < vh - 12 ? below : above > 12 ? above : Math.min(vh - ch - 12, Math.max(12, rect.top))
    let left = Math.min(vw - cw - 12, Math.max(12, rect.left + rect.width / 2 - cw / 2))
    // a tall target fills the screen: sit the card beside it instead
    if (top === Math.min(vh - ch - 12, Math.max(12, rect.top)) && rect.width < vw - cw - 40) {
      left = rect.right + PAD + 12 + cw < vw ? rect.right + PAD + 12 : Math.max(12, rect.left - PAD - 12 - cw)
      top = Math.min(vh - ch - 12, Math.max(12, rect.top))
    }
    setCardPos({ top, left })
  }, [rect, index])

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose()
      if (e.key === 'ArrowRight') last ? onClose() : setIndex(index + 1)
      if (e.key === 'ArrowLeft' && index > 0) setIndex(index - 1)
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [index, last, onClose, setIndex])

  if (!current) return null

  return (
    <div className="fixed inset-0 z-[70]" role="dialog" aria-modal="true" aria-labelledby="tour-title">
      {/* dimmed page with a hole over the target */}
      {rect ? (
        <div
          aria-hidden
          onClick={onClose}
          className="absolute rounded-xl transition-all duration-200"
          style={{
            top: rect.top - PAD,
            left: rect.left - PAD,
            width: rect.width + PAD * 2,
            height: rect.height + PAD * 2,
            boxShadow: '0 0 0 9999px var(--scrim), 0 0 0 2px rgb(var(--c-brand))',
          }}
        />
      ) : (
        <div aria-hidden onClick={onClose} className="absolute inset-0" style={{ background: 'var(--scrim)' }} />
      )}

      <div
        ref={cardRef}
        className="absolute w-[min(360px,calc(100vw-24px))] rounded-xl border border-line-strong bg-card p-4 shadow-pop"
        style={cardPos ?? { top: -9999, left: -9999 }}
      >
        <div className="mb-1 flex items-start justify-between gap-2">
          <div className="eyebrow">
            {t('guide.ui.stepOf', { n: Math.min(index, visible.length - 1) + 1, count: visible.length })}
          </div>
          <button
            type="button"
            onClick={onClose}
            aria-label={t('guide.ui.close')}
            className="-mr-1 -mt-1 grid h-7 w-7 place-items-center rounded-md text-text-muted hover:bg-fill-hover hover:text-text-high"
          >
            <X size={14} aria-hidden />
          </button>
        </div>
        <h2 id="tour-title" className="display text-[18px] leading-tight text-text-heading">
          {t(stepKey(tour.id, current.n, 'title'))}
        </h2>
        <p className="mt-1.5 text-[13px] leading-relaxed text-text-mid">{t(stepKey(tour.id, current.n, 'body'))}</p>
        {last && <p className="mt-2 text-[12px] text-text-muted">{t('guide.ui.replayHint')}</p>}
        <div className="mt-4 flex items-center gap-2">
          <button type="button" onClick={onClose} className="text-[12.5px] font-semibold text-text-muted hover:text-text-high">
            {t('guide.ui.skip')}
          </button>
          <div className="ml-auto flex items-center gap-2">
            {index > 0 && (
              <button
                type="button"
                onClick={() => setIndex(index - 1)}
                className="h-8 rounded-lg border border-line-strong px-3 text-[12.5px] font-semibold text-text-high hover:bg-fill-hover"
              >
                {t('guide.ui.back')}
              </button>
            )}
            <button
              type="button"
              autoFocus
              onClick={() => (last ? onClose() : setIndex(index + 1))}
              className="h-8 rounded-lg bg-primary px-3.5 text-[12.5px] font-semibold text-on-primary hover:opacity-90"
            >
              {last ? t('guide.ui.done') : t('guide.ui.next')}
            </button>
          </div>
        </div>
      </div>
    </div>
  )
}

/** The toast after a guide closes: where to replay it. Points at the ? button. */
function ReplayTip({ onClose }: { onClose: () => void }) {
  const t = useT()
  return (
    <div
      role="status"
      className="print-hide fixed right-3 top-[60px] z-[90] flex max-w-[300px] items-start gap-2 rounded-xl border border-line-strong bg-card px-3 py-2.5 text-[12.5px] text-text-high shadow-pop"
    >
      <CircleHelp size={15} className="mt-0.5 shrink-0 text-[var(--brand-ink)]" aria-hidden />
      <span className="min-w-0 flex-1">{t('guide.ui.replayHint')}</span>
      <button type="button" onClick={onClose} aria-label={t('guide.ui.close')} className="shrink-0 text-text-muted hover:text-text-high">
        <X size={13} aria-hidden />
      </button>
    </div>
  )
}
