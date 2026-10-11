'use client'

import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import {
  AlertCircle,
  ArrowDown,
  ArrowUp,
  ChevronDown,
  ExternalLink,
  FileUp,
  Loader2,
  Sparkles,
  Undo2,
  Upload,
  X,
} from 'lucide-react'
import { Advanced } from '@/components/ui/Advanced'
import { Button } from '@/components/ui/Button'
import { Select } from '@/components/ui/Select'
import { Toggle } from '@/components/ui/Toggle'
import { isIos } from '@/components/shell/pwa'
import { useT, type MessageKey } from '@/i18n'
import {
  DOCUMENT_TYPES,
  authedObjectUrl,
  openAuthed,
  testToolApp,
  type DocumentType,
  type DocumentsConfig,
} from './readySkills'
import {
  CLASSIC,
  COLUMNS,
  OVERRIDE_KEYS,
  PRESETS,
  analyzeDesign,
  assistTemplate,
  clone,
  completeTemplate,
  forType,
  normalizeHex,
  uploadLetterhead,
  withPreset,
  type Column,
  type DocTemplate,
  type OverrideKey,
  type Preset,
  type SectionId,
} from './documentTemplate'
import { HtmlLayoutEditor } from './HtmlLayoutEditor'
import { EMPTY_LAYOUT, completeLayout, fieldProblem, type HtmlLayout, type HtmlReference, type HtmlStarter } from './htmlLayout'

/**
 * The Documents skill's template designer — "Canva-like", kept practical: a
 * full-screen panel opened from the skill form ("Design template").
 *
 * - top: "Start from…" (the four presets as thumbnails), "Upload my design"
 *   (a PDF / PNG / JPEG → the server proposes a template, optionally keeping the
 *   PDF as the page background) and "Ask AI" (edit by words), with Undo;
 * - left: block panels (Layout, Brand & colours, Header, Info block, Items
 *   table, Totals, Notes & payment, Footer, Letterhead background) — simple
 *   fields first, the rest under Advanced — and the section order;
 * - right: a live PDF preview, re-rendered (debounced) through the skill test's
 *   documents dry run; "Open preview" opens it in a tab (the iPhone / iPad
 *   fallback, where an embedded PDF shows one page at best).
 *
 * "Editing" picks all documents or one type: a type's edits become a per-type
 * override. Nothing is saved here — Apply hands the template (and any business
 * lines read from an upload) back to the form, whose Save stores the skill.
 *
 * The top bar switches the LAYOUT: Designer (all of the above) | Custom layout
 * (HTML) — `HtmlLayoutEditor`, a Handlebars template that can match any existing
 * document. Switching keeps both; Apply stores `layout` + `html` with the template.
 */

const TYPE_LABEL: Record<DocumentType, MessageKey> = {
  invoice: 'readySkills.form.types.invoice',
  quotation: 'readySkills.form.types.quotation',
  receipt: 'readySkills.form.types.receipt',
  letter: 'readySkills.form.types.letter',
}

/** The skill's accent palette keys → the hex the PDF prints (DocumentAppConfig.Accents). */
export const ACCENT_HEX: Record<string, string> = {
  tangerine: '#E8590C', ink: '#1F2328', teal: '#0F766E', plum: '#7E3A8C',
  ochre: '#B7791F', blue: '#1D4ED8', green: '#15803D', rose: '#BE123C',
}

const BUSINESS_KEYS = ['business_name', 'address', 'registration_no', 'tax_no', 'phone', 'email', 'website'] as const
type BusinessKey = (typeof BUSINESS_KEYS)[number]

const field =
  'rounded-lg border border-line-strong bg-card px-3 py-2 text-[13px] text-text-primary outline-none transition-colors focus:border-brand-400 disabled:opacity-70'
const input = `w-full ${field}`
const numInput = `w-[96px] ${field}`

type Scope = 'all' | DocumentType

export function DocumentDesigner({
  config,
  presets,
  starters = [],
  reference = null,
  rendererMissing = false,
  readOnly = false,
  onApply,
  onClose,
}: {
  config: DocumentsConfig
  /** GET /skills/apps → documents.template_presets (the classic layout alone while it loads). */
  presets?: Record<string, DocTemplate>
  /** GET /skills/apps → documents.html_starters / html_reference / html_renderer (the custom layout). */
  starters?: HtmlStarter[]
  reference?: HtmlReference | null
  rendererMissing?: boolean
  readOnly?: boolean
  onApply: (next: {
    template: DocTemplate
    business: Partial<Record<BusinessKey, string>>
    layout: 'designer' | 'html'
    html: HtmlLayout | null
  }) => void
  onClose: () => void
}) {
  const t = useT()
  const presetOf = useCallback(
    (p: Preset): DocTemplate => completeTemplate(presets?.[p] ?? (p === 'classic' ? CLASSIC : { ...CLASSIC, preset: p })),
    [presets],
  )
  const [tpl, setTpl] = useState<DocTemplate>(() => completeTemplate(config.template ?? presets?.classic ?? CLASSIC))
  const [history, setHistory] = useState<DocTemplate[]>([])
  const [business, setBusiness] = useState<Partial<Record<BusinessKey, string>>>({})
  const [scope, setScope] = useState<Scope>('all')
  const [notice, setNotice] = useState<{ tone: 'ok' | 'error'; text: string } | null>(null)
  // the layout: the designer (above) or the custom HTML layout — switching keeps both
  const [mode, setMode] = useState<'designer' | 'html'>(config.layout === 'html' ? 'html' : 'designer')
  const [html, setHtml] = useState<HtmlLayout>(() => (config.html ? completeLayout(config.html) : EMPTY_LAYOUT))
  const rootRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    rootRef.current?.focus()
  }, [])

  /** A big change (a preset, an upload, an AI edit) — Undo brings back what was there. */
  const replace = (next: DocTemplate) => {
    setHistory((h) => [...h.slice(-19), tpl])
    setTpl(next)
  }
  const undo = () => {
    setHistory((h) => {
      if (!h.length) return h
      setTpl(h[h.length - 1])
      return h.slice(0, -1)
    })
    setNotice(null)
  }

  // ---- edits: the whole template, or one type's override --------------------------------
  const view = useMemo(() => forType(tpl, scope), [tpl, scope])
  const set = <K extends keyof DocTemplate>(section: K, patch: Partial<DocTemplate[K]> | DocTemplate[K]) => {
    setTpl((cur) => {
      const next = clone(cur)
      if (scope === 'all' || !(OVERRIDE_KEYS as readonly string[]).includes(section as string)) {
        const prev = next[section]
        next[section] = (Array.isArray(patch) || typeof prev !== 'object' || prev === null
          ? patch
          : { ...(prev as object), ...(patch as object) }) as DocTemplate[K]
        return next
      }
      const ov = { ...(next.overrides[scope] ?? {}) }
      const prev = ov[section as OverrideKey]
      ov[section as OverrideKey] = Array.isArray(patch)
        ? patch
        : { ...((prev as object | undefined) ?? {}), ...(patch as object) }
      next.overrides = { ...next.overrides, [scope]: ov }
      return next
    })
  }
  const typeHasOverride = scope !== 'all' && !!tpl.overrides[scope] && Object.keys(tpl.overrides[scope]!).length > 0
  const clearType = () => {
    if (scope === 'all') return
    setTpl((cur) => {
      const next = clone(cur)
      delete next.overrides[scope]
      return next
    })
  }

  // ---- the config the preview renders with (business lines from an upload fill blanks) ----
  const previewConfig = useMemo<DocumentsConfig>(
    () => ({ ...config, ...business, template: tpl, layout: 'designer' }),
    [config, business, tpl],
  )
  const previewType: DocumentType = scope === 'all' ? (config.actions[0] ?? 'invoice') : scope

  // ---- upload my design --------------------------------------------------------------------
  const fileRef = useRef<HTMLInputElement>(null)
  const [keepLetterhead, setKeepLetterhead] = useState(false)
  const [busy, setBusy] = useState<null | 'upload' | 'ask' | 'letterhead'>(null)
  const pickDesign = async (file: File | undefined) => {
    if (!file) return
    setBusy('upload')
    setNotice(null)
    try {
      const r = await analyzeDesign(file, { template: tpl, config, keepLetterhead, documentType: previewType })
      replace(completeTemplate(r.template))
      const filled: Partial<Record<BusinessKey, string>> = {}
      for (const k of BUSINESS_KEYS) {
        const v = r.business?.[k]
        if (v && !String(config[k] ?? '').trim()) filled[k] = v
      }
      setBusiness((b) => ({ ...b, ...filled }))
      const parts = [
        r.mode === 'ai' ? t('readySkills.designer.analyzedAi') : t('readySkills.designer.analyzedHeuristic'),
        r.summary,
        Object.keys(filled).length
          ? t('readySkills.designer.filledIn', {
              fields: Object.keys(filled).map((k) => t(`readySkills.designer.business.${k as BusinessKey}`)).join(', '),
            })
          : '',
        r.note,
      ]
      setNotice({ tone: 'ok', text: parts.filter(Boolean).join(' ') })
    } catch (e) {
      setNotice({ tone: 'error', text: e instanceof Error ? e.message : String(e) })
    } finally {
      setBusy(null)
      if (fileRef.current) fileRef.current.value = ''
    }
  }

  // ---- ask AI ---------------------------------------------------------------------------------
  const [ask, setAsk] = useState('')
  const runAsk = async () => {
    const instruction = ask.trim()
    if (!instruction) return
    setBusy('ask')
    setNotice(null)
    try {
      const r = await assistTemplate(tpl, instruction)
      replace(completeTemplate(r.template))
      setNotice({
        tone: 'ok',
        text: [r.summary, r.mode === 'sandbox' ? t('readySkills.designer.askSandbox') : ''].filter(Boolean).join(' '),
      })
      setAsk('')
    } catch (e) {
      setNotice({ tone: 'error', text: e instanceof Error ? e.message : String(e) })
    } finally {
      setBusy(null)
    }
  }

  // ---- the live preview -----------------------------------------------------------------------
  const [preview, setPreview] = useState<{ state: 'idle' | 'loading' | 'ok' | 'error'; url?: string; blob?: string; detail?: string }>({
    state: 'idle',
  })
  const [ios, setIos] = useState(false)
  useEffect(() => setIos(isIos()), [])
  const blobRef = useRef<string | null>(null)
  const seq = useRef(0)
  useEffect(() => {
    const mine = ++seq.current
    if (mode !== 'designer') return
    setPreview((p) => ({ ...p, state: 'loading' }))
    const timer = window.setTimeout(async () => {
      try {
        const r = await testToolApp(previewConfig, previewType)
        if (mine !== seq.current) return
        if (!r.ok || !r.url) {
          setPreview({ state: 'error', detail: r.detail })
          return
        }
        const blob = await authedObjectUrl(r.url)
        if (mine !== seq.current) {
          URL.revokeObjectURL(blob)
          return
        }
        if (blobRef.current) URL.revokeObjectURL(blobRef.current)
        blobRef.current = blob
        setPreview({ state: 'ok', url: r.url, blob })
      } catch (e) {
        if (mine === seq.current) setPreview({ state: 'error', detail: e instanceof Error ? e.message : String(e) })
      }
    }, 700)
    return () => window.clearTimeout(timer)
  }, [previewConfig, previewType, mode])
  useEffect(
    () => () => {
      if (blobRef.current) URL.revokeObjectURL(blobRef.current)
    },
    [],
  )

  // ---- letterhead background ---------------------------------------------------------------
  const lhRef = useRef<HTMLInputElement>(null)
  const pickLetterhead = async (file: File | undefined) => {
    if (!file) return
    setBusy('letterhead')
    setNotice(null)
    try {
      const r = await uploadLetterhead(file)
      setTpl((cur) => ({ ...clone(cur), letterhead: { ...cur.letterhead, file: r.letterhead } }))
    } catch (e) {
      setNotice({ tone: 'error', text: e instanceof Error ? e.message : String(e) })
    } finally {
      setBusy(null)
      if (lhRef.current) lhRef.current.value = ''
    }
  }

  const close = () => onClose()
  // an untouched, never-used custom layout isn't stored (a designer-only config keeps its shape)
  const keepHtml = html.template.trim() || config.html ? html : null
  const htmlBlocked = mode === 'html' && (!html.template.trim() || !!fieldProblem(html.fields))
  const apply = () => {
    onApply({ template: tpl, business, layout: mode, html: keepHtml })
    onClose()
  }

  const accentFallback = ACCENT_HEX[config.accent] ?? ACCENT_HEX.ink
  const typeName = scope === 'all' ? '' : t(TYPE_LABEL[scope])
  const shared = scope !== 'all'

  if (typeof document === 'undefined') return null
  return createPortal(
    <div
      ref={rootRef}
      role="dialog"
      aria-modal="true"
      aria-labelledby="doc-designer-title"
      tabIndex={-1}
      // Esc closes the designer only — the skill wizard's modal underneath must not see it
      // (React events bubble through portals). Clicks may bubble: the modal's scrim only
      // closes on a click on itself, and the Select lists listen on the document.
      onKeyDown={(e) => {
        if (e.key !== 'Escape') return
        e.stopPropagation()
        close()
      }}
      className="fixed inset-0 z-[70] flex flex-col bg-app outline-none"
    >
      {/* ---- top bar ---- */}
      <div className="flex flex-wrap items-center gap-3 border-b border-line bg-surface px-4 py-3">
        <div className="min-w-0 flex-1">
          <div id="doc-designer-title" className="display text-[19px] leading-tight text-text-heading">
            {t('readySkills.designer.title')}
          </div>
          <div className="mt-0.5 text-[12px] text-text-muted">{t('readySkills.designer.subtitle')}</div>
          <div className="mt-2 flex flex-wrap items-center gap-2">
            <div role="radiogroup" aria-label={t('docLayout.mode.label')} className="inline-flex overflow-hidden rounded-lg border border-line-strong">
              {(['designer', 'html'] as const).map((m) => (
                <button
                  key={m}
                  type="button"
                  role="radio"
                  aria-checked={mode === m}
                  disabled={readOnly && mode !== m}
                  onClick={() => setMode(m)}
                  className={`px-3 py-1.5 text-[12px] font-semibold transition-colors ${
                    mode === m ? 'bg-primary text-on-primary' : 'bg-card text-text-mid hover:bg-fill-hover'
                  }`}
                >
                  {t(`docLayout.mode.${m}`)}
                </button>
              ))}
            </div>
            <span className="text-[11.5px] leading-snug text-text-muted">
              {mode === 'html' ? t('docLayout.mode.htmlHint') : t('docLayout.mode.designerHint')}
            </span>
          </div>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          {mode === 'designer' && history.length > 0 && !readOnly && (
            <Button variant="ghost" size="sm" onClick={undo}>
              <Undo2 size={13} aria-hidden /> {t('readySkills.designer.undo')}
            </Button>
          )}
          <Button variant="secondary" size="sm" onClick={close}>
            {t('common.actions.cancel')}
          </Button>
          {!readOnly && (
            <Button size="sm" onClick={apply} disabled={htmlBlocked}>
              {t('common.actions.apply')}
            </Button>
          )}
          <button
            type="button"
            onClick={close}
            aria-label={t('common.actions.close')}
            className="grid h-8 w-8 place-items-center rounded-lg text-text-muted hover:bg-fill-hover hover:text-text-high"
          >
            <X size={15} aria-hidden />
          </button>
        </div>
      </div>

      <div className="min-h-0 flex-1 overflow-y-auto lg:overflow-hidden">
        {mode === 'html' ? (
          <HtmlLayoutEditor
            config={{ ...config, ...business }}
            value={html}
            onChange={setHtml}
            onBusiness={(filled) => setBusiness((b) => ({ ...b, ...filled }))}
            starters={starters}
            reference={reference}
            rendererMissing={rendererMissing}
            readOnly={readOnly}
            initialType={config.actions[0] ?? 'invoice'}
          />
        ) : (
        <div className="flex flex-col lg:h-full lg:flex-row">
          {/* ---- left: start-from + block panels ---- */}
          <div className="flex min-w-0 flex-col gap-4 border-line p-4 lg:w-[440px] lg:shrink-0 lg:overflow-y-auto lg:border-r">
            {!readOnly && (
              <section className="flex flex-col gap-3">
                <div className="eyebrow">{t('readySkills.designer.startFrom')}</div>
                <div className="grid grid-cols-4 gap-2">
                  {PRESETS.map((p) => (
                    <button
                      key={p}
                      type="button"
                      onClick={() => {
                        replace(withPreset(tpl, presetOf(p)))
                        setNotice(null)
                      }}
                      aria-pressed={tpl.preset === p}
                      className={`flex flex-col items-center gap-1 rounded-lg border p-1.5 transition-colors ${
                        tpl.preset === p ? 'border-brand-400 bg-fill-subtle' : 'border-line hover:border-line-strong'
                      }`}
                    >
                      <PresetThumb tpl={presetOf(p)} accent={tpl.colors.accent || accentFallback} />
                      <span className="text-[11.5px] font-semibold text-text-high">{t(`readySkills.designer.presets.${p}`)}</span>
                    </button>
                  ))}
                </div>

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
                    {busy === 'upload' ? t('readySkills.designer.analyzing') : t('readySkills.designer.upload')}
                  </Button>
                  <label className="flex items-center gap-2 text-[12px] text-text-mid">
                    <Toggle on={keepLetterhead} onChange={setKeepLetterhead} label={t('readySkills.designer.keepLetterhead')} />
                    {t('readySkills.designer.keepLetterhead')}
                  </label>
                </div>
                <div className="text-[11px] leading-snug text-text-muted">{t('readySkills.designer.uploadHint')}</div>

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
                    aria-label={t('readySkills.designer.askAi')}
                    placeholder={t('readySkills.designer.askPlaceholder')}
                    className={`${input} min-w-0 flex-1`}
                  />
                  <Button type="submit" variant="accent" size="sm" disabled={!!busy || !ask.trim()}>
                    {busy === 'ask' ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Sparkles size={13} aria-hidden />}{' '}
                    {t('readySkills.designer.askAi')}
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
                        {t('readySkills.designer.undo')}
                      </button>
                    )}
                  </div>
                )}
              </section>
            )}

            {/* ---- what is being edited ---- */}
            <div className="flex flex-col gap-1.5">
              <div className="text-[12px] font-semibold text-text-high">{t('readySkills.designer.editing')}</div>
              <Select
                value={scope}
                size="md"
                ariaLabel={t('readySkills.designer.editing')}
                onChange={(v) => setScope(v as Scope)}
                options={[
                  { value: 'all', label: t('readySkills.designer.editingAll') },
                  ...DOCUMENT_TYPES.map((d) => ({
                    value: d,
                    label: t(TYPE_LABEL[d]),
                    badge: tpl.overrides[d] && Object.keys(tpl.overrides[d]!).length ? t('readySkills.designer.customised') : undefined,
                  })),
                ]}
              />
              {shared && (
                <div className="flex flex-wrap items-center gap-2 text-[11.5px] leading-snug text-text-muted">
                  <span className="min-w-0 flex-1">{t('readySkills.designer.editingTypeNote', { type: typeName })}</span>
                  {typeHasOverride && !readOnly && (
                    <Button variant="ghost" size="sm" onClick={clearType}>
                      {t('readySkills.designer.resetType', { type: typeName })}
                    </Button>
                  )}
                </div>
              )}
            </div>

            <div className="flex min-w-0 flex-col gap-2">
              <LayoutPanel view={view} tpl={tpl} set={set} shared={shared} />
              <BrandPanel view={view} set={set} accentFallback={accentFallback} />
              <HeaderPanel view={view} set={set} accentFallback={accentFallback} />
              <InfoPanel view={view} set={set} />
              <TablePanel view={view} set={set} accentFallback={accentFallback} />
              <TotalsPanel view={view} set={set} />
              <NotesPanel view={view} set={set} />
              <FooterPanel view={view} set={set} />
              <Panel title={t('readySkills.designer.panels.letterhead')}>
                {shared && <SharedNote />}
                <div className="text-[12px] leading-relaxed text-text-mid">{t('readySkills.designer.letterheadIntro')}</div>
                <input
                  ref={lhRef}
                  type="file"
                  accept="application/pdf"
                  className="hidden"
                  onChange={(e) => pickLetterhead(e.target.files?.[0])}
                />
                <div className="flex flex-wrap items-center gap-2">
                  <Button variant="secondary" size="sm" disabled={!!busy || shared || readOnly} onClick={() => lhRef.current?.click()}>
                    {busy === 'letterhead' ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <FileUp size={13} aria-hidden />}{' '}
                    {tpl.letterhead.file ? t('readySkills.designer.letterheadReplace') : t('readySkills.designer.letterheadUpload')}
                  </Button>
                  {tpl.letterhead.file && (
                    <>
                      <Button
                        variant="ghost"
                        size="sm"
                        onClick={() => openAuthed(`/api/aicore/skills/documents/letterhead/${tpl.letterhead.file}`).catch(() => undefined)}
                      >
                        <ExternalLink size={13} aria-hidden /> {t('readySkills.designer.letterheadOpen')}
                      </Button>
                      <Button
                        variant="ghost"
                        size="sm"
                        disabled={shared || readOnly}
                        onClick={() => setTpl((cur) => ({ ...clone(cur), letterhead: { ...cur.letterhead, file: '' } }))}
                      >
                        <X size={13} aria-hidden /> {t('readySkills.designer.letterheadRemove')}
                      </Button>
                    </>
                  )}
                </div>
                {tpl.letterhead.file && (
                  <Labeled label={t('readySkills.designer.letterheadPages')}>
                    <Select
                      value={tpl.letterhead.pages}
                      size="md"
                      disabled={shared}
                      ariaLabel={t('readySkills.designer.letterheadPages')}
                      onChange={(v) => set('letterhead', { pages: v as 'all' | 'first' })}
                      options={(['all', 'first'] as const).map((p) => ({ value: p, label: t(`readySkills.designer.letterheadPagesOpts.${p}`) }))}
                    />
                  </Labeled>
                )}
              </Panel>
            </div>
          </div>

          {/* ---- right: the live preview ---- */}
          <div className="flex min-h-[70vh] min-w-0 flex-1 flex-col gap-2 p-4 lg:min-h-0">
            <div className="flex flex-wrap items-center gap-2">
              <div className="eyebrow min-w-0 flex-1">
                {t('readySkills.designer.previewOf', { type: t(TYPE_LABEL[previewType]) })}
              </div>
              {preview.state === 'loading' && (
                <span className="flex items-center gap-1.5 text-[12px] text-text-muted">
                  <Loader2 size={12} className="animate-spin" aria-hidden /> {t('readySkills.designer.previewing')}
                </span>
              )}
              {preview.url && (
                <Button
                  variant={ios ? 'primary' : 'ghost'}
                  size="sm"
                  onClick={() => openAuthed(preview.url!).catch(() => undefined)}
                >
                  <ExternalLink size={13} aria-hidden /> {t('readySkills.designer.openPreview')}
                </Button>
              )}
            </div>
            {ios && <div className="text-[11.5px] text-text-muted">{t('readySkills.designer.previewIosHint')}</div>}
            {preview.state === 'error' && (
              <div role="alert" className="flex items-start gap-2 rounded-lg border border-red/40 bg-red/10 px-3 py-2 text-[12px] text-[color:var(--red-ink)]">
                <AlertCircle size={13} className="mt-0.5 shrink-0" aria-hidden />
                <span>{preview.detail || t('readySkills.designer.previewFailed')}</span>
              </div>
            )}
            <div className="relative min-h-[60vh] flex-1 overflow-hidden rounded-xl border border-line bg-well">
              {preview.blob ? (
                <iframe
                  key={preview.blob}
                  src={`${preview.blob}#view=FitH&toolbar=0`}
                  title={t('readySkills.designer.previewOf', { type: t(TYPE_LABEL[previewType]) })}
                  className="absolute inset-0 h-full w-full bg-card"
                />
              ) : (
                <div className="absolute inset-0 grid place-items-center text-[12.5px] text-text-muted">
                  {preview.state === 'error' ? t('readySkills.designer.previewFailed') : t('readySkills.designer.previewing')}
                </div>
              )}
            </div>
          </div>
        </div>
        )}
      </div>
    </div>,
    document.body,
  )
}

// =========================================================================================
// panels
// =========================================================================================

type Setter = <K extends keyof DocTemplate>(section: K, patch: Partial<DocTemplate[K]> | DocTemplate[K]) => void

function LayoutPanel({ view, tpl, set, shared }: { view: DocTemplate; tpl: DocTemplate; set: Setter; shared: boolean }) {
  const t = useT()
  const order = view.sections
  const move = (i: number, by: number) => {
    const next = [...order]
    const j = i + by
    if (j < 0 || j >= next.length) return
    ;[next[i], next[j]] = [next[j], next[i]]
    set('sections', next)
  }
  const m = tpl.page.margins_mm
  const marginsChanged = JSON.stringify(m) !== JSON.stringify(CLASSIC.page.margins_mm)
  return (
    <Panel title={t('readySkills.designer.panels.layout')} defaultOpen>
      <div className="grid grid-cols-2 gap-3">
        <Labeled label={t('readySkills.designer.pageSize')}>
          <Select
            value={tpl.page.size}
            size="md"
            disabled={shared}
            ariaLabel={t('readySkills.designer.pageSize')}
            onChange={(v) => set('page', { size: v as 'A4' | 'Letter' })}
            options={(['A4', 'Letter'] as const).map((s) => ({ value: s, label: t(`readySkills.designer.pageSizes.${s}`) }))}
          />
        </Labeled>
        <Labeled label={t('readySkills.designer.headingFont')}>
          <Select
            value={view.fonts.heading}
            size="md"
            ariaLabel={t('readySkills.designer.headingFont')}
            onChange={(v) => set('fonts', { heading: v as 'sans' | 'serif' })}
            options={(['sans', 'serif'] as const).map((f) => ({ value: f, label: t(`readySkills.designer.fonts.${f}`) }))}
          />
        </Labeled>
      </div>
      <Labeled label={t('readySkills.designer.sectionOrder')} hint={t('readySkills.designer.sectionOrderHint')}>
        <ol className="flex flex-col gap-1">
          {order.map((s: SectionId, i) => (
            <li key={s} className="flex items-center gap-2 rounded-lg border border-line bg-card px-2.5 py-1.5 text-[12.5px] text-text-high">
              <span className="w-4 text-right text-[11px] text-text-faint">{i + 1}</span>
              <span className="min-w-0 flex-1">{t(`readySkills.designer.sections.${s}`)}</span>
              <button
                type="button"
                onClick={() => move(i, -1)}
                disabled={i === 0}
                aria-label={t('readySkills.designer.moveUp', { section: t(`readySkills.designer.sections.${s}`) })}
                className="grid h-6 w-6 place-items-center rounded text-text-muted hover:bg-fill-hover disabled:opacity-30"
              >
                <ArrowUp size={12} aria-hidden />
              </button>
              <button
                type="button"
                onClick={() => move(i, 1)}
                disabled={i === order.length - 1}
                aria-label={t('readySkills.designer.moveDown', { section: t(`readySkills.designer.sections.${s}`) })}
                className="grid h-6 w-6 place-items-center rounded text-text-muted hover:bg-fill-hover disabled:opacity-30"
              >
                <ArrowDown size={12} aria-hidden />
              </button>
            </li>
          ))}
        </ol>
      </Labeled>
      <Advanced count={(marginsChanged ? 1 : 0) + (view.fonts.body !== 'sans' ? 1 : 0) + (view.fonts.size !== 9.5 ? 1 : 0)}>
        <div className="flex flex-col gap-3">
          <div className="grid grid-cols-2 gap-3">
            <Labeled label={t('readySkills.designer.bodyFont')}>
              <Select
                value={view.fonts.body}
                size="md"
                ariaLabel={t('readySkills.designer.bodyFont')}
                onChange={(v) => set('fonts', { body: v as 'sans' | 'serif' })}
                options={(['sans', 'serif'] as const).map((f) => ({ value: f, label: t(`readySkills.designer.fonts.${f}`) }))}
              />
            </Labeled>
            <Labeled label={t('readySkills.designer.textSize')}>
              <NumberField value={view.fonts.size} min={7} max={13} step={0.5} onChange={(size) => set('fonts', { size })} />
            </Labeled>
          </div>
          <Labeled label={t('readySkills.designer.margins')} hint={shared ? t('readySkills.designer.sharedOnly') : undefined}>
            <div className="grid grid-cols-4 gap-2">
              {(['top', 'right', 'bottom', 'left'] as const).map((k) => (
                <label key={k} className="flex flex-col gap-1 text-[11px] text-text-muted">
                  {t(`readySkills.designer.marginSides.${k}`)}
                  <NumberField
                    value={m[k]}
                    min={k === 'bottom' ? 10 : 5}
                    max={60}
                    disabled={shared}
                    onChange={(v) => set('page', { margins_mm: { ...m, [k]: v } })}
                  />
                </label>
              ))}
            </div>
          </Labeled>
        </div>
      </Advanced>
    </Panel>
  )
}

function BrandPanel({ view, set, accentFallback }: { view: DocTemplate; set: Setter; accentFallback: string }) {
  const t = useT()
  const c = view.colors
  return (
    <Panel title={t('readySkills.designer.panels.brand')}>
      <ColorField
        label={t('readySkills.designer.accent')}
        hint={t('readySkills.designer.accentHint')}
        value={c.accent}
        fallback={accentFallback}
        allowEmpty
        onChange={(accent) => set('colors', { accent })}
      />
      <Advanced count={[c.text !== CLASSIC.colors.text, c.muted !== CLASSIC.colors.muted, c.line !== CLASSIC.colors.line].filter(Boolean).length}>
        <div className="flex flex-col gap-3">
          <ColorField label={t('readySkills.designer.textColor')} value={c.text} onChange={(text) => set('colors', { text })} />
          <ColorField label={t('readySkills.designer.mutedColor')} value={c.muted} onChange={(muted) => set('colors', { muted })} />
          <ColorField label={t('readySkills.designer.lineColor')} value={c.line} onChange={(line) => set('colors', { line })} />
        </div>
      </Advanced>
    </Panel>
  )
}

function HeaderPanel({ view, set, accentFallback }: { view: DocTemplate; set: Setter; accentFallback: string }) {
  const t = useT()
  const h = view.header
  const ti = view.title
  const advancedCount = [
    h.logo_size_mm !== 16, !h.show_business_name, h.business_name_size !== 14, !h.show_address, !h.show_ids,
    !h.show_contacts, h.extra_lines.length > 0, h.rule !== 'accent', !ti.show, ti.size !== 20, !!ti.color,
  ].filter(Boolean).length
  return (
    <Panel title={t('readySkills.designer.panels.header')}>
      <div className="grid grid-cols-2 gap-3">
        <Labeled label={t('readySkills.designer.headerLayout')}>
          <Select
            value={h.layout}
            size="md"
            ariaLabel={t('readySkills.designer.headerLayout')}
            onChange={(v) => set('header', { layout: v as DocTemplate['header']['layout'] })}
            options={(['split', 'stacked', 'centered'] as const).map((l) => ({ value: l, label: t(`readySkills.designer.headerLayouts.${l}`) }))}
          />
        </Labeled>
        <Labeled label={t('readySkills.designer.headerStyle')}>
          <Select
            value={h.style}
            size="md"
            ariaLabel={t('readySkills.designer.headerStyle')}
            onChange={(v) => set('header', { style: v as 'plain' | 'band' })}
            options={(['plain', 'band'] as const).map((s) => ({ value: s, label: t(`readySkills.designer.headerStyles.${s}`) }))}
          />
        </Labeled>
      </div>
      <Labeled label={t('readySkills.designer.logoPosition')}>
        <Sides value={h.logo_position} onChange={(logo_position) => set('header', { logo_position })} />
      </Labeled>
      <Labeled label={t('readySkills.designer.titleAlign')}>
        <Sides value={ti.align} onChange={(align) => set('title', { align })} />
      </Labeled>
      {h.style === 'band' && (
        <ColorField
          label={t('readySkills.designer.bandColor')}
          value={h.band_color}
          fallback={view.colors.accent || accentFallback}
          allowEmpty
          onChange={(band_color) => set('header', { band_color })}
        />
      )}
      <Labeled label={t('readySkills.designer.tagline')}>
        <input
          value={h.tagline}
          maxLength={160}
          placeholder={t('readySkills.designer.taglinePlaceholder')}
          onChange={(e) => set('header', { tagline: e.target.value })}
          className={input}
        />
      </Labeled>
      <Row label={t('readySkills.designer.titleUppercase')}>
        <Toggle on={ti.uppercase} label={t('readySkills.designer.titleUppercase')} onChange={(uppercase) => set('title', { uppercase })} />
      </Row>
      <Advanced count={advancedCount}>
        <div className="flex flex-col gap-3">
          <div className="grid grid-cols-2 gap-3">
            <Labeled label={t('readySkills.designer.logoSize')}>
              <NumberField value={h.logo_size_mm} min={6} max={50} onChange={(logo_size_mm) => set('header', { logo_size_mm })} />
            </Labeled>
            <Labeled label={t('readySkills.designer.businessNameSize')}>
              <NumberField value={h.business_name_size} min={8} max={32} onChange={(business_name_size) => set('header', { business_name_size })} />
            </Labeled>
          </div>
          <Row label={t('readySkills.designer.showBusinessName')}>
            <Toggle on={h.show_business_name} label={t('readySkills.designer.showBusinessName')} onChange={(show_business_name) => set('header', { show_business_name })} />
          </Row>
          <Row label={t('readySkills.designer.showAddress')}>
            <Toggle on={h.show_address} label={t('readySkills.designer.showAddress')} onChange={(show_address) => set('header', { show_address })} />
          </Row>
          <Row label={t('readySkills.designer.showIds')}>
            <Toggle on={h.show_ids} label={t('readySkills.designer.showIds')} onChange={(show_ids) => set('header', { show_ids })} />
          </Row>
          <Row label={t('readySkills.designer.showContacts')}>
            <Toggle on={h.show_contacts} label={t('readySkills.designer.showContacts')} onChange={(show_contacts) => set('header', { show_contacts })} />
          </Row>
          <Labeled label={t('readySkills.designer.extraLines')} hint={t('readySkills.designer.extraLinesHint')}>
            <LinesField value={h.extra_lines} max={6} onChange={(extra_lines) => set('header', { extra_lines })} />
          </Labeled>
          <Labeled label={t('readySkills.designer.rule')}>
            <Select
              value={h.rule}
              size="md"
              ariaLabel={t('readySkills.designer.rule')}
              onChange={(v) => set('header', { rule: v as DocTemplate['header']['rule'] })}
              options={(['accent', 'line', 'none'] as const).map((r) => ({ value: r, label: t(`readySkills.designer.rules.${r}`) }))}
            />
          </Labeled>
          {h.style === 'band' && (
            <ColorField label={t('readySkills.designer.bandTextColor')} value={h.band_text_color} onChange={(band_text_color) => set('header', { band_text_color })} />
          )}
          <Row label={t('readySkills.designer.showTitle')}>
            <Toggle on={ti.show} label={t('readySkills.designer.showTitle')} onChange={(show) => set('title', { show })} />
          </Row>
          <Labeled label={t('readySkills.designer.titleSize')}>
            <NumberField value={ti.size} min={10} max={40} onChange={(size) => set('title', { size })} />
          </Labeled>
          <ColorField
            label={t('readySkills.designer.titleColor')}
            value={ti.color}
            fallback={view.colors.accent || accentFallback}
            allowEmpty
            onChange={(color) => set('title', { color })}
          />
        </div>
      </Advanced>
    </Panel>
  )
}

function InfoPanel({ view, set }: { view: DocTemplate; set: Setter }) {
  const t = useT()
  return (
    <Panel title={t('readySkills.designer.panels.info')}>
      <Labeled label={t('readySkills.designer.arrangement')}>
        <Select
          value={view.info.arrangement}
          size="md"
          ariaLabel={t('readySkills.designer.arrangement')}
          onChange={(v) => set('info', { arrangement: v as DocTemplate['info']['arrangement'] })}
          options={(['meta_in_header', 'side_by_side', 'stacked'] as const).map((a) => ({
            value: a,
            label: t(`readySkills.designer.arrangements.${a}`),
          }))}
        />
      </Labeled>
      <Advanced count={view.info.bill_to_label ? 1 : 0}>
        <Labeled label={t('readySkills.designer.billToLabel')} hint={t('readySkills.designer.billToHint')}>
          <input
            value={view.info.bill_to_label}
            maxLength={40}
            onChange={(e) => set('info', { bill_to_label: e.target.value })}
            className={input}
          />
        </Labeled>
      </Advanced>
    </Panel>
  )
}

function TablePanel({ view, set, accentFallback }: { view: DocTemplate; set: Setter; accentFallback: string }) {
  const t = useT()
  const tb = view.table
  const toggle = (col: Column, on: boolean) =>
    set('table', { columns: COLUMNS.filter((c) => (c === col ? on : tb.columns.includes(c))) })
  const labelCount = Object.values(tb.labels).filter(Boolean).length
  return (
    <Panel title={t('readySkills.designer.panels.table')}>
      <Labeled label={t('readySkills.designer.columns')} hint={t('readySkills.designer.discountHint')}>
        <div className="grid grid-cols-2 gap-1.5">
          {COLUMNS.map((col) => {
            const locked = col === 'description' || col === 'amount'
            return (
              <div key={col} className="flex items-center justify-between gap-2 rounded-lg border border-line px-2.5 py-1.5">
                <span className="text-[12px] text-text-high">{t(`readySkills.designer.columnNames.${col}`)}</span>
                <Toggle
                  on={tb.columns.includes(col)}
                  locked={locked}
                  label={t(`readySkills.designer.columnNames.${col}`)}
                  onChange={(on) => toggle(col, on)}
                />
              </div>
            )
          })}
        </div>
      </Labeled>
      <div className="grid grid-cols-2 gap-3">
        <Labeled label={t('readySkills.designer.tableHeaderStyle')}>
          <Select
            value={tb.header_style}
            size="md"
            ariaLabel={t('readySkills.designer.tableHeaderStyle')}
            onChange={(v) => set('table', { header_style: v as DocTemplate['table']['header_style'] })}
            options={(['filled', 'underline', 'plain'] as const).map((s) => ({ value: s, label: t(`readySkills.designer.tableHeaderStyles.${s}`) }))}
          />
        </Labeled>
        <Labeled label={t('readySkills.designer.border')}>
          <Select
            value={tb.border}
            size="md"
            ariaLabel={t('readySkills.designer.border')}
            onChange={(v) => set('table', { border: v as DocTemplate['table']['border'] })}
            options={(['lines', 'grid', 'outer', 'none'] as const).map((b) => ({ value: b, label: t(`readySkills.designer.borders.${b}`) }))}
          />
        </Labeled>
      </div>
      <Row label={t('readySkills.designer.zebra')}>
        <Toggle on={tb.zebra} label={t('readySkills.designer.zebra')} onChange={(zebra) => set('table', { zebra })} />
      </Row>
      <Advanced count={labelCount + (tb.header_color ? 1 : 0) + (tb.header_text_color !== '#FFFFFF' ? 1 : 0)}>
        <div className="flex flex-col gap-3">
          {COLUMNS.filter((c) => tb.columns.includes(c)).map((col) => (
            <Labeled key={col} label={t('readySkills.designer.columnLabel', { column: t(`readySkills.designer.columnNames.${col}`) })}>
              <input
                value={tb.labels[col] ?? ''}
                maxLength={40}
                placeholder={t(`readySkills.designer.columnNames.${col}`)}
                onChange={(e) => {
                  const labels = { ...tb.labels }
                  if (e.target.value) labels[col] = e.target.value
                  else delete labels[col]
                  set('table', { labels })
                }}
                className={input}
              />
            </Labeled>
          ))}
          <ColorField
            label={t('readySkills.designer.headerColor')}
            value={tb.header_color}
            fallback={view.colors.accent || accentFallback}
            allowEmpty
            onChange={(header_color) => set('table', { header_color })}
          />
          <ColorField label={t('readySkills.designer.headerTextColor')} value={tb.header_text_color} onChange={(header_text_color) => set('table', { header_text_color })} />
          <ColorField label={t('readySkills.designer.zebraColor')} value={tb.zebra_color} onChange={(zebra_color) => set('table', { zebra_color })} />
        </div>
      </Advanced>
    </Panel>
  )
}

function TotalsPanel({ view, set }: { view: DocTemplate; set: Setter }) {
  const t = useT()
  return (
    <Panel title={t('readySkills.designer.panels.totals')}>
      <div className="grid grid-cols-2 gap-3">
        <Labeled label={t('readySkills.designer.totalsPosition')}>
          <Select
            value={view.totals.position}
            size="md"
            ariaLabel={t('readySkills.designer.totalsPosition')}
            onChange={(v) => set('totals', { position: v as DocTemplate['totals']['position'] })}
            options={(['right', 'left', 'full'] as const).map((p) => ({ value: p, label: t(`readySkills.designer.totalsPositions.${p}`) }))}
          />
        </Labeled>
        <Labeled label={t('readySkills.designer.totalsStyle')}>
          <Select
            value={view.totals.style}
            size="md"
            ariaLabel={t('readySkills.designer.totalsStyle')}
            onChange={(v) => set('totals', { style: v as DocTemplate['totals']['style'] })}
            options={(['lines', 'boxed', 'band'] as const).map((s) => ({ value: s, label: t(`readySkills.designer.totalsStyles.${s}`) }))}
          />
        </Labeled>
      </div>
    </Panel>
  )
}

function NotesPanel({ view, set }: { view: DocTemplate; set: Setter }) {
  const t = useT()
  const n = view.notes
  return (
    <Panel title={t('readySkills.designer.panels.notes')}>
      <Labeled label={t('readySkills.designer.terms')}>
        <textarea
          rows={2}
          value={n.terms}
          maxLength={1000}
          placeholder={t('readySkills.designer.termsPlaceholder')}
          onChange={(e) => set('notes', { terms: e.target.value })}
          className={`${input} resize-y`}
        />
      </Labeled>
      <Labeled label={t('readySkills.designer.bankDetails')}>
        <textarea
          rows={2}
          value={n.bank_details}
          maxLength={1000}
          placeholder={t('readySkills.designer.bankPlaceholder')}
          onChange={(e) => set('notes', { bank_details: e.target.value })}
          className={`${input} resize-y`}
        />
      </Labeled>
      <Row label={t('readySkills.designer.showPaymentInstructions')}>
        <Toggle
          on={n.show_payment_instructions}
          label={t('readySkills.designer.showPaymentInstructions')}
          onChange={(show_payment_instructions) => set('notes', { show_payment_instructions })}
        />
      </Row>
    </Panel>
  )
}

function FooterPanel({ view, set }: { view: DocTemplate; set: Setter }) {
  const t = useT()
  const f = view.footer
  return (
    <Panel title={t('readySkills.designer.panels.footer')}>
      <Labeled label={t('readySkills.designer.footerText')} hint={t('readySkills.designer.footerTextHint')}>
        <input value={f.text} maxLength={300} onChange={(e) => set('footer', { text: e.target.value })} className={input} />
      </Labeled>
      <Row label={t('readySkills.designer.pageNumbers')}>
        <Toggle on={f.page_numbers} label={t('readySkills.designer.pageNumbers')} onChange={(page_numbers) => set('footer', { page_numbers })} />
      </Row>
      <Advanced count={(f.align !== 'center' ? 1 : 0) + (f.show_number ? 0 : 1)}>
        <div className="flex flex-col gap-3">
          <Labeled label={t('readySkills.designer.footerAlign')}>
            <Sides value={f.align} onChange={(align) => set('footer', { align })} />
          </Labeled>
          <Row label={t('readySkills.designer.showNumber')}>
            <Toggle on={f.show_number} label={t('readySkills.designer.showNumber')} onChange={(show_number) => set('footer', { show_number })} />
          </Row>
        </div>
      </Advanced>
    </Panel>
  )
}

// =========================================================================================
// bits
// =========================================================================================

function SharedNote() {
  const t = useT()
  return <div className="text-[11.5px] text-text-muted">{t('readySkills.designer.sharedOnly')}</div>
}

function Panel({ title, children, defaultOpen = false }: { title: string; children: ReactNode; defaultOpen?: boolean }) {
  const [open, setOpen] = useState(defaultOpen)
  return (
    <section className="rounded-xl border border-line bg-card">
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        aria-expanded={open}
        className="flex w-full items-center gap-2 px-3.5 py-2.5 text-left"
      >
        <span className="min-w-0 flex-1 text-[13px] font-semibold text-text-heading">{title}</span>
        <ChevronDown size={14} className={`text-text-muted transition-transform ${open ? 'rotate-180' : ''}`} aria-hidden />
      </button>
      {open && <div className="flex flex-col gap-3 border-t border-line px-3.5 py-3">{children}</div>}
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

function Row({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex items-center justify-between gap-3">
      <span className="text-[12.5px] text-text-high">{label}</span>
      {children}
    </div>
  )
}

function Sides<S extends 'left' | 'center' | 'right'>({ value, onChange }: { value: S; onChange: (v: S) => void }) {
  const t = useT()
  return (
    <div role="radiogroup" className="inline-flex overflow-hidden rounded-lg border border-line-strong">
      {(['left', 'center', 'right'] as S[]).map((s) => (
        <button
          key={s}
          type="button"
          role="radio"
          aria-checked={value === s}
          onClick={() => onChange(s)}
          className={`px-3 py-1.5 text-[12px] font-semibold transition-colors ${
            value === s ? 'bg-primary text-on-primary' : 'bg-card text-text-mid hover:bg-fill-hover'
          }`}
        >
          {t(`readySkills.designer.sides.${s}`)}
        </button>
      ))}
    </div>
  )
}

function NumberField({
  value,
  min,
  max,
  step = 1,
  disabled,
  onChange,
}: {
  value: number
  min: number
  max: number
  step?: number
  disabled?: boolean
  onChange: (v: number) => void
}) {
  const [text, setText] = useState(String(value))
  useEffect(() => setText(String(value)), [value])
  return (
    <input
      type="number"
      inputMode="decimal"
      min={min}
      max={max}
      step={step}
      value={text}
      disabled={disabled}
      onChange={(e) => {
        setText(e.target.value)
        const n = Number(e.target.value)
        // only a value the server accepts reaches the template (the preview stays valid while typing)
        if (e.target.value !== '' && Number.isFinite(n) && n >= min && n <= max) onChange(Math.round(n * 10) / 10)
      }}
      onBlur={() => setText(String(value))}
      className={numInput}
    />
  )
}

/** A hex colour: the native picker + the text; `allowEmpty` = "" means "the accent". */
function ColorField({
  label,
  hint,
  value,
  fallback,
  allowEmpty,
  onChange,
}: {
  label: string
  hint?: string
  value: string
  fallback?: string
  allowEmpty?: boolean
  onChange: (hex: string) => void
}) {
  const t = useT()
  const [text, setText] = useState(value)
  useEffect(() => setText(value), [value])
  const shown = value || fallback || '#000000'
  const bad = text.trim() !== '' && !normalizeHex(text)
  return (
    <Labeled label={label} hint={bad ? t('readySkills.designer.hexInvalid') : hint}>
      <div className="flex flex-wrap items-center gap-2">
        <input
          type="color"
          value={shown.toLowerCase()}
          aria-label={label}
          onChange={(e) => onChange(normalizeHex(e.target.value) ?? value)}
          className="h-8 w-10 cursor-pointer rounded border border-line-strong bg-card p-0.5"
        />
        <input
          value={text}
          placeholder={allowEmpty ? fallback : '#000000'}
          aria-invalid={bad}
          onChange={(e) => {
            setText(e.target.value)
            const hex = normalizeHex(e.target.value)
            if (hex) onChange(hex)
            else if (allowEmpty && e.target.value.trim() === '') onChange('')
          }}
          className={`${numInput} w-[110px] font-mono`}
        />
        {allowEmpty && value && (
          <button type="button" onClick={() => onChange('')} className="text-[11.5px] font-semibold text-text-mid underline">
            {t('readySkills.designer.useAccent')}
          </button>
        )}
      </div>
    </Labeled>
  )
}

function LinesField({ value, max, onChange }: { value: string[]; max: number; onChange: (v: string[]) => void }) {
  const [text, setText] = useState(value.join('\n'))
  useEffect(() => setText(value.join('\n')), [value])
  return (
    <textarea
      rows={3}
      value={text}
      onChange={(e) => {
        setText(e.target.value)
        onChange(
          e.target.value
            .split('\n')
            .map((l) => l.trim())
            .filter(Boolean)
            .slice(0, max)
            .map((l) => l.slice(0, 200)),
        )
      }}
      className={`${input} resize-y`}
    />
  )
}

/**
 * A schematic page of a preset (the gallery thumbnail): header, title, table
 * header, rows and totals in the preset's own colours. These are DOCUMENT
 * colours from the template data, not UI theme colours.
 */
/** The sheet a PDF prints on — a document colour, the same in every UI theme. */
const PAPER = '#FFFFFF'

function PresetThumb({ tpl, accent }: { tpl: DocTemplate; accent: string }) {
  const a = tpl.colors.accent || accent
  const band = tpl.header.style === 'band'
  const headerFill = tpl.table.header_style === 'filled' ? tpl.table.header_color || a : 'transparent'
  const titleColor = tpl.title.color || (band ? tpl.header.band_text_color : a)
  const justify = tpl.title.align === 'left' ? 'flex-start' : tpl.title.align === 'center' ? 'center' : 'flex-end'
  return (
    <div aria-hidden className="relative aspect-[210/297] w-full overflow-hidden rounded border border-line p-[8%]" style={{ background: PAPER }}>
      <div
        className="flex items-start justify-between gap-1 rounded-[2px]"
        style={{ background: band ? tpl.header.band_color || a : 'transparent', padding: band ? '6% 6%' : 0 }}
      >
        <div className="flex flex-col gap-[2px]">
          <div className="h-[3px] w-[18px] rounded-sm" style={{ background: band ? tpl.header.band_text_color : tpl.colors.text }} />
          <div className="h-[2px] w-[14px] rounded-sm" style={{ background: band ? tpl.header.band_text_color : tpl.colors.muted, opacity: 0.7 }} />
        </div>
        <div className="flex flex-1" style={{ justifyContent: justify }}>
          <div className="h-[4px] w-[14px] rounded-sm" style={{ background: titleColor }} />
        </div>
      </div>
      {tpl.header.rule !== 'none' && (
        <div className="mt-[6%] h-px w-full" style={{ background: tpl.header.rule === 'accent' ? a : tpl.colors.line }} />
      )}
      <div className="mt-[8%] flex flex-col">
        <div
          className="h-[4px] w-full"
          style={{
            background: headerFill,
            borderBottom: tpl.table.header_style === 'filled' ? undefined : `1px solid ${tpl.table.header_color || a}`,
          }}
        />
        {[0, 1, 2, 3].map((i) => (
          <div
            key={i}
            className="h-[4px] w-full"
            style={{
              background: tpl.table.zebra && i % 2 === 1 ? tpl.table.zebra_color : 'transparent',
              borderBottom: tpl.table.border === 'lines' || tpl.table.border === 'grid' ? `1px solid ${tpl.colors.line}` : undefined,
            }}
          />
        ))}
      </div>
      <div className="mt-[6%] flex justify-end">
        <div
          className="h-[5px] w-[40%] rounded-[1px]"
          style={{
            background: tpl.totals.style === 'band' ? a : tpl.totals.style === 'boxed' ? tpl.table.zebra_color : 'transparent',
            borderTop: tpl.totals.style === 'band' ? undefined : `1px solid ${a}`,
          }}
        />
      </div>
    </div>
  )
}
