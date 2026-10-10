'use client'

import { useCallback, useEffect, useMemo, useState } from 'react'
import { Bot, BotOff, CalendarClock, ChevronDown, Loader2, ShieldOff } from 'lucide-react'
import { Card } from '@/components/ui/Card'
import { Button } from '@/components/ui/Button'
import { Toggle } from '@/components/ui/Toggle'
import { EntityPicker } from '@/components/ui/EntityPicker'
import { MultiSelect } from '@/components/ui/Select'
import { useEntity } from '@/lib/EntityContext'
import { useTags } from '@/lib/tags'
import {
  describeSchedule,
  emptySchedule,
  weekdayDate,
  getAiControls,
  putAiControls,
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

/**
 * The AI master switch — ONE prominent "AI is ON for <scope>" card at the top
 * of the AI monitor: per outlet (entity picker) or organization-wide, on · off ·
 * on a schedule (per-weekday hours, AI inside or outside them, org timezone),
 * plus the never-AI contact tags. Off = no AI replies, follow-ups or AI flow
 * messages anywhere in that scope. Editing needs ai.behaviour.manage; everyone
 * else sees the state.
 */
export function AiMasterSwitch() {
  const t = useT()
  const f = useFormat()
  const dayLabel = (d: Weekday) => f.weekday(weekdayDate(d))
  const timeShort = (iso: string) => `${f.weekday(iso)} ${f.time(iso)}`
  const { activeEntity } = useEntity()
  const [scope, setScope] = useState<string | null>(activeEntity?.slug ?? null)
  useEffect(() => setScope(activeEntity?.slug ?? null), [activeEntity?.slug])

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

  const load = useCallback(async () => {
    try {
      adopt(await getAiControls(scope))
      setLoadErr(null)
    } catch (e) {
      setData(null)
      setLoadErr(e instanceof Error ? e.message : String(e))
    }
  }, [scope, adopt])

  useEffect(() => {
    setMsg(null)
    void load()
  }, [load])

  const save = async (body: AiControlsWrite, okText: string) => {
    setBusy(true)
    setMsg(null)
    try {
      adopt(await putAiControls(scope, body))
      setMsg({ ok: true, text: okText })
    } catch (e) {
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    } finally {
      setBusy(false)
    }
  }

  const canManage = !!data?.can_manage
  const on = !!data?.now_on
  const scopeName = data?.entity_name ?? (scope ? scope : t('chats.aiControls.theOrganization'))
  const isSchedule = data?.mode === 'schedule'

  const hours = data ? describeSchedule(data.schedule, t, dayLabel) : ''
  const aiWindow = data
    ? t(data.schedule_mode === 'outside' ? 'chats.aiControls.windowOutside' : 'chats.aiControls.windowInside', { hours })
    : ''
  const subline = !data
    ? ''
    : data.mode === 'off'
      ? t('chats.aiControls.subOff')
      : data.mode === 'schedule'
        ? data.next_change_at
          ? t('chats.aiControls.subScheduleNext', {
              window: aiWindow,
              tz: data.timezone,
              next: t(on ? 'chats.aiControls.nextOff' : 'chats.aiControls.nextOn', { time: timeShort(data.next_change_at) }),
            })
          : t('chats.aiControls.subSchedule', { window: aiWindow, tz: data.timezone })
        : t('chats.aiControls.subOn')
  const provenance = !data
    ? ''
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
            onChange={setScope}
            size="sm"
            globalLabel={t('chats.aiControls.wholeOrg')}
            ariaLabel={t('chats.aiControls.scopeAria')}
            className="min-w-0 flex-1 sm:w-[230px] sm:flex-none"
          />
          <Toggle
            on={!!data && data.mode !== 'off'}
            disabled={!canManage || busy || !data}
            onChange={(v) =>
              save(
                { mode: v ? 'on' : 'off' },
                t(v ? 'chats.aiControls.switchedOn' : 'chats.aiControls.switchedOff', { scope: scopeName }),
              )
            }
            label={t('chats.aiControls.toggleLabel', { scope: scopeName })}
          />
        </div>
      </div>

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
                options={MODES.map((m) => ({ value: m.value, label: t(m.label) }))}
                onChange={setMode}
                label={t('chats.aiControls.modeAria')}
                disabled={!canManage}
              />
              {data.entity && data.own.mode && canManage && (
                <button
                  type="button"
                  onClick={() => save({ mode: 'inherit' }, t('chats.aiControls.followsParent', { scope: scopeName }))}
                  className="text-[12.5px] font-semibold text-[color:var(--brand-ink)] hover:underline"
                >
                  {t('chats.aiControls.useParent')}
                </button>
              )}
            </div>
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
          </div>

          {canManage ? (
            <div className="flex flex-wrap justify-end gap-2">
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
