'use client'

import { API } from '@/lib/apiBase'
import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { Building2, Check, Monitor, Moon, RotateCcw, Sun, type LucideIcon } from 'lucide-react'
import { Advanced } from '@/components/ui/Advanced'
import { Button } from '@/components/ui/Button'
import { Card } from '@/components/ui/Card'
import { Select, type SelectOption } from '@/components/ui/Select'
import { LanguageCard } from '@/components/i18n/LanguageCard'
import { authHeaders } from '@/lib/auth'
import {
  ACCENT_PRESETS,
  DEFAULT_APPEARANCE,
  FONTS,
  FONT_SCALE_RANGE,
  MODE_LABELS,
  RADIUS_LABELS,
  SPACING_RANGE,
  TEXT_SIZE_LABELS,
  THEMES,
  accentVars,
  applyAppearance,
  customisedCount,
  densityScale,
  fontDef,
  normalize,
  presetScale,
  resolveDark,
  saveAppearance,
  saveOrgDefaultAppearance,
  spaceScale,
  storedAppearance,
  textScale,
  type Appearance,
  type BodyFont,
  type CardPadding,
  type ContentWidth,
  type Density,
  type Gutter,
  type HeadingFont,
  type LineHeight,
  type Mode,
  type Motion,
  type NavLayout,
  type NavSide,
  type NavStart,
  type NavWidth,
  type RadiusStyle,
  type SurfaceStyle,
  type TextSize,
  type ThemeName,
} from '@/lib/theme'
import { useAccess } from '@/lib/useAccess'
import { Trans, useT, type MessageKey } from '@/i18n'

const HEX = /^#[0-9a-f]{6}$/i

/**
 * Settings → Personal → Appearance. Simple first — language, theme, mode and
 * text size — and everything else (accent, fonts, spacing, layout, details)
 * under "Advanced settings". Every control applies instantly (the whole app IS
 * the preview, plus the sample card at the top) through saveAppearance():
 * localStorage for the pre-paint script, the user's profile for every other
 * device. Holders of settings.appearance.org can also publish the current
 * look as the organization default — what everyone who never chose sees.
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
  const patch = (p: Partial<Appearance>, debounce = false) => commit({ ...a, ...p }, debounce)

  const themeDef = THEMES.find((th) => th.id === a.theme) ?? THEMES[0]
  const themeAccent = (dark ? themeDef.dark : themeDef.light).accent
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

  const fontLabel = (id: string) => {
    const f = fontDef(id)
    if (!f) return id
    return f.translate ? t(f.label as MessageKey) : f.label
  }
  const fontOptions = (heading: boolean): SelectOption[] => [
    { value: 'theme', label: t('settings.appearance.fonts.theme'), hint: fontLabel(themeFontId(a.theme, heading)) },
    ...(heading ? [{ value: 'sans', label: t('settings.appearance.fonts.sameAsBody') }] : []),
    ...FONTS.map((f) => ({
      value: f.id,
      label: fontLabel(f.id),
      group: t(f.kind === 'serif' ? 'settings.appearance.fontGroups.serif' : 'settings.appearance.fontGroups.sans'),
    })),
  ]
  const opts = <T extends string>(values: readonly T[], label: (v: T) => MessageKey) =>
    values.map((v) => ({ value: v, label: t(label(v)) }))

  const scalePct = Math.round(textScale(a) * 100)
  const spacePct = Math.round(spaceScale(a) * 100)
  const advancedCount = customisedCount(a) + (a.accent ? 1 : 0)

  return (
    <div className="flex flex-col gap-5">
      <LanguageCard />
      {/* Live sample in the real tokens — the whole app re-themes with it. */}
      <Card className="flex flex-col gap-4 p-4 sm:p-5" data-tour="appearance-preview">
        <div className="flex flex-wrap items-center gap-x-6 gap-y-3">
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
        </div>
        {/* a nested card shows card padding, density and the card style */}
        <Card className="bg-well p-3">
          <div className="flex items-baseline justify-between gap-3">
            <span className="text-[13px] font-semibold text-text-high">{t('settings.appearance.sampleCardTitle')}</span>
            <span className="text-[11.5px] text-text-muted">{t('settings.appearance.sampleCardMeta')}</span>
          </div>
          <div className="mt-2 flex flex-col gap-1.5">
            {(['sampleRow1', 'sampleRow2'] as const).map((k) => (
              <div key={k} className="flex items-center gap-2 rounded-lg border border-line bg-card px-3 py-2 text-[12.5px] text-text-mid">
                <span className="h-2 w-2 shrink-0 rounded-full bg-brand" aria-hidden />
                {t(`settings.appearance.${k}`)}
              </div>
            ))}
          </div>
        </Card>
      </Card>

      <Section tour="appearance-theme" title={t('settings.appearance.theme')} hint={t('settings.appearance.themeHint')}>
        <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-5" role="radiogroup" aria-label={t('settings.appearance.theme')}>
          {THEMES.map((th) => {
            const active = a.theme === th.id
            const s = dark ? th.dark : th.light
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
                {/* mini preview in the theme's own tokens (current mode) */}
                <div className="flex h-[92px] gap-1.5 p-2" style={{ background: s.app }} aria-hidden>
                  <div className="flex w-[18%] flex-col gap-1 pt-1" style={{ borderRight: `1px solid ${s.line}` }}>
                    <span className="h-[4px] w-[70%]" style={{ background: s.ink, opacity: 0.5, borderRadius: th.radius }} />
                    <span className="h-[4px] w-[55%]" style={{ background: s.ink, opacity: 0.25, borderRadius: th.radius }} />
                    <span className="h-[4px] w-[62%]" style={{ background: s.accent, borderRadius: th.radius }} />
                  </div>
                  <div className="flex min-w-0 flex-1 flex-col gap-1">
                    <div className="flex items-center justify-between">
                      <span style={{ fontFamily: th.font, color: s.ink, fontSize: 17, lineHeight: 1, fontWeight: th.weight }}>Aa</span>
                      <span className="h-2.5 w-2.5 rounded-full" style={{ background: s.accent }} />
                    </div>
                    <div
                      className="flex flex-1 flex-col gap-1 p-1.5"
                      style={{ background: s.card, border: `1px solid ${s.line}`, borderRadius: th.radius + 1 }}
                    >
                      <span className="h-[4px] w-[80%]" style={{ background: s.ink, opacity: 0.22, borderRadius: th.radius }} />
                      <span className="h-[4px] w-[55%]" style={{ background: s.ink, opacity: 0.22, borderRadius: th.radius }} />
                      <div className="mt-auto flex items-center gap-1">
                        <span className="h-[12px] w-[38%]" style={{ background: s.primary, borderRadius: th.radius }} />
                        <span className="h-[12px] w-[24%]" style={{ border: `1px solid ${s.line}`, borderRadius: th.radius }} />
                      </div>
                    </div>
                  </div>
                </div>
                <div className="border-t border-line px-3 py-2">
                  <div className="flex items-center gap-1.5 text-[13px] font-semibold text-text-high">
                    <span className="min-w-0 truncate">{t(th.name)}</span>
                    {active && <Check size={14} aria-hidden className="ml-auto shrink-0 text-text-heading" />}
                  </div>
                  <div className="mt-0.5 line-clamp-2 text-[11.5px] leading-snug text-text-muted">{t(th.blurb)}</div>
                </div>
              </button>
            )
          })}
        </div>
      </Section>

      <div className="grid grid-cols-1 gap-5 lg:grid-cols-2">
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

        <Section tour="appearance-size" title={t('settings.appearance.textSize')} hint={t('settings.appearance.textSizeHint')}>
          <div className="flex flex-wrap items-center gap-3">
            <Segmented<TextSize | ''>
              label={t('settings.appearance.textSize')}
              value={a.fontScale == null ? a.textSize : ''}
              onChange={(v) => v && patch({ textSize: v, fontScale: null })}
              options={(['sm', 'md', 'lg', 'xl'] as TextSize[]).map((v) => ({ value: v, label: t(TEXT_SIZE_LABELS[v]) }))}
            />
            <Slider
              label={t('settings.appearance.customSize')}
              min={FONT_SCALE_RANGE.min}
              max={FONT_SCALE_RANGE.max}
              value={scalePct}
              onChange={(v) => {
                const preset = (['sm', 'md', 'lg', 'xl'] as TextSize[]).find((s) => presetScale(s) === v)
                patch(preset ? { textSize: preset, fontScale: null } : { fontScale: v }, true)
              }}
            />
          </div>
        </Section>
      </div>

      <Advanced
        variant="card"
        data-tour="appearance-advanced"
        label={t('settings.appearance.advancedTitle')}
        hint={t('settings.appearance.advancedHint')}
        count={advancedCount}
      >
        <div className="flex flex-col gap-6">
          <Section title={t('settings.appearance.accent')} hint={t('settings.appearance.accentHint')}>
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
                  value={HEX.test(effectiveAccent) ? effectiveAccent.toLowerCase() : '#ff5a1f'}
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

          <Section title={t('settings.appearance.typography')} hint={t('settings.appearance.typographyHint')}>
            <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
              <FontField
                label={t('settings.appearance.bodyFont')}
                value={a.bodyFont}
                options={fontOptions(false)}
                onChange={(v) => set('bodyFont', v as BodyFont)}
                sample={t('settings.appearance.fontSample')}
                family="var(--font-sans)"
              />
              <FontField
                label={t('settings.appearance.headingFont')}
                value={a.headingFont}
                options={fontOptions(true)}
                onChange={(v) => set('headingFont', v as HeadingFont)}
                sample={t('settings.appearance.sampleHeading')}
                family="var(--font-display)"
                display
              />
            </div>
            <Field label={t('settings.appearance.lineHeight')}>
              <Segmented<LineHeight>
                label={t('settings.appearance.lineHeight')}
                value={a.lineHeight}
                onChange={(v) => set('lineHeight', v)}
                options={opts(['tight', 'normal', 'relaxed'] as const, (v) => `settings.appearance.lineHeights.${v}`)}
              />
            </Field>
          </Section>

          <Section title={t('settings.appearance.spacingTitle')} hint={t('settings.appearance.spacingHint')}>
            <Field label={t('settings.appearance.density')}>
              <div className="flex flex-wrap items-center gap-3">
                <Segmented<Density | ''>
                  label={t('settings.appearance.density')}
                  value={a.spacing == null ? a.density : ''}
                  onChange={(v) => v && patch({ density: v, spacing: null })}
                  options={opts(['compact', 'comfortable', 'spacious'] as const, (v) => `settings.appearance.densities.${v}`)}
                />
                <Slider
                  label={t('settings.appearance.customSpacing')}
                  min={SPACING_RANGE.min}
                  max={SPACING_RANGE.max}
                  step={5}
                  value={spacePct}
                  onChange={(v) => {
                    const preset = (['compact', 'comfortable', 'spacious'] as Density[]).find((d) => densityScale(d) === v)
                    patch(preset ? { density: preset, spacing: null } : { spacing: v }, true)
                  }}
                />
              </div>
            </Field>
            <div className="flex flex-wrap gap-x-8 gap-y-4">
              <Field label={t('settings.appearance.contentWidth')}>
                <Segmented<ContentWidth>
                  label={t('settings.appearance.contentWidth')}
                  value={a.contentWidth}
                  onChange={(v) => set('contentWidth', v)}
                  options={opts(['narrow', 'normal', 'wide', 'full'] as const, (v) => `settings.appearance.widths.${v}`)}
                />
              </Field>
              <Field label={t('settings.appearance.gutter')}>
                <Segmented<Gutter>
                  label={t('settings.appearance.gutter')}
                  value={a.gutter}
                  onChange={(v) => set('gutter', v)}
                  options={opts(['sm', 'md', 'lg'] as const, (v) => `settings.appearance.gutters.${v}`)}
                />
              </Field>
              <Field label={t('settings.appearance.cardPadding')}>
                <Segmented<CardPadding>
                  label={t('settings.appearance.cardPadding')}
                  value={a.cardPadding}
                  onChange={(v) => set('cardPadding', v)}
                  options={opts(['sm', 'md', 'lg'] as const, (v) => `settings.appearance.cardPads.${v}`)}
                />
              </Field>
            </div>
          </Section>

          <Section title={t('settings.appearance.layoutTitle')} hint={t('settings.appearance.layoutHint')}>
            <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
              <Field label={t('settings.appearance.navLayout')}>
                <Segmented<NavLayout>
                  label={t('settings.appearance.navLayout')}
                  value={a.navLayout}
                  onChange={(v) => set('navLayout', v)}
                  options={opts(['side', 'top'] as const, (v) => `settings.appearance.navLayouts.${v}`)}
                />
              </Field>
              {a.navLayout === 'side' && (
                <>
                  <Field label={t('settings.appearance.navSide')}>
                    <Segmented<NavSide>
                      label={t('settings.appearance.navSide')}
                      value={a.navSide}
                      onChange={(v) => set('navSide', v)}
                      options={opts(['left', 'right'] as const, (v) => `settings.appearance.navSides.${v}`)}
                    />
                  </Field>
                  <Field label={t('settings.appearance.navWidth')}>
                    <Segmented<NavWidth>
                      label={t('settings.appearance.navWidth')}
                      value={a.navWidth}
                      onChange={(v) => set('navWidth', v)}
                      options={opts(['narrow', 'normal', 'wide'] as const, (v) => `settings.appearance.navWidths.${v}`)}
                    />
                  </Field>
                  <Field label={t('settings.appearance.navStart')}>
                    <Segmented<NavStart>
                      label={t('settings.appearance.navStart')}
                      value={a.navStart}
                      onChange={(v) => set('navStart', v)}
                      options={opts(['expanded', 'collapsed'] as const, (v) => `settings.appearance.navStarts.${v}`)}
                    />
                  </Field>
                </>
              )}
            </div>
          </Section>

          <Section title={t('settings.appearance.detailsTitle')}>
            <div className="flex flex-wrap gap-x-8 gap-y-4">
              <Field label={t('settings.appearance.cornerStyle')}>
                <Segmented<RadiusStyle>
                  label={t('settings.appearance.cornerStyle')}
                  value={a.radius}
                  onChange={(v) => set('radius', v)}
                  options={opts(['theme', 'square', 'sharp', 'round'] as const, (v) => RADIUS_LABELS[v])}
                />
              </Field>
              <Field label={t('settings.appearance.surface')}>
                <Segmented<SurfaceStyle>
                  label={t('settings.appearance.surface')}
                  value={a.surface}
                  onChange={(v) => set('surface', v)}
                  options={opts(['theme', 'flat', 'bordered', 'shadow'] as const, (v) => `settings.appearance.surfaces.${v}`)}
                />
              </Field>
              <Field label={t('settings.appearance.motion')}>
                <Segmented<Motion>
                  label={t('settings.appearance.motion')}
                  value={a.motion}
                  onChange={(v) => set('motion', v)}
                  options={opts(['system', 'reduce', 'full'] as const, (v) => `settings.appearance.motions.${v}`)}
                />
              </Field>
            </div>
          </Section>
        </div>
      </Advanced>

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
          <RotateCcw size={13} aria-hidden /> {orgDefault ? t('settings.appearance.resetToOrg') : t('settings.appearance.reset')}
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

/** Which named face a theme uses for body text / headings (the "Theme" option's hint). */
function themeFontId(theme: ThemeName, heading: boolean): string {
  const body: Record<ThemeName, string> = {
    paper: 'schibsted', graphite: 'geist', soft: 'nunito', ocean: 'jakarta', forest: 'dmsans',
    midnight: 'inter', rose: 'manrope', sand: 'schibsted', nord: 'plex', contrast: 'inter',
    pixel: 'schibsted', riso: 'Work Sans', lacquer: 'Figtree', clay: 'Figtree',
  }
  const display: Partial<Record<ThemeName, string>> = {
    paper: 'fraunces', forest: 'lora', rose: 'fraunces', sand: 'sourceserif',
    pixel: 'Pixelify Sans', riso: 'Dela Gothic One', lacquer: 'Cormorant Garamond', clay: 'Fredoka',
  }
  return heading ? display[theme] ?? body[theme] : body[theme]
}

function Section({ title, hint, tour, children }: { title: string; hint?: string; tour?: string; children: ReactNode }) {
  return (
    <section data-tour={tour} className="flex flex-col gap-3">
      <div className="flex flex-wrap items-baseline gap-x-3 gap-y-0.5 border-b border-line pb-1.5">
        <h3 className="display text-[18px] leading-tight text-text-heading">{title}</h3>
        {hint && <span className="text-[12px] text-text-muted">{hint}</span>}
      </div>
      {children}
    </section>
  )
}

function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex min-w-0 flex-col gap-1.5">
      <span className="text-[12px] font-semibold text-text-mid">{label}</span>
      {children}
    </div>
  )
}

function FontField({
  label,
  value,
  options,
  onChange,
  sample,
  family,
  display = false,
}: {
  label: string
  value: string
  options: SelectOption[]
  onChange: (v: string) => void
  sample: string
  /** The live variable the sample renders in (it follows the pick at once). */
  family: string
  display?: boolean
}) {
  return (
    <Field label={label}>
      <Select value={value} onChange={onChange} options={options} ariaLabel={label} size="md" searchable />
      <div
        className={`truncate rounded-lg border border-line bg-well px-3 py-2 text-text-high ${display ? 'text-[19px]' : 'text-[14px]'}`}
        style={{
          fontFamily: family,
          fontWeight: display ? 'var(--display-weight)' : undefined,
          letterSpacing: display ? 'var(--display-tracking)' : undefined,
        }}
      >
        {sample}
      </div>
    </Field>
  )
}

function Slider({
  label,
  min,
  max,
  step = 1,
  value,
  onChange,
}: {
  label: string
  min: number
  max: number
  step?: number
  value: number
  onChange: (v: number) => void
}) {
  return (
    <label className="flex min-w-[200px] flex-1 items-center gap-3">
      <span className="sr-only">{label}</span>
      <input
        type="range"
        min={min}
        max={max}
        step={step}
        value={value}
        onChange={(e) => onChange(Number(e.target.value))}
        aria-label={label}
        aria-valuetext={`${value}%`}
        className="h-2 min-w-0 flex-1 cursor-pointer accent-[var(--primary)]"
      />
      <span className="w-[44px] shrink-0 text-right font-mono text-[12.5px] tabular-nums text-text-high">{value}%</span>
    </label>
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
    <div role="radiogroup" aria-label={label} className="inline-flex max-w-full flex-wrap gap-0.5 self-start rounded-lg border border-line-strong bg-card p-0.5">
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
