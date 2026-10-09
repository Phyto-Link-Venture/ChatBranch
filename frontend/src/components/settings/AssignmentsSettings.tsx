'use client'

import { useCallback, useEffect, useMemo, useState } from 'react'
import { AlertCircle, CheckCircle, Loader2, Pin, RefreshCw, Search, Shuffle, SlidersHorizontal } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Modal } from '@/components/ui/Modal'
import { Select } from '@/components/ui/Select'
import { Toggle } from '@/components/ui/Toggle'
import { ChannelLogo } from '@/components/ui/ChannelLogo'
import { HealthBadge, RoleChip, accountRole } from '@/components/ui/Badges'
import { authHeaders, expireSessionOn401 } from '@/lib/auth'
import { useAccess } from '@/lib/useAccess'
import { Trans, useFormat, useT, type MessageKey, type TFunction } from '@/i18n'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

interface AsgAccount {
  id: string
  name: string
  /** 'cloud' | 'baileys' for WhatsApp; other channels report their own. */
  provider: string
  health: string
  contacts: number
  /** reminder | conversation (WhatsApp). */
  role?: string
}

interface Assignment {
  contact_id: string | number
  contact_name: string
  phone: string
  channel: string
  account_id: string | null
  account_name: string
  pinned: boolean
  updated_at: string
}

async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}/whatsapp/${path}`, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
  })
  if (expireSessionOn401(res)) throw new Error('Session expired — redirecting to sign-in…')
  const data = await res.json().catch(() => ({}))
  if (!res.ok) throw new Error((data as { detail?: string }).detail || `HTTP ${res.status}`)
  return data as T
}

/** Display names for the channel selector — same wording as the /chats inbox.
 *  Brand names stay as they are; generic ones are translation keys. */
const CHANNEL_LABELS: Record<string, string | MessageKey> = {
  whatsapp: 'WhatsApp',
  facebook: 'Messenger',
  instagram: 'Instagram',
  telegram: 'Telegram',
  email: 'common.fields.email',
  website: 'admin.assignments.websiteWidget',
  sms: 'SMS',
  line: 'LINE',
  viber: 'Viber',
  wechat: 'WeChat',
  zalo: 'Zalo',
  tiktok: 'TikTok',
  douyin: 'Douyin',
}
const channelLabel = (t: TFunction, c: string) => {
  const l = CHANNEL_LABELS[c]
  if (!l) return c.charAt(0).toUpperCase() + c.slice(1)
  return l.includes('.') ? t(l as MessageKey) : l
}
const providerLabel = (t: TFunction, p: string, channel: string) =>
  p === 'baileys' ? 'Baileys' : p === 'cloud' ? 'Cloud API' : p || channelLabel(t, channel)

const key = (r: Assignment) => String(r.contact_id)

/**
 * Account assignment (the /assignments module): sticky contact → sending
 * account routing, per channel. One card per sending account shows how many
 * contacts it carries and how many are pinned; Manage opens the account's
 * contacts with move/pin controls (staged, saved together). "Rebalance
 * evenly" spreads every unpinned contact across the healthy accounts — pins
 * never move. Writes need the account_assignment.manage capability.
 */
export function AssignmentsSettings() {
  const t = useT()
  const f = useFormat()
  const { ready, can } = useAccess()
  const canManage = ready && can('account_assignment.manage')
  const [channel, setChannel] = useState('whatsapp')
  const [channels, setChannels] = useState<string[]>(['whatsapp'])
  const [accounts, setAccounts] = useState<AsgAccount[] | null>(null)
  const [assignments, setAssignments] = useState<Assignment[]>([])
  const [loadError, setLoadError] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [confirmRebalance, setConfirmRebalance] = useState(false)
  const [filter, setFilter] = useState('')
  const [managing, setManaging] = useState<AsgAccount | null>(null)

  const load = useCallback(async () => {
    try {
      const d = await api<{ channels?: string[]; accounts: AsgAccount[]; assignments: Assignment[] }>(
        `assignments?channel=${encodeURIComponent(channel)}`,
      )
      setAccounts(d.accounts)
      setAssignments(d.assignments)
      if (d.channels?.length) setChannels(d.channels)
      setLoadError('')
    } catch (e) {
      setAccounts(null)
      setLoadError(e instanceof Error ? e.message : String(e))
    }
  }, [channel])

  useEffect(() => {
    void load()
  }, [load])

  const q = filter.trim().toLowerCase()
  const matches = useCallback(
    (r: Assignment) => !q || `${r.contact_name} ${r.phone}`.toLowerCase().includes(q),
    [q],
  )
  const byAccount = useMemo(() => {
    const m = new Map<string, Assignment[]>()
    for (const r of assignments) {
      const id = r.account_id ?? ''
      m.set(id, [...(m.get(id) ?? []), r])
    }
    return m
  }, [assignments])

  const rebalance = async () => {
    setBusy(true)
    setError(null)
    setNotice(null)
    try {
      const r = await api<{ moved: number }>('assignments/rebalance', { method: 'POST', body: JSON.stringify({ channel }) })
      setNotice(
        r.moved === 0 ? t('admin.assignments.alreadyBalanced') : t('admin.assignments.rebalanced', { count: r.moved }),
      )
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
      setConfirmRebalance(false)
    }
  }

  if (accounts === null) {
    return (
      <div className="rounded-xl border border-dashed border-line-strong px-4 py-8 text-center text-[12.5px] text-text-muted">
        {loadError ? t('admin.assignments.unavailable', { error: loadError }) : t('common.status.loading')}
      </div>
    )
  }

  const total = assignments.length
  const pinnedTotal = assignments.filter((r) => r.pinned).length
  const channelOptions = [...new Set(channels.includes(channel) ? channels : [channel, ...channels])].map((c) => ({
    value: c,
    label: channelLabel(t, c),
  }))

  return (
    <div className="flex flex-col gap-4">
      {/* toolbar */}
      <div className="flex flex-wrap items-center gap-2">
        <Select
          value={channel}
          onChange={(v) => {
            setChannel(v)
            setError(null)
            setNotice(null)
          }}
          options={channelOptions}
          className="w-[170px] shrink-0"
          size="md"
          ariaLabel={t('common.fields.channel')}
        />
        <label className="flex h-[38px] min-w-[200px] flex-1 items-center gap-2 rounded-lg border border-line-strong bg-card px-2.5 sm:max-w-[300px]">
          <Search size={14} className="shrink-0 text-text-muted" aria-hidden />
          <input
            value={filter}
            onChange={(e) => setFilter(e.target.value)}
            placeholder={t('admin.assignments.findPlaceholder')}
            aria-label={t('admin.assignments.findAria')}
            className="min-w-0 flex-1 bg-transparent text-[13px] text-text-primary outline-none"
          />
        </label>
        <span className="flex items-center gap-1.5 sm:ml-auto">
          <Button
            variant="ghost"
            size="sm"
            onClick={() => void load()}
            title={t('common.actions.refresh')}
            aria-label={t('admin.assignments.refreshAria')}
          >
            <RefreshCw size={13} aria-hidden />
          </Button>
          {canManage && (
            <Button
              variant="secondary"
              size="sm"
              onClick={() => setConfirmRebalance(true)}
              disabled={busy || accounts.length === 0 || total === 0}
            >
              {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Shuffle size={13} aria-hidden />}
              {t('admin.assignments.rebalanceEvenly')}
            </Button>
          )}
        </span>
      </div>

      <p className="max-w-[760px] text-[12.5px] leading-relaxed text-text-muted">
        <Trans
          k="admin.assignments.intro"
          values={{ sticky: <span className="font-semibold text-text-high">{t('admin.assignments.introSticky')}</span> }}
        />{' '}
        {pinnedTotal
          ? t('admin.assignments.summaryPinned', { count: total, channel: channelLabel(t, channel), pinned: pinnedTotal })
          : t('admin.assignments.summary', { count: total, channel: channelLabel(t, channel) })}{' '}
        {t('admin.assignments.introPins')}
      </p>

      {error && (
        <div role="alert" className="flex items-start gap-2 rounded-lg border border-red/25 bg-red/10 px-3 py-2 text-[12.5px] text-[color:var(--red-ink)]">
          <AlertCircle size={14} className="mt-0.5 shrink-0" aria-hidden /> {error}
        </div>
      )}
      {notice && (
        <div role="status" className="flex items-start gap-2 rounded-lg border border-green/25 bg-green/10 px-3 py-2 text-[12.5px] text-[color:var(--green-ink)]">
          <CheckCircle size={14} className="mt-0.5 shrink-0" aria-hidden /> {notice}
        </div>
      )}

      {/* one card per sending account */}
      {accounts.length === 0 ? (
        <div className="rounded-xl border border-dashed border-line-strong px-4 py-8 text-center text-[12.5px] text-text-muted">
          {t('admin.assignments.noAccounts', { channel: channelLabel(t, channel) })}
        </div>
      ) : (
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-3">
          {accounts.map((a) => {
            const rows = byAccount.get(a.id) ?? []
            const pinned = rows.filter((r) => r.pinned).length
            const hits = q ? rows.filter(matches).length : 0
            const share = total ? Math.round((rows.length / total) * 100) : 0
            return (
              <div key={a.id} className="flex flex-col gap-3 rounded-xl border border-line bg-card p-4">
                <div className="flex items-start gap-2.5">
                  <ChannelLogo channel={a.provider === 'baileys' ? 'baileys' : channel} size={32} variant="tile" title="" />
                  <div className="min-w-0 flex-1">
                    <div className="truncate text-[14px] font-bold text-text-heading">{a.name}</div>
                    <div className="mt-1 flex flex-wrap items-center gap-1.5">
                      <span className="rounded-full border border-line-strong px-1.5 py-px text-[10px] font-semibold uppercase tracking-[0.04em] text-text-muted">
                        {providerLabel(t, a.provider, channel)}
                      </span>
                      {channel === 'whatsapp' && <RoleChip role={accountRole(a)} />}
                    </div>
                  </div>
                  <HealthBadge health={a.health} />
                </div>

                <div className="flex items-baseline gap-2">
                  <span className="display text-[30px] leading-none text-text-heading">{f.number(rows.length)}</span>
                  <span className="text-[12px] text-text-muted">
                    {t('admin.assignments.contactsLabel', { count: rows.length })}
                    {pinned ? ` · ${t('admin.assignments.pinnedCount', { count: pinned })}` : ''}
                  </span>
                  {q && (
                    <span className={`ml-auto text-[11.5px] font-semibold ${hits ? 'text-text-high' : 'text-text-faint'}`}>
                      {t('admin.assignments.matches', { count: hits })}
                    </span>
                  )}
                </div>
                <div className="h-1.5 overflow-hidden rounded-full bg-fill" aria-hidden>
                  <div className="h-full rounded-full" style={{ width: `${share}%`, background: 'var(--accent)' }} />
                </div>

                <Button variant="secondary" size="sm" className="self-start" onClick={() => setManaging(a)}>
                  <SlidersHorizontal size={13} aria-hidden />{' '}
                  {canManage ? t('admin.assignments.manageContacts') : t('admin.assignments.viewContacts')}
                </Button>
              </div>
            )
          })}
        </div>
      )}

      <ManageModal
        account={managing}
        accounts={accounts}
        channel={channel}
        rows={managing ? byAccount.get(managing.id) ?? [] : []}
        initialQuery={filter}
        canManage={canManage}
        onClose={() => setManaging(null)}
        onSaved={async (n) => {
          setNotice(t('admin.assignments.updated', { count: n }))
          await load()
        }}
      />

      <Modal
        open={confirmRebalance}
        onClose={() => setConfirmRebalance(false)}
        size="sm"
        title={t('admin.assignments.confirmTitle')}
        subtitle={t('admin.assignments.confirmSubtitle', { channel: channelLabel(t, channel) })}
        footer={
          <>
            <Button variant="ghost" onClick={() => setConfirmRebalance(false)}>
              {t('common.actions.cancel')}
            </Button>
            <Button variant="primary" onClick={() => void rebalance()} disabled={busy}>
              {busy && <Loader2 size={13} className="animate-spin" aria-hidden />}
              {t('admin.assignments.rebalance')}
            </Button>
          </>
        }
      >
        {error && (
          <div role="alert" className="mb-3 rounded-lg border border-red/25 bg-red/10 px-3 py-2 text-[12.5px] text-[color:var(--red-ink)]">
            {error}
          </div>
        )}
        <p className="text-[13px] leading-relaxed text-text-mid">
          {t('admin.assignments.mayMove', { count: total - pinnedTotal })}
        </p>
      </Modal>
    </div>
  )
}

interface Draft {
  account_id: string
  pinned: boolean
}

/** The account's contacts with move + pin controls. Edits are staged and saved
 *  together; errors render inside the modal. */
function ManageModal({
  account,
  accounts,
  channel,
  rows,
  initialQuery,
  canManage,
  onClose,
  onSaved,
}: {
  account: AsgAccount | null
  accounts: AsgAccount[]
  channel: string
  rows: Assignment[]
  initialQuery: string
  canManage: boolean
  onClose: () => void
  onSaved: (changed: number) => Promise<void>
}) {
  const t = useT()
  const f = useFormat()
  const [draft, setDraft] = useState<Record<string, Draft>>({})
  const [query, setQuery] = useState('')
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (!account) return
    setDraft({})
    setError(null)
    setQuery(initialQuery)
    // reset only when a different account opens
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [account?.id])

  if (!account) return null

  const current = (r: Assignment): Draft => draft[key(r)] ?? { account_id: r.account_id ?? '', pinned: r.pinned }
  const changed = rows.filter((r) => {
    const d = draft[key(r)]
    return d && (d.account_id !== (r.account_id ?? '') || d.pinned !== r.pinned)
  })
  const qq = query.trim().toLowerCase()
  const shown = qq ? rows.filter((r) => `${r.contact_name} ${r.phone}`.toLowerCase().includes(qq)) : rows
  const options = [
    { value: '', label: t('admin.assignments.automatic'), hint: t('admin.assignments.automaticHint') },
    ...accounts.map((a) => ({ value: a.id, label: a.name })),
  ]

  const setRow = (r: Assignment, patch: Partial<Draft>) =>
    setDraft((d) => ({ ...d, [key(r)]: { ...current(r), ...patch } }))

  const save = async () => {
    setSaving(true)
    setError(null)
    let done = 0
    try {
      for (const r of changed) {
        const d = current(r)
        await api('assignments', {
          method: 'PUT',
          body: JSON.stringify({ contact_id: r.contact_id, channel, account_id: d.account_id || null, pinned: d.pinned }),
        })
        done++
      }
      await onSaved(done)
      onClose()
    } catch (e) {
      const reason = e instanceof Error ? e.message : String(e)
      setError(done ? t('admin.assignments.partialSave', { count: done, error: reason }) : reason)
      if (done) await onSaved(done)
    } finally {
      setSaving(false)
    }
  }

  return (
    <Modal
      open={!!account}
      onClose={onClose}
      size="lg"
      title={account.name}
      subtitle={t('admin.assignments.manageSubtitle', { count: rows.length })}
      footer={
        <>
          <span className="mr-auto text-[12px] text-text-muted">
            {changed.length ? t('admin.assignments.unsavedCount', { count: changed.length }) : t('admin.assignments.noChanges')}
          </span>
          <Button variant="ghost" onClick={onClose}>
            {canManage ? t('common.actions.cancel') : t('common.actions.close')}
          </Button>
          {canManage && (
            <Button variant="primary" onClick={() => void save()} disabled={saving || changed.length === 0}>
              {saving && <Loader2 size={13} className="animate-spin" aria-hidden />}
              {t('settings.common.saveChanges')}
            </Button>
          )}
        </>
      }
    >
      {error && (
        <div role="alert" className="mb-3 flex items-start gap-2 rounded-lg border border-red/25 bg-red/10 px-3 py-2 text-[12.5px] text-[color:var(--red-ink)]">
          <AlertCircle size={14} className="mt-0.5 shrink-0" aria-hidden /> {error}
        </div>
      )}
      {rows.length > 6 && (
        <label className="mb-3 flex h-9 items-center gap-2 rounded-lg border border-line-strong bg-card px-2.5">
          <Search size={14} className="shrink-0 text-text-muted" aria-hidden />
          <input
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder={t('admin.assignments.searchContacts')}
            aria-label={t('admin.assignments.searchContacts')}
            className="min-w-0 flex-1 bg-transparent text-[13px] text-text-primary outline-none"
          />
        </label>
      )}
      {rows.length === 0 ? (
        <p className="py-6 text-center text-[12.5px] text-text-muted">{t('admin.assignments.noneRouted')}</p>
      ) : shown.length === 0 ? (
        <p className="py-6 text-center text-[12.5px] text-text-muted">{t('admin.assignments.noMatch', { query: query.trim() })}</p>
      ) : (
        <ul className="flex flex-col divide-y divide-line rounded-lg border border-line">
          {shown.map((r) => {
            const d = current(r)
            const dirty = d.account_id !== (r.account_id ?? '') || d.pinned !== r.pinned
            return (
              <li key={key(r)} className="flex flex-wrap items-center gap-x-3 gap-y-2 px-3 py-2.5">
                <div className="min-w-[160px] flex-1">
                  <div className="flex items-center gap-1.5 truncate text-[13px] font-semibold text-text-high">
                    {r.contact_name || t('admin.assignments.unnamed')}
                    {dirty && <span className="h-1.5 w-1.5 shrink-0 rounded-full bg-accent" aria-label={t('admin.assignments.changedAria')} />}
                  </div>
                  <div className="font-mono text-[11px] text-text-muted">
                    {r.phone}
                    {r.updated_at && (
                      <span className="ml-2 font-sans text-text-faint">
                        {t('admin.assignments.changedAgo', { when: f.relative(r.updated_at) })}
                      </span>
                    )}
                  </div>
                </div>
                <Select
                  value={d.account_id}
                  onChange={(v) => setRow(r, { account_id: v })}
                  options={options}
                  className="w-[200px] max-w-full"
                  disabled={!canManage || saving}
                  ariaLabel={t('admin.assignments.accountFor', { name: r.contact_name })}
                />
                <span
                  className="flex items-center gap-2"
                  title={d.pinned ? t('admin.assignments.pinnedHint') : t('admin.assignments.notPinned')}
                >
                  <Pin size={13} aria-hidden className={d.pinned ? 'text-text-heading' : 'text-text-faint'} />
                  <Toggle
                    on={d.pinned}
                    disabled={!canManage || saving || !d.account_id}
                    onChange={(v) => setRow(r, { pinned: v })}
                    label={t('admin.assignments.pinAria', { name: r.contact_name })}
                  />
                </span>
              </li>
            )
          })}
        </ul>
      )}
    </Modal>
  )
}
