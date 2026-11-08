'use client'

import { useCallback, useEffect, useState } from 'react'
import { AlertCircle, Check, Loader2, Plug, ShieldCheck, X } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Logo } from '@/components/shell/Logo'
import { Segmented } from '@/components/chats/Segmented'
import { API } from '@/lib/apiBase'
import { authHeaders, expireSessionOn401 } from '@/lib/auth'
import { useT } from '@/i18n'

/**
 * The Allow page — how an AI app gets access to this ChatBranch without anyone
 * copying a key.
 *
 * The app opens `/api/oauth/authorize`, which stores the request and sends the
 * browser here. The person is already signed in (the Shell sent them through
 * `/login?next=` if not), so all that is left is the one decision: what the app is
 * allowed to do, and Allow or Deny. Whatever they choose, the answer is a redirect
 * back to the app — this page never shows a token to anyone.
 *
 * Rendered bare by the Shell: no nav, nothing to wander into while being asked to
 * grant access.
 */

interface ConnectRequest {
  request: string
  client_name: string
  client_uri: string
  redirect_host: string
  scope: string
  read_only: boolean
  user: string
  expires_at: string
}

type Access = 'full' | 'read'

async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}/oauth/${path}`, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
  })
  const data = await res.json().catch(() => ({}))
  if (expireSessionOn401(res)) throw new Error('Session expired')
  if (!res.ok) throw new Error((data as { detail?: string }).detail || `HTTP ${res.status}`)
  return data as T
}

export default function ConnectPage() {
  const t = useT()
  const [request, setRequest] = useState<ConnectRequest | null>(null)
  const [access, setAccess] = useState<Access>('full')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const [done, setDone] = useState<'allowed' | 'denied' | null>(null)

  const id = typeof window === 'undefined'
    ? ''
    : new URLSearchParams(window.location.search).get('request') ?? ''

  const load = useCallback(async () => {
    if (!id) {
      setError(t('auth.connect.noRequest'))
      return
    }
    try {
      const r = await api<ConnectRequest>(id)
      setRequest(r)
      // the app may have asked for read-only itself; the person can still change it
      setAccess(r.read_only ? 'read' : 'full')
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }, [id, t])

  useEffect(() => {
    void load()
  }, [load])

  const decide = async (allow: boolean) => {
    setBusy(true)
    setError('')
    try {
      const r = await api<{ redirect: string; allowed: boolean }>(id, {
        method: 'POST',
        body: JSON.stringify({ allow, read_only: access === 'read' }),
      })
      setDone(r.allowed ? 'allowed' : 'denied')
      // back to the app, which takes it from here
      window.location.href = r.redirect
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
      setBusy(false)
    }
  }

  return (
    <div className="flex min-h-full items-center justify-center bg-app px-4 py-10">
      <div className="w-full max-w-[420px]">
        <div className="mb-6 flex justify-center">
          <Logo />
        </div>

        <div className="rounded-2xl border border-line bg-card p-5 shadow-pop sm:p-6">
          {done ? (
            <div className="flex flex-col items-center gap-2 py-6 text-center">
              <Loader2 size={20} className="animate-spin text-text-faint" aria-hidden />
              <p className="text-[13px] text-text-muted">{t('auth.connect.returning')}</p>
            </div>
          ) : error && !request ? (
            <div className="flex flex-col items-center gap-3 py-4 text-center">
              <AlertCircle size={28} style={{ color: 'var(--red-ink)' }} aria-hidden />
              <p className="text-[13px] font-semibold text-text-high">{t('auth.connect.cannotConnect')}</p>
              <p className="text-[12.5px] text-text-muted">{error}</p>
            </div>
          ) : !request ? (
            <div className="flex items-center justify-center gap-2 py-10 text-[12.5px] text-text-muted">
              <Loader2 size={14} className="animate-spin" aria-hidden /> {t('common.status.loading')}
            </div>
          ) : (
            <>
              <div className="flex flex-col items-center gap-2 text-center">
                <span className="grid h-11 w-11 place-items-center rounded-xl bg-fill text-text-high">
                  <Plug size={20} aria-hidden />
                </span>
                <h1 className="display text-[19px] text-text-heading">
                  {t('auth.connect.title', { app: request.client_name })}
                </h1>
                <p className="text-[12.5px] leading-relaxed text-text-muted">
                  {t('auth.connect.blurb', { app: request.client_name, user: request.user })}
                </p>
              </div>

              <div className="mt-5">
                <Segmented<Access>
                  value={access}
                  onChange={setAccess}
                  label={t('auth.connect.accessLabel')}
                  options={[
                    { value: 'full', label: t('auth.connect.accessFull') },
                    { value: 'read', label: t('auth.connect.accessRead') },
                  ]}
                />
                <p className="mt-2 text-[12px] leading-relaxed text-text-muted">
                  {access === 'read' ? t('auth.connect.accessReadHint') : t('auth.connect.accessFullHint')}
                </p>
              </div>

              <ul className="mt-4 space-y-1.5 border-t border-line pt-4 text-[12px] text-text-muted">
                <li className="flex items-start gap-2">
                  <ShieldCheck size={13} className="mt-0.5 shrink-0 text-text-faint" aria-hidden />
                  {t('auth.connect.factPermissions', { user: request.user })}
                </li>
                <li className="flex items-start gap-2">
                  <ShieldCheck size={13} className="mt-0.5 shrink-0 text-text-faint" aria-hidden />
                  {t('auth.connect.factRevoke')}
                </li>
                <li className="flex items-start gap-2">
                  <ShieldCheck size={13} className="mt-0.5 shrink-0 text-text-faint" aria-hidden />
                  {t('auth.connect.factRedirect', { host: request.redirect_host })}
                </li>
              </ul>

              {error && (
                <div
                  role="alert"
                  className="mt-4 flex items-start gap-2 rounded-lg border border-red/25 bg-red/10 px-3 py-2 text-[12.5px] text-[color:var(--red-ink)]"
                >
                  <AlertCircle size={14} className="mt-0.5 shrink-0" aria-hidden /> {error}
                </div>
              )}

              <div className="mt-5 flex gap-2">
                <Button variant="secondary" className="flex-1" disabled={busy} onClick={() => void decide(false)}>
                  <X size={14} aria-hidden /> {t('auth.connect.deny')}
                </Button>
                <Button className="flex-1" disabled={busy} onClick={() => void decide(true)}>
                  {busy ? <Loader2 size={14} className="animate-spin" aria-hidden /> : <Check size={14} aria-hidden />}
                  {t('auth.connect.allow')}
                </Button>
              </div>
            </>
          )}
        </div>

        {request && !done && (
          <p className="mt-4 text-center text-[11.5px] text-text-faint">{t('auth.connect.footer')}</p>
        )}
      </div>
    </div>
  )
}
