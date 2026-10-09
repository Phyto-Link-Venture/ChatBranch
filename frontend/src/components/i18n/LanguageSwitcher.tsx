'use client'

import { LOCALES, useI18n } from '@/i18n'

/**
 * A compact language switch for screens outside the shell (sign-in). It only
 * remembers the choice in this browser; once signed in, the profile's own
 * language (or the organization default) takes over.
 */
export function LanguageSwitcher({ className = '' }: { className?: string }) {
  const { locale, setLocale } = useI18n()
  return (
    <div className={`flex items-center justify-center gap-1 text-[12px] ${className}`} role="group" aria-label="Language">
      {LOCALES.map((l, i) => (
        <span key={l.code} className="flex items-center gap-1">
          {i > 0 && <span className="text-text-faint" aria-hidden>·</span>}
          <button
            type="button"
            lang={l.code}
            aria-pressed={locale === l.code}
            onClick={() => void setLocale(l.code, { persist: 'browser' })}
            className={locale === l.code ? 'font-semibold text-text-heading' : 'text-text-muted hover:text-text-high'}
          >
            {l.native}
          </button>
        </span>
      ))}
    </div>
  )
}
