// Languages the AI can conduct calls/chats in. ONE list for every picker
// (contact form, contact details, call launcher). "Auto-detect" = the agent
// opens in English, hears which of the three the contact speaks, and follows
// them. Keep in sync with the backend twins: crm.Contact.SUPPORTED_LANGUAGES
// and voice_agent/prompts.py LANGUAGE_NAMES.
export const LANGUAGES = [
  { value: 'auto', label: 'Auto-detect' },
  { value: 'en', label: 'English' },
  { value: 'ms', label: 'Bahasa Melayu' },
  { value: 'zh', label: '中文 (Mandarin)' },
]

export const languageLabel = (code: string | undefined): string =>
  LANGUAGES.find((l) => l.value === code)?.label ?? (code || '—')
