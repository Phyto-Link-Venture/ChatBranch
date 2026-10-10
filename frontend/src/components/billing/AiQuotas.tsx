'use client'

/**
 * AI usage quotas — monthly spending caps in the billing currency, priced like
 * the bill (GET /api/billing/ai/quotas with billing.view or billing.manage; PUT
 * with billing.quotas.manage or billing.manage — the row's `editable` says which
 * caps this person may change: never their own, never one a manager set).
 * Companies → branches, then people, each with a progress bar: amber from
 * 80 %, red + "Paused" at 100 % (the AI stops answering those chats — or that
 * person's AI features — until next month or until the cap is raised).
 * Billing managers set any cap; admins cap branches and people in their scope.
 */

import { useCallback, useEffect, useState } from 'react'
import { Gauge, Loader2, Pencil } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Card, CardTitle } from '@/components/ui/Card'
import { Modal } from '@/components/ui/Modal'
import { aiApi, ApiError } from '@/lib/aiMonitor'
import { useFormat, useT } from '@/i18n'

type QuotaState = 'ok' | 'warn' | 'paused'

interface EntityQuota {
  id: number
  name: string
  slug: string
  parent_id: number | null
  company: boolean
  depth: number
  cap: number | null
  spent: number
  pct: number
  state: QuotaState
  editable: boolean
}

interface UserQuota {
  username: string
  role: string
  cap: number | null
  spent: number
  pct: number
  state: QuotaState
  editable: boolean
}

interface Payload {
  month: string
  currency: string
  is_manager: boolean
  can_edit: boolean
  entities: EntityQuota[]
  users: UserQuota[]
  counts: { paused: number; warn: number }
}

type Target = { kind: 'entity'; key: string; name: string; cap: number | null } | { kind: 'user'; key: string; name: string; cap: number | null }

const fieldCls =
  'w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[13px] text-text-primary outline-none transition-colors placeholder:text-text-faint focus:border-brand-400'

const barCls: Record<QuotaState, string> = {
  ok: 'bg-brand-400',
  warn: 'bg-amber-400',
  paused: 'bg-red-400',
}

export function AiQuotas() {
  const t = useT()
  const f = useFormat()
  const [data, setData] = useState<Payload | null>(null)
  const [hidden, setHidden] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [showAll, setShowAll] = useState(false)
  const [editing, setEditing] = useState<Target | null>(null)
  const [amount, setAmount] = useState('')
  const [modalError, setModalError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const load = useCallback(async () => {
    try {
      setData(await aiApi<Payload>('billing/ai/quotas'))
      setError(null)
    } catch (e) {
      if (e instanceof ApiError && e.status === 403) setHidden(true)
      else setError(e instanceof Error ? e.message : String(e))
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  if (hidden) return null

  const digits = (n: number) => (n !== 0 && Math.abs(n) < 1 ? 4 : undefined)
  const money = (n: number) => f.currency(n, data?.currency || 'USD', digits(n))

  const open = (target: Target) => {
    setEditing(target)
    setAmount(target.cap != null ? String(target.cap) : '')
    setModalError(null)
  }

  const save = async (clear = false) => {
    if (!editing) return
    const raw = clear ? '' : amount.trim()
    const value = raw === '' ? null : Number(raw)
    if (value != null && (!Number.isFinite(value) || value < 0)) {
      setModalError(t('billing.quotas.invalid'))
      return
    }
    setBusy(true)
    setModalError(null)
    try {
      const body = editing.kind === 'entity' ? { entities: { [editing.key]: value } } : { users: { [editing.key]: value } }
      await aiApi('billing/ai/quotas', { method: 'PUT', body: JSON.stringify(body) })
      setEditing(null)
      await load()
    } catch (e) {
      setModalError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  // simple first: rows with a cap; the rest on click
  const entities = data ? data.entities.filter((e) => showAll || e.cap != null) : []
  const users = data ? data.users.filter((u) => showAll || u.cap != null) : []
  const capped = data ? data.entities.filter((e) => e.cap != null).length + data.users.filter((u) => u.cap != null).length : 0

  const row = (
    key: string,
    name: string,
    sub: string,
    q: { cap: number | null; spent: number; pct: number; state: QuotaState; editable: boolean },
    target: Target,
    indent = 0,
  ) => (
    <li key={key} className="flex flex-wrap items-center gap-x-4 gap-y-1.5 border-b border-line py-2.5 last:border-b-0">
      <div className="min-w-[160px] flex-1" style={{ paddingLeft: indent * 16 }}>
        <div className="flex items-center gap-2">
          <span className="truncate text-[13px] font-semibold text-text-high">{name}</span>
          {q.state === 'paused' && (
            <span className="rounded-full bg-red-400/15 px-2 py-0.5 text-[11px] font-semibold text-[var(--red-ink)]">
              {t('billing.quotas.paused')}
            </span>
          )}
        </div>
        <div className="text-[11.5px] text-text-muted">{sub}</div>
      </div>
      <div className="w-full min-w-[180px] sm:w-[260px]">
        {q.cap != null ? (
          <>
            <div
              className="h-2 overflow-hidden rounded-full bg-fill-subtle"
              role="progressbar"
              aria-valuemin={0}
              aria-valuemax={100}
              aria-valuenow={Math.min(100, Math.round(q.pct))}
              aria-label={name}
            >
              <div className={`h-full rounded-full ${barCls[q.state]}`} style={{ width: `${Math.min(100, q.pct)}%` }} />
            </div>
            <div className={`mt-1 text-[11.5px] tabular-nums ${q.state === 'ok' ? 'text-text-muted' : q.state === 'warn' ? 'text-[var(--amber-ink)]' : 'text-[var(--red-ink)]'}`}>
              {t('billing.quotas.used', { spent: money(q.spent), cap: money(q.cap), pct: f.number(Math.round(q.pct)) })}
            </div>
          </>
        ) : (
          <div className="text-[11.5px] text-text-faint">
            {t('billing.quotas.noCap', { spent: money(q.spent) })}
          </div>
        )}
      </div>
      <div className="w-[72px] text-right">
        {q.editable && (
          <Button variant="ghost" size="sm" onClick={() => open(target)}>
            <Pencil size={12} aria-hidden /> {t('billing.quotas.edit')}
          </Button>
        )}
      </div>
    </li>
  )

  return (
    <Card className="p-5 print:hidden" data-tour="billing-quotas">
      <CardTitle
        right={
          data && (data.counts.paused > 0 || data.counts.warn > 0) ? (
            <span className="text-[12px] font-semibold text-[var(--amber-ink)]">
              {t('billing.quotas.summary', { paused: data.counts.paused, warn: data.counts.warn })}
            </span>
          ) : undefined
        }
      >
        <span className="inline-flex items-center gap-2">
          <Gauge size={15} aria-hidden /> {t('billing.quotas.title')}
        </span>
      </CardTitle>
      <p className="mt-1 text-[12.5px] text-text-muted">{t('billing.quotas.help')}</p>

      {error && <p role="alert" className="mt-3 text-[13px] text-[var(--red-ink)]">{error}</p>}
      {!data && !error && (
        <div className="mt-3 flex items-center gap-2 text-[13px] text-text-muted">
          <Loader2 size={14} className="animate-spin" aria-hidden /> {t('common.status.loading')}
        </div>
      )}

      {data && (
        <>
          {capped === 0 && !showAll && (
            <p className="mt-3 text-[13px] text-text-muted">{t('billing.quotas.none')}</p>
          )}
          {entities.length > 0 && (
            <>
              <div className="eyebrow mt-4">{t('billing.quotas.entities')}</div>
              <ul className="mt-1">
                {entities.map((e) =>
                  row(
                    `e-${e.id}`,
                    e.name,
                    e.company ? t('billing.quotas.companyCap') : t('billing.quotas.branchCap'),
                    e,
                    { kind: 'entity', key: String(e.id), name: e.name, cap: e.cap },
                    showAll ? e.depth : 0,
                  ),
                )}
              </ul>
            </>
          )}
          {users.length > 0 && (
            <>
              <div className="eyebrow mt-4">{t('billing.quotas.users')}</div>
              <ul className="mt-1">
                {users.map((u) =>
                  row(`u-${u.username}`, u.username, t('billing.quotas.userCap'), u, {
                    kind: 'user',
                    key: u.username,
                    name: u.username,
                    cap: u.cap,
                  }),
                )}
              </ul>
            </>
          )}
          {(data.can_edit || capped > 0) && (
            <button
              type="button"
              onClick={() => setShowAll(!showAll)}
              className="mt-3 text-[12.5px] font-semibold text-[var(--brand-ink)] hover:underline"
            >
              {showAll ? t('billing.quotas.showCapped') : data.can_edit ? t('billing.quotas.addCap') : t('billing.quotas.showAll')}
            </button>
          )}
        </>
      )}

      <Modal
        open={!!editing}
        onClose={() => setEditing(null)}
        title={editing ? t('billing.quotas.editTitle', { name: editing.name }) : ''}
        size="sm"
        footer={
          <div className="flex w-full items-center gap-2">
            {editing?.cap != null && (
              <Button variant="ghost" size="sm" onClick={() => save(true)} disabled={busy}>
                {t('billing.quotas.clear')}
              </Button>
            )}
            <div className="ml-auto flex gap-2">
              <Button variant="secondary" size="sm" onClick={() => setEditing(null)} disabled={busy}>
                {t('common.actions.cancel')}
              </Button>
              <Button size="sm" onClick={() => save(false)} disabled={busy}>
                {busy && <Loader2 size={13} className="animate-spin" aria-hidden />}
                {t('common.actions.save')}
              </Button>
            </div>
          </div>
        }
      >
        {modalError && (
          <div role="alert" className="mb-3 rounded-lg bg-red-400/10 px-3 py-2 text-[12.5px] text-[var(--red-ink)]">
            {modalError}
          </div>
        )}
        <label className="block">
          <span className="mb-1 block text-[12px] font-semibold text-text-high">
            {t('billing.quotas.capLabel', { currency: data?.currency || 'USD' })}
          </span>
          <input
            type="number"
            min={0}
            step="0.01"
            autoFocus
            value={amount}
            placeholder={t('billing.quotas.capPlaceholder')}
            onChange={(e) => setAmount(e.target.value)}
            className={fieldCls}
          />
        </label>
        <p className="mt-2 text-[12px] text-text-muted">
          {editing?.kind === 'user' ? t('billing.quotas.userHelp') : t('billing.quotas.entityHelp')}
        </p>
      </Modal>
    </Card>
  )
}
