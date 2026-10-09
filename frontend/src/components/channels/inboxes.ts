/**
 * The Inboxes model behind /channels (Chatwoot's "inboxes"): every number,
 * mailbox, bot, page, widget and the SIP trunk the team answers from, read
 * from the EXISTING account endpoints and folded into one card shape.
 *
 *   WhatsApp (Cloud + QR)   GET/POST /api/whatsapp/accounts, POST …/{id}
 *   Email / social          GET/POST /api/messaging/accounts, PATCH …/{id}
 *   Website widget          GET/POST /api/widgets, PATCH …/{id}
 *   Voice (SIP trunk)       GET/PUT  /api/whatsapp/voice-settings
 *
 * "Who answers it" (owning team + AI reply default) rides on every account
 * write as top-level `team_id` / `ai_default` keys.
 */

import { authHeaders, expireSessionOn401 } from '@/lib/auth'
import { accountRole, type AccountRole } from '@/components/ui/Badges'
import type { HealthStatus } from '@/lib/channelHealth'

export const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'
/** The API's own origin — webhook URLs and the widget script live there. */
export const API_ORIGIN = API.replace(/\/api\/?$/, '')

// ── kinds (the "Add a channel" blocks) ────────────────────────────────────────

export type InboxKind =
  | 'wa_cloud'
  | 'wa_qr'
  | 'email'
  | 'telegram'
  | 'messenger'
  | 'instagram'
  | 'website'
  | 'voice'

/** Filter buckets on the Connected section (both WhatsApp kinds share one). */
export type ChannelFilter = 'all' | 'whatsapp' | 'email' | 'telegram' | 'messenger' | 'instagram' | 'website' | 'voice'

export interface KindMeta {
  kind: InboxKind
  /** ChannelLogo id. */
  logo: string
  label: string
  /** One-line description on the block. */
  blurb: string
  /** Shown on cards: "WhatsApp Cloud API", "Email (SMTP)"… */
  channelLabel: string
  filter: Exclude<ChannelFilter, 'all'>
  badge?: 'Beta' | 'Demo'
  /** Step 2's title — what "connect" means for this channel. */
  connectTitle: string
}

export const KINDS: KindMeta[] = [
  {
    kind: 'wa_cloud', logo: 'whatsapp', label: 'WhatsApp Business',
    blurb: 'Official Cloud API · templates · compliant', channelLabel: 'WhatsApp Cloud API',
    filter: 'whatsapp', connectTitle: 'Connect the number',
  },
  {
    kind: 'wa_qr', logo: 'whatsapp', label: 'WhatsApp by QR',
    blurb: 'Pair a personal number · demo only', channelLabel: 'WhatsApp by QR',
    filter: 'whatsapp', badge: 'Demo', connectTitle: 'Before you pair',
  },
  {
    kind: 'email', logo: 'email', label: 'Email',
    blurb: 'A mailbox · SMTP out, IMAP in', channelLabel: 'Email',
    filter: 'email', connectTitle: 'Connect the mailbox',
  },
  {
    kind: 'telegram', logo: 'telegram', label: 'Telegram',
    blurb: 'A bot from @BotFather', channelLabel: 'Telegram bot',
    filter: 'telegram', connectTitle: 'Connect the bot',
  },
  {
    kind: 'messenger', logo: 'messenger', label: 'Messenger',
    blurb: 'Facebook page messages', channelLabel: 'Messenger',
    filter: 'messenger', badge: 'Beta', connectTitle: 'Connect the page',
  },
  {
    kind: 'instagram', logo: 'instagram', label: 'Instagram',
    blurb: 'Business account DMs', channelLabel: 'Instagram',
    filter: 'instagram', badge: 'Beta', connectTitle: 'Connect the account',
  },
  {
    kind: 'website', logo: 'website', label: 'Website chat',
    blurb: 'A chat bubble you paste into your site', channelLabel: 'Website chat',
    filter: 'website', connectTitle: 'Design the widget',
  },
  {
    kind: 'voice', logo: 'voice', label: 'Voice (SIP)',
    blurb: 'AI phone calls over a Twilio SIP trunk', channelLabel: 'Voice (SIP trunk)',
    filter: 'voice', connectTitle: 'Connect the trunk',
  },
]

export const kindMeta = (k: InboxKind): KindMeta => KINDS.find((x) => x.kind === k) ?? KINDS[0]

export const FILTERS: { id: ChannelFilter; label: string; logo?: string }[] = [
  { id: 'all', label: 'All' },
  { id: 'whatsapp', label: 'WhatsApp', logo: 'whatsapp' },
  { id: 'email', label: 'Email', logo: 'email' },
  { id: 'telegram', label: 'Telegram', logo: 'telegram' },
  { id: 'messenger', label: 'Messenger', logo: 'messenger' },
  { id: 'instagram', label: 'Instagram', logo: 'instagram' },
  { id: 'website', label: 'Website', logo: 'website' },
  { id: 'voice', label: 'Voice', logo: 'voice' },
]

// ── wire types (exactly what the endpoints return) ───────────────────────────

/** Keys mirror ChannelAccount.config on the backend (AccountDelivery.Keys). */
export interface AccountDelivery {
  quiet_start: string
  quiet_end: string
  /** JSON per-weekday schedule; "" = use the single quiet_start/end window. */
  quiet_schedule: string
  queue_min_gap_s: string
  queue_max_gap_s: string
  llm_provider: string
  stt_provider: string
}

export const EMPTY_DELIVERY: AccountDelivery = {
  quiet_start: '',
  quiet_end: '',
  quiet_schedule: '',
  queue_min_gap_s: '',
  queue_max_gap_s: '',
  llm_provider: '',
  stt_provider: '',
}

export type AiDefault = '' | 'on' | 'off'

export interface WaAccount {
  id: string
  provider: 'cloud' | 'baileys'
  name: string
  phone_number_id: string
  waba_id?: string
  api_version?: string
  owner?: string
  access_token_masked: string
  app_secret_masked: string
  configured: boolean
  env_managed: boolean
  role?: AccountRole
  entity?: string | null
  entity_name?: string
  health_status?: string
  delivery?: AccountDelivery
  team_id?: number | null
  ai_default?: AiDefault
  ai_review_mode?: ReviewMode
}

export interface MessagingAccount {
  id: string
  channel: 'email' | 'facebook' | 'instagram' | 'telegram'
  name: string
  external_id?: string
  owner?: string
  entity?: string | null
  entity_name?: string
  access_token_masked?: string
  /** Messenger / Instagram only. */
  app_secret_masked?: string
  configured?: boolean
  team_id?: number | null
  ai_default?: AiDefault
  ai_review_mode?: ReviewMode
  config?: {
    host?: string
    port?: number | string
    username?: string
    use_tls?: boolean
    from_email?: string
    password?: string
    password_masked?: string
    /** inbound — blank = the default IMAP host + this mailbox's SMTP login */
    imap_host?: string
    imap_port?: number | string
  }
}

export interface Widget {
  id: number
  name: string
  token: string
  entity: string | null
  entity_name: string
  title: string
  greeting: string
  accent: string
  placeholder: string
  conversations: number
  embed: string
  script_url: string
  team_id?: number | null
  ai_default?: AiDefault
  ai_review_mode?: ReviewMode
}

export interface VoiceSettings {
  sip_username: string
  sip_password_masked: string
  termination_uri: string
  /** Per-field origin of the effective value: dashboard row / .env / unset. */
  source: Record<'sip_username' | 'sip_password' | 'termination_uri', 'settings' | 'env' | ''>
  sip_trunk_configured: boolean
  health: { configured: boolean; health: HealthStatus; detail: string; live_check: boolean }
}

export interface Team {
  id: number
  name: string
  auto_assign?: boolean
  entity?: string | null
}

// ── the card shape ───────────────────────────────────────────────────────────

/** Who checks the AI before it sends on this inbox; '' = inherit the AI monitor setting. */
export type ReviewMode = '' | 'auto' | 'review_flagged' | 'review_all' | 'shadow'

export interface Inbox {
  /** Unique across kinds, e.g. "wa:3", "msg:7", "widget:2", "voice". */
  key: string
  kind: InboxKind
  /** The id the account endpoints take ("voice" for the trunk singleton). */
  id: string
  name: string
  address: string
  entity: string | null
  entityName: string
  teamId: number | null
  aiDefault: AiDefault
  reviewMode: ReviewMode
  role?: AccountRole
  configured: boolean
  /** Read-only rows (the backend/.env WhatsApp account). */
  envManaged?: boolean
  /** Health-monitor key: the account id the health payload reports. */
  healthId?: string
  wa?: WaAccount
  msg?: MessagingAccount
  widget?: Widget
  voice?: VoiceSettings
}

const SOCIAL_KIND: Record<string, InboxKind> = {
  facebook: 'messenger',
  instagram: 'instagram',
  telegram: 'telegram',
  email: 'email',
}

export function fromWa(a: WaAccount): Inbox {
  const qr = a.provider === 'baileys'
  return {
    key: `wa:${a.id}`,
    kind: qr ? 'wa_qr' : 'wa_cloud',
    id: a.id,
    name: a.name,
    address: qr
      ? a.configured
        ? 'QR-paired personal number'
        : 'Not paired yet'
      : a.phone_number_id
        ? `Number ID ${a.phone_number_id}`
        : 'Number ID not set',
    entity: a.entity ?? null,
    entityName: a.entity_name ?? '',
    teamId: a.team_id ?? null,
    aiDefault: a.ai_default ?? '',
    reviewMode: a.ai_review_mode ?? '',
    role: accountRole(a),
    configured: a.configured,
    envManaged: a.env_managed,
    healthId: a.env_managed ? undefined : a.id,
    wa: a,
  }
}

export function fromMessaging(a: MessagingAccount): Inbox {
  const kind = SOCIAL_KIND[a.channel] ?? 'telegram'
  const address =
    kind === 'email'
      ? a.config?.from_email || a.external_id || 'From address not set'
      : kind === 'telegram'
        ? a.external_id
          ? `Bot ${a.external_id}`
          : 'Bot token linked'
        : a.external_id
          ? `${kind === 'messenger' ? 'Page' : 'Account'} ${a.external_id}`
          : 'ID not set'
  return {
    key: `msg:${a.id}`,
    kind,
    id: a.id,
    name: a.name,
    address,
    entity: a.entity ?? null,
    entityName: a.entity_name ?? '',
    teamId: a.team_id ?? null,
    aiDefault: a.ai_default ?? '',
    reviewMode: a.ai_review_mode ?? '',
    configured: !!a.configured,
    healthId: a.id,
    msg: a,
  }
}

export function fromWidget(w: Widget): Inbox {
  return {
    key: `widget:${w.id}`,
    kind: 'website',
    id: String(w.id),
    name: w.name,
    address: `${w.conversations} conversation${w.conversations === 1 ? '' : 's'} · token ${w.token.slice(0, 8)}…`,
    entity: w.entity ?? null,
    entityName: w.entity_name ?? '',
    teamId: w.team_id ?? null,
    aiDefault: w.ai_default ?? '',
    reviewMode: w.ai_review_mode ?? '',
    configured: true,
    healthId: String(w.id),
    widget: w,
  }
}

export function fromVoice(v: VoiceSettings): Inbox | null {
  // Only a trunk that is actually set up is an inbox; otherwise it is a block.
  if (!v.sip_trunk_configured && !v.sip_username && !v.termination_uri) return null
  return {
    key: 'voice',
    kind: 'voice',
    id: 'voice',
    name: 'Voice trunk',
    address: v.termination_uri || 'Termination URI not set',
    entity: null,
    entityName: '',
    teamId: null,
    aiDefault: '',
    reviewMode: '',
    configured: v.health.configured,
    voice: v,
  }
}

// ── fetch helpers ────────────────────────────────────────────────────────────

/** Error that keeps the HTTP status (a 501 "not wired up yet" is information, not failure). */
export class ApiError extends Error {
  status: number
  constructor(status: number, message: string) {
    super(message)
    this.status = status
  }
}

/** JSON call against the API root (`path` without the leading /api/). */
export async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}/${path}`, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...authHeaders(), ...(init?.headers ?? {}) },
  })
  const data = await res.json().catch(() => ({}))
  if (expireSessionOn401(res)) throw new ApiError(401, 'Session expired — redirecting to login…')
  if (!res.ok) {
    const d = data as { detail?: string; error?: { message?: string } }
    throw new ApiError(res.status, d.detail || d.error?.message || `HTTP ${res.status}`)
  }
  return data as T
}

export const errText = (e: unknown) => (e instanceof Error ? e.message : String(e))

/** Outcome of a Test run — `info` = honest "can't prove it from here". */
export interface TestResult {
  tone: 'ok' | 'info' | 'error'
  text: string
}

/** Run the channel's own test endpoint and phrase the answer for a human. */
export async function testInbox(inbox: Inbox): Promise<TestResult> {
  try {
    if (inbox.kind === 'wa_cloud' || inbox.kind === 'wa_qr') {
      const r = await api<{ ok: boolean; display_phone_number: string; verified_name: string; quality_rating: string }>(
        `whatsapp/accounts/${inbox.id}/test`,
        { method: 'POST' },
      )
      if (inbox.kind === 'wa_qr')
        return r.ok
          ? { tone: 'ok', text: `Paired and connected${r.display_phone_number ? ` · ${r.display_phone_number}` : ''}.` }
          : { tone: 'error', text: `Not connected (${r.quality_rating}) — pair it by scanning the QR.` }
      return r.ok
        ? {
            tone: 'ok',
            text: `Credentials present for ${r.verified_name || inbox.name}${
              r.display_phone_number ? ` · ${r.display_phone_number}` : ''
            }.`,
          }
        : { tone: 'error', text: `Account is ${r.quality_rating} — check the token.` }
    }
    if (inbox.kind === 'website') {
      // The public config endpoint is exactly what the embed calls first.
      const res = await fetch(`${API}/widget/${encodeURIComponent(inbox.widget?.token ?? '')}/config`)
      if (!res.ok) return { tone: 'error', text: `The widget did not answer (HTTP ${res.status}).` }
      const cfg = (await res.json()) as { title?: string }
      return { tone: 'ok', text: `Widget answers — it opens as “${cfg.title || inbox.name}”.` }
    }
    if (inbox.kind === 'voice') {
      const r = await api<{ ok?: boolean; detail?: string }>('whatsapp/voice-settings/test', { method: 'POST' })
      return { tone: r.ok ? 'ok' : 'info', text: r.detail || 'SIP trunk creds are set.' }
    }
    const r = await api<{ ok?: boolean; name?: string; id?: string; detail?: string }>(
      `messaging/accounts/${inbox.id}/test`,
      { method: 'POST', body: JSON.stringify({}) },
    )
    return { tone: 'ok', text: r.detail || `Connected${r.name ? ` as ${r.name}` : ''}.` }
  } catch (e) {
    // 501 = the backend can't place a live probe for this provider yet; it
    // still says whether the stored config looks complete.
    if (e instanceof ApiError && e.status === 501) return { tone: 'info', text: e.message }
    return { tone: 'error', text: errText(e) }
  }
}

export interface InboxesSnapshot {
  inboxes: Inbox[]
  teams: Team[]
  voice: VoiceSettings | null
  verifyToken: string
  /** Which sources failed to load (the rest still render). */
  failed: string[]
}

/** Load every inbox source independently — one being down never blanks the page. */
export async function loadInboxes(): Promise<InboxesSnapshot> {
  const [wa, msg, widgets, voice, teams, settings] = await Promise.allSettled([
    api<{ accounts: WaAccount[] }>('whatsapp/accounts'),
    api<{ accounts: MessagingAccount[] }>('messaging/accounts'),
    api<{ widgets: Widget[] }>('widgets'),
    api<VoiceSettings>('whatsapp/voice-settings'),
    api<{ teams: Team[] }>('teams'),
    api<{ verify_token: string }>('whatsapp/settings'),
  ])
  const failed: string[] = []
  const inboxes: Inbox[] = []
  if (wa.status === 'fulfilled') inboxes.push(...(wa.value.accounts ?? []).map(fromWa))
  else failed.push('WhatsApp')
  if (msg.status === 'fulfilled')
    inboxes.push(...(msg.value.accounts ?? []).filter((a) => SOCIAL_KIND[a.channel]).map(fromMessaging))
  else failed.push('Email & social')
  if (widgets.status === 'fulfilled') inboxes.push(...(widgets.value.widgets ?? []).map(fromWidget))
  else failed.push('Website chat')
  const v = voice.status === 'fulfilled' ? voice.value : null
  if (v) {
    const vi = fromVoice(v)
    if (vi) inboxes.push(vi)
  } else failed.push('Voice')
  return {
    inboxes,
    teams: teams.status === 'fulfilled' ? teams.value.teams ?? [] : [],
    voice: v,
    verifyToken: settings.status === 'fulfilled' ? settings.value.verify_token : '',
    failed,
  }
}

/** Remove an inbox through its own endpoint. */
export async function deleteInbox(inbox: Inbox): Promise<void> {
  if (inbox.kind === 'wa_cloud' || inbox.kind === 'wa_qr') {
    // best-effort unpair first — the account row goes regardless
    if (inbox.kind === 'wa_qr')
      await api(`whatsapp/accounts/${inbox.id}/baileys/logout`, { method: 'POST' }).catch(() => {})
    await api(`whatsapp/accounts/${inbox.id}`, { method: 'DELETE' })
    return
  }
  if (inbox.kind === 'website') {
    await api(`widgets/${inbox.id}`, { method: 'DELETE' })
    return
  }
  if (inbox.kind === 'voice') {
    // the trunk is a singleton setting: removing it clears the dashboard creds
    // (any backend/.env fallback stays in force)
    await api('whatsapp/voice-settings', {
      method: 'PUT',
      body: JSON.stringify({ sip_username: '', sip_password: '', termination_uri: '' }),
    })
    return
  }
  await api(`messaging/accounts/${inbox.id}`, { method: 'DELETE' })
}
