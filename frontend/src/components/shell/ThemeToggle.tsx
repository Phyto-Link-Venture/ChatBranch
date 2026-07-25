'use client'

import { useEffect, useState } from 'react'
import { Sun, Moon } from 'lucide-react'

type Theme = 'dark' | 'light'

/** Sun/moon toggle in the sidebar footer. Persists the choice to localStorage
 *  and swaps the `dark` / `light` class on <html> (Tailwind class strategy);
 *  the pre-paint script in layout.tsx applies it on load so there's no flash.
 *  Defaults to light. */
export function ThemeToggle() {
  const [theme, setTheme] = useState<Theme>('light')

  // Sync initial state from whatever the pre-paint script already applied.
  useEffect(() => {
    setTheme(document.documentElement.classList.contains('light') ? 'light' : 'dark')
  }, [])

  function toggle() {
    const next: Theme = theme === 'dark' ? 'light' : 'dark'
    setTheme(next)
    const cls = document.documentElement.classList
    cls.remove('dark', 'light')
    cls.add(next)
    try {
      localStorage.setItem('theme', next)
    } catch {
      /* private mode / storage disabled — theme still applies for this session */
    }
  }

  const isDark = theme === 'dark'
  return (
    <button
      onClick={toggle}
      aria-label={isDark ? 'Switch to light theme' : 'Switch to dark theme'}
      title={isDark ? 'Light mode' : 'Dark mode'}
      className="flex h-[34px] w-[34px] items-center justify-center rounded-lg border border-line bg-fill text-text-muted transition-colors hover:text-text-high"
    >
      {isDark ? (
        <Sun size={15} strokeWidth={1.75} />
      ) : (
        <Moon size={15} strokeWidth={1.75} />
      )}
    </button>
  )
}
