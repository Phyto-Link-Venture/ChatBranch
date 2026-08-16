'use client'

import { useCallback, useEffect, useState } from 'react'
import {
  CheckCircle,
  ChevronDown,
  Contact,
  Copy,
  FlaskConical,
  Link2,
  Loader2,
  Pencil,
  Plug,
  Plus,
  QrCode,
  Trash2,
  User,
} from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Select } from '@/components/ui/Select'
import { Modal } from '@/components/ui/Modal'
import { DataTable, type Column } from '@/components/ui/DataTable'
import { HealthBadge, RoleChip, accountRole, type AccountRole } from '@/components/ui/Badges'
import { roleHeaders } from '@/lib/role'
import { expireSessionOn401, hasCap } from '@/lib/auth'
import { fetchChannelHealth, type HealthAccount } from '@/lib/channelHealth'
import { EntityScopeSelect } from './EntityScopeSelect'
import { LLM_PROVIDER_LABELS, STT_PROVIDER_LABELS } from '@/lib/voiceProviders'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

interface WaAccount {
  id: string
  provider: 'cloud' | 'baileys'
  name: string
  phone_number_id: string
  waba_id?: string
  api_version: string
  owner: string
  created: string
  access_token_masked: string
  app_secret_masked: string
  configured: boolean
  env_managed: boolean
  /** Operating-model role — backends mid-flight may omit it (defaults:
   *  cloud → reminder, baileys → conversation; see accountRole()). */
  role?: AccountRole
  /** E.164-ish number when the backend knows it (e.g. paired Baileys row). */
  number?: string
  /** Owning outlet slug; null = organization-wide (the shared fallback).
   *  This is what attributes inbound chats to a branch without any tagging. */
  entity?: string | null
  entity_name?: string
  /** Per-account delivery + AI overrides ("" = inherit the organization-wide
   *  value). Edited from the account's Delivery & AI section. */
  delivery?: AccountDelivery
}

/** Keys mirror ChannelAccount.config on the backend (AccountDelivery.Keys). */
interface AccountDelivery {
  quiet_start: string
  quiet_end: string
  /** JSON per-weekday schedule; "" = use the single quiet_start/end window. */
  quiet_schedule: string
  queue_min_gap_s: string
  queue_max_gap_s: string
  llm_provider: string
  stt_provider: string
}

/** Weekdays, Monday first — mirrors AccountDelivery.Days on the server. */
const DAYS = [
  { key: 'mon', label: 'Monday' },
  { key: 'tue', label: 'Tuesday' },
  { key: 'wed', label: 'Wednesday' },
  { key: 'thu', label: 'Thursday' },
  { key: 'fri', label: 'Friday' },
  { key: 'sat', label: 'Saturday' },
  { key: 'sun', label: 'Sunday' },
] as const

const HHMM = /^([01]\d|2[0-3]):[0-5]\d$/

interface QuietDay {
  on: boolean
  start: string
  end: string
}

const emptyQuietDays = (): Record<string, QuietDay> =>
  Object.fromEntries(DAYS.map((d) => [d.key, { on: false, start: '21:00', end: '09:00' }]))

/** Stored schedule → working copy; null when nothing usable is stored. */
function parseQuietSchedule(raw: string): Record<string, QuietDay> | null {
  if (!raw.trim()) return null
  try {
    const parsed = JSON.parse(raw) as Record<string, Partial<QuietDay>>
    const days = emptyQuietDays()
    let any = false
    for (const d of DAYS) {
      const r = parsed?.[d.key]
      if (!r) continue
      days[d.key] = {
        on: r.on !== false,
        start: typeof r.start === 'string' ? r.start : days[d.key].start,
        end: typeof r.end === 'string' ? r.end : days[d.key].end,
      }
      any = true
    }
    return any ? days : null
  } catch {
    return null // malformed = fall back to the single window
  }
}

const EMPTY_DELIVERY: AccountDelivery = {
  quiet_start: '',
  quiet_end: '',
  quiet_schedule: '',
  queue_min_gap_s: '',
  queue_max_gap_s: '',
  llm_provider: '',
  stt_provider: '',
}

/** Organization-wide fallbacks, shown as "inherit" placeholders in the editor. */
interface DeliveryDefaults {
  quiet_start: string
  quiet_end: string
  queue_min_gap_s: string
  queue_max_gap_s: string
  llm_provider: string
  stt_provider: string
}

interface BaileysState {
  state: 'stopped' | 'starting' | 'qr' | 'connected' | 'disconnected' | 'logged_out' | 'error'
  qr: string | null
  me: string | null
  error: string | null
}

async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}/whatsapp/${path}`, {
    headers: { 'Content-Type': 'application/json', ...roleHeaders() },
    ...init,
  })
  const data = await res.json().catch(() => ({}))
  if (expireSessionOn401(res)) throw new Error('Session expired — redirecting to login…')
  if (!res.ok) throw new Error((data as { detail?: string }).detail || `HTTP ${res.status}`)
  return data as T
}

export function CopyRow({ label, value }: { label: string; value: string }) {
  const [copied, setCopied] = useState(false)
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(value)
      setCopied(true)
      setTimeout(() => setCopied(false), 1500)
    } catch {
      /* clipboard blocked — user can select the text manually */
    }
  }
  return (
    <div className="flex items-center gap-2 rounded-lg border border-line bg-fill px-3 py-2">
      <div className="min-w-0 flex-1">
        <div className="text-[10px] font-bold uppercase tracking-[0.07em] text-text-muted">{label}</div>
        <div className="truncate font-mono text-[11.5px] text-text-high">{value}</div>
      </div>
      <button
        onClick={copy}
        title={`Copy ${label}`}
        aria-label={`Copy ${label}`}
        className="grid h-7 w-7 shrink-0 cursor-pointer place-items-center rounded-md text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
      >
        {copied ? <CheckCircle size={13} className="text-green-300" aria-hidden /> : <Copy size={13} aria-hidden />}
      </button>
    </div>
  )
}

const inputCls =
  'w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[12.5px] text-text-primary outline-none transition-colors focus:border-brand-400'

/**
 * QR pairing dialog for Baileys accounts — hosted in the reusable Modal.
 * Starts the gateway session on open and polls its status every 2s until the
 * phone scans the code. The poller lives in a useEffect whose cleanup clears
 * the interval, so closing the modal (this component unmounts when `pairAcct`
 * clears) stops the polling — no leak.
 */
function BaileysPairModal({ account, onClose }: { account: WaAccount; onClose: () => void }) {
  const [state, setState] = useState<BaileysState | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false
    api(`accounts/${account.id}/baileys/start`, { method: 'POST' }).catch((e) =>
      setError(e instanceof Error ? e.message : String(e)),
    )
    // Poll while pairing is in progress; STOP on a terminal state — connected,
    // or a gateway error / "not attached" (state=stopped|error with an error
    // message). Without this the modal spun on "Contacting the gateway…"
    // forever when no Baileys sidecar is wired up.
    let t: ReturnType<typeof setInterval> | null = null
    const stop = () => {
      if (t) clearInterval(t)
      t = null
    }
    const tick = async () => {
      try {
        const s = await api<BaileysState>(`accounts/${account.id}/baileys/status`)
        if (cancelled) return
        setState(s)
        const gatewayError = s.error || (s.state === 'error' ? 'The WhatsApp gateway reported an error.' : null)
        if (gatewayError && s.state !== 'qr' && s.state !== 'connected') {
          setError(gatewayError)
          stop()
          return
        }
        setError(null)
        if (s.state === 'connected') stop()
      } catch (e) {
        if (!cancelled) {
          setError(e instanceof Error ? e.message : String(e))
          stop()
        }
      }
    }
    tick()
    t = setInterval(tick, 2000)
    return () => {
      cancelled = true
      stop()
    }
  }, [account.id])

  return (
    <Modal
      open
      onClose={onClose}
      title={
        <span className="flex items-center gap-2">
          <QrCode size={16} className="shrink-0 text-green-300" aria-hidden /> Pair {account.name}
        </span>
      }
      subtitle="On the phone: WhatsApp → Settings → Linked devices → Link a device"
      size="sm"
    >
      <div className="flex flex-col items-center gap-3 py-2 text-center">
        {error && (
          <div className="flex flex-col items-center gap-3">
            <div className="text-[12px] font-semibold text-red-300">{error}</div>
            <div className="max-w-[320px] text-[11px] leading-relaxed text-text-muted">
              QR pairing runs in the Baileys gateway sidecar (backend/baileys_service). Start it and
              retry — or use a WhatsApp Business (Cloud API) account, which needs no gateway.
            </div>
            <Button variant="secondary" onClick={onClose}>
              Close
            </Button>
          </div>
        )}
        {!error && state?.state === 'connected' && (
          <>
            <CheckCircle size={40} className="text-green-300" aria-hidden />
            <div className="text-[13px] font-bold text-text-high">Connected{state.me ? ` as +${state.me}` : ''}</div>
            <div className="text-[11.5px] text-text-muted">
              This number now sends and receives through the unified inbox.
            </div>
            <Button variant="primary" onClick={onClose}>
              Done
            </Button>
          </>
        )}
        {!error && state?.qr && state.state === 'qr' && (
          <>
            {/* data-URL QR from the gateway — rendered white-boxed for scan contrast */}
            {/* eslint-disable-next-line @next/next/no-img-element */}
            <img src={state.qr} alt="WhatsApp pairing QR code" className="h-56 w-56 rounded-xl bg-white p-2" />
            <div className="text-[11.5px] text-text-muted">
              Scan with the phone that owns this WhatsApp number. The code refreshes automatically.
            </div>
          </>
        )}
        {!error && !state?.qr && state?.state !== 'connected' && (
          <div className="flex items-center gap-2 py-8 text-[12px] text-text-muted">
            <Loader2 size={14} className="animate-spin" aria-hidden />
            {state?.state === 'logged_out'
              ? 'Session was logged out — generating a fresh QR…'
              : 'Contacting the WhatsApp gateway…'}
          </div>
        )}
      </div>
    </Modal>
  )
}

/**
 * WhatsApp ACCOUNTS manager — Whatomate's model: every admin can link any
 * number of WhatsApp Business accounts (name + phone number ID + token) or
 * QR-pair a personal number (Baileys). The account list is a DataTable; "Add
 * account" and a row's Edit both open the reusable Modal (Baileys' multi-step
 * QR pairing runs in a modal too). Inbound webhook traffic is routed to the
 * right account automatically by the receiving number. Full linking
 * walkthrough: backend/whatsapp/SETUP.md.
 */
export function WhatsAppSettings({
  onAccountsChanged,
}: {
  /** Fires with the account count after each refresh — lets the Channels
   *  picker keep its "N accounts" label in sync. Must be referentially
   *  stable (useCallback) or the panel refetches every render. */
  /** Called with (total, cloud) account counts — templates are a Cloud API
   *  feature, so the parent gates the template manager on `cloud`. */
  onAccountsChanged?: (count: number, cloud: number) => void
} = {}) {
  const [accounts, setAccounts] = useState<WaAccount[] | null>(null)
  const [verifyToken, setVerifyToken] = useState('chatbranch-verify')
  const [showAdd, setShowAdd] = useState(false)
  const [method, setMethod] = useState<'cloud' | 'baileys'>('cloud')
  const [pairAcct, setPairAcct] = useState<WaAccount | null>(null)
  const [name, setName] = useState('')
  const [pnid, setPnid] = useState('')
  const [wabaId, setWabaId] = useState('')
  const [token, setToken] = useState('')
  const [secret, setSecret] = useState('')
  const [busy, setBusy] = useState(false)
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)
  // account id → health snapshot; empty while the health endpoint is missing
  // or unreachable — the badges just don't render then.
  const [healthMap, setHealthMap] = useState<Record<string, HealthAccount>>({})
  // capability read after mount (localStorage) — account management is gated on
  // channels.manage (admin+ by default), so users without it get a read-only
  // view (chips only, no add/edit/delete/role switcher).
  const [canManage, setCanManage] = useState(false)
  useEffect(() => setCanManage(hasCap('channels.manage')), [])
  // collapsible advanced/webhook section — one-time developer setup, folded away
  const [showWebhook, setShowWebhook] = useState(false)
  // edit-existing-account form (separate fields so it can't clash with Add)
  const [editAcct, setEditAcct] = useState<WaAccount | null>(null)
  const [eName, setEName] = useState('')
  const [ePnid, setEPnid] = useState('')
  const [eWaba, setEWaba] = useState('')
  const [eToken, setEToken] = useState('')
  const [eSecret, setESecret] = useState('')
  const [eDelivery, setEDelivery] = useState<AccountDelivery>(EMPTY_DELIVERY)
  // Quiet hours have two modes: one window for every day, or a per-weekday
  // schedule. `quietDays` is the working copy; it is serialised into
  // delivery.quiet_schedule on save (and cleared when the toggle is off).
  const [perDayQuiet, setPerDayQuiet] = useState(false)
  const [quietDays, setQuietDays] = useState<Record<string, QuietDay>>(emptyQuietDays)
  const setQuietDay = (key: string, patch: Partial<QuietDay>) =>
    setQuietDays((prev) => ({ ...prev, [key]: { ...prev[key], ...patch } }))
  const [deliveryDefaults, setDeliveryDefaults] = useState<DeliveryDefaults | null>(null)

  const refresh = useCallback(async () => {
    // non-blocking — the account list must load even if health checks fail
    fetchChannelHealth()
      .then((d) => setHealthMap(Object.fromEntries(d.accounts.map((x) => [x.id, x]))))
      .catch(() => {})
    try {
      const [a, s] = await Promise.all([
        api<{ accounts: WaAccount[] }>('accounts'),
        api<{ verify_token: string }>('settings'),
      ])
      setAccounts(a.accounts)
      setVerifyToken(s.verify_token)
      onAccountsChanged?.(a.accounts.length, a.accounts.filter((x) => x.provider !== 'baileys').length)
    } catch {
      setAccounts(null)
    }
  }, [onAccountsChanged])

  useEffect(() => {
    refresh()
  }, [refresh])

  const run = async (fn: () => Promise<unknown>, okText?: string) => {
    setBusy(true)
    setMsg(null)
    try {
      await fn()
      await refresh()
      if (okText) setMsg({ ok: true, text: okText })
    } catch (e) {
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    } finally {
      setBusy(false)
    }
  }

  const openAdd = () => {
    setName('')
    setPnid('')
    setWabaId('')
    setToken('')
    setSecret('')
    setMethod('cloud')
    setMsg(null)
    setShowAdd(true)
  }

  const addAccount = () =>
    run(async () => {
      const created = await api<WaAccount>('accounts', {
        method: 'POST',
        body: JSON.stringify(
          method === 'baileys'
            ? { name, provider: 'baileys' }
            : { name, phone_number_id: pnid, waba_id: wabaId, access_token: token, app_secret: secret },
        ),
      })
      setName('')
      setPnid('')
      setWabaId('')
      setToken('')
      setSecret('')
      setShowAdd(false)
      if (created.provider === 'baileys') setPairAcct(created) // straight into QR pairing
    }, method === 'baileys' ? 'Account created — scan the QR to pair.' : 'Account added — live immediately.')

  const startEdit = (a: WaAccount) => {
    setEditAcct(a)
    setEName(a.name)
    setEPnid(a.phone_number_id || '')
    setEWaba(a.waba_id || '')
    setEToken('')
    setESecret('')
    const delivery = { ...EMPTY_DELIVERY, ...(a.delivery ?? {}) }
    setEDelivery(delivery)
    const parsed = parseQuietSchedule(delivery.quiet_schedule)
    setPerDayQuiet(parsed !== null)
    setQuietDays(parsed ?? emptyQuietDays())
    setMsg(null)
    // The inherited (organization-wide) values, so blank fields can say what
    // they currently resolve to. Best-effort — the editor works without them.
    Promise.allSettled([
      api<{ quiet_start?: string; quiet_end?: string }>('settings'),
      api<{ queue_min_gap_s?: number; queue_max_gap_s?: number }>('routing-rules'),
      fetch(`${API}/voice/llm-settings`, { headers: roleHeaders() }).then((r) => r.json()),
      fetch(`${API}/voice/stt-settings`, { headers: roleHeaders() }).then((r) => r.json()),
    ]).then(([q, r, l, t]) =>
      setDeliveryDefaults({
        quiet_start: (q.status === 'fulfilled' && q.value.quiet_start) || '',
        quiet_end: (q.status === 'fulfilled' && q.value.quiet_end) || '',
        queue_min_gap_s: r.status === 'fulfilled' && r.value.queue_min_gap_s != null ? String(r.value.queue_min_gap_s) : '10',
        queue_max_gap_s: r.status === 'fulfilled' && r.value.queue_max_gap_s != null ? String(r.value.queue_max_gap_s) : '30',
        llm_provider: (l.status === 'fulfilled' && l.value?.llm_provider) || 'openai',
        stt_provider: (t.status === 'fulfilled' && t.value?.stt_provider) || 'elevenlabs',
      }),
    )
  }

  const saveEdit = () => {
    if (!editAcct) return
    run(async () => {
      const patch: Record<string, string> = { name: eName.trim() }
      if (editAcct.provider === 'cloud') {
        if (ePnid.trim()) patch.phone_number_id = ePnid.trim()
        patch.waba_id = eWaba.trim()
        // secrets: blank = keep the stored value (never send an empty string —
        // the backend would blank the key)
        if (eToken.trim()) patch.access_token = eToken.trim()
        if (eSecret.trim()) patch.app_secret = eSecret.trim()
      }
      // Delivery & AI overrides — always sent (blank = clear back to inherit).
      const lo = eDelivery.queue_min_gap_s.trim()
      const hi = eDelivery.queue_max_gap_s.trim()
      if ((lo && !/^\d+$/.test(lo)) || (hi && !/^\d+$/.test(hi)))
        throw new Error('Send pacing gaps must be whole seconds (0 or more).')
      if (lo && hi && Number(lo) > Number(hi)) throw new Error('Min gap cannot exceed max gap.')
      for (const [k, v] of Object.entries(eDelivery)) patch[k] = v.trim()
      // Per-day schedule wins when the toggle is on; turning it off clears the
      // key so the single window takes over again.
      if (perDayQuiet) {
        for (const d of DAYS) {
          const r = quietDays[d.key]
          if (r.on && (!HHMM.test(r.start) || !HHMM.test(r.end)))
            throw new Error(`${d.label} quiet hours must be HH:MM (24h).`)
        }
        patch.quiet_schedule = JSON.stringify(
          Object.fromEntries(DAYS.map((d) => [d.key, quietDays[d.key]])),
        )
      } else {
        patch.quiet_schedule = ''
      }
      await api(`accounts/${editAcct.id}`, { method: 'POST', body: JSON.stringify(patch) })
      setEditAcct(null)
    }, 'Account updated — changes are live immediately.')
  }

  /** Role flip — optimistic with rollback, matching the assignments save
   *  pattern. Contract: POST /api/whatsapp/accounts/<id> {"role": …} (the
   *  account update endpoint only speaks POST/DELETE). */
  const setAccountRoleTo = async (a: WaAccount, role: AccountRole) => {
    if (accounts === null || accountRole(a) === role) return
    const prev = accounts
    setMsg(null)
    setAccounts(accounts.map((x) => (x.id === a.id ? { ...x, role } : x)))
    try {
      await api(`accounts/${a.id}`, { method: 'POST', body: JSON.stringify({ role }) })
    } catch (e) {
      setAccounts(prev)
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    }
  }

  /** Bind (or unbind) this number to an outlet. Optimistic like the role
   *  toggle above; the server re-files the number's existing chats. */
  const bindEntity = async (a: WaAccount, slug: string | null) => {
    if (accounts === null) return
    const prev = accounts
    setMsg(null)
    setAccounts(accounts.map((x) => (x.id === a.id ? { ...x, entity: slug } : x)))
    try {
      await api(`accounts/${a.id}`, { method: 'POST', body: JSON.stringify({ entity: slug }) })
    } catch (e) {
      setAccounts(prev)
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    }
  }

  const removeAccount = (a: WaAccount) => {
    if (!window.confirm(`Remove WhatsApp account "${a.name}"? Its chats stay but go back to sandbox.`)) return
    run(async () => {
      if (a.provider === 'baileys') {
        // best effort unpair — the account row goes regardless
        await api(`accounts/${a.id}/baileys/logout`, { method: 'POST' }).catch(() => {})
      }
      await api(`accounts/${a.id}`, { method: 'DELETE' })
    }, 'Account removed.')
  }

  const testAccount = (a: WaAccount) =>
    run(async () => {
      const r = await api<{ ok: boolean; display_phone_number: string; verified_name: string; quality_rating: string }>(
        `accounts/${a.id}/test`,
        { method: 'POST' },
      )
      setMsg(
        a.provider === 'baileys'
          ? r.ok
            ? { ok: true, text: `${a.name}: paired and connected · ${r.display_phone_number}` }
            : { ok: false, text: `${a.name}: not connected (${r.quality_rating}) — open Pair to scan the QR.` }
          : { ok: true, text: `${a.name}: connected as ${r.verified_name || 'unnamed'} · ${r.display_phone_number}` },
      )
    })

  const exportContacts = async () => {
    try {
      const res = await fetch(`${API}/messaging/contacts/vcard`, { headers: roleHeaders() })
      if (!res.ok) throw new Error(`HTTP ${res.status}`)
      const blob = await res.blob()
      const url = URL.createObjectURL(blob)
      const a = document.createElement('a')
      a.href = url
      a.download = 'contacts.vcf'
      a.click()
      URL.revokeObjectURL(url)
      setMsg({ ok: true, text: 'contacts.vcf downloaded — import it into the paired phone’s contacts.' })
    } catch (e) {
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    }
  }

  if (accounts === null) {
    return (
      <div className="py-6 text-[12px] text-text-muted">
        WhatsApp settings unavailable — is the backend API running?
      </div>
    )
  }

  const webhookUrl = `${API.replace(/\/api$/, '')}/api/whatsapp/webhook`
  const anyConfigured = accounts.some((a) => a.configured)
  const anyBaileys = accounts.some((a) => a.provider === 'baileys')
  const linkedCount = accounts.filter((a) => a.configured).length

  const columns: Column<WaAccount>[] = [
    {
      key: 'account',
      header: 'Account',
      sortable: true,
      value: (a) => `${a.name} ${a.phone_number_id ?? ''} ${a.owner ?? ''}`,
      render: (a) => {
        const h = healthMap[a.id]
        return (
          <div className="flex items-center gap-2.5">
            <div
              className={`grid h-9 w-9 shrink-0 place-items-center rounded-[9px] ${
                a.configured ? 'bg-green/15 text-green-300' : 'bg-fill text-text-muted'
              }`}
            >
              {a.provider === 'baileys' ? <QrCode size={16} aria-hidden /> : <Link2 size={16} aria-hidden />}
            </div>
            <div className="min-w-0">
              <div className="flex flex-wrap items-center gap-1.5">
                <span className="text-[13px] font-bold text-text-high">{a.name}</span>
                {a.provider === 'baileys' && (
                  <span
                    className="shrink-0 rounded-full bg-amber/15 px-1.5 py-px text-[9px] font-bold uppercase text-amber-300"
                    title="Unofficial WhatsApp Web transport (Baileys) — QR paired"
                  >
                    Baileys
                  </span>
                )}
                {a.env_managed && (
                  <span className="shrink-0 rounded-full bg-fill px-1.5 py-px text-[9px] font-bold uppercase text-text-muted">
                    from .env
                  </span>
                )}
                {h && <HealthBadge health={h.health} detail={h.detail} />}
              </div>
              <div className="mt-0.5 flex flex-wrap items-center gap-x-2 text-[11px] text-text-muted">
                {a.provider === 'baileys' ? (
                  <span>QR-paired session · text only · no 24h window</span>
                ) : (
                  <>
                    <span className="font-mono">ID {a.phone_number_id || '—'}</span>
                    {a.waba_id && <span className="font-mono">· WABA {a.waba_id}</span>}
                    <span>· token {a.access_token_masked || 'not set'}</span>
                  </>
                )}
                <span className="flex items-center gap-0.5">
                  · <User size={10} aria-hidden /> {a.owner}
                </span>
              </div>
            </div>
          </div>
        )
      },
    },
    {
      key: 'role',
      header: 'Role',
      width: 200,
      value: (a) => accountRole(a),
      render: (a) =>
        canManage ? (
          <div
            className="flex w-fit shrink-0 overflow-hidden rounded-lg border border-line"
            role="group"
            aria-label={`Role for ${a.name}`}
          >
            {(['reminder', 'conversation'] as const).map((r) => {
              const active = accountRole(a) === r
              return (
                <button
                  key={r}
                  onClick={() => setAccountRoleTo(a, r)}
                  disabled={busy || a.env_managed}
                  aria-pressed={active}
                  title={
                    a.env_managed
                      ? 'Managed by backend/.env'
                      : r === 'reminder'
                        ? 'Reminder (business) — templates auto-send directly, no queue delay (official API)'
                        : 'Conversation (personal) — AI chat, customer-initiated; sends human-paced to avoid bans'
                  }
                  className={`cursor-pointer px-2.5 py-1.5 text-[10.5px] font-bold transition-colors disabled:cursor-not-allowed disabled:opacity-40 ${
                    active ? 'bg-brand-400/15' : 'text-text-muted hover:bg-fill-hover hover:text-text-high'
                  }`}
                  style={active ? { color: 'var(--brand-ink)' } : undefined}
                >
                  {r === 'reminder' ? 'Reminder' : 'Conversation'}
                </button>
              )
            })}
          </div>
        ) : (
          <RoleChip role={accountRole(a)} />
        ),
    },
    {
      key: 'entity',
      header: 'Entity',
      width: 200,
      value: (a) => a.entity ?? '',
      render: (a) => (
        <EntityScopeSelect
          value={a.entity ?? null}
          disabled={!canManage || busy || a.env_managed}
          onChange={(slug) => bindEntity(a, slug)}
          globalLabel="Organization-wide"
          className="max-w-[185px]"
        />
      ),
    },
    {
      key: 'actions',
      header: 'Actions',
      align: 'right',
      render: (a) => (
        // Compact icon actions (with tooltips) so the row never wraps — matches
        // the Users/Contacts/Entities tables.
        <div className="flex items-center justify-end gap-1">
          {canManage && a.provider === 'baileys' && (
            <button
              onClick={() => setPairAcct(a)}
              disabled={busy}
              title="Pair (scan QR)"
              aria-label={`Pair ${a.name}`}
              className="grid h-7 w-7 shrink-0 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-green-300 disabled:cursor-not-allowed disabled:opacity-35"
            >
              <QrCode size={14} aria-hidden />
            </button>
          )}
          <button
            onClick={() => testAccount(a)}
            disabled={busy || !a.configured}
            title="Test connection"
            aria-label={`Test ${a.name}`}
            className="grid h-7 w-7 shrink-0 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high disabled:cursor-not-allowed disabled:opacity-35"
          >
            <Plug size={14} aria-hidden />
          </button>
          {canManage && (
            <>
              <button
                onClick={() => startEdit(a)}
                disabled={busy || a.env_managed}
                title={a.env_managed ? 'Managed by backend/.env' : 'Edit account (keys, delivery & AI)'}
                aria-label={`Edit ${a.name}`}
                className="grid h-7 w-7 shrink-0 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high disabled:cursor-not-allowed disabled:opacity-35"
              >
                <Pencil size={14} aria-hidden />
              </button>
              <button
                onClick={() => removeAccount(a)}
                disabled={busy || a.env_managed}
                title={a.env_managed ? 'Managed by backend/.env' : 'Remove account'}
                aria-label={`Remove ${a.name}`}
                className="grid h-7 w-7 shrink-0 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-red/15 hover:text-red-300 disabled:cursor-not-allowed disabled:opacity-35"
              >
                <Trash2 size={14} aria-hidden />
              </button>
            </>
          )}
        </div>
      ),
    },
  ]

  return (
    <div className="flex flex-col gap-4 pt-3.5">
      <div className="flex flex-wrap items-center gap-2">
        {anyConfigured ? (
          <span className="flex items-center gap-1.5 rounded-full bg-green/15 px-2.5 py-1 text-[11px] font-bold text-green-300">
            <Link2 size={12} aria-hidden /> {linkedCount} account{linkedCount > 1 ? 's' : ''} linked
          </span>
        ) : (
          <span className="flex items-center gap-1.5 rounded-full bg-amber/15 px-2.5 py-1 text-[11px] font-bold text-amber-300">
            <FlaskConical size={12} aria-hidden /> Sandbox — no account linked
          </span>
        )}
      </div>

      {accounts.length > 0 && (
        <p className="text-[11px] leading-relaxed text-text-muted">
          <span className="font-bold text-text-high">Account roles:</span> Reminder (business) numbers send templates
          and hand contacts to conversation numbers —{' '}
          <span className="font-semibold text-text-high">auto-sends go out directly, back to back</span> (official
          API; no queue delay). Conversation (personal) numbers do AI chat, customer-initiated only — their sends are
          deliberately human-paced (45–180s apart) to protect the number from bans.
        </p>
      )}

      {/* account table */}
      <DataTable
        columns={columns}
        rows={accounts}
        rowKey={(a) => a.id}
        searchKeys={['account']}
        searchPlaceholder="Search WhatsApp accounts…"
        empty="No WhatsApp accounts yet. Every admin can link their own — click “Add WhatsApp account”."
        toolbarRight={
          canManage ? (
            <Button variant="primary" onClick={openAdd}>
              <Plus size={13} aria-hidden /> Add WhatsApp account
            </Button>
          ) : undefined
        }
      />

      {msg && (
        <div className={`text-[12px] font-semibold ${msg.ok ? 'text-green-300' : 'text-red-300'}`}>{msg.text}</div>
      )}

      {/* Baileys deliverability: contacts in the phone's address book are not
          "cold contacts", so reminder sends bypass WhatsApp's 463 reach-out lock */}
      {anyBaileys && (
        <div className="flex items-start gap-3 rounded-xl border border-line bg-fill-subtle px-4 py-3">
          <Contact size={16} className="mt-0.5 shrink-0 text-green-300" aria-hidden />
          <div className="min-w-0 flex-1 text-[11.5px] leading-relaxed text-text-mid">
            <span className="font-bold text-text-high">Sending reminders to new numbers?</span> WhatsApp restricts
            first-contact messages from QR-paired accounts (error 463). Export your contacts as contacts and import the
            file into the paired phone (Contacts app → Import) — numbers in the address book are exempt.
          </div>
          <Button variant="secondary" onClick={exportContacts} disabled={busy}>
            <Contact size={13} aria-hidden /> Export contacts (.vcf)
          </Button>
        </div>
      )}

      {/* Advanced / webhook setup — one-time developer config, collapsed by
          default so it doesn't crowd the day-to-day accounts view. One webhook
          serves ALL accounts (routed by the receiving number). */}
      <div className="rounded-xl border border-line bg-card">
        <button
          type="button"
          onClick={() => setShowWebhook((v) => !v)}
          aria-expanded={showWebhook}
          className="flex w-full cursor-pointer items-center gap-2 px-4 py-3 text-left"
        >
          <ChevronDown
            size={15}
            aria-hidden
            className="shrink-0 text-text-muted transition-transform"
            style={{ transform: showWebhook ? 'rotate(0deg)' : 'rotate(-90deg)' }}
          />
          <span className="text-[12.5px] font-bold text-text-high">Advanced · Meta webhook setup</span>
          <span className="text-[11px] text-text-muted">— one-time, shared by all accounts</span>
        </button>
        {showWebhook && (
          <div className="border-t border-line px-4 py-3">
            <div className="grid grid-cols-1 gap-2 sm:grid-cols-2">
              <CopyRow label="Callback URL" value={webhookUrl} />
              <CopyRow label="Verify token" value={verifyToken} />
            </div>
            <p className="mt-2 text-[11px] leading-relaxed text-text-muted">
              Paste these in Meta App Dashboard → WhatsApp → Configuration → Webhook and subscribe to the{' '}
              <code className="rounded bg-fill px-1">messages</code> field. Inbound traffic is routed to the right
              account by the receiving number. Local dev needs a public HTTPS tunnel to the API — see{' '}
              <code className="rounded bg-fill px-1">backend/whatsapp/SETUP.md</code>.
            </p>
          </div>
        )}
      </div>

      {/* add-account modal */}
      <Modal
        open={showAdd}
        onClose={() => setShowAdd(false)}
        title="Add WhatsApp account"
        subtitle="Link an official Cloud API number, or QR-pair a personal number (Baileys)."
        size="lg"
        footer={
          <>
            <Button variant="ghost" onClick={() => setShowAdd(false)} disabled={busy}>
              Cancel
            </Button>
            <Button
              variant="primary"
              onClick={addAccount}
              disabled={busy || (method === 'cloud' ? !pnid.trim() || !token.trim() : !name.trim())}
            >
              {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Plus size={13} aria-hidden />}{' '}
              {method === 'baileys' ? 'Create & show QR' : 'Link account'}
            </Button>
          </>
        }
      >
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
          <label className="block">
            <span className="mb-1 block text-[11px] font-bold text-text-high">Connection method</span>
            <Select
              value={method}
              onChange={(v) => setMethod(v as 'cloud' | 'baileys')}
              options={[
                { value: 'cloud', label: 'Cloud API', hint: '(official, token)' },
                { value: 'baileys', label: 'Baileys', hint: '(QR scan, unofficial)' },
              ]}
              size="md"
              ariaLabel="Connection method"
            />
            {method === 'baileys' && (
              <span className="mt-1 block text-[10.5px] leading-relaxed text-amber-300">
                Unofficial WhatsApp Web protocol — no Meta approval needed, but it violates WhatsApp&apos;s ToS and the
                number can be banned. Use a dedicated/spare number.
              </span>
            )}
          </label>
          <label className="block">
            <span className="mb-1 block text-[11px] font-bold text-text-high">Account name</span>
            <input className={inputCls} value={name} onChange={(e) => setName(e.target.value)} placeholder="e.g. Support MY" />
          </label>
          {method === 'cloud' && (
            <>
              <label className="block">
                <span className="mb-1 block text-[11px] font-bold text-text-high">Phone number ID</span>
                <input className={inputCls} value={pnid} onChange={(e) => setPnid(e.target.value)} placeholder="123456789012345" />
                <span className="mt-1 block text-[10.5px] text-text-muted">
                  Meta App Dashboard → WhatsApp → API Setup — the numeric ID, not the phone number.
                </span>
              </label>
              <label className="block">
                <span className="mb-1 block text-[11px] font-bold text-text-high">WABA ID (for templates)</span>
                <input className={inputCls} value={wabaId} onChange={(e) => setWabaId(e.target.value)} placeholder="WhatsApp Business Account ID" />
                <span className="mt-1 block text-[10.5px] text-text-muted">
                  Same API Setup page — needed to list and send message templates. Optional otherwise.
                </span>
              </label>
              <label className="block">
                <span className="mb-1 block text-[11px] font-bold text-text-high">Access token</span>
                <input className={inputCls} type="password" autoComplete="off" value={token} onChange={(e) => setToken(e.target.value)} placeholder="EAAG…" />
                <span className="mt-1 block text-[10.5px] text-text-muted">
                  Use a System User token for production (dashboard tokens expire in 24h).
                </span>
              </label>
              <label className="block">
                <span className="mb-1 block text-[11px] font-bold text-text-high">App secret (optional)</span>
                <input className={inputCls} type="password" autoComplete="off" value={secret} onChange={(e) => setSecret(e.target.value)} placeholder="enables webhook signature checks" />
              </label>
            </>
          )}
          {method === 'baileys' && (
            <div className="rounded-lg border border-line bg-fill px-3 py-2 text-[11px] leading-relaxed text-text-mid sm:col-span-2">
              <QrCode size={12} className="mb-0.5 mr-1 inline text-green-300" aria-hidden />
              No credentials needed — after creating the account you&apos;ll scan a QR code with the phone, like
              WhatsApp Web. Requires the gateway process:{' '}
              <code className="rounded bg-fill-subtle px-1">npm run dev:baileys</code>. Text messages only (templates,
              media and calls stay on Cloud API accounts); the 24h window does not apply.
            </div>
          )}
        </div>
      </Modal>

      {/* edit-account modal */}
      <Modal
        open={!!editAcct}
        onClose={() => setEditAcct(null)}
        title={editAcct ? `Edit “${editAcct.name}”` : 'Edit account'}
        subtitle="Changes are live immediately. Leave a secret blank to keep the stored value."
        size="lg"
        footer={
          <>
            <Button variant="ghost" onClick={() => setEditAcct(null)} disabled={busy}>
              Cancel
            </Button>
            <Button variant="primary" onClick={saveEdit} disabled={busy || !eName.trim()}>
              {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <CheckCircle size={13} aria-hidden />}{' '}
              Save changes
            </Button>
          </>
        }
      >
        {editAcct && (
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
            {msg && !msg.ok && (
              <div
                role="alert"
                className="sm:col-span-2 rounded-lg border border-red-500/30 bg-red-500/10 px-3 py-2 text-[12px] font-semibold text-red-300"
              >
                {msg.text}
              </div>
            )}
            <label className="block">
              <span className="mb-1 block text-[11px] font-bold text-text-high">Account name</span>
              <input className={inputCls} value={eName} onChange={(e) => setEName(e.target.value)} />
            </label>
            {editAcct.provider === 'cloud' && (
              <>
                <label className="block">
                  <span className="mb-1 block text-[11px] font-bold text-text-high">Phone number ID</span>
                  <input className={inputCls} value={ePnid} onChange={(e) => setEPnid(e.target.value)} />
                </label>
                <label className="block">
                  <span className="mb-1 block text-[11px] font-bold text-text-high">WABA ID (for templates)</span>
                  <input className={inputCls} value={eWaba} onChange={(e) => setEWaba(e.target.value)} />
                </label>
                <label className="block">
                  <span className="mb-1 block text-[11px] font-bold text-text-high">
                    Access token{' '}
                    <span className="font-normal text-text-muted">(current: {editAcct.access_token_masked || 'not set'})</span>
                  </span>
                  <input
                    className={inputCls}
                    type="password"
                    autoComplete="off"
                    value={eToken}
                    onChange={(e) => setEToken(e.target.value)}
                    placeholder="Paste a new token — leave blank to keep the current one"
                  />
                  <span className="mt-1 block text-[10.5px] text-text-muted">
                    Must come from the Meta app that owns this number. Use a System User token for production.
                  </span>
                </label>
                <label className="block">
                  <span className="mb-1 block text-[11px] font-bold text-text-high">
                    App secret{' '}
                    <span className="font-normal text-text-muted">(current: {editAcct.app_secret_masked || 'not set'})</span>
                  </span>
                  <input
                    className={inputCls}
                    type="password"
                    autoComplete="off"
                    value={eSecret}
                    onChange={(e) => setESecret(e.target.value)}
                    placeholder="Paste a new secret — leave blank to keep the current one"
                  />
                </label>
              </>
            )}

            {/* Delivery & AI — per-account knobs (quiet hours, send pacing, the
                voice-call brain + STT). Blank = inherit the organization-wide
                value, shown as the placeholder. Backed by ChannelAccount.config. */}
            <div className="sm:col-span-2 mt-1 border-t border-line pt-3">
              <div className="text-[12px] font-bold text-text-high">Delivery &amp; AI for this account</div>
              <div className="mt-0.5 text-[10.5px] text-text-muted">
                Leave a field blank to inherit the organization-wide default (shown greyed). Applies to messages and
                calls sent through <span className="font-semibold text-text-mid">{editAcct.name}</span> only.
              </div>
            </div>
            <div className="block sm:col-span-2">
              <div className="mb-1 flex flex-wrap items-center gap-2">
                <span className="text-[11px] font-bold text-text-high">Quiet hours</span>
                <label className="flex cursor-pointer items-center gap-1.5 text-[10.5px] text-text-muted">
                  <input
                    type="checkbox"
                    checked={perDayQuiet}
                    onChange={(e) => setPerDayQuiet(e.target.checked)}
                    className="h-3.5 w-3.5 accent-emerald-500"
                  />
                  Different hours per day
                </label>
              </div>
              {!perDayQuiet ? (
                <>
                  <div className="flex items-center gap-1.5">
                    <input
                      className={`${inputCls} max-w-[110px] text-center`}
                      value={eDelivery.quiet_start}
                      onChange={(e) => setEDelivery({ ...eDelivery, quiet_start: e.target.value })}
                      placeholder={deliveryDefaults?.quiet_start || '21:00'}
                      aria-label="Quiet hours start"
                    />
                    <span className="text-[11px] text-text-muted">to</span>
                    <input
                      className={`${inputCls} max-w-[110px] text-center`}
                      value={eDelivery.quiet_end}
                      onChange={(e) => setEDelivery({ ...eDelivery, quiet_end: e.target.value })}
                      placeholder={deliveryDefaults?.quiet_end || '09:00'}
                      aria-label="Quiet hours end"
                    />
                  </div>
                  <span className="mt-1 block text-[10.5px] text-text-muted">
                    Outbound sends from this number are held between these times (HH:MM 24h, server time). A window
                    ending before it starts runs past midnight.
                    {deliveryDefaults && !deliveryDefaults.quiet_start && !deliveryDefaults.quiet_end &&
                      ' Default: none.'}
                  </span>
                </>
              ) : (
                <>
                  <div className="flex flex-col gap-1.5">
                    {DAYS.map((d) => {
                      const rule = quietDays[d.key]
                      return (
                        <div key={d.key} className="flex items-center gap-2">
                          <label className="flex w-[104px] shrink-0 cursor-pointer items-center gap-1.5">
                            <input
                              type="checkbox"
                              checked={rule.on}
                              onChange={(e) => setQuietDay(d.key, { on: e.target.checked })}
                              className="h-3.5 w-3.5 accent-emerald-500"
                              aria-label={`Quiet hours on ${d.label}`}
                            />
                            <span className="text-[11.5px] font-semibold text-text-high">{d.label}</span>
                          </label>
                          <input
                            className={`${inputCls} max-w-[92px] text-center`}
                            value={rule.start}
                            disabled={!rule.on}
                            onChange={(e) => setQuietDay(d.key, { start: e.target.value })}
                            placeholder="21:00"
                            aria-label={`${d.label} quiet start`}
                          />
                          <span className="text-[11px] text-text-muted">to</span>
                          <input
                            className={`${inputCls} max-w-[92px] text-center`}
                            value={rule.end}
                            disabled={!rule.on}
                            onChange={(e) => setQuietDay(d.key, { end: e.target.value })}
                            placeholder="09:00"
                            aria-label={`${d.label} quiet end`}
                          />
                          {rule.on && rule.start === rule.end && rule.start !== '' && (
                            <span className="text-[10px] font-semibold text-amber-300">quiet all day</span>
                          )}
                        </div>
                      )
                    })}
                  </div>
                  <span className="mt-1.5 block text-[10.5px] text-text-muted">
                    Unticked days never go quiet. A window ending before it starts runs past midnight into the next
                    day; setting both to the same time means the whole day is quiet.
                  </span>
                </>
              )}
            </div>
            <div className="block">
              <span className="mb-1 block text-[11px] font-bold text-text-high">Send pacing (seconds)</span>
              <div className="flex items-center gap-1.5">
                <input
                  className={`${inputCls} text-center`}
                  inputMode="numeric"
                  value={eDelivery.queue_min_gap_s}
                  onChange={(e) => setEDelivery({ ...eDelivery, queue_min_gap_s: e.target.value })}
                  placeholder={deliveryDefaults?.queue_min_gap_s || '10'}
                  aria-label="Send pacing minimum gap"
                />
                <span className="text-[11px] text-text-muted">to</span>
                <input
                  className={`${inputCls} text-center`}
                  inputMode="numeric"
                  value={eDelivery.queue_max_gap_s}
                  onChange={(e) => setEDelivery({ ...eDelivery, queue_max_gap_s: e.target.value })}
                  placeholder={deliveryDefaults?.queue_max_gap_s || '30'}
                  aria-label="Send pacing maximum gap"
                />
              </div>
              <span className="mt-1 block text-[10.5px] text-text-muted">
                Jittered gap between this number&apos;s queued sends — paces personal (Baileys) numbers for ban safety.
              </span>
            </div>
            <div className="block">
              <span className="mb-1 block text-[11px] font-bold text-text-high">Voice call brain (LLM)</span>
              <Select
                value={eDelivery.llm_provider}
                onChange={(v) => setEDelivery({ ...eDelivery, llm_provider: v })}
                size="md"
                ariaLabel="Voice call LLM provider"
                options={[
                  {
                    value: '',
                    label: `Inherit default${deliveryDefaults ? ` (${LLM_PROVIDER_LABELS[deliveryDefaults.llm_provider] ?? deliveryDefaults.llm_provider})` : ''}`,
                  },
                  ...Object.entries(LLM_PROVIDER_LABELS).map(([value, label]) => ({ value, label })),
                ]}
              />
              <span className="mt-1 block text-[10.5px] text-text-muted">
                LLM behind ElevenLabs-voice calls from this number — takes effect on the next call.
              </span>
            </div>
            <div className="block">
              <span className="mb-1 block text-[11px] font-bold text-text-high">Call STT engine</span>
              <Select
                value={eDelivery.stt_provider}
                onChange={(v) => setEDelivery({ ...eDelivery, stt_provider: v })}
                size="md"
                ariaLabel="Voice call STT provider"
                options={[
                  {
                    value: '',
                    label: `Inherit default${deliveryDefaults ? ` (${STT_PROVIDER_LABELS[deliveryDefaults.stt_provider] ?? deliveryDefaults.stt_provider})` : ''}`,
                  },
                  ...Object.entries(STT_PROVIDER_LABELS).map(([value, label]) => ({ value, label })),
                ]}
              />
              <span className="mt-1 block text-[10.5px] text-text-muted">
                Speech-to-text for the ElevenLabs voice pipeline on this number.
              </span>
            </div>
          </div>
        )}
      </Modal>

      {pairAcct && (
        <BaileysPairModal
          account={pairAcct}
          onClose={() => {
            setPairAcct(null)
            refresh()
          }}
        />
      )}
    </div>
  )
}
