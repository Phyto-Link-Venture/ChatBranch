'use client'

import { useCallback, useEffect, useState } from 'react'
import { ArrowDown, ArrowUp, CheckCircle2, FlaskConical, GripVertical, Loader2, Pencil, Plus, Search, Trash2, Workflow, X, XCircle, Zap } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Modal } from '@/components/ui/Modal'
import { Select } from '@/components/ui/Select'
import { Trans, useFormat, useT, type MessageKey } from '@/i18n'
import { authHeaders, hasCap } from '@/lib/auth'
import { useEntityQuery } from '@/lib/EntityContext'
import {
  AvailabilityChips,
  AvailabilityPicker,
  availabilityOf,
  useDefaultAvailability,
  type EntityRefLite,
} from '@/components/ui/AvailabilityPicker'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

/**
 * Automation rules — "when this happens to a conversation, do that".
 *
 * Distinct from the Flow Builder on purpose: a flow is CONTACT-centric and
 * time-triggered (a reminder sequence over days), a rule is CONVERSATION-centric
 * and fires on an event that just happened. The vocabulary (events, fields,
 * operators, actions) is fetched from the engine's own catalog, so the editor
 * can never offer an option the engine would silently ignore.
 */

interface Condition {
  field: string
  op: string
  value: string
}

interface Action {
  type: string
  value: string
}

interface Rule {
  id: number
  name: string
  description: string
  event: string
  active: boolean
  position: number
  conditions: Condition[]
  actions: Action[]
  times_fired: number
  last_fired_at: string | null
  entity: string | null
  entity_name: string
  /** "Available to" (primary first); [] + global = everyone */
  entities?: EntityRefLite[]
  global?: boolean
}

interface Catalog {
  events: { id: string; label: string }[]
  fields: { id: string; label: string }[]
  operators: { id: string; label: string }[]
  actions: { id: string; label: string }[]
  statuses: string[]
  teams: { id: number; name: string }[]
  agents: { username: string; role: string }[]
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
  'w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[12.5px] text-text-primary outline-none transition-colors focus:border-brand-400'

/** Operators that take no value — the editor hides the value box for these. */
const VALUELESS = ['is_empty', 'is_not_empty']
/** Actions that take no value. */
const NO_VALUE_ACTIONS = ['disable_ai', 'enable_ai']
/** Yes/no condition fields (the engine compares true/false; yes/no/on/off work too). */
const YES_NO_FIELDS = ['ai_on', 'handed_over', 'is_first_message']

/**
 * The catalog's labels arrive in English from the server; these translate them
 * by their STABLE ids. An id missing here (a newer engine) falls back to the
 * server's label, so the editor never hides an option.
 */
const EVENT_LABELS: Record<string, MessageKey> = {
  conversation_created: 'flows.automations.events.conversationCreated',
  message_created: 'flows.automations.events.messageCreated',
  status_changed: 'flows.automations.events.statusChanged',
  assigned: 'flows.automations.events.assigned',
}
const FIELD_LABELS: Record<string, MessageKey> = {
  message: 'flows.automations.fields.message',
  channel: 'common.fields.channel',
  status: 'common.fields.status',
  assignee: 'flows.automations.fields.assignee',
  team: 'flows.automations.fields.team',
  labels: 'flows.automations.fields.labels',
  contact_name: 'flows.automations.fields.contactName',
  contact_tags: 'flows.automations.fields.contactTags',
  is_first_message: 'flows.automations.fields.isFirstMessage',
  ai_on: 'flows.automations.fields.aiOn',
  handed_over: 'flows.automations.fields.handedOver',
  contact_linked: 'flows.automations.fields.contactLinked',
}
const OPERATOR_LABELS: Record<string, MessageKey> = {
  contains: 'flows.automations.operators.contains',
  not_contains: 'flows.automations.operators.notContains',
  equals: 'flows.automations.operators.equals',
  not_equals: 'flows.automations.operators.notEquals',
  is_empty: 'flows.automations.operators.isEmpty',
  is_not_empty: 'flows.automations.operators.isNotEmpty',
}
const ACTION_LABELS: Record<string, MessageKey> = {
  add_label: 'flows.automations.actions.addLabel',
  remove_label: 'flows.automations.actions.removeLabel',
  set_status: 'flows.automations.actions.setStatus',
  assign_team: 'flows.automations.actions.assignTeam',
  assign_agent: 'flows.automations.actions.assignAgent',
  disable_ai: 'flows.automations.actions.disableAi',
  enable_ai: 'flows.automations.actions.enableAi',
  add_note: 'flows.automations.actions.addNote',
  notify: 'flows.automations.actions.notify',
  hand_over: 'flows.automations.actions.handOver',
  ask_identity: 'flows.automations.actions.askIdentity',
}
/** Conversation statuses (the catalog's `statuses`) — glossary words. */
const STATUS_LABELS: Record<string, MessageKey> = {
  open: 'flows.automations.statuses.open',
  pending: 'flows.automations.statuses.pending',
  snoozed: 'flows.automations.statuses.snoozed',
  resolved: 'flows.automations.statuses.resolved',
}

export function AutomationsSettings() {
  const t = useT()
  const f = useFormat()
  const entityQ = useEntityQuery()
  const [rules, setRules] = useState<Rule[] | null>(null)
  const [catalog, setCatalog] = useState<Catalog | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)
  const [busy, setBusy] = useState(false)
  const [canManage, setCanManage] = useState(false)
  useEffect(() => setCanManage(hasCap('flows.manage')), [])

  const [open, setOpen] = useState(false)
  const [editing, setEditing] = useState<Rule | null>(null)
  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
  const [evt, setEvt] = useState('message_created')
  const [active, setActive] = useState(true)
  /** "Available to" slugs; [] = everyone */
  const [entities, setEntities] = useState<string[]>([])
  const defaultAvailability = useDefaultAvailability()
  const [conditions, setConditions] = useState<Condition[]>([])
  const [actions, setActions] = useState<Action[]>([])
  const [testing, setTesting] = useState<Rule | null>(null)
  const [dragId, setDragId] = useState<number | null>(null)

  // Rules run top to bottom: save a new order (optimistic, then the server's word)
  const reorder = async (next: Rule[]) => {
    const before = rules
    setRules(next)
    try {
      await api('automations/reorder', { method: 'POST', body: JSON.stringify({ ids: next.map((r) => r.id) }) })
    } catch (e) {
      setRules(before)
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    }
  }
  const move = (from: number, to: number) => {
    if (!rules || to < 0 || to >= rules.length || from === to) return
    const next = [...rules]
    const [r] = next.splice(from, 1)
    next.splice(to, 0, r)
    void reorder(next)
  }

  const load = useCallback(async () => {
    try {
      const d = await api<{ rules: Rule[] }>(`automations${entityQ && `?${entityQ}`}`)
      setRules(d.rules)
      setLoadError(null)
    } catch (e) {
      setRules(null)
      setLoadError(e instanceof Error ? e.message : String(e))
    }
    try {
      setCatalog(await api<Catalog>('automations/catalog'))
    } catch {
      setCatalog(null)
    }
  }, [entityQ])

  useEffect(() => {
    load()
  }, [load])

  const openCreate = () => {
    setEditing(null)
    setName('')
    setDescription('')
    setEvt('message_created')
    setActive(true)
    setEntities(defaultAvailability)
    setConditions([])
    setActions([{ type: 'add_label', value: '' }])
    setMsg(null)
    setOpen(true)
  }

  const openEdit = (r: Rule) => {
    setEditing(r)
    setName(r.name)
    setDescription(r.description)
    setEvt(r.event)
    setActive(r.active)
    setEntities(availabilityOf(r))
    setConditions(r.conditions ?? [])
    setActions(r.actions ?? [])
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
        event: evt,
        active,
        entities,
        // strip half-filled rows rather than saving conditions that can never match
        conditions: conditions.filter((c) => c.field && c.op),
        actions: actions.filter((a) => a.type),
      })
      if (editing) await api(`automations/${editing.id}`, { method: 'PATCH', body })
      else await api('automations', { method: 'POST', body })
      setOpen(false)
      setMsg({
        ok: true,
        text: editing
          ? t('flows.automations.ruleUpdated', { name: name.trim() })
          : t('flows.automations.ruleCreated', { name: name.trim() }),
      })
      await load()
    } catch (e) {
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    } finally {
      setBusy(false)
    }
  }

  const toggle = async (r: Rule) => {
    try {
      await api(`automations/${r.id}`, {
        method: 'PATCH',
        body: JSON.stringify({ active: !r.active }),
      })
      await load()
    } catch (e) {
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    }
  }

  const remove = async (r: Rule) => {
    if (!window.confirm(t('flows.automations.confirmDelete', { name: r.name }))) return
    try {
      await api(`automations/${r.id}`, { method: 'DELETE' })
      setMsg({ ok: true, text: t('flows.automations.ruleDeleted', { name: r.name }) })
      await load()
    } catch (e) {
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    }
  }

  if (rules === null) {
    return (
      <div className="py-6 text-[12px] text-text-muted">
        {loadError ? t('flows.automations.unavailableError', { error: loadError }) : t('flows.automations.unavailable')}
      </div>
    )
  }

  /** A catalog entry's label: ours by id, else the server's, else the id. */
  const label = (keys: Record<string, MessageKey>, list: { id: string; label: string }[] | undefined, id: string) =>
    keys[id] ? t(keys[id]) : (list?.find((x) => x.id === id)?.label ?? id)
  const catalogOptions = (keys: Record<string, MessageKey>, list: { id: string; label: string }[] | undefined) =>
    (list ?? []).map((x) => ({ value: x.id, label: keys[x.id] ? t(keys[x.id]) : x.label }))
  const statusLabel = (s: string) => (STATUS_LABELS[s] ? t(STATUS_LABELS[s]) : s)

  /** The value control depends on the action — a team picker beats free text. */
  const actionValueControl = (a: Action, i: number) => {
    if (NO_VALUE_ACTIONS.includes(a.type)) return null
    const set = (value: string) =>
      setActions((cur) => cur.map((x, j) => (j === i ? { ...x, value } : x)))
    if (a.type === 'assign_team')
      return (
        <Select
          value={a.value}
          onChange={set}
          size="md"
          ariaLabel={t('flows.automations.team')}
          className="flex-1"
          options={[
            { value: '', label: t('flows.automations.pickTeam') },
            ...(catalog?.teams ?? []).map((tm) => ({ value: String(tm.id), label: tm.name })),
          ]}
        />
      )
    if (a.type === 'assign_agent')
      return (
        <Select
          value={a.value}
          onChange={set}
          size="md"
          ariaLabel={t('flows.automations.agent')}
          className="flex-1"
          options={[
            { value: '', label: t('flows.automations.pickAgent') },
            ...(catalog?.agents ?? []).map((x) => ({ value: x.username, label: x.username, hint: x.role })),
          ]}
        />
      )
    if (a.type === 'ask_identity')
      return (
        <Select
          value={a.value || 'ask'}
          onChange={set}
          size="md"
          ariaLabel={t('flows.automations.noMatchingContact')}
          className="flex-1"
          options={[
            { value: 'ask', label: t('flows.automations.identityAsk') },
            { value: 'ask_and_create', label: t('flows.automations.identityAskCreate') },
          ]}
        />
      )
    if (a.type === 'set_status')
      return (
        <Select
          value={a.value}
          onChange={set}
          size="md"
          ariaLabel={t('common.fields.status')}
          className="flex-1"
          options={(catalog?.statuses ?? []).map((s) => ({ value: s, label: statusLabel(s) }))}
        />
      )
    return (
      <input
        value={a.value}
        onChange={(e) => set(e.target.value)}
        placeholder={
          a.type === 'add_note'
            ? t('flows.automations.noteText')
            : a.type === 'hand_over'
              ? t('flows.automations.whyForTeam')
              : t('flows.automations.value')
        }
        className={`${inputCls} flex-1 min-w-[140px]`}
        aria-label={t('flows.automations.actionValue')}
      />
    )
  }

  /** "→ Set status: Resolved" — a stored status value reads as its word. */
  const actionChip = (a: Action) => {
    const name = label(ACTION_LABELS, catalog?.actions, a.type)
    if (NO_VALUE_ACTIONS.includes(a.type) || !a.value) return t('flows.automations.actionChip', { action: name })
    const value = a.type === 'set_status' ? statusLabel(a.value) : a.value
    return t('flows.automations.actionChipValue', { action: name, value })
  }

  return (
    <div className="flex flex-col gap-4 pt-3.5">
      <p data-tour="automations-intro" className="max-w-[720px] text-[11.5px] leading-relaxed text-text-mid">
        <Trans
          k="flows.automations.intro"
          values={{
            when: <b>{t('flows.automations.introWhen')}</b>,
            if: <b>{t('flows.automations.introIf')}</b>,
            do: <b>{t('flows.automations.introDo')}</b>,
            flowBuilder: <b>{t('shell.nav.flowBuilder')}</b>,
          }}
        />
      </p>

      {msg && (!open || msg.ok) && (
        <div className={`text-[12px] font-semibold ${msg.ok ? 'text-green-300' : 'text-red-300'}`}>
          {msg.text}
        </div>
      )}

      <div className="flex items-center justify-between gap-3">
        <span className="text-[11px] font-bold uppercase tracking-[0.06em] text-text-faint">
          {t('flows.automations.ruleCount', { count: rules.length })}
        </span>
        {canManage && (
          <Button data-tour="automations-add" onClick={openCreate}>
            <Plus size={13} aria-hidden /> {t('flows.automations.addRule')}
          </Button>
        )}
      </div>

      <div data-tour="automations-list" className="flex flex-col gap-2">
        {rules.length === 0 && (
          <div className="rounded-xl border border-dashed border-line-strong px-4 py-6 text-center text-[12px] text-text-muted">
            <Trans k="flows.automations.empty" values={{ word: <i>{t('flows.automations.emptyWord')}</i> }} />
          </div>
        )}
        {rules.length > 1 && canManage && <p className="text-[11.5px] text-text-muted">{t('flows.automations.orderHint')}</p>}
        {rules.map((r, idx) => (
          <div
            key={r.id}
            draggable={canManage && rules.length > 1}
            onDragStart={(e) => {
              setDragId(r.id)
              e.dataTransfer.effectAllowed = 'move'
            }}
            onDragOver={(e) => {
              if (dragId != null) e.preventDefault()
            }}
            onDrop={(e) => {
              e.preventDefault()
              const from = rules.findIndex((x) => x.id === dragId)
              setDragId(null)
              if (from >= 0) move(from, idx)
            }}
            onDragEnd={() => setDragId(null)}
            className={`flex items-start gap-3 rounded-xl border bg-card px-4 py-3 transition-opacity ${
              dragId === r.id ? 'border-brand-400 opacity-60' : 'border-line'
            }`}
          >
            {canManage && rules.length > 1 && (
              <div className="-ml-2 flex shrink-0 flex-col items-center">
                <button
                  type="button"
                  onClick={() => move(idx, idx - 1)}
                  disabled={idx === 0}
                  aria-label={t('flows.automations.moveUp', { name: r.name })}
                  className="grid h-5 w-6 place-items-center rounded text-text-muted hover:bg-fill-hover hover:text-text-high disabled:opacity-30"
                >
                  <ArrowUp size={12} aria-hidden />
                </button>
                <span
                  title={t('flows.automations.dragToReorder')}
                  className="grid h-5 w-6 cursor-grab place-items-center text-text-faint active:cursor-grabbing"
                  aria-hidden
                >
                  <GripVertical size={13} />
                </span>
                <button
                  type="button"
                  onClick={() => move(idx, idx + 1)}
                  disabled={idx === rules.length - 1}
                  aria-label={t('flows.automations.moveDown', { name: r.name })}
                  className="grid h-5 w-6 place-items-center rounded text-text-muted hover:bg-fill-hover hover:text-text-high disabled:opacity-30"
                >
                  <ArrowDown size={12} aria-hidden />
                </button>
              </div>
            )}
            <span className="mt-1 w-4 shrink-0 text-center text-[11px] font-semibold tabular-nums text-text-faint">{idx + 1}</span>
            <div
              className={`mt-0.5 grid h-7 w-7 shrink-0 place-items-center rounded-lg ${
                r.active ? 'bg-brand-400/15 text-brand-300' : 'bg-fill text-text-muted'
              }`}
            >
              <Zap size={14} aria-hidden />
            </div>
            <div className="min-w-0 flex-1">
              <div className="flex flex-wrap items-center gap-2">
                <span className="text-[13px] font-bold text-text-high">{r.name}</span>
                <span className="rounded-full bg-fill px-2 py-px text-[10px] font-semibold text-text-muted">
                  {label(EVENT_LABELS, catalog?.events, r.event)}
                </span>
                {!r.active && (
                  <span className="rounded-full bg-fill px-2 py-px text-[10px] font-bold text-text-muted">{t('common.status.off')}</span>
                )}
                <AvailabilityChips entities={r.entities} global={r.global ?? r.entity === null} />

                {r.times_fired > 0 && (
                  <span
                    className="text-[10px] text-text-faint"
                    title={r.last_fired_at ? t('flows.automations.lastFired', { when: f.dateTime(r.last_fired_at) }) : undefined}
                  >
                    {t('flows.automations.fired', { count: r.times_fired })}
                  </span>
                )}
              </div>
              {r.description && (
                <div className="mt-0.5 text-[11.5px] text-text-muted">{r.description}</div>
              )}
              <div className="mt-1 flex flex-wrap gap-1.5 text-[10.5px]">
                {(r.conditions ?? []).map((c, i) => (
                  <span key={i} className="rounded bg-fill px-1.5 py-0.5 text-text-mid">
                    {VALUELESS.includes(c.op)
                      ? t('flows.automations.conditionChip', {
                          field: label(FIELD_LABELS, catalog?.fields, c.field),
                          op: label(OPERATOR_LABELS, catalog?.operators, c.op),
                        })
                      : t('flows.automations.conditionChipValue', {
                          field: label(FIELD_LABELS, catalog?.fields, c.field),
                          op: label(OPERATOR_LABELS, catalog?.operators, c.op),
                          value: c.value,
                        })}
                  </span>
                ))}
                {(r.actions ?? []).map((a, i) => (
                  <span key={i} className="rounded bg-brand-400/10 px-1.5 py-0.5 text-brand-300">
                    {actionChip(a)}
                  </span>
                ))}
              </div>
            </div>
            {canManage && (
              <div data-tour="automations-rule-actions" className="flex shrink-0 items-center gap-1">
                <button
                  onClick={() => toggle(r)}
                  title={r.active ? t('flows.automations.turnOff') : t('flows.automations.turnOn')}
                  aria-label={r.active ? t('flows.automations.deactivate', { name: r.name }) : t('flows.automations.activate', { name: r.name })}
                  className="rounded-lg px-2 py-1 text-[11px] font-semibold text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
                >
                  {r.active ? t('common.status.on') : t('common.status.off')}
                </button>
                <button
                  onClick={() => setTesting(r)}
                  title={t('flows.automations.testName', { name: r.name })}
                  aria-label={t('flows.automations.testName', { name: r.name })}
                  className="inline-flex h-7 items-center gap-1 rounded-lg px-2 text-[11px] font-semibold text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
                >
                  <FlaskConical size={13} aria-hidden /> {t('flows.automations.test')}
                </button>
                <button
                  onClick={() => openEdit(r)}
                  title={t('flows.automations.editName', { name: r.name })}
                  aria-label={t('flows.automations.editRule', { name: r.name })}
                  className="grid h-7 w-7 place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
                >
                  <Pencil size={13} aria-hidden />
                </button>
                <button
                  onClick={() => remove(r)}
                  title={t('flows.automations.deleteName', { name: r.name })}
                  aria-label={t('flows.automations.deleteRule', { name: r.name })}
                  className="grid h-7 w-7 place-items-center rounded-lg text-text-muted transition-colors hover:bg-red/15 hover:text-red-300"
                >
                  <Trash2 size={13} aria-hidden />
                </button>
              </div>
            )}
          </div>
        ))}
      </div>

      <TestRuleModal
        rule={testing}
        onClose={() => setTesting(null)}
        fieldLabel={(id) => label(FIELD_LABELS, catalog?.fields, id)}
        opLabel={(id) => label(OPERATOR_LABELS, catalog?.operators, id)}
      />

      <Modal
        open={open}
        onClose={() => setOpen(false)}
        title={editing ? t('flows.automations.editName', { name: editing.name }) : t('flows.automations.newRule')}
        subtitle={t('flows.automations.modalSubtitle')}
        size="lg"
        footer={
          <>
            <Button variant="ghost" onClick={() => setOpen(false)} disabled={busy}>
              {t('common.actions.cancel')}
            </Button>
            <Button variant="primary" onClick={save} disabled={busy || !name.trim()}>
              {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Workflow size={13} aria-hidden />}{' '}
              {editing ? t('flows.automations.saveChanges') : t('flows.automations.createRule')}
            </Button>
          </>
        }
      >
        <div className="flex flex-col gap-3">
          {msg && !msg.ok && (
            <div
              role="alert"
              className="rounded-lg border border-red-500/30 bg-red-500/10 px-3 py-2 text-[12px] font-semibold text-red-300"
            >
              {msg.text}
            </div>
          )}

          <div className="grid gap-3 sm:grid-cols-2">
            <label className="block">
              <span className="mb-1 block text-[11px] font-bold text-text-high">{t('flows.automations.ruleName')}</span>
              <input
                value={name}
                onChange={(e) => setName(e.target.value)}
                placeholder={t('flows.automations.ruleNamePlaceholder')}
                className={inputCls}
              />
            </label>
            <label className="block">
              <span className="mb-1 block text-[11px] font-bold text-text-high">{t('flows.automations.introWhen')}</span>
              <Select
                value={evt}
                onChange={setEvt}
                size="md"
                ariaLabel={t('flows.automations.triggerEvent')}
                options={catalogOptions(EVENT_LABELS, catalog?.events)}
              />
            </label>
          </div>

          <label className="block">
            <span className="mb-1 block text-[11px] font-bold text-text-high">{t('common.fields.description')}</span>
            <input
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              placeholder={t('flows.automations.descriptionPlaceholder')}
              className={inputCls}
            />
          </label>

          <AvailabilityPicker value={entities} onChange={setEntities} />

          {/* conditions */}
          <div className="rounded-lg border border-line bg-fill-subtle p-3">
            <div className="mb-2 flex items-center justify-between gap-2">
              <span className="min-w-0 text-[11px] font-bold text-text-high">
                {t('flows.automations.introIf')}{' '}
                <span className="font-normal text-text-muted">{t('flows.automations.ifHint')}</span>
              </span>
              <button
                type="button"
                onClick={() => setConditions((c) => [...c, { field: 'message', op: 'contains', value: '' }])}
                className="shrink-0 text-[11px] font-semibold text-brand-300 hover:underline"
              >
                {t('flows.automations.addCondition')}
              </button>
            </div>
            {conditions.length === 0 && (
              <span className="text-[10.5px] text-text-muted">
                {t('flows.automations.noConditions', { event: label(EVENT_LABELS, catalog?.events, evt).toLowerCase() })}
              </span>
            )}
            <div className="flex flex-col gap-2">
              {conditions.map((c, i) => (
                <div key={i} className="flex flex-wrap items-center gap-1.5">
                  <Select
                    value={c.field}
                    onChange={(field) =>
                      setConditions((cur) => cur.map((x, j) => (j === i ? { ...x, field } : x)))
                    }
                    size="md"
                    ariaLabel={t('flows.automations.conditionField')}
                    className="w-[150px] shrink-0"
                    options={catalogOptions(FIELD_LABELS, catalog?.fields)}
                  />
                  <Select
                    value={c.op}
                    onChange={(op) => setConditions((cur) => cur.map((x, j) => (j === i ? { ...x, op } : x)))}
                    size="md"
                    ariaLabel={t('flows.automations.conditionOperator')}
                    className="w-[150px] shrink-0"
                    options={catalogOptions(OPERATOR_LABELS, catalog?.operators)}
                  />
                  {!VALUELESS.includes(c.op) && YES_NO_FIELDS.includes(c.field) && (
                    <Select
                      value={['true', 'yes', 'on', '1'].includes(c.value.toLowerCase()) ? 'true' : c.value ? 'false' : ''}
                      onChange={(value) => setConditions((cur) => cur.map((x, j) => (j === i ? { ...x, value } : x)))}
                      size="md"
                      ariaLabel={t('flows.automations.conditionValue')}
                      className="min-w-[110px] flex-1"
                      options={[
                        { value: '', label: t('flows.automations.pick') },
                        { value: 'true', label: t('common.status.yes') },
                        { value: 'false', label: t('common.status.no') },
                      ]}
                    />
                  )}
                  {!VALUELESS.includes(c.op) && c.field === 'contact_linked' && (
                    // the server compares this one with the words yes / no
                    <Select
                      value={['yes', 'true'].includes(c.value.toLowerCase()) ? 'yes' : c.value ? 'no' : ''}
                      onChange={(value) => setConditions((cur) => cur.map((x, j) => (j === i ? { ...x, value } : x)))}
                      size="md"
                      ariaLabel={t('flows.automations.conditionValue')}
                      className="min-w-[110px] flex-1"
                      options={[
                        { value: '', label: t('flows.automations.pick') },
                        { value: 'yes', label: t('common.status.yes') },
                        { value: 'no', label: t('common.status.no') },
                      ]}
                    />
                  )}
                  {!VALUELESS.includes(c.op) && !YES_NO_FIELDS.includes(c.field) && c.field !== 'contact_linked' && (
                    <input
                      value={c.value}
                      onChange={(e) =>
                        setConditions((cur) =>
                          cur.map((x, j) => (j === i ? { ...x, value: e.target.value } : x)),
                        )
                      }
                      placeholder={t('flows.automations.value')}
                      aria-label={t('flows.automations.conditionValue')}
                      className={`${inputCls} flex-1 min-w-[140px]`}
                    />
                  )}
                  <button
                    type="button"
                    onClick={() => setConditions((cur) => cur.filter((_, j) => j !== i))}
                    aria-label={t('flows.automations.removeCondition')}
                    className="grid h-7 w-7 shrink-0 place-items-center rounded-md text-text-muted hover:bg-fill-hover"
                  >
                    <X size={13} aria-hidden />
                  </button>
                </div>
              ))}
            </div>
          </div>

          {/* actions */}
          <div className="rounded-lg border border-line bg-fill-subtle p-3">
            <div className="mb-2 flex items-center justify-between gap-2">
              <span className="text-[11px] font-bold text-text-high">{t('flows.automations.then')}</span>
              <button
                type="button"
                onClick={() => setActions((a) => [...a, { type: 'add_label', value: '' }])}
                className="shrink-0 text-[11px] font-semibold text-brand-300 hover:underline"
              >
                {t('flows.automations.addAction')}
              </button>
            </div>
            {actions.length === 0 && (
              <span className="text-[10.5px] text-[var(--amber-ink)]">{t('flows.automations.noActions')}</span>
            )}
            <div className="flex flex-col gap-2">
              {actions.map((a, i) => (
                <div key={i} className="flex flex-wrap items-center gap-1.5">
                  <Select
                    value={a.type}
                    onChange={(type) =>
                      setActions((cur) => cur.map((x, j) => (j === i ? { type, value: '' } : x)))
                    }
                    size="md"
                    ariaLabel={t('flows.automations.action')}
                    className="w-[190px] shrink-0"
                    options={catalogOptions(ACTION_LABELS, catalog?.actions)}
                  />
                  {actionValueControl(a, i)}
                  <button
                    type="button"
                    onClick={() => setActions((cur) => cur.filter((_, j) => j !== i))}
                    aria-label={t('flows.automations.removeAction')}
                    className="grid h-7 w-7 shrink-0 place-items-center rounded-md text-text-muted hover:bg-fill-hover"
                  >
                    <X size={13} aria-hidden />
                  </button>
                </div>
              ))}
            </div>
          </div>

          <label className="flex cursor-pointer items-center gap-2">
            <input
              type="checkbox"
              checked={active}
              onChange={(e) => setActive(e.target.checked)}
              className="h-3.5 w-3.5 accent-emerald-500"
            />
            <span className="text-[12px] text-text-primary">
              {t('common.status.active')}
              <span className="ml-1 text-[10.5px] text-text-muted">{t('flows.automations.activeHint')}</span>
            </span>
          </label>
        </div>
      </Modal>
    </div>
  )
}

interface TestResult {
  matches: boolean
  active: boolean
  conditions: { field: string; op: string; value: string; actual: string; ok: boolean }[]
}

/**
 * "Would this rule fire?" — pick a real conversation (and optionally type a
 * message), and the server runs the rule's conditions against it without
 * changing anything (POST /api/automations/{id}/test), condition by condition.
 */
function TestRuleModal({
  rule,
  onClose,
  fieldLabel,
  opLabel,
}: {
  rule: Rule | null
  onClose: () => void
  fieldLabel: (id: string) => string
  opLabel: (id: string) => string
}) {
  const t = useT()
  const [q, setQ] = useState('')
  const [hits, setHits] = useState<{ id: string; title: string; subtitle: string }[]>([])
  const [picked, setPicked] = useState<{ id: string; title: string } | null>(null)
  const [message, setMessage] = useState('')
  const [busy, setBusy] = useState(false)
  const [result, setResult] = useState<TestResult | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (!rule) return
    setQ('')
    setHits([])
    setPicked(null)
    setMessage('')
    setResult(null)
    setError(null)
  }, [rule])

  useEffect(() => {
    if (!rule || q.trim().length < 2) {
      setHits([])
      return
    }
    const timer = window.setTimeout(async () => {
      try {
        const d = await api<{ results: { id: string; title: string; subtitle: string }[] }>(
          `search?types=conversations&limit=8&q=${encodeURIComponent(q.trim())}`,
        )
        setHits(d.results ?? [])
      } catch {
        setHits([])
      }
    }, 250)
    return () => window.clearTimeout(timer)
  }, [q, rule])

  const run = async () => {
    if (!rule || !picked) return
    const at = picked.id.indexOf(':')
    setBusy(true)
    setError(null)
    try {
      setResult(
        await api<TestResult>(`automations/${rule.id}/test`, {
          method: 'POST',
          body: JSON.stringify({ channel: picked.id.slice(0, at), key: picked.id.slice(at + 1), message }),
        }),
      )
    } catch (e) {
      setResult(null)
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal
      open={rule != null}
      onClose={onClose}
      title={rule ? t('flows.automations.testName', { name: rule.name }) : ''}
      subtitle={t('flows.automations.testIntro')}
    >
      <div className="flex flex-col gap-4">
        {error && (
          <div role="alert" className="rounded-lg bg-red/10 px-3 py-2 text-[12.5px] text-[var(--red-ink)]">
            {error}
          </div>
        )}
        <div>
          <label className="mb-1 block text-[12px] font-semibold text-text-high">{t('flows.automations.testConversation')}</label>
          {picked ? (
            <div className="flex items-center justify-between gap-2 rounded-lg border border-line-strong px-3 py-2 text-[12.5px]">
              <span className="min-w-0 truncate text-text-high">{picked.title}</span>
              <button type="button" onClick={() => setPicked(null)} className="text-[12px] font-semibold text-text-muted hover:text-text-high">
                {t('flows.automations.testChange')}
              </button>
            </div>
          ) : (
            <>
              <label className="flex items-center gap-2 rounded-lg border border-line-strong bg-card px-3 py-2">
                <Search size={14} className="shrink-0 text-text-muted" aria-hidden />
                <input
                  value={q}
                  onChange={(e) => setQ(e.target.value)}
                  placeholder={t('flows.automations.testSearch')}
                  aria-label={t('flows.automations.testSearch')}
                  className="min-w-0 flex-1 bg-transparent text-[12.5px] text-text-primary outline-none placeholder:text-text-faint"
                />
              </label>
              {hits.length > 0 && (
                <ul className="mt-1 max-h-[200px] overflow-y-auto rounded-lg border border-line">
                  {hits.map((h) => (
                    <li key={h.id}>
                      <button
                        type="button"
                        onClick={() => setPicked({ id: h.id, title: h.title })}
                        className="block w-full px-3 py-2 text-left hover:bg-fill-hover"
                      >
                        <span className="block truncate text-[12.5px] font-semibold text-text-high">{h.title}</span>
                        <span className="block truncate text-[11.5px] text-text-muted">{h.subtitle}</span>
                      </button>
                    </li>
                  ))}
                </ul>
              )}
            </>
          )}
        </div>
        <div>
          <label htmlFor="rule-test-message" className="mb-1 block text-[12px] font-semibold text-text-high">
            {t('flows.automations.testMessage')}
          </label>
          <input
            id="rule-test-message"
            value={message}
            onChange={(e) => setMessage(e.target.value)}
            placeholder={t('flows.automations.testMessagePlaceholder')}
            className={inputCls}
          />
        </div>
        <div>
          <Button onClick={run} disabled={!picked || busy}>
            {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <FlaskConical size={13} aria-hidden />}
            {t('flows.automations.testRun')}
          </Button>
        </div>

        {result && (
          <div role="status" className="rounded-xl border border-line p-3">
            <div className={`flex items-center gap-2 text-[13.5px] font-semibold ${result.matches ? 'text-[var(--green-ink)]' : 'text-[var(--red-ink)]'}`}>
              {result.matches ? <CheckCircle2 size={16} aria-hidden /> : <XCircle size={16} aria-hidden />}
              {result.matches ? t('flows.automations.testMatches') : t('flows.automations.testNoMatch')}
            </div>
            {result.matches && !result.active && (
              <p className="mt-1 text-[12px] text-[var(--amber-ink)]">{t('flows.automations.testInactive')}</p>
            )}
            {(result.conditions ?? []).length === 0 ? (
              <p className="mt-2 text-[12px] text-text-muted">{t('flows.automations.testNoConditions')}</p>
            ) : (
              <ul className="mt-2 flex flex-col gap-1.5">
                {(result.conditions ?? []).map((c, i) => (
                  <li key={i} className="flex items-start gap-2 text-[12px]">
                    {c.ok ? (
                      <CheckCircle2 size={14} className="mt-0.5 shrink-0 text-[var(--green-ink)]" aria-hidden />
                    ) : (
                      <XCircle size={14} className="mt-0.5 shrink-0 text-[var(--red-ink)]" aria-hidden />
                    )}
                    <span className="min-w-0 text-text-mid">
                      <span className="font-semibold text-text-high">
                        {fieldLabel(c.field)} {opLabel(c.op)} {c.value && `“${c.value}”`}
                      </span>
                      <span className="block text-text-muted">{t('flows.automations.testActual', { value: c.actual || '—' })}</span>
                    </span>
                  </li>
                ))}
              </ul>
            )}
          </div>
        )}
      </div>
    </Modal>
  )
}
