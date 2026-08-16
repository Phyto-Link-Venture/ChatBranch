'use client'

import { useCallback, useEffect, useState } from 'react'
import { Check, Copy, Globe, Loader2, Pencil, Plus, RefreshCw, Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Modal } from '@/components/ui/Modal'
import { authHeaders, hasCap } from '@/lib/auth'
import { useEntity, useEntityQuery } from '@/lib/EntityContext'
import { EntityScopeSelect } from './EntityScopeSelect'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

/**
 * Website widgets — the embeddable chat bubble, and the one channel a customer
 * can start from with no phone number and no account.
 *
 * A widget is a channel account with channel="website"; its token is PUBLIC by
 * design (it ships in a script tag on someone else's page) and grants nothing
 * but "start or continue a conversation here". Rotating it invalidates every
 * existing embed, which is the point when a snippet ends up somewhere it
 * shouldn't.
 */

interface Widget {
  id: number
  name: string
  token: string
  entity: string | null
  entity_name: string
  title: string
  greeting: string
  accent: string
  placeholder: string
  conversations: number
  embed: string
  script_url: string
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

function CopyBox({ value }: { value: string }) {
  const [copied, setCopied] = useState(false)
  return (
    <div className="flex items-stretch gap-1.5">
      <input
        readOnly
        value={value}
        onFocus={(e) => e.currentTarget.select()}
        className={`${inputCls} cursor-default bg-fill-subtle font-mono !text-[11px]`}
        aria-label="Embed snippet"
      />
      <button
        type="button"
        onClick={async () => {
          try {
            await navigator.clipboard.writeText(value)
            setCopied(true)
            setTimeout(() => setCopied(false), 1500)
          } catch {
            /* clipboard blocked — the value is selectable */
          }
        }}
        title="Copy"
        aria-label="Copy embed snippet"
        className="grid w-9 shrink-0 place-items-center rounded-lg border border-line-strong bg-fill text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
      >
        {copied ? <Check size={13} className="text-green-300" aria-hidden /> : <Copy size={13} aria-hidden />}
      </button>
    </div>
  )
}

export function WidgetsSettings() {
  const entityQ = useEntityQuery()
  const { activeEntity } = useEntity()
  const [widgets, setWidgets] = useState<Widget[] | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)
  const [busy, setBusy] = useState(false)
  const [canManage, setCanManage] = useState(false)
  useEffect(() => setCanManage(hasCap('channels.manage')), [])

  const [open, setOpen] = useState(false)
  const [editing, setEditing] = useState<Widget | null>(null)
  const [name, setName] = useState('')
  const [title, setTitle] = useState('')
  const [greeting, setGreeting] = useState('')
  const [accent, setAccent] = useState('#3B82F6')
  const [placeholder, setPlaceholder] = useState('')
  const [entity, setEntity] = useState<string | null>(null)

  const load = useCallback(async () => {
    try {
      const d = await api<{ widgets: Widget[] }>(`widgets${entityQ && `?${entityQ}`}`)
      setWidgets(d.widgets)
      setLoadError(null)
    } catch (e) {
      setWidgets(null)
      setLoadError(e instanceof Error ? e.message : String(e))
    }
  }, [entityQ])

  useEffect(() => {
    load()
  }, [load])

  const openCreate = () => {
    setEditing(null)
    setName('')
    setTitle('Chat with us')
    setGreeting('Hi! How can we help?')
    setAccent('#3B82F6')
    setPlaceholder('Type your message…')
    setEntity(activeEntity?.slug ?? null)
    setMsg(null)
    setOpen(true)
  }

  const openEdit = (w: Widget) => {
    setEditing(w)
    setName(w.name)
    setTitle(w.title)
    setGreeting(w.greeting)
    setAccent(w.accent || '#3B82F6')
    setPlaceholder(w.placeholder)
    setEntity(w.entity)
    setMsg(null)
    setOpen(true)
  }

  const save = async () => {
    setBusy(true)
    setMsg(null)
    try {
      const body = JSON.stringify({ name: name.trim(), title, greeting, accent, placeholder, entity })
      if (editing) await api(`widgets/${editing.id}`, { method: 'PATCH', body })
      else await api('widgets', { method: 'POST', body })
      setOpen(false)
      setMsg({ ok: true, text: `Widget ${name.trim()} ${editing ? 'updated' : 'created'}.` })
      await load()
    } catch (e) {
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    } finally {
      setBusy(false)
    }
  }

  const rotate = async (w: Widget) => {
    if (
      !window.confirm(
        `Rotate the token for "${w.name}"? Every page still using the old snippet stops working until you update it.`,
      )
    )
      return
    try {
      await api(`widgets/${w.id}`, { method: 'PATCH', body: JSON.stringify({ rotate_token: true }) })
      setMsg({ ok: true, text: 'Token rotated — update the snippet on your site.' })
      await load()
    } catch (e) {
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    }
  }

  const remove = async (w: Widget) => {
    if (
      !window.confirm(
        `Delete widget "${w.name}"?` +
          (w.conversations > 0
            ? ` Its ${w.conversations} conversation(s) are KEPT — only the embed stops working.`
            : ''),
      )
    )
      return
    try {
      await api(`widgets/${w.id}`, { method: 'DELETE' })
      setMsg({ ok: true, text: `Widget ${w.name} deleted.` })
      await load()
    } catch (e) {
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    }
  }

  if (widgets === null) {
    return (
      <div className="py-6 text-[12px] text-text-muted">
        Widgets unavailable{loadError ? ` — ${loadError}` : ' — is the backend API running?'}
      </div>
    )
  }

  return (
    <div className="flex flex-col gap-4 pt-3.5">
      <div className="flex items-start gap-2">
        <p className="min-w-0 max-w-[720px] flex-1 text-[11.5px] leading-relaxed text-text-mid">
          Put a chat bubble on your website. Paste one <b>script tag</b> — no build step, no npm package —
          and visitor conversations land in the same inbox as WhatsApp and email, with the same AI, teams and
          automations. The token is public by design; rotate it if a snippet ends up somewhere it shouldn&apos;t.
        </p>
        {canManage && (
          <div className="shrink-0">
            <Button onClick={openCreate}>
              <Plus size={13} aria-hidden /> Add widget
            </Button>
          </div>
        )}
      </div>

      {msg && (!open || msg.ok) && (
        <div className={`text-[12px] font-semibold ${msg.ok ? 'text-green-300' : 'text-red-300'}`}>
          {msg.text}
        </div>
      )}

      {widgets.length === 0 && (
        <div className="rounded-xl border border-dashed border-line-strong px-4 py-6 text-center text-[12px] text-text-muted">
          No widgets yet — add one to put chat on your site.
        </div>
      )}

      <div className="flex flex-col gap-3">
        {widgets.map((w) => (
          <div key={w.id} className="rounded-xl border border-line bg-card p-4">
            <div className="mb-3 flex items-start gap-2.5">
              <div
                className="mt-0.5 grid h-9 w-9 shrink-0 place-items-center rounded-[9px]"
                style={{ background: (w.accent || '#3B82F6') + '22', color: w.accent || '#3B82F6' }}
              >
                <Globe size={16} aria-hidden />
              </div>
              <div className="min-w-0 flex-1">
                <div className="flex flex-wrap items-center gap-2">
                  <span className="text-[13px] font-bold text-text-high">{w.name}</span>
                  <span className="rounded-full bg-fill px-2 py-px text-[10px] font-semibold text-text-muted">
                    {w.conversations} conversation{w.conversations === 1 ? '' : 's'}
                  </span>
                  {w.entity_name && (
                    <span className="rounded-full bg-fill px-2 py-px text-[10px] text-text-muted">
                      {w.entity_name}
                    </span>
                  )}
                </div>
                <span className="mt-0.5 block text-[11px] text-text-muted">
                  “{w.title}” · <span className="font-mono">{w.token}</span>
                </span>
              </div>
              {canManage && (
                <div className="flex shrink-0 items-center gap-1">
                  <button
                    onClick={() => rotate(w)}
                    title="Rotate token"
                    aria-label={`Rotate token for ${w.name}`}
                    className="grid h-7 w-7 place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-amber-300"
                  >
                    <RefreshCw size={13} aria-hidden />
                  </button>
                  <button
                    onClick={() => openEdit(w)}
                    title={`Edit ${w.name}`}
                    aria-label={`Edit widget ${w.name}`}
                    className="grid h-7 w-7 place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
                  >
                    <Pencil size={13} aria-hidden />
                  </button>
                  <button
                    onClick={() => remove(w)}
                    title={`Delete ${w.name}`}
                    aria-label={`Delete widget ${w.name}`}
                    className="grid h-7 w-7 place-items-center rounded-lg text-text-muted transition-colors hover:bg-red/15 hover:text-red-300"
                  >
                    <Trash2 size={13} aria-hidden />
                  </button>
                </div>
              )}
            </div>
            <span className="mb-1 block text-[10.5px] font-bold text-text-high">
              Paste this before &lt;/body&gt;
            </span>
            <CopyBox value={w.embed} />
          </div>
        ))}
      </div>

      <Modal
        open={open}
        onClose={() => setOpen(false)}
        title={editing ? `Edit ${editing.name}` : 'Add website widget'}
        subtitle="How the bubble looks and what it says first."
        size="md"
        footer={
          <>
            <Button variant="ghost" onClick={() => setOpen(false)} disabled={busy}>
              Cancel
            </Button>
            <Button variant="primary" onClick={save} disabled={busy || !name.trim()}>
              {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Globe size={13} aria-hidden />}{' '}
              {editing ? 'Save changes' : 'Create widget'}
            </Button>
          </>
        }
      >
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
          {msg && !msg.ok && (
            <div
              role="alert"
              className="sm:col-span-2 rounded-lg border border-red-500/30 bg-red-500/10 px-3 py-2 text-[12px] font-semibold text-red-300"
            >
              {msg.text}
            </div>
          )}
          <label className="block">
            <span className="mb-1 block text-[11px] font-bold text-text-high">Internal name</span>
            <input
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder="e.g. Main site"
              className={inputCls}
            />
          </label>
          <label className="block">
            <span className="mb-1 block text-[11px] font-bold text-text-high">Header title</span>
            <input
              value={title}
              onChange={(e) => setTitle(e.target.value)}
              placeholder="Chat with us"
              className={inputCls}
            />
          </label>
          <label className="block sm:col-span-2">
            <span className="mb-1 block text-[11px] font-bold text-text-high">Greeting</span>
            <input
              value={greeting}
              onChange={(e) => setGreeting(e.target.value)}
              placeholder="Hi! How can we help?"
              className={inputCls}
            />
            <span className="mt-1 block text-[10.5px] text-text-muted">
              Shown before the visitor types — it is not sent as a message, so it never starts a conversation
              on its own.
            </span>
          </label>
          <label className="block">
            <span className="mb-1 block text-[11px] font-bold text-text-high">Accent colour</span>
            <div className="flex items-center gap-2">
              <input
                type="color"
                value={accent}
                onChange={(e) => setAccent(e.target.value)}
                aria-label="Accent colour"
                className="h-9 w-12 cursor-pointer rounded-lg border border-line-strong bg-card"
              />
              <input value={accent} onChange={(e) => setAccent(e.target.value)} className={inputCls} />
            </div>
          </label>
          <label className="block">
            <span className="mb-1 block text-[11px] font-bold text-text-high">Input placeholder</span>
            <input
              value={placeholder}
              onChange={(e) => setPlaceholder(e.target.value)}
              placeholder="Type your message…"
              className={inputCls}
            />
          </label>
          <label className="block sm:col-span-2">
            <span className="mb-1 block text-[11px] font-bold text-text-high">Applies to</span>
            <EntityScopeSelect
              value={entity}
              onChange={setEntity}
              globalLabel="Organization-wide"
              className="w-full"
            />
          </label>
        </div>
      </Modal>
    </div>
  )
}
