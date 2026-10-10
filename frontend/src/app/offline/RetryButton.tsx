'use client'

import { useEffect, useState } from 'react'
import { useT } from '@/i18n'

/** Only same-app paths — never bounce to another origin off a query string. */
function safeFrom(raw: string | null): string {
  return raw && raw.startsWith('/') && !raw.startsWith('//') && !raw.startsWith('/offline') ? raw : '/dashboard'
}

/** "Try again": back to the page the user was opening (?from=), and
 *  automatically as soon as the browser reports it's online again. */
export function RetryButton() {
  const t = useT()
  const [target, setTarget] = useState('/dashboard')

  useEffect(() => {
    const to = safeFrom(new URLSearchParams(window.location.search).get('from'))
    setTarget(to)
    const back = () => window.location.replace(to)
    window.addEventListener('online', back)
    return () => window.removeEventListener('online', back)
  }, [])

  return (
    <a
      href={target}
      className="mt-6 flex h-11 w-full items-center justify-center rounded-lg bg-primary text-[14px] font-semibold text-on-primary transition-opacity hover:opacity-90"
    >
      {t('common.actions.retry')}
    </a>
  )
}
