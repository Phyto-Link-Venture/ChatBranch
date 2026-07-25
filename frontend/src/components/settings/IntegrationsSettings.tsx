'use client'

import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  AlertCircle,
  CalendarDays,
  CheckCircle,
  CreditCard,
  FileSpreadsheet,
  FolderOpen,
  Link2,
  Loader2,
  Lock,
  Mail,
  Plug,
  RefreshCw,
  ShoppingBag,
  Trash2,
  Unplug,
  X,
} from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { roleHeaders } from '@/lib/role'
import { useRole } from '@/lib/useRole'
import { expireSessionOn401 } from '@/lib/auth'
import { useSettingsDirty } from './SettingsDirtyContext'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8000/api'

/* ---------- types (mirror integrations/views.py `_payload` / `providers`) ---------- */

interface FieldSpec {
  key: string
  label: string
  placeholder?: string
}

interface Provider {
  id: string
  label: string
  kind: 'oauth' | 'apikey'
  family: string
  blurb: string
  scopes: string[]
  config_fields: FieldSpec[]
  secret_fields: FieldSpec[]
  actions: string[]
  webhook_path: string
  accounts: number
  connected: number
  oauth_configured: boolean
}

interface Account {
  id: number
  provider: string
  provider_label: string
  kind: string
  name: string
  external_id: string
  config: Record<string, string>
  connected: boolean
  api_key_masked: string
  webhook_secret_masked: string
  has_refresh_token: boolean
  status: string
  status_detail: string
  last_ok_at: string | null
}

interface GlobalSettings {
  google_client_id: string
  google_client_secret_masked: string
  google_redirect_uri: string
  shopify_webhook_secret_masked: string
  stripe_webhook_secret_masked: string
  google_oauth_configured: boolean
  redirect_uri: string
  webhook_paths: Record<string, string>
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

const ICONS: Record<string, typeof Plug> = {
  google_calendar: CalendarDays,
  google_drive: FolderOpen,
  gmail: Mail,
  google_sheets: FileSpreadsheet,
  shopify: ShoppingBag,
  stripe: CreditCard,
}

/** Status chip colours match the channel-health chips — one visual language
 *  for "is this external thing working". Static hex only: `var(--…)` inside a
 *  lucide `color=` prop renders invisible. */
function StatusChip({ status, detail }: { status: string; detail?: string }) {
  const map: Record<string, { bg: string; fg: string; lbl: string }> = {
    healthy: { bg: 'rgba(16,185,129,0.12)', fg: 'var(--green-ink)', lbl: 'Healthy' },
    degraded: { bg: 'rgba(245,158,11,0.12)', fg: 'var(--amber-ink)', lbl: 'Degraded' },
    disconnected: { bg: 'rgba(239,68,68,0.12)', fg: 'var(--red-ink)', lbl: 'Disconnected' },
    revoked: { bg: 'rgba(239,68,68,0.12)', fg: 'var(--red-ink)', lbl: 'Revoked' },
    unknown: { bg: 'var(--fill)', fg: 'var(--text-muted)', lbl: 'Not checked' },
  }
  const s = map[status] || map.unknown
  return (
    <span
      title={detail || undefined}
      className="shrink-0 rounded-full px-2 py-px text-[10px] font-bold"
      style={{ background: s.bg, color: s.fg }}
    >
      {s.lbl}
    </span>
  )
}

interface DraftAccount {
  provider: string
  name: string
  config: Record<string, string>
  secrets: Record<string, string>
}

/**
 * Settings → Integrations. Card grid per provider (mirrors ChannelsSettings'
 * layout): connect button, connected account list, status chip, disconnect.
 *
 * Everything renders with ZERO credentials configured — that is the template's
 * shipping state. Google cards then say "OAuth client not configured yet" and
 * point at the keys panel instead of offering a Connect button that would 400.
 */
export function IntegrationsSettings() {
  const { role, ready } = useRole()
  const canManage = role === 'admin' || role === 'superadmin'
  const isSuper = role === 'superadmin'

  const [providers, setProviders] = useState<Provider[] | null>(null)
  const [accounts, setAccounts] = useState<Account[]>([])
  const [settings, setSettings] = useState<GlobalSettings | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState<string>('')
  const [draft, setDraft] = useState<DraftAccount | null>(null)

  // staged global keys (superadmin) — driven through the shared header cluster
  const [keys, setKeys] = useState<Record<string, string>>({})
  const { setDirty, register } = useSettingsDirty()
  const keysRef = useRef(keys)
  keysRef.current = keys

  const load = useCallback(async () => {
    try {
      const [p, a, s] = await Promise.all([
        api<{ providers: Provider[] }>('providers'),
        api<{ accounts: Account[] }>('accounts'),
        api<GlobalSettings>('settings'),
      ])
      setProviders(p.providers)
      setAccounts(a.accounts)
      setSettings(s)
      setKeys({
        google_client_id: s.google_client_id || '',
        google_client_secret: s.google_client_secret_masked || '',
        google_redirect_uri: s.google_redirect_uri || '',
        shopify_webhook_secret: s.shopify_webhook_secret_masked || '',
        stripe_webhook_secret: s.stripe_webhook_secret_masked || '',
      })
      setLoadError(null)
    } catch (e) {
      setProviders(null)
      setLoadError(e instanceof Error ? e.message : String(e))
    }
  }, [])

  useEffect(() => {
    load()
  }, [load])

  // The OAuth callback bounces back here with ?connected= / ?error=. Read it
  // from window (not useSearchParams — that forces a Suspense boundary in the
  // app router) and clean the URL so a refresh doesn't repeat the banner.
  useEffect(() => {
    if (typeof window === 'undefined') return
    const q = new URLSearchParams(window.location.search)
    const ok = q.get('connected')
    const bad = q.get('error')
    if (ok) setNotice(`Connected "${ok}".`)
    if (bad) setError(bad)
    if (ok || bad) window.history.replaceState({}, '', window.location.pathname)
  }, [])

  const saveKeys = useCallback(async () => {
    const body: Record<string, string> = {}
    for (const [k, v] of Object.entries(keysRef.current)) {
      if (!v.includes('••')) body[k] = v // untouched masked values stay untouched
    }
    if (Object.keys(body).length === 0) return
    await api('settings', { method: 'PUT', body: JSON.stringify(body) })
    setNotice('Integration keys saved.')
    await load()
  }, [load])

  useEffect(() => {
    register({ save: saveKeys, discard: () => void load() })
    return () => register(null)
  }, [register, saveKeys, load])

  const editKey = (key: string, value: string) => {
    setKeys((prev) => ({ ...prev, [key]: value }))
    setDirty(true)
  }

  const byProvider = useMemo(() => {
    const map: Record<string, Account[]> = {}
    for (const a of accounts) (map[a.provider] ||= []).push(a)
    return map
  }, [accounts])

  const connect = async (provider: Provider) => {
    setError(null)
    setNotice(null)
    setBusy(`connect-${provider.id}`)
    try {
      const d = await api<{ authorize_url: string }>(`oauth/${provider.id}/start`)
      window.location.href = d.authorize_url
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy('')
    }
  }

  const submitDraft = async () => {
    if (!draft) return
    setBusy('draft')
    setError(null)
    try {
      await api('accounts', {
        method: 'POST',
        body: JSON.stringify({
          provider: draft.provider,
          name: draft.name.trim() || undefined,
          config: draft.config,
          ...draft.secrets,
        }),
      })
      setDraft(null)
      setNotice('Integration saved.')
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy('')
    }
  }

  const testAccount = async (a: Account) => {
    setBusy(`test-${a.id}`)
    setError(null)
    setNotice(null)
    try {
      const d = await api<{ detail: string }>(`accounts/${a.id}/test`, { method: 'POST' })
      setNotice(`${a.name}: ${d.detail}`)
    } catch (e) {
      setError(`${a.name}: ${e instanceof Error ? e.message : String(e)}`)
    } finally {
      setBusy('')
      await load()
    }
  }

  const disconnect = async (a: Account) => {
    if (!window.confirm(`Disconnect "${a.name}"? Its stored tokens are deleted; the setup is kept.`)) return
    setBusy(`dc-${a.id}`)
    try {
      await api(`accounts/${a.id}/disconnect`, { method: 'POST' })
      setNotice(`${a.name} disconnected.`)
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy('')
    }
  }

  const remove = async (a: Account) => {
    if (!window.confirm(`Delete "${a.name}"? Flow steps using it will start skipping.`)) return
    setBusy(`rm-${a.id}`)
    try {
      await api(`accounts/${a.id}`, { method: 'DELETE' })
      setNotice(`${a.name} removed.`)
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy('')
    }
  }

  if (!ready || (providers === null && !loadError)) {
    return (
      <div className="flex items-center gap-1.5 py-6 text-[12px] text-text-muted">
        <Loader2 size={13} className="animate-spin" aria-hidden /> Loading integrations…
      </div>
    )
  }

  if (providers === null) {
    return (
      <div className="py-6 text-[12px]">
        <div
          className="flex items-start gap-2 rounded-lg border border-red-500/20 bg-red-500/10 px-3 py-2"
          style={{ color: 'var(--red-ink)' }}
        >
          <AlertCircle size={13} className="mt-0.5 shrink-0" aria-hidden />
          <span>Couldn&apos;t load integrations — {loadError}</span>
        </div>
        <Button variant="ghost" onClick={load} style={{ marginTop: 10 }}>
          <RefreshCw size={13} aria-hidden /> Retry
        </Button>
      </div>
    )
  }

  return (
    <div className="flex flex-col gap-5 pt-3.5">
      <p className="max-w-[720px] text-[11.5px] leading-relaxed text-text-mid">
        Connect the outside systems a conversation needs: book a slot, file a document, log a row, email from a real
        mailbox, or let orders and payments flow in on their own. Shopify and Stripe are{' '}
        <span className="font-bold text-text-high">inbound-first</span> — their webhooks turn an order or a successful
        payment into a Record the flow builder and the AI already understand. Everything here is optional: with nothing
        connected the app runs exactly as before and integration steps log a clear skip.
      </p>

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

      {/* ---- provider grid ---- */}
      <div className="grid gap-3 md:grid-cols-2">
        {providers.map((p) => {
          const Icon = ICONS[p.id] || Plug
          const rows = byProvider[p.id] || []
          const blocked = p.kind === 'oauth' && !p.oauth_configured
          return (
            <div key={p.id} className="flex flex-col gap-2.5 rounded-xl border border-line bg-card p-4">
              <div className="flex items-start gap-2.5">
                <span className="grid h-8 w-8 shrink-0 place-items-center rounded-lg bg-fill">
                  <Icon size={15} color="#10B981" aria-hidden />
                </span>
                <div className="min-w-0 flex-1">
                  <div className="flex items-center gap-2">
                    <span className="text-[12.5px] font-bold text-text-high">{p.label}</span>
                    {rows.some((r) => r.connected) ? (
                      <StatusChip status={rows.find((r) => r.connected)?.status || 'unknown'} />
                    ) : (
                      <span className="rounded-full bg-fill px-2 py-px text-[10px] font-bold text-text-muted">
                        Not connected
                      </span>
                    )}
                  </div>
                  <p className="mt-0.5 text-[11px] leading-relaxed text-text-muted">{p.blurb}</p>
                </div>
              </div>

              {p.actions.length > 0 && (
                <div className="flex flex-wrap gap-1">
                  {p.actions.map((a) => (
                    <span
                      key={a}
                      className="rounded-full border border-line bg-fill-subtle px-2 py-0.5 text-[10px] text-text-muted"
                    >
                      {a}
                    </span>
                  ))}
                </div>
              )}

              {p.webhook_path && (
                <div className="text-[10.5px] text-text-muted">
                  Webhook URL: <code className="rounded bg-fill px-1">{p.webhook_path}</code>
                </div>
              )}

              {rows.length > 0 && (
                <div className="flex flex-col gap-1">
                  {rows.map((a) => (
                    <div
                      key={a.id}
                      className="flex items-center gap-2 rounded-lg border border-line bg-fill-subtle px-2.5 py-1.5"
                    >
                      <span className="min-w-0 flex-1 truncate text-[11.5px] font-semibold text-text-high">
                        {a.name}
                        {a.config?.shop_domain ? (
                          <span className="ml-1 font-normal text-text-muted">· {a.config.shop_domain}</span>
                        ) : null}
                      </span>
                      <StatusChip status={a.status} detail={a.status_detail} />
                      {canManage && (
                        <>
                          <button
                            onClick={() => testAccount(a)}
                            disabled={busy === `test-${a.id}`}
                            title="Test this connection"
                            aria-label={`Test ${a.name}`}
                            className="grid h-6 w-6 shrink-0 cursor-pointer place-items-center rounded-md text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
                          >
                            {busy === `test-${a.id}` ? (
                              <Loader2 size={12} className="animate-spin" aria-hidden />
                            ) : (
                              <RefreshCw size={12} aria-hidden />
                            )}
                          </button>
                          {a.connected && (
                            <button
                              onClick={() => disconnect(a)}
                              title="Disconnect (forget the tokens)"
                              aria-label={`Disconnect ${a.name}`}
                              className="grid h-6 w-6 shrink-0 cursor-pointer place-items-center rounded-md text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
                            >
                              <Unplug size={12} aria-hidden />
                            </button>
                          )}
                          <button
                            onClick={() => remove(a)}
                            title="Delete this integration"
                            aria-label={`Delete ${a.name}`}
                            className="grid h-6 w-6 shrink-0 cursor-pointer place-items-center rounded-md text-text-muted transition-colors hover:bg-red/15 hover:text-red-300"
                          >
                            <Trash2 size={12} aria-hidden />
                          </button>
                        </>
                      )}
                    </div>
                  ))}
                </div>
              )}

              {canManage && (
                <div className="flex items-center gap-2">
                  {p.kind === 'oauth' ? (
                    blocked ? (
                      <span className="text-[11px] text-text-muted">
                        Add a Google OAuth client below to enable connecting.
                      </span>
                    ) : (
                      <Button onClick={() => connect(p)} disabled={busy === `connect-${p.id}`}>
                        {busy === `connect-${p.id}` ? (
                          <Loader2 size={13} className="animate-spin" aria-hidden />
                        ) : (
                          <Link2 size={13} aria-hidden />
                        )}
                        Connect
                      </Button>
                    )
                  ) : (
                    <Button
                      variant="secondary"
                      onClick={() =>
                        setDraft({
                          provider: p.id,
                          name: p.label,
                          config: Object.fromEntries(p.config_fields.map((f) => [f.key, ''])),
                          secrets: Object.fromEntries(p.secret_fields.map((f) => [f.key, ''])),
                        })
                      }
                    >
                      <Plug size={13} aria-hidden /> Add account
                    </Button>
                  )}
                </div>
              )}

              {draft?.provider === p.id && (
                <div className="rounded-lg border border-line bg-fill-subtle p-3">
                  <div className="mb-2 flex items-center justify-between">
                    <span className="text-[11.5px] font-bold text-text-high">New {p.label} account</span>
                    <button
                      onClick={() => setDraft(null)}
                      aria-label="Close form"
                      className="grid h-6 w-6 cursor-pointer place-items-center rounded-md text-text-muted hover:bg-fill-hover"
                    >
                      <X size={13} aria-hidden />
                    </button>
                  </div>
                  <label className="mb-2 block">
                    <span className="mb-1 block text-[10.5px] font-bold text-text-high">Name</span>
                    <input
                      value={draft.name}
                      onChange={(e) => setDraft({ ...draft, name: e.target.value })}
                      className={inputCls}
                    />
                  </label>
                  {p.config_fields.map((f) => (
                    <label key={f.key} className="mb-2 block">
                      <span className="mb-1 block text-[10.5px] font-bold text-text-high">{f.label}</span>
                      <input
                        value={draft.config[f.key] || ''}
                        placeholder={f.placeholder}
                        onChange={(e) => setDraft({ ...draft, config: { ...draft.config, [f.key]: e.target.value } })}
                        className={inputCls}
                      />
                    </label>
                  ))}
                  {p.secret_fields.map((f) => (
                    <label key={f.key} className="mb-2 block">
                      <span className="mb-1 block text-[10.5px] font-bold text-text-high">{f.label}</span>
                      <input
                        type="password"
                        autoComplete="off"
                        value={draft.secrets[f.key] || ''}
                        onChange={(e) =>
                          setDraft({ ...draft, secrets: { ...draft.secrets, [f.key]: e.target.value } })
                        }
                        className={inputCls}
                      />
                    </label>
                  ))}
                  <Button onClick={submitDraft} disabled={busy === 'draft'}>
                    {busy === 'draft' ? (
                      <Loader2 size={13} className="animate-spin" aria-hidden />
                    ) : (
                      <CheckCircle size={13} aria-hidden />
                    )}
                    Save
                  </Button>
                </div>
              )}
            </div>
          )
        })}
      </div>

      {/* ---- global keys ---- */}
      <div className="rounded-xl border border-line bg-card p-4">
        <div className="mb-1 flex items-center gap-2">
          <span className="text-[12.5px] font-bold text-text-high">OAuth client &amp; webhook secrets</span>
          {!isSuper && (
            <span className="flex items-center gap-1 text-[10.5px] font-semibold text-text-muted">
              <Lock size={11} aria-hidden /> superadmin only
            </span>
          )}
        </div>
        <p className="mb-3 max-w-[640px] text-[11px] leading-relaxed text-text-muted">
          One Google OAuth client covers Calendar, Drive, Gmail and Sheets — each product only requests its own scope
          when you connect it. Shopify and Stripe sign their webhooks; paste those signing secrets here or the endpoints
          will decline every delivery. Values already stored show masked — leave them alone to keep them.
        </p>
        <div className="grid gap-3 md:grid-cols-2">
          {(
            [
              ['google_client_id', 'Google client id', false],
              ['google_client_secret', 'Google client secret', true],
              ['google_redirect_uri', 'Google redirect URI', false],
              ['shopify_webhook_secret', 'Shopify webhook secret', true],
              ['stripe_webhook_secret', 'Stripe webhook secret (whsec_…)', true],
            ] as [string, string, boolean][]
          ).map(([key, label, secret]) => (
            <label key={key} className="block">
              <span className="mb-1 block text-[10.5px] font-bold text-text-high">{label}</span>
              <input
                value={keys[key] || ''}
                readOnly={!isSuper}
                autoComplete="off"
                placeholder={key === 'google_redirect_uri' ? settings?.redirect_uri : ''}
                onChange={(e) => editKey(key, e.target.value)}
                className={`${inputCls} ${isSuper ? '' : 'cursor-default bg-fill-subtle'}`}
                type={secret && !(keys[key] || '').includes('••') ? 'password' : 'text'}
              />
            </label>
          ))}
        </div>
        <p className="mt-3 text-[10.5px] text-text-muted">
          Redirect URI to register with Google:{' '}
          <code className="rounded bg-fill px-1">{settings?.redirect_uri || '—'}</code>
        </p>
      </div>
    </div>
  )
}
