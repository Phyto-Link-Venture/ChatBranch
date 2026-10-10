/** @type {import('tailwindcss').Config} */

// Accent scales resolve to "R G B" triplets in globals.css so opacity
// modifiers (bg-brand-400/10) keep working and the scale flips with the theme.
const rgbVar = (name) => `rgb(var(${name}) / <alpha-value>)`

// Density (Settings → Appearance → Advanced): padding, margins, gaps and
// space-* scale by --sp (= the user's --space, times --card-pad inside a card;
// globals.css). Widths, heights and insets keep the stock scale, so controls
// keep their size and only the air around them changes. 0 and px stay exact.
const defaultSpacing = require('tailwindcss/defaultTheme').spacing
const densitySpacing = Object.fromEntries(
  Object.entries(defaultSpacing).map(([k, v]) => [k, /rem$/.test(v) ? `calc(${v} * var(--sp, 1))` : v]),
)

module.exports = {
  darkMode: 'class', // html.dark / html.light — set by the pre-paint script + ThemeToggle
  content: [
    './src/pages/**/*.{js,ts,jsx,tsx,mdx}',
    './src/components/**/*.{js,ts,jsx,tsx,mdx}',
    './src/app/**/*.{js,ts,jsx,tsx,mdx}',
  ],
  theme: {
    padding: densitySpacing,
    margin: { auto: 'auto', ...densitySpacing },
    gap: densitySpacing,
    space: densitySpacing,
    scrollMargin: densitySpacing,
    scrollPadding: densitySpacing,
    extend: {
      colors: {
        // Surfaces (theme-aware — resolve to CSS vars set in globals.css)
        app: 'var(--app)',
        surface: 'var(--surface)',
        panel: 'var(--panel)',
        well: 'var(--well)',
        card: 'var(--card)',
        // Text
        'text-primary': 'var(--text-primary)',
        'text-heading': 'var(--text-heading)',
        'text-high': 'var(--text-high)',
        'text-mid': 'var(--text-mid)',
        'text-muted': 'var(--text-muted)',
        'text-faint': 'var(--text-faint)',
        // Neutral line / fill tokens (theme-aware)
        line: 'var(--line)',
        'line-strong': 'var(--line-strong)',
        fill: 'var(--fill)',
        'fill-hover': 'var(--fill-hover)',
        'fill-subtle': 'var(--fill-subtle)',
        // Primary action (ink in light, cream in dark) + its foreground
        primary: 'var(--primary)',
        'on-primary': 'var(--on-primary)',
        accent: 'var(--accent)',
        // Brand = TANGERINE. DEFAULT/500 fills, 400 stronger fill/border,
        // 300/200 readable text ink.
        brand: {
          DEFAULT: rgbVar('--c-brand'),
          500: rgbVar('--c-brand'),
          400: rgbVar('--c-brand-400'),
          300: rgbVar('--c-brand-300'),
          200: rgbVar('--c-brand-200'),
        },
        cyan: { DEFAULT: rgbVar('--c-cyan'), 600: rgbVar('--c-cyan-600'), 300: rgbVar('--c-cyan-300') },
        purple: {
          DEFAULT: rgbVar('--c-purple'),
          700: rgbVar('--c-purple-700'),
          600: rgbVar('--c-purple-600'),
          400: rgbVar('--c-purple-400'),
          300: rgbVar('--c-purple-300'),
        },
        green: { DEFAULT: rgbVar('--c-green'), 600: rgbVar('--c-green-600'), 300: rgbVar('--c-green-300') },
        amber: {
          DEFAULT: rgbVar('--c-amber'),
          600: rgbVar('--c-amber-600'),
          400: rgbVar('--c-amber-400'),
          300: rgbVar('--c-amber-300'),
        },
        red: { DEFAULT: rgbVar('--c-red'), 400: rgbVar('--c-red-400'), 300: rgbVar('--c-red-300') },
        blue: { DEFAULT: rgbVar('--c-blue'), 400: rgbVar('--c-blue-400'), 300: rgbVar('--c-blue-300') },
      },
      fontFamily: {
        // Per-theme font stacks live in globals.css (--font-sans/--font-display).
        sans: ['var(--font-sans)'],
        display: ['var(--font-display)'],
        mono: ['ui-monospace', 'SFMono-Regular', 'Menlo', 'monospace'],
      },
      // The radius scale is a theme variable: Paper & Ink is crisp, Soft Blocks
      // is round, and a user can override it in Settings → Appearance.
      borderRadius: {
        md: 'var(--r-md)',
        lg: 'var(--r-lg)',
        xl: 'var(--r-xl)',
        '2xl': 'var(--r-2xl)',
        '3xl': 'var(--r-3xl)',
      },
      boxShadow: {
        pop: 'var(--shadow-pop)',
      },
      keyframes: {
        pulseRing: {
          '0%': { boxShadow: '0 0 0 0 rgba(255,90,31,0.45)' },
          '70%': { boxShadow: '0 0 0 7px rgba(255,90,31,0)' },
          '100%': { boxShadow: '0 0 0 0 rgba(255,90,31,0)' },
        },
        pulseGreen: {
          '0%': { boxShadow: '0 0 0 0 rgba(47,125,79,0.45)' },
          '70%': { boxShadow: '0 0 0 7px rgba(47,125,79,0)' },
          '100%': { boxShadow: '0 0 0 0 rgba(47,125,79,0)' },
        },
        slideDown: {
          from: { opacity: '0', transform: 'translateY(-8px)' },
          to: { opacity: '1', transform: 'translateY(0)' },
        },
        wave1: { '0%,100%': { height: '4px' }, '50%': { height: '18px' } },
        wave2: { '0%,100%': { height: '8px' }, '50%': { height: '22px' } },
        wave3: { '0%,100%': { height: '6px' }, '50%': { height: '14px' } },
        wave4: { '0%,100%': { height: '10px' }, '50%': { height: '20px' } },
        spin: { to: { transform: 'rotate(360deg)' } },
        fadeIn: { from: { opacity: '0' }, to: { opacity: '1' } },
        blink: { '0%,100%': { opacity: '0.2' }, '50%': { opacity: '1' } },
      },
      animation: {
        pulseRing: 'pulseRing 2s ease-out infinite',
        pulseGreen: 'pulseGreen 2s infinite',
        slideDown: 'slideDown 0.25s ease',
        spin: 'spin 0.8s linear infinite',
        fadeIn: 'fadeIn 0.2s ease',
      },
    },
  },
  plugins: [],
}
