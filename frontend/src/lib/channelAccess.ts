/**
 * A role's channel policy (Role.ChannelAccess on the API): which channels — and which
 * connection methods of each — people holding the role may connect and use.
 *
 *   null / undefined  every channel (no limit; superadmin always)
 *   { whatsapp: ['cloud', 'ycloud'], sms: ['direct'] }   only these
 *
 * Method ids are the API's: WhatsApp's providers (cloud, ycloud, twilio, zernio,
 * baileys, waha); every other channel `direct` (its own credentials) or `zernio`
 * (a managed connection). The server enforces the policy; the dashboard only hides
 * what would be refused.
 */

export type ChannelPolicy = Record<string, string[]> | null

export interface PolicyMethod {
  id: string
  label: string
  official: boolean
}

export interface PolicyChannel {
  channel: string
  label: string
  methods: PolicyMethod[]
}

/** GET /api/auth/channel-access */
export interface ChannelAccessSnapshot {
  catalog: PolicyChannel[]
  roles: Record<string, ChannelPolicy>
}

export const methodAllowed = (policy: ChannelPolicy | undefined, channel: string, method: string): boolean =>
  !policy || (policy[channel] ?? []).includes(method)

/** The Channels page's catalog ids → the policy's (channel, method). Voice (the SIP
 *  trunk) is not a channel account and is never limited by a role's channels. */
const CATALOG_CHANNEL: Record<string, string> = { messenger: 'facebook' }
const CATALOG_METHOD: Record<string, string> = {
  wa_cloud: 'cloud',
  wa_ycloud: 'ycloud',
  wa_twilio: 'twilio',
  wa_qr: 'baileys',
  wa_waha: 'waha',
  zernio: 'zernio',
}

export function catalogMethodAllowed(policy: ChannelPolicy | undefined, channelId: string, methodId: string): boolean {
  if (!policy || channelId === 'voice') return true
  return methodAllowed(policy, CATALOG_CHANNEL[channelId] ?? channelId, CATALOG_METHOD[methodId] ?? 'direct')
}

export const readPolicy = (v: unknown): ChannelPolicy => {
  if (!v || typeof v !== 'object' || Array.isArray(v)) return null
  const out: Record<string, string[]> = {}
  for (const [k, list] of Object.entries(v as Record<string, unknown>))
    if (Array.isArray(list)) out[k] = list.filter((x): x is string => typeof x === 'string')
  return out
}
