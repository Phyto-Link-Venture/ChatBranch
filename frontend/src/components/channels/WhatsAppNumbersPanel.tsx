'use client'

/**
 * Phone numbers for the managed WhatsApp connection (new file — nothing else
 * renders this yet beyond WhatsAppSignupPanel).
 *
 *   GET    /api/social/phone-numbers?entity=      the outlet's numbers + the live price list
 *   POST   /api/social/phone-numbers/purchase     buy one  (confirm: true + a purchase intent id)
 *   DELETE /api/social/phone-numbers/{id}         release one (confirm=true + the number typed out)
 *
 * Both writes spend the owner's money — a purchase starts a monthly charge, a release cannot be
 * undone — so the price is on screen BEFORE the buy button, and each one asks in its own box:
 * a purchase needs the amount acknowledged, a release needs the number typed.
 */

import { useCallback, useEffect, useState } from 'react'
import { AlertTriangle, Check, Loader2, Phone } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Select } from '@/components/ui/Select'
import { errMessage, isNotSetUp, socialApi } from '@/lib/social'
import { inputCls } from '@/components/settings/WhatsAppSettings'
import { useT } from '@/i18n'

export interface OwnedNumber {
  id: string
  phone_number: string
  country?: string | null
  status?: string | null
  monthly_cents?: number | null
  currency?: string | null
  hosted_by_zernio?: boolean | null
  sms_capable?: boolean | null
  whatsapp_connected?: boolean | null
}

interface CountryOffer {
  code: string
  monthly_cents?: number | null
  whatsapp?: boolean | null
  sms?: boolean | null
  needs_kyc?: boolean | null
  in_stock?: boolean | null
}

interface NumbersPayload {
  numbers?: OwnedNumber[] | null
  countries?: CountryOffer[] | null
  currency?: string | null
}

/** Cents → "US$3.00" (the API quotes USD cents; never guess a local currency). */
const price = (cents?: number | null, currency?: string | null) =>
  typeof cents === 'number' && cents > 0
    ? `${currency === 'USD' || !currency ? 'US$' : `${currency} `}${(cents / 100).toFixed(2)}`
    : '—'

const digits = (s: string) => s.replace(/\D+/g, '')

export function WhatsAppNumbersPanel({ entity, onBought }: { entity: string | null; onBought?: () => void }) {
  const t = useT()
  const [data, setData] = useState<NumbersPayload | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  // buy
  const [country, setCountry] = useState('')
  const [wantsSms, setWantsSms] = useState(false)
  const [buyAck, setBuyAck] = useState(false)
  const [buying, setBuying] = useState(false)
  // one intent id per dialog: a retry must never buy a second number
  const [intentId, setIntentId] = useState(() => `cb-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`)

  // release
  const [releasing, setReleasing] = useState<OwnedNumber | null>(null)
  const [typed, setTyped] = useState('')
  const [busyRelease, setBusyRelease] = useState(false)

  const load = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      const q = entity ? `?entity=${encodeURIComponent(entity)}` : ''
      setData(await socialApi<NumbersPayload>(`social/phone-numbers${q}`))
    } catch (e) {
      setError(isNotSetUp(e) ? t('channels.zernio.notSetUpBody') : t('channels.waNumbers.loadFailed', { reason: errMessage(e) }))
    } finally {
      setLoading(false)
    }
  }, [entity, t])

  useEffect(() => {
    void load()
  }, [load])

  const offers = (data?.countries ?? []).filter((c) => c.whatsapp !== false)
  const chosen = offers.find((c) => c.code === country)
  const owned = data?.numbers ?? []

  const buy = async () => {
    if (!chosen || !buyAck) return
    setBuying(true)
    setError(null)
    setNotice(null)
    try {
      const r = await socialApi<{ number?: OwnedNumber; pending_kyc?: boolean }>('social/phone-numbers/purchase', {
        method: 'POST',
        body: JSON.stringify({
          country: chosen.code,
          entity: entity ?? undefined,
          wants_sms: wantsSms,
          connect_whatsapp: true,
          confirm: true,
          purchase_intent_id: intentId,
        }),
      })
      setNotice(
        r.pending_kyc
          ? t('channels.waNumbers.pendingKyc')
          : t('channels.waNumbers.bought', { number: r.number?.phone_number ?? chosen.code }),
      )
      setBuyAck(false)
      setIntentId(`cb-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`)
      await load()
      onBought?.()
    } catch (e) {
      setError(errMessage(e))
    } finally {
      setBuying(false)
    }
  }

  const release = async () => {
    if (!releasing) return
    setBusyRelease(true)
    setError(null)
    setNotice(null)
    try {
      await socialApi(
        `social/phone-numbers/${encodeURIComponent(releasing.id)}?confirm=true&phone_number=${encodeURIComponent(typed)}`,
        { method: 'DELETE' },
      )
      setNotice(t('channels.waNumbers.released', { number: releasing.phone_number }))
      setReleasing(null)
      setTyped('')
      await load()
    } catch (e) {
      setError(errMessage(e))
    } finally {
      setBusyRelease(false)
    }
  }

  return (
    <div className="flex flex-col gap-4">
      <p className="text-[12.5px] leading-relaxed text-text-mid">{t('channels.waNumbers.intro')}</p>

      {error && (
        <div
          role="alert"
          className="flex items-start gap-2 rounded-lg border border-red/35 bg-red/10 px-3 py-2 text-[12.5px] font-semibold"
          style={{ color: 'var(--red-ink)' }}
        >
          <AlertTriangle size={14} className="mt-0.5 shrink-0" aria-hidden /> {error}
        </div>
      )}
      {notice && (
        <div
          role="status"
          className="flex items-start gap-2 rounded-lg border border-green/35 bg-green/10 px-3 py-2 text-[12.5px] font-semibold"
          style={{ color: 'var(--green-ink)' }}
        >
          <Check size={14} className="mt-0.5 shrink-0" aria-hidden /> {notice}
        </div>
      )}

      {/* what this outlet already has */}
      <div className="flex flex-col gap-2">
        <span className="text-[12px] font-semibold text-text-high">{t('channels.waNumbers.owned')}</span>
        {loading ? (
          <span className="flex items-center gap-2 text-[12.5px] text-text-muted">
            <Loader2 size={13} className="animate-spin" aria-hidden /> {t('common.status.loading')}
          </span>
        ) : owned.length === 0 ? (
          <span className="text-[12.5px] text-text-muted">{t('channels.waNumbers.none')}</span>
        ) : (
          <ul className="flex flex-col gap-1.5">
            {owned.map((n) => (
              <li
                key={n.id}
                className="flex flex-wrap items-center gap-2 rounded-lg border border-line bg-well px-3 py-2 text-[12.5px]"
              >
                <Phone size={14} className="shrink-0 text-text-muted" aria-hidden />
                <span className="font-semibold text-text-high">{n.phone_number || n.id}</span>
                <span className="text-text-muted">{n.country}</span>
                <span className="text-text-muted">
                  {t('channels.waNumbers.monthly', { price: price(n.monthly_cents, n.currency ?? data?.currency) })}
                </span>
                {n.status && <span className="text-text-muted">{n.status}</span>}
                <button
                  type="button"
                  className="ml-auto text-[12px] font-semibold hover:underline"
                  style={{ color: 'var(--red-ink)' }}
                  onClick={() => {
                    setReleasing(n)
                    setTyped('')
                  }}
                >
                  {t('channels.waNumbers.release')}
                </button>
              </li>
            ))}
          </ul>
        )}
      </div>

      {/* buy one — the price is shown before the button, and acknowledged */}
      <div className="flex flex-col gap-2.5 rounded-lg border border-line px-3 py-3">
        <span className="text-[12px] font-semibold text-text-high">{t('channels.waNumbers.title')}</span>
        <label className="block">
          <span className="mb-1 block text-[12px] font-semibold text-text-high">{t('channels.waNumbers.country')}</span>
          <Select
            value={country}
            onChange={(v) => {
              setCountry(v)
              setBuyAck(false)
            }}
            size="md"
            ariaLabel={t('channels.waNumbers.country')}
            placeholder={t('common.actions.select')}
            options={offers.map((c) => ({
              value: c.code,
              label: t('channels.waNumbers.countryPrice', {
                code: c.code,
                price: price(c.monthly_cents, data?.currency),
              }),
              hint: [c.needs_kyc ? t('channels.waNumbers.kyc') : '', c.in_stock === false ? t('channels.waNumbers.outOfStock') : '']
                .filter(Boolean)
                .join(' · '),
              disabled: c.in_stock === false,
            }))}
          />
        </label>
        <label className="flex cursor-pointer items-center gap-2 text-[12.5px] text-text-high">
          <input
            type="checkbox"
            checked={wantsSms}
            onChange={(e) => setWantsSms(e.target.checked)}
            className="h-4 w-4 shrink-0"
            style={{ accentColor: 'var(--primary)' }}
          />
          {t('channels.waNumbers.wantsSms')}
        </label>
        {chosen && (
          <>
            <div
              className="flex gap-2.5 rounded-lg border border-amber/40 bg-amber/10 px-3 py-2.5 text-[12.5px] leading-relaxed"
              style={{ color: 'var(--amber-ink)' }}
            >
              <AlertTriangle size={16} className="mt-0.5 shrink-0" aria-hidden />
              <span>
                {t('channels.waNumbers.buyConfirmBody', { price: price(chosen.monthly_cents, data?.currency) })}
              </span>
            </div>
            <label className="flex cursor-pointer items-start gap-2.5 rounded-lg border border-line bg-well px-3 py-2.5">
              <input
                type="checkbox"
                checked={buyAck}
                onChange={(e) => setBuyAck(e.target.checked)}
                className="mt-0.5 h-4 w-4 shrink-0"
                style={{ accentColor: 'var(--primary)' }}
              />
              <span className="text-[12.5px] text-text-high">
                {t('channels.waNumbers.buyConfirmAck', { price: price(chosen.monthly_cents, data?.currency) })}
              </span>
            </label>
          </>
        )}
        <div>
          <Button onClick={buy} disabled={!chosen || !buyAck || buying}>
            {buying && <Loader2 size={13} className="animate-spin" aria-hidden />}
            {buying ? t('channels.waNumbers.buying') : t('channels.waNumbers.buy')}
          </Button>
        </div>
      </div>

      {/* release — typed confirmation, in place */}
      {releasing && (
        <div className="flex flex-col gap-2.5 rounded-lg border border-red/35 bg-red/5 px-3 py-3">
          <span className="text-[12.5px] font-semibold" style={{ color: 'var(--red-ink)' }}>
            {t('channels.waNumbers.releaseTitle', { number: releasing.phone_number || releasing.id })}
          </span>
          <p className="text-[12.5px] leading-relaxed text-text-mid">{t('channels.waNumbers.releaseBody')}</p>
          <label className="block">
            <span className="mb-1 block text-[12px] font-semibold text-text-high">
              {t('channels.waNumbers.releaseTyped')}
            </span>
            <input
              className={inputCls}
              value={typed}
              autoComplete="off"
              placeholder={releasing.phone_number}
              onChange={(e) => setTyped(e.target.value)}
            />
          </label>
          <div className="flex flex-wrap gap-2">
            <Button
              variant="danger"
              onClick={release}
              disabled={busyRelease || digits(typed).length === 0 || digits(typed) !== digits(releasing.phone_number || '')}
            >
              {busyRelease && <Loader2 size={13} className="animate-spin" aria-hidden />}
              {busyRelease ? t('channels.waNumbers.releasing') : t('channels.waNumbers.release')}
            </Button>
            <Button
              variant="secondary"
              onClick={() => {
                setReleasing(null)
                setTyped('')
              }}
            >
              {t('common.actions.cancel')}
            </Button>
          </div>
        </div>
      )}
    </div>
  )
}
