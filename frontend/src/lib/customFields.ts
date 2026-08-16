/**
 * Custom Fields — the per-vertical label schema for Service Providers and
 * Customers (Settings → Custom Fields). Definitions come from
 * GET /api/settings/custom-fields; VALUES are stored in each row's free-form
 * `attributes` JSON (Resource.attributes / Contact.attributes), which the AI
 * already reads — so a "Language: Malay" label on a teacher reaches the bot.
 */
import { authHeaders, expireSessionOn401 } from '@/lib/auth'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

export type FieldKind = 'text' | 'select' | 'multiselect' | 'number' | 'boolean'
export type FieldTarget = 'provider' | 'customer'

export interface FieldDef {
  key: string
  label: string
  kind: FieldKind
  options: string[]
  required: boolean
  position: number
}

export interface CustomFieldsSchema {
  provider: FieldDef[]
  customer: FieldDef[]
  kinds: FieldKind[]
  targets: FieldTarget[]
}

export const KIND_LABELS: Record<FieldKind, string> = {
  text: 'Text',
  select: 'Dropdown (pick one)',
  multiselect: 'Multi-select (pick many)',
  number: 'Number',
  boolean: 'Yes / No',
}

export const EMPTY_SCHEMA: CustomFieldsSchema = {
  provider: [],
  customer: [],
  kinds: ['text', 'select', 'multiselect', 'number', 'boolean'],
  targets: ['provider', 'customer'],
}

export async function fetchCustomFields(): Promise<CustomFieldsSchema> {
  const res = await fetch(`${API}/settings/custom-fields`, { headers: authHeaders() })
  if (expireSessionOn401(res)) return EMPTY_SCHEMA
  if (!res.ok) throw new Error(`HTTP ${res.status}`)
  const d = (await res.json()) as Partial<CustomFieldsSchema>
  return {
    provider: Array.isArray(d.provider) ? d.provider : [],
    customer: Array.isArray(d.customer) ? d.customer : [],
    kinds: Array.isArray(d.kinds) ? (d.kinds as FieldKind[]) : EMPTY_SCHEMA.kinds,
    targets: Array.isArray(d.targets) ? (d.targets as FieldTarget[]) : EMPTY_SCHEMA.targets,
  }
}

export async function saveCustomFields(
  patch: Partial<Pick<CustomFieldsSchema, 'provider' | 'customer'>>,
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

/** Human-readable rendering of one stored attribute value, for lists/badges. */
export function formatFieldValue(def: FieldDef, value: unknown): string {
  if (value === null || value === undefined || value === '') return ''
  switch (def.kind) {
    case 'boolean':
      return value ? 'Yes' : 'No'
    case 'multiselect':
      return Array.isArray(value) ? value.join(', ') : String(value)
    default:
      return String(value)
  }
}
