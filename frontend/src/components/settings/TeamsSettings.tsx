'use client'

import { useCallback, useEffect, useMemo, useState } from 'react'
import { Loader2, Pencil, Plus, Trash2, Users } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Modal } from '@/components/ui/Modal'
import { Toggle } from '@/components/ui/Toggle'
import { DataTable, type Column } from '@/components/ui/DataTable'
import { EntityPicker } from '@/components/ui/EntityPicker'
import { BindingGrid } from '@/components/ui/BindingGrid'
import { usersByRoleGroups } from '@/components/bindings/groups'
import { authHeaders, hasCap } from '@/lib/auth'
import { useEntity, useEntityQuery } from '@/lib/EntityContext'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

/**
 * Teams — a group of agents conversations are routed to.
 *
 * Assignment is two-level: a conversation belongs to a TEAM (who owns it) and
 * optionally to one agent inside it (who is working it now). With auto-assign
 * on, each new conversation goes to the next member in rotation, so nothing
 * lands in a queue nobody owns.
 */

interface TeamMember {
  user_id: number
  username: string
}

interface Team {
  id: number
  name: string
  description: string
  auto_assign: boolean
  entity: string | null
  entity_name: string
  members: TeamMember[]
  open_conversations: number
  created_at: string
}

interface UserRow {
  id: number
  username: string
  role: string
  active: boolean
}

async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}/${path}`, {
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
    ...init,
  })
  const data = await res.json().catch(() => ({}))
  if (!res.ok) {
    const d = data as { detail?: string; error?: { message?: string } }
    throw new Error(d.detail || d.error?.message || `HTTP ${res.status}`)
  }
  return data as T
}

const inputCls =
  'h-9 w-full rounded-lg border border-line-strong bg-card px-3 text-[13px] text-text-primary outline-none transition-colors placeholder:text-text-faint focus:border-text-primary'

export function TeamsSettings() {
  const entityQ = useEntityQuery()
  const { activeEntity, entities } = useEntity()
  const [teams, setTeams] = useState<Team[] | null>(null)
  const [users, setUsers] = useState<UserRow[]>([])
  const [loadError, setLoadError] = useState<string | null>(null)
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)
  const [busy, setBusy] = useState(false)
  const [canManage, setCanManage] = useState(false)
  useEffect(() => setCanManage(hasCap('users.manage')), [])

  // create/edit modal — `editing` null = create
  const [open, setOpen] = useState(false)
  const [editing, setEditing] = useState<Team | null>(null)
  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
  const [autoAssign, setAutoAssign] = useState(true)
  const [entity, setEntity] = useState<string | null>(null)
  const [memberIds, setMemberIds] = useState<Set<string>>(new Set())

  const load = useCallback(async () => {
    try {
      const d = await api<{ teams: Team[] }>(`teams${entityQ && `?${entityQ}`}`)
      setTeams(d.teams)
      setLoadError(null)
    } catch (e) {
      setTeams(null)
      setLoadError(e instanceof Error ? e.message : String(e))
    }
    try {
      const u = await api<{ users: UserRow[] }>('v1/users')
      setUsers(u.users.filter((x) => x.active))
    } catch {
      setUsers([]) // the picker degrades to "no users yet" rather than erroring
    }
  }, [entityQ])

  useEffect(() => {
    load()
  }, [load])

  const memberGroups = useMemo(
    () => usersByRoleGroups(users.map((u) => ({ id: u.id, label: u.username, role: u.role }))),
    [users],
  )
  const isMain = (slug: string | null) => !!slug && !entities.find((e) => e.slug === slug)?.parent

  const openCreate = () => {
    setEditing(null)
    setName('')
    setDescription('')
    setAutoAssign(true)
    setEntity(activeEntity?.slug ?? null)
    setMemberIds(new Set())
    setMsg(null)
    setOpen(true)
  }

  const openEdit = (t: Team) => {
    setEditing(t)
    setName(t.name)
    setDescription(t.description)
    setAutoAssign(t.auto_assign)
    setEntity(t.entity)
    setMemberIds(new Set(t.members.map((m) => String(m.user_id))))
    setMsg(null)
    setOpen(true)
  }

  const save = async () => {
    setBusy(true)
    setMsg(null)
    try {
      const body = JSON.stringify({
        name: name.trim(),
        description: description.trim(),
        auto_assign: autoAssign,
        entity,
        member_ids: [...memberIds].map(Number),
      })
      if (editing) await api(`teams/${editing.id}`, { method: 'PATCH', body })
      else await api('teams', { method: 'POST', body })
      setOpen(false)
      setMsg({ ok: true, text: `Team ${name.trim()} ${editing ? 'updated' : 'created'}.` })
      await load()
    } catch (e) {
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    } finally {
      setBusy(false)
    }
  }

  const remove = async (t: Team) => {
    if (
      !window.confirm(
        `Delete team "${t.name}"?` +
          (t.open_conversations > 0
            ? ` Its ${t.open_conversations} open conversation(s) return to unassigned — none are lost.`
            : ''),
      )
    )
      return
    setBusy(true)
    setMsg(null)
    try {
      await api(`teams/${t.id}`, { method: 'DELETE' })
      setMsg({ ok: true, text: `Team ${t.name} deleted.` })
      await load()
    } catch (e) {
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    } finally {
      setBusy(false)
    }
  }

  if (teams === null) {
    return (
      <div className="py-6 text-[13px] text-text-muted">
        Teams unavailable{loadError ? ` — ${loadError}` : ' — is the backend API running?'}
      </div>
    )
  }

  const columns: Column<Team>[] = [
    {
      key: 'name',
      header: 'Team',
      sortable: true,
      value: (t) => `${t.name} ${t.description}`,
      render: (t) => (
        <div className="min-w-0">
          <div className="flex flex-wrap items-center gap-2">
            <span className="text-[13.5px] font-semibold text-text-heading">{t.name}</span>
            {t.auto_assign && (
              <span className="rounded-full border border-line-strong px-2 py-px text-[10.5px] font-semibold text-text-mid">
                Round-robin
              </span>
            )}
          </div>
          {t.description && <span className="mt-0.5 block truncate text-[12px] text-text-muted">{t.description}</span>}
        </div>
      ),
    },
    {
      key: 'members',
      header: 'Members',
      width: 240,
      value: (t) => t.members.map((m) => m.username).join(' '),
      render: (t) =>
        t.members.length === 0 ? (
          <span className="text-[12px] font-semibold text-[color:var(--amber-ink)]">No members — nobody to assign to</span>
        ) : (
          <div className="flex flex-wrap gap-1">
            {t.members.slice(0, 3).map((m) => (
              <span key={m.user_id} className="rounded-md border border-line bg-well px-1.5 py-px text-[11px] font-semibold text-text-mid">
                {m.username}
              </span>
            ))}
            {t.members.length > 3 && <span className="text-[11px] text-text-faint">+{t.members.length - 3}</span>}
          </div>
        ),
    },
    {
      key: 'load',
      header: 'Open',
      width: 80,
      sortable: true,
      align: 'right',
      value: (t) => t.open_conversations,
      render: (t) => <span className="text-[13px] font-semibold tabular-nums text-text-high">{t.open_conversations}</span>,
    },
    {
      key: 'entity',
      header: 'Entity',
      width: 190,
      value: (t) => t.entity_name || '',
      render: (t) =>
        t.entity ? (
          <span className="inline-flex items-center gap-1.5 text-[12.5px] text-text-high">
            {isMain(t.entity) && (
              <span className="rounded-sm bg-primary px-1 py-px text-[9px] font-bold uppercase tracking-[0.05em] text-on-primary">
                Main
              </span>
            )}
            {t.entity_name}
          </span>
        ) : (
          <span className="text-[12.5px] text-text-muted">Organization-wide</span>
        ),
    },
    ...(canManage
      ? [
          {
            key: 'actions',
            header: 'Actions',
            srHeader: true,
            align: 'right' as const,
            width: 90,
            render: (t: Team) => (
              <div className="flex items-center justify-end gap-1">
                <button
                  onClick={() => openEdit(t)}
                  disabled={busy}
                  title={`Edit ${t.name}`}
                  aria-label={`Edit team ${t.name}`}
                  className="grid h-8 w-8 shrink-0 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high disabled:opacity-40"
                >
                  <Pencil size={14} aria-hidden />
                </button>
                <button
                  onClick={() => remove(t)}
                  disabled={busy}
                  title={`Delete ${t.name}`}
                  aria-label={`Delete team ${t.name}`}
                  className="grid h-8 w-8 shrink-0 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-red/10 hover:text-[color:var(--red-ink)] disabled:opacity-40"
                >
                  <Trash2 size={14} aria-hidden />
                </button>
              </div>
            ),
          },
        ]
      : []),
  ]

  return (
    <div className="flex flex-col gap-5 pt-4">
      <p className="max-w-[760px] text-[13px] leading-relaxed text-text-mid">
        A conversation belongs to a <b>team</b> first and to one of its agents second — so if nobody has picked it up
        yet, it is still owned. With <b>round-robin</b> on, each new conversation goes to the next member in turn.
        Deleting a team never deletes its conversations; they return to unassigned.
      </p>

      {msg && (!open || msg.ok) && (
        <div
          role={msg.ok ? 'status' : 'alert'}
          className={`text-[12.5px] font-semibold ${msg.ok ? 'text-[color:var(--green-ink)]' : 'text-[color:var(--red-ink)]'}`}
        >
          {msg.text}
        </div>
      )}

      <DataTable
        columns={columns}
        rows={teams}
        rowKey={(t) => t.id}
        searchKeys={['name', 'members', 'entity']}
        searchPlaceholder="Search teams…"
        empty="No teams yet — create one so new conversations have an owner."
        toolbarRight={
          canManage ? (
            <Button onClick={openCreate}>
              <Plus size={14} aria-hidden /> Add team
            </Button>
          ) : undefined
        }
      />

      <Modal
        open={open}
        onClose={() => setOpen(false)}
        title={editing ? `Edit ${editing.name}` : 'Add team'}
        subtitle="Who owns a conversation, and who it rotates to."
        size="lg"
        footer={
          <>
            <span className="mr-auto text-[12px] text-text-muted">
              {memberIds.size} member{memberIds.size === 1 ? '' : 's'}
            </span>
            <Button variant="ghost" onClick={() => setOpen(false)} disabled={busy}>
              Cancel
            </Button>
            <Button variant="primary" onClick={save} disabled={busy || !name.trim()}>
              {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Users size={13} aria-hidden />}{' '}
              {editing ? 'Save changes' : 'Add team'}
            </Button>
          </>
        }
      >
        <div className="flex flex-col gap-4">
          {msg && !msg.ok && (
            <div
              role="alert"
              className="rounded-lg border border-red/30 bg-red/10 px-3 py-2 text-[12.5px] font-semibold text-[color:var(--red-ink)]"
            >
              {msg.text}
            </div>
          )}
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
            <label className="block">
              <span className="mb-1.5 block text-[11.5px] font-bold text-text-high">Team name</span>
              <input value={name} onChange={(e) => setName(e.target.value)} placeholder="e.g. Front desk" className={inputCls} />
            </label>
            <div>
              <span className="mb-1.5 block text-[11.5px] font-bold text-text-high">Belongs to</span>
              <EntityPicker value={entity} onChange={setEntity} globalLabel="Organization-wide" ariaLabel="Team entity" />
            </div>
            <label className="block sm:col-span-2">
              <span className="mb-1.5 block text-[11.5px] font-bold text-text-high">Description</span>
              <input
                value={description}
                onChange={(e) => setDescription(e.target.value)}
                placeholder="What this team handles"
                className={inputCls}
              />
            </label>
          </div>
          <div className="flex items-center justify-between gap-3 rounded-lg border border-line px-3 py-2.5">
            <div>
              <div className="text-[13px] font-semibold text-text-high">Round-robin new conversations</div>
              <div className="text-[11.5px] text-text-muted">Each new conversation goes to the next member in turn.</div>
            </div>
            <Toggle on={autoAssign} onChange={setAutoAssign} label="Round-robin new conversations" />
          </div>
          <div className="border-t border-line pt-4">
            <span className="mb-2.5 block text-[11.5px] font-bold text-text-high">Members</span>
            <BindingGrid
              groups={memberGroups}
              value={memberIds}
              onChange={setMemberIds}
              disabled={busy}
              itemNoun="user"
              searchPlaceholder="Search users…"
              columns={2}
              emptyText="No users yet — create them under Users & Roles first."
            />
          </div>
        </div>
      </Modal>
    </div>
  )
}
