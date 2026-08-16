'use client'

import { useCallback, useEffect, useState } from 'react'
import {
  AlertCircle,
  CheckCircle,
  Loader2,
  Lock,
  Plus,
  RefreshCw,
  Trash2,
  X,
  Zap,
} from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { TagListInput } from '@/components/ui/TagListInput'
import { roleHeaders } from '@/lib/role'
import { useRole } from '@/lib/useRole'
import { expireSessionOn401 } from '@/lib/auth'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

interface Server {
  id: number
  name: string
  url: string
  transport: string
  enabled: boolean
  usable: boolean
  tool_allowlist: string[]
  headers: Record<string, string>
  auth_header: string
  auth_token_masked: string
  has_auth_token: boolean
  status: string
  status_detail: string
  last_ok_at: string | null
}

async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}/integrations/${path}`, {
    headers: { 'Content-Type': 'application/json', ...roleHeaders() },
    ...init,
  })
  if (expireSessionOn401(res)) throw new Error('Session expired — redirecting to login…')
  const data = await res.json().catch(() => ({}))
  if (!res.ok) throw new Error((data as { detail?: string }).detail || `HTTP ${res.status}`)
  return data as T
}

const inputCls =
  'w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[12.5px] text-text-primary outline-none transition-colors focus:border-brand-400'

const STATUS_COLOR: Record<string, string> = {
  healthy: 'var(--green-ink)',
  disconnected: 'var(--red-ink)',
  degraded: 'var(--amber-ink)',
  unknown: 'var(--text-muted)',
}

type Draft = Partial<Server> & { auth_token?: string }

const STATUS_LABEL: Record<string, string> = {
  healthy: 'Connected',
  disconnected: 'Disconnected',
  degraded: 'Degraded',
  unknown: 'Not tested',
}

function StatusChip({ s }: { s: Server }) {
  const color = STATUS_COLOR[s.status] || 'var(--text-muted)'
  const label = !s.enabled ? 'Disabled' : STATUS_LABEL[s.status] || s.status
  return (
    <span className="shrink-0 text-[10px] font-bold uppercase tracking-[0.04em]" style={{ color }}>
      {label}
    </span>
  )
}

/**
 * Settings → MCP Servers. Register external Model Context Protocol servers
 * (e.g. a Google Workspace server) whose tools the AI may call mid-conversation.
 * Reads run directly; anything that changes something real needs the customer's
 * confirmation first (enforced server-side). Test loads the server's tool list.
 */
export function McpServersSettings() {
  const { role, ready } = useRole()
  const canManage = role === 'admin' || role === 'superadmin'

  const [servers, setServers] = useState<Server[] | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [draft, setDraft] = useState<Draft | null>(null)
  const [busy, setBusy] = useState(false)
  const [testing, setTesting] = useState<number | null>(null)
  const [tools, setTools] = useState<{ id: number; names: string[] } | null>(null)

  const load = useCallback(async () => {
    try {
      const d = await api<{ servers: Server[] }>('mcp')
      setServers(d.servers)
      setLoadError(null)
    } catch (e) {
      setServers(null)
      setLoadError(e instanceof Error ? e.message : String(e))
    }
  }, [])

  useEffect(() => {
    load()
  }, [load])

  const submit = async () => {
    if (!draft) return
    setBusy(true)
    setError(null)
    try {
      const body: Record<string, unknown> = {
        name: draft.name || '',
        url: draft.url || '',
        transport: draft.transport || 'streamable-http',
        enabled: draft.enabled ?? true,
        auth_header: draft.auth_header || '',
        tool_allowlist: draft.tool_allowlist || [],
      }
      if (draft.auth_token) body.auth_token = draft.auth_token
      if (draft.id) {
        await api(`mcp/${draft.id}`, { method: 'PATCH', body: JSON.stringify(body) })
        setNotice('MCP server updated.')
      } else {
        await api('mcp', { method: 'POST', body: JSON.stringify(body) })
        setNotice('MCP server added — run Test to load its tools.')
      }
      setDraft(null)
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  const remove = async (s: Server) => {
    if (!window.confirm(`Remove "${s.name}"? The AI loses its tools immediately.`)) return
    try {
      await api(`mcp/${s.id}`, { method: 'DELETE' })
      setNotice(`"${s.name}" removed.`)
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  const test = async (s: Server) => {
    setTesting(s.id)
    setError(null)
    setTools(null)
    try {
      const d = await api<{ ok: boolean; tools: string[] }>(`mcp/${s.id}/test`, { method: 'POST' })
      setTools({ id: s.id, names: d.tools || [] })
      setNotice(`${s.name}: ${(d.tools || []).length} tools available.`)
      await load()
    } catch (e) {
      setError(`${s.name} test failed — ${e instanceof Error ? e.message : String(e)}`)
      await load()
    } finally {
      setTesting(null)
    }
  }

  const startEdit = (s: Server) =>
    setDraft({
      id: s.id,
      name: s.name,
      url: s.url,
      transport: s.transport,
      enabled: s.enabled,
      auth_header: s.auth_header,
      tool_allowlist: s.tool_allowlist,
    })

  if (!ready || (servers === null && !loadError)) {
    return (
      <div className="flex items-center gap-1.5 py-6 text-[12px] text-text-muted">
        <Loader2 size={13} className="animate-spin" aria-hidden /> Loading MCP servers…
      </div>
    )
  }

  if (servers === null) {
    return (
      <div className="py-6 text-[12px]">
        <div
          className="flex items-start gap-2 rounded-lg border border-red-500/20 bg-red-500/10 px-3 py-2"
          style={{ color: 'var(--red-ink)' }}
        >
          <AlertCircle size={13} className="mt-0.5 shrink-0" aria-hidden />
          <span>Couldn&apos;t load MCP servers — {loadError}</span>
        </div>
        <Button variant="ghost" onClick={load} style={{ marginTop: 10 }}>
          <RefreshCw size={13} aria-hidden /> Retry
        </Button>
      </div>
    )
  }

  return (
    <div className="flex flex-col gap-4 pt-3.5">
      <div className="flex items-start gap-2">
        <p className="min-w-0 max-w-[720px] flex-1 text-[11.5px] leading-relaxed text-text-mid">
          Connect external <span className="font-bold text-text-high">MCP servers</span> (Model Context
          Protocol) so the AI can use their tools mid-chat — e.g. a Google Workspace server to read a
          Google Sheet or manage a calendar. Read tools run directly; anything that changes something
          real needs the customer&apos;s confirmation first. Nothing is offered to the AI until a server
          is <span className="font-bold text-text-high">enabled</span> and its tools load.
        </p>
        {canManage && (
          <div className="shrink-0">
            <Button onClick={() => setDraft({ transport: 'streamable-http', enabled: true, auth_header: 'Authorization' })}>
              <Plus size={13} aria-hidden /> Add server
            </Button>
          </div>
        )}
      </div>

      {!canManage && (
        <div className="flex items-center gap-1.5 text-[11px] font-semibold text-text-muted">
          <Lock size={11} aria-hidden /> Read-only — only an admin can manage MCP servers.
        </div>
      )}
      {error && (
        <div
          className="flex items-start gap-2 rounded-lg border border-red-500/20 bg-red-500/10 px-3 py-2 text-[12px]"
          style={{ color: 'var(--red-ink)' }}
        >
          <AlertCircle size={13} className="mt-0.5 shrink-0" aria-hidden /> {error}
        </div>
      )}
      {notice && (
        <div
          className="flex items-start gap-2 rounded-lg border border-brand-400/20 bg-brand-400/10 px-3 py-2 text-[12px]"
          style={{ color: 'var(--green-ink)' }}
        >
          <CheckCircle size={13} className="mt-0.5 shrink-0" aria-hidden /> {notice}
        </div>
      )}

      {draft && (
        <div className="rounded-xl border border-line bg-fill-subtle p-4">
          <div className="mb-3 flex items-center justify-between">
            <span className="text-[12.5px] font-bold text-text-high">
              {draft.id ? `Edit "${draft.name}"` : 'Add MCP server'}
            </span>
            <button
              onClick={() => setDraft(null)}
              aria-label="Close form"
              className="grid h-7 w-7 cursor-pointer place-items-center rounded-md text-text-muted hover:bg-fill-hover"
            >
              <X size={14} aria-hidden />
            </button>
          </div>

          <div className="grid gap-3 md:grid-cols-2">
            <label className="block">
              <span className="mb-1 block text-[10.5px] font-bold text-text-high">Name</span>
              <input
                value={draft.name || ''}
                placeholder="Google Workspace"
                onChange={(e) => setDraft({ ...draft, name: e.target.value })}
                className={inputCls}
              />
            </label>
            <label className="block">
              <span className="mb-1 block text-[10.5px] font-bold text-text-high">Transport</span>
              <select
                value={draft.transport || 'streamable-http'}
                onChange={(e) => setDraft({ ...draft, transport: e.target.value })}
                className={inputCls}
              >
                <option value="streamable-http">Streamable HTTP</option>
                <option value="sse">HTTP + SSE</option>
              </select>
            </label>
          </div>

          <label className="mt-3 block">
            <span className="mb-1 block text-[10.5px] font-bold text-text-high">Server URL</span>
            <input
              value={draft.url || ''}
              placeholder="https://mcp.example.com/rpc"
              onChange={(e) => setDraft({ ...draft, url: e.target.value })}
              className={inputCls}
            />
          </label>

          <div className="mt-3 grid gap-3 md:grid-cols-2">
            <label className="block">
              <span className="mb-1 block text-[10.5px] font-bold text-text-high">Auth header (optional)</span>
              <input
                value={draft.auth_header || ''}
                placeholder="Authorization"
                onChange={(e) => setDraft({ ...draft, auth_header: e.target.value })}
                className={inputCls}
              />
            </label>
            <label className="block">
              <span className="mb-1 block text-[10.5px] font-bold text-text-high">Auth token (optional)</span>
              <input
                type="password"
                value={draft.auth_token || ''}
                placeholder={draft.id ? '•••• (unchanged)' : 'Bearer …'}
                onChange={(e) => setDraft({ ...draft, auth_token: e.target.value })}
                className={inputCls}
              />
            </label>
          </div>

          <label className="mt-3 block">
            <span className="mb-1 block text-[10.5px] font-bold text-text-high">
              Tool allowlist{' '}
              <span className="font-normal text-text-muted">(blank = all tools — type one, press Enter to add)</span>
            </span>
            <TagListInput
              value={draft.tool_allowlist || []}
              placeholder="e.g. read_sheet"
              addLabel="Add tool"
              onChange={(tool_allowlist) => setDraft({ ...draft, tool_allowlist })}
            />
          </label>

          <label className="mt-3 flex items-center gap-2 text-[11.5px] text-text-mid">
            <input
              type="checkbox"
              checked={draft.enabled ?? true}
              onChange={(e) => setDraft({ ...draft, enabled: e.target.checked })}
            />
            Enabled (offer this server&apos;s tools to the AI)
          </label>

          <div className="mt-3 flex items-center gap-2">
            <Button onClick={submit} disabled={busy || !(draft.name || '').trim() || !(draft.url || '').trim()}>
              {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <CheckCircle size={13} aria-hidden />}
              {draft.id ? 'Save changes' : 'Add server'}
            </Button>
            <Button variant="ghost" onClick={() => setDraft(null)} disabled={busy}>
              Cancel
            </Button>
          </div>
        </div>
      )}

      <div className="flex flex-col gap-1.5">
        {servers.length === 0 && (
          <div className="rounded-xl border border-dashed border-line-strong px-4 py-6 text-center text-[12px] text-text-muted">
            No MCP servers yet — add one to give the AI new tools (reading a sheet, managing a calendar…).
          </div>
        )}
        {servers.map((s) => (
          <div key={s.id} className="rounded-xl border border-line bg-card px-4 py-2.5">
            <div className="flex items-center gap-2.5">
              <button
                onClick={() => canManage && startEdit(s)}
                className="min-w-0 flex-1 cursor-pointer text-left"
              >
                <span className="flex items-center gap-2">
                  <span className="truncate text-[12px] font-bold text-text-high">{s.name}</span>
                  <StatusChip s={s} />
                </span>
                <span className="block truncate text-[11px] text-text-muted">{s.url}</span>
                {s.status_detail && (
                  <span className="block truncate text-[10.5px] text-text-muted">{s.status_detail}</span>
                )}
              </button>
              {canManage && (
                <>
                  <button
                    onClick={() => test(s)}
                    title="Test connection"
                    aria-label={`Test ${s.name}`}
                    className="grid h-7 w-7 shrink-0 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
                  >
                    {testing === s.id ? (
                      <Loader2 size={13} className="animate-spin" aria-hidden />
                    ) : (
                      <Zap size={13} aria-hidden />
                    )}
                  </button>
                  <button
                    onClick={() => remove(s)}
                    title={`Remove ${s.name}`}
                    aria-label={`Remove ${s.name}`}
                    className="grid h-7 w-7 shrink-0 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-red/15 hover:text-red-300"
                  >
                    <Trash2 size={13} aria-hidden />
                  </button>
                </>
              )}
            </div>
            {tools && tools.id === s.id && (
              <div className="mt-2 flex flex-wrap gap-1.5 border-t border-line pt-2">
                {tools.names.length === 0 && (
                  <span className="text-[10.5px] text-text-muted">No tools advertised.</span>
                )}
                {tools.names.map((n) => (
                  <span
                    key={n}
                    className="rounded-full px-2 py-px text-[10px] font-semibold"
                    style={{ background: 'var(--fill)', color: 'var(--text-mid)' }}
                  >
                    {n}
                  </span>
                ))}
              </div>
            )}
          </div>
        ))}
      </div>
    </div>
  )
}
