'use client'

/**
 * ChannelHub — the per-contact communication hub: which channels/identities a
 * contact is reachable on, through which provider accounts, connection status,
 * last activity and history counts. Data: GET /api/contacts/<id>/channels,
 * fetched whenever contactId changes (no polling — the hub is a profile, not
 * a live feed). Rendered in the /chats info panel and on the contact detail
 * page; parents supply their own section header / card chrome.
 *
 * Theming: semantic tokens only; icon/dot colors are static hex (never
 * `var()` in a lucide color prop); health text uses the *-ink vars via
 * HEALTH_META so it flips with light/dark.
 */

import { useEffect, useState } from 'react'
import {
  ChevronDown,
  ChevronRight,
  Facebook,
  Instagram,
  Mail,
  MessageCircle,
  Mic,
  Plus,
  Send,
  type LucideIcon,
} from 'lucide-react'
import { authHeaders } from '@/lib/auth'
import { HEALTH_META, relTime, type HealthStatus } from '@/lib/channelHealth'
import { Select, type SelectOption } from '@/components/ui/Select'
import { tint } from '@/lib/color'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

export interface ChannelIdentity {
  key: string
  address: string
  name: string
  account_id: string | null
  account_name: string | null
  account_provider: 'cloud' | 'baileys' | null
  account_health: HealthStatus
  last_active: string | null
  message_count: number
  inbound_count: number
  outbound_count: number
  last_direction: 'in' | 'out' | null
  opted_out: boolean
  window_open: boolean | null
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
}

export interface HubPayload {
  contact_id: number
  name: string
  phone: string
  email: string
  channels: HubChannel[]
}

const CH_META: Record<string, { lbl: string; color: string; Icon: LucideIcon }> = {
  whatsapp: { lbl: 'WhatsApp', color: 'rgb(var(--c-green))', Icon: MessageCircle },
  email: { lbl: 'Email', color: 'rgb(var(--c-amber))', Icon: Mail },
  facebook: { lbl: 'Messenger', color: 'rgb(var(--c-brand))', Icon: Facebook },
  instagram: { lbl: 'Instagram', color: '#E1306C', Icon: Instagram },
  telegram: { lbl: 'Telegram', color: '#229ED9', Icon: Send },
  voice: { lbl: 'AI Voice', color: 'rgb(var(--c-brand))', Icon: Mic },
}

/** Connection dot: emerald = connected via a healthy account, amber =
 * connected but degraded/unknown health, red = not currently reachable. */
function dotColor(ch: HubChannel): string {
  if (!ch.connected) return 'rgb(var(--c-red))'
  if (ch.channel === 'voice') return 'rgb(var(--c-green))'
  return (ch.identities ?? []).some((i) => i.account_health === 'healthy') ? 'rgb(var(--c-green))' : 'rgb(var(--c-amber))'
}

function IdentityRow({ ident }: { ident: ChannelIdentity }) {
  const health = HEALTH_META[ident.account_health] ?? HEALTH_META.unknown
  return (
    <div className="border-t border-line px-2.5 py-2">
      <div className="flex items-baseline justify-between gap-2">
        <span className="min-w-0 truncate text-[11.5px] font-semibold text-text-high">
          {ident.address || ident.key}
        </span>
        <span className="shrink-0 text-[10px] text-text-faint">{relTime(ident.last_active)}</span>
      </div>
      <div className="mt-0.5 flex flex-wrap items-center gap-x-2 gap-y-0.5 text-[10.5px] text-text-muted">
        <span>
          {ident.message_count} msgs · {ident.inbound_count} in / {ident.outbound_count} out
        </span>
        {ident.opted_out && (
          <span className="rounded bg-red/15 px-1 py-px text-[9px] font-bold uppercase text-red-300">
            Opted out
          </span>
        )}
        {ident.window_open === false && (
          <span className="rounded bg-amber/15 px-1 py-px text-[9px] font-bold uppercase text-amber-300">
            24h window closed
          </span>
        )}
      </div>
      <div className="mt-0.5 flex items-center justify-between gap-2 text-[10.5px]">
        <span className="min-w-0 truncate text-text-muted">
          {ident.account_name
            ? `via ${ident.account_name}${ident.account_provider ? ` (${ident.account_provider})` : ''}`
            : 'No provider account'}
        </span>
        <span className="shrink-0 font-semibold" style={{ color: health.ink }}>
          {health.lbl}
        </span>
      </div>
    </div>
  )
}

function ChannelRow({ ch }: { ch: HubChannel }) {
  const [open, setOpen] = useState(false)
  const meta = CH_META[ch.channel] ?? CH_META.whatsapp
  const idents = ch.identities ?? []
  const isVoice = ch.channel === 'voice'
  const expandable = isVoice ? Boolean(ch.last_outcome) : idents.length > 0
  const count = isVoice ? (ch.calls ?? 0) : idents.reduce((n, i) => n + i.message_count, 0)
  const lastActive = isVoice ? (ch.last_call ?? null) : (ch.last_active ?? null)

  return (
    <div className="rounded-lg border border-line bg-fill">
      <button
        type="button"
        onClick={() => expandable && setOpen(!open)}
        aria-expanded={open}
        aria-label={`${meta.lbl} channel details`}
        className={`flex w-full items-center gap-2 px-2.5 py-2 text-left ${
          expandable ? 'cursor-pointer' : 'cursor-default'
        }`}
      >
        <span
          className="grid h-6 w-6 shrink-0 place-items-center rounded-md"
          style={{ background: tint(meta.color, '1F') }}
        >
          <meta.Icon size={13} strokeWidth={1.75} style={{ color: meta.color }} />
        </span>
        <span className="min-w-0 flex-1">
          <span className="flex items-center gap-1.5">
            <span
              className="h-1.5 w-1.5 shrink-0 rounded-full"
              style={{ background: dotColor(ch) }}
              title={ch.connected ? 'Connected' : 'Not connected'}
            />
            <span className="truncate text-[12px] font-semibold text-text-high">{meta.lbl}</span>
            {!isVoice && idents.length > 1 && (
              <span className="shrink-0 rounded bg-fill-hover px-1 py-px text-[9.5px] font-bold text-text-muted">
                {idents.length} ids
              </span>
            )}
          </span>
          <span className="mt-px block truncate text-[10.5px] text-text-muted">
            {isVoice
              ? `${count} call${count === 1 ? '' : 's'}`
              : `${count} msg${count === 1 ? '' : 's'}`}
            {' · '}
            {lastActive ? relTime(lastActive) : 'no activity'}
          </span>
        </span>
        {expandable &&
          (open ? (
            <ChevronDown size={12} className="shrink-0 text-text-faint" aria-hidden />
          ) : (
            <ChevronRight size={12} className="shrink-0 text-text-faint" aria-hidden />
          ))}
      </button>
      {open && !isVoice && idents.map((i) => <IdentityRow key={`${ch.channel}-${i.key}`} ident={i} />)}
      {open && isVoice && ch.last_outcome && (
        <div className="border-t border-line px-2.5 py-2 text-[10.5px] text-text-muted">
          Last outcome: <span className="font-semibold text-text-mid">{ch.last_outcome}</span>
          {ch.last_call && <> · {relTime(ch.last_call)}</>}
        </div>
      )}
    </div>
  )
}

const ADD_CHANNELS: SelectOption[] = [
  { value: 'whatsapp', label: 'WhatsApp' },
  { value: 'email', label: 'Email' },
  { value: 'telegram', label: 'Telegram' },
  { value: 'facebook', label: 'Messenger' },
  { value: 'instagram', label: 'Instagram' },
  { value: 'voice', label: 'AI Voice' },
]

const ADD_PLACEHOLDER: Record<string, string> = {
  whatsapp: 'Phone e.g. 60123456789',
  email: 'name@example.com',
  telegram: 'Telegram chat ID',
  facebook: 'Messenger PSID',
  instagram: 'Instagram IGSID',
  voice: 'Phone e.g. +60123456789',
}

/** Register a channel identity by hand — POST /contacts/<id>/channels creates
 * (or claims) the thread and links it to this contact. */
function AddIdentity({ contactId, onAdded }: { contactId: number; onAdded: () => void }) {
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
    <div className="rounded-lg border border-dashed border-line p-2">
      <div className="mb-1.5 text-[10px] font-bold uppercase tracking-[0.08em] text-text-muted">
        Add channel
      </div>
      <div className="flex items-center gap-1.5">
        <Select
          value={channel}
          onChange={setChannel}
          options={ADD_CHANNELS}
          ariaLabel="Channel to add"
          className="w-[118px] shrink-0"
        />
        <input
          type="text"
          value={address}
          onChange={(e) => setAddress(e.target.value)}
          onKeyDown={(e) => e.key === 'Enter' && submit()}
          placeholder={ADD_PLACEHOLDER[channel]}
          aria-label="Channel address"
          className="h-8 min-w-0 flex-1 rounded-lg border border-line bg-fill px-2 text-[12px] text-text-high placeholder:text-text-faint focus:border-line-strong focus:outline-none"
        />
        <button
          type="button"
          onClick={submit}
          disabled={busy || !address.trim()}
          title="Add channel identity"
          aria-label="Add channel identity"
          className="grid h-8 w-8 shrink-0 cursor-pointer place-items-center rounded-lg border border-line bg-fill text-text-muted transition-colors hover:text-text-high disabled:cursor-not-allowed disabled:opacity-35"
        >
          <Plus size={13} aria-hidden />
        </button>
      </div>
      {error && <div className="mt-1 text-[10.5px] text-red-300">{error}</div>}
      <p className="mt-1 text-[10px] leading-relaxed text-text-faint">
        WhatsApp numbers and emails are ready to message right away. Telegram / Messenger /
        Instagram IDs come from the platform — the customer must have messaged the bot or page
        once.
      </p>
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
  const [hub, setHub] = useState<HubPayload | null>(null)
  const [failed, setFailed] = useState(false)
  const [reloadTick, setReloadTick] = useState(0)

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
    return <div className="text-[10.5px] text-text-faint">Couldn’t load channel info.</div>
  }
  if (!hub) {
    return <div className="text-[10.5px] text-text-faint">Loading channels…</div>
  }
  return (
    <div className="space-y-1.5">
      {hub.channels.length === 0 && (
        <div className="text-[10.5px] text-text-faint">No channels on record yet.</div>
      )}
      {hub.channels.map((ch) => (
        <ChannelRow key={ch.channel} ch={ch} />
      ))}
      {allowAdd && (
        <AddIdentity contactId={contactId} onAdded={() => setReloadTick((t) => t + 1)} />
      )}
    </div>
  )
}
