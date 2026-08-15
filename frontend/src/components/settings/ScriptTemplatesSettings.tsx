'use client'

import { useCallback, useEffect, useState } from 'react'
import { AlertCircle, CheckCircle, Loader2, Megaphone, Pencil, Plus, Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Modal } from '@/components/ui/Modal'
import { TemplateVariablePicker } from './TemplateVariablePicker'
import { DataTable, type Column } from '@/components/ui/DataTable'
import { roleHeaders } from '@/lib/role'
import { expireSessionOn401, hasCap } from '@/lib/auth'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8000/api'

interface ScriptTemplate {
  id: number
  name: string
  content: string
  created_by: string
}

/** Error that keeps the HTTP status so a 409 (duplicate name) can be
 *  surfaced inline on the field instead of as a generic banner. */
class ApiError extends Error {
  status: number
  constructor(status: number, message: string) {
    super(message)
    this.status = status
  }
}

async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}/calls/${path}`, {
    headers: { 'Content-Type': 'application/json', ...roleHeaders() },
    ...init,
  })
  const data = await res.json().catch(() => ({}))
  if (expireSessionOn401(res)) throw new ApiError(401, 'Session expired — redirecting to login…')
  if (!res.ok) throw new ApiError(res.status, (data as { detail?: string }).detail || `HTTP ${res.status}`)
  return data as T
}

const inputCls =
  'w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[12.5px] text-text-primary outline-none transition-colors focus:border-brand-400'

interface FormState {
  editing: ScriptTemplate | null // null = create
  name: string
  content: string
}

/**
 * Settings → Script Templates: reusable scripts for the one-way "dial, read
 * verbatim, hang up" call — no LLM conversation, no listening. Picked by name
 * from the /chats header's script-call button and by id from the flow
 * builder's "Script call" action. {{variables}} render from the linked
 * contact's CRM record at call time. Create/edit/delete are admin+
 * (server-enforced); agents get a read-only view.
 */
export function ScriptTemplatesSettings() {
  const [list, setList] = useState<ScriptTemplate[] | null>(null)
  const [form, setForm] = useState<FormState | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [nameError, setNameError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [canManage, setCanManage] = useState(false)

  // capability read after mount — create/edit/delete gated on
  // script_templates.manage (admin+ by default); others get a read-only view.
  useEffect(() => setCanManage(hasCap('script_templates.manage')), [])

  const load = useCallback(async () => {
    try {
      const d = await api<{ templates: ScriptTemplate[] }>('script-templates')
      setList(d.templates)
      setLoadError(null)
    } catch (e) {
      // Keep the real reason: a 500 from an unapplied migration, a 403, and a
      // dead server all used to render the same "is the API running?" line,
      // which sent debugging down the wrong path.
      setList(null)
      setLoadError(e instanceof Error ? e.message : String(e))
    }
  }, [])

  useEffect(() => {
    load()
  }, [load])

  const openCreate = () => {
    setNotice(null)
    setError(null)
    setNameError(null)
    setForm({ editing: null, name: '', content: '' })
  }

  const openEdit = (t: ScriptTemplate) => {
    setNotice(null)
    setError(null)
    setNameError(null)
    setForm({ editing: t, name: t.name, content: t.content })
  }

  const insertVariable = (v: string) => {
    if (!form) return
    const sep = form.content && !form.content.endsWith(' ') ? ' ' : ''
    setForm({ ...form, content: `${form.content}${sep}{{${v}}}` })
  }

  const submit = async () => {
    if (!form) return
    setBusy(true)
    setError(null)
    setNameError(null)
    setNotice(null)
    const name = form.name.trim()
    try {
      if (form.editing) {
        await api(`script-templates/${form.editing.id}`, {
          method: 'PATCH',
          body: JSON.stringify({ name, content: form.content.trim() }),
        })
        setNotice(`Script "${name}" updated.`)
      } else {
        await api('script-templates', {
          method: 'POST',
          body: JSON.stringify({ name, content: form.content.trim() }),
        })
        setNotice(`Script "${name}" saved — pick it from the script-call button or a flow's Script call action.`)
      }
      setForm(null)
      await load()
    } catch (e) {
      if (e instanceof ApiError && e.status === 409) {
        setNameError(`"${name}" already exists — pick another name or edit the existing script.`)
      } else {
        setError(e instanceof Error ? e.message : String(e))
      }
    } finally {
      setBusy(false)
    }
  }

  const remove = async (t: ScriptTemplate) => {
    if (!window.confirm(`Delete script "${t.name}"? Any flow step still pointing at it will skip the call.`)) return
    setError(null)
    setNotice(null)
    try {
      await api(`script-templates/${t.id}`, { method: 'DELETE' })
      setNotice(`Script "${t.name}" deleted.`)
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  if (list === null && !loadError) {
    return (
      <div className="flex items-center gap-1.5 py-6 text-[12px] text-text-muted">
        <Loader2 size={13} className="animate-spin" aria-hidden /> Loading script templates…
      </div>
    )
  }

  if (list === null) {
    return (
      <div className="py-6 text-[12px]">
        <div className="flex items-start gap-2 rounded-lg border border-red-500/20 bg-red-500/10 px-3 py-2" style={{ color: 'var(--red-ink)' }}>
          <AlertCircle size={13} className="mt-0.5 shrink-0" aria-hidden />
          <span>Couldn&apos;t load script templates — {loadError}</span>
        </div>
        <p className="mt-2 text-text-muted">
          An <span className="font-bold">HTTP 500</span> here usually means the backend is missing this
          feature&apos;s migration: run <code className="rounded bg-fill px-1">venv\Scripts\python manage.py
          migrate</code> in <code className="rounded bg-fill px-1">backend/</code>. A network/failed-to-fetch error
          means the API isn&apos;t running on port 8000.
        </p>
      </div>
    )
  }

  const columns: Column<ScriptTemplate>[] = [
    {
      key: 'name',
      header: 'Name',
      sortable: true,
      width: 240,
      value: (t) => t.name,
      render: (t) => (
        <span className="flex items-center gap-2">
          <Megaphone size={14} className="shrink-0 text-green-300" aria-hidden />
          <span className="text-[12px] font-bold text-text-high">{t.name}</span>
        </span>
      ),
    },
    {
      key: 'content',
      header: 'Script',
      sortable: true,
      value: (t) => t.content,
      render: (t) => (
        <span className="block max-w-[560px] truncate text-[12px] text-text-muted" title={t.content}>
          {t.content}
        </span>
      ),
    },
    ...(canManage
      ? [
          {
            key: 'actions',
            header: 'Actions',
            align: 'right' as const,
            width: 100,
            render: (t: ScriptTemplate) => (
              <div className="flex items-center justify-end gap-1">
                <button
                  onClick={() => openEdit(t)}
                  title={`Edit ${t.name}`}
                  aria-label={`Edit script ${t.name}`}
                  className="grid h-7 w-7 shrink-0 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
                >
                  <Pencil size={13} aria-hidden />
                </button>
                <button
                  onClick={() => remove(t)}
                  title={`Delete ${t.name}`}
                  aria-label={`Delete script ${t.name}`}
                  className="grid h-7 w-7 shrink-0 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-red/15 hover:text-red-300"
                >
                  <Trash2 size={13} aria-hidden />
                </button>
              </div>
            ),
          },
        ]
      : []),
  ]

  return (
    <div className="flex flex-col gap-4 pt-3.5">
      <p className="text-[11.5px] leading-relaxed text-text-mid">
        A script template is read <span className="font-bold text-text-high">verbatim</span> by the AI on a one-way
        call — dial, read it, hang up. No conversation, no listening. Pick one from the phone icon next to a
        contact&apos;s chat, or wire it into a flow step&apos;s <span className="font-bold text-text-high">Script
        call</span> action. Variables such as <code className="rounded bg-fill px-1">{'{{name}}'}</code> and{' '}
        <code className="rounded bg-fill px-1">{'{{record_status}}'}</code> are filled from the linked contact&apos;s
        CRM record.
      </p>

      {/* The editor renders its own copy — a failure while it is open would
          otherwise sit behind the modal scrim, invisible. */}
      {error && !form && (
        <div className="flex items-start gap-2 rounded-lg border border-red-500/20 bg-red-500/10 px-3 py-2 text-[12px]" style={{ color: 'var(--red-ink)' }}>
          <AlertCircle size={13} className="mt-0.5 shrink-0" aria-hidden /> {error}
        </div>
      )}
      {notice && (
        <div className="flex items-start gap-2 rounded-lg border border-brand-400/20 bg-brand-400/10 px-3 py-2 text-[12px]" style={{ color: 'var(--green-ink)' }}>
          <CheckCircle size={13} className="mt-0.5 shrink-0" aria-hidden /> {notice}
        </div>
      )}

      {form && (
        <Modal
          open
          onClose={() => setForm(null)}
          title={form.editing ? `Edit "${form.editing.name}"` : 'New script'}
          subtitle="Read verbatim on a one-way bot call — no conversation."
          size="lg"
          footer={
            <>
              <Button variant="ghost" onClick={() => setForm(null)} disabled={busy}>
                Cancel
              </Button>
              <Button onClick={submit} disabled={busy || !form.name.trim() || !form.content.trim()}>
                {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <CheckCircle size={13} aria-hidden />}
                {form.editing ? 'Save changes' : 'Save script'}
              </Button>
            </>
          }
        >
          {error && (
            <div
              role="alert"
              className="mb-3 flex items-start gap-2 rounded-lg border border-red-500/30 bg-red-500/10 px-3 py-2 text-[12px] font-semibold text-red-300"
            >
              <AlertCircle size={13} className="mt-0.5 shrink-0" aria-hidden /> {error}
            </div>
          )}
          <label className="mb-3 block">
            <span className="mb-1 block text-[11px] font-bold text-text-high">Name</span>
            <input
              value={form.name}
              onChange={(e) => {
                setForm({ ...form, name: e.target.value })
                setNameError(null)
              }}
              placeholder="Order status reminder"
              className={`${inputCls} ${nameError ? '!border-red-500/60' : ''}`}
            />
            {nameError && (
              <span className="mt-1 flex items-center gap-1 text-[10.5px] font-semibold" style={{ color: 'var(--red-ink)' }}>
                <AlertCircle size={11} className="shrink-0" aria-hidden /> {nameError}
              </span>
            )}
          </label>
          <label className="mb-2 block">
            <span className="mb-1 block text-[11px] font-bold text-text-high">Script</span>
            <textarea
              value={form.content}
              onChange={(e) => setForm({ ...form, content: e.target.value })}
              rows={5}
              placeholder={'Hello {{name}}, this is an automated reminder regarding your {{record_title}}, currently {{record_status}}…'}
              className={`${inputCls} resize-y leading-[1.6]`}
            />
          </label>
          <TemplateVariablePicker onInsert={insertVariable} />
        </Modal>
      )}

      <DataTable
        columns={columns}
        rows={list}
        rowKey={(t) => t.id}
        searchKeys={['name', 'content']}
        searchPlaceholder="Search scripts…"
        empty="No script templates yet — add the reminders your one-way calls should read."
        toolbarRight={
          canManage ? (
            <Button onClick={openCreate}>
              <Plus size={13} aria-hidden /> New script
            </Button>
          ) : undefined
        }
      />
    </div>
  )
}
