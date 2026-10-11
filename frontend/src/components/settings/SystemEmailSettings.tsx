'use client'

import Link from 'next/link'
import { useEffect, useMemo, useState } from 'react'
import { AlertCircle, Check, CircleAlert, Eye, EyeOff, Loader2, MailCheck, Send, Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Card, CardTitle } from '@/components/ui/Card'
import { Select } from '@/components/ui/Select'
import { Advanced } from '@/components/ui/Advanced'
import { useAccess } from '@/lib/useAccess'
import { SecurityError } from '@/lib/security'
import { systemEmail, type SecurityMode, type SystemEmailSettings as Settings } from '@/lib/systemEmail'
import { Trans, useT, type MessageKey } from '@/i18n'

const inputCls =
  'h-9 w-full rounded-lg border border-line-strong bg-card px-3 text-[13px] text-text-primary outline-none transition-colors placeholder:text-text-faint focus:border-text-primary disabled:opacity-60'

const MODE_LABEL: Record<SecurityMode, MessageKey> = {
  starttls: 'settings.systemEmail.securityModes.starttls',
  ssl: 'settings.systemEmail.securityModes.ssl',
  none: 'settings.systemEmail.securityModes.none',
}

/** The fields a field-level refusal can name (SystemEmailEndpoints). */
const INVALID: Record<string, MessageKey> = {
  host: 'settings.systemEmail.invalid.host',
  from_email: 'settings.systemEmail.invalid.from_email',
  port: 'settings.systemEmail.invalid.port',
  security: 'settings.systemEmail.invalid.security',
  username: 'settings.systemEmail.invalid.username',
}

interface Draft {
  from_name: string
  from_email: string
  host: string
  port: string
  security: SecurityMode
  username: string
  password: string
}

const draftOf = (s: Settings): Draft => ({
  from_name: s.from_name,
  from_email: s.from_email,
  host: s.host,
  port: String(s.port || 587),
  security: s.security,
  username: s.username,
  password: s.password_masked,
})

const sameDraft = (a: Draft, b: Draft) => (Object.keys(a) as (keyof Draft)[]).every((k) => a[k] === b[k])

/**
 * Settings → Organization → System email (app_keys.manage — superadmin by default,
 * organization-wide): the SMTP mailbox welcome emails and password links are sent
 * from. Shows what sign-in emails use RIGHT NOW (this mailbox, the default mail
 * settings, or nothing) and the login address the links are built on; the
 * password round-trips masked; "Send test email" goes to the caller's own address
 * through what is saved.
 */
export function SystemEmailSettings() {
  const t = useT()
  const { ready, can } = useAccess()
  const [data, setData] = useState<Settings | null>(null)
  const [draft, setDraft] = useState<Draft | null>(null)
  const [showPassword, setShowPassword] = useState(false)
  const [busy, setBusy] = useState<'save' | 'test' | 'remove' | null>(null)
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)
  const [fieldError, setFieldError] = useState<string | null>(null)
  const [loadError, setLoadError] = useState('')

  const seed = (s: Settings) => {
    setData(s)
    setDraft(draftOf(s))
    setShowPassword(false)
  }

  useEffect(() => {
    systemEmail
      .get()
      .then(seed)
      .catch(() => setLoadError(t('settings.systemEmail.loadFailed')))
  }, [t])

  const dirty = useMemo(() => !!data && !!draft && !sameDraft(draft, draftOf(data)), [data, draft])
  const canEdit = ready && can('app_keys.manage')

  const set = (patch: Partial<Draft>) => {
    setDraft((d) => (d ? { ...d, ...patch } : d))
    setFieldError(null)
  }

  const explain = (e: unknown): string => {
    if (e instanceof SecurityError) {
      if (e.field && INVALID[e.field]) return t(INVALID[e.field])
      if (e.code === 'email_not_configured') return t('settings.systemEmail.notConfigured')
    }
    return e instanceof Error ? e.message : String(e)
  }

  const save = async () => {
    if (!draft) return
    setBusy('save')
    setMsg(null)
    try {
      const next = await systemEmail.save({
        ...draft,
        port: Number(draft.port) || 587,
      })
      seed(next)
      setMsg({ ok: true, text: t('settings.systemEmail.saved') })
    } catch (e) {
      if (e instanceof SecurityError && e.field) setFieldError(e.field)
      setMsg({ ok: false, text: explain(e) })
    } finally {
      setBusy(null)
    }
  }

  const remove = async () => {
    if (!window.confirm(t('settings.systemEmail.removeConfirm'))) return
    setBusy('remove')
    setMsg(null)
    try {
      seed(
        await systemEmail.save({
          from_name: '',
          from_email: '',
          host: '',
          port: 587,
          security: 'starttls',
          username: '',
          password: '',
        }),
      )
      setMsg({ ok: true, text: t('settings.systemEmail.removed') })
    } catch (e) {
      setMsg({ ok: false, text: explain(e) })
    } finally {
      setBusy(null)
    }
  }

  const test = async () => {
    setBusy('test')
    setMsg(null)
    try {
      const r = await systemEmail.test()
      setMsg({ ok: true, text: t('settings.systemEmail.testSent', { email: r.to }) })
    } catch (e) {
      setMsg({ ok: false, text: t('settings.systemEmail.testFailed', { error: explain(e) }) })
    } finally {
      setBusy(null)
    }
  }

  if (loadError) return <p className="py-6 text-[12.5px] text-text-muted">{loadError}</p>
  if (!data || !draft) {
    return (
      <div className="flex items-center gap-2 py-6 text-[12.5px] text-text-muted">
        <Loader2 size={14} className="animate-spin" aria-hidden /> {t('common.status.loading')}
      </div>
    )
  }

  const inv = data.invites
  const sourceText =
    data.source === 'system'
      ? t('settings.systemEmail.sourceSystem', { from: data.from_email })
      : data.source === 'default'
        ? t('settings.systemEmail.sourceDefault', { from: data.default_mail.from })
        : t('settings.systemEmail.sourceNone')
  const loginProblem =
    inv.problem === 'no_login_address'
      ? t('settings.systemEmail.loginMissing')
      : inv.problem === 'localhost_login_address'
        ? t('settings.systemEmail.loginLocal')
        : null
  const advancedChanged = draft.security !== 'starttls' || draft.port !== '587'
  const fieldCls = (key: string) => `${inputCls} ${fieldError === key ? '!border-[color:var(--red-ink)]' : ''}`
  const canTest = data.source !== 'none' && !!data.my_email && !dirty

  return (
    <div className="flex flex-col gap-5">
      {/* where sign-in emails come from right now */}
      <Card className="p-4 sm:p-5" data-tour="system-email-status">
        <CardTitle>{t('settings.systemEmail.statusTitle')}</CardTitle>
        <ul className="mt-3 flex flex-col gap-2 text-[12.5px] text-text-high">
          <li className="flex items-start gap-2">
            {data.source === 'none' ? (
              <CircleAlert size={14} className="mt-0.5 shrink-0 text-[color:var(--amber-ink)]" aria-hidden />
            ) : (
              <MailCheck size={14} className="mt-0.5 shrink-0 text-[color:var(--green-ink)]" aria-hidden />
            )}
            <span>{sourceText}</span>
          </li>
          <li className="flex items-start gap-2">
            {loginProblem ? (
              <CircleAlert size={14} className="mt-0.5 shrink-0 text-[color:var(--amber-ink)]" aria-hidden />
            ) : (
              <Check size={14} className="mt-0.5 shrink-0 text-[color:var(--green-ink)]" aria-hidden />
            )}
            <span>{loginProblem ?? t('settings.systemEmail.loginOk', { url: inv.login_url ?? '' })}</span>
          </li>
        </ul>
        <p className="mt-3 text-[12px] text-text-muted">{t('settings.systemEmail.linkHours', { hours: inv.link_hours })}</p>
      </Card>

      {/* the mailbox */}
      <Card className="p-4 sm:p-5" data-tour="system-email-form">
        <CardTitle>{t('settings.systemEmail.formTitle')}</CardTitle>
        <p className="mt-1 max-w-[640px] text-[12.5px] text-text-muted">{t('settings.systemEmail.formIntro')}</p>
        <form
          className="mt-4 flex flex-col gap-4"
          onSubmit={(e) => {
            e.preventDefault()
            void save()
          }}
        >
          <fieldset disabled={!canEdit || busy !== null} className="grid grid-cols-1 gap-3 sm:grid-cols-2">
            <label className="block">
              <span className="mb-1.5 block text-[11.5px] font-bold text-text-high">{t('settings.systemEmail.host')}</span>
              <input
                value={draft.host}
                onChange={(e) => set({ host: e.target.value })}
                placeholder={t('settings.systemEmail.hostPlaceholder')}
                autoComplete="off"
                spellCheck={false}
                className={fieldCls('host')}
              />
            </label>
            <label className="block">
              <span className="mb-1.5 block text-[11.5px] font-bold text-text-high">{t('settings.systemEmail.fromEmail')}</span>
              <input
                type="email"
                value={draft.from_email}
                onChange={(e) => set({ from_email: e.target.value })}
                placeholder={t('settings.systemEmail.fromEmailPlaceholder')}
                autoComplete="off"
                spellCheck={false}
                className={fieldCls('from_email')}
              />
            </label>
            <label className="block">
              <span className="mb-1.5 block text-[11.5px] font-bold text-text-high">{t('settings.systemEmail.username')}</span>
              <input
                value={draft.username}
                onChange={(e) => set({ username: e.target.value })}
                placeholder={t('settings.systemEmail.usernamePlaceholder')}
                autoComplete="off"
                spellCheck={false}
                className={fieldCls('username')}
              />
            </label>
            <label className="block">
              <span className="mb-1.5 block text-[11.5px] font-bold text-text-high">{t('settings.systemEmail.password')}</span>
              <span className="relative block">
                <input
                  type={showPassword ? 'text' : 'password'}
                  value={draft.password}
                  onChange={(e) => set({ password: e.target.value })}
                  onFocus={(e) => {
                    // a masked value is a placeholder for the stored secret — typing replaces it
                    if (draft.password.includes('••')) e.currentTarget.select()
                  }}
                  placeholder={t('settings.systemEmail.passwordPlaceholder')}
                  // never let the browser autofill the operator's own login here
                  autoComplete="new-password"
                  className={`${inputCls} pr-10`}
                />
                <button
                  type="button"
                  onClick={() => setShowPassword((v) => !v)}
                  aria-label={showPassword ? t('auth.login.hidePassword') : t('auth.login.showPassword')}
                  className="absolute right-1 top-1/2 grid h-7 w-7 -translate-y-1/2 cursor-pointer place-items-center rounded-md text-text-muted hover:text-text-primary"
                >
                  {showPassword ? <EyeOff size={14} aria-hidden /> : <Eye size={14} aria-hidden />}
                </button>
              </span>
              {data.has_password && draft.password.includes('••') && (
                <span className="mt-1 block text-[11px] text-text-muted">{t('settings.systemEmail.passwordKept')}</span>
              )}
            </label>
            <label className="block sm:col-span-2">
              <span className="mb-1.5 block text-[11.5px] font-bold text-text-high">{t('settings.systemEmail.fromName')}</span>
              <input
                value={draft.from_name}
                onChange={(e) => set({ from_name: e.target.value })}
                placeholder={t('settings.systemEmail.fromNamePlaceholder')}
                autoComplete="off"
                className={`${inputCls} sm:max-w-[420px]`}
              />
              <span className="mt-1 block text-[11px] text-text-muted">{t('settings.systemEmail.fromNameHint')}</span>
            </label>
          </fieldset>

          <Advanced
            hint={t('settings.systemEmail.advancedHint')}
            count={advancedChanged ? 1 : 0}
            forceOpen={fieldError === 'port' || fieldError === 'security'}
          >
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-[1fr_140px]">
              <div>
                <span className="mb-1.5 block text-[11.5px] font-bold text-text-high">{t('settings.systemEmail.security')}</span>
                <Select
                  value={draft.security}
                  onChange={(v) => {
                    const mode = v as SecurityMode
                    // follow the usual port when the operator hasn't picked their own
                    const port = draft.port === '587' || draft.port === '465' ? (mode === 'ssl' ? '465' : '587') : draft.port
                    set({ security: mode, port })
                  }}
                  disabled={!canEdit || busy !== null}
                  size="md"
                  ariaLabel={t('settings.systemEmail.security')}
                  options={data.security_modes.map((m) => ({ value: m, label: t(MODE_LABEL[m]) }))}
                />
              </div>
              <label className="block">
                <span className="mb-1.5 block text-[11.5px] font-bold text-text-high">{t('settings.systemEmail.port')}</span>
                <input
                  inputMode="numeric"
                  value={draft.port}
                  onChange={(e) => set({ port: e.target.value.replace(/[^0-9]/g, '').slice(0, 5) })}
                  disabled={!canEdit || busy !== null}
                  className={fieldCls('port')}
                />
              </label>
            </div>
            <p className="mt-2 text-[11px] text-text-muted">{t('settings.systemEmail.portHint')}</p>
          </Advanced>

          {msg && (
            <div
              role={msg.ok ? 'status' : 'alert'}
              className={`flex items-start gap-2 rounded-lg border px-3 py-2 text-[12.5px] ${
                msg.ok
                  ? 'border-green/25 bg-green/10 text-[color:var(--green-ink)]'
                  : 'border-red/25 bg-red/10 text-[color:var(--red-ink)]'
              }`}
            >
              {msg.ok ? <Check size={14} className="mt-0.5 shrink-0" aria-hidden /> : <AlertCircle size={14} className="mt-0.5 shrink-0" aria-hidden />}
              <span className="min-w-0 break-words">{msg.text}</span>
            </div>
          )}

          <div className="flex flex-wrap items-center justify-between gap-3 border-t border-line pt-4">
            <div className="min-w-0 text-[12px] text-text-muted">
              {dirty ? (
                t('settings.systemEmail.testSaveFirst')
              ) : data.my_email ? (
                t('settings.systemEmail.testTo', { email: data.my_email })
              ) : (
                <Trans
                  k="settings.systemEmail.testNoEmail"
                  values={{
                    link: (
                      <Link href="/settings/account" className="font-semibold text-text-high underline underline-offset-2">
                        {t('settings.systemEmail.testNoEmailLink')}
                      </Link>
                    ),
                  }}
                />
              )}
            </div>
            <div className="flex flex-wrap items-center gap-2">
              {data.configured && (
                <Button type="button" variant="ghost" size="sm" onClick={() => void remove()} disabled={!canEdit || busy !== null}>
                  {busy === 'remove' ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Trash2 size={13} aria-hidden />}
                  {t('settings.systemEmail.remove')}
                </Button>
              )}
              <Button type="button" variant="secondary" size="sm" onClick={() => void test()} disabled={!canEdit || busy !== null || !canTest}>
                {busy === 'test' ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Send size={13} aria-hidden />}
                {t('settings.systemEmail.test')}
              </Button>
              <Button type="submit" size="sm" disabled={!canEdit || busy !== null || !dirty}>
                {busy === 'save' && <Loader2 size={13} className="animate-spin" aria-hidden />}
                {t('settings.systemEmail.save')}
              </Button>
            </div>
          </div>
        </form>
      </Card>
    </div>
  )
}
