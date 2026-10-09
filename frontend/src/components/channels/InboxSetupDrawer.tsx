'use client'

/**
 * The step-by-step inbox setup (Chatwoot-style): a right-side drawer on
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
import { AlertTriangle, Check, ChevronDown, Loader2, Plug, RefreshCw, Webhook, X } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Select } from '@/components/ui/Select'
import { Toggle } from '@/components/ui/Toggle'
import { EntityPicker } from '@/components/ui/EntityPicker'
import { ChannelLogo } from '@/components/ui/ChannelLogo'
import { accountRole, type AccountRole } from '@/components/ui/Badges'
import {
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
  WidgetLookFields,
  widgetLookErrors,
  type WidgetLook,
} from '@/components/settings/WidgetsSettings'
import {
  API_ORIGIN,
  KINDS,
  api,
  errText,
  fromMessaging,
  fromVoice,
  fromWa,
  fromWidget,
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
  type Widget,
} from './inboxes'
import { CRED_FIELDS, DEFAULT_NAME, GUIDES, type FieldSpec } from './setupSpec'

export type Step = 1 | 2 | 3 | 4 | 5 // 5 = done

export interface DrawerTarget {
  kind: InboxKind | null
  /** The inbox being edited; null = a new one. */
  inbox: Inbox | null
  step: Step
}

const STEPS: { n: 1 | 2 | 3 | 4; title: (k: InboxKind | null) => string }[] = [
  { n: 1, title: () => 'Choose channel' },
  { n: 2, title: (k) => (k ? kindMeta(k).connectTitle : 'Connect') },
  { n: 3, title: () => 'Who answers it' },
  { n: 4, title: () => 'Test and finish' },
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
  return (
    <span className="mb-1 block text-[12px] font-semibold text-text-high">
      {children}
      {optional && <span className="ml-1 font-normal text-text-muted">(optional)</span>}
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
  isSuper,
  voiceConfigured,
  onClose,
  onChanged,
}: {
  target: DrawerTarget
  teams: Team[]
  verifyToken: string
  isSuper: boolean
  /** The SIP trunk is a singleton — its block edits the existing one. */
  voiceConfigured: Inbox | null
  onClose: () => void
  /** Something was saved — refresh the inbox list. */
  onChanged: () => void
}) {
  const [kind, setKind] = useState<InboxKind | null>(target.kind)
  const [step, setStep] = useState<Step>(target.step)
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
  const [showDelivery, setShowDelivery] = useState(false)
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

  /** Seed every field from an inbox (edit) or the kind's defaults (new). */
  const seed = useCallback((k: InboxKind, inbox: Inbox | null) => {
    const c: Record<string, string> = {}
    if (inbox?.wa) {
      c.phone_number_id = inbox.wa.phone_number_id || ''
      c.waba_id = inbox.wa.waba_id || ''
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
          }
        : WIDGET_DEFAULTS,
    )
    setName(inbox?.name ?? '')
    setEntity(inbox?.entity ?? null)
    setTeamId(inbox?.teamId != null ? String(inbox.teamId) : '')
    setRole(inbox?.wa ? accountRole(inbox.wa) : k === 'wa_qr' ? 'conversation' : 'reminder')
    setAiDefault(inbox?.aiDefault ?? '')
    setReviewMode(inbox?.reviewMode ?? '')
    setDelivery(deliveryStateFrom(inbox?.wa?.delivery))
    setErrors({})
    setTouched({})
    setFormError(null)
    setTest(null)
    setTgResult(null)
  }, [])

  useEffect(() => {
    if (target.kind) seed(target.kind, target.inbox)
  }, [seed, target])

  // ── modal behaviour: scroll lock, Esc, focus trap, focus restore ──────────
  const panelRef = useRef<HTMLDivElement>(null)
  useEffect(() => {
    const restore = document.activeElement as HTMLElement | null
    const prev = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    const t = window.setTimeout(() => panelRef.current?.focus(), 0)
    return () => {
      window.clearTimeout(t)
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
        return `${f.label} is required.`
      }
      return f.check ? f.check(value) : null
    },
    [editing, kind],
  )

  const validateConnect = (): boolean => {
    const next: Record<string, string> = {}
    if (kind === 'wa_qr' && !ack) next.ack = 'Tick the box to confirm you understand the risk.'
    if (kind === 'website') Object.assign(next, widgetLookErrors(look))
    if (kind === 'voice' && editing && !creds.sip_password?.trim() && !saved?.voice?.sip_password_masked)
      next.sip_password = 'SIP password is required.'
    for (const f of fields) {
      const msg = fieldError(f, creds[f.key] ?? '')
      if (msg) next[f.key] = msg
    }
    setErrors(next)
    setTouched(Object.fromEntries(Object.keys(next).map((k) => [k, true])))
    return Object.keys(next).length === 0
  }

  const validateWho = (): boolean => {
    const next: Record<string, string> = {}
    if (kind !== 'voice' && !name.trim()) next.name = 'Give the inbox a name your team will recognise.'
    setErrors(next)
    return Object.keys(next).length === 0
  }

  // ── payloads ───────────────────────────────────────────────────────────
  const who = () => {
    const o: Record<string, string> = { team_id: teamId, ai_default: aiDefault, ai_review_mode: reviewMode }
    // only send the binding when it changes — a rebind re-files the inbox's
    // conversations and writes an audit row
    if (!saved || (saved.entity ?? null) !== entity) o.entity = entity ?? ''
    return o
  }

  /** Persist everything the drawer holds; returns the refreshed inbox. */
  const persist = async (): Promise<Inbox> => {
    if (!kind) throw new Error('Choose a channel first.')
    const c = (k: string) => (creds[k] ?? '').trim()
    const id = saved?.id

    if (kind === 'voice') {
      const body: Record<string, string> = { sip_username: c('sip_username'), termination_uri: c('termination_uri') }
      if (c('sip_password')) body.sip_password = c('sip_password')
      const v = await api<VoiceSettings>('whatsapp/voice-settings', { method: 'PUT', body: JSON.stringify(body) })
      const inbox = fromVoice(v)
      if (!inbox) throw new Error('The trunk did not save — check the fields.')
      return inbox
    }

    if (kind === 'wa_cloud' || kind === 'wa_qr') {
      const body: Record<string, string> = { name: name.trim(), role, ...who(), ...deliveryPatch(delivery) }
      if (kind === 'wa_cloud') {
        if (c('phone_number_id')) body.phone_number_id = c('phone_number_id')
        body.waba_id = c('waba_id')
        // secrets: blank = keep the stored value (never send an empty string)
        if (c('access_token')) body.access_token = c('access_token')
        if (c('app_secret')) body.app_secret = c('app_secret')
      } else if (!id) body.provider = 'baileys'
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

    // email + social → /api/messaging/accounts
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
    } else {
      if (kind !== 'telegram') body.external_id = c('external_id')
      if (c('access_token')) body.access_token = c('access_token')
      // Messenger / Instagram: Meta signs every webhook with the app secret
      if (kind !== 'telegram' && c('app_secret')) body.app_secret = c('app_secret')
    }
    if (!id) body.channel = kind === 'email' ? 'email' : SOCIAL_CHANNEL[kind]
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
      setSaved(inbox)
      // never keep secrets in memory once stored — blank now means "keep"
      setCreds((prev) => {
        const next = { ...prev }
        for (const f of fields) if (f.secret) next[f.key] = ''
        return next
      })
      setNotice(wasNew ? `${inbox.name} is connected.` : 'Changes saved.')
      onChanged()
      setStep(then)
    } catch (e) {
      setFormError(errText(e))
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
    setShowGuide(true)
    setNotice(null)
    setStep(2)
  }

  const next = () => {
    setFormError(null)
    if (step === 2) {
      if (kind === 'voice' && !isSuper) {
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
      if (!name.trim() && kind) setName(kind === 'email' && creds.from_email?.trim() ? creds.from_email.trim() : DEFAULT_NAME[kind])
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
          setTest({ tone: 'ok', text: r.detail || `Test email sent to ${testTo.trim()}.` })
        } catch (e) {
          const status = (e as { status?: number }).status
          setTest({ tone: status === 501 ? 'info' : 'error', text: errText(e) })
        }
      } else setTest(await testInbox(saved))
    } finally {
      setTesting(false)
    }
  }

  const registerTelegram = async () => {
    if (!saved) return
    const base = tgBase.trim().replace(/\/+$/, '')
    if (!/^https:\/\/\S+$/.test(base)) {
      setTgResult({ tone: 'error', text: 'Use the public https:// address that reaches this API (e.g. your tunnel URL).' })
      return
    }
    setTgResult(null)
    try {
      const r = await api<{ url?: string; detail?: string }>(`messaging/accounts/${saved.id}/set-webhook`, {
        method: 'POST',
        body: JSON.stringify({ base_url: base }),
      })
      setTgResult({ tone: 'ok', text: r.url ? `Webhook registered: ${r.url}` : r.detail || 'Webhook registered.' })
    } catch (e) {
      const status = (e as { status?: number }).status
      setTgResult({ tone: status === 501 ? 'info' : 'error', text: errText(e) })
    }
  }

  const rotateToken = async () => {
    if (!saved?.widget) return
    if (!window.confirm('Rotate the widget token? Every page still using the old snippet stops working until you update it.'))
      return
    setBusy(true)
    setFormError(null)
    try {
      const w = await api<Widget>(`widgets/${saved.id}`, { method: 'PATCH', body: JSON.stringify({ rotate_token: true }) })
      setSaved(fromWidget(w))
      setNotice('Token rotated — update the snippet on your site.')
      onChanged()
    } catch (e) {
      setFormError(errText(e))
    } finally {
      setBusy(false)
    }
  }

  // ── render helpers ─────────────────────────────────────────────────────
  const meta = kind ? kindMeta(kind) : null
  const teamName = (id: number | null) => teams.find((t) => t.id === id)?.name
  const voiceLocked = kind === 'voice' && !isSuper

  const renderField = (f: FieldSpec) => {
    const v = creds[f.key] ?? ''
    const err = touched[f.key] ? errors[f.key] : undefined
    const current =
      f.secret && saved
        ? f.key === 'access_token'
          ? saved.wa?.access_token_masked || saved.msg?.access_token_masked
          : f.key === 'app_secret'
            ? saved.wa?.app_secret_masked || saved.msg?.app_secret_masked
            : f.key === 'password'
              ? saved.msg?.config?.password_masked
              : f.key === 'sip_password'
                ? saved.voice?.sip_password_masked
                : ''
        : ''
    return (
      <label key={f.key} className={`block ${f.half ? '' : 'sm:col-span-2'}`}>
        <Label optional={f.optional}>
          {f.label}
          {current ? <span className="ml-1.5 font-normal text-text-muted">· current {current}</span> : null}
        </Label>
        <input
          className={`${inputCls} ${err ? '!border-[color:var(--red-ink)]' : ''}`}
          type={f.secret ? 'password' : 'text'}
          autoComplete="off"
          inputMode={f.inputMode}
          value={v}
          disabled={voiceLocked}
          placeholder={f.secret && saved ? 'Leave blank to keep the current one' : f.placeholder}
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
        <Help>{f.help}</Help>
      </label>
    )
  }

  // ── step bodies ────────────────────────────────────────────────────────
  const chooseStep = (
    <div className="flex flex-col gap-2">
      <p className="text-[12.5px] text-text-muted">Which channel should this inbox answer?</p>
      {KINDS.map((k) => (
        <button
          key={k.kind}
          type="button"
          onClick={() => choose(k.kind)}
          className={`flex cursor-pointer items-center gap-3 rounded-xl border px-3 py-2.5 text-left transition-colors hover:bg-fill-hover ${
            kind === k.kind ? 'border-text-primary' : 'border-line'
          }`}
        >
          <ChannelLogo channel={k.logo} variant="tile" size={32} title="" />
          <span className="min-w-0 flex-1">
            <span className="flex items-center gap-1.5 text-[13.5px] font-bold text-text-heading">
              {k.label}
              {k.badge && <KindBadge badge={k.badge} />}
            </span>
            <span className="block truncate text-[12px] text-text-muted">{k.blurb}</span>
          </span>
        </button>
      ))}
    </div>
  )

  const connectStep = !kind ? null : (
    <div className="flex flex-col gap-5">
      {kind === 'wa_qr' && (
        <>
          <div
            className="flex gap-2.5 rounded-lg border border-amber/40 bg-amber/10 px-3 py-2.5 text-[12.5px] leading-relaxed"
            style={{ color: 'var(--amber-ink)' }}
          >
            <AlertTriangle size={16} className="mt-0.5 shrink-0" aria-hidden />
            <span>
              <b>For demos only.</b> WhatsApp by QR uses the unofficial WhatsApp Web protocol. It needs no Meta
              approval, but it breaks WhatsApp&apos;s terms of service and the number can be banned. Use a spare
              number — never your main business line.
            </span>
          </div>
          <ul className="flex list-disc flex-col gap-1 pl-5 text-[12.5px] leading-relaxed text-text-mid">
            <li>Needs the WhatsApp gateway running alongside the API (<code className="rounded bg-fill px-1">npm run dev:baileys</code>).</li>
            <li>Text messages and voice calls; Meta templates stay on WhatsApp Business numbers.</li>
            <li>No 24-hour window — and no Meta compliance either.</li>
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
            <span className="text-[12.5px] text-text-high">I understand the risk and will pair a spare number.</span>
          </label>
          <FieldError text={errors.ack} />
        </>
      )}

      {kind === 'website' && (
        <>
          <p className="text-[12.5px] leading-relaxed text-text-mid">
            A chat bubble for your site: one script tag, no build step. Visitor conversations land in the same inbox
            as WhatsApp and email, with the same AI, teams and automations.
          </p>
          <WidgetLookFields value={look} onChange={setLook} errors={errors as Partial<Record<keyof WidgetLook, string>>} />
        </>
      )}

      {voiceLocked && (
        <div className="rounded-lg border border-line bg-well px-3 py-2 text-[12.5px] text-text-mid">
          Voice trunk credentials are managed by the super admin — you can review them here.
        </div>
      )}

      {fields.length > 0 && <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">{fields.map(renderField)}</div>}

      {kind === 'email' && (
        <div className="flex items-center justify-between gap-3 rounded-lg border border-line px-3 py-2.5">
          <span>
            <span className="block text-[12.5px] font-semibold text-text-high">Use TLS</span>
            <span className="block text-[11.5px] text-text-muted">Leave on unless your provider says otherwise.</span>
          </span>
          <Toggle on={tls} onChange={setTls} label="Use TLS" />
        </div>
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
            Where do I find these?
          </button>
          {showGuide && (
            <ol className="flex list-decimal flex-col gap-1 px-3 pb-3 pl-9 text-[12px] leading-relaxed text-text-mid">
              {GUIDES[kind]!.map((s) => (
                <li key={s}>{s}</li>
              ))}
            </ol>
          )}
        </div>
      )}
    </div>
  )

  const isWa = kind === 'wa_cloud' || kind === 'wa_qr'

  const whoStep = !kind ? null : kind === 'voice' ? (
    <div className="flex flex-col gap-3 text-[12.5px] leading-relaxed text-text-mid">
      <p>
        Calls on the SIP trunk are answered by the AI voice agent. Which contact is dialled — and with which voice
        and script — is chosen per call from a conversation, a campaign or a flow step.
      </p>
      <p className="text-text-muted">Nothing to set here. Continue to check the configuration.</p>
    </div>
  ) : (
    <div className="flex flex-col gap-5">
      <label className="block">
        <Label>{kind === 'website' ? 'Internal name' : 'Inbox name'}</Label>
        <input
          className={`${inputCls} ${errors.name ? '!border-[color:var(--red-ink)]' : ''}`}
          value={name}
          onChange={(e) => {
            setName(e.target.value)
            if (errors.name && e.target.value.trim()) setErrors((p) => ({ ...p, name: '' }))
          }}
          placeholder={DEFAULT_NAME[kind]}
          aria-invalid={!!errors.name}
        />
        {errors.name ? <FieldError text={errors.name} /> : <Help>What your team sees in the inbox and on reports.</Help>}
      </label>

      <label className="block">
        <Label>Entity</Label>
        <EntityPicker value={entity} onChange={setEntity} ariaLabel="Entity that owns this inbox" />
        <Help>
          The entity that owns this inbox — conversations arriving here are filed under it. Organization-wide
          inboxes are the fallback every entity shares.
        </Help>
      </label>

      <label className="block">
        <Label optional>Team</Label>
        <Select
          value={teamId}
          onChange={setTeamId}
          size="md"
          ariaLabel="Team that answers this inbox"
          options={[
            { value: '', label: 'No team', hint: '— the entity’s auto-assign team applies' },
            ...teams.map((t) => ({
              value: String(t.id),
              label: t.name,
              hint: t.auto_assign === false ? '(no round-robin)' : undefined,
            })),
          ]}
        />
        <Help>
          {teams.length === 0
            ? 'No teams yet — create them under Teams to route this inbox’s conversations to a group.'
            : 'New conversations here go to this team first, round-robin across its members.'}
        </Help>
      </label>

      {isWa && (
        <div>
          <Label>Account role</Label>
          <Segmented
            value={role}
            onChange={setRole}
            ariaLabel="Account role"
            options={[
              { value: 'reminder', label: 'Reminder (business)' },
              { value: 'conversation', label: 'Conversation (personal)' },
            ]}
          />
          <Help>
            {role === 'reminder'
              ? 'Sends approved templates straight away and hands contacts to a conversation number. The chatbot is off by default.'
              : 'Holds the AI conversations, customer-initiated. Sends are human-paced to protect the number.'}
          </Help>
        </div>
      )}

      <div>
        <Label>AI replies</Label>
        <Segmented
          value={aiDefault}
          onChange={setAiDefault}
          ariaLabel="AI reply default"
          options={[
            { value: '', label: isWa ? 'By role' : 'Default' },
            { value: 'on', label: 'On' },
            { value: 'off', label: 'Off' },
          ]}
        />
        <Help>
          Whether the assistant answers new conversations here
          {isWa
            ? ` — “By role” means ${role === 'reminder' ? 'off on reminder numbers (unless the routing rules turn it on)' : 'on for conversation numbers'}`
            : ' — “Default” means on'}
          . An agent can still switch it per chat; their choice always wins.
        </Help>
      </div>

      <div>
        <Label>Check the AI before it sends</Label>
        <Select
          size="md"
          value={reviewMode}
          onChange={(v) => setReviewMode(v as ReviewMode)}
          ariaLabel="AI review mode for this inbox"
          options={[
            { value: '', label: 'Use the AI monitor setting', hint: '(inherit)' },
            { value: 'auto', label: 'Send automatically' },
            { value: 'review_flagged', label: 'Hold risky replies for review' },
            { value: 'review_all', label: 'Hold every AI reply for review' },
            { value: 'shadow', label: 'Shadow — draft only, never send' },
          ]}
        />
        <Help>
          Held replies wait in the AI monitor (and on the chat) until someone approves, edits or rejects them.
        </Help>
      </div>

      {isWa && (
        <div className="rounded-lg border border-line">
          <button
            type="button"
            onClick={() => setShowDelivery((v) => !v)}
            aria-expanded={showDelivery}
            className="flex w-full cursor-pointer items-center gap-2 px-3 py-2.5 text-left"
          >
            <ChevronDown
              size={14}
              aria-hidden
              className="shrink-0 text-text-muted transition-transform"
              style={{ transform: showDelivery ? 'none' : 'rotate(-90deg)' }}
            />
            <span className="text-[12.5px] font-semibold text-text-high">Delivery &amp; AI</span>
            <span className="truncate text-[11.5px] text-text-muted">quiet hours · pacing · voice LLM &amp; STT</span>
          </button>
          {showDelivery && (
            <div className="border-t border-line px-3 py-3">
              <DeliveryEditor value={delivery} onChange={setDelivery} />
            </div>
          )}
        </div>
      )}
    </div>
  )

  const webhookUrl = `${API_ORIGIN}/api/messaging/webhook`
  const testStep = !kind ? null : (
    <div className="flex flex-col gap-6">
      {!saved ? (
        <p className="text-[12.5px] text-text-muted">
          {voiceLocked ? 'The voice trunk is not set up yet — ask the super admin to add its credentials.' : 'Save the inbox first.'}
        </p>
      ) : (
        <>
          {kind === 'wa_qr' && (
            <Section title="Pair the phone">
              <BaileysPairPanel accountId={saved.id} onConnected={onChanged} />
              <VcfTip />
            </Section>
          )}

          <Section title="Run a test">
            {kind === 'email' && (
              <label className="block">
                <Label optional>Send a test email to</Label>
                <input
                  className={inputCls}
                  inputMode="email"
                  value={testTo}
                  onChange={(e) => setTestTo(e.target.value)}
                  placeholder="you@company.com — blank checks the connection only"
                />
              </label>
            )}
            <div className="flex flex-wrap items-center gap-2">
              <Button variant="secondary" onClick={runTest} disabled={testing}>
                {testing ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Plug size={13} aria-hidden />}
                {kind === 'voice' ? 'Check configuration' : kind === 'website' ? 'Check the widget answers' : 'Test connection'}
              </Button>
            </div>
            {test && <ResultLine result={test} />}
          </Section>

          {kind === 'wa_cloud' && (
            <Section title="Point Meta at ChatBranch">
              <p className="text-[12.5px] leading-relaxed text-text-mid">
                Paste these in Meta App Dashboard → WhatsApp → Configuration → Webhook and subscribe to the{' '}
                <code className="rounded bg-fill px-1">messages</code> field. One webhook serves every number.
              </p>
              <MetaWebhookRows verifyToken={verifyToken} />
            </Section>
          )}

          {(kind === 'messenger' || kind === 'instagram') && (
            <Section title="Point Meta at ChatBranch">
              <p className="text-[12.5px] leading-relaxed text-text-mid">
                In your Meta app, subscribe {kind === 'messenger' ? 'Messenger' : 'Instagram'} to the{' '}
                <code className="rounded bg-fill px-1">messages</code> field with this callback — same verify token
                as WhatsApp.
              </p>
              <CopyRow label="Callback URL" value={webhookUrl} />
              <CopyRow label="Verify token" value={verifyToken} />
            </Section>
          )}

          {kind === 'telegram' && (
            <Section title="Register the webhook">
              <p className="text-[12.5px] leading-relaxed text-text-mid">
                Telegram delivers updates to <code className="rounded bg-fill px-1">…/api/messaging/webhook/telegram/&lt;bot token&gt;</code>.
                Enter the public https address that reaches this API.
              </p>
              <div className="flex flex-col gap-2 sm:flex-row">
                <input
                  className={inputCls}
                  inputMode="url"
                  value={tgBase}
                  onChange={(e) => setTgBase(e.target.value)}
                  placeholder="https://your-tunnel.example.com"
                  aria-label="Public base URL"
                />
                <Button variant="secondary" onClick={registerTelegram} className="shrink-0">
                  <Webhook size={13} aria-hidden /> Register
                </Button>
              </div>
              {tgResult && <ResultLine result={tgResult} />}
            </Section>
          )}

          {kind === 'website' && saved.widget && (
            <Section
              title="Put it on your site"
              aside={
                <button
                  type="button"
                  onClick={rotateToken}
                  disabled={busy}
                  className="inline-flex cursor-pointer items-center gap-1 text-[12px] font-semibold text-text-mid hover:text-text-high disabled:opacity-50"
                >
                  <RefreshCw size={12} aria-hidden /> Rotate token
                </button>
              }
            >
              <EmbedSnippet value={saved.widget.embed} />
              <p className="text-[11.5px] leading-relaxed text-text-muted">
                The token is public by design — it only lets a visitor start a conversation. Rotate it if the snippet
                ends up somewhere it shouldn&apos;t.
              </p>
            </Section>
          )}

          {kind === 'email' && (
            <p className="text-[12px] leading-relaxed text-text-muted">
              Customer replies are pulled in over IMAP using the shared mail settings under Advanced on the Inboxes
              page.
            </p>
          )}

          {kind === 'voice' && (
            <Section title="Run the voice agent">
              <ol className="flex list-decimal flex-col gap-1.5 pl-5 text-[12.5px] leading-relaxed text-text-mid">
                <li>
                  Put the LiveKit project in <code className="rounded bg-fill px-1">backend/.env</code>:{' '}
                  <code className="rounded bg-fill px-1">LIVEKIT_URL</code>,{' '}
                  <code className="rounded bg-fill px-1">LIVEKIT_API_KEY</code>,{' '}
                  <code className="rounded bg-fill px-1">LIVEKIT_API_SECRET</code>.
                </li>
                <li>
                  Once, create the outbound SIP trunk and copy the printed{' '}
                  <code className="rounded bg-fill px-1">SIP_OUTBOUND_TRUNK_ID</code> into the same file:
                </li>
              </ol>
              <CopyRow label="One-time trunk setup" value="npm --prefix backend/voice_agent run setup-trunk" />
              <p className="text-[12.5px] leading-relaxed text-text-mid">
                Then start the worker alongside the API — it answers every AI call, SIP and WhatsApp:
              </p>
              <CopyRow label="Run the worker" value="npm run dev:agent" />
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
      <h3 className="display text-[24px] text-text-heading">{target.inbox ? 'All set' : 'Inbox ready'}</h3>
      {saved && (
        <dl className="mt-1 grid w-full max-w-[340px] grid-cols-[auto_1fr] gap-x-4 gap-y-1.5 text-left text-[12.5px]">
          <dt className="text-text-muted">Inbox</dt>
          <dd className="truncate font-semibold text-text-high">{saved.name}</dd>
          <dt className="text-text-muted">Channel</dt>
          <dd className="truncate text-text-high">{kindMeta(saved.kind).channelLabel}</dd>
          {saved.kind !== 'voice' && (
            <>
              <dt className="text-text-muted">Entity</dt>
              <dd className="truncate text-text-high">{saved.entityName || 'Organization-wide'}</dd>
              <dt className="text-text-muted">Team</dt>
              <dd className="truncate text-text-high">{teamName(saved.teamId) || 'Entity auto-assign'}</dd>
            </>
          )}
        </dl>
      )}
    </div>
  )

  // ── footer ─────────────────────────────────────────────────────────────
  const primaryLabel =
    step === 2
      ? kind === 'voice'
        ? isSuper
          ? 'Save & continue'
          : 'Continue'
        : 'Continue'
      : step === 3
        ? kind === 'voice'
          ? 'Continue'
          : editing
            ? 'Save & continue'
            : 'Create inbox'
        : 'Finish'

  const footer =
    step === 5 ? (
      <>
        {!target.inbox && (
          <Button
            variant="secondary"
            onClick={() => {
              setSaved(null)
              setKind(null)
              setStep(1)
              setNotice(null)
            }}
          >
            Add another
          </Button>
        )}
        <Button className="ml-auto" onClick={onClose}>
          Close
        </Button>
      </>
    ) : step === 1 ? (
      <>
        <Button variant="secondary" onClick={onClose}>
          Cancel
        </Button>
        <Button className="ml-auto" onClick={() => kind && choose(kind)} disabled={!kind}>
          Continue
        </Button>
      </>
    ) : (
      <>
        <Button variant="secondary" onClick={back} disabled={busy || (step === 2 && editing && !!target.inbox)}>
          Back
        </Button>
        <Button className="ml-auto" onClick={next} disabled={busy}>
          {busy && <Loader2 size={13} className="animate-spin" aria-hidden />}
          {primaryLabel}
        </Button>
      </>
    )

  const title = saved ? saved.name : meta ? meta.label : 'Add an inbox'
  const eyebrow = target.inbox ? 'Inbox settings' : 'New inbox'

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
        className="flex h-[100dvh] w-full flex-col bg-card shadow-pop outline-none sm:max-w-[480px] sm:border-l sm:border-line-strong"
      >
        {/* header */}
        <div className="flex items-start gap-3 border-b border-line px-5 py-4 sm:px-6">
          {meta && <ChannelLogo channel={meta.logo} variant="tile" size={36} title="" className="mt-0.5" />}
          <div className="min-w-0 flex-1">
            <div className="eyebrow">{eyebrow}</div>
            <h2 id="inbox-drawer-title" className="display mt-0.5 truncate text-[24px] leading-tight text-text-heading">
              {title}
            </h2>
            {saved && meta && <div className="truncate text-[12px] text-text-muted">{meta.channelLabel}</div>}
          </div>
          <button
            type="button"
            onClick={onClose}
            aria-label="Close"
            className="grid h-8 w-8 shrink-0 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
          >
            <X size={16} aria-hidden />
          </button>
        </div>

        {/* stepper — a vertical list on wider screens, a compact row on phones */}
        <nav aria-label="Setup steps" className="border-b border-line px-5 py-3 sm:px-6">
          <ol className="hidden flex-col sm:flex">
            {STEPS.map((s) => {
              const state = step === 5 || s.n < step ? 'done' : s.n === step ? 'current' : 'todo'
              const jump = canJump(s.n)
              return (
                <li key={s.n}>
                  <button
                    type="button"
                    disabled={!jump}
                    onClick={() => jump && setStep(s.n)}
                    aria-current={state === 'current' ? 'step' : undefined}
                    className="flex w-full items-center gap-3 py-1.5 text-left enabled:cursor-pointer enabled:hover:opacity-80"
                  >
                    <StepCircle state={state} n={s.n} />
                    <span
                      className={`text-[13px] ${
                        state === 'current'
                          ? 'font-bold text-text-heading'
                          : state === 'done'
                            ? 'text-text-muted'
                            : 'text-text-mid'
                      }`}
                    >
                      {s.title(kind)}
                    </span>
                  </button>
                </li>
              )
            })}
          </ol>
          <div className="flex items-center gap-2 sm:hidden">
            {STEPS.map((s, i) => {
              const state = step === 5 || s.n < step ? 'done' : s.n === step ? 'current' : 'todo'
              return (
                <span key={s.n} className="flex items-center gap-2">
                  {i > 0 && <span className="h-px w-3 bg-line-strong" aria-hidden />}
                  <button
                    type="button"
                    disabled={!canJump(s.n)}
                    onClick={() => setStep(s.n)}
                    aria-label={`Step ${s.n}: ${s.title(kind)}`}
                    aria-current={state === 'current' ? 'step' : undefined}
                  >
                    <StepCircle state={state} n={s.n} />
                  </button>
                </span>
              )
            })}
            <span className="ml-1 min-w-0 truncate text-[13px] font-bold text-text-heading">
              {step === 5 ? 'Done' : STEPS[step - 1].title(kind)}
            </span>
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
          {step === 2 && connectStep}
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

export function KindBadge({ badge }: { badge: 'Beta' | 'Demo' }) {
  return (
    <span
      className={`rounded-full px-1.5 py-px text-[9.5px] font-bold uppercase tracking-[0.06em] ${
        badge === 'Demo' ? 'bg-amber/15' : 'bg-fill text-text-mid'
      }`}
      style={badge === 'Demo' ? { color: 'var(--amber-ink)' } : undefined}
      title={badge === 'Demo' ? 'Unofficial transport — demo use only' : 'Not yet validated against the live provider'}
    >
      {badge}
    </span>
  )
}
