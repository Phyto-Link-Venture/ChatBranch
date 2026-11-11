/**
 * Custom Fields — the per-vertical label schema for Customers (Settings →
 * Custom Fields). Definitions come from GET /api/settings/custom-fields;
 * VALUES are stored in each contact's free-form `attributes` JSON, which the
 * AI already reads — so a "Grade: 5" label on a customer reaches the bot.
 * (Fields that depend on a relationship — a driver's licence number — ride on
 * registry tags instead: lib/tags.ts.)
 */
import { API } from '@/lib/apiBase'
import { authHeaders, expireSessionOn401 } from '@/lib/auth'
import type { MessageKey, TFunction } from '@/i18n'

export type FieldKind = 'text' | 'select' | 'multiselect' | 'number' | 'boolean' | 'date' | 'json'
export type FieldTarget = 'customer'

export interface FieldDef {
  key: string
  label: string
  kind: FieldKind
  options: string[]
  required: boolean
  position: number
  /** let the AI answering a customer see this field's value (default off) */
  show_to_ai?: boolean
  /** the entity this field belongs to — only its company's AI sees it (null = organization-wide) */
  entity_id?: number | null
}

/** Kinds that need an option list. */
export const NEEDS_OPTIONS: FieldKind[] = ['select', 'multiselect']

/** A json field's cap, in characters (ContactCustomFields.JsonMaxChars). */
export const JSON_MAX_CHARS = 8000

export interface CustomFieldsSchema {
  customer: FieldDef[]
  kinds: FieldKind[]
  targets: FieldTarget[]
}

/** Field types → translation keys (render with `t(KIND_LABELS[kind])`; the stored kind stays the id). */
export const KIND_LABELS: Record<FieldKind, MessageKey> = {
  text: 'settings.customFields.kinds.text',
  select: 'settings.customFields.kinds.select',
  multiselect: 'settings.customFields.kinds.multiselect',
  number: 'settings.customFields.kinds.number',
  boolean: 'settings.customFields.kinds.boolean',
  date: 'settings.customFields.kinds.date',
  json: 'settings.customFields.kinds.json',
}

export const EMPTY_SCHEMA: CustomFieldsSchema = {
  customer: [],
  kinds: ['text', 'select', 'multiselect', 'number', 'boolean', 'date', 'json'],
  targets: ['customer'],
}

/** The canonical stored date (YYYY-MM-DD) in the reader's locale — the fallback
 *  when no `useFormat().date` was handed in (the hook needs a component). */
export function formatDateValue(iso: string, locale?: string): string {
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(iso.trim())
  if (!m) return iso
  const d = new Date(Number(m[1]), Number(m[2]) - 1, Number(m[3]))
  if (Number.isNaN(d.getTime())) return iso
  try {
    return d.toLocaleDateString(locale, { year: 'numeric', month: 'short', day: 'numeric' })
  } catch {
    return iso
  }
}

/** A json field's value as bounded `key: value` pairs — never raw braces. */
export function flattenJsonValue(value: unknown, maxEntries = 12, maxLen = 120): string {
  const obj =
    typeof value === 'string'
      ? (() => {
          try {
            const p: unknown = JSON.parse(value)
            return p && typeof p === 'object' && !Array.isArray(p) ? (p as Record<string, unknown>) : null
          } catch {
            return null
          }
        })()
      : value && typeof value === 'object' && !Array.isArray(value)
        ? (value as Record<string, unknown>)
        : null
  if (!obj) return ''
  const cap = (s: string) => (s.length <= maxLen ? s : `${s.slice(0, maxLen).trimEnd()}…`)
  const scalar = (v: unknown): string => {
    if (v === null || v === undefined) return ''
    if (Array.isArray(v)) return v.map(scalar).filter(Boolean).slice(0, 8).join(', ')
    if (typeof v === 'object')
      return Object.entries(v as Record<string, unknown>)
        .slice(0, 8)
        .map(([k, x]) => `${k}: ${scalar(x)}`)
        .join(', ')
    if (typeof v === 'boolean') return v ? 'Yes' : 'No'
    return String(v)
  }
  const parts: string[] = []
  const walk = (o: Record<string, unknown>, prefix: string, depth: number): boolean => {
    for (const [k, v] of Object.entries(o)) {
      if (parts.length >= maxEntries) return true
      const name = prefix ? `${prefix}.${k}` : k
      if (v === null || v === undefined) continue
      if (typeof v === 'object' && !Array.isArray(v) && depth < 2) {
        if (walk(v as Record<string, unknown>, name, depth + 1)) return true
      } else {
        parts.push(`${name}: ${cap(scalar(v)).replace(/[{}]/g, (c) => (c === '{' ? '(' : ')'))}`)
      }
    }
    return false
  }
  const more = walk(obj, '', 0)
  if (parts.length === 0) return ''
  return parts.join('; ') + (more ? '; …' : '')
}

export async function fetchCustomFields(): Promise<CustomFieldsSchema> {
  const res = await fetch(`${API}/settings/custom-fields`, { headers: authHeaders() })
  if (expireSessionOn401(res)) return EMPTY_SCHEMA
  if (!res.ok) throw new Error(`HTTP ${res.status}`)
  const d = (await res.json()) as Partial<CustomFieldsSchema>
  return {
    customer: Array.isArray(d.customer) ? d.customer : [],
    kinds: Array.isArray(d.kinds) ? (d.kinds as FieldKind[]) : EMPTY_SCHEMA.kinds,
    // only targets this client knows how to render
    targets: (Array.isArray(d.targets) ? (d.targets as string[]) : EMPTY_SCHEMA.targets).filter(
      (t): t is FieldTarget => t === 'customer',
    ),
  }
}

export async function saveCustomFields(
  patch: Partial<Pick<CustomFieldsSchema, 'customer'>>,
): Promise<CustomFieldsSchema> {
  const res = await fetch(`${API}/settings/custom-fields`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
    body: JSON.stringify(patch),
  })
  const data = await res.json().catch(() => ({}))
  if (!res.ok) throw new Error((data as { detail?: string }).detail || `HTTP ${res.status}`)
  return data as CustomFieldsSchema
}

/** Human-readable rendering of one stored attribute value, for lists/badges.
 *  Pass `t` for a translated Yes / No, and `dateFormat` (`useFormat().date`) to
 *  render a date the way the operator reads it — the stored form of a date is
 *  always the canonical YYYY-MM-DD, and a json value is flattened to
 *  `key: value` pairs so it never carries braces into a list or a badge. */
export function formatFieldValue(
  def: FieldDef,
  value: unknown,
  t?: TFunction,
  dateFormat: (iso: string) => string = formatDateValue,
): string {
  if (value === null || value === undefined || value === '') return ''
  switch (def.kind) {
    case 'boolean':
      if (t) return value ? t('common.status.yes') : t('common.status.no')
      return value ? 'Yes' : 'No'
    case 'multiselect':
      return Array.isArray(value) ? value.join(', ') : String(value)
    case 'date':
      return dateFormat(String(value)) || String(value)
    case 'json':
      return flattenJsonValue(value)
    default:
      return String(value)
  }
}
