'use client'

import { useEffect, useState } from 'react'
import { Sun, Moon } from 'lucide-react'
import { resolveDark, saveAppearance, storedAppearance, DEFAULT_APPEARANCE, type Appearance } from '@/lib/theme'

/** Sun/moon quick switch (sidebar footer). Flips light/dark inside the
 *  user's chosen theme and saves it like Settings → Appearance does; the rest
 *  of the look (theme, accent, text size…) is untouched. */
export function ThemeToggle() {
  const [prefs, setPrefs] = useState<Appearance>(DEFAULT_APPEARANCE)
  const [dark, setDark] = useState(false)

  useEffect(() => {
    const sync = () => {
      const a = storedAppearance() ?? DEFAULT_APPEARANCE
      setPrefs(a)
      setDark(document.documentElement.classList.contains('dark'))
    }
    sync()
    window.addEventListener('appearance-change', sync)
    return () => window.removeEventListener('appearance-change', sync)
  }, [])

  function toggle() {
    const next: Appearance = { ...prefs, mode: resolveDark(prefs.mode) ? 'light' : 'dark' }
    void saveAppearance(next)
  }

  return (
    <button
      onClick={toggle}
      aria-label={dark ? 'Switch to light mode' : 'Switch to dark mode'}
      title={dark ? 'Light mode' : 'Dark mode'}
      className="flex h-[34px] w-[34px] items-center justify-center rounded-lg border border-line bg-card text-text-muted transition-colors hover:text-text-high"
    >
      {dark ? <Sun size={15} strokeWidth={1.75} /> : <Moon size={15} strokeWidth={1.75} />}
    </button>
  )
}
