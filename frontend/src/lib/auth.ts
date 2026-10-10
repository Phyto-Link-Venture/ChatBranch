/**
 * Real RBAC client: login via /api/auth/login, bearer token + user cached in
 * localStorage, attached to every role-checked API call. Roles:
 *   superadmin — everything incl. app keys
 *   admin      — queues + channel account management (own tenant)
 *   agent      — queues only, scoped to their assigned entities
 * ("agent" was renamed to "collector" in an earlier entities RBAC pass, then
 * reverted back to "agent" — cached sessions holding the old "collector"
 * name are normalized on read.)
 *
 * Dynamic RBAC (upgrade §3): the cached user now also carries `capabilities`
 * — the fine-grained permission strings the current role is granted, resolved
 * server-side from the editable RolePermission matrix. UI gates prefer
 * `hasCap(cap)`; when capabilities are missing (an older cached session, or a
 * backend that predates the matrix) the helpers fall back to a static
 * role→capability default that mirrors the server seed.
 */
import { clearAssistantCache } from './assistantCache'
import { clearPlaygroundCache } from './playgroundCache'
import type { MessageKey, TFunction } from '@/i18n'

export type Role = 'superadmin' | 'admin' | 'agent'

export interface AuthUser {
  username: string
  name: string
  role: Role
  token: string
  /** Fine-grained permissions for the current role (dynamic RBAC). Optional so
   *  older cached sessions still parse — absence triggers the role fallback. */
  capabilities?: string[]
}

/**
 * The full capability catalog (spec §3). Kept in sync with the backend seed so
 * the role fallback and the Permissions matrix render sensibly even before the
 * API answers. `area`/`label` are the English copy (and the fallback for an id
 * this client does not know); `lbl` is the translation key the UI renders —
 * see `capabilityLabel` / `capabilityArea`.
 */
export const CAPABILITY_CATALOG: { capability: string; area: string; label: string; lbl: MessageKey }[] = [
  { capability: 'contacts.view', area: 'Contacts', label: 'View contacts', lbl: 'admin.caps.contactsView' },
  { capability: 'contacts.manage', area: 'Contacts', label: 'Create & edit contacts', lbl: 'admin.caps.contactsManage' },
  { capability: 'chats.view', area: 'Chats', label: 'View chats', lbl: 'admin.caps.chatsView' },
  { capability: 'chats.send', area: 'Chats', label: 'Send messages', lbl: 'admin.caps.chatsSend' },
  { capability: 'chats.manage', area: 'Chats', label: 'Manage chats (assign, close)', lbl: 'admin.caps.chatsManage' },
  { capability: 'flows.view', area: 'Flows', label: 'View flows', lbl: 'admin.caps.flowsView' },
  { capability: 'flows.manage', area: 'Flows', label: 'Build & edit flows', lbl: 'admin.caps.flowsManage' },
  { capability: 'channels.view', area: 'Channels', label: 'View channel accounts', lbl: 'admin.caps.channelsView' },
  { capability: 'channels.manage', area: 'Channels', label: 'Manage channel accounts', lbl: 'admin.caps.channelsManage' },
  { capability: 'integrations.view', area: 'Integrations', label: 'View integrations', lbl: 'admin.caps.integrationsView' },
  { capability: 'integrations.manage', area: 'Integrations', label: 'Manage integrations', lbl: 'admin.caps.integrationsManage' },
  { capability: 'users.view', area: 'Users', label: 'View users', lbl: 'admin.caps.usersView' },
  { capability: 'users.manage', area: 'Users', label: 'Create & manage users', lbl: 'admin.caps.usersManage' },
  { capability: 'entities.view', area: 'Entities', label: 'View entities & tenants', lbl: 'admin.caps.entitiesView' },
  { capability: 'entities.manage', area: 'Entities', label: 'Manage entities & tenants', lbl: 'admin.caps.entitiesManage' },
  { capability: 'templates.manage', area: 'Library', label: 'Manage message templates', lbl: 'admin.caps.templatesManage' },
  { capability: 'script_templates.manage', area: 'Library', label: 'Manage script templates', lbl: 'admin.caps.scriptTemplatesManage' },
  { capability: 'notifications.manage', area: 'Settings', label: 'Manage organization notification defaults', lbl: 'admin.caps.notificationsManage' },
  { capability: 'analytics.view', area: 'Review', label: 'View dashboard', lbl: 'admin.caps.analyticsView' },
  { capability: 'call_logs.view', area: 'Review', label: 'View call logs', lbl: 'admin.caps.callLogsView' },
  { capability: 'activity.view', area: 'Review', label: 'View activity log', lbl: 'admin.caps.activityView' },
  { capability: 'settings.ai.manage', area: 'Settings', label: 'Manage AI configuration', lbl: 'admin.caps.settingsAiManage' },
  { capability: 'account_assignment.manage', area: 'Settings', label: 'Manage account assignment', lbl: 'admin.caps.accountAssignmentManage' },
  { capability: 'app_keys.manage', area: 'Settings', label: 'Manage app & provider keys', lbl: 'admin.caps.appKeysManage' },
  { capability: 'pronunciation.manage', area: 'Settings', label: 'Manage pronunciation', lbl: 'admin.caps.pronunciationManage' },
  // Settings sub-pages — one capability each (Personal pages need none).
  { capability: 'settings.appearance.org', area: 'Settings', label: 'Set the organization default appearance', lbl: 'admin.caps.settingsAppearanceOrg' },
  { capability: 'settings.custom_fields.manage', area: 'Settings', label: 'Manage custom fields', lbl: 'admin.caps.settingsCustomFieldsManage' },
  { capability: 'settings.ai_keys.manage', area: 'Settings', label: 'Manage AI keys & models', lbl: 'admin.caps.settingsAiKeysManage' },
  { capability: 'rbac.manage', area: 'Admin', label: 'Edit the permission matrix', lbl: 'admin.caps.rbacManage' },
  { capability: 'tags.view', area: 'Library', label: 'View tags', lbl: 'admin.caps.tagsView' },
  { capability: 'tags.manage', area: 'Library', label: 'Create & edit tags', lbl: 'admin.caps.tagsManage' },
  { capability: 'api_keys.manage', area: 'Admin', label: 'Manage API keys & MCP', lbl: 'admin.caps.apiKeysManage' },
  { capability: 'ai.monitor', area: 'AI', label: 'See & approve AI drafts', lbl: 'admin.caps.aiMonitor' },
  { capability: 'ai.behaviour.manage', area: 'AI', label: 'Edit AI behaviour settings', lbl: 'admin.caps.aiBehaviourManage' },
  { capability: 'billing.view', area: 'Review', label: 'View the AI usage bill', lbl: 'admin.caps.billingView' },
  { capability: 'billing.manage', area: 'Settings', label: 'Set AI billing markup & see costs', lbl: 'admin.caps.billingManage' },
  { capability: 'billing.quotas.manage', area: 'Review', label: 'Set AI usage quotas', lbl: 'admin.caps.billingQuotasManage' },
]

const CAP_KEY = new Map<string, MessageKey>(CAPABILITY_CATALOG.map((c) => [c.capability, c.lbl]))

/** Area names as the API and this catalog spell them → translation keys. */
const AREA_KEY: Record<string, MessageKey> = {
  Contacts: 'admin.areas.contacts',
  Chats: 'admin.areas.chats',
  Flows: 'admin.areas.flows',
  Channels: 'admin.areas.channels',
  Integrations: 'admin.areas.integrations',
  Users: 'admin.areas.users',
  'Users & Roles': 'admin.areas.users',
  Entities: 'admin.areas.entities',
  'Entities & Tenants': 'admin.areas.entities',
  Library: 'admin.areas.library',
  Settings: 'admin.areas.settings',
  Review: 'admin.areas.review',
  Admin: 'admin.areas.admin',
  AI: 'admin.areas.ai',
}

/** A capability's display label in the current language — translated by id;
 *  an id this client does not know shows the server's own label. */
export function capabilityLabel(t: TFunction, c: { capability: string; label?: string }): string {
  const key = CAP_KEY.get(c.capability)
  return key ? t(key) : c.label || c.capability
}

/** A capability area's display name in the current language. */
export function capabilityArea(t: TFunction, area: string): string {
  const key = AREA_KEY[area]
  return key ? t(key) : area
}

/** The built-in roles' display names (built-ins cannot be renamed server-side). */
export const ROLE_LABEL_KEYS: Record<Role, MessageKey> = {
  superadmin: 'admin.roles.superadmin',
  admin: 'admin.roles.admin',
  agent: 'admin.roles.agent',
}

/** Plural group headings ("Admins") for lists grouped by role. */
export const ROLE_PLURAL_KEYS: Record<Role, MessageKey> = {
  superadmin: 'admin.roles.superadminPlural',
  admin: 'admin.roles.adminPlural',
  agent: 'admin.roles.agentPlural',
}

const titleCase = (slug: string) => slug.replace(/[-_]+/g, ' ').replace(/\b\w/g, (ch) => ch.toUpperCase())

/** A role's display name: built-ins translated, a custom role shows the label
 *  it was given (else its title-cased slug). */
export function roleDisplayName(t: TFunction, slug: string, label?: string | null): string {
  const key = ROLE_LABEL_KEYS[slug as Role]
  return key ? t(key) : label || titleCase(slug)
}

/** Plural form for group headings: "Admins"; custom roles keep their label. */
export function rolePluralName(t: TFunction, slug: string, label?: string | null): string {
  const key = ROLE_PLURAL_KEYS[slug as Role]
  return key ? t(key) : label || titleCase(slug)
}

const ALL_CAPS = CAPABILITY_CATALOG.map((c) => c.capability)

/**
 * Static role→capability fallback (mirrors the backend seed in spec §3). Used
 * only when the cached session has no `capabilities` array. Superadmin gets
 * everything; admin gets all except the two superadmin-reserved caps; agent
 * gets the read/work subset.
 */
export const DEFAULT_ROLE_CAPS: Record<Role, string[]> = {
  superadmin: ALL_CAPS,
  admin: ALL_CAPS.filter(
    (c) => c !== 'rbac.manage' && c !== 'app_keys.manage' && c !== 'settings.ai_keys.manage' && c !== 'billing.manage',
  ),
  agent: [
    'contacts.view',
    'chats.view',
    'chats.send',
    'flows.view',
    'analytics.view',
    'call_logs.view',
    'activity.view',
    'tags.view',
    'ai.monitor',
  ],
}

const KEY = 'chatbranch-auth'
// Older key names, newest first — read so a rename migrates live sessions
// instead of logging everyone out ('chatwood-auth' = pre-ChatBranch,
// 'ai-collector-auth' = pre-ChatWood).
const LEGACY_KEYS = ['chatwood-auth', 'ai-collector-auth']
const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

export function getAuth(): AuthUser | null {
  if (typeof window === 'undefined') return null
  try {
    let raw = localStorage.getItem(KEY)
    for (const k of LEGACY_KEYS) {
      if (raw) break
      raw = localStorage.getItem(k)
    }
    if (!raw) return null
    const u = JSON.parse(raw) as AuthUser
    if ((u.role as string) === 'collector') u.role = 'agent' // pre-revert cached session
    // a session cached while Service Providers existed may still carry one
    delete (u as { resource_id?: unknown }).resource_id
    return u.token ? u : null
  } catch {
    return null
  }
}

export function setAuth(user: AuthUser): void {
  try {
    localStorage.setItem(KEY, JSON.stringify(user))
  } catch {
    /* private mode — session-only auth */
  }
}

/** Every way a session ends (sign-out, a 401 on a stale token) comes through
 *  here — so the assistant's and the AI playground's per-user browser caches
 *  go with it. */
export function clearAuth(): void {
  try {
    localStorage.removeItem(KEY)
    for (const k of LEGACY_KEYS) localStorage.removeItem(k)
  } catch {
    /* ignore */
  }
  clearAssistantCache()
  clearPlaygroundCache()
  if (typeof window !== 'undefined') window.dispatchEvent(new Event('auth-change'))
}

export function getRole(): Role {
  return getAuth()?.role ?? 'agent'
}

/**
 * Capabilities the given (or currently cached) user holds. Prefers the
 * server-resolved list; falls back to the static role default when absent.
 */
export function capabilitiesFor(user: AuthUser | null): string[] {
  if (!user) return []
  if (Array.isArray(user.capabilities) && user.capabilities.length > 0) return user.capabilities
  return DEFAULT_ROLE_CAPS[user.role] ?? []
}

/** True when `user` is granted `cap` (server list, else role fallback). */
export function userHasCap(user: AuthUser | null, cap: string): boolean {
  if (!user) return false
  return capabilitiesFor(user).includes(cap)
}

/**
 * Does the currently signed-in user hold `cap`? Reads the mounted auth, so call
 * it after mount (never in render on the server) — same rule as getRole().
 */
export function hasCap(cap: string): boolean {
  return userHasCap(getAuth(), cap)
}

/** Headers for role-checked API calls. */
export function authHeaders(): Record<string, string> {
  const auth = getAuth()
  return auth ? { Authorization: `Bearer ${auth.token}` } : {}
}

/** Tokens rotate on every login, so a 401 while we hold a token means the
 * stored session is stale — clear it and bounce to /login instead of letting
 * pages misreport the API as unreachable. Returns true when redirecting. */
export function expireSessionOn401(res: Response): boolean {
  if (res.status !== 401 || typeof window === 'undefined') return false
  if (!getAuth()) return false // never logged in — Shell's guard handles that
  clearAuth()
  window.location.href = '/login?expired=1'
  return true
}

/**
 * Best-effort capability fetch after login. The login response is preferred
 * (spec §3: it "must capture capabilities"); when it omits them we ask whoami.
 * Never throws — a backend that predates the matrix just leaves capabilities
 * undefined and the role fallback takes over.
 */
async function fetchIdentity(token: string): Promise<{ capabilities?: string[] }> {
  for (const path of ['auth/whoami', 'v1/whoami']) {
    try {
      const res = await fetch(`${API}/${path}`, {
        headers: { Authorization: `Bearer ${token}` },
      })
      if (!res.ok) continue
      const data = (await res.json().catch(() => ({}))) as { capabilities?: unknown }
      const caps = Array.isArray(data.capabilities)
        ? data.capabilities.filter((c): c is string => typeof c === 'string')
        : undefined
      if (caps) return { capabilities: caps }
    } catch {
      /* try the next endpoint */
    }
  }
  return {}
}

export async function login(username: string, password: string): Promise<AuthUser> {
  const res = await fetch(`${API}/auth/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ username, password }),
  })
  const data = await res.json().catch(() => ({}))
  if (!res.ok) throw new Error(data.detail || `HTTP ${res.status}`)
  const user: AuthUser = { ...data.user, token: data.token }
  // Capabilities may ride on the login response (user or top-level); otherwise
  // ask whoami so the gates are capability-driven from the first paint.
  const inline =
    (Array.isArray(data.user?.capabilities) && data.user.capabilities) ||
    (Array.isArray(data.capabilities) && data.capabilities) ||
    undefined
  const ident = await fetchIdentity(user.token)
  user.capabilities = inline || ident.capabilities
  // a fresh sign-in starts with no assistant cache — whoever used this browser
  // before, their chat stays on the server under their own login — and no
  // AI playground transcript
  clearAssistantCache()
  clearPlaygroundCache()
  setAuth(user)
  // listeners (the entity switcher, the shell) re-read the new session
  if (typeof window !== 'undefined') window.dispatchEvent(new Event('auth-change'))
  return user
}

/**
 * Re-read the signed-in user's capabilities from whoami and update the cached
 * session, so a session cached before the capability catalog changed (or
 * before an admin edited the matrix) still gates the UI correctly. The shell
 * calls it once per sign-in; listeners hear an 'auth-change' window event.
 */
export async function refreshIdentity(): Promise<AuthUser | null> {
  const user = getAuth()
  if (!user) return null
  const ident = await fetchIdentity(user.token)
  if (!ident.capabilities) return user
  const next: AuthUser = { ...user, capabilities: ident.capabilities }
  setAuth(next)
  if (typeof window !== 'undefined') window.dispatchEvent(new Event('auth-change'))
  return next
}

export async function logout(): Promise<void> {
  try {
    await fetch(`${API}/auth/logout`, { method: 'POST', headers: authHeaders() })
  } catch {
    /* server unreachable — clear locally anyway */
  }
  clearAuth()
}
