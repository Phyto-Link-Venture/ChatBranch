'use client'

/**
 * "Start from a real conversation": search the conversations the caller can
 * see (the inbox list endpoint, scoped like /chats) and copy the latest
 * messages of one into the playground. Read only — the conversation itself
 * never changes. Errors render inside the modal.
 */

import { useEffect, useState } from 'react'
import { Loader2, Search } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { ChannelLogo } from '@/components/ui/ChannelLogo'
import { Modal } from '@/components/ui/Modal'
import { Select } from '@/components/ui/Select'
import { aiApi } from '@/lib/aiMonitor'
import { importConversation, type ImportedConversation } from '@/lib/aiPlayground'
import { useT } from '@/i18n'
import { fieldCls } from './SetupPane'

interface ThreadRow {
  channel: string
  key: string
  /** which company's conversation (a customer has one per company) */
  thread_id?: number | null
  company?: { id: number; name: string } | null
  name: string
  last_message?: string
  contact_name?: string
}

/** One conversation's identity — channel + key can name one per company. */
const rowId = (r: ThreadRow) => (r.thread_id ? `${r.channel}:${r.key}#${r.thread_id}` : `${r.channel}:${r.key}`)

export function ImportModal({
  open,
  onClose,
  entity,
  onImported,
}: {
  open: boolean
  onClose: () => void
  entity: string | null
  onImported: (c: ImportedConversation) => void
}) {
  const t = useT()
  const [q, setQ] = useState('')
  const [rows, setRows] = useState<ThreadRow[] | null>(null)
  const [take, setTake] = useState('20')
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (!open) return
    setError(null)
    const timer = window.setTimeout(() => {
      const p = new URLSearchParams({ limit: '12' })
      if (q.trim()) p.set('q', q.trim())
      if (entity) p.set('entity', entity)
      aiApi<{ threads: ThreadRow[] }>(`inbox/threads?${p}`)
        .then((d) => setRows(Array.isArray(d.threads) ? d.threads : []))
        .catch((e: Error) => {
          setRows([])
          setError(e.message)
        })
    }, 250)
    return () => window.clearTimeout(timer)
  }, [open, q, entity])

  const pick = async (row: ThreadRow) => {
    setBusy(rowId(row))
    setError(null)
    try {
      const c = await importConversation(row.channel, row.key, Number(take), row.thread_id)
      onImported(c)
      onClose()
    } catch (e) {
      setError(t('playground.errors.import', { error: e instanceof Error ? e.message : String(e) }))
    } finally {
      setBusy(null)
    }
  }

  return (
    <Modal open={open} onClose={onClose} title={t('playground.import.title')} subtitle={t('playground.import.note')}
      labelledById="pg-import-title" size="md">
      {error && (
        <div role="alert" className="mb-3 rounded-lg border border-red/30 bg-red/10 px-3 py-2 text-[12.5px] text-[color:var(--red-ink)]">
          {error}
        </div>
      )}
      <div className="flex flex-col gap-3">
        <div className="flex flex-wrap items-end gap-2">
          <div className="relative min-w-0 flex-1">
            <Search size={13} className="pointer-events-none absolute left-2.5 top-1/2 -translate-y-1/2 text-text-faint" aria-hidden />
            <input
              autoFocus
              value={q}
              onChange={(e) => setQ(e.target.value)}
              placeholder={t('playground.import.search')}
              aria-label={t('playground.import.search')}
              className={`${fieldCls} pl-8`}
            />
          </div>
          <div className="w-[150px]">
            <div className="mb-1 text-[11.5px] font-semibold text-text-high">{t('playground.import.take')}</div>
            <Select
              value={take}
              onChange={setTake}
              options={['10', '20', '40', '60'].map((n) => ({ value: n, label: n }))}
              ariaLabel={t('playground.import.take')}
              size="md"
            />
          </div>
        </div>
        <ul className="flex max-h-[50vh] flex-col gap-1 overflow-y-auto">
          {rows === null && (
            <li className="flex items-center gap-2 px-2 py-3 text-[12.5px] text-text-muted">
              <Loader2 size={13} className="animate-spin" aria-hidden /> {t('common.status.loading')}
            </li>
          )}
          {rows?.length === 0 && !error && (
            <li className="px-2 py-3 text-[12.5px] text-text-muted">{t('playground.errors.noConversations')}</li>
          )}
          {rows?.map((r) => {
            const id = rowId(r)
            return (
              <li key={id}>
                <button
                  type="button"
                  onClick={() => void pick(r)}
                  disabled={busy != null}
                  className="flex w-full items-center gap-2.5 rounded-lg border border-line px-3 py-2 text-left hover:bg-fill-hover disabled:opacity-60"
                >
                  <ChannelLogo channel={r.channel} size={18} />
                  <span className="min-w-0 flex-1">
                    <span className="block truncate text-[13px] font-semibold text-text-high">{r.contact_name || r.name}</span>
                    {r.last_message && <span className="block truncate text-[11.5px] text-text-muted">{r.last_message}</span>}
                  </span>
                  {busy === id ? (
                    <Loader2 size={14} className="shrink-0 animate-spin text-text-muted" aria-hidden />
                  ) : (
                    <span className="shrink-0 text-[11.5px] font-semibold text-brand-300">{t('playground.import.copy')}</span>
                  )}
                </button>
              </li>
            )
          })}
        </ul>
        <div className="flex justify-end">
          <Button size="sm" variant="secondary" onClick={onClose}>
            {t('common.actions.cancel')}
          </Button>
        </div>
      </div>
    </Modal>
  )
}
