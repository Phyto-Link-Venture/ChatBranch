/**
 * The dashboard languages. Keep in step with the API's
 * SettingsEndpoints.SupportedLocales (the preference endpoints validate
 * against it). Adding one = a `messages/<code>/` folder with every namespace,
 * an entry here, and the code in that C# list. Manglish is an OVERLAY on
 * English (`messages/manglish/`, `Overlay<T>`): it rewrites the sentences in a
 * friendly Malaysian-English voice and falls back to English for the rest.
 */
export const LOCALES = [
  { code: 'en', label: 'English', native: 'English', intl: 'en-MY' },
  { code: 'ms', label: 'Malay', native: 'Bahasa Melayu', intl: 'ms-MY' },
  { code: 'zh', label: 'Chinese (Simplified)', native: '简体中文', intl: 'zh-CN' },
  { code: 'manglish', label: 'Manglish (Malaysian English)', native: 'Manglish', intl: 'en-MY' },
] as const

export type Locale = (typeof LOCALES)[number]['code']

/**
 * Languages a CUSTOMER can have (a contact's language, the website widget's
 * words): Manglish is the dashboard's voice only, never one a customer is set to.
 */
export const CONTENT_LANGUAGES = LOCALES.filter((l) => l.code !== 'manglish')

export const DEFAULT_LOCALE: Locale = 'en'

/** Where the chosen language is cached for the next load (per browser). */
export const LOCALE_STORE_KEY = 'ui-locale'

export function isLocale(v: unknown): v is Locale {
  return typeof v === 'string' && LOCALES.some((l) => l.code === v)
}

/** The BCP 47 tag Intl formats with (dates, numbers, plural rules). */
export function intlTag(l: Locale): string {
  return LOCALES.find((x) => x.code === l)?.intl ?? 'en-MY'
}

/** The browser's preferred language when it is one of ours, else null. */
export function browserLocale(): Locale | null {
  if (typeof navigator === 'undefined') return null
  for (const tag of navigator.languages ?? [navigator.language]) {
    const base = (tag || '').toLowerCase().split('-')[0]
    if (base === 'id') return 'ms' // Indonesian readers get Malay rather than English
    if (isLocale(base)) return base
  }
  return null
}
