/**
 * Managed connections (Zernio behind them; users never see that name) — the wire
 * types and calls the dashboard uses: the Zernio key + webhook status (Settings →
 * App & provider keys) and the managed messaging accounts on /channels (WhatsApp,
 * Messenger, Instagram DM, Telegram, SMS). ChatBranch has no social-media posting,
 * comments or reviews: out of scope.
 *
 * Every field is optional on purpose: the backend may be older than this screen
 * (an endpoint that 404s) or the managed connection may not be set up yet —
 * callers ask `isNotSetUp(e)` and show a friendly "not set up yet" state instead
 * of an error. The routes keep their historical `/api/social/*` path.
 */
import { API } from '@/lib/apiBase'
import { authHeaders, expireSessionOn401 } from '@/lib/auth'

/* ---------------------------------------------------------------- types */

/** One Facebook Page of a managed Messenger account (GET /api/social/accounts → `pages`). */
export interface ManagedPage {
  /** The Facebook Page id. */
  id: string
  name?: string | null
  username?: string | null
  category?: string | null
  avatar?: string | null
  /** The Page whose Messenger conversations reach the inbox. */
  selected?: boolean | null
}

/** GET /api/social/accounts row (also the answer of PATCH …/accounts/{id} and …/pages/{pageId}/select). */
export interface ManagedAccount {
  /** The managed connection's account id. */
  id: string
  /** The inbox's ChatBranch channel (whatsapp, facebook = Messenger, instagram, telegram, sms). */
  channel?: string | null
  platform: string
  /** The outlet it is bound to (null = organization level — every outlet may use it). */
  entity_id?: number | null
  entity_slug?: string | null
  entity_name?: string | null
  /** Messenger only: the account's Facebook Pages. */
  pages?: ManagedPage[] | null
  username?: string | null
  display_name?: string | null
  /** What customers see: the number, the @username, the Page name. */
  address?: string | null
  avatar?: string | null
  active?: boolean | null
}

export interface ZernioStatus {
  configured?: boolean
  key_masked?: string | null
  webhook?: { registered?: boolean; url?: string | null; events?: string[] | null } | null
  profiles?: { entity_id?: number | null; entity_name?: string | null; profile_id?: string | null }[] | null
}

/* ----------------------------------------------------------- platforms */

/** Platform aliases a payload may use → our id. */
const PLATFORM_ALIAS: Record<string, string> = { messenger: 'facebook', ig: 'instagram' }

export const platformId = (p: string | null | undefined): string => {
  const k = (p ?? '').toLowerCase()
  return PLATFORM_ALIAS[k] ?? k
}

const LABELS: Record<string, string> = {
  whatsapp: 'WhatsApp',
  facebook: 'Messenger',
  instagram: 'Instagram',
  telegram: 'Telegram',
  sms: 'SMS',
}

/** A platform's brand name; unknown ids title-cased. */
export const platformLabel = (p: string | null | undefined): string => {
  const k = platformId(p)
  if (LABELS[k]) return LABELS[k]
  const raw = (p ?? '').trim()
  return raw ? raw.charAt(0).toUpperCase() + raw.slice(1) : '—'
}

/** "@username", the number, or the display name. */
export const accountHandle = (a: Pick<ManagedAccount, 'username' | 'display_name' | 'address'>): string =>
  a.address || (a.username ? (a.username.startsWith('@') ? a.username : `@${a.username}`) : a.display_name || '—')

/* ---------------------------------------------------------------- calls */

/** An API refusal that keeps its status and body. */
export class ZernioApiError extends Error {
  constructor(
    message: string,
    public status: number,
    public body: Record<string, unknown> = {},
  ) {
    super(message)
  }
}

async function parse<T>(res: Response): Promise<T> {
  const data = (await res.json().catch(() => ({}))) as Record<string, unknown>
  if (expireSessionOn401(res)) throw new ZernioApiError('Session expired — redirecting to login…', 401)
  if (!res.ok) throw new ZernioApiError((data.detail as string) || `HTTP ${res.status}`, res.status, data)
  return data as T
}

/** JSON call under `/api/` (path without the leading slash, e.g. `social/accounts`). */
export async function zernioApi<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}/${path}`, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...authHeaders(), ...(init?.headers ?? {}) },
  })
  return parse<T>(res)
}

/**
 * Is this refusal "the managed connection isn't set up" rather than a real error? A
 * backend without the endpoints (404 / 501), switched off (503), or a refusal that
 * says the key is missing.
 */
export function isNotSetUp(e: unknown): boolean {
  if (!(e instanceof ZernioApiError)) return false
  if (e.status === 404 || e.status === 501 || e.status === 503) return true
  const code = String(e.body.code ?? '')
  if (/not_configured|zernio/i.test(code)) return true
  return (e.status === 400 || e.status === 409 || e.status === 424) && /zernio|not configured|api key/i.test(e.message)
}

export const errMessage = (e: unknown): string => (e instanceof Error ? e.message : String(e))

/** `?entity=` query suffix helper: '' → '', 'entity=a' → '?entity=a' (or '&…'). */
export const withQuery = (path: string, q: string) => (q ? `${path}${path.includes('?') ? '&' : '?'}${q}` : path)
