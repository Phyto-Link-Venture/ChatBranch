'use client'

import { useEffect } from 'react'
import { usePathname, useRouter } from 'next/navigation'

/**
 * What the phone app has that a browser tab does not.
 *
 * The app (`mobile/`, Capacitor) loads the dashboard, so everything native has
 * to be wired from in here:
 *
 *  * the **splash screen** is hidden once React has painted, instead of after a
 *    fixed delay — a wrapper that flashes its own blank webview is the first
 *    thing a store reviewer notices (the config keeps a 3s net for a phone that
 *    cannot reach the deployment at all),
 *  * tapping a **notification** opens the conversation it is about, inside the
 *    app, rather than just bringing it to the foreground,
 *  * the **Android back button** walks the app's own history and only leaves at
 *    the root, which Google's own review guidance asks for,
 *  * the **status bar** follows the theme, so a dark theme does not get black
 *    text on a black bar.
 *
 * Every plugin is read off Capacitor's runtime registry (`window.Capacitor.Plugins`)
 * rather than imported: the plugins belong to the mobile workspace, and the web
 * bundle must not depend on them. In a browser there is nothing there and this
 * component does nothing at all.
 */

interface Plugin {
  [method: string]: ((...args: never[]) => Promise<unknown>) | undefined
}

function plugins(): Record<string, Plugin> | null {
  if (typeof window === 'undefined') return null
  const cap = (window as unknown as {
    Capacitor?: { isNativePlatform?: () => boolean; Plugins?: Record<string, Plugin> }
  }).Capacitor
  try {
    if (!cap?.isNativePlatform?.()) return null
  } catch {
    return null
  }
  return cap.Plugins ?? null
}

/** A link from a notification, made safe: inside the app, never off to another site. */
function internalPath(raw: unknown): string | null {
  if (typeof raw !== 'string' || raw.length === 0) return null
  // only a path — a notification must never be able to send the app to another origin
  if (!raw.startsWith('/') || raw.startsWith('//')) return null
  return raw
}

export function NativeBridge(): null {
  const router = useRouter()
  const pathname = usePathname()

  // The splash screen waits for the shell (launchAutoHide is off), so hide it
  // once this has mounted — one effect, no dependencies, runs after first paint.
  useEffect(() => {
    const p = plugins()
    void p?.SplashScreen?.hide?.()
  }, [])

  // Notification taps, and the Android back button. Re-registered when the
  // route changes so `canGoBack` reflects where the person actually is.
  useEffect(() => {
    const p = plugins()
    if (!p) return
    const listeners: { remove?: () => void }[] = []
    let cancelled = false

    const add = async (plugin: Plugin | undefined, event: string, fn: (data: never) => void) => {
      if (!plugin?.addListener) return
      try {
        const handle = (await (plugin.addListener as unknown as (
          e: string,
          f: (d: never) => void,
        ) => Promise<{ remove?: () => void }>)(event, fn))
        if (cancelled) handle.remove?.()
        else listeners.push(handle)
      } catch {
        /* a plugin this build does not have */
      }
    }

    void add(p.PushNotifications, 'pushNotificationActionPerformed', (event: never) => {
      const data = (event as unknown as { notification?: { data?: Record<string, unknown> } })
        .notification?.data
      const path = internalPath(data?.link)
      if (path) router.push(path)
    })

    void add(p.App, 'backButton', (event: never) => {
      const canGoBack = (event as unknown as { canGoBack?: boolean }).canGoBack
      if (canGoBack) router.back()
      // at the root there is nowhere to go back to: let the app close rather
      // than trapping the person in a webview with a dead button
      else void p.App?.exitApp?.()
    })

    return () => {
      cancelled = true
      listeners.forEach((l) => l.remove?.())
    }
  }, [router, pathname])

  // The status bar follows the theme — including a later switch, which is why
  // this watches the <html> class rather than reading it once.
  useEffect(() => {
    const p = plugins()
    if (!p?.StatusBar) return
    const apply = () => {
      const dark = document.documentElement.classList.contains('dark')
      // Capacitor's style names describe the CONTENT, not the background:
      // "Light" means light text, which is what a dark theme needs.
      void (p.StatusBar?.setStyle as unknown as (o: { style: string }) => Promise<void>)?.({
        style: dark ? 'DARK' : 'LIGHT',
      })
    }
    apply()
    const observer = new MutationObserver(apply)
    observer.observe(document.documentElement, { attributes: true, attributeFilter: ['class'] })
    return () => observer.disconnect()
  }, [])

  return null
}
