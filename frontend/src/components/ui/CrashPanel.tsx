'use client'

import { useEffect, useState } from 'react'
import { AlertTriangle, ClipboardCopy, RefreshCw, RotateCcw } from 'lucide-react'
import { useT } from '@/i18n'
import { isStaleBuildError, reloadForNewBuild, reportClientError } from '@/lib/clientErrors'

/**
 * What a page shows instead of Next.js's bare "Application error": what
 * happened, Try again (re-render), Reload, and Copy details for the admin.
 * The error is sent to the API log; a stale-build chunk error reloads once.
 */
export function CrashPanel({ error, reset }: { error: Error & { digest?: string }; reset: () => void }) {
  const t = useT()
  const [copied, setCopied] = useState(false)
  const [reloading, setReloading] = useState(false)

  useEffect(() => {
    if (isStaleBuildError(error) && reloadForNewBuild()) {
      setReloading(true)
      return
    }
    reportClientError(error, { digest: error.digest, where: 'page' })
  }, [error])

  const details = [
    `${error.name}: ${error.message}`,
    error.digest ? `digest: ${error.digest}` : '',
    `page: ${typeof window !== 'undefined' ? window.location.pathname + window.location.search : ''}`,
    `time: ${new Date().toISOString()}`,
    `browser: ${typeof navigator !== 'undefined' ? navigator.userAgent : ''}`,
    '',
    (error.stack ?? '').split('\n').slice(0, 12).join('\n'),
  ]
    .filter((x, i) => x !== '' || i === 5)
    .join('\n')

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(details)
      setCopied(true)
    } catch {
      /* clipboard blocked: the details are visible below */
    }
  }

  if (reloading)
    return (
      <div className="flex flex-1 items-center justify-center p-6 text-[13px] text-text-muted" role="status">
        <RefreshCw size={14} className="mr-2 animate-spin" aria-hidden /> {t('common.crash.updating')}
      </div>
    )

  return (
    <div className="flex flex-1 items-start justify-center p-4 sm:p-8">
      <div role="alert" className="w-full max-w-xl rounded-2xl border border-line bg-card p-5 sm:p-6">
        <div className="flex items-start gap-3">
          <span className="grid h-9 w-9 shrink-0 place-items-center rounded-full bg-red/10">
            <AlertTriangle size={16} className="text-red-300" aria-hidden />
          </span>
          <div className="min-w-0">
            <h1 className="display text-[20px] leading-tight text-text-heading">{t('common.crash.title')}</h1>
            <p className="mt-1 text-[13px] leading-relaxed text-text-muted">{t('common.crash.body')}</p>
          </div>
        </div>
        <pre className="mt-4 max-h-40 overflow-auto whitespace-pre-wrap break-words rounded-lg bg-well p-3 font-mono text-[11.5px] leading-relaxed text-text-mid">
          {error.name}: {error.message}
          {error.digest ? `\ndigest: ${error.digest}` : ''}
        </pre>
        <div className="mt-4 flex flex-wrap gap-2">
          <button
            type="button"
            onClick={reset}
            className="inline-flex items-center gap-1.5 rounded-lg bg-primary px-3.5 py-2 text-[13px] font-semibold text-on-primary hover:opacity-90"
          >
            <RotateCcw size={13} aria-hidden /> {t('common.crash.tryAgain')}
          </button>
          <button
            type="button"
            onClick={() => window.location.reload()}
            className="inline-flex items-center gap-1.5 rounded-lg border border-line-strong px-3.5 py-2 text-[13px] font-semibold text-text-high hover:bg-fill-hover"
          >
            <RefreshCw size={13} aria-hidden /> {t('common.crash.reload')}
          </button>
          <button
            type="button"
            onClick={() => void copy()}
            className="inline-flex items-center gap-1.5 rounded-lg px-3 py-2 text-[13px] text-text-mid hover:bg-fill-hover"
          >
            <ClipboardCopy size={13} aria-hidden /> {copied ? t('common.crash.copied') : t('common.crash.copy')}
          </button>
        </div>
      </div>
    </div>
  )
}
