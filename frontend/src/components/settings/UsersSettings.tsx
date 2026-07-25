'use client'

import { useCallback, useEffect, useState } from 'react'
import { KeyRound, Loader2, ShieldAlert, Tags, UserCheck, UserPlus, UserX } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { roleHeaders } from '@/lib/role'
import { expireSessionOn401, getAuth, authHeaders, type Role } from '@/lib/auth'
import { useRole } from '@/lib/useRole'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8000/api'

interface UserBrand {
  id: number
  name: string
  slug: string
}

interface ManagedUser {
  id: number
  username: string
  name: string
  role: Role
  active: boolean
  created: string | null
  last_login: string | null
  tenant: string | null
  brands: UserBrand[]
}

/** /api/v1/brands row — the pool the brand-access picker chooses from. */
interface BrandRow {
  id: number
  name: string
  slug: string
  tenant: string
  is_active: boolean
}

async function api<T>(path: string, init?: RequestInit): Promise<T> {
  // Canonical user management lives at /api/v1/users (DRF). Errors use the v1
  // envelope {error:{message}}; we also read legacy {detail} for safety.
  const res = await fetch(`${API}/v1/${path}`, {
    headers: { 'Content-Type': 'application/json', ...roleHeaders() },
    ...init,
  })
  const data = await res.json().catch(() => ({}))
  if (expireSessionOn401(res)) throw new Error('Session expired — redirecting to login…')
  if (!res.ok) {
    const d = data as { error?: { message?: string }; detail?: string }
    throw new Error(d.error?.message || d.detail || `HTTP ${res.status}`)
  }
  return data as T
}

const inputCls =
  'w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[12.5px] text-text-primary outline-none transition-colors focus:border-brand-400'

const ROLES: Role[] = ['superadmin', 'admin', 'agent']

const ROLE_LABEL: Record<Role, string> = {
  superadmin: 'Super admin',
  admin: 'Admin',
  agent: 'Agent',
}

// superadmin = emerald brand, admin = neutral zinc, agent = amber
const ROLE_CHIP: Record<Role, string> = {
  superadmin: 'bg-brand-400/15 text-brand-200',
  admin: 'bg-fill-hover text-text-mid',
  agent: 'bg-amber/15 text-amber-300',
}

/** Toggleable brand chips — used by both the add-user form and the per-user
 * brand-access editor. */
function BrandPicker({
  brands,
  selected,
  onToggle,
  showTenant,
}: {
  brands: BrandRow[]
  selected: number[]
  onToggle: (id: number) => void
  showTenant: boolean
}) {
  if (brands.length === 0) {
    return (
      <span className="text-[10.5px] text-text-muted">
        No brands yet — create one under Settings → Brands & Tenants.
      </span>
    )
  }
  return (
    <div className="flex flex-wrap gap-1.5">
      {brands.map((b) => {
        const on = selected.includes(b.id)
        return (
          <button
            type="button"
            key={b.id}
            aria-pressed={on}
            onClick={() => onToggle(b.id)}
            className={`cursor-pointer rounded-full border px-2.5 py-1 text-[11px] font-semibold transition-colors ${
              on
                ? 'border-brand-400/40 bg-brand-400/15 text-brand-200'
                : 'border-line-strong bg-surface text-text-muted hover:text-text-high'
            }`}
          >
            {on ? '✓ ' : ''}
            {b.name}
            {showTenant && <span className="ml-1 opacity-60">({b.tenant})</span>}
          </button>
        )
      })}
    </div>
  )
}

function Field({
  label,
  value,
  onChange,
  placeholder,
  hint,
  secret = false,
}: {
  label: string
  value: string
  onChange: (v: string) => void
  placeholder?: string
  hint?: string
  secret?: boolean
}) {
  return (
    <label className="block">
      <span className="mb-1 block text-[11px] font-bold text-text-high">{label}</span>
      <input
        type={secret ? 'password' : 'text'}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        placeholder={placeholder}
        // "new-password" (not "off", which browsers ignore on password fields)
        // stops Chrome autofilling the operator's own saved login here — that
        // ghost-fill left React state empty and the Add button dead-grey
        autoComplete={secret ? 'new-password' : 'off'}
        className={inputCls}
      />
      {hint && <span className="mt-1 block text-[10.5px] leading-relaxed text-text-muted">{hint}</span>}
    </label>
  )
}

/**
 * User management — SUPER ADMIN only (server-enforced: /api/v1/users).
 * Creates operator logins, changes roles, resets passwords and deactivates
 * accounts. Password resets and deactivation clear the target's bearer token
 * on the server, so their existing sessions die immediately. The backend
 * refuses to demote or deactivate the last remaining active super admin.
 */
export function UsersSettings() {
  const { role: myRole, ready } = useRole()
  const me = getAuth()?.username
  const [users, setUsers] = useState<ManagedUser[] | null>(null)
  const [busy, setBusy] = useState(false)
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)
  // inline reset-password expansion (one row at a time)
  const [pwFor, setPwFor] = useState<number | null>(null)
  const [pwDraft, setPwDraft] = useState('')
  // inline brand-access expansion (agents)
  const [brandsFor, setBrandsFor] = useState<number | null>(null)
  const [brandsDraft, setBrandsDraft] = useState<number[]>([])
  // add-user form
  const [newUsername, setNewUsername] = useState('')
  const [newName, setNewName] = useState('')
  const [newPassword, setNewPassword] = useState('')
  const [newRole, setNewRole] = useState<Role>('agent')
  const [newBrandIds, setNewBrandIds] = useState<number[]>([])
  // brand pool for the pickers (RBAC brands, /api/v1)
  const [brandPool, setBrandPool] = useState<BrandRow[]>([])

  const refresh = useCallback(async () => {
    try {
      const r = await api<{ users: ManagedUser[] }>('users')
      setUsers(r.users)
    } catch {
      setUsers(null)
    }
  }, [])

  useEffect(() => {
    refresh()
    fetch(`${API}/v1/brands`, { headers: authHeaders() })
      .then((r) => (r.ok ? r.json() : { brands: [] }))
      .then((d) => setBrandPool(Array.isArray(d.brands) ? d.brands : []))
      .catch(() => setBrandPool([]))
  }, [refresh])

  const multiTenant = new Set(brandPool.map((b) => b.tenant)).size > 1
  const toggleIn = (list: number[], id: number) =>
    list.includes(id) ? list.filter((x) => x !== id) : [...list, id]

  const run = async (fn: () => Promise<unknown>, okText?: string) => {
    setBusy(true)
    setMsg(null)
    try {
      await fn()
      await refresh()
      if (okText) setMsg({ ok: true, text: okText })
    } catch (e) {
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    } finally {
      setBusy(false)
    }
  }

  const isSuper = myRole === 'superadmin'
  /** Admins manage only agents (server-enforced too). */
  const canManage = (u: ManagedUser) => isSuper || u.role === 'agent'

  if (!ready) return null // role resolves after mount — don't flash the denial

  if (myRole === 'agent') {
    return (
      <div className="flex items-center gap-2 py-6 text-[12.5px] text-text-muted">
        <ShieldAlert size={15} className="text-amber-300" aria-hidden />
        Users are managed by admins.
      </div>
    )
  }

  if (users === null) {
    return (
      <div className="py-6 text-[12px] text-text-muted">
        User management unavailable — is the backend API running on port 8000?
      </div>
    )
  }

  const setRole = (u: ManagedUser, role: Role) =>
    run(
      () => api(`users/${u.id}`, { method: 'POST', body: JSON.stringify({ role }) }),
      `Role updated — ${u.username} is now ${ROLE_LABEL[role].toLowerCase()}.`,
    )

  const resetPassword = (u: ManagedUser) => {
    if (pwDraft.length < 8) {
      setMsg({ ok: false, text: 'Password must be at least 8 characters.' })
      return
    }
    const password = pwDraft
    run(async () => {
      await api(`users/${u.id}`, { method: 'POST', body: JSON.stringify({ password }) })
      setPwFor(null)
      setPwDraft('')
    }, `Password reset — ${u.username} has been signed out everywhere.`)
  }

  const toggleActive = (u: ManagedUser) => {
    if (u.active) {
      if (!window.confirm(`Deactivate ${u.username}? They will be signed out everywhere and unable to log in.`))
        return
      run(
        () => api(`users/${u.id}`, { method: 'DELETE' }),
        `${u.username} deactivated.`,
      )
    } else {
      run(
        () => api(`users/${u.id}`, { method: 'POST', body: JSON.stringify({ active: true }) }),
        `${u.username} reactivated — they can log in again.`,
      )
    }
  }

  const addUser = () => {
    // validate on click with explicit messages — a silently disabled button
    // reads as "broken", especially when browser autofill ghost-fills the form
    const username = newUsername.trim()
    if (!username) {
      setMsg({ ok: false, text: 'Username is required — type it in (autofilled text does not count).' })
      return
    }
    if (newPassword.length < 8) {
      setMsg({ ok: false, text: 'Password must be at least 8 characters.' })
      return
    }
    run(async () => {
      await api('users', {
        method: 'POST',
        body: JSON.stringify({
          username,
          name: newName.trim(),
          password: newPassword,
          role: newRole,
          brand_ids: newRole === 'agent' ? newBrandIds : [],
        }),
      })
      setNewUsername('')
      setNewName('')
      setNewPassword('')
      setNewRole('agent')
      setNewBrandIds([])
    }, `User ${username} created.`)
  }

  const saveBrands = (u: ManagedUser) =>
    run(async () => {
      await api(`users/${u.id}`, {
        method: 'POST',
        body: JSON.stringify({ brand_ids: brandsDraft }),
      })
      setBrandsFor(null)
    }, `Brand access updated for ${u.username}.`)

  return (
    <div className="flex flex-col gap-4 pt-3.5">
      <p className="text-[11.5px] leading-relaxed text-text-mid">
        Operator logins for this dashboard. Passwords are set here — a password reset or deactivation
        clears that user&apos;s session token, signing them out everywhere immediately. The last active
        super admin can never be demoted or deactivated.
        {!isSuper && ' As an admin you can create and manage agents in your own tenant.'}
      </p>

      {/* what each role (user group) can do — fixed capability matrix */}
      <div className="grid gap-2 sm:grid-cols-3">
        {(
          [
            { lbl: 'Super admin', chip: ROLE_CHIP.superadmin,
              items: ['Everything, across all tenants', 'App keys & global settings', 'Creates admins & agents', 'Brands in any tenant'] },
            { lbl: 'Admin', chip: ROLE_CHIP.admin,
              items: ['Own tenant only', 'Channel accounts, routing, flows, templates', 'Creates agents + assigns brand access', 'No app keys / global settings'] },
            { lbl: 'Agent', chip: ROLE_CHIP.agent,
              items: ['Work queues: chats, calls, contacts', 'Only contacts on their assigned brands', 'No settings management'] },
          ] as { lbl: string; chip: string; items: string[] }[]
        ).map((c) => (
          <div key={c.lbl} className="rounded-lg border border-line bg-fill p-3">
            <span className={`rounded-full px-2 py-px text-[10px] font-bold uppercase ${c.chip}`}>
              {c.lbl}
            </span>
            <ul className="mt-2 flex flex-col gap-1 text-[11px] leading-relaxed text-text-mid">
              {c.items.map((i) => (
                <li key={i}>· {i}</li>
              ))}
            </ul>
          </div>
        ))}
      </div>

      {/* user list */}
      <div className="flex flex-col">
        {users.map((u) => (
          <div key={u.id} className="border-b border-line py-3">
            <div className="flex items-center gap-3">
              <div className="min-w-0 flex-1">
                <div className="flex items-center gap-2">
                  <span className="text-[13px] font-semibold text-text-high">{u.username}</span>
                  {u.username === me && (
                    <span className="text-[10.5px] font-bold text-text-muted">(you)</span>
                  )}
                  <span
                    className={`rounded-full px-2 py-px text-[10px] font-bold uppercase ${ROLE_CHIP[u.role]}`}
                  >
                    {ROLE_LABEL[u.role]}
                  </span>
                  {u.active ? (
                    <span className="rounded-full bg-green/15 px-2 py-px text-[10px] font-bold text-green-300">
                      Active
                    </span>
                  ) : (
                    <span className="rounded-full bg-red/15 px-2 py-px text-[10px] font-bold text-red-300">
                      Deactivated
                    </span>
                  )}
                </div>
                <div className="mt-0.5 text-[11px] text-text-muted">
                  {u.name}
                  {u.tenant && <> · tenant <span className="font-semibold">{u.tenant}</span></>}
                  {' · last login '}
                  {u.last_login ? new Date(u.last_login).toLocaleString() : 'never'}
                </div>
                {u.role === 'agent' && (
                  <div className="mt-1 flex flex-wrap items-center gap-1">
                    {u.brands.length === 0 ? (
                      <span className="text-[10.5px] text-amber-300">
                        No brand access — this agent sees no contacts.
                      </span>
                    ) : (
                      u.brands.map((b) => (
                        <span
                          key={b.id}
                          className="rounded-full bg-fill-hover px-2 py-px text-[10px] font-semibold text-text-mid"
                        >
                          {b.name}
                        </span>
                      ))
                    )}
                  </div>
                )}
              </div>

              {u.role === 'agent' && (
                <Button
                  variant="secondary"
                  disabled={busy}
                  onClick={() => {
                    setBrandsDraft(u.brands.map((b) => b.id))
                    setBrandsFor(brandsFor === u.id ? null : u.id)
                  }}
                >
                  <Tags size={13} aria-hidden /> Brands
                </Button>
              )}

              {isSuper && (
                <select
                  value={u.role}
                  disabled={busy}
                  onChange={(e) => setRole(u, e.target.value as Role)}
                  aria-label={`Role for ${u.username}`}
                  className="cursor-pointer rounded-[7px] border border-line-strong bg-surface px-2.5 py-1.5 text-[12px] font-semibold text-text-high outline-none"
                >
                  {ROLES.map((r) => (
                    <option key={r} value={r}>
                      {ROLE_LABEL[r]}
                    </option>
                  ))}
                </select>
              )}

              {canManage(u) && (
                <>
                  <Button
                    variant="secondary"
                    disabled={busy}
                    onClick={() => {
                      setPwDraft('')
                      setPwFor(pwFor === u.id ? null : u.id)
                    }}
                  >
                    <KeyRound size={13} aria-hidden /> Reset password
                  </Button>

                  <Button variant={u.active ? 'danger' : 'success'} disabled={busy} onClick={() => toggleActive(u)}>
                    {u.active ? (
                      <>
                        <UserX size={13} aria-hidden /> Deactivate
                      </>
                    ) : (
                      <>
                        <UserCheck size={13} aria-hidden /> Reactivate
                      </>
                    )}
                  </Button>
                </>
              )}
            </div>

            {brandsFor === u.id && (
              <div className="mt-2.5 flex flex-col gap-2">
                <BrandPicker
                  brands={brandPool.filter((b) => !u.tenant || b.tenant === u.tenant)}
                  selected={brandsDraft}
                  onToggle={(id) => setBrandsDraft((d) => toggleIn(d, id))}
                  showTenant={multiTenant && !u.tenant}
                />
                <div className="flex items-center gap-2">
                  <Button variant="primary" disabled={busy} onClick={() => saveBrands(u)}>
                    {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : null} Save
                    brand access
                  </Button>
                  <Button variant="ghost" onClick={() => setBrandsFor(null)}>
                    Cancel
                  </Button>
                </div>
              </div>
            )}

            {pwFor === u.id && (
              <div className="mt-2.5 flex items-center gap-2">
                <input
                  type="password"
                  value={pwDraft}
                  onChange={(e) => setPwDraft(e.target.value)}
                  onKeyDown={(e) => e.key === 'Enter' && resetPassword(u)}
                  placeholder={`New password for ${u.username} (min 8 characters)`}
                  autoComplete="new-password"
                  aria-label={`New password for ${u.username}`}
                  className="h-[34px] min-w-0 flex-1 rounded-lg border border-line-strong bg-card px-3 text-[12px] text-text-primary outline-none placeholder:text-text-faint focus:border-brand-400"
                />
                <Button variant="primary" disabled={busy || pwDraft.length < 8} onClick={() => resetPassword(u)}>
                  {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : null} Set password
                </Button>
                <Button
                  variant="ghost"
                  onClick={() => {
                    setPwFor(null)
                    setPwDraft('')
                  }}
                >
                  Cancel
                </Button>
              </div>
            )}
          </div>
        ))}
      </div>

      {msg && (
        <div className={`text-[12px] font-semibold ${msg.ok ? 'text-green-300' : 'text-red-300'}`}>{msg.text}</div>
      )}

      {/* add user */}
      <div>
        <div className="mb-2 text-[11px] font-semibold uppercase tracking-[0.06em] text-text-muted">
          Add user
        </div>
        <div className="grid grid-cols-2 gap-3">
          <Field
            label="Username"
            value={newUsername}
            onChange={setNewUsername}
            placeholder="e.g. agent2"
            hint="Login name — must be unique."
          />
          <Field label="Display name" value={newName} onChange={setNewName} placeholder="e.g. Aina Rahman" />
          <Field
            label="Password"
            value={newPassword}
            onChange={setNewPassword}
            placeholder="min 8 characters"
            hint="Share it with the user out-of-band; you can reset it here any time."
            secret
          />
          <label className="block">
            <span className="mb-1 block text-[11px] font-bold text-text-high">Role</span>
            <select
              value={newRole}
              onChange={(e) => setNewRole(e.target.value as Role)}
              className="w-full cursor-pointer rounded-lg border border-line-strong bg-card px-3 py-2 text-[12.5px] font-semibold text-text-high outline-none"
            >
              {(isSuper ? ROLES : (['agent'] as Role[])).map((r) => (
                <option key={r} value={r}>
                  {ROLE_LABEL[r]}
                </option>
              ))}
            </select>
            <span className="mt-1 block text-[10.5px] leading-relaxed text-text-muted">
              Super admin: everything · Admin: own tenant, all brands · Agent: assigned
              brands only.
            </span>
          </label>
        </div>
        {newRole === 'agent' && (
          <div className="mt-3">
            <span className="mb-1.5 block text-[11px] font-bold text-text-high">Brand access</span>
            <BrandPicker
              brands={brandPool}
              selected={newBrandIds}
              onToggle={(id) => setNewBrandIds((d) => toggleIn(d, id))}
              showTenant={multiTenant}
            />
            <span className="mt-1.5 block text-[10.5px] leading-relaxed text-text-muted">
              An agent only sees contacts on their assigned brands (all brands must belong to
              one tenant). You can change this later with the Brands button on their row.
            </span>
          </div>
        )}
        <div className="mt-3">
          <Button variant="primary" onClick={addUser} disabled={busy}>
            {busy ? (
              <Loader2 size={13} className="animate-spin" aria-hidden />
            ) : (
              <UserPlus size={13} aria-hidden />
            )}{' '}
            Add user
          </Button>
        </div>
      </div>
    </div>
  )
}
