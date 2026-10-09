'use client'


import Link from 'next/link'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  AlertCircle,
  CalendarDays,
  Check,
  CheckCircle,
  Copy,
  KeyRound,
  Link2,
  Loader2,
  Lock,
  Plug,
  RefreshCw,
  Trash2,
  Unplug,
  UserRound,
} from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { ChannelLogo } from '@/components/ui/ChannelLogo'
import { Modal } from '@/components/ui/Modal'
import { DataTable, type Column } from '@/components/ui/DataTable'
import { EntityPicker } from '@/components/ui/EntityPicker'
import { roleHeaders } from '@/lib/role'
import { useAccess } from '@/lib/useAccess'
import { expireSessionOn401 } from '@/lib/auth'
import { Trans, useT, type MessageKey, type TFunction } from '@/i18n'
import { useSettingsDirty } from './SettingsDirtyContext'

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
  /** Who it answers for (IntegrationResolver: own → entity → parent entities → organization). */
  scope: 'personal' | 'entity' | 'organization'
  entity_name: string | null
  entity_slug: string | null
  owner: string | null
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

async function api<T>(path: string, t: TFunction, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}/integrations/${path}`, {
    headers: { 'Content-Type': 'application/json', ...roleHeaders() },
    ...init,
  })
  if (expireSessionOn401(res)) throw new Error(t('common.errors.sessionExpired'))
  const data = await res.json().catch(() => ({}))
  if (!res.ok) throw new Error((data as { detail?: string }).detail || `HTTP ${res.status}`)
  return data as T
}

const inputCls =
  'w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[12.5px] text-text-primary outline-none transition-colors focus:border-brand-400'

/** The catalog's one-line purpose, translated by provider id (the server's English is the fallback). */
const BLURBS: Record<string, MessageKey> = {
  google_calendar: 'channels.integrations.blurbs.google_calendar',
  google_drive: 'channels.integrations.blurbs.google_drive',
  gmail: 'channels.integrations.blurbs.gmail',
  google_sheets: 'channels.integrations.blurbs.google_sheets',
  google_contacts: 'channels.integrations.blurbs.google_contacts',
  shopify: 'channels.integrations.blurbs.shopify',
  stripe: 'channels.integrations.blurbs.stripe',
}

/** "Used by" chip: how far a connection reaches. */
function ScopeChip({ a }: { a: Account }) {
  const t = useT()
  if (a.scope === 'personal')
    return (
      <span className="inline-flex min-w-0 items-center gap-1 text-[11.5px] text-text-mid">
        <UserRound size={12} className="shrink-0 text-text-muted" aria-hidden />
        <span className="truncate">{t('channels.integrations.scope.personal', { owner: a.owner ?? '—' })}</span>
      </span>
    )
  return (
    <span className="inline-block max-w-full truncate rounded-full bg-fill px-2 py-px text-[10.5px] font-semibold text-text-high">
      {a.scope === 'entity'
        ? t('channels.integrations.scope.entity', { entity: a.entity_name ?? a.entity_slug ?? '—' })
        : t('channels.integrations.scope.organization')}
    </span>
  )
}

/** Providers with a real brand mark (ChannelLogo); anything else gets a plug. */
const BRANDED = new Set(['google_calendar', 'google_drive', 'gmail', 'google_sheets', 'shopify', 'stripe'])

function ProviderLogo({ id, size }: { id: string; size: number }) {
  if (BRANDED.has(id)) return <ChannelLogo channel={id} size={size} variant="tile" title="" />
  return (
    <span className="grid shrink-0 place-items-center rounded-lg bg-fill" style={{ width: size, height: size }}>
      <Plug size={Math.round(size * 0.5)} className="text-text-muted" aria-hidden />
    </span>
  )
}

/** Per-account health chip (used in the connected-accounts table). Static hex
 *  only: `var(--…)` inside a lucide `color=` prop renders invisible. */
function StatusChip({ status, detail }: { status: string; detail?: string }) {
  const t = useT()
  const map: Record<string, { bg: string; fg: string; lbl: MessageKey }> = {
    healthy: { bg: 'rgba(47,125,79,0.12)', fg: 'var(--green-ink)', lbl: 'channels.health.healthy' },
    degraded: { bg: 'rgba(200,137,26,0.12)', fg: 'var(--amber-ink)', lbl: 'channels.health.degraded' },
    disconnected: { bg: 'rgba(192,57,43,0.12)', fg: 'var(--red-ink)', lbl: 'channels.health.disconnected' },
    revoked: { bg: 'rgba(192,57,43,0.12)', fg: 'var(--red-ink)', lbl: 'channels.integrations.revoked' },
    unknown: { bg: 'var(--fill)', fg: 'var(--text-muted)', lbl: 'channels.integrations.notChecked' },
  }
  const s = map[status] || map.unknown
  return (
    <span
      title={detail || undefined}
      className="inline-block shrink-0 rounded-full px-2 py-px text-[10px] font-bold"
      style={{ background: s.bg, color: s.fg }}
    >
      {t(s.lbl)}
    </span>
  )
}

/** Connect-first card status: Connected / Needs setup / Not connected. */
function CardStatus({ status }: { status: 'connected' | 'needs_setup' | 'not_connected' }) {
  const t = useT()
  const all: Record<typeof status, { cls: string; lbl: MessageKey }> = {
    connected: { cls: 'bg-green/15 text-green-300', lbl: 'common.status.connected' },
    needs_setup: { cls: 'bg-amber/15 text-amber-300', lbl: 'channels.integrations.needsSetup' },
    not_connected: { cls: 'bg-fill text-text-muted', lbl: 'channels.integrations.notConnected' },
  }
  const map = all[status]
  return <span className={`shrink-0 rounded-full px-2 py-px text-[10px] font-bold ${map.cls}`}>{t(map.lbl)}</span>
}

interface DraftAccount {
  provider: string
  name: string
  /** Entity slug it is shared with; null = the whole organization. */
  entity: string | null
  config: Record<string, string>
  secrets: Record<string, string>
}

/**
 * Settings → Integrations (upgrade §B). These are SHARED connections — for the
 * whole organization or one entity and its branches; a person's own grants live
 * on Settings → My connections. Two clearly separated zones:
 *  1. Connect-first cards — icon, name, one-line purpose, status chip and a
 *     primary Connect button: "Connect for…" (organization or an entity), then
 *     the OAuth full-page redirect via /oauth/<p>/start?for=shared.
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
  const t = useT()
  // the loaders read the latest t without re-running (a language switch must not refetch)
  const tRef = useRef(t)
  tRef.current = t
  const { role, ready, can } = useAccess()
  // the server's rule for shared connections: admin+ with integrations.manage
  const canManage = (role === 'admin' || role === 'superadmin') && can('integrations.manage')

  const [providers, setProviders] = useState<Provider[] | null>(null)
  const [accounts, setAccounts] = useState<Account[]>([])
  const [settings, setSettings] = useState<GlobalSettings | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState<string>('')
  const [draft, setDraft] = useState<DraftAccount | null>(null)
  // "Connect for…": the OAuth provider being connected and who it will serve
  const [connectFor, setConnectFor] = useState<{ provider: Provider; entity: string | null } | null>(null)

  const load = useCallback(async () => {
    try {
      const [p, a, s] = await Promise.all([
        api<{ providers: Provider[] }>('providers', tRef.current),
        api<{ accounts: Account[] }>('accounts', tRef.current),
        api<GlobalSettings>('settings', tRef.current),
      ])
      setProviders(p.providers)
      // a Google sign-in that was started and never finished leaves an empty
      // placeholder row — nothing to show until a grant lands on it
      setAccounts(a.accounts.filter((x) => !(x.kind === 'oauth' && !x.connected && (!x.status || x.status === 'unknown'))))
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
      setNotice(tRef.current('channels.integrations.connectedNotice', { name: ok }))
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

  /** The shared row that already serves exactly this scope (connecting again replaces its grant). */
  const existingFor = (providerId: string, entity: string | null) =>
    (byProvider[providerId] || []).find(
      (a) => a.scope !== 'personal' && a.connected && (a.entity_slug ?? null) === entity,
    )

  const connect = async (provider: Provider, entity: string | null) => {
    setError(null)
    setNotice(null)
    setBusy(`connect-${provider.id}`)
    try {
      // A SHARED connection for the organization (no entity) or one entity and
      // its branches. The start endpoint returns a ready authorize URL, or
      // {configured:false} when the platform OAuth app isn't registered yet.
      const q = new URLSearchParams({ for: 'shared' })
      if (entity) q.set('entity', entity)
      const d = await api<{ authorize_url?: string; configured?: boolean; reason?: string }>(
        `oauth/${provider.id}/start?${q}`,
        t,
      )
      if (d.configured === false || !d.authorize_url) {
        setConnectFor(null)
        setError(d.reason || t('channels.integrations.notSetUp', { name: provider.label }))
        scrollToKeys()
        return
      }
      // Full-page redirect: the admin authorizes with the account to share and returns.
      window.location.href = d.authorize_url
    } catch (e) {
      setConnectFor(null)
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
      await api('accounts', t, {
        method: 'POST',
        body: JSON.stringify({
          provider: draft.provider,
          name: draft.name.trim() || undefined,
          entity: draft.entity ?? undefined,
          config: draft.config,
          ...draft.secrets,
        }),
      })
      setDraft(null)
      setNotice(t('channels.integrations.saved'))
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
      const d = await api<{ detail: string }>(`accounts/${a.id}/test`, t, { method: 'POST' })
      setNotice(`${a.name}: ${d.detail}`)
    } catch (e) {
      setError(`${a.name}: ${e instanceof Error ? e.message : String(e)}`)
    } finally {
      setBusy('')
      await load()
    }
  }

  const disconnect = async (a: Account) => {
    if (!window.confirm(t('channels.integrations.disconnectConfirm', { name: a.name }))) return
    setBusy(`dc-${a.id}`)
    try {
      await api(`accounts/${a.id}/disconnect`, t, { method: 'POST' })
      setNotice(t('channels.integrations.disconnected', { name: a.name }))
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy('')
    }
  }

  const remove = async (a: Account) => {
    if (!window.confirm(t('channels.integrations.deleteConfirm', { name: a.name }))) return
    setBusy(`rm-${a.id}`)
    try {
      await api(`accounts/${a.id}`, t, { method: 'DELETE' })
      setNotice(t('channels.integrations.removed', { name: a.name }))
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
      header: t('channels.integrations.colIntegration'),
      sortable: true,
      width: 200,
      value: (a) => a.provider_label,
      render: (a) => {
        return (
          <div className="flex items-center gap-2.5">
            <ProviderLogo id={a.provider} size={28} />
            <span className="text-[12.5px] font-semibold text-text-high">{a.provider_label}</span>
          </div>
        )
      },
    },
    {
      key: 'name',
      header: t('channels.integrations.colAccount'),
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
      key: 'scope',
      header: t('channels.integrations.colUsedBy'),
      sortable: true,
      width: 190,
      value: (a) =>
        a.scope === 'personal' ? `~${a.owner ?? ''}` : a.scope === 'entity' ? `1${a.entity_name ?? ''}` : '0',
      render: (a) => <ScopeChip a={a} />,
    },
    {
      key: 'status',
      header: t('common.fields.status'),
      sortable: true,
      width: 130,
      value: (a) => a.status,
      render: (a) => <StatusChip status={a.status} detail={a.status_detail} />,
    },
    {
      key: 'actions',
      header: t('channels.integrations.colActions'),
      align: 'right',
      width: 130,
      render: (a) =>
        canManage ? (
          <div className="flex items-center justify-end gap-1">
            <button
              onClick={() => testAccount(a)}
              disabled={busy === `test-${a.id}`}
              title={t('channels.integrations.testTitle')}
              aria-label={t('channels.integrations.testAria', { name: a.name })}
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
                title={t('channels.integrations.disconnectTitle')}
                aria-label={t('channels.integrations.disconnectAria', { name: a.name })}
                className="grid h-7 w-7 shrink-0 cursor-pointer place-items-center rounded-md text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
              >
                <Unplug size={13} aria-hidden />
              </button>
            )}
            <button
              onClick={() => remove(a)}
              title={t('channels.integrations.deleteTitle')}
              aria-label={t('channels.integrations.deleteAria', { name: a.name })}
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
        <Loader2 size={13} className="animate-spin" aria-hidden /> {t('channels.integrations.loading')}
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
          <span>{t('channels.integrations.loadFailed', { error: loadError ?? '' })}</span>
        </div>
        <Button variant="ghost" onClick={load} style={{ marginTop: 10 }}>
          <RefreshCw size={13} aria-hidden /> {t('common.actions.retry')}
        </Button>
      </div>
    )
  }

  return (
    <div className="flex flex-col gap-6 pt-3.5">
      <p className="max-w-[720px] text-[11.5px] leading-relaxed text-text-mid">
        <Trans
          k="channels.integrations.intro"
          values={{ b: <span className="font-bold text-text-high">{t('channels.integrations.inboundFirst')}</span> }}
        />
      </p>

      {/* Who uses which connection — the resolver's order, in words */}
      <section
        aria-labelledby="integrations-reach"
        className="max-w-[720px] rounded-xl border border-line bg-well px-4 py-3 text-[11.5px] leading-relaxed text-text-mid"
      >
        <h2 id="integrations-reach" className="text-[12.5px] font-bold text-text-high">
          {t('channels.integrations.reach.title')}
        </h2>
        <p className="mt-1">
          <Trans
            k="channels.integrations.reach.body"
            values={{
              link: (
                <Link href="/settings/connections" className="font-semibold text-brand-300 underline-offset-2 hover:underline">
                  {t('channels.integrations.reach.myConnections')}
                </Link>
              ),
            }}
          />
        </p>
        <ol className="mt-1.5 flex list-decimal flex-col gap-0.5 pl-5">
          <li>{t('channels.integrations.reach.own')}</li>
          <li>{t('channels.integrations.reach.entity')}</li>
          <li>{t('channels.integrations.reach.parents')}</li>
          <li>{t('channels.integrations.reach.org')}</li>
        </ol>
        <p className="mt-1.5 text-text-muted">{t('channels.integrations.reach.note')}</p>
      </section>

      {error && !connectFor && (
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
            <h2 className="text-[14px] font-extrabold tracking-[-0.3px] text-text-heading">
              {t('channels.integrations.connectTitle')}
            </h2>
          </div>
          <p className="mt-0.5 text-[11px] text-text-muted">{t('channels.integrations.connectHint')}</p>
        </div>

        <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-3">
          {providers.map((p) => {
            const status = providerStatus(p)
            const blocked = status === 'needs_setup'
            return (
              <div
                key={p.id}
                className="flex flex-col gap-3 rounded-xl border border-line bg-card p-4"
              >
                <div className="flex items-start gap-2.5">
                  <ProviderLogo id={p.id} size={36} />
                  <div className="min-w-0 flex-1">
                    <div className="flex items-center gap-2">
                      <span className="text-[12.5px] font-bold text-text-high">{p.label}</span>
                      <CardStatus status={status} />
                    </div>
                    <p className="mt-0.5 text-[11px] leading-relaxed text-text-muted">
                      {BLURBS[p.id] ? t(BLURBS[p.id]) : p.blurb}
                    </p>
                  </div>
                </div>

                <div className="mt-auto flex flex-wrap items-center gap-2">
                  {p.id === 'google_calendar' && (
                    // working hours belong to each booking skill (Connected app → Calendar)
                    <Link
                      href="/skills"
                      className="inline-flex items-center gap-1.5 rounded-lg border border-line-strong px-2.5 py-1.5 text-[12px] font-semibold text-text-high hover:bg-fill-hover"
                    >
                      <CalendarDays size={13} aria-hidden />
                      {t('channels.integrations.bookingHours')}
                    </Link>
                  )}
                  {!canManage ? (
                    <span className="text-[10.5px] text-text-muted">
                      {t('channels.integrations.managedByAdmin')}
                      {p.kind === 'oauth' && p.id !== 'google_contacts' && (
                        <>
                          {' '}
                          <Link href="/settings/connections" className="font-semibold text-brand-300 hover:underline">
                            {t('channels.integrations.connectYourOwn')}
                          </Link>
                        </>
                      )}
                    </span>
                  ) : p.id === 'google_contacts' ? (
                    // read-only, per person: everyone imports from their OWN address book
                    <Link
                      href="/settings/connections"
                      className="inline-flex items-center gap-1.5 rounded-lg border border-line-strong px-2.5 py-1.5 text-[12px] font-semibold text-text-high hover:bg-fill-hover"
                    >
                      <UserRound size={13} aria-hidden />
                      {t('channels.integrations.personalOnly')}
                    </Link>
                  ) : p.kind === 'oauth' ? (
                    blocked ? (
                      <div className="flex flex-col gap-1">
                        <Button variant="secondary" onClick={scrollToKeys}>
                          <Lock size={12} aria-hidden /> {t('channels.integrations.setUp')}
                        </Button>
                        <span className="text-[10.5px] leading-relaxed text-text-muted">
                          <Trans k="channels.integrations.setUpHint" values={{ name: <b>{p.label}</b> }} />
                        </span>
                      </div>
                    ) : (
                      <Button
                        onClick={() => {
                          setError(null)
                          setConnectFor({ provider: p, entity: null })
                        }}
                        disabled={busy === `connect-${p.id}`}
                      >
                        <Link2 size={13} aria-hidden />
                        {status === 'connected'
                          ? t('channels.integrations.connectAnother')
                          : t('channels.integrations.connectNamed', { name: p.label })}
                      </Button>
                    )
                  ) : (
                    <Button
                      variant="secondary"
                      onClick={() =>
                        setDraft({
                          provider: p.id,
                          name: p.label,
                          entity: null,
                          config: Object.fromEntries(p.config_fields.map((f) => [f.key, ''])),
                          secrets: Object.fromEntries(p.secret_fields.map((f) => [f.key, ''])),
                        })
                      }
                    >
                      <Plug size={13} aria-hidden />{' '}
                      {status === 'connected' ? t('channels.drawer.footer.addAnother') : t('channels.integrations.addAccount')}
                    </Button>
                  )}
                </div>
              </div>
            )
          })}
        </div>

      </section>

      {/* ---- connected accounts (across every provider) ---- */}
      {accounts.length > 0 && (
        <section className="flex flex-col gap-2">
          <h2 className="text-[14px] font-extrabold tracking-[-0.3px] text-text-heading">
            {t('channels.integrations.connectedAccounts')}
          </h2>
          <DataTable
            columns={accountColumns}
            rows={accounts}
            rowKey={(a) => a.id}
            searchKeys={['provider', 'name']}
            searchPlaceholder={t('channels.integrations.search')}
            empty={t('channels.integrations.empty')}
          />
        </section>
      )}

      {/* Connect for… (OAuth providers): who this shared connection serves */}
      {connectFor && (
        <Modal
          open
          onClose={() => setConnectFor(null)}
          title={t('channels.integrations.connectFor.title', { name: connectFor.provider.label })}
          subtitle={t('channels.integrations.connectFor.subtitle')}
          size="md"
          footer={
            <>
              <Button variant="ghost" onClick={() => setConnectFor(null)} disabled={busy !== ''}>
                {t('common.actions.cancel')}
              </Button>
              <Button
                onClick={() => connect(connectFor.provider, connectFor.entity)}
                disabled={busy === `connect-${connectFor.provider.id}`}
              >
                {busy === `connect-${connectFor.provider.id}` ? (
                  <Loader2 size={13} className="animate-spin" aria-hidden />
                ) : (
                  <Link2 size={13} aria-hidden />
                )}
                {t('channels.integrations.connectFor.continue')}
              </Button>
            </>
          }
        >
          {error && (
            <div
              role="alert"
              className="mb-3 flex items-start gap-2 rounded-lg border border-red-500/20 bg-red-500/10 px-3 py-2 text-[12px]"
              style={{ color: 'var(--red-ink)' }}
            >
              <AlertCircle size={13} className="mt-0.5 shrink-0" aria-hidden /> {error}
            </div>
          )}
          <span className="mb-1 block text-[10.5px] font-bold text-text-high">
            {t('channels.integrations.connectFor.sharedWith')}
          </span>
          <EntityPicker
            value={connectFor.entity}
            onChange={(entity) => setConnectFor({ ...connectFor, entity })}
            globalLabel={t('channels.integrations.scope.organization')}
            ariaLabel={t('channels.integrations.connectFor.sharedWith')}
          />
          <p className="mt-1.5 text-[11px] leading-relaxed text-text-muted">
            {connectFor.entity
              ? t('channels.integrations.connectFor.entityHint')
              : t('channels.integrations.connectFor.orgHint')}
          </p>
          {existingFor(connectFor.provider.id, connectFor.entity) && (
            <p className="mt-2 rounded-lg bg-amber/10 px-3 py-2 text-[11px] leading-relaxed" style={{ color: 'var(--amber-ink)' }}>
              {t('channels.integrations.connectFor.replaces')}
            </p>
          )}
          <p className="mt-3 text-[11px] leading-relaxed text-text-muted">
            <Trans
              k="channels.integrations.connectFor.personalHint"
              values={{
                link: (
                  <Link href="/settings/connections" className="font-semibold text-brand-300 hover:underline">
                    {t('channels.integrations.reach.myConnections')}
                  </Link>
                ),
              }}
            />
          </p>
        </Modal>
      )}

      {/* Add-account modal (api-key providers: Shopify custom app, etc.) */}
      {draft && draftProvider && (
        <Modal
          open
          onClose={() => setDraft(null)}
          title={t('channels.integrations.newAccount', { name: draftProvider.label })}
          subtitle={t('channels.integrations.newAccountHint')}
          size="md"
          footer={
            <>
              <Button variant="ghost" onClick={() => setDraft(null)} disabled={busy === 'draft'}>
                {t('common.actions.cancel')}
              </Button>
              <Button onClick={submitDraft} disabled={busy === 'draft'}>
                {busy === 'draft' ? (
                  <Loader2 size={13} className="animate-spin" aria-hidden />
                ) : (
                  <CheckCircle size={13} aria-hidden />
                )}
                {t('common.actions.save')}
              </Button>
            </>
          }
        >
          <label className="mb-3 block">
            <span className="mb-1 block text-[10.5px] font-bold text-text-high">{t('common.fields.name')}</span>
            <input
              value={draft.name}
              onChange={(e) => setDraft({ ...draft, name: e.target.value })}
              className={inputCls}
            />
          </label>
          <div className="mb-3">
            <span className="mb-1 block text-[10.5px] font-bold text-text-high">
              {t('channels.integrations.connectFor.sharedWith')}
            </span>
            <EntityPicker
              value={draft.entity}
              onChange={(entity) => setDraft({ ...draft, entity })}
              globalLabel={t('channels.integrations.scope.organization')}
              ariaLabel={t('channels.integrations.connectFor.sharedWith')}
            />
            <p className="mt-1 text-[10.5px] leading-relaxed text-text-muted">
              {draft.entity ? t('channels.integrations.connectFor.entityHint') : t('channels.integrations.connectFor.orgHint')}
            </p>
          </div>
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
