/**
 * The Inboxes model behind /channels (Chatwoot's "inboxes"): every number,
 * mailbox, bot, page, widget and the SIP trunk the team answers from, read
 * from the EXISTING account endpoints and folded into one card shape.
 *
 *   WhatsApp (Cloud + QR)   GET/POST /api/whatsapp/accounts, POST …/{id}
 *   Email / social          GET/POST /api/messaging/accounts, PATCH …/{id}
 *   (also LINE, Viber, SMS via Twilio, WeChat, TikTok, Douyin and Zalo — each
 *   with its own signed webhook at /api/messaging/webhook/{channel}/{id})
 *   Website widget          GET/POST /api/widgets, PATCH …/{id}
 *   Voice (SIP trunk)       GET/PUT  /api/whatsapp/voice-settings
 *
 * "Who answers it" (owning team + AI reply default) rides on every account
 * write as top-level `team_id` / `ai_default` keys.
 *
 * Display texts are translation keys (`channels.*`), resolved by the
 * components with `t()` — nothing here knows the language.
 */

import { API } from '@/lib/apiBase'
import { authHeaders, expireSessionOn401 } from '@/lib/auth'
import { accountRole, type AccountRole } from '@/components/ui/Badges'
import type { HealthStatus } from '@/lib/channelHealth'
import type { MessageKey, TFunction, Vars } from '@/i18n'
import type { channels as channelsEn } from '@/i18n/messages/en/channels'

export { API }

/** A translatable text built outside a component: a key and its placeholders. */
export interface Msg {
  k: MessageKey
  v?: Vars
}

/** Resolve plain text (data) or a `Msg` (UI copy) in the current language. */
export const txt = (m: string | Msg, t: TFunction): string => (typeof m === 'string' ? m : t(m.k, m.v))

// ── kinds (the "Add a channel" blocks) ────────────────────────────────────────

export type InboxKind =
  | 'wa_cloud'
  | 'wa_qr'
  | 'wa_ycloud'
  | 'wa_twilio'
  | 'email'
  | 'telegram'
  | 'messenger'
  | 'instagram'
  | 'website'
  | 'voice'
  | 'line'
  | 'viber'
  | 'sms'
  | 'wechat'
  | 'tiktok'
  | 'douyin'
  | 'zalo'

/** The inboxes whose channel tells us when a customer deletes ("unsends") a message —
 *  they offer "Keep messages the customer deletes" (Channels/MessageRecalls.cs). */
export const RECALL_KINDS: readonly InboxKind[] = ['wa_qr', 'line', 'messenger', 'instagram']

/** The channels added with their own transports — their kind IS their channel id. */
export const MORE_KINDS = ['line', 'viber', 'sms', 'wechat', 'tiktok', 'douyin', 'zalo'] as const
export type MoreKind = (typeof MORE_KINDS)[number]
export const isMoreKind = (k: InboxKind | null | undefined): k is MoreKind =>
  !!k && (MORE_KINDS as readonly string[]).includes(k)

/** Every WhatsApp kind: Meta's Cloud API, a QR-paired number, and the two official BSPs. */
export const WA_KINDS = ['wa_cloud', 'wa_qr', 'wa_ycloud', 'wa_twilio'] as const
export type WaKind = (typeof WA_KINDS)[number]
export const isWaKind = (k: InboxKind | null | undefined): k is WaKind =>
  !!k && (WA_KINDS as readonly string[]).includes(k)

/** WhatsApp through a Business Solution Provider: its own signed webhook per account. */
export const isBspKind = (k: InboxKind | null | undefined): k is 'wa_ycloud' | 'wa_twilio' =>
  k === 'wa_ycloud' || k === 'wa_twilio'

/** The API's `provider` for each WhatsApp kind. */
export const WA_PROVIDER: Record<WaKind, WaProvider> = {
  wa_cloud: 'cloud',
  wa_qr: 'baileys',
  wa_ycloud: 'ycloud',
  wa_twilio: 'twilio',
}

/** Filter buckets on the Connected section (every WhatsApp kind shares one). */
export type ChannelFilter =
  | 'all'
  | 'whatsapp'
  | 'email'
  | 'telegram'
  | 'messenger'
  | 'instagram'
  | 'website'
  | 'voice'
  | MoreKind

/** How the "Add a channel" blocks are grouped. */
export type KindGroup = 'messaging' | 'social' | 'sms_voice' | 'china'

/** `label` / `hint` are translation keys. */
export const KIND_GROUPS: { id: KindGroup; label: MessageKey; hint: MessageKey }[] = [
  { id: 'messaging', label: 'channels.groups.messaging', hint: 'channels.groups.messagingHint' },
  { id: 'social', label: 'channels.groups.social', hint: 'channels.groups.socialHint' },
  { id: 'sms_voice', label: 'channels.groups.smsVoice', hint: 'channels.groups.smsVoiceHint' },
  { id: 'china', label: 'channels.groups.china', hint: 'channels.groups.chinaHint' },
]

export interface KindMeta {
  kind: InboxKind
  /** ChannelLogo id. */
  logo: string
  /** Translation keys: the block's name… */
  label: MessageKey
  /** …its one-line description… */
  blurb: MessageKey
  /** …what cards call it ("WhatsApp Cloud API", "Email (SMTP)"…)… */
  channelLabel: MessageKey
  filter: Exclude<ChannelFilter, 'all'>
  badge?: 'Beta' | 'Demo'
  /** …and step 2's title — what "connect" means for this channel. */
  connectTitle: MessageKey
  /** Which "Add a channel" group the block sits in. */
  group: KindGroup
  /** The platform must approve the app before its API works (TikTok, Douyin). */
  approval?: boolean
}

type KindId = keyof (typeof channelsEn)['kinds']

/** The four texts of one kind, under `channels.kinds.<id>`. */
const texts = (id: KindId): Pick<KindMeta, 'label' | 'blurb' | 'channelLabel' | 'connectTitle'> => ({
  label: `channels.kinds.${id}.label`,
  blurb: `channels.kinds.${id}.blurb`,
  channelLabel: `channels.kinds.${id}.channelLabel`,
  connectTitle: `channels.kinds.${id}.connectTitle`,
})

export const KINDS: KindMeta[] = [
  { kind: 'wa_cloud', logo: 'whatsapp', ...texts('waCloud'), filter: 'whatsapp', group: 'messaging' },
  { kind: 'wa_ycloud', logo: 'whatsapp', ...texts('waYcloud'), filter: 'whatsapp', group: 'messaging' },
  { kind: 'wa_twilio', logo: 'whatsapp', ...texts('waTwilio'), filter: 'whatsapp', group: 'messaging' },
  { kind: 'wa_qr', logo: 'whatsapp', ...texts('waQr'), filter: 'whatsapp', badge: 'Demo', group: 'messaging' },
  { kind: 'email', logo: 'email', ...texts('email'), filter: 'email', group: 'sms_voice' },
  { kind: 'telegram', logo: 'telegram', ...texts('telegram'), filter: 'telegram', group: 'messaging' },
  { kind: 'line', logo: 'line', ...texts('line'), filter: 'line', group: 'messaging' },
  { kind: 'viber', logo: 'viber', ...texts('viber'), filter: 'viber', group: 'messaging' },
  { kind: 'zalo', logo: 'zalo', ...texts('zalo'), filter: 'zalo', group: 'messaging' },
  { kind: 'messenger', logo: 'messenger', ...texts('messenger'), filter: 'messenger', group: 'social' },
  { kind: 'instagram', logo: 'instagram', ...texts('instagram'), filter: 'instagram', group: 'social' },
  {
    kind: 'tiktok', logo: 'tiktok', ...texts('tiktok'),
    filter: 'tiktok', badge: 'Beta', group: 'social', approval: true,
  },
  { kind: 'website', logo: 'website', ...texts('website'), filter: 'website', group: 'messaging' },
  { kind: 'sms', logo: 'sms', ...texts('sms'), filter: 'sms', group: 'sms_voice' },
  { kind: 'voice', logo: 'voice', ...texts('voice'), filter: 'voice', group: 'sms_voice' },
  { kind: 'wechat', logo: 'wechat', ...texts('wechat'), filter: 'wechat', group: 'china' },
  {
    kind: 'douyin', logo: 'douyin', ...texts('douyin'),
    filter: 'douyin', badge: 'Beta', group: 'china', approval: true,
  },
]

export const kindMeta = (k: InboxKind): KindMeta => KINDS.find((x) => x.kind === k) ?? KINDS[0]

/** A filter chip: `label` is a translation key; a brand name stays as it is. */
export interface FilterDef {
  id: ChannelFilter
  label?: MessageKey
  brand?: string
  logo?: string
}

export const FILTERS: FilterDef[] = [
  { id: 'all', label: 'common.status.all' },
  { id: 'whatsapp', brand: 'WhatsApp', logo: 'whatsapp' },
  { id: 'email', label: 'common.fields.email', logo: 'email' },
  { id: 'telegram', brand: 'Telegram', logo: 'telegram' },
  { id: 'messenger', brand: 'Messenger', logo: 'messenger' },
  { id: 'instagram', brand: 'Instagram', logo: 'instagram' },
  { id: 'website', label: 'channels.filters.website', logo: 'website' },
  { id: 'voice', label: 'channels.filters.voice', logo: 'voice' },
  { id: 'line', brand: 'LINE', logo: 'line' },
  { id: 'viber', brand: 'Viber', logo: 'viber' },
  { id: 'zalo', brand: 'Zalo', logo: 'zalo' },
  { id: 'sms', brand: 'SMS', logo: 'sms' },
  { id: 'tiktok', brand: 'TikTok', logo: 'tiktok' },
  { id: 'wechat', brand: 'WeChat', logo: 'wechat' },
  { id: 'douyin', brand: 'Douyin', logo: 'douyin' },
]

export const filterLabel = (f: FilterDef, t: TFunction): string => (f.label ? t(f.label) : f.brand ?? f.id)

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

/** cloud = Meta's Cloud API · baileys = QR-paired (unofficial) · ycloud / twilio = official BSPs. */
export type WaProvider = 'cloud' | 'baileys' | 'ycloud' | 'twilio'

/** A YCloud / Twilio account's own settings (secrets come back masked). */
export interface WaBspSettings {
  phone_number?: string
  api_key_masked?: string
  webhook_secret_masked?: string
  account_sid?: string
  from_number?: string
  messaging_service_sid?: string
  api_key_sid?: string
  api_key_secret_masked?: string
  auth_token_masked?: string
}

export interface WaAccount {
  id: string
  provider: WaProvider
  /** YCloud / Twilio: this account's own signed webhook path. */
  webhook_path?: string | null
  bsp?: WaBspSettings | null
  /** Cloud API / YCloud / Twilio: template-capable and bound by the 24h window. */
  official?: boolean
  /** A live template registry is behind this number (Meta's / YCloud's WABA, Twilio's Content API). */
  template_registry?: boolean
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
  /** QR numbers: offline between sends so the phone keeps WhatsApp's notifications */
  keep_phone_notifications?: boolean
  /** A message the customer deletes for everyone stays readable (true) or is erased (false). */
  keep_recalled?: boolean
  /** The channel tells us about deleted messages (QR numbers). */
  recalls_supported?: boolean
}

export interface MessagingAccount {
  id: string
  channel: 'email' | 'facebook' | 'instagram' | 'telegram' | MoreKind
  name: string
  external_id?: string
  owner?: string
  entity?: string | null
  entity_name?: string
  access_token_masked?: string
  /** Messenger / Instagram, and the app / channel / client secret of the newer channels. */
  app_secret_masked?: string
  configured?: boolean
  /** LINE, Viber, SMS, WeChat, TikTok, Douyin, Zalo: this account's own signed webhook path. */
  webhook_path?: string
  /** TikTok / Douyin: the platform must approve the app first. */
  requires_approval?: boolean
  /** WeChat 48 h, Zalo 168 h, TikTok / Douyin 48 h — null where the platform has no window. */
  window_hours?: number | null
  /** LINE and Viber register their webhook through the API (the rest are pasted by hand). */
  can_set_webhook?: boolean
  /** Masked per-channel secrets (`<key>_masked`), e.g. channel_secret_masked, client_secret_masked. */
  [masked: `${string}_masked`]: string | undefined
  team_id?: number | null
  ai_default?: AiDefault
  ai_review_mode?: ReviewMode
  /** A message the customer unsends stays readable (true) or is erased (false). */
  keep_recalled?: boolean
  /** The channel tells us about unsent messages (LINE, Messenger, Instagram). */
  recalls_supported?: boolean
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
    /** the newer channels' plain settings (app_id, account_sid, from_number, client_key…) and `<key>_masked` secrets */
    [key: string]: string | number | boolean | undefined
  }
}

/** The website widget's language: auto = the visitor's browser, else a fixed one. */
export type WidgetLanguage = 'auto' | 'en' | 'ms' | 'zh'

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
  language?: WidgetLanguage
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
  /** Set when the name is the product's own (the SIP trunk): show `t(nameKey)`. */
  nameKey?: MessageKey
  /** The card's second line — data (an address) or a translatable text. */
  address: string | Msg
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

/** The name to show for an inbox in the current language. */
export const inboxName = (inbox: Inbox, t: TFunction): string => (inbox.nameKey ? t(inbox.nameKey) : inbox.name)

const SOCIAL_KIND: Record<string, InboxKind> = {
  facebook: 'messenger',
  instagram: 'instagram',
  telegram: 'telegram',
  email: 'email',
  line: 'line',
  viber: 'viber',
  sms: 'sms',
  wechat: 'wechat',
  tiktok: 'tiktok',
  douyin: 'douyin',
  zalo: 'zalo',
}

const msg = (k: MessageKey, v?: Vars): Msg => ({ k, v })

/** The address line of a newer channel's card. */
function moreAddress(a: MessagingAccount): string | Msg {
  const cfg = a.config ?? {}
  const str = (k: string) => (cfg[k] != null ? String(cfg[k]) : '')
  switch (a.channel) {
    case 'sms':
      return (
        str('from_number') ||
        (str('messaging_service_sid')
          ? msg('channels.address.smsService', { id: str('messaging_service_sid') })
          : msg('channels.address.numberNotSet'))
      )
    case 'wechat':
      return a.external_id
        ? msg('channels.address.account', { id: a.external_id })
        : str('app_id')
          ? msg('channels.address.appId', { id: str('app_id') })
          : msg('channels.address.appIdNotSet')
    case 'tiktok':
      return a.external_id ? msg('channels.address.business', { id: a.external_id }) : msg('channels.address.businessIdNotSet')
    case 'douyin':
      return a.external_id ? msg('channels.address.account', { id: a.external_id }) : msg('channels.address.openIdNotSet')
    case 'zalo':
      return a.external_id
        ? msg('channels.address.oa', { id: a.external_id })
        : str('app_id')
          ? msg('channels.address.app', { id: str('app_id') })
          : msg('channels.address.oaLinked')
    default:
      return a.external_id ? msg('channels.address.bot', { id: a.external_id }) : msg('channels.address.tokenLinked')
  }
}

const WA_KIND_OF: Record<WaProvider, WaKind> = {
  cloud: 'wa_cloud',
  baileys: 'wa_qr',
  ycloud: 'wa_ycloud',
  twilio: 'wa_twilio',
}

function waAddress(a: WaAccount): string | Msg {
  switch (a.provider) {
    case 'baileys':
      return msg(a.configured ? 'channels.address.qrPaired' : 'channels.address.notPaired')
    case 'ycloud':
      return a.bsp?.phone_number || msg('channels.address.numberNotSet')
    case 'twilio':
      return a.bsp?.from_number
        ? a.bsp.from_number
        : a.bsp?.messaging_service_sid
          ? msg('channels.address.smsService', { id: a.bsp.messaging_service_sid })
          : msg('channels.address.numberNotSet')
    default:
      return a.phone_number_id
        ? msg('channels.address.numberId', { id: a.phone_number_id })
        : msg('channels.address.numberIdNotSet')
  }
}

export function fromWa(a: WaAccount): Inbox {
  return {
    key: `wa:${a.id}`,
    kind: WA_KIND_OF[a.provider] ?? 'wa_cloud',
    id: a.id,
    name: a.name,
    address: waAddress(a),
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
  const address: string | Msg = isMoreKind(kind)
    ? moreAddress(a)
    : kind === 'email'
      ? a.config?.from_email || a.external_id || msg('channels.address.fromNotSet')
      : kind === 'telegram'
        ? a.external_id
          ? msg('channels.address.bot', { id: a.external_id })
          : a.access_token_masked
            ? msg('channels.address.botTokenLinked')
            : msg('channels.address.noBotToken')
        : a.external_id
          ? msg(kind === 'messenger' ? 'channels.address.page' : 'channels.address.account', { id: a.external_id })
          : msg('channels.address.idNotSet')
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
    address: msg('channels.address.widget', { count: w.conversations, token: `${w.token.slice(0, 8)}…` }),
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
    nameKey: 'channels.voiceTrunk',
    address: v.termination_uri || msg('channels.address.terminationNotSet'),
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

/** An error for display: the server's own detail as it is; our session notice translated. */
export const errText = (e: unknown, t?: TFunction): string => {
  if (t && e instanceof ApiError && e.status === 401) return t('common.errors.sessionExpired')
  return e instanceof Error ? e.message : String(e)
}

/** Outcome of a Test run — `info` = honest "can't prove it from here". */
export interface TestResult {
  tone: 'ok' | 'info' | 'error'
  text: string
}

/** Run the channel's own test endpoint and phrase the answer for a human. */
export async function testInbox(inbox: Inbox, t: TFunction): Promise<TestResult> {
  try {
    if (isWaKind(inbox.kind)) {
      const r = await api<{ ok: boolean; display_phone_number: string; verified_name: string; quality_rating: string }>(
        `whatsapp/accounts/${inbox.id}/test`,
        { method: 'POST' },
      )
      if (inbox.kind === 'wa_qr')
        return r.ok
          ? {
              tone: 'ok',
              text: r.display_phone_number
                ? t('channels.test.pairedAs', { number: r.display_phone_number })
                : t('channels.test.paired'),
            }
          : { tone: 'error', text: t('channels.test.notPaired', { state: r.quality_rating }) }
      return r.ok
        ? {
            tone: 'ok',
            text: r.display_phone_number
              ? t('channels.test.credentialsFor', { name: r.verified_name || inbox.name, number: r.display_phone_number })
              : t('channels.test.credentials', { name: r.verified_name || inbox.name }),
          }
        : { tone: 'error', text: t('channels.test.accountState', { state: r.quality_rating }) }
    }
    if (inbox.kind === 'website') {
      // The public config endpoint is exactly what the embed calls first.
      const res = await fetch(`${API}/widget/${encodeURIComponent(inbox.widget?.token ?? '')}/config`)
      if (!res.ok) return { tone: 'error', text: t('channels.test.widgetSilent', { status: res.status }) }
      const cfg = (await res.json()) as { title?: string }
      return { tone: 'ok', text: t('channels.test.widgetAnswers', { title: cfg.title || inbox.name }) }
    }
    if (inbox.kind === 'voice') {
      const r = await api<{ ok?: boolean; detail?: string }>('whatsapp/voice-settings/test', { method: 'POST' })
      return { tone: r.ok ? 'ok' : 'info', text: r.detail || t('channels.test.sipSet') }
    }
    const r = await api<{ ok?: boolean; warning?: boolean; name?: string; id?: string; detail?: string }>(
      `messaging/accounts/${inbox.id}/test`,
      { method: 'POST', body: JSON.stringify({}) },
    )
    // a degraded verdict (e.g. "webhook not active") is a warning, not a pass
    return {
      tone: r.warning ? 'info' : 'ok',
      text: r.detail || (r.name ? t('channels.test.connectedAs', { name: r.name }) : t('channels.test.connected')),
    }
  } catch (e) {
    // 501 = the backend can't place a live probe for this provider yet; it
    // still says whether the stored config looks complete.
    if (e instanceof ApiError && e.status === 501) return { tone: 'info', text: e.message }
    return { tone: 'error', text: errText(e, t) }
  }
}

export interface InboxesSnapshot {
  inboxes: Inbox[]
  teams: Team[]
  voice: VoiceSettings | null
  verifyToken: string
  /** Which sources failed to load (translation keys; the rest still render). */
  failed: MessageKey[]
}

/** Load every inbox source independently — one being down never blanks the page.
 *  `entityQ` is `useEntityQuery()` — the accounts follow the entity switcher. */
export async function loadInboxes(entityQ = ''): Promise<InboxesSnapshot> {
  const q = entityQ ? `?${entityQ}` : ''
  const [wa, msgs, widgets, voice, teams, settings] = await Promise.allSettled([
    api<{ accounts: WaAccount[] }>(`whatsapp/accounts${q}`),
    api<{ accounts: MessagingAccount[] }>(`messaging/accounts${q}`),
    api<{ widgets: Widget[] }>('widgets'),
    api<VoiceSettings>('whatsapp/voice-settings'),
    api<{ teams: Team[] }>('teams'),
    api<{ verify_token: string }>('whatsapp/settings'),
  ])
  const failed: MessageKey[] = []
  const inboxes: Inbox[] = []
  if (wa.status === 'fulfilled') inboxes.push(...(wa.value.accounts ?? []).map(fromWa))
  else failed.push('channels.sources.whatsapp')
  if (msgs.status === 'fulfilled')
    inboxes.push(...(msgs.value.accounts ?? []).filter((a) => SOCIAL_KIND[a.channel]).map(fromMessaging))
  else failed.push('channels.sources.emailSocial')
  if (widgets.status === 'fulfilled') inboxes.push(...(widgets.value.widgets ?? []).map(fromWidget))
  else failed.push('channels.sources.website')
  const v = voice.status === 'fulfilled' ? voice.value : null
  if (v) {
    const vi = fromVoice(v)
    if (vi) inboxes.push(vi)
  } else failed.push('channels.sources.voice')
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
  if (isWaKind(inbox.kind)) {
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
