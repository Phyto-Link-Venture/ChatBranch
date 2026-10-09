'use client'

import { useCallback, useEffect, useMemo, useState } from 'react'
import { Loader2, Pencil, Plus, Trash2, Users } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Modal } from '@/components/ui/Modal'
import { Toggle } from '@/components/ui/Toggle'
import { DataTable, type Column } from '@/components/ui/DataTable'
import { EntityPicker } from '@/components/ui/EntityPicker'
import { BindingGrid, nounCount } from '@/components/ui/BindingGrid'
import { usersByRoleGroups } from '@/components/bindings/groups'
import { authHeaders, hasCap } from '@/lib/auth'
import { useEntity, useEntityQuery } from '@/lib/EntityContext'
import { Trans, useFormat, useT } from '@/i18n'

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
  const t = useT()
  const f = useFormat()
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
    () => usersByRoleGroups(t, users.map((u) => ({ id: u.id, label: u.username, role: u.role }))),
    [users, t],
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

  const openEdit = (team: Team) => {
    setEditing(team)
    setName(team.name)
    setDescription(team.description)
    setAutoAssign(team.auto_assign)
    setEntity(team.entity)
    setMemberIds(new Set(team.members.map((m) => String(m.user_id))))
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
      setMsg({
        ok: true,
        text: editing ? t('admin.teams.updated', { name: name.trim() }) : t('admin.teams.created', { name: name.trim() }),
      })
      await load()
    } catch (e) {
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    } finally {
      setBusy(false)
    }
  }

  const remove = async (team: Team) => {
    if (
      !window.confirm(
        team.open_conversations > 0
          ? t('admin.teams.confirmDeleteOpen', { name: team.name, count: team.open_conversations })
          : t('admin.teams.confirmDelete', { name: team.name }),
      )
    )
      return
    setBusy(true)
    setMsg(null)
    try {
      await api(`teams/${team.id}`, { method: 'DELETE' })
      setMsg({ ok: true, text: t('admin.teams.deleted', { name: team.name }) })
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
        {loadError ? t('admin.teams.unavailableError', { error: loadError }) : t('admin.teams.unavailable')}
      </div>
    )
  }

  const columns: Column<Team>[] = [
    {
      key: 'name',
      header: t('admin.teams.col.team'),
      sortable: true,
      value: (team) => `${team.name} ${team.description}`,
      render: (team) => (
        <div className="min-w-0">
          <div className="flex flex-wrap items-center gap-2">
            <span className="text-[13.5px] font-semibold text-text-heading">{team.name}</span>
            {team.auto_assign && (
              <span className="rounded-full border border-line-strong px-2 py-px text-[10.5px] font-semibold text-text-mid">
                {t('admin.teams.roundRobin')}
              </span>
            )}
          </div>
          {team.description && <span className="mt-0.5 block truncate text-[12px] text-text-muted">{team.description}</span>}
        </div>
      ),
    },
    {
      key: 'members',
      header: t('admin.teams.col.members'),
      width: 240,
      value: (team) => team.members.map((m) => m.username).join(' '),
      render: (team) =>
        team.members.length === 0 ? (
          <span className="text-[12px] font-semibold text-[color:var(--amber-ink)]">{t('admin.teams.noMembers')}</span>
        ) : (
          <div className="flex flex-wrap gap-1">
            {team.members.slice(0, 3).map((m) => (
              <span key={m.user_id} className="rounded-md border border-line bg-well px-1.5 py-px text-[11px] font-semibold text-text-mid">
                {m.username}
              </span>
            ))}
            {team.members.length > 3 && <span className="text-[11px] text-text-faint">+{team.members.length - 3}</span>}
          </div>
        ),
    },
    {
      key: 'load',
      header: t('admin.teams.col.open'),
      width: 80,
      sortable: true,
      align: 'right',
      value: (team) => team.open_conversations,
      render: (team) => (
        <span className="text-[13px] font-semibold tabular-nums text-text-high">{f.number(team.open_conversations)}</span>
      ),
    },
    {
      key: 'entity',
      header: t('common.fields.entity'),
      width: 190,
      value: (team) => team.entity_name || '',
      render: (team) =>
        team.entity ? (
          <span className="inline-flex items-center gap-1.5 text-[12.5px] text-text-high">
            {isMain(team.entity) && (
              <span className="rounded-sm bg-primary px-1 py-px text-[9px] font-bold uppercase tracking-[0.05em] text-on-primary">
                {t('admin.bindings.groups.main')}
              </span>
            )}
            {team.entity_name}
          </span>
        ) : (
          <span className="text-[12.5px] text-text-muted">{t('admin.bindings.orgWide')}</span>
        ),
    },
    ...(canManage
      ? [
          {
            key: 'actions',
            header: t('admin.users.col.actions'),
            srHeader: true,
            align: 'right' as const,
            width: 90,
            render: (team: Team) => (
              <div className="flex items-center justify-end gap-1">
                <button
                  onClick={() => openEdit(team)}
                  disabled={busy}
                  title={t('admin.entities.editTitle', { name: team.name })}
                  aria-label={t('admin.teams.editAria', { name: team.name })}
                  className="grid h-8 w-8 shrink-0 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high disabled:opacity-40"
                >
                  <Pencil size={14} aria-hidden />
                </button>
                <button
                  onClick={() => remove(team)}
                  disabled={busy}
                  title={t('admin.entities.deleteTitle', { name: team.name })}
                  aria-label={t('admin.teams.deleteAria', { name: team.name })}
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
        <Trans
          k="admin.teams.intro"
          values={{
            team: <b>{t('admin.teams.introTeam')}</b>,
            roundRobin: <b>{t('admin.teams.introRoundRobin')}</b>,
          }}
        />
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
        rowKey={(team) => team.id}
        searchKeys={['name', 'members', 'entity']}
        searchPlaceholder={t('admin.teams.searchPlaceholder')}
        empty={t('admin.teams.empty')}
        toolbarRight={
          canManage ? (
            <Button onClick={openCreate}>
              <Plus size={14} aria-hidden /> {t('admin.teams.add')}
            </Button>
          ) : undefined
        }
      />

      <Modal
        open={open}
        onClose={() => setOpen(false)}
        title={editing ? t('admin.entities.editTitle', { name: editing.name }) : t('admin.teams.add')}
        subtitle={t('admin.teams.modalSubtitle')}
        size="lg"
        footer={
          <>
            <span className="mr-auto text-[12px] text-text-muted">
              {t('admin.teams.memberCount', { count: memberIds.size })}
            </span>
            <Button variant="ghost" onClick={() => setOpen(false)} disabled={busy}>
              {t('common.actions.cancel')}
            </Button>
            <Button variant="primary" onClick={save} disabled={busy || !name.trim()}>
              {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Users size={13} aria-hidden />}{' '}
              {editing ? t('settings.common.saveChanges') : t('admin.teams.add')}
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
              <span className="mb-1.5 block text-[11.5px] font-bold text-text-high">{t('admin.teams.name')}</span>
              <input
                value={name}
                onChange={(e) => setName(e.target.value)}
                placeholder={t('admin.teams.namePlaceholder')}
                className={inputCls}
              />
            </label>
            <div>
              <span className="mb-1.5 block text-[11.5px] font-bold text-text-high">{t('admin.tags.editor.belongsTo')}</span>
              <EntityPicker
                value={entity}
                onChange={setEntity}
                globalLabel={t('admin.bindings.orgWide')}
                ariaLabel={t('admin.teams.entityAria')}
              />
            </div>
            <label className="block sm:col-span-2">
              <span className="mb-1.5 block text-[11.5px] font-bold text-text-high">{t('common.fields.description')}</span>
              <input
                value={description}
                onChange={(e) => setDescription(e.target.value)}
                placeholder={t('admin.teams.descriptionPlaceholder')}
                className={inputCls}
              />
            </label>
          </div>
          <div className="flex items-center justify-between gap-3 rounded-lg border border-line px-3 py-2.5">
            <div>
              <div className="text-[13px] font-semibold text-text-high">{t('admin.teams.autoAssign')}</div>
              <div className="text-[11.5px] text-text-muted">{t('admin.teams.autoAssignHint')}</div>
            </div>
            <Toggle on={autoAssign} onChange={setAutoAssign} label={t('admin.teams.autoAssign')} />
          </div>
          <div className="border-t border-line pt-4">
            <span className="mb-2.5 block text-[11.5px] font-bold text-text-high">{t('admin.teams.col.members')}</span>
            <BindingGrid
              groups={memberGroups}
              value={memberIds}
              onChange={setMemberIds}
              disabled={busy}
              itemNoun="user"
              itemCount={nounCount(t, 'user')}
              searchPlaceholder={t('admin.users.searchPlaceholder')}
              columns={2}
              emptyText={t('admin.teams.noUsers')}
            />
          </div>
        </div>
      </Modal>
    </div>
  )
}
