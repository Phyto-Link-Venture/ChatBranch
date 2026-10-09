'use client'

import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'
import { authHeaders } from '@/lib/auth'
import { en, type Messages } from './messages/en'
import { DEFAULT_LOCALE, LOCALE_STORE_KEY, browserLocale, intlTag, isLocale, type Locale } from './locales'
import type { Dict, MessageKey, Vars } from './types'

type AnyDict = Dict<Messages>

/** English ships in the bundle (it is also every fallback); the rest load on demand. */
async function loadMessages(l: Locale): Promise<AnyDict> {
  if (l === 'ms') return (await import('./messages/ms')).ms
  if (l === 'zh') return (await import('./messages/zh')).zh
  return en
}

function lookup(dict: unknown, key: string): string | undefined {
  let node: unknown = dict
  for (const part of key.split('.')) {
    if (node == null || typeof node !== 'object') return undefined
    node = (node as Record<string, unknown>)[part]
  }
  return typeof node === 'string' ? node : undefined
}

const warned = new Set<string>()

/**
 * Resolve a key: the plural form for `vars.count` (Intl.PluralRules of the
 * locale), then the plain key, then English, then the key itself (with a
 * one-time console warning in development). `{name}` placeholders are filled
 * from `vars`; numbers are formatted for the locale.
 */
export function translate(dict: AnyDict, locale: Locale, key: string, vars?: Vars): string {
  let msg: string | undefined
  const count = vars?.count
  if (typeof count === 'number') {
    const cat = new Intl.PluralRules(intlTag(locale)).select(count)
    const enCat = new Intl.PluralRules('en').select(count)
    msg =
      lookup(dict, `${key}_${cat}`) ??
      lookup(dict, `${key}_other`) ??
      lookup(en, `${key}_${enCat}`) ??
      lookup(en, `${key}_other`)
  }
  msg ??= lookup(dict, key) ?? lookup(en, key)
  if (msg === undefined) {
    if (process.env.NODE_ENV !== 'production' && !warned.has(key)) {
      warned.add(key)
      console.warn(`[i18n] missing key: ${key}`)
    }
    return key
  }
  if (!vars) return msg
  const nf = new Intl.NumberFormat(intlTag(locale))
  return msg.replace(/\{(\w+)\}/g, (whole, name: string) => {
    const v = vars[name]
    if (v === undefined) return whole
    return typeof v === 'number' ? nf.format(v) : v
  })
}

export type TFunction = (key: MessageKey, vars?: Vars) => string

interface I18nState {
  locale: Locale
  /** false until the cached / chosen language has been applied after mount. */
  ready: boolean
  t: TFunction
  /** Switch language; `persist: 'user'` also saves it to the signed-in user's profile. */
  setLocale: (l: Locale, opts?: { persist?: 'user' | 'browser' }) => Promise<void>
}

const I18nContext = createContext<I18nState>({
  locale: DEFAULT_LOCALE,
  ready: false,
  t: (key, vars) => translate(en, DEFAULT_LOCALE, key, vars),
  setLocale: async () => {},
})

function readCached(): Locale | null {
  try {
    const v = localStorage.getItem(LOCALE_STORE_KEY)
    return isLocale(v) ? v : null
  } catch {
    return null
  }
}

function writeCached(l: Locale) {
  try {
    localStorage.setItem(LOCALE_STORE_KEY, l)
  } catch {
    /* storage blocked: the choice lasts this tab only */
  }
}

/**
 * The dashboard language. Server render and the first client paint are
 * English (no hydration mismatch); after mount the cached choice — else the
 * browser's language — is applied, and once per sign-in the shell calls
 * `syncLocaleFromServer` (the user's own choice → the organization default).
 */
export function I18nProvider({ children }: { children: ReactNode }) {
  const [locale, setLocaleState] = useState<Locale>(DEFAULT_LOCALE)
  const [dict, setDict] = useState<AnyDict>(en)
  const [ready, setReady] = useState(false)

  const apply = useCallback(async (l: Locale) => {
    const d = await loadMessages(l)
    setDict(d)
    setLocaleState(l)
    if (typeof document !== 'undefined') document.documentElement.lang = intlTag(l)
  }, [])

  useEffect(() => {
    const initial = readCached() ?? browserLocale() ?? DEFAULT_LOCALE
    void apply(initial).finally(() => setReady(true))
    const onChange = (e: Event) => {
      const l = (e as CustomEvent<Locale>).detail
      if (isLocale(l)) void apply(l)
    }
    window.addEventListener('locale-change', onChange)
    return () => window.removeEventListener('locale-change', onChange)
  }, [apply])

  const setLocale = useCallback(
    async (l: Locale, opts?: { persist?: 'user' | 'browser' }) => {
      writeCached(l)
      await apply(l)
      if (opts?.persist === 'user') await saveUserLocale(l)
    },
    [apply],
  )

  const t = useCallback<TFunction>((key, vars) => translate(dict, locale, key, vars), [dict, locale])
  const value = useMemo(() => ({ locale, ready, t, setLocale }), [locale, ready, t, setLocale])
  return <I18nContext.Provider value={value}>{children}</I18nContext.Provider>
}

export const useI18n = () => useContext(I18nContext)

/** The translate function for the current language. */
export const useT = (): TFunction => useContext(I18nContext).t

// ---- server sync --------------------------------------------------------------

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

async function saveUserLocale(l: Locale | null): Promise<void> {
  try {
    await fetch(`${API}/auth/preferences`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json', ...authHeaders() },
      body: JSON.stringify({ locale: l }),
    })
  } catch {
    /* offline: the browser keeps the choice; the next sign-in syncs it */
  }
}

/**
 * Once per sign-in: the user's own language, else the organization default,
 * else whatever this browser already shows. Broadcasts `locale-change`.
 */
export async function syncLocaleFromServer(): Promise<Locale | null> {
  try {
    const res = await fetch(`${API}/auth/preferences`, { headers: authHeaders() })
    if (!res.ok) return null
    const data = (await res.json()) as { locale?: unknown; org_locale?: unknown }
    const chosen = isLocale(data.locale) ? data.locale : isLocale(data.org_locale) ? data.org_locale : null
    if (chosen) {
      writeCached(chosen)
      window.dispatchEvent(new CustomEvent('locale-change', { detail: chosen }))
    }
    return chosen
  } catch {
    return null
  }
}

/** Reset the user's own choice so the organization default applies again. */
export async function clearUserLocale(): Promise<void> {
  await saveUserLocale(null)
}
