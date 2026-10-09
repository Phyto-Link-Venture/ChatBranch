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
        if (!ok) setNote({ ok: false, text: 'Applied on this device — could not save it to your profile.' })
      })
    if (debounce) {
      applyAppearance(next) // instant; the profile save waits for the drag to settle
      saveTimer.current = window.setTimeout(persist, 450)
    } else persist()
  }, [])

  useEffect(() => () => {
    if (saveTimer.current) window.clearTimeout(saveTimer.current)
  }, [])

  const set = <K extends keyof Appearance>(k: K, v: Appearance[K], debounce = false) => commit({ ...a, [k]: v }, debounce)

  const themeAccent = THEMES.find((t) => t.id === a.theme)?.swatch[2] ?? '#FF5A1F'
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
          ? { ok: true, text: orgDefault ? 'Back on the organization default.' : 'Back to the default look.' }
          : { ok: false, text: `Reset here, but the profile did not update (HTTP ${res.status}).` },
      )
    } catch {
      setNote({ ok: false, text: 'Reset here, but the server could not be reached.' })
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
        setNote({ ok: true, text: 'Organization default cleared — new users start on Paper & Ink, light.' })
      } else {
        const r = await saveOrgDefaultAppearance(a)
        if (!r.ok) throw new Error(r.error || 'Could not save')
        setOrgDefault(a)
        setNote({ ok: true, text: 'Saved as the organization default for everyone who has not chosen their own look.' })
      }
    } catch (e) {
      setNote({ ok: false, text: e instanceof Error ? e.message : String(e) })
    } finally {
      setOrgBusy(false)
    }
  }

  const themeName = (t: ThemeName) => THEMES.find((x) => x.id === t)?.name ?? t
  const describe = (x: Appearance) => `${themeName(x.theme)} · ${x.mode}${x.accent ? ` · ${x.accent}` : ''}`

  return (
    <div className="flex flex-col gap-5">
      <LanguageCard />
      {/* Live sample in the real tokens — the whole app re-themes with it. */}
      <Card className="flex flex-wrap items-center gap-x-6 gap-y-3 p-4 sm:p-5">
        <div className="min-w-0 flex-1 basis-[260px]">
          <div className="eyebrow">Preview</div>
          <div className="display mt-0.5 text-[22px] leading-tight text-text-heading">The quick brown fox</div>
          <p className="mt-1 text-[13px] text-text-mid">
            Body text reads like this, with a{' '}
            <span className="font-semibold" style={{ color: 'var(--brand-ink)' }}>
              highlighted link
            </span>{' '}
            in the accent ink.
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <Button variant="primary" size="sm" type="button">
            Primary
          </Button>
          <Button variant="secondary" size="sm" type="button">
            Secondary
          </Button>
          <span className="rounded-full bg-accent px-2.5 py-1 text-[11.5px] font-semibold text-[color:var(--on-accent)]">Accent</span>
        </div>
      </Card>

      <Section title="Theme" hint="Three looks, each in light and dark">
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-3" role="radiogroup" aria-label="Theme">
          {THEMES.map((t) => {
            const active = a.theme === t.id
            const [ground, ink, accent] = t.swatch
            const geo = PREVIEW[t.id]
            return (
              <button
                key={t.id}
                type="button"
                role="radio"
                aria-checked={active}
                onClick={() => set('theme', t.id)}
                className={`group relative flex flex-col overflow-hidden rounded-xl border bg-card text-left transition-colors ${
                  active ? 'border-text-heading ring-1 ring-text-heading' : 'border-line hover:border-line-strong'
                }`}
              >
                {/* mini preview, drawn with the theme's own swatches */}
                <div className="flex h-[104px] flex-col gap-1.5 p-3" style={{ background: ground }} aria-hidden>
                  <div className="flex items-center justify-between">
                    <span style={{ fontFamily: geo.font, color: ink, fontSize: 19, lineHeight: 1, fontWeight: t.id === 'paper' ? 500 : 700 }}>
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
                    {t.name}
                    {active && <Check size={14} aria-hidden className="ml-auto text-text-heading" />}
                  </div>
                  <div className="mt-0.5 text-[11.5px] leading-snug text-text-muted">{t.blurb}</div>
                </div>
              </button>
            )
          })}
        </div>
      </Section>

      <Section title="Mode" hint="System follows your device">
        <Segmented<Mode>
          label="Colour mode"
          value={a.mode}
          onChange={(v) => set('mode', v)}
          options={[
            { value: 'light', label: 'Light', Icon: Sun },
            { value: 'dark', label: 'Dark', Icon: Moon },
            { value: 'system', label: 'System', Icon: Monitor },
          ]}
        />
        {a.mode === 'system' && (
          <p className="mt-2 text-[12px] text-text-muted">Your device currently prefers {resolveDark('system') ? 'dark' : 'light'}.</p>
        )}
      </Section>

      <Section title="Accent colour" hint="Inks are adjusted so text stays readable">
        <div className="flex flex-wrap items-center gap-2" role="radiogroup" aria-label="Accent colour">
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
            Theme default
          </button>
          {ACCENT_PRESETS.map((hex) => {
            const active = (a.accent ?? '').toLowerCase() === hex.toLowerCase()
            return (
              <button
                key={hex}
                type="button"
                role="radio"
                aria-checked={active}
                aria-label={`Accent ${hex}`}
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
              aria-label="Custom accent colour"
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
              aria-label="Accent hex code"
              className="w-[74px] bg-transparent font-mono text-[12.5px] text-text-primary outline-none"
            />
          </label>
        </div>

        {/* derived, readability-adjusted colours for this theme + mode */}
        <div className="mt-3 flex flex-wrap items-center gap-x-5 gap-y-2 rounded-lg border border-line bg-well px-3 py-2.5 text-[12px]">
          <span className="flex items-center gap-2">
            <span className="h-4 w-4 rounded" style={{ background: derived['--c-brand'] ? `rgb(${derived['--c-brand']})` : effectiveAccent }} aria-hidden />
            <span className="text-text-muted">Fill</span>
          </span>
          <span className="flex items-center gap-2">
            <span className="font-semibold" style={{ color: derived['--brand-ink'] ?? 'var(--brand-ink)' }}>
              Readable ink {derived['--brand-ink']?.toUpperCase()}
            </span>
          </span>
          <span className="text-text-faint">
            {a.accent ? 'Your colour' : 'The theme’s own accent'}, darkened or lightened for {dark ? 'dark' : 'light'} mode so text keeps 4.5:1
            contrast.
          </span>
        </div>
      </Section>

      <div className="grid grid-cols-1 gap-5 lg:grid-cols-3">
        <Section title="Text size">
          <Segmented<TextSize>
            label="Text size"
            value={a.textSize}
            onChange={(v) => set('textSize', v)}
            options={[
              { value: 'sm', label: 'S' },
              { value: 'md', label: 'M' },
              { value: 'lg', label: 'L' },
              { value: 'xl', label: 'XL' },
            ]}
          />
        </Section>
        <Section title="Corners">
          <Segmented<RadiusStyle>
            label="Corner style"
            value={a.radius}
            onChange={(v) => set('radius', v)}
            options={[
              { value: 'theme', label: 'Theme' },
              { value: 'sharp', label: 'Sharp' },
              { value: 'round', label: 'Round' },
            ]}
          />
        </Section>
        <Section title="Heading font">
          <Segmented<HeadingFont>
            label="Heading font"
            value={a.headingFont}
            onChange={(v) => set('headingFont', v)}
            options={[
              { value: 'theme', label: 'Theme' },
              { value: 'serif', label: 'Serif' },
              { value: 'sans', label: 'Sans' },
            ]}
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

      <div className="flex flex-wrap items-center gap-2 border-t border-line pt-4">
        <Button variant="secondary" size="sm" type="button" onClick={() => void reset()}>
          <RotateCcw size={13} aria-hidden /> Reset to default
        </Button>
        <span className="text-[12px] text-text-muted">
          {orgDefault ? `Organization default: ${describe(orgDefault)}` : 'No organization default — Paper & Ink, light.'}
        </span>
        {(canOrg || can('settings.appearance.org')) && (
          <span className="flex flex-wrap items-center gap-2 sm:ml-auto">
            {orgDefault && (
              <Button variant="ghost" size="sm" type="button" disabled={orgBusy} onClick={() => void publishOrg(true)}>
                Clear organization default
              </Button>
            )}
            <Button variant="primary" size="sm" type="button" disabled={orgBusy} onClick={() => void publishOrg()}>
              <Building2 size={13} aria-hidden /> Use as organization default
            </Button>
          </span>
        )}
      </div>
    </div>
  )
}

function Section({ title, hint, children }: { title: string; hint?: string; children: ReactNode }) {
  return (
    <section>
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
