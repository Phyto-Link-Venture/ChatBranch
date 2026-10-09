// Languages the AI can conduct calls/chats in. ONE list for every picker
// (contact form, contact details, call launcher). "Auto-detect" = the agent
// opens in English, hears which of the three the contact speaks, and follows
// them. Keep in sync with the backend twins: crm.Contact.SUPPORTED_LANGUAGES
// and voice_agent/prompts.py LANGUAGE_NAMES.
//
// The language names are endonyms (each in its own language) and are never
// translated; only "Auto-detect" is. Pickers use `languageOptions(t)`.
import type { MessageKey, TFunction } from '@/i18n'

export const LANGUAGES: { value: string; label: string; lbl?: MessageKey }[] = [
  { value: 'auto', label: 'Auto-detect', lbl: 'ui.languages.auto' },
  { value: 'en', label: 'English' },
  { value: 'ms', label: 'Bahasa Melayu' },
  { value: 'zh', label: '中文 (Mandarin)' },
]

/** The picker's options in the dashboard language. */
export const languageOptions = (t: TFunction): { value: string; label: string }[] =>
  LANGUAGES.map((l) => ({ value: l.value, label: l.lbl ? t(l.lbl) : l.label }))

/** A language code's display name (translated when `t` is given). */
export const languageLabel = (code: string | undefined, t?: TFunction): string => {
  const l = LANGUAGES.find((x) => x.value === code)
  if (!l) return code || '—'
  return l.lbl && t ? t(l.lbl) : l.label
}
