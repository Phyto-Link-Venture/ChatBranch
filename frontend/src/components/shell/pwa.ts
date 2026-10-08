'use client'

/**
 * PWA plumbing for the shell: the service-worker registration and the
 * "Install app" prompt.
 *
 * `beforeinstallprompt` can fire before any component mounts, so the listener
 * is attached when this module first loads (the Shell imports it at startup)
 * and the event is parked until someone asks for it.
 */

import { useEffect, useState } from 'react'

interface InstallPromptEvent extends Event {
  prompt: () => Promise<void>
  userChoice: Promise<{ outcome: 'accepted' | 'dismissed' }>
}

let deferred: InstallPromptEvent | null = null
const listeners = new Set<() => void>()
const emit = () => listeners.forEach((fn) => fn())

if (typeof window !== 'undefined') {
  window.addEventListener('beforeinstallprompt', (e) => {
    e.preventDefault() // keep the browser's own mini-infobar out; we show a button
    deferred = e as InstallPromptEvent
    emit()
  })
  window.addEventListener('appinstalled', () => {
    deferred = null
    emit()
  })
}

/** Running as the installed app (standalone window / iOS home-screen). */
export function isStandalone(): boolean {
  if (typeof window === 'undefined') return false
  return (
    window.matchMedia?.('(display-mode: standalone)').matches ||
    (navigator as Navigator & { standalone?: boolean }).standalone === true
  )
}

/** `canInstall` is false until the browser says the app is installable, and
 *  stays false inside the installed app. */
export function useInstallPrompt(): { canInstall: boolean; install: () => Promise<void> } {
  const [canInstall, setCanInstall] = useState(false)
  useEffect(() => {
    const sync = () => setCanInstall(!!deferred && !isStandalone())
    sync()
    listeners.add(sync)
    return () => {
      listeners.delete(sync)
    }
  }, [])
  const install = async () => {
    const ev = deferred
    if (!ev) return
    deferred = null
    emit()
    await ev.prompt()
    await ev.userChoice.catch(() => undefined)
  }
  return { canInstall, install }
}

/** Service worker on in production builds, or in dev with NEXT_PUBLIC_SW_DEV=1.
 *  Otherwise any worker left over from an earlier build is removed so dev
 *  never serves stale cached assets. */
export function registerServiceWorker(): void {
  if (typeof window === 'undefined' || !('serviceWorker' in navigator)) return
  const enabled = process.env.NODE_ENV === 'production' || process.env.NEXT_PUBLIC_SW_DEV === '1'
  if (!enabled) {
    navigator.serviceWorker
      .getRegistrations()
      .then((regs) => regs.forEach((r) => void r.unregister()))
      .catch(() => {})
    return
  }
  const go = () => navigator.serviceWorker.register('/sw.js', { scope: '/' }).catch(() => {})
  if (document.readyState === 'complete') go()
  else window.addEventListener('load', go, { once: true })
}

/** Mount once (root layout). Registers the worker after the page has loaded. */
export function ServiceWorkerRegistrar(): null {
  useEffect(() => registerServiceWorker(), [])
  return null
}
