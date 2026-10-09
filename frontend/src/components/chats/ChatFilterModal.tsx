'use client'

import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { Loader2 } from 'lucide-react'
import { Modal } from '@/components/ui/Modal'
import { Button } from '@/components/ui/Button'
import { MultiSelect, Select, type SelectOption } from '@/components/ui/Select'
import { EntityMultiPicker } from '@/components/ui/EntityPicker'
import { ChannelLogo } from '@/components/ui/ChannelLogo'
import { useTags } from '@/lib/tags'
import {
  CHANNEL_META,
  CHANNELS,
  THREAD_STATUSES,
  api,
  logoKey,
  statusLabel,
  type UiAccount,
  type UiAgent,
  type UiTeam,
} from './inboxTypes'
import {
  DATE_PRESETS,
  EMPTY_FILTERS,
  SORTS,
  filtersToParams,
  type DatePreset,
  type DateRange,
  type InboxFilters,
} from './inboxFilters'

/** Eyebrow-labelled field cell of the filter grid. */
function Field({ label, children, wide, aside }: { label: string; children: ReactNode; wide?: boolean; aside?: ReactNode }) {
  return (
    <div className={`flex min-w-0 flex-col gap-2 ${wide ? 'sm:col-span-2' : ''}`}>
      <div className="flex items-center justify-between gap-2">
        <span className="eyebrow">{label}</span>
        {aside}
      </div>
      {children}
    </div>
  )
}

/** A multi-pick pill: ink when on, hairline when off. */
function Pill({ on, onClick, children, label }: { on: boolean; onClick: () => void; children: ReactNode; label?: string }) {
  return (
    <button
      type="button"
      aria-pressed={on}
      aria-label={label}
      onClick={onClick}
      className={`inline-flex h-8 items-center gap-1.5 rounded-full border px-3 text-[12.5px] font-medium transition-colors ${
        on ? 'border-primary bg-primary text-on-primary' : 'border-line-strong bg-card text-text-high hover:bg-fill-hover'
      }`}
    >
      {children}
    </button>
  )
}

/** Small segmented control for tri-state filters ("Any · On · Off"). */
function Segmented<T extends string>({
  value,
  onChange,
  options,
  label,
}: {
  value: T
  onChange: (v: T) => void
  options: { value: T; label: string }[]
  label: string
}) {
  return (
    <div className="flex min-w-0 flex-col gap-1.5">
      <span className="text-[12px] font-semibold text-text-mid">{label}</span>
      <div role="radiogroup" aria-label={label} className="flex w-full rounded-lg border border-line-strong bg-card p-0.5">
        {options.map((o) => {
          const on = o.value === value
          return (
            <button
              key={o.value || 'any'}
              type="button"
              role="radio"
              aria-checked={on}
              onClick={() => onChange(o.value)}
              className={`min-w-0 flex-1 truncate rounded-md px-2 py-1 text-[12px] font-medium transition-colors ${
                on ? 'bg-primary text-on-primary' : 'text-text-mid hover:bg-fill-hover hover:text-text-high'
              }`}
            >
              {o.label}
            </button>
          )
        })}
      </div>
    </div>
  )
}

function DateRangeField({ value, onChange, label }: { value: DateRange; onChange: (r: DateRange) => void; label: string }) {
  const input =
    'h-9 min-w-0 flex-1 rounded-lg border border-line-strong bg-card px-2.5 text-[12.5px] text-text-primary outline-none focus:border-text-primary'
  return (
    <Field label={label}>
      <Select
        value={value.preset}
        onChange={(v) => onChange({ preset: v as DatePreset, from: value.from, to: value.to })}
        options={DATE_PRESETS.map((p) => ({ value: p.value, label: p.label }))}
        size="md"
        ariaLabel={label}
      />
      {value.preset === 'custom' && (
        <div className="flex items-center gap-2">
          <input
            type="date"
            value={value.from ?? ''}
            max={value.to || undefined}
            onChange={(e) => onChange({ ...value, from: e.target.value || undefined })}
            aria-label={`${label} from`}
            className={input}
          />
          <span className="text-[12px] text-text-muted">to</span>
          <input
            type="date"
            value={value.to ?? ''}
            min={value.from || undefined}
            onChange={(e) => onChange({ ...value, to: e.target.value || undefined })}
            aria-label={`${label} to`}
            className={input}
          />
        </div>
      )}
    </Field>
  )
}

const toggle = <T,>(xs: T[], x: T) => (xs.includes(x) ? xs.filter((y) => y !== x) : [...xs, x])

/**
 * The inbox filter pop-up. Edits a DRAFT of the filters; nothing changes on
 * the page until "Show N chats". N is a live server count of the draft (the
 * same endpoint with count_only=1), so the button never promises a list the
 * server won't return.
 */
export default function ChatFilterModal({
  open,
  onClose,
  initial,
  q,
  topEntity,
  agents,
  teams,
  accounts,
  contactTags,
  onApply,
  onSaveView,
}: {
  open: boolean
  onClose: () => void
  initial: InboxFilters
  q: string
  topEntity: string | null
  agents: UiAgent[]
  teams: UiTeam[]
  accounts: UiAccount[]
  contactTags: string[]
  onApply: (f: InboxFilters) => void
  onSaveView: (name: string, f: InboxFilters) => Promise<void>
}) {
  // Conversation labels come from the Tags registry (Library → Tags).
  const { metas: conversationTags } = useTags('conversation')
  const [draft, setDraft] = useState<InboxFilters>(initial)
  const [count, setCount] = useState<number | null>(null)
  const [counting, setCounting] = useState(false)
  const [viewName, setViewName] = useState('')
  const [saving, setSaving] = useState(false)
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)

  // Every (re)open starts from what's applied on the page — only on the
  // closed→open edge, so saving a view (which re-applies the same filters
  // underneath) doesn't wipe the draft or the confirmation.
  const wasOpen = useRef(false)
  useEffect(() => {
    if (open && !wasOpen.current) {
      setDraft(initial)
      setViewName('')
      setMsg(null)
    }
    wasOpen.current = open
  }, [open, initial])

  const params = useMemo(() => {
    const p = filtersToParams(draft, { q, entity: topEntity })
    p.set('count_only', '1')
    return p.toString()
  }, [draft, q, topEntity])

  useEffect(() => {
    if (!open) return
    let cancelled = false
    setCounting(true)
    const t = setTimeout(() => {
      api<{ count: number }>(`inbox/threads?${params}`)
        .then((d) => !cancelled && setCount(d.count))
        .catch(() => !cancelled && setCount(null))
        .finally(() => !cancelled && setCounting(false))
    }, 220)
    return () => {
      cancelled = true
      clearTimeout(t)
    }
  }, [open, params])

  const set = (patch: Partial<InboxFilters>) => setDraft((d) => ({ ...d, ...patch }))

  const accountOptions: SelectOption[] = [
    ...accounts.map((a) => ({
      value: a.id,
      label: a.name || `Account #${a.id}`,
      group: CHANNEL_META[(a.channel ?? 'whatsapp') as keyof typeof CHANNEL_META]?.lbl ?? a.channel ?? 'Other',
      hint: a.provider === 'baileys' ? '(personal)' : undefined,
    })),
    { value: 'none', label: 'No sending account', group: 'Other' },
  ]
  const assigneeOptions: SelectOption[] = [
    { value: 'me', label: 'Me', group: 'Quick picks' },
    { value: 'unassigned', label: 'Unassigned', group: 'Quick picks' },
    ...agents.map((a) => ({ value: a.username, label: a.name, hint: `(${a.role})`, group: 'Teammates' })),
  ]
  const teamOptions: SelectOption[] = [
    ...teams.map((t) => ({ value: String(t.id), label: t.name })),
    { value: 'none', label: 'No team' },
  ]
  const labelOptions: SelectOption[] = [
    ...conversationTags.map((t) => ({ value: t.slug, label: t.label })),
    // keep a saved view's legacy free-text label visible
    ...draft.labels
      .filter((l) => !conversationTags.some((t) => t.slug === l))
      .map((l) => ({ value: l, label: l })),
  ]
  const tagOptions: SelectOption[] = Array.from(new Set([...contactTags, ...draft.tags]))
    .sort((a, b) => a.localeCompare(b))
    .map((t) => ({ value: t, label: t }))

  const saveView = async () => {
    const name = viewName.trim()
    if (!name) return
    setSaving(true)
    setMsg(null)
    try {
      await onSaveView(name, draft)
      setViewName('')
      setMsg({ ok: true, text: `Saved "${name}" — it's a tab above the list now.` })
    } catch (e) {
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    } finally {
      setSaving(false)
    }
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      size="lg"
      labelledById="chat-filter-title"
      title={
        <span className="flex items-baseline gap-3">
          Filter conversations
          <span className="font-sans text-[13px] font-normal tracking-normal text-text-muted">
            {counting && count === null ? '…' : count === null ? '' : `${count} match`}
          </span>
        </span>
      }
      footer={
        <div className="flex w-full flex-wrap items-center gap-2.5">
          <button
            type="button"
            onClick={() => setDraft({ ...EMPTY_FILTERS, sort: draft.sort })}
            className="text-[13px] font-semibold text-[color:var(--brand-ink)] hover:underline"
          >
            Clear all
          </button>
          <div className="ml-auto flex min-w-0 flex-wrap items-center gap-2">
            <label className="flex h-9 min-w-0 items-center gap-2 rounded-lg border border-line-strong bg-card px-2.5">
              <span className="shrink-0 text-[12.5px] text-text-muted">Save as view</span>
              <input
                value={viewName}
                onChange={(e) => setViewName(e.target.value)}
                onKeyDown={(e) => e.key === 'Enter' && saveView()}
                placeholder="e.g. VIP drivers"
                aria-label="View name"
                maxLength={60}
                className="w-[120px] min-w-0 bg-transparent text-[13px] text-text-primary outline-none placeholder:text-text-faint"
              />
              {viewName.trim() && (
                <button
                  type="button"
                  onClick={saveView}
                  disabled={saving}
                  className="shrink-0 text-[12.5px] font-semibold text-text-high hover:underline disabled:opacity-50"
                >
                  {saving ? 'Saving…' : 'Save'}
                </button>
              )}
            </label>
            <Button
              variant="primary"
              onClick={() => {
                onApply(draft)
                onClose()
              }}
            >
              {counting && <Loader2 size={13} className="animate-spin" aria-hidden />}
              {count === null ? 'Show chats' : `Show ${count} ${count === 1 ? 'chat' : 'chats'}`}
            </Button>
          </div>
        </div>
      }
    >
      {msg && (
        <div
          role={msg.ok ? 'status' : 'alert'}
          className={`mb-4 rounded-lg border px-3 py-2 text-[12.5px] font-semibold ${
            msg.ok ? 'border-green/30 bg-green/10 text-green-300' : 'border-red/30 bg-red/10 text-red-300'
          }`}
        >
          {msg.text}
        </div>
      )}
      <div className="grid grid-cols-1 gap-x-[22px] gap-y-[18px] sm:grid-cols-2">
        <Field label="Status">
          <div className="flex flex-wrap gap-1.5">
            {THREAD_STATUSES.map((s) => (
              <Pill key={s} on={draft.status.includes(s)} onClick={() => set({ status: toggle(draft.status, s) })}>
                {statusLabel(s)}
              </Pill>
            ))}
          </div>
        </Field>

        <Field label="Channel">
          <div className="flex flex-wrap gap-1.5">
            {CHANNELS.map((c) => {
              const on = draft.channels.includes(c)
              return (
                <Pill key={c} on={on} onClick={() => set({ channels: toggle(draft.channels, c) })}>
                  <ChannelLogo channel={logoKey(c)} size={13} variant={on ? 'mono' : 'mark'} title="" />
                  {CHANNEL_META[c].lbl}
                </Pill>
              )
            })}
          </div>
        </Field>

        <Field label="Assignee">
          <MultiSelect
            value={draft.assignees}
            onChange={(v) => set({ assignees: v })}
            options={assigneeOptions}
            placeholder="Anyone"
            size="md"
            ariaLabel="Assignee"
          />
        </Field>

        <Field label="Team">
          <MultiSelect
            value={draft.teams}
            onChange={(v) => set({ teams: v })}
            options={teamOptions}
            placeholder="Any team"
            size="md"
            ariaLabel="Team"
          />
        </Field>

        <Field
          label="Entity"
          aside={
            draft.entities !== null && (
              <button
                type="button"
                onClick={() => set({ entities: null })}
                className="text-[11.5px] font-semibold text-text-muted hover:text-text-high"
              >
                Follow top bar
              </button>
            )
          }
        >
          <EntityMultiPicker
            value={draft.entities ?? (topEntity ? [topEntity] : [])}
            onChange={(slugs) => set({ entities: slugs })}
            placeholder="All entities"
            ariaLabel="Entity"
          />
        </Field>

        <Field label="Sending account / number">
          <MultiSelect
            value={draft.accounts}
            onChange={(v) => set({ accounts: v })}
            options={accountOptions}
            placeholder="Any account"
            size="md"
            ariaLabel="Sending account"
          />
        </Field>

        <Field
          label="Conversation labels"
          aside={
            <div className="flex rounded-md border border-line p-px text-[11px]">
              {(['any', 'all'] as const).map((m) => (
                <button
                  key={m}
                  type="button"
                  aria-pressed={draft.labelsMode === m}
                  onClick={() => set({ labelsMode: m })}
                  className={`rounded px-1.5 py-px font-semibold ${
                    draft.labelsMode === m ? 'bg-primary text-on-primary' : 'text-text-muted hover:text-text-high'
                  }`}
                >
                  {m === 'any' ? 'Any of' : 'All of'}
                </button>
              ))}
            </div>
          }
        >
          <MultiSelect
            value={draft.labels}
            onChange={(v) => set({ labels: v })}
            options={labelOptions}
            placeholder="Any label"
            size="md"
            ariaLabel="Conversation labels"
          />
        </Field>

        <Field label="Contact tags">
          <MultiSelect
            value={draft.tags}
            onChange={(v) => set({ tags: v })}
            options={tagOptions}
            placeholder={tagOptions.length ? 'Any tag' : 'No contact tags yet'}
            size="md"
            ariaLabel="Contact tags"
            searchable
          />
        </Field>

        <DateRangeField label="Last activity" value={draft.active} onChange={(r) => set({ active: r })} />
        <DateRangeField label="Created" value={draft.created} onChange={(r) => set({ created: r })} />

        <Field label="Conversation state" wide>
          <label className="flex cursor-pointer items-center gap-2 text-[13px] text-text-high">
            <input
              type="checkbox"
              checked={draft.waiting}
              onChange={(e) => set({ waiting: e.target.checked })}
              className="h-4 w-4 accent-[var(--primary)]"
            />
            Waiting for our reply <span className="text-text-muted">(the last message came from the customer)</span>
          </label>
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
            <Segmented
              label="AI auto-reply"
              value={draft.ai}
              onChange={(v) => set({ ai: v })}
              options={[
                { value: '', label: 'Any' },
                { value: 'on', label: 'On' },
                { value: 'off', label: 'Off' },
              ]}
            />
            <Segmented
              label="24h window"
              value={draft.window}
              onChange={(v) => set({ window: v })}
              options={[
                { value: '', label: 'Any' },
                { value: 'open', label: 'Open' },
                { value: 'closed', label: 'Closed' },
              ]}
            />
            <Segmented
              label="Opted out"
              value={draft.optedOut}
              onChange={(v) => set({ optedOut: v })}
              options={[
                { value: '', label: 'Any' },
                { value: 'yes', label: 'Yes' },
                { value: 'no', label: 'No' },
              ]}
            />
            <Segmented
              label="Calls"
              value={draft.call}
              onChange={(v) => set({ call: v })}
              options={[
                { value: '', label: 'Any' },
                { value: 'any', label: 'Has a call' },
                { value: 'live', label: 'Live now' },
              ]}
            />
            <Segmented
              label="Contact"
              value={draft.linked}
              onChange={(v) => set({ linked: v })}
              options={[
                { value: '', label: 'Any' },
                { value: 'yes', label: 'Linked' },
                { value: 'no', label: 'Not linked' },
              ]}
            />
            <div className="flex min-w-0 flex-col gap-1.5">
              <span className="text-[12px] font-semibold text-text-mid">Sort</span>
              <Select
                value={draft.sort}
                onChange={(v) => set({ sort: v as InboxFilters['sort'] })}
                options={SORTS.map((s) => ({ value: s.value, label: s.label }))}
                ariaLabel="Sort"
              />
            </div>
          </div>
        </Field>
      </div>
    </Modal>
  )
}
