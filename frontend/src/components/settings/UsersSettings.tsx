'use client'

import { API } from '@/lib/apiBase'
import Link from 'next/link'
import { useCallback, useEffect, useMemo, useState } from 'react'
import {
  AtSign,
  Building2,
  CircleCheck,
  KeyRound,
  Loader2,
  MailPlus,
  Send,
  ShieldAlert,
  ShieldCheck,
  ShieldOff,
  UserCheck,
  UserPlus,
  UserX,
} from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Modal } from '@/components/ui/Modal'
import { Select, type SelectOption } from '@/components/ui/Select'
import { DataTable, type Column } from '@/components/ui/DataTable'
import { EntityAccessGrid } from '@/components/bindings/EntityAccessGrid'
import { orderEntityTree } from '@/components/bindings/groups'
import { roleHeaders } from '@/lib/role'
import { expireSessionOn401, getAuth, authHeaders, roleDisplayName } from '@/lib/auth'
import { useRole } from '@/lib/useRole'
import { useAccess } from '@/lib/useAccess'
import { useCompanyAdmin } from '@/lib/useCompanyAdmin'
import { useFormat, useT, type MessageKey } from '@/i18n'
import type { InviteLink, InviteOutcome, InviteReadiness } from '@/lib/systemEmail'

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
  /** An admin bound to entities: manages only those companies (and branches). */
  company_admin?: boolean
  /** Contact this login IS — a registered customer who signs in to use the
   *  integrations connected against their own record. null/absent = unbound. */
  contact_id?: number | null
  contact_name?: string | null
  /** Two-step sign-in is on — a users.manage holder may reset it (a lost phone). */
  two_factor?: boolean
  /** Where the welcome email / a password reset link goes ("" = none). */
  email?: string
  /** false = invited, still waiting for the person to choose a password through the link. */
  password_set?: boolean
  /** The newest set-password link not yet used (pending or expired). */
  invite_link?: InviteLink | null
}

/** A v1 refusal; `reason` is the password policy's stable code (translated by the page). */
class V1Error extends Error {
  constructor(
    message: string,
    public reason?: string,
    public min?: number,
    /** The v1 envelope's stable `error.code` (invite refusals, conflicts…). */
    public code?: string,
    /** The answer's `invite` outcome, when the request sent (or tried to send) an email. */
    public invite?: InviteOutcome,
  ) {
    super(message)
  }
}

/** /api/auth/roles row — the pool the role selectors choose from. */
interface RoleRow {
  slug: string
  label: string
  is_system: boolean
  user_count: number
}

/** /api/v1/entities row — the pool the entity-access grid chooses from. */
interface EntityRow {
  id: number
  name: string
  slug: string
  tenant: string
  parent_id: number | null
  is_active: boolean
}

async function api<T>(path: string, init?: RequestInit): Promise<T> {
  // Canonical user management lives at /api/v1/users. Errors use the v1
  // envelope {error:{message}}; we also read legacy {detail} for safety.
  const res = await fetch(`${API}/v1/${path}`, {
    headers: { 'Content-Type': 'application/json', ...roleHeaders() },
    ...init,
  })
  const data = await res.json().catch(() => ({}))
  if (expireSessionOn401(res)) throw new Error('Session expired — redirecting to login…')
  if (!res.ok) {
    const d = data as {
      error?: { message?: string; reason?: string; min?: number; code?: string }
      detail?: string
      code?: string
      invite?: InviteOutcome
    }
    throw new V1Error(
      d.error?.message || d.detail || `HTTP ${res.status}`,
      d.error?.reason,
      d.error?.min,
      d.error?.code ?? d.code,
      d.invite,
    )
  }
  return data as T
}

const inputCls =
  'h-9 w-full rounded-lg border border-line-strong bg-card px-3 text-[13px] text-text-primary outline-none transition-colors placeholder:text-text-faint focus:border-text-primary'

const BUILTIN_ROLES = ['superadmin', 'admin', 'agent']

// superadmin = ink, admin = neutral, agent = ochre, custom = plum
const ROLE_CHIP: Record<string, string> = {
  superadmin: 'bg-primary text-on-primary',
  admin: 'border border-line-strong text-text-high',
  agent: 'bg-amber/15 text-[color:var(--amber-ink)]',
}
const CUSTOM_ROLE_CHIP = 'bg-purple/15 text-[color:var(--purple-ink)]'

/** What each built-in role can do — the three cards above the table. */
const ROLE_CARDS: { slug: string; items: MessageKey[] }[] = [
  {
    slug: 'superadmin',
    items: ['admin.users.cards.superadmin1', 'admin.users.cards.superadmin2', 'admin.users.cards.superadmin3'],
  },
  {
    slug: 'admin',
    items: ['admin.users.cards.admin1', 'admin.users.cards.admin2', 'admin.users.cards.admin3'],
  },
  {
    slug: 'agent',
    items: ['admin.users.cards.agent1', 'admin.users.cards.agent2', 'admin.users.cards.agent3'],
  },
]

/** The server's welcome-email refusal codes (InviteMailer / POST /api/v1/users/{id}/invite). */
const INVITE_PROBLEMS: Record<string, MessageKey> = {
  email_not_configured: 'admin.users.invite.unavailable.email_not_configured',
  no_login_address: 'admin.users.invite.unavailable.no_login_address',
  localhost_login_address: 'admin.users.invite.unavailable.localhost_login_address',
  no_email: 'admin.users.invite.errors.no_email',
  inactive: 'admin.users.invite.errors.inactive',
  password_already_set: 'admin.users.invite.errors.password_already_set',
  too_many_invites: 'admin.users.invite.errors.too_many_invites',
  invite_needs_email: 'admin.users.invite.errors.invite_needs_email',
}

const builtinRows = (): RoleRow[] => BUILTIN_ROLES.map((slug) => ({ slug, label: '', is_system: true, user_count: 0 }))

function Field({
  label,
  value,
  onChange,
  placeholder,
  hint,
  secret = false,
  type = 'text',
}: {
  label: string
  value: string
  onChange: (v: string) => void
  placeholder?: string
  hint?: string
  secret?: boolean
  type?: 'text' | 'email'
}) {
  return (
    <label className="block">
      <span className="mb-1.5 block text-[11.5px] font-bold text-text-high">{label}</span>
      <input
        type={secret ? 'password' : type}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        placeholder={placeholder}
        // "new-password" (not "off", which browsers ignore on password fields)
        // stops Chrome autofilling the operator's own saved login here — that
        // ghost-fill left React state empty and the Add button dead-grey
        autoComplete={secret ? 'new-password' : 'off'}
        className={inputCls}
      />
      {hint && <span className="mt-1 block text-[11px] leading-relaxed text-text-muted">{hint}</span>}
    </label>
  )
}

function ErrorBox({ text }: { text: string }) {
  return (
    <div
      role="alert"
      className="rounded-lg border border-red/30 bg-red/10 px-3 py-2 text-[12.5px] font-semibold text-[color:var(--red-ink)]"
    >
      {text}
    </div>
  )
}

/**
 * User management — users.manage (server-enforced: /api/v1/users). Creates operator
 * logins, changes roles, resets passwords and deactivates accounts. Password
 * resets and deactivation clear the target's bearer token on the server, so
 * their existing sessions die immediately. The backend refuses to demote or
 * deactivate the last remaining active super admin.
 *
 * Roles are dynamic (upgrade §A): the selectors list every Role.slug from
 * /api/auth/roles — built-ins plus any custom role — not a hardcoded 3. Custom
 * roles are agent-level for assignment tiering: an admin may assign agent + any
 * custom role, but never admin/superadmin.
 *
 * Entity access uses the Permissions-page card pattern: one card per main
 * entity holding it and its sub-entities.
 */

/** Agent and every custom role: bound to the entities they're given. */
const agentLevel = (role: string) => role !== 'admin' && role !== 'superadmin'
/** Roles that take entity access: agent-level, and admin (entities = a COMPANY admin; none = organization-wide). */
const takesEntities = (role: string) => role !== 'superadmin'

export function UsersSettings() {
  const t = useT()
  const f = useFormat()
  const { role: myRole, ready } = useRole()
  // the page's gate is the capability; the role only mirrors the server's user tiers below
  const { can } = useAccess()
  const [me, setMe] = useState<string | undefined>(undefined)
  useEffect(() => setMe(getAuth()?.username), [])
  // am I a COMPANY admin (an admin bound to entities)? — read after mount
  const iAmCompanyAdmin = useCompanyAdmin()
  const [users, setUsers] = useState<ManagedUser[] | null>(null)
  // Seed with the built-ins so the role selectors never flash empty before the
  // /api/auth/roles fetch (which adds any custom roles) resolves.
  const [roleOptions, setRoleOptions] = useState<RoleRow[]>(builtinRows)
  const [busy, setBusy] = useState(false)
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)
  // reset-password modal (one row at a time)
  const [pwFor, setPwFor] = useState<number | null>(null)
  const [pwDraft, setPwDraft] = useState('')
  // entity-access modal (agents)
  const [entitiesFor, setEntitiesFor] = useState<number | null>(null)
  const [entitiesDraft, setEntitiesDraft] = useState<number[]>([])
  // add-user modal
  const [showAdd, setShowAdd] = useState(false)
  const [newUsername, setNewUsername] = useState('')
  const [newName, setNewName] = useState('')
  const [newPassword, setNewPassword] = useState('')
  const [newRole, setNewRole] = useState<string>('agent')
  const [newEntityIds, setNewEntityIds] = useState<number[]>([])
  const [newEmail, setNewEmail] = useState('')
  // the welcome email: on by default whenever one can go out (seeded when the form opens)
  const [sendInvite, setSendInvite] = useState(false)
  // after "Add user": who was created and what happened to the welcome email (shown in the modal)
  const [created, setCreated] = useState<{ username: string; invite: InviteOutcome } | null>(null)
  // can a welcome email go out right now (GET /api/v1/users `invites`)
  const [invites, setInvites] = useState<InviteReadiness | null>(null)
  // email modal (add / change a user's address)
  const [emailFor, setEmailFor] = useState<number | null>(null)
  const [emailDraft, setEmailDraft] = useState('')
  // entity pool for the access grid (RBAC entities, /api/v1)
  const [entityPool, setEntityPool] = useState<EntityRow[]>([])
  // Contact pool for the customer-login binding
  const [contactPool, setContactPool] = useState<{ id: number; name: string; phone: string }[]>([])

  const refresh = useCallback(async () => {
    try {
      const r = await api<{ users: ManagedUser[]; invites?: InviteReadiness }>('users')
      setUsers(r.users)
      setInvites(r.invites ?? null)
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
        setRoleOptions(rows.length > 0 ? rows : builtinRows())
      })
      .catch(() => setRoleOptions(builtinRows()))
  }, [refresh])

  const roleLabel = (slug: string) => roleDisplayName(t, slug, roleOptions.find((r) => r.slug === slug)?.label)
  const roleChip = (slug: string) => ROLE_CHIP[slug] || CUSTOM_ROLE_CHIP
  /** Roles the CURRENT operator may assign. Superadmin: all. An organization-wide
   *  admin: agent-level + admin (a COMPANY admin — companies required). A company
   *  admin: agent-level only. Never superadmin. */
  const orgWideMe = myRole === 'superadmin' || (myRole === 'admin' && !iAmCompanyAdmin)
  const assignableRoles = (): RoleRow[] =>
    myRole === 'superadmin'
      ? roleOptions
      : roleOptions.filter((r) => r.slug !== 'superadmin' && (r.slug !== 'admin' || orgWideMe))

  const roleSelectOptions: SelectOption[] = roleOptions.map((r) => ({
    value: r.slug,
    label: roleLabel(r.slug),
    group: r.is_system ? t('admin.users.builtinRoles') : t('admin.users.customRoles'),
  }))

  const contactOptions = useMemo<SelectOption[]>(
    () => [
      { value: '', label: t('admin.users.noContact') },
      ...contactPool.map((c) => ({ value: String(c.id), label: c.name, hint: c.phone })),
    ],
    [contactPool, t],
  )

  const run = async (fn: () => Promise<unknown>, okText?: string) => {
    setBusy(true)
    setMsg(null)
    try {
      await fn()
      await refresh()
      if (okText) setMsg({ ok: true, text: okText })
    } catch (e) {
      setMsg({ ok: false, text: explain(e) })
    } finally {
      setBusy(false)
    }
  }

  /** A refusal in the reader's language: the password policy, the welcome-email codes, else the server's words. */
  const explain = (e: unknown): string => {
    if (e instanceof V1Error) {
      if (e.reason) return passwordReason(e)
      const invite = inviteProblem(e.code, e.invite?.error ?? undefined)
      if (invite) return invite
      if (e.code === 'conflict' && /email/i.test(e.message)) return t('admin.users.emailTaken')
      if (e.code === 'bad_request' && /email address/i.test(e.message)) return t('admin.users.emailInvalid')
    }
    return e instanceof Error ? e.message : String(e)
  }

  /** Why a welcome email / reset link can't go (or didn't go) out — null for other codes. */
  const inviteProblem = (code: string | null | undefined, error?: string): string | null => {
    if (!code) return null
    if (code === 'send_failed') return t('admin.users.invite.errors.send_failed', { error: error ?? '' })
    const key = INVITE_PROBLEMS[code]
    return key ? t(key) : null
  }

  /** A refused new password, in the reader's language (the server's policy code). */
  const passwordReason = (e: V1Error): string => {
    switch (e.reason) {
      case 'password_too_short':
        return t('admin.users.passwordTooShortMin', { min: e.min ?? 8 })
      case 'password_common':
        return t('admin.users.passwordCommon')
      case 'password_is_username':
        return t('admin.users.passwordIsUsername')
      default:
        return e.message
    }
  }

  const resetTwoFactor = (u: ManagedUser) => {
    if (!window.confirm(t('admin.users.confirmResetTwoFactor', { user: u.username }))) return
    run(() => api(`users/${u.id}/2fa/reset`, { method: 'POST' }), t('admin.users.twoFactorReset', { user: u.username }))
  }

  const isSuper = myRole === 'superadmin'
  /** Admins manage agent-level users (agent + custom), never other admins /
   *  superadmins (server-enforced too). */
  const canManage = (u: ManagedUser) =>
    isSuper ||
    // nobody below superadmin edits their own login here (server-enforced too)
    (u.username !== me && (agentLevel(u.role) || (u.role === 'admin' && !!u.company_admin && orgWideMe)))

  const setRole = (u: ManagedUser, role: string) =>
    run(
      () => api(`users/${u.id}`, { method: 'POST', body: JSON.stringify({ role }) }),
      t('admin.users.roleUpdated', { user: u.username, role: roleLabel(role) }),
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
        ? t('admin.users.unbound', { user: u.username })
        : t('admin.users.bound', {
            contact: target?.name ?? t('admin.users.contactN', { id: contactId }),
            user: u.username,
          }),
    )
  }

  const resetPassword = (u: ManagedUser) => {
    if (pwDraft.length < 8) {
      setMsg({ ok: false, text: t('admin.users.passwordTooShort') })
      return
    }
    const password = pwDraft
    run(async () => {
      await api(`users/${u.id}`, { method: 'POST', body: JSON.stringify({ password }) })
      setPwFor(null)
      setPwDraft('')
    }, t('admin.users.passwordReset', { user: u.username }))
  }

  const toggleActive = (u: ManagedUser) => {
    if (u.active) {
      if (!window.confirm(t('admin.users.confirmDeactivate', { user: u.username }))) return
      run(() => api(`users/${u.id}`, { method: 'DELETE' }), t('admin.users.deactivatedMsg', { user: u.username }))
    } else {
      run(
        () => api(`users/${u.id}`, { method: 'POST', body: JSON.stringify({ active: true }) }),
        t('admin.users.reactivatedMsg', { user: u.username }),
      )
    }
  }

  /** Open "Add user" with an empty form — the welcome email on whenever one can go out. */
  const openAdd = () => {
    setMsg(null)
    setCreated(null)
    setNewUsername('')
    setNewName('')
    setNewEmail('')
    setNewPassword('')
    setNewRole('agent')
    setNewEntityIds([])
    setSendInvite(!!invites?.ready)
    setShowAdd(true)
  }

  const addUser = () => {
    // validate on click with explicit messages — a silently disabled button
    // reads as "broken", especially when browser autofill ghost-fills the form
    const username = newUsername.trim()
    const email = newEmail.trim()
    const invite = sendInvite && !!invites?.ready
    if (!username) {
      setMsg({ ok: false, text: t('admin.users.usernameRequired') })
      return
    }
    if (invite && !email) {
      setMsg({ ok: false, text: t('admin.users.invite.needsEmail') })
      return
    }
    if (email && !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email)) {
      setMsg({ ok: false, text: t('admin.users.emailInvalid') })
      return
    }
    if (!invite && newPassword.length < 8) {
      setMsg({ ok: false, text: t('admin.users.passwordTooShort') })
      return
    }
    if (newRole === 'admin' && !isSuper && newEntityIds.length === 0) {
      setMsg({ ok: false, text: t('admin.users.adminEntityRequired') })
      return
    }
    // the modal stays open on the outcome: sent to … / not sent because …
    run(async () => {
      const r = await api<ManagedUser & { invite?: InviteOutcome }>('users', {
        method: 'POST',
        body: JSON.stringify({
          username,
          name: newName.trim(),
          email,
          send_invite: invite,
          ...(invite ? {} : { password: newPassword }),
          role: newRole,
          entity_ids: takesEntities(newRole) ? newEntityIds : [],
        }),
      })
      setCreated({ username: r.username, invite: r.invite ?? { requested: false, sent: false } })
      setNewPassword('')
    })
  }

  /** Resend the welcome email (no password yet) or, with `reset`, a password reset link. */
  const sendLink = (u: ManagedUser, reset: boolean) =>
    run(async () => {
      await api<ManagedUser & { invite?: InviteOutcome }>(`users/${u.id}/invite`, {
        method: 'POST',
        body: JSON.stringify({ reset }),
      })
      if (reset) {
        setPwFor(null)
        setPwDraft('')
      }
    }, reset
      ? t('admin.users.invite.resetLinkSent', { email: u.email ?? '' })
      : t('admin.users.invite.resent', { email: u.email ?? '' }))

  const saveEmail = (u: ManagedUser) => {
    const email = emailDraft.trim()
    if (email && !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email)) {
      setMsg({ ok: false, text: t('admin.users.emailInvalid') })
      return
    }
    run(async () => {
      await api(`users/${u.id}`, { method: 'POST', body: JSON.stringify({ email }) })
      setEmailFor(null)
    }, t('admin.users.emailSaved', { user: u.username }))
  }

  const openEmail = (u: ManagedUser) => {
    setMsg(null)
    setEmailDraft(u.email ?? '')
    setEmailFor(u.id)
  }

  /** The welcome-email state of a row: waiting for the first password, and how long the link lasts. */
  const inviteState = (u: ManagedUser) => {
    const link = u.invite_link
    if (u.password_set !== false) {
      return link?.status === 'pending' && link.purpose === 'reset'
        ? t('admin.users.invite.resetPending', { date: f.dateTime(link.expires_at) })
        : null
    }
    if (!link) return null
    return link.status === 'pending'
      ? t('admin.users.invite.validUntil', { date: f.dateTime(link.expires_at) })
      : t('admin.users.invite.expired')
  }

  const saveEntities = (u: ManagedUser) =>
    run(async () => {
      await api(`users/${u.id}`, {
        method: 'POST',
        body: JSON.stringify({ entity_ids: entitiesDraft }),
      })
      setEntitiesFor(null)
    }, t('admin.users.entityAccessUpdated', { user: u.username }))

  // entity name lookup with the tree's main/sub knowledge, for the table chips
  const entityDepth = useMemo(
    () => new Map(orderEntityTree(entityPool).map((o) => [o.row.id, o.depth])),
    [entityPool],
  )

  if (!ready) return null // role resolves after mount — don't flash the denial

  if (!can('users.manage') && !can('users.view')) {
    return (
      <div className="flex items-center gap-2 py-6 text-[13px] text-text-muted">
        <ShieldAlert size={15} className="text-[color:var(--amber-ink)]" aria-hidden />
        {t('admin.users.adminsOnly')}
      </div>
    )
  }

  if (users === null) {
    return (
      <div className="py-6 text-[13px] text-text-muted">{t('admin.users.unavailable')}</div>
    )
  }

  const columns: Column<ManagedUser>[] = [
    {
      key: 'user',
      header: t('admin.users.col.user'),
      sortable: true,
      value: (u) => `${u.username} ${u.name} ${u.tenant ?? ''}`,
      render: (u) => (
        <div className="min-w-0">
          <div className="flex items-center gap-2">
            <span className="text-[13.5px] font-semibold text-text-heading">{u.username}</span>
            {u.username === me && <span className="text-[11px] font-semibold text-text-muted">{t('admin.users.you')}</span>}
            {u.company_admin && (
              <span className="rounded-full bg-brand-400/10 px-1.5 py-px text-[10.5px] font-semibold text-[color:var(--brand-ink)]">
                {t('admin.users.companyAdmin')}
              </span>
            )}
            {u.two_factor && (
              <span title={t('admin.users.twoFactorOn')} className="text-[color:var(--green-ink)]">
                <ShieldCheck size={13} aria-label={t('admin.users.twoFactorOn')} />
              </span>
            )}
          </div>
          {u.name && u.name !== u.username && <div className="mt-0.5 text-[11.5px] text-text-muted">{u.name}</div>}
          {u.email ? (
            <div className="mt-0.5 flex items-center gap-1 text-[11.5px] text-text-muted">
              <span className="truncate">{u.email}</span>
              {canManage(u) && (
                <button
                  type="button"
                  disabled={busy}
                  onClick={() => openEmail(u)}
                  title={t('admin.users.emailEdit')}
                  aria-label={t('admin.users.emailFor', { user: u.username })}
                  className="grid h-5 w-5 shrink-0 cursor-pointer place-items-center rounded text-text-faint hover:bg-fill-hover hover:text-text-high disabled:opacity-50"
                >
                  <AtSign size={11} aria-hidden />
                </button>
              )}
            </div>
          ) : (
            canManage(u) && (
              <button
                type="button"
                disabled={busy}
                onClick={() => openEmail(u)}
                aria-label={t('admin.users.emailFor', { user: u.username })}
                className="mt-0.5 cursor-pointer text-[11.5px] font-semibold text-text-mid underline-offset-2 hover:text-text-high hover:underline disabled:opacity-50"
              >
                {t('admin.users.emailAdd')}
              </button>
            )
          )}
        </div>
      ),
    },
    {
      key: 'role',
      header: t('admin.users.col.role'),
      sortable: true,
      width: 170,
      value: (u) => roleLabel(u.role),
      render: (u) =>
        isSuper ? (
          <Select
            value={u.role}
            disabled={busy}
            onChange={(v) => v !== u.role && setRole(u, v)}
            options={roleSelectOptions}
            ariaLabel={t('admin.users.roleFor', { user: u.username })}
            className="w-[150px]"
          />
        ) : (
          <span className={`rounded-full px-2 py-0.5 text-[11px] font-semibold ${roleChip(u.role)}`}>
            {roleLabel(u.role)}
          </span>
        ),
    },
    {
      key: 'status',
      header: t('common.fields.status'),
      sortable: true,
      width: 150,
      value: (u) =>
        !u.active
          ? t('admin.users.deactivated')
          : u.password_set === false
            ? t('admin.users.invite.waiting')
            : t('common.status.active'),
      render: (u) =>
        u.active && u.password_set === false ? (
          <div className="min-w-0">
            <span
              title={t('admin.users.invite.waiting')}
              className="inline-flex items-center gap-1.5 rounded-full bg-amber/15 px-2 py-0.5 text-[11px] font-semibold text-[color:var(--amber-ink)]"
            >
              <MailPlus size={11} aria-hidden /> {t('admin.users.invite.waitingShort')}
            </span>
            {inviteState(u) && <div className="mt-1 text-[11px] leading-snug text-text-muted">{inviteState(u)}</div>}
          </div>
        ) : u.active ? (
          <div className="min-w-0">
            <span className="inline-flex items-center gap-1.5 text-[12px] font-semibold text-[color:var(--green-ink)]">
              <span className="h-1.5 w-1.5 rounded-full bg-green" aria-hidden /> {t('common.status.active')}
            </span>
            {inviteState(u) && <div className="mt-1 text-[11px] leading-snug text-text-muted">{inviteState(u)}</div>}
          </div>
        ) : (
          <span className="inline-flex items-center gap-1.5 text-[12px] font-semibold text-[color:var(--red-ink)]">
            <span className="h-1.5 w-1.5 rounded-full bg-red" aria-hidden /> {t('admin.users.deactivated')}
          </span>
        ),
    },
    {
      key: 'entities',
      header: t('admin.users.entityAccess'),
      width: 220,
      value: (u) => u.entities.map((e) => e.name).join(' '),
      render: (u) =>
        u.role === 'superadmin' || (u.role === 'admin' && u.entities.length === 0) ? (
          <span className="text-[12px] text-text-muted">{t('admin.users.allEntities')}</span>
        ) : u.entities.length === 0 ? (
          <span className="text-[12px] text-text-muted">{t('admin.users.wholeOrg')}</span>
        ) : (
          <div className="flex flex-wrap items-center gap-1">
            {u.entities.slice(0, 3).map((b) => (
              <span
                key={b.id}
                className="inline-flex items-center gap-1 rounded-md border border-line bg-well px-1.5 py-px text-[11px] font-semibold text-text-mid"
              >
                {entityDepth.get(b.id) === 0 && (
                  <span className="rounded-sm bg-primary px-1 text-[8.5px] font-bold uppercase tracking-[0.05em] text-on-primary">
                    {t('admin.bindings.groups.main')}
                  </span>
                )}
                {b.name}
              </span>
            ))}
            {u.entities.length > 3 && <span className="text-[11px] text-text-faint">+{u.entities.length - 3}</span>}
          </div>
        ),
    },
    {
      key: 'contact',
      header: t('admin.users.col.contact'),
      width: 220,
      value: (u) => u.contact_name ?? '',
      render: (u) =>
        !canManage(u) ? (
          u.contact_id ? (
            <span className="text-[12px] font-semibold text-text-high">{u.contact_name || `#${u.contact_id}`}</span>
          ) : (
            <span className="text-[12px] text-text-faint">—</span>
          )
        ) : (
          <Select
            // The bound contact may be missing from the pool (another tenant,
            // list cap) — the extra option keeps its name visible.
            value={u.contact_id ? String(u.contact_id) : ''}
            disabled={busy}
            onChange={(v) => v !== (u.contact_id ? String(u.contact_id) : '') && setBoundContact(u, v)}
            options={
              u.contact_id != null && !contactPool.some((c) => c.id === u.contact_id)
                ? [...contactOptions, { value: String(u.contact_id), label: u.contact_name || `#${u.contact_id}` }]
                : contactOptions
            }
            ariaLabel={t('admin.users.contactFor', { user: u.username })}
            searchable
            className="w-[200px]"
          />
        ),
    },
    {
      key: 'last_login',
      header: t('admin.users.col.lastLogin'),
      sortable: true,
      width: 150,
      value: (u) => u.last_login ?? '',
      render: (u) => (
        <span className="text-[12px] text-text-muted">
          {u.last_login ? f.dateTime(u.last_login) : t('common.status.never')}
        </span>
      ),
    },
    {
      key: 'actions',
      header: t('admin.users.col.actions'),
      srHeader: true,
      align: 'right',
      render: (u) => (
        // Compact icon actions (with tooltips) so the row never wraps
        <div data-tour="users-row-actions" className="flex items-center justify-end gap-1">
          {/* entity access: agent-level users and company admins */}
          {takesEntities(u.role) && canManage(u) && (
            <button
              disabled={busy}
              onClick={() => {
                setMsg(null)
                setEntitiesDraft(u.entities.map((b) => b.id))
                setEntitiesFor(u.id)
              }}
              title={t('admin.users.entityAccess')}
              aria-label={t('admin.users.entityAccessFor', { user: u.username })}
              className="grid h-8 w-8 shrink-0 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high disabled:opacity-50"
            >
              <Building2 size={14} aria-hidden />
            </button>
          )}
          {canManage(u) && (
            <>
              {/* invited, no password yet: send the welcome email again (a fresh link) */}
              {u.active && u.password_set === false && (
                <button
                  disabled={busy || !u.email || !invites?.ready}
                  onClick={() => sendLink(u, false)}
                  title={
                    !u.email
                      ? t('admin.users.invite.errors.no_email')
                      : !invites?.ready
                        ? (inviteProblem(invites?.problem) ?? t('admin.users.invite.resend'))
                        : t('admin.users.invite.resend')
                  }
                  aria-label={t('admin.users.invite.resendFor', { user: u.username })}
                  className="grid h-8 w-8 shrink-0 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high disabled:cursor-not-allowed disabled:opacity-40"
                >
                  <Send size={14} aria-hidden />
                </button>
              )}
              <button
                disabled={busy}
                onClick={() => {
                  setMsg(null)
                  setPwDraft('')
                  setPwFor(u.id)
                }}
                title={t('admin.users.resetPassword')}
                aria-label={t('admin.users.resetPasswordFor', { user: u.username })}
                className="grid h-8 w-8 shrink-0 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high disabled:opacity-50"
              >
                <KeyRound size={14} aria-hidden />
              </button>
              {u.two_factor && u.username !== me && (
                <button
                  disabled={busy}
                  onClick={() => resetTwoFactor(u)}
                  title={t('admin.users.resetTwoFactor')}
                  aria-label={t('admin.users.resetTwoFactorFor', { user: u.username })}
                  className="grid h-8 w-8 shrink-0 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high disabled:opacity-50"
                >
                  <ShieldOff size={14} aria-hidden />
                </button>
              )}
              <button
                disabled={busy}
                onClick={() => toggleActive(u)}
                title={u.active ? t('admin.users.deactivate') : t('admin.users.reactivate')}
                aria-label={
                  u.active
                    ? t('admin.users.deactivateUser', { user: u.username })
                    : t('admin.users.reactivateUser', { user: u.username })
                }
                className={`grid h-8 w-8 shrink-0 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover disabled:opacity-50 ${
                  u.active ? 'hover:text-[color:var(--red-ink)]' : 'hover:text-[color:var(--green-ink)]'
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

  const modalOpen = showAdd || pwFor !== null || entitiesFor !== null || emailFor !== null
  // why a welcome email can't go out right now (null = it can)
  const inviteBlocker = invites && !invites.ready ? inviteProblem(invites.problem) : null
  const inviteOn = sendInvite && !!invites?.ready
  const entitiesFor_ = (u: { tenant: string | null }) =>
    entityPool.filter((b) => !u.tenant || b.tenant === u.tenant)

  return (
    <div className="flex flex-col gap-5 pt-4">
      <p className="max-w-[760px] text-[13px] leading-relaxed text-text-mid">
        {t('admin.users.intro')}
        {!isSuper && ` ${t(iAmCompanyAdmin ? 'admin.users.introCompanyAdmin' : 'admin.users.introAdmin')}`}
      </p>

      {/* what each built-in role can do */}
      <div data-tour="users-roles" className="grid gap-3 sm:grid-cols-3">
        {ROLE_CARDS.map((c) => (
          <div key={c.slug} className="rounded-xl border border-line bg-card p-3.5">
            <span className={`rounded-full px-2 py-0.5 text-[11px] font-semibold ${ROLE_CHIP[c.slug]}`}>
              {roleDisplayName(t, c.slug)}
            </span>
            <ul className="mt-2.5 flex flex-col gap-1 text-[12px] leading-relaxed text-text-mid">
              {c.items.map((i) => (
                <li key={i} className="flex gap-1.5">
                  <span className="text-text-faint">—</span>
                  {t(i)}
                </li>
              ))}
            </ul>
          </div>
        ))}
      </div>
      <p className="-mt-2 text-[11.5px] leading-relaxed text-text-muted">
        {t('admin.users.customRolesNote')}
      </p>

      {/* Failures raised while a modal is open render inside that modal; the
          scrim would otherwise hide them. Successes always show here. */}
      {msg && (msg.ok || !modalOpen) && (
        <div
          role={msg.ok ? 'status' : 'alert'}
          className={`text-[12.5px] font-semibold ${msg.ok ? 'text-[color:var(--green-ink)]' : 'text-[color:var(--red-ink)]'}`}
        >
          {msg.text}
        </div>
      )}

      <DataTable
        data-tour="users-table"
        columns={columns}
        rows={users}
        rowKey={(u) => u.id}
        searchKeys={['user', 'role', 'entities', 'contact']}
        searchPlaceholder={t('admin.users.searchPlaceholder')}
        empty={t('admin.users.empty')}
        pageSize={15}
        toolbarRight={
          <Button data-tour="users-add" onClick={openAdd}>
            <UserPlus size={14} aria-hidden /> {t('admin.users.addUser')}
          </Button>
        }
      />

      {/* add user modal — after saving it shows what happened to the welcome email */}
      <Modal
        open={showAdd}
        onClose={() => setShowAdd(false)}
        title={created ? t('admin.users.invite.sentTitle', { user: created.username }) : t('admin.users.addUser')}
        subtitle={created ? undefined : t('admin.users.addSubtitle')}
        size="lg"
        footer={
          created ? (
            <>
              <Button variant="ghost" onClick={openAdd}>
                <UserPlus size={13} aria-hidden /> {t('admin.users.invite.addAnother')}
              </Button>
              <Button variant="primary" onClick={() => setShowAdd(false)}>
                {t('admin.users.invite.done')}
              </Button>
            </>
          ) : (
            <>
              <Button variant="ghost" onClick={() => setShowAdd(false)} disabled={busy}>
                {t('common.actions.cancel')}
              </Button>
              <Button variant="primary" onClick={addUser} disabled={busy}>
                {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <UserPlus size={13} aria-hidden />}{' '}
                {t('admin.users.addUser')}
              </Button>
            </>
          )
        }
      >
        {created ? (
          <div className="flex flex-col gap-3">
            {!created.invite.requested ? (
              <div
                role="status"
                className="flex items-start gap-2 rounded-lg border border-green/25 bg-green/10 px-3 py-2.5 text-[13px] text-[color:var(--green-ink)]"
              >
                <CircleCheck size={15} className="mt-0.5 shrink-0" aria-hidden />
                <span>{t('admin.users.invite.createdNoInvite', { user: created.username })}</span>
              </div>
            ) : created.invite.sent ? (
              <div
                role="status"
                className="flex items-start gap-2 rounded-lg border border-green/25 bg-green/10 px-3 py-2.5 text-[13px] text-[color:var(--green-ink)]"
              >
                <Send size={15} className="mt-0.5 shrink-0" aria-hidden />
                <span>
                  {t('admin.users.invite.sent', {
                    email: created.invite.to ?? '',
                    date: created.invite.expires_at ? f.dateTime(created.invite.expires_at) : '',
                  })}
                </span>
              </div>
            ) : (
              <div
                role="alert"
                className="rounded-lg border border-red/30 bg-red/10 px-3 py-2.5 text-[13px] font-semibold text-[color:var(--red-ink)]"
              >
                {t('admin.users.invite.notSent', {
                  error: inviteProblem(created.invite.code, created.invite.error ?? undefined) ?? created.invite.error ?? '',
                })}
              </div>
            )}
          </div>
        ) : (
          <div className="flex flex-col gap-4">
            {msg && !msg.ok && <ErrorBox text={msg.text} />}
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
              <Field
                label={t('admin.users.username')}
                value={newUsername}
                onChange={setNewUsername}
                placeholder={t('admin.users.usernamePlaceholder')}
                hint={t('admin.users.usernameHint')}
              />
              <Field
                label={t('admin.users.displayName')}
                value={newName}
                onChange={setNewName}
                placeholder={t('admin.users.displayNamePlaceholder')}
              />
              <Field
                label={t('admin.users.email')}
                value={newEmail}
                onChange={setNewEmail}
                placeholder={t('admin.users.emailPlaceholder')}
                hint={t('admin.users.emailHint')}
                type="email"
              />
              <div>
                <span className="mb-1.5 block text-[11.5px] font-bold text-text-high">{t('admin.users.col.role')}</span>
                <Select
                  value={newRole}
                  onChange={setNewRole}
                  size="md"
                  ariaLabel={t('admin.users.col.role')}
                  options={assignableRoles().map((r) => ({
                    value: r.slug,
                    label: roleLabel(r.slug),
                    group: r.is_system ? t('admin.users.builtinRoles') : t('admin.users.customRoles'),
                  }))}
                />
                <span className="mt-1 block text-[11px] leading-relaxed text-text-muted">{t('admin.users.roleHint')}</span>
              </div>
            </div>

            {/* the welcome email (on whenever one can go out) — else the password field */}
            <div className="rounded-lg border border-line bg-well px-3 py-3" data-tour="users-invite">
              <label className={`flex items-start gap-2.5 ${invites?.ready ? 'cursor-pointer' : 'cursor-not-allowed'}`}>
                <input
                  type="checkbox"
                  checked={inviteOn}
                  disabled={!invites?.ready || busy}
                  onChange={(e) => setSendInvite(e.target.checked)}
                  className="mt-0.5 h-4 w-4 shrink-0 accent-[color:var(--primary)]"
                />
                <span className="min-w-0">
                  <span className="block text-[13px] font-semibold text-text-high">{t('admin.users.invite.send')}</span>
                  <span className="mt-0.5 block text-[11.5px] leading-relaxed text-text-muted">
                    {t('admin.users.invite.sendHint', { hours: invites?.link_hours ?? 72 })}
                  </span>
                </span>
              </label>
              {inviteBlocker && (
                <p className="mt-2 border-t border-line pt-2 text-[11.5px] leading-relaxed text-[color:var(--amber-ink)]">
                  {inviteBlocker}{' '}
                  {invites?.problem === 'email_not_configured' &&
                    (can('app_keys.manage') ? (
                      <Link href="/settings/system-email" className="font-semibold underline underline-offset-2">
                        {t('admin.users.invite.setUp')}
                      </Link>
                    ) : (
                      t('admin.users.invite.askSuperadmin')
                    ))}
                </p>
              )}
            </div>
            {!inviteOn && (
              <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
                <Field
                  label={t('admin.users.password')}
                  value={newPassword}
                  onChange={setNewPassword}
                  placeholder={t('admin.users.passwordPlaceholder')}
                  hint={t('admin.users.passwordHint')}
                  secret
                />
              </div>
            )}
            {takesEntities(newRole) && (
              <div className="border-t border-line pt-4">
                <span className="block text-[11.5px] font-bold text-text-high">{t('admin.users.entityAccess')}</span>
                <span className="mb-2.5 mt-0.5 block text-[11px] leading-relaxed text-text-muted">
                  {newRole === 'admin' ? t('admin.users.adminEntityHint') : t('admin.users.entityAccessHint')}
                </span>
                <EntityAccessGrid entities={entityPool} value={newEntityIds} onChange={setNewEntityIds} disabled={busy} />
              </div>
            )}
          </div>
        )}
      </Modal>

      {/* reset-password modal */}
      {(() => {
        const pwUser = pwFor !== null ? users.find((u) => u.id === pwFor) : undefined
        const close = () => {
          setPwFor(null)
          setPwDraft('')
        }
        return (
          <Modal
            open={!!pwUser}
            onClose={close}
            title={pwUser ? t('admin.users.resetPasswordTitle', { user: pwUser.username }) : t('admin.users.resetPassword')}
            subtitle={t('admin.users.resetSubtitle')}
            size="sm"
            footer={
              <>
                <Button variant="ghost" onClick={close}>
                  {t('common.actions.cancel')}
                </Button>
                <Button variant="primary" disabled={busy || pwDraft.length < 8} onClick={() => pwUser && resetPassword(pwUser)}>
                  {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : null} {t('admin.users.setPassword')}
                </Button>
              </>
            }
          >
            <div className="flex flex-col gap-3">
              {msg && !msg.ok && <ErrorBox text={msg.text} />}
              {/* an emailed link, so the person chooses it themselves (their current one works until then) */}
              {pwUser && pwUser.active && pwUser.password_set !== false && (
                <>
                  <div className="rounded-lg border border-line bg-well px-3 py-3">
                    {pwUser.email && invites?.ready ? (
                      <>
                        <p className="text-[12px] leading-relaxed text-text-mid">
                          {t('admin.users.invite.resetLinkHint', { email: pwUser.email, hours: invites.link_hours })}
                        </p>
                        <Button
                          variant="secondary"
                          size="sm"
                          className="mt-2.5"
                          disabled={busy}
                          onClick={() => sendLink(pwUser, true)}
                        >
                          {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Send size={13} aria-hidden />}
                          {t('admin.users.invite.resetLink')}
                        </Button>
                      </>
                    ) : (
                      <p className="text-[12px] leading-relaxed text-text-muted">
                        {!pwUser.email ? t('admin.users.invite.noEmailForReset') : inviteBlocker}
                      </p>
                    )}
                  </div>
                  <div className="eyebrow pt-1">{t('admin.users.invite.orSetYourself')}</div>
                </>
              )}
              {pwUser && (
                <label className="block">
                  <span className="mb-1.5 block text-[11.5px] font-bold text-text-high">{t('admin.users.newPassword')}</span>
                  <input
                    type="password"
                    value={pwDraft}
                    onChange={(e) => setPwDraft(e.target.value)}
                    onKeyDown={(e) => e.key === 'Enter' && resetPassword(pwUser)}
                    placeholder={t('admin.users.newPasswordPlaceholder')}
                    autoComplete="new-password"
                    aria-label={t('admin.users.newPasswordFor', { user: pwUser.username })}
                    className={inputCls}
                  />
                </label>
              )}
            </div>
          </Modal>
        )
      })()}

      {/* email modal — where welcome emails and reset links go */}
      {(() => {
        const emailUser = emailFor !== null ? users.find((u) => u.id === emailFor) : undefined
        return (
          <Modal
            open={!!emailUser}
            onClose={() => setEmailFor(null)}
            title={emailUser ? t('admin.users.emailTitle', { user: emailUser.username }) : t('admin.users.email')}
            subtitle={t('admin.users.emailSubtitle')}
            size="sm"
            footer={
              <>
                <Button variant="ghost" onClick={() => setEmailFor(null)}>
                  {t('common.actions.cancel')}
                </Button>
                <Button
                  variant="primary"
                  disabled={busy || emailDraft.trim() === (emailUser?.email ?? '')}
                  onClick={() => emailUser && saveEmail(emailUser)}
                >
                  {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : null} {t('admin.users.emailSave')}
                </Button>
              </>
            }
          >
            <div className="flex flex-col gap-3">
              {msg && !msg.ok && <ErrorBox text={msg.text} />}
              {emailUser && (
                <Field
                  label={t('admin.users.email')}
                  value={emailDraft}
                  onChange={setEmailDraft}
                  placeholder={t('admin.users.emailPlaceholder')}
                  type="email"
                />
              )}
            </div>
          </Modal>
        )
      })()}

      {/* entity-access modal */}
      {(() => {
        const entitiesUser = entitiesFor !== null ? users.find((u) => u.id === entitiesFor) : undefined
        return (
          <Modal
            open={!!entitiesUser}
            onClose={() => setEntitiesFor(null)}
            title={
              entitiesUser
                ? t('admin.users.entityAccessTitle', { user: entitiesUser.username })
                : t('admin.users.entityAccess')
            }
            subtitle={t('admin.users.entityModalSubtitle')}
            size="lg"
            footer={
              <>
                <span className="mr-auto text-[12px] text-text-muted">
                  {t('common.counts.selected', { count: entitiesDraft.length })}
                </span>
                <Button variant="ghost" onClick={() => setEntitiesFor(null)}>
                  {t('common.actions.cancel')}
                </Button>
                <Button variant="primary" disabled={busy} onClick={() => entitiesUser && saveEntities(entitiesUser)}>
                  {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : null} {t('admin.users.saveEntityAccess')}
                </Button>
              </>
            }
          >
            <div className="flex flex-col gap-3">
              {msg && !msg.ok && <ErrorBox text={msg.text} />}
              {entitiesUser && (
                <EntityAccessGrid
                  entities={entitiesFor_(entitiesUser)}
                  value={entitiesDraft}
                  onChange={setEntitiesDraft}
                  disabled={busy}
                />
              )}
            </div>
          </Modal>
        )
      })()}
    </div>
  )
}
