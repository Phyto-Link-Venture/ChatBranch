'use client'

import { API } from '@/lib/apiBase'
import { useCallback, useEffect, useMemo, useState } from 'react'
import Link from 'next/link'
import {
  Bot,
  CheckCircle2,
  FileText,
  History,
  Loader2,
  Paperclip,
  Pencil,
  Plus,
  Receipt,
  ShieldCheck,
  Trash2,
  X,
  XCircle,
} from 'lucide-react'
import { Advanced } from '@/components/ui/Advanced'
import { Button } from '@/components/ui/Button'
import { Modal } from '@/components/ui/Modal'
import { MultiSelect, Select } from '@/components/ui/Select'
import { TagListInput } from '@/components/ui/TagListInput'
import { Toggle } from '@/components/ui/Toggle'
import { channelLabelFor } from '@/components/ui/ChannelLogo'
import {
  AvailabilityChips,
  AvailabilityPicker,
  availabilityOf,
  useDefaultAvailability,
  type EntityRefLite,
} from '@/components/ui/AvailabilityPicker'
import { useFormat, useT, type MessageKey } from '@/i18n'
import { authHeaders, hasCap } from '@/lib/auth'
import { useEntityQuery } from '@/lib/EntityContext'

/**
 * Background AI tasks — silent AI workers (server: Ai/Tasks/AiTaskService.cs).
 * A task reads customers' inbound messages that match its conditions and acts
 * through the tools on its allowlist; it never writes to the customer. The
 * page: the list (trigger summary, "Available to", last runs, on/off), the
 * editor (instructions + tools up front, conditions under Advanced) with a
 * dry-run Test box, and a Runs panel per task.
 */

interface RunLite {
  id: number
  status: string
  summary: string
  finished_at: string | null
}

interface AiTask {
  id: number
  name: string
  enabled: boolean
  channels: string[]
  attachment: string
  keywords: string[]
  linked: string
  instructions: string
  tools: string[]
  config: Record<string, string>
  entities?: EntityRefLite[]
  global?: boolean
  run_count: number
  last_run_at: string | null
  recent: RunLite[]
}

interface BuiltinTool {
  id: string
  group: string
  label: string
  description: string
  provider: string
  connected: boolean
}

interface McpTool {
  id: string
  server: string
  name: string
  description: string
  read_only: boolean
}

interface Catalog {
  builtins: BuiltinTool[]
  mcp: McpTool[]
  mcp_servers: number
  mcp_servers_ready: number
}

interface Step {
  tool: string
  args: string
  result: string
  ok: boolean
  preview?: boolean
}

interface Run {
  id: number
  status: string
  channel: string
  key: string
  chat_url: string
  message: string
  media_type: string
  summary: string
  error: string
  extracted: Record<string, unknown>
  steps: Step[]
  model: string
  created_at: string
  finished_at: string | null
}

interface TestResult {
  ok: boolean
  error: string
  relevant: boolean
  summary: string
  extracted: Record<string, unknown>
  steps: Step[]
  notes: string[]
  model: string
  prompt_tokens: number
  completion_tokens: number
  attachment_conditions_met: boolean
  keywords_match: boolean
}

async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}/${path}`, {
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
    ...init,
  })
  const data = await res.json().catch(() => ({}))
  if (!res.ok) throw new Error((data as { detail?: string }).detail || `HTTP ${res.status}`)
  return data as T
}

const inputCls =
  'w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[12.5px] text-text-primary outline-none transition-colors focus:border-brand-400'

/** The conversation channels a task can read (ids; labels come from channelLabelFor). */
const CHANNELS = ['whatsapp', 'telegram', 'email', 'facebook', 'instagram', 'website', 'line', 'viber', 'sms', 'wechat', 'zalo', 'tiktok', 'douyin']

const GROUP_ORDER = ['chatbranch', 'apps', 'webhook'] as const
const GROUP_LABEL: Record<string, MessageKey> = {
  chatbranch: 'aiTasks.editor.groups.chatbranch',
  apps: 'aiTasks.editor.groups.apps',
  webhook: 'aiTasks.editor.groups.webhook',
}
const TOOL_KEYS = [
  'add_private_note',
  'add_conversation_label',
  'set_contact_field',
  'hand_over_to_person',
  'sheets_append_row',
  'drive_upload_attachment',
  'calendar_create_event',
  'webhook_post',
] as const
type ToolKey = (typeof TOOL_KEYS)[number]
const isToolKey = (id: string): id is ToolKey => (TOOL_KEYS as readonly string[]).includes(id)

const STATUS_KEY: Record<string, MessageKey> = {
  ok: 'aiTasks.status.ok',
  failed: 'aiTasks.status.failed',
  skipped: 'aiTasks.status.skipped',
  pending: 'aiTasks.status.pending',
  running: 'aiTasks.status.running',
}
const STATUS_CLS: Record<string, string> = {
  ok: 'bg-green-400/15 text-green-300',
  failed: 'bg-red-400/15 text-red-300',
  skipped: 'bg-fill text-text-muted',
  pending: 'bg-amber-400/15 text-amber-300',
  running: 'bg-blue-400/15 text-blue-300',
}

interface Draft {
  name: string
  instructions: string
  enabled: boolean
  channels: string[]
  attachment: string
  keywords: string[]
  linked: string
  tools: string[]
  config: Record<string, string>
  entities: string[]
}

const EMPTY: Draft = {
  name: '',
  instructions: '',
  enabled: true,
  channels: [],
  attachment: '',
  keywords: [],
  linked: '',
  tools: ['add_private_note'],
  config: {},
  entities: [],
}

export function AiTasks() {
  const t = useT()
  const f = useFormat()
  const entityQ = useEntityQuery()
  const defaultAvailability = useDefaultAvailability()
  const [tasks, setTasks] = useState<AiTask[] | null>(null)
  const [catalog, setCatalog] = useState<Catalog | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)
  const [canManage, setCanManage] = useState(false)
  useEffect(() => setCanManage(hasCap('settings.ai.manage')), [])

  const [editing, setEditing] = useState<AiTask | null>(null)
  const [open, setOpen] = useState(false)
  const [draft, setDraft] = useState<Draft>(EMPTY)
  const [busy, setBusy] = useState(false)
  const [formError, setFormError] = useState<string | null>(null)
  const [runsOf, setRunsOf] = useState<AiTask | null>(null)
  const [templatesOpen, setTemplatesOpen] = useState(false)

  const load = useCallback(async () => {
    try {
      const d = await api<{ tasks: AiTask[]; catalog: Catalog; can_manage: boolean }>(`ai/tasks${entityQ && `?${entityQ}`}`)
      setTasks(d.tasks)
      setCatalog(d.catalog)
      setLoadError(null)
    } catch (e) {
      setTasks(null)
      setLoadError(e instanceof Error ? e.message : String(e))
    }
  }, [entityQ])

  useEffect(() => {
    load()
  }, [load])

  const toolLabel = useCallback(
    (id: string) => {
      if (isToolKey(id)) return t(`aiTasks.tools.${id}.label` as MessageKey)
      const m = catalog?.mcp.find((x) => x.id === id)
      return m ? `${m.server} · ${m.name}` : id.replace(/^mcp:/, '')
    },
    [catalog, t],
  )

  const triggerSummary = (task: AiTask) => {
    const parts = [t('aiTasks.trigger.every')]
    if (task.attachment === 'pdf') parts.push(t('aiTasks.trigger.withPdf'))
    else if (task.attachment === 'image') parts.push(t('aiTasks.trigger.withImage'))
    else if (task.attachment === 'any') parts.push(t('aiTasks.trigger.withFile'))
    if (task.keywords.length > 0) parts.push(t('aiTasks.trigger.keywords', { words: task.keywords.map((k) => `“${k}”`).join(', ') }))
    if (task.linked === 'yes') parts.push(t('aiTasks.trigger.linked'))
    if (task.linked === 'no') parts.push(t('aiTasks.trigger.unlinked'))
    parts.push(
      task.channels.length > 0
        ? t('aiTasks.trigger.channels', { channels: task.channels.map((c) => channelLabelFor(c, t)).join(', ') })
        : t('aiTasks.trigger.allChannels'),
    )
    return parts.join(' ')
  }

  const openWith = (task: AiTask | null, d: Draft) => {
    setEditing(task)
    setDraft(d)
    setFormError(null)
    setMsg(null)
    setTemplatesOpen(false)
    setOpen(true)
  }

  const openEdit = (task: AiTask) =>
    openWith(task, {
      name: task.name,
      instructions: task.instructions,
      enabled: task.enabled,
      channels: task.channels,
      attachment: task.attachment,
      keywords: task.keywords,
      linked: task.linked,
      tools: task.tools,
      config: { ...task.config },
      entities: availabilityOf(task),
    })

  const fromTemplate = (which: 'invoices' | 'receipts' | 'blank') => {
    if (which === 'blank') return openWith(null, { ...EMPTY, entities: defaultAvailability })
    const base = `aiTasks.templates.${which}` as const
    openWith(null, {
      ...EMPTY,
      entities: defaultAvailability,
      name: t(`${base}.name`),
      instructions: t(`${base}.instructions`),
      attachment: 'any',
      tools:
        which === 'invoices'
          ? ['sheets_append_row', 'drive_upload_attachment', 'add_private_note']
          : ['sheets_append_row', 'add_conversation_label', 'add_private_note', 'hand_over_to_person'],
      config: {
        sheet_range: which === 'invoices' ? 'Invoices!A:Z' : 'Payments!A:Z',
        sheet_columns: t(`${base}.columns`),
      },
    })
  }

  const problem = (d: Draft): string | null => {
    if (d.tools.includes('sheets_append_row') && !(d.config.spreadsheet_id ?? '').trim())
      return t('aiTasks.editor.needsSpreadsheet')
    if (
      d.tools.includes('webhook_post') &&
      (!(d.config.webhook_url ?? '').startsWith('https://') || (d.config.webhook_secret ?? '').length < 16)
    )
      return t('aiTasks.editor.needsWebhook')
    return null
  }

  const body = (d: Draft) => ({
    name: d.name.trim(),
    instructions: d.instructions.trim(),
    enabled: d.enabled,
    channels: d.channels,
    attachment: d.attachment,
    keywords: d.keywords,
    linked: d.linked,
    tools: d.tools,
    config: d.config,
    entities: d.entities,
  })

  const save = async () => {
    const p = problem(draft)
    if (p) return setFormError(p)
    setBusy(true)
    setFormError(null)
    try {
      const payload = JSON.stringify(body(draft))
      const saved = editing
        ? await api<AiTask>(`ai/tasks/${editing.id}`, { method: 'PUT', body: payload })
        : await api<AiTask>('ai/tasks', { method: 'POST', body: payload })
      setEditing(saved)
      setMsg({ ok: true, text: t('aiTasks.saved', { name: saved.name }) })
      await load()
      setOpen(false)
    } catch (e) {
      setFormError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  const toggle = async (task: AiTask) => {
    try {
      await api(`ai/tasks/${task.id}`, { method: 'PUT', body: JSON.stringify({ enabled: !task.enabled }) })
      await load()
    } catch (e) {
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    }
  }

  const remove = async (task: AiTask) => {
    if (!window.confirm(t('aiTasks.confirmDelete', { name: task.name }))) return
    try {
      await api(`ai/tasks/${task.id}`, { method: 'DELETE' })
      setMsg({ ok: true, text: t('aiTasks.deleted', { name: task.name }) })
      await load()
    } catch (e) {
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    }
  }

  if (tasks === null)
    return (
      <div className="py-6 text-[12px] text-text-muted">
        {loadError ? t('aiTasks.loadError', { error: loadError }) : <Loader2 size={14} className="animate-spin" aria-hidden />}
      </div>
    )

  return (
    <div className="flex flex-col gap-4 pt-3.5">
      <div data-tour="ai-tasks-intro" className="flex max-w-[760px] flex-col gap-2">
        <p className="text-[12px] leading-relaxed text-text-mid">{t('aiTasks.intro')}</p>
        <span className="inline-flex w-fit items-center gap-1.5 rounded-full bg-green-400/10 px-2.5 py-1 text-[11px] font-semibold text-green-300">
          <ShieldCheck size={12} aria-hidden /> {t('aiTasks.safe')}
        </span>
        {!canManage && <p className="text-[11.5px] text-text-muted">{t('aiTasks.readOnly')}</p>}
      </div>

      {msg && !open && (
        <div role={msg.ok ? 'status' : 'alert'} className={`text-[12px] font-semibold ${msg.ok ? 'text-green-300' : 'text-red-300'}`}>
          {msg.text}
        </div>
      )}

      <div className="flex flex-wrap items-center justify-between gap-3">
        <span className="text-[11px] font-bold uppercase tracking-[0.06em] text-text-faint">
          {t('aiTasks.count', { count: tasks.length })}
        </span>
        {canManage && (
          <div data-tour="ai-tasks-add" className="flex flex-wrap gap-2">
            <Button variant="secondary" onClick={() => setTemplatesOpen((v) => !v)}>
              <FileText size={13} aria-hidden /> {t('aiTasks.fromTemplate')}
            </Button>
            <Button onClick={() => fromTemplate('blank')}>
              <Plus size={13} aria-hidden /> {t('aiTasks.add')}
            </Button>
          </div>
        )}
      </div>

      {templatesOpen && canManage && (
        <div className="grid gap-2 sm:grid-cols-3">
          {(['invoices', 'receipts', 'blank'] as const).map((k) => (
            <button
              key={k}
              type="button"
              onClick={() => fromTemplate(k)}
              className="flex flex-col gap-1 rounded-xl border border-line bg-card px-4 py-3 text-left transition-colors hover:border-brand-400"
            >
              <span className="flex items-center gap-1.5 text-[12.5px] font-bold text-text-high">
                {k === 'invoices' ? <FileText size={13} aria-hidden /> : k === 'receipts' ? <Receipt size={13} aria-hidden /> : <Plus size={13} aria-hidden />}
                {t(`aiTasks.templates.${k}.name`)}
              </span>
              <span className="text-[11px] leading-snug text-text-muted">{t(`aiTasks.templates.${k}.description`)}</span>
            </button>
          ))}
        </div>
      )}

      <div data-tour="ai-tasks-list" className="flex flex-col gap-2">
        {tasks.length === 0 && (
          <div className="rounded-xl border border-dashed border-line-strong px-4 py-6 text-center text-[12px] text-text-muted">
            {t('aiTasks.empty')}
          </div>
        )}
        {tasks.map((task) => (
          <div key={task.id} className="flex items-start gap-3 rounded-xl border border-line bg-card px-4 py-3">
            <div
              className={`mt-0.5 grid h-7 w-7 shrink-0 place-items-center rounded-lg ${
                task.enabled ? 'bg-brand-400/15 text-brand-300' : 'bg-fill text-text-muted'
              }`}
            >
              <Bot size={14} aria-hidden />
            </div>
            <div className="min-w-0 flex-1">
              <div className="flex flex-wrap items-center gap-2">
                <span className="text-[13px] font-bold text-text-high">{task.name}</span>
                {!task.enabled && (
                  <span className="rounded-full bg-fill px-2 py-px text-[10px] font-bold text-text-muted">{t('aiTasks.off')}</span>
                )}
                <AvailabilityChips entities={task.entities} global={task.global ?? true} />
              </div>
              <div className="mt-0.5 text-[11.5px] text-text-muted">{triggerSummary(task)}</div>
              <div className="mt-1 flex flex-wrap gap-1.5 text-[10.5px]">
                {task.tools.length === 0 && <span className="text-text-faint">{t('aiTasks.noTools')}</span>}
                {task.tools.map((id) => (
                  <span key={id} className="rounded-full bg-fill px-2 py-px font-semibold text-text-mid">
                    {toolLabel(id)}
                  </span>
                ))}
              </div>
              <div className="mt-1.5 flex flex-wrap items-center gap-1.5 text-[10.5px] text-text-faint">
                <span>{t('aiTasks.lastRuns')}:</span>
                {task.recent.length === 0 && <span>{t('aiTasks.neverRan')}</span>}
                {task.recent.map((r) => (
                  <span
                    key={r.id}
                    title={`${r.finished_at ? f.dateTime(r.finished_at) : ''} ${r.summary}`.trim()}
                    className={`rounded-full px-1.5 py-px font-semibold ${STATUS_CLS[r.status] ?? 'bg-fill text-text-muted'}`}
                  >
                    {STATUS_KEY[r.status] ? t(STATUS_KEY[r.status]) : r.status}
                  </span>
                ))}
                {task.run_count > 0 && <span>· {t('aiTasks.ranCount', { count: task.run_count })}</span>}
              </div>
            </div>
            <div data-tour="ai-tasks-actions" className="flex shrink-0 items-center gap-1.5">
              {canManage && (
                <Toggle
                  on={task.enabled}
                  onChange={() => toggle(task)}
                  label={task.enabled ? t('aiTasks.turnOff', { name: task.name }) : t('aiTasks.turnOn', { name: task.name })}
                />
              )}
              <Button variant="ghost" size="sm" onClick={() => setRunsOf(task)} aria-label={t('aiTasks.runs')}>
                <History size={13} aria-hidden /> <span className="hidden sm:inline">{t('aiTasks.runs')}</span>
              </Button>
              {canManage && (
                <>
                  <Button variant="ghost" size="sm" onClick={() => openEdit(task)} aria-label={t('aiTasks.edit')}>
                    <Pencil size={13} aria-hidden />
                  </Button>
                  <Button variant="ghost" size="sm" onClick={() => remove(task)} aria-label={t('aiTasks.delete')}>
                    <Trash2 size={13} aria-hidden />
                  </Button>
                </>
              )}
            </div>
          </div>
        ))}
      </div>

      {open && catalog && (
        <TaskEditor
          editing={editing}
          draft={draft}
          setDraft={setDraft}
          catalog={catalog}
          busy={busy}
          error={formError}
          onClose={() => setOpen(false)}
          onSave={save}
          body={body}
        />
      )}
      {runsOf && <RunsModal task={runsOf} toolLabel={toolLabel} onClose={() => setRunsOf(null)} />}
    </div>
  )
}

// ---- editor ----------------------------------------------------------------------------

function TaskEditor({
  editing,
  draft,
  setDraft,
  catalog,
  busy,
  error,
  onClose,
  onSave,
  body,
}: {
  editing: AiTask | null
  draft: Draft
  setDraft: (fn: (d: Draft) => Draft) => void
  catalog: Catalog
  busy: boolean
  error: string | null
  onClose: () => void
  onSave: () => void
  body: (d: Draft) => object
}) {
  const t = useT()
  const set = <K extends keyof Draft>(k: K, v: Draft[K]) => setDraft((d) => ({ ...d, [k]: v }))
  const setConf = (k: string, v: string) => setDraft((d) => ({ ...d, config: { ...d.config, [k]: v } }))
  const has = (id: string) => draft.tools.includes(id)
  const flip = (id: string) =>
    setDraft((d) => ({ ...d, tools: d.tools.includes(id) ? d.tools.filter((x) => x !== id) : [...d.tools, id] }))
  const advancedCount =
    (draft.channels.length > 0 ? 1 : 0) + (draft.keywords.length > 0 ? 1 : 0) + (draft.linked ? 1 : 0)

  const conf = (k: string, label: string, opts: { placeholder?: string; hint?: string; secret?: boolean } = {}) => (
    <label className="block">
      <span className="mb-1 block text-[11px] font-semibold text-text-high">{label}</span>
      <input
        value={draft.config[k] ?? ''}
        onChange={(e) => setConf(k, e.target.value)}
        placeholder={opts.placeholder}
        type={opts.secret ? 'password' : 'text'}
        autoComplete="off"
        className={inputCls}
      />
      {opts.hint && <span className="mt-1 block text-[10.5px] text-text-muted">{opts.hint}</span>}
    </label>
  )

  return (
    <Modal
      open
      onClose={onClose}
      size="xl"
      title={editing ? t('aiTasks.editor.editTitle', { name: editing.name }) : t('aiTasks.editor.newTitle')}
      subtitle={t('aiTasks.editor.subtitle')}
      footer={
        <div className="flex w-full items-center justify-between gap-2">
          <label className="flex items-center gap-2 text-[12px] text-text-mid">
            <Toggle on={draft.enabled} onChange={(v) => set('enabled', v)} label={t('aiTasks.editor.enabled')} />
            {t('aiTasks.editor.enabled')}
          </label>
          <div className="flex gap-2">
            <Button variant="secondary" onClick={onClose}>
              {t('common.actions.cancel')}
            </Button>
            <Button onClick={onSave} disabled={busy || !draft.name.trim() || !draft.instructions.trim()}>
              {busy && <Loader2 size={13} className="animate-spin" aria-hidden />} {t('aiTasks.editor.save')}
            </Button>
          </div>
        </div>
      }
    >
      <div className="flex flex-col gap-4">
        {error && (
          <div role="alert" className="rounded-lg bg-red-400/10 px-3 py-2 text-[12px] font-semibold text-red-300">
            {error}
          </div>
        )}
        <label className="block">
          <span className="mb-1 block text-[11px] font-bold text-text-high">{t('aiTasks.editor.name')}</span>
          <input value={draft.name} onChange={(e) => set('name', e.target.value)} placeholder={t('aiTasks.editor.namePlaceholder')} className={inputCls} />
        </label>

        <AvailabilityPicker value={draft.entities} onChange={(v) => set('entities', v)} />

        <div className="grid gap-3 sm:grid-cols-[1fr_260px]">
          <div className="rounded-lg border border-line bg-fill-subtle px-3 py-2.5 text-[12px]">
            <span className="font-bold text-text-high">{t('aiTasks.editor.when')}</span>{' '}
            <span className="text-text-mid">{t('aiTasks.editor.whenHint')}</span>
          </div>
          <Select
            value={draft.attachment}
            onChange={(v) => set('attachment', v)}
            size="md"
            ariaLabel={t('aiTasks.editor.attachment')}
            options={[
              { value: '', label: t('aiTasks.editor.attachmentAny') },
              { value: 'any', label: t('aiTasks.editor.attachmentFile') },
              { value: 'pdf', label: t('aiTasks.editor.attachmentPdf') },
              { value: 'image', label: t('aiTasks.editor.attachmentImage') },
            ]}
          />
        </div>

        <label className="block">
          <span className="mb-1 block text-[11px] font-bold text-text-high">{t('aiTasks.editor.instructions')}</span>
          <textarea
            value={draft.instructions}
            onChange={(e) => set('instructions', e.target.value)}
            placeholder={t('aiTasks.editor.instructionsPlaceholder')}
            rows={5}
            className={`${inputCls} resize-y leading-relaxed`}
          />
        </label>

        <Advanced label={t('common.advanced.title')} hint={t('aiTasks.editor.advancedHint')} count={advancedCount} forceOpen={advancedCount > 0}>
          <div className="flex flex-col gap-3">
            <label className="block">
              <span className="mb-1 block text-[11px] font-semibold text-text-high">{t('aiTasks.editor.channels')}</span>
              <MultiSelect
                value={draft.channels}
                onChange={(v) => set('channels', v)}
                size="md"
                placeholder={t('aiTasks.editor.channelsAll')}
                ariaLabel={t('aiTasks.editor.channels')}
                options={CHANNELS.map((c) => ({ value: c, label: channelLabelFor(c, t) }))}
              />
              <span className="mt-1 block text-[10.5px] text-text-muted">{t('aiTasks.editor.channelsHint')}</span>
            </label>
            <div>
              <span className="mb-1 block text-[11px] font-semibold text-text-high">{t('aiTasks.editor.keywords')}</span>
              <TagListInput value={draft.keywords} onChange={(v) => set('keywords', v)} placeholder={t('aiTasks.editor.keywordsPlaceholder')} />
              <span className="mt-1 block text-[10.5px] text-text-muted">{t('aiTasks.editor.keywordsHint')}</span>
            </div>
            <label className="block">
              <span className="mb-1 block text-[11px] font-semibold text-text-high">{t('aiTasks.editor.linked')}</span>
              <Select
                value={draft.linked}
                onChange={(v) => set('linked', v)}
                size="md"
                ariaLabel={t('aiTasks.editor.linked')}
                options={[
                  { value: '', label: t('aiTasks.editor.linkedAny') },
                  { value: 'yes', label: t('aiTasks.editor.linkedYes') },
                  { value: 'no', label: t('aiTasks.editor.linkedNo') },
                ]}
              />
            </label>
          </div>
        </Advanced>

        {/* the allowlist — grouped by where the tool acts, with "not connected" said honestly */}
        <div data-tour="ai-tasks-tools" className="rounded-lg border border-line p-3">
          <div className="mb-1 text-[11px] font-bold text-text-high">{t('aiTasks.editor.tools')}</div>
          <p className="mb-3 text-[10.5px] text-text-muted">{t('aiTasks.editor.toolsHint')}</p>
          <div className="flex flex-col gap-3">
            {GROUP_ORDER.map((g) => (
              <div key={g}>
                <div className="mb-1.5 text-[10px] font-bold uppercase tracking-[0.06em] text-text-faint">{t(GROUP_LABEL[g])}</div>
                <div className="grid gap-1.5 sm:grid-cols-2">
                  {catalog.builtins
                    .filter((b) => b.group === g)
                    .map((b) => (
                      <label
                        key={b.id}
                        className={`flex cursor-pointer items-start gap-2 rounded-lg border px-2.5 py-2 ${
                          has(b.id) ? 'border-brand-400 bg-brand-400/5' : 'border-line'
                        }`}
                      >
                        <input type="checkbox" checked={has(b.id)} onChange={() => flip(b.id)} className="mt-0.5" />
                        <span className="min-w-0">
                          <span className="flex flex-wrap items-center gap-1.5 text-[12px] font-semibold text-text-high">
                            {isToolKey(b.id) ? t(`aiTasks.tools.${b.id}.label` as MessageKey) : b.label}
                            {!b.connected && (
                              <span className="rounded-full bg-amber-400/15 px-1.5 py-px text-[9.5px] font-bold text-amber-300">
                                {t('aiTasks.editor.notConnected')}
                              </span>
                            )}
                          </span>
                          <span className="block text-[10.5px] leading-snug text-text-muted">
                            {isToolKey(b.id) ? t(`aiTasks.tools.${b.id}.hint` as MessageKey) : b.description}
                            {!b.connected && (
                              <>
                                {' '}
                                <Link href="/integrations" className="font-semibold text-brand-300 hover:underline">
                                  {t('aiTasks.editor.connectHint')}
                                </Link>
                              </>
                            )}
                          </span>
                        </span>
                      </label>
                    ))}
                </div>
              </div>
            ))}
            <div>
              <div className="mb-1.5 text-[10px] font-bold uppercase tracking-[0.06em] text-text-faint">{t('aiTasks.editor.groups.mcp')}</div>
              {catalog.mcp.length === 0 ? (
                <p className="text-[10.5px] text-text-muted">{t('aiTasks.editor.noMcp')}</p>
              ) : (
                <div className="grid gap-1.5 sm:grid-cols-2">
                  {catalog.mcp.map((m) => (
                    <label
                      key={m.id}
                      className={`flex cursor-pointer items-start gap-2 rounded-lg border px-2.5 py-2 ${
                        has(m.id) ? 'border-brand-400 bg-brand-400/5' : 'border-line'
                      }`}
                    >
                      <input type="checkbox" checked={has(m.id)} onChange={() => flip(m.id)} className="mt-0.5" />
                      <span className="min-w-0">
                        <span className="flex flex-wrap items-center gap-1.5 text-[12px] font-semibold text-text-high">
                          {m.server} · {m.name}
                          {m.read_only && (
                            <span className="rounded-full bg-fill px-1.5 py-px text-[9.5px] font-bold text-text-muted">
                              {t('aiTasks.editor.readOnlyTool')}
                            </span>
                          )}
                        </span>
                        <span className="block text-[10.5px] leading-snug text-text-muted">{m.description}</span>
                      </span>
                    </label>
                  ))}
                </div>
              )}
            </div>
          </div>

          {/* settings of the picked tools, right under them */}
          {(has('sheets_append_row') || has('drive_upload_attachment') || has('calendar_create_event') || has('webhook_post')) && (
            <div className="mt-3 grid gap-3 border-t border-line pt-3 sm:grid-cols-2">
              {has('sheets_append_row') && (
                <>
                  {conf('spreadsheet_id', t('aiTasks.editor.settings.spreadsheet'), { hint: t('aiTasks.editor.settings.spreadsheetHint') })}
                  {conf('sheet_range', t('aiTasks.editor.settings.range'), { placeholder: t('aiTasks.editor.settings.rangePlaceholder') })}
                  <div className="sm:col-span-2">
                    {conf('sheet_columns', t('aiTasks.editor.settings.columns'), { placeholder: t('aiTasks.editor.settings.columnsPlaceholder') })}
                  </div>
                </>
              )}
              {has('drive_upload_attachment') &&
                conf('drive_folder_id', t('aiTasks.editor.settings.folder'), { hint: t('aiTasks.editor.settings.folderHint') })}
              {has('calendar_create_event') &&
                conf('calendar_id', t('aiTasks.editor.settings.calendar'), { placeholder: t('aiTasks.editor.settings.calendarPlaceholder') })}
              {has('webhook_post') && (
                <>
                  {conf('webhook_url', t('aiTasks.editor.settings.webhookUrl'), { placeholder: 'https://' })}
                  {conf('webhook_secret', t('aiTasks.editor.settings.webhookSecret'), {
                    secret: true,
                    hint: t('aiTasks.editor.settings.webhookSecretHint'),
                  })}
                </>
              )}
            </div>
          )}
        </div>

        <TestBox editing={editing} draft={draft} body={body} />
      </div>
    </Modal>
  )
}

// ---- the dry run ---------------------------------------------------------------------------

function TestBox({ editing, draft, body }: { editing: AiTask | null; draft: Draft; body: (d: Draft) => object }) {
  const t = useT()
  const [text, setText] = useState('')
  const [file, setFile] = useState<{ name: string; mime: string; base64: string } | null>(null)
  const [busy, setBusy] = useState(false)
  const [result, setResult] = useState<TestResult | null>(null)
  const [error, setError] = useState<string | null>(null)

  const pick = (fl: File | undefined) => {
    if (!fl) return
    const reader = new FileReader()
    reader.onload = () => {
      const s = String(reader.result ?? '')
      setFile({ name: fl.name, mime: fl.type, base64: s.slice(s.indexOf(',') + 1) })
    }
    reader.readAsDataURL(fl)
  }

  const run = async () => {
    if (!editing) return
    setBusy(true)
    setError(null)
    setResult(null)
    try {
      setResult(
        await api<TestResult>(`ai/tasks/${editing.id}/test`, {
          method: 'POST',
          body: JSON.stringify({ text, attachment: file ?? undefined, task: body(draft) }),
        }),
      )
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div data-tour="ai-tasks-test" className="rounded-lg border border-dashed border-line-strong p-3">
      <div className="text-[11px] font-bold text-text-high">{t('aiTasks.test.title')}</div>
      <p className="mb-2 text-[10.5px] text-text-muted">{t('aiTasks.test.hint')}</p>
      {!editing ? (
        <p className="text-[11.5px] text-text-muted">{t('aiTasks.editor.saveFirst')}</p>
      ) : (
        <div className="flex flex-col gap-2">
          <textarea
            value={text}
            onChange={(e) => setText(e.target.value)}
            placeholder={t('aiTasks.test.samplePlaceholder')}
            aria-label={t('aiTasks.test.sample')}
            rows={2}
            className={`${inputCls} resize-y`}
          />
          <div className="flex flex-wrap items-center gap-2">
            {file ? (
              <span className="inline-flex items-center gap-1.5 rounded-full bg-fill px-2.5 py-1 text-[11px] text-text-mid">
                <Paperclip size={11} aria-hidden /> {file.name}
                <button type="button" onClick={() => setFile(null)} aria-label={t('aiTasks.test.remove')} className="text-text-muted hover:text-text-high">
                  <X size={11} aria-hidden />
                </button>
              </span>
            ) : (
              <label className="inline-flex cursor-pointer items-center gap-1.5 rounded-lg border border-line px-2.5 py-1.5 text-[11.5px] font-semibold text-text-mid hover:bg-fill-hover">
                <Paperclip size={12} aria-hidden /> {t('aiTasks.test.attach')}
                <input type="file" className="hidden" accept=".pdf,image/*,.txt,.csv" onChange={(e) => pick(e.target.files?.[0])} />
              </label>
            )}
            <Button size="sm" variant="secondary" onClick={run} disabled={busy || (!text.trim() && !file)}>
              {busy ? <Loader2 size={12} className="animate-spin" aria-hidden /> : null}
              {busy ? t('aiTasks.test.running') : t('aiTasks.test.run')}
            </Button>
          </div>
          {error && (
            <div role="alert" className="text-[11.5px] font-semibold text-red-300">
              {error}
            </div>
          )}
          {result && <TestOutcome r={result} />}
        </div>
      )}
    </div>
  )
}

function TestOutcome({ r }: { r: TestResult }) {
  const t = useT()
  if (!r.ok) return <div role="alert" className="text-[11.5px] font-semibold text-red-300">{r.error}</div>
  const entries = Object.entries(r.extracted ?? {})
  return (
    <div className="flex flex-col gap-2 rounded-lg bg-fill-subtle p-2.5 text-[11.5px]">
      {(!r.attachment_conditions_met || !r.keywords_match) && <p className="text-amber-300">{t('aiTasks.test.conditionsNo')}</p>}
      {r.summary && <p className="font-semibold text-text-high">{r.summary}</p>}
      {!r.relevant && r.steps.length === 0 && <p className="text-text-muted">{t('aiTasks.test.notRelevant')}</p>}
      {r.notes.map((n, i) => (
        <p key={i} className="text-text-muted">
          {n}
        </p>
      ))}
      <div>
        <div className="mb-1 text-[10px] font-bold uppercase tracking-[0.06em] text-text-faint">{t('aiTasks.test.extracted')}</div>
        {entries.length === 0 ? (
          <span className="text-text-muted">{t('aiTasks.test.nothing')}</span>
        ) : (
          <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-0.5">
            {entries.map(([k, v]) => (
              <div key={k} className="contents">
                <dt className="text-text-muted">{k}</dt>
                <dd className="break-words text-text-high">{typeof v === 'string' ? v : JSON.stringify(v)}</dd>
              </div>
            ))}
          </dl>
        )}
      </div>
      <div>
        <div className="mb-1 text-[10px] font-bold uppercase tracking-[0.06em] text-text-faint">{t('aiTasks.test.wouldDo')}</div>
        {r.steps.length === 0 ? <span className="text-text-muted">{t('aiTasks.test.noCalls')}</span> : <Steps steps={r.steps} />}
      </div>
      {r.model && (
        <p className="text-[10px] text-text-faint">
          {t('aiTasks.test.tokens', { model: r.model, tokens: r.prompt_tokens + r.completion_tokens })}
        </p>
      )}
    </div>
  )
}

function Steps({ steps, toolLabel }: { steps: Step[]; toolLabel?: (id: string) => string }) {
  const t = useT()
  return (
    <ul className="flex flex-col gap-1">
      {steps.map((s, i) => (
        <li key={i} className="rounded-md border border-line bg-card px-2 py-1.5">
          <div className="flex items-center gap-1.5 text-[11px] font-semibold text-text-high">
            {s.ok ? <CheckCircle2 size={12} className="text-green-300" aria-hidden /> : <XCircle size={12} className="text-red-300" aria-hidden />}
            {toolLabel ? toolLabel(s.tool) : s.tool}
            {s.preview && <span className="rounded-full bg-fill px-1.5 py-px text-[9.5px] text-text-muted">{t('aiTasks.test.preview')}</span>}
          </div>
          <pre className="mt-0.5 whitespace-pre-wrap break-all font-mono text-[10px] text-text-muted">{s.args}</pre>
          <pre className="mt-0.5 whitespace-pre-wrap break-all font-mono text-[10px] text-text-mid">{s.result}</pre>
        </li>
      ))}
    </ul>
  )
}

// ---- runs ------------------------------------------------------------------------------------

function RunsModal({ task, toolLabel, onClose }: { task: AiTask; toolLabel: (id: string) => string; onClose: () => void }) {
  const t = useT()
  const f = useFormat()
  const [status, setStatus] = useState('all')
  const [runs, setRuns] = useState<Run[] | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let live = true
    api<{ runs: Run[] }>(`ai/tasks/${task.id}/runs?status=${status}&limit=100`)
      .then((d) => live && setRuns(d.runs))
      .catch((e) => live && setError(e instanceof Error ? e.message : String(e)))
    return () => {
      live = false
    }
  }, [task.id, status])

  const statusOptions = useMemo(
    () => [
      { value: 'all', label: t('aiTasks.runsModal.filterAll') },
      ...(['ok', 'failed', 'skipped', 'pending'] as const).map((s) => ({ value: s, label: t(STATUS_KEY[s]) })),
    ],
    [t],
  )

  return (
    <Modal open onClose={onClose} size="xl" title={t('aiTasks.runsModal.title', { name: task.name })}>
      <div className="flex flex-col gap-3">
        {error && (
          <div role="alert" className="text-[12px] font-semibold text-red-300">
            {error}
          </div>
        )}
        <Select value={status} onChange={setStatus} size="sm" className="w-[180px]" ariaLabel={t('common.fields.status')} options={statusOptions} />
        {runs === null && !error && <Loader2 size={14} className="animate-spin text-text-muted" aria-hidden />}
        {runs?.length === 0 && <p className="text-[12px] text-text-muted">{t('aiTasks.runsModal.empty')}</p>}
        {runs?.map((r) => {
          const extracted = Object.entries(r.extracted ?? {})
          return (
            <div key={r.id} className="rounded-xl border border-line bg-card px-3 py-2.5">
              <div className="flex flex-wrap items-center gap-2 text-[11.5px]">
                <span className={`rounded-full px-2 py-px text-[10px] font-bold ${STATUS_CLS[r.status] ?? 'bg-fill text-text-muted'}`}>
                  {STATUS_KEY[r.status] ? t(STATUS_KEY[r.status]) : r.status}
                </span>
                <span className="text-text-faint">{f.dateTime(r.finished_at ?? r.created_at)}</span>
                <span className="text-text-faint">· {channelLabelFor(r.channel, t)}</span>
                <Link href={r.chat_url} className="ml-auto font-semibold text-brand-300 hover:underline">
                  {t('aiTasks.runsModal.openChat')}
                </Link>
              </div>
              {r.summary && <p className="mt-1 text-[12px] font-semibold text-text-high">{r.summary}</p>}
              <p className="mt-0.5 text-[11px] text-text-muted">
                <span className="font-semibold">{t('aiTasks.runsModal.message')}:</span> {r.message}
                {r.media_type && <Paperclip size={10} className="ml-1 inline" aria-hidden />}
              </p>
              {r.error && (
                <p className="mt-0.5 text-[11px] text-red-300">
                  <span className="font-semibold">{t('aiTasks.runsModal.error')}:</span> {r.error}
                </p>
              )}
              {extracted.length > 0 && (
                <details className="mt-1.5">
                  <summary className="cursor-pointer text-[11px] font-semibold text-text-mid">{t('aiTasks.runsModal.extracted')}</summary>
                  <dl className="mt-1 grid grid-cols-[auto_1fr] gap-x-3 gap-y-0.5 text-[11px]">
                    {extracted.map(([k, v]) => (
                      <div key={k} className="contents">
                        <dt className="text-text-muted">{k}</dt>
                        <dd className="break-words text-text-high">{typeof v === 'string' ? v : JSON.stringify(v)}</dd>
                      </div>
                    ))}
                  </dl>
                </details>
              )}
              {r.steps.length > 0 && (
                <details className="mt-1.5">
                  <summary className="cursor-pointer text-[11px] font-semibold text-text-mid">
                    {t('aiTasks.runsModal.calls')} ({r.steps.length})
                  </summary>
                  <div className="mt-1">
                    <Steps steps={r.steps} toolLabel={toolLabel} />
                  </div>
                </details>
              )}
            </div>
          )
        })}
      </div>
    </Modal>
  )
}
