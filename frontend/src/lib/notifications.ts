import { Activity, Bell, MessageSquare, ShieldCheck, Workflow, type LucideIcon } from 'lucide-react'
import { authHeaders, expireSessionOn401 } from './auth'

/**
 * Notification vocabulary shared by the inbox (/notifications), the personal
 * settings page and the organization defaults page — one list, same order and
 * wording as the server (NotificationCategories in ChatBranch.Data).
 */
export type NotifCategory = 'messages' | 'account_health' | 'flows' | 'admin' | 'system'

export interface NotifCategoryMeta {
  key: NotifCategory
  label: string
  description: string
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
    Icon: MessageSquare,
    ink: 'var(--green-ink)',
  },
  {
    key: 'account_health',
    label: 'Channel health',
    description: 'An account went down or degraded, routing failover and recovery prompts',
    Icon: Activity,
    ink: 'var(--red-ink)',
  },
  {
    key: 'flows',
    label: 'Flows & automations',
    description: 'Flow pushes, human handovers and automation alerts',
    Icon: Workflow,
    ink: 'var(--purple-ink)',
  },
  {
    key: 'admin',
    label: 'Admin & configuration',
    description: 'Users, app keys, integrations and channel account changes',
    Icon: ShieldCheck,
    ink: 'var(--cyan-ink)',
  },
  {
    key: 'system',
    label: 'System',
    description: 'Everything else the platform needs you to know',
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

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

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
