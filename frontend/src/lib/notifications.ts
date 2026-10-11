import { API } from '@/lib/apiBase'
import { Activity, Bell, MessageSquare, ShieldCheck, Workflow, type LucideIcon } from 'lucide-react'
import { authHeaders, expireSessionOn401 } from './auth'
import type { MessageKey } from '@/i18n'

/**
 * Notification vocabulary shared by the inbox (/notifications), the personal
 * settings page and the organization defaults page — one list, same order and
 * wording as the server (NotificationCategories in ChatBranch.Data).
 */
export type NotifCategory = 'messages' | 'account_health' | 'flows' | 'admin' | 'system'

export interface NotifCategoryMeta {
  key: NotifCategory
  /** English label — kept for callers not yet localised; render `t(labelKey)`. */
  label: string
  description: string
  /** Translation keys of the label and the one-line description. */
  labelKey: MessageKey
  descriptionKey: MessageKey
  Icon: LucideIcon
  /** Inline ink (theme-aware CSS var); chips tint it with tint(ink, 0.12).
   *  Kept out of Tailwind classes on purpose — lib/ is not in Tailwind's
   *  content globs, so a class named only here would never be generated. */
  ink: string
}

export const NOTIF_CATEGORIES: NotifCategoryMeta[] = [
  {
    key: 'messages',
    label: 'Customer messages',
    description: 'A customer wrote in, on any chat channel',
    labelKey: 'notifications.categories.messages',
    descriptionKey: 'notifications.categories.messagesHint',
    Icon: MessageSquare,
    ink: 'var(--green-ink)',
  },
  {
    key: 'account_health',
    label: 'Channel health',
    description: 'An account went down or degraded, routing failover and recovery prompts',
    labelKey: 'notifications.categories.accountHealth',
    descriptionKey: 'notifications.categories.accountHealthHint',
    Icon: Activity,
    ink: 'var(--red-ink)',
  },
  {
    key: 'flows',
    label: 'Flows & automations',
    description: 'Flow pushes, human handovers and automation alerts',
    labelKey: 'notifications.categories.flows',
    descriptionKey: 'notifications.categories.flowsHint',
    Icon: Workflow,
    ink: 'var(--purple-ink)',
  },
  {
    key: 'admin',
    label: 'Admin & configuration',
    description: 'Users, app keys, integrations and channel account changes',
    labelKey: 'notifications.categories.admin',
    descriptionKey: 'notifications.categories.adminHint',
    Icon: ShieldCheck,
    ink: 'var(--cyan-ink)',
  },
  {
    key: 'system',
    label: 'System',
    description: 'Everything else the platform needs you to know',
    labelKey: 'notifications.categories.system',
    descriptionKey: 'notifications.categories.systemHint',
    Icon: Bell,
    ink: 'var(--amber-ink)',
  },
]

export const categoryMeta = (key: string): NotifCategoryMeta =>
  NOTIF_CATEGORIES.find((c) => c.key === key) ?? NOTIF_CATEGORIES[NOTIF_CATEGORIES.length - 1]

export interface NotificationItem {
  id: number
  kind: string
  category: NotifCategory
  title: string
  body: string
  link: string
  read: boolean
  created_at: string
}

export interface NotificationPage {
  items: NotificationItem[]
  page: number
  page_size: number
  total: number
  has_more: boolean
  unread: number
  unread_by_category: Record<string, number>
}

export interface NotificationPrefs {
  categories: { key: NotifCategory; label: string; description: string }[]
  user: Record<string, boolean>
  only_assigned: boolean
  org: Record<string, boolean>
  can_manage_org: boolean
}

/** JSON fetch against /api with the bearer token; throws the server's detail. */
export async function notifApi<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}/${path}`, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...authHeaders(), ...(init?.headers || {}) },
  })
  if (expireSessionOn401(res)) throw new Error('Session expired — redirecting to sign-in…')
  const data = await res.json().catch(() => ({}))
  if (!res.ok) throw new Error((data as { detail?: string }).detail || `HTTP ${res.status}`)
  return data as T
}
