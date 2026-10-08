'use client'

import { Bot, Loader2, MoreHorizontal, PhoneCall, Search, SlidersHorizontal, X } from 'lucide-react'
import { ChannelLogo } from '@/components/ui/ChannelLogo'
import { tagMeta } from '@/lib/tags'
import { Avatar, StatusTick } from './MessageParts'
import { CHANNEL_META, listStamp, logoKey, statusLabel, type Channel, type UiThreadSummary } from './inboxTypes'
import type { FilterChip } from './inboxFilters'

export type ViewTab = { id: string; name: string; active: boolean; modified?: boolean }

/** Conversation list pane: header, search, filter button + chips, saved-view
 *  tabs and the rows themselves. Pure presentation — the page owns the data. */
export default function ChatListPane({
  rows,
  total,
  loading,
  hasMore,
  loadingMore,
  onLoadMore,
  activeId,
  onOpen,
  search,
  onSearch,
  activeFilterCount,
  chips,
  onRemoveChip,
  onClearFilters,
  onOpenFilters,
  tabs,
  onSelectTab,
  onManageViews,
  onSaveViewChanges,
  hidden,
}: {
  rows: UiThreadSummary[]
  total: number | null
  loading: boolean
  hasMore: boolean
  loadingMore: boolean
  onLoadMore: () => void
  activeId: { channel: Channel; key: string } | null
  onOpen: (row: UiThreadSummary) => void
  search: string
  onSearch: (s: string) => void
  activeFilterCount: number
  chips: FilterChip[]
  onRemoveChip: (chip: FilterChip) => void
  onClearFilters: () => void
  onOpenFilters: () => void
  tabs: ViewTab[]
  onSelectTab: (id: string) => void
  onManageViews: () => void
  onSaveViewChanges?: () => void
  /** mobile: hidden while a conversation is open */
  hidden: boolean
}) {
  const modifiedTab = tabs.find((t) => t.active && t.modified)
  return (
    <section
      aria-label="Conversations"
      className={`min-h-0 w-full shrink-0 flex-col border-r border-line bg-surface lg:w-[320px] xl:w-[340px] ${
        hidden ? 'hidden lg:flex' : 'flex'
      }`}
    >
      <div className="flex flex-col gap-3 border-b border-line px-4 pb-0 pt-4">
        <div className="flex items-baseline gap-2.5">
          <h1 className="display text-[28px] leading-none text-text-heading">Chats</h1>
          <span className="text-[13px] text-text-muted" aria-live="polite">
            {total === null ? '' : `${total} ${total === 1 ? 'conversation' : 'conversations'}`}
          </span>
        </div>

        <div className="flex gap-2">
          <label className="flex h-9 min-w-0 flex-1 items-center gap-2 rounded-lg border border-line-strong bg-card px-2.5 focus-within:border-text-primary">
            <Search size={14} className="shrink-0 text-text-muted" aria-hidden />
            <input
              value={search}
              onChange={(e) => onSearch(e.target.value)}
              placeholder="Name, number or message"
              aria-label="Search chats"
              className="min-w-0 flex-1 bg-transparent text-[13px] text-text-primary outline-none placeholder:text-text-faint"
            />
            {search && (
              <button type="button" onClick={() => onSearch('')} aria-label="Clear search" className="text-text-muted hover:text-text-high">
                <X size={13} aria-hidden />
              </button>
            )}
          </label>
          <button
            type="button"
            onClick={onOpenFilters}
            className="inline-flex h-9 shrink-0 items-center gap-1.5 rounded-lg bg-primary px-3 text-[13px] font-semibold text-on-primary transition-opacity hover:opacity-90"
          >
            <SlidersHorizontal size={14} aria-hidden /> Filter
            {activeFilterCount > 0 && (
              <span className="rounded-full bg-accent px-1.5 text-[11px] font-bold leading-[18px] text-[color:var(--on-accent)]">
                {activeFilterCount}
              </span>
            )}
          </button>
        </div>

        {chips.length > 0 && (
          <div className="flex flex-wrap gap-1.5">
            {chips.map((c) => (
              <span
                key={c.key}
                className="inline-flex max-w-full items-center gap-1.5 rounded-full border border-line-strong bg-card py-[3px] pl-2.5 pr-1.5 text-[12px] text-text-high"
              >
                <span className="truncate">{c.label}</span>
                <button
                  type="button"
                  onClick={() => onRemoveChip(c)}
                  aria-label={`Remove filter: ${c.label}`}
                  className="grid h-4 w-4 shrink-0 place-items-center rounded-full text-text-muted hover:bg-fill-hover hover:text-text-high"
                >
                  <X size={11} aria-hidden />
                </button>
              </span>
            ))}
            {chips.length > 1 && (
              <button type="button" onClick={onClearFilters} className="px-1 text-[12px] font-semibold text-[color:var(--brand-ink)] hover:underline">
                Clear all
              </button>
            )}
          </div>
        )}

        {/* saved views: All · Mine · Unassigned · the operator's own */}
        <div className="-mb-px flex items-end gap-1">
          <div className="no-scrollbar flex min-w-0 flex-1 gap-3.5 overflow-x-auto" role="tablist" aria-label="Views">
            {tabs.map((t) => (
              <button
                key={t.id}
                type="button"
                role="tab"
                aria-selected={t.active}
                onClick={() => onSelectTab(t.id)}
                className={`shrink-0 whitespace-nowrap border-b-2 pb-2 text-[13px] transition-colors ${
                  t.active
                    ? 'border-[color:var(--accent)] font-semibold text-text-heading'
                    : 'border-transparent text-text-muted hover:text-text-high'
                }`}
              >
                {t.name}
                {t.active && t.modified && <span className="ml-0.5 text-[color:var(--brand-ink)]" title="Filters changed since this view was saved">•</span>}
              </button>
            ))}
          </div>
          <button
            type="button"
            onClick={onManageViews}
            title="Manage saved views"
            aria-label="Manage saved views"
            className="mb-1 grid h-7 w-7 shrink-0 place-items-center rounded-md text-text-muted hover:bg-fill-hover hover:text-text-high"
          >
            <MoreHorizontal size={15} aria-hidden />
          </button>
        </div>
      </div>

      {modifiedTab && onSaveViewChanges && (
        <div className="flex items-center justify-between gap-2 border-b border-line bg-well px-4 py-1.5 text-[12px] text-text-mid">
          <span className="truncate">“{modifiedTab.name}” has unsaved filter changes</span>
          <button type="button" onClick={onSaveViewChanges} className="shrink-0 font-semibold text-text-high hover:underline">
            Update view
          </button>
        </div>
      )}

      <div className="min-h-0 flex-1 overflow-y-auto">
        {loading && rows.length === 0 && (
          <div className="flex items-center justify-center gap-2 px-4 py-10 text-[12.5px] text-text-muted">
            <Loader2 size={14} className="animate-spin" aria-hidden /> Loading conversations…
          </div>
        )}
        {!loading && rows.length === 0 && (
          <div className="px-6 py-10 text-center text-[13px] text-text-muted">
            {activeFilterCount > 0 || search.trim()
              ? 'No conversations match these filters.'
              : 'No conversations yet — they appear here as customers write in.'}
            {(activeFilterCount > 0 || search.trim()) && (
              <div className="mt-2">
                <button type="button" onClick={onClearFilters} className="font-semibold text-text-high underline-offset-2 hover:underline">
                  Clear filters
                </button>
              </div>
            )}
          </div>
        )}
        {rows.map((t) => {
          const selected = !!activeId && t.channel === activeId.channel && t.key === activeId.key
          const merged = (t._mergedChannels?.length ?? 0) > 1
          const callLive = !!t.call && ['in_call', 'ringing', 'connecting'].includes(t.call.status)
          return (
            <button
              key={`${t.channel}-${t.key}${t.pending ? '-pending' : ''}`}
              type="button"
              onClick={() => onOpen(t)}
              aria-current={selected ? 'true' : undefined}
              className={`flex w-full gap-2.5 border-b border-line px-4 py-3 text-left transition-colors ${
                selected ? 'bg-[var(--nav-active-bg)]' : 'hover:bg-fill'
              }`}
            >
              <Avatar name={t.name} channel={merged ? undefined : t.channel} strong={selected} />
              <span className="min-w-0 flex-1">
                <span className="flex items-baseline justify-between gap-2">
                  <span className={`truncate text-[13.5px] text-text-heading ${t.waiting || selected ? 'font-bold' : 'font-semibold'}`}>
                    {t.name}
                  </span>
                  <span className={`shrink-0 text-[11.5px] ${t.waiting ? 'font-semibold text-[color:var(--brand-ink)]' : 'text-text-muted'}`}>
                    {listStamp(t.last_ts)}
                  </span>
                </span>
                <span className="mt-0.5 flex items-center gap-1 text-[12.5px] text-text-mid">
                  {t.last_dir === 'out' && t.last_status && (
                    <span className="shrink-0 text-text-muted">
                      <StatusTick status={t.last_status} />
                    </span>
                  )}
                  <span className={`truncate ${t.pending ? 'italic text-text-muted' : ''}`}>{t.last_message || '—'}</span>
                  {t.waiting && <span className="ml-auto h-2 w-2 shrink-0 rounded-full bg-accent" title="Waiting for our reply" />}
                </span>
                {(merged || callLive || t.ai_enabled || (t.status && t.status !== 'open') || (t.labels ?? []).length > 0 || t.opted_out) && (
                  <span className="mt-1.5 flex flex-wrap items-center gap-1">
                    {merged &&
                      t._mergedChannels!.map((c) => (
                        <ChannelLogo key={c} channel={logoKey(c)} size={12} title={CHANNEL_META[c].lbl} />
                      ))}
                    {callLive && (
                      <span className="inline-flex items-center gap-0.5 rounded-full bg-green/15 px-1.5 text-[10.5px] font-semibold leading-[17px] text-green-300">
                        <PhoneCall size={9} aria-hidden /> {t.call!.status === 'in_call' ? 'On call' : t.call!.status}
                      </span>
                    )}
                    {t.status && t.status !== 'open' && (
                      <span className="rounded-full border border-line-strong px-1.5 text-[10.5px] leading-[17px] text-text-mid">
                        {statusLabel(t.status)}
                      </span>
                    )}
                    {(t.labels ?? []).slice(0, 3).map((l) => {
                      const m = tagMeta(l)
                      return (
                        <span
                          key={l}
                          className="inline-flex items-center gap-1 rounded-full border border-line-strong px-1.5 text-[10.5px] leading-[17px] text-text-mid"
                        >
                          <span className="h-1.5 w-1.5 rounded-full" style={{ background: m.color }} aria-hidden />
                          {m.label}
                        </span>
                      )
                    })}
                    {(t.labels ?? []).length > 3 && (
                      <span className="text-[10.5px] text-text-muted">+{(t.labels ?? []).length - 3}</span>
                    )}
                    {t.ai_enabled && !t.opted_out && (
                      <span className="inline-flex items-center gap-0.5 rounded-full bg-brand/15 px-1.5 text-[10.5px] font-semibold leading-[17px] text-brand-300">
                        <Bot size={9} aria-hidden /> AI on
                      </span>
                    )}
                    {t.opted_out && (
                      <span className="rounded-full bg-red/10 px-1.5 text-[10.5px] font-semibold leading-[17px] text-red-300">Opted out</span>
                    )}
                  </span>
                )}
              </span>
            </button>
          )
        })}
        {hasMore && (
          <div className="p-3 text-center">
            <button
              type="button"
              onClick={onLoadMore}
              disabled={loadingMore}
              className="inline-flex items-center gap-1.5 rounded-lg border border-line-strong bg-card px-3 py-1.5 text-[12.5px] font-semibold text-text-high hover:bg-fill-hover disabled:opacity-50"
            >
              {loadingMore && <Loader2 size={12} className="animate-spin" aria-hidden />} Load more
            </button>
          </div>
        )}
      </div>
    </section>
  )
}
