'use client'

/**
 * ChannelHub — the per-contact communication hub: which channels/identities a
 * contact is reachable on, through which provider accounts, connection status,
 * last activity and history counts. Data: GET /api/contacts/<id>/channels,
 * fetched whenever contactId changes (no polling — the hub is a profile, not
 * a live feed). Rendered in the /chats info panel and on the contact detail
 * page; parents supply their own section header / card chrome.
 *
 * Theming: semantic tokens only; each channel shows its real brand mark
 * (ChannelLogo); health text uses the *-ink vars via HEALTH_META so it flips
 * with light/dark.
 *
 * Identities: the contact's phone / email are registered as WhatsApp, voice,
 * SMS and email identities automatically (no conversation needed yet); a
 * WhatsApp number carries its lookup verdict (On WhatsApp / Not on WhatsApp /
 * Unverified — only a connected QR number can look numbers up, the Cloud API
 * cannot). With `allowAdd` (the contact page) the hub also offers "Find
 * channels" (re-run discovery) and "Invite to another app".
 */

import { useEffect, useState } from 'react'
import { useRouter } from 'next/navigation'
import { Check, ChevronDown, ChevronRight, HelpCircle, Loader2, MessageSquarePlus, Plus, RefreshCw, Send, X } from 'lucide-react'
import { authHeaders } from '@/lib/auth'
import { HEALTH_META, type HealthStatus } from '@/lib/channelHealth'
import { Trans, useFormat, useT, type MessageKey, type TFunction } from '@/i18n'
import { useAccess } from '@/lib/useAccess'
import { NO_LOOKUP_LABELS, cApi, errText, type DiscoverResult, type Verdict } from '@/lib/contactIdentity'
import { Select, type SelectOption } from '@/components/ui/Select'
import { ChannelLogo } from '@/components/ui/ChannelLogo'
import { InviteModal } from '@/components/contacts/InviteModal'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

export interface ChannelIdentity {
  key: string
  address: string
  name: string
  account_id: string | null
  account_name: string | null
  account_provider: 'cloud' | 'baileys' | 'ycloud' | 'twilio' | null
  account_health: HealthStatus
  last_active: string | null
  message_count: number
  inbound_count: number
  outbound_count: number
  last_direction: 'in' | 'out' | null
  opted_out: boolean
  window_open: boolean | null
  /** A registered identity (discovered from the contact's phone / email). */
  registered?: boolean
  source?: string | null
  /** WhatsApp only: the lookup verdict. */
  verification?: Verdict | null
  verification_detail?: string
  checked_at?: string | null
}

export interface HubChannel {
  channel: string
  connected: boolean
  /** Chat channels */
  linked_accounts?: number
  last_active?: string | null
  identities?: ChannelIdentity[]
  /** Voice */
  calls?: number
  last_call?: string | null
  last_outcome?: string | null
  number?: string | null
  registered?: boolean
}

export interface HubPayload {
  contact_id: number
  name: string
  phone: string
  email: string
  channels: HubChannel[]
  lookup_available?: boolean
  lookup_note?: string
  no_lookup_channels?: string[]
  invite_channels?: string[]
}

/** Display name + the brand mark to show (our `facebook` channel is Messenger).
 *  Email / Website / AI Voice are translated by chName(). */
const CH_META: Record<string, { lbl: string; logo: string }> = {
  whatsapp: { lbl: 'WhatsApp', logo: 'whatsapp' },
  email: { lbl: 'Email', logo: 'email' },
  facebook: { lbl: 'Messenger', logo: 'messenger' },
  instagram: { lbl: 'Instagram', logo: 'instagram' },
  telegram: { lbl: 'Telegram', logo: 'telegram' },
  website: { lbl: 'Website', logo: 'website' },
  voice: { lbl: 'AI Voice', logo: 'voice' },
  sms: { lbl: 'SMS', logo: 'sms' },
  line: { lbl: 'LINE', logo: 'line' },
  viber: { lbl: 'Viber', logo: 'viber' },
  wechat: { lbl: 'WeChat', logo: 'wechat' },
  zalo: { lbl: 'Zalo', logo: 'zalo' },
  tiktok: { lbl: 'TikTok', logo: 'tiktok' },
  douyin: { lbl: 'Douyin', logo: 'douyin' },
}

/** A channel's name in the current language (brand names as they are). */
const chName = (t: TFunction, c: string) =>
  c === 'email' || c === 'website'
    ? t(`chats.channels.${c}`)
    : c === 'voice'
      ? t('contacts.hub.aiVoice')
      : (CH_META[c]?.lbl ?? c)

const VERDICT: Record<Verdict, { lbl: MessageKey; cls: string; Icon: typeof Check | null }> = {
  verified: { lbl: 'contacts.hub.verdictVerified', cls: 'bg-green/10 text-green-300', Icon: Check },
  not_on_whatsapp: { lbl: 'contacts.hub.verdictNotOnWa', cls: 'bg-red/10 text-red-300', Icon: X },
  unknown: { lbl: 'contacts.hub.verdictUnverified', cls: 'bg-fill text-text-muted', Icon: HelpCircle },
  pending: { lbl: 'contacts.hub.verdictChecking', cls: 'bg-amber/10 text-amber-300', Icon: Loader2 },
}

const HEALTH_KEYS: Record<HealthStatus, MessageKey> = {
  healthy: 'contacts.hub.health.healthy',
  degraded: 'contacts.hub.health.degraded',
  unknown: 'contacts.hub.health.unknown',
  disconnected: 'contacts.hub.health.disconnected',
  banned: 'contacts.hub.health.banned',
}

/** "5 minutes ago" in the current language; "never" for no timestamp. */
function useRel() {
  const t = useT()
  const f = useFormat()
  return (iso: string | null | undefined) => (iso ? f.relative(iso) || t('common.status.never') : t('common.status.never'))
}

function VerdictChip({ ident }: { ident: ChannelIdentity }) {
  const t = useT()
  const v = ident.verification
  if (!v) return null
  const meta = VERDICT[v] ?? VERDICT.unknown
  const title =
    v === 'unknown'
      ? `${ident.verification_detail || t('contacts.hub.notChecked')} ${t('contacts.hub.cloudNote')}`
      : ident.verification_detail || t(meta.lbl)
  const Icon = meta.Icon
  return (
    <span
      className={`inline-flex items-center gap-1 rounded-full px-1.5 text-[10px] font-semibold ${meta.cls}`}
      title={title}
    >
      {Icon && <Icon size={9} className={v === 'pending' ? 'animate-spin' : ''} aria-hidden />}
      {t(meta.lbl)}
    </span>
  )
}

/** Connection dot: emerald = connected via a healthy account, amber =
 * connected but degraded/unknown health, red = not currently reachable. */
function dotColor(ch: HubChannel): string {
  if (!ch.connected) return 'rgb(var(--c-red))'
  if (ch.channel === 'voice') return 'rgb(var(--c-green))'
  return (ch.identities ?? []).some((i) => i.account_health === 'healthy') ? 'rgb(var(--c-green))' : 'rgb(var(--c-amber))'
}

const SOURCE_LABEL: Record<string, MessageKey> = {
  auto: 'contacts.hub.source.auto',
  import: 'contacts.hub.source.import',
  self: 'contacts.hub.source.self',
  thread: 'contacts.hub.source.thread',
  manual: 'contacts.hub.source.manual',
  invite: 'contacts.hub.source.invite',
}

function IdentityRow({ ident }: { ident: ChannelIdentity }) {
  const t = useT()
  const rel = useRel()
  const health = HEALTH_META[ident.account_health] ?? HEALTH_META.unknown
  const healthLabel = t(HEALTH_KEYS[ident.account_health] ?? HEALTH_KEYS.unknown)
  const source = SOURCE_LABEL[ident.source ?? '']
  return (
    <div className="border-t border-line px-2.5 py-2">
      <div className="flex items-baseline justify-between gap-2">
        <span className="min-w-0 truncate text-[12px] font-semibold text-text-high">
          {ident.address || ident.key}
        </span>
        <span className="shrink-0 text-[10.5px] text-text-muted">{rel(ident.last_active)}</span>
      </div>
      <div className="mt-0.5 flex flex-wrap items-center gap-x-2 gap-y-0.5 text-[11px] text-text-muted">
        <span>
          {ident.registered && ident.message_count === 0
            ? t('contacts.hub.noMessagesYet')
            : t('contacts.hub.msgsInOut', { count: ident.message_count, in: ident.inbound_count, out: ident.outbound_count })}
        </span>
        <VerdictChip ident={ident} />
        {ident.opted_out && (
          <span className="rounded-full bg-red/10 px-1.5 text-[10px] font-semibold text-red-300">
            {t('contacts.hub.optedOut')}
          </span>
        )}
        {ident.window_open === false && (
          <span className="rounded-full bg-amber/10 px-1.5 text-[10px] font-semibold text-amber-300">
            {t('contacts.hub.windowClosed')}
          </span>
        )}
      </div>
      <div className="mt-0.5 flex items-center justify-between gap-2 text-[11px]">
        <span className="min-w-0 truncate text-text-muted">
          {ident.account_name
            ? ident.account_provider
              ? t('contacts.hub.viaProvider', { name: ident.account_name, provider: ident.account_provider })
              : t('contacts.hub.via', { name: ident.account_name })
            : ident.registered && ident.message_count === 0
              ? t(source ?? 'contacts.hub.registered')
              : t('contacts.hub.noProvider')}
        </span>
        {!(ident.registered && ident.message_count === 0 && !ident.account_name) && (
          <span className="shrink-0 font-semibold" style={{ color: health.ink }}>
            {healthLabel}
          </span>
        )}
      </div>
    </div>
  )
}

function ChannelRow({ ch }: { ch: HubChannel }) {
  const t = useT()
  const rel = useRel()
  const [open, setOpen] = useState(false)
  const meta = CH_META[ch.channel] ?? CH_META.whatsapp
  const name = CH_META[ch.channel] ? chName(t, ch.channel) : meta.lbl
  const idents = ch.identities ?? []
  const isVoice = ch.channel === 'voice'
  const expandable = isVoice ? Boolean(ch.last_outcome) : idents.length > 0
  const count = isVoice ? (ch.calls ?? 0) : idents.reduce((n, i) => n + i.message_count, 0)
  const lastActive = isVoice ? (ch.last_call ?? null) : (ch.last_active ?? null)

  return (
    <div className="rounded-lg border border-line bg-card">
      <button
        type="button"
        onClick={() => expandable && setOpen(!open)}
        aria-expanded={open}
        aria-label={t('contacts.hub.channelDetails', { channel: name })}
        className={`flex w-full items-center gap-2.5 px-2.5 py-2 text-left ${
          expandable ? 'cursor-pointer hover:bg-fill' : 'cursor-default'
        }`}
      >
        <ChannelLogo channel={meta.logo} size={24} variant="tile" title="" />
        <span className="min-w-0 flex-1">
          <span className="flex items-center gap-1.5">
            <span className="truncate text-[12.5px] font-semibold text-text-high">{name}</span>
            <span
              className="h-1.5 w-1.5 shrink-0 rounded-full"
              style={{ background: dotColor(ch) }}
              title={ch.connected ? t('common.status.connected') : t('contacts.hub.notConnected')}
            />
            {!isVoice && idents.length > 1 && (
              <span className="shrink-0 rounded-full border border-line px-1.5 text-[10px] text-text-muted">
                {t('contacts.hub.ids', { count: idents.length })}
              </span>
            )}
          </span>
          <span className="mt-px block truncate text-[11px] text-text-muted">
            {t(isVoice ? 'contacts.hub.calls' : 'contacts.hub.msgs', { count })}
            {' · '}
            {lastActive ? rel(lastActive) : t('contacts.hub.noActivity')}
          </span>
        </span>
        {expandable &&
          (open ? (
            <ChevronDown size={12} className="shrink-0 text-text-muted" aria-hidden />
          ) : (
            <ChevronRight size={12} className="shrink-0 text-text-muted" aria-hidden />
          ))}
      </button>
      {open && !isVoice && idents.map((i) => <IdentityRow key={`${ch.channel}-${i.key}`} ident={i} />)}
      {isVoice && ch.number && (
        <div className="border-t border-line px-2.5 py-1.5 text-[11px] text-text-muted">
          <span className="font-semibold text-text-mid">{ch.number}</span>
          {ch.registered ? ` · ${t('contacts.hub.registeredForCalls')}` : ''}
        </div>
      )}
      {open && isVoice && ch.last_outcome && (
        <div className="border-t border-line px-2.5 py-2 text-[11px] text-text-muted">
          <Trans
            k="contacts.hub.lastOutcome"
            values={{ outcome: <span className="font-semibold text-text-mid">{ch.last_outcome}</span> }}
          />
          {ch.last_call && <> · {rel(ch.last_call)}</>}
        </div>
      )}
    </div>
  )
}

const ADD_CHANNELS = ['whatsapp', 'email', 'telegram', 'facebook', 'instagram', 'voice'] as const

const ADD_PLACEHOLDER: Record<string, MessageKey> = {
  whatsapp: 'contacts.hub.ph.whatsapp',
  email: 'contacts.hub.ph.email',
  telegram: 'contacts.hub.ph.telegram',
  facebook: 'contacts.hub.ph.facebook',
  instagram: 'contacts.hub.ph.instagram',
  voice: 'contacts.hub.ph.voice',
}

/** Register a channel identity by hand — POST /contacts/<id>/channels creates
 * (or claims) the thread and links it to this contact. */
function AddIdentity({ contactId, onAdded }: { contactId: number; onAdded: () => void }) {
  const t = useT()
  const addOptions: SelectOption[] = ADD_CHANNELS.map((c) => ({ value: c, label: chName(t, c) }))
  const [channel, setChannel] = useState('whatsapp')
  const [address, setAddress] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const submit = async () => {
    if (!address.trim() || busy) return
    setBusy(true)
    setError(null)
    try {
      const res = await fetch(`${API}/contacts/${contactId}/channels`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', ...authHeaders() },
        body: JSON.stringify({ channel, address: address.trim() }),
      })
      const body = await res.json().catch(() => ({}))
      if (!res.ok) throw new Error((body as { detail?: string }).detail || `HTTP ${res.status}`)
      setAddress('')
      onAdded()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="rounded-lg border border-dashed border-line-strong p-2.5">
      <div className="eyebrow mb-1.5">{t('contacts.hub.addChannel')}</div>
      <div className="flex items-center gap-1.5">
        <Select
          value={channel}
          onChange={setChannel}
          options={addOptions}
          ariaLabel={t('contacts.hub.channelToAdd')}
          className="w-[118px] shrink-0"
        />
        <input
          type="text"
          value={address}
          onChange={(e) => setAddress(e.target.value)}
          onKeyDown={(e) => e.key === 'Enter' && submit()}
          placeholder={ADD_PLACEHOLDER[channel] ? t(ADD_PLACEHOLDER[channel]) : ''}
          aria-label={t('contacts.hub.addressAria')}
          className="h-8 min-w-0 flex-1 rounded-lg border border-line-strong bg-card px-2 text-[12.5px] text-text-high placeholder:text-text-faint focus:border-text-primary focus:outline-none"
        />
        <button
          type="button"
          onClick={submit}
          disabled={busy || !address.trim()}
          title={t('contacts.hub.addIdentity')}
          aria-label={t('contacts.hub.addIdentity')}
          className="grid h-8 w-8 shrink-0 cursor-pointer place-items-center rounded-lg bg-primary text-on-primary transition-opacity hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-35"
        >
          <Plus size={13} aria-hidden />
        </button>
      </div>
      {error && <div role="alert" className="mt-1 text-[11px] text-red-300">{error}</div>}
      <p className="mt-1.5 text-[11px] leading-relaxed text-text-muted">{t('contacts.hub.addHint')}</p>
    </div>
  )
}

export default function ChannelHub({
  contactId,
  allowAdd = false,
}: {
  contactId: number | null | undefined
  /** Show the add-identity form (the contact detail page turns this on). */
  allowAdd?: boolean
}) {
  const t = useT()
  const [hub, setHub] = useState<HubPayload | null>(null)
  const [failed, setFailed] = useState(false)
  const [reloadTick, setReloadTick] = useState(0)
  const { can } = useAccess()
  const router = useRouter()
  const manage = allowAdd && can('contacts.manage')
  const [finding, setFinding] = useState(false)
  const [findMsg, setFindMsg] = useState<string | null>(null)
  const [inviteOpen, setInviteOpen] = useState(false)

  const findChannels = async () => {
    if (contactId == null) return
    setFinding(true)
    setFindMsg(null)
    try {
      const r = await cApi<DiscoverResult>(`contacts/${contactId}/channels/discover`, { method: 'POST' })
      const parts = [
        r.added > 0 ? t('contacts.hub.newIdentities', { count: r.added }) : t('contacts.hub.nothingNew'),
        r.queued > 0 ? t('contacts.hub.checksQueued', { count: r.queued }) : '',
        !r.lookup_available ? t('contacts.hub.unverifiedNote') : '',
      ].filter(Boolean)
      setFindMsg(parts.join(' · '))
      setReloadTick((n) => n + 1)
      // a queued lookup lands within seconds — look again shortly
      if (r.queued > 0) setTimeout(() => setReloadTick((n) => n + 1), 4000)
    } catch (e) {
      setFindMsg(errText(e))
    } finally {
      setFinding(false)
    }
  }

  /** Open (creating if needed) the chat for a registered-only identity. */
  const openChat = async (channel: string, address: string) => {
    if (contactId == null) return
    try {
      const r = await cApi<{ key: string }>(`contacts/${contactId}/channels`, {
        method: 'POST',
        body: JSON.stringify({ channel, address }),
      })
      router.push(`/chats?thread=${encodeURIComponent(`${channel}:${r.key}`)}`)
    } catch (e) {
      setFindMsg(errText(e))
    }
  }

  useEffect(() => {
    setFailed(false)
    if (contactId == null) return
    let cancelled = false
    fetch(`${API}/contacts/${contactId}/channels`, { headers: authHeaders() })
      .then((r) => {
        if (!r.ok) throw new Error(`HTTP ${r.status}`)
        return r.json() as Promise<HubPayload>
      })
      .then((data) => {
        if (!cancelled) setHub(data)
      })
      .catch(() => {
        if (!cancelled) setFailed(true)
      })
    return () => {
      cancelled = true
    }
  }, [contactId, reloadTick])

  // A different contact's profile must not flash while the new one loads.
  useEffect(() => {
    setHub(null)
  }, [contactId])

  if (contactId == null) return null
  if (failed) {
    return <div className="text-[11.5px] text-text-muted">{t('contacts.hub.loadFailed')}</div>
  }
  if (!hub) {
    return <div className="text-[11.5px] text-text-muted">{t('contacts.hub.loading')}</div>
  }
  const chats = hub.channels.flatMap((ch) =>
    (ch.identities ?? [])
      .filter((i) => i.message_count > 0 || !i.registered)
      .map((i) => ({ channel: ch.channel, key: i.key, label: t('contacts.hub.chatLabel', { channel: chName(t, ch.channel) }) })),
  )
  const registeredOnly = hub.channels.flatMap((ch) =>
    (ch.identities ?? [])
      .filter((i) => i.registered && i.message_count === 0 && (ch.channel === 'whatsapp' || ch.channel === 'email'))
      .map((i) => ({ channel: ch.channel, address: i.address })),
  )
  const noLookup = (hub.no_lookup_channels ?? []).map((c) => NO_LOOKUP_LABELS[c] ?? c)

  return (
    <div className="space-y-1.5">
      {manage && (
        <div className="flex flex-wrap items-center gap-1.5 pb-1">
          <button
            type="button"
            onClick={findChannels}
            disabled={finding}
            className="inline-flex h-7 items-center gap-1.5 rounded-lg border border-line-strong bg-card px-2.5 text-[11.5px] font-semibold text-text-high transition-colors hover:bg-fill-hover disabled:opacity-50"
          >
            <RefreshCw size={11} className={finding ? 'animate-spin' : ''} aria-hidden /> {t('contacts.hub.findChannels')}
          </button>
          <button
            type="button"
            onClick={() => setInviteOpen(true)}
            className="inline-flex h-7 items-center gap-1.5 rounded-lg border border-line-strong bg-card px-2.5 text-[11.5px] font-semibold text-text-high transition-colors hover:bg-fill-hover"
          >
            <Send size={11} aria-hidden /> {t('contacts.hub.invite')}
          </button>
        </div>
      )}
      {findMsg && (
        <div role="status" className="text-[11px] text-text-muted">
          {findMsg}
        </div>
      )}
      {hub.channels.length === 0 && (
        <div className="text-[11.5px] text-text-muted">{t('contacts.hub.noChannels')}</div>
      )}
      {hub.channels.map((ch) => (
        <ChannelRow key={ch.channel} ch={ch} />
      ))}
      {manage && registeredOnly.length > 0 && (
        <div className="flex flex-wrap gap-1.5">
          {registeredOnly.map((r) => (
            <button
              key={`${r.channel}:${r.address}`}
              type="button"
              onClick={() => openChat(r.channel, r.address)}
              className="inline-flex items-center gap-1 rounded-md px-1.5 py-0.5 text-[11px] font-semibold text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
              title={t('contacts.hub.openChatHint')}
            >
              <MessageSquarePlus size={11} aria-hidden /> {t('contacts.hub.openChat', { channel: chName(t, r.channel) })}
            </button>
          ))}
        </div>
      )}
      {allowAdd && (
        <p className="rounded-lg bg-fill-subtle px-2.5 py-2 text-[11px] leading-relaxed text-text-muted">
          {hub.lookup_available === false ? `${t('contacts.hub.cloudNote')} ` : ''}
          {noLookup.length > 0 &&
            t('contacts.hub.noLookupNote', { apps: `${noLookup.slice(0, 6).join(', ')}${noLookup.length > 6 ? '…' : ''}` })}
        </p>
      )}
      {allowAdd && (
        <AddIdentity contactId={contactId} onAdded={() => setReloadTick((n) => n + 1)} />
      )}
      {manage && (
        <InviteModal
          open={inviteOpen}
          onClose={() => setInviteOpen(false)}
          contactId={contactId}
          contactName={hub.name}
          available={hub.invite_channels ?? []}
          threads={chats}
        />
      )}
    </div>
  )
}
