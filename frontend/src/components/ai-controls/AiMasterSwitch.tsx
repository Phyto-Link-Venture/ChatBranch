'use client'

import { useCallback, useEffect, useMemo, useState } from 'react'
import { Bot, BotOff, CalendarClock, ChevronDown, Loader2, ShieldOff } from 'lucide-react'
import { Card } from '@/components/ui/Card'
import { Button } from '@/components/ui/Button'
import { Toggle } from '@/components/ui/Toggle'
import { EntityPicker } from '@/components/ui/EntityPicker'
import { MultiSelect, Select, type SelectOption } from '@/components/ui/Select'
import { useChannelLabel } from '@/components/ui/ChannelLogo'
import { useEntity } from '@/lib/EntityContext'
import { useTags } from '@/lib/tags'
// the same inbox list the AI behaviour settings pick from (one read of the
// accounts endpoint: numbers, mailboxes, bots, pages and website widgets)
import { listBehaviourInboxes, type BehaviourInbox } from '@/lib/aiMonitor'
import {
  describeSchedule,
  emptySchedule,
  weekdayDate,
  getAiControls,
  putAiControls,
  resetAiControls,
  type AiControlKey,
  type AiControls,
  type AiControlsWrite,
  type AiMasterMode,
  type AiSchedule,
  type AiScheduleMode,
  type Weekday,
} from '@/lib/aiControls'
import { AiScheduleEditor, scheduleHasErrors } from './AiScheduleEditor'
import { Trans, useFormat, useT, type MessageKey } from '@/i18n'

const MODES: { value: AiMasterMode; label: MessageKey }[] = [
  { value: 'on', label: 'common.status.on' },
  { value: 'off', label: 'common.status.off' },
  { value: 'schedule', label: 'chats.aiControls.modeSchedule' },
]

function Segmented<T extends string>({
  value,
  options,
  onChange,
  label,
  disabled,
}: {
  value: T
  options: { value: T; label: string }[]
  onChange: (v: T) => void
  label: string
  disabled?: boolean
}) {
  return (
    <div role="radiogroup" aria-label={label} className="inline-flex max-w-full rounded-lg border border-line-strong bg-card p-0.5">
      {options.map((o) => (
        <button
          key={o.value}
          type="button"
          role="radio"
          aria-checked={o.value === value}
          disabled={disabled}
          onClick={() => onChange(o.value)}
          className={`min-w-0 truncate rounded-md px-3 py-1 text-[12.5px] font-medium transition-colors disabled:cursor-not-allowed ${
            o.value === value ? 'bg-primary text-on-primary' : 'text-text-mid hover:bg-fill-hover hover:text-text-high'
          }`}
        >
          {o.label}
        </button>
      ))}
    </div>
  )
}

/** Where the value on screen comes from: "Set for this inbox" /
 *  "Inherited from Head Office (off)." — handing it back is the link beside it. */
function Origin({ own, here, inherited }: { own: boolean; here: string; inherited: string }) {
  return <p className="text-[12px] text-text-muted">{own ? here : inherited}</p>
}

/**
 * The AI master switch — ONE prominent "AI is ON for <scope>" card at the top
 * of the AI monitor: per outlet (entity picker), per INBOX (the inbox picker,
 * the same list the AI behaviour settings offer) or organization-wide, on · off ·
 * on a schedule (per-weekday hours, AI inside or outside them, org timezone),
 * plus the never-AI contact tags. Off = no AI replies, follow-ups or AI flow
 * messages anywhere in that scope. Editing needs ai.behaviour.manage; everyone
 * else sees the state.
 *
 * The inbox level is NOT nearest-level-wins (the AI behaviour settings are):
 * an inbox can only switch itself OFF or narrow its hours, and never answers
 * while the outlet, the organization or the schedule above it says no — the
 * copy says so wherever an inbox is picked.
 *
 * Nor is it the inbox's "ai_default" (Channels → Who answers it), which is
 * stamped on a conversation when it is created and loses to an operator's
 * per-chat switch. This is a live gate on every message.
 */
export function AiMasterSwitch() {
  const t = useT()
  const f = useFormat()
  const dayLabel = (d: Weekday) => f.weekday(weekdayDate(d))
  const timeShort = (iso: string) => `${f.weekday(iso)} ${f.time(iso)}`
  // levelEntity: the ticked entity — for a person bound to entities never the organization
  const { levelEntity, bound, entities } = useEntity()
  const [scope, setScope] = useState<string | null>(levelEntity?.slug ?? null)
  useEffect(() => setScope(levelEntity?.slug ?? null), [levelEntity?.slug])
  // the one inbox being edited (null = the outlet / organization level)
  const [account, setAccount] = useState<number | null>(null)
  // null until the list answers, so "no inboxes yet" never flashes while loading
  const [inboxes, setInboxes] = useState<BehaviourInbox[] | null>(null)
  const [inboxError, setInboxError] = useState<string | null>(null)

  const [data, setData] = useState<AiControls | null>(null)
  const [loadErr, setLoadErr] = useState<string | null>(null)
  const [open, setOpen] = useState(false)
  const [busy, setBusy] = useState(false)
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)

  // the editor's draft (this scope's own values, falling back to what it inherits)
  const [mode, setMode] = useState<AiMasterMode>('on')
  const [schedule, setSchedule] = useState<AiSchedule>(emptySchedule())
  const [scheduleMode, setScheduleMode] = useState<AiScheduleMode>('inside')
  const [neverTags, setNeverTags] = useState<string[]>([])

  const { tags } = useTags('contact')
  const tagOptions = useMemo(() => {
    const names = new Set([...(tags ?? []).map((t) => t.name), ...neverTags])
    return Array.from(names)
      .sort((a, b) => a.localeCompare(b))
      .map((n) => ({ value: n, label: n }))
  }, [tags, neverTags])

  const adopt = useCallback((d: AiControls) => {
    setData(d)
    setMode(d.mode)
    setSchedule({ ...emptySchedule(), ...(d.own.schedule ?? d.schedule) })
    setScheduleMode(d.own.schedule_mode || d.schedule_mode)
    setNeverTags(d.own.never_tags)
  }, [])

  // an inbox is read at ITS OWN level, so the query drops ?entity= — the same
  // rule as ?account= on /api/ai/behaviour
  const level = useMemo(() => ({ entity: scope, account }), [scope, account])

  const load = useCallback(async () => {
    try {
      adopt(await getAiControls(level))
      setLoadErr(null)
    } catch (e) {
      setData(null)
      setLoadErr(e instanceof Error ? e.message : String(e))
    }
  }, [level, adopt])

  useEffect(() => {
    setMsg(null)
    void load()
  }, [load])

  // the inboxes to choose from: every number, mailbox, bot, page and widget
  // this viewer can see
  useEffect(() => {
    let cancelled = false
    listBehaviourInboxes()
      .then((rows) => {
        if (cancelled) return
        setInboxes(rows)
        setInboxError(null)
      })
      .catch((e) => {
        if (!cancelled) setInboxError(e instanceof Error ? e.message : String(e))
      })
    return () => {
      cancelled = true
    }
  }, [])

  // a bound viewer may edit an inbox only at a level inside their own scope;
  // the accounts list also offers the inboxes their outlets inherit from above,
  // which the endpoint answers with a 404 — leave those out rather than offer a
  // dead end. Organization-wide inboxes stay (readable, read-only for them).
  const mine = useMemo(() => new Set(entities.map((e) => e.slug)), [entities])
  const choosable = useMemo(
    () => (inboxes ?? []).filter((i) => !bound || !i.entity || mine.has(i.entity)),
    [inboxes, bound, mine],
  )
  const inbox = useMemo(() => choosable.find((i) => i.id === account) ?? null, [choosable, account])
  const channelName = useChannelLabel()
  // grouped by the outlet that owns the inbox, so "which of my widgets?" is one glance
  const inboxOptions = useMemo<SelectOption[]>(
    () => [
      { value: '', label: t('chats.aiControls.inboxAll') },
      ...choosable.map((i) => ({
        value: String(i.id),
        label: i.name,
        hint: channelName(i.channel),
        group: i.entity_name || t('chats.aiControls.inboxOrgWide'),
      })),
    ],
    [choosable, channelName, t],
  )
  // an inbox belongs to one outlet (or to none = the whole organization), and
  // that is the level it inherits from: the outlet picker follows the inbox
  useEffect(() => {
    if (inbox && scope !== inbox.entity) setScope(inbox.entity)
  }, [inbox, scope])

  const save = async (body: AiControlsWrite, okText: string) => {
    setBusy(true)
    setMsg(null)
    try {
      adopt(await putAiControls(level, body))
      setMsg({ ok: true, text: okText })
    } catch (e) {
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    } finally {
      setBusy(false)
    }
  }

  /** Hand this level's own value (or all four) back to the level above. */
  const reset = async (key: AiControlKey | undefined, okText: string) => {
    setBusy(true)
    setMsg(null)
    try {
      adopt(await resetAiControls(level, key))
      setMsg({ ok: true, text: okText })
    } catch (e) {
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    } finally {
      setBusy(false)
    }
  }

  const canManage = !!data?.can_manage
  const on = !!data?.now_on
  const isInbox = !!account
  const scopeName = isInbox
    ? data?.account?.name ?? inbox?.name ?? ''
    : data?.entity_name ?? (scope ? scope : t('chats.aiControls.theOrganization'))
  // what this level follows when it sets nothing of its own
  const inheritName =
    (isInbox ? (inbox ? inbox.entity_name : data?.entity_name) : data?.entity_name) ||
    t('chats.aiControls.theOrganization')
  // an inbox can only subtract: when the level above is off (or out of hours)
  // nothing here can switch the AI back on
  const blockedAbove = !!data && isInbox && !data.inherited.now_on
  // does the level on screen set anything of its own?
  const hasOwn =
    !!data &&
    (!!data.own.mode || !!data.own.schedule || !!data.own.schedule_mode || data.own.never_tags.length > 0)
  const isSchedule = data?.mode === 'schedule'

  const hours = data ? describeSchedule(data.schedule, t, dayLabel) : ''
  const aiWindow = data
    ? t(data.schedule_mode === 'outside' ? 'chats.aiControls.windowOutside' : 'chats.aiControls.windowInside', { hours })
    : ''
  const subline = !data
    ? ''
    : // an inbox the level above has silenced says so first — "AI answers here"
      // would be a lie, and that is exactly the confusion to avoid
      blockedAbove
      ? t(
          data.inherited.mode === 'off'
            ? 'chats.aiControls.inboxBlockedOff'
            : 'chats.aiControls.inboxBlockedHours',
          { name: data.inherited.mode_set_at ?? inheritName },
        )
      : data.mode === 'off'
        ? t('chats.aiControls.subOff')
        : data.mode === 'schedule'
          ? data.next_change_at
            ? t('chats.aiControls.subScheduleNext', {
                window: aiWindow,
                tz: data.timezone,
                next: t(on ? 'chats.aiControls.nextOff' : 'chats.aiControls.nextOn', {
                  time: timeShort(data.next_change_at),
                }),
              })
            : t('chats.aiControls.subSchedule', { window: aiWindow, tz: data.timezone })
          : t('chats.aiControls.subOn')
  const provenance = !data
    ? ''
    : isInbox
      ? data.own.mode
        ? t('chats.aiControls.inboxSetHere', { name: inheritName })
        : t('chats.aiControls.inboxFollows', { name: inheritName })
      : data.mode_from === 'inherited'
        ? t('chats.aiControls.setOn', { name: data.mode_set_at ?? '' })
        : data.mode_from === 'organization' && data.entity
          ? t('chats.aiControls.followingOrg')
          : ''

  const tone = !data ? 'bg-fill text-text-muted' : on ? 'bg-green/15 text-[color:var(--green-ink)]' : isSchedule ? 'bg-amber/15 text-[color:var(--amber-ink)]' : 'bg-red/10 text-[color:var(--red-ink)]'

  return (
    <Card className="overflow-hidden">
      <div className="flex flex-wrap items-center gap-x-4 gap-y-3 px-4 py-4 sm:px-5">
        <span className={`grid h-11 w-11 shrink-0 place-items-center rounded-full ${tone}`} aria-hidden>
          {!data ? <Loader2 size={18} className="animate-spin" /> : on ? <Bot size={20} strokeWidth={1.8} /> : <BotOff size={20} strokeWidth={1.8} />}
        </span>
        <div className="min-w-0 flex-1 basis-[220px]">
          <div className="eyebrow">{t('chats.aiControls.masterSwitch')}</div>
          <div className="display text-[21px] leading-tight text-text-heading" aria-live="polite">
            {data ? (
              <Trans
                k="chats.aiControls.isOnFor"
                values={{
                  state: (
                    <span className={on ? 'text-[color:var(--green-ink)]' : 'text-[color:var(--red-ink)]'}>
                      {t(on ? 'chats.aiControls.stateOn' : 'chats.aiControls.stateOff')}
                    </span>
                  ),
                  scope: scopeName,
                }}
              />
            ) : loadErr ? (
              t('chats.aiControls.unavailable')
            ) : (
              t('common.status.loading')
            )}
          </div>
          <div className="mt-0.5 text-[12.5px] text-text-muted">
            {loadErr ?? subline} {provenance}
          </div>
        </div>
        <div className="flex w-full flex-wrap items-center gap-3 sm:w-auto">
          <EntityPicker
            value={scope}
            onChange={(v) => {
              setAccount(null) // a level up: the inbox pick goes with it
              setScope(v)
            }}
            size="sm"
            globalLabel={t('chats.aiControls.wholeOrg')}
            allowGlobal={!bound}
            ariaLabel={t('chats.aiControls.scopeAria')}
            className="min-w-0 flex-1 sm:w-[230px] sm:flex-none"
          />
          <Select
            value={account ? String(account) : ''}
            onChange={(v) => setAccount(v ? Number(v) : null)}
            options={inboxOptions}
            size="sm"
            className="min-w-0 flex-1 sm:w-[200px] sm:flex-none"
            disabled={choosable.length === 0}
            searchable={choosable.length > 6}
            placeholder={t('chats.aiControls.inboxAll')}
            ariaLabel={t('chats.aiControls.inboxAria')}
          />
          <Toggle
            // an inbox has no "on": switching it back on just hands the switch
            // to the level above again (it can never answer against it)
            on={!!data && (isInbox ? data.own.mode !== 'off' : data.mode !== 'off')}
            disabled={!canManage || busy || !data}
            onChange={(v) =>
              isInbox
                ? v
                  ? reset('ai_enabled', t('chats.aiControls.inboxFollowsAgain', { scope: scopeName, name: inheritName }))
                  : save({ mode: 'off' }, t('chats.aiControls.switchedOff', { scope: scopeName }))
                : save(
                    { mode: v ? 'on' : 'off' },
                    t(v ? 'chats.aiControls.switchedOn' : 'chats.aiControls.switchedOff', { scope: scopeName }),
                  )
            }
            label={t('chats.aiControls.toggleLabel', { scope: scopeName })}
          />
        </div>
      </div>

      {isInbox && (
        <div className="mx-4 mb-3 flex flex-col gap-1 rounded-md bg-amber/10 px-3 py-2 text-[12px] leading-relaxed text-text-mid sm:mx-5">
          <span className="font-semibold text-text-high">{t('chats.aiControls.inboxOnlyOff')}</span>
          <span>{t('chats.aiControls.inboxNotDefault')}</span>
        </div>
      )}
      {inboxError && (
        <div role="alert" className="mx-4 mb-3 text-[12px] text-[color:var(--red-ink)] sm:mx-5">
          {t('chats.aiControls.inboxesError', { error: inboxError })}
        </div>
      )}

      {msg && !open && (
        <div
          role={msg.ok ? 'status' : 'alert'}
          className={`mx-4 mb-3 rounded-md px-3 py-2 text-[12.5px] sm:mx-5 ${
            msg.ok ? 'bg-green/10 text-[color:var(--green-ink)]' : 'bg-red/10 text-[color:var(--red-ink)]'
          }`}
        >
          {msg.text}
        </div>
      )}

      <button
        type="button"
        onClick={() => setOpen((v) => !v)}
        aria-expanded={open}
        className="flex w-full flex-wrap items-center gap-2 border-t border-line px-4 py-2.5 text-left text-[12.5px] transition-colors hover:bg-fill sm:px-5"
      >
        <span className="font-semibold text-text-high">{t('chats.aiControls.scheduleExceptions')}</span>
        {data && (
          <>
            <span className="inline-flex items-center gap-1 rounded-full border border-line-strong px-2 py-px text-[11.5px] text-text-mid">
              <CalendarClock size={11} aria-hidden />
              {isSchedule
                ? t(data.schedule_mode === 'outside' ? 'chats.aiControls.chipOutside' : 'chats.aiControls.chipInside', { hours })
                : t('chats.aiControls.noSchedule')}
            </span>
            <span className="inline-flex items-center gap-1 rounded-full border border-line-strong px-2 py-px text-[11.5px] text-text-mid">
              <ShieldOff size={11} aria-hidden />
              {data.never_tags.length
                ? t('chats.aiControls.neverAi', {
                    tags: `${data.never_tags.slice(0, 3).join(', ')}${data.never_tags.length > 3 ? '…' : ''}`,
                  })
                : t('chats.aiControls.noNeverAiTags')}
            </span>
          </>
        )}
        <ChevronDown size={14} className={`ml-auto shrink-0 text-text-muted transition-transform ${open ? 'rotate-180' : ''}`} aria-hidden />
      </button>

      {open && data && (
        <div className="flex flex-col gap-5 border-t border-line px-4 py-4 sm:px-5">
          {msg && (
            <div
              role={msg.ok ? 'status' : 'alert'}
              className={`rounded-md px-3 py-2 text-[12.5px] ${
                msg.ok ? 'bg-green/10 text-[color:var(--green-ink)]' : 'bg-red/10 text-[color:var(--red-ink)]'
              }`}
            >
              {msg.text}
            </div>
          )}
          <div className="flex flex-col gap-2">
            <span className="eyebrow">{t('chats.aiControls.aiIn', { scope: scopeName })}</span>
            <div className="flex flex-wrap items-center gap-3">
              <Segmented
                value={mode}
                options={MODES.map((m) => ({
                  value: m.value,
                  // on an inbox "on" adds nothing — it only stops this inbox
                  // from switching itself off
                  label: isInbox && m.value === 'on' ? t('chats.aiControls.inboxModeOn') : t(m.label),
                }))}
                onChange={setMode}
                label={t('chats.aiControls.modeAria')}
                disabled={!canManage}
              />
              {(data.entity || isInbox) && data.own.mode && canManage && (
                <button
                  type="button"
                  onClick={() =>
                    save(
                      { mode: 'inherit' },
                      isInbox
                        ? t('chats.aiControls.inboxFollowsAgain', { scope: scopeName, name: inheritName })
                        : t('chats.aiControls.followsParent', { scope: scopeName }),
                    )
                  }
                  className="text-[12.5px] font-semibold text-[color:var(--brand-ink)] hover:underline"
                >
                  {isInbox ? t('chats.aiControls.useAbove') : t('chats.aiControls.useParent')}
                </button>
              )}
            </div>
            {data.level !== 'organization' && (
              <Origin
                own={!!data.own.mode}
                here={t(isInbox ? 'chats.aiControls.setForInbox' : 'chats.aiControls.setForOutlet')}
                inherited={t('chats.aiControls.inheritedFrom', {
                  name: data.inherited.mode_set_at ?? inheritName,
                  value: t(
                    data.inherited.mode === 'off'
                      ? 'common.status.off'
                      : data.inherited.mode === 'schedule'
                        ? 'chats.aiControls.modeSchedule'
                        : 'common.status.on',
                  ),
                })}
              />
            )}
            {blockedAbove && (
              <p className="text-[12px] font-semibold text-[color:var(--amber-ink)]">
                {t('chats.aiControls.inboxCannotTurnOn', { name: data.inherited.mode_set_at ?? inheritName })}
              </p>
            )}
          </div>

          {mode === 'schedule' && (
            <div className="flex flex-col gap-2">
              <div className="flex flex-wrap items-center justify-between gap-2">
                <span className="eyebrow">{t('chats.aiControls.hoursTz', { tz: data.timezone })}</span>
                <Segmented
                  value={scheduleMode}
                  onChange={setScheduleMode}
                  label={t('chats.aiControls.whenAria')}
                  disabled={!canManage}
                  options={[
                    { value: 'inside', label: t('chats.aiControls.inside') },
                    { value: 'outside', label: t('chats.aiControls.outside') },
                  ]}
                />
              </div>
              <p className="text-[12px] text-text-muted">
                {t(scheduleMode === 'inside' ? 'chats.aiControls.insideHint' : 'chats.aiControls.outsideHint')}{' '}
                {t('chats.aiControls.approvedStillGoes')}
              </p>
              {isInbox && (
                <p className="text-[12px] text-text-muted">
                  {t('chats.aiControls.inboxHoursNarrow', {
                    name: inheritName,
                    hours: describeSchedule(data.inherited.schedule, t, dayLabel),
                  })}
                </p>
              )}
              <AiScheduleEditor value={schedule} onChange={setSchedule} disabled={!canManage} />
            </div>
          )}

          <div className="flex flex-col gap-2">
            <span className="eyebrow">{t('chats.aiControls.neverTitle')}</span>
            <MultiSelect
              value={neverTags}
              onChange={setNeverTags}
              options={tagOptions}
              placeholder={t(tagOptions.length ? 'chats.aiControls.neverPlaceholder' : 'chats.aiControls.noContactTags')}
              size="md"
              ariaLabel={t('chats.aiControls.neverAria')}
              searchable
              maxChips={4}
              disabled={!canManage}
            />
            {data.inherited_never_tags.length > 0 && (
              <p className="text-[12px] text-text-muted">
                {t('chats.aiControls.alsoFromAbove', { tags: data.inherited_never_tags.join(', ') })}
              </p>
            )}
            {isInbox && <p className="text-[12px] text-text-muted">{t('chats.aiControls.inboxTagsAddUp')}</p>}
          </div>

          {canManage ? (
            <div className="flex flex-wrap items-center justify-end gap-2">
              {data.level !== 'organization' && hasOwn && (
                <button
                  type="button"
                  onClick={() =>
                    void reset(
                      undefined,
                      isInbox
                        ? t('chats.aiControls.inboxFollowsAgain', { scope: scopeName, name: inheritName })
                        : t('chats.aiControls.followsParent', { scope: scopeName }),
                    )
                  }
                  disabled={busy}
                  className="mr-auto text-[12.5px] font-semibold text-text-mid underline underline-offset-2 hover:text-text-high"
                >
                  {t('chats.aiControls.removeAllOverrides')}
                </button>
              )}
              <Button variant="secondary" size="sm" onClick={() => adopt(data)} disabled={busy}>
                {t('common.actions.discard')}
              </Button>
              <Button
                size="sm"
                // an unreadable window ("25:00", a half-typed time) would be dropped silently by the server
                disabled={busy || (mode === 'schedule' && scheduleHasErrors(schedule))}
                onClick={() =>
                  save(
                    {
                      mode,
                      ...(mode === 'schedule' ? { schedule, schedule_mode: scheduleMode } : {}),
                      never_tags: neverTags,
                    },
                    t('chats.aiControls.saved'),
                  )
                }
              >
                {busy && <Loader2 size={13} className="animate-spin" aria-hidden />} {t('common.actions.save')}
              </Button>
            </div>
          ) : (
            <p className="text-[12px] text-text-muted">{t('chats.aiControls.needsPermission')}</p>
          )}
        </div>
      )}
    </Card>
  )
}
