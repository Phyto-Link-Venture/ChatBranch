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
import { Trans, useT } from '@/i18n'
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
  const t = useT()
  const [data, setData] = useState<EmailSettings | null>(null)
  /** The load failure as thrown — phrased at render, in the current language. */
  const [loadError, setLoadError] = useState<unknown>(null)
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
      .catch((e) => setLoadError(e ?? new Error('load failed')))
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
      setMsg({ tone: 'ok', text: t('channels.advanced.saved') })
    } catch (e) {
      setMsg({ tone: 'error', text: errText(e, t) })
    } finally {
      setBusy(false)
    }
  }

  const test = async () => {
    setBusy(true)
    setMsg(null)
    try {
      await api('messaging/settings/test', { method: 'POST', body: JSON.stringify({ to: testTo.trim() }) })
      setMsg({ tone: 'ok', text: t('channels.drawer.testEmailSent', { to: testTo.trim() }) })
    } catch (e) {
      setMsg({ tone: e instanceof ApiError && e.status === 501 ? 'info' : 'error', text: errText(e, t) })
    } finally {
      setBusy(false)
    }
  }

  if (loadError)
    return (
      <p className="text-[12.5px] text-text-muted">
        {t('channels.advanced.unavailable', { error: errText(loadError, t) })}
      </p>
    )
  if (!data)
    return (
      <p className="flex items-center gap-2 text-[12.5px] text-text-muted">
        <Loader2 size={13} className="animate-spin" aria-hidden /> {t('common.status.loading')}
      </p>
    )

  const ro = !isSuper
  return (
    <div className="flex flex-col gap-4">
      <p className="text-[12.5px] leading-relaxed text-text-mid">
        <Trans
          k={ro ? 'channels.advanced.introReadOnly' : 'channels.advanced.intro'}
          values={{ env: <code className="rounded bg-fill px-1">.env</code> }}
        />{' '}
        <span
          className="ml-1 rounded-full px-2 py-px text-[11px] font-semibold"
          style={data.configured ? { color: 'var(--green-ink)' } : { color: 'var(--text-muted)' }}
        >
          {data.configured ? t('channels.advanced.configured') : t('channels.advanced.sandbox')}
        </span>
      </p>
      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">
        <Field
          label={t('channels.setup.labels.smtpHost')}
          value={f.smtp_host}
          onChange={set('smtp_host')}
          placeholder="smtp.gmail.com"
          disabled={ro}
        />
        <Field
          label={t('channels.setup.labels.port')}
          value={f.smtp_port}
          onChange={set('smtp_port')}
          placeholder="587"
          hint={t('channels.advanced.portHint')}
          disabled={ro}
        />
        <Field
          label={t('channels.setup.labels.fromAddress')}
          value={f.smtp_from}
          onChange={set('smtp_from')}
          placeholder="support@yourco.com"
          disabled={ro}
        />
        <Field
          label={t('channels.setup.labels.username')}
          value={f.smtp_username}
          onChange={set('smtp_username')}
          placeholder={t('channels.advanced.usernamePlaceholder')}
          disabled={ro}
        />
        <Field
          label={t('channels.setup.labels.password')}
          value={f.smtp_password}
          onChange={set('smtp_password')}
          secret
          placeholder={t('channels.setup.placeholders.appPassword')}
          disabled={ro}
        />
        <Field
          label={t('channels.advanced.subject')}
          value={f.email_subject}
          onChange={set('email_subject')}
          placeholder={t('channels.advanced.subjectPlaceholder')}
          disabled={ro}
        />
        <Field
          label={t('channels.advanced.imapHost')}
          value={f.imap_host}
          onChange={set('imap_host')}
          placeholder="imap.gmail.com"
          hint={t('channels.advanced.imapHint')}
          disabled={ro}
        />
        <Field
          label={t('channels.setup.labels.imapPort')}
          value={f.imap_port}
          onChange={set('imap_port')}
          placeholder="993"
          disabled={ro}
        />
        <div className="flex items-center justify-between gap-3 self-start rounded-lg border border-line px-3 py-2.5 sm:mt-5">
          <span className="text-[12.5px] font-semibold text-text-high">{t('channels.drawer.useTls')}</span>
          <Toggle on={tls} onChange={setTls} label={t('channels.drawer.useTls')} disabled={ro} />
        </div>
      </div>
      <div className="flex flex-wrap items-center gap-2">
        {isSuper && (
          <Button onClick={save} disabled={busy}>
            {busy && <Loader2 size={13} className="animate-spin" aria-hidden />} {t('channels.advanced.save')}
          </Button>
        )}
        <input
          className={`${inputCls} !w-[230px] max-w-full`}
          value={testTo}
          onChange={(e) => setTestTo(e.target.value)}
          placeholder="you@company.com"
          aria-label={t('channels.advanced.testRecipient')}
        />
        <Button variant="secondary" onClick={test} disabled={busy || !data.configured || !testTo.includes('@')}>
          <Plug size={13} aria-hidden /> {t('channels.advanced.sendTest')}
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
  const t = useT()
  const [open, setOpen] = useState(false)
  return (
    <section data-tour="channels-advanced" className="rounded-xl border border-line bg-card">
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
        <span className="display text-[18px] leading-tight text-text-heading">{t('channels.advanced.title')}</span>
        <span className="text-[12.5px] text-text-muted">{t('channels.advanced.subtitle')}</span>
      </button>
      {open && (
        <div className="flex flex-col gap-7 border-t border-line px-4 py-5 sm:px-5">
          <div className="flex flex-col gap-3">
            <h3 className="eyebrow">{t('channels.advanced.metaWebhook')}</h3>
            <p className="text-[12.5px] leading-relaxed text-text-mid">
              <Trans
                k="channels.advanced.metaIntro"
                values={{ field: <code className="rounded bg-fill px-1">messages</code> }}
              />
            </p>
            <div className="grid grid-cols-1 gap-2 lg:grid-cols-2">
              <MetaWebhookRows verifyToken={verifyToken} />
              <CopyRow label={t('channels.advanced.socialCallback')} value={`${API_ORIGIN}/api/messaging/webhook`} />
            </div>
          </div>
          <div className="flex flex-col gap-3">
            <h3 className="eyebrow">{t('channels.advanced.mailTitle')}</h3>
            <EmailFallbackForm isSuper={isSuper} />
          </div>
        </div>
      )}
    </section>
  )
}
