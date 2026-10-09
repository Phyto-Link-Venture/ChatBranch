'use client'

import Link from 'next/link'
import { useRouter } from 'next/navigation'
import { Card, CardTitle } from '@/components/ui/Card'
import { ChannelLogo, channelLabel } from '@/components/ui/ChannelLogo'
import { secondsSince, useLive } from '@/components/shell/liveStore'
import { useFormat, useT, type TFunction } from '@/i18n'

/** "just now" / "12 min" / "3 h" / "2 d" — how long someone has waited. */
function waitAge(seconds: number, t: TFunction): string {
  if (!Number.isFinite(seconds) || seconds < 60) return t('analytics.units.justNow')
  const m = Math.floor(seconds / 60)
  if (m < 60) return t('analytics.units.ageMinutes', { n: m })
  const h = Math.floor(m / 60)
  if (h < 24) return t('analytics.units.ageHours', { n: h })
  return t('analytics.units.ageDays', { n: Math.floor(h / 24) })
}

const initials = (name: string) =>
  name
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((w) => w[0]?.toUpperCase() ?? '')
    .join('') || '?'

/**
 * Waiting for a reply: conversations whose newest message is the customer's,
 * the longest wait first. Same live data as the top bar's Live chip (one
 * shared poll + the inbox stream), so the two always agree.
 */
export function WaitingCard({ entityQ, className = '' }: { entityQ: string; className?: string }) {
  const t = useT()
  const f = useFormat()
  const router = useRouter()
  const { data, failed } = useLive(entityQ)
  const rows = (data?.waiting ?? []).slice(0, 6)

  return (
    <Card className={`flex flex-col gap-1 px-5 py-[18px] ${className}`}>
      <CardTitle
        className="mb-2"
        right={
          <Link href="/chats" className="font-semibold text-[color:var(--brand-ink)] hover:underline">
            {t('analytics.waiting.open')}
          </Link>
        }
      >
        {t('analytics.waiting.title')}
        {!!data?.waiting_count && (
          <span className="ml-2 font-sans text-[13px] font-semibold tabular-nums text-text-muted">
            {f.number(data.waiting_count)}
          </span>
        )}
      </CardTitle>

      {!data ? (
        <p className="py-3 text-[13px] text-text-muted">
          {failed ? t('analytics.waiting.unavailable') : t('common.status.loading')}
        </p>
      ) : rows.length === 0 ? (
        <p className="py-3 text-[13px] text-text-muted">{t('analytics.waiting.nobody')}</p>
      ) : (
        <ul className="flex flex-col">
          {rows.map((w) => {
            const secs = secondsSince(w.waiting_since)
            return (
              <li key={`${w.channel}:${w.key}`} className="border-b border-line last:border-b-0">
                <button
                  type="button"
                  onClick={() => router.push(w.url)}
                  className="flex w-full cursor-pointer items-center gap-2.5 py-2 text-left text-[13px] transition-colors hover:bg-fill-subtle"
                >
                  <span className="grid h-7 w-7 shrink-0 place-items-center rounded-full bg-[var(--nav-active-bg)] text-[11px] font-semibold text-text-high">
                    {initials(w.name)}
                  </span>
                  <span className="min-w-0 flex-1 truncate">
                    <span className="font-semibold text-text-high">{w.name}</span>
                    <span className="text-text-muted"> · </span>
                    <span className="inline-flex translate-y-[2px] items-center gap-1 text-text-muted">
                      <ChannelLogo channel={w.channel} size={12} title="" />
                      {channelLabel(w.channel)}
                    </span>
                  </span>
                  <span
                    className={`shrink-0 text-[12px] font-semibold tabular-nums ${
                      secs >= 600 ? 'text-[color:var(--brand-ink)]' : 'text-text-muted'
                    }`}
                  >
                    {waitAge(secs, t)}
                  </span>
                </button>
              </li>
            )
          })}
        </ul>
      )}
    </Card>
  )
}
