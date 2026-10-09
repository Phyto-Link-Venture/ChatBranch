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
  getAiControls,
  putAiControls,
  type AiControls,
  type AiControlsWrite,
  type AiMasterMode,
  type AiSchedule,
  type AiScheduleMode,
} from '@/lib/aiControls'
import { AiScheduleEditor } from './AiScheduleEditor'

const MODES: { value: AiMasterMode; label: string }[] = [
  { value: 'on', label: 'On' },
  { value: 'off', label: 'Off' },
  { value: 'schedule', label: 'On a schedule' },
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

const timeShort = (iso: string) =>
  new Date(iso).toLocaleString([], { weekday: 'short', hour: '2-digit', minute: '2-digit' })

/**
 * The AI master switch — ONE prominent "AI is ON for <scope>" card at the top
 * of the AI monitor: per outlet (entity picker) or organization-wide, on · off ·
 * on a schedule (per-weekday hours, AI inside or outside them, org timezone),
 * plus the never-AI contact tags. Off = no AI replies, follow-ups or AI flow
 * messages anywhere in that scope. Editing needs ai.behaviour.manage; everyone
 * else sees the state.
 */
export function AiMasterSwitch() {
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
  const scopeName = data?.entity_name ?? (scope ? scope : 'the organization')
  const isSchedule = data?.mode === 'schedule'

  const subline = !data
    ? ''
    : data.mode === 'off'
      ? 'No AI replies, follow-ups or AI flow messages anywhere in this scope.'
      : data.mode === 'schedule'
        ? `The AI answers ${data.schedule_mode === 'outside' ? 'outside' : 'during'} ${describeSchedule(data.schedule)} (${data.timezone})${
            data.next_change_at ? ` — ${on ? 'off' : 'on'} again ${timeShort(data.next_change_at)}` : ''
          }.`
        : 'The AI answers here; per-chat switches, contact settings and inbox defaults still apply.'
  const provenance = !data
    ? ''
    : data.mode_from === 'inherited'
      ? `Set on ${data.mode_set_at}.`
      : data.mode_from === 'organization' && data.entity
        ? 'Following the organization-wide switch.'
        : ''

  const tone = !data ? 'bg-fill text-text-muted' : on ? 'bg-green/15 text-[color:var(--green-ink)]' : isSchedule ? 'bg-amber/15 text-[color:var(--amber-ink)]' : 'bg-red/10 text-[color:var(--red-ink)]'

  return (
    <Card className="overflow-hidden">
      <div className="flex flex-wrap items-center gap-x-4 gap-y-3 px-4 py-4 sm:px-5">
        <span className={`grid h-11 w-11 shrink-0 place-items-center rounded-full ${tone}`} aria-hidden>
          {!data ? <Loader2 size={18} className="animate-spin" /> : on ? <Bot size={20} strokeWidth={1.8} /> : <BotOff size={20} strokeWidth={1.8} />}
        </span>
        <div className="min-w-0 flex-1 basis-[220px]">
          <div className="eyebrow">AI master switch</div>
          <div className="display text-[21px] leading-tight text-text-heading" aria-live="polite">
            {data ? (
              <>
                AI is <span className={on ? 'text-[color:var(--green-ink)]' : 'text-[color:var(--red-ink)]'}>{on ? 'ON' : 'OFF'}</span> for{' '}
                {scopeName}
              </>
            ) : loadErr ? (
              'AI switch unavailable'
            ) : (
              'Loading…'
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
            globalLabel="Whole organization"
            ariaLabel="Scope of the AI switch"
            className="min-w-0 flex-1 sm:w-[230px] sm:flex-none"
          />
          <Toggle
            on={!!data && data.mode !== 'off'}
            disabled={!canManage || busy || !data}
            onChange={(v) => save({ mode: v ? 'on' : 'off' }, v ? `AI switched on for ${scopeName}.` : `AI switched off for ${scopeName}.`)}
            label={`AI for ${scopeName}`}
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
        <span className="font-semibold text-text-high">Schedule &amp; exceptions</span>
        {data && (
          <>
            <span className="inline-flex items-center gap-1 rounded-full border border-line-strong px-2 py-px text-[11.5px] text-text-mid">
              <CalendarClock size={11} aria-hidden />
              {isSchedule ? `${data.schedule_mode === 'outside' ? 'Outside' : 'During'} ${describeSchedule(data.schedule)}` : 'No schedule'}
            </span>
            <span className="inline-flex items-center gap-1 rounded-full border border-line-strong px-2 py-px text-[11.5px] text-text-mid">
              <ShieldOff size={11} aria-hidden />
              {data.never_tags.length ? `Never AI: ${data.never_tags.slice(0, 3).join(', ')}${data.never_tags.length > 3 ? '…' : ''}` : 'No never-AI tags'}
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
            <span className="eyebrow">AI in {scopeName}</span>
            <div className="flex flex-wrap items-center gap-3">
              <Segmented value={mode} options={MODES} onChange={setMode} label="AI mode" disabled={!canManage} />
              {data.entity && data.own.mode && canManage && (
                <button
                  type="button"
                  onClick={() => save({ mode: 'inherit' }, `${scopeName} follows its parent's switch again.`)}
                  className="text-[12.5px] font-semibold text-[color:var(--brand-ink)] hover:underline"
                >
                  Use the parent’s setting
                </button>
              )}
            </div>
          </div>

          {mode === 'schedule' && (
            <div className="flex flex-col gap-2">
              <div className="flex flex-wrap items-center justify-between gap-2">
                <span className="eyebrow">Hours ({data.timezone})</span>
                <Segmented
                  value={scheduleMode}
                  onChange={setScheduleMode}
                  label="When the AI answers"
                  disabled={!canManage}
                  options={[
                    { value: 'inside', label: 'AI during these hours' },
                    { value: 'outside', label: 'AI outside these hours' },
                  ]}
                />
              </div>
              <p className="text-[12px] text-text-muted">
                {scheduleMode === 'inside'
                  ? 'e.g. an after-hours bot: the AI answers only inside these windows.'
                  : 'e.g. people during office hours: the AI answers only outside these windows.'}{' '}
                A draft a person approves later still goes out.
              </p>
              <AiScheduleEditor value={schedule} onChange={setSchedule} disabled={!canManage} />
            </div>
          )}

          <div className="flex flex-col gap-2">
            <span className="eyebrow">Never use AI for contacts tagged</span>
            <MultiSelect
              value={neverTags}
              onChange={setNeverTags}
              options={tagOptions}
              placeholder={tagOptions.length ? 'No tags — pick VIPs, complaints…' : 'No contact tags yet'}
              size="md"
              ariaLabel="Never-AI tags"
              searchable
              maxChips={4}
              disabled={!canManage}
            />
            {data.inherited_never_tags.length > 0 && (
              <p className="text-[12px] text-text-muted">
                Also from above: {data.inherited_never_tags.join(', ')}
              </p>
            )}
          </div>

          {canManage ? (
            <div className="flex flex-wrap justify-end gap-2">
              <Button variant="secondary" size="sm" onClick={() => adopt(data)} disabled={busy}>
                Discard
              </Button>
              <Button
                size="sm"
                disabled={busy}
                onClick={() =>
                  save(
                    {
                      mode,
                      ...(mode === 'schedule' ? { schedule, schedule_mode: scheduleMode } : {}),
                      never_tags: neverTags,
                    },
                    'Saved.',
                  )
                }
              >
                {busy && <Loader2 size={13} className="animate-spin" aria-hidden />} Save
              </Button>
            </div>
          ) : (
            <p className="text-[12px] text-text-muted">Changing these needs the “Edit AI behaviour settings” permission.</p>
          )}
        </div>
      )}
    </Card>
  )
}
