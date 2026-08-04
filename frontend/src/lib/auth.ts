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
 * API answers. `area`/`label` drive the Permissions page grouping + copy.
 */
export const CAPABILITY_CATALOG: { capability: string; area: string; label: string }[] = [
  { capability: 'contacts.view', area: 'Contacts', label: 'View contacts' },
  { capability: 'contacts.manage', area: 'Contacts', label: 'Create & edit contacts' },
  { capability: 'chats.view', area: 'Chats', label: 'View chats' },
  { capability: 'chats.send', area: 'Chats', label: 'Send messages' },
  { capability: 'chats.manage', area: 'Chats', label: 'Manage chats (assign, close)' },
  { capability: 'flows.view', area: 'Flows', label: 'View flows' },
  { capability: 'flows.manage', area: 'Flows', label: 'Build & edit flows' },
  { capability: 'channels.view', area: 'Channels', label: 'View channel accounts' },
  { capability: 'channels.manage', area: 'Channels', label: 'Manage channel accounts' },
  { capability: 'integrations.view', area: 'Integrations', label: 'View integrations' },
  { capability: 'integrations.manage', area: 'Integrations', label: 'Manage integrations' },
  { capability: 'users.view', area: 'Users', label: 'View users' },
  { capability: 'users.manage', area: 'Users', label: 'Create & manage users' },
  { capability: 'entities.view', area: 'Entities', label: 'View entities & tenants' },
  { capability: 'entities.manage', area: 'Entities', label: 'Manage entities & tenants' },
  { capability: 'templates.manage', area: 'Library', label: 'Manage message templates' },
  { capability: 'script_templates.manage', area: 'Library', label: 'Manage script templates' },
  { capability: 'notifications.manage', area: 'Review', label: 'Manage notification prefs' },
  { capability: 'analytics.view', area: 'Review', label: 'View analytics' },
  { capability: 'call_logs.view', area: 'Review', label: 'View call logs' },
  { capability: 'activity.view', area: 'Review', label: 'View activity log' },
  { capability: 'settings.ai.manage', area: 'Settings', label: 'Manage AI configuration' },
  { capability: 'account_assignment.manage', area: 'Settings', label: 'Manage account assignment' },
  { capability: 'app_keys.manage', area: 'Settings', label: 'Manage app keys' },
  { capability: 'pronunciation.manage', area: 'Settings', label: 'Manage pronunciation' },
  { capability: 'rbac.manage', area: 'Admin', label: 'Edit the permission matrix' },
]

const ALL_CAPS = CAPABILITY_CATALOG.map((c) => c.capability)

/**
 * Static role→capability fallback (mirrors the backend seed in spec §3). Used
 * only when the cached session has no `capabilities` array. Superadmin gets
 * everything; admin gets all except the two superadmin-reserved caps; agent
 * gets the read/work subset.
 */
export const DEFAULT_ROLE_CAPS: Record<Role, string[]> = {
  superadmin: ALL_CAPS,
  admin: ALL_CAPS.filter((c) => c !== 'rbac.manage' && c !== 'app_keys.manage'),
  agent: [
    'contacts.view',
    'chats.view',
    'chats.send',
    'flows.view',
    'analytics.view',
    'call_logs.view',
    'activity.view',
  ],
}

const KEY = 'chatbranch-auth'
// Older key names, newest first — read so a rename migrates live sessions
// instead of logging everyone out ('chatwood-auth' = pre-ChatBranch,
// 'ai-collector-auth' = pre-ChatWood).
const LEGACY_KEYS = ['chatwood-auth', 'ai-collector-auth']
const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8000/api'

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

export function clearAuth(): void {
  try {
    localStorage.removeItem(KEY)
    for (const k of LEGACY_KEYS) localStorage.removeItem(k)
  } catch {
    /* ignore */
  }
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
async function fetchCapabilities(token: string): Promise<string[] | undefined> {
  for (const path of ['auth/whoami', 'v1/whoami']) {
    try {
      const res = await fetch(`${API}/${path}`, {
        headers: { Authorization: `Bearer ${token}` },
      })
      if (!res.ok) continue
      const data = await res.json().catch(() => ({}))
      const caps = (data as { capabilities?: unknown }).capabilities
      if (Array.isArray(caps)) return caps.filter((c): c is string => typeof c === 'string')
    } catch {
      /* try the next endpoint */
    }
  }
  return undefined
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
  user.capabilities = inline || (await fetchCapabilities(user.token))
  setAuth(user)
  return user
}

export async function logout(): Promise<void> {
  try {
    await fetch(`${API}/auth/logout`, { method: 'POST', headers: authHeaders() })
  } catch {
    /* server unreachable — clear locally anyway */
  }
  clearAuth()
}
