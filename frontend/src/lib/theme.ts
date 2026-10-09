'use client'

/**
 * Appearance: which look (theme), light/dark mode, and the per-user overrides
 * (accent colour, text size, corner style, heading font).
 *
 * Everything resolves to classes + CSS variables on <html> (globals.css holds
 * the three theme palettes). The resolved variable set is cached in
 * localStorage next to the choice so the pre-paint script in layout.tsx can
 * apply it before React hydrates — no flash, and no colour maths in the
 * inline script.
 *
 * Persistence: localStorage for the instant path, the user's server profile
 * (`/api/auth/preferences`) so the choice follows them across devices, and an
 * organization default (`/api/settings/appearance`) for users who never chose.
 */

import { authHeaders, getAuth } from './auth'
import type { MessageKey } from '@/i18n'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

export type ThemeName = 'paper' | 'graphite' | 'soft'
export type Mode = 'light' | 'dark' | 'system'
export type TextSize = 'sm' | 'md' | 'lg' | 'xl'
export type RadiusStyle = 'theme' | 'sharp' | 'round'
export type HeadingFont = 'theme' | 'serif' | 'sans'

export interface Appearance {
  theme: ThemeName
  mode: Mode
  /** Hex override for the accent, or null for the theme's own. */
  accent: string | null
  textSize: TextSize
  radius: RadiusStyle
  headingFont: HeadingFont
}

export const DEFAULT_APPEARANCE: Appearance = {
  theme: 'paper',
  mode: 'light',
  accent: null,
  textSize: 'md',
  radius: 'theme',
  headingFont: 'theme',
}

/** `name` / `blurb` are translation keys — render with `t(theme.name)`. */
export const THEMES: { id: ThemeName; name: MessageKey; blurb: MessageKey; swatch: string[] }[] = [
  { id: 'paper', name: 'settings.appearance.themes.paper.name', blurb: 'settings.appearance.themes.paper.blurb', swatch: ['#FAF7F2', '#1A1A1A', '#FF5A1F'] },
  { id: 'graphite', name: 'settings.appearance.themes.graphite.name', blurb: 'settings.appearance.themes.graphite.blurb', swatch: ['#FFFFFF', '#18181B', '#6D5DFC'] },
  { id: 'soft', name: 'settings.appearance.themes.soft.name', blurb: 'settings.appearance.themes.soft.blurb', swatch: ['#F6F4FD', '#5B47D6', '#8AD9B5'] },
]

/** Option names shown in Settings → Appearance (translation keys; the stored values stay the ids). */
export const MODE_LABELS: Record<Mode, MessageKey> = {
  light: 'settings.appearance.modes.light',
  dark: 'settings.appearance.modes.dark',
  system: 'settings.appearance.modes.system',
}
export const TEXT_SIZE_LABELS: Record<TextSize, MessageKey> = {
  sm: 'settings.appearance.textSizes.sm',
  md: 'settings.appearance.textSizes.md',
  lg: 'settings.appearance.textSizes.lg',
  xl: 'settings.appearance.textSizes.xl',
}
export const RADIUS_LABELS: Record<RadiusStyle, MessageKey> = {
  theme: 'settings.appearance.radii.theme',
  sharp: 'settings.appearance.radii.sharp',
  round: 'settings.appearance.radii.round',
}
export const HEADING_FONT_LABELS: Record<HeadingFont, MessageKey> = {
  theme: 'settings.appearance.fonts.theme',
  serif: 'settings.appearance.fonts.serif',
  sans: 'settings.appearance.fonts.sans',
}

export const ACCENT_PRESETS = ['#FF5A1F', '#6D5DFC', '#2F7D4F', '#1F6FD1', '#D6336C', '#C8891A', '#0F8C83', '#1A1A1A']

const ZOOM: Record<TextSize, number> = { sm: 0.92, md: 1, lg: 1.08, xl: 1.16 }
const RADII: Record<Exclude<RadiusStyle, 'theme'>, number[]> = {
  sharp: [3, 4, 5, 6, 8],
  round: [10, 12, 16, 20, 26],
}
/** Page ground per theme/mode — the background accent text must read on. */
const GROUND: Record<ThemeName, { light: string; dark: string }> = {
  paper: { light: '#FAF7F2', dark: '#1C1A17' },
  graphite: { light: '#F7F7F8', dark: '#0E0E10' },
  soft: { light: '#F6F4FD', dark: '#17152A' },
}

export const STORE_KEY = 'ui-appearance'

// ── colour maths ─────────────────────────────────────────────────────────────

type RGB = [number, number, number]

function hexToRgb(hex: string): RGB | null {
  const m = /^#?([0-9a-f]{6})$/i.exec(hex.trim())
  if (!m) return null
  const n = parseInt(m[1], 16)
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255]
}

function luminance([r, g, b]: RGB): number {
  const ch = (v: number) => {
    const s = v / 255
    return s <= 0.03928 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4
  }
  return 0.2126 * ch(r) + 0.7152 * ch(g) + 0.0722 * ch(b)
}

export function contrast(a: RGB, b: RGB): number {
  const la = luminance(a)
  const lb = luminance(b)
  return (Math.max(la, lb) + 0.05) / (Math.min(la, lb) + 0.05)
}

function rgbToHsl([r, g, b]: RGB): [number, number, number] {
  const rn = r / 255, gn = g / 255, bn = b / 255
  const max = Math.max(rn, gn, bn), min = Math.min(rn, gn, bn)
  const l = (max + min) / 2
  if (max === min) return [0, 0, l]
  const d = max - min
  const s = l > 0.5 ? d / (2 - max - min) : d / (max + min)
  let h = 0
  if (max === rn) h = (gn - bn) / d + (gn < bn ? 6 : 0)
  else if (max === gn) h = (bn - rn) / d + 2
  else h = (rn - gn) / d + 4
  return [h / 6, s, l]
}

function hslToRgb([h, s, l]: [number, number, number]): RGB {
  if (s === 0) return [l * 255, l * 255, l * 255].map(Math.round) as RGB
  const q = l < 0.5 ? l * (1 + s) : l + s - l * s
  const p = 2 * l - q
  const f = (t: number) => {
    if (t < 0) t += 1
    if (t > 1) t -= 1
    if (t < 1 / 6) return p + (q - p) * 6 * t
    if (t < 1 / 2) return q
    if (t < 2 / 3) return p + (q - p) * (2 / 3 - t) * 6
    return p
  }
  return [f(h + 1 / 3), f(h), f(h - 1 / 3)].map((v) => Math.round(v * 255)) as RGB
}

/** Walk lightness toward (dark mode) lighter / (light mode) darker until the
 *  colour reaches `target` contrast on `ground`. */
function towardContrast(c: RGB, ground: RGB, target: number, dark: boolean): RGB {
  const [h, s, l0] = rgbToHsl(c)
  let l = l0
  let out = c
  for (let i = 0; i < 60 && contrast(out, ground) < target; i++) {
    l = dark ? Math.min(1, l + 0.02) : Math.max(0, l - 0.02)
    out = hslToRgb([h, s, l])
  }
  return out
}

const trip = (c: RGB) => c.join(' ')
const hex = (c: RGB) => '#' + c.map((v) => v.toString(16).padStart(2, '0')).join('')

/** CSS variables an accent override contributes. Text inks are pushed to
 *  4.5:1 and fills to 3:1 against the theme's ground, so any picked colour
 *  stays legible. */
export function accentVars(accentHex: string, theme: ThemeName, dark: boolean): Record<string, string> {
  const base = hexToRgb(accentHex)
  if (!base) return {}
  const ground = hexToRgb(dark ? GROUND[theme].dark : GROUND[theme].light)!
  const white: RGB = [255, 255, 255]
  const ink: RGB = [17, 17, 17]
  const fill = towardContrast(base, ground, 3, dark)
  const fill400 = towardContrast(base, ground, 3.6, dark)
  const text = towardContrast(base, ground, 4.6, dark)
  const text200 = towardContrast(base, ground, 6, dark)
  const onAccent = contrast(base, white) >= contrast(base, ink) ? '#ffffff' : '#111111'
  const vars: Record<string, string> = {
    '--accent': accentHex,
    '--on-accent': onAccent,
    '--focus': accentHex,
    '--c-brand': trip(fill),
    '--c-brand-400': trip(fill400),
    '--c-brand-300': trip(text),
    '--c-brand-200': trip(text200),
    '--brand-ink': hex(text),
  }
  // Paper & Ink keeps its ink primary (that IS the look); the other two
  // paint primary actions in the accent.
  if (theme !== 'paper') {
    const primary = contrast(fill, white) >= 4.5 ? fill : towardContrast(base, white, 4.5, false)
    vars['--primary'] = hex(dark ? fill : primary)
    vars['--on-primary'] = contrast(dark ? fill : primary, white) >= 4.5 ? '#ffffff' : '#111111'
    vars['--nav-active-text'] = hex(text)
  }
  return vars
}

// ── resolve + apply ──────────────────────────────────────────────────────────

export function resolveDark(mode: Mode): boolean {
  if (mode === 'dark') return true
  if (mode === 'light') return false
  return typeof window !== 'undefined' && !!window.matchMedia?.('(prefers-color-scheme: dark)').matches
}

export function resolveVars(a: Appearance, dark: boolean): Record<string, string> {
  const vars: Record<string, string> = { '--ui-zoom': String(ZOOM[a.textSize] ?? 1) }
  if (a.radius !== 'theme') {
    const r = RADII[a.radius]
    ;['--r-md', '--r-lg', '--r-xl', '--r-2xl', '--r-3xl'].forEach((k, i) => (vars[k] = `${r[i]}px`))
  }
  if (a.headingFont === 'serif') {
    vars['--font-display'] = "'Fraunces Variable', 'Fraunces', Georgia, serif"
    vars['--display-weight'] = '500'
    vars['--display-tracking'] = '-0.02em'
  } else if (a.headingFont === 'sans') {
    vars['--font-display'] = 'var(--font-sans)'
    vars['--display-weight'] = '700'
    vars['--display-tracking'] = '-0.02em'
  }
  if (a.accent) Object.assign(vars, accentVars(a.accent, a.theme, dark))
  return vars
}

let applied: string[] = []

export function applyAppearance(a: Appearance): void {
  if (typeof document === 'undefined') return
  const root = document.documentElement
  const dark = resolveDark(a.mode)
  root.classList.remove('theme-paper', 'theme-graphite', 'theme-soft', 'light', 'dark')
  root.classList.add(`theme-${a.theme}`, dark ? 'dark' : 'light')
  for (const k of applied) root.style.removeProperty(k)
  const vars = resolveVars(a, dark)
  for (const [k, v] of Object.entries(vars)) root.style.setProperty(k, v)
  applied = Object.keys(vars)
  root.style.colorScheme = dark ? 'dark' : 'light'
  try {
    localStorage.setItem(STORE_KEY, JSON.stringify({ prefs: a, dark, vars, cls: `theme-${a.theme}` }))
    localStorage.setItem('theme', dark ? 'dark' : 'light')
  } catch {
    /* storage disabled: the look still applies for this session */
  }
  window.dispatchEvent(new CustomEvent('appearance-change', { detail: a }))
}

export function normalize(raw: unknown): Appearance {
  const r = (raw && typeof raw === 'object' ? raw : {}) as Partial<Appearance>
  const pick = <T extends string>(v: unknown, ok: readonly T[], d: T): T => (ok.includes(v as T) ? (v as T) : d)
  return {
    theme: pick(r.theme, ['paper', 'graphite', 'soft'] as const, DEFAULT_APPEARANCE.theme),
    mode: pick(r.mode, ['light', 'dark', 'system'] as const, DEFAULT_APPEARANCE.mode),
    accent: typeof r.accent === 'string' && hexToRgb(r.accent) ? r.accent : null,
    textSize: pick(r.textSize, ['sm', 'md', 'lg', 'xl'] as const, 'md'),
    radius: pick(r.radius, ['theme', 'sharp', 'round'] as const, 'theme'),
    headingFont: pick(r.headingFont, ['theme', 'serif', 'sans'] as const, 'theme'),
  }
}

/** The choice currently cached in this browser (pre-paint already applied it). */
export function storedAppearance(): Appearance | null {
  try {
    const raw = localStorage.getItem(STORE_KEY)
    if (raw) return normalize(JSON.parse(raw).prefs)
    // Pre-appearance installs only stored a light/dark choice.
    const legacy = localStorage.getItem('theme')
    if (legacy === 'dark' || legacy === 'light') return { ...DEFAULT_APPEARANCE, mode: legacy }
  } catch {
    /* ignore */
  }
  return null
}

// ── server persistence ───────────────────────────────────────────────────────

/** Load the signed-in user's saved look (falling back to the organization
 *  default) and apply it. Called once by the shell after sign-in. */
export async function syncAppearanceFromServer(): Promise<Appearance | null> {
  if (!getAuth()) return null
  try {
    const res = await fetch(`${API}/auth/preferences`, { headers: { ...authHeaders() } })
    if (!res.ok) return null
    const data = (await res.json()) as { appearance?: unknown; org_default?: unknown }
    const chosen = data.appearance ?? data.org_default
    if (!chosen) return null
    const a = normalize(chosen)
    applyAppearance(a)
    return a
  } catch {
    return null
  }
}

export async function saveAppearance(a: Appearance): Promise<boolean> {
  applyAppearance(a)
  if (!getAuth()) return true
  try {
    const res = await fetch(`${API}/auth/preferences`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json', ...authHeaders() },
      body: JSON.stringify({ appearance: a }),
    })
    return res.ok
  } catch {
    return false
  }
}

export async function saveOrgDefaultAppearance(a: Appearance): Promise<{ ok: boolean; error?: string }> {
  try {
    const res = await fetch(`${API}/settings/appearance`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json', ...authHeaders() },
      body: JSON.stringify({ appearance: a }),
    })
    if (res.ok) return { ok: true }
    const body = await res.json().catch(() => ({}))
    return { ok: false, error: (body as { error?: string }).error || `HTTP ${res.status}` }
  } catch {
    // no `error`: the caller shows its own (translated) "server unreachable" copy
    return { ok: false }
  }
}
