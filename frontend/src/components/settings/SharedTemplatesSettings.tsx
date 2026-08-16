'use client'

import { useCallback, useEffect, useState } from 'react'
import { AlertCircle, CheckCircle, Loader2, MessageSquare, Pencil, Plus, Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Modal } from '@/components/ui/Modal'
import { TemplateVariablePicker } from './TemplateVariablePicker'
import { DataTable, type Column } from '@/components/ui/DataTable'
import { roleHeaders } from '@/lib/role'
import { expireSessionOn401, hasCap } from '@/lib/auth'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

interface CannedResponse {
  id: number
  short_code: string
  content: string
  created_by: string
}

/** Error that keeps the HTTP status so a 409 (duplicate short code) can be
 *  surfaced inline on the field instead of as a generic banner. */
class ApiError extends Error {
  status: number
  constructor(status: number, message: string) {
    super(message)
    this.status = status
  }
}

async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}/messaging/${path}`, {
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
  editing: CannedResponse | null // null = create
  short_code: string
  content: string
}

/**
 * Settings → Message Templates: CROSS-CHANNEL shared templates (canned
 * responses). One library serves every channel's composer — WhatsApp,
 * Messenger, Instagram, Telegram and Email — via the "/" short-code picker
 * and the quick-reply chips. {{variables}} render from the linked contact's
 * CRM record at send time. Create/edit/delete are admin+ (server-enforced);
 * agents get a read-only view.
 */
export function SharedTemplatesSettings() {
  const [list, setList] = useState<CannedResponse[] | null>(null)
  const [form, setForm] = useState<FormState | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [codeError, setCodeError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [canManage, setCanManage] = useState(false)

  // capability read after mount (localStorage) — create/edit/delete are gated
  // on templates.manage (admin+ by default), so users without it get a
  // read-only view here.
  useEffect(() => setCanManage(hasCap('templates.manage')), [])

  const load = useCallback(async () => {
    try {
      const d = await api<{ canned: CannedResponse[] }>('canned')
      setList(d.canned)
    } catch {
      setList(null)
    }
  }, [])

  useEffect(() => {
    load()
  }, [load])

  const openCreate = () => {
    setNotice(null)
    setError(null)
    setCodeError(null)
    setForm({ editing: null, short_code: '', content: '' })
  }

  const openEdit = (c: CannedResponse) => {
    setNotice(null)
    setError(null)
    setCodeError(null)
    setForm({ editing: c, short_code: c.short_code, content: c.content })
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
    setCodeError(null)
    setNotice(null)
    const shortCode = form.short_code.trim().toLowerCase().replace(/^\//, '')
    try {
      if (form.editing) {
        await api(`canned/${form.editing.id}`, {
          method: 'PATCH',
          body: JSON.stringify({ short_code: shortCode, content: form.content.trim() }),
        })
        setNotice(`Template /${shortCode} updated.`)
      } else {
        await api('canned', {
          method: 'POST',
          body: JSON.stringify({ short_code: shortCode, content: form.content.trim() }),
        })
        setNotice(`Template /${shortCode} saved — available in every channel's composer.`)
      }
      setForm(null)
      await load()
    } catch (e) {
      if (e instanceof ApiError && e.status === 409) {
        setCodeError(`/${shortCode} already exists — pick another short code or edit the existing template.`)
      } else {
        setError(e instanceof Error ? e.message : String(e))
      }
    } finally {
      setBusy(false)
    }
  }

  const remove = async (c: CannedResponse) => {
    if (!window.confirm(`Delete template /${c.short_code}? It disappears from every channel's composer.`)) return
    setError(null)
    setNotice(null)
    try {
      await api(`canned/${c.id}`, { method: 'DELETE' })
      setNotice(`Template /${c.short_code} deleted.`)
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  if (list === null) {
    return (
      <div className="py-6 text-[12px] text-text-muted">
        Message templates unavailable — is the backend API running?
      </div>
    )
  }

  const columns: Column<CannedResponse>[] = [
    {
      key: 'code',
      header: 'Short code',
      sortable: true,
      width: 200,
      value: (c) => c.short_code,
      render: (c) => (
        <span className="flex items-center gap-2">
          <MessageSquare size={14} className="shrink-0 text-green-300" aria-hidden />
          <span className="font-mono text-[12px] font-bold text-text-high">/{c.short_code}</span>
        </span>
      ),
    },
    {
      key: 'content',
      header: 'Content',
      sortable: true,
      value: (c) => c.content,
      render: (c) => (
        <span className="block max-w-[560px] truncate text-[12px] text-text-muted" title={c.content}>
          {c.content}
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
            render: (c: CannedResponse) => (
              <div className="flex items-center justify-end gap-1">
                <button
                  onClick={() => openEdit(c)}
                  title={`Edit /${c.short_code}`}
                  aria-label={`Edit template ${c.short_code}`}
                  className="grid h-7 w-7 shrink-0 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
                >
                  <Pencil size={13} aria-hidden />
                </button>
                <button
                  onClick={() => remove(c)}
                  title={`Delete /${c.short_code}`}
                  aria-label={`Delete template ${c.short_code}`}
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
        Shared templates work in <span className="font-bold text-text-high">every channel&apos;s composer</span> —
        WhatsApp, Messenger, Instagram, Telegram and Email. Type{' '}
        <code className="rounded bg-fill px-1">/short-code</code> in any conversation to insert one. Variables such as{' '}
        <code className="rounded bg-fill px-1">{'{{name}}'}</code> and{' '}
        <code className="rounded bg-fill px-1">{'{{record_status}}'}</code> are filled from the linked contact&apos;s
        CRM record. WhatsApp&apos;s Meta-approved templates live with the channel: Settings → Channels → WhatsApp.
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
          title={form.editing ? `Edit /${form.editing.short_code}` : 'New template'}
          subtitle="Works in every channel's composer via the /short-code picker."
          size="lg"
          footer={
            <>
              <Button variant="ghost" onClick={() => setForm(null)} disabled={busy}>
                Cancel
              </Button>
              <Button onClick={submit} disabled={busy || !form.short_code.trim() || !form.content.trim()}>
                {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <CheckCircle size={13} aria-hidden />}
                {form.editing ? 'Save changes' : 'Save template'}
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
            <span className="mb-1 block text-[11px] font-bold text-text-high">Short code</span>
            <input
              value={form.short_code}
              onChange={(e) => {
                setForm({ ...form, short_code: e.target.value })
                setCodeError(null)
              }}
              placeholder="payment-link"
              className={`${inputCls} ${codeError ? '!border-red-500/60' : ''}`}
            />
            {codeError ? (
              <span className="mt-1 flex items-center gap-1 text-[10.5px] font-semibold" style={{ color: 'var(--red-ink)' }}>
                <AlertCircle size={11} className="shrink-0" aria-hidden /> {codeError}
              </span>
            ) : (
              <span className="mt-1 block text-[10.5px] text-text-muted">
                What agents type after &quot;/&quot; — lowercase letters, digits, dashes.
              </span>
            )}
          </label>
          <label className="mb-2 block">
            <span className="mb-1 block text-[11px] font-bold text-text-high">Content</span>
            <textarea
              value={form.content}
              onChange={(e) => setForm({ ...form, content: e.target.value })}
              rows={4}
              placeholder={'Hi {{name}}, here is the latest on {{record_title}}: {{record_status}}. More info: {{link}}'}
              className={`${inputCls} resize-y leading-[1.6]`}
            />
          </label>
          <TemplateVariablePicker onInsert={insertVariable} />
        </Modal>
      )}

      <DataTable
        columns={columns}
        rows={list}
        rowKey={(c) => c.id}
        searchKeys={['code', 'content']}
        searchPlaceholder="Search templates…"
        empty="No shared templates yet — add the replies your agents type most."
        toolbarRight={
          canManage ? (
            <Button onClick={openCreate}>
              <Plus size={13} aria-hidden /> New template
            </Button>
          ) : undefined
        }
      />
    </div>
  )
}
