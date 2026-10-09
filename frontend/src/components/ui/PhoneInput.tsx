'use client'

import { useMemo } from 'react'
import { Select } from '@/components/ui/Select'
import { intlTag, useI18n, useT } from '@/i18n'

/**
 * Phone field with a country-code picker: pick the country once, type the
 * national number, and the value stored stays E.164 (`+60123456789`) — which
 * is what WhatsApp, SIP and the CRM matcher all expect. Typing "+60" by hand
 * (and getting it subtly wrong) is no longer the only way in.
 *
 * The component is CONTROLLED on the full E.164 string, so it can be dropped
 * into an existing form without changing what that form saves.
 */

export interface Country {
  /** ISO 3166-1 alpha-2 — a stable key and the source of the localised name. */
  iso: string
  /** English name (the fallback when the browser can't name the region). */
  name: string
  /** Dial code WITHOUT the plus. */
  dial: string
  flag: string
}

/** Common destinations first-class; the list is alphabetical by country name. */
export const COUNTRIES: Country[] = [
  { iso: 'AU', name: 'Australia', dial: '61', flag: '🇦🇺' },
  { iso: 'BD', name: 'Bangladesh', dial: '880', flag: '🇧🇩' },
  { iso: 'BN', name: 'Brunei', dial: '673', flag: '🇧🇳' },
  { iso: 'KH', name: 'Cambodia', dial: '855', flag: '🇰🇭' },
  { iso: 'CA', name: 'Canada', dial: '1', flag: '🇨🇦' },
  { iso: 'CN', name: 'China', dial: '86', flag: '🇨🇳' },
  { iso: 'FR', name: 'France', dial: '33', flag: '🇫🇷' },
  { iso: 'DE', name: 'Germany', dial: '49', flag: '🇩🇪' },
  { iso: 'HK', name: 'Hong Kong', dial: '852', flag: '🇭🇰' },
  { iso: 'IN', name: 'India', dial: '91', flag: '🇮🇳' },
  { iso: 'ID', name: 'Indonesia', dial: '62', flag: '🇮🇩' },
  { iso: 'IE', name: 'Ireland', dial: '353', flag: '🇮🇪' },
  { iso: 'IT', name: 'Italy', dial: '39', flag: '🇮🇹' },
  { iso: 'JP', name: 'Japan', dial: '81', flag: '🇯🇵' },
  { iso: 'MY', name: 'Malaysia', dial: '60', flag: '🇲🇾' },
  { iso: 'MM', name: 'Myanmar', dial: '95', flag: '🇲🇲' },
  { iso: 'NL', name: 'Netherlands', dial: '31', flag: '🇳🇱' },
  { iso: 'NZ', name: 'New Zealand', dial: '64', flag: '🇳🇿' },
  { iso: 'PK', name: 'Pakistan', dial: '92', flag: '🇵🇰' },
  { iso: 'PH', name: 'Philippines', dial: '63', flag: '🇵🇭' },
  { iso: 'QA', name: 'Qatar', dial: '974', flag: '🇶🇦' },
  { iso: 'SA', name: 'Saudi Arabia', dial: '966', flag: '🇸🇦' },
  { iso: 'SG', name: 'Singapore', dial: '65', flag: '🇸🇬' },
  { iso: 'KR', name: 'South Korea', dial: '82', flag: '🇰🇷' },
  { iso: 'ES', name: 'Spain', dial: '34', flag: '🇪🇸' },
  { iso: 'LK', name: 'Sri Lanka', dial: '94', flag: '🇱🇰' },
  { iso: 'TW', name: 'Taiwan', dial: '886', flag: '🇹🇼' },
  { iso: 'TH', name: 'Thailand', dial: '66', flag: '🇹🇭' },
  { iso: 'AE', name: 'UAE', dial: '971', flag: '🇦🇪' },
  { iso: 'GB', name: 'United Kingdom', dial: '44', flag: '🇬🇧' },
  { iso: 'US', name: 'United States', dial: '1', flag: '🇺🇸' },
  { iso: 'VN', name: 'Vietnam', dial: '84', flag: '🇻🇳' },
]

/** Default country when a field is empty — the deployment's home market. */
export const DEFAULT_DIAL = '60'

/**
 * Split an E.164-ish value into (dial, national). The LONGEST matching dial
 * code wins, so +65… is Singapore rather than a mis-read +6.
 */
export function splitPhone(value: string): { dial: string; national: string } {
  const digits = (value || '').replace(/[^\d+]/g, '').replace(/^\+/, '')
  if (!digits) return { dial: DEFAULT_DIAL, national: '' }
  const match = [...COUNTRIES]
    .map((c) => c.dial)
    .filter((d) => digits.startsWith(d))
    .sort((a, b) => b.length - a.length)[0]
  if (!match) return { dial: DEFAULT_DIAL, national: digits }
  return { dial: match, national: digits.slice(match.length) }
}

/** A country's name in the dashboard language (Intl region names; the short
 *  form for Hong Kong, whose long name is "Hong Kong SAR China"). English keeps
 *  the list's own names. */
function countryName(c: Country, tag: string): string {
  if (tag.startsWith('en')) return c.name
  try {
    return new Intl.DisplayNames([tag], { type: 'region', style: c.iso === 'HK' ? 'short' : 'long' }).of(c.iso) ?? c.name
  } catch {
    return c.name
  }
}

/**
 * One option per DIAL CODE, not per country — +1 is both the US and Canada, and
 * duplicate option values would collide in the list and make the selected label
 * ambiguous. Countries sharing a code are named together in the hint, sorted in
 * the dashboard language.
 */
function dialOptions(tag: string) {
  const byDial = new Map<string, Country[]>()
  for (const c of COUNTRIES) byDial.set(c.dial, [...(byDial.get(c.dial) ?? []), c])
  return [...byDial.entries()]
    .map(([dial, list]) => ({
      value: dial,
      label: `${list[0].flag} +${dial}`,
      hint: list.map((c) => countryName(c, tag)).join(' / '),
    }))
    .sort((a, b) => a.hint.localeCompare(b.hint, tag))
}

export function PhoneInput({
  value,
  onChange,
  disabled = false,
  placeholder = '12-345 6789',
  ariaLabel,
  className = '',
}: {
  /** Full E.164 value, e.g. "+60123456789" ("" when empty). */
  value: string
  onChange: (e164: string) => void
  disabled?: boolean
  placeholder?: string
  ariaLabel?: string
  className?: string
}) {
  const t = useT()
  const { locale } = useI18n()
  const options = useMemo(() => dialOptions(intlTag(locale)), [locale])
  const { dial, national } = useMemo(() => splitPhone(value), [value])

  const emit = (nextDial: string, nextNational: string) => {
    const digits = nextNational.replace(/\D/g, '')
    // An empty national part clears the field rather than storing a bare "+60",
    // which would look like a real number to every downstream matcher.
    onChange(digits ? `+${nextDial}${digits}` : '')
  }

  return (
    <div className={`flex items-stretch gap-1.5 ${className}`}>
      <Select
        value={dial}
        onChange={(d) => emit(d, national)}
        disabled={disabled}
        size="md"
        ariaLabel={t('ui.phone.countryCode')}
        className="w-[132px] shrink-0"
        options={options}
      />
      <input
        type="tel"
        inputMode="tel"
        value={national}
        disabled={disabled}
        aria-label={ariaLabel ?? t('ui.phone.number')}
        placeholder={placeholder}
        onChange={(e) => emit(dial, e.target.value)}
        className="min-w-0 flex-1 rounded-lg border border-line-strong bg-well px-3 py-2 text-[12.5px] text-text-primary outline-none transition-colors focus:border-brand-400 disabled:opacity-60"
      />
    </div>
  )
}
