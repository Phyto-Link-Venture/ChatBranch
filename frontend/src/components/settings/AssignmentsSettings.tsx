'use client'

import { useCallback, useEffect, useMemo, useState } from 'react'
import { AlertCircle, CheckCircle, Loader2, Pin, RefreshCw, Search, Shuffle } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Select } from '@/components/ui/Select'
import { HealthBadge, RoleChip, accountRole } from '@/components/ui/Badges'
import { roleHeaders, getRole } from '@/lib/role'
import { expireSessionOn401 } from '@/lib/auth'
import { relTime } from '@/lib/channelHealth'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

interface AsgAccount {
  id: string
  name: string
  /** 'cloud' | 'baileys' for WhatsApp; other channels report their own. */
  provider: string
  health: string
  contacts: number
  /** reminder | conversation — optional while the backend contract lands. */
  role?: string
}

interface Assignment {
  contact_id: string
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
    headers: { 'Content-Type': 'application/json', ...roleHeaders() },
    ...init,
  })
  const data = await res.json().catch(() => ({}))
  if (expireSessionOn401(res)) throw new Error('Session expired — redirecting to login…')
  if (!res.ok) throw new Error((data as { detail?: string }).detail || `HTTP ${res.status}`)
  return data as T
}

const inputCls =
  'w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[12.5px] text-text-primary outline-none transition-colors focus:border-brand-400'

/** Display names for the channel selector — same wording as the /chats inbox. */
const CHANNEL_LABELS: Record<string, string> = {
  whatsapp: 'WhatsApp',
  facebook: 'Messenger',
  instagram: 'Instagram',
  telegram: 'Telegram',
  email: 'Email',
}
const channelLabel = (c: string) => CHANNEL_LABELS[c] || c.charAt(0).toUpperCase() + c.slice(1)

/** Settings → Account Assignment: sticky contact → sending-account routing,
 *  per channel. Every contact is pinned to one account on each channel so
 *  conversations stay on the same number/identity. Default = even
 *  auto-distribution across healthy accounts with automatic failover when an
 *  account goes down; individual contacts can be reassigned or pinned here.
 *  Reassignments save per row (PUT, optimistic UI with rollback) and
 *  "Rebalance evenly" redistributes every unpinned customer — both admin+
 *  only. */
export function AssignmentsSettings() {
  const [channel, setChannel] = useState('whatsapp')
  const [channels, setChannels] = useState<string[]>(['whatsapp'])
  const [accounts, setAccounts] = useState<AsgAccount[] | null>(null)
  const [assignments, setAssignments] = useState<Assignment[]>([])
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [savingId, setSavingId] = useState<string | null>(null)
  const [filter, setFilter] = useState('')
  const [canManage, setCanManage] = useState(false)

  // role read after mount (localStorage) — reassign/rebalance are admin+ on
  // the server, so agents get a read-only view here.
  useEffect(() => setCanManage(getRole() !== 'agent'), [])

  const load = useCallback(async () => {
    try {
      const d = await api<{
        channel?: string
        channels?: string[]
        accounts: AsgAccount[]
        assignments: Assignment[]
      }>(`assignments?channel=${encodeURIComponent(channel)}`)
      setAccounts(d.accounts)
      setAssignments(d.assignments)
      // channels with ≥1 linked account — older backends omit the field
      if (d.channels?.length) setChannels(d.channels)
    } catch {
      setAccounts(null)
    }
  }, [channel])

  useEffect(() => {
    load()
  }, [load])

  const filtered = useMemo(() => {
    const q = filter.trim().toLowerCase()
    if (!q) return assignments
    return assignments.filter(
      (r) =>
        (r.contact_name || '').toLowerCase().includes(q) ||
        (r.phone || '').toLowerCase().includes(q),
    )
  }, [assignments, filter])

  if (accounts === null) {
    return (
      <div className="py-6 text-[12px] text-text-muted">
        Account assignment unavailable — is the backend API running?
      </div>
    )
  }

  // env-default is a read-only pseudo-account — visible in the tiles but never
  // an assignment target.
  const assignable = accounts.filter((a) => a.id !== 'env-default')
  const accountOptions = [
    { value: '', label: 'Auto', hint: '(even distribution)' },
    ...assignable.map((a) => ({ value: a.id, label: a.name })),
  ]

  /** PUT one row — optimistic update, roll back on failure, then re-sync so
   *  the per-account counts (and any server-side auto-assign) stay accurate. */
  const save = async (row: Assignment, patch: { account_id?: string | null; pinned?: boolean }) => {
    const prev = assignments
    const accountId = patch.account_id !== undefined ? patch.account_id : row.account_id
    const pinned = patch.pinned !== undefined ? patch.pinned : row.pinned
    const accountName = accountId
      ? accounts.find((a) => a.id === accountId)?.name || row.account_name
      : ''
    setError(null)
    setNotice(null)
    setAssignments(
      assignments.map((r) =>
        r.contact_id === row.contact_id
          ? { ...r, account_id: accountId, pinned, account_name: accountName, updated_at: new Date().toISOString() }
          : r,
      ),
    )
    setSavingId(row.contact_id)
    try {
      await api('assignments', {
        method: 'PUT',
        body: JSON.stringify({
          contact_id: row.contact_id,
          channel,
          account_id: accountId,
          pinned,
        }),
      })
      await load()
    } catch (e) {
      setAssignments(prev)
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setSavingId(null)
    }
  }

  const rebalance = async () => {
    if (!window.confirm('Redistribute all unpinned contacts evenly across healthy accounts?')) return
    setBusy(true)
    setError(null)
    setNotice(null)
    try {
      const r = await api<{ moved: number; accounts: Record<string, number> }>(
        'assignments/rebalance',
        { method: 'POST', body: JSON.stringify({ channel }) },
      )
      setNotice(
        r.moved === 0
          ? 'Already balanced — no contacts moved.'
          : `Rebalanced — ${r.moved} customer${r.moved === 1 ? '' : 's'} moved.`,
      )
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="flex flex-col gap-4 pt-3.5">
      <p className="text-[11.5px] leading-relaxed text-text-mid">
        Every contact is <span className="font-bold text-text-high">stickily routed</span> to one
        sending account per channel so their whole conversation stays on the same number. By default
        contacts are spread evenly across healthy accounts and fail over automatically when an
        account goes down. Reassign individual contacts below, or pin them so rebalancing never
        moves them.
      </p>

      {/* channel selector — one assignment map per channel */}
      <div className="flex items-center gap-2">
        <Select
          value={channel}
          onChange={(v) => {
            setChannel(v)
            setError(null)
            setNotice(null)
          }}
          options={[...new Set(channels.includes(channel) ? channels : [channel, ...channels])].map((c) => ({
            value: c,
            label: channelLabel(c),
          }))}
          className="w-[180px] shrink-0"
          ariaLabel="Channel"
        />
        <span className="text-[11px] text-text-muted">
          Channels with at least one linked account. Assignments are managed per channel.
        </span>
      </div>

      {/* per-account summary tiles */}
      <div className="grid grid-cols-2 gap-2.5 lg:grid-cols-3">
        {accounts.length === 0 && (
          <div className="col-span-full rounded-xl border border-dashed border-line-strong px-4 py-6 text-center text-[12px] text-text-muted">
            No {channelLabel(channel)} accounts to assign to yet — link one under Settings → Channels.
          </div>
        )}
        {accounts.map((a) => (
          <div key={a.id} className="rounded-[10px] border border-line bg-fill-subtle px-3 py-2.5">
            <div className="flex items-center gap-1.5">
              <span className="min-w-0 truncate text-[12.5px] font-bold text-text-high">{a.name}</span>
              <span className="shrink-0 rounded-full bg-fill px-1.5 py-px text-[9px] font-bold uppercase text-text-muted">
                {a.provider === 'baileys'
                  ? 'Baileys'
                  : a.provider === 'cloud'
                    ? 'Cloud'
                    : a.provider || channelLabel(channel)}
              </span>
              {channel === 'whatsapp' && <RoleChip role={accountRole(a)} />}
            </div>
            <div className="mt-1.5 flex items-center gap-2">
              <HealthBadge health={a.health} />
              <span className="text-[11px] text-text-muted">
                {a.contacts} contact{a.contacts === 1 ? '' : 's'}
              </span>
            </div>
          </div>
        ))}
      </div>

      {/* toolbar: filter + rebalance */}
      <div className="flex items-center gap-2">
        <div className="relative w-[240px] shrink-0">
          <Search
            size={13}
            className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-text-muted"
            aria-hidden
          />
          <input
            value={filter}
            onChange={(e) => setFilter(e.target.value)}
            placeholder="Filter by name or phone…"
            aria-label="Filter assignments"
            className={`${inputCls} !pl-8`}
          />
        </div>
        <span className="text-[11px] text-text-muted">
          {filtered.length === assignments.length
            ? `${assignments.length} customer${assignments.length === 1 ? '' : 's'}`
            : `${filtered.length} of ${assignments.length}`}
        </span>
        <div className="flex-1" />
        <Button variant="ghost" onClick={load} title="Refresh" aria-label="Refresh assignments">
          <RefreshCw size={13} aria-hidden />
        </Button>
        {canManage && (
          <Button variant="secondary" onClick={rebalance} disabled={busy || assignable.length === 0}>
            {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Shuffle size={13} aria-hidden />}{' '}
            Rebalance evenly
          </Button>
        )}
      </div>

      {error && (
        <div className="flex items-start gap-2 rounded-lg border border-red-500/20 bg-red-500/10 px-3 py-2 text-[12px]" style={{ color: 'var(--red-ink)' }}>
          <AlertCircle size={13} className="mt-0.5 shrink-0" aria-hidden /> {error}
        </div>
      )}
      {notice && (
        <div className="flex items-start gap-2 rounded-lg border border-brand-400/20 bg-brand-400/10 px-3 py-2 text-[12px]" style={{ color: 'var(--green-ink)' }}>
          <CheckCircle size={13} className="mt-0.5 shrink-0" aria-hidden /> {notice}
        </div>
      )}

      {/* assignments table */}
      <div>
        {assignments.length === 0 ? (
          <div className="py-6 text-center text-[12px] text-text-muted">
            No contacts to assign yet.
          </div>
        ) : filtered.length === 0 ? (
          <div className="py-6 text-center text-[12px] text-text-muted">
            No contacts match “{filter.trim()}”.
          </div>
        ) : (
          filtered.map((r) => {
            const saving = savingId === r.contact_id
            return (
              <div key={r.contact_id} className="flex items-center gap-3 border-b border-line py-2.5">
                <div className="min-w-0 flex-1">
                  <div className="truncate text-[12.5px] font-semibold text-text-high">
                    {r.contact_name}
                  </div>
                  <div className="font-mono text-[10.5px] text-text-muted">{r.phone}</div>
                </div>
                {r.updated_at && (
                  <span className="hidden shrink-0 text-[10.5px] text-text-faint sm:block" title="Last assignment change">
                    {relTime(r.updated_at)}
                  </span>
                )}
                <Select
                  value={r.account_id || ''}
                  onChange={(v) => save(r, { account_id: v || null })}
                  options={accountOptions}
                  className="w-[190px] shrink-0"
                  disabled={!canManage || saving}
                  ariaLabel={`Account for ${r.contact_name}`}
                />
                <button
                  onClick={() => save(r, { pinned: !r.pinned })}
                  disabled={!canManage || saving}
                  aria-pressed={r.pinned}
                  aria-label={`${r.pinned ? 'Unpin' : 'Pin'} ${r.contact_name}`}
                  title={
                    r.pinned
                      ? "Pinned — won't be moved by rebalancing; failover still applies"
                      : "Pin to the selected account — rebalancing won't move pinned contacts"
                  }
                  className={`grid h-8 w-8 shrink-0 cursor-pointer place-items-center rounded-lg transition-colors disabled:cursor-not-allowed disabled:opacity-40 ${
                    r.pinned ? 'bg-brand-400/10' : 'text-text-muted hover:bg-fill-hover hover:text-text-high'
                  }`}
                  style={r.pinned ? { color: 'var(--brand-ink)' } : undefined}
                >
                  {saving ? (
                    <Loader2 size={13} className="animate-spin" aria-hidden />
                  ) : (
                    <Pin size={13} aria-hidden />
                  )}
                </button>
              </div>
            )
          })
        )}
      </div>
    </div>
  )
}
