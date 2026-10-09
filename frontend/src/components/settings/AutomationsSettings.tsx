'use client'

import { useCallback, useEffect, useState } from 'react'
import { Loader2, Pencil, Plus, Trash2, Workflow, X, Zap } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Modal } from '@/components/ui/Modal'
import { Select } from '@/components/ui/Select'
import { authHeaders, hasCap } from '@/lib/auth'
import { useEntity, useEntityQuery } from '@/lib/EntityContext'
import { EntityScopeSelect } from './EntityScopeSelect'

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

export function AutomationsSettings() {
  const entityQ = useEntityQuery()
  const { activeEntity } = useEntity()
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
  const [entity, setEntity] = useState<string | null>(null)
  const [conditions, setConditions] = useState<Condition[]>([])
  const [actions, setActions] = useState<Action[]>([])

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
    setEntity(activeEntity?.slug ?? null)
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
    setEntity(r.entity)
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
        entity,
        // strip half-filled rows rather than saving conditions that can never match
        conditions: conditions.filter((c) => c.field && c.op),
        actions: actions.filter((a) => a.type),
      })
      if (editing) await api(`automations/${editing.id}`, { method: 'PATCH', body })
      else await api('automations', { method: 'POST', body })
      setOpen(false)
      setMsg({ ok: true, text: `Rule ${name.trim()} ${editing ? 'updated' : 'created'}.` })
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
    if (!window.confirm(`Delete rule "${r.name}"? Conversations it already touched are unaffected.`)) return
    try {
      await api(`automations/${r.id}`, { method: 'DELETE' })
      setMsg({ ok: true, text: `Rule ${r.name} deleted.` })
      await load()
    } catch (e) {
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    }
  }

  if (rules === null) {
    return (
      <div className="py-6 text-[12px] text-text-muted">
        Automations unavailable{loadError ? ` — ${loadError}` : ' — is the backend API running?'}
      </div>
    )
  }

  const label = (list: { id: string; label: string }[] | undefined, id: string) =>
    list?.find((x) => x.id === id)?.label ?? id

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
          ariaLabel="Team"
          className="flex-1"
          options={[
            { value: '', label: 'Pick a team…' },
            ...(catalog?.teams ?? []).map((t) => ({ value: String(t.id), label: t.name })),
          ]}
        />
      )
    if (a.type === 'assign_agent')
      return (
        <Select
          value={a.value}
          onChange={set}
          size="md"
          ariaLabel="Agent"
          className="flex-1"
          options={[
            { value: '', label: 'Pick an agent…' },
            ...(catalog?.agents ?? []).map((x) => ({ value: x.username, label: x.username, hint: x.role })),
          ]}
        />
      )
    if (a.type === 'set_status')
      return (
        <Select
          value={a.value}
          onChange={set}
          size="md"
          ariaLabel="Status"
          className="flex-1"
          options={(catalog?.statuses ?? []).map((s) => ({ value: s, label: s }))}
        />
      )
    return (
      <input
        value={a.value}
        onChange={(e) => set(e.target.value)}
        placeholder={a.type === 'add_note' ? 'Note text' : a.type === 'hand_over' ? 'Why (shown to the team)' : 'Value'}
        className={`${inputCls} flex-1 min-w-[140px]`}
        aria-label="Action value"
      />
    )
  }

  return (
    <div className="flex flex-col gap-4 pt-3.5">
      <p className="max-w-[720px] text-[11.5px] leading-relaxed text-text-mid">
        <b>When</b> something happens to a conversation, <b>if</b> the conditions hold, <b>do</b> these
        actions. Every matching rule runs, in order — this is not a first-match switch. For contact-centric
        sequences over days (reminders, follow-ups) use the <b>Flow Builder</b> instead.
      </p>

      {msg && (!open || msg.ok) && (
        <div className={`text-[12px] font-semibold ${msg.ok ? 'text-green-300' : 'text-red-300'}`}>
          {msg.text}
        </div>
      )}

      <div className="flex items-center justify-between gap-3">
        <span className="text-[11px] font-bold uppercase tracking-[0.06em] text-text-faint">
          {rules.length} rule{rules.length === 1 ? '' : 's'}
        </span>
        {canManage && (
          <Button onClick={openCreate}>
            <Plus size={13} aria-hidden /> Add rule
          </Button>
        )}
      </div>

      <div className="flex flex-col gap-2">
        {rules.length === 0 && (
          <div className="rounded-xl border border-dashed border-line-strong px-4 py-6 text-center text-[12px] text-text-muted">
            No rules yet — e.g. &ldquo;when a message contains <i>refund</i>, label it and assign the billing
            team&rdquo;.
          </div>
        )}
        {rules.map((r) => (
          <div key={r.id} className="flex items-start gap-3 rounded-xl border border-line bg-card px-4 py-3">
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
                  {label(catalog?.events, r.event)}
                </span>
                {!r.active && (
                  <span className="rounded-full bg-fill px-2 py-px text-[10px] font-bold text-text-muted">Off</span>
                )}
                {r.times_fired > 0 && (
                  <span className="text-[10px] text-text-faint">fired {r.times_fired}×</span>
                )}
              </div>
              {r.description && (
                <div className="mt-0.5 text-[11.5px] text-text-muted">{r.description}</div>
              )}
              <div className="mt-1 flex flex-wrap gap-1.5 text-[10.5px]">
                {(r.conditions ?? []).map((c, i) => (
                  <span key={i} className="rounded bg-fill px-1.5 py-0.5 text-text-mid">
                    {label(catalog?.fields, c.field)} {label(catalog?.operators, c.op)}{' '}
                    {VALUELESS.includes(c.op) ? '' : `“${c.value}”`}
                  </span>
                ))}
                {(r.actions ?? []).map((a, i) => (
                  <span key={i} className="rounded bg-brand-400/10 px-1.5 py-0.5 text-brand-300">
                    → {label(catalog?.actions, a.type)}
                    {NO_VALUE_ACTIONS.includes(a.type) || !a.value ? '' : `: ${a.value}`}
                  </span>
                ))}
              </div>
            </div>
            {canManage && (
              <div className="flex shrink-0 items-center gap-1">
                <button
                  onClick={() => toggle(r)}
                  title={r.active ? 'Turn off' : 'Turn on'}
                  aria-label={`${r.active ? 'Deactivate' : 'Activate'} ${r.name}`}
                  className="rounded-lg px-2 py-1 text-[11px] font-semibold text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
                >
                  {r.active ? 'On' : 'Off'}
                </button>
                <button
                  onClick={() => openEdit(r)}
                  title={`Edit ${r.name}`}
                  aria-label={`Edit rule ${r.name}`}
                  className="grid h-7 w-7 place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
                >
                  <Pencil size={13} aria-hidden />
                </button>
                <button
                  onClick={() => remove(r)}
                  title={`Delete ${r.name}`}
                  aria-label={`Delete rule ${r.name}`}
                  className="grid h-7 w-7 place-items-center rounded-lg text-text-muted transition-colors hover:bg-red/15 hover:text-red-300"
                >
                  <Trash2 size={13} aria-hidden />
                </button>
              </div>
            )}
          </div>
        ))}
      </div>

      <Modal
        open={open}
        onClose={() => setOpen(false)}
        title={editing ? `Edit ${editing.name}` : 'New automation rule'}
        subtitle="When this happens, if these hold, do that."
        size="lg"
        footer={
          <>
            <Button variant="ghost" onClick={() => setOpen(false)} disabled={busy}>
              Cancel
            </Button>
            <Button variant="primary" onClick={save} disabled={busy || !name.trim()}>
              {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Workflow size={13} aria-hidden />}{' '}
              {editing ? 'Save changes' : 'Create rule'}
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
              <span className="mb-1 block text-[11px] font-bold text-text-high">Rule name</span>
              <input
                value={name}
                onChange={(e) => setName(e.target.value)}
                placeholder="e.g. Route refunds to billing"
                className={inputCls}
              />
            </label>
            <label className="block">
              <span className="mb-1 block text-[11px] font-bold text-text-high">When</span>
              <Select
                value={evt}
                onChange={setEvt}
                size="md"
                ariaLabel="Trigger event"
                options={(catalog?.events ?? []).map((e) => ({ value: e.id, label: e.label }))}
              />
            </label>
          </div>

          <label className="block">
            <span className="mb-1 block text-[11px] font-bold text-text-high">Description</span>
            <input
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              placeholder="What this rule is for"
              className={inputCls}
            />
          </label>

          <label className="block">
            <span className="mb-1 block text-[11px] font-bold text-text-high">Applies to</span>
            <EntityScopeSelect
              value={entity}
              onChange={setEntity}
              globalLabel="Organization-wide"
              className="w-full"
            />
          </label>

          {/* conditions */}
          <div className="rounded-lg border border-line bg-fill-subtle p-3">
            <div className="mb-2 flex items-center justify-between">
              <span className="text-[11px] font-bold text-text-high">
                If <span className="font-normal text-text-muted">(all must hold — none = always)</span>
              </span>
              <button
                type="button"
                onClick={() => setConditions((c) => [...c, { field: 'message', op: 'contains', value: '' }])}
                className="text-[11px] font-semibold text-brand-300 hover:underline"
              >
                + Add condition
              </button>
            </div>
            {conditions.length === 0 && (
              <span className="text-[10.5px] text-text-muted">
                No conditions — this rule fires on every {label(catalog?.events, evt).toLowerCase()}.
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
                    ariaLabel="Condition field"
                    className="w-[150px] shrink-0"
                    options={(catalog?.fields ?? []).map((f) => ({ value: f.id, label: f.label }))}
                  />
                  <Select
                    value={c.op}
                    onChange={(op) => setConditions((cur) => cur.map((x, j) => (j === i ? { ...x, op } : x)))}
                    size="md"
                    ariaLabel="Condition operator"
                    className="w-[150px] shrink-0"
                    options={(catalog?.operators ?? []).map((o) => ({ value: o.id, label: o.label }))}
                  />
                  {!VALUELESS.includes(c.op) && YES_NO_FIELDS.includes(c.field) && (
                    <Select
                      value={['true', 'yes', 'on', '1'].includes(c.value.toLowerCase()) ? 'true' : c.value ? 'false' : ''}
                      onChange={(value) => setConditions((cur) => cur.map((x, j) => (j === i ? { ...x, value } : x)))}
                      size="md"
                      ariaLabel="Condition value"
                      className="min-w-[110px] flex-1"
                      options={[
                        { value: '', label: 'Pick…' },
                        { value: 'true', label: 'Yes' },
                        { value: 'false', label: 'No' },
                      ]}
                    />
                  )}
                  {!VALUELESS.includes(c.op) && !YES_NO_FIELDS.includes(c.field) && (
                    <input
                      value={c.value}
                      onChange={(e) =>
                        setConditions((cur) =>
                          cur.map((x, j) => (j === i ? { ...x, value: e.target.value } : x)),
                        )
                      }
                      placeholder="Value"
                      aria-label="Condition value"
                      className={`${inputCls} flex-1 min-w-[140px]`}
                    />
                  )}
                  <button
                    type="button"
                    onClick={() => setConditions((cur) => cur.filter((_, j) => j !== i))}
                    aria-label="Remove condition"
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
            <div className="mb-2 flex items-center justify-between">
              <span className="text-[11px] font-bold text-text-high">Then</span>
              <button
                type="button"
                onClick={() => setActions((a) => [...a, { type: 'add_label', value: '' }])}
                className="text-[11px] font-semibold text-brand-300 hover:underline"
              >
                + Add action
              </button>
            </div>
            {actions.length === 0 && (
              <span className="text-[10.5px] text-amber-300">
                A rule with no actions does nothing — add at least one.
              </span>
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
                    ariaLabel="Action"
                    className="w-[190px] shrink-0"
                    options={(catalog?.actions ?? []).map((x) => ({ value: x.id, label: x.label }))}
                  />
                  {actionValueControl(a, i)}
                  <button
                    type="button"
                    onClick={() => setActions((cur) => cur.filter((_, j) => j !== i))}
                    aria-label="Remove action"
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
              Active
              <span className="ml-1 text-[10.5px] text-text-muted">— off means it never runs</span>
            </span>
          </label>
        </div>
      </Modal>
    </div>
  )
}
