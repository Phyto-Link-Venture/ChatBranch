'use client'

import { API } from '@/lib/apiBase'
import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { AlertCircle, Check, Eye, EyeOff, Loader2, LogOut } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Card, CardTitle } from '@/components/ui/Card'
import { expireSessionOn401, roleDisplayName, setCachedName } from '@/lib/auth'
import { roleHeaders } from '@/lib/role'
import { passwordProblem } from '@/lib/security'
import { useFormat, useT, type MessageKey } from '@/i18n'
import { TwoFactorCard } from './TwoFactorCard'

/** The server's floor (MyAccountEndpoints.MinPasswordLength — the same as creating a user). */
const MIN_PASSWORD = 8

interface Account {
  username: string
  first_name: string
  last_name: string
  name: string
  email: string
  role: string
  entities: { id: number; slug: string; name: string }[]
  last_login: string | null
  active_sessions: number
  auth_source: 'session' | 'api'
  /** The floor the next password must meet (the server's password policy). */
  password_min_length?: number
  /** strict = a real install (≥ 8 characters, not the username, not a common password); demo = 8 characters only. */
  password_policy?: 'strict' | 'demo'
}

class ApiError extends Error {
  constructor(
    message: string,
    public status: number,
    public field?: string,
    public code?: string,
    public min?: number,
  ) {
    super(message)
  }
}

async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}/auth/me/${path}`, {
    headers: { 'Content-Type': 'application/json', ...roleHeaders() },
    ...init,
  })
  if (expireSessionOn401(res)) throw new ApiError('Session expired — redirecting to login…', 401)
  const data = (await res.json().catch(() => ({}))) as { detail?: string; field?: string; code?: string; min?: number }
  if (!res.ok) throw new ApiError(data.detail || `HTTP ${res.status}`, res.status, data.field, data.code, data.min)
  return data as T
}

const inputCls =
  'w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[13px] text-text-primary outline-none transition-colors placeholder:text-text-faint focus:border-text-primary disabled:opacity-60'
const errBorder = '!border-[color:var(--red-ink)]'

const looksLikeEmail = (s: string) => /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(s)

type Strength = 'weak' | 'fair' | 'good' | 'strong'

/** A rough strength hint: length plus character variety, minus obvious repeats. */
function strengthOf(pw: string): Strength {
  let score = 0
  if (pw.length >= MIN_PASSWORD) score++
  if (pw.length >= 12) score++
  if (pw.length >= 16) score++
  const kinds = [/[a-z]/, /[A-Z]/, /\d/, /[^A-Za-z0-9]/].filter((r) => r.test(pw)).length
  if (kinds >= 3) score++
  if (/^(.)\1+$/.test(pw) || new Set(pw).size < 4) score = Math.min(score, 1)
  return score <= 1 ? 'weak' : score === 2 ? 'fair' : score === 3 ? 'good' : 'strong'
}

const STRENGTH_BAR: Record<Strength, { width: string; cls: string }> = {
  weak: { width: '25%', cls: 'bg-red' },
  fair: { width: '50%', cls: 'bg-amber' },
  good: { width: '75%', cls: 'bg-blue' },
  strong: { width: '100%', cls: 'bg-green' },
}

function Alert({ kind, children }: { kind: 'ok' | 'error'; children: string }) {
  return kind === 'ok' ? (
    <div role="status" className="flex items-start gap-2 rounded-lg border border-green/25 bg-green/10 px-3 py-2 text-[12.5px] text-[color:var(--green-ink)]">
      <Check size={14} className="mt-0.5 shrink-0" aria-hidden /> {children}
    </div>
  ) : (
    <div role="alert" className="flex items-start gap-2 rounded-lg border border-red/25 bg-red/10 px-3 py-2 text-[12.5px] text-[color:var(--red-ink)]">
      <AlertCircle size={14} className="mt-0.5 shrink-0" aria-hidden /> {children}
    </div>
  )
}

function Field({
  label,
  hint,
  error,
  children,
}: {
  label: string
  hint?: string
  error?: string
  children: React.ReactNode
}) {
  return (
    <label className="block">
      <span className="mb-1 block text-[11.5px] font-bold text-text-high">{label}</span>
      {children}
      {error ? (
        <span className="mt-1 block text-[11.5px] text-[color:var(--red-ink)]">{error}</span>
      ) : (
        hint && <span className="mt-1 block text-[11px] leading-relaxed text-text-muted">{hint}</span>
      )}
    </label>
  )
}

function PasswordInput({
  value,
  onChange,
  autoComplete,
  invalid,
  shown,
  onToggle,
}: {
  value: string
  onChange: (v: string) => void
  autoComplete: string
  invalid?: boolean
  shown: boolean
  onToggle: () => void
}) {
  const t = useT()
  return (
    <span className="relative block">
      <input
        type={shown ? 'text' : 'password'}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        autoComplete={autoComplete}
        aria-invalid={invalid || undefined}
        className={`${inputCls} pr-10 ${invalid ? errBorder : ''}`}
      />
      <button
        type="button"
        onClick={onToggle}
        aria-label={shown ? t('account.password.hide') : t('account.password.show')}
        title={shown ? t('account.password.hide') : t('account.password.show')}
        className="absolute inset-y-0 right-0 grid w-9 cursor-pointer place-items-center text-text-muted hover:text-text-high"
      >
        {shown ? <EyeOff size={15} aria-hidden /> : <Eye size={15} aria-hidden />}
      </button>
    </span>
  )
}

/**
 * Settings → Personal → My account: the caller's OWN name, email, password and
 * signed-in devices (/api/auth/me/*). No capability — everyone has an account;
 * admins manage other people on /users. The username never changes. Each card
 * keeps its own errors inside it.
 */
export function MyAccountSettings() {
  const t = useT()
  const f = useFormat()
  const [account, setAccount] = useState<Account | null>(null)
  const [loadError, setLoadError] = useState('')

  // profile
  const [first, setFirst] = useState('')
  const [last, setLast] = useState('')
  const [email, setEmail] = useState('')
  const [profileBusy, setProfileBusy] = useState(false)
  const [profileMsg, setProfileMsg] = useState<{ kind: 'ok' | 'error'; text: string; field?: string } | null>(null)

  // password
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const [confirm, setConfirm] = useState('')
  const [shown, setShown] = useState(false)
  const [pwBusy, setPwBusy] = useState(false)
  const [pwErrors, setPwErrors] = useState<{ current?: string; next?: string; confirm?: string; form?: string }>({})
  const [pwDone, setPwDone] = useState('')

  // sessions
  const [sessBusy, setSessBusy] = useState(false)
  const [sessMsg, setSessMsg] = useState<{ kind: 'ok' | 'error'; text: string } | null>(null)

  const adopt = useCallback((a: Account) => {
    setAccount(a)
    setFirst(a.first_name)
    setLast(a.last_name)
    setEmail(a.email)
  }, [])

  const load = useCallback(async () => {
    try {
      adopt(await api<Account>('account'))
    } catch (e) {
      setLoadError(e instanceof Error && e.message ? e.message : t('account.loadFailed'))
    }
  }, [adopt, t])

  useEffect(() => {
    void load()
  }, [load])

  if (!account) {
    return loadError ? (
      <Alert kind="error">{loadError}</Alert>
    ) : (
      <div className="flex items-center gap-2 py-6 text-[12.5px] text-text-muted">
        <Loader2 size={14} className="animate-spin" aria-hidden /> {t('common.status.loading')}
      </div>
    )
  }

  const viaKey = account.auth_source === 'api'
  const minPassword = account.password_min_length ?? MIN_PASSWORD
  const profileDirty =
    first.trim() !== account.first_name || last.trim() !== account.last_name || email.trim() !== account.email

  const saveProfile = async (e: FormEvent) => {
    e.preventDefault()
    setProfileMsg(null)
    const mail = email.trim()
    if (mail && !looksLikeEmail(mail)) {
      setProfileMsg({ kind: 'error', text: t('account.profile.invalidEmail'), field: 'email' })
      return
    }
    setProfileBusy(true)
    try {
      const a = await api<Account>('account', {
        method: 'PATCH',
        body: JSON.stringify({ first_name: first.trim(), last_name: last.trim(), email: mail }),
      })
      adopt(a)
      setCachedName(a.name)
      setProfileMsg({ kind: 'ok', text: t('account.profile.saved') })
    } catch (err) {
      const e2 = err as ApiError
      const text =
        e2.status === 409 ? t('account.profile.emailTaken')
        : e2.field === 'email' && e2.status === 400 ? t('account.profile.invalidEmail')
        : e2.message
      setProfileMsg({ kind: 'error', text, field: e2.field })
    } finally {
      setProfileBusy(false)
    }
  }

  const changePassword = async (e: FormEvent) => {
    e.preventDefault()
    setPwDone('')
    const errs: typeof pwErrors = {}
    if (!current) errs.current = t('account.password.required')
    if (next.length < minPassword) errs.next = t('account.password.tooShort', { min: minPassword })
    else if (next === current) errs.next = t('account.password.same')
    if (!errs.next && confirm !== next) errs.confirm = t('account.password.mismatch')
    setPwErrors(errs)
    if (Object.keys(errs).length) return
    setPwBusy(true)
    try {
      const r = await api<{ signed_out: number }>('password', {
        method: 'POST',
        body: JSON.stringify({ current_password: current, new_password: next }),
      })
      setCurrent('')
      setNext('')
      setConfirm('')
      setShown(false)
      setPwDone(t('account.password.changed', { count: r.signed_out }))
      setAccount((a) => (a ? { ...a, active_sessions: Math.max(1, a.active_sessions - r.signed_out) } : a))
    } catch (err) {
      const e2 = err as ApiError
      if (e2.status === 429) setPwErrors({ form: t('account.password.tooMany') })
      else if (e2.field === 'current_password') setPwErrors({ current: t('account.password.wrong') })
      else if (e2.field === 'new_password') setPwErrors({ next: passwordProblem(t, e2.code, e2.min) ?? e2.message })
      else setPwErrors({ form: e2.message })
    } finally {
      setPwBusy(false)
    }
  }

  const signOutOthers = async () => {
    setSessMsg(null)
    setSessBusy(true)
    try {
      const r = await api<{ signed_out: number; active_sessions: number }>('sessions/revoke-others', { method: 'POST' })
      setAccount((a) => (a ? { ...a, active_sessions: r.active_sessions } : a))
      setSessMsg({
        kind: 'ok',
        text: r.signed_out > 0 ? t('account.sessions.signedOut', { count: r.signed_out }) : t('account.sessions.none'),
      })
    } catch (err) {
      setSessMsg({ kind: 'error', text: err instanceof Error ? err.message : String(err) })
    } finally {
      setSessBusy(false)
    }
  }

  const strength = next ? strengthOf(next) : null

  return (
    <div className="flex flex-col gap-5">
      {viaKey && <Alert kind="error">{t('account.apiKeyNote')}</Alert>}

      {/* ---- profile ---- */}
      <Card className="p-4 sm:p-5" data-tour="account-profile">
        <CardTitle right={<span className="text-text-muted">{t('account.profile.onlyYou')}</span>}>
          {t('account.profile.title')}
        </CardTitle>
        <p className="mt-1 text-[12.5px] text-text-muted">{t('account.profile.intro')}</p>
        <form onSubmit={(e) => void saveProfile(e)} className="mt-4 flex flex-col gap-3">
          {profileMsg && <Alert kind={profileMsg.kind}>{profileMsg.text}</Alert>}
          <div className="grid gap-3 sm:grid-cols-2">
            <Field label={t('account.profile.firstName')}>
              <input
                value={first}
                onChange={(e) => setFirst(e.target.value)}
                maxLength={150}
                autoComplete="given-name"
                disabled={viaKey}
                className={inputCls}
              />
            </Field>
            <Field label={t('account.profile.lastName')}>
              <input
                value={last}
                onChange={(e) => setLast(e.target.value)}
                maxLength={150}
                autoComplete="family-name"
                disabled={viaKey}
                className={inputCls}
              />
            </Field>
          </div>
          <Field label={t('account.profile.email')}>
            <input
              type="email"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              placeholder={t('account.profile.emailPlaceholder')}
              autoComplete="email"
              maxLength={254}
              disabled={viaKey}
              aria-invalid={profileMsg?.field === 'email' || undefined}
              className={`${inputCls} ${profileMsg?.kind === 'error' && profileMsg.field === 'email' ? errBorder : ''}`}
            />
          </Field>
          <dl className="grid gap-x-6 gap-y-2 rounded-lg bg-fill-subtle px-3 py-2.5 text-[12.5px] sm:grid-cols-2">
            <Info label={t('account.profile.username')} hint={t('account.profile.usernameHint')}>
              <span className="font-mono">{account.username}</span>
            </Info>
            <Info label={t('account.profile.role')}>{roleDisplayName(t, account.role)}</Info>
            <Info label={t('account.profile.entities')}>
              {account.entities.length === 0 || ['admin', 'superadmin'].includes(account.role)
                ? t('account.profile.allEntities')
                : account.entities.map((e) => e.name || e.slug).join(', ')}
            </Info>
            <Info label={t('account.profile.lastLogin')}>
              {account.last_login ? f.dateTime(account.last_login) : t('account.profile.never')}
            </Info>
          </dl>
          <div className="flex justify-end">
            <Button type="submit" size="sm" disabled={viaKey || profileBusy || !profileDirty}>
              {profileBusy && <Loader2 size={13} className="animate-spin" aria-hidden />}
              {t('account.profile.save')}
            </Button>
          </div>
        </form>
      </Card>

      {/* ---- password ---- */}
      <Card className="p-4 sm:p-5" data-tour="account-password">
        <CardTitle>{t('account.password.title')}</CardTitle>
        <p className="mt-1 text-[12.5px] text-text-muted">{t('account.password.intro')}</p>
        <form onSubmit={(e) => void changePassword(e)} className="mt-4 flex flex-col gap-3">
          {pwDone && <Alert kind="ok">{pwDone}</Alert>}
          {pwErrors.form && <Alert kind="error">{pwErrors.form}</Alert>}
          {/* a hidden username lets password managers file the new password correctly */}
          <input type="text" name="username" value={account.username} autoComplete="username" readOnly hidden />
          <Field label={t('account.password.current')} error={pwErrors.current}>
            <PasswordInput
              value={current}
              onChange={setCurrent}
              autoComplete="current-password"
              invalid={!!pwErrors.current}
              shown={shown}
              onToggle={() => setShown((s) => !s)}
            />
          </Field>
          <div className="grid gap-3 sm:grid-cols-2">
            <Field label={t('account.password.new')} error={pwErrors.next}>
              <PasswordInput
                value={next}
                onChange={setNext}
                autoComplete="new-password"
                invalid={!!pwErrors.next}
                shown={shown}
                onToggle={() => setShown((s) => !s)}
              />
            </Field>
            <Field label={t('account.password.confirm')} error={pwErrors.confirm}>
              <PasswordInput
                value={confirm}
                onChange={setConfirm}
                autoComplete="new-password"
                invalid={!!pwErrors.confirm}
                shown={shown}
                onToggle={() => setShown((s) => !s)}
              />
            </Field>
          </div>
          <div aria-live="polite">
            {strength && (
              <div className="flex items-center gap-2">
                <span className="h-1.5 w-28 overflow-hidden rounded-full bg-fill">
                  <span
                    className={`block h-full rounded-full ${STRENGTH_BAR[strength].cls}`}
                    style={{ width: STRENGTH_BAR[strength].width }}
                  />
                </span>
                <span className="text-[11.5px] text-text-mid">
                  {t('account.password.strengthLabel', {
                    level: t(`account.password.strength.${strength}` as MessageKey),
                  })}
                </span>
              </div>
            )}
            <p className="mt-1 text-[11px] leading-relaxed text-text-muted">
              {account.password_policy === 'strict'
                ? t('account.password.policyHint', { min: minPassword })
                : t('account.password.hint', { min: minPassword })}
            </p>
          </div>
          <div className="flex justify-end">
            <Button type="submit" size="sm" disabled={viaKey || pwBusy || !current || !next || !confirm}>
              {pwBusy && <Loader2 size={13} className="animate-spin" aria-hidden />}
              {t('account.password.submit')}
            </Button>
          </div>
        </form>
      </Card>

      {/* ---- two-step sign-in ---- */}
      <TwoFactorCard disabled={viaKey} />

      {/* ---- sessions ---- */}
      <Card className="p-4 sm:p-5" data-tour="account-sessions">
        <CardTitle>{t('account.sessions.title')}</CardTitle>
        <p className="mt-1 text-[12.5px] text-text-muted">{t('account.sessions.intro')}</p>
        <div className="mt-3 flex flex-col gap-3">
          {sessMsg && <Alert kind={sessMsg.kind}>{sessMsg.text}</Alert>}
          <div className="flex flex-wrap items-center justify-between gap-3">
            <span className="text-[13px] text-text-high">
              {t('account.sessions.count', { count: Math.max(1, account.active_sessions) })}
            </span>
            <Button
              variant="secondary"
              size="sm"
              disabled={viaKey || sessBusy || account.active_sessions <= 1}
              onClick={() => void signOutOthers()}
            >
              {sessBusy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <LogOut size={13} aria-hidden />}
              {t('account.sessions.signOutOthers')}
            </Button>
          </div>
        </div>
      </Card>
    </div>
  )
}

function Info({ label, hint, children }: { label: string; hint?: string; children: React.ReactNode }) {
  return (
    <div className="min-w-0">
      <dt className="text-[11px] font-bold text-text-muted">{label}</dt>
      <dd className="truncate text-text-high" title={hint}>
        {children}
      </dd>
    </div>
  )
}
