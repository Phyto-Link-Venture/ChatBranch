'use client'

import { useCallback, useEffect, useMemo, useState } from 'react'
import { AlertTriangle, KeyRound, Loader2, Lock, Plus, Trash2 } from 'lucide-react'
import { CAPABILITY_CATALOG } from '@/lib/auth'
import { Button } from '@/components/ui/Button'
import { Modal } from '@/components/ui/Modal'
import { Select } from '@/components/ui/Select'
import { CopyField, Notice, Section, Segmented, api, fmtAgo, fmtDate, inputCls } from './shared'

/**
 * API keys — personal credentials for scripts and AI apps. A key acts as its
 * owner (same role, permissions and entity scope); "limited" keys carry only
 * the capabilities ticked here. The secret is shown exactly once.
 */

export interface ApiKeyRow {
  id: number
  name: string
  prefix: string
  display: string
  scopes: string[]
  created_at: string
  last_used_at: string | null
  expires_at: string | null
  revoked_at: string | null
  status: 'active' | 'revoked' | 'expired'
  owner: string
  owner_id: number
  mine: boolean
}

interface KeysResponse {
  keys: ApiKeyRow[]
  can_create: boolean
  can_manage_others: boolean
  capabilities: string[]
}

const EXPIRY = [
  { value: '0', label: 'Never expires' },
  { value: '30', label: '30 days' },
  { value: '90', label: '90 days' },
  { value: '365', label: '1 year' },
]

const STATUS_CLS: Record<ApiKeyRow['status'], string> = {
  active: 'bg-green/10 text-green-300',
  revoked: 'bg-fill-hover text-text-muted',
  expired: 'bg-amber/10 text-amber-300',
}

export function ApiKeysCard({ onCreated }: { onCreated: (token: string) => void }) {
  const [data, setData] = useState<KeysResponse | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [view, setView] = useState<'mine' | 'tenant'>('mine')
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)

  const [open, setOpen] = useState(false)
  const [name, setName] = useState('')
  const [access, setAccess] = useState<'full' | 'limited'>('full')
  const [scopes, setScopes] = useState<string[]>([])
  const [expiry, setExpiry] = useState('0')
  const [busy, setBusy] = useState(false)
  const [modalError, setModalError] = useState<string | null>(null)
  const [revealed, setRevealed] = useState<{ token: string; name: string } | null>(null)

  const load = useCallback(async () => {
    try {
      const d = await api<KeysResponse>(`developers/keys${view === 'tenant' ? '?scope=tenant' : ''}`)
      setData(d)
      setLoadError(null)
    } catch (e) {
      setLoadError(e instanceof Error ? e.message : String(e))
    }
  }, [view])

  useEffect(() => {
    load()
  }, [load])

  const grouped = useMemo(() => {
    const held = new Set(data?.capabilities ?? [])
    const groups = new Map<string, { capability: string; label: string }[]>()
    for (const c of CAPABILITY_CATALOG) {
      if (!held.has(c.capability)) continue
      const arr = groups.get(c.area) ?? []
      arr.push(c)
      groups.set(c.area, arr)
    }
    return Array.from(groups.entries())
  }, [data])

  const openCreate = () => {
    setName('')
    setAccess('full')
    setScopes([])
    setExpiry('0')
    setModalError(null)
    setRevealed(null)
    setMsg(null)
    setOpen(true)
  }

  const create = async () => {
    setBusy(true)
    setModalError(null)
    try {
      const d = await api<{ token: string; key: ApiKeyRow }>('developers/keys', {
        method: 'POST',
        body: JSON.stringify({
          name: name.trim(),
          scopes: access === 'limited' ? scopes : [],
          expires_in_days: Number(expiry) || null,
        }),
      })
      setRevealed({ token: d.token, name: d.key.name })
      onCreated(d.token)
      await load()
    } catch (e) {
      setModalError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  const revoke = async (k: ApiKeyRow) => {
    const whose = k.mine ? '' : ` (owned by ${k.owner})`
    if (!window.confirm(`Revoke "${k.name}"${whose}? Anything using it stops working immediately.`)) return
    try {
      await api(`developers/keys/${k.id}`, { method: 'DELETE' })
      setMsg({ ok: true, text: `"${k.name}" revoked.` })
      await load()
    } catch (e) {
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    }
  }

  const keys = data?.keys ?? []
  const toggleScope = (cap: string) =>
    setScopes((s) => (s.includes(cap) ? s.filter((x) => x !== cap) : [...s, cap]))

  return (
    <Section
      title="API keys"
      description={
        <>
          Personal keys for scripts and AI apps. A key acts as you — your role, your permissions, your entities —
          and everything it does is recorded in Activity under <span className="font-semibold text-text-high">API</span> or{' '}
          <span className="font-semibold text-text-high">MCP</span>.
        </>
      }
      actions={
        <>
          {data?.can_manage_others && (
            <Segmented
              value={view}
              onChange={setView}
              ariaLabel="Whose keys"
              options={[
                { value: 'mine', label: 'My keys' },
                { value: 'tenant', label: 'Everyone’s' },
              ]}
            />
          )}
          {data?.can_create && (
            <Button size="sm" onClick={openCreate}>
              <Plus size={13} aria-hidden /> Create key
            </Button>
          )}
        </>
      }
    >
      <div className="flex flex-col gap-3">
        {msg && !open && <Notice ok={msg.ok}>{msg.text}</Notice>}
        {data && !data.can_create && (
          <div className="flex items-center gap-1.5 text-[12px] text-text-muted">
            <Lock size={12} aria-hidden /> Creating keys needs the “Manage API keys” permission — ask an admin.
          </div>
        )}
        {loadError && !data && <Notice ok={false}>Couldn’t load keys — {loadError}</Notice>}
        {!data && !loadError && (
          <div className="flex items-center gap-2 py-4 text-[12.5px] text-text-muted">
            <Loader2 size={13} className="animate-spin" aria-hidden /> Loading keys…
          </div>
        )}
        {data && keys.length === 0 && (
          <div className="rounded-lg border border-dashed border-line-strong px-4 py-8 text-center">
            <KeyRound size={18} className="mx-auto text-text-faint" aria-hidden />
            <p className="mt-2 text-[13px] text-text-muted">
              {view === 'tenant' ? 'Nobody in your tenant has a key yet.' : 'You have no API keys yet.'}
            </p>
          </div>
        )}
        {keys.length > 0 && (
          <ul className="divide-y divide-line rounded-lg border border-line">
            {keys.map((k) => (
              <li key={k.id} className={`flex flex-wrap items-center gap-x-4 gap-y-1.5 px-3.5 py-3 ${k.status !== 'active' ? 'opacity-70' : ''}`}>
                <div className="min-w-0 flex-1">
                  <div className="flex flex-wrap items-center gap-2">
                    <span className="truncate text-[13.5px] font-semibold text-text-high">{k.name}</span>
                    <span className={`rounded-full px-2 py-px text-[10.5px] font-semibold capitalize ${STATUS_CLS[k.status]}`}>{k.status}</span>
                    {!k.mine && (
                      <span className="rounded-full bg-fill px-2 py-px text-[10.5px] font-semibold text-text-mid">{k.owner}</span>
                    )}
                  </div>
                  <div className="mt-0.5 font-mono text-[11.5px] text-text-muted">{k.display}</div>
                  <div className="mt-1 flex flex-wrap gap-x-3 gap-y-0.5 text-[11.5px] text-text-muted">
                    <span title={k.scopes.join(', ')}>
                      {k.scopes.length === 0 ? 'Full access' : `${k.scopes.length} permission${k.scopes.length === 1 ? '' : 's'}`}
                    </span>
                    <span>Created {fmtDate(k.created_at)}</span>
                    <span>Last used {fmtAgo(k.last_used_at)}</span>
                    <span>{k.expires_at ? `${k.status === 'expired' ? 'Expired' : 'Expires'} ${fmtDate(k.expires_at)}` : 'No expiry'}</span>
                  </div>
                </div>
                {k.status === 'active' && (
                  <Button variant="danger" size="sm" onClick={() => revoke(k)} aria-label={`Revoke ${k.name}`}>
                    <Trash2 size={13} aria-hidden /> Revoke
                  </Button>
                )}
              </li>
            ))}
          </ul>
        )}
      </div>

      <Modal
        open={open}
        onClose={() => setOpen(false)}
        title={revealed ? 'Copy your new key' : 'Create an API key'}
        subtitle={revealed ? undefined : 'Use it from scripts (Authorization: Bearer …) or to connect an AI app over MCP.'}
        size="md"
        footer={
          revealed ? (
            <Button onClick={() => setOpen(false)}>Done</Button>
          ) : (
            <>
              <Button variant="ghost" onClick={() => setOpen(false)} disabled={busy}>
                Cancel
              </Button>
              <Button onClick={create} disabled={busy || !name.trim() || (access === 'limited' && scopes.length === 0)}>
                {busy && <Loader2 size={13} className="animate-spin" aria-hidden />} Create key
              </Button>
            </>
          )
        }
      >
        {revealed ? (
          <div className="flex flex-col gap-3">
            <div
              role="alert"
              className="flex items-start gap-2 rounded-lg border border-amber/30 bg-amber/10 px-3 py-2 text-[12.5px]"
              style={{ color: 'var(--amber-ink)' }}
            >
              <AlertTriangle size={14} className="mt-0.5 shrink-0" aria-hidden />
              <span>This is the only time the key is shown. Copy it somewhere safe — if it’s lost, revoke it and create another.</span>
            </div>
            <div className="eyebrow">{revealed.name}</div>
            <CopyField value={revealed.token} ariaLabel="New API key" />
            <p className="text-[12px] text-text-muted">
              It’s also filled into “Connect an AI app” below for this visit, so you can copy a ready-made config.
            </p>
          </div>
        ) : (
          <div className="flex flex-col gap-4">
            {modalError && (
              <div role="alert" className="rounded-lg border border-red/30 bg-red/10 px-3 py-2 text-[12.5px]" style={{ color: 'var(--red-ink)' }}>
                {modalError}
              </div>
            )}
            <label className="block">
              <span className="mb-1 block text-[12px] font-semibold text-text-high">Name</span>
              <input
                value={name}
                onChange={(e) => setName(e.target.value)}
                placeholder="e.g. Claude Desktop on my laptop"
                maxLength={80}
                className={inputCls}
              />
            </label>
            <div>
              <span className="mb-1.5 block text-[12px] font-semibold text-text-high">Access</span>
              <Segmented
                value={access}
                onChange={setAccess}
                ariaLabel="Access"
                options={[
                  { value: 'full', label: 'Everything I can do' },
                  { value: 'limited', label: 'Only what I pick' },
                ]}
              />
              {access === 'limited' && (
                <div className="mt-3 flex max-h-[280px] flex-col gap-3 overflow-y-auto rounded-lg border border-line p-3">
                  {grouped.map(([area, caps]) => (
                    <div key={area}>
                      <div className="eyebrow mb-1.5">{area}</div>
                      <div className="flex flex-wrap gap-1.5">
                        {caps.map((c) => {
                          const on = scopes.includes(c.capability)
                          return (
                            <button
                              key={c.capability}
                              type="button"
                              role="checkbox"
                              aria-checked={on}
                              onClick={() => toggleScope(c.capability)}
                              className={`rounded-full border px-2.5 py-1 text-[12px] font-medium transition-colors ${
                                on
                                  ? 'border-transparent bg-primary text-on-primary'
                                  : 'border-line-strong text-text-mid hover:bg-fill-hover hover:text-text-high'
                              }`}
                            >
                              {c.label}
                            </button>
                          )
                        })}
                      </div>
                    </div>
                  ))}
                  <p className="text-[11.5px] text-text-muted">
                    A key without any “manage” or “send” permission is read-only on the REST API.
                  </p>
                </div>
              )}
            </div>
            <div>
              <span className="mb-1 block text-[12px] font-semibold text-text-high">Expiry</span>
              <div className="w-[200px]">
                <Select value={expiry} onChange={setExpiry} options={EXPIRY} size="md" ariaLabel="Expiry" />
              </div>
            </div>
          </div>
        )}
      </Modal>
    </Section>
  )
}
