'use client'

/**
 * The one line the Inboxes page shows after a managed WhatsApp signup comes back.
 *
 * The API's callback (GET /api/social/connect/whatsapp/callback) bounces the browser here with
 *   ?zernio=connected&platform=whatsapp&account=…[&pending=1]   it worked
 *   ?zernio=error&platform=whatsapp&detail=…                    it did not, and why
 * Without this, a refusal from Meta would land on a silent page. `platform=whatsapp` is required:
 * the generic hosted connect (Instagram, Messenger…) lands on `?zernio=connected` too, and this
 * banner is not about those.
 *
 * The query is read from `window.location` in an effect (not `useSearchParams`, which would need a
 * Suspense boundary) and dismissed with `history.replaceState`, so the page's own path — /channels
 * or /settings/channels — is kept.
 */

import { useEffect, useState } from 'react'
import { AlertTriangle, Check, X } from 'lucide-react'
import { useT } from '@/i18n'

type Result = { ok: boolean; detail: string; pending: boolean }

export function WhatsAppConnectResult() {
  const t = useT()
  const [result, setResult] = useState<Result | null>(null)

  useEffect(() => {
    try {
      const q = new URLSearchParams(window.location.search)
      const outcome = q.get('zernio') ?? ''
      if (q.get('platform') !== 'whatsapp' || (outcome !== 'connected' && outcome !== 'error')) return
      setResult({ ok: outcome === 'connected', detail: q.get('detail') ?? '', pending: q.get('pending') === '1' })
    } catch {
      /* no window query to read: nothing to show */
    }
  }, [])

  if (!result) return null
  const text = result.ok
    ? result.pending
      ? t('channels.waResult.pending')
      : t('channels.waResult.connected')
    : t('channels.waResult.failed', { detail: result.detail || t('common.errors.generic') })

  return (
    <div
      role={result.ok ? 'status' : 'alert'}
      className={`flex items-start gap-2 rounded-lg border px-3 py-2 text-[12.5px] font-semibold ${
        result.ok ? 'border-green/35 bg-green/10' : 'border-red/35 bg-red/10'
      }`}
      style={{ color: result.ok ? 'var(--green-ink)' : 'var(--red-ink)' }}
    >
      {result.ok ? (
        <Check size={14} className="mt-0.5 shrink-0" aria-hidden />
      ) : (
        <AlertTriangle size={14} className="mt-0.5 shrink-0" aria-hidden />
      )}
      <span className="flex-1">{text}</span>
      <button
        type="button"
        aria-label={t('common.actions.close')}
        onClick={() => {
          setResult(null)
          try {
            window.history.replaceState(null, '', window.location.pathname)
          } catch {
            /* the banner is gone either way */
          }
        }}
      >
        <X size={14} aria-hidden />
      </button>
    </div>
  )
}
