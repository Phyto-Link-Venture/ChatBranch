'use client'

/**
 * The step-by-step inbox setup: a right-side drawer on
 * desktop, a full-screen sheet on phones.
 *
 *   1 Choose channel → 2 Connect → 3 Who answers it → 4 Test & finish → Done
 *
 * New inboxes are created when step 3 is confirmed (the SIP trunk, which has
 * no "who answers it", when step 2 is); from then on — and when an existing
 * inbox is opened from its card — each "Save & continue" updates it through
 * the same account endpoints the channel always used. Errors render INSIDE
 * the drawer (a page-level message would sit behind the scrim).
 */

import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { AlertTriangle, BadgeCheck, Check, ChevronDown, ChevronRight, Loader2, Plug, RefreshCw, Share2, Webhook, X } from 'lucide-react'
import { UnknownCustomersField, useInboxIdentify } from '@/components/channels/UnknownCustomersField'
import { Advanced } from '@/components/ui/Advanced'
import { Button } from '@/components/ui/Button'
import { Select } from '@/components/ui/Select'
import { Toggle } from '@/components/ui/Toggle'
import { EntityPicker } from '@/components/ui/EntityPicker'
import { useEntity } from '@/lib/EntityContext'
import { useAccess } from '@/lib/useAccess'
import { useCompanyAdmin } from '@/lib/useCompanyAdmin'
import { absoluteApiResource } from '@/lib/apiBase'
import { ChannelLogo } from '@/components/ui/ChannelLogo'
import { accountRole, type AccountRole } from '@/components/ui/Badges'
import { WahaPairPanel } from './WahaPairPanel'
import { Trans, useT, type MessageKey, type TFunction } from '@/i18n'
import {
  BaileysHistoryPanel,
  BaileysPairPanel,
  CopyRow,
  DeliveryEditor,
  MetaWebhookRows,
  VcfTip,
  deliveryPatch,
  deliveryStateFrom,
  inputCls,
  type DeliveryState,
} from '@/components/settings/WhatsAppSettings'
import {
  EmbedSnippet,
  WIDGET_DEFAULTS,
  widgetEmbedSnippet,
  WidgetLookFields,
  widgetLookErrors,
  type WidgetLook,
} from '@/components/settings/WidgetsSettings'
import {
  api,
  errText,
  fromMessaging,
  fromVoice,
  fromWa,
  fromWidget,
  inboxName,
  isBspKind,
  isMoreKind,
  isWaKind,
  isUnofficialWaKind,
  WA_PROVIDER,
  RECALL_KINDS,
  kindMeta,
  testInbox,
  type AiDefault,
  type ReviewMode,
  type Inbox,
  type InboxKind,
  type MessagingAccount,
  type Team,
  type TestResult,
  type VoiceSettings,
  type WaAccount,
  type WahaState,
  type Widget,
} from './inboxes'
import { CRED_FIELDS, DEFAULT_NAME, GUIDES, ONE_OF, WEBHOOK_HELP, type FieldSpec } from './setupSpec'
import { channelDef, channelOfKind, type ChannelDef, type MethodDef } from './catalog'
import { catalogMethodAllowed } from '@/lib/channelAccess'
import { ChannelTileGrid, MethodCards, channelName } from './ChannelPicker'
import { ZernioConnectPanel } from './ZernioConnectPanel'
import { MarketplacePanel } from './MarketplacePanel'

export type Step = 1 | 2 | 3 | 4 | 5 // 5 = done

export interface DrawerTarget {
  kind: InboxKind | null
  /** Open a NEW inbox on this channel (catalog.ts id): its methods, or its only method's setup. */
  channel?: string | null
  /** The inbox being edited; null = a new one. */
  inbox: Inbox | null
  step: Step
}

const STEPS: { n: 1 | 2 | 3 | 4; title: (k: InboxKind | null, t: TFunction) => string }[] = [
  { n: 1, title: (_k, t) => t('channels.drawer.steps.choose') },
  { n: 2, title: (k, t) => (k ? t(kindMeta(k).connectTitle) : t('channels.drawer.steps.connect')) },
  { n: 3, title: (_k, t) => t('channels.drawer.steps.who') },
  { n: 4, title: (_k, t) => t('channels.drawer.steps.test') },
]

const SOCIAL_CHANNEL: Partial<Record<InboxKind, 'facebook' | 'instagram' | 'telegram'>> = {
  messenger: 'facebook',
  instagram: 'instagram',
  telegram: 'telegram',
}


const FOCUSABLE =
  'a[href], button:not([disabled]), textarea:not([disabled]), input:not([disabled]), select:not([disabled]), [tabindex]:not([tabindex="-1"])'

// ── small presentational pieces ──────────────────────────────────────────────

function StepCircle({ state, n }: { state: 'done' | 'current' | 'todo'; n: number }) {
  if (state === 'done')
    return (
      <span className="grid h-6 w-6 shrink-0 place-items-center rounded-full bg-primary text-on-primary">
        <Check size={13} strokeWidth={2.6} aria-hidden />
      </span>
    )
  if (state === 'current')
    return (
      <span
        className="grid h-6 w-6 shrink-0 place-items-center rounded-full bg-accent text-[12px] font-bold"
        style={{ color: 'var(--on-accent)' }}
      >
        {n}
      </span>
    )
  return (
    <span className="grid h-6 w-6 shrink-0 place-items-center rounded-full border border-line-strong text-[12px] text-text-mid">
      {n}
    </span>
  )
}

function Segmented<T extends string>({
  value,
  onChange,
  options,
  ariaLabel,
  disabled,
}: {
  value: T
  onChange: (v: T) => void
  options: { value: T; label: string }[]
  ariaLabel: string
  disabled?: boolean
}) {
  return (
    <div role="radiogroup" aria-label={ariaLabel} className="flex flex-wrap gap-0.5 rounded-lg border border-line-strong bg-card p-0.5">
      {options.map((o) => {
        const on = o.value === value
        return (
          <button
            key={o.value}
            type="button"
            role="radio"
            aria-checked={on}
            disabled={disabled}
            onClick={() => onChange(o.value)}
            className={`min-w-0 flex-1 cursor-pointer whitespace-nowrap rounded-md px-3 py-1.5 text-[12.5px] font-semibold transition-colors disabled:cursor-not-allowed disabled:opacity-50 ${
              on ? 'bg-primary text-on-primary' : 'text-text-mid hover:bg-fill-hover hover:text-text-high'
            }`}
          >
            {o.label}
          </button>
        )
      })}
    </div>
  )
}

function Label({ children, optional }: { children: ReactNode; optional?: boolean }) {
  const t = useT()
  return (
    <span className="mb-1 block text-[12px] font-semibold text-text-high">
      {children}
      {optional && <span className="ml-1 font-normal text-text-muted">({t('common.status.optional').toLowerCase()})</span>}
    </span>
  )
}

function FieldError({ text }: { text?: string | null }) {
  if (!text) return null
  return (
    <span className="mt-1 block text-[11.5px] font-semibold" style={{ color: 'var(--red-ink)' }}>
      {text}
    </span>
  )
}

function Help({ children }: { children: ReactNode }) {
  return <span className="mt-1 block text-[11.5px] leading-relaxed text-text-muted">{children}</span>
}

function ResultLine({ result }: { result: TestResult }) {
  const ink = result.tone === 'ok' ? 'var(--green-ink)' : result.tone === 'info' ? 'var(--amber-ink)' : 'var(--red-ink)'
  return (
    <div
      role={result.tone === 'error' ? 'alert' : 'status'}
      className="rounded-lg border border-line bg-well px-3 py-2 text-[12.5px] leading-relaxed"
      style={{ color: ink }}
    >
      {result.text}
    </div>
  )
}

function Section({ title, children, aside }: { title: string; children: ReactNode; aside?: ReactNode }) {
  return (
    <section className="flex flex-col gap-3">
      <div className="flex items-baseline justify-between gap-2 border-b border-line pb-1.5">
        <h3 className="display text-[17px] leading-tight text-text-heading">{title}</h3>
        {aside}
      </div>
      {children}
    </section>
  )
}

// ── the drawer ───────────────────────────────────────────────────────────────

export function InboxSetupDrawer({
  target,
  teams,
  verifyToken,
  canKeys,
  voiceConfigured,
  onClose,
  onChanged,
}: {
  target: DrawerTarget
  teams: Team[]
  verifyToken: string
  canKeys: boolean
  /** The SIP trunk is a singleton — its block edits the existing one. */
  voiceConfigured: Inbox | null
  onClose: () => void
  /** Something was saved — refresh the inbox list. */
  onChanged: () => void
}) {
  const t = useT()
  const companyAdmin = useCompanyAdmin()
  const { activeEntity, selectedEntities, entities: myEntities } = useEntity()
  /** Where a NEW inbox lands by default: the entity being worked in (the
   *  switcher's single pick, else its first tick); a company admin — who may
   *  not add organization-wide numbers — falls back to their first entity. */
  const defaultEntity =
    activeEntity?.slug ?? selectedEntities[0]?.slug ?? (companyAdmin ? myEntities[0]?.slug : undefined) ?? null
  const defaultEntityRef = useRef(defaultEntity)
  defaultEntityRef.current = defaultEntity
  const [kind, setKind] = useState<InboxKind | null>(target.kind)
  const [step, setStep] = useState<Step>(target.step)
  /** Step 1's two halves: the channel grid, then (when it has more than one) its methods. */
  const [phase, setPhase] = useState<'channel' | 'method'>('channel')
  const [channelId, setChannelId] = useState<string | null>(
    target.channel ?? (target.kind ? (channelOfKind(target.kind)?.id ?? null) : null),
  )
  /** A method whose setup is not an inbox kind's: via Zernio, or a marketplace's seller service. */
  const [alt, setAlt] = useState<MethodDef | null>(null)
  const channel = channelDef(channelId)
  const { can, channelAccess } = useAccess()
  /** The persisted inbox (opened for edit, or created in this drawer). */
  const [saved, setSaved] = useState<Inbox | null>(target.inbox)
  const editing = saved !== null

  // step 2 — credentials / look
  const [creds, setCreds] = useState<Record<string, string>>({})
  const [tls, setTls] = useState(true)
  const [ack, setAck] = useState(false)
  const [look, setLook] = useState<WidgetLook>(WIDGET_DEFAULTS)
  // step 3 — who answers it
  const [name, setName] = useState('')
  const [entity, setEntity] = useState<string | null>(null)
  const [teamId, setTeamId] = useState('')
  const [role, setRole] = useState<AccountRole>('reminder')
  const [aiDefault, setAiDefault] = useState<AiDefault>('')
  const [reviewMode, setReviewMode] = useState<ReviewMode>('')
  const [delivery, setDelivery] = useState<DeliveryState>(() => deliveryStateFrom(null))
  // QR numbers: stay offline between sends so the phone still rings (on for a new number)
  const [keepPhone, setKeepPhone] = useState(true)
  // QR numbers: how far back to import the number's existing chats when it links
  const [historyDays, setHistoryDays] = useState('30')
  const [keepRecalled, setKeepRecalled] = useState(false)
  // validation + status
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [touched, setTouched] = useState<Record<string, boolean>>({})
  const [formError, setFormError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [notice, setNotice] = useState<string | null>(null)
  // step 4
  const [test, setTest] = useState<TestResult | null>(null)
  const [testing, setTesting] = useState(false)
  const [testTo, setTestTo] = useState('')
  const [tgBase, setTgBase] = useState('')
  const [tgResult, setTgResult] = useState<TestResult | null>(null)
  const [showGuide, setShowGuide] = useState(target.inbox === null)
  // "Unknown customers" (identify_unknown) — saved right after the account
  const identify = useInboxIdentify(saved?.id ?? null)

  // A WAHA inbox keeps its base URL / session / engine on its own route, not on the WhatsApp
  // account payload, so an edit has to fetch them — otherwise saving a team change would be
  // refused for a base_url the person never cleared.
  useEffect(() => {
    if (kind !== 'wa_waha' || !saved?.id) return
    let cancelled = false
    api<WahaState>(`whatsapp/accounts/${saved.id}/waha/status`)
      .then((st) => {
        if (cancelled) return
        setCreds((prev) => ({
          ...prev,
          base_url: prev.base_url || st.base_url || '',
          session: prev.session || st.session || '',
          engine: prev.engine || st.engine || '',
        }))
      })
      .catch(() => {
        /* the panel below reports an unreachable WAHA; the form just stays as typed */
      })
    return () => {
      cancelled = true
    }
  }, [kind, saved?.id])

  /** Seed every field from an inbox (edit) or the kind's defaults (new). */
  const seed = useCallback((k: InboxKind, inbox: Inbox | null) => {
    const c: Record<string, string> = {}
    if (inbox?.wa) {
      c.phone_number_id = inbox.wa.phone_number_id || ''
      c.waba_id = inbox.wa.waba_id || ''
      // YCloud / Twilio: their non-secret settings
      const bsp = inbox.wa.bsp ?? {}
      for (const key of ['phone_number', 'account_sid', 'from_number', 'messaging_service_sid', 'api_key_sid'] as const)
        if (bsp[key]) c[key] = String(bsp[key])
    }
    if (inbox?.msg) {
      const cfg = inbox.msg.config ?? {}
      c.from_email = cfg.from_email || inbox.msg.external_id || ''
      c.host = cfg.host || ''
      c.port = cfg.port != null ? String(cfg.port) : '587'
      c.username = cfg.username || ''
      c.imap_host = cfg.imap_host || ''
      c.imap_port = cfg.imap_port != null && cfg.imap_port !== '' ? String(cfg.imap_port) : ''
      c.external_id = inbox.msg.channel === 'telegram' ? '' : inbox.msg.external_id || ''
      // LINE, Viber, SMS, WeChat, TikTok, Douyin, Zalo: their non-secret settings
      for (const key of ['app_id', 'account_sid', 'from_number', 'messaging_service_sid', 'client_key', 'sender_name'])
        if (cfg[key] != null && cfg[key] !== '') c[key] = String(cfg[key])
    } else if (k === 'email') c.port = '587'
    if (inbox?.voice) {
      c.sip_username = inbox.voice.sip_username || ''
      c.termination_uri = inbox.voice.termination_uri || ''
    }
    setCreds(c)
    setTls(inbox?.msg?.config?.use_tls !== false)
    setAck(!!inbox)
    setLook(
      inbox?.widget
        ? {
            title: inbox.widget.title || WIDGET_DEFAULTS.title,
            greeting: inbox.widget.greeting,
            accent: inbox.widget.accent || WIDGET_DEFAULTS.accent,
            placeholder: inbox.widget.placeholder,
            language: inbox.widget.language || WIDGET_DEFAULTS.language,
          }
        : WIDGET_DEFAULTS,
    )
    setName(inbox?.name ?? '')
    setEntity(inbox ? inbox.entity ?? null : defaultEntityRef.current)
    setTeamId(inbox?.teamId != null ? String(inbox.teamId) : '')
    setRole(inbox?.wa ? accountRole(inbox.wa) : isUnofficialWaKind(k) ? 'conversation' : 'reminder')
    setAiDefault(inbox?.aiDefault ?? '')
    setReviewMode(inbox?.reviewMode ?? '')
    setDelivery(deliveryStateFrom(inbox?.wa?.delivery))
    setKeepPhone(inbox?.wa ? !!inbox.wa.keep_phone_notifications : true)
    setHistoryDays(String(inbox?.wa?.import_history_days ?? 30))
    setKeepRecalled(!!(inbox?.wa?.keep_recalled ?? inbox?.msg?.keep_recalled))
    setErrors({})
    setTouched({})
    setFormError(null)
    setTest(null)
    setTgResult(null)
  }, [])

  useEffect(() => {
    if (target.kind) seed(target.kind, target.inbox)
  }, [seed, target])

  // The entity list / company-admin flag can arrive after the drawer opened:
  // fill a NEW inbox's still-empty entity once they do.
  useEffect(() => {
    if (!saved && defaultEntity) setEntity((cur) => cur ?? defaultEntity)
  }, [saved, defaultEntity])

  // ── modal behaviour: scroll lock, Esc, focus trap, focus restore ──────────
  const panelRef = useRef<HTMLDivElement>(null)
  useEffect(() => {
    const restore = document.activeElement as HTMLElement | null
    const prev = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    const timer = window.setTimeout(() => panelRef.current?.focus(), 0)
    return () => {
      window.clearTimeout(timer)
      document.body.style.overflow = prev
      restore?.focus?.()
    }
  }, [])

  // Esc closes even when focus has fallen back to <body> (a step change
  // unmounts the button that had it), so listen on the window, not the panel.
  useEffect(() => {
    const onEsc = (e: KeyboardEvent) => {
      if (e.key !== 'Escape' || e.defaultPrevented) return
      // an open dropdown inside the drawer closes first
      if (document.querySelector('[role="listbox"]')) return
      onClose()
    }
    window.addEventListener('keydown', onEsc)
    return () => window.removeEventListener('keydown', onEsc)
  }, [onClose])

  const onKeyDown = (e: React.KeyboardEvent) => {
    if (e.key !== 'Tab' || !panelRef.current) return
    const items = Array.from(panelRef.current.querySelectorAll<HTMLElement>(FOCUSABLE)).filter(
      (el) => el.offsetParent !== null,
    )
    if (items.length === 0) return
    const first = items[0]
    const last = items[items.length - 1]
    if (e.shiftKey && document.activeElement === first) {
      e.preventDefault()
      last.focus()
    } else if (!e.shiftKey && document.activeElement === last) {
      e.preventDefault()
      first.focus()
    }
  }

  // Scroll the body to the top whenever the step changes.
  const bodyRef = useRef<HTMLDivElement>(null)
  useEffect(() => {
    bodyRef.current?.scrollTo({ top: 0 })
    // keep keyboard focus inside the dialog when the focused button unmounted
    if (!panelRef.current?.contains(document.activeElement)) panelRef.current?.focus()
  }, [step])

  // ── validation ─────────────────────────────────────────────────────────
  const fields: FieldSpec[] = useMemo(() => (kind ? CRED_FIELDS[kind] ?? [] : []), [kind])

  const fieldError = useCallback(
    (f: FieldSpec, v: string): string | null => {
      const value = v.trim()
      if (!value) {
        if (f.optional) return null
        // secrets may stay blank once stored (blank = keep the current one)
        if (f.secret && editing) return null
        if (kind === 'telegram' && f.key === 'external_id') return null
        return t('channels.drawer.required', { field: t(f.label) })
      }
      return f.check ? f.check(value, t) : null
    },
    [editing, kind, t],
  )

  const validateConnect = (): boolean => {
    // connected through Zernio: its credentials live there
    if (saved?.viaZernio) {
      setErrors({})
      return true
    }
    const next: Record<string, string> = {}
    if (isUnofficialWaKind(kind) && !ack) next.ack = t('channels.drawer.ackRequired')
    if (kind === 'website') Object.assign(next, widgetLookErrors(look, t))
    if (kind === 'voice' && editing && !creds.sip_password?.trim() && !saved?.voice?.sip_password_masked)
      next.sip_password = t('channels.drawer.required', { field: t('channels.setup.labels.sipPassword') })
    for (const f of fields) {
      const msg = fieldError(f, creds[f.key] ?? '')
      if (msg) next[f.key] = msg
    }
    const oneOf = kind ? ONE_OF[kind] : undefined
    if (oneOf && !oneOf.keys.some((k) => (creds[k] ?? '').trim()))
      for (const k of oneOf.keys) next[k] = next[k] || t(oneOf.message)
    setErrors(next)
    setTouched(Object.fromEntries(Object.keys(next).map((k) => [k, true])))
    return Object.keys(next).length === 0
  }

  const validateWho = (): boolean => {
    const next: Record<string, string> = {}
    if (kind !== 'voice' && !name.trim()) next.name = t('channels.drawer.nameRequired')
    setErrors(next)
    return Object.keys(next).length === 0
  }

  // ── payloads ───────────────────────────────────────────────────────────
  const who = () => {
    const o: Record<string, string> = { team_id: teamId, ai_default: aiDefault, ai_review_mode: reviewMode }
    if (kind && RECALL_KINDS.includes(kind)) o.keep_recalled = keepRecalled ? 'true' : 'false'
    // only send the binding when it changes — a rebind re-files the inbox's
    // conversations and writes an audit row
    if (!saved || (saved.entity ?? null) !== entity) o.entity = entity ?? ''
    return o
  }

  /** Persist everything the drawer holds; returns the refreshed inbox. */
  const persist = async (): Promise<Inbox> => {
    if (!kind) throw new Error(t('channels.drawer.chooseFirst'))
    const c = (k: string) => (creds[k] ?? '').trim()
    const id = saved?.id

    if (kind === 'voice') {
      const body: Record<string, string> = { sip_username: c('sip_username'), termination_uri: c('termination_uri') }
      if (c('sip_password')) body.sip_password = c('sip_password')
      const v = await api<VoiceSettings>('whatsapp/voice-settings', { method: 'PUT', body: JSON.stringify(body) })
      const inbox = fromVoice(v)
      if (!inbox) throw new Error(t('channels.drawer.trunkNotSaved'))
      return inbox
    }

    if (isWaKind(kind) && !saved?.msg) {
      const body: Record<string, string> = { name: name.trim(), role, ...who(), ...deliveryPatch(delivery, t) }
      if (saved?.viaZernio) {
        // connected through Zernio: only the inbox settings are ours to change
      } else if (isBspKind(kind)) {
        // YCloud / Twilio — keys mirror the accounts API; a blank secret keeps the stored one
        if (!id) body.provider = WA_PROVIDER[kind]
        for (const f of fields) {
          const v = c(f.key)
          if (f.secret) {
            if (v) body[f.key] = v
          } else body[f.key] = v
        }
      } else if (kind === 'wa_cloud') {
        if (c('phone_number_id')) body.phone_number_id = c('phone_number_id')
        body.waba_id = c('waba_id')
        // secrets: blank = keep the stored value (never send an empty string)
        if (c('access_token')) body.access_token = c('access_token')
        if (c('app_secret')) body.app_secret = c('app_secret')
      } else if (kind === 'wa_waha') {
        // WAHA: the connection settings (base URL, session, engine, API key) go to its own
        // route; everything provider-agnostic then rides the shared account update below.
        const waha: Record<string, string> = {}
        for (const f of fields) {
          const v = c(f.key)
          // a blank secret keeps the stored one; a blank optional field is left untouched
          if (v) waha[f.key] = v
          else if (!f.secret && !f.optional) waha[f.key] = v
        }
        const made = await api<WaAccount>(id ? `whatsapp/accounts/${id}/waha` : 'whatsapp/accounts/waha', {
          method: id ? 'PUT' : 'POST',
          body: JSON.stringify(id ? waha : { ...waha, name: name.trim(), entity: entity ?? '' }),
        })
        const a = await api<WaAccount>(`whatsapp/accounts/${made.id}`, {
          method: 'POST',
          body: JSON.stringify(body),
        })
        return fromWa(a)
      } else if (!id) body.provider = 'baileys'
      if (kind === 'wa_qr') {
        body.keep_phone_notifications = keepPhone ? 'true' : 'false'
        body.import_history_days = historyDays
      }
      const a = await api<WaAccount>(id ? `whatsapp/accounts/${id}` : 'whatsapp/accounts', {
        method: 'POST',
        body: JSON.stringify(body),
      })
      return fromWa(a)
    }

    if (kind === 'website') {
      const body = { name: name.trim(), ...look, accent: look.accent.trim(), ...who() }
      const w = await api<Widget>(id ? `widgets/${id}` : 'widgets', {
        method: id ? 'PATCH' : 'POST',
        body: JSON.stringify(body),
      })
      return fromWidget(w)
    }

    // email, Messenger, Instagram (Meta) → /api/messaging/accounts
    const body: Record<string, unknown> = { name: name.trim(), ...who() }
    if (kind === 'email') {
      Object.assign(body, {
        from_email: c('from_email'),
        host: c('host'),
        port: c('port') || '587',
        username: c('username'),
        use_tls: tls,
        // optional inbound — "" clears back to the default IMAP host
        imap_host: c('imap_host'),
        imap_port: c('imap_port'),
      })
      if (c('password')) body.password = c('password')
    } else if (saved?.viaZernio) {
      // connected through Zernio — nothing to send but the inbox settings
    } else if (isMoreKind(kind)) {
      // LINE, Viber, SMS, WeChat, TikTok, Douyin, Zalo — keys mirror the API's field table;
      // a blank secret means "keep the stored one"
      for (const f of fields) {
        const v = c(f.key)
        if (f.secret) {
          if (v) body[f.key] = v
        } else body[f.key] = v
      }
    } else {
      if (kind !== 'telegram') body.external_id = c('external_id')
      if (c('access_token')) body.access_token = c('access_token')
      // Messenger / Instagram: Meta signs every webhook with the app secret
      if (kind !== 'telegram' && c('app_secret')) body.app_secret = c('app_secret')
    }
    if (!id) body.channel = kind === 'email' ? 'email' : isMoreKind(kind) ? kind : SOCIAL_CHANNEL[kind]
    const m = await api<MessagingAccount>(id ? `messaging/accounts/${id}` : 'messaging/accounts', {
      method: id ? 'PATCH' : 'POST',
      body: JSON.stringify(body),
    })
    return fromMessaging(m)
  }

  const save = async (then: Step) => {
    setBusy(true)
    setFormError(null)
    setNotice(null)
    try {
      const wasNew = !saved
      const inbox = await persist()
      if (kind !== 'voice') await identify.save(inbox.id)
      setSaved(inbox)
      // never keep secrets in memory once stored — blank now means "keep"
      setCreds((prev) => {
        const next = { ...prev }
        for (const f of fields) if (f.secret) next[f.key] = ''
        return next
      })
      setNotice(wasNew ? t('channels.drawer.connectedNotice', { name: inboxName(inbox, t) }) : t('channels.drawer.changesSaved'))
      onChanged()
      setStep(then)
    } catch (e) {
      setFormError(errText(e, t))
    } finally {
      setBusy(false)
    }
  }

  // ── navigation ─────────────────────────────────────────────────────────
  const choose = (k: InboxKind) => {
    if (k === 'voice' && voiceConfigured) {
      // the trunk is a singleton: its block edits the one that exists
      setSaved(voiceConfigured)
      setKind('voice')
      seed('voice', voiceConfigured)
    } else {
      setSaved(null)
      setKind(k)
      seed(k, null)
    }
    setAlt(null)
    setShowGuide(true)
    setNotice(null)
    setStep(2)
  }

  /** Step 1 → a channel: its method cards, or straight into its only method. */
  const pickChannel = (id: string) => {
    const ch = channelDef(id)
    if (!ch) return
    setChannelId(id)
    setFormError(null)
    if (ch.methods.length === 1) pickMethod(ch.methods[0])
    else {
      setAlt(null)
      setPhase('method')
      setStep(1)
    }
  }

  /** Step 2 → a method: the inbox kind's own setup, or the Zernio / marketplace panel. */
  const pickMethod = (mth: MethodDef) => {
    setFormError(null)
    if (mth.kind) {
      choose(mth.kind)
      return
    }
    setSaved(null)
    setKind(null)
    setAlt(mth)
    setNotice(null)
    setStep(2)
  }

  /** Back to step 1: the method cards when the channel has several, else the channel grid. */
  const toPicker = (to: 'channel' | 'method' = channel && channel.methods.length > 1 ? 'method' : 'channel') => {
    setFormError(null)
    setAlt(null)
    setStep(1)
    setPhase(to)
  }

  /** Start over for another inbox. */
  const restart = () => {
    setSaved(null)
    setKind(null)
    setAlt(null)
    setChannelId(null)
    setPhase('channel')
    setStep(1)
    setNotice(null)
  }

  // Opened on a channel (a catalog tile): skip straight past the grid.
  useEffect(() => {
    if (!target.inbox && !target.kind && target.channel) pickChannel(target.channel)
    // once, on open
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  /** Why a method can't be used by this person (null = it can). */
  const methodBlocked = (mth: MethodDef, ch: ChannelDef | undefined = channel): string | null => {
    if (mth.kind === 'voice' && !canKeys && !voiceConfigured) return t('channels.page.superOnly')
    if (!can('channels.manage')) return t('channels.page.addNeedsPermission')
    // the role's channel policy (Permissions → role → Channel access)
    if (ch && !catalogMethodAllowed(channelAccess, ch.id, mth.id)) return t('channels.page.roleNotAllowed')
    return null
  }
  const channelBlocked = (ch: ChannelDef): string | null => {
    const reasons = ch.methods.map((x) => methodBlocked(x, ch))
    return reasons.every((r) => r) ? reasons[0] : null
  }

  const next = () => {
    setFormError(null)
    if (step === 2) {
      if (kind === 'voice' && !canKeys) {
        // read-only for everyone but the super admin
        setStep(4)
        return
      }
      if (!validateConnect()) return
      if (kind === 'voice') {
        // no "who answers it" for the trunk — it is saved here
        void save(4)
        return
      }
      if (!name.trim() && kind)
        setName(kind === 'email' && creds.from_email?.trim() ? creds.from_email.trim() : t(DEFAULT_NAME[kind]))
      setStep(3)
      return
    }
    if (step === 3) {
      if (kind === 'voice') {
        setStep(4)
        return
      }
      if (!validateWho()) return
      // a new inbox also needs valid credentials before it is created
      if (!editing && !validateConnect()) {
        setStep(2)
        return
      }
      void save(4)
      return
    }
    if (step === 4) setStep(5)
  }

  const back = () => {
    setFormError(null)
    if (step === 5) setStep(4)
    else if (step === 2 && !saved) toPicker()
    else if (step > 1) setStep((step - 1) as Step)
  }

  /** Which stepper entries are clickable: done ones always; any once saved. */
  const canJump = (n: number) => {
    if (busy || n === step) return false
    if (n === 1) return !editing
    if (!kind) return false
    if (editing) return true
    return n < step
  }

  const runTest = async () => {
    if (!saved) return
    setTesting(true)
    setTest(null)
    try {
      if (saved.kind === 'email' && testTo.trim()) {
        try {
          const r = await api<{ detail?: string }>(`messaging/accounts/${saved.id}/test`, {
            method: 'POST',
            body: JSON.stringify({ to: testTo.trim() }),
          })
          setTest({ tone: 'ok', text: r.detail || t('channels.drawer.testEmailSent', { to: testTo.trim() }) })
        } catch (e) {
          const status = (e as { status?: number }).status
          setTest({ tone: status === 501 ? 'info' : 'error', text: errText(e, t) })
        }
      } else setTest(await testInbox(saved, t))
    } finally {
      setTesting(false)
    }
  }

  const registerTelegram = async () => {
    if (!saved) return
    const base = tgBase.trim().replace(/\/+$/, '')
    if (!/^https:\/\/\S+$/.test(base)) {
      setTgResult({ tone: 'error', text: t('channels.drawer.httpsRequired') })
      return
    }
    // Telegram, LINE and Viber register through the same endpoint
    setTgResult(null)
    try {
      const r = await api<{ url?: string; detail?: string }>(`messaging/accounts/${saved.id}/set-webhook`, {
        method: 'POST',
        body: JSON.stringify({ base_url: base }),
      })
      setTgResult({
        tone: 'ok',
        text: r.url ? t('channels.drawer.webhookRegisteredAt', { url: r.url }) : r.detail || t('channels.drawer.webhookRegistered'),
      })
    } catch (e) {
      const status = (e as { status?: number }).status
      setTgResult({ tone: status === 501 ? 'info' : 'error', text: errText(e, t) })
    }
  }

  const rotateToken = async () => {
    if (!saved?.widget) return
    if (!window.confirm(t('channels.drawer.rotateConfirm'))) return
    setBusy(true)
    setFormError(null)
    try {
      const w = await api<Widget>(`widgets/${saved.id}`, { method: 'PATCH', body: JSON.stringify({ rotate_token: true }) })
      setSaved(fromWidget(w))
      setNotice(t('channels.drawer.tokenRotated'))
      onChanged()
    } catch (e) {
      setFormError(errText(e, t))
    } finally {
      setBusy(false)
    }
  }

  // ── render helpers ─────────────────────────────────────────────────────
  const meta = kind ? kindMeta(kind) : null
  const teamName = (id: number | null) => teams.find((tm) => tm.id === id)?.name
  const voiceLocked = kind === 'voice' && !canKeys

  /** The masked stored value of a secret field ("" when none / not a secret). */
  const currentSecret = (f: FieldSpec): string =>
    (f.secret && saved
      ? f.key === 'access_token'
        ? saved.wa?.access_token_masked || saved.msg?.access_token_masked
        : f.key === 'app_secret'
          ? saved.wa?.app_secret_masked || saved.msg?.app_secret_masked
          : f.key === 'api_key_secret' && saved.wa
            ? saved.wa.bsp?.api_key_secret_masked
            : f.key === 'password'
            ? saved.msg?.config?.password_masked
            : f.key === 'sip_password'
              ? saved.voice?.sip_password_masked
              : // the newer channels: `<key>_masked` on the account or in its config
                saved.msg?.[`${f.key}_masked`] || String(saved.msg?.config?.[`${f.key}_masked`] ?? '')
    : '') || ''

  const renderField = (f: FieldSpec) => {
    const v = creds[f.key] ?? ''
    const err = touched[f.key] ? errors[f.key] : undefined
    const current = currentSecret(f)
    return (
      <label key={f.key} className={`block ${f.half ? '' : 'sm:col-span-2'}`}>
        <Label optional={f.optional}>
          {t(f.label)}
          {current ? (
            <span className="ml-1.5 font-normal text-text-muted">· {t('channels.drawer.current', { value: current })}</span>
          ) : null}
        </Label>
        <input
          className={`${inputCls} ${err ? '!border-[color:var(--red-ink)]' : ''}`}
          type={f.secret ? 'password' : 'text'}
          autoComplete="off"
          inputMode={f.inputMode}
          value={v}
          disabled={voiceLocked}
          placeholder={
            f.secret && saved ? t('channels.drawer.keepCurrent') : f.placeholderKey ? t(f.placeholderKey) : f.placeholder
          }
          aria-invalid={!!err}
          onChange={(e) => {
            setCreds((p) => ({ ...p, [f.key]: e.target.value }))
            if (errors[f.key]) setErrors((p) => ({ ...p, [f.key]: fieldError(f, e.target.value) ?? '' }))
          }}
          onBlur={() => {
            setTouched((p) => ({ ...p, [f.key]: true }))
            const msg = v.trim() ? fieldError(f, v) : null // blank is only flagged on Continue
            setErrors((p) => ({ ...p, [f.key]: msg ?? '' }))
          }}
        />
        <FieldError text={err} />
        <Help>{t(f.help)}</Help>
      </label>
    )
  }

  // Step 2: required fields up front; optional ones (and the email ports + TLS,
  // which have defaults) under Advanced — except a "one of these" pair (SMS),
  // where one of the two is needed.
  const oneOfKeys = (kind && ONE_OF[kind]?.keys) || []
  const isAdvancedField = (f: FieldSpec) =>
    (f.optional || f.key === 'port' || f.key === 'imap_port') && !f.recommended && !oneOfKeys.includes(f.key)
  const basicFields = fields.filter((f) => !isAdvancedField(f))
  const advancedFields = fields.filter(isAdvancedField)
  /** An advanced field holding something other than its default (or a stored secret). */
  const fieldCustomised = (f: FieldSpec) => {
    const v = (creds[f.key] ?? '').trim()
    if (kind === 'email' && f.key === 'port') return v !== '' && v !== '587'
    return v !== '' || !!currentSecret(f)
  }
  const connectAdvancedCount =
    advancedFields.filter(fieldCustomised).length + (kind === 'email' && !tls ? 1 : 0)
  const connectAdvancedError = advancedFields.some((f) => !!errors[f.key])

  // Step 3: what sits under Advanced, and whether any of it is customised.
  const deliveryCustomised =
    delivery.perDay || Object.values(delivery.delivery).some((v) => String(v ?? '').trim() !== '')
  const whoAdvancedCount =
    (teamId ? 1 : 0) + (reviewMode ? 1 : 0) + (identify.value.mode ? 1 : 0) + (isWaKind(kind) && deliveryCustomised ? 1 : 0)

  // Step 4: the public-base-URL registration is developer setup; keep it open
  // once something was typed or registered (never hide a result or an error).
  const devSetupOpen = tgBase.trim() !== '' || tgResult !== null

  // ── step bodies ────────────────────────────────────────────────────────
  const chooseStep =
    phase === 'method' && channel ? (
      <MethodCards channel={channel} onPick={pickMethod} disabled={(mth) => methodBlocked(mth, channel)} />
    ) : (
      <div className="flex flex-col gap-3">
        <p className="text-[12.5px] text-text-muted">{t('channels.drawer.chooseIntro')}</p>
        <ChannelTileGrid dense onPick={pickChannel} selected={channelId} disabled={channelBlocked} groupIdPrefix="drawer-grp" />
      </div>
    )

  const zernioNote = (
    <div className="flex gap-2.5 rounded-lg border border-line bg-well px-3 py-2.5 text-[12.5px] leading-relaxed text-text-mid">
      <Share2 size={16} className="mt-0.5 shrink-0 text-text-muted" aria-hidden />
      <span>{t('channels.drawer.viaZernioNote')}</span>
    </div>
  )

  const connectStep = !kind ? null : saved?.viaZernio ? zernioNote : (
    <div className="flex flex-col gap-5">
      {isUnofficialWaKind(kind) && (
        <>
          <div
            className="flex gap-2.5 rounded-lg border border-amber/40 bg-amber/10 px-3 py-2.5 text-[12.5px] leading-relaxed"
            style={{ color: 'var(--amber-ink)' }}
          >
            <AlertTriangle size={16} className="mt-0.5 shrink-0" aria-hidden />
            <span>
              {kind === 'wa_waha' ? (
                <Trans k="channels.drawer.waha.warning" values={{ b: <b>{t('channels.drawer.qr.demoOnly')}</b> }} />
              ) : (
                <Trans k="channels.drawer.qr.warning" values={{ b: <b>{t('channels.drawer.qr.demoOnly')}</b> }} />
              )}
            </span>
          </div>
          <ul className="flex list-disc flex-col gap-1 pl-5 text-[12.5px] leading-relaxed text-text-mid">
            <li>
              {kind === 'wa_waha' ? (
                t('channels.drawer.waha.youHostIt')
              ) : (
                <Trans
                  k="channels.drawer.qr.needsGateway"
                  values={{ cmd: <code className="rounded bg-fill px-1">npm run dev:baileys</code> }}
                />
              )}
            </li>
            <li>{kind === 'wa_waha' ? t('channels.drawer.waha.textAndFiles') : t('channels.drawer.qr.textAndCalls')}</li>
            <li>{t('channels.drawer.qr.noWindow')}</li>
            {kind === 'wa_waha' && <li>{t('channels.drawer.waha.tier')}</li>}
          </ul>
          <label className="flex cursor-pointer items-start gap-2.5 rounded-lg border border-line bg-well px-3 py-2.5">
            <input
              type="checkbox"
              checked={ack}
              onChange={(e) => {
                setAck(e.target.checked)
                if (e.target.checked) setErrors((p) => ({ ...p, ack: '' }))
              }}
              className="mt-0.5 h-4 w-4 shrink-0"
              style={{ accentColor: 'var(--primary)' }}
            />
            <span className="text-[12.5px] text-text-high">{t('channels.drawer.qr.ack')}</span>
          </label>
          <FieldError text={errors.ack} />
        </>
      )}

      {kind === 'website' && (
        <>
          <p className="text-[12.5px] leading-relaxed text-text-mid">{t('channels.drawer.websiteIntro')}</p>
          <WidgetLookFields value={look} onChange={setLook} errors={errors as Partial<Record<keyof WidgetLook, string>>} />
        </>
      )}

      {meta?.approval && (
        <div
          className="flex gap-2.5 rounded-lg border border-amber/40 bg-amber/10 px-3 py-2.5 text-[12.5px] leading-relaxed"
          style={{ color: 'var(--amber-ink)' }}
        >
          <BadgeCheck size={16} className="mt-0.5 shrink-0" aria-hidden />
          <span>
            <Trans
              k="channels.drawer.approvalNote"
              values={{ b: <b>{t('channels.drawer.approvalTitle')}</b>, platform: t(meta.label) }}
            />
          </span>
        </div>
      )}

      {voiceLocked && (
        <div className="rounded-lg border border-line bg-well px-3 py-2 text-[12.5px] text-text-mid">
          {t('channels.drawer.voiceLocked')}
        </div>
      )}

      {basicFields.length > 0 && (
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">{basicFields.map(renderField)}</div>
      )}

      {(advancedFields.length > 0 || kind === 'email') && (
        <Advanced count={connectAdvancedCount} forceOpen={connectAdvancedCount > 0 || connectAdvancedError}>
          {advancedFields.length > 0 && (
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">{advancedFields.map(renderField)}</div>
          )}
          {kind === 'email' && (
            <div className="flex items-center justify-between gap-3 rounded-lg border border-line px-3 py-2.5">
              <span>
                <span className="block text-[12.5px] font-semibold text-text-high">{t('channels.drawer.useTls')}</span>
                <span className="block text-[11.5px] text-text-muted">{t('channels.drawer.useTlsHint')}</span>
              </span>
              <Toggle on={tls} onChange={setTls} label={t('channels.drawer.useTls')} />
            </div>
          )}
        </Advanced>
      )}

      {GUIDES[kind] && (
        <div className="rounded-lg bg-well">
          <button
            type="button"
            onClick={() => setShowGuide((v) => !v)}
            aria-expanded={showGuide}
            className="flex w-full cursor-pointer items-center gap-2 px-3 py-2.5 text-left text-[12.5px] font-semibold text-text-high"
          >
            <ChevronDown
              size={14}
              aria-hidden
              className="shrink-0 text-text-muted transition-transform"
              style={{ transform: showGuide ? 'none' : 'rotate(-90deg)' }}
            />
            {t('channels.drawer.whereToFind')}
          </button>
          {showGuide && (
            <ol className="flex list-decimal flex-col gap-1 px-3 pb-3 pl-9 text-[12px] leading-relaxed text-text-mid">
              {GUIDES[kind]!.map((s) => (
                <li key={s}>{t(s)}</li>
              ))}
            </ol>
          )}
        </div>
      )}
    </div>
  )

  // a WhatsApp number on the messaging accounts (one connected through Zernio) has no role / delivery
  const isWa = isWaKind(kind) && !saved?.msg

  const whoStep = !kind ? null : kind === 'voice' ? (
    <div className="flex flex-col gap-3 text-[12.5px] leading-relaxed text-text-mid">
      <p>{t('channels.drawer.who.voiceIntro')}</p>
      <p className="text-text-muted">{t('channels.drawer.who.voiceNothing')}</p>
    </div>
  ) : (
    <div className="flex flex-col gap-5">
      <label className="block">
        <Label>{kind === 'website' ? t('channels.drawer.who.internalName') : t('channels.drawer.who.inboxName')}</Label>
        <input
          className={`${inputCls} ${errors.name ? '!border-[color:var(--red-ink)]' : ''}`}
          value={name}
          onChange={(e) => {
            setName(e.target.value)
            if (errors.name && e.target.value.trim()) setErrors((p) => ({ ...p, name: '' }))
          }}
          placeholder={t(DEFAULT_NAME[kind])}
          aria-invalid={!!errors.name}
        />
        {errors.name ? <FieldError text={errors.name} /> : <Help>{t('channels.drawer.who.nameHelp')}</Help>}
      </label>

      <label className="block">
        <Label>{t('common.fields.entity')}</Label>
        <EntityPicker
          value={entity}
          onChange={setEntity}
          // company admins add numbers for their own companies only (the API
          // refuses organization-wide); an old org-wide inbox still shows as such
          allowGlobal={!companyAdmin || (editing && saved?.entity == null)}
          ariaLabel={t('channels.drawer.who.entityAria')}
        />
        <Help>{t('channels.drawer.who.entityHelp')}</Help>
      </label>

      {isWa && (
        <div>
          <Label>{t('channels.drawer.who.accountRole')}</Label>
          <Segmented
            value={role}
            onChange={setRole}
            ariaLabel={t('channels.drawer.who.accountRole')}
            options={[
              { value: 'reminder', label: t('channels.drawer.who.roleReminder') },
              { value: 'conversation', label: t('channels.drawer.who.roleConversation') },
            ]}
          />
          <Help>
            {role === 'reminder' ? t('channels.drawer.who.roleReminderHelp') : t('channels.drawer.who.roleConversationHelp')}
          </Help>
        </div>
      )}

      {kind === 'wa_qr' && (
        <div
          data-tour="keep-phone-notifications"
          className="flex items-center justify-between gap-3 rounded-lg border border-line px-3 py-2.5"
        >
          <span>
            <span className="block text-[12.5px] font-semibold text-text-high">{t('channels.drawer.who.keepPhone')}</span>
            <span className="block text-[11.5px] text-text-muted">
              {keepPhone ? t('channels.drawer.who.keepPhoneOnHelp') : t('channels.drawer.who.keepPhoneOffHelp')}
            </span>
          </span>
          <Toggle on={keepPhone} onChange={setKeepPhone} label={t('channels.drawer.who.keepPhone')} />
        </div>
      )}

      {kind === 'wa_qr' && (
        <div data-tour="import-history">
          <Label>{t('channels.drawer.who.importHistory')}</Label>
          <Select
            value={historyDays}
            onChange={setHistoryDays}
            ariaLabel={t('channels.drawer.who.importHistory')}
            options={[
              { value: '0', label: t('channels.drawer.who.importHistoryOff') },
              { value: '30', label: t('channels.drawer.who.importHistory30') },
              { value: '90', label: t('channels.drawer.who.importHistory90') },
              { value: '-1', label: t('channels.drawer.who.importHistoryAll') },
            ]}
          />
          <Help>{t('channels.drawer.who.importHistoryHelp')}</Help>
        </div>
      )}

      {kind && RECALL_KINDS.includes(kind) && (
        <div
          data-tour="keep-recalled"
          className="flex items-center justify-between gap-3 rounded-lg border border-line px-3 py-2.5"
        >
          <span>
            <span className="block text-[12.5px] font-semibold text-text-high">{t('channels.drawer.who.keepRecalled')}</span>
            <span className="block text-[11.5px] text-text-muted">
              {keepRecalled ? t('channels.drawer.who.keepRecalledOnHelp') : t('channels.drawer.who.keepRecalledOffHelp')}
            </span>
          </span>
          <Toggle on={keepRecalled} onChange={setKeepRecalled} label={t('channels.drawer.who.keepRecalled')} />
        </div>
      )}

      <div>
        <Label>{t('channels.drawer.who.aiReplies')}</Label>
        <Segmented
          value={aiDefault}
          onChange={setAiDefault}
          ariaLabel={t('channels.drawer.who.aiRepliesAria')}
          options={[
            { value: '', label: isWa ? t('channels.drawer.who.byRole') : t('channels.drawer.who.default') },
            { value: 'on', label: t('common.status.on') },
            { value: 'off', label: t('common.status.off') },
          ]}
        />
        <Help>
          {t(
            !isWa
              ? 'channels.drawer.who.aiHelpDefault'
              : role === 'reminder'
                ? 'channels.drawer.who.aiHelpReminder'
                : 'channels.drawer.who.aiHelpConversation',
          )}
        </Help>
      </div>

      <Advanced count={whoAdvancedCount} forceOpen={whoAdvancedCount > 0}>
        <label className="block">
          <Label optional>{t('channels.drawer.who.team')}</Label>
          <Select
            value={teamId}
            onChange={setTeamId}
            size="md"
            ariaLabel={t('channels.drawer.who.teamAria')}
            options={[
              { value: '', label: t('channels.drawer.who.noTeam'), hint: t('channels.drawer.who.noTeamHint') },
              ...teams.map((tm) => ({
                value: String(tm.id),
                label: tm.name,
                hint: tm.auto_assign === false ? t('channels.drawer.who.noRoundRobin') : undefined,
              })),
            ]}
          />
          <Help>{teams.length === 0 ? t('channels.drawer.who.noTeams') : t('channels.drawer.who.teamHelp')}</Help>
        </label>

        <div>
          <Label>{t('channels.drawer.who.review')}</Label>
          <Select
            size="md"
            value={reviewMode}
            onChange={(v) => setReviewMode(v as ReviewMode)}
            ariaLabel={t('channels.drawer.who.reviewAria')}
            options={[
              { value: '', label: t('channels.drawer.who.reviewInherit'), hint: t('channels.drawer.who.reviewInheritHint') },
              { value: 'auto', label: t('channels.drawer.who.reviewAuto') },
              { value: 'review_flagged', label: t('channels.drawer.who.reviewFlagged') },
              { value: 'review_all', label: t('channels.drawer.who.reviewAll') },
              { value: 'shadow', label: t('channels.drawer.who.reviewShadow') },
            ]}
          />
          <Help>{t('channels.drawer.who.reviewHelp')}</Help>
        </div>

        <UnknownCustomersField
          value={identify.value}
          onChange={identify.setValue}
          defaults={identify.defaults}
          inherited={identify.inherited}
        />

        {isWa && (
          <div>
            <Label>{t('channels.drawer.who.delivery')}</Label>
            <Help>{t('channels.drawer.who.deliveryHint')}</Help>
            <div className="mt-2">
              <DeliveryEditor value={delivery} onChange={setDelivery} />
            </div>
          </div>
        )}
      </Advanced>
    </div>
  )

  const webhookUrl = absoluteApiResource('/api/messaging/webhook')
  const testStep = !kind ? null : (
    <div className="flex flex-col gap-6">
      {!saved ? (
        <p className="text-[12.5px] text-text-muted">
          {voiceLocked ? t('channels.drawer.test.voiceNotSet') : t('channels.drawer.test.saveFirst')}
        </p>
      ) : (
        <>
          {kind === 'wa_qr' && (
            <Section title={t('channels.drawer.test.pair')}>
              <BaileysPairPanel accountId={saved.id} onConnected={onChanged} />
              <VcfTip />
              {/* WhatsApp's replay on linking is shallow — this asks for the rest, paced */}
              <BaileysHistoryPanel accountId={saved.id} />
            </Section>
          )}

          {kind === 'wa_waha' && (
            <Section title={t('channels.drawer.test.pair')}>
              <WahaPairPanel accountId={saved.id} onConnected={onChanged} />
              <VcfTip />
            </Section>
          )}

          {kind !== 'wa_waha' && (
          <Section title={t('channels.drawer.test.runTest')}>
            {kind === 'email' && (
              <label className="block">
                <Label optional>{t('channels.drawer.test.sendTo')}</Label>
                <input
                  className={inputCls}
                  inputMode="email"
                  value={testTo}
                  onChange={(e) => setTestTo(e.target.value)}
                  placeholder={t('channels.drawer.test.sendToPlaceholder')}
                />
              </label>
            )}
            <div className="flex flex-wrap items-center gap-2">
              <Button variant="secondary" onClick={runTest} disabled={testing}>
                {testing ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Plug size={13} aria-hidden />}
                {kind === 'voice'
                  ? t('channels.drawer.test.checkConfig')
                  : kind === 'website'
                    ? t('channels.drawer.test.checkWidget')
                    : t('channels.drawer.test.testConnection')}
              </Button>
            </div>
            {test && <ResultLine result={test} />}
          </Section>
          )}

          {kind === 'wa_cloud' && !saved.viaZernio && (
            <Section title={t('channels.drawer.test.pointMeta')}>
              <p className="text-[12.5px] leading-relaxed text-text-mid">
                <Trans
                  k="channels.drawer.test.metaWa"
                  values={{ field: <code className="rounded bg-fill px-1">messages</code> }}
                />
              </p>
              <MetaWebhookRows verifyToken={verifyToken} />
            </Section>
          )}

          {(kind === 'messenger' || kind === 'instagram') && !saved.viaZernio && (
            <Section title={t('channels.drawer.test.pointMeta')}>
              <p className="text-[12.5px] leading-relaxed text-text-mid">
                <Trans
                  k="channels.drawer.test.metaSocial"
                  values={{
                    product: kind === 'messenger' ? 'Messenger' : 'Instagram',
                    field: <code className="rounded bg-fill px-1">messages</code>,
                  }}
                />
              </p>
              <CopyRow label={t('channels.whatsapp.callbackUrl')} value={webhookUrl} />
              <CopyRow label={t('channels.whatsapp.verifyToken')} value={verifyToken} />
            </Section>
          )}

          {kind === 'telegram' && !saved.viaZernio && (
            <Section title={t('channels.drawer.test.registerWebhook')}>
              <p className="text-[12.5px] leading-relaxed text-text-mid">
                <Trans
                  k="channels.drawer.test.telegramIntro"
                  values={{
                    path: (
                      <code className="rounded bg-fill px-1">
                        …/api/messaging/webhook/telegram/&lt;{t('channels.drawer.test.botToken')}&gt;
                      </code>
                    ),
                  }}
                />
              </p>
              <Advanced label={t('channels.drawer.developerSetup')} forceOpen={devSetupOpen}>
                <div className="flex flex-col gap-2 sm:flex-row">
                  <input
                    className={inputCls}
                    inputMode="url"
                    value={tgBase}
                    onChange={(e) => setTgBase(e.target.value)}
                    placeholder="https://your-tunnel.example.com"
                    aria-label={t('channels.drawer.test.publicBaseUrl')}
                  />
                  <Button variant="secondary" onClick={registerTelegram} className="shrink-0">
                    <Webhook size={13} aria-hidden /> {t('channels.drawer.test.register')}
                  </Button>
                </div>
                {tgResult && <ResultLine result={tgResult} />}
              </Advanced>
            </Section>
          )}

          {isBspKind(kind) && saved.wa && (
            <Section title={t('channels.drawer.test.pointPlatform', { platform: meta ? t(meta.label) : '' })}>
              {WEBHOOK_HELP[kind] && (
                <p className="text-[12.5px] leading-relaxed text-text-mid">{t(WEBHOOK_HELP[kind]!)}</p>
              )}
              <CopyRow label={t('channels.drawer.test.webhookUrl')} value={absoluteApiResource(saved.wa.webhook_path ?? '')} />
              {kind === 'wa_ycloud' && !saved.wa.app_secret_masked && (
                <p className="text-[11.5px] leading-relaxed" style={{ color: 'var(--amber-ink)' }}>
                  {t('channels.drawer.test.bspSecretMissing')}
                </p>
              )}
              {kind === 'wa_twilio' && (
                <p className="text-[11.5px] leading-relaxed text-text-muted">{t('channels.drawer.test.twilioPublic')}</p>
              )}
            </Section>
          )}

          {isMoreKind(kind) && saved.msg && !saved.viaZernio && (
            <Section title={t('channels.drawer.test.pointPlatform', { platform: meta ? t(meta.label) : '' })}>
              {WEBHOOK_HELP[kind] && (
                <p className="text-[12.5px] leading-relaxed text-text-mid">{t(WEBHOOK_HELP[kind]!)}</p>
              )}
              <CopyRow label={t('channels.drawer.test.webhookUrl')} value={absoluteApiResource(saved.msg.webhook_path ?? '')} />
              {kind === 'wechat' && (
                <p className="text-[11.5px] leading-relaxed text-text-muted">{t('channels.drawer.test.wechatNote')}</p>
              )}
              {saved.msg.can_set_webhook && (
                <Advanced label={t('channels.drawer.developerSetup')} forceOpen={devSetupOpen}>
                  <div className="flex flex-col gap-2 sm:flex-row">
                    <input
                      className={inputCls}
                      inputMode="url"
                      value={tgBase}
                      onChange={(e) => setTgBase(e.target.value)}
                      placeholder="https://your-tunnel.example.com"
                      aria-label={t('channels.drawer.test.publicBaseUrl')}
                    />
                    <Button variant="secondary" onClick={registerTelegram} className="shrink-0">
                      <Webhook size={13} aria-hidden /> {t('channels.drawer.test.register')}
                    </Button>
                  </div>
                  {tgResult && <ResultLine result={tgResult} />}
                </Advanced>
              )}
              {saved.msg.window_hours ? (
                <p className="text-[11.5px] leading-relaxed text-text-muted">
                  {t('channels.drawer.test.windowNote', {
                    window:
                      saved.msg.window_hours >= 72
                        ? t('channels.drawer.test.days', { count: saved.msg.window_hours / 24 })
                        : t('channels.drawer.test.hours', { count: saved.msg.window_hours }),
                  })}
                </p>
              ) : null}
            </Section>
          )}

          {kind === 'website' && saved.widget && (
            <Section
              title={t('channels.drawer.test.putOnSite')}
              aside={
                <button
                  type="button"
                  onClick={rotateToken}
                  disabled={busy}
                  className="inline-flex cursor-pointer items-center gap-1 text-[12px] font-semibold text-text-mid hover:text-text-high disabled:opacity-50"
                >
                  <RefreshCw size={12} aria-hidden /> {t('channels.drawer.test.rotateToken')}
                </button>
              }
            >
              <EmbedSnippet value={widgetEmbedSnippet(saved.widget.token)} />
              <p className="text-[11.5px] leading-relaxed text-text-muted">{t('channels.drawer.test.tokenPublic')}</p>
            </Section>
          )}

          {kind === 'email' && (
            <p className="text-[12px] leading-relaxed text-text-muted">{t('channels.drawer.test.emailImap')}</p>
          )}

          {kind === 'voice' && (
            <Section title={t('channels.drawer.test.runAgent')}>
              <ol className="flex list-decimal flex-col gap-1.5 pl-5 text-[12.5px] leading-relaxed text-text-mid">
                <li>
                  <Trans
                    k="channels.drawer.test.agentStep1"
                    values={{
                      file: <code className="rounded bg-fill px-1">backend/.env</code>,
                      keys: (
                        <>
                          <code className="rounded bg-fill px-1">LIVEKIT_URL</code>,{' '}
                          <code className="rounded bg-fill px-1">LIVEKIT_API_KEY</code>,{' '}
                          <code className="rounded bg-fill px-1">LIVEKIT_API_SECRET</code>
                        </>
                      ),
                    }}
                  />
                </li>
                <li>
                  <Trans
                    k="channels.drawer.test.agentStep2"
                    values={{ id: <code className="rounded bg-fill px-1">SIP_OUTBOUND_TRUNK_ID</code> }}
                  />
                </li>
              </ol>
              <Advanced label={t('channels.drawer.developerSetup')}>
                <CopyRow label={t('channels.drawer.test.trunkSetup')} value="npm --prefix backend/voice_agent run setup-trunk" />
                <p className="text-[12.5px] leading-relaxed text-text-mid">{t('channels.drawer.test.agentThen')}</p>
                <CopyRow label={t('channels.drawer.test.runWorker')} value="npm run dev:agent" />
              </Advanced>
            </Section>
          )}
        </>
      )}
    </div>
  )

  const doneStep = (
    <div className="flex flex-col items-center gap-3 py-6 text-center">
      <span className="grid h-14 w-14 place-items-center rounded-full bg-primary text-on-primary">
        <Check size={28} strokeWidth={2.4} aria-hidden />
      </span>
      <h3 className="display text-[24px] text-text-heading">
        {target.inbox ? t('channels.drawer.done.allSet') : t('channels.drawer.done.ready')}
      </h3>
      {saved && (
        <dl className="mt-1 grid w-full max-w-[340px] grid-cols-[auto_1fr] gap-x-4 gap-y-1.5 text-left text-[12.5px]">
          <dt className="text-text-muted">{t('channels.drawer.done.inbox')}</dt>
          <dd className="truncate font-semibold text-text-high">{inboxName(saved, t)}</dd>
          <dt className="text-text-muted">{t('common.fields.channel')}</dt>
          <dd className="truncate text-text-high">{t(kindMeta(saved.kind).channelLabel)}</dd>
          {saved.kind !== 'voice' && (
            <>
              <dt className="text-text-muted">{t('common.fields.entity')}</dt>
              <dd className="truncate text-text-high">{saved.entityName || t('channels.drawer.done.orgWide')}</dd>
              <dt className="text-text-muted">{t('channels.drawer.who.team')}</dt>
              <dd className="truncate text-text-high">{teamName(saved.teamId) || t('channels.drawer.done.autoAssign')}</dd>
            </>
          )}
        </dl>
      )}
    </div>
  )

  // ── footer ─────────────────────────────────────────────────────────────
  const primaryKey: MessageKey =
    step === 2
      ? kind === 'voice' && canKeys
        ? 'channels.drawer.footer.saveContinue'
        : 'common.actions.continue'
      : step === 3
        ? kind === 'voice'
          ? 'common.actions.continue'
          : editing
            ? 'channels.drawer.footer.saveContinue'
            : 'channels.drawer.footer.create'
        : 'channels.drawer.footer.finish'
  const primaryLabel = t(primaryKey)

  const footer =
    step === 5 ? (
      <>
        {!target.inbox && (
          <Button variant="secondary" onClick={restart}>
            {t('channels.drawer.footer.addAnother')}
          </Button>
        )}
        <Button className="ml-auto" onClick={onClose}>
          {t('common.actions.close')}
        </Button>
      </>
    ) : step === 1 ? (
      <>
        {phase === 'method' && (
          <Button variant="secondary" onClick={() => toPicker('channel')}>
            {t('common.actions.back')}
          </Button>
        )}
        <Button variant="secondary" className="ml-auto" onClick={onClose}>
          {t('common.actions.cancel')}
        </Button>
      </>
    ) : alt ? (
      <>
        <Button variant="secondary" onClick={() => toPicker()}>
          {t('common.actions.back')}
        </Button>
        <Button variant="secondary" className="ml-auto" onClick={onClose}>
          {t('common.actions.close')}
        </Button>
      </>
    ) : (
      <>
        <Button variant="secondary" onClick={back} disabled={busy || (step === 2 && editing && !!target.inbox)}>
          {t('common.actions.back')}
        </Button>
        <Button className="ml-auto" onClick={next} disabled={busy}>
          {busy && <Loader2 size={13} className="animate-spin" aria-hidden />}
          {primaryLabel}
        </Button>
      </>
    )

  const chName = channel ? channelName(channel, t) : null
  const methodLabel = alt ? t(alt.label) : kind && channel && channel.methods.length > 1 ? t(meta!.label) : null
  const title = saved
    ? inboxName(saved, t)
    : alt && chName
      ? chName
      : meta
        ? t(meta.label)
        : chName ?? t('channels.drawer.addInbox')
  const eyebrow = target.inbox ? t('channels.drawer.inboxSettings') : t('channels.drawer.newInbox')
  const logo = meta?.logo ?? channel?.logo

  // ── the stepper: Channel → Method (when there is a choice) → the method's setup ──
  type NavItem = { id: string; title: string; state: 'done' | 'current' | 'todo'; onJump: (() => void) | null }
  const multiMethod = !!channel && channel.methods.length > 1
  const pickable = !busy && !editing
  const navItems: NavItem[] = []
  if (!target.inbox) {
    const atChannel = step === 1 && phase === 'channel'
    const atMethod = step === 1 && phase === 'method'
    navItems.push({
      id: 'channel',
      title: chName ? t('channels.drawer.steps.channelNamed', { channel: chName }) : t('channels.drawer.steps.channel'),
      state: atChannel ? 'current' : channelId ? 'done' : 'todo',
      onJump: pickable && !atChannel ? () => toPicker('channel') : null,
    })
    if (multiMethod || !channelId)
      navItems.push({
        id: 'method',
        title: methodLabel && step > 1 ? t('channels.drawer.steps.methodNamed', { method: methodLabel }) : t('channels.drawer.steps.method'),
        state: atMethod ? 'current' : step > 1 ? 'done' : 'todo',
        onJump: pickable && multiMethod && !atMethod ? () => toPicker('method') : null,
      })
  }
  if (alt)
    navItems.push({
      id: 'connect',
      title: t(alt.zernio ? 'channels.drawer.steps.zernio' : 'channels.drawer.steps.shops'),
      state: step === 2 ? 'current' : 'todo',
      onJump: null,
    })
  else
    for (const s of STEPS) {
      if (s.n === 1) continue
      navItems.push({
        id: `s${s.n}`,
        title: s.title(kind, t),
        state: step === 5 || s.n < step ? 'done' : s.n === step ? 'current' : 'todo',
        onJump: canJump(s.n) ? () => setStep(s.n) : null,
      })
    }
  const currentTitle = step === 5 ? t('common.actions.done') : (navItems.find((x) => x.state === 'current')?.title ?? '')

  /** "Add inbox › WhatsApp › YCloud" — earlier crumbs go back to that step. */
  const crumbs: { label: string; onClick: (() => void) | null }[] = target.inbox
    ? []
    : [
        { label: t('channels.drawer.addInbox'), onClick: channelId && pickable ? () => toPicker('channel') : null },
        ...(chName ? [{ label: chName, onClick: multiMethod && pickable && step > 1 ? () => toPicker('method') : null }] : []),
        ...(methodLabel && step > 1 ? [{ label: methodLabel, onClick: null }] : []),
      ]

  return createPortal(
    <div
      className="fixed inset-0 z-[60] flex animate-fadeIn justify-end"
      style={{ background: 'var(--scrim)' }}
      onMouseDown={(e) => {
        if (e.target === e.currentTarget && !busy) onClose()
      }}
      onKeyDown={onKeyDown}
    >
      <aside
        ref={panelRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby="inbox-drawer-title"
        tabIndex={-1}
        className="flex h-[100dvh] w-full flex-col bg-card shadow-pop outline-none sm:max-w-[560px] sm:border-l sm:border-line-strong"
      >
        {/* header */}
        <div className="flex items-start gap-3 border-b border-line px-5 py-4 sm:px-6">
          {logo && <ChannelLogo channel={logo} variant="tile" size={36} title="" className="mt-0.5" />}
          <div className="min-w-0 flex-1">
            {crumbs.length > 1 ? (
              <nav aria-label={t('channels.drawer.breadcrumb')}>
                <ol className="flex min-w-0 flex-wrap items-center gap-x-1 gap-y-0.5 text-[11.5px] text-text-muted">
                  {crumbs.map((c, i) => (
                    <li key={i} className="flex min-w-0 items-center gap-1">
                      {i > 0 && <ChevronRight size={11} className="shrink-0 text-text-faint" aria-hidden />}
                      {c.onClick ? (
                        <button
                          type="button"
                          onClick={c.onClick}
                          className="cursor-pointer truncate font-semibold text-text-mid underline-offset-2 hover:text-text-high hover:underline"
                        >
                          {c.label}
                        </button>
                      ) : (
                        <span className="truncate" aria-current={i === crumbs.length - 1 ? 'page' : undefined}>
                          {c.label}
                        </span>
                      )}
                    </li>
                  ))}
                </ol>
              </nav>
            ) : (
              <div className="eyebrow">{eyebrow}</div>
            )}
            <h2 id="inbox-drawer-title" className="display mt-0.5 truncate text-[24px] leading-tight text-text-heading">
              {title}
            </h2>
            {saved && meta && <div className="truncate text-[12px] text-text-muted">{t(meta.channelLabel)}</div>}
          </div>
          <button
            type="button"
            onClick={onClose}
            aria-label={t('common.actions.close')}
            className="grid h-8 w-8 shrink-0 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
          >
            <X size={16} aria-hidden />
          </button>
        </div>

        {/* stepper — a vertical list on wider screens, a compact row on phones */}
        <nav aria-label={t('channels.drawer.steps.aria')} className="border-b border-line px-5 py-3 sm:px-6">
          <ol className="hidden flex-col sm:flex">
            {navItems.map((s, i) => (
              <li key={s.id}>
                <button
                  type="button"
                  disabled={!s.onJump}
                  onClick={() => s.onJump?.()}
                  aria-current={s.state === 'current' ? 'step' : undefined}
                  className="flex w-full items-center gap-3 py-1.5 text-left enabled:cursor-pointer enabled:hover:opacity-80"
                >
                  <StepCircle state={s.state} n={i + 1} />
                  <span
                    className={`truncate text-[13px] ${
                      s.state === 'current'
                        ? 'font-bold text-text-heading'
                        : s.state === 'done'
                          ? 'text-text-muted'
                          : 'text-text-mid'
                    }`}
                  >
                    {s.title}
                  </span>
                </button>
              </li>
            ))}
          </ol>
          <div className="flex items-center gap-2 sm:hidden">
            {navItems.map((s, i) => (
              <span key={s.id} className="flex items-center gap-2">
                {i > 0 && <span className="h-px w-3 bg-line-strong" aria-hidden />}
                <button
                  type="button"
                  disabled={!s.onJump}
                  onClick={() => s.onJump?.()}
                  aria-label={t('channels.drawer.steps.stepAria', { n: i + 1, title: s.title })}
                  aria-current={s.state === 'current' ? 'step' : undefined}
                >
                  <StepCircle state={s.state} n={i + 1} />
                </button>
              </span>
            ))}
            <span className="ml-1 min-w-0 truncate text-[13px] font-bold text-text-heading">{currentTitle}</span>
          </div>
        </nav>

        {/* body */}
        <div ref={bodyRef} className="min-h-0 flex-1 overflow-y-auto px-5 py-5 sm:px-6">
          {formError && (
            <div
              role="alert"
              className="mb-4 flex items-start gap-2 rounded-lg border border-red/35 bg-red/10 px-3 py-2 text-[12.5px] font-semibold"
              style={{ color: 'var(--red-ink)' }}
            >
              <AlertTriangle size={14} className="mt-0.5 shrink-0" aria-hidden /> {formError}
            </div>
          )}
          {notice && !formError && step !== 5 && (
            <div
              role="status"
              className="mb-4 flex items-start gap-2 rounded-lg border border-green/35 bg-green/10 px-3 py-2 text-[12.5px] font-semibold"
              style={{ color: 'var(--green-ink)' }}
            >
              <Check size={14} className="mt-0.5 shrink-0" aria-hidden /> {notice}
            </div>
          )}
          {step === 1 && chooseStep}
          {step === 2 &&
            (alt?.zernio && channel ? (
              <ZernioConnectPanel
                key={`${channel.id}-${alt.zernio.platform}`}
                channelName={channelName(channel, t)}
                method={alt}
                onChanged={onChanged}
                onAnother={restart}
                onClose={onClose}
              />
            ) : alt?.commerce && channel ? (
              <MarketplacePanel channel={alt.commerce} channelName={channelName(channel, t)} onChanged={onChanged} />
            ) : (
              connectStep
            ))}
          {step === 3 && whoStep}
          {step === 4 && testStep}
          {step === 5 && doneStep}
        </div>

        {/* footer */}
        <div className="flex flex-wrap items-center gap-2 border-t border-line bg-surface px-5 py-3.5 sm:px-6">{footer}</div>
      </aside>
    </div>,
    document.body,
  )
}

export { ApprovalChip, KindBadge } from './parts'
