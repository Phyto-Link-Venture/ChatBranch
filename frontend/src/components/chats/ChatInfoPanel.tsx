'use client'

import type { ReactNode } from 'react'
import Link from 'next/link'
import { ExternalLink, Link2, Mail, X } from 'lucide-react'
import { Select } from '@/components/ui/Select'
import { ChannelLogo } from '@/components/ui/ChannelLogo'
import ChannelHub from '@/components/ChannelHub'
import { CONVERSATION_TAGS } from '@/lib/tags'
import { tint } from '@/lib/color'
import { Avatar, BetaChip } from './MessageParts'
import {
  CHANNEL_META,
  THREAD_STATUSES,
  logoKey,
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
}) {
  const labels = active.labels ?? []
  const meta = CHANNEL_META[active.channel]
  return (
    <aside
      aria-label="Conversation details"
      className="hidden w-[288px] shrink-0 flex-col gap-3 overflow-y-auto border-l border-line bg-surface p-3 xl:flex"
    >
      <section className="flex flex-col items-center rounded-xl border border-line bg-card px-4 py-5 text-center">
        <Avatar name={active.name} size={56} strong />
        <div className="display mt-2.5 text-[20px] leading-tight text-text-heading">{active.name}</div>
        <div className="mt-1 flex items-center justify-center gap-1.5 text-[12px] text-text-muted">
          <ChannelLogo channel={logoKey(active.channel)} size={13} />
          {meta.lbl}
          {meta.beta && <BetaChip />}
        </div>
        <div className="mt-0.5 max-w-full truncate text-[12px] text-text-mid">{active.sub}</div>
      </section>

      <InfoCard
        title="Contact"
        note={
          active.contact_id != null
            ? 'Every channel this contact uses merges in the "All channels" view above the chat.'
            : 'Linking joins this chat with the same person’s other channels (WhatsApp links by phone automatically, email by address).'
        }
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
              Unlink
            </button>
          </div>
        ) : (
          <div className="flex items-center gap-1.5">
            <Select
              value={linkSel}
              onChange={onLinkSel}
              options={linkContacts.map((c) => ({ value: String(c.id), label: c.name, hint: c.phone ? `(${c.phone})` : undefined }))}
              placeholder="Link to a contact…"
              ariaLabel="Link to contact"
              className="min-w-0 flex-1"
              searchable
            />
            <button
              type="button"
              onClick={() => linkSel && onLink(Number(linkSel))}
              disabled={busy || !linkSel}
              title="Link contact"
              aria-label="Link contact"
              className="grid h-8 w-8 shrink-0 place-items-center rounded-lg border border-line-strong bg-card text-text-muted transition-colors hover:text-text-high disabled:opacity-35"
            >
              <Link2 size={13} aria-hidden />
            </button>
          </div>
        )}
      </InfoCard>

      {active.contact_id != null && (
        <InfoCard title="Channels">
          <ChannelHub contactId={active.contact_id} />
        </InfoCard>
      )}

      <InfoCard title="Conversation" note="A new customer message re-opens a resolved or snoozed conversation.">
        <div className="flex flex-col gap-2.5">
          <label className="flex flex-col gap-1">
            <span className="text-[11.5px] font-semibold text-text-mid">Status</span>
            <Select
              value={active.status ?? 'open'}
              onChange={(v) => onSetStatus(v as ThreadStatus)}
              options={THREAD_STATUSES.map((s) => ({ value: s, label: statusLabel(s) }))}
              disabled={busy}
              size="md"
              ariaLabel="Conversation status"
            />
          </label>
          {teams.length > 0 && (
            <label className="flex flex-col gap-1">
              <span className="text-[11.5px] font-semibold text-text-mid">Team</span>
              <Select
                value={active.team_id ? String(active.team_id) : ''}
                onChange={onAssignTeam}
                options={[{ value: '', label: 'No team' }, ...teams.map((t) => ({ value: String(t.id), label: t.name }))]}
                disabled={busy}
                size="md"
                ariaLabel="Owning team"
              />
            </label>
          )}
          <label className="flex flex-col gap-1">
            <span className="text-[11.5px] font-semibold text-text-mid">Assignee</span>
            <Select
              value={active.assignee ?? ''}
              onChange={onAssign}
              options={[
                { value: '', label: 'Unassigned' },
                ...agents.map((a) => ({ value: a.username, label: a.name, hint: `(${a.role})` })),
                // keep an assignee visible even if their profile disappears
                ...(active.assignee && !agents.some((a) => a.username === active.assignee)
                  ? [{ value: active.assignee, label: active.assignee }]
                  : []),
              ]}
              disabled={busy}
              size="md"
              ariaLabel="Assigned agent"
            />
          </label>
        </div>
      </InfoCard>

      {active.channel === 'whatsapp' && waAccounts.length > 0 && (
        <InfoCard
          title="Sending account"
          note="Switching starts a new chat on the customer’s side — free text unlocks after they reply to the new number (use a template to open it)."
        >
          <div className="flex items-center justify-between gap-2">
            <span className="flex min-w-0 items-center gap-1.5 text-[13px] font-semibold text-text-high">
              <ChannelLogo channel="whatsapp" size={13} />
              <span className="truncate">
                {waAccounts.find((a) => a.id === String(active.account_id ?? ''))?.name ??
                  (active.account_id ? `account #${active.account_id}` : 'Auto')}
              </span>
            </span>
          </div>
          {canManage && waAccounts.length > 1 && (
            <Select
              value={String(active.account_id ?? '')}
              onChange={onSwitchAccount}
              options={[
                ...(!active.account_id ? [{ value: '', label: 'Auto' }] : []),
                ...waAccounts.map((a) => ({ value: a.id, label: a.name })),
              ]}
              disabled={busy}
              ariaLabel="Switch sending account"
              className="mt-2"
            />
          )}
        </InfoCard>
      )}

      {active.channel === 'email' && (
        <InfoCard title="Email">
          <p className="flex gap-1.5 text-[12px] leading-relaxed text-text-mid">
            <Mail size={13} className="mt-0.5 shrink-0 text-text-muted" aria-hidden />
            {imapConfigured
              ? 'Two-way email — sends go via SMTP and replies are pulled from the IMAP inbox about once a minute.'
              : 'Outbound-only — replies arrive in your mailbox, not here. Set the IMAP host in Channels to pull them in.'}
          </p>
        </InfoCard>
      )}

      <InfoCard title="Labels">
        <div className="flex flex-wrap gap-1.5">
          {CONVERSATION_TAGS.map((tg) => {
            const on = labels.includes(tg.slug)
            return (
              <button
                key={tg.slug}
                type="button"
                aria-pressed={on}
                onClick={() => onSaveLabels(on ? labels.filter((l) => l !== tg.slug) : [...labels, tg.slug])}
                disabled={busy}
                title={on ? `Remove ${tg.label}` : `Add ${tg.label}`}
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
        {labels.some((l) => !CONVERSATION_TAGS.some((t) => t.slug === l)) && (
          <div className="mt-2 flex flex-wrap gap-1">
            {labels
              .filter((l) => !CONVERSATION_TAGS.some((t) => t.slug === l))
              .map((label) => (
                <span key={label} className="inline-flex items-center gap-1 rounded-full border border-line px-2 py-0.5 text-[11.5px] text-text-mid">
                  {label}
                  <button
                    type="button"
                    onClick={() => onSaveLabels(labels.filter((l) => l !== label))}
                    disabled={busy}
                    aria-label={`Remove ${label}`}
                    className="grid h-3.5 w-3.5 place-items-center rounded-full hover:bg-fill-hover"
                  >
                    <X size={9} aria-hidden />
                  </button>
                </span>
              ))}
          </div>
        )}
      </InfoCard>

      <InfoCard title="Activity">
        <p className="text-[12px] leading-relaxed text-text-mid">
          {active.messages.filter((m) => m.source !== 'system').length} messages ·{' '}
          {active.messages.filter((m) => m.source === 'ai').length} AI replies
          {active.messages.some((m) => m.private) && <> · {active.messages.filter((m) => m.private).length} private notes</>}
        </p>
        {sandbox && <p className="mt-1 text-[12px] text-amber-300">Sandbox — nothing is sent externally.</p>}
      </InfoCard>
    </aside>
  )
}
