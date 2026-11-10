'use client'

/**
 * Step 3 of the "Add inbox" stepper for a marketplace (Shopee, Lazada, TikTok
 * Shop, Shopify). A shop's grant lives in the seller service, never here, so
 * there is nothing to type in: this shows what the seller service holds
 * (GET /api/commerce/status) and turns its shops into inboxes
 * (POST /api/commerce/shops/sync, `channels.manage`).
 */

import { useCallback, useEffect, useState } from 'react'
import { AlertTriangle, Check, Loader2, RefreshCw } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { useAccess } from '@/lib/useAccess'
import { useT } from '@/i18n'
import { api, errText } from './inboxes'
import type { CommerceChannel } from './catalog'

export interface CommerceStatus {
  attached?: boolean
  configured?: boolean
  reason?: string
  marketplaces?: { channel: string; label?: string; carries_chat?: boolean }[]
  inboxes?: { id: number | string; channel: string; shop?: string; name?: string; health?: string; entity_id?: number | null }[]
  shops?: { channel: string; shop: string; shop_name?: string; region?: string; status?: string; in_inbox?: boolean }[]
}

export const loadCommerceStatus = () => api<CommerceStatus>('commerce/status')

export function MarketplacePanel({
  channel,
  channelName,
  onChanged,
}: {
  channel: CommerceChannel
  channelName: string
  onChanged: () => void
}) {
  const t = useT()
  const { can } = useAccess()
  const canManage = can('channels.manage')
  const [status, setStatus] = useState<CommerceStatus | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [notice, setNotice] = useState<string | null>(null)

  const load = useCallback(async () => {
    try {
      setStatus(await loadCommerceStatus())
      setError(null)
    } catch (e) {
      setStatus({})
      setError(errText(e, t))
    }
  }, [t])

  useEffect(() => {
    void load()
  }, [load])

  const sync = async () => {
    setBusy(true)
    setError(null)
    setNotice(null)
    try {
      const r = await api<{ added?: string[] }>('commerce/shops/sync', { method: 'POST', body: JSON.stringify({}) })
      setNotice(t('channels.market.synced', { count: r.added?.length ?? 0 }))
      onChanged()
      await load()
    } catch (e) {
      setError(errText(e, t))
    } finally {
      setBusy(false)
    }
  }

  const shops = (status?.shops ?? []).filter((s) => s.channel === channel)
  const carriesChat = status?.marketplaces?.find((m) => m.channel === channel)?.carries_chat ?? channel !== 'shopify'

  return (
    <div className="flex flex-col gap-4">
      <p className="text-[12.5px] leading-relaxed text-text-mid">{t('channels.market.intro', { channel: channelName })}</p>
      {!carriesChat && (
        <div
          className="flex gap-2.5 rounded-lg border border-amber/40 bg-amber/10 px-3 py-2.5 text-[12.5px] leading-relaxed"
          style={{ color: 'var(--amber-ink)' }}
        >
          <AlertTriangle size={16} className="mt-0.5 shrink-0" aria-hidden /> {t('channels.market.noChat', { channel: channelName })}
        </div>
      )}
      {error && (
        <div
          role="alert"
          className="flex items-start gap-2 rounded-lg border border-red/35 bg-red/10 px-3 py-2 text-[12.5px] font-semibold"
          style={{ color: 'var(--red-ink)' }}
        >
          <AlertTriangle size={14} className="mt-0.5 shrink-0" aria-hidden /> {error}
        </div>
      )}
      {notice && (
        <div role="status" className="flex items-center gap-1.5 text-[12.5px] font-semibold" style={{ color: 'var(--green-ink)' }}>
          <Check size={13} aria-hidden /> {notice}
        </div>
      )}
      {!status ? (
        <div className="flex items-center gap-2 text-[12.5px] text-text-muted">
          <Loader2 size={14} className="animate-spin" aria-hidden /> {t('common.status.loading')}
        </div>
      ) : (
        <>
          <div className="rounded-lg border border-line bg-well px-3 py-2 text-[12.5px]">
            {status.attached ? (
              <span style={{ color: 'var(--green-ink)' }} className="font-semibold">
                {t('channels.market.attached')}
              </span>
            ) : (
              <span style={{ color: 'var(--amber-ink)' }}>
                {t('channels.market.notAttached', { reason: status.reason || '—' })}
              </span>
            )}
          </div>
          <section className="flex flex-col gap-2">
            <h3 className="eyebrow">{t('channels.market.shops')}</h3>
            {shops.length === 0 ? (
              <p className="text-[12.5px] text-text-muted">{t('channels.market.none', { channel: channelName })}</p>
            ) : (
              <ul className="flex flex-col gap-1.5">
                {shops.map((s) => (
                  <li key={s.shop} className="flex items-center gap-2 rounded-lg border border-line px-3 py-2 text-[12.5px]">
                    <span className="min-w-0 flex-1 truncate font-semibold text-text-high">{s.shop_name || s.shop}</span>
                    {s.region && <span className="text-text-muted">{s.region}</span>}
                    <span
                      className="shrink-0 text-[11px] font-semibold"
                      style={{ color: s.in_inbox ? 'var(--green-ink)' : 'var(--text-muted)' }}
                    >
                      {s.in_inbox ? t('channels.market.inInbox') : t('channels.market.notInInbox')}
                    </span>
                  </li>
                ))}
              </ul>
            )}
          </section>
          {canManage ? (
            <div>
              <Button onClick={sync} disabled={busy || !status.configured}>
                {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <RefreshCw size={13} aria-hidden />}
                {t('channels.market.sync')}
              </Button>
            </div>
          ) : (
            <p className="text-[12px] text-text-muted">{t('channels.page.addNeedsPermission')}</p>
          )}
        </>
      )}
    </div>
  )
}
