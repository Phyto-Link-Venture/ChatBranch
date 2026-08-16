'use client'

import { useCallback, useEffect, useMemo, useState } from 'react'
import { AlertCircle, Loader2, ShieldCheck } from 'lucide-react'
import { authHeaders, CAPABILITY_CATALOG } from '@/lib/auth'
import { useRole } from '@/lib/useRole'
import { RoleList } from '@/components/permissions/RoleList'
import { RoleCardGrid } from '@/components/permissions/RoleCardGrid'
import { RoleFormModal } from '@/components/permissions/RoleFormModal'
import {
  BUILTIN_ROLES,
  cloneMatrix,
  humanize,
  type Matrix,
  type PermsResponse,
  type RoleRow,
} from '@/components/permissions/types'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

/**
 * Permissions — two-level RBAC UI (upgrade §B).
 *
 * Level 1: a grid of roles (system + custom) with full CRUD — add
 * (POST /api/auth/roles), rename (PATCH, custom only), delete (DELETE, custom
 * only, 409-when-assigned handled).
 *
 * Level 2: selecting a role opens a card grid — one resource card per
 * capability `area`, each with a No/Partial/Full status pill, a grant-all
 * checkbox, and a Manage modal for the area's individual capabilities. The
 * working matrix is persisted whole via PUT /api/auth/permissions.
 *
 * Superadmin-gated; role read after mount (hydration-safe). Degrades to the
 * three built-in roles + shipped catalog if the backend is unavailable.
 */
export default function PermissionsPage() {
  const { role, ready } = useRole()

  const [data, setData] = useState<PermsResponse | null>(null)
  const [roleMeta, setRoleMeta] = useState<Record<string, RoleRow>>({})
  const [matrix, setMatrix] = useState<Matrix>({})
  const [loadError, setLoadError] = useState<string | null>(null)
  const [saveMsg, setSaveMsg] = useState<{ ok: boolean; text: string } | null>(null)
  const [saving, setSaving] = useState(false)

  // Level 1 ↔ 2 selection (mirrored to ?role= for deep-linking, hydration-safe).
  const [selectedRole, setSelectedRole] = useState<string | null>(null)

  // add / rename modal
  const [roleModal, setRoleModal] = useState<{ mode: 'add' | 'rename'; slug?: string } | null>(null)
  const [roleBusy, setRoleBusy] = useState(false)
  const [roleError, setRoleError] = useState<string | null>(null)

  const load = useCallback(async () => {
    setLoadError(null)
    try {
      const res = await fetch(`${API}/auth/permissions`, { headers: authHeaders() })
      const body = await res.json().catch(() => ({}))
      if (!res.ok) throw new Error((body as { detail?: string }).detail || `HTTP ${res.status}`)
      const d = body as PermsResponse
      if (!Array.isArray(d.catalog) || d.catalog.length === 0) d.catalog = CAPABILITY_CATALOG
      if (!Array.isArray(d.roles) || d.roles.length === 0) d.roles = BUILTIN_ROLES
      setData(d)
      setMatrix(cloneMatrix(d.matrix || {}))
    } catch (e) {
      setData(null)
      setLoadError(e instanceof Error ? e.message : String(e))
    }
    // Role metadata (label / is_system / user_count) — best effort; the list
    // falls back to humanized slugs + built-in detection without it.
    try {
      const res = await fetch(`${API}/auth/roles`, { headers: authHeaders() })
      if (res.ok) {
        const body = (await res.json().catch(() => ({}))) as { roles?: RoleRow[] }
        const meta: Record<string, RoleRow> = {}
        for (const r of body.roles || []) meta[r.slug] = r
        setRoleMeta(meta)
      }
    } catch {
      /* ignore — labels fall back to humanized slugs */
    }
  }, [])

  useEffect(() => {
    if (ready && role === 'superadmin') load()
  }, [ready, role, load])

  // Deep-link: adopt ?role= once, after mount.
  useEffect(() => {
    const p = new URLSearchParams(window.location.search).get('role')
    if (p) setSelectedRole(p)
  }, [])

  const selectRole = useCallback((slug: string | null) => {
    setSelectedRole(slug)
    setSaveMsg(null)
    const url = new URL(window.location.href)
    if (slug) url.searchParams.set('role', slug)
    else url.searchParams.delete('role')
    window.history.replaceState(null, '', url.toString())
  }, [])

  const dirty = useMemo(() => {
    if (!data) return false
    return JSON.stringify(matrix) !== JSON.stringify(data.matrix)
  }, [matrix, data])

  const roles = data?.roles || BUILTIN_ROLES
  const catalog = data?.catalog || CAPABILITY_CATALOG

  const labelFor = useCallback(
    (slug: string) => roleMeta[slug]?.label || humanize(slug),
    [roleMeta],
  )
  const isSystemRole = useCallback(
    (slug: string) => (roleMeta[slug] ? roleMeta[slug].is_system : BUILTIN_ROLES.includes(slug)),
    [roleMeta],
  )

  // A role selected but not (yet) present resolves to Level 1.
  const activeRole = data && selectedRole && roles.includes(selectedRole) ? selectedRole : null

  const discard = () => {
    if (data) setMatrix(cloneMatrix(data.matrix || {}))
    setSaveMsg(null)
  }

  const save = async () => {
    setSaving(true)
    setSaveMsg(null)
    const outgoing = cloneMatrix(matrix)
    // Anti-lockout guard client-side too — the server enforces it as well.
    outgoing.superadmin = { ...(outgoing.superadmin || {}), 'rbac.manage': true }
    try {
      const res = await fetch(`${API}/auth/permissions`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json', ...authHeaders() },
        body: JSON.stringify({ matrix: outgoing }),
      })
      const body = await res.json().catch(() => ({}))
      if (!res.ok) throw new Error((body as { detail?: string }).detail || `HTTP ${res.status}`)
      const next = (body as PermsResponse).matrix || outgoing
      setData((d) => (d ? { ...d, matrix: next } : d))
      setMatrix(cloneMatrix(next))
      setSaveMsg({ ok: true, text: 'Permissions saved.' })
    } catch (e) {
      setSaveMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    } finally {
      setSaving(false)
    }
  }

  const submitRole = async (label: string) => {
    setRoleBusy(true)
    setRoleError(null)
    const mode = roleModal?.mode
    try {
      if (mode === 'rename' && roleModal?.slug) {
        const res = await fetch(`${API}/auth/roles/${roleModal.slug}`, {
          method: 'PATCH',
          headers: { 'Content-Type': 'application/json', ...authHeaders() },
          body: JSON.stringify({ label }),
        })
        const body = await res.json().catch(() => ({}))
        if (!res.ok) throw new Error((body as { detail?: string }).detail || `HTTP ${res.status}`)
        setRoleModal(null)
        setSaveMsg({ ok: true, text: `Role renamed to “${label}”.` })
        await load()
      } else {
        const res = await fetch(`${API}/auth/roles`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json', ...authHeaders() },
          body: JSON.stringify({ label }),
        })
        const body = await res.json().catch(() => ({}))
        if (!res.ok) throw new Error((body as { detail?: string }).detail || `HTTP ${res.status}`)
        setRoleModal(null)
        setSaveMsg({
          ok: true,
          text: `Role “${label}” created — open it to grant access, then save.`,
        })
        await load()
        // Jump straight into the new role's card grid when the slug is echoed.
        const slug = (body as { slug?: string }).slug
        if (slug) selectRole(slug)
      }
    } catch (e) {
      setRoleError(e instanceof Error ? e.message : String(e))
    } finally {
      setRoleBusy(false)
    }
  }

  const deleteRole = async (slug: string) => {
    const count = roleMeta[slug]?.user_count ?? 0
    if (count > 0) {
      setSaveMsg({
        ok: false,
        text: `“${labelFor(slug)}” is assigned to ${count} user${count === 1 ? '' : 's'} — reassign them before deleting it.`,
      })
      return
    }
    if (!window.confirm(`Delete the custom role “${labelFor(slug)}”? This can't be undone.`)) return
    setSaveMsg(null)
    try {
      const res = await fetch(`${API}/auth/roles/${slug}`, { method: 'DELETE', headers: authHeaders() })
      if (!res.ok) {
        const body = (await res.json().catch(() => ({}))) as { detail?: string; user_count?: number; count?: number }
        if (res.status === 409) {
          const n = body.user_count ?? body.count
          throw new Error(
            `“${labelFor(slug)}” is still assigned${n ? ` to ${n} user${n === 1 ? '' : 's'}` : ''} — reassign them first.`,
          )
        }
        throw new Error(body.detail || `HTTP ${res.status}`)
      }
      if (selectedRole === slug) selectRole(null)
      setSaveMsg({ ok: true, text: `Role “${labelFor(slug)}” deleted.` })
      await load()
    } catch (e) {
      setSaveMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    }
  }

  if (!ready) return null // role resolves after mount — don't flash the denial

  if (role !== 'superadmin') {
    return (
      <div className="flex items-center gap-2 py-8 text-[12.5px] text-text-muted">
        <ShieldCheck size={15} className="text-amber-300" aria-hidden />
        The permission matrix is managed by super admins.
      </div>
    )
  }

  return (
    <div className="flex min-h-0 flex-1 flex-col gap-3.5">
      {/* Header */}
      <div className="flex items-center gap-3">
        <div className="grid h-[38px] w-[38px] shrink-0 place-items-center rounded-xl border border-line bg-fill">
          <ShieldCheck size={18} strokeWidth={1.9} className="text-brand-300" aria-hidden />
        </div>
        <div className="min-w-0">
          <h1 className="text-[20px] font-extrabold tracking-[-0.5px] text-text-heading">Permissions</h1>
          <div className="mt-0.5 text-[12px] text-text-muted">
            {activeRole
              ? 'Grant a role access, resource by resource'
              : 'Roles and what each one can do'}
          </div>
        </div>
      </div>

      {loadError && (
        <div
          className="flex items-start gap-2 rounded-lg border border-red-500/20 bg-red-500/10 px-3 py-2 text-[12px]"
          style={{ color: 'var(--red-ink)' }}
        >
          <AlertCircle size={13} className="mt-0.5 shrink-0" aria-hidden />
          <span>
            Couldn&apos;t load the permission matrix — {loadError}. The editable RBAC UI needs the
            backend (GET/PUT /api/auth/permissions).
          </span>
        </div>
      )}
      {saveMsg && !saveMsg.ok && (
        <div
          className="flex items-start gap-2 rounded-lg border border-red-500/20 bg-red-500/10 px-3 py-2 text-[12px]"
          style={{ color: 'var(--red-ink)' }}
        >
          <AlertCircle size={13} className="mt-0.5 shrink-0" aria-hidden /> {saveMsg.text}
        </div>
      )}

      {data === null && !loadError ? (
        <div className="flex items-center gap-1.5 py-6 text-[12px] text-text-muted">
          <Loader2 size={13} className="animate-spin" aria-hidden /> Loading permissions…
        </div>
      ) : activeRole ? (
        <RoleCardGrid
          role={activeRole}
          roleLabel={labelFor(activeRole)}
          isSystem={isSystemRole(activeRole)}
          catalog={catalog}
          roleCaps={matrix[activeRole] || {}}
          onChangeRoleCaps={(next) => {
            setMatrix((m) => ({ ...m, [activeRole]: next }))
            setSaveMsg(null)
          }}
          onBack={() => selectRole(null)}
          dirty={dirty}
          saving={saving}
          onSave={() => void save()}
          onDiscard={discard}
          saveMsg={saveMsg}
        />
      ) : data ? (
        <RoleList
          roles={roles}
          roleMeta={roleMeta}
          catalog={catalog}
          matrix={matrix}
          labelFor={labelFor}
          isSystemRole={isSystemRole}
          onSelect={(slug) => selectRole(slug)}
          onAdd={() => {
            setRoleError(null)
            setRoleModal({ mode: 'add' })
          }}
          onRename={(slug) => {
            setRoleError(null)
            setRoleModal({ mode: 'rename', slug })
          }}
          onDelete={(slug) => void deleteRole(slug)}
        />
      ) : null}

      <p className="text-[11px] leading-relaxed text-text-muted">
        Access gates the <strong>action</strong>; entity and ownership scoping still apply on top
        (an admin manages only their own organization&apos;s users, etc.). Super admin keeps{' '}
        <code className="rounded bg-fill px-1">rbac.manage</code> locked on so access can never be
        fully removed. Custom roles start with no access — grant capabilities, then{' '}
        <strong>Save changes</strong>.
      </p>

      <RoleFormModal
        open={!!roleModal}
        mode={roleModal?.mode ?? 'add'}
        initialLabel={roleModal?.mode === 'rename' && roleModal.slug ? labelFor(roleModal.slug) : ''}
        busy={roleBusy}
        error={roleError}
        onSubmit={(label) => void submitRole(label)}
        onClose={() => setRoleModal(null)}
      />
    </div>
  )
}
