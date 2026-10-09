'use client'

import { useCallback, useEffect, useState } from 'react'
import Link from 'next/link'
import { AlertCircle, BookUser, Check, Loader2, Plug } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Card, CardTitle } from '@/components/ui/Card'
import { ChannelLogo } from '@/components/ui/ChannelLogo'
import { expireSessionOn401 } from '@/lib/auth'
import { roleHeaders } from '@/lib/role'
import { useAccess } from '@/lib/useAccess'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

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
  const [mine, setMine] = useState<Mine | null>(null)
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')
  const [busy, setBusy] = useState('')
  const { ready, can } = useAccess()

  const load = useCallback(async () => {
    try {
      setMine(await api<Mine>('mine'))
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Your connections are unavailable — is the API running?')
    }
  }, [])

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
    if (ok) setNotice(`Connected your ${ok}.`)
    if (bad) setError(bad === 'access_denied' ? 'Google sign-in was cancelled — nothing was connected.' : bad)
    if (ok || bad) window.history.replaceState({}, '', window.location.pathname)
  }, [])

  const connect = async (p: Product) => {
    setError('')
    setNotice('')
    setBusy(`connect-${p.provider}`)
    try {
      const d = await api<{ authorize_url?: string; configured?: boolean; reason?: string }>(
        `oauth/${p.provider}/start?return=connections`,
      )
      if (d.configured === false || !d.authorize_url) {
        setError(d.reason || `${p.label} isn't available yet — ask an admin to register the Google client.`)
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
      setNotice(`Disconnected your ${p.label}.`)
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

      <Card className="p-4 sm:p-5">
        <CardTitle right={<span className="text-text-muted">Only you</span>}>My Google account</CardTitle>
        <p className="mt-1 text-[12.5px] text-text-muted">
          Connect your own Google products. Each grant is yours alone — other people keep using their own or the shared
          connection, and you can disconnect at any time.
        </p>
        {mine && !mine.google_configured && (
          <p className="mt-3 rounded-lg bg-fill px-3 py-2 text-[12.5px] text-text-mid">
            Google sign-in isn&rsquo;t set up for this workspace yet. A super admin registers the Google client once
            (Settings → App &amp; provider keys); then everyone connects their own account in one click.
          </p>
        )}
        <div className="mt-3 divide-y divide-line border-t border-line">
          {mine === null ? (
            <div className="flex items-center gap-2 py-4 text-[12.5px] text-text-muted">
              <Loader2 size={14} className="animate-spin" aria-hidden /> Loading…
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
                          Connected
                        </span>
                      )}
                      {lapsed && (
                        <span
                          className="rounded-full bg-amber/10 px-2 py-px text-[10px] font-bold text-amber-300"
                          title={p.account?.status_detail || undefined}
                        >
                          {p.account?.status === 'revoked' ? 'Disconnected' : 'Needs reconnecting'}
                        </span>
                      )}
                    </div>
                    <div className="text-[12px] leading-snug text-text-muted">{p.used_for || p.blurb}</div>
                  </div>
                  <div className="flex shrink-0 gap-2">
                    {on ? (
                      <>
                        <Button
                          variant="secondary"
                          size="sm"
                          disabled={!!busy || !mine.google_configured}
                          onClick={() => void connect(p)}
                          title="Sign in again (e.g. to switch Google account)"
                        >
                          Reconnect
                        </Button>
                        <Button variant="ghost" size="sm" disabled={!!busy} onClick={() => void disconnect(p)}>
                          {busy === `disconnect-${p.provider}` && <Loader2 size={13} className="animate-spin" aria-hidden />}
                          Disconnect
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
                        Connect
                      </Button>
                    )}
                  </div>
                </div>
              )
            })
          )}
        </div>
      </Card>

      <p className="text-[12.5px] text-text-muted">
        Shared connections for the whole organization or an outlet are managed by admins on{' '}
        {ready && can('integrations.view') ? (
          <Link href="/integrations" className="font-semibold text-text-high hover:underline">
            Integrations
          </Link>
        ) : (
          'Integrations'
        )}
        .
      </p>
    </div>
  )
}
