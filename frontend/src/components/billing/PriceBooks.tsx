'use client'

/**
 * Settings → AI billing → Price books (billing.manage, superadmin by default).
 *
 * The answer to "imagine I'm having 100 entities and thousand users, how can I
 * set it?": name a PLAN once (markup over provider cost, a flat monthly amount,
 * or a price per 1,000 tokens / per AI reply, plus a monthly fee and the AI
 * allowance each of its logins gets), then price as many companies as you like
 * with it in ONE call. A plan set on a main entity flows down to its branches.
 *
 * The company list is searchable, filterable and PAGINATED — it never renders a
 * hundred live inputs — and each row shows the plan that actually governs it,
 * whether that is inherited or set on the row, and this month's real requests,
 * tokens, provider cost and resulting bill. Where a number is still typed (a
 * per-company override) the bill it would produce is computed live from those
 * real figures, so nobody prices blind.
 *
 * GET/POST /api/billing/ai/price-books, DELETE …/{slug},
 * POST /api/bindings/pricebook-entities (the bulk assignment).
 */

import { useCallback, useEffect, useMemo, useState } from 'react'
import { ChevronLeft, ChevronRight, Loader2, Pencil, Plus, Save, Search, Tags, Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Card, CardTitle } from '@/components/ui/Card'
import { Modal } from '@/components/ui/Modal'
import { Select } from '@/components/ui/Select'
import { aiApi } from '@/lib/aiMonitor'
import {
  type PlanBody,
  type PriceBookPayload,
  type PriceMode,
  type PricePlan,
  type PriceRow,
  priceBooks,
  usageAmount,
} from '@/lib/priceBooks'
import { useFormat, useT } from '@/i18n'

const fieldCls =
  'w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[13px] text-text-primary outline-none transition-colors placeholder:text-text-faint focus:border-brand-400'

const PAGE_SIZE = 25

const MODES: PriceMode[] = ['markup', 'flat', 'unit']

/** The per-company override map, as GET/PUT /api/billing/ai/settings carries it. */
interface SettingsTerms {
  currency: string
  usd_rate: number
  default_markup_pct: number
  default_monthly_fee: number
  companies: Record<string, { markup_pct?: number | null; monthly_fee?: number | null }>
}

interface PlanDraft extends PlanBody {
  mode: PriceMode
  label: string
}

const emptyDraft = (): PlanDraft => ({
  label: '',
  slug: '',
  mode: 'markup',
  markup_pct: 0,
  flat_amount: 0,
  unit_per_1k_tokens: 0,
  unit_per_reply: 0,
  monthly_fee: 0,
  user_allowance: 0,
  active: true,
  is_default: false,
  note: '',
})

const draftOf = (p: PricePlan): PlanDraft => ({
  label: p.label,
  slug: p.slug,
  mode: p.mode,
  markup_pct: p.markup_pct,
  flat_amount: p.flat_amount,
  unit_per_1k_tokens: p.unit_per_1k_tokens,
  unit_per_reply: p.unit_per_reply,
  monthly_fee: p.monthly_fee,
  user_allowance: p.user_allowance,
  active: p.active,
  is_default: p.is_default,
  note: p.note,
})

/** "" ⇄ null for the optional override numbers. */
const num = (v: string): number | null => (v.trim() === '' ? null : Number(v))

export function PriceBooks() {
  const t = useT()
  const f = useFormat()

  const [data, setData] = useState<PriceBookPayload | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const [search, setSearch] = useState('')
  const [query, setQuery] = useState('')
  const [scope, setScope] = useState<'companies' | 'entities'>('companies')
  const [onlyOverrides, setOnlyOverrides] = useState(false)
  const [page, setPage] = useState(1)

  const [selected, setSelected] = useState<number[]>([])
  const [assignTo, setAssignTo] = useState('')
  const [draft, setDraft] = useState<PlanDraft | null>(null)
  const [editingSlug, setEditingSlug] = useState<string | null>(null)
  const [override, setOverride] = useState<PriceRow | null>(null)
  const [overrideMarkup, setOverrideMarkup] = useState('')
  const [overrideFee, setOverrideFee] = useState('')

  // the search box types; the server is asked 300 ms later
  useEffect(() => {
    const id = setTimeout(() => {
      setQuery(search.trim())
      setPage(1)
    }, 300)
    return () => clearTimeout(id)
  }, [search])

  const load = useCallback(async () => {
    try {
      setData(await priceBooks.list({ q: query, scope, page, page_size: PAGE_SIZE, overrides_only: onlyOverrides }))
      setError(null)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }, [query, scope, page, onlyOverrides])

  useEffect(() => {
    void load()
  }, [load])

  const currency = data?.currency ?? 'USD'
  const money = (n: number) => f.currency(n, currency, Math.abs(n) > 0 && Math.abs(n) < 1 ? 4 : 2)

  const planOptions = useMemo(
    () =>
      (data?.plans ?? [])
        .filter((p) => p.active)
        .map((p) => ({ value: String(p.id), label: p.label, hint: p.summary })),
    [data],
  )

  if (!data) {
    return error ? (
      <p role="alert" className="text-[13px] text-[var(--red-ink)]">{error}</p>
    ) : (
      <div className="flex items-center gap-2 text-[13px] text-text-muted">
        <Loader2 size={14} className="animate-spin" aria-hidden /> {t('common.status.loading')}
      </div>
    )
  }

  const run = async (act: () => Promise<string | null>) => {
    setBusy(true)
    setError(null)
    setNotice(null)
    try {
      const said = await act()
      await load()
      if (said) setNotice(said)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  const savePlan = () =>
    run(async () => {
      if (!draft) return null
      const body: PlanBody = { ...draft, slug: editingSlug ?? draft.slug ?? '' }
      await priceBooks.save(body)
      setDraft(null)
      setEditingSlug(null)
      return t('common.status.saved')
    })

  const deletePlan = (plan: PricePlan) =>
    run(async () => {
      if (!window.confirm(t('billing.plans.deleteConfirm', { name: plan.label }))) return null
      await priceBooks.remove(plan.slug)
      return t('common.status.saved')
    })

  const applyAssignment = (remove: boolean) =>
    run(async () => {
      if (selected.length === 0) return null
      const planId = Number(assignTo)
      if (!remove && !planId) return null
      if (remove) {
        // every selected row may sit on a different plan: unbind each from its own
        const byPlan = new Map<number, number[]>()
        for (const id of selected) {
          const row = data.rows.find((r) => r.id === id)
          const plan = data.plans.find((p) => p.slug === row?.plan)
          if (!row?.assigned_here || !plan) continue
          byPlan.set(plan.id, [...(byPlan.get(plan.id) ?? []), id])
        }
        let removed = 0
        for (const [planId2, ids] of byPlan) removed += (await priceBooks.assign(planId2, [], ids)).removed
        setSelected([])
        return t('billing.plans.assigned', { added: 0, removed })
      }
      const res = await priceBooks.assign(planId, selected)
      setSelected([])
      return t('billing.plans.assigned', { added: res.added, removed: res.removed })
    })

  const openOverride = (row: PriceRow) => {
    setOverride(row)
    setOverrideMarkup(row.override?.markup_pct == null ? '' : String(row.override.markup_pct))
    setOverrideFee(row.override?.monthly_fee == null ? '' : String(row.override.monthly_fee))
  }

  const saveOverride = () =>
    run(async () => {
      if (!override) return null
      // the override map is organisation-wide, so merge into the stored terms
      const current = await aiApi<{ settings: SettingsTerms }>('billing/ai/settings')
      const terms = current.settings
      const key = String(override.company_id)
      const markup = num(overrideMarkup)
      const fee = num(overrideFee)
      const companies = { ...terms.companies }
      if (markup == null && fee == null) delete companies[key]
      else companies[key] = { markup_pct: markup, monthly_fee: fee }
      await aiApi('billing/ai/settings', {
        method: 'PUT',
        body: JSON.stringify({ ...terms, companies }),
      })
      setOverride(null)
      return t('common.status.saved')
    })

  const sourceLabel = (row: PriceRow) =>
    row.plan_source === 'company_override'
      ? t('billing.plans.fromOverride')
      : row.plan_source === 'org_default_plan'
        ? t('billing.plans.fromDefaultPlan')
        : row.plan_source === 'builtin'
          ? t('billing.plans.fromBuiltin')
          : row.inherited && row.assigned_at_name
            ? t('billing.plans.inherited', { name: row.assigned_at_name })
            : t('billing.plans.setHere')

  const priceLine = (row: PriceRow) =>
    row.mode === 'flat'
      ? `${money(row.monthly_fee)}${t('billing.plans.perMonth')}`
      : row.mode === 'unit'
        ? [
            row.unit_per_1k_tokens > 0 ? `${money(row.unit_per_1k_tokens)} / 1k` : null,
            row.unit_per_reply > 0 ? `${money(row.unit_per_reply)} / ✉` : null,
          ]
            .filter(Boolean)
            .join(' + ')
        : `${f.number(row.markup_pct)}%`

  const pages = Math.max(1, Math.ceil(data.total / data.page_size))
  const from = data.total === 0 ? 0 : (data.page - 1) * data.page_size + 1
  const to = Math.min(data.total, data.page * data.page_size)
  const pageIds = data.rows.map((r) => r.id)
  const allOnPage = pageIds.length > 0 && pageIds.every((id) => selected.includes(id))

  // the live preview under the override inputs, from this company's REAL month
  const previewCost = override?.usage?.cost_usd ?? 0
  const previewMarkup = num(overrideMarkup)
  const previewFee = num(overrideFee)
  const previewTotal =
    (previewMarkup == null
      ? (override?.usage?.usage_amount ?? 0)
      : previewCost * (1 + previewMarkup / 100) * data.usd_rate) +
    (previewFee == null ? (override?.usage?.monthly_fee ?? 0) : previewFee)

  return (
    <div className="flex flex-col gap-4">
      {/* ---- the plans themselves ------------------------------------------------ */}
      <Card className="p-5" data-tour="billingset-plans">
        <CardTitle
          right={
            <Button size="sm" variant="secondary" onClick={() => { setDraft(emptyDraft()); setEditingSlug(null) }}>
              <Plus size={13} aria-hidden /> {t('billing.plans.newPlan')}
            </Button>
          }
        >
          {t('billing.plans.title')}
        </CardTitle>
        <p className="mt-1 text-[12.5px] text-text-muted">{t('billing.plans.help')}</p>
        {data.plans.length === 0 ? (
          <p className="mt-3 text-[13px] text-text-muted">{t('billing.plans.noPlans')}</p>
        ) : (
          <ul className="mt-3 flex flex-col gap-2">
            {data.plans.map((p) => (
              <li
                key={p.slug}
                className="flex flex-wrap items-center gap-x-3 gap-y-1 rounded-lg border border-line px-3 py-2 text-[13px]"
              >
                <span className="font-semibold text-text-high">{p.label}</span>
                <span className="text-text-mid">{p.summary}</span>
                {p.is_default && (
                  <span className="rounded bg-fill-subtle px-1.5 py-0.5 text-[11px] font-semibold text-text-mid">
                    {t('billing.plans.defaultBadge')}
                  </span>
                )}
                {!p.active && (
                  <span className="rounded bg-fill-subtle px-1.5 py-0.5 text-[11px] font-semibold text-text-mid">
                    {t('billing.plans.inactiveBadge')}
                  </span>
                )}
                <span className="text-[12px] text-text-muted">
                  {t('billing.plans.pricesEntities', { count: p.entities })}
                </span>
                <span className="ml-auto flex items-center gap-1">
                  <Button
                    size="sm"
                    variant="ghost"
                    onClick={() => { setDraft(draftOf(p)); setEditingSlug(p.slug) }}
                    aria-label={`${t('billing.plans.editPlan')} — ${p.label}`}
                  >
                    <Pencil size={13} aria-hidden /> {t('billing.plans.editPlan')}
                  </Button>
                  <Button
                    size="sm"
                    variant="ghost"
                    disabled={busy}
                    onClick={() => void deletePlan(p)}
                    aria-label={`${t('billing.plans.deletePlan')} — ${p.label}`}
                  >
                    <Trash2 size={13} aria-hidden />
                  </Button>
                </span>
              </li>
            ))}
          </ul>
        )}
      </Card>

      {/* ---- who is on which plan ------------------------------------------------- */}
      <Card className="p-5" data-tour="billingset-companies">
        <CardTitle>{t('billing.plans.companiesTitle')}</CardTitle>
        <div className="mt-3 flex flex-wrap items-center gap-2">
          <label className="relative flex-1 min-w-[180px]">
            <Search size={13} className="pointer-events-none absolute left-2.5 top-1/2 -translate-y-1/2 text-text-faint" aria-hidden />
            <input
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder={t('billing.plans.search')}
              aria-label={t('billing.plans.search')}
              className={`${fieldCls} pl-8`}
            />
          </label>
          <Select
            value={scope}
            onChange={(v) => { setScope(v === 'entities' ? 'entities' : 'companies'); setPage(1) }}
            ariaLabel={t('billing.plans.scopeCompanies')}
            options={[
              { value: 'companies', label: t('billing.plans.scopeCompanies') },
              { value: 'entities', label: t('billing.plans.scopeEntities') },
            ]}
          />
          <label className="flex items-center gap-1.5 text-[12.5px] text-text-mid">
            <input
              type="checkbox"
              checked={onlyOverrides}
              onChange={(e) => { setOnlyOverrides(e.target.checked); setPage(1) }}
            />
            {t('billing.plans.onlyOverrides')}
          </label>
        </div>

        {/* bulk assignment: one call for every ticked row */}
        <div className="mt-3 flex flex-wrap items-center gap-2 rounded-lg bg-fill-subtle px-3 py-2">
          <label className="flex items-center gap-1.5 text-[12.5px] text-text-mid">
            <input
              type="checkbox"
              checked={allOnPage}
              onChange={(e) =>
                setSelected(e.target.checked
                  ? Array.from(new Set([...selected, ...pageIds]))
                  : selected.filter((id) => !pageIds.includes(id)))
              }
            />
            {t('billing.plans.selectAll')}
          </label>
          <span className="text-[12.5px] font-semibold text-text-high">
            {t('billing.plans.selected', { count: selected.length })}
          </span>
          <span className="ml-auto flex flex-wrap items-center gap-2">
            <Select
              value={assignTo}
              onChange={setAssignTo}
              options={planOptions}
              placeholder={t('billing.plans.pickPlan')}
              ariaLabel={t('billing.plans.assignTo')}
            />
            <Button size="sm" disabled={busy || selected.length === 0 || !assignTo} onClick={() => void applyAssignment(false)}>
              <Tags size={13} aria-hidden /> {t('billing.plans.assign')}
            </Button>
            <Button size="sm" variant="secondary" disabled={busy || selected.length === 0} onClick={() => void applyAssignment(true)}>
              {t('billing.plans.unassign')}
            </Button>
          </span>
        </div>

        {data.rows.length === 0 ? (
          <p className="mt-3 text-[13px] text-text-muted">{t('billing.plans.noRows')}</p>
        ) : (
          <div className="mt-3 overflow-x-auto">
            <table className="w-full min-w-[640px] text-[13px]">
              <thead>
                <tr className="border-b border-line text-left text-[11.5px] uppercase tracking-[0.05em] text-text-muted">
                  <th className="py-2 pr-2 font-semibold" />
                  <th className="py-2 pr-3 font-semibold">{t('billing.plans.colCompany')}</th>
                  <th className="py-2 pr-3 font-semibold">{t('billing.plans.colPlan')}</th>
                  <th className="py-2 pr-3 font-semibold">{t('billing.plans.colThisMonth')}</th>
                  <th className="py-2 font-semibold">{t('billing.plans.colBill')}</th>
                </tr>
              </thead>
              <tbody>
                {data.rows.map((row) => (
                  <tr key={row.id} className="border-b border-line align-top last:border-b-0">
                    <td className="py-2 pr-2">
                      <input
                        type="checkbox"
                        checked={selected.includes(row.id)}
                        onChange={(e) =>
                          setSelected(e.target.checked
                            ? [...selected, row.id]
                            : selected.filter((id) => id !== row.id))
                        }
                        aria-label={row.name}
                      />
                    </td>
                    <td className="py-2 pr-3" style={{ paddingLeft: row.depth * 14 }}>
                      <span className="font-semibold text-text-high">{row.name}</span>
                      <span className="block text-[11.5px] text-text-faint">{row.slug}</span>
                    </td>
                    <td className="py-2 pr-3">
                      <span className="font-semibold text-text-high">
                        {row.plan_label ?? t('billing.plans.noPlanYet')}
                      </span>
                      <span className="block text-[11.5px] text-text-muted">
                        {t(`billing.plans.mode${row.mode === 'flat' ? 'Flat' : row.mode === 'unit' ? 'Unit' : 'Markup'}`)}
                        {' · '}
                        {priceLine(row)}
                      </span>
                      <span className="block text-[11.5px] text-text-faint">{sourceLabel(row)}</span>
                    </td>
                    <td className="py-2 pr-3 text-text-mid">
                      {row.usage && row.usage.requests > 0
                        ? t('billing.plans.usageLine', {
                            requests: f.number(row.usage.requests),
                            tokens: f.number(row.usage.tokens),
                            cost: `USD ${f.number(row.usage.cost_usd ?? 0, { maximumFractionDigits: 4 })}`,
                          })
                        : t('billing.plans.noUsage')}
                    </td>
                    <td className="py-2">
                      <span className="font-semibold text-text-high">{row.usage ? money(row.usage.total) : '—'}</span>
                      {row.company && (
                        <Button
                          size="sm"
                          variant="ghost"
                          className="mt-1 block"
                          onClick={() => openOverride(row)}
                        >
                          {row.override ? t('billing.plans.fromOverride') : t('billing.plans.overrideMarkup')}
                        </Button>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}

        <div className="mt-3 flex items-center gap-2 text-[12.5px] text-text-muted">
          <span>{t('billing.plans.showing', { from, to, total: data.total })}</span>
          <span className="ml-auto flex items-center gap-1">
            <Button
              size="sm"
              variant="ghost"
              disabled={data.page <= 1}
              onClick={() => setPage(data.page - 1)}
              aria-label={t('billing.plans.prev')}
            >
              <ChevronLeft size={14} aria-hidden />
            </Button>
            <Button
              size="sm"
              variant="ghost"
              disabled={data.page >= pages}
              onClick={() => setPage(data.page + 1)}
              aria-label={t('billing.plans.next')}
            >
              <ChevronRight size={14} aria-hidden />
            </Button>
          </span>
        </div>

        {error && <p role="alert" className="mt-2 text-[12.5px] text-[var(--red-ink)]">{error}</p>}
        {notice && <p role="status" className="mt-2 text-[12.5px] text-[var(--green-ink)]">{notice}</p>}
      </Card>

      {/* ---- the plan editor ------------------------------------------------------ */}
      {draft && (
        <Modal
          open
          onClose={() => { setDraft(null); setEditingSlug(null) }}
          title={editingSlug ? t('billing.plans.editPlan') : t('billing.plans.newPlan')}
          footer={
            <div className="flex items-center gap-2">
              <Button disabled={busy} onClick={() => void savePlan()}>
                {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Save size={13} aria-hidden />}
                {t('billing.plans.savePlan')}
              </Button>
            </div>
          }
        >
          <div className="flex flex-col gap-3">
            <label className="block">
              <span className="mb-1 block text-[12px] font-semibold text-text-high">{t('billing.plans.planName')}</span>
              <input value={draft.label} onChange={(e) => setDraft({ ...draft, label: e.target.value })} className={fieldCls} />
            </label>
            {!editingSlug && (
              <label className="block">
                <span className="mb-1 block text-[12px] font-semibold text-text-high">{t('billing.plans.planKey')}</span>
                <input value={draft.slug ?? ''} onChange={(e) => setDraft({ ...draft, slug: e.target.value })} className={fieldCls} />
                <span className="mt-1 block text-[11.5px] text-text-muted">{t('billing.plans.planKeyHelp')}</span>
              </label>
            )}
            <label className="block">
              <span className="mb-1 block text-[12px] font-semibold text-text-high">{t('billing.plans.mode')}</span>
              <Select
                value={draft.mode}
                onChange={(v) => setDraft({ ...draft, mode: (MODES.includes(v as PriceMode) ? v : 'markup') as PriceMode })}
                size="md"
                ariaLabel={t('billing.plans.mode')}
                options={[
                  { value: 'markup', label: t('billing.plans.modeMarkup') },
                  { value: 'flat', label: t('billing.plans.modeFlat') },
                  { value: 'unit', label: t('billing.plans.modeUnit') },
                ]}
              />
              <span className="mt-1 block text-[11.5px] text-text-muted">
                {draft.mode === 'flat'
                  ? t('billing.plans.modeFlatHelp')
                  : draft.mode === 'unit'
                    ? t('billing.plans.modeUnitHelp')
                    : t('billing.plans.modeMarkupHelp')}
              </span>
            </label>

            {draft.mode === 'markup' && (
              <label className="block">
                <span className="mb-1 block text-[12px] font-semibold text-text-high">{t('billing.plans.markupPct')}</span>
                {/* decimals allowed: 12.5% is a real markup */}
                <input
                  type="number"
                  min={0}
                  step="0.01"
                  value={draft.markup_pct ?? 0}
                  onChange={(e) => setDraft({ ...draft, markup_pct: Number(e.target.value) })}
                  className={fieldCls}
                />
              </label>
            )}
            {draft.mode === 'flat' && (
              <label className="block">
                <span className="mb-1 block text-[12px] font-semibold text-text-high">
                  {t('billing.plans.flatAmount', { currency })}
                </span>
                <input
                  type="number"
                  min={0}
                  step="0.01"
                  value={draft.flat_amount ?? 0}
                  onChange={(e) => setDraft({ ...draft, flat_amount: Number(e.target.value) })}
                  className={fieldCls}
                />
              </label>
            )}
            {draft.mode === 'unit' && (
              <div className="grid gap-3 sm:grid-cols-2">
                <label className="block">
                  <span className="mb-1 block text-[12px] font-semibold text-text-high">
                    {t('billing.plans.perThousandTokens', { currency })}
                  </span>
                  <input
                    type="number"
                    min={0}
                    step="0.0001"
                    value={draft.unit_per_1k_tokens ?? 0}
                    onChange={(e) => setDraft({ ...draft, unit_per_1k_tokens: Number(e.target.value) })}
                    className={fieldCls}
                  />
                </label>
                <label className="block">
                  <span className="mb-1 block text-[12px] font-semibold text-text-high">
                    {t('billing.plans.perReply', { currency })}
                  </span>
                  <input
                    type="number"
                    min={0}
                    step="0.0001"
                    value={draft.unit_per_reply ?? 0}
                    onChange={(e) => setDraft({ ...draft, unit_per_reply: Number(e.target.value) })}
                    className={fieldCls}
                  />
                </label>
              </div>
            )}

            <div className="grid gap-3 sm:grid-cols-2">
              <label className="block">
                <span className="mb-1 block text-[12px] font-semibold text-text-high">
                  {t('billing.plans.monthlyFee', { currency })}
                </span>
                <input
                  type="number"
                  min={0}
                  step="0.01"
                  value={draft.monthly_fee ?? 0}
                  onChange={(e) => setDraft({ ...draft, monthly_fee: Number(e.target.value) })}
                  className={fieldCls}
                />
              </label>
              <label className="block">
                <span className="mb-1 block text-[12px] font-semibold text-text-high">
                  {t('billing.plans.userAllowance', { currency })}
                </span>
                <input
                  type="number"
                  min={0}
                  step="0.01"
                  value={draft.user_allowance ?? 0}
                  onChange={(e) => setDraft({ ...draft, user_allowance: Number(e.target.value) })}
                  className={fieldCls}
                />
                <span className="mt-1 block text-[11.5px] text-text-muted">{t('billing.plans.userAllowanceHelp')}</span>
              </label>
            </div>

            <label className="flex items-center gap-2 text-[12.5px] text-text-mid">
              <input
                type="checkbox"
                checked={draft.active !== false}
                onChange={(e) => setDraft({ ...draft, active: e.target.checked })}
              />
              {t('billing.plans.active')}
            </label>
            <label className="flex items-center gap-2 text-[12.5px] text-text-mid">
              <input
                type="checkbox"
                checked={draft.is_default === true}
                onChange={(e) => setDraft({ ...draft, is_default: e.target.checked })}
              />
              {t('billing.plans.isDefault')}
            </label>
            <label className="block">
              <span className="mb-1 block text-[12px] font-semibold text-text-high">{t('billing.plans.note')}</span>
              <input value={draft.note ?? ''} onChange={(e) => setDraft({ ...draft, note: e.target.value })} className={fieldCls} />
            </label>
          </div>
        </Modal>
      )}

      {/* ---- the per-company override, with the bill it would produce ------------- */}
      {override && (
        <Modal
          open
          onClose={() => setOverride(null)}
          title={t('billing.plans.overrideTitle', { name: override.name })}
          footer={
            <div className="flex items-center gap-2">
              <Button disabled={busy} onClick={() => void saveOverride()}>
                {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Save size={13} aria-hidden />}
                {t('billing.plans.saveOverride')}
              </Button>
              <Button
                variant="secondary"
                disabled={busy}
                onClick={() => { setOverrideMarkup(''); setOverrideFee('') }}
              >
                {t('billing.plans.overrideClear')}
              </Button>
            </div>
          }
        >
          <div className="flex flex-col gap-3">
            <p className="text-[12.5px] text-text-muted">{t('billing.plans.overrideHelp')}</p>
            <p className="text-[12.5px] text-text-mid">
              {t('billing.plans.planOf', { name: override.plan_label ?? t('billing.plans.noPlanYet') })}
            </p>
            <div className="grid gap-3 sm:grid-cols-2">
              <label className="block">
                <span className="mb-1 block text-[12px] font-semibold text-text-high">{t('billing.plans.overrideMarkup')}</span>
                <input
                  type="number"
                  min={0}
                  step="0.01"
                  value={overrideMarkup}
                  onChange={(e) => setOverrideMarkup(e.target.value)}
                  placeholder={`${f.number(override.markup_pct)}`}
                  className={fieldCls}
                />
              </label>
              <label className="block">
                <span className="mb-1 block text-[12px] font-semibold text-text-high">
                  {t('billing.plans.overrideFee', { currency })}
                </span>
                <input
                  type="number"
                  min={0}
                  step="0.01"
                  value={overrideFee}
                  onChange={(e) => setOverrideFee(e.target.value)}
                  placeholder={`${f.number(override.monthly_fee)}`}
                  className={fieldCls}
                />
              </label>
            </div>
            {/* live, from this company's real month — never a guess */}
            <p className="rounded-lg bg-fill-subtle px-3 py-2 text-[12.5px] text-text-mid">
              {t('billing.plans.usageLine', {
                requests: f.number(override.usage?.requests ?? 0),
                tokens: f.number(override.usage?.tokens ?? 0),
                cost: `USD ${f.number(previewCost, { maximumFractionDigits: 4 })}`,
              })}
              <span className="mt-1 block font-semibold text-text-high">
                {t('billing.plans.overridePreview', {
                  cost: `USD ${f.number(previewCost, { maximumFractionDigits: 4 })}`,
                  total: money(previewTotal),
                })}
              </span>
            </p>
          </div>
        </Modal>
      )}
    </div>
  )
}
