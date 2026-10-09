'use client'

import Link from 'next/link'
import { useEffect, useMemo, useState } from 'react'
import { RefreshCw } from 'lucide-react'
import { Card, CardTitle } from '@/components/ui/Card'
import { ChannelLogo, channelLabel } from '@/components/ui/ChannelLogo'
import { RecoveryBanner } from '@/components/settings/RoutingRulesSettings'
import {
  checkChannelHealth,
  fetchChannelHealth,
  HEALTH_META,
  relTime,
  type ChannelHealthPayload,
  type HealthStatus,
} from '@/lib/channelHealth'

interface Row {
  key: string
  channel: string
  name: string
  meta?: string
  health: HealthStatus
  detail?: string
  notSetUp?: boolean
}

const ORDER = [
  'whatsapp',
  'telegram',
  'line',
  'viber',
  'zalo',
  'facebook',
  'instagram',
  'tiktok',
  'wechat',
  'douyin',
  'website',
  'sms',
  'email',
  'voice',
]

/**
 * Channel health (moved here from the old Dashboard): every connected account
 * with its live state, the recovery banner when a conversation number is
 * down, "Check now" for admins and a link to Channels. Polls every 60 s and
 * follows the entity switcher — an outlet never sees a sibling's outage.
 */
export function ChannelHealthCard({
  entityQ,
  canCheck,
  canManage,
}: {
  entityQ: string
  /** "Check now" probes every account — admin+ server-side. */
  canCheck: boolean
  /** The caller can open Channels (channels.view). */
  canManage: boolean
}) {
  const [health, setHealth] = useState<ChannelHealthPayload | null>(null)
  const [failed, setFailed] = useState(false)
  const [checking, setChecking] = useState(false)

  useEffect(() => {
    let cancelled = false
    const poll = async () => {
      try {
        const data = await fetchChannelHealth(entityQ)
        if (!cancelled) {
          setHealth(data)
          setFailed(false)
        }
      } catch {
        if (!cancelled) setFailed(true)
      }
    }
    void poll()
    const t = setInterval(poll, 60_000)
    return () => {
      cancelled = true
      clearInterval(t)
    }
  }, [entityQ])

  const check = async () => {
    setChecking(true)
    try {
      setHealth(await checkChannelHealth(entityQ))
      setFailed(false)
    } catch {
      setFailed(true)
    } finally {
      setChecking(false)
    }
  }

  const rows = useMemo<Row[]>(() => {
    if (!health) return []
    const out: Row[] = health.accounts.map((a) => ({
      key: `${a.channel}:${a.id}`,
      channel: a.channel,
      name: a.name || channelLabel(a.channel),
      meta: a.channel === 'whatsapp' ? (a.provider === 'baileys' ? 'Personal' : 'Business') : undefined,
      health: (a.health in HEALTH_META ? a.health : 'unknown') as HealthStatus,
      detail: a.detail,
    }))
    if (health.email?.configured)
      out.push({ key: 'email', channel: 'email', name: 'Email (SMTP)', health: health.email.health, detail: health.email.detail })
    if (health.voice?.configured)
      out.push({ key: 'voice', channel: 'voice', name: 'Voice (SIP)', health: health.voice.health, detail: health.voice.detail })
    return out.sort((a, b) => ORDER.indexOf(a.channel) - ORDER.indexOf(b.channel))
  }, [health])

  const down = rows.filter((r) => r.health === 'disconnected' || r.health === 'banned').length

  return (
    <Card className="flex flex-col gap-3 px-5 py-[18px]">
      <CardTitle
        right={
          <span className="flex items-center gap-3">
            {canCheck && (
              <button
                onClick={() => void check()}
                disabled={checking}
                className="inline-flex cursor-pointer items-center gap-1 font-semibold text-text-mid transition-colors hover:text-text-high disabled:opacity-60"
              >
                <RefreshCw size={12} strokeWidth={1.9} className={checking ? 'animate-spin' : ''} aria-hidden />
                {checking ? 'Checking…' : 'Check now'}
              </button>
            )}
            {canManage && (
              <Link href="/channels" className="font-semibold text-[color:var(--brand-ink)] hover:underline">
                Manage
              </Link>
            )}
          </span>
        }
      >
        Channel health
      </CardTitle>

      <RecoveryBanner />

      {!health ? (
        <p className="py-4 text-[13px] text-text-muted">
          {failed ? 'Channel health is unavailable — is the API running?' : 'Checking channels…'}
        </p>
      ) : rows.length === 0 ? (
        <p className="py-4 text-[13px] text-text-muted">
          No channel accounts yet
          {canManage ? (
            <>
              {' '}—{' '}
              <Link href="/channels" className="font-semibold text-[color:var(--brand-ink)] hover:underline">
                connect one in Channels
              </Link>
              .
            </>
          ) : (
            ' — an administrator connects them in Channels.'
          )}
        </p>
      ) : (
        <ul className="flex flex-col">
          {rows.map((r) => {
            const m = HEALTH_META[r.health] ?? HEALTH_META.unknown
            return (
              <li
                key={r.key}
                title={r.detail || undefined}
                className="flex items-center gap-2.5 border-b border-line py-2 text-[13px] last:border-b-0"
              >
                <span className="h-2 w-2 shrink-0 rounded-full" style={{ background: m.dot }} aria-hidden />
                <ChannelLogo channel={r.channel} size={15} />
                <span className="min-w-0 flex-1 truncate text-text-high">
                  {r.name}
                  {r.meta && <span className="text-text-muted"> · {r.meta}</span>}
                </span>
                <span className="shrink-0 text-[12px] font-semibold" style={{ color: m.ink }}>
                  {m.lbl}
                </span>
              </li>
            )
          })}
        </ul>
      )}

      {health && (
        <div className="mt-auto text-[11.5px] text-text-muted">
          {down > 0 ? `${down} down · ` : ''}checked {relTime(health.checked_at)}
        </div>
      )}
    </Card>
  )
}
