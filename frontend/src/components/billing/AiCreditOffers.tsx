'use client'

/**
 * Settings → AI billing: promotions on AI credit (billing.manage, superadmin by
 * default) — GET/POST /api/billing/ai/credit/offers, DELETE …/offers/:slug and a
 * manual grant of an offer's bonus (POST …/offers/:slug/grant).
 *
 * Buyers pick an open offer on the top-up card. The bonus is always written as its
 * own `promo_bonus` ledger line, so paid credit and promotional credit stay apart.
 */

import { useCallback, useEffect, useState } from 'react'
import { Gift, Loader2, Plus } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Card, CardTitle } from '@/components/ui/Card'
import { Select } from '@/components/ui/Select'
import {
  type CreditOffer,
  type OfferKind,
  type OffersPayload,
  deleteOffer,
  getOffers,
  grantOffer,
  newIdempotencyKey,
  saveOffer,
} from '@/lib/aiCreditTopup'
import { type MessageKey, useFormat, useT } from '@/i18n'

const fieldCls =
  'w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[13px] text-text-primary outline-none transition-colors placeholder:text-text-faint focus:border-brand-400'

const KIND_KEYS: Record<OfferKind, MessageKey> = {
  bonus_pct: 'billing.credit.offers.kinds.bonusPct',
  bonus_fixed: 'billing.credit.offers.kinds.bonusFixed',
  bundle: 'billing.credit.offers.kinds.bundle',
}

interface Draft {
  slug: string
  label: string
  kind: OfferKind
  bonus_pct: string
  bonus_fixed: string
  pay_amount: string
  credit_amount: string
  max_redemptions: string
  starts_at: string
  ends_at: string
  entity: string
  active: boolean
  note: string
}

const EMPTY: Draft = {
  slug: '',
  label: '',
  kind: 'bonus_pct',
  bonus_pct: '10',
  bonus_fixed: '',
  pay_amount: '',
  credit_amount: '',
  max_redemptions: '0',
  starts_at: '',
  ends_at: '',
  entity: '',
  active: true,
  note: '',
}

/** An ISO instant as the viewer's local calendar day (YYYY-MM-DD) for a date input. */
const day = (iso: string | null) => {
  if (!iso) return ''
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return ''
  const p = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`
}

/** A picked day as an instant in the viewer's time zone: the start of the day, or (end) its last second —
 *  so "Ends 30 Nov" still includes 30 November in Malaysia, not 00:00 UTC. */
const instant = (date: string, end = false) =>
  date ? new Date(`${date}T${end ? '23:59:59' : '00:00:00'}`).toISOString() : ''

function toDraft(o: CreditOffer): Draft {
  return {
    slug: o.slug,
    label: o.label,
    kind: o.kind,
    bonus_pct: String(o.bonus_pct || ''),
    bonus_fixed: String(o.bonus_fixed || ''),
    pay_amount: String(o.pay_amount || ''),
    credit_amount: String(o.credit_amount || ''),
    max_redemptions: String(o.max_redemptions),
    starts_at: day(o.starts_at),
    ends_at: day(o.ends_at),
    entity: o.entity_id ? String(o.entity_id) : '',
    active: o.active,
    note: o.note ?? '',
  }
}

const num = (s: string) => (s.trim() === '' ? 0 : Number(s))

export function AiCreditOffers() {
  const t = useT()
  const f = useFormat()
  const [data, setData] = useState<OffersPayload | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [draft, setDraft] = useState<Draft | null>(null)
  const [granting, setGranting] = useState<CreditOffer | null>(null)
  const [grantTo, setGrantTo] = useState('')
  const [grantBase, setGrantBase] = useState('')
  const [grantNote, setGrantNote] = useState('')

  const load = useCallback(async () => {
    try {
      setData(await getOffers())
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  const currency = data?.currency || 'USD'
  const money = (n: number) => f.currency(n, currency)
  const companies = (data?.companies ?? []).map((c) => ({ value: String(c.id), label: c.name }))
  const set = <K extends keyof Draft>(k: K, v: Draft[K]) => setDraft((d) => (d ? { ...d, [k]: v } : d))

  const run = async (work: () => Promise<string | void>) => {
    setBusy(true)
    setError(null)
    setNotice(null)
    try {
      const msg = await work()
      if (msg) setNotice(msg)
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  const save = () =>
    draft &&
    run(async () => {
      await saveOffer({
        ...(draft.slug ? { slug: draft.slug } : {}),
        label: draft.label.trim(),
        kind: draft.kind,
        bonus_pct: num(draft.bonus_pct),
        bonus_fixed: num(draft.bonus_fixed),
        pay_amount: num(draft.pay_amount),
        credit_amount: num(draft.credit_amount),
        max_redemptions: Math.max(0, Math.round(num(draft.max_redemptions))),
        starts_at: instant(draft.starts_at),
        ends_at: instant(draft.ends_at, true),
        entity: draft.entity,
        active: draft.active,
        note: draft.note,
      })
      setDraft(null)
      return t('billing.credit.offers.saved')
    })

  const toggle = (o: CreditOffer) => run(async () => void (await saveOffer({ slug: o.slug, active: !o.active })))

  const remove = (o: CreditOffer) => {
    if (!window.confirm(t('billing.credit.offers.confirmDelete', { label: o.label }))) return
    void run(async () => void (await deleteOffer(o.slug)))
  }

  const grant = () =>
    granting &&
    run(async () => {
      const res = await grantOffer(granting.slug, {
        entity: grantTo,
        ...(granting.kind === 'bonus_pct' ? { amount: num(grantBase) } : {}),
        note: grantNote.trim(),
        idempotency_key: newIdempotencyKey(),
      })
      setGranting(null)
      setGrantBase('')
      setGrantNote('')
      return t('billing.credit.offers.granted', {
        amount: money(res.entry.amount),
        name: res.name,
        balance: money(res.balance),
      })
    })

  return (
    <Card className="p-5" data-tour="billingset-offers">
      <CardTitle
        right={
          !draft ? (
            <Button size="sm" variant="secondary" onClick={() => setDraft({ ...EMPTY })}>
              <Plus size={13} aria-hidden /> {t('billing.credit.offers.new')}
            </Button>
          ) : undefined
        }
      >
        <span className="inline-flex items-center gap-2">
          <Gift size={15} aria-hidden /> {t('billing.credit.offers.title')}
        </span>
      </CardTitle>
      <p className="mt-1 text-[12.5px] text-text-muted">{t('billing.credit.offers.help')}</p>

      {!data && !error && (
        <div className="mt-3 flex items-center gap-2 text-[13px] text-text-muted">
          <Loader2 size={14} className="animate-spin" aria-hidden /> {t('common.status.loading')}
        </div>
      )}

      {draft && (
        <div className="mt-4 rounded-lg border border-line p-4">
          <div className="grid gap-3 sm:grid-cols-2">
            <label className="block">
              <span className="mb-1 block text-[12px] font-semibold text-text-high">{t('billing.credit.offers.label')}</span>
              <input value={draft.label} maxLength={120} onChange={(e) => set('label', e.target.value)} className={fieldCls} />
            </label>
            <label className="block">
              <span className="mb-1 block text-[12px] font-semibold text-text-high">{t('billing.credit.offers.kind')}</span>
              <Select
                value={draft.kind}
                onChange={(v) => set('kind', v as OfferKind)}
                options={(data?.kinds ?? (['bonus_pct', 'bonus_fixed', 'bundle'] as OfferKind[])).map((k) => ({
                  value: k,
                  label: t(KIND_KEYS[k]),
                }))}
                size="md"
                ariaLabel={t('billing.credit.offers.kind')}
              />
            </label>
            {draft.kind === 'bonus_pct' && (
              <label className="block">
                <span className="mb-1 block text-[12px] font-semibold text-text-high">{t('billing.credit.offers.bonusPct')}</span>
                <input type="number" step="0.1" min={0} value={draft.bonus_pct} onChange={(e) => set('bonus_pct', e.target.value)} className={fieldCls} />
              </label>
            )}
            {draft.kind === 'bonus_fixed' && (
              <label className="block">
                <span className="mb-1 block text-[12px] font-semibold text-text-high">
                  {t('billing.credit.offers.bonusFixed', { currency })}
                </span>
                <input type="number" step="0.01" min={0} value={draft.bonus_fixed} onChange={(e) => set('bonus_fixed', e.target.value)} className={fieldCls} />
              </label>
            )}
            <label className="block">
              <span className="mb-1 block text-[12px] font-semibold text-text-high">
                {t(draft.kind === 'bundle' ? 'billing.credit.offers.payAmount' : 'billing.credit.offers.minPurchase', { currency })}
              </span>
              <input type="number" step="0.01" min={0} value={draft.pay_amount} onChange={(e) => set('pay_amount', e.target.value)} className={fieldCls} />
            </label>
            {draft.kind === 'bundle' && (
              <label className="block">
                <span className="mb-1 block text-[12px] font-semibold text-text-high">
                  {t('billing.credit.offers.creditAmount', { currency })}
                </span>
                <input type="number" step="0.01" min={0} value={draft.credit_amount} onChange={(e) => set('credit_amount', e.target.value)} className={fieldCls} />
              </label>
            )}
            <label className="block">
              <span className="mb-1 block text-[12px] font-semibold text-text-high">{t('billing.credit.offers.maxRedemptions')}</span>
              <input type="number" step="1" min={0} value={draft.max_redemptions} onChange={(e) => set('max_redemptions', e.target.value)} className={fieldCls} />
            </label>
            <label className="block">
              <span className="mb-1 block text-[12px] font-semibold text-text-high">{t('billing.credit.offers.company')}</span>
              <Select
                value={draft.entity}
                onChange={(v) => set('entity', v)}
                options={[{ value: '', label: t('billing.credit.offers.everyCompany') }, ...companies]}
                size="md"
                ariaLabel={t('billing.credit.offers.company')}
              />
            </label>
            <label className="block">
              <span className="mb-1 block text-[12px] font-semibold text-text-high">{t('billing.credit.offers.startsAt')}</span>
              <input type="date" value={draft.starts_at} onChange={(e) => set('starts_at', e.target.value)} className={fieldCls} />
            </label>
            <label className="block">
              <span className="mb-1 block text-[12px] font-semibold text-text-high">{t('billing.credit.offers.endsAt')}</span>
              <input type="date" value={draft.ends_at} onChange={(e) => set('ends_at', e.target.value)} className={fieldCls} />
            </label>
            <label className="flex items-center gap-2 text-[12.5px] text-text-high">
              <input type="checkbox" checked={draft.active} onChange={(e) => set('active', e.target.checked)} />
              {t('billing.credit.offers.active')}
            </label>
          </div>
          <div className="mt-3 flex flex-wrap gap-2">
            <Button onClick={save} disabled={busy || !draft.label.trim()}>
              {busy && <Loader2 size={13} className="animate-spin" aria-hidden />}
              {t('billing.credit.offers.save')}
            </Button>
            <Button variant="ghost" onClick={() => setDraft(null)}>
              {t('billing.credit.offers.cancel')}
            </Button>
          </div>
        </div>
      )}

      {granting && (
        <div className="mt-4 rounded-lg border border-line p-4">
          <div className="text-[13px] font-semibold text-text-high">
            {t('billing.credit.offers.grantTitle')} — {granting.label}
          </div>
          <p className="mt-1 text-[12px] text-text-muted">{t('billing.credit.offers.grantHelp')}</p>
          <div className="mt-3 grid gap-3 sm:grid-cols-2">
            <Select
              value={grantTo}
              onChange={setGrantTo}
              options={companies.filter((c) => !granting.entity_id || c.value === String(granting.entity_id))}
              size="md"
              ariaLabel={t('billing.credit.offers.company')}
              placeholder={t('billing.credit.topup.pickCompany')}
            />
            {granting.kind === 'bonus_pct' && (
              <input
                type="number"
                step="0.01"
                min={0}
                value={grantBase}
                placeholder={t('billing.credit.offers.grantBase')}
                aria-label={t('billing.credit.offers.grantBase')}
                onChange={(e) => setGrantBase(e.target.value)}
                className={fieldCls}
              />
            )}
            <input
              value={grantNote}
              maxLength={300}
              placeholder={t('billing.credit.form.notePlaceholder')}
              onChange={(e) => setGrantNote(e.target.value)}
              className={`${fieldCls} sm:col-span-2`}
            />
          </div>
          <div className="mt-3 flex flex-wrap gap-2">
            <Button onClick={grant} disabled={busy || !grantTo}>
              {t('billing.credit.offers.grant')}
            </Button>
            <Button variant="ghost" onClick={() => setGranting(null)}>
              {t('billing.credit.offers.cancel')}
            </Button>
          </div>
        </div>
      )}

      {(error || notice) && (
        <p
          role={error ? 'alert' : 'status'}
          className={`mt-3 text-[12.5px] ${error ? 'text-[var(--red-ink)]' : 'text-[var(--green-ink)]'}`}
        >
          {error || notice}
        </p>
      )}

      {data && (
        <>
          {data.offers.length === 0 ? (
            <p className="mt-3 text-[13px] text-text-muted">{t('billing.credit.offers.none')}</p>
          ) : (
            <ul className="mt-4">
              {data.offers.map((o) => (
                <li key={o.slug} className="flex flex-wrap items-center gap-x-4 gap-y-2 border-b border-line py-2.5 last:border-b-0">
                  <div className="min-w-[200px] flex-1">
                    <div className="flex items-center gap-2">
                      <span className="text-[13px] font-semibold text-text-high">{o.label}</span>
                      <span className={`text-[11.5px] ${o.unavailable ? 'text-text-faint' : 'text-[var(--green-ink)]'}`}>
                        {o.active ? t('billing.credit.offers.active') : t('billing.credit.offers.inactive')}
                      </span>
                    </div>
                    <div className="text-[11.5px] text-text-muted">
                      {o.summary}
                      {' · '}
                      {o.max_redemptions > 0
                        ? t('billing.credit.offers.usedOf', { used: o.redemptions ?? 0, max: o.max_redemptions })
                        : t('billing.credit.offers.used', { used: o.redemptions ?? 0 })}
                      {o.company && ` · ${o.company}`}
                      {o.unavailable && o.active && ` · ${o.unavailable}`}
                    </div>
                  </div>
                  <div className="flex flex-wrap gap-1.5">
                    <Button size="sm" variant="secondary" disabled={busy} onClick={() => setDraft(toDraft(o))}>
                      {t('billing.credit.offers.edit')}
                    </Button>
                    <Button size="sm" variant="secondary" disabled={busy || !!o.unavailable} onClick={() => setGranting(o)}>
                      {t('billing.credit.offers.grant')}
                    </Button>
                    <Button size="sm" variant="ghost" disabled={busy} onClick={() => toggle(o)}>
                      {o.active ? t('billing.credit.offers.deactivate') : t('billing.credit.offers.activate')}
                    </Button>
                    {(o.redemptions ?? 0) === 0 && (
                      <Button size="sm" variant="danger" disabled={busy} onClick={() => remove(o)}>
                        {t('billing.credit.offers.delete')}
                      </Button>
                    )}
                  </div>
                </li>
              ))}
            </ul>
          )}
        </>
      )}
    </Card>
  )
}
