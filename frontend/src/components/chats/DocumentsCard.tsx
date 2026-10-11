'use client'

/**
 * "Documents" in the chat info panel: the PDFs (invoices, quotations, receipts,
 * letters) and QR codes made in this conversation — by the AI or a teammate — each
 * opening the auth-gated file, plus "New document" (chats.send): a form made from
 * what the conversation's Documents skill offers (GET /api/documents/form), including
 * a custom layout's own fields (PRO, Attn tel, …). The server does every sum and
 * numbers it; "Send it now" delivers it like an AI reply's file. Fails soft.
 */

import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react'
import { AlertCircle, ExternalLink, FileText, Loader2, Plus, QrCode, Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Modal } from '@/components/ui/Modal'
import { Select } from '@/components/ui/Select'
import { Toggle } from '@/components/ui/Toggle'
import { openAuthed, type DocumentType } from '@/components/skills/readySkills'
import { useAccess } from '@/lib/useAccess'
import {
  createDocument,
  documentForm,
  listThreadDocuments,
  type ChatDocument,
  type DocumentForm,
  type DocumentFormType,
} from '@/lib/documents'
import { useFormat, useT, type MessageKey } from '@/i18n'

const STATUS: Record<string, MessageKey> = {
  pending: 'readySkills.draft.status.pending',
  sent: 'readySkills.draft.status.sent',
  void: 'readySkills.draft.status.void',
  failed: 'readySkills.draft.status.failed',
  issued: 'readySkills.draft.status.issued',
}

const TYPE_LABEL: Record<DocumentType, MessageKey> = {
  invoice: 'readySkills.form.types.invoice',
  quotation: 'readySkills.form.types.quotation',
  receipt: 'readySkills.form.types.receipt',
  letter: 'readySkills.form.types.letter',
}

const field =
  'rounded-lg border border-line-strong bg-card px-3 py-2 text-[13px] text-text-primary outline-none transition-colors focus:border-brand-400 disabled:opacity-70'
const input = `w-full ${field}`

export function DocumentsCard({
  channel,
  threadKey,
  refreshToken,
}: {
  channel: string
  threadKey: string
  /** Any value that changes when the conversation updates (refetch trigger). */
  refreshToken?: unknown
}) {
  const t = useT()
  const f = useFormat()
  const { ready, can } = useAccess()
  const [rows, setRows] = useState<ChatDocument[] | null>(null)
  const [failed, setFailed] = useState(false)
  const [creating, setCreating] = useState(false)
  const [notice, setNotice] = useState<string | null>(null)
  const [opening, setOpening] = useState<number | null>(null)

  const load = useCallback(async () => {
    try {
      setRows(await listThreadDocuments(channel, threadKey))
      setFailed(false)
    } catch {
      setRows([])
      setFailed(true)
    }
  }, [channel, threadKey])

  useEffect(() => {
    void load()
  }, [load, refreshToken])
  useEffect(() => setNotice(null), [channel, threadKey])

  const labelOf = (d: ChatDocument) => (d.type === 'qr' ? d.label : `${t(TYPE_LABEL[d.type])} ${d.number}`.trim())

  const open = async (d: ChatDocument) => {
    setOpening(d.id)
    try {
      await openAuthed(d.url)
    } catch {
      /* the row stays; nothing new opens */
    } finally {
      setOpening(null)
    }
  }

  return (
    <section className="rounded-xl border border-line bg-card p-3.5" aria-label={t('docLayout.chat.title')}>
      <div className="mb-2 flex items-center justify-between gap-2">
        <h3 className="eyebrow">{t('docLayout.chat.title')}</h3>
        {ready && can('chats.send') && (
          <Button
            variant="ghost"
            size="sm"
            onClick={() => {
              setNotice(null)
              setCreating(true)
            }}
          >
            <Plus size={13} aria-hidden /> {t('docLayout.chat.new')}
          </Button>
        )}
      </div>
      {notice && (
        <p role="status" className="mb-2 text-[12px] leading-relaxed text-text-mid">
          {notice}
        </p>
      )}
      {rows === null ? (
        <div className="flex items-center gap-2 text-[12px] text-text-muted">
          <Loader2 size={13} className="animate-spin" aria-hidden /> {t('common.status.loading')}
        </div>
      ) : rows.length === 0 ? (
        <p className="text-[12px] leading-relaxed text-text-muted">{t(failed ? 'docLayout.chat.loadFailed' : 'docLayout.chat.none')}</p>
      ) : (
        <ul className="flex flex-col divide-y divide-line">
          {rows.slice(0, 8).map((d) => {
            const Icon = d.type === 'qr' ? QrCode : FileText
            return (
              <li key={d.id} className="flex items-center gap-2 py-1.5 first:pt-0 last:pb-0">
                <Icon size={13} className="shrink-0 text-text-muted" aria-hidden />
                <div className="min-w-0 flex-1">
                  <div className="truncate text-[12.5px] font-semibold text-text-high">{labelOf(d)}</div>
                  <div className="truncate text-[11px] text-text-muted">
                    {d.total != null ? `${f.currency(d.total, d.currency || 'MYR')} · ` : ''}
                    {t(STATUS[d.status] ?? STATUS.issued)} · {f.date(d.created_at)}
                  </div>
                </div>
                <button
                  type="button"
                  onClick={() => open(d)}
                  aria-label={`${t('docLayout.chat.open')} ${labelOf(d)}`}
                  className="grid h-7 w-7 shrink-0 place-items-center rounded-lg text-text-muted hover:bg-fill-hover hover:text-text-high"
                >
                  {opening === d.id ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <ExternalLink size={13} aria-hidden />}
                </button>
              </li>
            )
          })}
        </ul>
      )}
      {creating && (
        <CreateDocumentModal
          channel={channel}
          threadKey={threadKey}
          onClose={() => setCreating(false)}
          onCreated={(d, sent) => {
            setCreating(false)
            setNotice(t(sent ? 'docLayout.create.sent' : 'docLayout.create.created', {
              label: d.type === 'qr' ? d.label : t(TYPE_LABEL[d.type]),
              number: d.number,
            }))
            void load()
          }}
        />
      )}
    </section>
  )
}

interface ItemRow {
  description: string
  qty: string
  unit_price: string
  discount: string
}

const EMPTY_ITEM: ItemRow = { description: '', qty: '1', unit_price: '', discount: '' }

function CreateDocumentModal({
  channel,
  threadKey,
  onClose,
  onCreated,
}: {
  channel: string
  threadKey: string
  onClose: () => void
  onCreated: (d: ChatDocument, sent: boolean) => void
}) {
  const t = useT()
  const [form, setForm] = useState<DocumentForm | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [type, setType] = useState<DocumentType>('invoice')
  const [customer, setCustomer] = useState('')
  const [items, setItems] = useState<ItemRow[]>([{ ...EMPTY_ITEM }])
  const [subject, setSubject] = useState('')
  const [reference, setReference] = useState('')
  const [notes, setNotes] = useState('')
  const [dueDate, setDueDate] = useState('')
  const [paymentMethod, setPaymentMethod] = useState('')
  const [letter, setLetter] = useState('')
  const [values, setValues] = useState<Record<string, string>>({})
  const [send, setSend] = useState(false)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    let live = true
    documentForm(channel, threadKey)
      .then((f) => {
        if (!live) return
        setForm(f)
        if (f.types.length && !f.types.some((x) => x.type === 'invoice')) setType(f.types[0].type)
      })
      .catch((e) => live && setError(e instanceof Error ? e.message : String(e)))
    return () => {
      live = false
    }
  }, [channel, threadKey])

  const chosen: DocumentFormType | undefined = useMemo(() => form?.types.find((x) => x.type === type), [form, type])
  const fields = useMemo(() => chosen?.fields ?? [], [chosen])
  const blocked = chosen?.layout === 'html' && form?.html_renderer === 'missing'
  // a custom field's default shows until someone types over it
  useEffect(() => {
    setValues((v) => {
      const missing = fields.filter((fd) => v[fd.key] === undefined)
      if (!missing.length) return v
      const next = { ...v }
      for (const fd of missing) next[fd.key] = fd.default
      return next
    })
  }, [fields])

  const patchItem = (i: number, p: Partial<ItemRow>) => setItems((rows) => rows.map((r, j) => (j === i ? { ...r, ...p } : r)))

  const submit = async () => {
    setError(null)
    const args: Record<string, unknown> = {}
    if (customer.trim()) args[type === 'letter' ? 'recipient_name' : 'customer_name'] = customer.trim()
    if (type === 'letter') {
      const paragraphs = letter.split(/\n\s*\n/).map((p) => p.trim()).filter(Boolean)
      if (!subject.trim() || !paragraphs.length) return setError(t('docLayout.create.problems.letter'))
      args.title = subject.trim()
      args.paragraphs = paragraphs
    } else {
      const list = items
        .filter((r) => r.description.trim() && r.unit_price.trim())
        .map((r) => ({
          description: r.description.trim(),
          qty: r.qty.trim() ? Number(r.qty) : 1,
          unit_price: r.unit_price.trim(),
          ...(r.discount.trim() ? { discount: r.discount.trim() } : {}),
        }))
      if (!list.length) return setError(t('docLayout.create.problems.items'))
      args.items = list
      if (subject.trim()) args.title = subject.trim()
      if (reference.trim()) args.reference = reference.trim()
      if (notes.trim()) args.notes = notes.trim()
      if (type === 'invoice' && dueDate) args.due_date = dueDate
      if (type === 'receipt' && paymentMethod.trim()) args.payment_method = paymentMethod.trim()
    }
    if (fields.length) {
      const custom: Record<string, string> = {}
      for (const fd of fields) {
        const v = (values[fd.key] ?? '').trim()
        if (fd.required && !v) return setError(t('docLayout.create.problems.required', { field: fd.label || fd.key }))
        if (v) custom[fd.key] = v
      }
      args.fields = custom
    }
    setBusy(true)
    try {
      const d = await createDocument({ channel, key: threadKey, type, send, fields: args })
      onCreated(d, send)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  const priced = type !== 'letter'
  return (
    <Modal
      open
      onClose={onClose}
      size="lg"
      title={t('docLayout.create.title')}
      subtitle={t('docLayout.create.subtitle')}
      labelledById="create-document-title"
      footer={
        <div className="flex flex-wrap items-center justify-end gap-2">
          <Button variant="secondary" size="sm" onClick={onClose}>
            {t('common.actions.cancel')}
          </Button>
          <Button size="sm" onClick={submit} disabled={busy || !form || !chosen || blocked}>
            {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <FileText size={13} aria-hidden />}{' '}
            {busy ? t('docLayout.create.creating') : t('docLayout.create.create')}
          </Button>
        </div>
      }
    >
      <div className="flex flex-col gap-4">
        {error && (
          <div role="alert" className="flex items-start gap-2 rounded-lg border border-red/40 bg-red/10 px-3 py-2 text-[12px] text-[color:var(--red-ink)]">
            <AlertCircle size={13} className="mt-0.5 shrink-0" aria-hidden />
            <span className="min-w-0">{error}</span>
          </div>
        )}
        {!form ? (
          !error && (
            <div className="flex items-center gap-2 text-[12px] text-text-muted">
              <Loader2 size={13} className="animate-spin" aria-hidden /> {t('common.status.loading')}
            </div>
          )
        ) : form.types.length === 0 ? (
          <p className="text-[12.5px] leading-relaxed text-text-mid">{t('docLayout.chat.noSkill')}</p>
        ) : (
          <>
            {blocked && (
              <div role="status" className="flex items-start gap-2 rounded-lg border border-amber/40 bg-amber/10 px-3 py-2 text-[12px] text-text-high">
                <AlertCircle size={13} className="mt-0.5 shrink-0" aria-hidden />
                <span className="min-w-0">{t('docLayout.create.rendererMissing')}</span>
              </div>
            )}
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
              <Labeled label={t('docLayout.create.type')}>
                <Select
                  value={type}
                  size="md"
                  ariaLabel={t('docLayout.create.type')}
                  onChange={(v) => setType(v as DocumentType)}
                  options={form.types.map((x) => ({ value: x.type, label: t(TYPE_LABEL[x.type]) }))}
                />
              </Labeled>
              <Labeled label={t('docLayout.create.customer')} hint={t('docLayout.create.customerHint')}>
                <input value={customer} maxLength={120} onChange={(e) => setCustomer(e.target.value)} className={input} />
              </Labeled>
            </div>

            <Labeled label={t('docLayout.create.subject')}>
              <input value={subject} maxLength={160} onChange={(e) => setSubject(e.target.value)} className={input} />
            </Labeled>

            {priced ? (
              <>
                <div className="flex flex-col gap-2">
                  <div className="text-[12px] font-semibold text-text-high">{t('docLayout.create.items')}</div>
                  {items.map((r, i) => (
                    <div key={i} className="grid grid-cols-2 gap-2 rounded-lg border border-line p-2.5 sm:grid-cols-[1fr_70px_110px_90px_32px]">
                      <label className="col-span-2 flex min-w-0 flex-col gap-1 text-[11px] text-text-muted sm:col-span-1">
                        {t('docLayout.create.description')}
                        <textarea
                          rows={1}
                          value={r.description}
                          maxLength={300}
                          onChange={(e) => patchItem(i, { description: e.target.value })}
                          className={`${input} resize-y`}
                        />
                      </label>
                      <label className="flex min-w-0 flex-col gap-1 text-[11px] text-text-muted">
                        {t('docLayout.create.qty')}
                        <input inputMode="decimal" value={r.qty} onChange={(e) => patchItem(i, { qty: e.target.value })} className={input} />
                      </label>
                      <label className="flex min-w-0 flex-col gap-1 text-[11px] text-text-muted">
                        {t('docLayout.create.unitPrice')}
                        <input inputMode="decimal" value={r.unit_price} onChange={(e) => patchItem(i, { unit_price: e.target.value })} className={input} />
                      </label>
                      <label className="flex min-w-0 flex-col gap-1 text-[11px] text-text-muted">
                        {t('docLayout.create.discount')}
                        <input
                          value={r.discount}
                          placeholder={t('docLayout.create.discountPlaceholder')}
                          onChange={(e) => patchItem(i, { discount: e.target.value })}
                          className={input}
                        />
                      </label>
                      <div className="flex items-end justify-end">
                        <button
                          type="button"
                          disabled={items.length === 1}
                          onClick={() => setItems((rows) => rows.filter((_, j) => j !== i))}
                          aria-label={t('docLayout.create.removeItem', { n: i + 1 })}
                          className="grid h-[38px] w-8 place-items-center rounded-lg text-text-muted hover:bg-fill-hover hover:text-text-high disabled:opacity-30"
                        >
                          <Trash2 size={14} aria-hidden />
                        </button>
                      </div>
                    </div>
                  ))}
                  <div>
                    <Button variant="secondary" size="sm" disabled={items.length >= 50} onClick={() => setItems((rows) => [...rows, { ...EMPTY_ITEM }])}>
                      <Plus size={13} aria-hidden /> {t('docLayout.create.addItem')}
                    </Button>
                  </div>
                </div>
                <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
                  <Labeled label={t('docLayout.create.reference')}>
                    <input value={reference} maxLength={60} onChange={(e) => setReference(e.target.value)} className={input} />
                  </Labeled>
                  {type === 'invoice' && (
                    <Labeled label={t('docLayout.create.dueDate')}>
                      <input type="date" value={dueDate} onChange={(e) => setDueDate(e.target.value)} className={input} />
                    </Labeled>
                  )}
                  {type === 'receipt' && (
                    <Labeled label={t('docLayout.create.paymentMethod')}>
                      <input value={paymentMethod} maxLength={60} onChange={(e) => setPaymentMethod(e.target.value)} className={input} />
                    </Labeled>
                  )}
                </div>
                <Labeled label={t('docLayout.create.notes')}>
                  <textarea rows={2} value={notes} maxLength={1000} onChange={(e) => setNotes(e.target.value)} className={`${input} resize-y`} />
                </Labeled>
              </>
            ) : (
              <Labeled label={t('docLayout.create.letterText')} hint={t('docLayout.create.letterTextHint')}>
                <textarea rows={6} value={letter} maxLength={8000} onChange={(e) => setLetter(e.target.value)} className={`${input} resize-y`} />
              </Labeled>
            )}

            {fields.length > 0 && (
              <section className="flex flex-col gap-3 rounded-lg border border-line p-3">
                <div className="eyebrow">{t('docLayout.create.layoutFields')}</div>
                <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
                  {fields.map((fd) => (
                    <Labeled key={fd.key} label={fd.required ? `${fd.label || fd.key} · ${t('docLayout.create.required')}` : fd.label || fd.key}>
                      {fd.type === 'multiline' ? (
                        <textarea
                          rows={2}
                          value={values[fd.key] ?? ''}
                          maxLength={2000}
                          onChange={(e) => setValues((v) => ({ ...v, [fd.key]: e.target.value }))}
                          className={`${input} resize-y`}
                        />
                      ) : (
                        <input
                          type={fd.type === 'date' ? 'date' : 'text'}
                          inputMode={fd.type === 'number' ? 'decimal' : undefined}
                          value={values[fd.key] ?? ''}
                          maxLength={500}
                          onChange={(e) => setValues((v) => ({ ...v, [fd.key]: e.target.value }))}
                          className={input}
                        />
                      )}
                    </Labeled>
                  ))}
                </div>
              </section>
            )}

            <label className="flex items-center gap-2 text-[12.5px] text-text-high">
              <Toggle on={send} onChange={setSend} label={t('docLayout.create.send')} />
              {t('docLayout.create.send')}
            </label>
          </>
        )}
      </div>
    </Modal>
  )
}

function Labeled({ label, hint, children }: { label: string; hint?: string; children: ReactNode }) {
  return (
    <label className="block min-w-0">
      <span className="mb-1.5 block text-[12px] font-semibold text-text-high">{label}</span>
      {children}
      {hint && <span className="mt-1 block text-[11px] leading-snug text-text-muted">{hint}</span>}
    </label>
  )
}
