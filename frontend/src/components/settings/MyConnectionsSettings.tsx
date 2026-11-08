'use client'

import { API } from '@/lib/apiBase'
import { useCallback, useEffect, useState } from 'react'
import Link from 'next/link'
import { AlertCircle, BookUser, Check, Loader2, Plug } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Card, CardTitle } from '@/components/ui/Card'
import { ChannelLogo } from '@/components/ui/ChannelLogo'
import { expireSessionOn401 } from '@/lib/auth'
import { roleHeaders } from '@/lib/role'
import { useAccess } from '@/lib/useAccess'
import { Trans, useT, type MessageKey } from '@/i18n'
import { ConnectedAppsCard } from './ConnectedAppsCard'

interface OwnAccount {
  id: number
  connected: boolean
  status: string
  status_detail?: string
  scopes: string[]
  status_changed_at?: string
}

interface Product {
  provider: string
  label: string
  blurb: string
  used_for: string
  account: OwnAccount | null
}

interface Mine {
  google_configured: boolean
  username?: string
  connections: Product[]
}

/** What a personal grant is used for, translated by provider id (the server's English is the fallback). */
const USED_FOR: Record<string, MessageKey> = {
  google_calendar: 'settings.connections.usedFor.google_calendar',
  google_contacts: 'settings.connections.usedFor.google_contacts',
  gmail: 'settings.connections.usedFor.gmail',
  google_drive: 'settings.connections.usedFor.google_drive',
  google_sheets: 'settings.connections.usedFor.google_sheets',
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

const BRANDED = new Set(['google_calendar', 'google_drive', 'gmail', 'google_sheets'])

function ProductLogo({ id }: { id: string }) {
  if (BRANDED.has(id)) return <ChannelLogo channel={id} size={32} variant="tile" title="" />
  return (
    <span className="grid h-8 w-8 shrink-0 place-items-center rounded-lg bg-fill">
      {id === 'google_contacts' ? (
        <BookUser size={16} className="text-text-muted" aria-hidden />
      ) : (
        <Plug size={16} className="text-text-muted" aria-hidden />
      )}
    </span>
  )
}

/**
 * Settings → Personal → My connections: the Google products a person connects
 * for THEMSELVES — their own calendar (booking skills set to "the assigned
 * agent's calendar" check and book into it), their contact book (Contacts →
 * Import) and their own Gmail / Drive / Sheets for actions they run. No
 * capability needed; shared, organization-wide connections stay on
 * Integrations. Connect is a full-page Google consent that returns here.
 */
export function MyConnectionsSettings() {
  const t = useT()
  const [mine, setMine] = useState<Mine | null>(null)
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')
  const [busy, setBusy] = useState('')
  const { ready, can } = useAccess()

  const load = useCallback(async () => {
    try {
      setMine(await api<Mine>('mine'))
    } catch (e) {
      setError(e instanceof Error ? e.message : t('settings.connections.unavailable'))
    }
  }, [t])

  useEffect(() => {
    void load()
  }, [load])

  // The Google callback lands back here with ?connected= / ?error= (read from
  // window, not useSearchParams, which would force a Suspense boundary).
  useEffect(() => {
    if (typeof window === 'undefined') return
    const q = new URLSearchParams(window.location.search)
    const ok = q.get('connected')
    const bad = q.get('error')
    if (ok) setNotice(t('settings.connections.connected', { product: ok }))
    if (bad) setError(bad === 'access_denied' ? t('settings.connections.cancelled') : bad)
    if (ok || bad) window.history.replaceState({}, '', window.location.pathname)
  }, [t])

  const connect = async (p: Product) => {
    setError('')
    setNotice('')
    setBusy(`connect-${p.provider}`)
    try {
      const d = await api<{ authorize_url?: string; configured?: boolean; reason?: string }>(
        `oauth/${p.provider}/start?return=connections`,
      )
      if (d.configured === false || !d.authorize_url) {
        setError(d.reason || t('settings.connections.notAvailable', { product: p.label }))
        return
      }
      window.location.href = d.authorize_url
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy('')
    }
  }

  const disconnect = async (p: Product) => {
    if (!p.account) return
    setError('')
    setNotice('')
    setBusy(`disconnect-${p.provider}`)
    try {
      await api(`accounts/${p.account.id}/disconnect`, { method: 'POST' })
      setNotice(t('settings.connections.disconnected', { product: p.label }))
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy('')
    }
  }

  if (mine === null && error) return <p className="py-6 text-[12.5px] text-text-muted">{error}</p>

  return (
    <div className="flex flex-col gap-5">
      {notice && (
        <div role="status" className="flex items-start gap-2 rounded-lg border border-green/25 bg-green/10 px-3 py-2 text-[12.5px] text-[color:var(--green-ink)]">
          <Check size={14} className="mt-0.5 shrink-0" aria-hidden /> {notice}
        </div>
      )}
      {error && mine !== null && (
        <div role="alert" className="flex items-start gap-2 rounded-lg border border-red/25 bg-red/10 px-3 py-2 text-[12.5px] text-[color:var(--red-ink)]">
          <AlertCircle size={14} className="mt-0.5 shrink-0" aria-hidden /> {error}
        </div>
      )}

      <Card className="p-4 sm:p-5" data-tour="connections-card">
        <CardTitle right={<span className="text-text-muted">{t('settings.connections.onlyYou')}</span>}>
          {t('settings.connections.title')}
        </CardTitle>
        <p className="mt-1 text-[12.5px] text-text-muted">{t('settings.connections.intro')}</p>
        {mine && !mine.google_configured && (
          <p className="mt-3 rounded-lg bg-fill px-3 py-2 text-[12.5px] text-text-mid">{t('settings.connections.notConfigured')}</p>
        )}
        <div data-tour="connections-list" className="mt-3 divide-y divide-line border-t border-line">
          {mine === null ? (
            <div className="flex items-center gap-2 py-4 text-[12.5px] text-text-muted">
              <Loader2 size={14} className="animate-spin" aria-hidden /> {t('common.status.loading')}
            </div>
          ) : (
            mine.connections.map((p) => {
              const on = !!p.account?.connected
              const lapsed = !!p.account && !on
              return (
                <div key={p.provider} className="flex flex-wrap items-center gap-3 py-3 sm:flex-nowrap">
                  <ProductLogo id={p.provider} />
                  <div className="min-w-0 flex-1 basis-56">
                    <div className="flex flex-wrap items-center gap-2">
                      <span className="text-[13.5px] font-semibold text-text-high">{p.label}</span>
                      {on && (
                        <span className="rounded-full bg-green/10 px-2 py-px text-[10px] font-bold text-green-300">
                          {t('common.status.connected')}
                        </span>
                      )}
                      {lapsed && (
                        <span
                          className="rounded-full bg-amber/10 px-2 py-px text-[10px] font-bold text-amber-300"
                          title={p.account?.status_detail || undefined}
                        >
                          {p.account?.status === 'revoked'
                            ? t('settings.connections.statusDisconnected')
                            : t('settings.connections.needsReconnect')}
                        </span>
                      )}
                    </div>
                    <div className="text-[12px] leading-snug text-text-muted">
                      {USED_FOR[p.provider] ? t(USED_FOR[p.provider]) : p.used_for || p.blurb}
                    </div>
                  </div>
                  <div data-tour="connections-actions" className="flex shrink-0 gap-2">
                    {on ? (
                      <>
                        <Button
                          variant="secondary"
                          size="sm"
                          disabled={!!busy || !mine.google_configured}
                          onClick={() => void connect(p)}
                          title={t('settings.connections.reconnectHint')}
                        >
                          {t('common.actions.reconnect')}
                        </Button>
                        <Button variant="ghost" size="sm" disabled={!!busy} onClick={() => void disconnect(p)}>
                          {busy === `disconnect-${p.provider}` && <Loader2 size={13} className="animate-spin" aria-hidden />}
                          {t('common.actions.disconnect')}
                        </Button>
                      </>
                    ) : (
                      <Button
                        variant="primary"
                        size="sm"
                        disabled={!!busy || !mine.google_configured}
                        onClick={() => void connect(p)}
                      >
                        {busy === `connect-${p.provider}` && <Loader2 size={13} className="animate-spin" aria-hidden />}
                        {t('common.actions.connect')}
                      </Button>
                    )}
                  </div>
                </div>
              )
            })
          )}
        </div>
      </Card>

      {/* AI apps that connected by signing in, rather than with a pasted key */}
      <ConnectedAppsCard />

      <p data-tour="connections-order" className="text-[12.5px] text-text-muted">
        {t('settings.connections.order')}{' '}
        <Trans
          k="settings.connections.shared"
          values={{
            link:
              ready && can('integrations.view') ? (
                <Link href="/integrations" className="font-semibold text-text-high hover:underline">
                  {t('shell.nav.integrations')}
                </Link>
              ) : (
                t('shell.nav.integrations')
              ),
          }}
        />
      </p>
    </div>
  )
}
