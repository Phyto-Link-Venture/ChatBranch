'use client'

import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { Building2, Check, Monitor, Moon, RotateCcw, Sun, type LucideIcon } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Card } from '@/components/ui/Card'
import { LanguageCard } from '@/components/i18n/LanguageCard'
import { authHeaders } from '@/lib/auth'
import {
  ACCENT_PRESETS,
  DEFAULT_APPEARANCE,
  HEADING_FONT_LABELS,
  MODE_LABELS,
  RADIUS_LABELS,
  TEXT_SIZE_LABELS,
  THEMES,
  accentVars,
  applyAppearance,
  normalize,
  resolveDark,
  saveAppearance,
  saveOrgDefaultAppearance,
  storedAppearance,
  type Appearance,
  type HeadingFont,
  type Mode,
  type RadiusStyle,
  type TextSize,
  type ThemeName,
} from '@/lib/theme'
import { useAccess } from '@/lib/useAccess'
import { Trans, useT } from '@/i18n'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

/** Preview geometry per theme — how each look draws its corners and headings. */
const PREVIEW: Record<ThemeName, { radius: number; font: string }> = {
  paper: { radius: 3, font: "'Fraunces Variable', 'Fraunces', Georgia, serif" },
  graphite: { radius: 6, font: 'var(--font-sans)' },
  soft: { radius: 12, font: 'var(--font-sans)' },
}

const HEX = /^#[0-9a-f]{6}$/i

/**
 * Settings → Personal → Appearance. Every control applies instantly (the whole
 * app IS the preview) through saveAppearance(): localStorage for the pre-paint
 * script, the user's profile for every other device. Holders of
 * settings.appearance.org can also publish the current look as the
 * organization default — what everyone who never chose sees.
 */
export function AppearanceSettings() {
  const t = useT()
  const { can } = useAccess()
  const [a, setA] = useState<Appearance>(DEFAULT_APPEARANCE)
  const [dark, setDark] = useState(false)
  const [orgDefault, setOrgDefault] = useState<Appearance | null>(null)
  const [canOrg, setCanOrg] = useState(false)
  const [note, setNote] = useState<{ ok: boolean; text: string } | null>(null)
  const [orgBusy, setOrgBusy] = useState(false)
  const [hexDraft, setHexDraft] = useState('')
  const saveTimer = useRef<number | null>(null)

  // Current look (after mount — localStorage), and follow changes made
  // elsewhere (the sidebar's light/dark switch).
  useEffect(() => {
    const sync = () => {
      const cur = storedAppearance() ?? DEFAULT_APPEARANCE
      setA(cur)
      setHexDraft(cur.accent ?? '')
      setDark(document.documentElement.classList.contains('dark'))
    }
    sync()
    window.addEventListener('appearance-change', sync)
    return () => window.removeEventListener('appearance-change', sync)
  }, [])

  // Server side: the saved choice, the organization default, and whether the
  // caller may set it.
  useEffect(() => {
    fetch(`${API}/auth/preferences`, { headers: authHeaders() })
      .then((r) => (r.ok ? r.json() : null))
      .then((d: { appearance?: unknown; org_default?: unknown; can_set_org_default?: boolean } | null) => {
        if (!d) return
        setOrgDefault(d.org_default ? normalize(d.org_default) : null)
        setCanOrg(!!d.can_set_org_default)
        // Signed in on a new device: adopt the profile's look (else the
        // organization default) once.
        const chosen = d.appearance ?? d.org_default
        if (chosen && !storedAppearance()) applyAppearance(normalize(chosen))
      })
      .catch(() => {
        /* offline: local choice still works */
      })
  }, [])

  const commit = useCallback((next: Appearance, debounce = false) => {
    setA(next)
    setNote(null)
    if (saveTimer.current) window.clearTimeout(saveTimer.current)
    const persist = () =>
      void saveAppearance(next).then((ok) => {
        if (!ok) setNote({ ok: false, text: t('settings.appearance.notSavedToProfile') })
      })
    if (debounce) {
      applyAppearance(next) // instant; the profile save waits for the drag to settle
      saveTimer.current = window.setTimeout(persist, 450)
    } else persist()
  }, [t])

  useEffect(() => () => {
    if (saveTimer.current) window.clearTimeout(saveTimer.current)
  }, [])

  const set = <K extends keyof Appearance>(k: K, v: Appearance[K], debounce = false) => commit({ ...a, [k]: v }, debounce)

  const themeAccent = THEMES.find((th) => th.id === a.theme)?.swatch[2] ?? '#FF5A1F'
  const effectiveAccent = a.accent ?? themeAccent
  const derived = useMemo(() => accentVars(effectiveAccent, a.theme, dark), [effectiveAccent, a.theme, dark])

  const reset = async () => {
    const base = orgDefault ?? DEFAULT_APPEARANCE
    applyAppearance(base)
    setA(base)
    setHexDraft(base.accent ?? '')
    try {
      const res = await fetch(`${API}/auth/preferences`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json', ...authHeaders() },
        body: JSON.stringify({ appearance: null }),
      })
      setNote(
        res.ok
          ? { ok: true, text: orgDefault ? t('settings.appearance.backToOrg') : t('settings.appearance.backToDefault') }
          : { ok: false, text: t('settings.appearance.resetNotSaved', { status: res.status }) },
      )
    } catch {
      setNote({ ok: false, text: t('settings.appearance.resetOffline') })
    }
  }

  const publishOrg = async (clear = false) => {
    setOrgBusy(true)
    setNote(null)
    try {
      if (clear) {
        const res = await fetch(`${API}/settings/appearance`, {
          method: 'PUT',
          headers: { 'Content-Type': 'application/json', ...authHeaders() },
          body: JSON.stringify({ appearance: null }),
        })
        if (!res.ok) throw new Error(`HTTP ${res.status}`)
        setOrgDefault(null)
        setNote({ ok: true, text: t('settings.appearance.orgCleared') })
      } else {
        const r = await saveOrgDefaultAppearance(a)
        if (!r.ok) throw new Error(r.error || t('common.errors.network'))
        setOrgDefault(a)
        setNote({ ok: true, text: t('settings.appearance.orgSaved') })
      }
    } catch (e) {
      setNote({ ok: false, text: e instanceof Error ? e.message : String(e) })
    } finally {
      setOrgBusy(false)
    }
  }

  const themeName = (id: ThemeName) => {
    const th = THEMES.find((x) => x.id === id)
    return th ? t(th.name) : id
  }
  const describe = (x: Appearance) => `${themeName(x.theme)} · ${t(MODE_LABELS[x.mode])}${x.accent ? ` · ${x.accent}` : ''}`

  return (
    <div className="flex flex-col gap-5">
      <LanguageCard />
      {/* Live sample in the real tokens — the whole app re-themes with it. */}
      <Card className="flex flex-wrap items-center gap-x-6 gap-y-3 p-4 sm:p-5" data-tour="appearance-preview">
        <div className="min-w-0 flex-1 basis-[260px]">
          <div className="eyebrow">{t('admin.tags.editor.preview')}</div>
          <div className="display mt-0.5 text-[22px] leading-tight text-text-heading">{t('settings.appearance.sampleHeading')}</div>
          <p className="mt-1 text-[13px] text-text-mid">
            <Trans
              k="settings.appearance.sampleBody"
              values={{
                link: (
                  <span className="font-semibold" style={{ color: 'var(--brand-ink)' }}>
                    {t('settings.appearance.sampleLink')}
                  </span>
                ),
              }}
            />
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <Button variant="primary" size="sm" type="button">
            {t('settings.appearance.samplePrimary')}
          </Button>
          <Button variant="secondary" size="sm" type="button">
            {t('settings.appearance.sampleSecondary')}
          </Button>
          <span className="rounded-full bg-accent px-2.5 py-1 text-[11.5px] font-semibold text-[color:var(--on-accent)]">
            {t('settings.appearance.sampleAccent')}
          </span>
        </div>
      </Card>

      <Section tour="appearance-theme" title={t('settings.appearance.theme')} hint={t('settings.appearance.themeHint')}>
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-3" role="radiogroup" aria-label={t('settings.appearance.theme')}>
          {THEMES.map((th) => {
            const active = a.theme === th.id
            const [ground, ink, accent] = th.swatch
            const geo = PREVIEW[th.id]
            return (
              <button
                key={th.id}
                type="button"
                role="radio"
                aria-checked={active}
                onClick={() => set('theme', th.id)}
                className={`group relative flex flex-col overflow-hidden rounded-xl border bg-card text-left transition-colors ${
                  active ? 'border-text-heading ring-1 ring-text-heading' : 'border-line hover:border-line-strong'
                }`}
              >
                {/* mini preview, drawn with the theme's own swatches */}
                <div className="flex h-[104px] flex-col gap-1.5 p-3" style={{ background: ground }} aria-hidden>
                  <div className="flex items-center justify-between">
                    <span style={{ fontFamily: geo.font, color: ink, fontSize: 19, lineHeight: 1, fontWeight: th.id === 'paper' ? 500 : 700 }}>
                      Aa
                    </span>
                    <span className="h-3 w-3 rounded-full" style={{ background: accent }} />
                  </div>
                  <div className="h-[5px] w-[78%]" style={{ background: ink, opacity: 0.22, borderRadius: geo.radius }} />
                  <div className="h-[5px] w-[56%]" style={{ background: ink, opacity: 0.22, borderRadius: geo.radius }} />
                  <div className="mt-auto flex items-center gap-1.5">
                    <span className="h-[18px] w-[52px]" style={{ background: ink, borderRadius: geo.radius }} />
                    <span className="h-[18px] w-[34px] border" style={{ borderColor: ink, opacity: 0.35, borderRadius: geo.radius }} />
                    <span className="ml-auto h-[18px] w-[18px]" style={{ background: accent, borderRadius: geo.radius }} />
                  </div>
                </div>
                <div className="border-t border-line px-3 py-2.5">
                  <div className="flex items-center gap-1.5 text-[13px] font-semibold text-text-high">
                    {t(th.name)}
                    {active && <Check size={14} aria-hidden className="ml-auto text-text-heading" />}
                  </div>
                  <div className="mt-0.5 text-[11.5px] leading-snug text-text-muted">{t(th.blurb)}</div>
                </div>
              </button>
            )
          })}
        </div>
      </Section>

      <Section tour="appearance-mode" title={t('settings.appearance.mode')} hint={t('settings.appearance.modeHint')}>
        <Segmented<Mode>
          label={t('settings.appearance.colourMode')}
          value={a.mode}
          onChange={(v) => set('mode', v)}
          options={[
            { value: 'light', label: t(MODE_LABELS.light), Icon: Sun },
            { value: 'dark', label: t(MODE_LABELS.dark), Icon: Moon },
            { value: 'system', label: t(MODE_LABELS.system), Icon: Monitor },
          ]}
        />
        {a.mode === 'system' && (
          <p className="mt-2 text-[12px] text-text-muted">
            {resolveDark('system') ? t('settings.appearance.devicePrefersDark') : t('settings.appearance.devicePrefersLight')}
          </p>
        )}
      </Section>

      <Section tour="appearance-accent" title={t('settings.appearance.accent')} hint={t('settings.appearance.accentHint')}>
        <div className="flex flex-wrap items-center gap-2" role="radiogroup" aria-label={t('settings.appearance.accent')}>
          <button
            type="button"
            role="radio"
            aria-checked={a.accent === null}
            onClick={() => {
              setHexDraft('')
              set('accent', null)
            }}
            className={`inline-flex h-9 items-center gap-2 rounded-full border px-3 text-[12.5px] font-semibold transition-colors ${
              a.accent === null ? 'border-text-heading text-text-heading' : 'border-line-strong text-text-mid hover:text-text-high'
            }`}
          >
            <span className="h-4 w-4 rounded-full" style={{ background: themeAccent }} aria-hidden />
            {t('settings.appearance.themeDefault')}
          </button>
          {ACCENT_PRESETS.map((hex) => {
            const active = (a.accent ?? '').toLowerCase() === hex.toLowerCase()
            return (
              <button
                key={hex}
                type="button"
                role="radio"
                aria-checked={active}
                aria-label={t('settings.appearance.accentAria', { hex })}
                title={hex}
                onClick={() => {
                  setHexDraft(hex)
                  set('accent', hex)
                }}
                className={`grid h-9 w-9 place-items-center rounded-full border-2 transition-transform hover:scale-105 ${
                  active ? 'border-text-heading' : 'border-transparent'
                }`}
              >
                <span className="h-6 w-6 rounded-full ring-1 ring-line-strong" style={{ background: hex }} />
              </button>
            )
          })}
          <label className="ml-1 inline-flex h-9 items-center gap-2 rounded-full border border-line-strong bg-card pl-1.5 pr-3">
            <input
              type="color"
              value={HEX.test(effectiveAccent) ? effectiveAccent : '#ff5a1f'}
              onChange={(e) => {
                setHexDraft(e.target.value)
                set('accent', e.target.value, true)
              }}
              aria-label={t('settings.appearance.customAccent')}
              className="h-6 w-6 cursor-pointer rounded-full border-0 bg-transparent p-0"
            />
            <input
              value={hexDraft}
              onChange={(e) => {
                const v = e.target.value.trim()
                setHexDraft(v)
                const withHash = v.startsWith('#') ? v : `#${v}`
                if (HEX.test(withHash)) set('accent', withHash, true)
              }}
              placeholder="#RRGGBB"
              maxLength={7}
              aria-label={t('settings.appearance.hexAria')}
              className="w-[74px] bg-transparent font-mono text-[12.5px] text-text-primary outline-none"
            />
          </label>
        </div>

        {/* derived, readability-adjusted colours for this theme + mode */}
        <div className="mt-3 flex flex-wrap items-center gap-x-5 gap-y-2 rounded-lg border border-line bg-well px-3 py-2.5 text-[12px]">
          <span className="flex items-center gap-2">
            <span className="h-4 w-4 rounded" style={{ background: derived['--c-brand'] ? `rgb(${derived['--c-brand']})` : effectiveAccent }} aria-hidden />
            <span className="text-text-muted">{t('settings.appearance.fill')}</span>
          </span>
          <span className="flex items-center gap-2">
            <span className="font-semibold" style={{ color: derived['--brand-ink'] ?? 'var(--brand-ink)' }}>
              {t('settings.appearance.readableInk', { hex: derived['--brand-ink']?.toUpperCase() ?? '' })}
            </span>
          </span>
          <span className="text-text-faint">
            {a.accent
              ? dark
                ? t('settings.appearance.adjustedOwnDark')
                : t('settings.appearance.adjustedOwnLight')
              : dark
                ? t('settings.appearance.adjustedThemeDark')
                : t('settings.appearance.adjustedThemeLight')}
          </span>
        </div>
      </Section>

      <div data-tour="appearance-size" className="grid grid-cols-1 gap-5 lg:grid-cols-3">
        <Section title={t('settings.appearance.textSize')}>
          <Segmented<TextSize>
            label={t('settings.appearance.textSize')}
            value={a.textSize}
            onChange={(v) => set('textSize', v)}
            options={(['sm', 'md', 'lg', 'xl'] as TextSize[]).map((v) => ({ value: v, label: t(TEXT_SIZE_LABELS[v]) }))}
          />
        </Section>
        <Section title={t('settings.appearance.corners')}>
          <Segmented<RadiusStyle>
            label={t('settings.appearance.cornerStyle')}
            value={a.radius}
            onChange={(v) => set('radius', v)}
            options={(['theme', 'sharp', 'round'] as RadiusStyle[]).map((v) => ({ value: v, label: t(RADIUS_LABELS[v]) }))}
          />
        </Section>
        <Section title={t('settings.appearance.headingFont')}>
          <Segmented<HeadingFont>
            label={t('settings.appearance.headingFont')}
            value={a.headingFont}
            onChange={(v) => set('headingFont', v)}
            options={(['theme', 'serif', 'sans'] as HeadingFont[]).map((v) => ({ value: v, label: t(HEADING_FONT_LABELS[v]) }))}
          />
        </Section>
      </div>

      {note && (
        <div
          role={note.ok ? 'status' : 'alert'}
          className="rounded-lg border px-3 py-2 text-[12.5px]"
          style={{
            color: note.ok ? 'var(--green-ink)' : 'var(--red-ink)',
            borderColor: note.ok ? 'rgb(var(--c-green) / 0.3)' : 'rgb(var(--c-red) / 0.3)',
          }}
        >
          {note.text}
        </div>
      )}

      <div data-tour="appearance-footer" className="flex flex-wrap items-center gap-2 border-t border-line pt-4">
        <Button variant="secondary" size="sm" type="button" onClick={() => void reset()}>
          <RotateCcw size={13} aria-hidden /> {t('settings.appearance.reset')}
        </Button>
        <span className="text-[12px] text-text-muted">
          {orgDefault
            ? t('settings.appearance.orgDefaultIs', { look: describe(orgDefault) })
            : t('settings.appearance.noOrgDefault')}
        </span>
        {(canOrg || can('settings.appearance.org')) && (
          <span className="flex flex-wrap items-center gap-2 sm:ml-auto">
            {orgDefault && (
              <Button variant="ghost" size="sm" type="button" disabled={orgBusy} onClick={() => void publishOrg(true)}>
                {t('settings.appearance.clearOrg')}
              </Button>
            )}
            <Button variant="primary" size="sm" type="button" disabled={orgBusy} onClick={() => void publishOrg()}>
              <Building2 size={13} aria-hidden /> {t('settings.appearance.useAsOrg')}
            </Button>
          </span>
        )}
      </div>
    </div>
  )
}

function Section({ title, hint, tour, children }: { title: string; hint?: string; tour?: string; children: ReactNode }) {
  return (
    <section data-tour={tour}>
      <div className="mb-2.5 flex flex-wrap items-baseline gap-x-3 gap-y-0.5 border-b border-line pb-1.5">
        <h3 className="display text-[18px] leading-tight text-text-heading">{title}</h3>
        {hint && <span className="text-[12px] text-text-muted">{hint}</span>}
      </div>
      {children}
    </section>
  )
}

function Segmented<T extends string>({
  value,
  options,
  onChange,
  label,
}: {
  value: T
  options: { value: T; label: string; Icon?: LucideIcon }[]
  onChange: (v: T) => void
  label: string
}) {
  return (
    <div role="radiogroup" aria-label={label} className="inline-flex max-w-full flex-wrap gap-0.5 rounded-lg border border-line-strong bg-card p-0.5">
      {options.map((o) => {
        const active = o.value === value
        return (
          <button
            key={o.value}
            type="button"
            role="radio"
            aria-checked={active}
            onClick={() => onChange(o.value)}
            className={`inline-flex h-8 min-w-[44px] items-center justify-center gap-1.5 rounded-md px-3 text-[12.5px] font-semibold transition-colors ${
              active ? 'bg-primary text-on-primary' : 'text-text-mid hover:bg-fill-hover hover:text-text-high'
            }`}
          >
            {o.Icon && <o.Icon size={14} aria-hidden />}
            {o.label}
          </button>
        )
      })}
    </div>
  )
}
