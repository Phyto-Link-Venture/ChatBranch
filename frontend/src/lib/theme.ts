'use client'

/**
 * Appearance: which look (theme), light/dark mode, and the per-user overrides —
 * accent colour, text size (presets or a free scale), corner style, body and
 * heading fonts, line height, density (padding / gaps / margins), page width
 * and margins, card padding, the navigation layout (sidebar left / right, or a
 * top bar on desktop), reduced motion and the card surface style.
 *
 * Everything resolves to classes, data attributes and CSS variables on <html>
 * (globals.css holds the ten theme palettes and the rules that read the
 * attributes). The resolved set is cached in localStorage next to the choice
 * so the pre-paint script (lib/themeScript.ts) can apply it before React
 * hydrates — no flash, and no colour maths in the inline script.
 *
 * Persistence: localStorage for the instant path, the user's server profile
 * (`/api/auth/preferences`) so the choice follows them across devices, and an
 * organization default (`/api/settings/appearance`) for users who never chose.
 * The server validates every key against the same vocabularies
 * (SettingsEndpoints.Appearance.cs — keep both in step).
 */

import { API } from '@/lib/apiBase'
import { authHeaders, getAuth } from './auth'
import type { MessageKey } from '@/i18n'

export const THEME_IDS = ['paper', 'graphite', 'soft', 'ocean', 'forest', 'midnight', 'rose', 'sand', 'nord', 'contrast', 'pixel', 'riso', 'lacquer', 'clay'] as const
export type ThemeName = (typeof THEME_IDS)[number]
export type Mode = 'light' | 'dark' | 'system'
export type TextSize = 'sm' | 'md' | 'lg' | 'xl'
export type RadiusStyle = 'theme' | 'square' | 'sharp' | 'round'

export const FONT_IDS = [
  'system', 'inter', 'schibsted', 'geist', 'jakarta', 'manrope', 'dmsans', 'plex', 'space', 'nunito', 'notosc',
  'fraunces', 'sourceserif', 'lora',
] as const
export type FontId = (typeof FONT_IDS)[number]
export type BodyFont = 'theme' | FontId
/** `serif` (Fraunces) and `sans` (= the body font) are the pre-2026-10 values, still accepted. */
export type HeadingFont = 'theme' | 'serif' | 'sans' | FontId
export type LineHeight = 'tight' | 'normal' | 'relaxed'
export type Density = 'compact' | 'comfortable' | 'spacious'
export type ContentWidth = 'narrow' | 'normal' | 'wide' | 'full'
export type Gutter = 'sm' | 'md' | 'lg'
export type CardPadding = 'sm' | 'md' | 'lg'
export type NavLayout = 'side' | 'top'
export type NavSide = 'left' | 'right'
export type NavWidth = 'narrow' | 'normal' | 'wide'
export type NavStart = 'expanded' | 'collapsed'
export type Motion = 'system' | 'reduce' | 'full'
export type SurfaceStyle = 'theme' | 'flat' | 'bordered' | 'shadow'

export interface Appearance {
  theme: ThemeName
  mode: Mode
  /** Hex override for the accent, or null for the theme's own. */
  accent: string | null
  textSize: TextSize
  /** Free text scale in percent (FONT_SCALE_RANGE), or null = the `textSize` preset. */
  fontScale: number | null
  radius: RadiusStyle
  headingFont: HeadingFont
  bodyFont: BodyFont
  lineHeight: LineHeight
  density: Density
  /** Free spacing scale in percent (SPACING_RANGE), or null = the `density` preset. */
  spacing: number | null
  contentWidth: ContentWidth
  gutter: Gutter
  cardPadding: CardPadding
  navLayout: NavLayout
  navSide: NavSide
  navWidth: NavWidth
  navStart: NavStart
  motion: Motion
  surface: SurfaceStyle
}

/** The look before anyone chooses — the values ChatBranch always had. */
export const DEFAULT_APPEARANCE: Appearance = {
  theme: 'paper',
  mode: 'light',
  accent: null,
  textSize: 'md',
  fontScale: null,
  radius: 'theme',
  headingFont: 'theme',
  bodyFont: 'theme',
  lineHeight: 'normal',
  density: 'comfortable',
  spacing: null,
  contentWidth: 'full',
  gutter: 'md',
  cardPadding: 'md',
  navLayout: 'side',
  navSide: 'left',
  navWidth: 'normal',
  navStart: 'expanded',
  motion: 'system',
  surface: 'theme',
}

export const FONT_SCALE_RANGE = { min: 85, max: 130 } as const
export const SPACING_RANGE = { min: 70, max: 140 } as const

/** A theme's mini preview: page ground, card, heading ink, primary action, accent, hairline. */
export interface Swatch {
  app: string
  card: string
  ink: string
  primary: string
  accent: string
  line: string
}

export interface ThemeDef {
  id: ThemeName
  /** Translation keys — render with `t(theme.name)`. */
  name: MessageKey
  blurb: MessageKey
  light: Swatch
  dark: Swatch
  /** Preview geometry: corner radius (px) and the heading face. */
  radius: number
  font: string
  weight: number
  /** The primary action is ink (not the accent) — an accent override keeps it. */
  inkPrimary?: boolean
}

const sw = (app: string, card: string, ink: string, primary: string, accent: string, line: string): Swatch => ({ app, card, ink, primary, accent, line })
const CJK = "'PingFang SC', 'Hiragino Sans GB', 'Microsoft YaHei', 'Noto Sans SC Variable', 'Noto Sans CJK SC', sans-serif"
const SANS_TAIL = `system-ui, -apple-system, 'Segoe UI', ${CJK}`
const SERIF_TAIL = `Georgia, 'Songti SC', 'Noto Serif CJK SC', ${CJK}`

// Swatch hex values mirror the theme tokens in globals.css (theme data, not component colours).
export const THEMES: ThemeDef[] = [
  {
    id: 'paper', name: 'settings.appearance.themes.paper.name', blurb: 'settings.appearance.themes.paper.blurb',
    light: sw('#FAF7F2', '#FFFDF9', '#141210', '#1A1A1A', '#FF5A1F', '#E4DDD1'),
    dark: sw('#1C1A17', '#26231E', '#FBF8F2', '#F2EDE4', '#FF7A45', '#38332C'),
    radius: 3, font: `'Fraunces Variable', 'Fraunces', ${SERIF_TAIL}`, weight: 500, inkPrimary: true,
  },
  {
    id: 'graphite', name: 'settings.appearance.themes.graphite.name', blurb: 'settings.appearance.themes.graphite.blurb',
    light: sw('#F7F7F8', '#FFFFFF', '#0F0F11', '#6D5DFC', '#6D5DFC', '#E6E6EA'),
    dark: sw('#0E0E10', '#18181B', '#FFFFFF', '#6A5AF2', '#8B7DFF', '#26262B'),
    radius: 6, font: `'Geist Variable', 'Geist', ${SANS_TAIL}`, weight: 650,
  },
  {
    id: 'soft', name: 'settings.appearance.themes.soft.name', blurb: 'settings.appearance.themes.soft.blurb',
    light: sw('#F6F4FD', '#FFFFFF', '#1B1830', '#5B47D6', '#8AD9B5', '#E7E3F4'),
    dark: sw('#17152A', '#23203D', '#FFFFFF', '#A99BFF', '#8AD9B5', '#312D52'),
    radius: 12, font: `'Nunito Variable', 'Nunito', ${SANS_TAIL}`, weight: 800,
  },
  {
    id: 'ocean', name: 'settings.appearance.themes.ocean.name', blurb: 'settings.appearance.themes.ocean.blurb',
    light: sw('#F4F8FB', '#FFFFFF', '#0A1A28', '#0B66C3', '#0EA5E9', '#DCE6EE'),
    dark: sw('#0B1622', '#132233', '#FFFFFF', '#4DA3F5', '#38BDF8', '#1F3144'),
    radius: 8, font: `'Plus Jakarta Sans Variable', 'Plus Jakarta Sans', ${SANS_TAIL}`, weight: 700,
  },
  {
    id: 'forest', name: 'settings.appearance.themes.forest.name', blurb: 'settings.appearance.themes.forest.blurb',
    light: sw('#F5F7F2', '#FCFDFB', '#101A12', '#1E6B3A', '#8BC34A', '#DCE3D6'),
    dark: sw('#111812', '#1A251C', '#F7FBF5', '#6CCB8A', '#A5D66B', '#263327'),
    radius: 5, font: `'Lora Variable', 'Lora', ${SERIF_TAIL}`, weight: 600,
  },
  {
    id: 'midnight', name: 'settings.appearance.themes.midnight.name', blurb: 'settings.appearance.themes.midnight.blurb',
    light: sw('#F3F5FA', '#FFFFFF', '#0B1026', '#1E2A78', '#4F5BFF', '#DFE3EF'),
    dark: sw('#070B1A', '#10172F', '#FFFFFF', '#7C87FF', '#5EEAD4', '#1B2342'),
    radius: 7, font: `'Inter Variable', 'Inter', ${SANS_TAIL}`, weight: 650, inkPrimary: true,
  },
  {
    id: 'rose', name: 'settings.appearance.themes.rose.name', blurb: 'settings.appearance.themes.rose.blurb',
    light: sw('#FCF6F7', '#FFFFFF', '#1F1216', '#B8285A', '#F472A0', '#F0DFE3'),
    dark: sw('#1A1014', '#26181E', '#FFFFFF', '#F27AA3', '#F9A8C4', '#3A262D'),
    radius: 12, font: `'Fraunces Variable', 'Fraunces', ${SERIF_TAIL}`, weight: 500,
  },
  {
    id: 'sand', name: 'settings.appearance.themes.sand.name', blurb: 'settings.appearance.themes.sand.blurb',
    light: sw('#F7F1E6', '#FDF9F2', '#20180F', '#9A4A12', '#D9893B', '#E5D8C2'),
    dark: sw('#1B1712', '#29231B', '#FFF9EF', '#E8A465', '#E8A465', '#3A3226'),
    radius: 4, font: `'Source Serif 4 Variable', 'Source Serif 4', ${SERIF_TAIL}`, weight: 600,
  },
  {
    id: 'nord', name: 'settings.appearance.themes.nord.name', blurb: 'settings.appearance.themes.nord.blurb',
    light: sw('#ECEFF4', '#F8F9FB', '#242933', '#3B6E9E', '#88C0D0', '#D8DEE9'),
    dark: sw('#2E3440', '#3B4252', '#FFFFFF', '#88C0D0', '#8FBCBB', '#434C5E'),
    radius: 5, font: `'IBM Plex Sans Variable', 'IBM Plex Sans', ${SANS_TAIL}`, weight: 600,
  },
  {
    id: 'contrast', name: 'settings.appearance.themes.contrast.name', blurb: 'settings.appearance.themes.contrast.blurb',
    light: sw('#FFFFFF', '#FFFFFF', '#000000', '#000000', '#0040B0', '#767676'),
    dark: sw('#000000', '#0A0A0A', '#FFFFFF', '#FFFFFF', '#FFD60A', '#8A8A8A'),
    radius: 3, font: `'Inter Variable', 'Inter', ${SANS_TAIL}`, weight: 800, inkPrimary: true,
  },
  {
    id: 'pixel', name: 'settings.appearance.themes.pixel.name', blurb: 'settings.appearance.themes.pixel.blurb',
    light: sw('#FFFFFF', '#FFFFFF', '#0E0E10', '#1E4BD2', '#F0542D', '#0E0E10'),
    dark: sw('#111214', '#17191C', '#F4F5F7', '#5B82FF', '#FF6E4A', '#8A8F99'),
    radius: 0, font: `'Pixelify Sans Variable', 'Pixelify Sans', ${SANS_TAIL}`, weight: 700,
  },
  {
    id: 'riso', name: 'settings.appearance.themes.riso.name', blurb: 'settings.appearance.themes.riso.blurb',
    light: sw('#F3EFE6', '#FBF9F4', '#1A1A1A', '#006E70', '#FF48B0', '#1A1A1A'),
    dark: sw('#141416', '#1C1C1F', '#F1EEE8', '#19C3C6', '#FF5CBC', '#BDB7AE'),
    radius: 0, font: `'Dela Gothic One', 'Arial Black', ${SANS_TAIL}`, weight: 400,
  },
  {
    id: 'lacquer', name: 'settings.appearance.themes.lacquer.name', blurb: 'settings.appearance.themes.lacquer.blurb',
    light: sw('#F3EADB', '#FBF5EA', '#2A100D', '#7A1E1E', '#C9A24A', '#E0D0B4'),
    dark: sw('#140C0B', '#1D1211', '#F6EBDD', '#A3302C', '#C9A24A', '#3B2C1F'),
    radius: 14, font: `'Cormorant Garamond Variable', 'Cormorant Garamond', ${SERIF_TAIL}`, weight: 600,
  },
  {
    id: 'clay', name: 'settings.appearance.themes.clay.name', blurb: 'settings.appearance.themes.clay.blurb',
    light: sw('#FFF6E9', '#FFFBF5', '#2A1E4A', '#6B4BD8', '#7EE0B8', '#EADFCF'),
    dark: sw('#17112B', '#261C45', '#FBF7FF', '#9C82FF', '#6FD9AE', '#3A2E62'),
    radius: 22, font: `'Fredoka Variable', 'Fredoka', ${SANS_TAIL}`, weight: 600,
  },
]

export interface FontDef {
  id: FontId
  /** The font's own name (a proper noun, shown as is), or a translation key for "System". */
  label: string | MessageKey
  translate?: boolean
  stack: string
  kind: 'sans' | 'serif'
  /** Heading weight / tracking when used as the heading face. */
  weight: number
  tracking: string
}

/** Every face is self-hosted (@fontsource-variable, imported in app/layout.tsx;
 *  the browser only downloads a face once something renders in it). Noto Sans
 *  SC's stylesheet is large, so lib/theme.ts loads it only when chosen. */
export const FONTS: FontDef[] = [
  { id: 'system', label: 'settings.appearance.fontNames.system', translate: true, stack: `${SANS_TAIL}`, kind: 'sans', weight: 700, tracking: '-0.02em' },
  { id: 'inter', label: 'Inter', stack: `'Inter Variable', 'Inter', ${SANS_TAIL}`, kind: 'sans', weight: 700, tracking: '-0.025em' },
  { id: 'schibsted', label: 'Schibsted Grotesk', stack: `'Schibsted Grotesk Variable', 'Schibsted Grotesk', ${SANS_TAIL}`, kind: 'sans', weight: 700, tracking: '-0.02em' },
  { id: 'geist', label: 'Geist', stack: `'Geist Variable', 'Geist', ${SANS_TAIL}`, kind: 'sans', weight: 650, tracking: '-0.03em' },
  { id: 'jakarta', label: 'Plus Jakarta Sans', stack: `'Plus Jakarta Sans Variable', 'Plus Jakarta Sans', ${SANS_TAIL}`, kind: 'sans', weight: 700, tracking: '-0.025em' },
  { id: 'manrope', label: 'Manrope', stack: `'Manrope Variable', 'Manrope', ${SANS_TAIL}`, kind: 'sans', weight: 700, tracking: '-0.02em' },
  { id: 'dmsans', label: 'DM Sans', stack: `'DM Sans Variable', 'DM Sans', ${SANS_TAIL}`, kind: 'sans', weight: 700, tracking: '-0.02em' },
  { id: 'plex', label: 'IBM Plex Sans', stack: `'IBM Plex Sans Variable', 'IBM Plex Sans', ${SANS_TAIL}`, kind: 'sans', weight: 600, tracking: '-0.02em' },
  { id: 'space', label: 'Space Grotesk', stack: `'Space Grotesk Variable', 'Space Grotesk', ${SANS_TAIL}`, kind: 'sans', weight: 600, tracking: '-0.02em' },
  { id: 'nunito', label: 'Nunito', stack: `'Nunito Variable', 'Nunito', ${SANS_TAIL}`, kind: 'sans', weight: 800, tracking: '-0.015em' },
  { id: 'notosc', label: 'Noto Sans SC · 思源黑体', stack: `'Noto Sans SC Variable', 'Noto Sans SC', ${SANS_TAIL}`, kind: 'sans', weight: 700, tracking: '-0.01em' },
  { id: 'fraunces', label: 'Fraunces', stack: `'Fraunces Variable', 'Fraunces', ${SERIF_TAIL}`, kind: 'serif', weight: 500, tracking: '-0.02em' },
  { id: 'sourceserif', label: 'Source Serif 4', stack: `'Source Serif 4 Variable', 'Source Serif 4', ${SERIF_TAIL}`, kind: 'serif', weight: 600, tracking: '-0.015em' },
  { id: 'lora', label: 'Lora', stack: `'Lora Variable', 'Lora', ${SERIF_TAIL}`, kind: 'serif', weight: 600, tracking: '-0.01em' },
]
export const fontDef = (id: string): FontDef | undefined => FONTS.find((f) => f.id === id)

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
  square: 'settings.appearance.radii.square',
  sharp: 'settings.appearance.radii.sharp',
  round: 'settings.appearance.radii.round',
}

export const ACCENT_PRESETS = ['#FF5A1F', '#6D5DFC', '#2F7D4F', '#1F6FD1', '#D6336C', '#C8891A', '#0F8C83', '#1A1A1A']

const ZOOM: Record<TextSize, number> = { sm: 0.92, md: 1, lg: 1.08, xl: 1.16 }
const SPACE: Record<Density, number> = { compact: 0.8, comfortable: 1, spacious: 1.2 }
const RADII: Record<Exclude<RadiusStyle, 'theme'>, number[]> = {
  square: [0, 0, 0, 0, 0],
  sharp: [3, 4, 5, 6, 8],
  round: [10, 12, 16, 20, 26],
}
const LINE_HEIGHT: Record<LineHeight, number> = { tight: 1.35, normal: 1.5, relaxed: 1.7 }
const CONTENT_MAX: Record<ContentWidth, string> = { narrow: '960px', normal: '1200px', wide: '1480px', full: '100%' }
const GUTTER: Record<Gutter, number> = { sm: 0.6, md: 1, lg: 1.5 }
const CARD_PAD: Record<CardPadding, number> = { sm: 0.8, md: 1, lg: 1.25 }
const NAV_W: Record<NavWidth, string> = { narrow: '216px', normal: '248px', wide: '288px' }

/** The text scale (percent) a preset stands for — the slider starts there. */
export const presetScale = (s: TextSize) => Math.round(ZOOM[s] * 100)
export const densityScale = (d: Density) => Math.round(SPACE[d] * 100)

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
  const def = THEMES.find((th) => th.id === theme) ?? THEMES[0]
  const ground = hexToRgb(dark ? def.dark.app : def.light.app)!
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
  // Ink-primary looks (Paper & Ink, Midnight, High contrast) keep their ink
  // primary (that IS the look); the others paint primary actions in the accent.
  if (!def.inkPrimary) {
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

/** The effective text scale (1 = 100 %). */
export function textScale(a: Appearance): number {
  return a.fontScale != null ? a.fontScale / 100 : ZOOM[a.textSize] ?? 1
}

/** The effective spacing scale (1 = 100 %). */
export function spaceScale(a: Appearance): number {
  return a.spacing != null ? a.spacing / 100 : SPACE[a.density] ?? 1
}

export function resolveVars(a: Appearance, dark: boolean): Record<string, string> {
  const vars: Record<string, string> = {}
  const set = (k: string, v: string, dflt: string) => {
    if (v !== dflt) vars[k] = v
  }
  set('--ui-zoom', String(textScale(a)), '1')
  set('--space', String(spaceScale(a)), '1')
  set('--lh', String(LINE_HEIGHT[a.lineHeight] ?? 1.5), '1.5')
  set('--content-max', CONTENT_MAX[a.contentWidth] ?? '100%', '100%')
  set('--gutter', String(GUTTER[a.gutter] ?? 1), '1')
  set('--card-pad', String(CARD_PAD[a.cardPadding] ?? 1), '1')
  set('--nav-w', NAV_W[a.navWidth] ?? '248px', '248px')
  if (a.radius !== 'theme') {
    const r = RADII[a.radius]
    ;['--r-md', '--r-lg', '--r-xl', '--r-2xl', '--r-3xl'].forEach((k, i) => (vars[k] = `${r[i]}px`))
  }
  const body = a.bodyFont !== 'theme' ? fontDef(a.bodyFont) : undefined
  if (body) vars['--font-sans'] = body.stack
  if (a.headingFont === 'sans') {
    vars['--font-display'] = 'var(--font-sans)'
    vars['--display-weight'] = '700'
    vars['--display-tracking'] = '-0.02em'
  } else if (a.headingFont !== 'theme') {
    const h = fontDef(a.headingFont === 'serif' ? 'fraunces' : a.headingFont)
    if (h) {
      vars['--font-display'] = h.stack
      vars['--display-weight'] = String(h.weight)
      vars['--display-tracking'] = h.tracking
    }
  }
  if (a.accent) Object.assign(vars, accentVars(a.accent, a.theme, dark))
  return vars
}

/** Data attributes on <html> that globals.css reads (layout, motion, surfaces).
 *  Defaults are left off so an untouched look carries no attributes. */
export function resolveAttrs(a: Appearance): Record<string, string> {
  const attrs: Record<string, string> = {}
  if (a.navLayout === 'top') attrs['data-nav'] = 'top'
  if (a.navSide === 'right') attrs['data-nav-side'] = 'right'
  if (a.motion !== 'system') attrs['data-motion'] = a.motion
  if (a.surface !== 'theme') attrs['data-surface'] = a.surface
  return attrs
}
const ATTRS = ['data-nav', 'data-nav-side', 'data-motion', 'data-surface']

let applied: string[] = []
let cjkLoaded = false

/** Noto Sans SC's stylesheet is ~100 KB of unicode ranges — only fetched when chosen. */
function loadLazyFonts(a: Appearance): void {
  if (cjkLoaded || (a.bodyFont !== 'notosc' && a.headingFont !== 'notosc')) return
  cjkLoaded = true
  void import('@fontsource-variable/noto-sans-sc/wght.css').catch(() => {
    cjkLoaded = false
  })
}

export function applyAppearance(a: Appearance): void {
  if (typeof document === 'undefined') return
  const root = document.documentElement
  const dark = resolveDark(a.mode)
  root.classList.remove(...THEME_IDS.map((id) => `theme-${id}`), 'light', 'dark')
  root.classList.add(`theme-${a.theme}`, dark ? 'dark' : 'light')
  for (const k of applied) root.style.removeProperty(k)
  const vars = resolveVars(a, dark)
  for (const [k, v] of Object.entries(vars)) root.style.setProperty(k, v)
  applied = Object.keys(vars)
  const attrs = resolveAttrs(a)
  for (const k of ATTRS) {
    if (attrs[k]) root.setAttribute(k, attrs[k])
    else root.removeAttribute(k)
  }
  root.style.colorScheme = dark ? 'dark' : 'light'
  loadLazyFonts(a)
  try {
    localStorage.setItem(STORE_KEY, JSON.stringify({ prefs: a, dark, vars, attrs, cls: `theme-${a.theme}` }))
    localStorage.setItem('theme', dark ? 'dark' : 'light')
  } catch {
    /* storage disabled: the look still applies for this session */
  }
  window.dispatchEvent(new CustomEvent('appearance-change', { detail: a }))
}

const clampPct = (v: unknown, r: { min: number; max: number }): number | null =>
  typeof v === 'number' && Number.isFinite(v) ? Math.min(r.max, Math.max(r.min, Math.round(v))) : null

/** Any stored / server value → a complete Appearance. Unknown values fall back
 *  to the defaults, so a look saved by an older or newer build always loads. */
export function normalize(raw: unknown): Appearance {
  const r = (raw && typeof raw === 'object' ? raw : {}) as Record<string, unknown>
  const d = DEFAULT_APPEARANCE
  const pick = <T extends string>(v: unknown, ok: readonly T[], dflt: T): T => (ok.includes(v as T) ? (v as T) : dflt)
  const heading = r.headingFont === 'serif' ? 'fraunces' : r.headingFont
  return {
    theme: pick(r.theme, THEME_IDS, d.theme),
    mode: pick(r.mode, ['light', 'dark', 'system'] as const, d.mode),
    accent: typeof r.accent === 'string' && hexToRgb(r.accent) ? r.accent : null,
    textSize: pick(r.textSize, ['sm', 'md', 'lg', 'xl'] as const, d.textSize),
    fontScale: clampPct(r.fontScale, FONT_SCALE_RANGE),
    radius: pick(r.radius, ['theme', 'square', 'sharp', 'round'] as const, d.radius),
    headingFont: pick<HeadingFont>(heading, ['theme', 'sans', ...FONT_IDS], d.headingFont),
    bodyFont: pick<BodyFont>(r.bodyFont, ['theme', ...FONT_IDS], d.bodyFont),
    lineHeight: pick(r.lineHeight, ['tight', 'normal', 'relaxed'] as const, d.lineHeight),
    density: pick(r.density, ['compact', 'comfortable', 'spacious'] as const, d.density),
    spacing: clampPct(r.spacing, SPACING_RANGE),
    contentWidth: pick(r.contentWidth, ['narrow', 'normal', 'wide', 'full'] as const, d.contentWidth),
    gutter: pick(r.gutter, ['sm', 'md', 'lg'] as const, d.gutter),
    cardPadding: pick(r.cardPadding, ['sm', 'md', 'lg'] as const, d.cardPadding),
    navLayout: pick(r.navLayout, ['side', 'top'] as const, d.navLayout),
    navSide: pick(r.navSide, ['left', 'right'] as const, d.navSide),
    navWidth: pick(r.navWidth, ['narrow', 'normal', 'wide'] as const, d.navWidth),
    navStart: pick(r.navStart, ['expanded', 'collapsed'] as const, d.navStart),
    motion: pick(r.motion, ['system', 'reduce', 'full'] as const, d.motion),
    surface: pick(r.surface, ['theme', 'flat', 'bordered', 'shadow'] as const, d.surface),
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

/** How many of the Advanced settings differ from the defaults. */
export function customisedCount(a: Appearance): number {
  const keys: (keyof Appearance)[] = [
    'radius', 'headingFont', 'bodyFont', 'lineHeight', 'density', 'spacing', 'contentWidth', 'gutter',
    'cardPadding', 'navLayout', 'navSide', 'navWidth', 'navStart', 'motion', 'surface',
  ]
  return keys.filter((k) => a[k] !== DEFAULT_APPEARANCE[k]).length
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
