'use client'

/**
 * Shared building blocks of the Templates module (/templates): one list
 * layout and one editor for message quick replies and call scripts, plus the
 * channel picker that limits a quick reply to WhatsApp, Email… or leaves it
 * shared across every channel.
 */

import { useRef, useState, type ReactNode } from 'react'
import { AlertCircle, CheckCircle, Loader2, Pencil, Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Modal } from '@/components/ui/Modal'
import { DataTable, type Column } from '@/components/ui/DataTable'
import { EntityPicker } from '@/components/ui/EntityPicker'
import { ChannelLogo } from '@/components/ui/ChannelLogo'
import { TemplateVariablePicker } from '@/components/settings/TemplateVariablePicker'
import { authHeaders, expireSessionOn401 } from '@/lib/auth'

export const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

/** Error that keeps the HTTP status so a 409 can land on the right field. */
export class ApiError extends Error {
  status: number
  constructor(status: number, message: string) {
    super(message)
    this.status = status
  }
}

export async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}/${path}`, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
  })
  const data = await res.json().catch(() => ({}))
  if (expireSessionOn401(res)) throw new ApiError(401, 'Session expired — redirecting to login…')
  if (!res.ok) throw new ApiError(res.status, (data as { detail?: string }).detail || `HTTP ${res.status}`)
  return data as T
}

export const errText = (e: unknown) => (e instanceof Error ? e.message : String(e))

export const inputCls =
  'w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[13px] text-text-primary outline-none transition-colors placeholder:text-text-faint focus:border-text-primary disabled:opacity-60'

// ── channels a quick reply can be limited to ─────────────────────────────────

/** Thread channel ids (what the inbox composer filters by) → label + logo. */
export const TEMPLATE_CHANNELS: { id: string; label: string; logo: string }[] = [
  { id: 'whatsapp', label: 'WhatsApp', logo: 'whatsapp' },
  { id: 'telegram', label: 'Telegram', logo: 'telegram' },
  { id: 'email', label: 'Email', logo: 'email' },
  { id: 'facebook', label: 'Messenger', logo: 'messenger' },
  { id: 'instagram', label: 'Instagram', logo: 'instagram' },
  { id: 'website', label: 'Website', logo: 'website' },
  { id: 'sms', label: 'SMS', logo: 'sms' },
  { id: 'line', label: 'LINE', logo: 'line' },
  { id: 'viber', label: 'Viber', logo: 'viber' },
  { id: 'wechat', label: 'WeChat', logo: 'wechat' },
  { id: 'zalo', label: 'Zalo', logo: 'zalo' },
  { id: 'tiktok', label: 'TikTok', logo: 'tiktok' },
  { id: 'douyin', label: 'Douyin', logo: 'douyin' },
]

const channelMeta = (id: string) => TEMPLATE_CHANNELS.find((c) => c.id === id)

/** "All channels" or a subset — chip toggles. `[]` means every channel. */
export function ChannelsPicker({
  value,
  onChange,
  disabled,
}: {
  value: string[]
  onChange: (next: string[]) => void
  disabled?: boolean
}) {
  const all = value.length === 0
  const chip = (on: boolean) =>
    `inline-flex h-8 cursor-pointer items-center gap-1.5 rounded-full border px-3 text-[12.5px] font-semibold transition-colors disabled:cursor-not-allowed disabled:opacity-50 ${
      on
        ? 'border-transparent bg-primary text-on-primary'
        : 'border-line-strong bg-card text-text-mid hover:bg-fill-hover hover:text-text-high'
    }`
  const toggle = (id: string) => {
    const set = new Set(value)
    if (set.has(id)) set.delete(id)
    else set.add(id)
    const next = TEMPLATE_CHANNELS.map((c) => c.id).filter((c) => set.has(c))
    // every channel ticked is the same as "all" — and keeps new channels in
    onChange(next.length === TEMPLATE_CHANNELS.length ? [] : next)
  }
  return (
    <div className="flex flex-wrap gap-1.5" role="group" aria-label="Channels">
      <button type="button" className={chip(all)} aria-pressed={all} disabled={disabled} onClick={() => onChange([])}>
        All channels
      </button>
      {TEMPLATE_CHANNELS.map((c) => {
        const on = !all && value.includes(c.id)
        return (
          <button
            key={c.id}
            type="button"
            className={chip(on)}
            aria-pressed={on}
            disabled={disabled}
            onClick={() => toggle(c.id)}
          >
            <ChannelLogo channel={c.logo} size={13} variant={on ? 'mono' : 'mark'} title="" />
            {c.label}
          </button>
        )
      })}
    </div>
  )
}

/** Compact display of a channel list: logos, or "All channels". */
export function ChannelMarks({ channels, voice }: { channels: string[]; voice?: boolean }) {
  if (voice)
    return (
      <span className="inline-flex items-center gap-1.5 text-[12px] text-text-mid">
        <ChannelLogo channel="voice" size={13} variant="mono" title="" /> Voice calls
      </span>
    )
  if (channels.length === 0) return <span className="text-[12px] text-text-muted">All channels</span>
  return (
    <span className="inline-flex flex-wrap items-center gap-1.5" title={channels.map((c) => channelMeta(c)?.label ?? c).join(', ')}>
      {channels.map((c) => (
        <ChannelLogo key={c} channel={channelMeta(c)?.logo ?? c} size={15} title={channelMeta(c)?.label ?? c} />
      ))}
      {channels.length === 1 && <span className="text-[12px] text-text-mid">{channelMeta(channels[0])?.label} only</span>}
    </span>
  )
}

// ── the shared list ──────────────────────────────────────────────────────────

export interface TemplateRow {
  key: string | number
  /** Short code ("/greet") or script/template name. */
  name: string
  mono?: boolean
  /** One-line content preview under the name. */
  preview: string
  /** Extra chips beside the name (e.g. a Meta approval status). */
  badges?: ReactNode
  type: string
  channels: string[]
  voice?: boolean
  /** null = not applicable for this kind of template. */
  entityName: string | null
  updated: string | null
  onEdit?: () => void
  onDelete?: () => void
}

function updatedLabel(iso: string | null): string {
  if (!iso) return '—'
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return '—'
  const sameYear = d.getFullYear() === new Date().getFullYear()
  return d.toLocaleDateString(undefined, { day: 'numeric', month: 'short', ...(sameYear ? {} : { year: 'numeric' }) })
}

export function TemplateTable({
  rows,
  empty,
  searchPlaceholder,
  toolbarRight,
}: {
  rows: TemplateRow[]
  empty: ReactNode
  searchPlaceholder: string
  toolbarRight?: ReactNode
}) {
  const hasActions = rows.some((r) => r.onEdit || r.onDelete)
  const columns: Column<TemplateRow>[] = [
    {
      key: 'name',
      header: 'Name',
      sortable: true,
      value: (r) => `${r.name} ${r.preview}`,
      render: (r) => (
        <div className="min-w-[200px] max-w-[460px]">
          <div className="flex flex-wrap items-center gap-1.5">
            <span className={`text-[13px] font-bold text-text-heading ${r.mono ? 'font-mono' : ''}`}>{r.name}</span>
            {r.badges}
          </div>
          <div className="mt-0.5 line-clamp-2 text-[12px] leading-snug text-text-muted" title={r.preview}>
            {r.preview || '—'}
          </div>
        </div>
      ),
    },
    { key: 'type', header: 'Type', sortable: true, width: 130, value: (r) => r.type, render: (r) => <span className="whitespace-nowrap text-[12.5px] text-text-mid">{r.type}</span> },
    {
      key: 'channels',
      header: 'Channels',
      sortable: true,
      width: 170,
      value: (r) => (r.voice ? 'voice' : r.channels.length ? r.channels.join(' ') : 'all'),
      render: (r) => <ChannelMarks channels={r.channels} voice={r.voice} />,
    },
    {
      key: 'entity',
      header: 'Entity',
      sortable: true,
      width: 170,
      value: (r) => r.entityName ?? '',
      render: (r) => (
        <span className="whitespace-nowrap text-[12.5px] text-text-mid">
          {r.entityName === null ? '—' : r.entityName || 'Organization-wide'}
        </span>
      ),
    },
    {
      key: 'updated',
      header: 'Updated',
      sortable: true,
      width: 100,
      value: (r) => r.updated ?? '',
      render: (r) => <span className="whitespace-nowrap text-[12.5px] text-text-muted">{updatedLabel(r.updated)}</span>,
    },
    ...(hasActions
      ? [
          {
            key: 'actions',
            header: 'Actions',
            srHeader: true,
            align: 'right' as const,
            width: 84,
            render: (r: TemplateRow) => (
              <div className="flex items-center justify-end gap-1">
                {r.onEdit && (
                  <button
                    type="button"
                    onClick={r.onEdit}
                    title={`Edit ${r.name}`}
                    aria-label={`Edit ${r.name}`}
                    className="grid h-8 w-8 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
                  >
                    <Pencil size={14} aria-hidden />
                  </button>
                )}
                {r.onDelete && (
                  <button
                    type="button"
                    onClick={r.onDelete}
                    title={`Delete ${r.name}`}
                    aria-label={`Delete ${r.name}`}
                    className="grid h-8 w-8 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-red/10 hover:text-red-300"
                  >
                    <Trash2 size={14} aria-hidden />
                  </button>
                )}
              </div>
            ),
          },
        ]
      : []),
  ]
  return (
    <DataTable
      columns={columns}
      rows={rows}
      rowKey={(r) => r.key}
      searchKeys={['name', 'type', 'entity']}
      searchPlaceholder={searchPlaceholder}
      empty={empty}
      initialSort={{ key: 'name', dir: 'asc' }}
      toolbarRight={toolbarRight}
    />
  )
}

// ── banners ──────────────────────────────────────────────────────────────────

export function Notice({ tone, children }: { tone: 'ok' | 'error'; children: ReactNode }) {
  return (
    <div
      role={tone === 'error' ? 'alert' : 'status'}
      className={`flex items-start gap-2 rounded-lg border px-3 py-2 text-[12.5px] font-semibold ${
        tone === 'error' ? 'border-red/35 bg-red/10' : 'border-green/35 bg-green/10'
      }`}
      style={{ color: tone === 'error' ? 'var(--red-ink)' : 'var(--green-ink)' }}
    >
      {tone === 'error' ? (
        <AlertCircle size={14} className="mt-0.5 shrink-0" aria-hidden />
      ) : (
        <CheckCircle size={14} className="mt-0.5 shrink-0" aria-hidden />
      )}
      <span>{children}</span>
    </div>
  )
}

// ── the shared editor ────────────────────────────────────────────────────────

export interface TemplateDraft {
  name: string
  content: string
  entity: string | null
  channels: string[]
}

const CODE_RE = /^[a-z0-9][a-z0-9_-]*$/

/**
 * One editor for both kinds: a message quick reply (short code + channels) or
 * a call script (name). Variables insert at the cursor. Errors render INSIDE
 * the modal (a page-level message would sit behind the scrim); a 409 lands on
 * the name field.
 */
export function TemplateEditorModal({
  kind,
  initial,
  editing,
  onSave,
  onClose,
}: {
  kind: 'message' | 'script'
  initial: TemplateDraft
  editing: boolean
  /** Throws on failure — an ApiError 409 is shown on the name field. */
  onSave: (draft: TemplateDraft) => Promise<void>
  onClose: () => void
}) {
  const [draft, setDraft] = useState<TemplateDraft>(initial)
  const [nameError, setNameError] = useState<string | null>(null)
  const [contentError, setContentError] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const textRef = useRef<HTMLTextAreaElement>(null)
  const caret = useRef<{ start: number; end: number } | null>(null)

  const isMsg = kind === 'message'
  const normName = (v: string) => (isMsg ? v.trim().toLowerCase().replace(/^\/+/, '') : v.trim())

  const checkName = (v: string): string | null => {
    const n = normName(v)
    if (!n) return isMsg ? 'Give it a short code — what agents type after “/”.' : 'Give the script a name.'
    if (isMsg && !CODE_RE.test(n)) return 'Lowercase letters, digits, dashes or underscores — no spaces.'
    return null
  }

  /** Insert {{token}} where the cursor was (or at the end). */
  const insert = (token: string) => {
    const text = draft.content
    const sel = caret.current ?? { start: text.length, end: text.length }
    const before = text.slice(0, sel.start)
    const after = text.slice(sel.end)
    const pad = before && !/\s$/.test(before) ? ' ' : ''
    const piece = `${pad}{{${token}}}`
    const next = before + piece + after
    setDraft({ ...draft, content: next })
    const pos = before.length + piece.length
    caret.current = { start: pos, end: pos }
    requestAnimationFrame(() => {
      textRef.current?.focus()
      textRef.current?.setSelectionRange(pos, pos)
    })
  }

  const submit = async () => {
    const ne = checkName(draft.name)
    const ce = draft.content.trim() ? null : isMsg ? 'Write the reply.' : 'Write what the call should say.'
    setNameError(ne)
    setContentError(ce)
    setError(null)
    if (ne || ce) return
    setBusy(true)
    try {
      await onSave({ ...draft, name: normName(draft.name), content: draft.content.trim() })
    } catch (e) {
      if (e instanceof ApiError && e.status === 409)
        setNameError(
          isMsg
            ? `/${normName(draft.name)} already exists for this entity — pick another short code.`
            : `“${normName(draft.name)}” already exists — pick another name.`,
        )
      else setError(errText(e))
    } finally {
      setBusy(false)
    }
  }

  const title = isMsg
    ? editing
      ? `Edit /${initial.name}`
      : 'New quick reply'
    : editing
      ? `Edit “${initial.name}”`
      : 'New call script'

  return (
    <Modal
      open
      onClose={() => !busy && onClose()}
      title={title}
      subtitle={
        isMsg
          ? 'Inserted from the composer with “/short-code”. Variables fill from the contact’s record.'
          : 'Read verbatim on a one-way bot call — dial, read, hang up. No conversation.'
      }
      size="lg"
      footer={
        <>
          <Button variant="secondary" onClick={onClose} disabled={busy}>
            Cancel
          </Button>
          <Button onClick={submit} disabled={busy}>
            {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <CheckCircle size={13} aria-hidden />}
            {editing ? 'Save changes' : isMsg ? 'Save quick reply' : 'Save script'}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-5">
        {error && <Notice tone="error">{error}</Notice>}

        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
          <label className="block">
            <span className="mb-1 block text-[12px] font-semibold text-text-high">{isMsg ? 'Short code' : 'Name'}</span>
            <div className="relative">
              {isMsg && (
                <span className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 font-mono text-[13px] text-text-muted">
                  /
                </span>
              )}
              <input
                value={draft.name}
                onChange={(e) => {
                  setDraft({ ...draft, name: e.target.value })
                  if (nameError) setNameError(checkName(e.target.value))
                }}
                onBlur={() => draft.name.trim() && setNameError(checkName(draft.name))}
                placeholder={isMsg ? 'delivery-update' : 'Order status reminder'}
                aria-invalid={!!nameError}
                className={`${inputCls} ${isMsg ? 'pl-6 font-mono' : ''} ${nameError ? '!border-[color:var(--red-ink)]' : ''}`}
              />
            </div>
            {nameError ? (
              <span className="mt-1 block text-[11.5px] font-semibold" style={{ color: 'var(--red-ink)' }}>
                {nameError}
              </span>
            ) : (
              <span className="mt-1 block text-[11.5px] text-text-muted">
                {isMsg ? 'Lowercase letters, digits and dashes.' : 'Picked from the script-call button and flow steps.'}
              </span>
            )}
          </label>
          <label className="block">
            <span className="mb-1 block text-[12px] font-semibold text-text-high">Entity</span>
            <EntityPicker
              value={draft.entity}
              onChange={(entity) => setDraft({ ...draft, entity })}
              globalLabel="Organization-wide (every entity)"
              ariaLabel="Entity this template belongs to"
            />
            <span className="mt-1 block text-[11.5px] text-text-muted">
              Organization-wide templates are available to every entity.
            </span>
          </label>
        </div>

        {isMsg && (
          <div>
            <span className="mb-1.5 block text-[12px] font-semibold text-text-high">Channels</span>
            <ChannelsPicker value={draft.channels} onChange={(channels) => setDraft({ ...draft, channels })} />
            <span className="mt-1.5 block text-[11.5px] text-text-muted">
              {draft.channels.length === 0
                ? 'Offered in every channel’s composer, including channels added later.'
                : `Only offered when replying on ${draft.channels
                    .map((c) => channelMeta(c)?.label ?? c)
                    .join(', ')
                    .replace(/, ([^,]*)$/, ' or $1')}.`}
            </span>
          </div>
        )}

        <label className="block">
          <span className="mb-1 flex items-baseline justify-between gap-2">
            <span className="text-[12px] font-semibold text-text-high">{isMsg ? 'Message' : 'Script'}</span>
            <span className="text-[11px] text-text-faint">{draft.content.length} characters</span>
          </span>
          <textarea
            ref={textRef}
            value={draft.content}
            onChange={(e) => {
              setDraft({ ...draft, content: e.target.value })
              caret.current = { start: e.target.selectionStart, end: e.target.selectionEnd }
              if (contentError && e.target.value.trim()) setContentError(null)
            }}
            onSelect={(e) =>
              (caret.current = { start: e.currentTarget.selectionStart, end: e.currentTarget.selectionEnd })
            }
            rows={isMsg ? 5 : 7}
            placeholder={
              isMsg
                ? 'Hi {{first_name}}, here is the latest on {{record_title}}: {{record_status}}.'
                : 'Hello {{name}}, this is an automated call about your {{record_title}}…'
            }
            aria-invalid={!!contentError}
            className={`${inputCls} resize-y leading-[1.6] ${contentError ? '!border-[color:var(--red-ink)]' : ''}`}
          />
          {contentError && (
            <span className="mt-1 block text-[11.5px] font-semibold" style={{ color: 'var(--red-ink)' }}>
              {contentError}
            </span>
          )}
        </label>
        <TemplateVariablePicker onInsert={insert} />
      </div>
    </Modal>
  )
}

/** Small confirm dialog for deletes (errors inside it). */
export function ConfirmDelete({
  title,
  body,
  onConfirm,
  onClose,
}: {
  title: string
  body: ReactNode
  onConfirm: () => Promise<void>
  onClose: () => void
}) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  return (
    <Modal
      open
      onClose={() => !busy && onClose()}
      title={title}
      size="sm"
      footer={
        <>
          <Button variant="secondary" onClick={onClose} disabled={busy}>
            Cancel
          </Button>
          <Button
            variant="danger"
            disabled={busy}
            onClick={async () => {
              setBusy(true)
              setError(null)
              try {
                await onConfirm()
              } catch (e) {
                setError(errText(e))
                setBusy(false)
              }
            }}
          >
            {busy && <Loader2 size={13} className="animate-spin" aria-hidden />} Delete
          </Button>
        </>
      }
    >
      {error && (
        <div className="mb-3">
          <Notice tone="error">{error}</Notice>
        </div>
      )}
      <div className="text-[13px] leading-relaxed text-text-mid">{body}</div>
    </Modal>
  )
}
