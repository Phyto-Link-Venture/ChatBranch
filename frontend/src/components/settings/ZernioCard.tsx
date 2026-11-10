'use client'

/**
 * Settings → App & provider keys → "Zernio (social media)": the ONE deployment-wide Zernio API key
 * (super admin; saved through the global settings endpoint like the other app
 * keys and masked on read), the webhook Zernio calls us on, and which entity
 * owns which Zernio profile. Social accounts themselves are connected per entity
 * on /channels.
 *
 * Reads GET /api/zernio/status; Test = POST /api/zernio/test; Register webhook =
 * POST /api/zernio/webhook/register. A server without these endpoints shows a
 * calm "not available yet" line instead of an error.
 */

import { useCallback, useEffect, useRef, useState } from 'react'
import Link from 'next/link'
import { AlertCircle, CheckCircle, KeyRound, Loader2, RefreshCw, Share2, Webhook } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { useAccess } from '@/lib/useAccess'
import { SocialApiError, errMessage, isNotSetUp, socialApi, type ZernioStatus } from '@/lib/social'
import { useT } from '@/i18n'

const inputCls =
  'w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[12.5px] text-text-primary outline-none transition-colors focus:border-brand-400'

export function ZernioCard() {
  const t = useT()
  const tRef = useRef(t)
  tRef.current = t
  const { ready, can } = useAccess()
  const canKeys = can('app_keys.manage')

  const [status, setStatus] = useState<ZernioStatus | null>(null)
  /** 'missing' = this server has no Zernio endpoints yet; 'denied' = not allowed to read it. */
  const [state, setState] = useState<'loading' | 'ok' | 'missing' | 'denied' | 'error'>('loading')
  const [loadError, setLoadError] = useState('')
  const [key, setKey] = useState('')
  const [busy, setBusy] = useState<'' | 'save' | 'test' | 'webhook'>('')
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)

  const load = useCallback(async () => {
    try {
      const s = await socialApi<ZernioStatus>('zernio/status')
      setStatus(s)
      setState('ok')
    } catch (e) {
      if (isNotSetUp(e)) setState('missing')
      else if (e instanceof SocialApiError && e.status === 403) setState('denied')
      else {
        setLoadError(errMessage(e))
        setState('error')
      }
    }
  }, [])

  useEffect(() => {
    if (ready) void load()
  }, [ready, load])

  const saveKey = async () => {
    if (!key.trim()) return
    setBusy('save')
    setMsg(null)
    try {
      // the app-keys mechanism: global settings, write-only, masked on read
      await socialApi('whatsapp/settings', { method: 'POST', body: JSON.stringify({ zernio_api_key: key.trim() }) })
      setKey('')
      setMsg({ ok: true, text: t('social.zernio.saved') })
      await load()
    } catch (e) {
      setMsg({ ok: false, text: errMessage(e) })
    } finally {
      setBusy('')
    }
  }

  const test = async () => {
    setBusy('test')
    setMsg(null)
    try {
      const r = await socialApi<{ profiles?: unknown[]; detail?: string }>('zernio/test', { method: 'POST' })
      setMsg({
        ok: true,
        text: r.detail || t('social.zernio.testOk', { count: Array.isArray(r.profiles) ? r.profiles.length : 0 }),
      })
      await load()
    } catch (e) {
      setMsg({ ok: false, text: isNotSetUp(e) ? t('social.zernio.notSetUpBody') : errMessage(e) })
    } finally {
      setBusy('')
    }
  }

  const registerWebhook = async () => {
    setBusy('webhook')
    setMsg(null)
    try {
      await socialApi('zernio/webhook/register', { method: 'POST' })
      setMsg({ ok: true, text: t('social.zernio.webhookRegistered') })
      await load()
    } catch (e) {
      setMsg({ ok: false, text: errMessage(e) })
    } finally {
      setBusy('')
    }
  }

  if (!ready) return null
  // Only the people who can see integrations reach this page; the key itself is super admin.
  if (state === 'denied' && !canKeys) return null

  const configured = !!status?.configured
  const webhook = status?.webhook ?? null
  const profiles = status?.profiles ?? []

  return (
    <section data-tour="appkeys-zernio" aria-labelledby="zernio-h" className="flex flex-col gap-3">
      <div>
        <div className="flex items-center gap-2">
          <Share2 size={15} className="text-brand-300" aria-hidden />
          <h2 id="zernio-h" className="text-[14px] font-extrabold tracking-[-0.3px] text-text-heading">
            {t('social.zernio.title')}
          </h2>
          {state === 'ok' && (
            <span
              className={`shrink-0 rounded-full px-2 py-px text-[10px] font-bold ${
                configured ? 'bg-green/15 text-green-300' : 'bg-amber/15 text-amber-300'
              }`}
            >
              {configured ? t('common.status.connected') : t('social.zernio.needsKey')}
            </span>
          )}
        </div>
        <p className="mt-0.5 max-w-[720px] text-[11px] leading-relaxed text-text-muted">{t('social.zernio.hint')}</p>
      </div>

      <div className="flex max-w-[860px] flex-col gap-4 rounded-xl border border-line bg-card p-4">
        {state === 'loading' && (
          <div className="flex items-center gap-1.5 text-[12px] text-text-muted">
            <Loader2 size={13} className="animate-spin" aria-hidden /> {t('common.status.loading')}
          </div>
        )}
        {state === 'missing' && (
          <div className="rounded-lg border border-dashed border-line-strong px-3 py-2.5 text-[12px] leading-relaxed text-text-mid">
            <div className="font-bold text-text-high">{t('social.zernio.notSetUpTitle')}</div>
            {t('social.zernio.unavailable')}
          </div>
        )}
        {state === 'error' && (
          <div className="flex flex-wrap items-center gap-2 text-[12px]" style={{ color: 'var(--red-ink)' }}>
            <AlertCircle size={13} aria-hidden /> {loadError}
            <Button variant="ghost" size="sm" onClick={() => void load()}>
              <RefreshCw size={12} aria-hidden /> {t('common.actions.retry')}
            </Button>
          </div>
        )}

        {msg && (
          <div
            role={msg.ok ? 'status' : 'alert'}
            className={`flex items-start gap-2 rounded-lg border px-3 py-2 text-[12px] ${
              msg.ok ? 'border-green/25 bg-green/10' : 'border-red/25 bg-red/10'
            }`}
            style={{ color: msg.ok ? 'var(--green-ink)' : 'var(--red-ink)' }}
          >
            {msg.ok ? <CheckCircle size={13} className="mt-0.5 shrink-0" aria-hidden /> : <AlertCircle size={13} className="mt-0.5 shrink-0" aria-hidden />}
            {msg.text}
          </div>
        )}

        {state === 'ok' && (
          <>
            {/* API key */}
            <div className="flex flex-col gap-1.5">
              <span className="flex items-center gap-1.5 text-[11px] font-bold text-text-high">
                <KeyRound size={12} aria-hidden /> {t('social.zernio.apiKey')}
              </span>
              {configured && status?.key_masked && (
                <span className="font-mono text-[12px] text-text-mid">{status.key_masked}</span>
              )}
              {canKeys ? (
                <div className="flex flex-wrap items-center gap-2">
                  <input
                    type="password"
                    autoComplete="off"
                    value={key}
                    onChange={(e) => setKey(e.target.value)}
                    placeholder={configured ? t('social.zernio.replaceKey') : t('social.zernio.keyPlaceholder')}
                    aria-label={t('social.zernio.apiKey')}
                    className={`${inputCls} max-w-[420px] flex-1`}
                  />
                  <Button size="sm" onClick={saveKey} disabled={!key.trim() || busy !== ''}>
                    {busy === 'save' && <Loader2 size={12} className="animate-spin" aria-hidden />}
                    {t('common.actions.save')}
                  </Button>
                  <Button variant="secondary" size="sm" onClick={test} disabled={!configured || busy !== ''}>
                    {busy === 'test' ? <Loader2 size={12} className="animate-spin" aria-hidden /> : <RefreshCw size={12} aria-hidden />}
                    {t('social.zernio.test')}
                  </Button>
                </div>
              ) : (
                <span className="text-[11px] text-text-muted">{t('social.zernio.superOnly')}</span>
              )}
              <span className="text-[10.5px] leading-relaxed text-text-muted">{t('social.zernio.keyHint')}</span>
            </div>

            {/* Webhook */}
            <div className="flex flex-col gap-1.5 border-t border-line pt-3">
              <span className="flex items-center gap-1.5 text-[11px] font-bold text-text-high">
                <Webhook size={12} aria-hidden /> {t('social.zernio.webhook')}
                <span
                  className={`rounded-full px-2 py-px text-[10px] font-bold ${
                    webhook?.registered ? 'bg-green/15 text-green-300' : 'bg-fill text-text-muted'
                  }`}
                >
                  {webhook?.registered ? t('social.zernio.registered') : t('social.zernio.notRegistered')}
                </span>
              </span>
              {webhook?.url && <span className="break-all font-mono text-[11.5px] text-text-mid">{webhook.url}</span>}
              {webhook?.events && webhook.events.length > 0 && (
                <span className="text-[10.5px] text-text-muted">
                  {t('social.zernio.events', { events: webhook.events.join(', ') })}
                </span>
              )}
              <span className="text-[10.5px] leading-relaxed text-text-muted">{t('social.zernio.webhookHint')}</span>
              {canKeys && (
                <div>
                  <Button variant="secondary" size="sm" onClick={registerWebhook} disabled={!configured || busy !== ''}>
                    {busy === 'webhook' ? <Loader2 size={12} className="animate-spin" aria-hidden /> : <Webhook size={12} aria-hidden />}
                    {webhook?.registered ? t('social.zernio.reRegister') : t('social.zernio.register')}
                  </Button>
                </div>
              )}
            </div>

            {/* Entity → profile */}
            <div className="flex flex-col gap-1.5 border-t border-line pt-3">
              <span className="text-[11px] font-bold text-text-high">{t('social.zernio.profiles')}</span>
              <span className="text-[10.5px] leading-relaxed text-text-muted">{t('social.zernio.profilesHint')}</span>
              {profiles.length === 0 ? (
                <span className="text-[11.5px] text-text-muted">{t('social.zernio.noProfiles')}</span>
              ) : (
                <ul className="flex flex-col divide-y divide-line rounded-lg border border-line">
                  {profiles.map((p, i) => (
                    <li key={`${p.entity_id ?? 'org'}-${i}`} className="flex flex-wrap items-center justify-between gap-2 px-3 py-1.5 text-[12px]">
                      <span className="font-semibold text-text-high">
                        {p.entity_name || (p.entity_id == null ? t('social.zernio.orgProfile') : `#${p.entity_id}`)}
                      </span>
                      <span className="font-mono text-[11px] text-text-muted">{p.profile_id || '—'}</span>
                    </li>
                  ))}
                </ul>
              )}
              <span className="text-[11px] text-text-muted">
                {t('social.zernio.connectOn')}{' '}
                <Link href="/channels#social-accounts" className="font-semibold text-brand-300 hover:underline">
                  {t('shell.nav.channels')}
                </Link>
              </span>
            </div>
          </>
        )}
      </div>
    </section>
  )
}
