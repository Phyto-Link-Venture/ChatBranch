'use client'

import { useState, type ReactNode } from 'react'
import { Play, Trash2, X } from 'lucide-react'
import { Advanced } from '@/components/ui/Advanced'
import { Button } from '@/components/ui/Button'
import { MultiSelect, Select, type SelectOption } from '@/components/ui/Select'
import { Trans, useT } from '@/i18n'
import {
  AI_MESSAGE,
  CALL_CHANNELS,
  CHATBOT_CHANNELS,
  DURATION_UNITS,
  INTEGRATION_ACTIONS,
  INTEGRATION_ACTION_LABELS,
  LEGACY_REPEATS,
  LEGACY_TRIGGERS,
  MESSAGE_CHANNELS,
  REPEAT_OPTIONS,
  SEND_CHANNELS,
  TEAM_PUSH,
  TRIGGERS,
  WEEKDAYS,
  actionChannelLabel,
  actionType,
  channelsOf,
  choiceOptions,
  durationText,
  isChatbotStep,
  type ActionType,
} from '@/lib/flow/nodeConfig'
import { describeStep } from '@/lib/flow/describe'
import { startFlow } from '@/lib/flow/api'
import type { FlowConfig, FlowNode } from '@/lib/flow/types'
import ContactScopePicker from './ContactScopePicker'
import DecisionEditor from './DecisionEditor'
import SkillPicker from './SkillPicker'
import VariableTextarea from './VariableTextarea'
import WaTemplateConfig from './WaTemplateConfig'
import { Field, Notice, NumberInput, Segmented, SwitchRow, TextInput, inputCls } from './fields'
import { useFlowView } from './FlowViewContext'
import { StepIcon } from './stepIcons'

/**
 * The step editor — one plain-language form per step type, used by BOTH views
 * (Steps list and Advanced canvas). Every control writes straight into the
 * node's `data.config`, which is exactly what the engine executes: option
 * VALUES stay the engine's English vocabulary, only their labels translate.
 */

const str = (v: unknown) => (v === undefined || v === null ? '' : String(v))
const bool = (v: unknown) => v === true || v === 'true' || v === 'Yes' || v === 'yes'

export default function ConfigPanel({
  node,
  flowId,
  onPatch,
  onLabel,
  onDelete,
  onClose,
}: {
  node: FlowNode
  flowId: string
  onPatch: (patch: FlowConfig) => void
  onLabel: (label: string) => void
  onDelete?: () => void
  onClose: () => void
}) {
  const t = useT()
  const { describeCtx, issues } = useFlowView()
  const cfg = node.data.config ?? {}
  const text = describeStep(t, node, describeCtx)
  const set = (key: string, value: FlowConfig[string]) => onPatch({ [key]: value })
  const myIssues = issues[node.id] ?? []

  return (
    <aside className="flex h-full w-full flex-col bg-card" aria-label={t('flows.config.settings')}>
      <header className="flex items-start gap-3 border-b border-line px-4 py-3">
        <span
          className={`mt-0.5 grid h-8 w-8 shrink-0 place-items-center rounded-lg border ${
            node.type === 'trigger' ? 'border-accent text-[color:var(--brand-ink)]' : 'border-line-strong text-text-high'
          } bg-well`}
        >
          <StepIcon kind={String(node.type)} config={cfg} size={16} />
        </span>
        <div className="min-w-0 flex-1">
          <div className="eyebrow" style={node.type === 'trigger' ? { color: 'var(--brand-ink)' } : undefined}>
            {text.eyebrow}
          </div>
          <div className="display mt-0.5 text-[17px] leading-snug text-text-heading">{text.title}</div>
        </div>
        <button
          type="button"
          onClick={onClose}
          aria-label={t('flows.config.close')}
          className="grid h-8 w-8 shrink-0 place-items-center rounded-lg text-text-muted hover:bg-fill-hover hover:text-text-high"
        >
          <X size={16} aria-hidden />
        </button>
      </header>

      <div className="flex min-h-0 flex-1 flex-col gap-4 overflow-y-auto px-4 py-4">
        {myIssues.length > 0 && (
          <Notice tone="amber">
            {myIssues.map((m) => (
              <div key={m}>{t(m)}</div>
            ))}
          </Notice>
        )}

        {node.type === 'trigger' && <TriggerForm cfg={cfg} set={set} onPatch={onPatch} flowId={flowId} />}
        {node.type === 'action' && <ActionForm cfg={cfg} onPatch={onPatch} set={set} />}
        {node.type === 'wait' && <WaitForm cfg={cfg} set={set} />}
        {node.type === 'followup' && <FollowUpForm cfg={cfg} set={set} onPatch={onPatch} />}
        {node.type === 'decision' && <DecisionEditor cfg={cfg} onPatch={onPatch} />}
        {node.type === 'handover' && <HandoverForm cfg={cfg} set={set} />}
        {node.type === 'update' && <UpdateForm cfg={cfg} set={set} />}
        {node.type === 'end' && <EndForm cfg={cfg} set={set} />}
        {node.type === 'stage' && <Notice>{t('flows.config.legacySection')}</Notice>}

        <div className="border-t border-line pt-4">
          <Field label={t('flows.config.nameLabel')} help={t('flows.config.nameHelp')}>
            <TextInput value={str(node.data.label)} onChange={onLabel} placeholder={t('flows.config.namePlaceholder')} ariaLabel={t('flows.config.stepName')} />
          </Field>
        </div>
      </div>

      {onDelete && (
        <footer className="flex items-center justify-between gap-2 border-t border-line bg-surface px-4 py-2.5">
          <span className="min-w-0 text-[11.5px] text-text-muted">{t('flows.config.autosaves')}</span>
          <Button variant="danger" size="sm" onClick={onDelete}>
            <Trash2 size={13} aria-hidden /> {t('flows.config.deleteStep')}
          </Button>
        </footer>
      )}
    </aside>
  )
}

type SetFn = (key: string, value: FlowConfig[string]) => void

// ---- WHEN ---------------------------------------------------------------------------

function TriggerForm({ cfg, set, onPatch, flowId }: { cfg: FlowConfig; set: SetFn; onPatch: (p: FlowConfig) => void; flowId: string }) {
  const t = useT()
  const { lookups } = useFlowView()
  const when = str(cfg.when) || 'Days after due date'
  const legacy = !TRIGGERS.some((x) => x.value === when)
  const options: SelectOption[] = [
    ...choiceOptions(t, TRIGGERS),
    ...(legacy
      ? [
          {
            value: when,
            label: t('flows.config.legacySuffix', { label: LEGACY_TRIGGERS[when] ? t(LEGACY_TRIGGERS[when]) : when }),
            group: t('flows.config.noLongerOffered'),
          },
        ]
      : []),
  ]
  const contacts = Array.isArray(cfg.contacts) ? cfg.contacts.map(String) : []
  const contactLabels =
    cfg.contactLabels && typeof cfg.contactLabels === 'object' && !Array.isArray(cfg.contactLabels)
      ? (cfg.contactLabels as Record<string, string>)
      : {}
  const audience = (str(cfg.audience) || (contacts.length ? 'some' : str(cfg.onlyTag) ? 'tag' : 'all')) as 'all' | 'tag' | 'some'
  const defaultRepeat = when === 'Message received' ? 'Every time' : 'Once'
  const repeat = str(cfg.repeat) || defaultRepeat
  const showAudience = when !== 'Manual / API'
  const showRepeat = when !== 'Scheduled' && when !== 'Manual / API' && when !== 'Contact created'
  const advancedSet = (showAudience && audience !== 'all' ? 1 : 0) + (showRepeat && repeat !== defaultRepeat ? 1 : 0)
  const repeatOptions: SelectOption[] = [
    ...choiceOptions(t, REPEAT_OPTIONS),
    ...(LEGACY_REPEATS[repeat] ? [{ value: repeat, label: t(LEGACY_REPEATS[repeat]) }] : []),
  ]

  return (
    <>
      <Field label={t('flows.config.startWhen')}>
        <Select value={when} onChange={(v) => set('when', v)} options={options} size="md" ariaLabel={t('flows.config.trigger')} />
      </Field>
      {legacy && <Notice tone="amber">{t('flows.config.legacyTriggerNotice')}</Notice>}

      {(when === 'Tag added' || when === 'Contact has tag') && (
        <Field label={t('flows.config.tag')} help={t('flows.config.tagHelp')}>
          <TextInput value={str(cfg.tag)} onChange={(v) => set('tag', v)} placeholder={t('flows.config.tagPlaceholder')} ariaLabel={t('flows.config.tag')} />
        </Field>
      )}

      {when === 'Message received' && (
        <>
          <Field label={t('flows.config.channelOn')} help={t('flows.config.channelOnHelp')}>
            <Select
              value={str(cfg.channel)}
              onChange={(v) => set('channel', v)}
              options={choiceOptions(t, MESSAGE_CHANNELS)}
              size="md"
              ariaLabel={t('common.fields.channel')}
            />
          </Field>
          <Field label={t('flows.config.containsLabel')} help={t('flows.config.containsHelp')}>
            <TextInput value={str(cfg.keyword)} onChange={(v) => set('keyword', v)} placeholder={t('flows.config.containsPlaceholder')} ariaLabel={t('flows.config.keywords')} />
          </Field>
        </>
      )}

      {when === 'Date field' && (
        <>
          <Field label={t('flows.config.dateField')} help={t('flows.config.dateFieldHelp')}>
            <FieldPicker value={str(cfg.field)} onChange={(v) => set('field', v)} />
          </Field>
          <Field label={t('flows.config.when')}>
            <div className="flex flex-wrap items-center gap-2">
              {cfg.dateWhen !== 'on' && cfg.dateWhen && (
                <NumberInput value={cfg.days as number} onChange={(v) => set('days', v)} min={1} ariaLabel={t('flows.config.days')} className="!w-20" />
              )}
              <div className="min-w-[180px] flex-1">
                <Select
                  value={str(cfg.dateWhen) || 'on'}
                  onChange={(v) => onPatch({ dateWhen: v, days: v === 'on' ? 0 : Number(cfg.days) || 1 })}
                  options={[
                    { value: 'before', label: t('flows.config.daysBeforeDate') },
                    { value: 'on', label: t('flows.config.onTheDate') },
                    { value: 'after', label: t('flows.config.daysAfterDate') },
                  ]}
                  size="md"
                  ariaLabel={t('flows.config.beforeOnAfter')}
                />
              </div>
            </div>
          </Field>
          <SwitchRow
            label={t('flows.config.everyYear')}
            help={t('flows.config.everyYearHelp')}
            on={bool(cfg.yearly)}
            onChange={(v) => set('yearly', v)}
          />
        </>
      )}

      {when === 'Scheduled' && (
        <>
          <Field label={t('flows.config.time')} help={t('flows.config.timeHelp')}>
            <TextInput type="time" value={str(cfg.time) || '09:00'} onChange={(v) => set('time', v)} ariaLabel={t('flows.config.time')} />
          </Field>
          <Field label={t('flows.config.onTheseDays')} help={t('flows.config.onTheseDaysHelp')}>
            <MultiSelect
              value={Array.isArray(cfg.weekdays) ? cfg.weekdays.map(String) : []}
              onChange={(v) => set('weekdays', v)}
              options={choiceOptions(t, WEEKDAYS)}
              size="md"
              placeholder={t('flows.config.everyDay')}
              maxChips={7}
              ariaLabel={t('flows.config.weekdays')}
            />
          </Field>
          {audience === 'all' && <Notice tone="amber">{t('flows.config.scheduleEveryone')}</Notice>}
        </>
      )}

      {when === 'Manual / API' && <ManualStart flowId={flowId} />}

      {(when === 'Record status is' || when.includes('due date')) && (
        <Field label={when === 'Record status is' ? t('flows.config.recordStatus') : t('flows.config.days')}>
          {when === 'Record status is' ? (
            <TextInput value={str(cfg.status)} onChange={(v) => set('status', v)} ariaLabel={t('flows.config.recordStatus')} />
          ) : (
            <NumberInput value={cfg.days as number} onChange={(v) => set('days', v)} ariaLabel={t('flows.config.days')} />
          )}
        </Field>
      )}

      {(showAudience || showRepeat) && (
        <Advanced count={advancedSet} forceOpen={advancedSet > 0}>
          {showAudience && (
            <div className="flex flex-col gap-3 rounded-lg border border-line bg-well p-3">
              <Field label={t('flows.config.whoCanEnter')}>
                <Segmented
                  ariaLabel={t('flows.config.audience')}
                  value={audience}
                  onChange={(v) => {
                    if (v === 'all') onPatch({ audience: 'all', contacts: [], contactLabels: undefined, onlyTag: '' })
                    if (v === 'tag') onPatch({ audience: 'tag', contacts: [], contactLabels: undefined })
                    if (v === 'some') onPatch({ audience: 'some', onlyTag: '' })
                  }}
                  options={[
                    { value: 'all', label: t('flows.config.audienceAll') },
                    { value: 'tag', label: t('flows.config.audienceTag') },
                    { value: 'some', label: t('flows.config.audienceSome') },
                  ]}
                />
              </Field>
              {audience === 'tag' && (
                <TextInput value={str(cfg.onlyTag)} onChange={(v) => set('onlyTag', v)} placeholder={t('flows.config.onlyTagged')} ariaLabel={t('flows.config.onlyTagged')} />
              )}
              {audience === 'some' && (
                <>
                  <ContactScopePicker
                    selected={contacts}
                    labels={contactLabels}
                    onChange={(ids, labels) => onPatch({ contacts: ids, contactLabels: labels })}
                  />
                  {contacts.length === 0 && (
                    <div className="text-[11.5px] text-[color:var(--amber-ink)]">{t('flows.config.pickAtLeastOne')}</div>
                  )}
                </>
              )}
            </div>
          )}

          {showRepeat && (
            <Field label={t('flows.config.ifAgain')}>
              <Select value={repeat} onChange={(v) => set('repeat', v)} options={repeatOptions} size="md" ariaLabel={t('flows.config.repeat')} />
            </Field>
          )}
        </Advanced>
      )}
      {lookups.fields.length === 0 && when === 'Date field' && (
        <div className="text-[11.5px] text-text-muted">{t('flows.config.noDateFields')}</div>
      )}
    </>
  )
}

function ManualStart({ flowId }: { flowId: string }) {
  const t = useT()
  const [ids, setIds] = useState<string[]>([])
  const [labels, setLabels] = useState<Record<string, string>>({})
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)
  const [busy, setBusy] = useState(false)
  return (
    <div className="flex flex-col gap-2 rounded-lg border border-line bg-well p-3">
      <div className="text-[12px] font-semibold text-text-mid">{t('flows.config.startNowFor')}</div>
      <ContactScopePicker
        selected={ids}
        labels={labels}
        onChange={(i, l) => {
          setIds(i)
          setLabels(l)
        }}
      />
      <div className="flex items-center justify-between gap-2">
        <span className="min-w-0 text-[11.5px] text-text-muted">
          {t('flows.config.manualHelp', { endpoint: `POST /api/flows/${flowId}/start` })}
        </span>
        <Button
          size="sm"
          disabled={!ids.length || busy}
          onClick={async () => {
            setBusy(true)
            const r = await startFlow(flowId, ids.map(Number))
            setBusy(false)
            if (typeof r === 'string') setMsg({ ok: false, text: r })
            else {
              setMsg({
                ok: true,
                text: r.skipped
                  ? t('flows.config.startedSkipped', { started: r.started, count: r.skipped })
                  : t('flows.config.started', { count: r.started }),
              })
              setIds([])
              setLabels({})
            }
          }}
        >
          <Play size={12} aria-hidden /> {t('flows.config.start')}
        </Button>
      </div>
      {msg && (
        <div role={msg.ok ? 'status' : 'alert'} className={`text-[12px] ${msg.ok ? 'text-[color:var(--green-ink)]' : 'text-[color:var(--red-ink)]'}`}>
          {msg.text}
        </div>
      )}
    </div>
  )
}

/** A customer custom field — a dropdown when fields exist, else a key box. */
function FieldPicker({ value, onChange }: { value: string; onChange: (v: string) => void }) {
  const t = useT()
  const { lookups } = useFlowView()
  if (!lookups.fields.length)
    return <TextInput value={value} onChange={onChange} placeholder={t('flows.config.fieldKeyPlaceholder')} ariaLabel={t('flows.config.field')} />
  const options: SelectOption[] = lookups.fields.map((fd) => ({ value: fd.key, label: fd.label || fd.key, hint: fd.key }))
  if (value && !options.some((o) => o.value === value)) options.push({ value, label: t('flows.config.notAField', { value }) })
  return (
    <Select value={value} onChange={onChange} options={options} size="md" searchable ariaLabel={t('flows.config.field')} placeholder={t('flows.config.chooseField')} />
  )
}

// ---- SEND / AI / CALL / APP -----------------------------------------------------------------

/** What the chosen channels need on screen (template mode, email subject) — always visible. */
function DeliveryFields({
  cfg,
  onPatch,
  channels,
  ai,
}: {
  cfg: FlowConfig
  onPatch: (p: FlowConfig) => void
  channels: string[]
  ai: boolean
}) {
  const t = useT()
  const deliver = channels.filter((c) => c !== AI_MESSAGE)
  return (
    <>
      {deliver.includes('WhatsApp') && (
        <WaTemplateConfig
          config={cfg}
          onPatch={onPatch}
          modes={ai ? ['text', 'auto'] : ['text', 'template', 'auto']}
          textLabel={ai ? t('flows.config.aiText') : undefined}
        />
      )}
      {deliver.includes('Email') && (
        <Field label={t('flows.config.emailSubject')}>
          <TextInput
            value={str(cfg.subject)}
            onChange={(v) => onPatch({ subject: v })}
            placeholder={t('flows.config.emailSubjectPlaceholder')}
            ariaLabel={t('flows.config.emailSubject')}
          />
        </Field>
      )}
    </>
  )
}

/**
 * The step's Advanced settings: which channels it sends on (default WhatsApp), who receives it
 * (default the primary number / address), human-like typing (default off) and any `extra`
 * step-specific fields (`extraSet` = how many of those are customised).
 */
function DeliveryAdvanced({
  cfg,
  onPatch,
  set,
  channels,
  ai,
  children,
  extraSet = 0,
}: {
  cfg: FlowConfig
  onPatch: (p: FlowConfig) => void
  set: SetFn
  channels: string[]
  ai: boolean
  children?: ReactNode
  extraSet?: number
}) {
  const t = useT()
  const deliver = channels.filter((c) => c !== AI_MESSAGE)
  const allLinked = bool(cfg.allLinked ?? cfg.waAllNumbers)
  const opts = ai
    ? choiceOptions(t, SEND_CHANNELS.filter((c) => c.value !== 'SMS'))
    : [...choiceOptions(t, SEND_CHANNELS), { value: TEAM_PUSH, label: t('flows.channels.teamPush') }]
  const showSendTo = deliver.includes('WhatsApp') || deliver.includes('Email')
  const channelsSet = !(deliver.length === 1 && deliver[0] === 'WhatsApp')
  const count = (channelsSet ? 1 : 0) + (showSendTo && allLinked ? 1 : 0) + (bool(cfg.humanize) ? 1 : 0) + extraSet
  const names = deliver.map((c) => opts.find((o) => o.value === c)?.label ?? c).join(', ')
  return (
    <Advanced
      count={count}
      forceOpen={count > 0}
      hint={
        names
          ? t('flows.config.deliveryHint', { channels: names })
          : ai
            ? t('flows.config.deliveryHint', { channels: t('flows.config.latestChannel') })
            : undefined
      }
    >
      <Field label={t('flows.config.sendOn')} help={ai ? t('flows.config.sendOnHelpAi') : t('flows.config.sendOnHelp')}>
        <MultiSelect
          value={deliver}
          onChange={(v) => onPatch({ channels: ai ? [AI_MESSAGE, ...v] : v })}
          options={opts}
          size="md"
          placeholder={ai ? t('flows.config.latestChannel') : t('flows.config.chooseChannel')}
          ariaLabel={t('flows.config.channels')}
          maxChips={3}
        />
      </Field>
      {showSendTo && (
        <Field label={t('flows.config.sendTo')} help={allLinked ? t('flows.config.sendToAllHelp') : t('flows.config.sendToPrimaryHelp')}>
          <Segmented
            ariaLabel={t('flows.config.sendTo')}
            value={allLinked ? 'all' : 'primary'}
            onChange={(v) => onPatch({ allLinked: v === 'all' ? true : undefined, waAllNumbers: undefined })}
            options={[
              { value: 'primary', label: t('flows.config.sendToPrimary') },
              { value: 'all', label: t('flows.config.sendToAll') },
            ]}
          />
        </Field>
      )}
      {children}
      <HumanizeRow cfg={cfg} set={set} />
    </Advanced>
  )
}

function HumanizeRow({ cfg, set }: { cfg: FlowConfig; set: SetFn }) {
  const t = useT()
  return (
    <SwitchRow
      label={t('flows.config.humanize')}
      help={t('flows.config.humanizeHelp')}
      on={bool(cfg.humanize)}
      onChange={(v) => set('humanize', v || undefined)}
    />
  )
}

function ActionForm({ cfg, onPatch, set }: { cfg: FlowConfig; onPatch: (p: FlowConfig) => void; set: SetFn }) {
  const t = useT()
  const { lookups } = useFlowView()
  const channels = channelsOf(cfg)
  const type = actionType(cfg)
  const mixed =
    type === 'send' && channels.some((c) => (CALL_CHANNELS as readonly string[]).includes(c) || (INTEGRATION_ACTIONS as readonly string[]).includes(c))

  const switchType = (next: ActionType) => {
    if (next === type) return
    if (next === 'send') onPatch({ channels: ['WhatsApp'] })
    if (next === 'ai') onPatch({ channels: [AI_MESSAGE, 'WhatsApp'] })
    if (next === 'call') onPatch({ channels: ['AI voice call'], callVia: str(cfg.callVia) || 'Phone (SIP)' })
    if (next === 'app') onPatch({ channels: ['Create calendar event'] })
  }

  return (
    <>
      <Field label={t('flows.config.thisStep')}>
        <Segmented
          ariaLabel={t('flows.config.stepType')}
          value={type}
          onChange={switchType}
          options={[
            { value: 'send', label: t('flows.config.typeMessage') },
            { value: 'ai', label: t('flows.stepChoice.ai') },
            { value: 'call', label: t('flows.stepChoice.call') },
            { value: 'app', label: t('flows.config.typeApp') },
          ]}
        />
      </Field>

      {mixed && (
        <>
          <Notice tone="amber">{t('flows.config.mixedNotice')}</Notice>
          <Field label={t('flows.config.channels')}>
            <MultiSelect
              value={channels}
              onChange={(v) => onPatch({ channels: v })}
              options={[
                ...choiceOptions(t, SEND_CHANNELS),
                { value: TEAM_PUSH, label: t('flows.channels.teamPush') },
                ...CALL_CHANNELS.map((c) => ({ value: c, label: actionChannelLabel(t, c) })),
                ...INTEGRATION_ACTIONS.map((c) => ({ value: c, label: t(INTEGRATION_ACTION_LABELS[c]), group: t('flows.config.appActionsGroup') })),
              ]}
              size="md"
              ariaLabel={t('flows.config.channels')}
            />
          </Field>
        </>
      )}

      {type === 'send' && !mixed && (
        <>
          <DeliveryFields cfg={cfg} onPatch={onPatch} channels={channels} ai={false} />
          <VariableTextarea
            label={t('flows.config.message')}
            value={str(cfg.script)}
            onChange={(v) => set('script', v)}
            placeholder={t('flows.config.messagePlaceholder')}
            help={cfg.waMode === 'template' ? t('flows.config.messageTemplateHelp') : undefined}
          />
          <DeliveryAdvanced cfg={cfg} onPatch={onPatch} set={set} channels={channels} ai={false} />
        </>
      )}

      {type === 'send' && mixed && (
        <VariableTextarea label={t('flows.config.messageOrInstruction')} value={str(cfg.script)} onChange={(v) => set('script', v)} />
      )}

      {type === 'ai' && (
        <>
          <Field label={t('flows.config.skill')} help={t('flows.config.skillHelp')}>
            <SkillPicker value={str(cfg.skill)} skills={lookups.skills} loaded={lookups.skillsLoaded} onChange={(v) => set('skill', v)} />
          </Field>
          <VariableTextarea
            label={t('flows.config.aiBriefLabel')}
            value={str(cfg.script)}
            onChange={(v) => set('script', v)}
            placeholder={t('flows.config.aiBriefPlaceholder')}
            help={t('flows.config.aiBriefHelp')}
          />
          <DeliveryFields cfg={cfg} onPatch={onPatch} channels={channels} ai />
          <DeliveryAdvanced cfg={cfg} onPatch={onPatch} set={set} channels={channels} ai />
        </>
      )}

      {type === 'call' && (
        <>
          <Field label={t('flows.config.callType')}>
            <Segmented
              ariaLabel={t('flows.config.callType')}
              value={channels.includes('Script call') ? 'Script call' : 'AI voice call'}
              onChange={(v) => onPatch({ channels: [v] })}
              options={[
                { value: 'AI voice call', label: t('flows.call.aiVoiceCall'), hint: t('flows.config.aiCallHint') },
                { value: 'Script call', label: t('flows.call.scriptCall'), hint: t('flows.config.scriptCallHint') },
              ]}
            />
          </Field>
          <Field label={t('flows.config.callOver')}>
            <Segmented
              ariaLabel={t('flows.config.callOver')}
              value={str(cfg.callVia).startsWith('WhatsApp') ? 'WhatsApp (Baileys)' : 'Phone (SIP)'}
              onChange={(v) => set('callVia', v)}
              options={[
                { value: 'Phone (SIP)', label: t('flows.config.phoneLine') },
                { value: 'WhatsApp (Baileys)', label: 'WhatsApp' },
              ]}
            />
          </Field>
          {channels.includes('Script call') ? (
            <Field label={t('flows.config.script')} help={t('flows.config.scriptHelp')}>
              <Select
                value={str(cfg.scriptTemplateId)}
                onChange={(v) => set('scriptTemplateId', v)}
                options={[
                  ...lookups.scripts.map((s) => ({ value: String(s.id), label: s.name })),
                  ...(str(cfg.scriptTemplateId) && !lookups.scripts.some((s) => String(s.id) === str(cfg.scriptTemplateId))
                    ? [{ value: str(cfg.scriptTemplateId), label: t('flows.config.missingScript', { id: str(cfg.scriptTemplateId) }) }]
                    : []),
                ]}
                size="md"
                placeholder={t('flows.config.chooseScript')}
                ariaLabel={t('flows.config.script')}
              />
            </Field>
          ) : (
            <>
              <Field label={t('flows.config.skill')}>
                <SkillPicker value={str(cfg.skill)} skills={lookups.skills} loaded={lookups.skillsLoaded} onChange={(v) => set('skill', v)} />
              </Field>
              <VariableTextarea
                label={t('flows.config.callBriefLabel')}
                value={str(cfg.script)}
                onChange={(v) => set('script', v)}
                placeholder={t('flows.config.callBriefPlaceholder')}
              />
            </>
          )}
          {(channels.includes('Script call') || str(cfg.callVia).startsWith('WhatsApp')) && (
            <Field label={t('flows.config.voice')}>
              <Select
                value={str(cfg.voice)}
                onChange={(v) => set('voice', v)}
                options={[
                  { value: '', label: t('flows.config.defaultVoice') },
                  ...lookups.voices.map((v) => ({ value: v.id, label: v.name, hint: 'ElevenLabs' })),
                  ...(str(cfg.voice) && !lookups.voices.some((v) => v.id === str(cfg.voice))
                    ? [{ value: str(cfg.voice), label: t('flows.config.unavailableVoice', { voice: str(cfg.voice) }) }]
                    : []),
                ]}
                size="md"
                ariaLabel={t('flows.config.voice')}
              />
            </Field>
          )}
        </>
      )}

      {type === 'app' && (
        <>
          <Field label={t('flows.config.action')} help={t('flows.config.actionHelp')}>
            <Select
              value={channels[0] ?? ''}
              onChange={(v) => onPatch({ channels: [v] })}
              options={INTEGRATION_ACTIONS.map((a) => ({ value: a, label: t(INTEGRATION_ACTION_LABELS[a]) }))}
              size="md"
              ariaLabel={t('flows.stepChoice.app')}
            />
          </Field>
          <AppFields action={channels[0] ?? ''} cfg={cfg} set={set} />
          <VariableTextarea
            label={channels[0] === 'Send via Gmail' ? t('flows.config.emailBody') : t('flows.config.textOrDescription')}
            value={str(cfg.script)}
            onChange={(v) => set('script', v)}
          />
        </>
      )}
    </>
  )
}

function AppFields({ action, cfg, set }: { action: string; cfg: FlowConfig; set: SetFn }) {
  const t = useT()
  const text = (key: string, label: string, help?: string, placeholder?: string) => (
    <Field key={key} label={label} help={help}>
      <TextInput value={str(cfg[key])} onChange={(v) => set(key, v)} placeholder={placeholder} ariaLabel={label} />
    </Field>
  )
  switch (action) {
    case 'Create calendar event':
    case 'Find free slot':
      return (
        <>
          {action === 'Create calendar event' && text('eventTitle', t('flows.config.eventTitle'), t('flows.config.eventTitleHelp'))}
          {text('eventStart', t('flows.config.eventStart'), t('flows.config.eventStartHelp'), '2026-08-01T10:00')}
          <Field label={t('flows.config.lengthMinutes')}>
            <NumberInput value={cfg.eventMinutes as number} onChange={(v) => set('eventMinutes', v)} min={5} ariaLabel={t('flows.config.lengthMinutes')} />
          </Field>
          {text('calendarId', t('flows.config.calendarId'), t('flows.config.calendarIdHelp'))}
        </>
      )
    case 'Upload file to Drive':
      return text('fileName', t('flows.config.fileName'), t('flows.config.fileNameHelp'))
    case 'Append row to Sheet':
      return (
        <>
          {text('spreadsheetId', t('flows.config.spreadsheetId'), t('flows.config.spreadsheetIdHelp'))}
          {text('sheetRange', t('flows.config.range'), undefined, 'Sheet1!A:Z')}
        </>
      )
    case 'Send via Gmail':
      return text('subject', t('flows.config.subject'))
    case 'Update order status':
      return text('orderStatus', t('flows.config.newStatus'), t('flows.config.newStatusHelp'), t('flows.config.newStatusPlaceholder'))
    case 'Create Stripe payment link':
      return text('successUrl', t('flows.config.successUrl'), undefined, 'https://…')
    default:
      return null
  }
}

// ---- WAIT / FOLLOW UP ---------------------------------------------------------------------

function DurationRow({
  amount,
  unit,
  onAmount,
  onUnit,
  label,
}: {
  amount: unknown
  unit: unknown
  onAmount: (v: number | '') => void
  onUnit: (v: string) => void
  label: string
}) {
  const t = useT()
  return (
    <div className="flex items-center gap-2">
      <NumberInput value={amount as number} onChange={onAmount} min={0} step={1} ariaLabel={t('flows.config.amountOf', { label })} className="!w-24" />
      <div className="min-w-0 flex-1">
        <Select value={str(unit) || 'days'} onChange={onUnit} options={choiceOptions(t, DURATION_UNITS)} size="md" ariaLabel={t('flows.config.unitOf', { label })} />
      </div>
    </div>
  )
}

function WaitForm({ cfg, set }: { cfg: FlowConfig; set: SetFn }) {
  const t = useT()
  const mode = cfg.mode === 'reply' ? 'reply' : 'time'
  // A new step gives up after 2 days (nodeConfig STEP_CHOICES); anything else is a customised value.
  const giveUp = durationText(t, cfg.timeoutAmount, cfg.timeoutUnit)
  const giveUpSet = !(Number(cfg.timeoutAmount) === 2 && (str(cfg.timeoutUnit) || 'days') === 'days')
  return (
    <>
      <Field label={t('flows.config.waitFor')}>
        <Segmented
          ariaLabel={t('flows.config.waitFor')}
          value={mode}
          onChange={(v) => set('mode', v)}
          options={[
            { value: 'time', label: t('flows.config.aSetTime') },
            { value: 'reply', label: t('flows.config.aReply') },
          ]}
        />
      </Field>
      {mode === 'time' ? (
        <Field label={t('flows.config.howLong')}>
          <DurationRow amount={cfg.amount} unit={cfg.unit} onAmount={(v) => set('amount', v)} onUnit={(v) => set('unit', v)} label={t('flows.stepChoice.wait')} />
        </Field>
      ) : (
        <>
          <Advanced
            count={giveUpSet ? 1 : 0}
            forceOpen={giveUpSet}
            hint={giveUp ? t('flows.config.giveUpHint', { duration: giveUp }) : t('flows.config.giveUpNeverHint')}
          >
            <Field label={t('flows.config.giveUpAfter')} help={t('flows.config.giveUpAfterHelp')}>
              <DurationRow
                amount={cfg.timeoutAmount}
                unit={cfg.timeoutUnit}
                onAmount={(v) => set('timeoutAmount', v)}
                onUnit={(v) => set('timeoutUnit', v)}
                label={t('flows.config.giveUpAfter')}
              />
            </Field>
          </Advanced>
          <Notice>
            <Trans
              k="flows.config.waitMovesOn"
              values={{
                step: (
                  <strong>
                    {t('flows.stepChoice.branch')} → {t('flows.decision.replied')}
                  </strong>
                ),
              }}
            />
          </Notice>
        </>
      )}
    </>
  )
}

function FollowUpForm({ cfg, set, onPatch }: { cfg: FlowConfig; set: SetFn; onPatch: (p: FlowConfig) => void }) {
  const t = useT()
  const { lookups } = useFlowView()
  const channels = channelsOf(cfg)
  const ai = channels.includes(AI_MESSAGE)
  const max = Number(cfg.maxFollowUps) || 2
  return (
    <>
      <Field label={t('flows.config.waitBeforeEach')}>
        <DurationRow
          amount={cfg.waitAmount}
          unit={cfg.waitUnit}
          onAmount={(v) => set('waitAmount', v)}
          onUnit={(v) => set('waitUnit', v)}
          label={t('flows.config.waitBeforeEach')}
        />
      </Field>
      <Field label={t('flows.config.followUpAtMost')}>
        <Select
          value={String(max)}
          onChange={(v) => set('maxFollowUps', Number(v))}
          options={[1, 2, 3, 4, 5].map((count) => ({ value: String(count), label: t('flows.config.times', { count }) }))}
          size="md"
          ariaLabel={t('flows.config.maxFollowUps')}
        />
      </Field>
      <Field label={t('flows.config.eachFollowUpIs')}>
        <Segmented
          ariaLabel={t('flows.config.followUpContent')}
          value={ai ? 'ai' : 'text'}
          onChange={(v) =>
            onPatch({ channels: v === 'ai' ? [AI_MESSAGE, ...channels.filter((c) => c !== AI_MESSAGE)] : channels.filter((c) => c !== AI_MESSAGE) })
          }
          options={[
            { value: 'text', label: t('flows.config.myMessage') },
            { value: 'ai', label: t('flows.config.aiWritesIt') },
          ]}
        />
      </Field>
      {ai && (
        <Field label={t('flows.config.skill')}>
          <SkillPicker value={str(cfg.skill)} skills={lookups.skills} loaded={lookups.skillsLoaded} onChange={(v) => set('skill', v)} />
        </Field>
      )}
      <VariableTextarea
        label={ai ? t('flows.config.followUpBriefLabel') : t('flows.config.followUpMessage')}
        value={str(cfg.script)}
        onChange={(v) => set('script', v)}
        placeholder={ai ? t('flows.config.followUpBriefPlaceholder') : t('flows.config.followUpMessagePlaceholder')}
        help={ai ? t('flows.config.followUpBriefHelp') : t('flows.config.followUpMessageHelp')}
      />
      <DeliveryFields cfg={cfg} onPatch={onPatch} channels={channels} ai={ai} />
      <div className="flex flex-col gap-3 rounded-lg border border-line bg-well p-3">
        <div className="text-[12px] font-semibold text-text-mid">{t('flows.config.stopWhen')}</div>
        <SwitchRow label={t('flows.config.theyReply')} help={t('flows.config.theyReplyHelp')} on onChange={() => {}} locked />
        <SwitchRow label={t('flows.config.resolved')} on={bool(cfg.stopOnResolved)} onChange={(v) => set('stopOnResolved', v)} />
      </div>
      <DeliveryAdvanced cfg={cfg} onPatch={onPatch} set={set} channels={channels} ai={ai} extraSet={str(cfg.stopTag).trim() ? 1 : 0}>
        <Field label={t('flows.config.stopTag')}>
          <TextInput value={str(cfg.stopTag)} onChange={(v) => set('stopTag', v)} placeholder={t('flows.config.stopTagPlaceholder')} ariaLabel={t('flows.config.stopTag')} />
        </Field>
      </DeliveryAdvanced>
      <Notice>{t('flows.config.quietHoursNotice')}</Notice>
    </>
  )
}

// ---- IF / HAND OVER / UPDATE / END -------------------------------------------------------

function HandoverForm({ cfg, set }: { cfg: FlowConfig; set: SetFn }) {
  const t = useT()
  const { lookups } = useFlowView()
  const team = str(cfg.team)
  const priority = str(cfg.priority) || 'Normal'
  const prioritySet = priority !== 'Normal'
  const options: SelectOption[] = [
    { value: '', label: t('flows.config.adminsNoTeam') },
    ...lookups.teams.map((tm) => ({ value: String(tm.id), label: tm.name })),
  ]
  if (team && !options.some((o) => o.value === team)) options.push({ value: team, label: t('flows.config.notATeam', { team }) })
  return (
    <>
      <Field label={t('flows.config.handOverTo')} help={t('flows.config.handOverToHelp')}>
        <Select value={team} onChange={(v) => set('team', v)} options={options} size="md" ariaLabel={t('flows.config.team')} />
      </Field>
      <VariableTextarea label={t('flows.config.whyNote')} rows={3} value={str(cfg.reason)} onChange={(v) => set('reason', v)} />
      <Advanced count={prioritySet ? 1 : 0} forceOpen={prioritySet}>
        <Field label={t('flows.config.priority')}>
          <Segmented
            ariaLabel={t('flows.config.priority')}
            value={priority as 'Normal' | 'High' | 'Urgent'}
            onChange={(v) => set('priority', v)}
            options={[
              { value: 'Normal', label: t('flows.priority.normal') },
              { value: 'High', label: t('flows.priority.high') },
              { value: 'Urgent', label: t('flows.priority.urgent') },
            ]}
          />
        </Field>
      </Advanced>
      <Notice>{t('flows.config.handoverNotice')}</Notice>
    </>
  )
}

function UpdateForm({ cfg, set }: { cfg: FlowConfig; set: SetFn }) {
  const t = useT()
  if (isChatbotStep(cfg)) return <ChatbotForm cfg={cfg} set={set} />
  const op = str(cfg.op) || 'add_tag'
  return (
    <>
      <Field label={t('flows.config.change')}>
        <Segmented
          ariaLabel={t('flows.config.change')}
          value={op as 'add_tag' | 'remove_tag' | 'set_field'}
          onChange={(v) => set('op', v)}
          options={[
            { value: 'add_tag', label: t('flows.config.addTag') },
            { value: 'remove_tag', label: t('flows.config.removeTag') },
            { value: 'set_field', label: t('flows.config.setField') },
          ]}
        />
      </Field>
      {op === 'set_field' ? (
        <>
          <Field label={t('flows.config.field')}>
            <FieldPicker value={str(cfg.field)} onChange={(v) => set('field', v)} />
          </Field>
          <Field label={t('flows.config.value')} help={t('flows.config.valueHelp')}>
            <input value={str(cfg.value)} onChange={(e) => set('value', e.target.value)} className={inputCls} aria-label={t('flows.config.value')} />
          </Field>
        </>
      ) : (
        <Field label={t('flows.config.tag')}>
          <TextInput value={str(cfg.tag)} onChange={(v) => set('tag', v)} placeholder={t('flows.config.qualifiedPlaceholder')} ariaLabel={t('flows.config.tag')} />
        </Field>
      )}
    </>
  )
}

/** "Set chatbot on/off" — the per-chat AI switch on this contact's conversations (FlowEngine.SetChatbot). */
function ChatbotForm({ cfg, set }: { cfg: FlowConfig; set: SetFn }) {
  const t = useT()
  const value = str(cfg.value) === 'on' ? 'on' : 'off'
  const scope = str(cfg.scope) === 'channels' ? 'channels' : 'all'
  const channels = Array.isArray(cfg.channels) ? (cfg.channels as unknown[]).map(String) : []
  return (
    <>
      <Field label={t('flows.describe.eyebrowChatbot')}>
        <Segmented
          ariaLabel={t('flows.describe.eyebrowChatbot')}
          value={value}
          onChange={(v) => set('value', v)}
          options={[
            { value: 'off', label: t('common.status.off'), hint: t('flows.config.chatbotOffHint') },
            { value: 'on', label: t('common.status.on'), hint: t('flows.config.chatbotOnHint') },
          ]}
        />
      </Field>
      <Field label={t('flows.config.whichChats')}>
        <Segmented
          ariaLabel={t('flows.config.whichChats')}
          value={scope}
          onChange={(v) => set('scope', v)}
          options={[
            { value: 'all', label: t('flows.config.allTheirChats') },
            { value: 'channels', label: t('flows.config.someChannels') },
          ]}
        />
      </Field>
      {scope === 'channels' && (
        <Field label={t('flows.config.channels')}>
          <MultiSelect
            value={channels}
            onChange={(v) => set('channels', v)}
            options={choiceOptions(t, CHATBOT_CHANNELS)}
            placeholder={t('flows.config.pickChannels')}
            size="md"
            ariaLabel={t('flows.config.channels')}
          />
        </Field>
      )}
      <Notice>{t('flows.config.chatbotNotice')}</Notice>
    </>
  )
}

function EndForm({ cfg, set }: { cfg: FlowConfig; set: SetFn }) {
  const t = useT()
  return (
    <>
      <Field label={t('flows.config.outcome')} help={t('flows.config.outcomeHelp')}>
        <TextInput value={str(cfg.logCode)} onChange={(v) => set('logCode', v.toUpperCase().replace(/\s+/g, '_'))} placeholder="DONE" ariaLabel={t('flows.config.outcome')} />
      </Field>
      <SwitchRow
        label={t('flows.config.rememberOutcome')}
        help={t('flows.config.rememberOutcomeHelp')}
        on={bool(cfg.updateMemory)}
        onChange={(v) => set('updateMemory', v ? 'Yes' : 'No')}
      />
    </>
  )
}
