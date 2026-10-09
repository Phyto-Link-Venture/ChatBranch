import type { MessageKey } from '@/i18n'

/**
 * Activity log (GET /api/audit) — types and the source vocabulary shared by the
 * Activity page and anything else that shows audit rows. Lives under
 * components/ (not lib/) because it carries Tailwind classes, and only
 * components/ and app/ are in Tailwind's content scan.
 */

export type AuditSource = 'user' | 'api' | 'mcp' | 'assistant' | 'automation' | 'flow' | 'webhook' | 'system'

export interface AuditEntry {
  id: number
  created_at: string
  actor: string
  user_id: number | null
  source: AuditSource | string
  action: string
  target: string
  detail: string
  entity_id: number | null
  entity: string | null
  entity_name: string | null
  method: string
  path: string
  status: number | null
  ip: string
  user_agent: string
  request_id: string
}

export interface AuditPage {
  entries: AuditEntry[]
  total: number
  page: number
  page_size: number
  pages: number
}

export interface AuditFacets {
  actors: { value: string; count: number }[]
  actions: { value: string; count: number }[]
  sources: { value: string; count: number }[]
}

/** Label + one-line meaning (translation keys) and token classes per source (badge + chip). */
export const SOURCE_META: Record<AuditSource, { label: MessageKey; hint: MessageKey; cls: string; dot: string }> = {
  user: {
    label: 'review.sources.user',
    hint: 'review.sources.userHint',
    cls: 'bg-blue/10 text-blue-300',
    dot: 'bg-blue',
  },
  api: {
    label: 'review.sources.api',
    hint: 'review.sources.apiHint',
    cls: 'bg-purple/10 text-purple-300',
    dot: 'bg-purple',
  },
  mcp: {
    label: 'review.sources.mcp',
    hint: 'review.sources.mcpHint',
    cls: 'bg-cyan/10 text-cyan-300',
    dot: 'bg-cyan',
  },
  assistant: {
    label: 'review.sources.assistant',
    hint: 'review.sources.assistantHint',
    cls: 'bg-brand-400/10 text-brand-300',
    dot: 'bg-brand',
  },
  automation: {
    label: 'review.sources.automation',
    hint: 'review.sources.automationHint',
    cls: 'bg-amber/10 text-amber-300',
    dot: 'bg-amber',
  },
  flow: {
    label: 'review.sources.flow',
    hint: 'review.sources.flowHint',
    cls: 'bg-green/10 text-green-300',
    dot: 'bg-green',
  },
  webhook: {
    label: 'review.sources.webhook',
    hint: 'review.sources.webhookHint',
    cls: 'bg-fill-hover text-text-mid',
    dot: 'bg-text-muted',
  },
  system: {
    label: 'review.sources.system',
    hint: 'review.sources.systemHint',
    cls: 'bg-fill text-text-muted',
    dot: 'bg-text-faint',
  },
}

export const SOURCES = Object.keys(SOURCE_META) as AuditSource[]

export function sourceMeta(s: string) {
  return SOURCE_META[s as AuditSource] ?? SOURCE_META.system
}

/** "200" → ok / "4xx" → refused / "5xx" → failed, as token classes. */
export function statusTone(status: number | null): string {
  if (status == null) return 'text-text-faint'
  if (status >= 500) return 'text-red-300'
  if (status >= 400) return 'text-amber-300'
  return 'text-green-300'
}
