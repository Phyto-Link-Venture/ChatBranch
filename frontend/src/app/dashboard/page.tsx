'use client'

import { useEffect, useMemo, useState } from 'react'
import { useRouter } from 'next/navigation'
import {
  Phone,
  Users,
  ChevronRight,
  Facebook,
  Instagram,
  Mail,
  MessageCircle,
  MessagesSquare,
  Mic,
  RefreshCw,
  Send,
  UserCheck,
  UserRound,
  type LucideIcon,
} from 'lucide-react'
import { useApp } from '@/lib/AppContext'
import { authHeaders } from '@/lib/auth'
import {
  checkChannelHealth,
  fetchChannelHealth,
  isDown,
  relTime,
  HEALTH_META,
  type ChannelHealthPayload,
  type HealthStatus,
} from '@/lib/channelHealth'
import { useRole } from '@/lib/useRole'
import { useEntity, useEntityQuery } from '@/lib/EntityContext'
import { initials, primaryRecord } from '@/lib/mocks'
import { StatusBadge } from '@/components/ui/Badges'
import { RecoveryBanner } from '@/components/settings/RoutingRulesSettings'
import { tint } from '@/lib/color'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

interface EngagementStats {
  threads_total: number
  awaiting_reply: number
  replied: number
  calls_total: number
  needs_human: number
  followups_set: number
}

/* Channel-Health "fan" tab metadata: entity color + short code + icon. */
const CH_TAB: Record<string, { lbl: string; code: string; color: string; Icon: LucideIcon }> = {
  whatsapp: { lbl: 'WhatsApp', code: 'WA', color: 'rgb(var(--c-green))', Icon: MessageCircle },
  telegram: { lbl: 'Telegram', code: 'TG', color: '#229ED9', Icon: Send },
  facebook: { lbl: 'Messenger', code: 'FB', color: 'rgb(var(--c-brand))', Icon: Facebook },
  instagram: { lbl: 'Instagram', code: 'IG', color: '#E1306C', Icon: Instagram },
  email: { lbl: 'Email', code: 'EM', color: 'rgb(var(--c-amber))', Icon: Mail },
  voice: { lbl: 'Voice', code: 'VC', color: 'var(--purple-ink)', Icon: Mic },
}
const CH_ORDER = ['whatsapp', 'telegram', 'facebook', 'instagram', 'email', 'voice']

interface ChAccount {
  key: string
  name: string
  health: HealthStatus
  detail?: string
  meta?: string
}

/**
 * Executive dashboard: headline numbers + live engagement stats in one card,
 * live Channel Health as a folder-tab fan in another, and the open-contacts
 * list below. Live data preserved: channel-health polling, the engagement
 * endpoint, and the contact book.
 */
export default function DashboardPage() {
  const router = useRouter()
  const { contacts } = useApp()
  const { role } = useRole()
  // Entity scope from the topbar switcher. The contact list already narrows
  // via AppContext; the health card and engagement stats follow the same
  // scope so the dashboard never mixes one outlet's numbers with another's.
  const entityQ = useEntityQuery()
  const { activeEntity } = useEntity()

  const [engagement, setEngagement] = useState<EngagementStats | null>(null)
  const [health, setHealth] = useState<ChannelHealthPayload | null>(null)
  const [healthFailed, setHealthFailed] = useState(false)
  const [healthChecking, setHealthChecking] = useState(false)
  const [activeCh, setActiveCh] = useState<string | null>(null)

  const runHealthCheck = async () => {
    setHealthChecking(true)
    try {
      setHealth(await checkChannelHealth(entityQ))
      setHealthFailed(false)
    } catch {
      setHealthFailed(true)
    } finally {
      setHealthChecking(false)
    }
  }

  useEffect(() => {
    let cancelled = false
    const poll = async () => {
      try {
        const data = await fetchChannelHealth(entityQ)
        if (!cancelled) {
          setHealth(data)
          setHealthFailed(false)
        }
      } catch {
        if (!cancelled) setHealthFailed(true)
      }
    }
    poll()
    const t = setInterval(poll, 60_000)
    return () => {
      cancelled = true
      clearInterval(t)
    }
  }, [entityQ])

  useEffect(() => {
    fetch(`${API}/messaging/engagement${entityQ && `?${entityQ}`}`, { headers: authHeaders() })
      .then((r) => (r.ok ? r.json() : null))
      .then((data) => setEngagement(data?.stats ?? null))
      .catch(() => {})
  }, [entityQ])

  const activeN = contacts.filter((d) => d.status === 'active').length
  const openContacts = useMemo(() => contacts.filter((d) => d.status !== 'resolved'), [contacts])

  // Group live channel-health into fan tabs (one per channel present).
  const channels = useMemo(() => {
    if (!health) return [] as { key: string; accounts: ChAccount[]; down: boolean }[]
    const out: { key: string; accounts: ChAccount[]; down: boolean }[] = []
    for (const key of CH_ORDER) {
      if (key === 'email') {
        out.push({
          key,
          down: health.email.configured && isDown(health.email.health),
          accounts: [
            {
              key: 'email',
              name: 'Email (SMTP)',
              health: health.email.configured ? health.email.health : 'unknown',
              detail: health.email.detail,
              meta: health.email.configured ? undefined : 'Not set up',
            },
          ],
        })
      } else if (key === 'voice') {
        if (!health.voice) continue
        out.push({
          key,
          down: health.voice.configured && isDown(health.voice.health),
          accounts: [
            {
              key: 'voice',
              name: 'Voice (Twilio SIP)',
              health: health.voice.configured ? health.voice.health : 'unknown',
              detail: health.voice.detail,
              meta: health.voice.configured ? undefined : 'Not set up',
            },
          ],
        })
      } else {
        const accts = health.accounts.filter((a) => a.channel === key)
        if (accts.length === 0) continue
        out.push({
          key,
          down: accts.some((a) => isDown(a.health)),
          accounts: accts.map((a) => ({
            key: `${a.channel}:${a.id}`,
            name: a.name,
            health: a.health as HealthStatus,
            detail: a.detail,
            meta: a.channel === 'whatsapp' ? (a.provider === 'baileys' ? 'Baileys' : 'Cloud') : undefined,
          })),
        })
      }
    }
    return out
  }, [health])

  useEffect(() => {
    if (channels.length && (!activeCh || !channels.some((c) => c.key === activeCh))) {
      setActiveCh(channels[0].key)
    }
  }, [channels, activeCh])

  const stats: { label: string; value: string; sub: string; color: string; Icon: LucideIcon; href: string }[] = [
    { label: 'Open Contacts', value: String(openContacts.length), sub: 'not yet resolved', color: 'var(--brand-ink)', Icon: Users, href: '/contacts' },
    { label: 'Active Calls', value: String(activeN), sub: activeN + ' in progress now', color: 'var(--green-ink)', Icon: Phone, href: '/chats' },
    { label: 'Awaiting Reply', value: String(engagement?.awaiting_reply ?? '—'), sub: 'threads waiting on us', color: 'var(--amber-ink)', Icon: MessagesSquare, href: '/chats' },
    { label: 'Needs Human', value: String(engagement?.needs_human ?? '—'), sub: 'flagged for review', color: 'var(--red-ink)', Icon: UserRound, href: '/chats' },
  ]

  const engagementStats = [
    { lbl: 'Threads', val: String(engagement?.threads_total ?? '—'), color: 'var(--brand-ink)', Icon: MessagesSquare },
    { lbl: 'Replied', val: String(engagement?.replied ?? '—'), color: 'var(--green-ink)', Icon: UserCheck },
    { lbl: 'Calls', val: String(engagement?.calls_total ?? '—'), color: 'var(--purple-ink)', Icon: Phone },
    { lbl: 'Follow-ups set', val: String(engagement?.followups_set ?? '—'), color: 'var(--amber-ink)', Icon: MessagesSquare },
  ]

  const activeChannel = channels.find((c) => c.key === activeCh) ?? null

  return (
    <div className="flex flex-col gap-3.5">
      {/* Header */}
      <div>
        <h1 className="text-[20px] font-extrabold tracking-[-0.5px] text-text-heading">Overview</h1>
        <div className="mt-0.5 text-[12px] text-text-muted">
          {openContacts.length} open {openContacts.length === 1 ? 'contact' : 'contacts'}
          {health?.checked_at && <span className="text-text-faint"> · updated {relTime(health.checked_at)}</span>}
        </div>
      </div>

      {/* Row: [stats + engagement] | [channel health] */}
      <div className="grid grid-cols-1 items-stretch gap-3.5 lg:grid-cols-[1.6fr_1fr]">
        {/* Left card: stat grid + engagement summary */}
        <div className="flex flex-col rounded-2xl border border-line bg-card p-4">
          <div className="grid grid-cols-2 gap-2.5">
            {stats.map((s) => (
              <div
                key={s.label}
                onClick={() => router.push(s.href)}
                role="link"
                tabIndex={0}
                onKeyDown={(e) => e.key === 'Enter' && router.push(s.href)}
                className="cursor-pointer rounded-2xl border border-line bg-well px-4 py-3.5 transition-colors hover:border-line-strong"
              >
                <div className="mb-2 flex items-start justify-between">
                  <span className="text-[10.5px] font-semibold uppercase tracking-[0.06em] text-text-muted">
                    {s.label}
                  </span>
                  <span className="grid h-[28px] w-[28px] place-items-center rounded-lg" style={{ background: tint(s.color, '1A') }}>
                    <s.Icon size={14} strokeWidth={1.85} style={{ color: s.color }} />
                  </span>
                </div>
                <div className="text-[22px] font-extrabold tracking-[-0.7px] text-text-primary">{s.value}</div>
                <div className="mt-0.5 flex items-center gap-1.5 text-[11px] font-medium text-text-faint">
                  {s.sub}
                </div>
              </div>
            ))}
          </div>

          {/* Divider + engagement stats */}
          <div className="my-3.5 border-t border-line" />
          <div className="flex items-center gap-2">
            <div className="min-w-0 flex-1">
              <div className="text-[13px] font-bold text-text-high">Engagement</div>
              <div className="truncate text-[11px] text-text-muted">
                Conversation volume &amp; response follow-through
              </div>
            </div>
          </div>

          <div className="mt-3 grid grid-cols-2 gap-2.5 lg:grid-cols-4">
            {engagementStats.map((s) => (
              <div
                key={s.lbl}
                onClick={() => router.push('/chats')}
                role="link"
                tabIndex={0}
                onKeyDown={(e) => e.key === 'Enter' && router.push('/chats')}
                className="cursor-pointer rounded-xl border border-line bg-well px-3 py-2.5 transition-colors hover:border-line-strong"
              >
                <div className="mb-1 flex items-center gap-1.5">
                  <span className="grid h-6 w-6 place-items-center rounded-md" style={{ background: tint(s.color, '1A') }}>
                    <s.Icon size={12} strokeWidth={1.85} style={{ color: s.color }} />
                  </span>
                </div>
                <div className="text-[17px] font-extrabold leading-tight tracking-[-0.5px] text-text-primary">{s.val}</div>
                <div className="truncate text-[10px] text-text-muted">{s.lbl}</div>
              </div>
            ))}
          </div>
        </div>

        {/* Right card: Channel Health folder-tab fan */}
        <div className="flex min-w-0 flex-col">
          <div className="mb-2 flex items-center gap-2">
            <span className="text-[13px] font-bold text-text-high">Channel Health</span>
            <div className="ml-auto flex items-center gap-1.5">
              {role !== 'agent' && (
                <button
                  onClick={runHealthCheck}
                  disabled={healthChecking}
                  className="flex cursor-pointer items-center gap-1.5 rounded-lg border border-line bg-fill px-2.5 py-1.5 text-[11.5px] font-semibold text-text-mid transition-colors hover:bg-fill-hover disabled:opacity-60"
                >
                  <RefreshCw size={12} strokeWidth={1.75} className={healthChecking ? 'animate-spin' : ''} />
                  {healthChecking ? 'Checking…' : 'Check now'}
                </button>
              )}
              <button
                onClick={() => router.push('/settings/channels')}
                className="flex cursor-pointer items-center gap-1 rounded-lg border border-line bg-fill px-2.5 py-1.5 text-[11.5px] font-semibold text-text-mid transition-colors hover:bg-fill-hover"
              >
                Manage
                <ChevronRight size={12} strokeWidth={1.75} />
              </button>
            </div>
          </div>

          {!health ? (
            <div className="flex-1 rounded-2xl border border-line bg-card px-4 py-6 text-[12px] text-text-muted">
              {healthFailed ? 'Channel health unavailable — is the backend API running?' : 'Checking channel health…'}
            </div>
          ) : channels.length === 0 ? (
            <div className="flex-1 rounded-2xl border border-line bg-card px-4 py-6 text-[12px] text-text-muted">
              No channel accounts linked yet — connect one in Settings.
            </div>
          ) : (
            <div className="flex min-h-0 flex-1 flex-col">
              {/* Fan tabs */}
              <div className="flex items-end px-1">
                {channels.map((c, i) => {
                  const meta = CH_TAB[c.key]
                  const active = c.key === activeCh
                  return (
                    <button
                      key={c.key}
                      onClick={() => setActiveCh(c.key)}
                      title={meta.lbl}
                      className={`fan-tab flex items-center gap-1.5 px-3 py-2 text-[11px] font-bold ${active ? 'active' : ''}`}
                      style={{
                        marginLeft: i === 0 ? 0 : -6,
                        zIndex: active ? 40 : channels.length - i,
                        color: active ? 'var(--text-high)' : 'var(--text-muted)',
                      }}
                    >
                      <span className="h-2 w-2 shrink-0 rounded-full" style={{ background: c.down ? 'var(--red-ink)' : meta.color }} />
                      {meta.code}
                    </button>
                  )
                })}
              </div>

              {/* Panel below tabs (square top-left so it butts the leftmost tab) */}
              <div className="min-h-0 flex-1 rounded-b-2xl rounded-tr-2xl border border-line bg-panel p-3">
                <RecoveryBanner className="mb-2.5" />
                <div className="flex flex-col gap-2">
                  {activeChannel?.accounts.map((a) => {
                    const m = HEALTH_META[a.health] || HEALTH_META.unknown
                    return (
                      <div
                        key={a.key}
                        title={a.detail || undefined}
                        className="flex items-center gap-2.5 rounded-xl border border-line bg-well px-3 py-2.5"
                      >
                        <span className="h-2.5 w-2.5 shrink-0 rounded-full" style={{ background: m.dot }} />
                        <div className="min-w-0 flex-1">
                          <div className="flex items-center gap-1.5">
                            <span className="truncate text-[12.5px] font-bold text-text-high">{a.name}</span>
                            {a.meta && (
                              <span className="shrink-0 rounded-full bg-fill px-1.5 py-px text-[9px] font-bold uppercase text-text-muted">
                                {a.meta}
                              </span>
                            )}
                          </div>
                          <div className="text-[10.5px]">
                            <span className="font-semibold" style={{ color: m.ink }}>
                              {a.meta === 'Not set up' ? 'Not set up' : m.lbl}
                            </span>
                          </div>
                        </div>
                      </div>
                    )
                  })}
                </div>
              </div>
            </div>
          )}
        </div>
      </div>

      {/* Open contacts — full width list */}
      <div className="flex min-h-0 flex-1 flex-col overflow-hidden rounded-2xl border border-line bg-card">
        <div className="flex items-center justify-between border-b border-line px-5 py-3.5">
          <div>
            <div className="mb-0.5 flex items-center gap-2">
              <span className="text-[13px] font-bold text-text-high">Open Contacts</span>
              <span className="rounded-full bg-red/[0.13] px-2 py-px text-[11px] font-bold text-red-300">
                {openContacts.length}
              </span>
            </div>
            <div className="text-[11px] text-text-faint">Not yet resolved</div>
          </div>
          <button
            onClick={() => router.push('/contacts')}
            className="flex cursor-pointer items-center gap-1.5 rounded-lg border border-line bg-fill px-3 py-1.5 text-[12px] font-semibold text-text-mid transition-colors hover:bg-fill-hover"
          >
            View all
            <ChevronRight size={12} strokeWidth={1.75} />
          </button>
        </div>
        <div className="min-h-0 flex-1 overflow-y-auto pr-1">
          {openContacts.map((d) => {
            const record = primaryRecord(d)
            return (
              <div
                key={d.id}
                className="trow flex cursor-pointer items-center gap-3 border-b border-line px-5 py-[11px]"
                onClick={() => router.push(`/contacts/${d.id}`)}
              >
                <div
                  className="grid h-[30px] w-[30px] shrink-0 place-items-center rounded-full text-[11px] font-bold text-white"
                  style={{ background: 'var(--primary)' }}
                >
                  {initials(d.name)}
                </div>
                <div className="min-w-0 flex-1">
                  <div className="truncate text-[13px] font-semibold text-text-high">{d.name}</div>
                  <div className="text-[11px] text-text-muted">{d.phone}</div>
                </div>
                <div className="hidden w-40 shrink-0 text-right text-[11px] text-text-muted sm:block">
                  {record ? `${record.type} · ${record.status}` : 'No records'}
                </div>
                <div className="w-24 shrink-0 text-right">
                  <StatusBadge status={d.status} />
                </div>
              </div>
            )
          })}
        </div>
      </div>
    </div>
  )
}
