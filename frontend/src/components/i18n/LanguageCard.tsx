'use client'

import { useEffect, useState } from 'react'
import { Building2, Check, Languages } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Card } from '@/components/ui/Card'
import { authHeaders } from '@/lib/auth'
import { LOCALES, clearUserLocale, isLocale, useI18n, useT, type Locale } from '@/i18n'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

interface Prefs {
  locale: Locale | null
  org_locale: Locale | null
  can_set_org_default: boolean
}

/**
 * Settings → Appearance → Language: the signed-in user's own dashboard
 * language (saved to their profile), back to the organization default, and —
 * for people holding settings.appearance.org — the organization default itself.
 */
export function LanguageCard() {
  const t = useT()
  const { locale, setLocale } = useI18n()
  const [prefs, setPrefs] = useState<Prefs | null>(null)
  const [notice, setNotice] = useState('')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    fetch(`${API}/auth/preferences`, { headers: authHeaders() })
      .then((r) => (r.ok ? r.json() : null))
      .then((d) => {
        if (!d) return
        setPrefs({
          locale: isLocale(d.locale) ? d.locale : null,
          org_locale: isLocale(d.org_locale) ? d.org_locale : null,
          can_set_org_default: !!d.can_set_org_default,
        })
      })
      .catch(() => setError(t('common.errors.network')))
  }, [t])

  const native = (l: Locale | null) => LOCALES.find((x) => x.code === l)?.native ?? ''

  const choose = async (l: Locale) => {
    setError('')
    setBusy(true)
    try {
      await setLocale(l, { persist: 'user' })
      setPrefs((p) => (p ? { ...p, locale: l } : p))
      setNotice(t('shell.language.saved'))
    } finally {
      setBusy(false)
    }
  }

  const backToOrgDefault = async () => {
    if (!prefs) return
    setBusy(true)
    try {
      await clearUserLocale()
      setPrefs({ ...prefs, locale: null })
      if (prefs.org_locale) await setLocale(prefs.org_locale)
      setNotice(t('shell.language.saved'))
    } finally {
      setBusy(false)
    }
  }

  const setOrgDefault = async (l: Locale | null) => {
    setError('')
    setBusy(true)
    try {
      const res = await fetch(`${API}/settings/appearance`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json', ...authHeaders() },
        body: JSON.stringify({ locale: l }),
      })
      if (!res.ok) throw new Error(((await res.json().catch(() => ({}))) as { detail?: string }).detail || `HTTP ${res.status}`)
      setPrefs((p) => (p ? { ...p, org_locale: l } : p))
      setNotice(t('shell.language.orgSaved'))
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Card className="p-4 sm:p-5" data-tour="appearance-language">
      <div className="flex items-start gap-3">
        <span className="grid h-8 w-8 shrink-0 place-items-center rounded-lg bg-fill">
          <Languages size={15} className="text-text-mid" aria-hidden />
        </span>
        <div className="min-w-0 flex-1">
          <div className="text-[14px] font-semibold text-text-high">{t('shell.language.title')}</div>
          <p className="text-[12.5px] leading-snug text-text-muted">{t('shell.language.hint')}</p>
        </div>
      </div>

      <div className="mt-3 flex flex-wrap gap-2" role="radiogroup" aria-label={t('shell.language.title')}>
        {LOCALES.map((l) => {
          const on = locale === l.code
          return (
            <button
              key={l.code}
              type="button"
              role="radio"
              aria-checked={on}
              disabled={busy}
              onClick={() => void choose(l.code)}
              className={`inline-flex items-center gap-2 rounded-lg border px-3 py-2 text-[13px] transition-colors ${
                on ? 'border-text-heading bg-fill font-semibold text-text-heading' : 'border-line-strong text-text-mid hover:bg-fill-hover'
              }`}
            >
              {on && <Check size={13} aria-hidden />}
              <span lang={l.code}>{l.native}</span>
            </button>
          )
        })}
      </div>

      <div className="mt-3 flex flex-wrap items-center gap-x-4 gap-y-2 text-[12px] text-text-muted">
        <span>
          {prefs?.org_locale
            ? t('shell.language.orgDefaultIs', { lang: native(prefs.org_locale) })
            : t('shell.language.noOrgDefault')}
        </span>
        {prefs?.locale && (
          <button type="button" className="font-semibold text-text-high hover:underline" disabled={busy} onClick={() => void backToOrgDefault()}>
            {t('shell.language.useOrgDefault')}
          </button>
        )}
      </div>

      {prefs?.can_set_org_default && (
        <div className="mt-3 flex flex-wrap gap-2 border-t border-line pt-3">
          <Button variant="secondary" size="sm" disabled={busy || prefs.org_locale === locale} onClick={() => void setOrgDefault(locale)}>
            <Building2 size={13} aria-hidden /> {t('shell.language.setOrgDefault')}
          </Button>
          {prefs.org_locale && (
            <Button variant="ghost" size="sm" disabled={busy} onClick={() => void setOrgDefault(null)}>
              {t('shell.language.clearOrgDefault')}
            </Button>
          )}
        </div>
      )}

      {notice && <p role="status" className="mt-2 text-[12px] text-[color:var(--green-ink)]">{notice}</p>}
      {error && <p role="alert" className="mt-2 text-[12px] text-[color:var(--red-ink)]">{error}</p>}
      {locale !== 'en' && <p className="mt-2 text-[11.5px] text-text-faint">{t('shell.language.reviewNote')}</p>}
    </Card>
  )
}
