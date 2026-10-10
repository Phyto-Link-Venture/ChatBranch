'use client'

/**
 * The playground's chat: the operator writes as the customer; each AI turn
 * renders as the bubbles it would send, with a production chip (would send ·
 * held · hand over · blocked · AI off) and its actions — inspect, regenerate
 * the last one, 👍 / "This should have said…". With two set-ups the AI turns
 * sit side by side, one column each. Bubbles are the inbox's Paper & Ink
 * tokens (app/chats/chats.css).
 */

import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from 'react'
import {
  Check,
  Loader2,
  MessageSquareWarning,
  Pencil,
  RefreshCw,
  ScanSearch,
  Send,
  ThumbsUp,
  X,
  type LucideIcon,
} from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { useT } from '@/i18n'
import { plannedReactions, resultText, type ColumnResult, type Entry } from '@/lib/aiPlayground'
import '@/components/chats/messageExtras.css'
import { ProductionChip } from './ProductionChip'
import { fieldCls } from './SetupPane'

export interface Selection {
  entryId: string
  column: number
}

function AiCell({
  result,
  entryId,
  column,
  isLast,
  busy,
  selected,
  canTeach,
  compact,
  onSelect,
  onRegenerate,
  onTeach,
}: {
  result: ColumnResult | null
  entryId: string
  column: number
  isLast: boolean
  busy: boolean
  selected: boolean
  canTeach: boolean
  /** Two set-ups side by side: icon-only actions. */
  compact: boolean
  onSelect: (s: Selection) => void
  onRegenerate: (column: number) => void
  onTeach: (entryId: string, column: number, kind: 'good' | 'bad') => void
}) {
  const t = useT()
  if (!result) return <div className="text-right text-[12px] text-text-faint">—</div>
  if (result.pending)
    return (
      <div className="flex items-center justify-end gap-2 text-[12px] text-text-muted" role="status">
        <Loader2 size={13} className="animate-spin" aria-hidden /> {t('playground.chat.writing')}
      </div>
    )
  if (result.error || !result.reply)
    return (
      <div className="flex flex-col items-end gap-1.5">
        <div role="alert" className="rounded-lg border border-red/30 bg-red/10 px-3 py-2 text-[12.5px] text-[color:var(--red-ink)]">
          {result.error === 'interrupted'
            ? t('playground.chat.interrupted')
            : t('playground.chat.failed', { error: result.error ?? '' })}
        </div>
        {isLast && (
          <Button size="sm" variant="ghost" onClick={() => onRegenerate(column)} disabled={busy}>
            <RefreshCw size={12} aria-hidden /> {t('playground.chat.regenerate')}
          </Button>
        )}
      </div>
    )
  const r = result.reply
  const bubbles = r.bubbles.length > 0 ? r.bubbles : resultText(result) ? [resultText(result)] : []
  const sent = r.production.verdict === 'send' || (r.production.verdict === 'handover' && !!r.reply)
  return (
    <div className="flex flex-col items-end gap-1.5">
      {bubbles.length === 0 ? (
        <div className="text-[12px] italic text-text-muted">
          {r.reaction
            ? t('playground.chat.reactionOnly', { emoji: r.reaction.emoji })
            : t('playground.chat.noText')}
        </div>
      ) : (
        bubbles.map((b, i) => (
          <div key={i} className={`cb-bubble cb-bubble-out ${sent ? '' : 'opacity-75'}`} style={{ maxWidth: '100%' }}>
            {i === 0 && <span className="cb-bubble-source">{t('playground.chat.ai')}</span>}
            {/* the quote production would send with the first bubble — shown, never sent */}
            {i === 0 && r.quote && (
              <span className="cb-quote" title={t('playground.chat.wouldQuote')}>
                <span className="cb-quote-author">{t('playground.chat.customer')}</span>
                <span className="cb-quote-text">{r.quote.text}</span>
              </span>
            )}
            {b}
          </div>
        ))
      )}
      {r.reaction && bubbles.length > 0 && (
        <span className="text-[11px] text-text-muted">
          {t('playground.chat.wouldReact', { emoji: r.reaction.emoji })}
        </span>
      )}
      <div className="flex max-w-full flex-wrap items-center justify-end gap-1.5">
        {!sent && bubbles.length > 0 && (
          <span className="text-[11px] font-semibold text-text-muted">{t('playground.chat.drafted')}</span>
        )}
        {bubbles.length > 1 && (
          <span className="text-[11px] text-text-muted">{t('playground.chat.bubbles', { count: bubbles.length })}</span>
        )}
        <ProductionChip production={r.production} />
      </div>
      <div className="flex flex-wrap items-center justify-end gap-0.5">
        <Action
          Icon={ScanSearch}
          label={selected ? t('playground.chat.selected') : t('playground.chat.inspect')}
          compact={compact}
          pressed={selected}
          onClick={() => onSelect({ entryId, column })}
        />
        {isLast && (
          <Action Icon={RefreshCw} label={t('playground.chat.regenerate')} compact={compact} disabled={busy}
            onClick={() => onRegenerate(column)} />
        )}
        {canTeach && resultText(result) && (
          <>
            <Action Icon={ThumbsUp} label={t('playground.teach.good')} compact onClick={() => onTeach(entryId, column, 'good')} />
            <Action Icon={MessageSquareWarning} label={t('playground.teach.bad')} compact={compact}
              onClick={() => onTeach(entryId, column, 'bad')} />
          </>
        )}
      </div>
    </div>
  )
}

/** One small action under an AI turn — icon only (named for assistive tech) when space is tight. */
function Action({
  Icon,
  label,
  compact,
  pressed,
  disabled,
  onClick,
}: {
  Icon: LucideIcon
  label: string
  compact: boolean
  pressed?: boolean
  disabled?: boolean
  onClick: () => void
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      aria-pressed={pressed}
      aria-label={compact ? label : undefined}
      title={compact ? label : undefined}
      className={`inline-flex h-7 items-center gap-1 rounded-md text-[11.5px] font-semibold transition-colors disabled:opacity-50 ${
        compact ? 'w-7 justify-center' : 'px-2'
      } ${pressed ? 'bg-fill text-text-heading' : 'text-text-muted hover:bg-fill-hover hover:text-text-high'}`}
    >
      <Icon size={12} aria-hidden />
      {!compact && label}
    </button>
  )
}

export function ChatPane({
  entries,
  columnNames,
  busy,
  maxTurns,
  maxChars,
  selected,
  canTeach,
  onSend,
  onAnswer,
  onRegenerate,
  onEditCustomer,
  onSelect,
  onTeach,
}: {
  entries: Entry[]
  columnNames: string[]
  busy: boolean
  maxTurns: number
  maxChars: number
  selected: Selection | null
  canTeach: boolean
  onSend: (text: string) => void
  onAnswer: () => void
  onRegenerate: (column?: number) => void
  onEditCustomer: (entryId: string, text: string) => void
  onSelect: (s: Selection) => void
  onTeach: (entryId: string, column: number, kind: 'good' | 'bad') => void
}) {
  const t = useT()
  const [draft, setDraft] = useState('')
  const [editing, setEditing] = useState<{ id: string; text: string } | null>(null)
  const listRef = useRef<HTMLDivElement>(null)
  const columns = columnNames.length
  const last = entries[entries.length - 1]
  // every entry is one turn of the transcript the next reply sends
  const full = entries.length >= maxTurns
  // the reactions the AI turns would make, on the customer bubbles they target
  const reactions = useMemo(() => plannedReactions(entries), [entries])

  // keep the newest turn in view
  useEffect(() => {
    const el = listRef.current
    if (el) el.scrollTop = el.scrollHeight
  }, [entries])

  const send = () => {
    const text = draft.trim()
    if (!text || busy || full) return
    onSend(text.slice(0, maxChars))
    setDraft('')
  }
  const onKey = (e: KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) {
      e.preventDefault()
      send()
    }
  }

  return (
    <div className="flex h-full min-h-0 flex-col">
      <div ref={listRef} className="cb-paper min-h-0 flex-1 overflow-y-auto rounded-xl border border-line p-3 sm:p-4" aria-live="polite">
        {entries.length === 0 ? (
          <div className="grid h-full min-h-[160px] place-items-center px-6 text-center text-[13px] text-text-muted">
            {t('playground.chat.empty')}
          </div>
        ) : (
          <div className="flex flex-col gap-3">
            {columns > 1 && (
              <div className="sticky top-0 z-[1] -mx-1 grid grid-cols-2 gap-3 bg-panel px-1 py-1">
                {columnNames.map((n) => (
                  <div key={n} className="eyebrow text-right">{n}</div>
                ))}
              </div>
            )}
            {entries.map((e, idx) => {
              if (e.kind === 'customer') {
                const isEditing = editing?.id === e.id
                return (
                  <div key={e.id} className="flex flex-col items-start gap-1">
                    {isEditing ? (
                      <div className="w-full max-w-[560px]">
                        <textarea
                          autoFocus
                          rows={3}
                          value={editing.text}
                          maxLength={maxChars}
                          onChange={(ev) => setEditing({ id: e.id, text: ev.target.value })}
                          aria-label={t('playground.chat.edit')}
                          className={`${fieldCls} resize-y`}
                        />
                        <div className="mt-1 flex flex-wrap items-center gap-2">
                          <Button
                            size="sm"
                            variant="primary"
                            disabled={!editing.text.trim() || busy}
                            onClick={() => {
                              onEditCustomer(e.id, editing.text.trim())
                              setEditing(null)
                            }}
                          >
                            <Check size={12} aria-hidden /> {t('playground.chat.save')}
                          </Button>
                          <Button size="sm" variant="ghost" onClick={() => setEditing(null)}>
                            <X size={12} aria-hidden /> {t('common.actions.cancel')}
                          </Button>
                          <span className="text-[11.5px] text-text-muted">{t('playground.chat.editHint')}</span>
                        </div>
                      </div>
                    ) : (
                      <>
                        <div className="cb-bubble cb-bubble-in">
                          <span className="cb-bubble-source">{t('playground.chat.customer')}</span>
                          {e.text}
                        </div>
                        {reactions.get(e.id)?.length ? (
                          <div className="cb-reactions justify-start">
                            {reactions.get(e.id)!.map((x) => (
                              <span
                                key={`${x.column}:${x.emoji}`}
                                className="cb-reaction-chip cb-reaction-mine"
                                title={
                                  columns > 1
                                    ? `${columnNames[x.column] ?? ''} — ${t('playground.chat.wouldReact', { emoji: x.emoji })}`
                                    : t('playground.chat.wouldReact', { emoji: x.emoji })
                                }
                              >
                                <span className="text-[13px] leading-none">{x.emoji}</span>
                              </span>
                            ))}
                          </div>
                        ) : null}
                        <button
                          type="button"
                          onClick={() => setEditing({ id: e.id, text: e.text })}
                          disabled={busy}
                          className="inline-flex h-6 items-center gap-1 rounded-md px-1.5 text-[11px] font-semibold text-text-muted hover:bg-fill-hover hover:text-text-high disabled:opacity-50"
                        >
                          <Pencil size={11} aria-hidden /> {t('playground.chat.edit')}
                        </button>
                      </>
                    )}
                  </div>
                )
              }
              if (e.kind === 'history')
                return (
                  <div key={e.id} className="flex justify-end">
                    <div className="cb-bubble cb-bubble-out opacity-70">
                      <span className="cb-bubble-source">{t('playground.chat.history')}</span>
                      {e.text}
                    </div>
                  </div>
                )
              const isLast = idx === entries.length - 1
              return (
                <div key={e.id} className={columns > 1 ? 'grid grid-cols-2 gap-3' : ''}>
                  {Array.from({ length: columns }, (_, c) => (
                    <AiCell
                      key={c}
                      result={e.results[c] ?? null}
                      entryId={e.id}
                      column={c}
                      isLast={isLast}
                      busy={busy}
                      selected={selected?.entryId === e.id && selected.column === c}
                      canTeach={canTeach}
                      compact={columns > 1}
                      onSelect={onSelect}
                      onRegenerate={onRegenerate}
                      onTeach={onTeach}
                    />
                  ))}
                </div>
              )
            })}
          </div>
        )}
      </div>

      <div className="mt-2.5 flex flex-col gap-2">
        {full && <p className="text-[11.5px] text-amber-300">{t('playground.chat.limit', { count: maxTurns })}</p>}
        {last?.kind === 'customer' && !busy && (
          <Button size="sm" variant="secondary" onClick={onAnswer} className="w-fit">
            <Send size={12} aria-hidden /> {t('playground.chat.answer')}
          </Button>
        )}
        <div className="flex items-end gap-2">
          <textarea
            rows={2}
            value={draft}
            onChange={(e) => setDraft(e.target.value)}
            onKeyDown={onKey}
            maxLength={maxChars}
            disabled={full}
            placeholder={t('playground.chat.placeholder')}
            aria-label={t('playground.chat.placeholder')}
            className={`${fieldCls} min-h-[44px] flex-1 resize-none`}
          />
          <Button variant="primary" onClick={send} disabled={!draft.trim() || busy || full}>
            {busy ? <Loader2 size={14} className="animate-spin" aria-hidden /> : <Send size={14} aria-hidden />}
            <span className="hidden sm:inline">{t('playground.chat.send')}</span>
          </Button>
        </div>
      </div>
    </div>
  )
}
