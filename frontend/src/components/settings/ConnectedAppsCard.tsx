'use client'

import { useCallback, useEffect, useState } from 'react'
import { AlertCircle, Bot, Loader2, Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Card, CardTitle } from '@/components/ui/Card'
import { API } from '@/lib/apiBase'
import { authHeaders, expireSessionOn401 } from '@/lib/auth'
import { useFormat, useT } from '@/i18n'

/**
 * Settings → My connections → "AI apps": the apps that connected by SIGNING IN
 * rather than by being handed an API key, and the one place to end any of them.
 *
 * Each row is one person's own connection — another login's never appears here, and
 * ending one takes effect on the app's very next request. The card only shows itself
 * when there is something to show, so a ChatBranch nobody has connected an app to
 * stays quiet.
 */

interface Connection {
  id: number
  client_name: string
  read_only: boolean
  connected_at: string
  last_used_at: string | null
  expires_at: string
}

export function ConnectedAppsCard() {
  const t = useT()
  const f = useFormat()
  const [rows, setRows] = useState<Connection[] | null>(null)
  const [busy, setBusy] = useState<number | null>(null)
  const [error, setError] = useState('')

  const load = useCallback(async () => {
    try {
      const res = await fetch(`${API}/oauth/connections`, { headers: authHeaders() })
      if (expireSessionOn401(res)) return
      if (!res.ok) return
      const data = (await res.json()) as { connections: Connection[] }
      setRows(data.connections ?? [])
    } catch {
      setRows([])
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  const disconnect = async (id: number) => {
    setBusy(id)
    setError('')
    try {
      const res = await fetch(`${API}/oauth/connections/${id}`, {
        method: 'DELETE',
        headers: authHeaders(),
      })
      if (expireSessionOn401(res)) return
      if (!res.ok) throw new Error(((await res.json().catch(() => ({}))) as { detail?: string }).detail || `HTTP ${res.status}`)
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(null)
    }
  }

  // nothing connected and nothing to say: no empty card in the way
  if (rows === null || rows.length === 0) return null

  return (
    <Card className="p-4 sm:p-5">
      <CardTitle>{t('settings.connections.appsTitle')}</CardTitle>
      <p className="mt-1 text-[12.5px] text-text-muted">{t('settings.connections.appsHint')}</p>
      <ul className="mt-3 divide-y divide-line border-t border-line">
        {rows.map((row) => (
          <li key={row.id} className="flex items-center gap-3 py-3">
            <span className="grid h-8 w-8 shrink-0 place-items-center rounded-lg bg-fill text-text-high">
              <Bot size={15} aria-hidden />
            </span>
            <div className="min-w-0 flex-1">
              <div className="truncate text-[13.5px] font-semibold text-text-high">
                {row.client_name}
                <span className="ml-2 text-[11.5px] font-normal text-text-faint">
                  {row.read_only ? t('settings.connections.appsReadOnly') : t('settings.connections.appsFull')}
                </span>
              </div>
              <div className="text-[12px] leading-snug text-text-muted">
                {row.last_used_at
                  ? t('settings.connections.appsLastUsed', { when: f.relative(row.last_used_at) })
                  : t('settings.connections.appsNeverUsed')}
              </div>
            </div>
            <Button variant="ghost" size="sm" disabled={busy === row.id} onClick={() => void disconnect(row.id)}>
              {busy === row.id ? (
                <Loader2 size={13} className="animate-spin" aria-hidden />
              ) : (
                <Trash2 size={13} aria-hidden />
              )}
              {t('common.actions.disconnect')}
            </Button>
          </li>
        ))}
      </ul>
      {error && (
        <div
          role="alert"
          className="mt-3 flex items-start gap-2 rounded-lg border border-red/25 bg-red/10 px-3 py-2 text-[12.5px] text-[color:var(--red-ink)]"
        >
          <AlertCircle size={14} className="mt-0.5 shrink-0" aria-hidden /> {error}
        </div>
      )}
    </Card>
  )
}
