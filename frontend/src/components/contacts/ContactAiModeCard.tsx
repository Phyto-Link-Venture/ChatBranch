'use client'

import { useCallback, useEffect, useState } from 'react'
import Link from 'next/link'
import { Loader2 } from 'lucide-react'
import { getContactAiMode, putContactAiMode, type ContactAiMode, type ContactAiModePayload } from '@/lib/aiControls'
import { Trans, useT, type MessageKey } from '@/i18n'

const OPTIONS: { value: ContactAiMode; label: MessageKey; hint: MessageKey }[] = [
  { value: '', label: 'contacts.aiMode.follow', hint: 'contacts.aiMode.followHint' },
  { value: 'never', label: 'common.status.never', hint: 'contacts.aiMode.neverHint' },
  { value: 'always', label: 'contacts.aiMode.always', hint: 'contacts.aiMode.alwaysHint' },
]

/**
 * "AI for this contact" on the contact page: follow the chat · never · always.
 * Never beats the per-chat switch; always beats the inbox default but not a
 * chat switched off by hand — and neither beats the master switch, an
 * opt-out or a never-AI tag. PUT /api/contacts/{id}/ai-mode (contacts.manage).
 */
export default function ContactAiModeCard({ contactId }: { contactId: number }) {
  const t = useT()
  const [data, setData] = useState<ContactAiModePayload | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(async () => {
    try {
      setData(await getContactAiMode(contactId))
      setError(null)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }, [contactId])

  useEffect(() => {
    void load()
  }, [load])

  const pick = async (mode: ContactAiMode) => {
    if (!data || mode === data.mode) return
    setBusy(true)
    setError(null)
    try {
      setData(await putContactAiMode(contactId, mode))
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  const current = OPTIONS.find((o) => o.value === (data?.mode ?? ''))
  return (
    <div className="rounded-2xl border border-line bg-card p-4">
      <div className="mb-3 flex items-baseline justify-between gap-2">
        <div className="text-[12px] font-bold uppercase tracking-[0.06em] text-text-mid">{t('contacts.aiMode.title')}</div>
        {busy && <Loader2 size={13} className="animate-spin text-text-muted" aria-hidden />}
      </div>
      {!data && !error && (
        <div className="flex items-center gap-2 text-[12px] text-text-muted">
          <Loader2 size={13} className="animate-spin" aria-hidden /> {t('common.status.loading')}
        </div>
      )}
      {data && (
        <>
          <div role="radiogroup" aria-label={t('contacts.aiMode.title')} className="flex w-full rounded-lg border border-line-strong bg-card p-0.5">
            {OPTIONS.map((o) => (
              <button
                key={o.value || 'follow'}
                type="button"
                role="radio"
                aria-checked={data.mode === o.value}
                disabled={!data.can_manage || busy}
                onClick={() => pick(o.value)}
                className={`min-w-0 flex-1 truncate rounded-md px-2 py-1 text-[12px] font-medium transition-colors disabled:cursor-not-allowed ${
                  data.mode === o.value ? 'bg-primary text-on-primary' : 'text-text-mid hover:bg-fill-hover hover:text-text-high'
                }`}
              >
                {t(o.label)}
              </button>
            ))}
          </div>
          <p className="mt-2 text-[11.5px] text-text-muted">{current ? t(current.hint) : ''}</p>
          {data.never_tag && (
            <p className="mt-1.5 rounded-lg bg-amber/10 px-2.5 py-1.5 text-[11.5px] text-[color:var(--amber-ink)]">
              <Trans
                k="contacts.aiMode.neverTag"
                values={{
                  tag: data.never_tag,
                  link: (
                    <Link href="/ai-monitor" className="font-semibold underline-offset-2 hover:underline">
                      {t('contacts.aiMode.neverTagsLink')}
                    </Link>
                  ),
                }}
              />
            </p>
          )}
        </>
      )}
      {error && (
        <div role="alert" className="mt-2.5 rounded-lg border border-red/30 bg-red/10 px-3 py-2 text-[12px] font-semibold text-[color:var(--red-ink)]">
          {error}
        </div>
      )}
    </div>
  )
}
