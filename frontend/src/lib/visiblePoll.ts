/**
 * Run `fn` now and then every `ms` — but only while the tab is visible. A
 * hidden tab (another tab in front, a minimised window, a phone's app in the
 * background) stops asking; coming back runs `fn` at once and resumes the
 * timer, so what is on screen is as fresh as with a plain setInterval.
 * Returns the stop function (for a useEffect cleanup).
 */
export function pollWhileVisible(fn: () => void, ms: number): () => void {
  let timer: ReturnType<typeof setInterval> | null = null
  const hidden = () => typeof document !== 'undefined' && document.visibilityState === 'hidden'
  const start = () => {
    if (timer === null) timer = setInterval(fn, ms)
  }
  const stop = () => {
    if (timer !== null) clearInterval(timer)
    timer = null
  }
  const onVisibility = () => {
    if (hidden()) stop()
    else {
      fn()
      start()
    }
  }
  fn()
  if (!hidden()) start()
  if (typeof document !== 'undefined') document.addEventListener('visibilitychange', onVisibility)
  return () => {
    stop()
    if (typeof document !== 'undefined') document.removeEventListener('visibilitychange', onVisibility)
  }
}

/** Has `next` the same JSON as the last value seen under `ref`? Records it either way.
 *  Lets a poller skip a setState (and the page re-render) when nothing changed. */
export function unchangedJson(ref: { current: string | null }, next: unknown): boolean {
  const s = JSON.stringify(next)
  if (s === ref.current) return true
  ref.current = s
  return false
}
