'use client'

import { useEffect, useState } from 'react'
import { ChevronLeft, Loader2, Send } from 'lucide-react'
import { Modal } from '@/components/ui/Modal'
import { Button } from '@/components/ui/Button'
import { api, type UiMessage } from './inboxTypes'
import { useT, type MessageKey, type TFunction } from '@/i18n'

interface WaTemplate {
  name: string
  language: string
  category: string
  status: string
  body: string
  variables: number
  bindings?: string[]
  resolved?: string[] // bound slots pre-filled from the thread's CRM contact
}

/** Fill {{1}}…{{N}} with values; unfilled slots keep their placeholder. */
const renderTemplateBody = (body: string, vars: string[]) =>
  body.replace(/\{\{(\d+)\}\}/g, (raw, n) => vars[Number(n) - 1]?.trim() || raw)

const CATEGORIES = ['marketing', 'utility', 'authentication']
const TPL_STATUSES = ['approved', 'pending', 'rejected', 'paused', 'disabled']

/** Meta's category / status codes in the current language (unknown ones as they come). */
const categoryLabel = (t: TFunction, c: string) =>
  CATEGORIES.includes(c.toLowerCase()) ? t(`chats.templatePicker.category.${c.toLowerCase()}` as MessageKey) : c
const tplStatusLabel = (t: TFunction, s: string) =>
  TPL_STATUSES.includes(s.toLowerCase()) ? t(`chats.templatePicker.tplStatus.${s.toLowerCase()}` as MessageKey) : s

/** WhatsApp template picker — approved templates re-open a closed 24h window. */
export default function TemplatePickerModal({
  thread,
  onClose,
  onSent,
}: {
  thread: { key: string; account_id?: string | null }
  onClose: () => void
  onSent: (m: UiMessage) => void
}) {
  const t = useT()
  const [data, setData] = useState<{ templates: WaTemplate[]; sandbox: boolean } | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [sel, setSel] = useState<WaTemplate | null>(null)
  const [vars, setVars] = useState<string[]>([])
  const [sending, setSending] = useState(false)
  const [sendError, setSendError] = useState<string | null>(null)

  useEffect(() => {
    // wa_id lets the backend resolve bound {{n}} slots from this thread's
    // CRM contact, so the inputs arrive pre-filled.
    const params = new URLSearchParams({ wa_id: thread.key })
    if (thread.account_id) params.set('account_id', thread.account_id)
    api<{ templates: WaTemplate[]; sandbox: boolean }>(`whatsapp/templates?${params}`)
      .then(setData)
      .catch((e) => setLoadError(e instanceof Error ? e.message : String(e)))
  }, [thread.account_id, thread.key])

  const pick = (tpl: WaTemplate) => {
    setSel(tpl)
    setVars(Array.from({ length: tpl.variables }, (_, i) => tpl.resolved?.[i] || ''))
    setSendError(null)
  }

  const send = async () => {
    if (!sel) return
    setSending(true)
    setSendError(null)
    try {
      const r = await api<{ message: UiMessage }>(`whatsapp/threads/${thread.key}/send-template`, {
        method: 'POST',
        body: JSON.stringify({ name: sel.name, language: sel.language, variables: vars }),
      })
      onSent(r.message)
      onClose()
    } catch (e) {
      setSendError(e instanceof Error ? e.message : String(e))
    } finally {
      setSending(false)
    }
  }

  const canSend = !!sel && vars.every((v) => v.trim()) && !sending

  return (
    <Modal
      open
      onClose={onClose}
      size="md"
      labelledById="tpl-picker-title"
      title={
        <span className="flex items-center gap-2">
          {sel && (
            <button
              type="button"
              onClick={() => setSel(null)}
              title={t('chats.templatePicker.back')}
              aria-label={t('chats.templatePicker.backAria')}
              className="grid h-7 w-7 shrink-0 place-items-center rounded-lg border border-line text-text-muted hover:text-text-high"
            >
              <ChevronLeft size={15} aria-hidden />
            </button>
          )}
          {sel ? sel.name : t('chats.templatePicker.title')}
        </span>
      }
      subtitle={
        sel
          ? sel.variables
            ? t('chats.templatePicker.metaVars', {
                language: sel.language,
                category: categoryLabel(t, sel.category),
                count: sel.variables,
              })
            : t('chats.templatePicker.meta', { language: sel.language, category: categoryLabel(t, sel.category) })
          : t('chats.templatePicker.subtitle')
      }
      footer={
        sel ? (
          <>
            <Button variant="ghost" onClick={onClose} disabled={sending}>
              {t('common.actions.cancel')}
            </Button>
            <Button variant="primary" onClick={send} disabled={!canSend}>
              {sending ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Send size={13} aria-hidden />} {t('chats.templatePicker.send')}
            </Button>
          </>
        ) : undefined
      }
    >
      {sendError && (
        <div role="alert" className="mb-3 rounded-lg border border-red/30 bg-red/10 px-3 py-2 text-[12px] font-semibold text-red-300">
          {sendError}
        </div>
      )}
      {!data && !loadError && (
        <div className="flex items-center justify-center gap-2 py-10 text-[12px] text-text-muted">
          <Loader2 size={14} className="animate-spin" aria-hidden /> {t('chats.templatePicker.loading')}
        </div>
      )}
      {loadError && (
        <div role="alert" className="py-8 text-center text-[12px] text-red-300">
          {loadError}
        </div>
      )}
      {data && !sel && (
        <div className="-mx-[22px] -my-[18px] divide-y divide-line">
          {data.templates.length === 0 && (
            <div className="px-5 py-8 text-center text-[12px] text-text-muted">
              {t('chats.templatePicker.empty')}
            </div>
          )}
          {data.templates.map((tpl) => (
            <button
              key={`${tpl.name}-${tpl.language}`}
              type="button"
              onClick={() => pick(tpl)}
              className="block w-full cursor-pointer px-[22px] py-3 text-left transition-colors hover:bg-fill"
            >
              <span className="flex flex-wrap items-center gap-1.5">
                <span className="truncate text-[13px] font-semibold text-text-high">{tpl.name}</span>
                <span className="shrink-0 rounded-full border border-line px-1.5 py-px text-[9.5px] font-bold uppercase text-text-muted">
                  {categoryLabel(t, tpl.category)}
                </span>
                <span
                  className={`shrink-0 rounded-full px-1.5 py-px text-[9.5px] font-bold uppercase ${
                    tpl.status.toUpperCase() === 'APPROVED' ? 'bg-green/15 text-green-300' : 'bg-amber/15 text-amber-300'
                  }`}
                >
                  {tplStatusLabel(t, tpl.status)}
                </span>
                {data.sandbox && (
                  <span className="shrink-0 rounded-full bg-amber/15 px-1.5 py-px text-[9.5px] font-bold uppercase text-amber-300">
                    {t('chats.templatePicker.sandbox')}
                  </span>
                )}
              </span>
              <span className="mt-0.5 block truncate text-[12px] text-text-muted">{tpl.body}</span>
            </button>
          ))}
        </div>
      )}
      {data && sel && (
        <div className="flex flex-col gap-3">
          {vars.map((v, i) => {
            const bound = !!sel.bindings?.[i]
            return (
              <label key={i} className="block">
                <span className="mb-1 flex items-center gap-1.5 font-mono text-[11px] font-bold text-text-high">
                  {`{{${i + 1}}}`}
                  {bound && (
                    <span
                      className="rounded-full bg-brand-400/10 px-1.5 py-px font-sans text-[9px] font-bold uppercase text-brand-300"
                      title={t('chats.templatePicker.autoFilledHint')}
                    >
                      {t('chats.templatePicker.autoFilled')}
                    </span>
                  )}
                </span>
                <input
                  value={v}
                  onChange={(e) => setVars(vars.map((x, j) => (j === i ? e.target.value : x)))}
                  placeholder={t('chats.templatePicker.valueFor', { slot: `{{${i + 1}}}` })}
                  className="h-9 w-full rounded-lg border border-line-strong bg-card px-3 text-[13px] text-text-primary outline-none transition-colors focus:border-text-primary"
                />
              </label>
            )
          })}
          <div>
            <div className="eyebrow mb-1.5">{t('chats.templatePicker.preview')}</div>
            <div className="whitespace-pre-wrap rounded-lg border border-line bg-well px-3 py-2.5 text-[13px] leading-relaxed text-text-primary">
              {renderTemplateBody(sel.body, vars)}
            </div>
          </div>
        </div>
      )}
    </Modal>
  )
}
