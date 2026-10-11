import { authHeaders, expireSessionOn401 } from '@/lib/auth'
import type { DocumentType } from './readySkills'
import { aicore } from './skillsApi'

/**
 * The Documents skill's CUSTOM LAYOUT (`config.layout: "html"` + `config.html`) — the
 * client side of `ChatBranch.Data/Documents/DocumentHtmlConfig.cs`. A Handlebars HTML
 * template (`{{x}}` escapes, `{{{business.logo}}}` is the only raw value), the custom
 * fields it prints (`{{fields.key}}`), the page defaults, a letterhead and the layout's
 * fixed words (terms, bank details). The server normalizes, sanitizes and test-prints it
 * on save; the editor previews it through `/skills/documents/html/preview`.
 */

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

export const FIELD_TYPES = ['text', 'multiline', 'date', 'number'] as const
export type FieldType = (typeof FIELD_TYPES)[number]
export const MAX_FIELDS = 20
export const FIELD_KEY = /^[a-z][a-z0-9_]{0,31}$/

/** Names a custom field can't take (DocumentHtmlConfig.ReservedKeys). */
export const RESERVED_KEYS = new Set([
  'business', 'document', 'customer', 'items', 'totals', 'fields', 'terms', 'page', 'labels', 'customer_name',
  'customer_company', 'customer_address', 'customer_email', 'customer_phone', 'recipient_name', 'recipient_address',
  'due_date', 'reference', 'notes', 'title', 'payment_method', 'paragraphs', 'closing', 'body', 'number', 'type',
  'currency', 'language', 'issue_date', 'valid_until',
])

export interface HtmlField {
  key: string
  label: string
  type: FieldType
  required: boolean
  default: string
}

export interface HtmlLayout {
  version: 1
  template: string
  templates: Partial<Record<DocumentType, string>>
  fields: HtmlField[]
  page: { size: 'A4' | 'Letter'; margin_mm: { top: number; right: number; bottom: number; left: number } }
  letterhead: { file: string; pages: 'all' | 'first' }
  terms: string
  bank_details: string
}

export const EMPTY_LAYOUT: HtmlLayout = {
  version: 1,
  template: '',
  templates: {},
  fields: [],
  page: { size: 'A4', margin_mm: { top: 16, right: 18, bottom: 20, left: 18 } },
  letterhead: { file: '', pages: 'all' },
  terms: '',
  bank_details: '',
}

const isObj = (v: unknown): v is Record<string, unknown> => !!v && typeof v === 'object' && !Array.isArray(v)

/** A stored / proposed layout → a complete one. */
export function completeLayout(raw: unknown): HtmlLayout {
  const r = isObj(raw) ? raw : {}
  const page = isObj(r.page) ? r.page : {}
  const margins = isObj(page.margin_mm) ? page.margin_mm : {}
  const lh = isObj(r.letterhead) ? r.letterhead : {}
  const num = (v: unknown, d: number) => (typeof v === 'number' && Number.isFinite(v) ? v : d)
  return {
    version: 1,
    template: typeof r.template === 'string' ? r.template : '',
    templates: isObj(r.templates) ? (r.templates as HtmlLayout['templates']) : {},
    fields: Array.isArray(r.fields)
      ? r.fields.filter(isObj).map((f) => ({
          key: String(f.key ?? ''),
          label: String(f.label ?? ''),
          type: (FIELD_TYPES as readonly string[]).includes(String(f.type)) ? (f.type as FieldType) : 'text',
          required: f.required === true,
          default: typeof f.default === 'string' ? f.default : '',
        }))
      : [],
    page: {
      size: page.size === 'Letter' ? 'Letter' : 'A4',
      margin_mm: {
        top: num(margins.top, 16),
        right: num(margins.right, 18),
        bottom: num(margins.bottom, 20),
        left: num(margins.left, 18),
      },
    },
    letterhead: { file: typeof lh.file === 'string' ? lh.file : '', pages: lh.pages === 'first' ? 'first' : 'all' },
    terms: typeof r.terms === 'string' ? r.terms : '',
    bank_details: typeof r.bank_details === 'string' ? r.bank_details : '',
  }
}

/** The first problem with a field list the server would refuse (key rules), or null. */
export function fieldProblem(fields: HtmlField[]): { index: number; kind: 'key' | 'reserved' | 'duplicate' } | null {
  const seen = new Set<string>()
  for (let i = 0; i < fields.length; i++) {
    const k = fields[i].key
    if (!FIELD_KEY.test(k)) return { index: i, kind: 'key' }
    if (RESERVED_KEYS.has(k)) return { index: i, kind: 'reserved' }
    if (seen.has(k)) return { index: i, kind: 'duplicate' }
    seen.add(k)
  }
  return null
}

// ---- the catalog (GET /skills/apps → documents) ------------------------------------------

export interface HtmlStarter {
  id: 'malaysian-classic' | 'clean-modern' | 'letter' | string
  name: string
  description: string
  html: HtmlLayout
}

export interface HtmlReference {
  groups: { id: string; vars: { path: string; example: string; description: string }[] }[]
  helpers: { name: string; usage: string; description: string }[]
  labels: string[]
}

// ---- API ------------------------------------------------------------------------------------

export type PreviewResult =
  | { ok: true; html: string; type: DocumentType; language: 'en' | 'ms'; number: string }
  | { ok: false; error: string; line: number | null }

/** The instant preview: the template with sample data, sanitized + hardened, for a sandboxed iframe. */
export function previewLayout(body: {
  html: string
  fields: HtmlField[]
  type: DocumentType
  language: 'en' | 'ms'
  config: unknown
}, signal?: AbortSignal): Promise<PreviewResult> {
  return aicore<PreviewResult>('skills/documents/html/preview', { method: 'POST', body: JSON.stringify(body), signal })
}

export interface DraftResult {
  mode: 'ai' | 'starter'
  starter: string | null
  html: HtmlLayout
  business: Partial<Record<'business_name' | 'address' | 'registration_no' | 'tax_no' | 'phone' | 'email' | 'website', string>>
  summary: string
  note: string
}

/** "Upload my design": a PDF / PNG / JPEG → a whole custom layout (nothing saved). */
export async function draftLayout(file: File, opts: { instruction: string; config: unknown }): Promise<DraftResult> {
  const fd = new FormData()
  fd.append('file', file)
  if (opts.instruction.trim()) fd.append('instruction', opts.instruction.trim())
  fd.append('config', JSON.stringify(opts.config))
  const res = await fetch(`${API}/aicore/skills/documents/html/draft`, { method: 'POST', body: fd, headers: authHeaders() })
  if (expireSessionOn401(res)) throw new Error('Session expired — redirecting to login…')
  const data = await res.json().catch(() => ({}))
  if (!res.ok) throw new Error((data as { detail?: string }).detail || `HTTP ${res.status}`)
  return { ...(data as DraftResult), html: completeLayout((data as DraftResult).html) }
}

/** "Ask AI": the edited template + fields (nothing saved). */
export async function assistLayout(html: string, fields: HtmlField[], instruction: string) {
  const r = await aicore<{ mode: 'ai' | 'sandbox'; template: string; fields: HtmlField[]; summary: string; note: string }>(
    'skills/documents/html/assist',
    { method: 'POST', body: JSON.stringify({ html, fields, instruction }) },
  )
  return { ...r, fields: completeLayout({ fields: r.fields }).fields }
}
