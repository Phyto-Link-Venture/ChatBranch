'use client'

import { useCallback, useEffect, useMemo, useState } from 'react'
import { Building2, Loader2, Radio, Tag, Tags, UserCog, Users, type LucideIcon } from 'lucide-react'
import { BindingGrid, SaveBar, type BindingGroup } from '@/components/ui/BindingGrid'
import { Select, type SelectOption } from '@/components/ui/Select'
import { EntityPicker } from '@/components/ui/EntityPicker'
import { authHeaders, hasCap } from '@/lib/auth'
import type { Entity } from '@/lib/EntityContext'
import {
  accountsByChannelGroups,
  entityTreeGroups,
  idDiff,
  itemsByEntityGroups,
  sameSet,
  usersByRoleGroups,
  type TreeEntity,
} from './groups'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

/**
 * One screen for every binding, in the Permissions-page pattern: pick WHAT you
 * are binding, pick the ONE thing on the left side, then switch the other side
 * on and off as cards (None / k of N / All, tri-state checkbox, Manage modal).
 * Nothing is saved until Save, and Save sends only the DIFF to
 * POST /api/bindings/{kind} — never "here is the whole list", so a search
 * filter can never silently unbind what it was hiding.
 */

interface Kind {
  id: string
  label: string
  target: string
  candidate: string
  hint: string
  primary: boolean
  capability: string
}

interface Pools {
  entities: { id: number; label: string; slug: string; parent_id: number | null; sub: string }[]
  teams: { id: number; label: string; sub: string }[]
  users: { id: number; label: string; sub: string }[]
  contacts: { id: number; label: string; sub: string; entity_ids: number[] }[]
  tags: { id: number; label: string; sub: string; slug: string; color: string; applies_to: string; group: string }[]
  accounts: { id: number; label: string; sub: string; channel: string; provider: string; entity_id: number | null }[]
}

const KIND_ICON: Record<string, LucideIcon> = {
  'user-entities': UserCog,
  'team-users': Users,
  'contact-entities': Building2,
  'tag-contacts': Tag,
  'tag-entities': Tags,
  'entity-accounts': Radio,
}

const SIDE: Record<string, { one: string; many: string }> = {
  entity: { one: 'entity', many: 'entities' },
  team: { one: 'team', many: 'teams' },
  user: { one: 'user', many: 'users' },
  contact: { one: 'contact', many: 'contacts' },
  tag: { one: 'tag', many: 'tags' },
  account: { one: 'channel account', many: 'channel accounts' },
}

const ROLE_LABELS: Record<string, string> = { superadmin: 'Super admins', admin: 'Admins', agent: 'Agents' }
const roleLabel = (role: string) =>
  ROLE_LABELS[role] ??
  role.replace(/[-_]+/g, ' ').replace(/\b\w/g, (c) => c.toUpperCase())

async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}/${path}`, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
  })
  const data = await res.json().catch(() => ({}))
  if (!res.ok) {
    const d = data as { detail?: string; error?: { message?: string } }
    throw new Error(d.detail || d.error?.message || `HTTP ${res.status}`)
  }
  return data as T
}

export function BindingsWorkbench() {
  const [kinds, setKinds] = useState<Kind[]>([])
  const [pools, setPools] = useState<Pools | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [kindId, setKindId] = useState('user-entities')
  const [targetId, setTargetId] = useState<number | null>(null)
  const [saved, setSaved] = useState<Set<string>>(new Set())
  const [draft, setDraft] = useState<Set<string>>(new Set())
  const [loadingMembers, setLoadingMembers] = useState(false)
  const [saving, setSaving] = useState(false)
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)
  const [caps, setCaps] = useState<Record<string, boolean>>({})

  const loadCatalog = useCallback(async () => {
    try {
      const d = await api<{ kinds: Kind[]; pools: Pools }>('bindings/catalog')
      setKinds(d.kinds.filter((k) => k.primary))
      setPools(d.pools)
      setLoadError(null)
      setCaps(Object.fromEntries(d.kinds.map((k) => [k.id, hasCap(k.capability)])))
    } catch (e) {
      setLoadError(e instanceof Error ? e.message : String(e))
    }
  }, [])

  useEffect(() => {
    void loadCatalog()
  }, [loadCatalog])

  const kind = kinds.find((k) => k.id === kindId) ?? null
  const canManage = !!caps[kindId]
  const dirty = !sameSet(saved, draft)
  const { add, remove } = idDiff(saved, draft)

  const treeEntities: TreeEntity[] = useMemo(
    () => (pools?.entities ?? []).map((e) => ({ id: e.id, name: e.label, parent_id: e.parent_id, slug: e.slug })),
    [pools],
  )

  // ---- targets ------------------------------------------------------------
  const targets = useMemo<{ id: number; label: string }[]>(() => {
    if (!pools || !kind) return []
    switch (kind.target) {
      case 'user':
        // admins see every entity by role — entity access is for agent-level logins
        return kind.id === 'user-entities' ? pools.users.filter((u) => u.sub !== 'admin' && u.sub !== 'superadmin') : pools.users
      case 'team':
        return pools.teams
      case 'contact':
        return pools.contacts
      case 'entity':
        return pools.entities
      case 'tag':
        return pools.tags.filter((t) => t.applies_to === (kind.id === 'tag-contacts' ? 'contact' : 'entity'))
      default:
        return []
    }
  }, [pools, kind])

  // keep a valid target selected whenever the kind (or the pools) change
  useEffect(() => {
    setTargetId((cur) => (targets.some((t) => t.id === cur) ? cur : (targets[0]?.id ?? null)))
  }, [targets])

  const loadMembers = useCallback(async () => {
    // a target left over from the previous kind is about to be replaced
    if (!kind || targetId == null || !targets.some((t) => t.id === targetId)) {
      setSaved(new Set())
      setDraft(new Set())
      return
    }
    setLoadingMembers(true)
    try {
      const d = await api<{ member_ids: number[] }>(`bindings/${kind.id}?target=${targetId}`)
      const ids = new Set((d.member_ids ?? []).map(String))
      setSaved(ids)
      setDraft(new Set(ids))
    } catch (e) {
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    } finally {
      setLoadingMembers(false)
    }
  }, [kind, targetId, targets])

  useEffect(() => {
    void loadMembers()
  }, [loadMembers])

  const guard = () => !dirty || window.confirm('Discard your unsaved changes?')

  // ---- candidate cards -------------------------------------------------------
  const groups = useMemo<BindingGroup[]>(() => {
    if (!pools || !kind) return []
    switch (kind.candidate) {
      case 'entity':
        return entityTreeGroups(treeEntities)
      case 'user':
        return usersByRoleGroups(pools.users.map((u) => ({ id: u.id, label: u.label, role: u.sub })))
      case 'contact':
        return itemsByEntityGroups(
          treeEntities,
          pools.contacts.map((c) => ({ id: c.id, label: c.label, hint: c.sub, entity_ids: c.entity_ids })),
          'contact',
        )
      case 'account':
        return accountsByChannelGroups(
          pools.accounts.map((a) => ({
            id: a.id,
            label: a.label,
            channel: a.channel,
            sub: a.entity_id === null ? 'Organization-wide' : a.entity_id === targetId ? undefined : `Now on ${a.sub}`,
          })),
        )
      default:
        return []
    }
  }, [pools, kind, treeEntities, targetId])

  const save = async () => {
    if (!kind || targetId == null) return
    setSaving(true)
    setMsg(null)
    try {
      const d = await api<{ added: number; removed: number; member_ids: number[] }>(`bindings/${kind.id}`, {
        method: 'POST',
        body: JSON.stringify({ target: targetId, add, remove }),
      })
      const ids = new Set(d.member_ids.map(String))
      setSaved(ids)
      setDraft(new Set(ids))
      setMsg({ ok: true, text: `Saved — ${d.added} bound, ${d.removed} unbound.` })
      // an account moving entity changes the pool's "now on" hints
      if (kind.id === 'entity-accounts') void loadCatalog()
    } catch (e) {
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    } finally {
      setSaving(false)
    }
  }

  if (loadError) {
    return <div className="py-6 text-[13px] text-text-muted">Bindings unavailable — {loadError}</div>
  }
  if (!pools || !kind) {
    return (
      <div className="flex items-center gap-2 py-6 text-[13px] text-text-muted">
        <Loader2 size={14} className="animate-spin" aria-hidden /> Loading…
      </div>
    )
  }

  const side = SIDE[kind.target] ?? { one: kind.target, many: `${kind.target}s` }
  const cand = SIDE[kind.candidate] ?? { one: kind.candidate, many: `${kind.candidate}s` }
  const targetLabel = targets.find((t) => t.id === targetId)?.label ?? ''

  // entity targets use the tree picker (MAIN badge + indented sub-entities)
  const pickerEntities: Entity[] = pools.entities.map((e) => ({
    slug: e.slug,
    name: e.label,
    tenant: '',
    parent: e.parent_id !== null ? pools.entities.find((p) => p.id === e.parent_id)?.slug ?? null : null,
  }))

  const targetOptions: SelectOption[] =
    kind.target === 'user'
      ? targets.map((u) => ({ value: String(u.id), label: u.label, group: roleLabel((u as { sub?: string }).sub ?? '') }))
      : kind.target === 'tag'
        ? pools.tags
            .filter((t) => targets.some((x) => x.id === t.id))
            .map((t) => ({ value: String(t.id), label: t.label, group: t.group || 'Ungrouped' }))
        : targets.map((t) => ({
            value: String(t.id),
            label: t.label,
            hint: 'sub' in t ? String((t as { sub?: string }).sub ?? '') : undefined,
          }))

  return (
    <div className="flex flex-col gap-6 pt-4">
      <p className="max-w-[760px] text-[13px] leading-relaxed text-text-mid">
        Choose what you are binding, pick the one thing on the left, then switch the other side on or off — a whole
        card at a time with its checkbox, or one by one under <b>Manage</b>. Nothing changes until you save, and saving
        sends only what you changed.
      </p>

      {/* 1 · kind */}
      <section className="flex flex-col gap-2.5">
        <span className="eyebrow">1 · What to bind</span>
        <div role="radiogroup" aria-label="What to bind" className="grid grid-cols-1 gap-2 sm:grid-cols-2 xl:grid-cols-3">
          {kinds.map((k) => {
            const Icon = KIND_ICON[k.id] ?? Tag
            const on = k.id === kindId
            return (
              <button
                key={k.id}
                type="button"
                role="radio"
                aria-checked={on}
                onClick={() => {
                  if (k.id === kindId || !guard()) return
                  setMsg(null)
                  setKindId(k.id)
                }}
                className={`flex items-start gap-3 rounded-xl border p-3 text-left transition-colors ${
                  on ? 'border-text-primary bg-card shadow-pop' : 'border-line bg-card hover:border-line-strong'
                }`}
              >
                <span
                  className={`grid h-8 w-8 shrink-0 place-items-center rounded-lg ${on ? 'bg-primary text-on-primary' : 'bg-fill text-text-mid'}`}
                >
                  <Icon size={15} aria-hidden />
                </span>
                <span className="min-w-0">
                  <span className="block text-[13.5px] font-bold text-text-heading">{k.label}</span>
                  <span className="mt-0.5 line-clamp-2 block text-[11.5px] leading-snug text-text-muted">{k.hint}</span>
                </span>
              </button>
            )
          })}
        </div>
      </section>

      {/* 2 · target */}
      <section className="flex flex-col gap-2.5">
        <span className="eyebrow">2 · Pick the {side.one}</span>
        <div className="max-w-[460px]">
          {targets.length === 0 ? (
            <p className="text-[13px] text-text-muted">
              No {side.many} yet{kind.target === 'tag' ? ` — create a ${kind.id === 'tag-contacts' ? 'contact' : 'entity'} tag under Library → Tags` : ''}.
            </p>
          ) : kind.target === 'entity' ? (
            <EntityPicker
              value={pools.entities.find((e) => e.id === targetId)?.slug ?? null}
              onChange={(slug) => {
                if (!guard()) return
                setTargetId(pools.entities.find((e) => e.slug === slug)?.id ?? null)
              }}
              allowGlobal={false}
              entities={pickerEntities}
              ariaLabel="Entity"
            />
          ) : (
            <Select
              value={targetId == null ? '' : String(targetId)}
              onChange={(v) => {
                if (!guard()) return
                setTargetId(v ? Number(v) : null)
              }}
              options={targetOptions}
              size="md"
              ariaLabel={side.one}
              searchable={targetOptions.length > 8}
            />
          )}
        </div>
      </section>

      {/* 3 · candidates */}
      {targetId != null && (
        <section className="flex flex-col gap-2.5">
          <div className="flex flex-wrap items-baseline justify-between gap-2">
            <span className="eyebrow">
              3 · {cand.many} for {targetLabel}
            </span>
            <span className="text-[12px] text-text-muted">
              {draft.size} bound{!canManage && ' · read-only'}
            </span>
          </div>
          {msg && (
            <div
              role={msg.ok ? 'status' : 'alert'}
              className={`text-[12.5px] font-semibold ${msg.ok ? 'text-[color:var(--green-ink)]' : 'text-[color:var(--red-ink)]'}`}
            >
              {msg.text}
            </div>
          )}
          {loadingMembers ? (
            <div className="flex items-center gap-2 py-6 text-[13px] text-text-muted">
              <Loader2 size={14} className="animate-spin" aria-hidden /> Loading…
            </div>
          ) : (
            <BindingGrid
              groups={groups}
              value={draft}
              onChange={(next) => {
                setDraft(next)
                setMsg(null)
              }}
              disabled={!canManage || saving}
              itemNoun={cand.one}
              searchPlaceholder={`Search ${cand.many}…`}
              manageTitle={(g) => `${targetLabel} — ${g.label}`}
              emptyText={`No ${cand.many} to bind yet.`}
            />
          )}
          <SaveBar
            dirty={dirty && canManage}
            saving={saving}
            onSave={save}
            onDiscard={() => setDraft(new Set(saved))}
            summary={[add.length ? `+${add.length}` : '', remove.length ? `−${remove.length}` : ''].filter(Boolean).join(' · ')}
          />
        </section>
      )}
    </div>
  )
}
