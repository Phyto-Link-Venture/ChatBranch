'use client'

/**
 * Step 3 of the "Add inbox" stepper for a "via Zernio" method.
 *
 *   POST /api/social/connect {platform, entity, purpose: "inbox" | "social"}
 *
 * The answer decides what happens next — coded defensively, the backend may be
 * older or newer than this screen:
 *   {url} (or auth_url / authUrl)      → open Zernio's hosted connect page; it sends
 *                                        the person back to /channels?zernio=connected
 *   {fields: [...]} and/or {steps: [...]} → a credential / instruction step (Telegram
 *                                        bot token, WhatsApp credentials…); the form is
 *                                        POSTed to `submit_url` / `complete_url` / `next`
 *                                        when given, else back to /api/social/connect
 *                                        with `credentials` (+ `session_id`)
 *   {status: "pending", status_url}    → poll it until connected
 *   {ok | connected | account}         → done
 * 404 / 501 / 503 / code zernio_not_configured → "Managed connections aren't enabled yet,
 * ask your admin"; a super admin (app_keys.manage) gets a link to the provider card under
 * Settings → App & provider keys. The word "Zernio" never reaches the screen here. The new inbox is bound to the outlet picked here (the entity
 * switcher's outlet by default).
 */

import { useEffect, useRef, useState } from 'react'
import Link from 'next/link'
import { AlertTriangle, Check, ExternalLink, Loader2, Share2 } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { EntityPicker } from '@/components/ui/EntityPicker'
import { useAccess } from '@/lib/useAccess'
import { useEntity } from '@/lib/EntityContext'
import { errMessage, isNotSetUp, socialApi, SocialApiError } from '@/lib/social'
import { CopyRow, inputCls } from '@/components/settings/WhatsAppSettings'
import { useT } from '@/i18n'
import type { MethodDef } from './catalog'

interface ConnectField {
  key?: string
  name?: string
  id?: string
  label?: string
  type?: string
  secret?: boolean
  required?: boolean
  optional?: boolean
  help?: string
  description?: string
  placeholder?: string
}

interface ConnectStep {
  title?: string
  label?: string
  body?: string
  text?: string
  description?: string
  url?: string
  code?: string
}

interface ConnectResponse {
  url?: string
  auth_url?: string
  authUrl?: string
  title?: string
  instructions?: string
  fields?: ConnectField[] | null
  steps?: (ConnectStep | string)[] | null
  session_id?: string
  session?: string
  state?: string
  step?: string
  submit_url?: string
  complete_url?: string
  next?: string
  status_url?: string
  poll_url?: string
  status?: string
  connected?: boolean
  ok?: boolean
  account?: unknown
  name?: string
  detail?: string
  message?: string
}

type Phase = 'intro' | 'redirect' | 'form' | 'waiting' | 'done' | 'notSetUp'

const DONE_STATUS = /^(connected|active|done|complete|completed|success|succeeded)$/i
const PENDING_STATUS = /^(pending|waiting|in_progress|processing|awaiting.*)$/i

const fieldKey = (f: ConnectField, i: number) => f.key ?? f.name ?? f.id ?? `field_${i}`
const urlOf = (r: ConnectResponse) => r.url ?? r.auth_url ?? r.authUrl ?? ''
const isDone = (r: ConnectResponse) =>
  r.connected === true || DONE_STATUS.test(r.status ?? '') || (!!r.account && !PENDING_STATUS.test(r.status ?? ''))

/** An API path the response names, relative to /api/ (absolute URLs outside the API are ignored). */
function apiPath(p?: string): string | null {
  if (!p) return null
  const s = p.trim()
  if (/^https?:\/\//i.test(s)) {
    const i = s.indexOf('/api/')
    return i >= 0 ? s.slice(i + 5) : null
  }
  return s.replace(/^\/?(api\/)?/, '')
}

export function ZernioConnectPanel({
  channelName,
  method,
  onChanged,
  onAnother,
  onClose,
}: {
  /** The channel's display name ("WhatsApp", "YouTube"…). */
  channelName: string
  method: MethodDef
  onChanged: () => void
  onAnother: () => void
  onClose: () => void
}) {
  const t = useT()
  const { can } = useAccess()
  const canManage = can('social.manage')
  const { levelEntity, selectedEntities, bound } = useEntity()
  const platform = method.zernio?.platform ?? ''
  const purpose = method.zernio?.purpose ?? 'inbox'

  const [entity, setEntity] = useState<string | null>(levelEntity?.slug ?? selectedEntities[0]?.slug ?? null)
  // follow the switcher until the person picks something here
  const touched = useRef(false)
  useEffect(() => {
    if (!touched.current) setEntity(levelEntity?.slug ?? selectedEntities[0]?.slug ?? null)
  }, [levelEntity, selectedEntities])

  const [phase, setPhase] = useState<Phase>('intro')
  const [resp, setResp] = useState<ConnectResponse | null>(null)
  const [values, setValues] = useState<Record<string, string>>({})
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({})
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const base = { platform, entity: entity ?? undefined, purpose }

  const fail = (e: unknown) => {
    if (isNotSetUp(e)) {
      setPhase('notSetUp')
      return
    }
    if (e instanceof SocialApiError && e.status === 403) setError(t('social.accounts.needsPermission'))
    else if (e instanceof SocialApiError && e.status === 400 && /platform must be one of/i.test(e.message))
      setError(t('channels.zernio.unsupported', { channel: channelName }))
    else setError(errMessage(e))
  }

  /** Act on whatever the connect endpoint answered. */
  const handle = (r: ConnectResponse) => {
    const url = urlOf(r)
    if (url) {
      setPhase('redirect')
      window.location.assign(url)
      return
    }
    if ((r.fields && r.fields.length > 0) || (r.steps && r.steps.length > 0)) {
      setResp(r)
      setValues({})
      setFieldErrors({})
      setPhase('form')
      return
    }
    if (isDone(r) || r.ok === true) {
      setResp(r)
      setPhase('done')
      onChanged()
      return
    }
    if (apiPath(r.status_url ?? r.poll_url)) {
      setResp(r)
      setPhase('waiting')
      return
    }
    setError(r.detail || r.message || t('channels.zernio.noNext'))
  }

  const start = async () => {
    setBusy(true)
    setError(null)
    try {
      handle(await socialApi<ConnectResponse>('social/connect', { method: 'POST', body: JSON.stringify(base) }))
    } catch (e) {
      fail(e)
    } finally {
      setBusy(false)
    }
  }

  const fields = resp?.fields ?? []
  const steps = (resp?.steps ?? []).map((s) => (typeof s === 'string' ? { body: s } : s))

  const submit = async () => {
    if (!resp) return
    const errs: Record<string, string> = {}
    fields.forEach((f, i) => {
      const k = fieldKey(f, i)
      const required = f.required ?? !f.optional
      if (required && !(values[k] ?? '').trim())
        errs[k] = t('channels.drawer.required', { field: f.label || k })
    })
    setFieldErrors(errs)
    if (Object.keys(errs).length > 0) return
    setBusy(true)
    setError(null)
    const credentials = Object.fromEntries(fields.map((f, i) => [fieldKey(f, i), (values[fieldKey(f, i)] ?? '').trim()]))
    const session = resp.session_id ?? resp.session ?? resp.state
    try {
      const path = apiPath(resp.submit_url ?? resp.complete_url ?? resp.next) ?? 'social/connect'
      const r = await socialApi<ConnectResponse>(path, {
        method: 'POST',
        body: JSON.stringify({ ...base, ...(session ? { session_id: session } : {}), ...(resp.step ? { step: resp.step } : {}), credentials }),
      })
      // never keep secrets around once sent
      setValues({})
      handle(r)
    } catch (e) {
      fail(e)
    } finally {
      setBusy(false)
    }
  }

  // poll a pending connection (e.g. Telegram waiting for the bot to be added)
  useEffect(() => {
    if (phase !== 'waiting' || !resp) return
    const path = apiPath(resp.status_url ?? resp.poll_url)
    if (!path) return
    let tries = 0
    let stop = false
    const id = window.setInterval(async () => {
      if (stop) return
      tries += 1
      try {
        const r = await socialApi<ConnectResponse>(path)
        if (isDone(r)) {
          stop = true
          window.clearInterval(id)
          setResp(r)
          setPhase('done')
          onChanged()
          return
        }
      } catch (e) {
        stop = true
        window.clearInterval(id)
        setPhase('form')
        fail(e)
        return
      }
      if (tries >= 40) {
        stop = true
        window.clearInterval(id)
        setError(t('channels.zernio.timedOut'))
      }
    }, 3000)
    return () => {
      stop = true
      window.clearInterval(id)
    }
    // fail / onChanged / t are stable enough for a 2-minute poll
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [phase, resp])

  const errorBox = error && (
    <div
      role="alert"
      className="flex items-start gap-2 rounded-lg border border-red/35 bg-red/10 px-3 py-2 text-[12.5px] font-semibold"
      style={{ color: 'var(--red-ink)' }}
    >
      <AlertTriangle size={14} className="mt-0.5 shrink-0" aria-hidden /> {error}
    </div>
  )

  if (phase === 'notSetUp')
    return (
      <div className="flex flex-col items-start gap-2 rounded-xl border border-dashed border-line-strong px-4 py-5">
        <span className="flex items-center gap-2 text-[15px] font-bold text-text-heading">
          <Share2 size={15} aria-hidden /> {t('social.zernio.notSetUpTitle')}
        </span>
        <p className="text-[12.5px] leading-relaxed text-text-muted">{t('channels.zernio.notSetUpBody')}</p>
        {can('app_keys.manage') && (
          <Link href="/settings/appkeys#managed-connections" className="text-[12.5px] font-semibold text-brand-300 hover:underline">
            {t('social.zernio.openIntegrations')}
          </Link>
        )}
      </div>
    )

  if (phase === 'done')
    return (
      <div className="flex flex-col items-center gap-3 py-6 text-center">
        <span className="grid h-14 w-14 place-items-center rounded-full bg-primary text-on-primary">
          <Check size={28} strokeWidth={2.4} aria-hidden />
        </span>
        <h3 className="display text-[22px] text-text-heading">{t('channels.zernio.doneTitle')}</h3>
        <p className="max-w-[360px] text-[12.5px] leading-relaxed text-text-muted">
          {t('channels.zernio.doneBody', { channel: resp?.name || channelName })}
        </p>
        <div className="mt-2 flex flex-wrap justify-center gap-2">
          <Button variant="secondary" onClick={onAnother}>
            {t('channels.drawer.footer.addAnother')}
          </Button>
          <Button onClick={onClose}>{t('common.actions.close')}</Button>
        </div>
      </div>
    )

  if (phase === 'form' || phase === 'waiting')
    return (
      <form
        className="flex flex-col gap-4"
        onSubmit={(e) => {
          e.preventDefault()
          void submit()
        }}
      >
        {errorBox}
        <p className="text-[12.5px] leading-relaxed text-text-mid">
          {resp?.instructions || resp?.title || t('channels.zernio.formIntro', { channel: channelName })}
        </p>
        {steps.length > 0 && (
          <ol className="flex list-decimal flex-col gap-2 pl-5 text-[12.5px] leading-relaxed text-text-mid">
            {steps.map((s, i) => (
              <li key={i} className="flex flex-col gap-1">
                {(s.title || s.label) && <span className="font-semibold text-text-high">{s.title || s.label}</span>}
                {(s.body || s.text || s.description) && <span>{s.body || s.text || s.description}</span>}
                {s.code && <CopyRow label={t('channels.zernio.code')} value={s.code} />}
                {s.url && (
                  <a
                    href={s.url}
                    target="_blank"
                    rel="noreferrer"
                    className="inline-flex items-center gap-1 font-semibold text-brand-300 hover:underline"
                  >
                    <ExternalLink size={12} aria-hidden /> {t('channels.zernio.open')}
                  </a>
                )}
              </li>
            ))}
          </ol>
        )}
        {fields.map((f, i) => {
          const k = fieldKey(f, i)
          const secret = f.secret || f.type === 'password'
          const err = fieldErrors[k]
          return (
            <label key={k} className="block">
              <span className="mb-1 block text-[12px] font-semibold text-text-high">
                {f.label || k}
                {(f.optional || f.required === false) && (
                  <span className="ml-1 font-normal text-text-muted">({t('common.status.optional').toLowerCase()})</span>
                )}
              </span>
              <input
                className={`${inputCls} ${err ? '!border-[color:var(--red-ink)]' : ''}`}
                type={secret ? 'password' : 'text'}
                autoComplete="off"
                value={values[k] ?? ''}
                placeholder={f.placeholder}
                aria-invalid={!!err}
                disabled={phase === 'waiting'}
                onChange={(e) => setValues((v) => ({ ...v, [k]: e.target.value }))}
              />
              {err ? (
                <span className="mt-1 block text-[11.5px] font-semibold" style={{ color: 'var(--red-ink)' }}>
                  {err}
                </span>
              ) : (
                (f.help || f.description) && (
                  <span className="mt-1 block text-[11.5px] leading-relaxed text-text-muted">{f.help || f.description}</span>
                )
              )}
            </label>
          )
        })}
        {phase === 'waiting' ? (
          <div role="status" className="flex items-center gap-2 text-[12.5px] text-text-muted">
            <Loader2 size={14} className="animate-spin" aria-hidden /> {t('channels.zernio.waiting')}
          </div>
        ) : (
          (fields.length > 0 || apiPath(resp?.submit_url ?? resp?.complete_url ?? resp?.next)) && (
            <div>
              <Button type="submit" disabled={busy}>
                {busy && <Loader2 size={13} className="animate-spin" aria-hidden />} {t('channels.zernio.submit')}
              </Button>
            </div>
          )
        )}
        {phase === 'form' && fields.length === 0 && apiPath(resp?.status_url ?? resp?.poll_url) && (
          <div>
            <Button type="button" variant="secondary" onClick={() => setPhase('waiting')}>
              {t('channels.zernio.checkNow')}
            </Button>
          </div>
        )}
      </form>
    )

  // intro / redirect
  return (
    <div className="flex flex-col gap-4">
      {errorBox}
      <div className="flex gap-2.5 rounded-lg border border-line bg-well px-3 py-2.5 text-[12.5px] leading-relaxed text-text-mid">
        <Share2 size={16} className="mt-0.5 shrink-0 text-text-muted" aria-hidden />
        <span>
          {t(purpose === 'social' ? 'channels.zernio.introSocial' : 'channels.zernio.introInbox', { channel: channelName })}
        </span>
      </div>
      {!canManage ? (
        <p className="text-[12.5px] text-text-muted">{t('social.accounts.needsPermission')}</p>
      ) : (
        <>
          <label className="block">
            <span className="mb-1 block text-[12px] font-semibold text-text-high">{t('channels.zernio.outlet')}</span>
            <EntityPicker
              value={entity}
              onChange={(slug) => {
                touched.current = true
                setEntity(slug)
              }}
              allowGlobal={!bound}
              globalLabel={t('social.accounts.orgLevel')}
              ariaLabel={t('channels.zernio.outlet')}
              disabled={busy || phase === 'redirect'}
            />
            <span className="mt-1 block text-[11.5px] leading-relaxed text-text-muted">{t('channels.zernio.outletHelp')}</span>
          </label>
          <div className="flex flex-col items-start gap-1.5">
            <Button onClick={start} disabled={busy || phase === 'redirect'}>
              {(busy || phase === 'redirect') && <Loader2 size={13} className="animate-spin" aria-hidden />}
              {phase === 'redirect' ? t('channels.zernio.redirecting') : t('channels.zernio.start')}
            </Button>
            <span className="text-[11.5px] text-text-muted">{t('channels.zernio.redirectNote', { channel: channelName })}</span>
          </div>
        </>
      )}
    </div>
  )
}
