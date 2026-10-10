'use client'

import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { Loader2 } from 'lucide-react'
import { Modal } from '@/components/ui/Modal'
import { Button } from '@/components/ui/Button'
import { MultiSelect, Select, type SelectOption } from '@/components/ui/Select'
import { EntityMultiPicker } from '@/components/ui/EntityPicker'
import { ChannelLogo } from '@/components/ui/ChannelLogo'
import { useTags } from '@/lib/tags'
import { HANDOVER_KINDS } from '@/lib/aiControls'
import { useT } from '@/i18n'
import {
  CHANNELS,
  THREAD_STATUSES,
  api,
  channelName,
  logoKey,
  roleLabel,
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
  const t = useT()
  const input =
    'h-9 min-w-0 flex-1 rounded-lg border border-line-strong bg-card px-2.5 text-[12.5px] text-text-primary outline-none focus:border-text-primary'
  return (
    <Field label={label}>
      <Select
        value={value.preset}
        onChange={(v) => onChange({ preset: v as DatePreset, from: value.from, to: value.to })}
        options={DATE_PRESETS.map((p) => ({ value: p.value, label: t(p.label) }))}
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
            aria-label={t('chats.filterModal.dateFrom', { label })}
            className={input}
          />
          <span className="text-[12px] text-text-muted">{t('chats.filterModal.to')}</span>
          <input
            type="date"
            value={value.to ?? ''}
            min={value.from || undefined}
            onChange={(e) => onChange({ ...value, to: e.target.value || undefined })}
            aria-label={t('chats.filterModal.dateTo', { label })}
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
  topEntities,
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
  topEntities: string[]
  agents: UiAgent[]
  teams: UiTeam[]
  accounts: UiAccount[]
  contactTags: string[]
  onApply: (f: InboxFilters) => void
  onSaveView: (name: string, f: InboxFilters) => Promise<void>
}) {
  const t = useT()
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
    const p = filtersToParams(draft, { q, entities: topEntities })
    p.set('count_only', '1')
    return p.toString()
  }, [draft, q, topEntities])

  useEffect(() => {
    if (!open) return
    let cancelled = false
    setCounting(true)
    const timer = setTimeout(() => {
      api<{ count: number }>(`inbox/threads?${params}`)
        .then((d) => !cancelled && setCount(d.count))
        .catch(() => !cancelled && setCount(null))
        .finally(() => !cancelled && setCounting(false))
    }, 220)
    return () => {
      cancelled = true
      clearTimeout(timer)
    }
  }, [open, params])

  const set = (patch: Partial<InboxFilters>) => setDraft((d) => ({ ...d, ...patch }))

  const accountOptions: SelectOption[] = [
    ...accounts.map((a) => ({
      value: a.id,
      label: a.name || t('chats.info.accountN', { id: a.id }),
      group: a.channel ? channelName(t, a.channel) : channelName(t, 'whatsapp'),
      hint: a.provider === 'baileys' ? t('chats.filterModal.personal') : undefined,
    })),
    { value: 'none', label: t('chats.filterModal.noSendingAccount'), group: t('chats.filterModal.other') },
  ]
  const quickPicks = t('chats.filterModal.quickPicks')
  const assigneeOptions: SelectOption[] = [
    { value: 'me', label: t('chats.filterModal.me'), group: quickPicks },
    { value: 'unassigned', label: t('chats.filterModal.unassigned'), group: quickPicks },
    ...agents.map((a) => ({
      value: a.username,
      label: a.name,
      hint: `(${roleLabel(t, a.role)})`,
      group: t('chats.filterModal.teammates'),
    })),
  ]
  const teamOptions: SelectOption[] = [
    ...teams.map((tm) => ({ value: String(tm.id), label: tm.name })),
    { value: 'none', label: t('chats.filterModal.noTeam') },
  ]
  const labelOptions: SelectOption[] = [
    ...conversationTags.map((tg) => ({ value: tg.slug, label: tg.label })),
    // keep a saved view's legacy free-text label visible
    ...draft.labels
      .filter((l) => !conversationTags.some((tg) => tg.slug === l))
      .map((l) => ({ value: l, label: l })),
  ]
  const tagOptions: SelectOption[] = Array.from(new Set([...contactTags, ...draft.tags]))
    .sort((a, b) => a.localeCompare(b))
    .map((tag) => ({ value: tag, label: tag }))

  const saveView = async () => {
    const name = viewName.trim()
    if (!name) return
    setSaving(true)
    setMsg(null)
    try {
      await onSaveView(name, draft)
      setViewName('')
      setMsg({ ok: true, text: t('chats.filterModal.savedView', { name }) })
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
          {t('chats.filterModal.title')}
          <span className="font-sans text-[13px] font-normal tracking-normal text-text-muted">
            {counting && count === null ? '…' : count === null ? '' : t('chats.filterModal.matches', { count })}
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
            {t('chats.list.clearAll')}
          </button>
          <div className="ml-auto flex min-w-0 flex-wrap items-center gap-2">
            <label className="flex h-9 min-w-0 items-center gap-2 rounded-lg border border-line-strong bg-card px-2.5">
              <span className="shrink-0 text-[12.5px] text-text-muted">{t('chats.filterModal.saveAsView')}</span>
              <input
                value={viewName}
                onChange={(e) => setViewName(e.target.value)}
                onKeyDown={(e) => e.key === 'Enter' && saveView()}
                placeholder={t('chats.filterModal.viewNamePlaceholder')}
                aria-label={t('chats.filterModal.viewNameAria')}
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
                  {saving ? t('common.status.saving') : t('common.actions.save')}
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
              {count === null ? t('chats.filterModal.showChats') : t('chats.filterModal.showN', { count })}
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
        <Field label={t('common.fields.status')}>
          <div className="flex flex-wrap gap-1.5">
            {THREAD_STATUSES.map((s) => (
              <Pill key={s} on={draft.status.includes(s)} onClick={() => set({ status: toggle(draft.status, s) })}>
                {statusLabel(t, s)}
              </Pill>
            ))}
          </div>
        </Field>

        <Field label={t('common.fields.channel')}>
          <div className="flex flex-wrap gap-1.5">
            {CHANNELS.map((c) => {
              const on = draft.channels.includes(c)
              return (
                <Pill key={c} on={on} onClick={() => set({ channels: toggle(draft.channels, c) })}>
                  <ChannelLogo channel={logoKey(c)} size={13} variant={on ? 'mono' : 'mark'} title="" />
                  {channelName(t, c)}
                </Pill>
              )
            })}
          </div>
        </Field>

        <Field label={t('chats.filterModal.assignee')}>
          <MultiSelect
            value={draft.assignees}
            onChange={(v) => set({ assignees: v })}
            options={assigneeOptions}
            placeholder={t('chats.filterModal.anyone')}
            size="md"
            ariaLabel={t('chats.filterModal.assignee')}
          />
        </Field>

        <Field label={t('chats.filterModal.team')}>
          <MultiSelect
            value={draft.teams}
            onChange={(v) => set({ teams: v })}
            options={teamOptions}
            placeholder={t('chats.filterModal.anyTeam')}
            size="md"
            ariaLabel={t('chats.filterModal.team')}
          />
        </Field>

        <Field
          label={t('common.fields.entity')}
          aside={
            draft.entities !== null && (
              <button
                type="button"
                onClick={() => set({ entities: null })}
                className="text-[11.5px] font-semibold text-text-muted hover:text-text-high"
              >
                {t('chats.filterModal.followTopBar')}
              </button>
            )
          }
        >
          <EntityMultiPicker
            value={draft.entities ?? topEntities}
            onChange={(slugs) => set({ entities: slugs })}
            placeholder={t('chats.filterModal.allEntities')}
            ariaLabel={t('common.fields.entity')}
          />
        </Field>

        <Field label={t('chats.filterModal.account')}>
          <MultiSelect
            value={draft.accounts}
            onChange={(v) => set({ accounts: v })}
            options={accountOptions}
            placeholder={t('chats.filterModal.anyAccount')}
            size="md"
            ariaLabel={t('chats.filterModal.accountAria')}
          />
        </Field>

        <Field
          label={t('chats.filterModal.labels')}
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
                  {t(m === 'any' ? 'chats.filterModal.anyOf' : 'chats.filterModal.allOf')}
                </button>
              ))}
            </div>
          }
        >
          <MultiSelect
            value={draft.labels}
            onChange={(v) => set({ labels: v })}
            options={labelOptions}
            placeholder={t('chats.filterModal.anyLabel')}
            size="md"
            ariaLabel={t('chats.filterModal.labels')}
          />
        </Field>

        <Field label={t('chats.filterModal.contactTags')}>
          <MultiSelect
            value={draft.tags}
            onChange={(v) => set({ tags: v })}
            options={tagOptions}
            placeholder={t(tagOptions.length ? 'chats.filterModal.anyTag' : 'chats.filterModal.noContactTags')}
            size="md"
            ariaLabel={t('chats.filterModal.contactTags')}
            searchable
          />
        </Field>

        <DateRangeField label={t('chats.filterModal.lastActivity')} value={draft.active} onChange={(r) => set({ active: r })} />
        <DateRangeField label={t('common.fields.created')} value={draft.created} onChange={(r) => set({ created: r })} />

        <Field label={t('chats.filterModal.state')} wide>
          <label className="flex cursor-pointer items-center gap-2 text-[13px] text-text-high">
            <input
              type="checkbox"
              checked={draft.waiting}
              onChange={(e) => set({ waiting: e.target.checked })}
              className="h-4 w-4 accent-[var(--primary)]"
            />
            {t('chats.filterModal.waiting')} <span className="text-text-muted">{t('chats.filterModal.waitingHint')}</span>
          </label>
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
            <Segmented
              label={t('chats.filterModal.aiAutoReply')}
              value={draft.ai}
              onChange={(v) => set({ ai: v })}
              options={[
                { value: '', label: t('chats.filterModal.any') },
                { value: 'on', label: t('common.status.on') },
                { value: 'off', label: t('common.status.off') },
              ]}
            />
            <Segmented
              label={t('chats.filterModal.window24h')}
              value={draft.window}
              onChange={(v) => set({ window: v })}
              options={[
                { value: '', label: t('chats.filterModal.any') },
                { value: 'open', label: t('chats.filterModal.open') },
                { value: 'closed', label: t('chats.filterModal.closed') },
              ]}
            />
            <Segmented
              label={t('chats.list.optedOut')}
              value={draft.optedOut}
              onChange={(v) => set({ optedOut: v })}
              options={[
                { value: '', label: t('chats.filterModal.any') },
                { value: 'yes', label: t('common.status.yes') },
                { value: 'no', label: t('common.status.no') },
              ]}
            />
            <Segmented
              label={t('chats.filterModal.calls')}
              value={draft.call}
              onChange={(v) => set({ call: v })}
              options={[
                { value: '', label: t('chats.filterModal.any') },
                { value: 'any', label: t('chats.filterModal.hasCall') },
                { value: 'live', label: t('chats.filterModal.liveNow') },
              ]}
            />
            <Segmented
              label={t('chats.filterModal.contact')}
              value={draft.linked}
              onChange={(v) => set({ linked: v })}
              options={[
                { value: '', label: t('chats.filterModal.any') },
                { value: 'yes', label: t('chats.filterModal.linked') },
                { value: 'no', label: t('chats.filterModal.notLinked') },
              ]}
            />
            <div className="flex min-w-0 flex-col gap-1.5">
              <span className="text-[12px] font-semibold text-text-mid">{t('chats.filterModal.sort')}</span>
              <Select
                value={draft.sort}
                onChange={(v) => set({ sort: v as InboxFilters['sort'] })}
                options={SORTS.map((s) => ({ value: s.value, label: t(s.label) }))}
                ariaLabel={t('chats.filterModal.sort')}
              />
            </div>
          </div>
        </Field>

        {/* human handover (Ai/Handover): the AI — or a rule, a flow, a person — passed it on */}
        <Field label={t('chats.filterModal.handedToHuman')} wide>
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
            <Segmented
              label={t('chats.filterModal.handover')}
              value={draft.handover}
              onChange={(v) => set({ handover: v })}
              options={[
                { value: '', label: t('chats.filterModal.any') },
                { value: 'open', label: t('chats.filterModal.needsHuman') },
                { value: 'any', label: t('chats.filterModal.ever') },
                { value: 'none', label: t('common.status.never') },
              ]}
            />
            <div className="flex min-w-0 flex-col gap-1.5">
              <span className="text-[12px] font-semibold text-text-mid">{t('chats.filterModal.why')}</span>
              <MultiSelect
                value={draft.handoverKinds}
                onChange={(v) => set({ handoverKinds: v })}
                options={HANDOVER_KINDS.map((k) => ({ value: k.value, label: t(k.label) }))}
                placeholder={t('chats.filterModal.anyReason')}
                size="md"
                ariaLabel={t('chats.filterModal.handoverReason')}
              />
            </div>
          </div>
        </Field>
      </div>
    </Modal>
  )
}
