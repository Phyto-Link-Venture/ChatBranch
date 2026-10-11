import { API, apiResource } from '@/lib/apiBase'
import type { MessageKey, TFunction } from '@/i18n'
import { authHeaders, expireSessionOn401 } from '@/lib/auth'
import { TOOL_APP_IDS, aicore, type ToolAppId } from './skillsApi'
import type { DocTemplate } from './documentTemplate'
import type { HtmlLayout, HtmlReference, HtmlStarter } from './htmlLayout'

/**
 * The ready-made "Connected app" skills besides the calendar — Documents (PDF
 * invoices, quotations, receipts, letters), QR codes, Payment links (Stripe) and
 * the Google Sheets log — plus the instruction templates the Skills page's
 * gallery offers. Their settings live in `AgentSkill.config` (`app: "documents"`
 * …), validated by the server (`AppSkills.Normalize`); the shapes and defaults
 * here mirror `GET /api/aicore/skills/apps`.
 */

export type { ToolAppId }
export { TOOL_APP_IDS }

export type DocumentType = 'invoice' | 'quotation' | 'receipt' | 'letter'
export const DOCUMENT_TYPES: DocumentType[] = ['invoice', 'quotation', 'receipt', 'letter']
export const ACCENTS = ['ink', 'tangerine', 'teal', 'plum', 'ochre', 'blue', 'green', 'rose'] as const

export interface DocumentsConfig {
  app: 'documents'
  actions: DocumentType[]
  business_name: string
  address: string
  registration_no: string
  tax_no: string
  phone: string
  email: string
  website: string
  currency: string
  tax_label: string
  tax_rate: number
  payment_instructions: string
  footer_note: string
  quotation_valid_days: number
  invoice_due_days: number
  prefixes: Record<DocumentType, string>
  start_numbers: Record<DocumentType, number>
  number_digits: number
  accent: string
  logo: string
  language: 'auto' | 'en' | 'ms'
  /** The designed look (the template designer); absent / null = the classic layout. */
  template?: DocTemplate | null
  /** designer (default) | html — print with the custom layout below. */
  layout?: 'designer' | 'html'
  /** The custom layout (HTML template + fields); kept while the designer is in use. */
  html?: HtmlLayout | null
}
export interface QrConfig {
  app: 'qr'
  caption: string
  pixels_per_module: number
}
export interface PaymentsConfig {
  app: 'payments'
  provider: 'stripe'
  currency: string
  max_amount: number
  success_url: string
}
export interface SheetsConfig {
  app: 'sheets'
  spreadsheet_id: string
  sheet_range: string
  columns: string[]
  label: string
}
export type ToolAppConfig = DocumentsConfig | QrConfig | PaymentsConfig | SheetsConfig

export const TOOL_DEFAULTS: { [K in ToolAppId]: Extract<ToolAppConfig, { app: K }> } = {
  documents: {
    app: 'documents',
    actions: [...DOCUMENT_TYPES],
    business_name: '',
    address: '',
    registration_no: '',
    tax_no: '',
    phone: '',
    email: '',
    website: '',
    currency: 'MYR',
    tax_label: 'SST',
    tax_rate: 0,
    payment_instructions: '',
    footer_note: 'Thank you for your business.',
    quotation_valid_days: 14,
    invoice_due_days: 14,
    prefixes: { invoice: 'INV-', quotation: 'QUO-', receipt: 'RCP-', letter: 'LTR-' },
    start_numbers: { invoice: 1, quotation: 1, receipt: 1, letter: 1 },
    number_digits: 4,
    accent: 'ink',
    logo: '',
    language: 'auto',
  },
  qr: { app: 'qr', caption: '', pixels_per_module: 10 },
  payments: { app: 'payments', provider: 'stripe', currency: 'MYR', max_amount: 10000, success_url: '' },
  sheets: { app: 'sheets', spreadsheet_id: '', sheet_range: 'Sheet1!A:Z', columns: ['Request', 'Details'], label: 'Leads' },
}

const isObj = (v: unknown): v is Record<string, unknown> => !!v && typeof v === 'object' && !Array.isArray(v)

/** True when a stored app config is one of these (not the calendar). */
export function isToolAppConfig(raw: unknown): boolean {
  return isObj(raw) && typeof raw.app === 'string' && (TOOL_APP_IDS as readonly string[]).includes(raw.app)
}

/** A stored / partial config → a complete one (defaults for anything missing). */
export function toolAppConfig(raw: unknown, app?: ToolAppId): ToolAppConfig {
  const r = isObj(raw) ? raw : {}
  const id = (app ?? (typeof r.app === 'string' ? r.app : 'documents')) as ToolAppId
  const base = TOOL_DEFAULTS[(TOOL_APP_IDS as readonly string[]).includes(id) ? id : 'documents']
  const merged: Record<string, unknown> = { ...base }
  for (const [k, v] of Object.entries(r)) if (v !== undefined && v !== null) merged[k] = v
  if (base.app === 'documents') {
    const d = base as DocumentsConfig
    merged.prefixes = { ...d.prefixes, ...(isObj(r.prefixes) ? r.prefixes : {}) }
    merged.start_numbers = { ...d.start_numbers, ...(isObj(r.start_numbers) ? r.start_numbers : {}) }
  }
  merged.app = base.app
  return merged as unknown as ToolAppConfig
}

/** The first problem with a config (shown in the wizard's alert), or null. */
export function toolAppProblem(c: ToolAppConfig, t: TFunction): string | null {
  switch (c.app) {
    case 'documents':
      if (!c.actions.length) return t('readySkills.form.problems.pickType')
      if (!/^[A-Za-z]{3}$/.test(c.currency)) return t('readySkills.form.problems.currency')
      if (!(Number(c.tax_rate) >= 0 && Number(c.tax_rate) <= 100)) return t('readySkills.form.problems.taxRate')
      return null
    case 'payments':
      if (!/^[A-Za-z]{3}$/.test(c.currency)) return t('readySkills.form.problems.currency')
      if (!(Number(c.max_amount) >= 1)) return t('readySkills.form.problems.maxAmount')
      if (c.success_url && !/^https:\/\//i.test(c.success_url)) return t('readySkills.form.problems.successUrl')
      return null
    case 'sheets':
      return c.columns.some((x) => x.trim()) ? null : t('readySkills.form.problems.columns')
    default:
      return null
  }
}

/** GET /skills/apps — the non-calendar entries. */
export interface ToolAppCatalogEntry {
  app: ToolAppId
  label: string
  description: string
  connected: boolean
  provider_label?: string
  cjk_font?: boolean
  /** Documents: the designer's "Start from…" presets (canonical templates). */
  template_presets?: Record<string, DocTemplate>
  /** Documents: can this server print a custom layout (headless Chromium)? */
  html_renderer?: 'available' | 'missing'
  /** Documents: the ready-made custom layouts. */
  html_starters?: HtmlStarter[]
  /** Documents: every variable / helper / label the custom layout may use. */
  html_reference?: HtmlReference
}

export async function fetchToolApps(): Promise<ToolAppCatalogEntry[] | null> {
  try {
    const d = await aicore<{ apps: { app: string }[] }>('skills/apps')
    return Array.isArray(d.apps)
      ? (d.apps.filter((a) => (TOOL_APP_IDS as readonly string[]).includes(a.app)) as ToolAppCatalogEntry[])
      : null
  } catch {
    return null
  }
}

/** POST /skills/test with an unsaved tool-app config — Documents / QR render a sample (nothing sent), Payments / Sheets check their connection. */
export function testToolApp(config: ToolAppConfig, documentType?: DocumentType) {
  return aicore<{ ok: boolean; detail: string; url?: string; number?: string; total?: number }>('skills/test', {
    method: 'POST',
    body: JSON.stringify({ kind: 'app', config, app_action: 'preview', document_type: documentType }),
  })
}

/** Upload the Documents letterhead logo; the returned name goes in `config.logo`. */
export async function uploadLogo(file: File): Promise<{ logo: string; url: string }> {
  const fd = new FormData()
  fd.append('file', file)
  const res = await fetch(`${API}/aicore/skills/documents/logo`, { method: 'POST', body: fd, headers: authHeaders() })
  if (expireSessionOn401(res)) throw new Error('Session expired — redirecting to login…')
  const data = await res.json().catch(() => ({}))
  if (!res.ok) throw new Error((data as { detail?: string }).detail || `HTTP ${res.status}`)
  return data as { logo: string; url: string }
}

/**
 * Open an auth-gated file (a document, a preview) in a new tab: the tab is opened
 * at once (so no pop-up blocker objects) and pointed at the fetched blob.
 */
export async function openAuthed(url: string): Promise<void> {
  const tab = typeof window !== 'undefined' ? window.open('', '_blank') : null
  try {
    const res = await fetch(apiResource(url), { headers: authHeaders() })
    if (!res.ok) throw new Error(`HTTP ${res.status}`)
    const blobUrl = URL.createObjectURL(await res.blob())
    if (tab) tab.location.href = blobUrl
    else window.open(blobUrl, '_blank')
  } catch (e) {
    tab?.close()
    throw e
  }
}

/** Fetch an auth-gated image as an object URL (the logo thumbnail). */
export async function authedObjectUrl(url: string): Promise<string> {
  const res = await fetch(apiResource(url), { headers: authHeaders() })
  if (!res.ok) throw new Error(`HTTP ${res.status}`)
  return URL.createObjectURL(await res.blob())
}

// ---- the gallery ------------------------------------------------------------------

export const READY_APPS: { app: ToolAppId; name: MessageKey; blurb: MessageKey; useWhen: MessageKey; instructions: MessageKey; provider?: string }[] = [
  {
    app: 'documents',
    name: 'readySkills.apps.documents.name',
    blurb: 'readySkills.apps.documents.blurb',
    useWhen: 'readySkills.apps.documents.useWhen',
    instructions: 'readySkills.apps.documents.instructions',
  },
  {
    app: 'qr',
    name: 'readySkills.apps.qr.name',
    blurb: 'readySkills.apps.qr.blurb',
    useWhen: 'readySkills.apps.qr.useWhen',
    instructions: 'readySkills.apps.qr.instructions',
  },
  {
    app: 'payments',
    name: 'readySkills.apps.payments.name',
    blurb: 'readySkills.apps.payments.blurb',
    useWhen: 'readySkills.apps.payments.useWhen',
    instructions: 'readySkills.apps.payments.instructions',
    provider: 'Stripe',
  },
  {
    app: 'sheets',
    name: 'readySkills.apps.sheets.name',
    blurb: 'readySkills.apps.sheets.blurb',
    useWhen: 'readySkills.apps.sheets.useWhen',
    instructions: 'readySkills.apps.sheets.instructions',
    provider: 'Google Sheets',
  },
]

export type PromptTemplateId = 'quoteThenInvoice' | 'collectOrder' | 'politeFollowUp' | 'paymentReminder'
export const PROMPT_TEMPLATES: { id: PromptTemplateId; name: MessageKey; useWhen: MessageKey; instructions: MessageKey }[] = (
  ['quoteThenInvoice', 'collectOrder', 'politeFollowUp', 'paymentReminder'] as const
).map((id) => ({
  id,
  name: `readySkills.templates.${id}.name` as MessageKey,
  useWhen: `readySkills.templates.${id}.useWhen` as MessageKey,
  instructions: `readySkills.templates.${id}.instructions` as MessageKey,
}))

export const readyApp = (app: ToolAppId) => READY_APPS.find((a) => a.app === app)!
