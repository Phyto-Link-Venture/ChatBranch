'use client'

/**
 * Step 3 of "Add inbox" for WhatsApp → managed connection: self-serve onboarding.
 *
 * The client signs in to Meta in their own browser and picks the number; nothing is pasted here.
 *   POST /api/social/connect {platform:"whatsapp", entity, onboarding, signup}
 *     → {url} — Meta's Embedded Signup (or Zernio's hosted page, which preselects the number),
 *       and a one-time state; the browser comes back to /channels through the API callback.
 *
 * The MODE is asked for, never assumed, because the two modes have opposite consequences for the
 * number (the API refuses a start without one):
 *   api          Cloud API only — it STOPS working in the WhatsApp Business app, the phone's chat
 *                history does not come across, and reversing it means deregistering the number.
 *   business_app coexistence — it keeps working in the app, messages mirror both ways, someone
 *                scans a QR code, and the API side is capped (20 msg/s, no groups/calls/
 *                disappearing/view-once).
 * Both modes need the number disconnected from any previous provider first: one provider at a time.
 *
 * Also here: buying or managing a number (WhatsAppNumbersPanel) and the advanced fallback of
 * pasting the business's own Meta System User credentials (POST /api/social/connect/whatsapp).
 */

import { useEffect, useRef, useState } from 'react'
import Link from 'next/link'
import { AlertTriangle, ChevronDown, ChevronRight, Loader2, MessageCircle } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { EntityPicker } from '@/components/ui/EntityPicker'
import { useAccess } from '@/lib/useAccess'
import { useEntity } from '@/lib/EntityContext'
import { errMessage, isNotSetUp, zernioApi, ZernioApiError } from '@/lib/zernio'
import { inputCls } from '@/components/settings/WhatsAppSettings'
import { useT, type MessageKey } from '@/i18n'
import { WhatsAppNumbersPanel } from './WhatsAppNumbersPanel'

type Mode = 'api' | 'business_app'

interface StartResponse {
  url?: string
  state?: string
  onboarding?: string
  signup?: string
  coexistence?: boolean
  detail?: string
}

/** The warnings each mode must show, in the order they matter. */
const WARNINGS: Record<Mode, MessageKey[]> = {
  api: ['channels.waSignup.warnOneProvider', 'channels.waSignup.warnLeavesApp', 'channels.waSignup.warnHardToReverse'],
  business_app: ['channels.waSignup.warnOneProvider', 'channels.waSignup.warnScan', 'channels.waSignup.warnLimits'],
}

const CRED_FIELDS: { key: string; label: MessageKey; help?: MessageKey; secret: boolean; required: boolean; placeholder?: string }[] = [
  { key: 'access_token', label: 'channels.waSignup.fields.token', help: 'channels.waSignup.fields.tokenHelp', secret: true, required: true, placeholder: 'EAAB…' },
  { key: 'waba_id', label: 'channels.waSignup.fields.waba', secret: false, required: true },
  { key: 'phone_number_id', label: 'channels.waSignup.fields.phoneId', secret: false, required: true },
  { key: 'pin', label: 'channels.waSignup.fields.pin', help: 'channels.waSignup.fields.pinHelp', secret: true, required: false, placeholder: '123456' },
]

export function WhatsAppSignupPanel({
  onChanged,
  onAnother,
  onClose,
}: {
  onChanged: () => void
  onAnother: () => void
  onClose: () => void
}) {
  const t = useT()
  const { can } = useAccess()
  const canManage = can('channels.manage')
  const { levelEntity, selectedEntities, bound } = useEntity()

  const [entity, setEntity] = useState<string | null>(levelEntity?.slug ?? selectedEntities[0]?.slug ?? null)
  const touched = useRef(false)
  useEffect(() => {
    if (!touched.current) setEntity(levelEntity?.slug ?? selectedEntities[0]?.slug ?? null)
  }, [levelEntity, selectedEntities])

  const [mode, setMode] = useState<Mode | null>(null)
  const [ack, setAck] = useState(false)
  const [ackError, setAckError] = useState(false)
  const [busy, setBusy] = useState(false)
  const [redirecting, setRedirecting] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notSetUp, setNotSetUp] = useState(false)

  const [numbersOpen, setNumbersOpen] = useState(false)
  const [credsOpen, setCredsOpen] = useState(false)
  const [creds, setCreds] = useState<Record<string, string>>({})
  const [credBusy, setCredBusy] = useState(false)
  const [credDone, setCredDone] = useState(false)

  const fail = (e: unknown) => {
    if (isNotSetUp(e)) {
      setNotSetUp(true)
      return
    }
    if (e instanceof ZernioApiError && e.status === 403) setError(t('managed.accounts.needsPermission'))
    else setError(errMessage(e))
  }

  const start = async () => {
    if (!mode) return
    if (!ack) {
      setAckError(true)
      return
    }
    setBusy(true)
    setError(null)
    try {
      const r = await zernioApi<StartResponse>('social/connect', {
        method: 'POST',
        body: JSON.stringify({
          platform: 'whatsapp',
          entity: entity ?? undefined,
          onboarding: mode,
          // the hosted page connects exactly the number the person picked (Cloud-API-only mode);
          // coexistence runs in Meta's own window, and the API answers which one it used
          signup: 'hosted',
        }),
      })
      if (!r.url) {
        setError(r.detail || t('channels.zernio.noNext'))
        return
      }
      setRedirecting(true)
      window.location.assign(r.url)
    } catch (e) {
      fail(e)
    } finally {
      setBusy(false)
    }
  }

  const submitCreds = async () => {
    const missing = CRED_FIELDS.filter((f) => f.required && !(creds[f.key] ?? '').trim())
    if (missing.length > 0) {
      setError(t('channels.drawer.required', { field: t(missing[0].label) }))
      return
    }
    setCredBusy(true)
    setError(null)
    try {
      await zernioApi('social/connect/whatsapp', {
        method: 'POST',
        body: JSON.stringify({
          platform: 'whatsapp',
          entity: entity ?? undefined,
          credentials: Object.fromEntries(CRED_FIELDS.map((f) => [f.key, (creds[f.key] ?? '').trim()])),
        }),
      })
      setCreds({})             // never keep a token around once it is sent
      setCredDone(true)
      onChanged()
    } catch (e) {
      fail(e)
    } finally {
      setCredBusy(false)
    }
  }

  if (notSetUp)
    return (
      <div className="flex flex-col items-start gap-2 rounded-xl border border-dashed border-line-strong px-4 py-5">
        <span className="flex items-center gap-2 text-[15px] font-bold text-text-heading">
          <MessageCircle size={15} aria-hidden /> {t('managed.zernio.notSetUpTitle')}
        </span>
        <p className="text-[12.5px] leading-relaxed text-text-muted">{t('channels.zernio.notSetUpBody')}</p>
        {can('app_keys.manage') && (
          <Link href="/settings/appkeys#managed-connections" className="text-[12.5px] font-semibold text-brand-300 hover:underline">
            {t('managed.zernio.openIntegrations')}
          </Link>
        )}
      </div>
    )

  if (credDone)
    return (
      <div className="flex flex-col items-center gap-3 py-6 text-center">
        <h3 className="display text-[22px] text-text-heading">{t('channels.zernio.doneTitle')}</h3>
        <p className="max-w-[360px] text-[12.5px] leading-relaxed text-text-muted">{t('channels.waSignup.advancedDone')}</p>
        <div className="mt-2 flex flex-wrap justify-center gap-2">
          <Button variant="secondary" onClick={onAnother}>
            {t('channels.drawer.footer.addAnother')}
          </Button>
          <Button onClick={onClose}>{t('common.actions.close')}</Button>
        </div>
      </div>
    )

  return (
    <div className="flex flex-col gap-4">
      {error && (
        <div
          role="alert"
          className="flex items-start gap-2 rounded-lg border border-red/35 bg-red/10 px-3 py-2 text-[12.5px] font-semibold"
          style={{ color: 'var(--red-ink)' }}
        >
          <AlertTriangle size={14} className="mt-0.5 shrink-0" aria-hidden /> {error}
        </div>
      )}

      <div className="flex gap-2.5 rounded-lg border border-line bg-well px-3 py-2.5 text-[12.5px] leading-relaxed text-text-mid">
        <MessageCircle size={16} className="mt-0.5 shrink-0 text-text-muted" aria-hidden />
        <span>{t('channels.waSignup.intro')}</span>
      </div>

      {!canManage ? (
        <p className="text-[12.5px] text-text-muted">{t('managed.accounts.needsPermission')}</p>
      ) : (
        <>
          <label className="block">
            <span className="mb-1 block text-[12px] font-semibold text-text-high">{t('channels.waSignup.outlet')}</span>
            <EntityPicker
              value={entity}
              onChange={(slug) => {
                touched.current = true
                setEntity(slug)
              }}
              allowGlobal={!bound}
              globalLabel={t('managed.accounts.orgLevel')}
              ariaLabel={t('channels.waSignup.outlet')}
              disabled={busy || redirecting}
            />
            <span className="mt-1 block text-[11.5px] leading-relaxed text-text-muted">{t('channels.waSignup.outletHelp')}</span>
          </label>

          {/* the mode: two opposite outcomes for the number, so it is picked explicitly */}
          <fieldset className="flex flex-col gap-2">
            <legend className="mb-1 text-[12px] font-semibold text-text-high">{t('channels.waSignup.modeTitle')}</legend>
            {(['api', 'business_app'] as Mode[]).map((m) => (
              <label
                key={m}
                className={`flex cursor-pointer items-start gap-2.5 rounded-lg border px-3 py-2.5 ${
                  mode === m ? 'border-primary bg-primary/5' : 'border-line bg-well'
                }`}
              >
                <input
                  type="radio"
                  name="wa-onboarding"
                  value={m}
                  checked={mode === m}
                  onChange={() => {
                    setMode(m)
                    setAck(false)
                    setAckError(false)
                  }}
                  className="mt-0.5 h-4 w-4 shrink-0"
                  style={{ accentColor: 'var(--primary)' }}
                />
                <span className="flex flex-col gap-0.5">
                  <span className="text-[12.5px] font-semibold text-text-high">
                    {t(m === 'api' ? 'channels.waSignup.modeApi' : 'channels.waSignup.modeApp')}
                  </span>
                  <span className="text-[11.5px] leading-relaxed text-text-muted">
                    {t(m === 'api' ? 'channels.waSignup.modeApiHint' : 'channels.waSignup.modeAppHint')}
                  </span>
                </span>
              </label>
            ))}
          </fieldset>

          {mode && (
            <>
              <div
                className="flex flex-col gap-2 rounded-lg border border-amber/40 bg-amber/10 px-3 py-2.5 text-[12.5px] leading-relaxed"
                style={{ color: 'var(--amber-ink)' }}
              >
                <span className="flex items-center gap-2 font-semibold">
                  <AlertTriangle size={16} className="shrink-0" aria-hidden /> {t('channels.waSignup.warningsTitle')}
                </span>
                <ul className="flex list-disc flex-col gap-1.5 pl-5">
                  {WARNINGS[mode].map((k) => (
                    <li key={k}>{t(k)}</li>
                  ))}
                </ul>
              </div>
              <label className="flex cursor-pointer items-start gap-2.5 rounded-lg border border-line bg-well px-3 py-2.5">
                <input
                  type="checkbox"
                  checked={ack}
                  onChange={(e) => {
                    setAck(e.target.checked)
                    if (e.target.checked) setAckError(false)
                  }}
                  className="mt-0.5 h-4 w-4 shrink-0"
                  style={{ accentColor: 'var(--primary)' }}
                />
                <span className="text-[12.5px] text-text-high">{t('channels.waSignup.ack')}</span>
              </label>
              {ackError && (
                <span className="text-[11.5px] font-semibold" style={{ color: 'var(--red-ink)' }}>
                  {t('channels.waSignup.ackRequired')}
                </span>
              )}
            </>
          )}

          <div className="flex flex-col items-start gap-1.5">
            <Button onClick={start} disabled={!mode || busy || redirecting}>
              {(busy || redirecting) && <Loader2 size={13} className="animate-spin" aria-hidden />}
              {redirecting ? t('channels.waSignup.starting') : t('channels.waSignup.start')}
            </Button>
            <span className="text-[11.5px] text-text-muted">{t('channels.waSignup.startNote')}</span>
          </div>

          {/* buy or manage a number */}
          <div className="rounded-lg border border-line">
            <button
              type="button"
              className="flex w-full items-center gap-2 px-3 py-2.5 text-left text-[12.5px] font-semibold text-text-high"
              onClick={() => setNumbersOpen((v) => !v)}
              aria-expanded={numbersOpen}
            >
              {numbersOpen ? <ChevronDown size={14} aria-hidden /> : <ChevronRight size={14} aria-hidden />}
              {t('channels.waSignup.numbersToggle')}
            </button>
            {numbersOpen && (
              <div className="border-t border-line px-3 py-3">
                <WhatsAppNumbersPanel entity={entity} />
              </div>
            )}
          </div>

          {/* the advanced fallback: the business's own Meta credentials */}
          <div className="rounded-lg border border-line">
            <button
              type="button"
              className="flex w-full items-center gap-2 px-3 py-2.5 text-left text-[12.5px] font-semibold text-text-high"
              onClick={() => setCredsOpen((v) => !v)}
              aria-expanded={credsOpen}
            >
              {credsOpen ? <ChevronDown size={14} aria-hidden /> : <ChevronRight size={14} aria-hidden />}
              {t('channels.waSignup.advancedTitle')}
            </button>
            {credsOpen && (
              <form
                className="flex flex-col gap-3 border-t border-line px-3 py-3"
                onSubmit={(e) => {
                  e.preventDefault()
                  void submitCreds()
                }}
              >
                <p className="text-[12.5px] leading-relaxed text-text-muted">{t('channels.waSignup.advancedHint')}</p>
                {CRED_FIELDS.map((f) => (
                  <label key={f.key} className="block">
                    <span className="mb-1 block text-[12px] font-semibold text-text-high">
                      {t(f.label)}
                      {!f.required && (
                        <span className="ml-1 font-normal text-text-muted">({t('common.status.optional').toLowerCase()})</span>
                      )}
                    </span>
                    <input
                      className={inputCls}
                      type={f.secret ? 'password' : 'text'}
                      autoComplete="off"
                      placeholder={f.placeholder}
                      value={creds[f.key] ?? ''}
                      onChange={(e) => setCreds((v) => ({ ...v, [f.key]: e.target.value }))}
                    />
                    {f.help && <span className="mt-1 block text-[11.5px] leading-relaxed text-text-muted">{t(f.help)}</span>}
                  </label>
                ))}
                <div>
                  <Button type="submit" variant="secondary" disabled={credBusy}>
                    {credBusy && <Loader2 size={13} className="animate-spin" aria-hidden />}
                    {t('channels.waSignup.advancedSubmit')}
                  </Button>
                </div>
              </form>
            )}
          </div>
        </>
      )}
    </div>
  )
}
