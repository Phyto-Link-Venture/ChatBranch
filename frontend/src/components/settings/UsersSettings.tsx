'use client'

import { useCallback, useEffect, useState } from 'react'
import { KeyRound, Loader2, ShieldAlert, Tags, UserCheck, UserPlus, UserX } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Modal } from '@/components/ui/Modal'
import { DataTable, type Column } from '@/components/ui/DataTable'
import { roleHeaders } from '@/lib/role'
import { expireSessionOn401, getAuth, authHeaders } from '@/lib/auth'
import { useRole } from '@/lib/useRole'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8000/api'

interface UserEntity {
  id: number
  name: string
  slug: string
}

interface ManagedUser {
  id: number
  username: string
  name: string
  /** Free string now — built-ins (superadmin/admin/agent) PLUS any custom role
   *  slug from /api/auth/roles (custom roles pass, upgrade §A). */
  role: string
  active: boolean
  created: string | null
  last_login: string | null
  tenant: string | null
  entities: UserEntity[]
  /** Service Provider this login IS (crm.Resource). Bound = the user sees only
   *  that provider's own customers and chats. null/absent = unbound.
   *  Still honoured server-side; no longer edited from this table. */
  resource_id?: number | null
  resource_name?: string | null
  /** Contact this login IS — a registered customer who signs in to use the
   *  integrations connected against their own record. null/absent = unbound. */
  contact_id?: number | null
  contact_name?: string | null
}

/** /api/auth/roles row — the pool the role selectors choose from. */
interface RoleRow {
  slug: string
  label: string
  is_system: boolean
  user_count: number
}

/** /api/v1/entities row — the pool the brand-access picker chooses from. */
interface EntityRow {
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

const BUILTIN_ROLES = ['superadmin', 'admin', 'agent']

const FALLBACK_ROLE_LABEL: Record<string, string> = {
  superadmin: 'Super admin',
  admin: 'Admin',
  agent: 'Agent',
}

// superadmin = blue entity, admin = neutral zinc, agent = amber, custom = purple
const ROLE_CHIP: Record<string, string> = {
  superadmin: 'bg-brand-400/15 text-brand-200',
  admin: 'bg-fill-hover text-text-mid',
  agent: 'bg-amber/15 text-amber-300',
}
const CUSTOM_ROLE_CHIP = 'bg-purple/15 text-purple-300'

const humanize = (slug: string) =>
  FALLBACK_ROLE_LABEL[slug] ||
  slug.replace(/[-_]+/g, ' ').replace(/\b\w/g, (c) => c.toUpperCase())

/** Toggleable entity chips — used by both the add-user form and the per-user
 * brand-access editor. */
function EntityPicker({
  entities,
  selected,
  onToggle,
  showTenant,
}: {
  entities: EntityRow[]
  selected: number[]
  onToggle: (id: number) => void
  showTenant: boolean
}) {
  if (entities.length === 0) {
    return (
      <span className="text-[10.5px] text-text-muted">
        No entities yet — create one under Entities.
      </span>
    )
  }
  return (
    <div className="flex flex-wrap gap-1.5">
      {entities.map((b) => {
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
 * User management — admin+ (server-enforced: /api/v1/users). Creates operator
 * logins, changes roles, resets passwords and deactivates accounts. Password
 * resets and deactivation clear the target's bearer token on the server, so
 * their existing sessions die immediately. The backend refuses to demote or
 * deactivate the last remaining active super admin.
 *
 * Roles are dynamic (upgrade §A): the selectors list every Role.slug from
 * /api/auth/roles — built-ins plus any custom role — not a hardcoded 3. Custom
 * roles are agent-level for assignment tiering: an admin may assign agent + any
 * custom role, but never admin/superadmin.
 */
export function UsersSettings() {
  const { role: myRole, ready } = useRole()
  const me = getAuth()?.username
  const [users, setUsers] = useState<ManagedUser[] | null>(null)
  // Seed with the built-ins so the role selectors never flash empty before the
  // /api/auth/roles fetch (which adds any custom roles) resolves.
  const [roleOptions, setRoleOptions] = useState<RoleRow[]>(() =>
    BUILTIN_ROLES.map((slug) => ({ slug, label: humanize(slug), is_system: true, user_count: 0 })),
  )
  const [busy, setBusy] = useState(false)
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)
  // reset-password modal (one row at a time)
  const [pwFor, setPwFor] = useState<number | null>(null)
  const [pwDraft, setPwDraft] = useState('')
  // brand-access modal (agents)
  const [entitiesFor, setEntitiesFor] = useState<number | null>(null)
  const [entitiesDraft, setEntitiesDraft] = useState<number[]>([])
  // add-user modal
  const [showAdd, setShowAdd] = useState(false)
  // add-user form
  const [newUsername, setNewUsername] = useState('')
  const [newName, setNewName] = useState('')
  const [newPassword, setNewPassword] = useState('')
  const [newRole, setNewRole] = useState<string>('agent')
  const [newEntityIds, setNewEntityIds] = useState<number[]>([])
  // entity pool for the pickers (RBAC entities, /api/v1)
  const [entityPool, setEntityPool] = useState<EntityRow[]>([])
  // Contact pool for the customer-login binding
  const [contactPool, setContactPool] = useState<{ id: number; name: string; phone: string }[]>([])

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
    fetch(`${API}/v1/entities`, { headers: authHeaders() })
      .then((r) => (r.ok ? r.json() : { entities: [] }))
      .then((d) => setEntityPool(Array.isArray(d.entities) ? d.entities : []))
      .catch(() => setEntityPool([]))
    // Contacts to bind a customer login to.
    fetch(`${API}/contacts`, { headers: authHeaders() })
      .then((r) => (r.ok ? r.json() : { contacts: [] }))
      .then((d) => setContactPool(Array.isArray(d?.contacts) ? d.contacts : []))
      .catch(() => setContactPool([]))
    // Dynamic role pool — built-ins + custom roles. Falls back to the 3
    // built-ins if the roles endpoint predates the custom-roles pass.
    fetch(`${API}/auth/roles`, { headers: authHeaders() })
      .then((r) => (r.ok ? r.json() : { roles: [] }))
      .then((d) => {
        const rows: RoleRow[] = Array.isArray(d.roles) ? d.roles : []
        setRoleOptions(
          rows.length > 0
            ? rows
            : BUILTIN_ROLES.map((slug) => ({ slug, label: humanize(slug), is_system: true, user_count: 0 })),
        )
      })
      .catch(() =>
        setRoleOptions(BUILTIN_ROLES.map((slug) => ({ slug, label: humanize(slug), is_system: true, user_count: 0 }))),
      )
  }, [refresh])

  const multiTenant = new Set(entityPool.map((b) => b.tenant)).size > 1
  const toggleIn = (list: number[], id: number) =>
    list.includes(id) ? list.filter((x) => x !== id) : [...list, id]

  const roleLabel = (slug: string) => roleOptions.find((r) => r.slug === slug)?.label || humanize(slug)
  const roleChip = (slug: string) => ROLE_CHIP[slug] || CUSTOM_ROLE_CHIP
  /** Roles the CURRENT operator may assign. Superadmin: all. Admin: agent-level
   *  only (agent + any custom) — never admin/superadmin. */
  const assignableRoles = (): RoleRow[] =>
    (myRole === 'superadmin' ? roleOptions : roleOptions.filter((r) => r.slug !== 'admin' && r.slug !== 'superadmin'))

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
  /** Admins manage agent-level users (agent + custom), never other admins /
   *  superadmins (server-enforced too). */
  const canManage = (u: ManagedUser) => isSuper || (u.role !== 'admin' && u.role !== 'superadmin')

  const setRole = (u: ManagedUser, role: string) =>
    run(
      () => api(`users/${u.id}`, { method: 'POST', body: JSON.stringify({ role }) }),
      `Role updated — ${u.username} is now ${roleLabel(role).toLowerCase()}.`,
    )

  /** Bind (or unbind, with '') this login to a CONTACT — the customer whose
   *  own integrations this user connects. Same POST /users/<id> seam as the
   *  role change; errors surface in `msg`. */
  const setBoundContact = (u: ManagedUser, raw: string) => {
    const contactId = raw ? Number(raw) : null
    const target = contactPool.find((c) => c.id === contactId)
    run(
      () => api(`users/${u.id}`, { method: 'POST', body: JSON.stringify({ contact_id: contactId }) }),
      contactId === null
        ? `Unbound — ${u.username} is no longer linked to a contact.`
        : `Bound to ${target?.name ?? `contact #${contactId}`} — ${u.username} signs in as that customer.`,
    )
  }

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
      run(() => api(`users/${u.id}`, { method: 'DELETE' }), `${u.username} deactivated.`)
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
          entity_ids: newRole === 'agent' ? newEntityIds : [],
        }),
      })
      setNewUsername('')
      setNewName('')
      setNewPassword('')
      setNewRole('agent')
      setNewEntityIds([])
      setShowAdd(false)
    }, `User ${username} created.`)
  }

  const saveEntities = (u: ManagedUser) =>
    run(async () => {
      await api(`users/${u.id}`, {
        method: 'POST',
        body: JSON.stringify({ entity_ids: entitiesDraft }),
      })
      setEntitiesFor(null)
    }, `Entity access updated for ${u.username}.`)

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

  const columns: Column<ManagedUser>[] = [
    {
      key: 'user',
      header: 'User',
      sortable: true,
      value: (u) => `${u.username} ${u.name} ${u.tenant ?? ''}`,
      render: (u) => (
        <div className="min-w-0">
          <div className="flex items-center gap-2">
            <span className="text-[13px] font-semibold text-text-high">{u.username}</span>
            {u.username === me && <span className="text-[10.5px] font-bold text-text-muted">(you)</span>}
          </div>
          <div className="mt-0.5 text-[11px] text-text-muted">
            {u.name || '—'}
            {u.tenant && (
              <>
                {' · tenant '}
                <span className="font-semibold">{u.tenant}</span>
              </>
            )}
          </div>
        </div>
      ),
    },
    {
      key: 'role',
      header: 'Role',
      sortable: true,
      width: 150,
      value: (u) => roleLabel(u.role),
      render: (u) =>
        isSuper ? (
          <select
            value={u.role}
            disabled={busy}
            onChange={(e) => setRole(u, e.target.value)}
            aria-label={`Role for ${u.username}`}
            className="cursor-pointer rounded-[7px] border border-line-strong bg-surface px-2.5 py-1.5 text-[12px] font-semibold text-text-high outline-none"
          >
            {roleOptions.map((r) => (
              <option key={r.slug} value={r.slug}>
                {r.label}
              </option>
            ))}
          </select>
        ) : (
          <span className={`rounded-full px-2 py-px text-[10px] font-bold uppercase ${roleChip(u.role)}`}>
            {roleLabel(u.role)}
          </span>
        ),
    },
    {
      key: 'status',
      header: 'Status',
      sortable: true,
      width: 110,
      value: (u) => (u.active ? 'active' : 'deactivated'),
      render: (u) =>
        u.active ? (
          <span className="rounded-full bg-green/15 px-2 py-px text-[10px] font-bold text-green-300">Active</span>
        ) : (
          <span className="rounded-full bg-red/15 px-2 py-px text-[10px] font-bold text-red-300">Deactivated</span>
        ),
    },
    {
      key: 'entities',
      header: 'Entities',
      width: 190,
      render: (u) =>
        u.role !== 'agent' ? (
          <span className="text-[10.5px] text-text-faint">—</span>
        ) : u.entities.length === 0 ? (
          <span className="text-[10.5px] text-amber-300">No access</span>
        ) : (
          <div className="flex flex-wrap items-center gap-1">
            {u.entities.slice(0, 3).map((b) => (
              <span key={b.id} className="rounded-full bg-fill-hover px-2 py-px text-[10px] font-semibold text-text-mid">
                {b.name}
              </span>
            ))}
            {u.entities.length > 3 && <span className="text-[10px] text-text-faint">+{u.entities.length - 3}</span>}
          </div>
        ),
    },
    {
      key: 'contact',
      header: 'Contact',
      width: 210,
      value: (u) => u.contact_name ?? '',
      render: (u) =>
        !canManage(u) ? (
          u.contact_id ? (
            <span className={`rounded-full px-2 py-px text-[10px] font-bold ${CUSTOM_ROLE_CHIP}`}>
              {u.contact_name || `#${u.contact_id}`}
            </span>
          ) : (
            <span className="text-[10.5px] text-text-faint">—</span>
          )
        ) : (
          <select
            // The bound contact may be missing from the pool (another tenant,
            // list cap) — the fallback <option> keeps its name visible instead
            // of showing a blank select.
            value={u.contact_id ?? ''}
            disabled={busy}
            onChange={(e) => setBoundContact(u, e.target.value)}
            aria-label={`Contact for ${u.username}`}
            className="w-full cursor-pointer rounded-[7px] border border-line-strong bg-surface px-2.5 py-1.5 text-[12px] font-semibold text-text-high outline-none"
          >
            <option value="">— none —</option>
            {u.contact_id != null && !contactPool.some((c) => c.id === u.contact_id) && (
              <option value={u.contact_id}>{u.contact_name || `#${u.contact_id}`}</option>
            )}
            {contactPool.map((c) => (
              <option key={c.id} value={c.id}>
                {c.name}
                {c.phone ? ` · ${c.phone}` : ''}
              </option>
            ))}
          </select>
        ),
    },
    {
      key: 'last_login',
      header: 'Last login',
      sortable: true,
      width: 150,
      value: (u) => u.last_login ?? '',
      render: (u) => (
        <span className="text-[11px] text-text-muted">
          {u.last_login ? new Date(u.last_login).toLocaleString() : 'never'}
        </span>
      ),
    },
    {
      key: 'actions',
      header: 'Actions',
      align: 'right',
      render: (u) => (
        // Compact icon actions (with tooltips) so the row never wraps — matches
        // the other tables (Contacts/Entities/Templates).
        <div className="flex items-center justify-end gap-1">
          {u.role === 'agent' && (
            <button
              disabled={busy}
              onClick={() => {
                setMsg(null)
                setEntitiesDraft(u.entities.map((b) => b.id))
                setEntitiesFor(u.id)
              }}
              title="Entity access"
              aria-label={`Entity access for ${u.username}`}
              className="grid h-7 w-7 shrink-0 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high disabled:opacity-50"
            >
              <Tags size={14} aria-hidden />
            </button>
          )}
          {canManage(u) && (
            <>
              <button
                disabled={busy}
                onClick={() => {
                  setMsg(null)
                  setPwDraft('')
                  setPwFor(u.id)
                }}
                title="Reset password"
                aria-label={`Reset password for ${u.username}`}
                className="grid h-7 w-7 shrink-0 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high disabled:opacity-50"
              >
                <KeyRound size={14} aria-hidden />
              </button>
              <button
                disabled={busy}
                onClick={() => toggleActive(u)}
                title={u.active ? 'Deactivate' : 'Reactivate'}
                aria-label={`${u.active ? 'Deactivate' : 'Reactivate'} ${u.username}`}
                className={`grid h-7 w-7 shrink-0 cursor-pointer place-items-center rounded-lg transition-colors hover:bg-fill-hover disabled:opacity-50 ${
                  u.active ? 'text-text-muted hover:text-red-300' : 'text-text-muted hover:text-green-300'
                }`}
              >
                {u.active ? <UserX size={14} aria-hidden /> : <UserCheck size={14} aria-hidden />}
              </button>
            </>
          )}
        </div>
      ),
    },
  ]

  return (
    <div className="flex flex-col gap-4 pt-3.5">
      <p className="text-[11.5px] leading-relaxed text-text-mid">
        Operator logins for this dashboard. Passwords are set here — a password reset or deactivation clears that
        user&apos;s session token, signing them out everywhere immediately. The last active super admin can never be
        demoted or deactivated.
        {!isSuper && ' As an admin you can create and manage agent-level users in your own tenant.'}
      </p>

      {/* what each role (user group) can do — the built-in capability matrix */}
      <div className="grid gap-2 sm:grid-cols-3">
        {(
          [
            {
              lbl: 'Super admin', chip: ROLE_CHIP.superadmin,
              items: ['Everything, across all tenants', 'App keys & global settings', 'Creates admins & agents', 'Entities in any tenant'],
            },
            {
              lbl: 'Admin', chip: ROLE_CHIP.admin,
              items: ['Own tenant only', 'Channel accounts, routing, flows, templates', 'Creates agent-level users + entity access', 'No app keys / global settings'],
            },
            {
              lbl: 'Agent', chip: ROLE_CHIP.agent,
              items: ['Work queues: chats, calls, contacts', 'Only contacts on their assigned entities', 'No settings management'],
            },
          ] as { lbl: string; chip: string; items: string[] }[]
        ).map((c) => (
          <div key={c.lbl} className="rounded-lg border border-line bg-fill p-3">
            <span className={`rounded-full px-2 py-px text-[10px] font-bold uppercase ${c.chip}`}>{c.lbl}</span>
            <ul className="mt-2 flex flex-col gap-1 text-[11px] leading-relaxed text-text-mid">
              {c.items.map((i) => (
                <li key={i}>· {i}</li>
              ))}
            </ul>
          </div>
        ))}
      </div>
      <p className="-mt-1 text-[10.5px] leading-relaxed text-text-muted">
        Custom roles (managed under Permissions) are agent-level for assignment and use whatever capabilities the
        permission matrix grants them.
      </p>

      <p className="-mt-1 text-[10.5px] leading-relaxed text-text-muted">
        Binding a login to a Service Provider scopes that user to their own customers and chats. Pair with the Provider
        role.
      </p>

      {/* user table */}
      <DataTable
        columns={columns}
        rows={users}
        rowKey={(u) => u.id}
        searchKeys={['user', 'role', 'provider']}
        searchPlaceholder="Search users…"
        empty="No users yet."
        toolbarRight={
          <Button
            onClick={() => {
              setMsg(null)
              setShowAdd(true)
            }}
          >
            <UserPlus size={13} aria-hidden /> Add user
          </Button>
        }
      />

      {/* Failures raised while a modal is open render inside that modal (below);
          the scrim would otherwise hide them. Successes always show here. */}
      {msg && (msg.ok || !(showAdd || pwFor !== null || entitiesFor !== null)) && (
        <div className={`text-[12px] font-semibold ${msg.ok ? 'text-green-300' : 'text-red-300'}`}>{msg.text}</div>
      )}

      {/* add user modal */}
      <Modal
        open={showAdd}
        onClose={() => setShowAdd(false)}
        title="Add user"
        subtitle="Create an operator login for this dashboard."
        size="lg"
        footer={
          <>
            <Button variant="ghost" onClick={() => setShowAdd(false)} disabled={busy}>
              Cancel
            </Button>
            <Button variant="primary" onClick={addUser} disabled={busy}>
              {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <UserPlus size={13} aria-hidden />}{' '}
              Add user
            </Button>
          </>
        }
      >
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
          {msg && !msg.ok && (
            <div
              role="alert"
              className="sm:col-span-2 rounded-lg border border-red-500/30 bg-red-500/10 px-3 py-2 text-[12px] font-semibold text-red-300"
            >
              {msg.text}
            </div>
          )}
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
              onChange={(e) => setNewRole(e.target.value)}
              className="w-full cursor-pointer rounded-lg border border-line-strong bg-card px-3 py-2 text-[12.5px] font-semibold text-text-high outline-none"
            >
              {assignableRoles().map((r) => (
                <option key={r.slug} value={r.slug}>
                  {r.label}
                </option>
              ))}
            </select>
            <span className="mt-1 block text-[10.5px] leading-relaxed text-text-muted">
              Super admin: everything · Admin: own tenant, all entities · Agent &amp; custom: assigned entities only.
            </span>
          </label>
        </div>
        {newRole === 'agent' && (
          <div className="mt-3">
            <span className="mb-1.5 block text-[11px] font-bold text-text-high">Entity access</span>
            <EntityPicker
              entities={entityPool}
              selected={newEntityIds}
              onToggle={(id) => setNewEntityIds((d) => toggleIn(d, id))}
              showTenant={multiTenant}
            />
            <span className="mt-1.5 block text-[10.5px] leading-relaxed text-text-muted">
              An agent only sees contacts on their assigned entities (all entities must belong to one tenant). You can
              change this later with the Entities button on their row.
            </span>
          </div>
        )}
      </Modal>

      {/* reset-password modal */}
      {(() => {
        const pwUser = pwFor !== null ? users.find((u) => u.id === pwFor) : undefined
        return (
          <Modal
            open={!!pwUser}
            onClose={() => {
              setPwFor(null)
              setPwDraft('')
            }}
            title={pwUser ? `Reset password — ${pwUser.username}` : 'Reset password'}
            subtitle="Setting a new password signs the user out everywhere immediately."
            size="sm"
            footer={
              <>
                <Button
                  variant="ghost"
                  onClick={() => {
                    setPwFor(null)
                    setPwDraft('')
                  }}
                >
                  Cancel
                </Button>
                <Button
                  variant="primary"
                  disabled={busy || pwDraft.length < 8}
                  onClick={() => pwUser && resetPassword(pwUser)}
                >
                  {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : null} Set password
                </Button>
              </>
            }
          >
            {msg && !msg.ok && (
              <div
                role="alert"
                className="mb-3 block rounded-lg border border-red-500/30 bg-red-500/10 px-3 py-2 text-[12px] font-semibold text-red-300"
              >
                {msg.text}
              </div>
            )}
            {pwUser && (
              <label className="block">
                <span className="mb-1 block text-[11px] font-bold text-text-high">New password</span>
                <input
                  type="password"
                  value={pwDraft}
                  onChange={(e) => setPwDraft(e.target.value)}
                  onKeyDown={(e) => e.key === 'Enter' && resetPassword(pwUser)}
                  placeholder={`New password for ${pwUser.username} (min 8 characters)`}
                  autoComplete="new-password"
                  aria-label={`New password for ${pwUser.username}`}
                  className={inputCls}
                />
              </label>
            )}
          </Modal>
        )
      })()}

      {/* brand-access modal */}
      {(() => {
        const entitiesUser = entitiesFor !== null ? users.find((u) => u.id === entitiesFor) : undefined
        return (
          <Modal
            open={!!entitiesUser}
            onClose={() => setEntitiesFor(null)}
            title={entitiesUser ? `Entity access — ${entitiesUser.username}` : 'Entity access'}
            subtitle="An agent only sees contacts on their assigned entities."
            size="md"
            footer={
              <>
                <Button variant="ghost" onClick={() => setEntitiesFor(null)}>
                  Cancel
                </Button>
                <Button variant="primary" disabled={busy} onClick={() => entitiesUser && saveEntities(entitiesUser)}>
                  {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : null} Save entity access
                </Button>
              </>
            }
          >
            {msg && !msg.ok && (
              <div
                role="alert"
                className="mb-3 block rounded-lg border border-red-500/30 bg-red-500/10 px-3 py-2 text-[12px] font-semibold text-red-300"
              >
                {msg.text}
              </div>
            )}
            {entitiesUser && (
              <EntityPicker
                entities={entityPool.filter((b) => !entitiesUser.tenant || b.tenant === entitiesUser.tenant)}
                selected={entitiesDraft}
                onToggle={(id) => setEntitiesDraft((d) => toggleIn(d, id))}
                showTenant={multiTenant && !entitiesUser.tenant}
              />
            )}
          </Modal>
        )
      })()}
    </div>
  )
}
