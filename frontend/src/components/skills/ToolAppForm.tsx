'use client'

import { useEffect, useRef, useState, type ReactNode } from 'react'
import { AlertCircle, CheckCircle2, FileText, ImagePlus, Info, Loader2, Palette, PlugZap, QrCode, X } from 'lucide-react'
import { Advanced } from '@/components/ui/Advanced'
import { Button } from '@/components/ui/Button'
import { Select } from '@/components/ui/Select'
import { Toggle } from '@/components/ui/Toggle'
import { TagListInput } from '@/components/ui/TagListInput'
import { TAG_PALETTE } from '@/lib/tags'
import { useT, type MessageKey } from '@/i18n'
import {
  DOCUMENT_TYPES,
  TOOL_DEFAULTS,
  authedObjectUrl,
  openAuthed,
  testToolApp,
  uploadLogo,
  type DocumentType,
  type DocumentsConfig,
  type PaymentsConfig,
  type QrConfig,
  type SheetsConfig,
  type ToolAppCatalogEntry,
  type ToolAppConfig,
} from './readySkills'
import { DocumentDesigner } from './DocumentDesigner'
import type { DocTemplate } from './documentTemplate'

/**
 * Settings of a ready-made Connected app skill other than the calendar —
 * Documents (PDF), QR codes, Payment links, Google Sheets log. Simple first:
 * the few fields a first-time user needs; numbering, logo, accent, labels and
 * limits sit in Advanced. "Preview PDF" renders a SAMPLE with the unsaved
 * settings (nothing sent, no number used); Payments / Sheets "Check
 * connection" says whether the integration is there.
 */

const field =
  'rounded-lg border border-line-strong bg-card px-3 py-2 text-[13px] text-text-primary outline-none transition-colors focus:border-brand-400 disabled:opacity-70'
const input = `w-full ${field}`
const numInput = `w-[110px] ${field}`

const TYPE_LABEL: Record<DocumentType, MessageKey> = {
  invoice: 'readySkills.form.types.invoice',
  quotation: 'readySkills.form.types.quotation',
  receipt: 'readySkills.form.types.receipt',
  letter: 'readySkills.form.types.letter',
}

export function ToolAppForm({
  config,
  onChange,
  entry,
  readOnly = false,
}: {
  config: ToolAppConfig
  onChange: (next: ToolAppConfig) => void
  /** This app's GET /skills/apps entry (connection status); null while loading / offline. */
  entry: ToolAppCatalogEntry | null
  readOnly?: boolean
}) {
  const t = useT()
  const [busy, setBusy] = useState(false)
  const [result, setResult] = useState<{ ok: boolean; text: string } | null>(null)

  const run = async () => {
    setBusy(true)
    setResult(null)
    try {
      const firstType = config.app === 'documents' ? config.actions[0] : undefined
      const r = await testToolApp(config, firstType)
      if (r.url) await openAuthed(r.url)
      setResult({ ok: r.ok, text: r.detail })
    } catch (e) {
      setResult({ ok: false, text: e instanceof Error ? e.message : String(e) })
    } finally {
      setBusy(false)
    }
  }

  const action =
    config.app === 'documents' ? t('readySkills.form.preview')
      : config.app === 'qr' ? t('readySkills.form.previewQr')
        : t('readySkills.form.check')
  const ActionIcon = config.app === 'documents' ? FileText : config.app === 'qr' ? QrCode : PlugZap

  return (
    <div className="flex flex-col gap-4">
      {entry && entry.provider_label && !entry.connected && (
        <Note tone="amber">{t('readySkills.form.connectAt', { provider: entry.provider_label })}</Note>
      )}
      {config.app === 'documents' && (
        <DocumentsFields
          c={config}
          set={(p) => onChange({ ...config, ...p })}
          readOnly={readOnly}
          cjk={entry?.cjk_font}
          presets={entry?.template_presets}
          entry={entry}
        />
      )}
      {config.app === 'qr' && <QrFields c={config} set={(p) => onChange({ ...config, ...p })} readOnly={readOnly} />}
      {config.app === 'payments' && (
        <PaymentsFields c={config} set={(p) => onChange({ ...config, ...p })} readOnly={readOnly} />
      )}
      {config.app === 'sheets' && <SheetsFields c={config} set={(p) => onChange({ ...config, ...p })} readOnly={readOnly} />}

      <div className="flex flex-wrap items-center gap-3">
        <Button variant="secondary" size="sm" onClick={run} disabled={busy}>
          {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <ActionIcon size={13} aria-hidden />} {action}
        </Button>
        {result && (
          <span
            role={result.ok ? 'status' : 'alert'}
            className={`flex min-w-0 items-start gap-1.5 text-[12px] leading-snug ${result.ok ? 'text-text-mid' : 'text-[color:var(--red-ink)]'}`}
          >
            {result.ok ? <CheckCircle2 size={13} className="mt-0.5 shrink-0" aria-hidden /> : <AlertCircle size={13} className="mt-0.5 shrink-0" aria-hidden />}
            <span className="min-w-0">{result.text}</span>
          </span>
        )}
      </div>
    </div>
  )
}

// ---- Documents --------------------------------------------------------------------

function DocumentsFields({
  c,
  set,
  readOnly,
  cjk,
  presets,
  entry,
}: {
  c: DocumentsConfig
  set: (p: Partial<DocumentsConfig>) => void
  readOnly: boolean
  cjk?: boolean
  presets?: Record<string, DocTemplate>
  entry: ToolAppCatalogEntry | null
}) {
  const t = useT()
  const d = TOOL_DEFAULTS.documents
  const [designing, setDesigning] = useState(false)
  const overridden = c.template ? DOCUMENT_TYPES.filter((x) => c.template?.overrides?.[x] && Object.keys(c.template.overrides[x]!).length) : []
  const customised = [
    c.registration_no, c.tax_no, c.website, c.logo,
    c.footer_note !== d.footer_note ? 'x' : '', c.accent !== d.accent ? 'x' : '', c.language !== d.language ? 'x' : '',
    c.quotation_valid_days !== d.quotation_valid_days ? 'x' : '', c.invoice_due_days !== d.invoice_due_days ? 'x' : '',
    JSON.stringify(c.prefixes) !== JSON.stringify(d.prefixes) ? 'x' : '',
    JSON.stringify(c.start_numbers) !== JSON.stringify(d.start_numbers) ? 'x' : '',
  ].filter(Boolean).length

  return (
    <>
      <Section title={t('readySkills.form.letterhead')}>
        <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
          <Field label={t('readySkills.form.businessName')}>
            <input
              value={c.business_name}
              disabled={readOnly}
              placeholder={t('readySkills.form.businessNamePlaceholder')}
              onChange={(e) => set({ business_name: e.target.value })}
              className={input}
            />
          </Field>
          <Field label={t('readySkills.form.phone')}>
            <input value={c.phone} disabled={readOnly} onChange={(e) => set({ phone: e.target.value })} className={input} />
          </Field>
          <Field label={t('readySkills.form.address')}>
            <textarea
              rows={2}
              value={c.address}
              disabled={readOnly}
              onChange={(e) => set({ address: e.target.value })}
              className={`${input} resize-y`}
            />
          </Field>
          <Field label={t('readySkills.form.email')}>
            <input
              type="email"
              value={c.email}
              disabled={readOnly}
              onChange={(e) => set({ email: e.target.value })}
              className={input}
            />
          </Field>
        </div>
      </Section>

      <Section title={t('readySkills.designer.lookLabel')}>
        <div className="flex flex-wrap items-center gap-3 rounded-lg border border-line px-3 py-2.5">
          <div className="min-w-0 flex-1">
            <div className="text-[13px] font-semibold text-text-high">
              {c.layout === 'html'
                ? t('docLayout.look.custom')
                : c.template
                  ? t('readySkills.designer.lookCustom', { preset: t(`readySkills.designer.presets.${c.template.preset}`) })
                  : t('readySkills.designer.lookClassic')}
            </div>
            <div className="text-[11.5px] leading-snug text-text-muted">
              {c.layout === 'html'
                ? t('docLayout.look.customHint', { count: c.html?.fields.length ?? 0 })
                : overridden.length
                  ? t('readySkills.designer.lookOverrides', { types: overridden.map((x) => t(TYPE_LABEL[x])).join(', ') })
                  : t('readySkills.designer.lookHint')}
            </div>
          </div>
          <Button variant="secondary" size="sm" onClick={() => setDesigning(true)}>
            <Palette size={13} aria-hidden /> {t('readySkills.designer.open')}
          </Button>
          {c.layout === 'html' && !readOnly ? (
            <Button variant="ghost" size="sm" onClick={() => set({ layout: 'designer' })}>
              {t('docLayout.look.useDesigner')}
            </Button>
          ) : (
            c.template && !readOnly && (
              <Button variant="ghost" size="sm" onClick={() => set({ template: null })}>
                {t('readySkills.designer.useClassic')}
              </Button>
            )
          )}
        </div>
        {c.layout === 'html' && entry?.html_renderer === 'missing' && (
          <div className="mt-2">
            <Note tone="amber">{t('docLayout.renderer.missing')}</Note>
          </div>
        )}
        {designing && (
          <DocumentDesigner
            config={c}
            presets={presets}
            starters={entry?.html_starters}
            reference={entry?.html_reference ?? null}
            rendererMissing={entry?.html_renderer === 'missing'}
            readOnly={readOnly}
            onClose={() => setDesigning(false)}
            onApply={({ template, business, layout, html }) => set({ ...business, template, layout, html })}
          />
        )}
      </Section>

      <Section title={t('readySkills.form.money')}>
        <div className="flex flex-wrap items-start gap-3">
          <Field label={t('readySkills.form.currency')}>
            <input
              value={c.currency}
              maxLength={3}
              disabled={readOnly}
              onChange={(e) => set({ currency: e.target.value.toUpperCase() })}
              className={numInput}
            />
          </Field>
          <Field label={t('readySkills.form.taxLabel')}>
            <input value={c.tax_label} disabled={readOnly} onChange={(e) => set({ tax_label: e.target.value })} className={numInput} />
          </Field>
          <Field label={t('readySkills.form.taxRate')} hint={t('readySkills.form.taxHint')}>
            <input
              type="number"
              min={0}
              max={100}
              step="0.01"
              value={c.tax_rate}
              disabled={readOnly}
              onChange={(e) => set({ tax_rate: Number(e.target.value) })}
              className={numInput}
            />
          </Field>
        </div>
        <div className="mt-3">
          <Field label={t('readySkills.form.paymentInstructions')}>
            <textarea
              rows={2}
              value={c.payment_instructions}
              disabled={readOnly}
              placeholder={t('readySkills.form.paymentInstructionsPlaceholder')}
              onChange={(e) => set({ payment_instructions: e.target.value })}
              className={`${input} resize-y`}
            />
          </Field>
        </div>
      </Section>

      <Section title={t('readySkills.form.documentsAllowed')}>
        <div className="grid grid-cols-2 gap-2 sm:grid-cols-4">
          {DOCUMENT_TYPES.map((type) => (
            <div key={type} className="flex items-center justify-between gap-2 rounded-lg border border-line px-3 py-2">
              <span className="text-[12.5px] text-text-high">{t(TYPE_LABEL[type])}</span>
              <Toggle
                on={c.actions.includes(type)}
                disabled={readOnly}
                label={t(TYPE_LABEL[type])}
                onChange={(on) =>
                  set({ actions: DOCUMENT_TYPES.filter((x) => (x === type ? on : c.actions.includes(x))) })
                }
              />
            </div>
          ))}
        </div>
      </Section>
      {cjk === false && <Note tone="muted">{t('readySkills.form.cjkMissing')}</Note>}

      <Advanced count={customised} forceOpen={false}>
        <div className="flex flex-col gap-4">
          <div className="grid grid-cols-1 gap-3 md:grid-cols-3">
            <Field label={t('readySkills.form.registrationNo')}>
              <input value={c.registration_no} disabled={readOnly} onChange={(e) => set({ registration_no: e.target.value })} className={input} />
            </Field>
            <Field label={t('readySkills.form.taxNo')}>
              <input value={c.tax_no} disabled={readOnly} onChange={(e) => set({ tax_no: e.target.value })} className={input} />
            </Field>
            <Field label={t('readySkills.form.website')}>
              <input value={c.website} disabled={readOnly} onChange={(e) => set({ website: e.target.value })} className={input} />
            </Field>
          </div>
          <LogoField logo={c.logo} readOnly={readOnly} onChange={(logo) => set({ logo })} />
          <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
            <Field label={t('readySkills.form.accent')}>
              <div className="flex flex-wrap gap-1.5">
                {TAG_PALETTE.map((p) => (
                  <button
                    key={p.key}
                    type="button"
                    disabled={readOnly}
                    title={t(p.label)}
                    aria-label={t(p.label)}
                    aria-pressed={c.accent === p.key}
                    onClick={() => set({ accent: p.key })}
                    className={`h-7 w-7 rounded-full border-2 transition-transform ${c.accent === p.key ? 'scale-110 border-text-heading' : 'border-transparent'}`}
                    style={{ background: p.dot }}
                  />
                ))}
              </div>
            </Field>
            <Field label={t('readySkills.form.language')}>
              <Select
                value={c.language}
                disabled={readOnly}
                size="md"
                ariaLabel={t('readySkills.form.language')}
                onChange={(v) => set({ language: v as DocumentsConfig['language'] })}
                options={(['auto', 'en', 'ms'] as const).map((l) => ({ value: l, label: t(`readySkills.form.languages.${l}`) }))}
              />
            </Field>
          </div>
          <Field label={t('readySkills.form.footerNote')}>
            <input value={c.footer_note} disabled={readOnly} onChange={(e) => set({ footer_note: e.target.value })} className={input} />
          </Field>
          <div className="flex flex-wrap gap-3">
            <Field label={t('readySkills.form.quotationValidDays')}>
              <input type="number" min={1} max={365} value={c.quotation_valid_days} disabled={readOnly}
                onChange={(e) => set({ quotation_valid_days: Number(e.target.value) })} className={numInput} />
            </Field>
            <Field label={t('readySkills.form.invoiceDueDays')}>
              <input type="number" min={0} max={365} value={c.invoice_due_days} disabled={readOnly}
                onChange={(e) => set({ invoice_due_days: Number(e.target.value) })} className={numInput} />
            </Field>
          </div>
          <Field label={t('readySkills.form.numbering')} hint={t('readySkills.form.numberingHint')}>
            <div className="flex flex-col gap-2">
              {DOCUMENT_TYPES.map((type) => (
                <div key={type} className="flex flex-wrap items-center gap-2">
                  <span className="w-[110px] text-[12.5px] text-text-mid">{t(TYPE_LABEL[type])}</span>
                  <input
                    aria-label={`${t(TYPE_LABEL[type])} · ${t('readySkills.form.prefix')}`}
                    value={c.prefixes[type] ?? ''}
                    maxLength={12}
                    disabled={readOnly}
                    onChange={(e) => set({ prefixes: { ...c.prefixes, [type]: e.target.value } })}
                    className={numInput}
                  />
                  <input
                    type="number"
                    min={1}
                    aria-label={`${t(TYPE_LABEL[type])} · ${t('readySkills.form.startNumber')}`}
                    value={c.start_numbers[type] ?? 1}
                    disabled={readOnly}
                    onChange={(e) => set({ start_numbers: { ...c.start_numbers, [type]: Number(e.target.value) } })}
                    className={numInput}
                  />
                </div>
              ))}
              <div className="flex items-center gap-2">
                <span className="w-[110px] text-[12.5px] text-text-mid">{t('readySkills.form.numberDigits')}</span>
                <input type="number" min={1} max={10} value={c.number_digits} disabled={readOnly}
                  onChange={(e) => set({ number_digits: Number(e.target.value) })} className={numInput} />
              </div>
            </div>
          </Field>
        </div>
      </Advanced>
    </>
  )
}

function LogoField({ logo, onChange, readOnly }: { logo: string; onChange: (logo: string) => void; readOnly: boolean }) {
  const t = useT()
  const [thumb, setThumb] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const inputRef = useRef<HTMLInputElement>(null)

  useEffect(() => {
    let url: string | null = null
    let cancelled = false
    setThumb(null)
    if (logo)
      authedObjectUrl(`/api/aicore/skills/documents/logo/${logo}`)
        .then((u) => {
          url = u
          if (!cancelled) setThumb(u)
        })
        .catch(() => undefined)
    return () => {
      cancelled = true
      if (url) URL.revokeObjectURL(url)
    }
  }, [logo])

  const pick = async (file: File | undefined) => {
    if (!file) return
    setBusy(true)
    setError(null)
    try {
      onChange((await uploadLogo(file)).logo)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
      if (inputRef.current) inputRef.current.value = ''
    }
  }

  return (
    <Field label={t('readySkills.form.logo')} hint={t('readySkills.form.logoHint')}>
      <div className="flex flex-wrap items-center gap-3">
        {thumb && (
          // eslint-disable-next-line @next/next/no-img-element -- an object URL of an auth-gated image
          <img src={thumb} alt="" className="h-10 max-w-[160px] rounded border border-line bg-card object-contain p-1" />
        )}
        <input
          ref={inputRef}
          type="file"
          accept="image/png,image/jpeg"
          className="hidden"
          onChange={(e) => pick(e.target.files?.[0])}
        />
        <Button variant="secondary" size="sm" disabled={readOnly || busy} onClick={() => inputRef.current?.click()}>
          {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <ImagePlus size={13} aria-hidden />}{' '}
          {t('readySkills.form.logoUpload')}
        </Button>
        {logo && !readOnly && (
          <Button variant="ghost" size="sm" onClick={() => onChange('')}>
            <X size={13} aria-hidden /> {t('readySkills.form.logoRemove')}
          </Button>
        )}
        {error && <span role="alert" className="text-[12px] text-[color:var(--red-ink)]">{error}</span>}
      </div>
    </Field>
  )
}

// ---- QR, Payments, Sheets ------------------------------------------------------------

function QrFields({ c, set, readOnly }: { c: QrConfig; set: (p: Partial<QrConfig>) => void; readOnly: boolean }) {
  const t = useT()
  return (
    <div className="flex flex-wrap items-start gap-3">
      <div className="min-w-[240px] flex-1">
        <Field label={t('readySkills.form.qrCaption')}>
          <input value={c.caption} disabled={readOnly} onChange={(e) => set({ caption: e.target.value })} className={input} />
        </Field>
      </div>
      <Field label={t('readySkills.form.qrSize')}>
        <input type="number" min={4} max={20} value={c.pixels_per_module} disabled={readOnly}
          onChange={(e) => set({ pixels_per_module: Number(e.target.value) })} className={numInput} />
      </Field>
    </div>
  )
}

function PaymentsFields({
  c,
  set,
  readOnly,
}: {
  c: PaymentsConfig
  set: (p: Partial<PaymentsConfig>) => void
  readOnly: boolean
}) {
  const t = useT()
  return (
    <>
      <div className="flex flex-wrap items-start gap-3">
        <Field label={t('readySkills.form.paymentsCurrency')}>
          <input value={c.currency} maxLength={3} disabled={readOnly}
            onChange={(e) => set({ currency: e.target.value.toUpperCase() })} className={numInput} />
        </Field>
        <Field label={t('readySkills.form.maxAmount')}>
          <input type="number" min={1} value={c.max_amount} disabled={readOnly}
            onChange={(e) => set({ max_amount: Number(e.target.value) })} className={numInput} />
        </Field>
      </div>
      <Advanced count={c.success_url ? 1 : 0}>
        <Field label={t('readySkills.form.successUrl')}>
          <input value={c.success_url} disabled={readOnly} placeholder="https://"
            onChange={(e) => set({ success_url: e.target.value })} className={input} />
        </Field>
      </Advanced>
    </>
  )
}

function SheetsFields({ c, set, readOnly }: { c: SheetsConfig; set: (p: Partial<SheetsConfig>) => void; readOnly: boolean }) {
  const t = useT()
  return (
    <>
      <Field label={t('readySkills.form.sheetId')}>
        <input value={c.spreadsheet_id} disabled={readOnly} placeholder={t('readySkills.form.sheetIdPlaceholder')}
          onChange={(e) => set({ spreadsheet_id: e.target.value.trim() })} className={input} />
      </Field>
      <Field label={t('readySkills.form.sheetColumns')} hint={t('readySkills.form.sheetColumnsHint')}>
        <TagListInput value={c.columns} onChange={(columns) => set({ columns })} disabled={readOnly} />
      </Field>
      <Advanced count={(c.sheet_range !== TOOL_DEFAULTS.sheets.sheet_range ? 1 : 0) + (c.label !== TOOL_DEFAULTS.sheets.label ? 1 : 0)}>
        <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
          <Field label={t('readySkills.form.sheetRange')}>
            <input value={c.sheet_range} disabled={readOnly} onChange={(e) => set({ sheet_range: e.target.value })} className={input} />
          </Field>
          <Field label={t('readySkills.form.sheetLabel')}>
            <input value={c.label} disabled={readOnly} onChange={(e) => set({ label: e.target.value })} className={input} />
          </Field>
        </div>
      </Advanced>
    </>
  )
}

// ---- bits ---------------------------------------------------------------------------

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="min-w-0">
      <div className="eyebrow mb-2">{title}</div>
      {children}
    </section>
  )
}

function Field({ label, hint, children }: { label: string; hint?: string; children: ReactNode }) {
  return (
    <div className="min-w-0">
      <div className="mb-1.5 text-[12px] font-semibold text-text-high">{label}</div>
      {children}
      {hint && <div className="mt-1 text-[11px] leading-snug text-text-muted">{hint}</div>}
    </div>
  )
}

function Note({ tone, children }: { tone: 'amber' | 'muted'; children: ReactNode }) {
  return (
    <div
      className={`flex items-start gap-2 rounded-lg border px-3 py-2 text-[12px] leading-relaxed ${
        tone === 'amber' ? 'border-amber/40 bg-amber/10 text-text-high' : 'border-line bg-fill-subtle text-text-mid'
      }`}
    >
      <Info size={13} className="mt-0.5 shrink-0" aria-hidden />
      <span className="min-w-0">{children}</span>
    </div>
  )
}
