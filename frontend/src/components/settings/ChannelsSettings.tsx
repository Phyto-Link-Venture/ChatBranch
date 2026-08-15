'use client'

import { useCallback, useEffect, useState } from 'react'
import {
  Facebook,
  Instagram,
  LayoutTemplate,
  Loader2,
  Mail,
  MessageCircle,
  Pencil,
  Phone,
  Plug,
  Plus,
  Send,
  Trash2,
  User,
  Webhook,
  type LucideIcon,
} from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Modal } from '@/components/ui/Modal'
import { DataTable, type Column } from '@/components/ui/DataTable'
import { WhatsAppSettings } from '@/components/settings/WhatsAppSettings'
import { TemplatesSettings } from '@/components/settings/TemplatesSettings'
import { roleHeaders } from '@/lib/role'
import { authHeaders, expireSessionOn401, hasCap } from '@/lib/auth'
import { useRole } from '@/lib/useRole'
import { EntityScopeSelect } from './EntityScopeSelect'
import {
  fetchChannelHealth,
  HEALTH_META,
  type HealthAccount,
  type HealthStatus,
  type VoiceHealth,
} from '@/lib/channelHealth'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8000/api'

// ── types ─────────────────────────────────────────────────────────────────────

type ChannelId = 'whatsapp' | 'telegram' | 'facebook' | 'instagram' | 'email' | 'voice'
type SocialChannel = 'facebook' | 'instagram' | 'telegram'

interface EmailSettings {
  smtp_host: string
  smtp_port: string
  smtp_username: string
  smtp_from: string
  smtp_tls: string
  email_subject: string
  imap_host: string
  imap_port: string
  smtp_password_masked: string
  configured: boolean
}

interface SocialAccount {
  id: string
  channel: SocialChannel
  name: string
  external_id: string
  owner: string
  access_token_masked: string
  configured: boolean
  /** Owning outlet slug; null = organization-wide (the shared fallback). */
  entity?: string | null
  entity_name?: string
}

/** SMTP sender account row from GET /api/messaging/accounts (channel "email").
 *  The backend contract is landing in parallel, so everything beyond
 *  id/channel/name is optional — the panel must render whatever arrives. */
interface EmailAccountConfig {
  host?: string
  port?: number | string
  username?: string
  /** Masked placeholder — PATCHing it back unchanged keeps the stored one. */
  password?: string
  use_tls?: boolean
  from_email?: string
}

interface EmailAccount {
  id: string
  channel: 'email'
  name: string
  /** Owning outlet slug; null = organization-wide (the shared fallback). */
  entity?: string | null
  entity_name?: string
  from_email?: string
  external_id?: string
  owner?: string
  configured?: boolean
  config?: EmailAccountConfig
}

type MessagingAccount = SocialAccount | EmailAccount

// Picker order per product spec: WhatsApp / Telegram / Messenger / Instagram /
// Email / Voice. `provider` names the REAL transport — only Messenger/Instagram
// are Meta products.
const CHANNEL_TYPES: {
  id: ChannelId
  lbl: string
  provider: string
  color: string
  Icon: LucideIcon
  beta?: boolean
}[] = [
  { id: 'whatsapp', lbl: 'WhatsApp', provider: 'Cloud API / Baileys', color: '#10B981', Icon: MessageCircle },
  { id: 'telegram', lbl: 'Telegram', provider: 'Bot API', color: '#229ED9', Icon: Send },
  { id: 'facebook', lbl: 'Messenger', provider: 'Meta Send API', color: '#3B82F6', Icon: Facebook, beta: true },
  { id: 'instagram', lbl: 'Instagram', provider: 'Meta Send API', color: '#E1306C', Icon: Instagram, beta: true },
  { id: 'email', lbl: 'Email', provider: 'SMTP', color: '#F59E0B', Icon: Mail },
  { id: 'voice', lbl: 'Voice', provider: 'Twilio SIP trunk', color: '#F22F46', Icon: Phone },
]

async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}/messaging/${path}`, {
    headers: { 'Content-Type': 'application/json', ...roleHeaders() },
    ...init,
  })
  const data = await res.json().catch(() => ({}))
  if (expireSessionOn401(res)) throw new Error('Session expired — redirecting to login…')
  if (!res.ok) throw new Error((data as { detail?: string }).detail || `HTTP ${res.status}`)
  return data as T
}

const inputCls =
  'w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[12.5px] text-text-primary outline-none transition-colors focus:border-brand-400'

function Field({
  label,
  value,
  onChange,
  placeholder,
  hint,
  secret = false,
}: {
  label: string
  value: string
  onChange: (v: string) => void
  placeholder?: string
  hint?: string
  secret?: boolean
}) {
  return (
    <label className="block">
      <span className="mb-1 block text-[11px] font-bold text-text-high">{label}</span>
      <input
        type={secret ? 'password' : 'text'}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        placeholder={placeholder}
        autoComplete="off"
        className={inputCls}
      />
      {hint && <span className="mt-1 block text-[10.5px] leading-relaxed text-text-muted">{hint}</span>}
    </label>
  )
}

function BetaChip() {
  return (
    <span
      className="rounded bg-amber/15 px-1.5 py-px text-[9px] font-bold uppercase text-amber-300"
      title="Not yet validated against the live provider"
    >
      Beta
    </span>
  )
}

// ── Email (SMTP) panel — sender accounts + the global fallback config ────────

/** Muted chip naming the channel's real transport (e.g. "SMTP", "Bot API"). */
function ProviderTag({ children }: { children: string }) {
  return (
    <span className="rounded bg-fill px-1.5 py-px text-[10px] font-semibold text-text-muted">{children}</span>
  )
}

/** Per-account SMTP manager — same multi-account pattern as the social panels.
 *  Backed by /api/messaging/accounts with channel "email" (contract landing in
 *  parallel: POST/PATCH {channel, name, from_email, host, port, username,
 *  password, use_tls}; PATCHing the masked password back keeps the stored one;
 *  POST accounts/<id>/test optionally takes {to}). Renders defensively while
 *  the backend is mid-flight — no email rows just means an empty list. */
function EmailAccountsPanel({
  accounts,
  health,
  refresh,
  canManage,
}: {
  accounts: EmailAccount[]
  health: HealthAccount[]
  refresh: () => Promise<void>
  canManage: boolean
}) {
  const [showForm, setShowForm] = useState(false)
  const [editing, setEditing] = useState<EmailAccount | null>(null)
  const [name, setName] = useState('')
  const [from, setFrom] = useState('')
  const [host, setHost] = useState('')
  const [port, setPort] = useState('587')
  const [user, setUser] = useState('')
  const [pass, setPass] = useState('')
  const [tls, setTls] = useState(true)
  const [busy, setBusy] = useState(false)
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)

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

  const fromOf = (a: EmailAccount) => a.from_email || a.config?.from_email || a.external_id || ''

  const openAdd = () => {
    setEditing(null)
    setName('')
    setFrom('')
    setHost('')
    setPort('587')
    setUser('')
    setPass('')
    setTls(true)
    setShowForm(true)
  }

  const openEdit = (a: EmailAccount) => {
    setEditing(a)
    setName(a.name || '')
    setFrom(fromOf(a))
    setHost(a.config?.host || '')
    setPort(String(a.config?.port ?? '587'))
    setUser(a.config?.username || '')
    setPass(a.config?.password || '') // masked — sending it back unchanged keeps the stored one
    setTls(a.config?.use_tls !== false)
    setShowForm(true)
  }

  const save = () =>
    run(async () => {
      const body = JSON.stringify({
        channel: 'email',
        name,
        from_email: from,
        host,
        port: Number(port) || 587,
        username: user,
        password: pass,
        use_tls: tls,
      })
      if (editing) await api(`accounts/${editing.id}`, { method: 'PATCH', body })
      else await api('accounts', { method: 'POST', body })
      setShowForm(false)
      setEditing(null)
    }, editing ? 'Email account updated.' : 'Email account added.')

  /** Bind (or unbind) this mailbox to an outlet — the server re-files its
   *  existing conversations under the new scope. */
  const bindEntity = (a: EmailAccount, slug: string | null) =>
    run(
      () => api(`accounts/${a.id}`, { method: 'PATCH', body: JSON.stringify({ entity: slug }) }),
      slug ? 'Mailbox bound to entity.' : 'Mailbox is now organization-wide.',
    )

  const removeAccount = (a: EmailAccount) => {
    if (!window.confirm(`Remove email account "${a.name}"?`)) return
    run(() => api(`accounts/${a.id}`, { method: 'DELETE' }), 'Email account removed.')
  }

  const testAccount = (a: EmailAccount) => {
    const to = window.prompt(
      `Send a test email via "${a.name}" to (leave empty to just check the SMTP connection):`,
      '',
    )
    if (to === null) return
    const rcpt = to.trim()
    run(async () => {
      await api(`accounts/${a.id}/test`, { method: 'POST', body: JSON.stringify(rcpt ? { to: rcpt } : {}) })
      setMsg({ ok: true, text: rcpt ? `Test email sent via ${a.name} to ${rcpt}.` : `${a.name}: SMTP connection OK.` })
    })
  }

  const columns: Column<EmailAccount>[] = [
    {
      key: 'account',
      header: 'Account',
      sortable: true,
      value: (a) => `${a.name} ${fromOf(a)} ${a.config?.host ?? ''}`,
      render: (a) => {
        const h = health.find((x) => String(x.id) === String(a.id))
        const hm = h ? HEALTH_META[h.health] || HEALTH_META.unknown : null
        return (
          <div className="flex items-center gap-3">
            <div
              className="grid h-9 w-9 shrink-0 place-items-center rounded-[9px] text-white"
              style={{ background: '#F59E0B' }}
            >
              <Mail size={16} aria-hidden />
            </div>
            <div className="min-w-0">
              <div className="flex items-center gap-2">
                <span className="truncate text-[13px] font-bold text-text-high">{a.name}</span>
                {h && hm && (
                  <span
                    className="shrink-0 rounded-full px-2 py-px text-[10px] font-bold"
                    style={{ background: hm.bg, color: hm.ink }}
                    title={h.detail || hm.lbl}
                  >
                    {hm.lbl}
                  </span>
                )}
              </div>
              <div className="mt-0.5 flex flex-wrap items-center gap-x-2 text-[11px] text-text-muted">
                <span className="font-mono">{fromOf(a) || '—'}</span>
                <span>· {a.config?.host || 'host not set'}</span>
                {a.owner && (
                  <span className="flex items-center gap-0.5">
                    · <User size={10} aria-hidden /> {a.owner}
                  </span>
                )}
              </div>
            </div>
          </div>
        )
      },
    },
    {
      key: 'entity',
      header: 'Entity',
      render: (a) => (
        <EntityScopeSelect
          value={a.entity ?? null}
          disabled={!canManage || busy}
          onChange={(slug) => bindEntity(a, slug)}
          globalLabel="Organization-wide"
          className="max-w-[190px]"
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
          <button
            onClick={() => testAccount(a)}
            disabled={busy}
            title="Test connection"
            aria-label={`Test ${a.name}`}
            className="grid h-7 w-7 shrink-0 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high disabled:opacity-50"
          >
            <Plug size={14} aria-hidden />
          </button>
          {canManage && (
            <>
              <button
                onClick={() => openEdit(a)}
                disabled={busy}
                title="Edit account"
                aria-label={`Edit ${a.name}`}
                className="grid h-7 w-7 shrink-0 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high disabled:opacity-50"
              >
                <Pencil size={14} aria-hidden />
              </button>
              <button
                onClick={() => removeAccount(a)}
                disabled={busy}
                title="Remove account"
                aria-label={`Remove ${a.name}`}
                className="grid h-7 w-7 shrink-0 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-red/15 hover:text-red-300 disabled:opacity-50"
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
    <div>
      <div className="mb-2 flex items-center gap-2">
        <Mail size={14} className="text-amber-300" aria-hidden />
        <span className="text-[12.5px] font-bold text-text-high">Email accounts</span>
        <ProviderTag>SMTP</ProviderTag>
      </div>

      <Modal
        open={showForm}
        onClose={() => {
          setShowForm(false)
          setEditing(null)
        }}
        title={editing ? `Edit ${editing.name || 'email account'}` : 'Add email account'}
        subtitle="An SMTP sender mailbox for outbound email."
        size="lg"
        footer={
          <>
            <Button
              variant="ghost"
              onClick={() => {
                setShowForm(false)
                setEditing(null)
              }}
            >
              Cancel
            </Button>
            <Button variant="primary" onClick={save} disabled={busy || !name.trim() || !host.trim()}>
              {editing ? <Pencil size={13} aria-hidden /> : <Plus size={13} aria-hidden />}{' '}
              {editing ? 'Save account' : 'Add account'}
            </Button>
          </>
        }
      >
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
          <Field label="Account name" value={name} onChange={setName} placeholder="e.g. Support mailbox" />
          <Field label="From address" value={from} onChange={setFrom} placeholder="support@yourco.com" />
          <Field label="SMTP host" value={host} onChange={setHost} placeholder="smtp.gmail.com" />
          <Field label="Port" value={port} onChange={setPort} placeholder="587" hint="465 = implicit TLS, 587 = STARTTLS" />
          <Field
            label="Username"
            value={user}
            onChange={setUser}
            placeholder="usually the from address"
            hint="Gmail needs the FULL address (name@domain), not just the part before the @."
          />
          <Field
            label="Password"
            value={pass}
            onChange={setPass}
            secret
            placeholder="app password"
            hint={editing ? 'Leave the masked value untouched to keep the stored password.' : undefined}
          />
        </div>
        <label className="mt-3 flex w-fit cursor-pointer items-center gap-2 text-[12px] text-text-primary">
          <input
            type="checkbox"
            checked={tls}
            onChange={(e) => setTls(e.target.checked)}
            style={{ accentColor: '#3B82F6' }}
          />
          Use TLS
        </label>
      </Modal>

      <DataTable
        columns={columns}
        rows={accounts}
        rowKey={(a) => a.id}
        searchKeys={['account']}
        searchPlaceholder="Search email accounts…"
        empty="No email sender accounts yet — outbound email uses the default / fallback SMTP settings below until one is added. Add as many mailboxes as you need."
        toolbarRight={
          canManage ? (
            <Button variant="primary" onClick={openAdd}>
              <Plus size={13} aria-hidden /> Add account
            </Button>
          ) : undefined
        }
      />

      {msg && (
        <div className={`mt-3 text-[12px] font-semibold ${msg.ok ? 'text-green-300' : 'text-red-300'}`}>{msg.text}</div>
      )}
    </div>
  )
}

/** The pre-existing app-level SMTP config (config.json / .env) — kept as the
 *  sandbox fallback used when no sender accounts exist. Super-admin writes. */
function EmailFallbackForm({ email, refresh }: { email: EmailSettings; refresh: () => Promise<void> }) {
  const { role } = useRole()
  const isSuper = role === 'superadmin'
  const [host, setHost] = useState(email.smtp_host)
  const [port, setPort] = useState(email.smtp_port || '587')
  const [user, setUser] = useState(email.smtp_username)
  const [pass, setPass] = useState(email.smtp_password_masked)
  const [from, setFrom] = useState(email.smtp_from)
  const [subject, setSubject] = useState(email.email_subject)
  const [imapHost, setImapHost] = useState(email.imap_host)
  const [imapPort, setImapPort] = useState(email.imap_port || '993')
  const [testTo, setTestTo] = useState('')
  const [busy, setBusy] = useState(false)
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)

  // re-seed from the server after every refresh (same behavior as the old
  // Email & Social tab — saved values, incl. the masked password, win)
  useEffect(() => {
    setHost(email.smtp_host)
    setPort(email.smtp_port || '587')
    setUser(email.smtp_username)
    setPass(email.smtp_password_masked)
    setFrom(email.smtp_from)
    setSubject(email.email_subject)
    setImapHost(email.imap_host)
    setImapPort(email.imap_port || '993')
  }, [email])

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

  const saveEmail = () =>
    run(
      () =>
        api('settings', {
          method: 'POST',
          body: JSON.stringify({
            smtp_host: host,
            smtp_port: port,
            smtp_username: user,
            smtp_password: pass,
            smtp_from: from,
            email_subject: subject,
            imap_host: imapHost,
            imap_port: imapPort,
          }),
        }),
      'Email settings saved.',
    )

  const testEmail = () =>
    run(
      () => api('settings/test', { method: 'POST', body: JSON.stringify({ to: testTo }) }),
      `Test email sent to ${testTo}.`,
    )

  return (
    <div>
      <div className="mb-1 flex items-center gap-2">
        <span className="text-[12.5px] font-bold text-text-high">Default / fallback SMTP</span>
        <span
          className={`rounded-full px-2 py-px text-[10px] font-bold ${
            email.configured ? 'bg-green/15 text-green-300' : 'bg-fill text-text-muted'
          }`}
        >
          {email.configured ? 'configured' : 'sandbox'}
        </span>
      </div>
      <p className="mb-2 text-[11px] leading-relaxed text-text-muted">
        Used when no email accounts are added above — seeded from <code className="rounded bg-fill px-1">.env</code> /{' '}
        <code className="rounded bg-fill px-1">config.json</code>. Also carries the IMAP inbound settings.
      </p>
      {!isSuper && (
        <p className="mb-2 text-[11px] text-text-muted">Email settings are managed by the super admin.</p>
      )}
      <div className="grid grid-cols-3 gap-3">
        <Field label="SMTP host" value={host} onChange={setHost} placeholder="smtp.gmail.com" />
        <Field label="Port" value={port} onChange={setPort} placeholder="587" hint="465 = implicit TLS, 587 = STARTTLS" />
        <Field label="From address" value={from} onChange={setFrom} placeholder="support@yourco.com" />
        <Field
          label="Username"
          value={user}
          onChange={setUser}
          placeholder="usually the from address"
          hint="Gmail needs the FULL address (name@domain), not just the part before the @."
        />
        <Field label="Password" value={pass} onChange={setPass} secret placeholder="app password" />
        <Field label="Default subject" value={subject} onChange={setSubject} placeholder="Regarding your account" />
        <Field
          label="IMAP host (inbound)"
          value={imapHost}
          onChange={setImapHost}
          placeholder="imap.gmail.com"
          hint="Pulls customer replies into the inbox (same login as SMTP). Leave empty for outbound-only."
        />
        <Field label="IMAP port" value={imapPort} onChange={setImapPort} placeholder="993" />
      </div>
      <div className="mt-3 flex flex-wrap items-center gap-2">
        {isSuper && (
          <Button variant="primary" onClick={saveEmail} disabled={busy}>
            {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : null} Save email settings
          </Button>
        )}
        <input
          value={testTo}
          onChange={(e) => setTestTo(e.target.value)}
          placeholder="you@company.com"
          aria-label="Test email recipient"
          className="h-[34px] w-[220px] rounded-lg border border-line-strong bg-card px-3 text-[12px] text-text-primary outline-none focus:border-brand-400"
        />
        <Button variant="secondary" onClick={testEmail} disabled={busy || !email.configured || !testTo.includes('@')}>
          <Plug size={13} aria-hidden /> Send test email
        </Button>
      </div>
      {msg && (
        <div className={`mt-3 text-[12px] font-semibold ${msg.ok ? 'text-green-300' : 'text-red-300'}`}>{msg.text}</div>
      )}
    </div>
  )
}

/** Email channel panel: multi-account SMTP senders on top, the global
 *  fallback config (used when no accounts exist) clearly labeled below. */
function EmailPanel({
  email,
  accounts,
  health,
  refresh,
  canManage,
}: {
  email: EmailSettings
  accounts: EmailAccount[]
  health: HealthAccount[]
  refresh: () => Promise<void>
  canManage: boolean
}) {
  return (
    <div className="flex flex-col gap-5">
      <EmailAccountsPanel accounts={accounts} health={health} refresh={refresh} canManage={canManage} />
      <div className="border-t border-line pt-4">
        <EmailFallbackForm email={email} refresh={refresh} />
      </div>
    </div>
  )
}

// ── Social account panel (Messenger / Instagram / Telegram) ──────────────────

// `provider` = the real transport shown in the panel header — Meta only owns
// Messenger + Instagram; Telegram is its own Bot API (BotFather token).
const SOCIAL_META: Record<
  SocialChannel,
  { lbl: string; provider: string; color: string; Icon: LucideIcon; accountLbl: string; namePh: string; beta?: boolean }
> = {
  facebook: {
    lbl: 'Messenger', provider: 'Meta Send API',
    color: '#3B82F6', Icon: Facebook, accountLbl: 'page', namePh: 'e.g. Northwind Page', beta: true,
  },
  instagram: {
    lbl: 'Instagram', provider: 'Meta Send API',
    color: '#E1306C', Icon: Instagram, accountLbl: 'account', namePh: 'e.g. Northwind IG', beta: true,
  },
  telegram: {
    lbl: 'Telegram', provider: 'Bot API (BotFather token)',
    color: '#229ED9', Icon: Send, accountLbl: 'bot', namePh: 'e.g. Northwind Bot',
  },
}

function SocialPanel({
  channel,
  accounts,
  refresh,
  canManage,
}: {
  channel: SocialChannel
  accounts: SocialAccount[]
  refresh: () => Promise<void>
  canManage: boolean
}) {
  const meta = SOCIAL_META[channel]
  const [showAdd, setShowAdd] = useState(false)
  const [accName, setAccName] = useState('')
  const [extId, setExtId] = useState('')
  const [token, setToken] = useState('')
  const [busy, setBusy] = useState(false)
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)

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

  const addAccount = () =>
    run(async () => {
      await api('accounts', {
        method: 'POST',
        body: JSON.stringify({ channel, name: accName, external_id: extId, access_token: token }),
      })
      setAccName('')
      setExtId('')
      setToken('')
      setShowAdd(false)
    }, 'Account linked.')

  const removeAccount = (a: SocialAccount) => {
    if (!window.confirm(`Remove ${meta.lbl} account "${a.name}"?`)) return
    run(() => api(`accounts/${a.id}`, { method: 'DELETE' }), 'Account removed.')
  }

  const testAccount = (a: SocialAccount) =>
    run(async () => {
      const r = await api<{ name: string; id: string }>(`accounts/${a.id}/test`, { method: 'POST' })
      setMsg({ ok: true, text: `${a.name}: connected as ${r.name || r.id}` })
    })

  /** Bind (or unbind) this account to an outlet. The server re-files the
   *  account's existing conversations under the new scope. */
  const bindEntity = (a: SocialAccount, slug: string | null) =>
    run(
      () => api(`accounts/${a.id}`, { method: 'PATCH', body: JSON.stringify({ entity: slug }) }),
      slug ? 'Account bound to entity.' : 'Account is now organization-wide.',
    )

  const setTelegramWebhook = (a: SocialAccount) => {
    const base = window.prompt(
      'Public https:// origin Telegram should deliver updates to (e.g. your ngrok/tunnel URL):',
      'https://',
    )
    if (!base || !base.trim()) return
    run(async () => {
      const r = await api<{ url: string }>(`accounts/${a.id}/set-webhook`, {
        method: 'POST',
        body: JSON.stringify({ base_url: base.trim() }),
      })
      setMsg({ ok: true, text: `Webhook registered: ${r.url}` })
    })
  }

  const columns: Column<SocialAccount>[] = [
    {
      key: 'account',
      header: 'Account',
      sortable: true,
      value: (a) => `${a.name} ${a.external_id ?? ''} ${a.owner ?? ''}`,
      render: (a) => (
        <div className="flex items-center gap-3">
          <div
            className="grid h-9 w-9 shrink-0 place-items-center rounded-[9px] text-white"
            style={{ background: meta.color }}
          >
            <meta.Icon size={16} aria-hidden />
          </div>
          <div className="min-w-0">
            <div className="truncate text-[13px] font-bold text-text-high">{a.name}</div>
            <div className="mt-0.5 flex flex-wrap items-center gap-x-2 text-[11px] text-text-muted">
              <span className="font-mono">ID {a.external_id || '—'}</span>
              <span>· token {a.access_token_masked || 'not set'}</span>
              {a.owner && (
                <span className="flex items-center gap-0.5">
                  · <User size={10} aria-hidden /> {a.owner}
                </span>
              )}
            </div>
          </div>
        </div>
      ),
    },
    {
      key: 'entity',
      header: 'Entity',
      render: (a) => (
        <EntityScopeSelect
          value={a.entity ?? null}
          disabled={!canManage || busy}
          onChange={(slug) => bindEntity(a, slug)}
          globalLabel="Organization-wide"
          className="max-w-[190px]"
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
          {canManage && channel === 'telegram' && (
            <button
              onClick={() => setTelegramWebhook(a)}
              disabled={busy || !a.configured}
              title="Set webhook"
              aria-label={`Set webhook for ${a.name}`}
              className="grid h-7 w-7 shrink-0 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high disabled:cursor-not-allowed disabled:opacity-50"
            >
              <Webhook size={14} aria-hidden />
            </button>
          )}
          <button
            onClick={() => testAccount(a)}
            disabled={busy || !a.configured}
            title="Test connection"
            aria-label={`Test ${a.name}`}
            className="grid h-7 w-7 shrink-0 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high disabled:cursor-not-allowed disabled:opacity-50"
          >
            <Plug size={14} aria-hidden />
          </button>
          {canManage && (
            <button
              onClick={() => removeAccount(a)}
              disabled={busy}
              title="Remove account"
              aria-label={`Remove ${a.name}`}
              className="grid h-7 w-7 shrink-0 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-red/15 hover:text-red-300 disabled:opacity-50"
            >
              <Trash2 size={14} aria-hidden />
            </button>
          )}
        </div>
      ),
    },
  ]

  return (
    <div>
      <div className="mb-2 flex items-center gap-2">
        <meta.Icon size={14} style={{ color: meta.color }} aria-hidden />
        <span className="text-[12.5px] font-bold text-text-high">{meta.lbl} accounts</span>
        <ProviderTag>{meta.provider}</ProviderTag>
        {meta.beta && <BetaChip />}
      </div>

      <Modal
        open={showAdd}
        onClose={() => setShowAdd(false)}
        title={`Add ${meta.lbl} ${meta.accountLbl}`}
        subtitle={`Link a ${meta.lbl} ${meta.accountLbl} via ${meta.provider}.`}
        size="md"
        footer={
          <>
            <Button variant="ghost" onClick={() => setShowAdd(false)}>
              Cancel
            </Button>
            <Button variant="primary" onClick={addAccount} disabled={busy || !token.trim()}>
              <Plus size={13} aria-hidden /> Link account
            </Button>
          </>
        }
      >
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
          <Field label="Account name" value={accName} onChange={setAccName} placeholder={meta.namePh} />
          {channel !== 'telegram' && (
            <Field
              label={channel === 'facebook' ? 'Page ID' : 'IG account ID'}
              value={extId}
              onChange={setExtId}
              placeholder="1234567890"
              hint="Used to route inbound webhook events to this account."
            />
          )}
          <Field
            label={channel === 'telegram' ? 'Bot token' : 'Page access token'}
            value={token}
            onChange={setToken}
            secret
            placeholder={channel === 'telegram' ? '123456:ABC-DEF…' : 'EAAG…'}
            hint={
              channel === 'telegram'
                ? 'From @BotFather (/newbot). The bot id is filled in automatically via getMe.'
                : 'Meta App Dashboard → Messenger/Instagram → Access Tokens (page-scoped).'
            }
          />
        </div>
      </Modal>

      <DataTable
        columns={columns}
        rows={accounts}
        rowKey={(a) => a.id}
        searchKeys={['account']}
        searchPlaceholder={`Search ${meta.lbl} accounts…`}
        empty={`No ${meta.lbl} ${meta.accountLbl}s linked yet — ${meta.lbl} runs in sandbox until one is. Add as many as you need; inbound traffic is routed to the right one automatically.`}
        toolbarRight={
          canManage ? (
            <Button variant="primary" onClick={() => setShowAdd(true)}>
              <Plus size={13} aria-hidden /> Add account
            </Button>
          ) : undefined
        }
      />

      <p className="mt-2 text-[11px] leading-relaxed text-text-muted">
        {channel === 'telegram' ? (
          <>
            Telegram delivers updates to{' '}
            <code className="rounded bg-fill px-1">…/api/messaging/webhook/telegram/&lt;bot token&gt;</code> — use
            the <strong>Set webhook</strong> button after linking the bot.
          </>
        ) : (
          <>
            Meta webhook: <code className="rounded bg-fill px-1">{API.replace(/\/api$/, '')}/api/messaging/webhook</code>{' '}
            — subscribe the Meta app to <code className="rounded bg-fill px-1">messages</code> under{' '}
            {channel === 'facebook' ? 'Messenger' : 'Instagram'}, same verify token as WhatsApp.
          </>
        )}
      </p>

      {msg && (
        <div className={`mt-3 text-[12px] font-semibold ${msg.ok ? 'text-green-300' : 'text-red-300'}`}>{msg.text}</div>
      )}
    </div>
  )
}

// ── Voice (Twilio) panel — SIP trunk creds ────────────────────────────────────

interface VoiceSettingsPayload {
  sip_username: string
  sip_password_masked: string
  termination_uri: string
  /** Per-field origin of the effective value: dashboard row / .env / unset. */
  source: Record<'sip_username' | 'sip_password' | 'termination_uri', 'settings' | 'env' | ''>
  sip_trunk_configured: boolean
  health: { configured: boolean; health: HealthStatus; detail: string; live_check: boolean }
}

/** Voice channel panel: the Twilio SIP trunk creds (username / password /
 *  termination URI), dashboard-managed with .env as the fallback. There's
 *  no REST endpoint that proves a SIP credential works short of placing a
 *  real call, so health here just reflects whether everything needed to
 *  dial is present. SIP_OUTBOUND_TRUNK_ID stays .env-only — the voice-agent
 *  worker is a separate process that reads it once at boot. */
function VoicePanel({ refreshHealth }: { refreshHealth: () => void }) {
  const { role } = useRole()
  const isSuper = role === 'superadmin'
  const [data, setData] = useState<VoiceSettingsPayload | null>(null)
  const [failed, setFailed] = useState(false)
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [uri, setUri] = useState('')
  const [busy, setBusy] = useState(false)
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)

  const load = useCallback(async () => {
    try {
      const res = await fetch(`${API}/whatsapp/voice-settings`, { headers: authHeaders() })
      if (expireSessionOn401(res)) return
      if (!res.ok) throw new Error(`HTTP ${res.status}`)
      const d = (await res.json()) as VoiceSettingsPayload
      setData(d)
      setUsername(d.sip_username)
      setPassword(d.sip_password_masked)
      setUri(d.termination_uri)
      setFailed(false)
    } catch {
      setFailed(true)
    }
  }, [])

  useEffect(() => {
    load()
  }, [load])

  const save = async () => {
    setBusy(true)
    setMsg(null)
    try {
      const res = await fetch(`${API}/whatsapp/voice-settings`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json', ...authHeaders() },
        body: JSON.stringify({ sip_username: username, sip_password: password, termination_uri: uri }),
      })
      if (expireSessionOn401(res)) return
      const d = (await res.json().catch(() => ({}))) as VoiceSettingsPayload & { detail?: string }
      if (!res.ok) throw new Error(d.detail || `HTTP ${res.status}`)
      setData(d)
      setUsername(d.sip_username)
      setPassword(d.sip_password_masked)
      setUri(d.termination_uri)
      setMsg({ ok: true, text: 'SIP trunk creds saved.' })
      refreshHealth()
    } catch (e) {
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    } finally {
      setBusy(false)
    }
  }

  const test = async () => {
    setBusy(true)
    setMsg(null)
    try {
      const res = await fetch(`${API}/whatsapp/voice-settings/test`, {
        method: 'POST',
        headers: authHeaders(),
      })
      if (expireSessionOn401(res)) return
      const d = (await res.json().catch(() => ({}))) as { ok?: boolean; health?: string; detail?: string }
      if (!res.ok) throw new Error(d.detail || `HTTP ${res.status}`)
      setMsg({ ok: !!d.ok, text: d.detail || `Voice status: ${d.health}` })
      await load()
      refreshHealth()
    } catch (e) {
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    } finally {
      setBusy(false)
    }
  }

  if (failed) {
    return (
      <div className="py-6 text-[12px] text-text-muted">
        Voice settings unavailable — is the backend API running on port 8000?
      </div>
    )
  }
  if (!data) return null

  const srcHint = (k: keyof VoiceSettingsPayload['source']): string | undefined =>
    data.source[k] === 'env' ? 'Currently from backend/.env — saving here overrides it, no restart needed.' : undefined
  const hm = HEALTH_META[data.health.health] || HEALTH_META.unknown

  return (
    <div>
      <div className="mb-1 flex items-center gap-2">
        <Phone size={14} style={{ color: '#F22F46' }} aria-hidden />
        <span className="text-[12.5px] font-bold text-text-high">Voice trunk (Twilio)</span>
        <ProviderTag>LiveKit SIP → Twilio</ProviderTag>
        <span
          className={`rounded-full px-2 py-px text-[10px] font-bold ${
            data.sip_trunk_configured ? 'bg-green/15 text-green-300' : 'bg-fill text-text-muted'
          }`}
          title="SIP_OUTBOUND_TRUNK_ID env + a termination URI"
        >
          {data.sip_trunk_configured ? 'SIP trunk configured' : 'SIP trunk not set'}
        </span>
        {data.health.configured && (
          <span
            className="rounded-full px-2 py-px text-[10px] font-bold"
            style={{ background: hm.bg, color: hm.ink }}
            title={data.health.detail || hm.lbl}
          >
            {hm.lbl}
          </span>
        )}
      </div>
      <p className="mb-2 text-[11px] leading-relaxed text-text-muted">
        SIP trunk creds are what the voice-agent worker actually dials with. Saved values override the{' '}
        <code className="rounded bg-fill px-1">.env</code> fallback immediately.
      </p>
      {!isSuper && <p className="mb-2 text-[11px] text-text-muted">Voice settings are managed by the super admin.</p>}
      <div className="grid grid-cols-3 gap-3">
        <Field
          label="SIP username"
          value={username}
          onChange={setUsername}
          placeholder="your Twilio SIP username"
          hint={srcHint('sip_username') || 'Twilio Console → Elastic SIP Trunking.'}
        />
        <Field
          label="SIP password"
          value={password}
          onChange={setPassword}
          secret
          placeholder="your Twilio SIP password"
          hint={srcHint('sip_password') || 'Leave the masked value untouched to keep the stored password.'}
        />
        <Field
          label="Termination URI"
          value={uri}
          onChange={setUri}
          placeholder="your-trunk.pstn.twilio.com"
          hint={srcHint('termination_uri') || 'From the Twilio console (Elastic SIP Trunking).'}
        />
      </div>
      <div className="mt-3 flex flex-wrap items-center gap-2">
        {isSuper && (
          <Button variant="primary" onClick={save} disabled={busy || !username.trim() || !password.trim()}>
            {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : null} Save SIP creds
          </Button>
        )}
        <Button variant="secondary" onClick={test} disabled={busy}>
          <Plug size={13} aria-hidden /> Check configuration
        </Button>
      </div>
      <p className="mt-3 text-[11px] leading-relaxed text-text-muted">
        The outbound trunk id stays in <code className="rounded bg-fill px-1">backend/.env</code> as{' '}
        <code className="rounded bg-fill px-1">SIP_OUTBOUND_TRUNK_ID</code> (the voice-agent worker is a separate
        process that reads it once at boot; one-time setup via{' '}
        <code className="rounded bg-fill px-1">voice_agent/setup_trunk.py</code>).
      </p>
      {msg && (
        <div className={`mt-3 text-[12px] font-semibold ${msg.ok ? 'text-green-300' : 'text-red-300'}`}>{msg.text}</div>
      )}
    </div>
  )
}

// ── Channels tab — one place to configure every messaging channel ────────────

/** Worst-health wins when a channel has several accounts. */
const HEALTH_RANK: Record<HealthStatus, number> = {
  healthy: 0,
  unknown: 1,
  degraded: 1,
  disconnected: 2,
  banned: 2,
}

/**
 * Settings → Channels: the consolidated channel manager. Pick a channel type
 * (WhatsApp / Telegram / Messenger / Instagram / Email), then configure it —
 * link as many accounts as the channel supports. WhatsApp bundles its
 * message-template manager; social channels and email are multi-account
 * (email additionally keeps the global SMTP config as the fallback sender).
 */
export function ChannelsSettings() {
  const [active, setActive] = useState<ChannelId>('whatsapp')
  // channels.manage gates add/edit/delete across the channel panels (read after
  // mount — localStorage). WhatsApp reads it itself; email/social get it here.
  const [canManage, setCanManage] = useState(false)
  useEffect(() => setCanManage(hasCap('channels.manage')), [])
  const [waCount, setWaCount] = useState<number | null>(null)
  // Cloud API accounts only — Meta templates don't exist for Baileys numbers.
  const [waCloudCount, setWaCloudCount] = useState<number>(0)
  const [email, setEmail] = useState<EmailSettings | null>(null)
  const [accounts, setAccounts] = useState<MessagingAccount[]>([])
  const [messagingUp, setMessagingUp] = useState(true)
  const [health, setHealth] = useState<Partial<Record<ChannelId, { status: HealthStatus; detail: string }>>>({})
  const [healthAccounts, setHealthAccounts] = useState<HealthAccount[]>([])
  const [voice, setVoice] = useState<VoiceHealth | null>(null)

  const refreshMessaging = useCallback(async () => {
    // settled independently — a mid-deploy accounts endpoint must not blank
    // the whole tab, and vice versa
    const [s, a] = await Promise.allSettled([
      api<EmailSettings>('settings'),
      api<{ accounts: MessagingAccount[] }>('accounts'),
    ])
    if (s.status === 'fulfilled') {
      setEmail(s.value)
      setMessagingUp(true)
    } else {
      setEmail(null)
      setMessagingUp(false)
    }
    setAccounts(a.status === 'fulfilled' && Array.isArray(a.value.accounts) ? a.value.accounts : [])
  }, [])

  const refreshHealth = useCallback(async () => {
    try {
      const payload = await fetchChannelHealth()
      const next: Partial<Record<ChannelId, { status: HealthStatus; detail: string }>> = {}
      for (const acc of payload.accounts) {
        const id = acc.channel as ChannelId
        const cur = next[id]
        if (!cur || HEALTH_RANK[acc.health] > HEALTH_RANK[cur.status]) {
          next[id] = { status: acc.health, detail: acc.detail }
        }
      }
      // global SMTP fallback — worst-wins against any email account rows above
      if (payload.email?.configured) {
        const g = { status: payload.email.health as HealthStatus, detail: payload.email.detail }
        const cur = next.email
        if (!cur || HEALTH_RANK[g.status] > HEALTH_RANK[cur.status]) next.email = g
      }
      // voice trunk is a singleton, not an account row
      if (payload.voice?.configured) {
        next.voice = { status: payload.voice.health, detail: payload.voice.detail }
      }
      setVoice(payload.voice ?? null)
      setHealth(next)
      setHealthAccounts(payload.accounts)
    } catch {
      setHealth({}) // health endpoint down — picker just skips the dots
      setHealthAccounts([])
      setVoice(null)
    }
  }, [])

  useEffect(() => {
    refreshMessaging()
    refreshHealth()
    // WhatsApp count for the picker — the panel keeps it fresh once mounted
    fetch(`${API}/whatsapp/accounts`, { headers: authHeaders() })
      .then((r) => (r.ok ? r.json() : Promise.reject()))
      .then((d: { accounts: unknown[] }) => setWaCount(d.accounts.length))
      .catch(() => setWaCount(null))
  }, [refreshMessaging, refreshHealth])

  const onWaAccountsChanged = useCallback((count: number, cloud: number) => {
    setWaCount(count)
    setWaCloudCount(cloud)
    refreshHealth().catch(() => {})
  }, [refreshHealth])

  const refreshAll = useCallback(async () => {
    await refreshMessaging()
    refreshHealth().catch(() => {})
  }, [refreshMessaging, refreshHealth])

  const countLabel = (id: ChannelId): string => {
    if (id === 'whatsapp') {
      return waCount === null ? 'accounts —' : `${waCount} account${waCount === 1 ? '' : 's'}`
    }
    if (id === 'voice') {
      if (!voice) return 'status —'
      if (!voice.configured) return 'trunk not set'
      return voice.live_check ? 'live monitoring' : 'trunk configured'
    }
    const n = accounts.filter((a) => a.channel === id).length
    if (id === 'email' && n === 0) return email?.configured ? 'fallback configured' : 'no accounts'
    return `${n} account${n === 1 ? '' : 's'}`
  }

  return (
    <div className="flex flex-col gap-5 pt-3.5">
      {/* channel-type picker — flex-wrap with a per-card min width so nothing
          (e.g. "Voice") is clipped on narrower widths; cards grow to fill a
          wide row and wrap to the next line when space runs out. */}
      <div className="flex flex-wrap gap-2" role="tablist" aria-label="Channel type">
        {CHANNEL_TYPES.map((c) => {
          const isActive = c.id === active
          const h = health[c.id]
          return (
            <button
              key={c.id}
              role="tab"
              aria-selected={isActive}
              onClick={() => setActive(c.id)}
              className={`flex min-w-[150px] flex-1 basis-[150px] cursor-pointer items-center gap-2.5 rounded-xl border px-3 py-2.5 text-left transition-colors ${
                isActive ? 'border-brand-400/40 bg-brand-400/10' : 'border-line bg-card hover:bg-fill'
              }`}
            >
              <div
                className="grid h-8 w-8 shrink-0 place-items-center rounded-lg text-white"
                style={{ background: c.color }}
              >
                <c.Icon size={15} aria-hidden />
              </div>
              <div className="min-w-0 flex-1">
                <div className="flex items-center gap-1.5">
                  <span className="truncate text-[12.5px] font-bold text-text-high">{c.lbl}</span>
                  {h && (
                    <span
                      className="h-[7px] w-[7px] shrink-0 rounded-full"
                      style={{ background: HEALTH_META[h.status].dot }}
                      title={`${HEALTH_META[h.status].lbl}${h.detail ? ` — ${h.detail}` : ''}`}
                    />
                  )}
                </div>
                <div className="truncate text-[10.5px] text-text-muted">
                  {c.provider} · {countLabel(c.id)}
                </div>
              </div>
            </button>
          )
        })}
      </div>

      {/* selected channel's panel */}
      {active === 'whatsapp' ? (
        <div className="flex flex-col gap-5">
          <WhatsAppSettings onAccountsChanged={onWaAccountsChanged} />
          {/* Cloud API template manager lives with the channel it belongs to —
              and only appears once a Cloud API (WhatsApp Business) account is
              connected: Meta templates mean nothing without one, and showing
              them on an empty channel just confuses. */}
          {waCloudCount > 0 && (
          <div className="border-t border-line pt-4">
            <div className="mb-1 flex items-center gap-2">
              <LayoutTemplate size={14} className="text-brand-300" aria-hidden />
              <span className="text-[12.5px] font-bold text-text-high">Message templates</span>
            </div>
            <p className="text-[11px] leading-relaxed text-text-muted">
              Pre-approved WhatsApp templates per Cloud API account — required to message outside the 24-hour
              window. Live accounts go through Meta review; sandbox templates are usable immediately.
            </p>
            <TemplatesSettings />
          </div>
          )}
        </div>
      ) : active === 'voice' ? (
        // voice talks to /api/whatsapp (health module), not /api/messaging —
        // it must stay reachable even when the messaging endpoints are down
        <VoicePanel refreshHealth={() => { refreshHealth().catch(() => {}) }} />
      ) : !messagingUp ? (
        <div className="py-6 text-[12px] text-text-muted">
          Channel settings unavailable — is the backend API running on port 8000?
        </div>
      ) : active === 'email' ? (
        email && (
          <EmailPanel
            key="email"
            email={email}
            accounts={accounts.filter((a): a is EmailAccount => a.channel === 'email')}
            health={healthAccounts.filter((h) => h.channel === 'email')}
            refresh={refreshAll}
            canManage={canManage}
          />
        )
      ) : (
        <SocialPanel
          key={active}
          channel={active}
          accounts={accounts.filter((a): a is SocialAccount => a.channel === active)}
          refresh={refreshAll}
          canManage={canManage}
        />
      )}
    </div>
  )
}
