'use client'

import { Plus, Trash2 } from 'lucide-react'
import { Advanced } from '@/components/ui/Advanced'
import { Button } from '@/components/ui/Button'
import { EntityPicker } from '@/components/ui/EntityPicker'
import { MultiSelect, Select, type SelectOption } from '@/components/ui/Select'
import { useFormat, useT, type MessageKey } from '@/i18n'
import { CONTENT_LANGUAGES } from '@/i18n/locales'
import { useTags } from '@/lib/tags'
import {
  AI_DEFAULTS,
  CHECK_GROUPS,
  CONVERTIBLE_LEGACY,
  KIND_AI_PICK,
  KIND_AI_YES_NO,
  KIND_CONDITIONS,
  MAX_OPTIONS,
  aiOptionsOf,
  checkDef,
  conditionsOf,
  decisionKind,
  fieldOpsFor,
  historyOf,
  inputFor,
  matchOf,
  nextOptionId,
  thresholdOf,
  unsureOf,
  type DecisionKind,
} from '@/lib/flow/conditions'
import { LEGACY_DECISIONS, MESSAGE_CHANNELS, choiceLabel } from '@/lib/flow/nodeConfig'
import type { DecisionOption, FlowCondition, FlowConfig } from '@/lib/flow/types'
import { Field, Notice, NumberInput, Segmented, SwitchRow, TextInput, textareaCls } from './fields'
import { useFlowView } from './FlowViewContext'

/**
 * The "Ask & branch" editor (Steps view and canvas inspector alike):
 *   Check conditions — rows of subject → operator → value joined by All / Any;
 *   AI yes / no      — a plain-words question over the conversation history;
 *   AI picks a path  — 2–6 options, each its own path, + "None of these".
 * Everything is written straight into the step's config in the engine's
 * English vocabulary (lib/flow/conditions.ts); only the labels translate. A
 * step saved before the condition builder opens as the equivalent condition
 * and is saved in the new shape on its first edit.
 */

const str = (v: unknown) => (v === undefined || v === null ? '' : String(v))

const KIND_VALUE: Record<Exclude<DecisionKind, 'legacy'>, string> = {
  conditions: KIND_CONDITIONS,
  ai_yes_no: KIND_AI_YES_NO,
  ai_pick: KIND_AI_PICK,
}

export default function DecisionEditor({ cfg, onPatch }: { cfg: FlowConfig; onPatch: (p: FlowConfig) => void }) {
  const t = useT()
  const kind = decisionKind(cfg)
  const basis = str(cfg.basedOn)

  const setKind = (k: Exclude<DecisionKind, 'legacy'>) => {
    if (k === kind && basis === KIND_VALUE[k]) return
    if (k === 'conditions') {
      const conds = conditionsOf(cfg)
      onPatch({
        basedOn: KIND_CONDITIONS,
        match: matchOf(cfg),
        conditions: conds.length ? conds : [{ subject: 'reply', op: 'replied' }],
      })
      return
    }
    // keep what the other AI kind already set; fill the rest with defaults
    const patch: FlowConfig = { basedOn: KIND_VALUE[k] }
    for (const [key, v] of Object.entries(AI_DEFAULTS)) if (cfg[key] === undefined) patch[key] = v
    const unsure = str(cfg.unsure)
    if (k === 'ai_pick') {
      if (aiOptionsOf(cfg).length < 2)
        patch.options = [
          { id: 'opt-1', label: '', description: '' },
          { id: 'opt-2', label: '', description: '' },
        ]
      if (!['none', 'wait', 'end'].includes(unsure)) patch.unsure = 'none'
    } else if (!['yes', 'no', 'wait', 'end'].includes(unsure)) patch.unsure = 'no'
    onPatch(patch)
  }

  return (
    <>
      <Field label={t('flows.decisionEditor.kind')}>
        <Segmented
          ariaLabel={t('flows.decisionEditor.kind')}
          value={(kind === 'legacy' ? '' : kind) as Exclude<DecisionKind, 'legacy'>}
          onChange={setKind}
          options={[
            { value: 'conditions', label: t('flows.decision.conditions'), hint: t('flows.decisionEditor.kindConditionsHint') },
            { value: 'ai_yes_no', label: t('flows.decision.aiYesNo'), hint: t('flows.decisionEditor.kindAiYesNoHint') },
            { value: 'ai_pick', label: t('flows.decision.aiPick'), hint: t('flows.decisionEditor.kindAiPickHint') },
          ]}
        />
      </Field>
      {kind === 'legacy' && <LegacyForm cfg={cfg} onPatch={onPatch} />}
      {kind === 'conditions' && <ConditionsForm cfg={cfg} onPatch={onPatch} converted={CONVERTIBLE_LEGACY.includes(basis)} />}
      {(kind === 'ai_yes_no' || kind === 'ai_pick') && <AiForm cfg={cfg} onPatch={onPatch} pick={kind === 'ai_pick'} />}
    </>
  )
}

// ---- legacy checks that still park / read a record ------------------------------------

function LegacyForm({ cfg, onPatch }: { cfg: FlowConfig; onPatch: (p: FlowConfig) => void }) {
  const t = useT()
  const basis = str(cfg.basedOn) || "Contact's answer"
  return (
    <>
      <Notice tone="amber">
        {t('flows.decisionEditor.legacyNotice', { label: LEGACY_DECISIONS[basis] ? t(LEGACY_DECISIONS[basis]) : basis })}
      </Notice>
      {basis === 'Record status' && (
        <Field label={t('flows.config.recordStatus')}>
          <TextInput value={str(cfg.status)} onChange={(v) => onPatch({ status: v })} ariaLabel={t('flows.config.recordStatus')} />
        </Field>
      )}
      <Notice>{t('flows.config.yesNoPaths')}</Notice>
    </>
  )
}

// ---- Check conditions -------------------------------------------------------------------------

const DEFAULT_AMOUNT: Partial<Record<string, number>> = { days: 7, hours: 24, count: 3 }

function ConditionsForm({ cfg, onPatch, converted }: { cfg: FlowConfig; onPatch: (p: FlowConfig) => void; converted: boolean }) {
  const t = useT()
  const conds = conditionsOf(cfg)
  const match = matchOf(cfg)
  const { tags } = useTags('contact')
  const save = (next: FlowCondition[], m: 'all' | 'any' = match) =>
    onPatch({ basedOn: KIND_CONDITIONS, match: m, conditions: next })
  const basis = str(cfg.basedOn)
  return (
    <>
      {converted && (
        <Notice>
          {t('flows.decisionEditor.convertedNotice', { label: LEGACY_DECISIONS[basis] ? t(LEGACY_DECISIONS[basis]) : basis })}
        </Notice>
      )}
      {conds.length > 1 && (
        <Field label={t('flows.decisionEditor.match')}>
          <Segmented
            ariaLabel={t('flows.decisionEditor.match')}
            value={match}
            onChange={(m) => save(conds, m)}
            options={[
              { value: 'all', label: t('flows.decisionEditor.matchAll'), hint: t('flows.decisionEditor.matchAllHint') },
              { value: 'any', label: t('flows.decisionEditor.matchAny'), hint: t('flows.decisionEditor.matchAnyHint') },
            ]}
          />
        </Field>
      )}
      <div className="flex flex-col gap-2">
        {conds.map((c, i) => (
          <div key={i} className="flex flex-col gap-2">
            {i > 0 && (
              <div className="eyebrow text-center text-text-muted">
                {t(match === 'any' ? 'flows.decisionEditor.or' : 'flows.decisionEditor.and')}
              </div>
            )}
            <ConditionRow
              index={i}
              cond={c}
              tags={tags.map((x) => x.name)}
              onChange={(nc) => save(conds.map((x, j) => (j === i ? nc : x)))}
              onRemove={conds.length > 1 ? () => save(conds.filter((_, j) => j !== i)) : undefined}
            />
          </div>
        ))}
      </div>
      <div>
        <Button variant="secondary" size="sm" onClick={() => save([...conds, { subject: 'tag', op: 'has', value: '' }])}>
          <Plus size={13} aria-hidden /> {t('flows.decisionEditor.addCondition')}
        </Button>
      </div>
      <Notice>{t('flows.decisionEditor.conditionsPaths')}</Notice>
    </>
  )
}

function ConditionRow({
  index,
  cond,
  tags,
  onChange,
  onRemove,
}: {
  index: number
  cond: FlowCondition
  tags: string[]
  onChange: (c: FlowCondition) => void
  onRemove?: () => void
}) {
  const t = useT()
  const { lookups } = useFlowView()
  const fields = lookups.fields
  const fieldDef = cond.subject === 'field' ? fields.find((f) => f.key === str(cond.field)) : undefined
  const kind = fieldDef?.kind

  const fieldGroup = t('flows.cond.group.field')
  const whatOptions: SelectOption[] = [
    ...fields.map((f) => ({ value: `field:${f.key}`, label: f.label || f.key, hint: f.key, group: fieldGroup })),
    { value: 'field:', label: t('flows.decisionEditor.otherField'), group: fieldGroup },
    ...CHECK_GROUPS.flatMap((g) => g.checks.map((d) => ({ value: `${d.subject}:${d.op}`, label: t(d.lbl), group: t(g.lbl) }))),
  ]
  const whatValue =
    cond.subject === 'field'
      ? fields.some((f) => f.key === str(cond.field))
        ? `field:${str(cond.field)}`
        : 'field:'
      : `${cond.subject}:${cond.op}`
  if (cond.subject !== 'field' && !checkDef(cond))
    whatOptions.push({ value: whatValue, label: `${cond.subject} ${cond.op}`, group: t('flows.config.noLongerOffered') })

  const pickWhat = (v: string) => {
    if (v.startsWith('field:')) {
      const key = v.slice(6)
      const def = fields.find((f) => f.key === key)
      const ops = fieldOpsFor(def?.kind)
      const op = cond.subject === 'field' && ops.some((o) => o.op === cond.op) ? cond.op : ops[0].op
      const same = cond.subject === 'field' && str(cond.field) === key
      const next: FlowCondition = { subject: 'field', op, field: key || (cond.subject === 'field' && !def ? str(cond.field) : '') }
      if (same) Object.assign(next, { value: cond.value, values: cond.values, amount: cond.amount })
      onChange(withAmount(next, def?.kind))
      return
    }
    const [subject, op] = v.split(':')
    if (subject === cond.subject && op === cond.op) return
    onChange(withAmount({ subject, op, value: subject === 'contact' && op === 'opted_out' ? 'any' : '' }))
  }

  const ops = fieldOpsFor(kind)
  return (
    <div className="rounded-xl border border-line-strong bg-card p-2.5">
      <div className="flex items-start gap-2">
        <div className="flex min-w-0 flex-1 flex-col gap-2">
          <Select
            value={whatValue}
            onChange={pickWhat}
            options={whatOptions}
            size="md"
            searchable
            ariaLabel={t('flows.decisionEditor.whatToCheck', { n: index + 1 })}
          />
          {cond.subject === 'field' && !fieldDef && (
            <TextInput
              value={str(cond.field)}
              onChange={(v) => onChange({ ...cond, field: v.trim() })}
              placeholder={t('flows.config.fieldKeyPlaceholder')}
              ariaLabel={t('flows.config.field')}
            />
          )}
          {cond.subject === 'field' && (
            <Select
              value={cond.op}
              onChange={(op) => onChange(withAmount({ ...cond, op }, kind))}
              options={[
                ...ops.map((o) => ({ value: o.op, label: t(o.lbl) })),
                ...(ops.some((o) => o.op === cond.op) ? [] : [{ value: cond.op, label: cond.op }]),
              ]}
              size="md"
              ariaLabel={t('flows.decisionEditor.operator')}
            />
          )}
          <ValueInput cond={cond} fieldKind={kind} fieldOptions={fieldDef?.options ?? []} tags={tags} onChange={onChange} />
        </div>
        {onRemove && (
          <button
            type="button"
            onClick={onRemove}
            title={t('flows.decisionEditor.removeCondition')}
            aria-label={t('flows.decisionEditor.removeCondition')}
            className="grid h-9 w-8 shrink-0 place-items-center rounded-md text-text-muted hover:bg-fill-hover hover:text-[color:var(--red-ink)]"
          >
            <Trash2 size={14} aria-hidden />
          </button>
        )}
      </div>
    </div>
  )
}

/** Days / hours / counts start with a sensible number. */
function withAmount(c: FlowCondition, fieldKind?: string): FlowCondition {
  const input = inputFor(c, fieldKind)
  const d = DEFAULT_AMOUNT[input]
  return d !== undefined && !(Number(c.amount) > 0) ? { ...c, amount: d } : c
}

const AMOUNT_UNIT: Partial<Record<string, MessageKey>> = {
  days: 'flows.decisionEditor.unitDays',
  hours: 'flows.decisionEditor.unitHours',
  count: 'flows.decisionEditor.unitMessages',
}

function ValueInput({
  cond,
  fieldKind,
  fieldOptions,
  tags,
  onChange,
}: {
  cond: FlowCondition
  fieldKind?: string
  fieldOptions: string[]
  tags: string[]
  onChange: (c: FlowCondition) => void
}) {
  const t = useT()
  const { lookups } = useFlowView()
  const input = inputFor(cond, fieldKind)
  const value = str(cond.value)
  const setValue = (v: string) => onChange({ ...cond, value: v })
  const label = t('flows.config.value')
  const withCurrent = (opts: SelectOption[]) =>
    value && !opts.some((o) => o.value === value) ? [...opts, { value, label: value }] : opts

  switch (input) {
    case 'none':
      return null
    case 'text':
      return <TextInput value={value} onChange={setValue} ariaLabel={label} placeholder={t('flows.decisionEditor.valuePlaceholder')} />
    case 'words':
      return (
        <Field label={t('flows.decisionEditor.wordsLabel')} help={t('flows.config.anyOfWordsHelp')}>
          <TextInput value={value} onChange={setValue} ariaLabel={t('flows.config.keywords')} placeholder={t('flows.config.anyOfWordsPlaceholder')} />
        </Field>
      )
    case 'number':
      return <NumberInput value={value} onChange={(v) => setValue(v === '' ? '' : String(v))} step={1} min={-1e12} ariaLabel={label} />
    case 'date':
      return <TextInput type="date" value={value} onChange={setValue} ariaLabel={label} />
    case 'days':
    case 'hours':
    case 'count':
      return (
        <div className="flex items-center gap-2">
          <NumberInput
            value={cond.amount ?? ''}
            onChange={(v) => onChange({ ...cond, amount: v === '' ? undefined : v })}
            min={1}
            ariaLabel={label}
            className="!w-24"
          />
          <span className="text-[12.5px] text-text-mid">{t(AMOUNT_UNIT[input]!, { count: Number(cond.amount) || 0 })}</span>
        </div>
      )
    case 'tag':
      return tags.length ? (
        <Select
          value={value}
          onChange={setValue}
          options={withCurrent(tags.map((n) => ({ value: n, label: n })))}
          size="md"
          searchable
          placeholder={t('flows.decisionEditor.chooseTag')}
          ariaLabel={t('flows.config.tag')}
        />
      ) : (
        <TextInput value={value} onChange={setValue} placeholder={t('flows.config.vipPlaceholder')} ariaLabel={t('flows.config.tag')} />
      )
    case 'language':
      return (
        <Select
          value={value}
          onChange={setValue}
          options={withCurrent(CONTENT_LANGUAGES.map((l) => ({ value: l.code, label: l.native })))}
          size="md"
          placeholder={t('flows.decisionEditor.chooseLanguage')}
          ariaLabel={t('flows.decisionEditor.language')}
        />
      )
    case 'entity':
      return (
        <EntityPicker
          value={value || null}
          onChange={(slug) => setValue(slug ?? '')}
          allowGlobal={false}
          ariaLabel={t('flows.decisionEditor.entity')}
        />
      )
    case 'optedScope':
      return (
        <Segmented
          ariaLabel={t('flows.decisionEditor.optedScope')}
          value={value === 'this' ? 'this' : 'any'}
          onChange={setValue}
          options={[
            { value: 'any', label: t('flows.decisionEditor.optedAny') },
            { value: 'this', label: t('flows.decisionEditor.optedThis') },
          ]}
        />
      )
    case 'status':
      return (
        <Select
          value={value}
          onChange={setValue}
          options={withCurrent(
            (['open', 'pending', 'snoozed', 'resolved'] as const).map((s) => ({ value: s, label: t(`chats.status.${s}`) })),
          )}
          size="md"
          placeholder={t('flows.decisionEditor.chooseStatus')}
          ariaLabel={t('flows.decisionEditor.status')}
        />
      )
    case 'team':
      return (
        <Select
          value={value}
          onChange={setValue}
          options={withCurrent(lookups.teams.map((tm) => ({ value: String(tm.id), label: tm.name })))}
          size="md"
          placeholder={t('flows.decisionEditor.chooseTeam')}
          ariaLabel={t('flows.config.team')}
        />
      )
    case 'channel':
      return (
        <Select
          value={value}
          onChange={setValue}
          options={withCurrent(MESSAGE_CHANNELS.filter((c) => c.value).map((c) => ({ value: c.value, label: choiceLabel(t, c) })))}
          size="md"
          placeholder={t('flows.decisionEditor.chooseChannel')}
          ariaLabel={t('flows.decisionEditor.channel')}
        />
      )
    case 'fieldOption':
      return fieldOptions.length ? (
        <Select
          value={value}
          onChange={setValue}
          options={withCurrent(fieldOptions.map((o) => ({ value: o, label: o })))}
          size="md"
          placeholder={t('flows.decisionEditor.chooseOption')}
          ariaLabel={label}
        />
      ) : (
        <TextInput value={value} onChange={setValue} ariaLabel={label} />
      )
    case 'fieldOptions':
      return fieldOptions.length ? (
        <MultiSelect
          value={cond.values ?? []}
          onChange={(vs) => onChange({ ...cond, values: vs, value: '' })}
          options={fieldOptions.map((o) => ({ value: o, label: o }))}
          size="md"
          placeholder={t('flows.decisionEditor.chooseOptions')}
          ariaLabel={label}
        />
      ) : (
        <TextInput value={value} onChange={setValue} ariaLabel={label} placeholder={t('flows.config.anyOfWordsPlaceholder')} />
      )
    default:
      return null
  }
}

// ---- Let AI decide ------------------------------------------------------------------------------

const THRESHOLDS = [0.5, 0.6, 0.7, 0.8, 0.9]

function AiForm({ cfg, onPatch, pick }: { cfg: FlowConfig; onPatch: (p: FlowConfig) => void; pick: boolean }) {
  const t = useT()
  const f = useFormat()
  const h = historyOf(cfg)
  const threshold = thresholdOf(cfg)
  const unsure = unsureOf(cfg)
  const thresholdOptions = [...new Set([...THRESHOLDS, threshold])]
    .sort((a, b) => a - b)
    .map((n) => ({ value: String(n), label: f.percent(n) }))
  // Advanced holds everything but the question / paths; open it when any value is customised.
  const maxAsks = cfg.maxAsks === undefined || cfg.maxAsks === '' ? 2 : Number(cfg.maxAsks)
  const waitHours = cfg.waitHours === undefined || cfg.waitHours === '' ? 24 : Number(cfg.waitHours)
  const customised = [
    h.messages !== 20,
    h.scope !== 'all',
    h.sinceEntered,
    threshold !== 0.6,
    unsure !== (pick ? 'none' : 'no'),
    unsure === 'wait' && maxAsks !== 2,
    unsure === 'wait' && waitHours !== 24,
    pick && aiOptionsOf(cfg).some((o) => str(o.description).trim() !== ''),
  ].filter(Boolean).length
  const unsureOptions: SelectOption[] = pick
    ? [
        { value: 'none', label: t('flows.decisionEditor.unsureNone') },
        { value: 'wait', label: t('flows.decisionEditor.unsureWait') },
        { value: 'end', label: t('flows.decisionEditor.unsureEnd') },
      ]
    : [
        { value: 'no', label: t('flows.decisionEditor.unsureNo') },
        { value: 'yes', label: t('flows.decisionEditor.unsureYes') },
        { value: 'wait', label: t('flows.decisionEditor.unsureWait') },
        { value: 'end', label: t('flows.decisionEditor.unsureEnd') },
      ]
  return (
    <>
      <Field
        label={t('flows.decisionEditor.question')}
        help={t(pick ? 'flows.decisionEditor.questionHelpPick' : 'flows.decisionEditor.questionHelp')}
      >
        <textarea
          rows={3}
          value={str(cfg.question)}
          onChange={(e) => onPatch({ question: e.target.value })}
          placeholder={t(pick ? 'flows.decisionEditor.questionPlaceholderPick' : 'flows.decisionEditor.questionPlaceholder')}
          aria-label={t('flows.decisionEditor.question')}
          className={textareaCls}
        />
      </Field>

      {pick && <OptionsEditor cfg={cfg} onPatch={onPatch} />}

      <Advanced count={customised} forceOpen={customised > 0}>
        {pick && <OptionDescriptions cfg={cfg} onPatch={onPatch} />}
        <div className="flex flex-col gap-3 rounded-xl border border-line bg-fill-subtle p-3">
          <div className="eyebrow">{t('flows.decisionEditor.historyTitle')}</div>
          <Field label={t('flows.decisionEditor.historyMessages')} help={t('flows.decisionEditor.historyMessagesHelp')}>
            <NumberInput
              value={cfg.historyMessages === undefined ? h.messages : str(cfg.historyMessages)}
              onChange={(v) => onPatch({ historyMessages: v === '' ? '' : Math.min(100, Math.max(5, Math.round(v))) })}
              min={5}
              step={5}
              ariaLabel={t('flows.decisionEditor.historyMessages')}
              className="!w-24"
            />
          </Field>
          <Field label={t('flows.decisionEditor.historyFrom')}>
            <Segmented
              ariaLabel={t('flows.decisionEditor.historyFrom')}
              value={h.scope}
              onChange={(v) => onPatch({ historyScope: v })}
              options={[
                { value: 'all', label: t('flows.decisionEditor.scopeAll') },
                { value: 'channel', label: t('flows.decisionEditor.scopeChannel') },
                { value: 'conversation', label: t('flows.decisionEditor.scopeConversation') },
              ]}
            />
          </Field>
          <SwitchRow
            label={t('flows.decisionEditor.sinceEntered')}
            help={t('flows.decisionEditor.sinceEnteredHelp')}
            on={h.sinceEntered}
            onChange={(v) => onPatch({ sinceEntered: v })}
          />
        </div>

        <Field label={t('flows.decisionEditor.threshold')} help={t('flows.decisionEditor.thresholdHelp')}>
          <Select
            value={String(threshold)}
            onChange={(v) => onPatch({ threshold: Number(v) })}
            options={thresholdOptions}
            size="md"
            ariaLabel={t('flows.decisionEditor.threshold')}
          />
        </Field>
        <Field label={t('flows.decisionEditor.whenUnsure')} help={t('flows.decisionEditor.whenUnsureHelp')}>
          <Select
            value={unsure}
            onChange={(v) => onPatch({ unsure: v })}
            options={unsureOptions}
            size="md"
            ariaLabel={t('flows.decisionEditor.whenUnsure')}
          />
        </Field>
        {unsure === 'wait' && (
          <div className="grid grid-cols-2 gap-3">
            <Field label={t('flows.decisionEditor.maxAsks')}>
              <NumberInput
                value={cfg.maxAsks === undefined ? 2 : str(cfg.maxAsks)}
                onChange={(v) => onPatch({ maxAsks: v === '' ? '' : Math.min(5, Math.max(1, Math.round(v))) })}
                min={1}
                ariaLabel={t('flows.decisionEditor.maxAsks')}
              />
            </Field>
            <Field label={t('flows.decisionEditor.waitHours')} help={t('flows.decisionEditor.waitHoursHelp')}>
              <NumberInput
                value={cfg.waitHours === undefined ? 24 : str(cfg.waitHours)}
                onChange={(v) => onPatch({ waitHours: v === '' ? '' : Math.max(0, Math.round(v)) })}
                min={0}
                ariaLabel={t('flows.decisionEditor.waitHours')}
              />
            </Field>
          </div>
        )}
      </Advanced>
      <Notice>{t(pick ? 'flows.decisionEditor.aiPickNotice' : 'flows.decisionEditor.aiNotice')}</Notice>
    </>
  )
}

/** The optional "when to pick it" words per path — Advanced, so the paths stay a short list. */
function OptionDescriptions({ cfg, onPatch }: { cfg: FlowConfig; onPatch: (p: FlowConfig) => void }) {
  const t = useT()
  const options = aiOptionsOf(cfg)
  const patch = (i: number, description: string) =>
    onPatch({ options: options.map((o, j) => (j === i ? { ...o, description } : o)) })
  return (
    <Field label={t('flows.decisionEditor.optionDescriptions')} help={t('flows.decisionEditor.optionDescriptionsHelp')}>
      <div className="flex flex-col gap-2">
        {options.map((o, i) => (
          <div key={o.id} className="flex items-start gap-2">
            <span className="mt-2 grid h-5 w-5 shrink-0 place-items-center rounded-full border border-blue/40 text-[10.5px] font-bold text-[color:var(--blue-ink)]">
              {i + 1}
            </span>
            <div className="flex min-w-0 flex-1 flex-col gap-1">
              {o.label.trim() && <span className="truncate text-[12px] font-medium text-text-high">{o.label}</span>}
              <TextInput
                value={str(o.description)}
                onChange={(v) => patch(i, v)}
                placeholder={t('flows.decisionEditor.optionDescriptionPlaceholder')}
                ariaLabel={t('flows.decisionEditor.optionDescription', { n: i + 1 })}
              />
            </div>
          </div>
        ))}
      </div>
    </Field>
  )
}

function OptionsEditor({ cfg, onPatch }: { cfg: FlowConfig; onPatch: (p: FlowConfig) => void }) {
  const t = useT()
  const options = aiOptionsOf(cfg)
  const save = (next: DecisionOption[]) => onPatch({ options: next })
  const patch = (i: number, p: Partial<DecisionOption>) => save(options.map((o, j) => (j === i ? { ...o, ...p } : o)))
  return (
    <Field label={t('flows.decisionEditor.options')} help={t('flows.decisionEditor.optionsHelp')}>
      <div className="flex flex-col gap-2">
        {options.map((o, i) => (
          <div key={o.id} className="flex items-start gap-2 rounded-xl border border-line-strong bg-card p-2.5">
            <span className="mt-2 grid h-5 w-5 shrink-0 place-items-center rounded-full border border-blue/40 text-[10.5px] font-bold text-[color:var(--blue-ink)]">
              {i + 1}
            </span>
            <div className="flex min-w-0 flex-1 flex-col gap-1.5">
              <TextInput
                value={o.label}
                onChange={(v) => patch(i, { label: v })}
                placeholder={t('flows.decisionEditor.optionLabelPlaceholder')}
                ariaLabel={t('flows.decisionEditor.optionLabel', { n: i + 1 })}
              />
            </div>
            {options.length > 2 && (
              <button
                type="button"
                onClick={() => save(options.filter((_, j) => j !== i))}
                title={t('flows.decisionEditor.removeOption')}
                aria-label={t('flows.decisionEditor.removeOption')}
                className="grid h-9 w-8 shrink-0 place-items-center rounded-md text-text-muted hover:bg-fill-hover hover:text-[color:var(--red-ink)]"
              >
                <Trash2 size={14} aria-hidden />
              </button>
            )}
          </div>
        ))}
        <div className="rounded-xl border border-dashed border-line px-3 py-2 text-[12px] text-text-muted">
          {t('flows.decisionEditor.noneOption')}
        </div>
        {options.length < MAX_OPTIONS && (
          <div>
            <Button
              variant="secondary"
              size="sm"
              onClick={() => save([...options, { id: nextOptionId(options), label: '', description: '' }])}
            >
              <Plus size={13} aria-hidden /> {t('flows.decisionEditor.addOption')}
            </Button>
          </div>
        )}
      </div>
    </Field>
  )
}
