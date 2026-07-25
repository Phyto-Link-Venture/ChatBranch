/**
 * Real RBAC client: login via /api/auth/login, bearer token + user cached in
 * localStorage, attached to every role-checked API call. Roles:
 *   superadmin — everything incl. app keys
 *   admin      — queues + channel account management (own tenant)
 *   agent      — queues only, scoped to their assigned brands
 * ("agent" was renamed to "collector" in an earlier brands RBAC pass, then
 * reverted back to "agent" — cached sessions holding the old "collector"
 * name are normalized on read.)
 */
export type Role = 'superadmin' | 'admin' | 'agent'

export interface AuthUser {
  username: string
  name: string
  role: Role
  token: string
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

export async function login(username: string, password: string): Promise<AuthUser> {
  const res = await fetch(`${API}/auth/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ username, password }),
  })
  const data = await res.json().catch(() => ({}))
  if (!res.ok) throw new Error(data.detail || `HTTP ${res.status}`)
  const user: AuthUser = { ...data.user, token: data.token }
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
