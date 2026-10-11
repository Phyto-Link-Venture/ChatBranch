'use client'

import type { ReactNode } from 'react'
import Link from 'next/link'
import { ExternalLink, Link2, Mail, X } from 'lucide-react'
import { Select } from '@/components/ui/Select'
import { ChannelLogo } from '@/components/ui/ChannelLogo'
import ChannelHub from '@/components/ChannelHub'
import { BookingsCard } from '@/components/bookings/BookingsCard'
import { DocumentsCard } from './DocumentsCard'
import { CreateFromChat } from '@/components/contacts/CreateFromChatModal'
import { useTags } from '@/lib/tags'
import { tint } from '@/lib/color'
import { Avatar, BetaChip } from './MessageParts'
import { useT } from '@/i18n'
import {
  CHANNEL_META,
  THREAD_STATUSES,
  channelName,
  logoKey,
  roleLabel,
  statusLabel,
  type ThreadStatus,
  type UiAccount,
  type UiAgent,
  type UiTeam,
  type UiThread,
} from './inboxTypes'

/** One stacked card of the info panel, headed by an eyebrow. */
function InfoCard({ title, children, note }: { title: string; children: ReactNode; note?: ReactNode }) {
  return (
    <section className="rounded-xl border border-line bg-card p-3.5">
      <h3 className="eyebrow mb-2">{title}</h3>
      {children}
      {note && <p className="mt-2 text-[11.5px] leading-relaxed text-text-muted">{note}</p>}
    </section>
  )
}

export interface LinkContact {
  id: number
  name: string
  phone: string
  email: string
}

/** The right-hand panel: who this is, who owns the conversation, labels. */
export default function ChatInfoPanel({
  active,
  busy,
  sandbox,
  imapConfigured,
  linkContacts,
  linkSel,
  onLinkSel,
  onLink,
  waAccounts,
  canManage,
  onSwitchAccount,
  teams,
  onAssignTeam,
  agents,
  onAssign,
  onSetStatus,
  onSaveLabels,
  drawer = false,
}: {
  active: UiThread
  busy: boolean
  sandbox: boolean
  imapConfigured: boolean
  linkContacts: LinkContact[]
  linkSel: string
  onLinkSel: (v: string) => void
  onLink: (contactId: number | null) => void
  waAccounts: UiAccount[]
  canManage: boolean
  onSwitchAccount: (id: string) => void
  teams: UiTeam[]
  onAssignTeam: (id: string) => void
  agents: UiAgent[]
  onAssign: (username: string) => void
  onSetStatus: (s: ThreadStatus) => void
  onSaveLabels: (labels: string[]) => void
  /** Rendered inside the phone/tablet drawer (below xl) instead of as the side column. */
  drawer?: boolean
}) {
  // Conversation labels come from the Tags registry (Library → Tags).
  const t = useT()
  const { metas: conversationTags } = useTags('conversation')
  const labels = active.labels ?? []
  const meta = CHANNEL_META[active.channel]
  return (
    <aside
      data-tour={drawer ? undefined : 'chats-info'}
      aria-label={t('chats.info.aria')}
      className={
        drawer
          ? 'flex min-h-0 flex-1 flex-col gap-3 overflow-y-auto p-3'
          : 'hidden w-[288px] shrink-0 flex-col gap-3 overflow-y-auto border-l border-line bg-surface p-3 xl:flex'
      }
    >
      <section className="flex flex-col items-center rounded-xl border border-line bg-card px-4 py-5 text-center">
        <Avatar name={active.name} size={56} strong />
        <div className="display mt-2.5 text-[20px] leading-tight text-text-heading">{active.name}</div>
        <div className="mt-1 flex items-center justify-center gap-1.5 text-[12px] text-text-muted">
          <ChannelLogo channel={logoKey(active.channel)} size={13} />
          {channelName(t, active.channel)}
          {meta.beta && <BetaChip />}
        </div>
        <div className="mt-0.5 max-w-full truncate text-[12px] text-text-mid">{active.sub}</div>
      </section>

      <InfoCard
        title={t('chats.info.contact')}
        note={t(active.contact_id != null ? 'chats.info.contactNoteLinked' : 'chats.info.contactNoteUnlinked')}
      >
        {active.contact_id != null ? (
          <div className="flex items-center justify-between gap-2">
            <Link
              href={`/contacts/${active.contact_id}`}
              className="inline-flex min-w-0 items-center gap-1 truncate text-[13px] font-semibold text-text-high hover:underline"
            >
              <span className="truncate">{active.contact_name || `#${active.contact_id}`}</span>
              <ExternalLink size={11} className="shrink-0 text-text-muted" aria-hidden />
            </Link>
            <button
              type="button"
              onClick={() => onLink(null)}
              disabled={busy}
              className="shrink-0 rounded-md px-1.5 py-0.5 text-[11px] font-semibold text-text-muted transition-colors hover:bg-red/10 hover:text-red-300"
            >
              {t('chats.info.unlink')}
            </button>
          </div>
        ) : (
          <>
          <div className="flex items-center gap-1.5">
            <Select
              value={linkSel}
              onChange={onLinkSel}
              options={linkContacts.map((c) => ({ value: String(c.id), label: c.name, hint: c.phone ? `(${c.phone})` : undefined }))}
              placeholder={t('chats.info.linkPlaceholder')}
              ariaLabel={t('chats.info.linkAria')}
              className="min-w-0 flex-1"
              searchable
            />
            <button
              type="button"
              onClick={() => linkSel && onLink(Number(linkSel))}
              disabled={busy || !linkSel}
              title={t('chats.info.linkContact')}
              aria-label={t('chats.info.linkContact')}
              className="grid h-8 w-8 shrink-0 place-items-center rounded-lg border border-line-strong bg-card text-text-muted transition-colors hover:text-text-high disabled:opacity-35"
            >
              <Link2 size={13} aria-hidden />
            </button>
          </div>
          {/* an unknown customer: a new contact prefilled from what they said */}
          <CreateFromChat channel={active.channel} threadKey={active.key} threadId={active.thread_id} onCreated={(id) => onLink(id)} />
          </>
        )}
      </InfoCard>

      {active.contact_id != null && (
        <InfoCard title={t('chats.info.channels')}>
          <ChannelHub contactId={active.contact_id} />
        </InfoCard>
      )}

      {/* upcoming appointments — refetched as the conversation moves (a booking
          is made when the AI's reply is delivered) */}
      {active.contact_id != null && (
        <BookingsCard contactId={active.contact_id} refreshToken={active.messages.length} compact />
      )}

      {/* invoices, quotations, receipts and letters made here — and "New document" */}
      <DocumentsCard
        channel={active.channel}
        threadKey={active.key}
        threadId={active.thread_id}
        refreshToken={active.messages.length}
      />

      <InfoCard title={t('chats.info.conversation')} note={t('chats.info.conversationNote')}>
        <div className="flex flex-col gap-2.5">
          <label className="flex flex-col gap-1">
            <span className="text-[11.5px] font-semibold text-text-mid">{t('common.fields.status')}</span>
            <Select
              value={active.status ?? 'open'}
              onChange={(v) => onSetStatus(v as ThreadStatus)}
              options={THREAD_STATUSES.map((s) => ({ value: s, label: statusLabel(t, s) }))}
              disabled={busy}
              size="md"
              ariaLabel={t('chats.info.statusAria')}
            />
          </label>
          {teams.length > 0 && (
            <label className="flex flex-col gap-1">
              <span className="text-[11.5px] font-semibold text-text-mid">{t('chats.info.team')}</span>
              <Select
                value={active.team_id ? String(active.team_id) : ''}
                onChange={onAssignTeam}
                options={[{ value: '', label: t('chats.info.noTeam') }, ...teams.map((tm) => ({ value: String(tm.id), label: tm.name }))]}
                disabled={busy}
                size="md"
                ariaLabel={t('chats.info.teamAria')}
              />
            </label>
          )}
          <label className="flex flex-col gap-1">
            <span className="text-[11.5px] font-semibold text-text-mid">{t('chats.info.assignee')}</span>
            <Select
              value={active.assignee ?? ''}
              onChange={onAssign}
              options={[
                { value: '', label: t('chats.info.unassigned') },
                ...agents.map((a) => ({ value: a.username, label: a.name, hint: `(${roleLabel(t, a.role)})` })),
                // keep an assignee visible even if their profile disappears
                ...(active.assignee && !agents.some((a) => a.username === active.assignee)
                  ? [{ value: active.assignee, label: active.assignee }]
                  : []),
              ]}
              disabled={busy}
              size="md"
              ariaLabel={t('chats.info.assigneeAria')}
            />
          </label>
        </div>
      </InfoCard>

      {active.channel === 'whatsapp' && waAccounts.length > 0 && (
        <InfoCard
          title={t('chats.info.sendingAccount')}
          note={t('chats.info.sendingAccountNote')}
        >
          <div className="flex items-center justify-between gap-2">
            <span className="flex min-w-0 items-center gap-1.5 text-[13px] font-semibold text-text-high">
              <ChannelLogo channel="whatsapp" size={13} />
              <span className="truncate">
                {waAccounts.find((a) => a.id === String(active.account_id ?? ''))?.name ??
                  (active.account_id ? t('chats.info.accountN', { id: active.account_id }) : t('chats.info.auto'))}
              </span>
            </span>
          </div>
          {canManage && waAccounts.length > 1 && (
            <Select
              value={String(active.account_id ?? '')}
              onChange={onSwitchAccount}
              options={[
                ...(!active.account_id ? [{ value: '', label: t('chats.info.auto') }] : []),
                ...waAccounts.map((a) => ({ value: a.id, label: a.name })),
              ]}
              disabled={busy}
              ariaLabel={t('chats.info.switchAccountAria')}
              className="mt-2"
            />
          )}
        </InfoCard>
      )}

      {active.channel === 'email' && (
        <InfoCard title={t('common.fields.email')}>
          <p className="flex gap-1.5 text-[12px] leading-relaxed text-text-mid">
            <Mail size={13} className="mt-0.5 shrink-0 text-text-muted" aria-hidden />
            {t(imapConfigured ? 'chats.info.emailTwoWay' : 'chats.info.emailOutbound')}
          </p>
        </InfoCard>
      )}

      <InfoCard title={t('chats.info.labels')}>
        <div className="flex flex-wrap gap-1.5">
          {conversationTags.map((tg) => {
            const on = labels.includes(tg.slug)
            return (
              <button
                key={tg.slug}
                type="button"
                aria-pressed={on}
                onClick={() => onSaveLabels(on ? labels.filter((l) => l !== tg.slug) : [...labels, tg.slug])}
                disabled={busy}
                title={t(on ? 'chats.info.removeLabel' : 'chats.info.addLabel', { label: tg.label })}
                className="inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-[11.5px] transition-colors disabled:opacity-50"
                style={
                  on
                    ? { background: tint(tg.color, 0.14), borderColor: tint(tg.color, 0.55), color: 'var(--text-high)', fontWeight: 600 }
                    : { borderColor: 'var(--line)', color: 'var(--text-muted)' }
                }
              >
                <span className="h-1.5 w-1.5 rounded-full" style={{ background: tg.color }} aria-hidden />
                {tg.label}
              </button>
            )
          })}
        </div>
        {/* legacy free-text labels not in the registry — still removable */}
        {labels.some((l) => !conversationTags.some((tg) => tg.slug === l)) && (
          <div className="mt-2 flex flex-wrap gap-1">
            {labels
              .filter((l) => !conversationTags.some((tg) => tg.slug === l))
              .map((label) => (
                <span key={label} className="inline-flex items-center gap-1 rounded-full border border-line px-2 py-0.5 text-[11.5px] text-text-mid">
                  {label}
                  <button
                    type="button"
                    onClick={() => onSaveLabels(labels.filter((l) => l !== label))}
                    disabled={busy}
                    aria-label={t('chats.info.removeLabel', { label })}
                    className="grid h-3.5 w-3.5 place-items-center rounded-full hover:bg-fill-hover"
                  >
                    <X size={9} aria-hidden />
                  </button>
                </span>
              ))}
          </div>
        )}
      </InfoCard>

      <InfoCard title={t('chats.info.activity')}>
        <p className="text-[12px] leading-relaxed text-text-mid">
          {t('chats.info.messages', { count: active.messages.filter((m) => m.source !== 'system').length })} ·{' '}
          {t('chats.info.aiReplies', { count: active.messages.filter((m) => m.source === 'ai').length })}
          {active.messages.some((m) => m.private) && (
            <> · {t('chats.info.privateNotes', { count: active.messages.filter((m) => m.private).length })}</>
          )}
        </p>
        {sandbox && <p className="mt-1 text-[12px] text-amber-300">{t('chats.info.sandbox')}</p>}
      </InfoCard>
    </aside>
  )
}
