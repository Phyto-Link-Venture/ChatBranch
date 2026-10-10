'use client'

/**
 * "Access requests" on /contacts: a button with a count badge that opens the
 * review list. Approvers (superadmin, org-wide admins, company admins holding
 * contacts.manage) see the requests they can decide — the customer's name, the
 * phone masked to its last 4 digits and the entities it is in now — with
 * Approve / Deny (reason). Requesters see their own pending requests (no
 * customer details until approved) with Cancel.
 * Server: GET/POST /api/contacts/access-requests (ContactAccessEndpoints.cs).
 */

import { useCallback, useEffect, useState } from 'react'
import { KeyRound, Loader2 } from 'lucide-react'
import { authHeaders, expireSessionOn401, hasCap } from '@/lib/auth'
import { Button } from '@/components/ui/Button'
import { Modal } from '@/components/ui/Modal'
import { useFormat, useT } from '@/i18n'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

export type AccessRequest = {
  id: number
  status: 'pending' | 'approved' | 'denied' | 'cancelled'
  note: string
  reason: string
  requested_by: string
  decided_by: string
  created_at: string
  decided_at: string | null
  entity: { id: number; name: string; slug: string } | null
  company_name: string
  mine: boolean
  can_decide: boolean
  contact: {
    id: number
    name: string
    phone: string
    email: string
    entities: { id: number; name: string; slug: string }[]
  } | null
}

type Listing = {
  requests: AccessRequest[]
  can_approve: boolean
  counts: { to_decide: number; mine_pending: number }
}

/** Errors from the API: the v1 envelope or a plain `detail`. */
async function errorOf(res: Response): Promise<string> {
  const d = (await res.json().catch(() => ({}))) as { error?: { message?: string }; detail?: string }
  return d.error?.message || d.detail || `HTTP ${res.status}`
}

export function AccessRequestsPanel({
  openId,
  refreshKey = 0,
  onChanged,
}: {
  /** `?access=<id>` — a notification's deep link opens the list. */
  openId?: number | null
  /** bump to reload (e.g. after the Add-contact form sent a request) */
  refreshKey?: number
  /** a request was approved (the contacts list may have a new row) */
  onChanged?: () => void
}) {
  const t = useT()
  const fmt = useFormat()
  const [allowed, setAllowed] = useState(false)
  const [data, setData] = useState<Listing | null>(null)
  const [open, setOpen] = useState(false)
  const [busy, setBusy] = useState<number | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  // the request whose Deny reason box is open
  const [denying, setDenying] = useState<number | null>(null)
  const [reason, setReason] = useState('')

  // capabilities live in localStorage — read after mount
  useEffect(() => setAllowed(hasCap('contacts.manage') || hasCap('chats.send')), [])

  const load = useCallback(async () => {
    try {
      const res = await fetch(`${API}/contacts/access-requests?status=pending`, { headers: authHeaders() })
      if (expireSessionOn401(res) || !res.ok) return
      setData((await res.json()) as Listing)
    } catch {
      /* API down — keep the last list */
    }
  }, [])

  useEffect(() => {
    if (allowed) load()
  }, [allowed, load, refreshKey])

  useEffect(() => {
    if (allowed && openId) {
      setError(null)
      setNotice(null)
      setOpen(true)
    }
  }, [allowed, openId])

  const act = async (r: AccessRequest, action: 'approve' | 'deny' | 'cancel') => {
    setBusy(r.id)
    setError(null)
    setNotice(null)
    try {
      const res = await fetch(`${API}/contacts/access-requests/${r.id}/${action}`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', ...authHeaders() },
        body: JSON.stringify(action === 'deny' ? { reason: reason.trim() } : {}),
      })
      if (!res.ok) throw new Error(await errorOf(res))
      setNotice(
        action === 'approve'
          ? t('contacts.access.approved', { name: r.contact?.name ?? '', entity: r.entity?.name ?? '' })
          : action === 'deny'
            ? t('contacts.access.denied')
            : t('contacts.access.cancelled'),
      )
      setDenying(null)
      setReason('')
      await load()
      if (action === 'approve') onChanged?.()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(null)
    }
  }

  if (!allowed || !data) return null
  const waiting = data.counts.to_decide + data.counts.mine_pending
  // approvers always see the button; requesters while they have one pending
  if (!data.can_approve && data.counts.mine_pending === 0 && !open) return null

  const toDecide = data.requests.filter((r) => r.can_decide)
  const mine = data.requests.filter((r) => r.mine && !r.can_decide)

  const row = (r: AccessRequest) => (
    <li key={r.id} className="rounded-xl border border-line bg-card p-3">
      <div className="flex flex-wrap items-baseline justify-between gap-2">
        <div className="text-[13px] font-semibold text-text-heading">
          {r.mine
            ? t('contacts.access.yourRequest', { entity: r.entity?.name ?? '' })
            : t('contacts.access.requestedBy', { user: r.requested_by, entity: r.entity?.name ?? '' })}
        </div>
        <div className="text-[11.5px] text-text-muted">{fmt.dateTime(r.created_at)}</div>
      </div>
      {r.contact ? (
        <div className="mt-1.5 text-[12.5px] text-text-mid">
          <span className="font-semibold text-text-high">{r.contact.name}</span>
          {r.contact.phone && <span className="ml-2 font-mono">{r.contact.phone}</span>}
          <div className="text-[11.5px] text-text-muted">
            {t('contacts.access.currentEntities', {
              entities: r.contact.entities.length
                ? r.contact.entities.map((e) => e.name).join(', ')
                : t('contacts.access.noEntities'),
            })}
          </div>
        </div>
      ) : (
        <div className="mt-1.5 text-[12px] text-text-muted">{t('contacts.access.hidden')}</div>
      )}
      {r.note && <div className="mt-1.5 text-[12.5px] text-text-mid">{t('contacts.access.note', { note: r.note })}</div>}
      {r.can_decide && denying === r.id && (
        <label className="mt-2 block">
          <span className="mb-1 block text-[11px] font-bold text-text-high">{t('contacts.access.denyReason')}</span>
          <input
            value={reason}
            onChange={(e) => setReason(e.target.value)}
            maxLength={500}
            className="h-9 w-full rounded-lg border border-line-strong bg-card px-3 text-[13px] text-text-primary outline-none focus:border-text-primary"
          />
        </label>
      )}
      <div className="mt-2 flex flex-wrap justify-end gap-2">
        {r.can_decide && denying !== r.id && (
          <>
            <Button size="sm" variant="secondary" onClick={() => { setDenying(r.id); setReason('') }} disabled={busy !== null}>
              {t('contacts.access.deny')}
            </Button>
            <Button size="sm" variant="primary" onClick={() => act(r, 'approve')} disabled={busy !== null}>
              {busy === r.id && <Loader2 size={12} className="animate-spin" aria-hidden />} {t('contacts.access.approve')}
            </Button>
          </>
        )}
        {r.can_decide && denying === r.id && (
          <>
            <Button size="sm" variant="ghost" onClick={() => setDenying(null)} disabled={busy !== null}>
              {t('common.actions.cancel')}
            </Button>
            <Button size="sm" variant="danger" onClick={() => act(r, 'deny')} disabled={busy !== null}>
              {busy === r.id && <Loader2 size={12} className="animate-spin" aria-hidden />} {t('contacts.access.confirmDeny')}
            </Button>
          </>
        )}
        {r.mine && !r.can_decide && r.status === 'pending' && (
          <Button size="sm" variant="ghost" onClick={() => act(r, 'cancel')} disabled={busy !== null}>
            {busy === r.id && <Loader2 size={12} className="animate-spin" aria-hidden />} {t('contacts.access.cancel')}
          </Button>
        )}
      </div>
    </li>
  )

  return (
    <>
      <Button
        data-tour="contacts-access"
        variant="secondary"
        onClick={() => {
          setError(null)
          setNotice(null)
          setOpen(true)
        }}
        aria-label={t('contacts.access.buttonAria', { count: waiting })}
      >
        <KeyRound size={14} aria-hidden /> {t('contacts.access.button')}
        {waiting > 0 && (
          <span className="ml-1 grid min-w-[18px] place-items-center rounded-full bg-primary px-1.5 text-[10.5px] font-bold text-on-primary">
            {waiting}
          </span>
        )}
      </Button>
      <Modal
        open={open}
        onClose={() => setOpen(false)}
        title={t('contacts.access.title')}
        subtitle={t('contacts.access.subtitle')}
        size="lg"
        footer={
          <Button variant="secondary" onClick={() => setOpen(false)}>
            {t('common.actions.close')}
          </Button>
        }
      >
        {error && (
          <div role="alert" className="mb-3 rounded-lg border border-red/30 bg-red/10 px-3 py-2 text-[12.5px] font-semibold text-red-300">
            {error}
          </div>
        )}
        {notice && (
          <div role="status" className="mb-3 rounded-lg border border-green/30 bg-green/10 px-3 py-2 text-[12.5px] font-semibold text-green-300">
            {notice}
          </div>
        )}
        {toDecide.length === 0 && mine.length === 0 && (
          <p className="text-[13px] text-text-muted">{t('contacts.access.empty')}</p>
        )}
        {toDecide.length > 0 && (
          <section className="mb-4">
            <h3 className="eyebrow mb-2">{t('contacts.access.toDecide')}</h3>
            <ul className="flex flex-col gap-2">{toDecide.map(row)}</ul>
          </section>
        )}
        {mine.length > 0 && (
          <section>
            <h3 className="eyebrow mb-2">{t('contacts.access.mine')}</h3>
            <ul className="flex flex-col gap-2">{mine.map(row)}</ul>
          </section>
        )}
      </Modal>
    </>
  )
}

/**
 * The Add-contact form's offer after a hidden clash: "This customer already
 * exists in another branch of {company}. Ask an admin to share them with
 * {entity}?" + an optional note + Request access.
 */
export function RequestAccessOffer({
  token,
  company,
  entity,
  onSent,
}: {
  token: string
  company: string
  entity: string
  onSent?: () => void
}) {
  const t = useT()
  const [note, setNote] = useState('')
  const [busy, setBusy] = useState(false)
  const [sent, setSent] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const send = async () => {
    setBusy(true)
    setError(null)
    try {
      const res = await fetch(`${API}/contacts/access-requests`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', ...authHeaders() },
        body: JSON.stringify({ request_token: token, note: note.trim() }),
      })
      if (!res.ok) throw new Error(await errorOf(res))
      setSent(true)
      onSent?.()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  const companyName = company || t('contacts.access.yourCompany')
  if (sent)
    return (
      <div role="status" className="mt-2 font-normal text-green-300">
        {t('contacts.access.sent', { company: companyName })}
      </div>
    )
  return (
    <div data-tour="contacts-request-access" className="mt-2 flex flex-col gap-2 font-normal text-text-mid">
      <span>{t('contacts.access.offer', { company: companyName, entity })}</span>
      <input
        value={note}
        onChange={(e) => setNote(e.target.value)}
        maxLength={500}
        placeholder={t('contacts.access.notePlaceholder')}
        aria-label={t('contacts.access.notePlaceholder')}
        className="h-9 w-full rounded-lg border border-line-strong bg-card px-3 text-[13px] text-text-primary outline-none focus:border-text-primary"
      />
      {error && <span className="font-semibold text-red-300">{error}</span>}
      <div>
        <Button size="sm" variant="secondary" onClick={send} disabled={busy}>
          {busy && <Loader2 size={12} className="animate-spin" aria-hidden />} {t('contacts.access.request')}
        </Button>
      </div>
    </div>
  )
}
