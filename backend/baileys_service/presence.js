/**
 * "Keep phone notifications" for a paired (QR) number.
 *
 * WhatsApp silences the phone's own notifications while a linked device shows as
 * online — the same as leaving WhatsApp Web open — and a Baileys session announces
 * "available" when it connects. A number with this setting therefore stays
 * UNAVAILABLE and only comes online while it is sending or typing, going quiet
 * again `idleMs` after the last activity; the phone keeps buzzing in between.
 * Without it the session stays online all the time (the old behaviour: companions
 * that look present get their sends prioritised).
 *
 * Pure and timer-injectable, so `node presence.js --selftest` (and
 * `node calls.js --selftest`) check it offline.
 */
import path from 'path'
import { fileURLToPath, pathToFileURL } from 'url'

/** How long a "keep phone notifications" number stays online after its last send / typing. */
export const DEFAULT_IDLE_MS = 30_000

/**
 * @param {{ send: (kind: 'available' | 'unavailable') => Promise<unknown>,
 *           idleMs?: number, setTimer?: typeof setTimeout, clearTimer?: typeof clearTimeout }} opts
 */
export function createPresence({ send, idleMs = DEFAULT_IDLE_MS, setTimer = setTimeout, clearTimer = clearTimeout }) {
  let keep = false
  let online = false
  let timer = null
  const announce = async (kind) => {
    try { await send(kind) } catch { /* presence is best-effort */ }
  }
  const cancel = () => {
    if (timer) clearTimer(timer)
    timer = null
  }
  return {
    get keep() { return keep },
    get online() { return online },
    /** Apply the setting (on connect, or when it changes): quiet now, or online for good. */
    async configure(on) {
      keep = !!on
      cancel()
      online = !keep
      await announce(keep ? 'unavailable' : 'available')
    },
    /** About to send / type: online for a moment (keep mode), quiet again idleMs later. */
    async active() {
      if (!keep) return
      cancel()
      timer = setTimer(() => {
        timer = null
        if (!keep) return
        online = false
        announce('unavailable')
      }, idleMs)
      if (!online) {
        online = true
        await announce('available')
      }
    },
    /** The socket closed: forget the pending timer. */
    stop: cancel,
  }
}

// ---- offline selftest (node presence.js --selftest; also run by calls.js --selftest) ----

function assert(cond, what) {
  if (!cond) throw new Error(`presence selftest failed: ${what}`)
}

export async function selftest() {
  const sent = []
  const timers = []
  const p = createPresence({
    send: async (k) => { sent.push(k) },
    idleMs: 1000,
    setTimer: (fn, ms) => { const t = { fn, ms, cleared: false }; timers.push(t); return t },
    clearTimer: (t) => { t.cleared = true },
  })
  const fire = () => timers.filter((t) => !t.cleared).forEach((t) => { t.cleared = true; t.fn() })

  // default (setting off): online for good, activity changes nothing
  await p.configure(false)
  assert(sent.join() === 'available' && p.online, 'off = available on connect')
  await p.active()
  assert(sent.length === 1 && timers.length === 0, 'off = no toggling around sends')

  // setting on: quiet at once, online only around activity, quiet again after idleMs
  await p.configure(true)
  assert(sent.at(-1) === 'unavailable' && !p.online, 'on = unavailable on connect')
  await p.active()
  assert(sent.at(-1) === 'available' && p.online, 'a send brings it online')
  await p.active()
  assert(sent.filter((k) => k === 'available').length === 2, 'back-to-back sends announce once')
  assert(timers.filter((t) => !t.cleared).length === 1, 'one pending timer, restarted by each send')
  fire()
  assert(sent.at(-1) === 'unavailable' && !p.online, 'quiet again after idleMs')

  // switching it off while a timer is pending: online for good, the timer never fires
  await p.active()
  await p.configure(false)
  fire()
  assert(sent.at(-1) === 'available' && p.online, 'turning it off keeps it online')

  // a failing presence update never throws into a send
  const broken = createPresence({ send: async () => { throw new Error('socket closed') } })
  await broken.configure(true)
  await broken.active()
  broken.stop()
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href
    && process.argv.includes('--selftest')) {
  await selftest()
  console.log(`presence selftest OK (${path.basename(fileURLToPath(import.meta.url))})`)
}
