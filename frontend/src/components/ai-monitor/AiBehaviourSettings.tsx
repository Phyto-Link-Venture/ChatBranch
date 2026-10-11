'use client'

/**
 * AI behaviour — what gets reviewed before it is sent, and how the AI paces a
 * conversation like a person (wait for the customer to finish, type, split
 * long replies, leave acknowledgements alone, follow up when it goes quiet).
 *
 * Every field comes from the server catalog (GET /api/ai/behaviour), so labels
 * and validation can't drift from the engine. Organization defaults are edited
 * with no entity picked; picking an entity edits ITS overrides, each field
 * saying whether it inherits the organization value. A live example timeline
 * replays a sample conversation with the values on screen (saved or not).
 *
 * Reusable: rendered as the Settings tab of /ai-monitor and importable from
 * Settings → AI as <AiBehaviourSettings />.
 */

import { useCallback, useEffect, useMemo, useState } from 'react'
import { Loader2, RotateCcw } from 'lucide-react'
import { Advanced } from '@/components/ui/Advanced'
import { Card, CardTitle } from '@/components/ui/Card'
import { EntityPicker } from '@/components/ui/EntityPicker'
import { useEntity } from '@/lib/EntityContext'
import { SaveBar } from '@/components/ui/BindingGrid'
import { Select } from '@/components/ui/Select'
import { Toggle } from '@/components/ui/Toggle'
import { aiApi, type BehaviourField, type BehaviourPayload } from '@/lib/aiMonitor'
import { useFormat, useT, type MessageKey, type TFunction } from '@/i18n'

const OPTION_LABELS: Record<string, Record<string, MessageKey>> = {
  ai_review_mode: {
    auto: 'ai.behaviour.options.reviewMode.auto',
    review_flagged: 'ai.behaviour.options.reviewMode.reviewFlagged',
    review_all: 'ai.behaviour.options.reviewMode.reviewAll',
    shadow: 'ai.behaviour.options.reviewMode.shadow',
  },
  ai_learning_mode: {
    review: 'ai.behaviour.options.learningMode.review',
    auto: 'ai.behaviour.options.learningMode.auto',
    off: 'ai.behaviour.options.learningMode.off',
  },
  ai_split: {
    off: 'ai.behaviour.options.split.off',
    auto: 'ai.behaviour.options.split.auto',
    always: 'ai.behaviour.options.split.always',
  },
  ai_emoji_style: {
    off: 'ai.behaviour.options.emojiStyle.off',
    light: 'ai.behaviour.options.emojiStyle.light',
    expressive: 'ai.behaviour.options.emojiStyle.expressive',
  },
  ai_quote_replies: {
    auto: 'ai.behaviour.options.quoteReplies.auto',
    off: 'ai.behaviour.options.quoteReplies.off',
  },
}

/**
 * The server sends each field's label/help in English (its catalog is the
 * engine's contract). They are shown in the dashboard's language by the
 * field's stable `key`; an unknown key falls back to the server's own text.
 */
const FIELD_TEXT: Record<string, { label: MessageKey; help?: MessageKey }> = {
  ai_review_mode: { label: 'ai.behaviour.fields.reviewMode.label', help: 'ai.behaviour.fields.reviewMode.help' },
  ai_review_autosend_minutes: {
    label: 'ai.behaviour.fields.autosendMinutes.label',
    help: 'ai.behaviour.fields.autosendMinutes.help',
  },
  ai_review_first_n: { label: 'ai.behaviour.fields.firstN.label', help: 'ai.behaviour.fields.firstN.help' },
  ai_review_keywords: { label: 'ai.behaviour.fields.keywords.label', help: 'ai.behaviour.fields.keywords.help' },
  ai_review_notify: { label: 'ai.behaviour.fields.notify.label', help: 'ai.behaviour.fields.notify.help' },
  ai_wait_seconds: { label: 'ai.behaviour.fields.waitSeconds.label', help: 'ai.behaviour.fields.waitSeconds.help' },
  ai_wait_max_seconds: {
    label: 'ai.behaviour.fields.waitMaxSeconds.label',
    help: 'ai.behaviour.fields.waitMaxSeconds.help',
  },
  ai_ack_skip: { label: 'ai.behaviour.fields.ackSkip.label', help: 'ai.behaviour.fields.ackSkip.help' },
  ai_ack_text: { label: 'ai.behaviour.fields.ackText.label', help: 'ai.behaviour.fields.ackText.help' },
  ai_typing: { label: 'ai.behaviour.fields.typing.label', help: 'ai.behaviour.fields.typing.help' },
  ai_typing_cps: { label: 'ai.behaviour.fields.typingCps.label', help: 'ai.behaviour.fields.typingCps.help' },
  ai_typing_min_s: { label: 'ai.behaviour.fields.typingMin.label' },
  ai_typing_max_s: { label: 'ai.behaviour.fields.typingMax.label' },
  ai_split: { label: 'ai.behaviour.fields.split.label', help: 'ai.behaviour.fields.split.help' },
  ai_split_max_chars: { label: 'ai.behaviour.fields.splitMaxChars.label' },
  ai_emoji_style: { label: 'ai.behaviour.fields.emojiStyle.label', help: 'ai.behaviour.fields.emojiStyle.help' },
  ai_reactions: { label: 'ai.behaviour.fields.reactions.label', help: 'ai.behaviour.fields.reactions.help' },
  ai_quote_replies: {
    label: 'ai.behaviour.fields.quoteReplies.label',
    help: 'ai.behaviour.fields.quoteReplies.help',
  },
  ai_followup: { label: 'ai.behaviour.fields.followUp.label', help: 'ai.behaviour.fields.followUp.help' },
  ai_followup_first_hours: { label: 'ai.behaviour.fields.followUpFirstHours.label' },
  ai_followup_between_hours: { label: 'ai.behaviour.fields.followUpBetweenHours.label' },
  ai_followup_max: { label: 'ai.behaviour.fields.followUpMax.label' },
  ai_followup_template: {
    label: 'ai.behaviour.fields.followUpTemplate.label',
    help: 'ai.behaviour.fields.followUpTemplate.help',
  },
  ai_followup_guidance: {
    label: 'ai.behaviour.fields.followUpGuidance.label',
    help: 'ai.behaviour.fields.followUpGuidance.help',
  },
  ai_learning_mode: { label: 'ai.behaviour.fields.learningMode.label', help: 'ai.behaviour.fields.learningMode.help' },
}

/** Group titles/descriptions by group id (the server's English for an unknown group). */
const GROUP_TEXT: Record<string, { title: MessageKey; description: MessageKey }> = {
  review: { title: 'ai.behaviour.groups.review.title', description: 'ai.behaviour.groups.review.description' },
  wait: { title: 'ai.behaviour.groups.wait.title', description: 'ai.behaviour.groups.wait.description' },
  typing: { title: 'ai.behaviour.groups.typing.title', description: 'ai.behaviour.groups.typing.description' },
  split: { title: 'ai.behaviour.groups.split.title', description: 'ai.behaviour.groups.split.description' },
  human: { title: 'ai.behaviour.groups.human.title', description: 'ai.behaviour.groups.human.description' },
  followup: { title: 'ai.behaviour.groups.followup.title', description: 'ai.behaviour.groups.followup.description' },
  learning: { title: 'ai.behaviour.groups.learning.title', description: 'ai.behaviour.groups.learning.description' },
}

/** A field's label in the dashboard's language (server text for an unknown key). */
export function behaviourFieldLabel(f: Pick<BehaviourField, 'key' | 'label'>, t: TFunction): string {
  const k = FIELD_TEXT[f.key]
  return k ? t(k.label) : f.label
}

/** A field's help line in the dashboard's language ("" when the server sends none). */
function behaviourFieldHelp(f: Pick<BehaviourField, 'key' | 'help'>, t: TFunction): string {
  if (!f.help) return ''
  const k = FIELD_TEXT[f.key]?.help
  return k ? t(k) : f.help
}

/** One select option's label (the raw value for an option this build doesn't know). */
function optionLabel(fieldKey: string, value: string, t: TFunction): string {
  const k = OPTION_LABELS[fieldKey]?.[value]
  return k ? t(k) : value
}

/** Fields that only matter when another one is set a certain way. */
const DEPENDS: Record<string, { on: string; when: (v: string) => boolean; note: MessageKey }> = {
  ai_review_autosend_minutes: {
    on: 'ai_review_mode',
    when: (v) => v === 'review_flagged' || v === 'review_all',
    note: 'ai.behaviour.depends.held',
  },
  ai_review_first_n: { on: 'ai_review_mode', when: (v) => v === 'review_flagged', note: 'ai.behaviour.depends.risky' },
  ai_review_keywords: { on: 'ai_review_mode', when: (v) => v === 'review_flagged', note: 'ai.behaviour.depends.risky' },
  ai_wait_max_seconds: { on: 'ai_wait_seconds', when: (v) => Number(v) > 0, note: 'ai.behaviour.depends.wait' },
  ai_ack_text: { on: 'ai_ack_skip', when: (v) => v === 'true', note: 'ai.behaviour.depends.ack' },
  ai_typing_cps: { on: 'ai_typing', when: (v) => v === 'true', note: 'ai.behaviour.depends.typing' },
  ai_typing_min_s: { on: 'ai_typing', when: (v) => v === 'true', note: 'ai.behaviour.depends.typing' },
  ai_typing_max_s: { on: 'ai_typing', when: (v) => v === 'true', note: 'ai.behaviour.depends.typing' },
  ai_split_max_chars: { on: 'ai_split', when: (v) => v !== 'off', note: 'ai.behaviour.depends.split' },
  ai_followup_first_hours: { on: 'ai_followup', when: (v) => v === 'true', note: 'ai.behaviour.depends.followUps' },
  ai_followup_between_hours: { on: 'ai_followup', when: (v) => v === 'true', note: 'ai.behaviour.depends.followUps' },
  ai_followup_max: { on: 'ai_followup', when: (v) => v === 'true', note: 'ai.behaviour.depends.followUps' },
  ai_followup_template: { on: 'ai_followup', when: (v) => v === 'true', note: 'ai.behaviour.depends.followUps' },
  ai_followup_guidance: { on: 'ai_followup', when: (v) => v === 'true', note: 'ai.behaviour.depends.followUps' },
}

/**
 * The one field per card a first-time user needs; the rest of the card sits
 * under "Advanced settings" (opened by itself when one of them is customised).
 */
const ESSENTIAL = new Set([
  'ai_review_mode',
  'ai_wait_seconds',
  'ai_typing',
  'ai_split',
  // the three human-like touches are each a first-time choice: none hides under Advanced
  'ai_emoji_style',
  'ai_reactions',
  'ai_quote_replies',
  'ai_followup',
  'ai_learning_mode',
])

/** Customised = overridden at the picked entity, or differs from the engine default. */
function customised(f: BehaviourField, value: string, entity: string | null): boolean {
  return (!!entity && f.scope === 'entity') || value !== f.default
}

const inputCls =
  'w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[13px] text-text-primary outline-none transition-colors focus:border-brand-400 disabled:opacity-60'

function show(field: BehaviourField, v: string, t: TFunction): string {
  if (field.type === 'bool') return v === 'true' ? t('common.status.on') : t('common.status.off')
  if (field.type === 'select') return optionLabel(field.key, v, t)
  return v === '' ? t('ai.behaviour.blank') : v
}

export function AiBehaviourSettings({ initialEntity = null }: { initialEntity?: string | null }) {
  const t = useT()
  // a person bound to their entities edits their entity's level, never the organization's
  const { bound, levelEntity } = useEntity()
  const start = initialEntity ?? levelEntity?.slug ?? null
  const [entity, setEntity] = useState<string | null>(start)
  // follow the switcher (and whoami's `bound` arriving after mount)
  useEffect(() => setEntity(start), [start])
  const [data, setData] = useState<BehaviourPayload | null>(null)
  const [draft, setDraft] = useState<Record<string, string>>({})
  const [loadError, setLoadError] = useState<string | null>(null)
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)
  const [saving, setSaving] = useState(false)

  const apply = (p: BehaviourPayload) => {
    setData(p)
    setDraft(Object.fromEntries(p.fields.map((f) => [f.key, f.value])))
  }

  const load = useCallback(async () => {
    try {
      apply(await aiApi<BehaviourPayload>(`ai/behaviour${entity ? `?entity=${encodeURIComponent(entity)}` : ''}`))
      setLoadError(null)
    } catch (e) {
      setLoadError(e instanceof Error ? e.message : String(e))
    }
  }, [entity])

  useEffect(() => {
    setMsg(null)
    void load()
  }, [load])

  const fields = useMemo(() => data?.fields ?? [], [data])
  const changed = useMemo(
    () => fields.filter((f) => (draft[f.key] ?? '') !== f.value).map((f) => f.key),
    [fields, draft],
  )
  const canManage = !!data?.can_manage

  const save = async () => {
    setSaving(true)
    setMsg(null)
    try {
      const values = Object.fromEntries(changed.map((k) => [k, draft[k] ?? '']))
      apply(
        await aiApi<BehaviourPayload>(`ai/behaviour${entity ? `?entity=${encodeURIComponent(entity)}` : ''}`, {
          method: 'PUT',
          body: JSON.stringify({ values }),
        }),
      )
      setMsg({
        ok: true,
        text: entity ? t('ai.common.savedFor', { name: data?.entity_name ?? entity }) : t('ai.behaviour.savedOrg'),
      })
    } catch (e) {
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    } finally {
      setSaving(false)
    }
  }

  const resetField = async (key?: string) => {
    if (!entity) return
    setMsg(null)
    try {
      const q = `ai/behaviour?entity=${encodeURIComponent(entity)}${key ? `&key=${encodeURIComponent(key)}` : ''}`
      apply(await aiApi<BehaviourPayload>(q, { method: 'DELETE' }))
      setMsg({ ok: true, text: key ? t('ai.behaviour.backToDefault') : t('ai.behaviour.overridesRemoved') })
    } catch (e) {
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    }
  }

  const overrides = fields.filter((f) => f.scope === 'entity').length

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-end gap-3">
        <div className="min-w-[240px] flex-1 basis-[260px] sm:max-w-[360px]">
          <label className="mb-1 block text-[12px] font-semibold text-text-mid">{t('ai.behaviour.settingsFor')}</label>
          <EntityPicker
            value={entity}
            onChange={(v) => {
              if (changed.length && !window.confirm(t('ai.behaviour.discardConfirm'))) return
              setEntity(v)
            }}
            globalLabel={t('ai.behaviour.orgDefaultEvery')}
            allowGlobal={!bound}
            ariaLabel={t('ai.behaviour.entityAria')}
          />
        </div>
        <p className="min-w-0 flex-1 basis-[260px] pb-1 text-[12.5px] leading-relaxed text-text-muted">
          {entity
            ? overrides
              ? t('ai.behaviour.overridesHere', { count: overrides })
              : t('ai.behaviour.inheritsAll')
            : t('ai.behaviour.appliesEverywhere')}
          {entity && overrides > 0 && canManage && (
            <button
              type="button"
              onClick={() => void resetField()}
              className="ml-2 font-semibold text-text-mid underline underline-offset-2 hover:text-text-high"
            >
              {t('ai.behaviour.removeAllOverrides')}
            </button>
          )}
        </p>
      </div>

      {loadError && (
        <div role="alert" className="text-[12.5px] text-[color:var(--red-ink)]">
          {t('ai.behaviour.loadError', { error: loadError })}
        </div>
      )}
      {msg && (
        <div
          role={msg.ok ? 'status' : 'alert'}
          className={`text-[12.5px] ${msg.ok ? 'text-[color:var(--green-ink)]' : 'text-[color:var(--red-ink)]'}`}
        >
          {msg.text}
        </div>
      )}
      {data && !canManage && (
        <p className="text-[12.5px] text-text-muted">{t('ai.behaviour.readOnly')}</p>
      )}

      {!data && !loadError && (
        <div className="flex items-center gap-2 p-4 text-[13px] text-text-muted">
          <Loader2 size={15} className="animate-spin" aria-hidden /> {t('common.status.loading')}
        </div>
      )}

      {data && (
        <div className="grid items-start gap-4 lg:grid-cols-[minmax(0,1fr)_340px]">
          <div className="flex min-w-0 flex-col gap-4">
            {data.groups.map((g) => (
              <Card key={g.id} className="px-5 py-4">
                <CardTitle>{GROUP_TEXT[g.id] ? t(GROUP_TEXT[g.id].title) : g.title}</CardTitle>
                <p className="mb-3 mt-0.5 text-[12.5px] text-text-muted">
                  {GROUP_TEXT[g.id] ? t(GROUP_TEXT[g.id].description) : g.description}
                </p>
                {(() => {
                  const inGroup = fields.filter((f) => f.group === g.id)
                  const hasEssential = inGroup.some((f) => ESSENTIAL.has(f.key))
                  const main = hasEssential ? inGroup.filter((f) => ESSENTIAL.has(f.key)) : inGroup
                  const extra = hasEssential ? inGroup.filter((f) => !ESSENTIAL.has(f.key)) : []
                  const row = (f: BehaviourField) => (
                    <FieldRow
                      key={f.key}
                      field={f}
                      value={draft[f.key] ?? ''}
                      all={draft}
                      entity={entity}
                      disabled={!canManage}
                      onChange={(v) => setDraft((cur) => ({ ...cur, [f.key]: v }))}
                      onReset={() => void resetField(f.key)}
                    />
                  )
                  const custom = extra.filter((f) => customised(f, draft[f.key] ?? '', entity)).length
                  return (
                    <>
                      <div className="flex flex-col divide-y divide-line">{main.map(row)}</div>
                      {extra.length > 0 && (
                        <Advanced className="mt-3" count={custom} forceOpen={custom > 0}>
                          <div className="flex flex-col divide-y divide-line">{extra.map(row)}</div>
                        </Advanced>
                      )}
                    </>
                  )
                })()}
              </Card>
            ))}
            <SaveBar
              dirty={changed.length > 0}
              saving={saving}
              onSave={() => void save()}
              onDiscard={() => setDraft(Object.fromEntries(fields.map((f) => [f.key, f.value])))}
              summary={
                entity
                  ? t('ai.behaviour.changesFor', { count: changed.length, name: data.entity_name ?? entity })
                  : t('ai.behaviour.changes', { count: changed.length })
              }
            />
          </div>
          <div className="lg:sticky lg:top-0">
            <ExampleTimeline values={draft} />
          </div>
        </div>
      )}
    </div>
  )
}

function FieldRow({
  field: f,
  value,
  all,
  entity,
  disabled,
  onChange,
  onReset,
}: {
  field: BehaviourField
  value: string
  all: Record<string, string>
  entity: string | null
  disabled: boolean
  onChange: (v: string) => void
  onReset: () => void
}) {
  const t = useT()
  const label = behaviourFieldLabel(f, t)
  const help = behaviourFieldHelp(f, t)
  const dep = DEPENDS[f.key]
  const inactive = dep ? !dep.when(all[dep.on] ?? '') : false
  const id = `ab-${f.key}`
  const step = f.key.includes('hours') ? 0.5 : 1

  const control =
    f.type === 'bool' ? (
      <Toggle on={value === 'true'} onChange={(v) => onChange(v ? 'true' : 'false')} disabled={disabled} label={label} />
    ) : f.type === 'select' ? (
      <Select
        value={value}
        onChange={onChange}
        disabled={disabled}
        size="md"
        ariaLabel={label}
        className="w-full sm:w-[300px]"
        options={(f.options ?? []).map((o) => ({ value: o, label: optionLabel(f.key, o, t) }))}
      />
    ) : f.type === 'number' ? (
      <input
        id={id}
        type="number"
        inputMode="decimal"
        min={f.min ?? undefined}
        max={f.max ?? undefined}
        step={step}
        value={value}
        disabled={disabled}
        onChange={(e) => onChange(e.target.value)}
        className={`${inputCls} sm:w-[140px]`}
      />
    ) : f.type === 'textarea' ? (
      <textarea
        id={id}
        rows={2}
        value={value}
        disabled={disabled}
        onChange={(e) => onChange(e.target.value)}
        className={inputCls}
      />
    ) : (
      <input id={id} value={value} disabled={disabled} onChange={(e) => onChange(e.target.value)} className={inputCls} />
    )

  const wide = f.type === 'text' || f.type === 'textarea'
  return (
    <div className={`py-3 first:pt-1 last:pb-1 ${inactive ? 'opacity-60' : ''}`}>
      <div className={`flex gap-x-4 gap-y-2 ${wide ? 'flex-col' : 'flex-col sm:flex-row sm:items-start sm:justify-between'}`}>
        <div className="min-w-0 flex-1">
          <label htmlFor={f.type === 'bool' || f.type === 'select' ? undefined : id} className="text-[13px] font-semibold text-text-high">
            {label}
          </label>
          {help && <p className="mt-0.5 text-[12px] leading-relaxed text-text-muted">{help}</p>}
          {inactive && dep && <p className="mt-0.5 text-[11.5px] text-text-faint">{t(dep.note)}</p>}
        </div>
        <div className={`shrink-0 ${wide ? 'w-full' : ''}`}>{control}</div>
      </div>
      {entity && (
        <div className="mt-1.5 flex flex-wrap items-center gap-2 text-[11.5px]">
          {f.scope === 'entity' ? (
            <>
              <span className="rounded-md bg-brand-400/10 px-1.5 py-0.5 font-semibold text-brand-300">
                {t('ai.behaviour.setForEntity')}
              </span>
              <span className="text-text-faint">
                {t('ai.behaviour.orgDefaultValue', { value: show(f, f.inherited_value, t) })}
              </span>
              {!disabled && (
                <button
                  type="button"
                  onClick={onReset}
                  className="inline-flex items-center gap-1 font-semibold text-text-mid hover:text-text-high"
                >
                  <RotateCcw size={11} aria-hidden /> {t('ai.behaviour.useDefault')}
                </button>
              )}
            </>
          ) : (
            <span className="text-text-faint">
              {value !== f.value ? t('ai.behaviour.inheritsOverrides') : t('ai.behaviour.inherits')}
            </span>
          )}
        </div>
      )}
    </div>
  )
}

// ---- the live example ---------------------------------------------------------

type Row = { at: number; who: 'customer' | 'ai' | 'note'; text: string }

const SAMPLE: { at: number; text: MessageKey }[] = [
  { at: 0, text: 'ai.example.msgHi' },
  { at: 6, text: 'ai.example.msgDeliver' },
  { at: 11, text: 'ai.example.msgPrice' },
]

function sampleReply(covers: number[], t: TFunction): string {
  const has = (i: number) => covers.includes(i)
  if (has(1) && has(2)) return t('ai.example.replyBoth')
  if (has(2)) return t('ai.example.replyPrice')
  if (has(1)) return t('ai.example.replyDeliver')
  return t('ai.example.replyHi')
}

function splitBubbles(text: string, mode: string, max: number): string[] {
  const paras = text.split(/\n\s*\n/).map((p) => p.trim()).filter(Boolean)
  if (mode === 'off' || paras.length < 2) return [text]
  if (mode === 'auto' && text.length <= max) return [text]
  return paras.slice(0, 3)
}

function riskyReasons(text: string, keywords: string, t: TFunction): string[] {
  const out: string[] = []
  for (const k of keywords.split(/[,;\n]/).map((x) => x.trim()).filter(Boolean)) {
    const re = new RegExp(`\\b${k.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}\\b`, 'i')
    if (re.test(text)) out.push(t('ai.example.mentions', { word: k }))
  }
  if (/(RM|\$|USD|€|£)\s?\d/i.test(text)) out.push(t('ai.example.mentionsMoney'))
  return out
}

function clock(sec: number): string {
  const s = Math.round(sec)
  if (s >= 3600) return `${Math.floor(s / 3600)}h${String(Math.floor((s % 3600) / 60)).padStart(2, '0')}`
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`
}

function ExampleTimeline({ values: v }: { values: Record<string, string> }) {
  const t = useT()
  const fmt = useFormat()
  const num = (k: string, d = 0) => (Number.isFinite(Number(v[k])) && v[k] !== '' ? Number(v[k]) : d)
  const on = (k: string) => v[k] === 'true'

  const rows = useMemo<Row[]>(() => {
    const out: Row[] = []
    const wait = Math.max(0, num('ai_wait_seconds'))
    const maxWait = Math.max(wait, num('ai_wait_max_seconds'))
    const mode = v.ai_review_mode || 'auto'
    const firstN = num('ai_review_first_n')
    const autosend = num('ai_review_autosend_minutes')
    const typing = on('ai_typing')
    const cps = Math.max(0.5, num('ai_typing_cps', 8))
    const tMin = num('ai_typing_min_s', 2)
    const tMax = Math.max(tMin, num('ai_typing_max_s', 12))
    const delay = (s: string) => (typing ? Math.min(tMax, Math.max(tMin, s.length / cps)) : 0)

    // which customer messages each reply answers (the debounce)
    const replies: { at: number; covers: number[] }[] = []
    let job: { created: number; due: number } | null = null
    let pending: number[] = []
    SAMPLE.forEach((m, i) => {
      if (job && m.at >= job.due) {
        replies.push({ at: job.due, covers: pending })
        pending = []
        job = null
      }
      out.push({ at: m.at, who: 'customer', text: t(m.text) })
      pending.push(i)
      if (wait === 0) {
        replies.push({ at: m.at, covers: pending })
        pending = []
        return
      }
      if (!job) job = { created: m.at, due: m.at + wait }
      else job.due = Math.min(m.at + wait, job.created + maxWait)
    })
    const lastJob = job as { created: number; due: number } | null
    if (lastJob) replies.push({ at: lastJob.due, covers: pending })

    let at = 0
    replies.forEach((r, idx) => {
      const text = sampleReply(r.covers, t)
      at = Math.max(at, r.at)
      if (wait > 0)
        out.push({
          at: r.at,
          who: 'note',
          text: r.covers.length > 1 ? t('ai.example.oneReply', { count: r.covers.length }) : t('ai.example.startsWriting'),
        })
      const reasons =
        mode === 'review_all'
          ? [t('ai.example.everyReviewed')]
          : mode === 'review_flagged'
            ? [
                ...riskyReasons(text, v.ai_review_keywords ?? '', t),
                ...(idx < firstN
                  ? [firstN === 1 ? t('ai.example.firstReply') : t('ai.example.firstN', { count: firstN })]
                  : []),
              ]
            : []
      if (mode === 'shadow') {
        out.push({ at, who: 'note', text: t('ai.example.shadow') })
        return
      }
      if (reasons.length) {
        out.push({ at, who: 'note', text: t('ai.example.held', { reasons: fmt.list(reasons) }) })
        if (autosend > 0) {
          at += autosend * 60
          out.push({ at, who: 'note', text: t('ai.example.autoAfter', { minutes: autosend }) })
        } else {
          out.push({ at, who: 'note', text: t('ai.example.waitsApproval') })
        }
      }
      const bubbles = splitBubbles(text, v.ai_split || 'off', num('ai_split_max_chars', 320))
      bubbles.forEach((b, bi) => {
        const d = typing ? delay(b) : bi === 0 ? 0 : 2
        if (typing) out.push({ at, who: 'note', text: t('ai.example.typing', { seconds: Math.round(d) }) })
        at += d
        out.push({ at, who: 'ai', text: b })
      })
    })

    // an acknowledgement afterwards
    at += 60
    out.push({ at, who: 'customer', text: t('ai.example.msgAck') })
    if (on('ai_ack_skip')) {
      const ack = (v.ai_ack_text ?? '').trim()
      if (mode !== 'shadow' && ack) out.push({ at: at + Math.max(0, wait), who: 'ai', text: ack })
      else if (mode !== 'shadow' && on('ai_reactions'))
        out.push({ at: at + Math.max(0, wait), who: 'note', text: t('ai.example.ackReaction', { emoji: '🙏' }) })
      else out.push({ at, who: 'note', text: t('ai.example.ackNoReply') })
    } else {
      out.push({ at: at + Math.max(0, wait), who: 'note', text: t('ai.example.repliesToAck') })
    }
    return out
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [v, t, fmt])

  const followUps = useMemo(() => {
    if (!on('ai_followup')) return null
    const first = num('ai_followup_first_hours', 4)
    const between = num('ai_followup_between_hours', 24)
    const max = Math.max(0, Math.round(num('ai_followup_max', 2)))
    const out: { h: number; text: string }[] = []
    for (let i = 0; i < max; i++) out.push({ h: first + i * between, text: t('ai.example.followUp', { n: i + 1 }) })
    return out
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [v, t])

  return (
    <Card className="px-5 py-4">
      <CardTitle>{t('ai.example.title')}</CardTitle>
      <p className="mb-3 mt-0.5 text-[12px] text-text-muted">{t('ai.example.subtitle')}</p>
      <ol className="relative flex flex-col gap-2 border-l border-line pl-4" aria-label={t('ai.example.ariaLabel')}>
        {rows.map((r, i) => (
          <li key={i} className="relative">
            <span
              className={`absolute -left-[21px] top-[7px] h-2 w-2 rounded-full ${
                r.who === 'customer' ? 'bg-text-faint' : r.who === 'ai' ? 'bg-accent' : 'border border-line-strong bg-card'
              }`}
              aria-hidden
            />
            <div className="flex items-baseline gap-2">
              <span className="w-10 shrink-0 text-[10.5px] tabular-nums text-text-faint">{clock(r.at)}</span>
              {r.who === 'note' ? (
                <span className="text-[12px] italic text-text-muted">{r.text}</span>
              ) : (
                <span
                  className={`min-w-0 whitespace-pre-wrap break-words rounded-lg px-2 py-1 text-[12.5px] leading-snug ${
                    r.who === 'customer' ? 'border border-line bg-card text-text-high' : 'bg-fill text-text-high'
                  }`}
                >
                  <span className="mr-1 text-[10px] font-semibold uppercase tracking-wide text-text-faint">
                    {r.who === 'customer' ? t('ai.common.customer') : t('ai.common.ai')}
                  </span>
                  {r.text}
                </span>
              )}
            </div>
          </li>
        ))}
      </ol>
      <div className="mt-4 border-t border-line pt-3">
        <div className="eyebrow mb-1.5">{t('ai.example.ifNoAnswer')}</div>
        {followUps === null ? (
          <p className="text-[12px] text-text-muted">{t('ai.example.followUpsOff')}</p>
        ) : followUps.length === 0 ? (
          <p className="text-[12px] text-text-muted">{t('ai.example.noFollowUps')}</p>
        ) : (
          <ul className="flex flex-col gap-1 text-[12px] text-text-mid">
            {followUps.map((f) => (
              <li key={f.h} className="flex gap-2">
                <span className="w-10 shrink-0 tabular-nums text-text-faint">{t('ai.example.hoursOffset', { hours: f.h })}</span>
                {f.text}
              </li>
            ))}
            <li className="flex gap-2 text-text-muted">
              <span className="w-10 shrink-0" />
              {t('ai.example.thenStops')}
            </li>
          </ul>
        )}
      </div>
    </Card>
  )
}
