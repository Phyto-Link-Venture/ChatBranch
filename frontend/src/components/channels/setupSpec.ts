/**
 * Per-channel setup content for the Inboxes drawer: the credential fields of
 * step 2 (with "where do I find this?" help and inline validation) and the
 * short step-by-step guide shown under them. Pure data — the drawer renders it.
 *
 * Every text is a translation key under `channels.setup.*`. Example values in
 * placeholders (IDs, host names, token shapes) are not words and stay as they
 * are; a placeholder that IS words uses `placeholderKey`.
 */

import type { MessageKey, TFunction } from '@/i18n'
import type { InboxKind } from './inboxes'

export interface FieldSpec {
  key: string
  label: MessageKey
  /** A literal example (an ID, a host, a token shape). */
  placeholder?: string
  /** A placeholder made of words. */
  placeholderKey?: MessageKey
  /** Where to find the value — always shown under the field. */
  help: MessageKey
  secret?: boolean
  optional?: boolean
  /** Optional to save, but things break without it (e.g. Meta's app secret: no inbound) — shown up front, never under Advanced. */
  recommended?: boolean
  inputMode?: 'numeric' | 'email' | 'url' | 'text'
  /** Format check; return a message, or null when fine. Only runs on non-empty values. */
  check?: (v: string, t: TFunction) => string | null
  /** Narrow fields pair up two-per-row on wider screens. */
  half?: boolean
}

type Check = NonNullable<FieldSpec['check']>

const digits =
  (field: MessageKey): Check =>
  (v, t) =>
    /^\d{5,}$/.test(v) ? null : t('channels.setup.checks.digits', { field: t(field) })

const email: Check = (v, t) => (/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(v) ? null : t('channels.setup.checks.email'))

const pattern =
  (re: RegExp, hint: MessageKey): Check =>
  (v, t) =>
    re.test(v) ? null : t(hint)

const e164 = pattern(/^\+[1-9]\d{6,14}$/, 'channels.setup.checks.e164')

const host = pattern(
  /^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$/i,
  'channels.setup.checks.host',
)

const port =
  (example: string): Check =>
  (v, t) =>
    /^\d+$/.test(v) && Number(v) > 0 && Number(v) < 65536 ? null : t('channels.setup.checks.port', { example })

const L = 'channels.setup.labels' as const
const H = 'channels.setup.help' as const
const P = 'channels.setup.placeholders' as const

export const CRED_FIELDS: Partial<Record<InboxKind, FieldSpec[]>> = {
  wa_cloud: [
    {
      key: 'phone_number_id', label: `${L}.phoneNumberId`, placeholder: '123456789012345', inputMode: 'numeric',
      help: `${H}.waCloud.phoneNumberId`, check: digits(`${L}.phoneNumberId`),
    },
    {
      key: 'waba_id', label: `${L}.wabaId`, placeholder: '102938475610293', inputMode: 'numeric',
      optional: true, help: `${H}.waCloud.wabaId`, check: digits(`${L}.wabaIdShort`),
    },
    {
      key: 'access_token', label: `${L}.accessToken`, placeholder: 'EAAG…', secret: true,
      help: `${H}.waCloud.accessToken`,
    },
    {
      key: 'app_secret', label: `${L}.appSecret`, placeholderKey: `${P}.signatureChecks`, secret: true,
      optional: true, recommended: true, help: `${H}.waCloud.appSecret`,
    },
  ],
  email: [
    {
      key: 'from_email', label: `${L}.fromAddress`, placeholder: 'support@yourco.com', inputMode: 'email',
      help: `${H}.email.fromEmail`, check: email,
    },
    {
      key: 'host', label: `${L}.smtpHost`, placeholder: 'smtp.gmail.com', half: true,
      help: `${H}.email.host`, check: host,
    },
    {
      key: 'port', label: `${L}.port`, placeholder: '587', inputMode: 'numeric', half: true, optional: true,
      help: `${H}.email.port`, check: port('587'),
    },
    {
      key: 'username', label: `${L}.username`, placeholder: 'support@yourco.com',
      help: `${H}.email.username`,
    },
    {
      key: 'password', label: `${L}.password`, placeholderKey: `${P}.appPassword`, secret: true,
      help: `${H}.email.password`,
    },
    {
      key: 'imap_host', label: `${L}.imapHostReplies`, placeholder: 'imap.gmail.com', half: true, optional: true,
      help: `${H}.email.imapHost`, check: host,
    },
    {
      key: 'imap_port', label: `${L}.imapPort`, placeholder: '993', inputMode: 'numeric', half: true, optional: true,
      help: `${H}.email.imapPort`, check: port('993'),
    },
  ],
  telegram: [
    {
      key: 'access_token', label: `${L}.botToken`, placeholder: '123456789:AAH…', secret: true,
      help: `${H}.telegram.accessToken`,
      check: pattern(/^\d+:[\w-]{10,}$/, 'channels.setup.checks.botToken'),
    },
  ],
  messenger: [
    {
      key: 'external_id', label: `${L}.pageId`, placeholder: '1029384756', inputMode: 'numeric',
      help: `${H}.messenger.externalId`, check: digits(`${L}.pageId`),
    },
    {
      key: 'access_token', label: `${L}.pageAccessToken`, placeholder: 'EAAG…', secret: true,
      help: `${H}.messenger.accessToken`,
    },
    {
      key: 'app_secret', label: `${L}.appSecret`, placeholderKey: `${P}.fromAppSettings`, secret: true,
      optional: true, recommended: true, help: `${H}.meta.appSecret`,
    },
  ],
  instagram: [
    {
      key: 'external_id', label: `${L}.igAccountId`, placeholder: '17841400000000000', inputMode: 'numeric',
      help: `${H}.instagram.externalId`, check: digits(`${L}.igAccountId`),
    },
    {
      key: 'access_token', label: `${L}.pageAccessToken`, placeholder: 'EAAG…', secret: true,
      help: `${H}.instagram.accessToken`,
    },
    {
      key: 'app_secret', label: `${L}.appSecret`, placeholderKey: `${P}.fromAppSettings`, secret: true,
      optional: true, recommended: true, help: `${H}.meta.appSecret`,
    },
  ],
  line: [
    {
      key: 'access_token', label: `${L}.channelAccessToken`, placeholderKey: `${P}.longLivedToken`, secret: true,
      help: `${H}.line.accessToken`,
    },
    {
      key: 'channel_secret', label: `${L}.channelSecret`, placeholderKey: `${P}.hex32`, secret: true,
      help: `${H}.line.channelSecret`,
      check: pattern(/^[0-9a-f]{32}$/i, 'channels.setup.checks.lineSecret'),
    },
  ],
  viber: [
    {
      key: 'access_token', label: `${L}.authenticationToken`, placeholder: '4453b6ac12345678-…', secret: true,
      help: `${H}.viber.accessToken`,
    },
    {
      key: 'sender_name', label: `${L}.senderName`, placeholderKey: `${P}.yourShop`, optional: true,
      help: `${H}.viber.senderName`,
      check: (v, t) => (v.length <= 28 ? null : t('channels.setup.checks.viberName')),
    },
  ],
  sms: [
    {
      key: 'account_sid', label: `${L}.accountSid`, placeholder: 'AC…', inputMode: 'text',
      help: `${H}.sms.accountSid`,
      check: pattern(/^AC[0-9a-f]{32}$/i, 'channels.setup.checks.accountSid'),
    },
    {
      key: 'access_token', label: `${L}.authToken`, placeholderKey: `${P}.fromTwilio`, secret: true,
      help: `${H}.sms.accessToken`,
    },
    {
      key: 'from_number', label: `${L}.fromNumber`, placeholder: '+60123456789', half: true, optional: true,
      help: `${H}.sms.fromNumber`, check: e164,
    },
    {
      key: 'messaging_service_sid', label: `${L}.messagingServiceSid`, placeholder: 'MG…', half: true, optional: true,
      help: `${H}.sms.messagingServiceSid`,
      check: pattern(/^MG[0-9a-f]{32}$/i, 'channels.setup.checks.messagingServiceSid'),
    },
  ],
  wechat: [
    {
      key: 'app_id', label: `${L}.wechatAppId`, placeholder: 'wx…', half: true,
      help: `${H}.wechat.appId`,
      check: pattern(/^wx[0-9A-Za-z]{6,32}$/, 'channels.setup.checks.wechatAppId'),
    },
    {
      key: 'app_secret', label: `${L}.wechatAppSecret`, placeholderKey: `${P}.resetToView`, secret: true, half: true,
      help: `${H}.wechat.appSecret`,
    },
    {
      key: 'token', label: `${L}.serverToken`, placeholderKey: `${P}.token3to32`, secret: true,
      help: `${H}.wechat.token`,
      check: pattern(/^[A-Za-z0-9]{3,32}$/, 'channels.setup.checks.wechatToken'),
    },
    {
      key: 'encoding_aes_key', label: `${L}.encodingAesKey`, placeholderKey: `${P}.aes43`, secret: true,
      optional: true, help: `${H}.wechat.encodingAesKey`,
      check: pattern(/^[A-Za-z0-9]{43}$/, 'channels.setup.checks.encodingAesKey'),
    },
    {
      key: 'external_id', label: `${L}.originalId`, placeholder: 'gh_…', optional: true,
      help: `${H}.wechat.externalId`,
    },
  ],
  tiktok: [
    {
      key: 'external_id', label: `${L}.businessId`, placeholder: '7000000000000000000',
      help: `${H}.tiktok.externalId`,
    },
    {
      key: 'access_token', label: `${L}.businessAccessToken`, placeholder: 'act.…', secret: true,
      help: `${H}.tiktok.accessToken`,
    },
    {
      key: 'app_id', label: `${L}.appId`, placeholderKey: `${P}.yourAppId`, optional: true, half: true,
      help: `${H}.tiktok.appId`,
    },
    {
      key: 'app_secret', label: `${L}.appSecret`, placeholderKey: `${P}.signsWebhooks`, secret: true, half: true,
      help: `${H}.tiktok.appSecret`,
    },
  ],
  douyin: [
    {
      key: 'client_key', label: `${L}.clientKey`, placeholder: 'aw…', half: true,
      help: `${H}.douyin.clientKey`,
    },
    {
      key: 'client_secret', label: `${L}.clientSecret`, placeholderKey: `${P}.signsWebhooks`, secret: true, half: true,
      help: `${H}.douyin.clientSecret`,
    },
    {
      key: 'external_id', label: `${L}.enterpriseOpenId`, placeholderKey: `${P}.openIdOfEnterprise`,
      help: `${H}.douyin.externalId`,
    },
    {
      key: 'access_token', label: `${L}.accessToken`, placeholder: 'act.…', secret: true,
      help: `${H}.douyin.accessToken`,
    },
    {
      key: 'refresh_token', label: `${L}.refreshToken`, placeholder: 'rft.…', secret: true, optional: true,
      help: `${H}.douyin.refreshToken`,
    },
  ],
  zalo: [
    {
      key: 'app_id', label: `${L}.appId`, placeholder: '1234567890123456789', inputMode: 'numeric', half: true,
      help: `${H}.zalo.appId`, check: digits(`${L}.appId`),
    },
    {
      key: 'oa_secret_key', label: `${L}.oaSecretKey`, placeholderKey: `${P}.signsWebhooks`, secret: true, half: true,
      help: `${H}.zalo.oaSecretKey`,
    },
    {
      key: 'access_token', label: `${L}.oaAccessToken`, placeholderKey: `${P}.fromExplorer`, secret: true,
      help: `${H}.zalo.accessToken`,
    },
    {
      key: 'refresh_token', label: `${L}.refreshToken`, placeholderKey: `${P}.renewsToken`, secret: true,
      optional: true, half: true, help: `${H}.zalo.refreshToken`,
    },
    {
      key: 'app_secret', label: `${L}.appSecretKey`, placeholderKey: `${P}.forRenewal`, secret: true, optional: true,
      half: true, help: `${H}.zalo.appSecret`,
    },
    {
      key: 'external_id', label: `${L}.oaId`, placeholderKey: `${P}.learnedOnTest`, optional: true,
      help: `${H}.zalo.externalId`,
    },
  ],
  voice: [
    {
      key: 'sip_username', label: `${L}.sipUsername`, placeholderKey: `${P}.sipUsername`,
      help: `${H}.voice.sipUsername`,
    },
    {
      key: 'sip_password', label: `${L}.sipPassword`, placeholderKey: `${P}.sipPassword`, secret: true,
      help: `${H}.voice.sipPassword`,
    },
    {
      key: 'termination_uri', label: `${L}.terminationUri`, placeholder: 'your-trunk.pstn.twilio.com',
      help: `${H}.voice.terminationUri`, check: host,
    },
  ],
}

const G = 'channels.setup.guides' as const

/** "Where do I find these?" — the short walk-through per channel (translation keys). */
export const GUIDES: Partial<Record<InboxKind, MessageKey[]>> = {
  wa_cloud: [`${G}.waCloud.s1`, `${G}.waCloud.s2`, `${G}.waCloud.s3`, `${G}.waCloud.s4`],
  email: [`${G}.email.s1`, `${G}.email.s2`, `${G}.email.s3`, `${G}.email.s4`],
  telegram: [`${G}.telegram.s1`, `${G}.telegram.s2`, `${G}.telegram.s3`, `${G}.telegram.s4`],
  messenger: [`${G}.messenger.s1`, `${G}.messenger.s2`, `${G}.messenger.s3`],
  instagram: [`${G}.instagram.s1`, `${G}.instagram.s2`, `${G}.instagram.s3`],
  line: [`${G}.line.s1`, `${G}.line.s2`, `${G}.line.s3`, `${G}.line.s4`],
  viber: [`${G}.viber.s1`, `${G}.viber.s2`, `${G}.viber.s3`, `${G}.viber.s4`],
  sms: [`${G}.sms.s1`, `${G}.sms.s2`, `${G}.sms.s3`, `${G}.sms.s4`],
  wechat: [`${G}.wechat.s1`, `${G}.wechat.s2`, `${G}.wechat.s3`, `${G}.wechat.s4`],
  tiktok: [`${G}.tiktok.s1`, `${G}.tiktok.s2`, `${G}.tiktok.s3`, `${G}.tiktok.s4`],
  douyin: [`${G}.douyin.s1`, `${G}.douyin.s2`, `${G}.douyin.s3`, `${G}.douyin.s4`],
  zalo: [`${G}.zalo.s1`, `${G}.zalo.s2`, `${G}.zalo.s3`, `${G}.zalo.s4`],
  voice: [`${G}.voice.s1`, `${G}.voice.s2`, `${G}.voice.s3`, `${G}.voice.s4`],
}

const N = 'channels.setup.defaultNames' as const

/** A sensible starting name for a new inbox (editable in step 3), as a key. */
export const DEFAULT_NAME: Record<InboxKind, MessageKey> = {
  wa_cloud: `${N}.waCloud`,
  wa_qr: `${N}.waQr`,
  email: `${N}.email`,
  telegram: `${N}.telegram`,
  messenger: `${N}.messenger`,
  instagram: `${N}.instagram`,
  website: `${N}.website`,
  voice: `${N}.voice`,
  line: `${N}.line`,
  viber: `${N}.viber`,
  sms: `${N}.sms`,
  wechat: `${N}.wechat`,
  tiktok: `${N}.tiktok`,
  douyin: `${N}.douyin`,
  zalo: `${N}.zalo`,
}

/** Channel-wide rules on top of the per-field checks (step 2). */
export const ONE_OF: Partial<Record<InboxKind, { keys: string[]; message: MessageKey }>> = {
  sms: { keys: ['from_number', 'messaging_service_sid'], message: 'channels.setup.checks.smsOneOf' },
}

const W = 'channels.setup.webhookHelp' as const

/** Where the webhook URL goes on each platform (the test step shows it with a copy button). */
export const WEBHOOK_HELP: Partial<Record<InboxKind, MessageKey>> = {
  line: `${W}.line`,
  viber: `${W}.viber`,
  sms: `${W}.sms`,
  wechat: `${W}.wechat`,
  tiktok: `${W}.tiktok`,
  douyin: `${W}.douyin`,
  zalo: `${W}.zalo`,
}
