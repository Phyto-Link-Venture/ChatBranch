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
import { fetchChannelHealth, HEALTH_META, type ChannelHealthPayload, type HealthStatus } from '@/lib/channelHealth'
import { InboxSetupDrawer, KindBadge, type DrawerTarget, type Step } from '@/components/channels/InboxSetupDrawer'
import { ChannelsAdvanced } from '@/components/channels/ChannelsAdvanced'
import {
  FILTERS,
  KINDS,
  deleteInbox,
  errText,
  kindMeta,
  loadInboxes,
  testInbox,
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
  const m = HEALTH_META[status] ?? HEALTH_META.unknown
  return (
    <span className="inline-flex items-center gap-1.5" title={detail || m.lbl}>
      <span className="h-2 w-2 shrink-0 rounded-full" style={{ background: m.dot }} aria-hidden />
      <span style={{ color: m.ink }} className="font-semibold">
        {m.lbl}
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
  const meta = kindMeta(inbox.kind)
  const editable = canManage && !inbox.envManaged && (inbox.kind !== 'voice' || isSuper)
  const btn =
    'inline-flex h-8 cursor-pointer items-center gap-1.5 rounded-lg border border-line-strong bg-card px-3 text-[12.5px] font-semibold text-text-high transition-colors hover:bg-fill-hover disabled:cursor-not-allowed disabled:opacity-50'
  return (
    <article className="flex min-w-0 flex-col gap-3 rounded-xl border border-line bg-card p-4">
      <div className="flex items-start gap-3">
        <ChannelLogo channel={meta.logo} variant="tile" size={36} title="" />
        <div className="min-w-0 flex-1">
          <div className="flex min-w-0 flex-wrap items-center gap-1.5">
            <span className="truncate text-[14px] font-bold text-text-heading">{inbox.name}</span>
            {inbox.role && <RoleChip role={inbox.role} />}
            {meta.badge && <KindBadge badge={meta.badge} />}
            {inbox.envManaged && (
              <span className="rounded-full bg-fill px-1.5 py-px text-[9.5px] font-bold uppercase text-text-muted">
                from .env
              </span>
            )}
          </div>
          <div className="truncate text-[12px] text-text-muted">
            {meta.channelLabel} · {inbox.address}
          </div>
        </div>
      </div>

      <div className="flex flex-wrap items-center gap-x-2 gap-y-1 text-[12px] text-text-muted">
        {health ? <HealthDot status={health.status} detail={health.detail} /> : <span>Not monitored</span>}
        {inbox.kind !== 'voice' && (
          <>
            <span aria-hidden>·</span>
            <span className="truncate">{inbox.entityName || 'All entities'}</span>
            {teamName && (
              <>
                <span aria-hidden>·</span>
                <span className="truncate">{teamName}</span>
              </>
            )}
            {inbox.aiDefault && (
              <>
                <span aria-hidden>·</span>
                <span>AI {inbox.aiDefault}</span>
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
              <Loader2 size={12} className="animate-spin" aria-hidden /> Testing…
            </span>
          ) : (
            test.text
          )}
        </div>
      )}

      <div className="mt-auto flex flex-wrap items-center gap-2 pt-0.5">
        {editable && (
          <button type="button" className={btn} onClick={onSettings}>
            <Settings2 size={13} aria-hidden /> Settings
          </button>
        )}
        <button type="button" className={btn} onClick={onTest} disabled={test === 'running'}>
          <Plug size={13} aria-hidden /> Test
        </button>
        {editable && inbox.kind === 'wa_qr' && (
          <button type="button" className={btn} onClick={onPair}>
            <QrCode size={13} aria-hidden /> Pair
          </button>
        )}
        {editable && (
          <button
            type="button"
            onClick={onDelete}
            className="ml-auto inline-flex h-8 cursor-pointer items-center gap-1.5 rounded-lg px-2.5 text-[12.5px] font-semibold transition-colors hover:bg-red/10"
            style={{ color: 'var(--red-ink)' }}
            aria-label={`Delete ${inbox.name}`}
          >
            <Trash2 size={13} aria-hidden /> Delete
          </button>
        )}
      </div>
    </article>
  )
}

export function ChannelsSettings() {
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

  const reload = useCallback(async () => {
    const [s, h] = await Promise.allSettled([loadInboxes(), fetchChannelHealth()])
    if (s.status === 'fulfilled') setSnap(s.value)
    setHealth(h.status === 'fulfilled' ? h.value : null)
  }, [])

  useEffect(() => {
    reload()
  }, [reload])

  const inboxes = useMemo(() => snap?.inboxes ?? [], [snap])
  const counts = useMemo(() => {
    const c: Partial<Record<ChannelFilter, number>> = { all: inboxes.length }
    for (const i of inboxes) {
      const f = kindMeta(i.kind).filter
      c[f] = (c[f] ?? 0) + 1
    }
    return c
  }, [inboxes])
  const visible = filter === 'all' ? inboxes : inboxes.filter((i) => kindMeta(i.kind).filter === filter)
  const voiceInbox = inboxes.find((i) => i.kind === 'voice') ?? null
  const teamName = (id: number | null) => (id == null ? undefined : snap?.teams.find((t) => t.id === id)?.name)

  const open = (kind: InboxKind | null, inbox: Inbox | null, step: Step) =>
    setDrawer({ kind, inbox, step, id: Date.now() })

  const openBlock = (kind: InboxKind) => {
    if (kind === 'voice' && voiceInbox) open('voice', voiceInbox, 2)
    else open(kind, null, 2)
  }

  const runTest = async (inbox: Inbox) => {
    setTests((t) => ({ ...t, [inbox.key]: 'running' }))
    const r = await testInbox(inbox)
    setTests((t) => ({ ...t, [inbox.key]: r }))
  }

  const confirmDelete = async () => {
    if (!toDelete) return
    setDeleting(true)
    setDeleteError(null)
    try {
      await deleteInbox(toDelete)
      setPageMsg(`${toDelete.name} was removed.`)
      setToDelete(null)
      await reload()
    } catch (e) {
      setDeleteError(errText(e))
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
    <div className="flex min-h-0 flex-1 flex-col gap-4">
      <PageHeader
        eyebrow="Channels"
        title="Inboxes"
        subtitle="Every number, mailbox and widget your team answers from."
        actions={
          canManage ? (
            <Button onClick={() => open(null, null, 1)}>
              <Plus size={14} aria-hidden /> Add inbox
            </Button>
          ) : undefined
        }
      />

      <div className="flex min-h-0 flex-1 flex-col gap-9 overflow-y-auto pb-6">
        {pageMsg && (
          <div role="status" className="text-[12.5px] font-semibold" style={{ color: 'var(--green-ink)' }}>
            {pageMsg}
          </div>
        )}
        {snap && snap.failed.length > 0 && (
          <div className="rounded-lg border border-amber/40 bg-amber/10 px-3 py-2 text-[12.5px]" style={{ color: 'var(--amber-ink)' }}>
            Couldn&apos;t load {snap.failed.join(', ')} — is the API running? The rest is shown below.
          </div>
        )}

        {/* ── Connected ─────────────────────────────────────────────────── */}
        <section className="flex flex-col gap-3" aria-labelledby="connected-h">
          <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
            <h2 id="connected-h" className="display text-[22px] leading-tight text-text-heading">
              Connected
            </h2>
            {snap && (
              <span className="text-[12.5px] text-text-muted">
                {inboxes.length} inbox{inboxes.length === 1 ? '' : 'es'}
              </span>
            )}
          </div>

          {inboxes.length > 0 && (
            <div className="flex flex-wrap gap-1.5" role="group" aria-label="Filter by channel">
              {FILTERS.filter((f) => f.id === 'all' || counts[f.id]).map((f) => (
                <button
                  key={f.id}
                  type="button"
                  aria-pressed={filter === f.id}
                  onClick={() => setFilter(f.id)}
                  className={chip(filter === f.id)}
                >
                  {f.logo && <ChannelLogo channel={f.logo} size={13} variant={filter === f.id ? 'mono' : 'mark'} title="" />}
                  {f.label}
                  <span className={filter === f.id ? 'opacity-70' : 'text-text-faint'}>{counts[f.id] ?? 0}</span>
                </button>
              ))}
            </div>
          )}

          {!snap ? (
            <div className="flex items-center gap-2 py-8 text-[12.5px] text-text-muted">
              <Loader2 size={14} className="animate-spin" aria-hidden /> Loading inboxes…
            </div>
          ) : inboxes.length === 0 ? (
            <div className="flex flex-col items-start gap-2 rounded-xl border border-dashed border-line-strong px-5 py-7">
              <span className="display text-[19px] text-text-heading">No inboxes yet</span>
              <p className="max-w-[560px] text-[12.5px] leading-relaxed text-text-muted">
                Pick a channel below to connect your first number, mailbox or website chat. Until then every channel
                runs in sandbox — messages are stored locally and nothing is sent.
              </p>
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
                  No {FILTERS.find((f) => f.id === filter)?.label} inboxes.
                </div>
              )}
              {canManage && filter === 'all' && (
                <button
                  type="button"
                  onClick={() => document.getElementById('add-channel-h')?.scrollIntoView({ behavior: 'smooth' })}
                  className="flex min-h-[120px] cursor-pointer items-center justify-center rounded-xl border border-dashed border-line-strong px-4 text-[12.5px] text-text-muted transition-colors hover:bg-fill-subtle hover:text-text-high"
                >
                  Pick a block below to add an inbox
                </button>
              )}
            </div>
          )}
        </section>

        {/* ── Add a channel ─────────────────────────────────────────────── */}
        <section className="flex flex-col gap-3" aria-labelledby="add-channel-h">
          <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
            <h2 id="add-channel-h" className="display text-[22px] leading-tight text-text-heading">
              Add a channel
            </h2>
            <span className="text-[12.5px] text-text-muted">
              {canManage
                ? 'Pick a block — setup walks you through it.'
                : 'You need the “Manage channel accounts” permission to add inboxes.'}
            </span>
          </div>
          <div className="grid grid-cols-1 gap-3.5 min-[460px]:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4">
            {KINDS.map((k) => {
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
                    {k.badge && <KindBadge badge={k.badge} />}
                  </span>
                  <span className="mt-1 text-[14px] font-bold text-text-heading">{k.label}</span>
                  <span className="text-[12px] leading-snug text-text-mid">{k.blurb}</span>
                  {k.kind === 'wa_qr' && (
                    <span className="text-[11.5px] leading-snug" style={{ color: 'var(--amber-ink)' }}>
                      Unofficial — breaks WhatsApp&apos;s terms. Use a spare number.
                    </span>
                  )}
                  {k.kind === 'voice' && (voiceInbox || voiceLocked) && (
                    <span className="text-[11.5px] text-text-muted">
                      {voiceInbox ? 'Already connected — opens its settings' : 'Super admin only'}
                    </span>
                  )}
                </button>
              )
            })}
          </div>
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
        title={toDelete ? `Delete ${toDelete.name}?` : 'Delete inbox?'}
        size="sm"
        footer={
          <>
            <Button variant="secondary" onClick={() => setToDelete(null)} disabled={deleting}>
              Cancel
            </Button>
            <Button variant="danger" onClick={confirmDelete} disabled={deleting}>
              {deleting && <Loader2 size={13} className="animate-spin" aria-hidden />} Delete inbox
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
            {toDelete.kind === 'voice'
              ? 'This clears the SIP credentials saved here. Any values in backend/.env stay in force.'
              : toDelete.kind === 'website'
                ? 'The embed stops working on your site. Its conversations are kept in the inbox.'
                : toDelete.kind === 'wa_qr'
                  ? 'The phone is unpaired and the inbox removed. Its conversations stay, back in sandbox.'
                  : 'The inbox is removed. Its conversations stay, back in sandbox.'}
          </p>
        )}
      </Modal>
    </div>
  )
}
