/** @type {import('tailwindcss').Config} */
module.exports = {
  darkMode: 'class', // html.dark / html.light — set by the pre-paint script + ThemeToggle
  content: [
    './src/pages/**/*.{js,ts,jsx,tsx,mdx}',
    './src/components/**/*.{js,ts,jsx,tsx,mdx}',
    './src/app/**/*.{js,ts,jsx,tsx,mdx}',
  ],
  theme: {
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
        // Neutral line / fill tokens (theme-aware) — replace raw white/α utilities
        line: 'var(--line)',
        'line-strong': 'var(--line-strong)',
        fill: 'var(--fill)',
        'fill-hover': 'var(--fill-hover)',
        'fill-subtle': 'var(--fill-subtle)',
        // Brand scales — brand = OCEAN BLUE (rebranded from emerald)
        // DEFAULT=primary, 400=hover, 300=light/ink, 200=palest.
        brand: { DEFAULT: '#3B82F6', 400: '#60A5FA', 300: '#93C5FD', 200: '#BFDBFE' },
        cyan: { DEFAULT: '#06B6D4', 600: '#0891B2', 300: '#67E8F9' },
        purple: { DEFAULT: '#8B5CF6', 600: '#7C3AED', 700: '#4F46E5', 400: '#A78BFA', 300: '#C4B5FD' },
        green: { DEFAULT: '#10B981', 600: '#059669', 300: '#6EE7B7' },
        amber: { DEFAULT: '#F59E0B', 600: '#D97706', 400: '#FBBF24', 300: '#FCD34D' },
        red: { DEFAULT: '#EF4444', 400: '#F87171', 300: '#FCA5A5' },
        blue: { DEFAULT: '#3B82F6', 400: '#60A5FA', 300: '#93C5FD' },
      },
      fontFamily: {
        sans: ['var(--font-jakarta)', 'Plus Jakarta Sans', 'sans-serif'],
        mono: ['ui-monospace', 'SFMono-Regular', 'Menlo', 'monospace'],
      },
      keyframes: {
        pulseRing: {
          '0%': { boxShadow: '0 0 0 0 rgba(59,130,246,0.5)' },
          '70%': { boxShadow: '0 0 0 7px rgba(59,130,246,0)' },
          '100%': { boxShadow: '0 0 0 0 rgba(59,130,246,0)' },
        },
        pulseGreen: {
          '0%': { boxShadow: '0 0 0 0 rgba(16,185,129,0.5)' },
          '70%': { boxShadow: '0 0 0 7px rgba(16,185,129,0)' },
          '100%': { boxShadow: '0 0 0 0 rgba(16,185,129,0)' },
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
        slideDown: 'slideDown 0.35s ease',
        spin: 'spin 0.8s linear infinite',
        fadeIn: 'fadeIn 0.2s ease',
      },
    },
  },
  plugins: [],
}
