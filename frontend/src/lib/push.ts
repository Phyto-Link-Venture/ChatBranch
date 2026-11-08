'use client'

/**
 * Notifications on this device, when ChatBranch is not open.
 *
 * Two kinds of device, one shape:
 *
 *  * **A browser** (or the installed PWA) subscribes through its service worker
 *    with the server's VAPID public key, and hands back an endpoint plus two keys.
 *    Nothing is registered anywhere else: the key pair is the deployment's own.
 *  * **The phone app** (Capacitor) has no service worker — it asks the OS for a
 *    token through `@capacitor/push-notifications`, which this file reaches only if
 *    the plugin is actually there (a dynamic import, so the web build never pulls
 *    it in and a browser never tries).
 *
 * Permission is only ever asked for on a click: a page that calls
 * `Notification.requestPermission()` on load gets the prompt dismissed and, in
 * Safari, blocked for good.
 */

import { API } from './apiBase'
import { authHeaders, expireSessionOn401 } from './auth'

export interface PushConfig {
  supported: boolean
  web: boolean
  app: boolean
  vapid_public_key: string
}

export interface PushDevice {
  id: number
  platform: 'web' | 'android' | 'ios'
  label: string
  created_at: string
  last_push_at: string | null
  last_error: string
  endpoint_tail: string
}

/** What this device can do about push, before anything is asked of the person. */
export type PushAbility =
  /** Not a browser that can do it, and not the app either. */
  | 'unsupported'
  /** The person said no, in the browser or the OS — only they can undo it. */
  | 'denied'
  /** Ready to ask. */
  | 'askable'
  /** Already subscribed on this device. */
  | 'on'

async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}/push/${path}`, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
  })
  const data = await res.json().catch(() => ({}))
  if (expireSessionOn401(res)) throw new Error('Session expired')
  if (!res.ok) throw new Error((data as { detail?: string }).detail || `HTTP ${res.status}`)
  return data as T
}

export const pushConfig = () => api<PushConfig>('config')
export const pushDevices = () => api<{ devices: PushDevice[] }>('devices')
export const forgetPushDevice = (id: number) => api<{ ok: boolean }>(`devices/${id}`, { method: 'DELETE' })
export const testPush = () => api<{ ok: boolean; sent: number; problems: string[] }>('test', { method: 'POST' })

/** Is this the phone app rather than a browser? */
export function inNativeApp(): boolean {
  if (typeof window === 'undefined') return false
  const cap = (window as unknown as { Capacitor?: { isNativePlatform?: () => boolean } }).Capacitor
  try {
    return !!cap?.isNativePlatform?.()
  } catch {
    return false
  }
}

/** What the OS calls this platform, for the device list. */
function nativePlatform(): 'android' | 'ios' {
  const cap = (window as unknown as { Capacitor?: { getPlatform?: () => string } }).Capacitor
  return cap?.getPlatform?.() === 'ios' ? 'ios' : 'android'
}

/** A name for this device a person will recognise in the list. */
export function deviceLabel(): string {
  if (typeof navigator === 'undefined') return 'This device'
  if (inNativeApp()) return `ChatBranch app (${nativePlatform() === 'ios' ? 'iPhone or iPad' : 'Android'})`
  const ua = navigator.userAgent
  const browser = /Edg\//.test(ua) ? 'Edge'
    : /OPR\//.test(ua) ? 'Opera'
    : /Chrome\//.test(ua) ? 'Chrome'
    : /Firefox\//.test(ua) ? 'Firefox'
    : /Safari\//.test(ua) ? 'Safari'
    : 'Browser'
  const os = /iPhone|iPad/.test(ua) ? 'iPhone or iPad'
    : /Android/.test(ua) ? 'Android'
    : /Mac OS X/.test(ua) ? 'Mac'
    : /Windows/.test(ua) ? 'Windows'
    : /Linux/.test(ua) ? 'Linux'
    : ''
  return os ? `${browser} on ${os}` : browser
}

/** What this device can do right now. Never asks for anything. */
export async function pushAbility(): Promise<PushAbility> {
  if (typeof window === 'undefined') return 'unsupported'
  if (inNativeApp()) {
    const plugin = await nativePlugin()
    if (!plugin) return 'unsupported'
    try {
      const { receive } = await plugin.checkPermissions()
      return receive === 'denied' ? 'denied' : receive === 'granted' ? 'on' : 'askable'
    } catch {
      return 'askable'
    }
  }
  if (!('serviceWorker' in navigator) || !('PushManager' in window) || !('Notification' in window))
    return 'unsupported'
  if (Notification.permission === 'denied') return 'denied'
  try {
    const reg = await navigator.serviceWorker.getRegistration()
    const existing = await reg?.pushManager.getSubscription()
    if (existing) return 'on'
  } catch {
    /* no worker yet — still askable */
  }
  return Notification.permission === 'granted' ? 'askable' : 'askable'
}

/**
 * Ask for permission and register this device. Returns the device, or throws with
 * a reason a person can act on. Must be called from a click.
 */
export async function enablePush(): Promise<PushDevice> {
  const config = await pushConfig()
  return inNativeApp() ? enableNative(config) : enableWeb(config)
}

async function enableWeb(config: PushConfig): Promise<PushDevice> {
  if (!config.web) throw new Error('This server cannot send browser notifications yet.')
  if (!('serviceWorker' in navigator) || !('PushManager' in window))
    throw new Error('This browser cannot show notifications when ChatBranch is closed.')

  const permission = await Notification.requestPermission()
  if (permission !== 'granted') throw new Error('Notifications were not allowed for this site.')

  // the worker is registered by the shell; wait for it rather than racing it
  const reg = (await navigator.serviceWorker.getRegistration()) ?? (await navigator.serviceWorker.ready)
  const subscription = await reg.pushManager.subscribe({
    // every push carries a payload, and browsers require this to be true
    userVisibleOnly: true,
    applicationServerKey: fromB64Url(config.vapid_public_key),
  })
  const json = subscription.toJSON() as { endpoint?: string; keys?: { p256dh?: string; auth?: string } }
  if (!json.endpoint || !json.keys?.p256dh || !json.keys?.auth)
    throw new Error('The browser gave an incomplete subscription — try again.')

  return api<PushDevice>('devices', {
    method: 'POST',
    body: JSON.stringify({
      platform: 'web',
      endpoint: json.endpoint,
      p256dh: json.keys.p256dh,
      auth: json.keys.auth,
      label: deviceLabel(),
    }),
  })
}

async function enableNative(config: PushConfig): Promise<PushDevice> {
  if (!config.app)
    throw new Error('This server has no Firebase credentials yet, so the app cannot be alerted.')
  const plugin = await nativePlugin()
  if (!plugin) throw new Error('This app build has no push support.')

  const asked = await plugin.requestPermissions()
  if (asked.receive !== 'granted') throw new Error('Notifications were not allowed for this app.')

  const token = await new Promise<string>((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('The device did not return a push token.')), 20000)
    void plugin.addListener('registration', (t: { value: string }) => {
      clearTimeout(timer)
      resolve(t.value)
    })
    void plugin.addListener('registrationError', (e: unknown) => {
      clearTimeout(timer)
      reject(new Error(typeof e === 'string' ? e : 'The device could not register for notifications.'))
    })
    void plugin.register()
  })

  return api<PushDevice>('devices', {
    method: 'POST',
    body: JSON.stringify({ platform: nativePlatform(), endpoint: token, label: deviceLabel() }),
  })
}

/**
 * Stop alerting this device: drop the browser subscription (so the push service
 * forgets it too) and the row. The OS permission is the person's own to withdraw.
 */
export async function disablePush(deviceId?: number): Promise<void> {
  if (!inNativeApp() && typeof navigator !== 'undefined' && 'serviceWorker' in navigator) {
    try {
      const reg = await navigator.serviceWorker.getRegistration()
      const sub = await reg?.pushManager.getSubscription()
      await sub?.unsubscribe()
    } catch {
      /* already gone */
    }
  }
  if (deviceId != null) await forgetPushDevice(deviceId)
}

/** The tail of this browser's current subscription, to spot its own row in the list. */
export async function thisDeviceTail(): Promise<string | null> {
  if (typeof navigator === 'undefined' || inNativeApp() || !('serviceWorker' in navigator)) return null
  try {
    const reg = await navigator.serviceWorker.getRegistration()
    const sub = await reg?.pushManager.getSubscription()
    return sub ? sub.endpoint.slice(-12) : null
  } catch {
    return null
  }
}

interface NativePushPlugin {
  checkPermissions(): Promise<{ receive: string }>
  requestPermissions(): Promise<{ receive: string }>
  register(): Promise<void>
  addListener(event: string, fn: (data: never) => void): Promise<unknown>
}

/**
 * The Capacitor push plugin, read off Capacitor's own plugin registry rather than
 * imported. `@capacitor/push-notifications` is a dependency of the MOBILE workspace,
 * not of the dashboard, so importing it here — even dynamically — would make the web
 * build depend on a package it must not need. In the app the native bridge publishes
 * every installed plugin on `window.Capacitor.Plugins`; in a browser there is
 * nothing there, which is the honest answer.
 */
async function nativePlugin(): Promise<NativePushPlugin | null> {
  if (!inNativeApp()) return null
  const cap = (window as unknown as {
    Capacitor?: { Plugins?: Record<string, unknown> }
  }).Capacitor
  const plugin = cap?.Plugins?.PushNotifications as NativePushPlugin | undefined
  return plugin ?? null
}

function fromB64Url(value: string): ArrayBuffer {
  const padded = value.replace(/-/g, '+').replace(/_/g, '/')
  const raw = atob(padded.padEnd(padded.length + ((4 - (padded.length % 4)) % 4), '='))
  const buffer = new ArrayBuffer(raw.length)
  const out = new Uint8Array(buffer)
  for (let i = 0; i < raw.length; i++) out[i] = raw.charCodeAt(i)
  return buffer
}
