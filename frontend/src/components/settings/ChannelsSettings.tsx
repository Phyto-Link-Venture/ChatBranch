'use client'

/**
 * /channels — the Inboxes page, the ONE place every
 * connection is managed. Every number, mailbox, bot, page, website widget, the
 * SIP trunk, every Zernio-connected social account and every marketplace shop:
 *
 *  - Connected: one list across every channel — channel logo, method badge
 *    ("Cloud API", "YCloud", "QR", "via Zernio"…), outlet, health and
 *    capabilities. Inboxes keep Settings / Test / Pair / Delete; Zernio social
 *    accounts keep their outlet picker and Facebook Page rows
 *    (components/channels/SocialAccountCard); shops come from the seller service.
 *  - Add a channel: one tile per CHANNEL; picking one opens the "Add inbox"
 *    stepper (components/channels/InboxSetupDrawer: channel → method → setup).
 *  - Advanced (collapsed): the shared Meta webhook and default mail settings.
 *
 * Everything reads and writes the existing account endpoints (see
 * components/channels/inboxes.ts). Inboxes need `channels.manage` to change,
 * Zernio accounts `social.manage`.
 */

import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { CheckCircle, Loader2, Plug, Plus, QrCode, Settings2, Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Modal } from '@/components/ui/Modal'
import { PageHeader } from '@/components/ui/PageHeader'
import { ChannelLogo } from '@/components/ui/ChannelLogo'
import { RoleChip } from '@/components/ui/Badges'
import { hasCap } from '@/lib/auth'
import { useEntity, useEntityQuery } from '@/lib/EntityContext'
import { fetchChannelHealth, HEALTH_META, type ChannelHealthPayload, type HealthStatus } from '@/lib/channelHealth'
import { platformId } from '@/lib/social'
import { useFormat, useT, type MessageKey } from '@/i18n'
import {
  ApprovalChip,
  InboxSetupDrawer,
  KindBadge,
  type DrawerTarget,
  type Step,
} from '@/components/channels/InboxSetupDrawer'
import { ChannelsAdvanced } from '@/components/channels/ChannelsAdvanced'
import { WhatsAppConnectResult } from '@/components/channels/WhatsAppConnectResult'
import { SocialAccountCard, SocialDisconnectModal, useSocialAccounts } from '@/components/channels/SocialAccountCard'
import { ChannelTileGrid } from '@/components/channels/ChannelPicker'
import { CapChip, HealthDot, MethodBadge, OutletChip, cardBtn } from '@/components/channels/parts'
import { loadCommerceStatus, type CommerceStatus } from '@/components/channels/MarketplacePanel'
import { methodBadge, type ChannelDef } from '@/components/channels/catalog'
import {
  FILTERS,
  asFilter,
  deleteInbox,
  errText,
  filterLabel,
  inboxName,
  kindMeta,
  loadInboxes,
  testInbox,
  txt,
  type ChannelFilter,
  type Inbox,
  type InboxKind,
  type InboxesSnapshot,
  type TestResult,
} from '@/components/channels/inboxes'

type Shop = NonNullable<CommerceStatus['inboxes']>[number]

const HEALTH_IDS = new Set<string>(Object.keys(HEALTH_META))
const asHealth = (h?: string | null): HealthStatus | null => (h && HEALTH_IDS.has(h) ? (h as HealthStatus) : null)

function healthOf(inbox: Inbox, health: ChannelHealthPayload | null): { status: HealthStatus; detail: string } | null {
  if (!health) return null
  if (inbox.kind === 'voice')
    return health.voice ? { status: health.voice.health, detail: health.voice.detail } : null
  const row = health.accounts.find((a) => String(a.id) === inbox.healthId)
  return row ? { status: row.health, detail: row.detail } : null
}

/** What an inbox can do, as chips. */
function inboxCaps(inbox: Inbox): MessageKey[] {
  if (inbox.kind === 'voice') return ['channels.caps.calls']
  const caps: MessageKey[] = ['channels.caps.messages']
  if (inbox.wa?.official || inbox.wa?.template_registry) caps.push('channels.caps.templates')
  if (inbox.kind === 'wa_qr') caps.push('channels.caps.calls')
  if (inbox.wa?.recalls_supported || inbox.msg?.recalls_supported) caps.push('channels.caps.recalls')
  return caps
}

/** The result line of an inbox's Test. */
function TestLine({ test }: { test: TestResult | 'running' }) {
  const t = useT()
  return (
      <div
        role="status"
        className="rounded-lg bg-well px-2.5 py-1.5 text-[12px] leading-relaxed"
        style={{
          color:
            test === 'running'
              ? 'var(--text-muted)'
              : test.tone === 'ok'
                ? 'var(--green-ink)'
                : test.tone === 'info'
                  ? 'var(--amber-ink)'
                  : 'var(--red-ink)',
        }}
      >
        {test === 'running' ? (
          <span className="inline-flex items-center gap-1.5">
            <Loader2 size={12} className="animate-spin" aria-hidden /> {t('channels.card.testing')}
          </span>
        ) : (
          test.text
        )}
      </div>
  )
}

function InboxCard({
  inbox,
  health,
  teamName,
  canManage,
  canKeys,
  test,
  onSettings,
  onTest,
  onPair,
  onDelete,
}: {
  inbox: Inbox
  health: { status: HealthStatus; detail: string } | null
  teamName?: string
  canManage: boolean
  canKeys: boolean
  test?: TestResult | 'running'
  onSettings: () => void
  onTest: () => void
  onPair: () => void
  onDelete: () => void
}) {
  const t = useT()
  const meta = kindMeta(inbox.kind)
  const name = inboxName(inbox, t)
  const editable = canManage && !inbox.envManaged && (inbox.kind !== 'voice' || canKeys)
  const btn = cardBtn
  const badge = methodBadge(inbox)
  return (
    <article className="flex min-w-0 flex-col gap-3 rounded-xl border border-line bg-card p-4">
      <div className="flex items-start gap-3">
        <ChannelLogo channel={meta.logo} variant="tile" size={36} title="" />
        <div className="min-w-0 flex-1">
          <div className="flex min-w-0 flex-wrap items-center gap-1.5">
            <span className="truncate text-[14px] font-bold text-text-heading">{name}</span>
            {badge && <MethodBadge label={badge} zernio={inbox.viaZernio} />}
            {inbox.role && <RoleChip role={inbox.role} />}
            {meta.badge && <KindBadge badge={meta.badge} />}
            {meta.approval && <ApprovalChip compact />}
            {inbox.envManaged && (
              <span className="rounded-full bg-fill px-1.5 py-px text-[9.5px] font-bold uppercase text-text-muted">
                {t('channels.card.fromEnv')}
              </span>
            )}
          </div>
          <div className="truncate text-[12px] text-text-muted">
            {/* a managed WhatsApp number isn't on Meta's Cloud API from the operator's side — just "WhatsApp" */}
            {inbox.viaZernio && meta.filter === 'whatsapp' ? 'WhatsApp' : t(meta.channelLabel)} · {txt(inbox.address, t)}
          </div>
        </div>
      </div>

      <div className="flex flex-wrap items-center gap-x-2 gap-y-1 text-[12px] text-text-muted">
        {health ? <HealthDot status={health.status} detail={health.detail} /> : <span>{t('channels.card.notMonitored')}</span>}
        {inbox.kind !== 'voice' && (
          <>
            <span aria-hidden>·</span>
            <span data-tour="channels-card-outlet" className="min-w-0">
              <OutletChip name={inbox.entityName || t('channels.card.allEntities')} />
            </span>
            {teamName && (
              <>
                <span aria-hidden>·</span>
                <span className="truncate">{teamName}</span>
              </>
            )}
            {inbox.aiDefault && (
              <>
                <span aria-hidden>·</span>
                <span>{t(inbox.aiDefault === 'on' ? 'channels.card.aiOn' : 'channels.card.aiOff')}</span>
              </>
            )}
          </>
        )}
      </div>

      <div className="flex flex-wrap gap-1" aria-label={t('channels.card.capabilities')}>
        {inboxCaps(inbox).map((c) => (
          <CapChip key={c} label={t(c)} />
        ))}
      </div>

      {test && <TestLine test={test} />}

      <div data-tour="channels-card-actions" className="mt-auto flex flex-wrap items-center gap-2 pt-0.5">
        {editable && (
          <button type="button" className={btn} onClick={onSettings}>
            <Settings2 size={13} aria-hidden /> {t('channels.card.settings')}
          </button>
        )}
        <button type="button" className={btn} onClick={onTest} disabled={test === 'running'}>
          <Plug size={13} aria-hidden /> {t('channels.card.test')}
        </button>
        {editable && inbox.kind === 'wa_qr' && (
          <button type="button" className={btn} onClick={onPair}>
            <QrCode size={13} aria-hidden /> {t('channels.card.pair')}
          </button>
        )}
        {editable && (
          <button
            type="button"
            onClick={onDelete}
            className="ml-auto inline-flex h-8 cursor-pointer items-center gap-1.5 rounded-lg px-2.5 text-[12.5px] font-semibold transition-colors hover:bg-red/10"
            style={{ color: 'var(--red-ink)' }}
            aria-label={t('channels.card.deleteNamed', { name })}
          >
            <Trash2 size={13} aria-hidden /> {t('common.actions.delete')}
          </button>
        )}
      </div>
    </article>
  )
}

/** A marketplace shop (the seller service holds its grant — nothing to edit here). */
function ShopCard({ shop, health, outlet }: { shop: Shop; health: { status: HealthStatus; detail: string } | null; outlet: string }) {
  const t = useT()
  return (
    <article className="flex min-w-0 flex-col gap-3 rounded-xl border border-line bg-card p-4">
      <div className="flex items-start gap-3">
        <ChannelLogo channel={shop.channel} variant="tile" size={36} title="" />
        <div className="min-w-0 flex-1">
          <div className="flex min-w-0 flex-wrap items-center gap-1.5">
            <span className="truncate text-[14px] font-bold text-text-heading">{shop.name || shop.shop || String(shop.id)}</span>
            <MethodBadge label="channels.methods.badges.seller" />
            <KindBadge badge="Beta" />
          </div>
          <div className="truncate text-[12px] text-text-muted">{shop.shop}</div>
        </div>
      </div>
      <div className="flex flex-wrap items-center gap-x-2 gap-y-1 text-[12px] text-text-muted">
        {health ? <HealthDot status={health.status} detail={health.detail} /> : <span>{t('channels.card.notMonitored')}</span>}
        <span aria-hidden>·</span>
        <OutletChip name={outlet} />
      </div>
      <div className="flex flex-wrap gap-1" aria-label={t('channels.card.capabilities')}>
        <CapChip label={t(shop.channel === 'shopify' ? 'channels.caps.noChat' : 'channels.caps.buyerChat')} />
      </div>
    </article>
  )
}

const DELETE_NOTE: Partial<Record<InboxKind, MessageKey>> = {
  voice: 'channels.page.deleteVoice',
  website: 'channels.page.deleteWebsite',
  wa_qr: 'channels.page.deleteQr',
}

/** One card in the Connected list. */
type Row =
  | { type: 'inbox'; key: string; filter: ChannelFilter | null; inbox: Inbox }
  | {
      type: 'social'
      key: string
      filter: ChannelFilter | null
      account: NonNullable<ReturnType<typeof useSocialAccounts>['accounts']>[number]
      /** The account's DM inbox, shown inside its card. */
      inbox?: Inbox
    }
  | { type: 'shop'; key: string; filter: ChannelFilter | null; shop: Shop }

export function ChannelsSettings() {
  const t = useT()
  const tRef = useRef(t)
  tRef.current = t
  const f = useFormat()
  const { entities } = useEntity()
  const [snap, setSnap] = useState<InboxesSnapshot | null>(null)
  const [health, setHealth] = useState<ChannelHealthPayload | null>(null)
  const [shops, setShops] = useState<Shop[]>([])
  const [filter, setFilter] = useState<ChannelFilter>('all')
  const [drawer, setDrawer] = useState<(DrawerTarget & { id: number }) | null>(null)
  const [tests, setTests] = useState<Record<string, TestResult | 'running'>>({})
  const [toDelete, setToDelete] = useState<Inbox | null>(null)
  const [deleting, setDeleting] = useState(false)
  const [deleteError, setDeleteError] = useState<string | null>(null)
  const [pageMsg, setPageMsg] = useState<string | null>(null)
  const [pageError, setPageError] = useState<string | null>(null)
  // capabilities read after mount (localStorage — see CLAUDE.md hydration rule)
  const [canManage, setCanManage] = useState(false)
  const [canKeys, setCanKeys] = useState(false)
  const [canSocial, setCanSocial] = useState(false)
  useEffect(() => {
    setCanManage(hasCap('channels.manage'))
    // the SIP trunk and the default mail settings are provider credentials (app_keys.manage)
    setCanKeys(hasCap('app_keys.manage'))
    // Zernio connections (social networks and "via Zernio" methods)
    setCanSocial(hasCap('social.manage'))
  }, [])

  const social = useSocialAccounts()
  const reloadSocial = social.load

  const entityQ = useEntityQuery()
  const reload = useCallback(async () => {
    const [s, h, c] = await Promise.allSettled([loadInboxes(entityQ), fetchChannelHealth(entityQ), loadCommerceStatus()])
    if (s.status === 'fulfilled') setSnap(s.value)
    setHealth(h.status === 'fulfilled' ? h.value : null)
    // no seller service (or an older backend) simply means no shops
    setShops(c.status === 'fulfilled' ? (c.value.inboxes ?? []) : [])
  }, [entityQ])

  useEffect(() => {
    reload()
  }, [reload])

  // Zernio's connect page sends the person back with ?zernio=connected (or an error)
  useEffect(() => {
    if (typeof window === 'undefined') return
    const q = new URLSearchParams(window.location.search)
    const z = q.get('zernio')
    if (!z) return
    if (z === 'connected') setPageMsg(tRef.current('social.accounts.connectedNotice'))
    else setPageError(tRef.current('social.accounts.connectFailed', { reason: q.get('error') || z }))
    q.delete('zernio')
    q.delete('error')
    const rest = q.toString()
    window.history.replaceState({}, '', `${window.location.pathname}${rest ? `?${rest}` : ''}`)
  }, [])

  const inboxes = useMemo(() => snap?.inboxes ?? [], [snap])
  const rows = useMemo<Row[]>(() => {
    const out: Row[] = inboxes.map((inbox) => ({ type: 'inbox', key: inbox.key, filter: kindMeta(inbox.kind).filter, inbox }))
    for (const a of social.accounts ?? []) {
      const pid = platformId(a.platform)
      // the same managed account's DM inbox (Instagram / Messenger) joins its card: one card per account
      const at = out.findIndex(
        (r) => r.type === 'inbox' && !!r.inbox.viaZernio && (r.inbox.msg?.zernio_account_id ?? r.inbox.wa?.zernio_account_id) === a.id,
      )
      const linked = at >= 0 ? (out.splice(at, 1)[0] as Extract<Row, { type: 'inbox' }>).inbox : undefined
      // our Facebook filter is Messenger's for DMs; a Facebook account is its Page's posts / comments
      out.push({ type: 'social', key: `social:${a.id}`, filter: asFilter(pid), account: a, inbox: linked })
    }
    for (const shop of shops) out.push({ type: 'shop', key: `shop:${shop.id}`, filter: asFilter(shop.channel), shop })
    return out
  }, [inboxes, social.accounts, shops])

  const counts = useMemo(() => {
    const c: Partial<Record<ChannelFilter, number>> = { all: rows.length }
    for (const r of rows) if (r.filter) c[r.filter] = (c[r.filter] ?? 0) + 1
    return c
  }, [rows])
  const visible = filter === 'all' ? rows : rows.filter((r) => r.filter === filter)
  const voiceInbox = inboxes.find((i) => i.kind === 'voice') ?? null
  const teamName = (id: number | null) => (id == null ? undefined : snap?.teams.find((tm) => tm.id === id)?.name)
  const activeFilter = FILTERS.find((x) => x.id === filter)
  const canAdd = canManage || canSocial
  const loaded = !!snap && social.accounts !== null

  const open = (kind: InboxKind | null, inbox: Inbox | null, step: Step, channel: string | null = null) =>
    setDrawer({ kind, inbox, step, channel, id: Date.now() })

  /** A catalog tile: the stepper opens on that channel (its methods, or its only method's setup). */
  const openChannel = (id: string) => {
    if (id === 'voice' && voiceInbox) open('voice', voiceInbox, 2)
    else open(null, null, 1, id)
  }

  /** Why this person can't add this channel (null = they can). */
  const channelBlocked = (c: ChannelDef): string | null => {
    const reasons = c.methods.map((m) => {
      if (m.zernio) return canSocial ? null : t('channels.drawer.needsSocial')
      if (m.kind === 'voice' && !canKeys && !voiceInbox) return t('channels.page.superOnly')
      return canManage ? null : t('channels.page.addNeedsPermission')
    })
    return reasons.every((r) => r) ? reasons[0] : null
  }

  const runTest = async (inbox: Inbox) => {
    setTests((x) => ({ ...x, [inbox.key]: 'running' }))
    const r = await testInbox(inbox, t)
    setTests((x) => ({ ...x, [inbox.key]: r }))
  }

  const confirmDelete = async () => {
    if (!toDelete) return
    setDeleting(true)
    setDeleteError(null)
    try {
      await deleteInbox(toDelete)
      setPageMsg(t('channels.page.removed', { name: inboxName(toDelete, t) }))
      setToDelete(null)
      await reload()
    } catch (e) {
      setDeleteError(errText(e, t))
    } finally {
      setDeleting(false)
    }
  }

  /** A managed account's DM inbox, as its card shows it. */
  const linkedInbox = (inbox: Inbox) => ({
    health: healthOf(inbox, health),
    meta: [
      teamName(inbox.teamId),
      inbox.aiDefault ? t(inbox.aiDefault === 'on' ? 'channels.card.aiOn' : 'channels.card.aiOff') : undefined,
    ].filter((x): x is string => !!x),
    test: tests[inbox.key] && <TestLine test={tests[inbox.key]} />,
    testing: tests[inbox.key] === 'running',
    onSettings: canManage ? () => open(inbox.kind, inbox, 3) : undefined,
    onTest: () => runTest(inbox),
  })

  const shopOutlet = (s: Shop) =>
    s.entity_id == null ? t('channels.card.allEntities') : (entities.find((e) => e.id === s.entity_id)?.name ?? `#${s.entity_id}`)
  const shopHealth = (s: Shop) => {
    const row = health?.accounts.find((a) => String(a.id) === String(s.id))
    if (row) return { status: row.health, detail: row.detail }
    const st = asHealth(s.health)
    return st ? { status: st, detail: '' } : null
  }

  const chip = (active: boolean) =>
    `inline-flex h-8 cursor-pointer items-center gap-1.5 rounded-full border px-3 text-[12.5px] font-semibold transition-colors ${
      active
        ? 'border-transparent bg-primary text-on-primary'
        : 'border-line-strong bg-card text-text-mid hover:bg-fill-hover hover:text-text-high'
    }`

  return (
    <div className="flex shrink-0 flex-col gap-4 lg:min-h-0 lg:flex-1 lg:shrink">
      <PageHeader
        eyebrow={t('shell.groups.channels')}
        title={t('channels.page.title')}
        subtitle={t('channels.page.subtitle')}
        actions={
          canAdd ? (
            <Button data-tour="channels-add" onClick={() => open(null, null, 1)}>
              <Plus size={14} aria-hidden /> {t('channels.page.addInbox')}
            </Button>
          ) : undefined
        }
      />

      <div className="flex flex-col gap-9 pb-6 lg:min-h-0 lg:flex-1 lg:overflow-y-auto">
        {/* a managed WhatsApp signup coming back (?zernio=connected | error) */}
        <WhatsAppConnectResult />
        {pageMsg && (
          <div role="status" className="flex items-center gap-1.5 text-[12.5px] font-semibold" style={{ color: 'var(--green-ink)' }}>
            <CheckCircle size={13} aria-hidden /> {pageMsg}
          </div>
        )}
        {pageError && !drawer && (
          <div
            role="alert"
            className="rounded-lg border border-red/35 bg-red/10 px-3 py-2 text-[12.5px] font-semibold"
            style={{ color: 'var(--red-ink)' }}
          >
            {pageError}
          </div>
        )}
        {snap && snap.failed.length > 0 && (
          <div className="rounded-lg border border-amber/40 bg-amber/10 px-3 py-2 text-[12.5px]" style={{ color: 'var(--amber-ink)' }}>
            {t('channels.page.loadFailed', { sources: f.list(snap.failed.map((k) => t(k))) })}
          </div>
        )}
        {social.error && (
          <div className="rounded-lg border border-amber/40 bg-amber/10 px-3 py-2 text-[12.5px]" style={{ color: 'var(--amber-ink)' }}>
            {t('channels.page.socialFailed', { reason: social.error })}
          </div>
        )}

        {/* ── Connected: every inbox, Zernio account and shop ──────────────── */}
        <section data-tour="channels-connected" className="flex scroll-mt-4 flex-col gap-3" aria-labelledby="connected-h">
          {/* old links point at /channels#social-accounts — they land here now */}
          <span id="social-accounts" className="sr-only" aria-hidden />
          <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
            <h2 id="connected-h" className="display text-[22px] leading-tight text-text-heading">
              {t('channels.page.connected')}
            </h2>
            {loaded && (
              <span className="text-[12.5px] text-text-muted">{t('channels.page.inboxCount', { count: rows.length })}</span>
            )}
          </div>

          {rows.length > 0 && (
            <div data-tour="channels-filter" className="flex flex-wrap gap-1.5" role="group" aria-label={t('channels.page.filterByChannel')}>
              {FILTERS.filter((x) => x.id === 'all' || counts[x.id]).map((x) => (
                <button
                  key={x.id}
                  type="button"
                  aria-pressed={filter === x.id}
                  onClick={() => setFilter(x.id)}
                  className={chip(filter === x.id)}
                >
                  {x.logo && <ChannelLogo channel={x.logo} size={13} variant={filter === x.id ? 'mono' : 'mark'} title="" />}
                  {filterLabel(x, t)}
                  <span className={filter === x.id ? 'opacity-70' : 'text-text-faint'}>{f.number(counts[x.id] ?? 0)}</span>
                </button>
              ))}
            </div>
          )}

          {!snap ? (
            <div className="flex items-center gap-2 py-8 text-[12.5px] text-text-muted">
              <Loader2 size={14} className="animate-spin" aria-hidden /> {t('channels.page.loading')}
            </div>
          ) : rows.length === 0 ? (
            <div className="flex flex-col items-start gap-2 rounded-xl border border-dashed border-line-strong px-5 py-7">
              <span className="display text-[19px] text-text-heading">{t('channels.page.emptyTitle')}</span>
              <p className="max-w-[560px] text-[12.5px] leading-relaxed text-text-muted">{t('channels.page.emptyBody')}</p>
              {canAdd && (
                <Button className="mt-1" onClick={() => open(null, null, 1)}>
                  <Plus size={14} aria-hidden /> {t('channels.page.addInbox')}
                </Button>
              )}
            </div>
          ) : (
            <div className="grid grid-cols-1 gap-3.5 md:grid-cols-2 min-[1360px]:grid-cols-3">
              {visible.map((r) =>
                r.type === 'inbox' ? (
                  <InboxCard
                    key={r.key}
                    inbox={r.inbox}
                    health={healthOf(r.inbox, health)}
                    teamName={teamName(r.inbox.teamId)}
                    canManage={canManage}
                    canKeys={canKeys}
                    test={tests[r.inbox.key]}
                    onSettings={() => open(r.inbox.kind, r.inbox, r.inbox.kind === 'voice' ? 2 : 3)}
                    onTest={() => runTest(r.inbox)}
                    onPair={() => open(r.inbox.kind, r.inbox, 4)}
                    onDelete={() => {
                      setDeleteError(null)
                      setPageMsg(null)
                      setToDelete(r.inbox)
                    }}
                  />
                ) : r.type === 'social' ? (
                  <SocialAccountCard
                    key={r.key}
                    a={r.account}
                    s={social}
                    inbox={r.inbox && linkedInbox(r.inbox)}
                  />
                ) : (
                  <ShopCard key={r.key} shop={r.shop} health={shopHealth(r.shop)} outlet={shopOutlet(r.shop)} />
                ),
              )}
              {visible.length === 0 && (
                <div className="rounded-xl border border-dashed border-line-strong px-4 py-6 text-[12.5px] text-text-muted">
                  {t('channels.page.noneOfKind', { channel: activeFilter ? filterLabel(activeFilter, t) : filter })}
                </div>
              )}
              {canAdd && filter === 'all' && (
                <button
                  type="button"
                  onClick={() => open(null, null, 1)}
                  className="flex min-h-[120px] cursor-pointer items-center justify-center gap-1.5 rounded-xl border border-dashed border-line-strong px-4 text-center text-[12.5px] text-text-muted transition-colors hover:bg-fill-subtle hover:text-text-high"
                >
                  <Plus size={14} aria-hidden /> {t('channels.page.addAnother')}
                </button>
              )}
            </div>
          )}
        </section>

        {/* ── Add a channel: one tile per channel → the stepper ─────────────── */}
        <section data-tour="channels-catalog" className="flex flex-col gap-3" aria-labelledby="add-channel-h">
          <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
            <h2 id="add-channel-h" className="display text-[22px] leading-tight text-text-heading">
              {t('channels.page.addChannel')}
            </h2>
            <span className="text-[12.5px] text-text-muted">
              {canAdd ? t('channels.page.addHint') : t('channels.page.addNeedsPermission')}
            </span>
          </div>
          <ChannelTileGrid
            onPick={openChannel}
            selected={drawer && !drawer.inbox ? drawer.channel : null}
            disabled={channelBlocked}
            groupIdPrefix="add-group"
          />
        </section>

        {canManage && <ChannelsAdvanced verifyToken={snap?.verifyToken ?? ''} canKeys={canKeys} />}
      </div>

      {drawer && (
        <InboxSetupDrawer
          key={drawer.id}
          target={drawer}
          teams={snap?.teams ?? []}
          verifyToken={snap?.verifyToken ?? ''}
          canKeys={canKeys}
          voiceConfigured={voiceInbox}
          onClose={() => setDrawer(null)}
          onChanged={() => {
            reload().catch(() => {})
            void reloadSocial()
          }}
        />
      )}

      <SocialDisconnectModal s={social} onDone={(name) => setPageMsg(t('social.accounts.disconnected', { name }))} />

      <Modal
        open={!!toDelete}
        onClose={() => !deleting && setToDelete(null)}
        title={toDelete ? t('channels.page.deleteTitle', { name: inboxName(toDelete, t) }) : t('channels.page.deleteTitleGeneric')}
        size="sm"
        footer={
          <>
            <Button variant="secondary" onClick={() => setToDelete(null)} disabled={deleting}>
              {t('common.actions.cancel')}
            </Button>
            <Button variant="danger" onClick={confirmDelete} disabled={deleting}>
              {deleting && <Loader2 size={13} className="animate-spin" aria-hidden />} {t('channels.page.deleteConfirm')}
            </Button>
          </>
        }
      >
        {deleteError && (
          <div
            role="alert"
            className="mb-3 rounded-lg border border-red/35 bg-red/10 px-3 py-2 text-[12.5px] font-semibold"
            style={{ color: 'var(--red-ink)' }}
          >
            {deleteError}
          </div>
        )}
        {toDelete && (
          <p className="text-[13px] leading-relaxed text-text-mid">
            {t(DELETE_NOTE[toDelete.kind] ?? 'channels.page.deleteDefault')}
          </p>
        )}
      </Modal>
    </div>
  )
}
