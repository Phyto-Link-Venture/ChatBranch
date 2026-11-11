'use client'

/**
 * Settings → AI billing: hand prepaid AI credit to a company, or correct a
 * balance (billing.manage, superadmin by default).
 * POST /api/billing/ai/credit/grant | /adjust, both of which append ONE
 * immutable ledger line — a correction is a new signed line, never an edit.
 *
 * Credit is held per company (a top-level entity); a branch draws on its
 * company's balance. The optional per-login balance covers only what that
 * person runs themselves (Ask ChatBranch, the AI playground, skill drafting,
 * on-demand summaries): automated AI work carries no username.
 */

import Link from 'next/link'
import { useCallback, useEffect, useState } from 'react'
import { ArrowUpRight, Loader2, Wallet } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Card, CardTitle } from '@/components/ui/Card'
import { Select } from '@/components/ui/Select'
import {
  type CreditPayload,
  creditDigits,
  creditKindKey,
  getCredit,
  writeCredit,
} from '@/lib/aiCredit'
import { useFormat, useT } from '@/i18n'

const fieldCls =
  'w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[13px] text-text-primary outline-none transition-colors placeholder:text-text-faint focus:border-brand-400'

type Mode = 'grant' | 'adjust'

export function AiCreditGrants() {
  const t = useT()
  const f = useFormat()
  const [data, setData] = useState<CreditPayload | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [mode, setMode] = useState<Mode>('grant')
  const [target, setTarget] = useState('')
  const [amount, setAmount] = useState('')
  const [note, setNote] = useState('')

  const load = useCallback(async () => {
    try {
      const d = await getCredit('?page_size=20')
      setData(d)
      setTarget((cur) => cur || (d.companies[0] ? `entity:${d.companies[0].slug || d.companies[0].id}` : ''))
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  const currency = data?.currency || 'USD'
  const money = (n: number) => f.currency(n, currency, creditDigits(n))

  const options = [
    ...(data?.companies ?? []).map((c) => ({
      value: `entity:${c.slug || c.id}`,
      label: c.name,
      hint: c.credit.opened ? money(c.credit.balance) : t('billing.credit.none'),
      group: t('billing.credit.companies'),
    })),
    ...(data?.users ?? []).map((u) => ({
      value: `user:${u.user_name}`,
      label: u.user_name,
      hint: money(u.balance),
      group: t('billing.credit.users'),
    })),
  ]

  const submit = async () => {
    const value = Number(amount.trim())
    if (!Number.isFinite(value) || value === 0) {
      setError(t('billing.credit.form.invalid'))
      return
    }
    if (mode === 'grant' && value < 0) {
      setError(t('billing.credit.form.grantPositive'))
      return
    }
    const [kind, key] = target.split(/:(.*)/s)
    if (!key) {
      setError(t('billing.credit.form.pickTarget'))
      return
    }
    setBusy(true)
    setError(null)
    setNotice(null)
    try {
      const res = await writeCredit(mode, {
        ...(kind === 'user' ? { user: key } : { entity: key }),
        amount: value,
        note: note.trim(),
      })
      setNotice(
        t('billing.credit.form.done', {
          amount: money(Math.abs(res.entry.amount)),
          name: res.name,
          balance: money(res.balance),
        }),
      )
      setAmount('')
      setNote('')
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Card className="p-5" data-tour="billingset-credit">
      <CardTitle
        right={
          <Link href="/billing" className="inline-flex items-center gap-1 font-semibold text-[var(--brand-ink)] hover:underline">
            {t('billing.credit.openBalances')} <ArrowUpRight size={12} aria-hidden />
          </Link>
        }
      >
        <span className="inline-flex items-center gap-2">
          <Wallet size={15} aria-hidden /> {t('billing.credit.form.title')}
        </span>
      </CardTitle>
      <p className="mt-1 text-[12.5px] text-text-muted">{t('billing.credit.form.help')}</p>
      <p className="mt-2 rounded-lg border border-line px-3 py-2 text-[12.5px] font-semibold text-text-high">
        {t('billing.credit.phaseNote')}
      </p>

      {!data && !error && (
        <div className="mt-3 flex items-center gap-2 text-[13px] text-text-muted">
          <Loader2 size={14} className="animate-spin" aria-hidden /> {t('common.status.loading')}
        </div>
      )}

      {data && (
        <>
          <div className="mt-4 flex gap-2">
            {(['grant', 'adjust'] as Mode[]).map((m) => (
              <Button key={m} size="sm" variant={mode === m ? 'primary' : 'secondary'} onClick={() => setMode(m)}>
                {t(m === 'grant' ? 'billing.credit.form.modeGrant' : 'billing.credit.form.modeAdjust')}
              </Button>
            ))}
          </div>

          <div className="mt-3 grid gap-3 sm:grid-cols-2">
            <label className="block">
              <span className="mb-1 block text-[12px] font-semibold text-text-high">{t('billing.credit.form.target')}</span>
              <Select
                value={target}
                onChange={setTarget}
                options={options}
                size="md"
                ariaLabel={t('billing.credit.form.target')}
                placeholder={t('billing.credit.form.pickTarget')}
              />
            </label>
            <label className="block">
              <span className="mb-1 block text-[12px] font-semibold text-text-high">
                {t('billing.credit.form.amount', { currency })}
              </span>
              <input
                type="number"
                step="0.01"
                min={mode === 'grant' ? 0.01 : undefined}
                value={amount}
                placeholder={mode === 'grant' ? '100' : '-25'}
                onChange={(e) => setAmount(e.target.value)}
                className={fieldCls}
              />
            </label>
            <label className="block sm:col-span-2">
              <span className="mb-1 block text-[12px] font-semibold text-text-high">{t('billing.credit.form.note')}</span>
              <input
                value={note}
                maxLength={300}
                placeholder={t('billing.credit.form.notePlaceholder')}
                onChange={(e) => setNote(e.target.value)}
                className={fieldCls}
              />
            </label>
          </div>

          <p className="mt-2 text-[12px] text-text-muted">
            {t(mode === 'grant' ? 'billing.credit.form.grantHelp' : 'billing.credit.form.adjustHelp')}
          </p>

          <div className="mt-3 flex flex-wrap items-center gap-3">
            <Button onClick={submit} disabled={busy || options.length === 0}>
              {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Wallet size={13} aria-hidden />}
              {t(mode === 'grant' ? 'billing.credit.form.submitGrant' : 'billing.credit.form.submitAdjust')}
            </Button>
            {error && (
              <span role="alert" className="text-[12.5px] text-[var(--red-ink)]">
                {error}
              </span>
            )}
            {notice && (
              <span role="status" className="text-[12.5px] text-[var(--green-ink)]">
                {notice}
              </span>
            )}
          </div>

          {data.ledger.length > 0 && (
            <>
              <div className="eyebrow mt-5">{t('billing.credit.ledger')}</div>
              <ul className="mt-1 flex flex-col gap-1">
                {data.ledger.map((l) => (
                  <li key={l.id} className="flex flex-wrap items-baseline gap-x-2 text-[12.5px] text-text-mid">
                    <span className="tabular-nums text-text-muted">{f.dateTime(l.created_at)}</span>
                    <span
                      className={`font-semibold tabular-nums ${l.amount < 0 ? 'text-[var(--red-ink)]' : 'text-[var(--green-ink)]'}`}
                    >
                      {l.amount < 0 ? '−' : '+'}
                      {money(Math.abs(l.amount))}
                    </span>
                    <span className="text-text-high">{l.company || l.user_name}</span>
                    <span className="text-text-muted">{t(creditKindKey(l.kind))}</span>
                    {l.note && <span className="text-text-muted">· {l.note}</span>}
                    <span className="text-text-faint">
                      · {l.actor} · {t('billing.credit.form.balanceAfter', { balance: money(l.balance_after) })}
                    </span>
                  </li>
                ))}
              </ul>
            </>
          )}
        </>
      )}
    </Card>
  )
}
