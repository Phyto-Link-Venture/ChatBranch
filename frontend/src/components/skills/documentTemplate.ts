import { API } from '@/lib/apiBase'
import { authHeaders, expireSessionOn401 } from '@/lib/auth'
import type { DocumentType } from './readySkills'
import { aicore } from './skillsApi'

/**
 * The Documents skill's PDF template (`config.template`) — the client side of
 * `ChatBranch.Data/Documents/DocumentTemplate.cs`. The server normalizes and
 * validates it (unknown keys refused) and every key below is in its canonical
 * form; `CLASSIC` mirrors the server's classic preset (= the layout a config
 * with NO template prints). The four presets come from `GET /skills/apps`
 * (`template_presets`) so they never drift from the renderer.
 */

export const PRESETS = ['classic', 'modern', 'minimal', 'bold'] as const
export type Preset = (typeof PRESETS)[number]
export type Face = 'sans' | 'serif'
export type Side = 'left' | 'center' | 'right'
export const COLUMNS = ['index', 'description', 'qty', 'unit_price', 'discount', 'amount'] as const
export type Column = (typeof COLUMNS)[number]
export const SECTION_IDS = ['info', 'items', 'totals', 'notes', 'payment', 'terms', 'bank_details'] as const
export type SectionId = (typeof SECTION_IDS)[number]
/** What a per-type override may change (page, preset and letterhead belong to the whole template). */
export const OVERRIDE_KEYS = ['fonts', 'colors', 'header', 'title', 'info', 'table', 'totals', 'notes', 'footer', 'sections'] as const
export type OverrideKey = (typeof OVERRIDE_KEYS)[number]

export interface DocTemplate {
  version: 1
  preset: Preset
  page: { size: 'A4' | 'Letter'; margins_mm: { top: number; right: number; bottom: number; left: number } }
  fonts: { body: Face; heading: Face; size: number }
  colors: { accent: string; text: string; muted: string; line: string }
  header: {
    layout: 'split' | 'stacked' | 'centered'
    style: 'plain' | 'band'
    band_color: string
    band_text_color: string
    logo_position: Side
    logo_size_mm: number
    show_business_name: boolean
    business_name_size: number
    show_address: boolean
    show_ids: boolean
    show_contacts: boolean
    extra_lines: string[]
    tagline: string
    rule: 'accent' | 'line' | 'none'
  }
  title: { show: boolean; align: Side; size: number; uppercase: boolean; color: string }
  info: { arrangement: 'meta_in_header' | 'side_by_side' | 'stacked'; bill_to_label: string }
  table: {
    columns: Column[]
    labels: Partial<Record<Column, string>>
    header_style: 'filled' | 'underline' | 'plain'
    header_color: string
    header_text_color: string
    zebra: boolean
    zebra_color: string
    border: 'lines' | 'grid' | 'outer' | 'none'
  }
  totals: { position: 'right' | 'left' | 'full'; style: 'lines' | 'boxed' | 'band' }
  notes: { terms: string; bank_details: string; show_payment_instructions: boolean }
  footer: { text: string; align: Side; page_numbers: boolean; show_number: boolean }
  sections: SectionId[]
  letterhead: { file: string; pages: 'all' | 'first' }
  overrides: Partial<Record<DocumentType, Partial<Record<OverrideKey, unknown>>>>
}

export const CLASSIC: DocTemplate = {
  version: 1,
  preset: 'classic',
  page: { size: 'A4', margins_mm: { top: 16, right: 18, bottom: 20, left: 18 } },
  fonts: { body: 'sans', heading: 'sans', size: 9.5 },
  colors: { accent: '', text: '#1F2328', muted: '#6B7280', line: '#E5E7EB' },
  header: {
    layout: 'split', style: 'plain', band_color: '', band_text_color: '#FFFFFF', logo_position: 'left', logo_size_mm: 16,
    show_business_name: true, business_name_size: 14, show_address: true, show_ids: true, show_contacts: true,
    extra_lines: [], tagline: '', rule: 'accent',
  },
  title: { show: true, align: 'right', size: 20, uppercase: true, color: '' },
  info: { arrangement: 'meta_in_header', bill_to_label: '' },
  table: {
    columns: [...COLUMNS], labels: {}, header_style: 'filled', header_color: '', header_text_color: '#FFFFFF',
    zebra: false, zebra_color: '#F5F6F8', border: 'lines',
  },
  totals: { position: 'right', style: 'lines' },
  notes: { terms: '', bank_details: '', show_payment_instructions: true },
  footer: { text: '', align: 'center', page_numbers: true, show_number: true },
  sections: [...SECTION_IDS],
  letterhead: { file: '', pages: 'all' },
  overrides: {},
}

const isObj = (v: unknown): v is Record<string, unknown> => !!v && typeof v === 'object' && !Array.isArray(v)

/** Deep merge for plain objects (arrays and scalars replace). */
export function deepMerge<T>(base: T, over: unknown): T {
  if (!isObj(base) || !isObj(over)) return (over === undefined ? base : (over as T))
  const out: Record<string, unknown> = { ...base }
  for (const [k, v] of Object.entries(over)) {
    if (v === undefined) continue
    out[k] = isObj(v) && isObj(out[k]) && k !== 'labels' ? deepMerge(out[k], v) : v
  }
  return out as T
}

export const clone = <T,>(v: T): T => JSON.parse(JSON.stringify(v)) as T

/** A stored / proposed template → a complete one (classic for anything missing). */
export function completeTemplate(raw: unknown): DocTemplate {
  return deepMerge(clone(CLASSIC), isObj(raw) ? raw : {})
}

/** The template a document type prints with (its override merged in). */
export function forType(t: DocTemplate, type: DocumentType | 'all'): DocTemplate {
  if (type === 'all') return t
  const ov = t.overrides?.[type]
  return ov ? deepMerge(clone(t), ov) : t
}

/** A preset's look with this template's WORDS kept — the server's `WithPreset`. */
export function withPreset(t: DocTemplate, preset: DocTemplate): DocTemplate {
  const next = clone(preset)
  next.page = clone(t.page)
  next.header.extra_lines = [...t.header.extra_lines]
  next.header.tagline = t.header.tagline
  next.header.logo_position = t.header.logo_position
  next.table.columns = [...t.table.columns]
  next.table.labels = { ...t.table.labels }
  next.notes = clone(t.notes)
  next.footer.text = t.footer.text
  next.footer.page_numbers = t.footer.page_numbers
  next.sections = [...t.sections]
  next.letterhead = clone(t.letterhead)
  next.overrides = clone(t.overrides ?? {})
  if (t.colors.accent) next.colors.accent = t.colors.accent
  return next
}

/** "#abc" / "1d4ed8" → "#1D4ED8"; anything else null (the server's rule). */
export function normalizeHex(s: string): string | null {
  let h = s.trim().replace(/^#/, '')
  if (/^[0-9a-f]{3}$/i.test(h)) h = h.split('').map((c) => c + c).join('')
  return /^[0-9a-f]{6}$/i.test(h) ? `#${h.toUpperCase()}` : null
}

// ---- API ------------------------------------------------------------------------------

export interface AnalyzeResult {
  mode: 'ai' | 'heuristic'
  template: DocTemplate
  business: Partial<Record<'business_name' | 'address' | 'registration_no' | 'tax_no' | 'phone' | 'email' | 'website', string>>
  summary: string
  note: string
  letterhead: string | null
  letterhead_url: string | null
  preview: { ok: boolean; url: string | null; detail: string }
}

async function upload<T>(path: string, fd: FormData): Promise<T> {
  const res = await fetch(`${API}/aicore/${path}`, { method: 'POST', body: fd, headers: authHeaders() })
  if (expireSessionOn401(res)) throw new Error('Session expired — redirecting to login…')
  const data = await res.json().catch(() => ({}))
  if (!res.ok) throw new Error((data as { detail?: string }).detail || `HTTP ${res.status}`)
  return data as T
}

/** "Upload my design": a PDF / PNG / JPEG → a proposed template (nothing saved). */
export function analyzeDesign(
  file: File,
  opts: { template: DocTemplate | null; config: unknown; keepLetterhead: boolean; documentType?: DocumentType },
): Promise<AnalyzeResult> {
  const fd = new FormData()
  fd.append('file', file)
  if (opts.template) fd.append('template', JSON.stringify(opts.template))
  fd.append('config', JSON.stringify(opts.config))
  if (opts.keepLetterhead) fd.append('keep_letterhead', 'true')
  if (opts.documentType) fd.append('document_type', opts.documentType)
  return upload<AnalyzeResult>('skills/documents/template/analyze', fd)
}

/** "Ask AI": edit the template from words (nothing saved). */
export function assistTemplate(template: DocTemplate, instruction: string) {
  return aicore<{ mode: 'ai' | 'sandbox'; template: DocTemplate; summary: string; note: string }>(
    'skills/documents/template/assist',
    { method: 'POST', body: JSON.stringify({ template, instruction }) },
  )
}

/** Keep a PDF's first page as the letterhead background; the name goes in `template.letterhead.file`. */
export function uploadLetterhead(file: File): Promise<{ letterhead: string; url: string }> {
  const fd = new FormData()
  fd.append('file', file)
  return upload('skills/documents/letterhead', fd)
}
