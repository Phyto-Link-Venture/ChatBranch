'use client'

import { useEffect, useMemo, useState } from 'react'
import { Eye, EyeOff, Loader2, Lock, PlugZap } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { BranchMark } from '@/components/shell/Logo'
import { Trans, useT, type TFunction } from '@/i18n'
import { CodeBlock, CopyField, Notice, Section, Segmented, api, mcpEndpoint } from './shared'

/**
 * "Connect an AI app": ChatBranch's own MCP server. Shows the endpoint, a
 * copy-paste config per client (filled with a key created during this visit,
 * never stored), a live connection test, and the tools an app would get.
 */

interface ToolInfo {
  name: string
  title: string
  description: string
  write: boolean
  capability: string | null
  allowed: boolean
}

interface McpInfo {
  path: string
  server: { name: string; version: string }
  protocol_versions: string[]
  tools: ToolInfo[]
}

type Client = 'claude-code' | 'claude-desktop' | 'cursor' | 'http'

const KEY_PLACEHOLDER = '<your-api-key>'

function snippet(t: TFunction, client: Client, url: string, key: string): { code: string; hint: string; caption: string } {
  switch (client) {
    case 'claude-code':
      return {
        caption: t('admin.developers.connect.terminal'),
        hint: t('admin.developers.connect.hintClaudeCode'),
        code: `claude mcp add --transport http chatbranch ${url} --header "Authorization: Bearer ${key}"`,
      }
    case 'claude-desktop':
      return {
        caption: 'claude_desktop_config.json',
        hint: t('admin.developers.connect.hintClaudeDesktop'),
        code: JSON.stringify(
          {
            mcpServers: {
              chatbranch: {
                command: 'npx',
                args: ['-y', 'mcp-remote', url, '--header', 'Authorization:${CHATBRANCH_AUTH}'],
                env: { CHATBRANCH_AUTH: `Bearer ${key}` },
              },
            },
          },
          null,
          2,
        ),
      }
    case 'cursor':
      return {
        caption: '~/.cursor/mcp.json',
        hint: t('admin.developers.connect.hintCursor'),
        code: JSON.stringify(
          { mcpServers: { chatbranch: { url, headers: { Authorization: `Bearer ${key}` } } } },
          null,
          2,
        ),
      }
    default:
      return {
        caption: 'HTTP',
        hint: t('admin.developers.connect.hintHttp'),
        code: `curl -s ${url} \\
  -H "Authorization: Bearer ${key}" \\
  -H "Content-Type: application/json" \\
  -H "Accept: application/json, text/event-stream" \\
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'`,
      }
  }
}

export function ConnectAiCard({ freshToken }: { freshToken: string | null }) {
  const t = useT()
  const [url, setUrl] = useState('')
  const [client, setClient] = useState<Client>('claude-code')
  const [info, setInfo] = useState<McpInfo | null>(null)
  const [testKey, setTestKey] = useState('')
  const [showKey, setShowKey] = useState(false)
  const [testing, setTesting] = useState(false)
  const [result, setResult] = useState<{ ok: boolean; text: string } | null>(null)

  useEffect(() => setUrl(mcpEndpoint()), [])
  useEffect(() => {
    api<McpInfo>('developers/mcp').then(setInfo).catch(() => setInfo(null))
  }, [])
  useEffect(() => {
    if (freshToken) setTestKey(freshToken)
  }, [freshToken])

  const key = freshToken || KEY_PLACEHOLDER
  const s = useMemo(() => snippet(t, client, url || '/mcp', key), [t, client, url, key])

  const test = async () => {
    setTesting(true)
    setResult(null)
    try {
      if (!testKey.trim()) {
        const d = await api<{ ok: boolean; detail: string }>('developers/mcp/test', { method: 'POST' })
        setResult({ ok: d.ok, text: t('admin.developers.connect.serverCheck', { detail: d.detail }) })
        return
      }
      // The real thing: exactly what an AI app does, from this browser.
      const rpc = async (id: number, method: string, params: object = {}) => {
        const r = await fetch(url, {
          method: 'POST',
          headers: {
            Authorization: `Bearer ${testKey.trim()}`,
            'Content-Type': 'application/json',
            Accept: 'application/json, text/event-stream',
          },
          body: JSON.stringify({ jsonrpc: '2.0', id, method, params }),
        })
        const body = await r.json().catch(() => ({}))
        if (!r.ok || body.error) throw new Error(body.error?.message || `HTTP ${r.status}`)
        return body.result
      }
      const init = await rpc(1, 'initialize', {
        protocolVersion: '2025-06-18',
        capabilities: {},
        clientInfo: { name: 'ChatBranch dashboard', version: '1' },
      })
      const list = await rpc(2, 'tools/list')
      setResult({
        ok: true,
        text: t('admin.developers.connect.connected', {
          server: `${init.serverInfo?.title ?? 'ChatBranch'} ${init.serverInfo?.version ?? ''}`.trim(),
          protocol: String(init.protocolVersion ?? ''),
          count: list.tools?.length ?? 0,
        }),
      })
    } catch (e) {
      setResult({ ok: false, text: t('admin.developers.connect.failed', { error: e instanceof Error ? e.message : String(e) }) })
    } finally {
      setTesting(false)
    }
  }

  const allowed = info?.tools.filter((tool) => tool.allowed).length ?? 0

  return (
    <Section
      data-tour="dev-connect"
      title={t('admin.developers.connect.title')}
      description={t('admin.developers.connect.description')}
    >
      <div className="grid gap-5 lg:grid-cols-[minmax(0,1.15fr)_minmax(0,1fr)]">
        <div className="flex min-w-0 flex-col gap-3">
          <div className="flex items-center gap-2.5">
            <span className="grid h-10 w-10 shrink-0 place-items-center rounded-lg border border-line bg-well text-text-heading">
              <BranchMark size={24} />
            </span>
            <div className="min-w-0">
              <div className="display text-[16px] leading-tight text-text-heading">ChatBranch</div>
              <div className="text-[11.5px] text-text-muted">
                {t('admin.developers.connect.serverLine', { version: info ? info.server.version : '' })}
              </div>
            </div>
          </div>
          <div>
            <div className="eyebrow mb-1.5">{t('admin.developers.connect.endpoint')}</div>
            <CopyField value={url} ariaLabel={t('admin.developers.connect.endpointAria')} />
          </div>
          <div className="flex flex-col gap-2">
            <Segmented
              value={client}
              onChange={setClient}
              ariaLabel={t('admin.developers.connect.appAria')}
              options={[
                { value: 'claude-code', label: 'Claude Code' },
                { value: 'claude-desktop', label: 'Claude Desktop' },
                { value: 'cursor', label: 'Cursor' },
                { value: 'http', label: t('admin.developers.connect.other') },
              ]}
            />
            <p className="text-[12px] text-text-muted">{s.hint}</p>
            <CodeBlock code={s.code} label={t('common.actions.copy')} caption={s.caption} />
            {!freshToken && (
              <p className="text-[11.5px] text-text-muted">
                <Trans
                  k="admin.developers.connect.replaceKey"
                  values={{ placeholder: <span className="font-mono">{KEY_PLACEHOLDER}</span> }}
                />
              </p>
            )}
          </div>

          <div className="mt-1 rounded-lg border border-line p-3">
            <div className="eyebrow mb-2">{t('admin.developers.connect.test')}</div>
            <div className="flex flex-wrap items-center gap-2">
              <div className="relative min-w-[200px] flex-1">
                <input
                  type={showKey ? 'text' : 'password'}
                  value={testKey}
                  onChange={(e) => setTestKey(e.target.value)}
                  placeholder={t('admin.developers.connect.pastePlaceholder')}
                  autoComplete="off"
                  className="h-8 w-full rounded-lg border border-line-strong bg-card px-2.5 pr-8 font-mono text-[12px] text-text-primary outline-none placeholder:font-sans placeholder:text-text-faint focus:border-text-muted"
                  aria-label={t('admin.developers.connect.testKeyAria')}
                />
                <button
                  type="button"
                  onClick={() => setShowKey((v) => !v)}
                  aria-label={showKey ? t('admin.developers.connect.hideKey') : t('admin.developers.connect.showKey')}
                  className="absolute right-1.5 top-1/2 grid h-6 w-6 -translate-y-1/2 place-items-center rounded text-text-faint hover:text-text-high"
                >
                  {showKey ? <EyeOff size={13} aria-hidden /> : <Eye size={13} aria-hidden />}
                </button>
              </div>
              <Button variant="secondary" size="sm" onClick={test} disabled={testing}>
                {testing ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <PlugZap size={13} aria-hidden />}
                {t('admin.developers.connect.test')}
              </Button>
            </div>
            {result && (
              <div className="mt-2.5">
                <Notice ok={result.ok}>{result.text}</Notice>
              </div>
            )}
          </div>
        </div>

        <div className="min-w-0">
          <div className="mb-1.5 flex items-baseline justify-between gap-2">
            <div className="eyebrow">{t('admin.developers.connect.tools')}</div>
            {info && (
              <span className="text-[11.5px] text-text-muted">
                {t('admin.developers.connect.availableOf', { allowed, total: info.tools.length })}
              </span>
            )}
          </div>
          <ul className="max-h-[440px] divide-y divide-line overflow-y-auto rounded-lg border border-line">
            {(info?.tools ?? []).map((tool) => (
              <li key={tool.name} className={`px-3 py-2.5 ${tool.allowed ? '' : 'opacity-60'}`} title={tool.description}>
                <div className="flex items-center gap-2">
                  <span className="truncate font-mono text-[12px] text-text-high">{tool.name}</span>
                  <span
                    className={`ml-auto shrink-0 rounded-full px-1.5 py-px text-[10px] font-semibold ${
                      tool.write ? 'bg-amber/10 text-amber-300' : 'bg-fill-hover text-text-muted'
                    }`}
                  >
                    {tool.write ? t('admin.developers.connect.changesData') : t('admin.developers.connect.read')}
                  </span>
                  {!tool.allowed && (
                    <Lock size={11} className="shrink-0 text-text-faint" aria-label={t('admin.developers.connect.notAvailable')} />
                  )}
                </div>
                <div className="mt-0.5 line-clamp-2 text-[11.5px] text-text-muted">
                  {tool.title}
                  {!tool.allowed && tool.capability && ` — ${t('admin.developers.connect.needs', { cap: tool.capability })}`}
                </div>
              </li>
            ))}
            {!info && <li className="px-3 py-6 text-center text-[12px] text-text-muted">{t('admin.developers.connect.loadingTools')}</li>}
          </ul>
        </div>
      </div>
    </Section>
  )
}
