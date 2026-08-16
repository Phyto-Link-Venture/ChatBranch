'use client'


import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  AlertCircle,
  CalendarDays,
  Check,
  CheckCircle,
  Copy,
  CreditCard,
  FileSpreadsheet,
  FolderOpen,
  KeyRound,
  Link2,
  Loader2,
  Lock,
  Mail,
  Plug,
  RefreshCw,
  ShoppingBag,
  Trash2,
  Unplug,
} from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Modal } from '@/components/ui/Modal'
import { DataTable, type Column } from '@/components/ui/DataTable'
import { roleHeaders } from '@/lib/role'
import { useRole } from '@/lib/useRole'
import { expireSessionOn401 } from '@/lib/auth'
import { useSettingsDirty } from './SettingsDirtyContext'
import { BookingHoursSettings } from './BookingHoursSettings'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

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

/** Per-account health chip (used in the connected-accounts table). Static hex
 *  only: `var(--…)` inside a lucide `color=` prop renders invisible. */
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
      className="inline-block shrink-0 rounded-full px-2 py-px text-[10px] font-bold"
      style={{ background: s.bg, color: s.fg }}
    >
      {s.lbl}
    </span>
  )
}

/** Connect-first card status: Connected / Needs setup / Not connected. */
function CardStatus({ status }: { status: 'connected' | 'needs_setup' | 'not_connected' }) {
  const map = {
    connected: { cls: 'bg-green/15 text-green-300', lbl: 'Connected' },
    needs_setup: { cls: 'bg-amber/15 text-amber-300', lbl: 'Needs setup' },
    not_connected: { cls: 'bg-fill text-text-muted', lbl: 'Not connected' },
  }[status]
  return <span className={`shrink-0 rounded-full px-2 py-px text-[10px] font-bold ${map.cls}`}>{map.lbl}</span>
}

interface DraftAccount {
  provider: string
  name: string
  config: Record<string, string>
  secrets: Record<string, string>
}

/**
 * Settings → Integrations (upgrade §B). Two clearly separated zones:
 *  1. Connect-first cards — icon, name, one-line purpose, status chip and a
 *     primary Connect button (OAuth full-page redirect via /oauth/<p>/start).
 *  2. A pointer to Settings → App & Provider Keys, where the one-time system
 *     credentials (Google OAuth client, Shopify/Stripe webhook secrets) live.
 *     The #oauth-client-keys anchor is kept for the "Set up" scroll link.
 * Connected accounts (across every provider) live in a DataTable between them.
 *
 * Everything renders with ZERO credentials configured — that is the template's
 * shipping state. Google cards then read "Needs setup" and point at the keys
 * section instead of offering a Connect button that would 400.
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

  // Booking hours belong to the Google Calendar integration (they drive the
  // slots the AI offers + the booking guard), not the global settings — so the
  // editor lives here, revealed from the Google Calendar card.
  const [showBookingHours, setShowBookingHours] = useState(false)

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
      setLoadError(null)
    } catch (e) {
      setProviders(null)
      setLoadError(e instanceof Error ? e.message : String(e))
    }
  }, [])

  useEffect(() => {
    load()
  }, [load])

  // Deep link from the old /settings/booking-hours route (and legacy ?tab=)
  // opens the Google Calendar booking-hours panel straight away. Same
  // window-based read as the OAuth params below (no useSearchParams Suspense).
  useEffect(() => {
    if (typeof window === 'undefined') return
    if (new URLSearchParams(window.location.search).get('booking-hours') === '1') {
      setShowBookingHours(true)
    }
  }, [])

  // The OAuth callback bounces back here with ?connected= / ?error=. Read it
  // from window (not useSearchParams — that forces a Suspense boundary in the
  // app router) and clean the URL so a refresh doesn't repeat the banner.
  // On success we re-load so the freshly-connected account + its status appear.
  useEffect(() => {
    if (typeof window === 'undefined') return
    const q = new URLSearchParams(window.location.search)
    const ok = q.get('connected')
    const bad = q.get('error')
    if (ok) {
      setNotice(`Connected "${ok}".`)
      void load()
    }
    if (bad) setError(bad)
    if (ok || bad) window.history.replaceState({}, '', window.location.pathname)
  }, [load])

  const byProvider = useMemo(() => {
    const map: Record<string, Account[]> = {}
    for (const a of accounts) (map[a.provider] ||= []).push(a)
    return map
  }, [accounts])

  // Provider backing the open add-account modal (api-key draft).
  const draftProvider = draft ? providers?.find((p) => p.id === draft.provider) ?? null : null

  /** Scroll the one-time OAuth-client keys panel into view (the "Set up" link). */
  const scrollToKeys = () => {
    if (typeof document === 'undefined') return
    document.getElementById('oauth-client-keys')?.scrollIntoView({ behavior: 'smooth', block: 'start' })
  }

  const connect = async (provider: Provider) => {
    setError(null)
    setNotice(null)
    setBusy(`connect-${provider.id}`)
    try {
      // The start endpoint returns a ready authorize URL, or {configured:false}
      // when the platform OAuth app hasn't been registered yet (spec §5).
      const d = await api<{ authorize_url?: string; configured?: boolean; reason?: string }>(
        `oauth/${provider.id}/start`,
      )
      if (d.configured === false || !d.authorize_url) {
        setError(
          d.reason ||
            `${provider.label} isn't set up yet — register a Google OAuth client below, then connect in one click.`,
        )
        scrollToKeys()
        return
      }
      // Full-page redirect: the user authorizes with THEIR account and returns.
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

  const providerStatus = (p: Provider): 'connected' | 'needs_setup' | 'not_connected' => {
    const rows = byProvider[p.id] || []
    if (rows.some((r) => r.connected)) return 'connected'
    if (p.kind === 'oauth' && !p.oauth_configured) return 'needs_setup'
    return 'not_connected'
  }

  const accountColumns: Column<Account>[] = [
    {
      key: 'provider',
      header: 'Integration',
      sortable: true,
      width: 200,
      value: (a) => a.provider_label,
      render: (a) => {
        const Icon = ICONS[a.provider] || Plug
        return (
          <div className="flex items-center gap-2.5">
            <span className="grid h-7 w-7 shrink-0 place-items-center rounded-lg bg-fill">
              <Icon size={14} color="#3B82F6" aria-hidden />
            </span>
            <span className="text-[12.5px] font-semibold text-text-high">{a.provider_label}</span>
          </div>
        )
      },
    },
    {
      key: 'name',
      header: 'Account',
      sortable: true,
      value: (a) => `${a.name} ${a.config?.shop_domain ?? ''}`,
      render: (a) => (
        <span className="text-[12.5px] text-text-mid">
          {a.name}
          {a.config?.shop_domain && <span className="ml-1 text-text-muted">· {a.config.shop_domain}</span>}
        </span>
      ),
    },
    {
      key: 'status',
      header: 'Status',
      sortable: true,
      width: 130,
      value: (a) => a.status,
      render: (a) => <StatusChip status={a.status} detail={a.status_detail} />,
    },
    {
      key: 'actions',
      header: 'Actions',
      align: 'right',
      width: 130,
      render: (a) =>
        canManage ? (
          <div className="flex items-center justify-end gap-1">
            <button
              onClick={() => testAccount(a)}
              disabled={busy === `test-${a.id}`}
              title="Test this connection"
              aria-label={`Test ${a.name}`}
              className="grid h-7 w-7 shrink-0 cursor-pointer place-items-center rounded-md text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
            >
              {busy === `test-${a.id}` ? (
                <Loader2 size={13} className="animate-spin" aria-hidden />
              ) : (
                <RefreshCw size={13} aria-hidden />
              )}
            </button>
            {a.connected && (
              <button
                onClick={() => disconnect(a)}
                title="Disconnect (forget the tokens)"
                aria-label={`Disconnect ${a.name}`}
                className="grid h-7 w-7 shrink-0 cursor-pointer place-items-center rounded-md text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
              >
                <Unplug size={13} aria-hidden />
              </button>
            )}
            <button
              onClick={() => remove(a)}
              title="Delete this integration"
              aria-label={`Delete ${a.name}`}
              className="grid h-7 w-7 shrink-0 cursor-pointer place-items-center rounded-md text-text-muted transition-colors hover:bg-red/15 hover:text-red-300"
            >
              <Trash2 size={13} aria-hidden />
            </button>
          </div>
        ) : (
          <span className="text-[10.5px] text-text-faint">—</span>
        ),
    },
  ]

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
    <div className="flex flex-col gap-6 pt-3.5">
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

      {/* ============================ ZONE 1: connect ============================ */}
      <section className="flex flex-col gap-3">
        <div>
          <div className="flex items-center gap-2">
            <Link2 size={15} className="text-brand-300" aria-hidden />
            <h2 className="text-[14px] font-extrabold tracking-[-0.3px] text-text-heading">Connect an integration</h2>
          </div>
          <p className="mt-0.5 text-[11px] text-text-muted">
            One click to link your own account. Google products share a single sign-in.
          </p>
        </div>

        <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-3">
          {providers.map((p) => {
            const Icon = ICONS[p.id] || Plug
            const status = providerStatus(p)
            const blocked = status === 'needs_setup'
            return (
              <div
                key={p.id}
                className="flex flex-col gap-3 rounded-xl border border-line bg-card p-4"
              >
                <div className="flex items-start gap-2.5">
                  <span className="grid h-9 w-9 shrink-0 place-items-center rounded-lg bg-fill">
                    <Icon size={16} color="#3B82F6" aria-hidden />
                  </span>
                  <div className="min-w-0 flex-1">
                    <div className="flex items-center gap-2">
                      <span className="text-[12.5px] font-bold text-text-high">{p.label}</span>
                      <CardStatus status={status} />
                    </div>
                    <p className="mt-0.5 text-[11px] leading-relaxed text-text-muted">{p.blurb}</p>
                  </div>
                </div>

                <div className="mt-auto flex flex-wrap items-center gap-2">
                  {p.id === 'google_calendar' && (
                    <Button
                      variant="secondary"
                      onClick={() => setShowBookingHours((v) => !v)}
                      aria-expanded={showBookingHours}
                    >
                      <CalendarDays size={13} aria-hidden />
                      {showBookingHours ? 'Hide booking hours' : 'Booking hours'}
                    </Button>
                  )}
                  {!canManage ? (
                    <span className="text-[10.5px] text-text-muted">Managed by an admin.</span>
                  ) : p.kind === 'oauth' ? (
                    blocked ? (
                      <div className="flex flex-col gap-1">
                        <Button variant="secondary" onClick={scrollToKeys}>
                          <Lock size={12} aria-hidden /> Set up (admin, one-time)
                        </Button>
                        <span className="text-[10.5px] leading-relaxed text-text-muted">
                          Register the Google OAuth client once (below); then connect <b>{p.label}</b> in one click.
                        </span>
                      </div>
                    ) : (
                      <Button onClick={() => connect(p)} disabled={busy === `connect-${p.id}`}>
                        {busy === `connect-${p.id}` ? (
                          <Loader2 size={13} className="animate-spin" aria-hidden />
                        ) : (
                          <Link2 size={13} aria-hidden />
                        )}
                        {status === 'connected' ? `Reconnect ${p.label}` : `Connect ${p.label}`}
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
                      <Plug size={13} aria-hidden /> {status === 'connected' ? 'Add another' : 'Add account'}
                    </Button>
                  )}
                </div>
              </div>
            )
          })}
        </div>

        {/* ---- Google Calendar → booking hours (the per-day appointment
             schedule the AI books against). Lives with the calendar integration
             rather than as a standalone global tab. ---- */}
        {showBookingHours && (
          <div className="mt-1 rounded-xl border border-line bg-card p-4">
            <div className="mb-1 flex items-center gap-2">
              <CalendarDays size={15} color="#3B82F6" aria-hidden />
              <h3 className="text-[13px] font-bold text-text-high">Google Calendar · Booking hours</h3>
            </div>
            <p className="mb-2 text-[11px] leading-relaxed text-text-muted">
              The per-day windows the AI offers appointments in and the booking guard enforces —
              what&apos;s offered and what&apos;s bookable stay in step.
            </p>
            <BookingHoursSettings />
          </div>
        )}
      </section>

      {/* ---- connected accounts (across every provider) ---- */}
      {accounts.length > 0 && (
        <section className="flex flex-col gap-2">
          <h2 className="text-[14px] font-extrabold tracking-[-0.3px] text-text-heading">Connected accounts</h2>
          <DataTable
            columns={accountColumns}
            rows={accounts}
            rowKey={(a) => a.id}
            searchKeys={['provider', 'name']}
            searchPlaceholder="Search connected accounts…"
            empty="No accounts connected yet."
          />
        </section>
      )}

      {/* Add-account modal (api-key providers: Shopify custom app, etc.) */}
      {draft && draftProvider && (
        <Modal
          open
          onClose={() => setDraft(null)}
          title={`New ${draftProvider.label} account`}
          subtitle="Paste this provider's token — the manual fallback for token-only integrations."
          size="md"
          footer={
            <>
              <Button variant="ghost" onClick={() => setDraft(null)} disabled={busy === 'draft'}>
                Cancel
              </Button>
              <Button onClick={submitDraft} disabled={busy === 'draft'}>
                {busy === 'draft' ? (
                  <Loader2 size={13} className="animate-spin" aria-hidden />
                ) : (
                  <CheckCircle size={13} aria-hidden />
                )}
                Save
              </Button>
            </>
          }
        >
          <label className="mb-3 block">
            <span className="mb-1 block text-[10.5px] font-bold text-text-high">Name</span>
            <input
              value={draft.name}
              onChange={(e) => setDraft({ ...draft, name: e.target.value })}
              className={inputCls}
            />
          </label>
          {draftProvider.config_fields.map((f) => (
            <label key={f.key} className="mb-3 block">
              <span className="mb-1 block text-[10.5px] font-bold text-text-high">{f.label}</span>
              <input
                value={draft.config[f.key] || ''}
                placeholder={f.placeholder}
                onChange={(e) => setDraft({ ...draft, config: { ...draft.config, [f.key]: e.target.value } })}
                className={inputCls}
              />
            </label>
          ))}
          {draftProvider.secret_fields.map((f) => (
            <label key={f.key} className="mb-3 block">
              <span className="mb-1 block text-[10.5px] font-bold text-text-high">{f.label}</span>
              <input
                type="password"
                autoComplete="off"
                value={draft.secrets[f.key] || ''}
                onChange={(e) => setDraft({ ...draft, secrets: { ...draft.secrets, [f.key]: e.target.value } })}
                className={inputCls}
              />
            </label>
          ))}
        </Modal>
      )}

      {/* Provider keys (Google OAuth client, Shopify/Stripe webhook secrets) are a
          one-time SYSTEM setup and live in Settings → App & Provider Keys. Nothing
          is rendered for them here; the anchor stays so old links still resolve. */}
      <section id="oauth-client-keys" className="sr-only" aria-hidden="true">
      </section>
    </div>
  )
}
