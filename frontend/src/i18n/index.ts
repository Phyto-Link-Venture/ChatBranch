// The dashboard's localisation. Components use:
//   const t = useT();  t('chats.filter.title')  t('common.counts.items', { count })
//   <Trans k="…" values={{ link: <Link …/> }} />   for sentences with elements
//   const f = useFormat();  f.dateTime(row.created_at)  f.relative(ts)  f.number(n)
// Strings live in messages/<locale>/<namespace>.ts; English is the source and
// the types make every other locale carry exactly the same keys.
export { I18nProvider, useI18n, useT, translate, syncLocaleFromServer, clearUserLocale, type TFunction } from './I18nProvider'
export { Trans } from './Trans'
export { useFormat } from './format'
export { LOCALES, DEFAULT_LOCALE, isLocale, intlTag, browserLocale, type Locale } from './locales'
export type { MessageKey, Vars } from './types'
