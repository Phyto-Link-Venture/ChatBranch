'use client'

/**
 * /channels — the Inboxes page (Chatwoot's model). Every number, mailbox,
 * bot, page, website widget and the SIP trunk is an INBOX:
 *
 *  - Connected: one card per inbox across every channel — health, owning
 *    entity and team, WhatsApp role — with Settings / Test / Pair / Delete.
 *  - Add a channel: a block per channel; picking one opens the step-by-step
 *    setup drawer (components/channels/InboxSetupDrawer).
 *  - Advanced (collapsed): the shared Meta webhook and default mail settings.
 *
 * Everything reads and writes the existing account endpoints (see
 * components/channels/inboxes.ts). Editing needs `channels.manage`.
 */

import { useCallback, useEffect, useMemo, useState } from 'react'
import { Loader2, Plug, Plus, QrCode, Settings2, Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Modal } from '@/components/ui/Modal'
import { PageHeader } from '@/components/ui/PageHeader'
import { ChannelLogo } from '@/components/ui/ChannelLogo'
import { RoleChip } from '@/components/ui/Badges'
import { getRole, hasCap } from '@/lib/auth'
import { useEntityQuery } from '@/lib/EntityContext'
import { fetchChannelHealth, HEALTH_META, type ChannelHealthPayload, type HealthStatus } from '@/lib/channelHealth'
import { useFormat, useT, type MessageKey } from '@/i18n'
import {
  ApprovalChip,
  InboxSetupDrawer,
  KindBadge,
  type DrawerTarget,
  type Step,
} from '@/components/channels/InboxSetupDrawer'
import { ChannelsAdvanced } from '@/components/channels/ChannelsAdvanced'
import {
  FILTERS,
  KINDS,
  KIND_GROUPS,
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

function healthOf(inbox: Inbox, health: ChannelHealthPayload | null): { status: HealthStatus; detail: string } | null {
  if (!health) return null
  if (inbox.kind === 'voice')
    return health.voice ? { status: health.voice.health, detail: health.voice.detail } : null
  const row = health.accounts.find((a) => String(a.id) === inbox.healthId)
  return row ? { status: row.health, detail: row.detail } : null
}

function HealthDot({ status, detail }: { status: HealthStatus; detail?: string }) {
  const t = useT()
  const m = HEALTH_META[status] ?? HEALTH_META.unknown
  return (
    <span className="inline-flex items-center gap-1.5" title={detail || t(m.key)}>
      <span className="h-2 w-2 shrink-0 rounded-full" style={{ background: m.dot }} aria-hidden />
      <span style={{ color: m.ink }} className="font-semibold">
        {t(m.key)}
      </span>
    </span>
  )
}

function InboxCard({
  inbox,
  health,
  teamName,
  canManage,
  isSuper,
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
  isSuper: boolean
  test?: TestResult | 'running'
  onSettings: () => void
  onTest: () => void
  onPair: () => void
  onDelete: () => void
}) {
  const t = useT()
  const meta = kindMeta(inbox.kind)
  const name = inboxName(inbox, t)
  const editable = canManage && !inbox.envManaged && (inbox.kind !== 'voice' || isSuper)
  const btn =
    'inline-flex h-8 cursor-pointer items-center gap-1.5 rounded-lg border border-line-strong bg-card px-3 text-[12.5px] font-semibold text-text-high transition-colors hover:bg-fill-hover disabled:cursor-not-allowed disabled:opacity-50'
  return (
    <article className="flex min-w-0 flex-col gap-3 rounded-xl border border-line bg-card p-4">
      <div className="flex items-start gap-3">
        <ChannelLogo channel={meta.logo} variant="tile" size={36} title="" />
        <div className="min-w-0 flex-1">
          <div className="flex min-w-0 flex-wrap items-center gap-1.5">
            <span className="truncate text-[14px] font-bold text-text-heading">{name}</span>
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
            {t(meta.channelLabel)} · {txt(inbox.address, t)}
          </div>
        </div>
      </div>

      <div className="flex flex-wrap items-center gap-x-2 gap-y-1 text-[12px] text-text-muted">
        {health ? <HealthDot status={health.status} detail={health.detail} /> : <span>{t('channels.card.notMonitored')}</span>}
        {inbox.kind !== 'voice' && (
          <>
            <span aria-hidden>·</span>
            <span className="truncate">{inbox.entityName || t('channels.card.allEntities')}</span>
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

      {test && (
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
      )}

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

const DELETE_NOTE: Partial<Record<InboxKind, MessageKey>> = {
  voice: 'channels.page.deleteVoice',
  website: 'channels.page.deleteWebsite',
  wa_qr: 'channels.page.deleteQr',
}

export function ChannelsSettings() {
  const t = useT()
  const f = useFormat()
  const [snap, setSnap] = useState<InboxesSnapshot | null>(null)
  const [health, setHealth] = useState<ChannelHealthPayload | null>(null)
  const [filter, setFilter] = useState<ChannelFilter>('all')
  const [drawer, setDrawer] = useState<(DrawerTarget & { id: number }) | null>(null)
  const [tests, setTests] = useState<Record<string, TestResult | 'running'>>({})
  const [toDelete, setToDelete] = useState<Inbox | null>(null)
  const [deleting, setDeleting] = useState(false)
  const [deleteError, setDeleteError] = useState<string | null>(null)
  const [pageMsg, setPageMsg] = useState<string | null>(null)
  // capability + role read after mount (localStorage — see CLAUDE.md hydration rule)
  const [canManage, setCanManage] = useState(false)
  const [isSuper, setIsSuper] = useState(false)
  useEffect(() => {
    setCanManage(hasCap('channels.manage'))
    setIsSuper(getRole() === 'superadmin')
  }, [])

  const entityQ = useEntityQuery()
  const reload = useCallback(async () => {
    const [s, h] = await Promise.allSettled([loadInboxes(entityQ), fetchChannelHealth(entityQ)])
    if (s.status === 'fulfilled') setSnap(s.value)
    setHealth(h.status === 'fulfilled' ? h.value : null)
  }, [entityQ])

  useEffect(() => {
    reload()
  }, [reload])

  const inboxes = useMemo(() => snap?.inboxes ?? [], [snap])
  const counts = useMemo(() => {
    const c: Partial<Record<ChannelFilter, number>> = { all: inboxes.length }
    for (const i of inboxes) {
      const k = kindMeta(i.kind).filter
      c[k] = (c[k] ?? 0) + 1
    }
    return c
  }, [inboxes])
  const visible = filter === 'all' ? inboxes : inboxes.filter((i) => kindMeta(i.kind).filter === filter)
  const voiceInbox = inboxes.find((i) => i.kind === 'voice') ?? null
  const teamName = (id: number | null) => (id == null ? undefined : snap?.teams.find((tm) => tm.id === id)?.name)
  const activeFilter = FILTERS.find((x) => x.id === filter)

  const open = (kind: InboxKind | null, inbox: Inbox | null, step: Step) =>
    setDrawer({ kind, inbox, step, id: Date.now() })

  const openBlock = (kind: InboxKind) => {
    if (kind === 'voice' && voiceInbox) open('voice', voiceInbox, 2)
    else open(kind, null, 2)
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
          canManage ? (
            <Button data-tour="channels-add" onClick={() => open(null, null, 1)}>
              <Plus size={14} aria-hidden /> {t('channels.page.addInbox')}
            </Button>
          ) : undefined
        }
      />

      <div className="flex flex-col gap-9 pb-6 lg:min-h-0 lg:flex-1 lg:overflow-y-auto">
        {pageMsg && (
          <div role="status" className="text-[12.5px] font-semibold" style={{ color: 'var(--green-ink)' }}>
            {pageMsg}
          </div>
        )}
        {snap && snap.failed.length > 0 && (
          <div className="rounded-lg border border-amber/40 bg-amber/10 px-3 py-2 text-[12.5px]" style={{ color: 'var(--amber-ink)' }}>
            {t('channels.page.loadFailed', { sources: f.list(snap.failed.map((k) => t(k))) })}
          </div>
        )}

        {/* ── Connected ─────────────────────────────────────────────────── */}
        <section data-tour="channels-connected" className="flex flex-col gap-3" aria-labelledby="connected-h">
          <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
            <h2 id="connected-h" className="display text-[22px] leading-tight text-text-heading">
              {t('channels.page.connected')}
            </h2>
            {snap && (
              <span className="text-[12.5px] text-text-muted">
                {t('channels.page.inboxCount', { count: inboxes.length })}
              </span>
            )}
          </div>

          {inboxes.length > 0 && (
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
          ) : inboxes.length === 0 ? (
            <div className="flex flex-col items-start gap-2 rounded-xl border border-dashed border-line-strong px-5 py-7">
              <span className="display text-[19px] text-text-heading">{t('channels.page.emptyTitle')}</span>
              <p className="max-w-[560px] text-[12.5px] leading-relaxed text-text-muted">{t('channels.page.emptyBody')}</p>
            </div>
          ) : (
            <div className="grid grid-cols-1 gap-3.5 md:grid-cols-2 min-[1360px]:grid-cols-3">
              {visible.map((inbox) => (
                <InboxCard
                  key={inbox.key}
                  inbox={inbox}
                  health={healthOf(inbox, health)}
                  teamName={teamName(inbox.teamId)}
                  canManage={canManage}
                  isSuper={isSuper}
                  test={tests[inbox.key]}
                  onSettings={() => open(inbox.kind, inbox, inbox.kind === 'voice' ? 2 : 3)}
                  onTest={() => runTest(inbox)}
                  onPair={() => open(inbox.kind, inbox, 4)}
                  onDelete={() => {
                    setDeleteError(null)
                    setPageMsg(null)
                    setToDelete(inbox)
                  }}
                />
              ))}
              {visible.length === 0 && (
                <div className="rounded-xl border border-dashed border-line-strong px-4 py-6 text-[12.5px] text-text-muted">
                  {t('channels.page.noneOfKind', { channel: activeFilter ? filterLabel(activeFilter, t) : filter })}
                </div>
              )}
              {canManage && filter === 'all' && (
                <button
                  type="button"
                  onClick={() => document.getElementById('add-channel-h')?.scrollIntoView({ behavior: 'smooth' })}
                  className="flex min-h-[120px] cursor-pointer items-center justify-center rounded-xl border border-dashed border-line-strong px-4 text-center text-[12.5px] text-text-muted transition-colors hover:bg-fill-subtle hover:text-text-high"
                >
                  {t('channels.page.pickBlock')}
                </button>
              )}
            </div>
          )}
        </section>

        {/* ── Add a channel ─────────────────────────────────────────────── */}
        <section data-tour="channels-catalog" className="flex flex-col gap-3" aria-labelledby="add-channel-h">
          <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
            <h2 id="add-channel-h" className="display text-[22px] leading-tight text-text-heading">
              {t('channels.page.addChannel')}
            </h2>
            <span className="text-[12.5px] text-text-muted">
              {canManage ? t('channels.page.addHint') : t('channels.page.addNeedsPermission')}
            </span>
          </div>
          {KIND_GROUPS.map((g) => (
            <div key={g.id} className="flex flex-col gap-2" role="group" aria-labelledby={`add-group-${g.id}`}>
              <div className="flex flex-wrap items-baseline gap-x-2 gap-y-0.5">
                <h3 id={`add-group-${g.id}`} className="eyebrow">
                  {t(g.label)}
                </h3>
                <span className="text-[11.5px] text-text-faint">{t(g.hint)}</span>
              </div>
              <div className="grid grid-cols-1 gap-3.5 min-[460px]:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4">
                {KINDS.filter((k) => k.group === g.id).map((k) => {
                  const selected = !!drawer && drawer.kind === k.kind && !drawer.inbox
                  const voiceLocked = k.kind === 'voice' && !isSuper && !voiceInbox
                  const disabled = !canManage || voiceLocked
                  return (
                    <button
                      key={k.kind}
                      type="button"
                      disabled={disabled}
                      onClick={() => openBlock(k.kind)}
                      className={`group flex min-w-0 flex-col gap-2 rounded-xl border bg-card p-4 text-left transition-colors enabled:cursor-pointer enabled:hover:border-text-primary disabled:cursor-not-allowed disabled:opacity-60 ${
                        selected ? 'border-text-primary shadow-[0_0_0_1px_var(--text-primary)]' : 'border-line'
                      }`}
                    >
                      <span className="flex items-start justify-between gap-2">
                        <ChannelLogo channel={k.logo} variant="tile" size={38} title="" />
                        <span className="flex flex-wrap justify-end gap-1">
                          {k.badge && <KindBadge badge={k.badge} />}
                          {k.approval && <ApprovalChip />}
                        </span>
                      </span>
                      <span className="mt-1 text-[14px] font-bold text-text-heading">{t(k.label)}</span>
                      <span className="text-[12px] leading-snug text-text-mid">{t(k.blurb)}</span>
                      {k.kind === 'wa_qr' && (
                        <span className="text-[11.5px] leading-snug" style={{ color: 'var(--amber-ink)' }}>
                          {t('channels.page.qrWarning')}
                        </span>
                      )}
                      {k.kind === 'voice' && (voiceInbox || voiceLocked) && (
                        <span className="text-[11.5px] text-text-muted">
                          {voiceInbox ? t('channels.page.voiceConnected') : t('channels.page.superOnly')}
                        </span>
                      )}
                    </button>
                  )
                })}
              </div>
            </div>
          ))}
        </section>

        {canManage && <ChannelsAdvanced verifyToken={snap?.verifyToken ?? ''} isSuper={isSuper} />}
      </div>

      {drawer && (
        <InboxSetupDrawer
          key={drawer.id}
          target={drawer}
          teams={snap?.teams ?? []}
          verifyToken={snap?.verifyToken ?? ''}
          isSuper={isSuper}
          voiceConfigured={voiceInbox}
          onClose={() => setDrawer(null)}
          onChanged={() => {
            reload().catch(() => {})
          }}
        />
      )}

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
