'use client'

import { useCallback, useEffect, useState } from 'react'
import { Loader2, Lock, Pencil, Plus, Server, Trash2, Zap } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Modal } from '@/components/ui/Modal'
import { MultiSelect, Select } from '@/components/ui/Select'
import { TagListInput } from '@/components/ui/TagListInput'
import { Toggle } from '@/components/ui/Toggle'
import { Advanced } from '@/components/ui/Advanced'
import { useRole } from '@/lib/useRole'
import { useT, type MessageKey } from '@/i18n'
import { Notice, Section, api, inputCls, useAgo } from './shared'

/**
 * External MCP servers — other systems' tools (a Google Workspace server, an
 * internal API…) that the ChatBranch assistant may use. Test runs a real
 * initialize + tools/list and caches what the server offers; only enabled,
 * connected servers are offered, narrowed by the allowlist. Tools the server
 * marks read-only run directly; anything else waits for the person's
 * confirmation in the assistant.
 */

interface McpTool {
  name: string
  title: string
  description: string
  read_only: boolean
  offered: boolean
  qualified_name: string
}

interface McpServer {
  id: number
  name: string
  url: string
  transport: string
  enabled: boolean
  usable: boolean
  tool_allowlist: string[]
  auth_header: string
  auth_token_masked: string
  has_auth_token: boolean
  status: 'unknown' | 'healthy' | 'disconnected' | string
  status_detail: string
  last_ok_at: string | null
  server_name: string
  tool_namespace: string
  tools: McpTool[]
}

type Draft = {
  id?: number
  name: string
  url: string
  transport: string
  enabled: boolean
  auth_header: string
  auth_token: string
  tool_allowlist: string[]
  known_tools: string[]
}

const STATUS: Record<string, { label: MessageKey; cls: string; dot: string }> = {
  healthy: { label: 'common.status.connected', cls: 'bg-green/10 text-green-300', dot: 'bg-green' },
  disconnected: { label: 'admin.developers.mcp.status.unreachable', cls: 'bg-red/10 text-red-300', dot: 'bg-red' },
  degraded: { label: 'admin.developers.mcp.status.degraded', cls: 'bg-amber/10 text-amber-300', dot: 'bg-amber' },
  unknown: { label: 'admin.developers.mcp.status.notTested', cls: 'bg-fill-hover text-text-muted', dot: 'bg-text-faint' },
}

function StatusChip({ s }: { s: McpServer }) {
  const t = useT()
  const m: { label: MessageKey; cls: string; dot: string } = !s.enabled
    ? { label: 'common.status.disabled', cls: 'bg-fill text-text-muted', dot: 'bg-text-faint' }
    : STATUS[s.status] ?? STATUS.unknown
  return (
    <span className={`inline-flex shrink-0 items-center gap-1 rounded-full px-2 py-px text-[10.5px] font-semibold ${m.cls}`}>
      <span className={`h-[5px] w-[5px] rounded-full ${m.dot}`} aria-hidden />
      {t(m.label)}
    </span>
  )
}

const EMPTY_DRAFT: Draft = {
  name: '',
  url: '',
  transport: 'streamable-http',
  enabled: true,
  auth_header: 'Authorization',
  auth_token: '',
  tool_allowlist: [],
  known_tools: [],
}

export function McpServersCard() {
  const t = useT()
  const ago = useAgo()
  const { role, ready } = useRole()
  const canManage = role === 'admin' || role === 'superadmin'

  const [servers, setServers] = useState<McpServer[] | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)
  const [draft, setDraft] = useState<Draft | null>(null)
  const [modalError, setModalError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [testing, setTesting] = useState<number | null>(null)

  const load = useCallback(async () => {
    try {
      const d = await api<{ servers: McpServer[] }>('integrations/mcp')
      setServers(d.servers)
      setLoadError(null)
    } catch (e) {
      setLoadError(e instanceof Error ? e.message : String(e))
    }
  }, [])

  useEffect(() => {
    load()
  }, [load])

  const save = async () => {
    if (!draft) return
    setBusy(true)
    setModalError(null)
    try {
      const body: Record<string, unknown> = {
        name: draft.name.trim(),
        url: draft.url.trim(),
        transport: draft.transport,
        enabled: draft.enabled,
        auth_header: draft.auth_header.trim(),
        tool_allowlist: draft.tool_allowlist,
      }
      if (draft.auth_token) body.auth_token = draft.auth_token
      const saved = draft.id
        ? await api<McpServer>(`integrations/mcp/${draft.id}`, { method: 'PATCH', body: JSON.stringify(body) })
        : await api<McpServer>('integrations/mcp', { method: 'POST', body: JSON.stringify(body) })
      setDraft(null)
      await load()
      // A new (or re-addressed) server is tested straight away.
      if (saved.status === 'unknown') await test(saved)
      else setMsg({ ok: true, text: t('admin.developers.mcp.saved', { name: saved.name }) })
    } catch (e) {
      setModalError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  const test = async (s: { id: number; name: string }) => {
    setTesting(s.id)
    setMsg(null)
    try {
      const d = await api<{ ok: boolean; detail: string; tools: string[] }>(`integrations/mcp/${s.id}/test`, {
        method: 'POST',
      })
      setMsg({ ok: d.ok, text: d.ok ? `${s.name}: ${d.detail}` : `${s.name} — ${d.detail}` })
      await load()
    } catch (e) {
      setMsg({ ok: false, text: `${s.name} — ${e instanceof Error ? e.message : String(e)}` })
    } finally {
      setTesting(null)
    }
  }

  const remove = async (s: McpServer) => {
    if (!window.confirm(t('admin.developers.mcp.confirmRemove', { name: s.name }))) return
    try {
      await api(`integrations/mcp/${s.id}`, { method: 'DELETE' })
      setMsg({ ok: true, text: t('admin.developers.mcp.removed', { name: s.name }) })
      await load()
    } catch (e) {
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    }
  }

  const toggleEnabled = async (s: McpServer, enabled: boolean) => {
    try {
      await api(`integrations/mcp/${s.id}`, { method: 'PATCH', body: JSON.stringify({ enabled }) })
      await load()
    } catch (e) {
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    }
  }

  const openEdit = (s: McpServer) => {
    setModalError(null)
    setDraft({
      id: s.id,
      name: s.name,
      url: s.url,
      transport: s.transport,
      enabled: s.enabled,
      auth_header: s.auth_header,
      auth_token: '',
      tool_allowlist: s.tool_allowlist,
      known_tools: s.tools.map((tool) => tool.name),
    })
  }

  const editing = draft?.id ? servers?.find((s) => s.id === draft.id) : undefined

  return (
    <Section
      data-tour="dev-mcp"
      title={t('admin.developers.mcp.title')}
      description={t('admin.developers.mcp.description')}
      actions={
        canManage && (
          <Button
            size="sm"
            variant="secondary"
            onClick={() => {
              setModalError(null)
              setDraft({ ...EMPTY_DRAFT })
            }}
          >
            <Plus size={13} aria-hidden /> {t('admin.developers.mcp.addServer')}
          </Button>
        )
      }
    >
      <div className="flex flex-col gap-3">
        {ready && !canManage && (
          <div className="flex items-center gap-1.5 text-[12px] text-text-muted">
            <Lock size={12} aria-hidden /> {t('admin.developers.mcp.readOnly')}
          </div>
        )}
        {msg && !draft && <Notice ok={msg.ok}>{msg.text}</Notice>}
        {loadError && !servers && <Notice ok={false}>{t('admin.developers.mcp.loadError', { error: loadError })}</Notice>}
        {!servers && !loadError && (
          <div className="flex items-center gap-2 py-3 text-[12.5px] text-text-muted">
            <Loader2 size={13} className="animate-spin" aria-hidden /> {t('admin.developers.mcp.loading')}
          </div>
        )}
        {servers && servers.length === 0 && (
          <div className="rounded-lg border border-dashed border-line-strong px-4 py-8 text-center">
            <Server size={18} className="mx-auto text-text-faint" aria-hidden />
            <p className="mt-2 text-[13px] text-text-muted">{t('admin.developers.mcp.empty')}</p>
          </div>
        )}
        {servers && servers.length > 0 && (
          <ul className="flex flex-col gap-2">
            {servers.map((s) => (
              <li key={s.id} className="rounded-lg border border-line px-3.5 py-3">
                <div className="flex flex-wrap items-start gap-3">
                  <div className="min-w-0 flex-1">
                    <div className="flex flex-wrap items-center gap-2">
                      <span className="truncate text-[13.5px] font-semibold text-text-high">{s.name}</span>
                      <StatusChip s={s} />
                    </div>
                    <div className="mt-0.5 truncate font-mono text-[11.5px] text-text-muted">{s.url}</div>
                    <div className="mt-1 text-[11.5px] text-text-muted">
                      {s.status_detail || t('admin.developers.mcp.runTest')}
                      {s.last_ok_at && ` · ${t('admin.developers.mcp.lastConnected', { when: ago(s.last_ok_at) })}`}
                    </div>
                  </div>
                  {canManage && (
                    <div className="flex shrink-0 items-center gap-1.5">
                      <Toggle
                        on={s.enabled}
                        onChange={(v) => toggleEnabled(s, v)}
                        label={t('admin.developers.mcp.offerTools', { name: s.name })}
                      />
                      <Button
                        variant="ghost"
                        size="sm"
                        onClick={() => test(s)}
                        disabled={testing === s.id}
                        aria-label={t('admin.developers.mcp.testAria', { name: s.name })}
                      >
                        {testing === s.id ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Zap size={13} aria-hidden />}
                        {t('admin.developers.mcp.test')}
                      </Button>
                      <button
                        type="button"
                        onClick={() => openEdit(s)}
                        aria-label={t('admin.developers.mcp.editAria', { name: s.name })}
                        className="grid h-8 w-8 place-items-center rounded-lg text-text-muted hover:bg-fill-hover hover:text-text-high"
                      >
                        <Pencil size={13} aria-hidden />
                      </button>
                      <button
                        type="button"
                        onClick={() => remove(s)}
                        aria-label={t('admin.developers.mcp.removeAria', { name: s.name })}
                        className="grid h-8 w-8 place-items-center rounded-lg text-text-muted hover:bg-red/10 hover:text-red-300"
                      >
                        <Trash2 size={13} aria-hidden />
                      </button>
                    </div>
                  )}
                </div>
                {s.tools.length > 0 && (
                  <div className="mt-2.5 flex flex-wrap gap-1.5 border-t border-line pt-2.5">
                    {s.tools.map((tool) => (
                      <span
                        key={tool.name}
                        title={`${tool.description || tool.title}${tool.offered ? '' : ` — ${t('admin.developers.mcp.notAllowlisted')}`}`}
                        className={`inline-flex items-center gap-1 rounded-full border px-2 py-0.5 font-mono text-[11px] ${
                          tool.offered ? 'border-line-strong text-text-mid' : 'border-dashed border-line text-text-faint line-through'
                        }`}
                      >
                        {tool.name}
                        <span className={`font-sans text-[9.5px] font-semibold ${tool.read_only ? 'text-text-faint' : 'text-amber-300'}`}>
                          {tool.read_only ? t('admin.developers.mcp.toolRead') : t('admin.developers.mcp.toolAsks')}
                        </span>
                      </span>
                    ))}
                  </div>
                )}
              </li>
            ))}
          </ul>
        )}
      </div>

      <Modal
        open={!!draft}
        onClose={() => setDraft(null)}
        title={
          draft?.id
            ? t('admin.developers.mcp.editTitle', { name: editing?.name ?? t('admin.developers.mcp.server') })
            : t('admin.developers.mcp.addTitle')
        }
        subtitle={t('admin.developers.mcp.modalSubtitle')}
        size="md"
        footer={
          <>
            <Button variant="ghost" onClick={() => setDraft(null)} disabled={busy}>
              {t('common.actions.cancel')}
            </Button>
            <Button onClick={save} disabled={busy || !draft?.name.trim() || !draft?.url.trim()}>
              {busy && <Loader2 size={13} className="animate-spin" aria-hidden />}
              {draft?.id ? t('settings.common.saveChanges') : t('admin.developers.mcp.addAndTest')}
            </Button>
          </>
        }
      >
        {draft && (
          <div className="flex flex-col gap-3.5">
            {modalError && (
              <div role="alert" className="rounded-lg border border-red/30 bg-red/10 px-3 py-2 text-[12.5px]" style={{ color: 'var(--red-ink)' }}>
                {modalError}
              </div>
            )}
            <label className="block">
              <span className="mb-1 block text-[12px] font-semibold text-text-high">{t('common.fields.name')}</span>
              <input
                value={draft.name}
                onChange={(e) => setDraft({ ...draft, name: e.target.value })}
                placeholder="Google Workspace"
                className={inputCls}
              />
            </label>
            <label className="block">
              <span className="mb-1 block text-[12px] font-semibold text-text-high">{t('admin.developers.mcp.serverUrl')}</span>
              <input
                value={draft.url}
                onChange={(e) => setDraft({ ...draft, url: e.target.value })}
                placeholder="https://mcp.example.com/mcp"
                className={`${inputCls} font-mono text-[12.5px]`}
              />
            </label>
            <label className="block">
              <span className="mb-1 block text-[12px] font-semibold text-text-high">{t('admin.developers.mcp.token')}</span>
              <input
                type="password"
                value={draft.auth_token}
                onChange={(e) => setDraft({ ...draft, auth_token: e.target.value })}
                placeholder={
                  editing?.has_auth_token
                    ? t('admin.developers.mcp.unchanged', { masked: editing.auth_token_masked })
                    : t('common.status.optional')
                }
                autoComplete="off"
                className={inputCls}
              />
              <span className="mt-1 block text-[11.5px] text-text-muted">{t('admin.developers.mcp.tokenNote')}</span>
            </label>
            {(() => {
              // transport, header name and allowlist all have working defaults
              // (Streamable HTTP, Authorization, every tool) — open when changed
              const advancedCount = [
                draft.transport !== EMPTY_DRAFT.transport,
                !!draft.auth_header.trim() && draft.auth_header.trim() !== EMPTY_DRAFT.auth_header,
                draft.tool_allowlist.length > 0,
              ].filter(Boolean).length
              return (
                <Advanced key={draft.id ?? 'new'} count={advancedCount} forceOpen={advancedCount > 0}>
                  <div className="grid gap-3 sm:grid-cols-2">
                    <div>
                      <span className="mb-1 block text-[12px] font-semibold text-text-high">{t('admin.developers.mcp.transport')}</span>
                      <Select
                        value={draft.transport}
                        onChange={(v) => setDraft({ ...draft, transport: v })}
                        size="md"
                        ariaLabel={t('admin.developers.mcp.transport')}
                        options={[
                          { value: 'streamable-http', label: 'Streamable HTTP' },
                          { value: 'sse', label: t('admin.developers.mcp.sseLegacy'), hint: t('admin.developers.mcp.sseHint') },
                        ]}
                      />
                    </div>
                    <label className="block">
                      <span className="mb-1 block text-[12px] font-semibold text-text-high">{t('admin.developers.mcp.authHeader')}</span>
                      <input
                        value={draft.auth_header}
                        onChange={(e) => setDraft({ ...draft, auth_header: e.target.value })}
                        placeholder="Authorization"
                        className={inputCls}
                      />
                    </label>
                  </div>
                  <div>
                    <span className="mb-1 block text-[12px] font-semibold text-text-high">{t('admin.developers.mcp.toolsToOffer')}</span>
                    {draft.known_tools.length > 0 ? (
                      <MultiSelect
                        value={draft.tool_allowlist}
                        onChange={(tool_allowlist) => setDraft({ ...draft, tool_allowlist })}
                        options={draft.known_tools.map((name) => ({ value: name, label: name }))}
                        placeholder={t('admin.developers.mcp.allTools')}
                        size="md"
                        ariaLabel={t('admin.developers.mcp.toolsToOffer')}
                      />
                    ) : (
                      <TagListInput
                        value={draft.tool_allowlist}
                        onChange={(tool_allowlist) => setDraft({ ...draft, tool_allowlist })}
                        placeholder={t('admin.developers.mcp.toolPlaceholder')}
                        addLabel={t('admin.developers.mcp.addTool')}
                      />
                    )}
                  </div>
                </Advanced>
              )
            })()}
            <div className="flex items-center justify-between gap-3 rounded-lg border border-line px-3 py-2.5">
              <div>
                <div className="text-[12.5px] font-semibold text-text-high">{t('admin.developers.mcp.offer')}</div>
                <div className="text-[11.5px] text-text-muted">{t('admin.developers.mcp.offerHint')}</div>
              </div>
              <Toggle
                on={draft.enabled}
                onChange={(enabled) => setDraft({ ...draft, enabled })}
                label={t('common.status.enabled')}
              />
            </div>
          </div>
        )}
      </Modal>
    </Section>
  )
}
