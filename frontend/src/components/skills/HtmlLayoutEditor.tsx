'use client'

import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import {
  AlertCircle,
  CheckCircle2,
  ExternalLink,
  FileText,
  FileUp,
  Loader2,
  Plus,
  Sparkles,
  Trash2,
  Undo2,
  Upload,
  X,
} from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Select } from '@/components/ui/Select'
import { Toggle } from '@/components/ui/Toggle'
import { useT, type MessageKey } from '@/i18n'
import { CodeEditor } from './CodeEditor'
import { uploadLetterhead } from './documentTemplate'
import { DOCUMENT_TYPES, openAuthed, testToolApp, type DocumentType, type DocumentsConfig } from './readySkills'
import {
  FIELD_TYPES,
  MAX_FIELDS,
  assistLayout,
  completeLayout,
  draftLayout,
  fieldProblem,
  previewLayout,
  type FieldType,
  type HtmlField,
  type HtmlLayout,
  type HtmlReference,
  type HtmlStarter,
} from './htmlLayout'

/**
 * The Documents skill's CUSTOM LAYOUT editor (the designer's "Custom layout (HTML)"
 * mode): the template in the code editor (HTML + Handlebars colours, the error line
 * marked), the custom Fields, the layout's Words & page (terms, bank details, page,
 * letterhead) and a Reference of every variable / helper / label — one click inserts
 * it at the cursor. Top: Start from (the starters), Upload my design (→ the AI draft,
 * or the closest starter without a model), Ask AI and Undo. Right: an INSTANT preview
 * (the server renders the template with sample data → a sandboxed iframe, debounced
 * while typing) and the exact "PDF preview" (the skill test's documents dry run).
 * Nothing is saved here — the designer's Apply hands the layout to the skill form.
 */

const field =
  'rounded-lg border border-line-strong bg-card px-3 py-2 text-[13px] text-text-primary outline-none transition-colors focus:border-brand-400 disabled:opacity-70'
const input = `w-full ${field}`

const TYPE_LABEL: Record<DocumentType, MessageKey> = {
  invoice: 'readySkills.form.types.invoice',
  quotation: 'readySkills.form.types.quotation',
  receipt: 'readySkills.form.types.receipt',
  letter: 'readySkills.form.types.letter',
}

const STARTER_KEY: Record<string, 'malaysianClassic' | 'cleanModern' | 'letter'> = {
  'malaysian-classic': 'malaysianClassic',
  'clean-modern': 'cleanModern',
  letter: 'letter',
}

const GROUP_KEY: Record<string, MessageKey> = {
  business: 'docLayout.reference.groups.business',
  document: 'docLayout.reference.groups.document',
  customer: 'docLayout.reference.groups.customer',
  items: 'docLayout.reference.groups.items',
  totals: 'docLayout.reference.groups.totals',
  other: 'docLayout.reference.groups.other',
  fields: 'docLayout.reference.groups.fields',
}

const HELPER_KEY: Record<string, MessageKey> = {
  money: 'docLayout.reference.helperHelp.money',
  number: 'docLayout.reference.helperHelp.number',
  date: 'docLayout.reference.helperHelp.date',
  words: 'docLayout.reference.helperHelp.words',
  upper: 'docLayout.reference.helperHelp.upper',
  lower: 'docLayout.reference.helperHelp.lower',
  inc: 'docLayout.reference.helperHelp.inc',
  eq: 'docLayout.reference.helperHelp.eq',
  ne: 'docLayout.reference.helperHelp.ne',
  gt: 'docLayout.reference.helperHelp.gt',
  nl2br: 'docLayout.reference.helperHelp.nl2br',
  first_line: 'docLayout.reference.helperHelp.firstLine',
  rest_lines: 'docLayout.reference.helperHelp.restLines',
  label: 'docLayout.reference.helperHelp.label',
  default: 'docLayout.reference.helperHelp.default',
  or: 'docLayout.reference.helperHelp.or',
}

/** Paths that are lists — inserted as a loop. */
const LISTS = new Set(['business.address_lines', 'customer.address_lines', 'document.paragraphs', 'terms', 'description_rest'])

/** What a click on a reference entry inserts. */
function snippetFor(path: string): string {
  if (path === 'business.logo') return '{{#if business.logo}}<img src="{{{business.logo}}}" alt="">{{/if}}'
  if (path === 'items') return '{{#each items}}\n  <tr><td>{{index}}</td><td>{{description}}</td><td>{{amount_text}}</td></tr>\n{{/each}}'
  if (LISTS.has(path)) return `{{#each ${path}}}{{this}}<br>{{/each}}`
  if (path.startsWith('{{')) return path
  return `{{${path}}}`
}

const BUSINESS_KEYS = ['business_name', 'address', 'registration_no', 'tax_no', 'phone', 'email', 'website'] as const
type BusinessKey = (typeof BUSINESS_KEYS)[number]

/** The paper the preview prints on — a document colour, the same in every UI theme. */
const PAPER = '#FFFFFF'
const DESK = '#E5E7EB'

/** The screen view of a printable page: a sheet of paper with the template's own @page margins. */
function screenPage(html: string, layout: HtmlLayout): string {
  const css = (layout.template.match(/@page\s*\{([^}]*)\}/i) ?? [])[1] ?? ''
  const margin = (css.match(/margin\s*:\s*([^;]+)/i) ?? [])[1]?.trim()
  const m = layout.page.margin_mm
  const padding = margin || `${m.top}mm ${m.right}mm ${m.bottom}mm ${m.left}mm`
  const letter = /size\s*:\s*letter/i.test(css) || (!/size\s*:/i.test(css) && layout.page.size === 'Letter')
  const width = letter ? '215.9mm' : '210mm'
  const style =
    `<style>@media screen{html{background:${DESK} !important}body{box-sizing:border-box !important;width:${width} !important;` +
    `min-height:${letter ? '279.4mm' : '297mm'} !important;margin:0 auto !important;padding:${padding} !important;` +
    `background:${PAPER} !important}}</style>`
  return html.includes('</body>') ? html.replace(/<\/body>(?![\s\S]*<\/body>)/i, `${style}</body>`) : html + style
}

export function HtmlLayoutEditor({
  config,
  value,
  onChange,
  onBusiness,
  starters,
  reference,
  rendererMissing,
  readOnly = false,
  initialType,
}: {
  config: DocumentsConfig
  value: HtmlLayout
  onChange: (next: HtmlLayout) => void
  /** Business lines read from an uploaded design (only the empty ones are filled). */
  onBusiness: (filled: Partial<Record<BusinessKey, string>>) => void
  starters: HtmlStarter[]
  reference: HtmlReference | null
  rendererMissing: boolean
  readOnly?: boolean
  initialType: DocumentType
}) {
  const t = useT()
  const [history, setHistory] = useState<HtmlLayout[]>([])
  const [tab, setTab] = useState<'fields' | 'words' | 'reference'>('fields')
  const [notice, setNotice] = useState<{ tone: 'ok' | 'error'; text: string } | null>(null)
  const [busy, setBusy] = useState<null | 'upload' | 'ask' | 'pdf' | 'letterhead'>(null)
  const layout = value

  const set = useCallback((patch: Partial<HtmlLayout>) => onChange({ ...layout, ...patch }), [layout, onChange])
  /** A big change (a starter, an upload, an AI edit) — Undo brings back what was there. */
  const replace = (next: HtmlLayout) => {
    setHistory((h) => [...h.slice(-19), layout])
    onChange(next)
  }
  const undo = () => {
    if (!history.length) return
    onChange(history[history.length - 1])
    setHistory((h) => h.slice(0, -1))
    setNotice(null)
  }

  // ---- inserting at the cursor (kept across clicks on the reference) ------------------
  const area = useRef<HTMLTextAreaElement>(null)
  const caret = useRef<{ start: number; end: number }>({ start: 0, end: 0 })
  useEffect(() => {
    const el = area.current
    if (!el) return
    const remember = () => {
      caret.current = { start: el.selectionStart, end: el.selectionEnd }
    }
    el.addEventListener('select', remember)
    el.addEventListener('keyup', remember)
    el.addEventListener('click', remember)
    el.addEventListener('blur', remember)
    return () => {
      el.removeEventListener('select', remember)
      el.removeEventListener('keyup', remember)
      el.removeEventListener('click', remember)
      el.removeEventListener('blur', remember)
    }
  }, [])
  const insert = (snippet: string) => {
    if (readOnly) return
    const v = layout.template
    const start = Math.min(caret.current.start, v.length)
    const end = Math.min(Math.max(caret.current.end, start), v.length)
    set({ template: v.slice(0, start) + snippet + v.slice(end) })
    const at = start + snippet.length
    caret.current = { start: at, end: at }
    requestAnimationFrame(() => {
      area.current?.focus()
      area.current?.setSelectionRange(at, at)
    })
  }

  // ---- start from / upload / ask ---------------------------------------------------------
  const applyStarter = (s: HtmlStarter) => {
    const next = completeLayout(s.html)
    replace({ ...next, terms: layout.terms, bank_details: layout.bank_details, letterhead: layout.letterhead })
    setNotice(null)
  }

  const fileRef = useRef<HTMLInputElement>(null)
  const [instruction, setInstruction] = useState('')
  const pickDesign = async (file: File | undefined) => {
    if (!file) return
    setBusy('upload')
    setNotice(null)
    try {
      const r = await draftLayout(file, { instruction, config: { ...config, html: layout } })
      replace(r.html)
      const filled: Partial<Record<BusinessKey, string>> = {}
      for (const k of BUSINESS_KEYS) {
        const v = r.business?.[k]
        if (v && !String(config[k] ?? '').trim()) filled[k] = v
      }
      onBusiness(filled)
      setNotice({
        tone: 'ok',
        text: [
          r.mode === 'ai' ? t('docLayout.upload.doneAi') : t('docLayout.upload.doneStarter'),
          r.summary,
          Object.keys(filled).length
            ? t('docLayout.upload.filledIn', {
                fields: Object.keys(filled).map((k) => t(`readySkills.designer.business.${k as BusinessKey}`)).join(', '),
              })
            : '',
          r.note,
        ]
          .filter(Boolean)
          .join(' '),
      })
    } catch (e) {
      setNotice({ tone: 'error', text: e instanceof Error ? e.message : String(e) })
    } finally {
      setBusy(null)
      if (fileRef.current) fileRef.current.value = ''
    }
  }

  const [ask, setAsk] = useState('')
  const runAsk = async () => {
    const words = ask.trim()
    if (!words || !layout.template.trim()) return
    setBusy('ask')
    setNotice(null)
    try {
      const r = await assistLayout(layout.template, layout.fields, words)
      if (r.mode === 'ai') replace({ ...layout, template: r.template, fields: r.fields })
      setNotice({ tone: 'ok', text: [r.summary, r.mode === 'sandbox' ? t('docLayout.ask.sandbox') : '', r.note].filter(Boolean).join(' ') })
      if (r.mode === 'ai') setAsk('')
    } catch (e) {
      setNotice({ tone: 'error', text: e instanceof Error ? e.message : String(e) })
    } finally {
      setBusy(null)
    }
  }

  // ---- the instant preview ---------------------------------------------------------------
  const [type, setType] = useState<DocumentType>(initialType)
  const [language, setLanguage] = useState<'en' | 'ms'>(config.language === 'ms' ? 'ms' : 'en')
  const [preview, setPreview] = useState<{ state: 'idle' | 'loading' | 'ok' | 'error'; error?: string; line?: number | null }>({
    state: 'idle',
  })
  // the last page that printed: kept on screen while the next one renders or while the template has an error
  const [shown, setShown] = useState<string | null>(null)
  const previewConfig = useMemo(() => ({ ...config, layout: 'html' as const, html: layout }), [config, layout])
  useEffect(() => {
    if (!layout.template.trim()) {
      setPreview({ state: 'idle' })
      return
    }
    const ctl = new AbortController()
    setPreview((p) => ({ ...p, state: 'loading' }))
    const timer = window.setTimeout(async () => {
      try {
        const r = await previewLayout(
          { html: layout.template, fields: layout.fields, type, language, config: previewConfig },
          ctl.signal,
        )
        if (ctl.signal.aborted) return
        if (r.ok) {
          setShown(screenPage(r.html, layout))
          setPreview({ state: 'ok' })
        } else setPreview({ state: 'error', error: r.error, line: r.line })
      } catch (e) {
        if (!ctl.signal.aborted)
          setPreview({ state: 'error', error: e instanceof Error ? e.message : String(e), line: null })
      }
    }, 450)
    return () => {
      ctl.abort()
      window.clearTimeout(timer)
    }
  }, [layout, previewConfig, type, language])

  const pdfPreview = async () => {
    setBusy('pdf')
    setNotice(null)
    try {
      const r = await testToolApp(previewConfig, type)
      if (r.url) await openAuthed(r.url)
      if (!r.ok) setNotice({ tone: 'error', text: r.detail })
    } catch (e) {
      setNotice({ tone: 'error', text: e instanceof Error ? e.message : String(e) })
    } finally {
      setBusy(null)
    }
  }

  // ---- the preview's scale (an A4 sheet fitted to the column) -------------------------------
  const box = useRef<HTMLDivElement>(null)
  const [scale, setScale] = useState(0.6)
  useEffect(() => {
    const el = box.current
    if (!el || typeof ResizeObserver === 'undefined') return
    const ro = new ResizeObserver(([entry]) => setScale(Math.min(1, entry.contentRect.width / 860)))
    ro.observe(el)
    return () => ro.disconnect()
  }, [])

  // ---- letterhead ----------------------------------------------------------------------------
  const lhRef = useRef<HTMLInputElement>(null)
  const pickLetterhead = async (file: File | undefined) => {
    if (!file) return
    setBusy('letterhead')
    setNotice(null)
    try {
      const r = await uploadLetterhead(file)
      set({ letterhead: { ...layout.letterhead, file: r.letterhead } })
    } catch (e) {
      setNotice({ tone: 'error', text: e instanceof Error ? e.message : String(e) })
    } finally {
      setBusy(null)
      if (lhRef.current) lhRef.current.value = ''
    }
  }

  const problem = fieldProblem(layout.fields)
  const problemText = problem
    ? t(`docLayout.fields.problems.${problem.kind}`, { n: problem.index + 1, key: layout.fields[problem.index]?.key ?? '' })
    : null

  return (
    <div className="flex min-h-0 flex-col lg:h-full lg:flex-row">
      {/* ---- left: tools, the template, fields / words / reference ---- */}
      <div className="flex min-w-0 flex-col gap-4 border-line p-4 lg:w-[56%] lg:shrink-0 lg:overflow-y-auto lg:border-r">
        {rendererMissing && (
          <div role="status" className="flex items-start gap-2 rounded-lg border border-amber/40 bg-amber/10 px-3 py-2 text-[12px] leading-relaxed text-text-high">
            <AlertCircle size={13} className="mt-0.5 shrink-0" aria-hidden />
            <span className="min-w-0">{t('docLayout.renderer.missing')}</span>
          </div>
        )}

        {!readOnly && (
          <section className="flex flex-col gap-3">
            <div className="flex flex-wrap items-center gap-2">
              <div className="eyebrow min-w-0 flex-1">{t('docLayout.start.title')}</div>
              {history.length > 0 && (
                <Button variant="ghost" size="sm" onClick={undo}>
                  <Undo2 size={13} aria-hidden /> {t('docLayout.undo')}
                </Button>
              )}
            </div>
            <div className="grid grid-cols-1 gap-2 sm:grid-cols-3">
              {starters.map((s) => {
                const key = STARTER_KEY[s.id]
                return (
                  <button
                    key={s.id}
                    type="button"
                    onClick={() => applyStarter(s)}
                    className="flex flex-col gap-1 rounded-lg border border-line bg-card p-2.5 text-left transition-colors hover:border-line-strong"
                  >
                    <span className="text-[12.5px] font-semibold text-text-high">
                      {key ? t(`docLayout.start.starters.${key}.name`) : s.name}
                    </span>
                    <span className="text-[11px] leading-snug text-text-muted">
                      {key ? t(`docLayout.start.starters.${key}.description`) : s.description}
                    </span>
                  </button>
                )
              })}
            </div>
            <div className="text-[11px] leading-snug text-text-muted">{t('docLayout.start.hint')}</div>

            <div className="flex flex-col gap-2 rounded-lg border border-line p-3">
              <div className="flex flex-wrap items-center gap-2">
                <input
                  ref={fileRef}
                  type="file"
                  accept="application/pdf,image/png,image/jpeg"
                  className="hidden"
                  onChange={(e) => pickDesign(e.target.files?.[0])}
                />
                <Button variant="secondary" size="sm" disabled={!!busy} onClick={() => fileRef.current?.click()}>
                  {busy === 'upload' ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Upload size={13} aria-hidden />}{' '}
                  {busy === 'upload' ? t('docLayout.upload.busy') : t('docLayout.upload.button')}
                </Button>
                <input
                  value={instruction}
                  onChange={(e) => setInstruction(e.target.value)}
                  maxLength={1000}
                  aria-label={t('docLayout.upload.instruction')}
                  placeholder={t('docLayout.upload.instructionPlaceholder')}
                  className={`${field} min-w-0 flex-1`}
                />
              </div>
              <div className="text-[11px] leading-snug text-text-muted">{t('docLayout.upload.hint')}</div>
            </div>

            <form
              className="flex gap-2"
              onSubmit={(e) => {
                e.preventDefault()
                runAsk()
              }}
            >
              <input
                value={ask}
                onChange={(e) => setAsk(e.target.value)}
                maxLength={1000}
                aria-label={t('docLayout.ask.label')}
                placeholder={t('docLayout.ask.placeholder')}
                className={`${input} min-w-0 flex-1`}
              />
              <Button type="submit" variant="accent" size="sm" disabled={!!busy || !ask.trim() || !layout.template.trim()}>
                {busy === 'ask' ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Sparkles size={13} aria-hidden />}{' '}
                {t('docLayout.ask.label')}
              </Button>
            </form>

            {notice && (
              <div
                role={notice.tone === 'error' ? 'alert' : 'status'}
                className={`flex items-start gap-2 rounded-lg border px-3 py-2 text-[12px] leading-relaxed ${
                  notice.tone === 'error' ? 'border-red/40 bg-red/10 text-[color:var(--red-ink)]' : 'border-line bg-fill-subtle text-text-mid'
                }`}
              >
                {notice.tone === 'error' ? <AlertCircle size={13} className="mt-0.5 shrink-0" aria-hidden /> : <Sparkles size={13} className="mt-0.5 shrink-0" aria-hidden />}
                <span className="min-w-0 flex-1">{notice.text}</span>
                {notice.tone === 'ok' && history.length > 0 && (
                  <button type="button" onClick={undo} className="shrink-0 font-semibold text-text-high underline">
                    {t('docLayout.undo')}
                  </button>
                )}
              </div>
            )}
          </section>
        )}

        {/* ---- the template ---- */}
        <section className="flex flex-col gap-1.5">
          <div className="eyebrow">{t('docLayout.tabs.html')}</div>
          <CodeEditor
            value={layout.template}
            onChange={(template) => set({ template })}
            readOnly={readOnly}
            minRows={16}
            maxRows={34}
            ariaLabel={t('docLayout.editor.aria')}
            highlight="html"
            invalid={preview.state === 'error'}
            errorLine={preview.state === 'error' ? preview.line ?? null : null}
            textareaRef={area}
          />
          {layout.template.trim() && (
            <div
              role={preview.state === 'error' ? 'alert' : 'status'}
              className={`flex items-start gap-1.5 text-[12px] leading-snug ${
                preview.state === 'error' ? 'text-[color:var(--red-ink)]' : 'text-text-muted'
              }`}
            >
              {preview.state === 'error' ? (
                <AlertCircle size={13} className="mt-0.5 shrink-0" aria-hidden />
              ) : preview.state === 'loading' ? (
                <Loader2 size={13} className="mt-0.5 shrink-0 animate-spin" aria-hidden />
              ) : (
                <CheckCircle2 size={13} className="mt-0.5 shrink-0" aria-hidden />
              )}
              <span className="min-w-0">
                {preview.state === 'error'
                  ? preview.error || t('docLayout.preview.failed')
                  : preview.state === 'loading'
                    ? t('docLayout.editor.checking')
                    : t('docLayout.editor.ok')}
              </span>
            </div>
          )}
        </section>

        {/* ---- fields / words & page / reference ---- */}
        <section className="flex min-w-0 flex-col gap-3">
          <div role="tablist" className="inline-flex w-fit max-w-full overflow-x-auto rounded-lg border border-line-strong">
            {(['fields', 'words', 'reference'] as const).map((id) => (
              <button
                key={id}
                type="button"
                role="tab"
                aria-selected={tab === id}
                onClick={() => setTab(id)}
                className={`whitespace-nowrap px-3 py-1.5 text-[12px] font-semibold transition-colors ${
                  tab === id ? 'bg-primary text-on-primary' : 'bg-card text-text-mid hover:bg-fill-hover'
                }`}
              >
                {t(`docLayout.tabs.${id}`)}
                {id === 'fields' && layout.fields.length > 0 ? ` · ${layout.fields.length}` : ''}
              </button>
            ))}
          </div>

          {tab === 'fields' && (
            <FieldsPanel fields={layout.fields} readOnly={readOnly} problem={problemText} onInsert={insert}
              onChange={(fields) => set({ fields })} />
          )}

          {tab === 'words' && (
            <div className="flex flex-col gap-3">
              <Labeled label={t('docLayout.words.terms')} hint={t('docLayout.words.termsHint')}>
                <textarea
                  rows={4}
                  value={layout.terms}
                  maxLength={2000}
                  disabled={readOnly}
                  onChange={(e) => set({ terms: e.target.value })}
                  className={`${input} resize-y`}
                />
              </Labeled>
              <Labeled label={t('docLayout.words.bank')} hint={t('docLayout.words.bankHint')}>
                <textarea
                  rows={2}
                  value={layout.bank_details}
                  maxLength={1000}
                  disabled={readOnly}
                  onChange={(e) => set({ bank_details: e.target.value })}
                  className={`${input} resize-y`}
                />
              </Labeled>
              <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
                <Labeled label={t('docLayout.words.page')}>
                  <Select
                    value={layout.page.size}
                    size="md"
                    disabled={readOnly}
                    ariaLabel={t('docLayout.words.page')}
                    onChange={(v) => set({ page: { ...layout.page, size: v as 'A4' | 'Letter' } })}
                    options={(['A4', 'Letter'] as const).map((s) => ({ value: s, label: t(`readySkills.designer.pageSizes.${s}`) }))}
                  />
                </Labeled>
              </div>
              <Labeled label={t('docLayout.words.margins')} hint={t('docLayout.words.marginsHint')}>
                <div className="grid grid-cols-2 gap-2 sm:grid-cols-4">
                  {(['top', 'right', 'bottom', 'left'] as const).map((k) => (
                    <label key={k} className="flex flex-col gap-1 text-[11px] text-text-muted">
                      {t(`docLayout.words.sides.${k}`)}
                      <input
                        type="number"
                        inputMode="decimal"
                        min={0}
                        max={60}
                        step={0.5}
                        disabled={readOnly}
                        value={layout.page.margin_mm[k]}
                        onChange={(e) => {
                          const n = Number(e.target.value)
                          if (e.target.value !== '' && Number.isFinite(n) && n >= 0 && n <= 60)
                            set({ page: { ...layout.page, margin_mm: { ...layout.page.margin_mm, [k]: n } } })
                        }}
                        className={input}
                      />
                    </label>
                  ))}
                </div>
              </Labeled>
              <Labeled label={t('docLayout.words.letterhead')} hint={t('docLayout.words.letterheadHint')}>
                <input ref={lhRef} type="file" accept="application/pdf" className="hidden" onChange={(e) => pickLetterhead(e.target.files?.[0])} />
                <div className="flex flex-wrap items-center gap-2">
                  <Button variant="secondary" size="sm" disabled={!!busy || readOnly} onClick={() => lhRef.current?.click()}>
                    {busy === 'letterhead' ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <FileUp size={13} aria-hidden />}{' '}
                    {layout.letterhead.file ? t('docLayout.words.letterheadReplace') : t('docLayout.words.letterheadUpload')}
                  </Button>
                  {layout.letterhead.file && (
                    <>
                      <Button
                        variant="ghost"
                        size="sm"
                        onClick={() => openAuthed(`/api/aicore/skills/documents/letterhead/${layout.letterhead.file}`).catch(() => undefined)}
                      >
                        <ExternalLink size={13} aria-hidden /> {t('docLayout.words.letterheadOpen')}
                      </Button>
                      <Button variant="ghost" size="sm" disabled={readOnly} onClick={() => set({ letterhead: { ...layout.letterhead, file: '' } })}>
                        <X size={13} aria-hidden /> {t('docLayout.words.letterheadRemove')}
                      </Button>
                    </>
                  )}
                </div>
                {layout.letterhead.file && (
                  <div className="mt-2 max-w-[260px]">
                    <Select
                      value={layout.letterhead.pages}
                      size="md"
                      disabled={readOnly}
                      ariaLabel={t('docLayout.words.letterheadPages')}
                      onChange={(v) => set({ letterhead: { ...layout.letterhead, pages: v as 'all' | 'first' } })}
                      options={(['all', 'first'] as const).map((p) => ({ value: p, label: t(`docLayout.words.letterheadPagesOpts.${p}`) }))}
                    />
                  </div>
                )}
              </Labeled>
            </div>
          )}

          {tab === 'reference' && <ReferencePanel reference={reference} fields={layout.fields} onInsert={insert} readOnly={readOnly} />}
        </section>
      </div>

      {/* ---- right: the preview ---- */}
      <div className="flex min-h-[70vh] min-w-0 flex-1 flex-col gap-2 p-4 lg:min-h-0">
        <div className="flex flex-wrap items-center gap-2">
          <div className="eyebrow min-w-0 flex-1">{t('docLayout.preview.title')}</div>
          <div className="w-[150px]">
            <Select
              value={type}
              size="sm"
              ariaLabel={t('docLayout.preview.type')}
              onChange={(v) => setType(v as DocumentType)}
              options={DOCUMENT_TYPES.map((d) => ({ value: d, label: t(TYPE_LABEL[d]) }))}
            />
          </div>
          <div className="w-[120px]">
            <Select
              value={language}
              size="sm"
              ariaLabel={t('docLayout.preview.language')}
              onChange={(v) => setLanguage(v as 'en' | 'ms')}
              options={(['en', 'ms'] as const).map((l) => ({ value: l, label: t(`docLayout.preview.languages.${l}`) }))}
            />
          </div>
          <Button variant="secondary" size="sm" disabled={!!busy || !layout.template.trim()} onClick={pdfPreview}>
            {busy === 'pdf' ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <FileText size={13} aria-hidden />}{' '}
            {busy === 'pdf' ? t('docLayout.preview.pdfBusy') : t('docLayout.preview.pdf')}
          </Button>
        </div>
        <div className="text-[11.5px] text-text-muted">{t('docLayout.preview.instant')}</div>
        <div ref={box} className="relative min-h-[60vh] flex-1 overflow-hidden rounded-xl border border-line bg-well">
          {shown && layout.template.trim() ? (
            <div className="absolute inset-0 overflow-auto">
              <div style={{ width: 860 * scale, height: 1220 * scale }}>
                <iframe
                  sandbox=""
                  srcDoc={shown}
                  title={t('docLayout.preview.frame')}
                  style={{ width: 860, height: 1220, transform: `scale(${scale})`, transformOrigin: 'top left' }}
                  className="border-0"
                />
              </div>
            </div>
          ) : (
            <div className="absolute inset-0 grid place-items-center p-6 text-center text-[12.5px] text-text-muted">
              {layout.template.trim() ? t('readySkills.designer.previewing') : t('docLayout.preview.empty')}
            </div>
          )}
        </div>
      </div>
    </div>
  )
}

// =========================================================================================
// panels
// =========================================================================================

function FieldsPanel({
  fields,
  readOnly,
  problem,
  onInsert,
  onChange,
}: {
  fields: HtmlField[]
  readOnly: boolean
  problem: string | null
  onInsert: (snippet: string) => void
  onChange: (fields: HtmlField[]) => void
}) {
  const t = useT()
  const patch = (i: number, p: Partial<HtmlField>) => onChange(fields.map((f, j) => (j === i ? { ...f, ...p } : f)))
  return (
    <div className="flex flex-col gap-3">
      <div className="text-[12px] leading-relaxed text-text-mid">
        {t('docLayout.fields.intro')} {t('docLayout.fields.printAs')}{' '}
        <code className="rounded bg-fill-subtle px-1 font-mono text-[11.5px] text-text-high">{'{{fields.key}}'}</code>
      </div>
      {problem && (
        <div role="alert" className="flex items-start gap-2 rounded-lg border border-red/40 bg-red/10 px-3 py-2 text-[12px] text-[color:var(--red-ink)]">
          <AlertCircle size={13} className="mt-0.5 shrink-0" aria-hidden />
          <span>{problem}</span>
        </div>
      )}
      {fields.length === 0 && <div className="text-[12px] text-text-muted">{t('docLayout.fields.none')}</div>}
      {fields.map((f, i) => (
        <div key={i} className="grid grid-cols-2 gap-2 rounded-lg border border-line bg-card p-2.5 sm:grid-cols-[1fr_1fr_130px]">
          <label className="flex min-w-0 flex-col gap-1 text-[11px] text-text-muted">
            {t('docLayout.fields.key')}
            <input
              value={f.key}
              maxLength={32}
              disabled={readOnly}
              onChange={(e) => patch(i, { key: e.target.value.trim() })}
              className={`${input} font-mono`}
            />
          </label>
          <label className="flex min-w-0 flex-col gap-1 text-[11px] text-text-muted">
            {t('docLayout.fields.label')}
            <input value={f.label} maxLength={60} disabled={readOnly} onChange={(e) => patch(i, { label: e.target.value })} className={input} />
          </label>
          <div className="col-span-2 flex min-w-0 flex-col gap-1 text-[11px] text-text-muted sm:col-span-1">
            {t('docLayout.fields.type')}
            <Select
              value={f.type}
              size="md"
              disabled={readOnly}
              ariaLabel={t('docLayout.fields.type')}
              onChange={(v) => patch(i, { type: v as FieldType })}
              options={FIELD_TYPES.map((x) => ({ value: x, label: t(`docLayout.fields.types.${x}`) }))}
            />
          </div>
          <label className="col-span-2 flex min-w-0 flex-col gap-1 text-[11px] text-text-muted">
            {t('docLayout.fields.default')}
            <input
              value={f.default}
              maxLength={f.type === 'multiline' ? 2000 : 500}
              disabled={readOnly}
              placeholder={f.type === 'date' ? 'YYYY-MM-DD' : undefined}
              onChange={(e) => patch(i, { default: e.target.value })}
              className={input}
            />
          </label>
          <div className="col-span-2 flex flex-wrap items-center gap-2 sm:col-span-3">
            <span className="flex items-center gap-2 text-[12px] text-text-high">
              <Toggle on={f.required} disabled={readOnly} label={t('docLayout.fields.required')} onChange={(required) => patch(i, { required })} />
              {t('docLayout.fields.required')}
            </span>
            <span className="min-w-0 flex-1" />
            {f.key && (
              <Button variant="ghost" size="sm" disabled={readOnly} onClick={() => onInsert(`{{fields.${f.key}}}`)}>
                <code className="font-mono text-[11.5px]">{`{{fields.${f.key}}}`}</code>
              </Button>
            )}
            <button
              type="button"
              disabled={readOnly}
              onClick={() => onChange(fields.filter((_, j) => j !== i))}
              aria-label={t('docLayout.fields.remove', { name: f.label || f.key || String(i + 1) })}
              className="grid h-8 w-8 place-items-center rounded-lg text-text-muted hover:bg-fill-hover hover:text-text-high disabled:opacity-40"
            >
              <Trash2 size={14} aria-hidden />
            </button>
          </div>
        </div>
      ))}
      <div className="flex flex-wrap items-center gap-3">
        <Button
          variant="secondary"
          size="sm"
          disabled={readOnly || fields.length >= MAX_FIELDS}
          onClick={() => onChange([...fields, { key: '', label: '', type: 'text', required: false, default: '' }])}
        >
          <Plus size={13} aria-hidden /> {t('docLayout.fields.add')}
        </Button>
        <span className="text-[11px] text-text-muted">{t('docLayout.fields.max')}</span>
      </div>
    </div>
  )
}

function ReferencePanel({
  reference,
  fields,
  onInsert,
  readOnly,
}: {
  reference: HtmlReference | null
  fields: HtmlField[]
  onInsert: (snippet: string) => void
  readOnly: boolean
}) {
  const t = useT()
  if (!reference) return null
  // "Your fields" always shows the CURRENT fields (the catalog's list is an example)
  const groups = reference.groups.map((g) =>
    g.id === 'fields' && fields.some((f) => f.key)
      ? { ...g, vars: fields.filter((f) => f.key).map((f) => ({ path: `fields.${f.key}`, example: f.default || f.label, description: f.label })) }
      : g,
  )
  const chip =
    'flex min-w-0 items-baseline gap-2 rounded-md border border-line bg-card px-2 py-1 text-left transition-colors hover:border-brand-400 disabled:opacity-60'
  return (
    <div className="flex flex-col gap-3">
      <div className="text-[12px] leading-relaxed text-text-mid">{t('docLayout.reference.intro')}</div>
      {groups.map((g) => (
        <RefGroup key={g.id} title={GROUP_KEY[g.id] ? t(GROUP_KEY[g.id]) : g.id}>
          {g.vars.map((v) => (
            <button key={v.path} type="button" disabled={readOnly} onClick={() => onInsert(snippetFor(v.path))} className={chip}>
              <code className="shrink-0 font-mono text-[11.5px] text-text-high">{v.path}</code>
              <span className="min-w-0 truncate text-[11px] text-text-muted">{v.example}</span>
            </button>
          ))}
        </RefGroup>
      ))}
      <RefGroup title={t('docLayout.reference.helpers')}>
        {reference.helpers.map((h) => (
          <button
            key={h.name}
            type="button"
            disabled={readOnly}
            onClick={() => onInsert(h.usage.split(' · ')[0])}
            className={`${chip} flex-col items-start`}
          >
            <code className="font-mono text-[11.5px] text-text-high">{h.usage.split(' · ')[0]}</code>
            <span className="text-[11px] leading-snug text-text-muted">{HELPER_KEY[h.name] ? t(HELPER_KEY[h.name]) : h.description}</span>
          </button>
        ))}
      </RefGroup>
      <RefGroup title={t('docLayout.reference.labels')} flat>
        <div className="flex flex-wrap gap-1.5">
          {reference.labels.map((l) => (
            <button
              key={l}
              type="button"
              disabled={readOnly}
              onClick={() => onInsert(`{{label "${l}"}}`)}
              className="rounded-md border border-line bg-card px-2 py-0.5 font-mono text-[11px] text-text-high hover:border-brand-400 disabled:opacity-60"
            >
              {l}
            </button>
          ))}
        </div>
      </RefGroup>
    </div>
  )
}

function RefGroup({ title, children, flat = false }: { title: string; children: ReactNode; flat?: boolean }) {
  return (
    <section className="flex min-w-0 flex-col gap-1.5">
      <div className="text-[12px] font-semibold text-text-high">{title}</div>
      {flat ? children : <div className="grid min-w-0 grid-cols-1 gap-1.5 sm:grid-cols-2">{children}</div>}
    </section>
  )
}

function Labeled({ label, hint, children }: { label: string; hint?: string; children: ReactNode }) {
  return (
    <div className="min-w-0">
      <div className="mb-1.5 text-[12px] font-semibold text-text-high">{label}</div>
      {children}
      {hint && <div className="mt-1 text-[11px] leading-snug text-text-muted">{hint}</div>}
    </div>
  )
}
