/**
 * ChatBranch × Zernio (social media) — the wire types and calls the dashboard
 * uses: the Zernio key + webhook status (Integrations), connected social
 * accounts (Channels), social threads in the inbox (`social` on a thread) and
 * social posts (/social).
 *
 * Every field is optional on purpose: the backend may be older than this
 * screen (an endpoint that 404s) or Zernio may not be set up yet — callers ask
 * `isNotSetUp(e)` and show a friendly "Zernio isn't set up yet" state instead
 * of an error.
 */
import { API } from '@/lib/apiBase'
import { authHeaders, expireSessionOn401 } from '@/lib/auth'

/* ---------------------------------------------------------------- types */

export type SocialKind = 'comment' | 'review' | 'dm'

export interface SocialAuthor {
  name?: string | null
  handle?: string | null
  avatar?: string | null
}

export interface SocialPostRef {
  id?: string | null
  text?: string | null
  permalink?: string | null
  media_url?: string | null
}

/** `social` on a thread payload (omitted for non-social threads). */
export interface SocialMeta {
  kind: SocialKind
  platform?: string | null
  account_username?: string | null
  post?: SocialPostRef | null
  /** Reviews: 1..5 stars. */
  rating?: number | null
  /** Comments: hidden on the platform. */
  hidden?: boolean | null
  author?: SocialAuthor | null
}

/** One Facebook Page of a `facebook` account (GET /api/social/accounts → `pages`). */
export interface SocialPage {
  /** The Facebook Page id. */
  id: string
  /** The post target for it: "<account id>:<page id>". */
  target: string
  name?: string | null
  username?: string | null
  category?: string | null
  avatar?: string | null
  /** The Page's own outlet binding; null = it follows the account's level. */
  entity_id?: number | null
  /** Where it publishes: its own outlet, else the account's (null = organization). */
  effective_entity_id?: number | null
  entity_slug?: string | null
  entity_name?: string | null
  /** Zernio's selected Page — the one whose comments and messages reach the inbox. */
  selected?: boolean | null
}

/** GET /api/social/accounts row (also the answer of PATCH …/accounts/{id} and …/pages/{pageId}). */
export interface SocialAccount {
  /** The Zernio account id. */
  id: string
  platform: string
  /** The outlet it is bound to (null = organization level — every outlet may use it). */
  entity_id?: number | null
  entity_slug?: string | null
  entity_name?: string | null
  /** facebook only: the Pages it can post to (only those the current outlet view may use). */
  pages?: SocialPage[] | null
  username?: string | null
  display_name?: string | null
  avatar?: string | null
  profile_url?: string | null
  active?: boolean | null
  can_publish?: boolean | null
  can_comments?: boolean | null
  can_dm?: boolean | null
  can_reviews?: boolean | null
}

export type PostStatus = 'draft' | 'scheduled' | 'publishing' | 'published' | 'partial' | 'failed' | 'deleted'

export interface PostMedia {
  url: string
  type?: string | null
}

export interface PostResult {
  account_id?: string | null
  /** A Facebook Page target's page id. */
  page_id?: string | null
  platform?: string | null
  status?: string | null
  url?: string | null
  error?: string | null
}

export interface SocialPost {
  id: number | string
  entity_id?: number | null
  zernio_post_id?: string | null
  content: string
  media?: PostMedia[] | null
  account_ids?: string[] | null
  platforms?: string[] | null
  /** UTC; null = now. */
  scheduled_at?: string | null
  timezone?: string | null
  status: PostStatus | string
  error?: string | null
  results?: PostResult[] | null
  created_by?: string | null
  created_at?: string | null
  updated_at?: string | null
}

export interface ZernioStatus {
  configured?: boolean
  key_masked?: string | null
  webhook?: { registered?: boolean; url?: string | null; events?: string[] | null } | null
  profiles?: { entity_id?: number | null; entity_name?: string | null; profile_id?: string | null }[] | null
}

/* ----------------------------------------------------------- platforms */

export interface PlatformDef {
  id: string
  /** Brand name (never translated). */
  label: string
  /** Characters one post may carry there. */
  limit: number
}

/**
 * The networks Zernio connects, in the order the Connect buttons show them.
 * `limit` drives the composer's per-platform counter.
 */
export const PLATFORMS: PlatformDef[] = [
  { id: 'instagram', label: 'Instagram', limit: 2200 },
  { id: 'facebook', label: 'Facebook', limit: 63206 },
  { id: 'tiktok', label: 'TikTok', limit: 2200 },
  { id: 'youtube', label: 'YouTube', limit: 5000 },
  { id: 'linkedin', label: 'LinkedIn', limit: 3000 },
  { id: 'twitter', label: 'X', limit: 280 },
  { id: 'threads', label: 'Threads', limit: 500 },
  { id: 'pinterest', label: 'Pinterest', limit: 500 },
  { id: 'googlebusiness', label: 'Google Business', limit: 1500 },
  { id: 'bluesky', label: 'Bluesky', limit: 300 },
  { id: 'reddit', label: 'Reddit', limit: 40000 },
  { id: 'discord', label: 'Discord', limit: 2000 },
  { id: 'telegram', label: 'Telegram', limit: 4096 },
  { id: 'snapchat', label: 'Snapchat', limit: 250 },
]

const PLATFORM_BY_ID = new Map(PLATFORMS.map((p) => [p.id, p]))

/** Platform aliases a payload may use (`x`, `google_business`…) → our id. */
const PLATFORM_ALIAS: Record<string, string> = {
  x: 'twitter',
  google: 'googlebusiness',
  google_business: 'googlebusiness',
  gbp: 'googlebusiness',
}

export const platformId = (p: string | null | undefined): string => {
  const k = (p ?? '').toLowerCase()
  return PLATFORM_ALIAS[k] ?? k
}

export const platformDef = (p: string | null | undefined): PlatformDef | undefined => PLATFORM_BY_ID.get(platformId(p))

/** A platform's brand name ("X", "Google Business"); unknown ids title-cased. */
export const platformLabel = (p: string | null | undefined): string => {
  const d = platformDef(p)
  if (d) return d.label
  const k = (p ?? '').trim()
  return k ? k.charAt(0).toUpperCase() + k.slice(1) : '—'
}

/** "@username", or the display name when there is no handle. */
export const accountHandle = (a: Pick<SocialAccount, 'username' | 'display_name'>): string =>
  a.username ? (a.username.startsWith('@') ? a.username : `@${a.username}`) : a.display_name || '—'

/** The account part of a post target ("<account id>" or "<account id>:<page id>"). */
export const targetAccountId = (target: string): string => target.split(':')[0]

/** A post target's display name: the Facebook Page's name, else the account's handle. */
export function targetName(accounts: SocialAccount[], target: string, pageId?: string | null): string | null {
  const [acct, page = pageId ?? ''] = target.split(':')
  const a = accounts.find((x) => x.id === acct)
  if (!a) return null
  const p = page ? a.pages?.find((x) => x.id === page) : undefined
  return p?.name || accountHandle(a)
}

/* ---------------------------------------------------------------- calls */

/** An API refusal that keeps its status and body. */
export class SocialApiError extends Error {
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
  if (expireSessionOn401(res)) throw new SocialApiError('Session expired — redirecting to login…', 401)
  if (!res.ok) throw new SocialApiError((data.detail as string) || `HTTP ${res.status}`, res.status, data)
  return data as T
}

/** JSON call under `/api/` (path without the leading slash, e.g. `social/accounts`). */
export async function socialApi<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}/${path}`, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...authHeaders(), ...(init?.headers ?? {}) },
  })
  return parse<T>(res)
}

/** Multipart upload under `/api/` — the browser sets the boundary. */
export async function socialUpload<T>(path: string, form: FormData): Promise<T> {
  const res = await fetch(`${API}/${path}`, { method: 'POST', headers: { ...authHeaders() }, body: form })
  return parse<T>(res)
}

/**
 * Is this refusal "Zernio isn't set up" rather than a real error? A backend
 * without the endpoints (404 / 501), Zernio switched off (503), or a refusal
 * that says the key is missing.
 */
export function isNotSetUp(e: unknown): boolean {
  if (!(e instanceof SocialApiError)) return false
  if (e.status === 404 || e.status === 501 || e.status === 503) return true
  const code = String(e.body.code ?? '')
  if (/not_configured|zernio/i.test(code)) return true
  return (e.status === 400 || e.status === 409 || e.status === 424) && /zernio|not configured|api key/i.test(e.message)
}

export const errMessage = (e: unknown): string => (e instanceof Error ? e.message : String(e))

/** `?entity=` query suffix helper: '' → '', 'entity=a' → '?entity=a' (or '&…'). */
export const withQuery = (path: string, q: string) => (q ? `${path}${path.includes('?') ? '&' : '?'}${q}` : path)

/** The per-thread social action route (`hide`, `private-reply`). */
export const socialThreadPath = (key: string, action: string, threadId?: number | null) =>
  `social/threads/${encodeURIComponent(key)}/${action}${threadId ? `?thread_id=${threadId}` : ''}`
