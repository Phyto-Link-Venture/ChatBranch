'use client'

import { Logo } from '@/components/shell/Logo'
import { useT } from '@/i18n'
import { RetryButton } from './RetryButton'

/** The offline page's card — a client component so its copy follows the
 *  dashboard language (English until the cached choice is applied). */
export function OfflineCard() {
  const t = useT()
  return (
    <div className="flex min-h-dvh items-center justify-center bg-app px-4 py-10">
      <div className="w-full max-w-[420px]">
        <div className="mb-8 flex justify-center">
          <Logo size={30} textClassName="text-[26px]" />
        </div>
        <div className="rounded-xl border border-line bg-card px-6 py-7 sm:px-8">
          <div className="eyebrow">{t('shell.offline.eyebrow')}</div>
          <h1 className="display mt-1 text-[32px] leading-[1.05] text-text-heading">{t('shell.offline.title')}</h1>
          <div className="mt-4 border-t border-text-primary/80" />
          <p className="mt-4 text-[14px] leading-relaxed text-text-mid">{t('shell.offline.body')}</p>
          <RetryButton />
        </div>
      </div>
    </div>
  )
}
