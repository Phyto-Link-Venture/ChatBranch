'use client'

/**
 * Advanced (collapsed by default on /channels): the one-time, shared settings
 * that are not an inbox of their own —
 *  - the Meta webhook every WhatsApp Cloud number (and Messenger/Instagram)
 *    delivers to, routed by the receiving number;
 *  - the default / fallback SMTP sender + the IMAP inbound settings, used when
 *    no email inbox exists (seeded from .env / config.json). Super admin writes.
 */

import { useEffect, useState } from 'react'
import { ChevronDown, Loader2, Plug } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Toggle } from '@/components/ui/Toggle'
import { CopyRow, MetaWebhookRows, inputCls } from '@/components/settings/WhatsAppSettings'
import { API_ORIGIN, ApiError, api, errText } from './inboxes'

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

function Field({
  label,
  value,
  onChange,
  placeholder,
  hint,
  secret,
  disabled,
}: {
  label: string
  value: string
  onChange: (v: string) => void
  placeholder?: string
  hint?: string
  secret?: boolean
  disabled?: boolean
}) {
  return (
    <label className="block">
      <span className="mb-1 block text-[12px] font-semibold text-text-high">{label}</span>
      <input
        type={secret ? 'password' : 'text'}
        autoComplete="off"
        className={inputCls}
        value={value}
        disabled={disabled}
        onChange={(e) => onChange(e.target.value)}
        placeholder={placeholder}
      />
      {hint && <span className="mt-1 block text-[11.5px] leading-relaxed text-text-muted">{hint}</span>}
    </label>
  )
}

function EmailFallbackForm({ isSuper }: { isSuper: boolean }) {
  const [data, setData] = useState<EmailSettings | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [f, setF] = useState<Record<string, string>>({})
  const [tls, setTls] = useState(true)
  const [testTo, setTestTo] = useState('')
  const [busy, setBusy] = useState(false)
  const [msg, setMsg] = useState<{ tone: 'ok' | 'info' | 'error'; text: string } | null>(null)

  const seed = (d: EmailSettings) => {
    setData(d)
    setF({
      smtp_host: d.smtp_host,
      smtp_port: d.smtp_port || '587',
      smtp_username: d.smtp_username,
      smtp_password: d.smtp_password_masked, // masked — sent back unchanged keeps the stored one
      smtp_from: d.smtp_from,
      email_subject: d.email_subject,
      imap_host: d.imap_host,
      imap_port: d.imap_port || '993',
    })
    setTls(String(d.smtp_tls).toLowerCase() !== 'false')
  }

  useEffect(() => {
    api<EmailSettings>('messaging/settings')
      .then(seed)
      .catch((e) => setLoadError(errText(e)))
  }, [])

  const set = (k: string) => (v: string) => setF((p) => ({ ...p, [k]: v }))

  const save = async () => {
    setBusy(true)
    setMsg(null)
    try {
      seed(
        await api<EmailSettings>('messaging/settings', {
          method: 'POST',
          body: JSON.stringify({ ...f, smtp_tls: tls ? 'true' : 'false' }),
        }),
      )
      setMsg({ tone: 'ok', text: 'Default mail settings saved.' })
    } catch (e) {
      setMsg({ tone: 'error', text: errText(e) })
    } finally {
      setBusy(false)
    }
  }

  const test = async () => {
    setBusy(true)
    setMsg(null)
    try {
      await api('messaging/settings/test', { method: 'POST', body: JSON.stringify({ to: testTo.trim() }) })
      setMsg({ tone: 'ok', text: `Test email sent to ${testTo.trim()}.` })
    } catch (e) {
      setMsg({ tone: e instanceof ApiError && e.status === 501 ? 'info' : 'error', text: errText(e) })
    } finally {
      setBusy(false)
    }
  }

  if (loadError) return <p className="text-[12.5px] text-text-muted">Mail settings unavailable — {loadError}</p>
  if (!data)
    return (
      <p className="flex items-center gap-2 text-[12.5px] text-text-muted">
        <Loader2 size={13} className="animate-spin" aria-hidden /> Loading…
      </p>
    )

  const ro = !isSuper
  return (
    <div className="flex flex-col gap-4">
      <p className="text-[12.5px] leading-relaxed text-text-mid">
        Used to send when no email inbox exists, and to pull customer replies in over IMAP. Seeded from{' '}
        <code className="rounded bg-fill px-1">.env</code>
        {ro && ' — only the super admin can change it'}.{' '}
        <span
          className="ml-1 rounded-full px-2 py-px text-[11px] font-semibold"
          style={data.configured ? { color: 'var(--green-ink)' } : { color: 'var(--text-muted)' }}
        >
          {data.configured ? 'Configured' : 'Sandbox'}
        </span>
      </p>
      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">
        <Field label="SMTP host" value={f.smtp_host} onChange={set('smtp_host')} placeholder="smtp.gmail.com" disabled={ro} />
        <Field
          label="Port"
          value={f.smtp_port}
          onChange={set('smtp_port')}
          placeholder="587"
          hint="465 = implicit TLS, 587 = STARTTLS"
          disabled={ro}
        />
        <Field label="From address" value={f.smtp_from} onChange={set('smtp_from')} placeholder="support@yourco.com" disabled={ro} />
        <Field
          label="Username"
          value={f.smtp_username}
          onChange={set('smtp_username')}
          placeholder="usually the from address"
          disabled={ro}
        />
        <Field label="Password" value={f.smtp_password} onChange={set('smtp_password')} secret placeholder="app password" disabled={ro} />
        <Field
          label="Default subject"
          value={f.email_subject}
          onChange={set('email_subject')}
          placeholder="Regarding your enquiry"
          disabled={ro}
        />
        <Field
          label="IMAP host (inbound)"
          value={f.imap_host}
          onChange={set('imap_host')}
          placeholder="imap.gmail.com"
          hint="Same login as SMTP. Leave empty for outbound-only."
          disabled={ro}
        />
        <Field label="IMAP port" value={f.imap_port} onChange={set('imap_port')} placeholder="993" disabled={ro} />
        <div className="flex items-center justify-between gap-3 self-start rounded-lg border border-line px-3 py-2.5 sm:mt-5">
          <span className="text-[12.5px] font-semibold text-text-high">Use TLS</span>
          <Toggle on={tls} onChange={setTls} label="Use TLS" disabled={ro} />
        </div>
      </div>
      <div className="flex flex-wrap items-center gap-2">
        {isSuper && (
          <Button onClick={save} disabled={busy}>
            {busy && <Loader2 size={13} className="animate-spin" aria-hidden />} Save mail settings
          </Button>
        )}
        <input
          className={`${inputCls} !w-[230px]`}
          value={testTo}
          onChange={(e) => setTestTo(e.target.value)}
          placeholder="you@company.com"
          aria-label="Test email recipient"
        />
        <Button variant="secondary" onClick={test} disabled={busy || !data.configured || !testTo.includes('@')}>
          <Plug size={13} aria-hidden /> Send test email
        </Button>
      </div>
      {msg && (
        <div
          role={msg.tone === 'error' ? 'alert' : 'status'}
          className="text-[12.5px] font-semibold"
          style={{
            color: msg.tone === 'ok' ? 'var(--green-ink)' : msg.tone === 'info' ? 'var(--amber-ink)' : 'var(--red-ink)',
          }}
        >
          {msg.text}
        </div>
      )}
    </div>
  )
}

export function ChannelsAdvanced({ verifyToken, isSuper }: { verifyToken: string; isSuper: boolean }) {
  const [open, setOpen] = useState(false)
  return (
    <section className="rounded-xl border border-line bg-card">
      <button
        type="button"
        onClick={() => setOpen((v) => !v)}
        aria-expanded={open}
        className="flex w-full cursor-pointer flex-wrap items-center gap-x-2 gap-y-0.5 px-4 py-3.5 text-left sm:px-5"
      >
        <ChevronDown
          size={16}
          aria-hidden
          className="shrink-0 text-text-muted transition-transform"
          style={{ transform: open ? 'none' : 'rotate(-90deg)' }}
        />
        <span className="display text-[18px] leading-tight text-text-heading">Advanced</span>
        <span className="text-[12.5px] text-text-muted">Meta webhook · default mail settings</span>
      </button>
      {open && (
        <div className="flex flex-col gap-7 border-t border-line px-4 py-5 sm:px-5">
          <div className="flex flex-col gap-3">
            <h3 className="eyebrow">Meta webhook</h3>
            <p className="text-[12.5px] leading-relaxed text-text-mid">
              One-time setup, shared by every WhatsApp Business number: paste these in Meta App Dashboard → WhatsApp
              → Configuration → Webhook and subscribe to <code className="rounded bg-fill px-1">messages</code>.
              Inbound traffic is routed to the right inbox by the receiving number. Local development needs a public
              https tunnel to the API.
            </p>
            <div className="grid grid-cols-1 gap-2 lg:grid-cols-2">
              <MetaWebhookRows verifyToken={verifyToken} />
              <CopyRow label="Messenger & Instagram callback URL" value={`${API_ORIGIN}/api/messaging/webhook`} />
            </div>
          </div>
          <div className="flex flex-col gap-3">
            <h3 className="eyebrow">Default mail settings (SMTP + IMAP)</h3>
            <EmailFallbackForm isSuper={isSuper} />
          </div>
        </div>
      )}
    </section>
  )
}
