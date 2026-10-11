'use client'

import type { ReactNode } from 'react'
import { Bot, Building2, CheckSquare, Hand, Loader2, MoreHorizontal, PhoneCall, Search, SlidersHorizontal, Square, X } from 'lucide-react'
import { ChannelLogo } from '@/components/ui/ChannelLogo'
import { tagMeta } from '@/lib/tags'
import { handoverKindLabel } from '@/lib/aiControls'
import { Avatar, StatusTick } from './MessageParts'
import { channelName, listStamp, logoKey, sameThread, statusLabel, threadIdentity, type ThreadRef, type UiThreadSummary } from './inboxTypes'
import { isHeaderSort, type FilterChip, type HeaderSort, type Sort, type TriState } from './inboxFilters'
import { Segmented } from './Segmented'
import { useFormat, useT } from '@/i18n'

export type ViewTab = { id: string; name: string; active: boolean; modified?: boolean; count?: number | null }

/** Row identity for multi-select: `channel:key#thread_id` — a customer has one conversation per company. */
export const rowRef = (t: { channel: string; key: string; thread_id?: number | null }) => threadIdentity(t)

/** Conversation list pane: header, search, filter button, the quick contact
 *  toggle + order switch, chips, saved-view tabs and the rows themselves. Pure
 *  presentation — the page owns the data. */
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
  linked,
  onLinked,
  sort,
  onSort,
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
  multiCompany = false,
  selectMode = false,
  selected: pickedRefs,
  onToggleSelect,
  onToggleSelectMode,
  footer,
}: {
  rows: UiThreadSummary[]
  total: number | null
  loading: boolean
  hasMore: boolean
  loadingMore: boolean
  onLoadMore: () => void
  activeId: ThreadRef | null
  onOpen: (row: UiThreadSummary) => void
  /** the caller sees conversations of more than one company: each row names its company */
  multiCompany?: boolean
  search: string
  onSearch: (s: string) => void
  /** the `linked` filter (the same one as the filter pop-up's "Contact"): '' all · yes · no */
  linked: TriState
  onLinked: (v: TriState) => void
  /** the list's order; the header switch offers newest / customer_newest */
  sort: Sort
  onSort: (s: HeaderSort) => void
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
  /** multi-select (bulk actions): rows show a checkbox and a click toggles them */
  selectMode?: boolean
  selected?: Set<string>
  onToggleSelect?: (row: UiThreadSummary) => void
  /** shows the "Select" button in the header (only for people who may act in bulk) */
  onToggleSelectMode?: () => void
  /** pinned under the list (the bulk bar) */
  footer?: ReactNode
}) {
  const t = useT()
  const f = useFormat()
  const modifiedTab = tabs.find((tab) => tab.active && tab.modified)
  return (
    <section
      aria-label={t('chats.list.sectionAria')}
      // a phone on its side (short and below lg): the header + list scroll as ONE
      // pane — a sticky header left the list a sliver of a scroll box
      className={`min-h-0 w-full shrink-0 flex-col border-r border-line bg-surface max-lg:[@media(max-height:560px)]:overflow-y-auto lg:w-[320px] xl:w-[340px] ${
        hidden ? 'hidden lg:flex' : 'flex'
      }`}
    >
      <div className="flex flex-col gap-3 border-b border-line px-4 pb-0 pt-4">
        <div className="flex items-baseline gap-2.5">
          <h1 className="display text-[28px] leading-none text-text-heading">{t('shell.nav.chats')}</h1>
          <span className="min-w-0 truncate text-[13px] text-text-muted" aria-live="polite">
            {total === null ? '' : t('chats.list.conversations', { count: total })}
          </span>
          {onToggleSelectMode && (
            <button
              type="button"
              onClick={onToggleSelectMode}
              aria-pressed={selectMode}
              className={`ml-auto inline-flex h-7 shrink-0 items-center gap-1 rounded-md px-2 text-[12px] font-semibold transition-colors ${
                selectMode ? 'bg-[var(--nav-active-bg)] text-text-heading' : 'text-text-muted hover:bg-fill-hover hover:text-text-high'
              }`}
            >
              <CheckSquare size={13} aria-hidden /> {selectMode ? t('common.actions.done') : t('common.actions.select')}
            </button>
          )}
        </div>

        <div className="flex gap-2">
          <label data-tour="chats-search" className="flex h-9 min-w-0 flex-1 items-center gap-2 rounded-lg border border-line-strong bg-card px-2.5 focus-within:border-text-primary">
            <Search size={14} className="shrink-0 text-text-muted" aria-hidden />
            <input
              value={search}
              onChange={(e) => onSearch(e.target.value)}
              placeholder={t('chats.list.searchPlaceholder')}
              aria-label={t('chats.list.searchAria')}
              className="min-w-0 flex-1 bg-transparent text-[13px] text-text-primary outline-none placeholder:text-text-faint"
            />
            {search && (
              <button type="button" onClick={() => onSearch('')} aria-label={t('chats.list.clearSearch')} className="text-text-muted hover:text-text-high">
                <X size={13} aria-hidden />
              </button>
            )}
          </label>
          <button
            data-tour="chats-filter"
            type="button"
            onClick={onOpenFilters}
            className="inline-flex h-9 shrink-0 items-center gap-1.5 rounded-lg bg-primary px-3 text-[13px] font-semibold text-on-primary transition-opacity hover:opacity-90"
          >
            <SlidersHorizontal size={14} aria-hidden /> {t('common.actions.filter')}
            {activeFilterCount > 0 && (
              <span className="rounded-full bg-accent px-1.5 text-[11px] font-bold leading-[18px] text-[color:var(--on-accent)]">
                {activeFilterCount}
              </span>
            )}
          </button>
        </div>

        {/* quick picks: the same `linked` filter and `sort` the filter pop-up edits */}
        <div data-tour="chats-quick" className="flex flex-col gap-1.5">
          <Segmented<TriState>
            label={t('chats.filterModal.quickContacts')}
            showLabel={false}
            fit
            value={linked}
            onChange={onLinked}
            options={[
              { value: '', label: t('chats.filterModal.quickAll') },
              { value: 'yes', label: t('chats.filterModal.quickContactsOnly') },
              { value: 'no', label: t('chats.filterModal.quickNotInContacts') },
            ]}
          />
          <Segmented<Sort>
            label={t('chats.filterModal.quickSort')}
            showLabel={false}
            fit
            value={sort}
            onChange={(s) => isHeaderSort(s) && onSort(s)}
            options={[
              { value: 'newest', label: t('chats.filterModal.sortNewestMessage') },
              { value: 'customer_newest', label: t('chats.filterModal.sortNewestFromCustomer') },
            ]}
          />
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
                  aria-label={t('chats.list.removeChip', { label: c.label })}
                  className="grid h-4 w-4 shrink-0 place-items-center rounded-full text-text-muted hover:bg-fill-hover hover:text-text-high"
                >
                  <X size={11} aria-hidden />
                </button>
              </span>
            ))}
            {chips.length > 1 && (
              <button type="button" onClick={onClearFilters} className="px-1 text-[12px] font-semibold text-[color:var(--brand-ink)] hover:underline">
                {t('chats.list.clearAll')}
              </button>
            )}
          </div>
        )}

        {/* saved views: All · Mine · Unassigned · the operator's own */}
        <div className="-mb-px flex items-end gap-1">
          <div data-tour="chats-tabs" className="no-scrollbar flex min-w-0 flex-1 gap-3.5 overflow-x-auto" role="tablist" aria-label={t('chats.list.viewsAria')}>
            {tabs.map((tab) => (
              <button
                key={tab.id}
                type="button"
                role="tab"
                aria-selected={tab.active}
                onClick={() => onSelectTab(tab.id)}
                className={`shrink-0 whitespace-nowrap border-b-2 pb-2 text-[13px] transition-colors ${
                  tab.active
                    ? 'border-[color:var(--accent)] font-semibold text-text-heading'
                    : 'border-transparent text-text-muted hover:text-text-high'
                }`}
              >
                {tab.name}
                {typeof tab.count === 'number' && tab.count > 0 && (
                  <span className="ml-1 rounded-full bg-amber/15 px-1.5 text-[10.5px] font-bold tabular-nums leading-[16px] text-amber-300">
                    {tab.count > 99 ? '99+' : f.number(tab.count)}
                  </span>
                )}
                {tab.active && tab.modified && (
                  <span className="ml-0.5 text-[color:var(--brand-ink)]" title={t('chats.list.modifiedHint')}>
                    •
                  </span>
                )}
              </button>
            ))}
          </div>
          <button
            type="button"
            onClick={onManageViews}
            title={t('chats.list.manageViews')}
            aria-label={t('chats.list.manageViews')}
            className="mb-1 grid h-7 w-7 shrink-0 place-items-center rounded-md text-text-muted hover:bg-fill-hover hover:text-text-high"
          >
            <MoreHorizontal size={15} aria-hidden />
          </button>
        </div>
      </div>

      {modifiedTab && onSaveViewChanges && (
        <div className="flex items-center justify-between gap-2 border-b border-line bg-well px-4 py-1.5 text-[12px] text-text-mid">
          <span className="truncate">{t('chats.list.unsavedChanges', { name: modifiedTab.name })}</span>
          <button type="button" onClick={onSaveViewChanges} className="shrink-0 font-semibold text-text-high hover:underline">
            {t('chats.list.updateView')}
          </button>
        </div>
      )}

      <div
        data-tour="chats-list"
        className="min-h-0 flex-1 overflow-y-auto max-lg:[@media(max-height:560px)]:flex-none max-lg:[@media(max-height:560px)]:overflow-visible"
      >
        {loading && rows.length === 0 && (
          <div className="flex items-center justify-center gap-2 px-4 py-10 text-[12.5px] text-text-muted">
            <Loader2 size={14} className="animate-spin" aria-hidden /> {t('chats.list.loading')}
          </div>
        )}
        {!loading && rows.length === 0 && (
          <div className="px-6 py-10 text-center text-[13px] text-text-muted">
            {activeFilterCount > 0 || search.trim() ? t('chats.list.emptyFiltered') : t('chats.list.empty')}
            {(activeFilterCount > 0 || search.trim()) && (
              <div className="mt-2">
                <button type="button" onClick={onClearFilters} className="font-semibold text-text-high underline-offset-2 hover:underline">
                  {t('chats.list.clearFilters')}
                </button>
              </div>
            )}
          </div>
        )}
        {rows.map((row) => {
          const selected = !row.pending && sameThread(row, activeId)
          const merged = (row._mergedChannels?.length ?? 0) > 1
          const callLive = !!row.call && ['in_call', 'ringing', 'connecting'].includes(row.call.status)
          const picking = selectMode && !row.pending
          const picked = picking && !!pickedRefs?.has(rowRef(row))
          const handedOver = row.handover?.open ? row.handover : null
          const callStatus = row.call?.status
          // "Newest from customer" orders by when THEY last wrote — the stamp says that time
          const byCustomer = sort === 'customer_newest' && !!row.last_inbound_ts
          const stamp = byCustomer ? row.last_inbound_ts! : row.last_ts
          return (
            <button
              key={`${threadIdentity(row)}${row.pending ? '-pending' : ''}`}
              type="button"
              onClick={() => (picking ? onToggleSelect?.(row) : onOpen(row))}
              aria-current={selected ? 'true' : undefined}
              aria-pressed={picking ? picked : undefined}
              className={`flex w-full gap-2.5 border-b border-line px-4 py-3 text-left transition-colors ${
                selected || picked ? 'bg-[var(--nav-active-bg)]' : 'hover:bg-fill'
              }`}
            >
              {picking && (
                <span className="mt-2 shrink-0 text-text-mid" aria-hidden>
                  {picked ? <CheckSquare size={16} className="text-text-heading" /> : <Square size={16} />}
                </span>
              )}
              <Avatar name={row.name} channel={merged ? undefined : row.channel} strong={selected} />
              <span className="min-w-0 flex-1">
                <span className="flex items-baseline justify-between gap-2">
                  <span className={`truncate text-[13.5px] text-text-heading ${row.waiting || selected ? 'font-bold' : 'font-semibold'}`}>
                    {row.name}
                  </span>
                  <span
                    className={`shrink-0 text-[11.5px] ${row.waiting ? 'font-semibold text-[color:var(--brand-ink)]' : 'text-text-muted'}`}
                    title={byCustomer ? t('chats.list.customerTime') : undefined}
                  >
                    {listStamp(stamp, f)}
                  </span>
                </span>
                <span className="mt-0.5 flex items-center gap-1 text-[12.5px] text-text-mid">
                  {row.last_dir === 'out' && row.last_status && (
                    <span className="shrink-0 text-text-muted">
                      <StatusTick status={row.last_status} />
                    </span>
                  )}
                  <span className={`truncate ${row.pending ? 'italic text-text-muted' : ''}`}>
                    {row.pending ? t('chats.list.noConversationYet') : row.last_message || '—'}
                  </span>
                  {row.waiting && <span className="ml-auto h-2 w-2 shrink-0 rounded-full bg-accent" title={t('chats.list.waitingDot')} />}
                </span>
                {((multiCompany && !!row.company) || merged || callLive || row.ai_enabled || handedOver || (row.status && row.status !== 'open') || (row.labels ?? []).length > 0 || row.opted_out) && (
                  <span className="mt-1.5 flex flex-wrap items-center gap-1">
                    {multiCompany && row.company && (
                      // one customer can write to several companies: which company's conversation this is
                      <span
                        className="inline-flex max-w-full items-center gap-0.5 rounded-full border border-line-strong px-1.5 text-[10.5px] font-semibold leading-[17px] text-text-high"
                        title={t('chats.company.rowHint', { name: row.company.name })}
                      >
                        <Building2 size={9} className="shrink-0" aria-hidden />
                        <span className="truncate">{row.company.name}</span>
                      </span>
                    )}
                    {handedOver && (
                      <span
                        className="inline-flex max-w-full items-center gap-0.5 rounded-full bg-amber/15 px-1.5 text-[10.5px] font-semibold leading-[17px] text-amber-300"
                        title={`${handoverKindLabel(t, handedOver.kind, handedOver.kind_label)} — ${handedOver.reason}`}
                      >
                        <Hand size={9} className="shrink-0" aria-hidden />
                        <span className="truncate">
                          {t(handedOver.by === 'ai' ? 'chats.list.aiHandedOver' : 'chats.list.handedOver')} · {handedOver.reason}
                        </span>
                      </span>
                    )}
                    {merged &&
                      row._mergedChannels!.map((c) => (
                        <ChannelLogo key={c} channel={logoKey(c)} size={12} title={channelName(t, c)} />
                      ))}
                    {callLive && (
                      <span className="inline-flex items-center gap-0.5 rounded-full bg-green/15 px-1.5 text-[10.5px] font-semibold leading-[17px] text-green-300">
                        <PhoneCall size={9} aria-hidden />{' '}
                        {callStatus === 'in_call'
                          ? t('chats.list.onCall')
                          : callStatus === 'ringing'
                            ? t('chats.list.ringing')
                            : callStatus === 'connecting'
                              ? t('chats.list.connecting')
                              : callStatus}
                      </span>
                    )}
                    {row.status && row.status !== 'open' && (
                      <span className="rounded-full border border-line-strong px-1.5 text-[10.5px] leading-[17px] text-text-mid">
                        {statusLabel(t, row.status)}
                      </span>
                    )}
                    {(row.labels ?? []).slice(0, 3).map((l) => {
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
                    {(row.labels ?? []).length > 3 && (
                      <span className="text-[10.5px] text-text-muted">+{(row.labels ?? []).length - 3}</span>
                    )}
                    {row.ai_enabled && !row.opted_out && !row.pending && row.channel !== 'email' && !row.contact_id ? (
                      // same state as the chat header: the AI only answers a chat linked to a contact
                      <span
                        title={t('chats.page.aiNeedsContactHint')}
                        className="inline-flex items-center gap-0.5 rounded-full border border-line px-1.5 text-[10.5px] font-semibold leading-[17px] text-[var(--amber-ink)]"
                      >
                        <Bot size={9} aria-hidden /> {t('chats.page.aiNeedsContact')}
                      </span>
                    ) : (
                      row.ai_enabled &&
                      !row.opted_out && (
                        <span className="inline-flex items-center gap-0.5 rounded-full bg-brand/15 px-1.5 text-[10.5px] font-semibold leading-[17px] text-brand-300">
                          <Bot size={9} aria-hidden /> {t('chats.list.aiOn')}
                        </span>
                      )
                    )}
                    {row.opted_out && (
                      <span className="rounded-full bg-red/10 px-1.5 text-[10.5px] font-semibold leading-[17px] text-red-300">
                        {t('chats.list.optedOut')}
                      </span>
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
              {loadingMore && <Loader2 size={12} className="animate-spin" aria-hidden />} {t('chats.list.loadMore')}
            </button>
          </div>
        )}
      </div>
      {footer}
    </section>
  )
}
