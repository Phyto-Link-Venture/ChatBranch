/**
 * Custom Fields — the per-vertical label schema for Customers (Settings →
 * Custom Fields). Definitions come from GET /api/settings/custom-fields;
 * VALUES are stored in each contact's free-form `attributes` JSON, which the
 * AI already reads — so a "Grade: 5" label on a customer reaches the bot.
 * (Fields that depend on a relationship — a driver's licence number — ride on
 * registry tags instead: lib/tags.ts.)
 */
import { authHeaders, expireSessionOn401 } from '@/lib/auth'
import type { MessageKey, TFunction } from '@/i18n'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

export type FieldKind = 'text' | 'select' | 'multiselect' | 'number' | 'boolean'
export type FieldTarget = 'customer'

export interface FieldDef {
  key: string
  label: string
  kind: FieldKind
  options: string[]
  required: boolean
  position: number
}

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
}

export const EMPTY_SCHEMA: CustomFieldsSchema = {
  customer: [],
  kinds: ['text', 'select', 'multiselect', 'number', 'boolean'],
  targets: ['customer'],
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
 *  Pass `t` for a translated Yes / No. */
export function formatFieldValue(def: FieldDef, value: unknown, t?: TFunction): string {
  if (value === null || value === undefined || value === '') return ''
  switch (def.kind) {
    case 'boolean':
      if (t) return value ? t('common.status.yes') : t('common.status.no')
      return value ? 'Yes' : 'No'
    case 'multiselect':
      return Array.isArray(value) ? value.join(', ') : String(value)
    default:
      return String(value)
  }
}
