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
import { Advanced } from '@/components/ui/Advanced'
import { ChannelLogo } from '@/components/ui/ChannelLogo'
import { TemplateVariablePicker } from '@/components/settings/TemplateVariablePicker'
import { useFormat, useT, type MessageKey, type TFunction } from '@/i18n'
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

/** Marker message for an expired session — shown translated by `errText`. */
const SESSION_EXPIRED = 'session-expired'

export async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}/${path}`, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
  })
  const data = await res.json().catch(() => ({}))
  if (expireSessionOn401(res)) throw new ApiError(401, SESSION_EXPIRED)
  if (!res.ok) throw new ApiError(res.status, (data as { detail?: string }).detail || `HTTP ${res.status}`)
  return data as T
}

/** An error for display: our own messages translated, a server `detail` as sent. */
export const errText = (e: unknown, t: TFunction) => {
  if (e instanceof ApiError && e.message === SESSION_EXPIRED) return t('common.errors.sessionExpired')
  if (e instanceof TypeError) return t('common.errors.network') // fetch could not reach the API
  return e instanceof Error ? e.message : String(e)
}

export const inputCls =
  'w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[13px] text-text-primary outline-none transition-colors placeholder:text-text-faint focus:border-text-primary disabled:opacity-60'

// ── channels a quick reply can be limited to ─────────────────────────────────

/** Thread channel ids (what the inbox composer filters by) → label + logo.
 * Brand names show as is; the two generic channels carry a translation key. */
export const TEMPLATE_CHANNELS: { id: string; label: string; lbl?: MessageKey; logo: string }[] = [
  { id: 'whatsapp', label: 'WhatsApp', logo: 'whatsapp' },
  { id: 'telegram', label: 'Telegram', logo: 'telegram' },
  { id: 'email', label: 'Email', lbl: 'common.fields.email', logo: 'email' },
  { id: 'facebook', label: 'Messenger', logo: 'messenger' },
  { id: 'instagram', label: 'Instagram', logo: 'instagram' },
  { id: 'website', label: 'Website', lbl: 'library.channels.website', logo: 'website' },
  { id: 'sms', label: 'SMS', logo: 'sms' },
  { id: 'line', label: 'LINE', logo: 'line' },
  { id: 'viber', label: 'Viber', logo: 'viber' },
  { id: 'wechat', label: 'WeChat', logo: 'wechat' },
  { id: 'zalo', label: 'Zalo', logo: 'zalo' },
  { id: 'tiktok', label: 'TikTok', logo: 'tiktok' },
  { id: 'douyin', label: 'Douyin', logo: 'douyin' },
]

const channelMeta = (id: string) => TEMPLATE_CHANNELS.find((c) => c.id === id)

/** A channel id's name in the current language ("Website" → "Laman web"). */
export function channelName(t: TFunction, id: string): string {
  const m = channelMeta(id)
  if (!m) return id
  return m.lbl ? t(m.lbl) : m.label
}

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
  const t = useT()
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
    <div className="flex flex-wrap gap-1.5" role="group" aria-label={t('library.table.channels')}>
      <button type="button" className={chip(all)} aria-pressed={all} disabled={disabled} onClick={() => onChange([])}>
        {t('library.channels.all')}
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
            {channelName(t, c.id)}
          </button>
        )
      })}
    </div>
  )
}

/** Compact display of a channel list: logos, or "All channels". */
export function ChannelMarks({ channels, voice }: { channels: string[]; voice?: boolean }) {
  const t = useT()
  const f = useFormat()
  if (voice)
    return (
      <span className="inline-flex items-center gap-1.5 text-[12px] text-text-mid">
        <ChannelLogo channel="voice" size={13} variant="mono" title="" /> {t('library.channels.voiceCalls')}
      </span>
    )
  if (channels.length === 0) return <span className="text-[12px] text-text-muted">{t('library.channels.all')}</span>
  return (
    <span className="inline-flex flex-wrap items-center gap-1.5" title={f.list(channels.map((c) => channelName(t, c)))}>
      {channels.map((c) => (
        <ChannelLogo key={c} channel={channelMeta(c)?.logo ?? c} size={15} title={channelName(t, c)} />
      ))}
      {channels.length === 1 && (
        <span className="text-[12px] text-text-mid">{t('library.channels.only', { channel: channelName(t, channels[0]) })}</span>
      )}
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
  const t = useT()
  const f = useFormat()
  const updatedLabel = (iso: string | null) => f.date(iso) || '—'
  const hasActions = rows.some((r) => r.onEdit || r.onDelete)
  const columns: Column<TemplateRow>[] = [
    {
      key: 'name',
      header: t('common.fields.name'),
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
    { key: 'type', header: t('common.fields.type'), sortable: true, width: 130, value: (r) => r.type, render: (r) => <span className="whitespace-nowrap text-[12.5px] text-text-mid">{r.type}</span> },
    {
      key: 'channels',
      header: t('library.table.channels'),
      sortable: true,
      width: 170,
      value: (r) => (r.voice ? 'voice' : r.channels.length ? r.channels.join(' ') : 'all'),
      render: (r) => <ChannelMarks channels={r.channels} voice={r.voice} />,
    },
    {
      key: 'entity',
      header: t('common.fields.entity'),
      sortable: true,
      width: 170,
      value: (r) => r.entityName ?? '',
      render: (r) => (
        <span className="whitespace-nowrap text-[12.5px] text-text-mid">
          {r.entityName === null ? '—' : r.entityName || t('library.table.orgWide')}
        </span>
      ),
    },
    {
      key: 'updated',
      header: t('common.fields.updated'),
      sortable: true,
      width: 100,
      value: (r) => r.updated ?? '',
      render: (r) => <span className="whitespace-nowrap text-[12.5px] text-text-muted">{updatedLabel(r.updated)}</span>,
    },
    ...(hasActions
      ? [
          {
            key: 'actions',
            header: t('library.table.actions'),
            srHeader: true,
            align: 'right' as const,
            width: 84,
            render: (r: TemplateRow) => (
              <div className="flex items-center justify-end gap-1">
                {r.onEdit && (
                  <button
                    type="button"
                    onClick={r.onEdit}
                    title={t('library.table.editName', { name: r.name })}
                    aria-label={t('library.table.editName', { name: r.name })}
                    className="grid h-8 w-8 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
                  >
                    <Pencil size={14} aria-hidden />
                  </button>
                )}
                {r.onDelete && (
                  <button
                    type="button"
                    onClick={r.onDelete}
                    title={t('library.table.deleteName', { name: r.name })}
                    aria-label={t('library.table.deleteName', { name: r.name })}
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
  const t = useT()
  const f = useFormat()
  const [draft, setDraft] = useState<TemplateDraft>(initial)
  const [nameError, setNameError] = useState<string | null>(null)
  const [contentError, setContentError] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const textRef = useRef<HTMLTextAreaElement>(null)
  const caret = useRef<{ start: number; end: number } | null>(null)

  const isMsg = kind === 'message'
  // Entity (default organization-wide) and channels (default every channel)
  // sit under Advanced — opened when either is set.
  const advancedCount = (draft.entity ? 1 : 0) + (isMsg && draft.channels.length > 0 ? 1 : 0)
  const normName = (v: string) => (isMsg ? v.trim().toLowerCase().replace(/^\/+/, '') : v.trim())

  const checkName = (v: string): string | null => {
    const n = normName(v)
    if (!n) return isMsg ? t('library.editor.needShortCode') : t('library.editor.needScriptName')
    if (isMsg && !CODE_RE.test(n)) return t('library.editor.badShortCode')
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
    const ce = draft.content.trim() ? null : isMsg ? t('library.editor.writeReply') : t('library.editor.writeScript')
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
            ? t('library.editor.shortCodeTaken', { code: normName(draft.name) })
            : t('library.editor.scriptNameTaken', { name: normName(draft.name) }),
        )
      else setError(errText(e, t))
    } finally {
      setBusy(false)
    }
  }

  const title = isMsg
    ? editing
      ? t('library.editor.editShortCode', { code: initial.name })
      : t('library.editor.newQuickReply')
    : editing
      ? t('library.editor.editScript', { name: initial.name })
      : t('library.editor.newScript')

  return (
    <Modal
      open
      onClose={() => !busy && onClose()}
      title={title}
      subtitle={isMsg ? t('library.editor.quickReplySubtitle') : t('library.editor.scriptSubtitle')}
      size="lg"
      footer={
        <>
          <Button variant="secondary" onClick={onClose} disabled={busy}>
            {t('common.actions.cancel')}
          </Button>
          <Button onClick={submit} disabled={busy}>
            {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <CheckCircle size={13} aria-hidden />}
            {editing ? t('library.editor.saveChanges') : isMsg ? t('library.editor.saveQuickReply') : t('library.editor.saveScript')}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-5">
        {error && <Notice tone="error">{error}</Notice>}

        <div>
          <label className="block">
            <span className="mb-1 block text-[12px] font-semibold text-text-high">
              {isMsg ? t('library.editor.shortCode') : t('common.fields.name')}
            </span>
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
                placeholder={isMsg ? 'delivery-update' : t('library.editor.scriptNamePlaceholder')}
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
                {isMsg ? t('library.editor.shortCodeHint') : t('library.editor.scriptNameHint')}
              </span>
            )}
          </label>
        </div>

        <label className="block">
          <span className="mb-1 flex items-baseline justify-between gap-2">
            <span className="text-[12px] font-semibold text-text-high">
              {isMsg ? t('library.editor.message') : t('library.editor.script')}
            </span>
            <span className="text-[11px] text-text-faint">{t('library.editor.characters', { count: draft.content.length })}</span>
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
            placeholder={isMsg ? t('library.editor.messagePlaceholder') : t('library.editor.scriptPlaceholder')}
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

        <Advanced count={advancedCount} forceOpen={advancedCount > 0}>
          <label className="block">
            <span className="mb-1 block text-[12px] font-semibold text-text-high">{t('common.fields.entity')}</span>
            <EntityPicker
              value={draft.entity}
              onChange={(entity) => setDraft({ ...draft, entity })}
              globalLabel={t('library.editor.orgWideEvery')}
              ariaLabel={t('library.editor.entityAria')}
            />
            <span className="mt-1 block text-[11.5px] text-text-muted">{t('library.editor.orgWideHint')}</span>
          </label>
          {isMsg && (
            <div>
              <span className="mb-1.5 block text-[12px] font-semibold text-text-high">{t('library.table.channels')}</span>
              <ChannelsPicker value={draft.channels} onChange={(channels) => setDraft({ ...draft, channels })} />
              <span className="mt-1.5 block text-[11.5px] text-text-muted">
                {draft.channels.length === 0
                  ? t('library.editor.everyChannelHint')
                  : t('library.editor.onlyChannelsHint', { channels: f.list(draft.channels.map((c) => channelName(t, c))) })}
              </span>
            </div>
          )}
        </Advanced>
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
  const t = useT()
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
            {t('common.actions.cancel')}
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
                setError(errText(e, t))
                setBusy(false)
              }
            }}
          >
            {busy && <Loader2 size={13} className="animate-spin" aria-hidden />} {t('common.actions.delete')}
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
