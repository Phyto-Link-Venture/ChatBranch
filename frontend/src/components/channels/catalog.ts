/**
 * The "Add inbox" stepper's catalog: one entry per CHANNEL (what the customer
 * uses — WhatsApp, Telegram, YouTube…) and, under it, the METHODS ChatBranch can
 * connect it with (Official Cloud API, YCloud, Twilio, QR, via Zernio…).
 *
 *   Step 1  pick a channel (tiles grouped by CHANNEL_GROUPS)
 *   Step 2  pick a method — only when the channel has more than one
 *   Step 3  the method's setup: a native inbox kind's existing drawer steps
 *           (`kind`), the Zernio connect flow (`zernio`) or the marketplace
 *           seller-service panel (`commerce`)
 *
 * Texts are translation keys (`channels.catalog.*`, `channels.methods.*`);
 * brand names stay as they are.
 */

import type { MessageKey } from '@/i18n'
import type { Inbox, InboxKind } from './inboxes'

export type ChannelGroup = 'messaging' | 'social' | 'sms_voice' | 'china' | 'website' | 'marketplace'

export const CHANNEL_GROUPS: { id: ChannelGroup; label: MessageKey; hint: MessageKey }[] = [
  { id: 'messaging', label: 'channels.groups.messaging', hint: 'channels.groups.messagingHint' },
  { id: 'social', label: 'channels.groups.social', hint: 'channels.groups.socialHint' },
  { id: 'sms_voice', label: 'channels.groups.smsVoice', hint: 'channels.groups.smsVoiceHint' },
  { id: 'china', label: 'channels.groups.china', hint: 'channels.groups.chinaHint' },
  { id: 'website', label: 'channels.groups.website', hint: 'channels.groups.websiteHint' },
  { id: 'marketplace', label: 'channels.groups.marketplace', hint: 'channels.groups.marketplaceHint' },
]

/** Zernio's `purpose`: a native ChatBranch inbox (DMs) or social features (posts / comments). */
export type ZernioPurpose = 'inbox' | 'social'

/** The marketplace channels the seller service carries (Channels/Commerce). */
export type CommerceChannel = 'shopee' | 'lazada' | 'tiktok_shop' | 'shopify'

export type MethodId =
  | 'wa_cloud'
  | 'wa_ycloud'
  | 'wa_twilio'
  | 'wa_qr'
  | 'wa_waha'
  | 'meta'
  | 'bot'
  | 'sms_twilio'
  | 'tiktok_direct'
  | 'direct'
  | 'zernio'
  | 'seller'

export interface MethodDef {
  id: MethodId
  /** Native setup: the inbox kind whose drawer steps run as step 3. */
  kind?: InboxKind
  /** Zernio setup: POST /api/social/connect {platform, entity, purpose}. */
  zernio?: { platform: string; purpose: ZernioPurpose }
  /** Marketplace: the shops come from the seller service. */
  commerce?: CommerceChannel
  /** Card title, the one-line pro / con, and the badge connected cards carry. */
  label: MessageKey
  pro: MessageKey
  con: MessageKey
  badge?: MessageKey
  recommended?: boolean
  /** Unofficial transport (QR, WAHA) — amber warning tone. */
  unofficial?: boolean
}

export interface ChannelDef {
  id: string
  /** ChannelLogo id. */
  logo: string
  /** A brand name (never translated)… */
  brand?: string
  /** …or a translation key (Email, Website chat…). */
  label?: MessageKey
  blurb: MessageKey
  group: ChannelGroup
  methods: MethodDef[]
  badge?: 'Beta' | 'Demo'
  /** The platform must approve the app before its messaging API works. */
  approval?: boolean
}

// ── methods ──────────────────────────────────────────────────────────────────

const m = (id: string) => ({
  label: `channels.methods.${id}.label` as MessageKey,
  pro: `channels.methods.${id}.pro` as MessageKey,
  con: `channels.methods.${id}.con` as MessageKey,
})

const BADGE = {
  cloud: 'channels.methods.badges.cloud',
  ycloud: 'channels.methods.badges.ycloud',
  twilio: 'channels.methods.badges.twilio',
  qr: 'channels.methods.badges.qr',
  waha: 'channels.methods.badges.waha',
  zernio: 'channels.methods.badges.zernio',
  meta: 'channels.methods.badges.meta',
  bot: 'channels.methods.badges.bot',
  direct: 'channels.methods.badges.direct',
} as const satisfies Record<string, MessageKey>

/** "via Zernio" for a native channel's DMs (a ChatBranch inbox with provider "zernio"). */
const zernioInbox = (platform: string): MethodDef => ({
  id: 'zernio',
  zernio: { platform, purpose: 'inbox' },
  ...m('zernioInbox'),
  badge: BADGE.zernio,
})

/** "via Zernio" for a social network (posts, comments, reviews, DMs where offered). */
const zernioSocial = (platform: string): MethodDef => ({
  id: 'zernio',
  zernio: { platform, purpose: 'social' },
  ...m('zernioSocial'),
  badge: BADGE.zernio,
})

/** A channel with exactly one native method — step 2 is skipped. */
const direct = (kind: InboxKind): MethodDef => ({ id: 'direct', kind, ...m('direct') })

const seller = (channel: CommerceChannel): MethodDef => ({ id: 'seller', commerce: channel, ...m('seller') })

const social = (id: string, logo: string, brand: string): ChannelDef => ({
  id,
  logo,
  brand,
  blurb: `channels.catalog.blurbs.${id}` as MessageKey,
  group: 'social',
  methods: [zernioSocial(id)],
})

const blurb = (id: string) => `channels.catalog.blurbs.${id}` as MessageKey

// ── channels ─────────────────────────────────────────────────────────────────

export const CHANNELS: ChannelDef[] = [
  // Messaging apps
  {
    id: 'whatsapp',
    logo: 'whatsapp',
    brand: 'WhatsApp',
    blurb: blurb('whatsapp'),
    group: 'messaging',
    methods: [
      { id: 'wa_cloud', kind: 'wa_cloud', ...m('waCloud'), badge: BADGE.cloud, recommended: true },
      { id: 'wa_ycloud', kind: 'wa_ycloud', ...m('waYcloud'), badge: BADGE.ycloud },
      { id: 'wa_twilio', kind: 'wa_twilio', ...m('waTwilio'), badge: BADGE.twilio },
      { id: 'wa_qr', kind: 'wa_qr', ...m('waQr'), badge: BADGE.qr, unofficial: true },
      { id: 'wa_waha', kind: 'wa_waha', ...m('waWaha'), badge: BADGE.waha, unofficial: true },
      zernioInbox('whatsapp'),
    ],
  },
  {
    id: 'messenger',
    logo: 'messenger',
    brand: 'Messenger',
    blurb: blurb('messenger'),
    group: 'messaging',
    methods: [{ id: 'meta', kind: 'messenger', ...m('metaDirect'), badge: BADGE.meta }, zernioInbox('facebook')],
  },
  {
    id: 'instagram',
    logo: 'instagram',
    brand: 'Instagram',
    blurb: blurb('instagram'),
    group: 'messaging',
    methods: [{ id: 'meta', kind: 'instagram', ...m('metaDirect'), badge: BADGE.meta }, zernioInbox('instagram')],
  },
  {
    id: 'telegram',
    logo: 'telegram',
    brand: 'Telegram',
    blurb: blurb('telegram'),
    group: 'messaging',
    methods: [{ id: 'bot', kind: 'telegram', ...m('botToken'), badge: BADGE.bot, recommended: true }, zernioInbox('telegram')],
  },
  { id: 'line', logo: 'line', brand: 'LINE', blurb: 'channels.kinds.line.blurb', group: 'messaging', methods: [direct('line')] },
  { id: 'viber', logo: 'viber', brand: 'Viber', blurb: 'channels.kinds.viber.blurb', group: 'messaging', methods: [direct('viber')] },
  { id: 'zalo', logo: 'zalo', brand: 'Zalo', blurb: 'channels.kinds.zalo.blurb', group: 'messaging', methods: [direct('zalo')] },

  // Social networks — via Zernio (TikTok also has its own Business Messaging API)
  social('facebook', 'facebook', 'Facebook Page'),
  {
    id: 'tiktok',
    logo: 'tiktok',
    brand: 'TikTok',
    blurb: blurb('tiktok'),
    group: 'social',
    methods: [
      { id: 'tiktok_direct', kind: 'tiktok', ...m('tiktokDirect'), badge: BADGE.direct },
      zernioSocial('tiktok'),
    ],
  },
  social('youtube', 'youtube', 'YouTube'),
  social('linkedin', 'linkedin', 'LinkedIn'),
  social('twitter', 'x', 'X'),
  social('threads', 'threads', 'Threads'),
  social('pinterest', 'pinterest', 'Pinterest'),
  social('googlebusiness', 'googlebusiness', 'Google Business'),
  social('bluesky', 'bluesky', 'Bluesky'),
  social('reddit', 'reddit', 'Reddit'),
  social('discord', 'discord', 'Discord'),
  social('snapchat', 'snapchat', 'Snapchat'),

  // SMS, email & voice
  {
    id: 'sms',
    logo: 'sms',
    brand: 'SMS',
    blurb: blurb('sms'),
    group: 'sms_voice',
    methods: [{ id: 'sms_twilio', kind: 'sms', ...m('smsTwilio'), badge: BADGE.twilio, recommended: true }, zernioInbox('sms')],
  },
  { id: 'email', logo: 'email', label: 'channels.kinds.email.label', blurb: 'channels.kinds.email.blurb', group: 'sms_voice', methods: [direct('email')] },
  { id: 'voice', logo: 'voice', label: 'channels.kinds.voice.label', blurb: 'channels.kinds.voice.blurb', group: 'sms_voice', methods: [direct('voice')] },

  // China
  { id: 'wechat', logo: 'wechat', brand: 'WeChat', blurb: 'channels.kinds.wechat.blurb', group: 'china', methods: [direct('wechat')] },
  {
    id: 'douyin',
    logo: 'douyin',
    label: 'channels.kinds.douyin.label',
    blurb: 'channels.kinds.douyin.blurb',
    group: 'china',
    methods: [direct('douyin')],
    badge: 'Beta',
    approval: true,
  },

  // Website chat
  {
    id: 'website',
    logo: 'website',
    label: 'channels.kinds.website.label',
    blurb: 'channels.kinds.website.blurb',
    group: 'website',
    methods: [direct('website')],
  },

  // Marketplaces — the shops live in the seller service
  { id: 'shopee', logo: 'shopee', brand: 'Shopee', blurb: blurb('shopee'), group: 'marketplace', methods: [seller('shopee')], badge: 'Beta' },
  { id: 'lazada', logo: 'lazada', brand: 'Lazada', blurb: blurb('lazada'), group: 'marketplace', methods: [seller('lazada')], badge: 'Beta' },
  {
    id: 'tiktok_shop',
    logo: 'tiktok_shop',
    brand: 'TikTok Shop',
    blurb: blurb('tiktokShop'),
    group: 'marketplace',
    methods: [seller('tiktok_shop')],
    badge: 'Beta',
  },
  { id: 'shopify', logo: 'shopify', brand: 'Shopify', blurb: blurb('shopify'), group: 'marketplace', methods: [seller('shopify')], badge: 'Beta' },
]

export const channelDef = (id: string | null | undefined): ChannelDef | undefined =>
  id ? CHANNELS.find((c) => c.id === id) : undefined

/** The channel tile a native inbox kind sits under (every WhatsApp kind → WhatsApp). */
export const channelOfKind = (kind: InboxKind): ChannelDef | undefined =>
  CHANNELS.find((c) => c.methods.some((x) => x.kind === kind))

/** The method a native inbox kind is set up with. */
export const methodOfKind = (kind: InboxKind): MethodDef | undefined =>
  channelOfKind(kind)?.methods.find((x) => x.kind === kind)

/** Every channel that can be connected through Zernio. */
export const isZernioOnly = (c: ChannelDef) => c.methods.every((x) => !!x.zernio)

/** The method badge on a connected card ("Cloud API", "via Zernio"…); none for one-method channels. */
export function methodBadge(inbox: Inbox): MessageKey | undefined {
  if (inbox.viaZernio) return BADGE.zernio
  const ch = channelOfKind(inbox.kind)
  if (!ch || ch.methods.length < 2) return undefined
  return methodOfKind(inbox.kind)?.badge
}

export const ZERNIO_BADGE: MessageKey = BADGE.zernio
