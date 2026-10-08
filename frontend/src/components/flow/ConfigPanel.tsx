'use client'

import { useState } from 'react'
import { Play, Trash2, X } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { MultiSelect, Select, type SelectOption } from '@/components/ui/Select'
import {
  AI_MESSAGE,
  CALL_CHANNELS,
  DECISIONS,
  DURATION_UNITS,
  INTEGRATION_ACTIONS,
  LEGACY_DECISIONS,
  LEGACY_REPEATS,
  LEGACY_TRIGGERS,
  MESSAGE_CHANNELS,
  REPEAT_OPTIONS,
  SEND_CHANNELS,
  TEAM_PUSH,
  TRIGGERS,
  WEEKDAYS,
  actionType,
  channelsOf,
  type ActionType,
} from '@/lib/flow/nodeConfig'
import { describeStep } from '@/lib/flow/describe'
import { startFlow } from '@/lib/flow/api'
import type { FlowConfig, FlowNode } from '@/lib/flow/types'
import ContactScopePicker from './ContactScopePicker'
import SkillPicker from './SkillPicker'
import VariableTextarea from './VariableTextarea'
import WaTemplateConfig from './WaTemplateConfig'
import { Field, Notice, NumberInput, Segmented, SwitchRow, TextInput, inputCls } from './fields'
import { useFlowView } from './FlowViewContext'
import { StepIcon } from './stepIcons'

/**
 * The step editor — one plain-language form per step type, used by BOTH views
 * (Steps list and Advanced canvas). Every control writes straight into the
 * node's `data.config`, which is exactly what the engine executes.
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
  const { describeCtx, issues } = useFlowView()
  const cfg = node.data.config ?? {}
  const text = describeStep(node, describeCtx)
  const set = (key: string, value: FlowConfig[string]) => onPatch({ [key]: value })
  const myIssues = issues[node.id] ?? []

  return (
    <aside className="flex h-full w-full flex-col bg-card" aria-label="Step settings">
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
          aria-label="Close step settings"
          className="grid h-8 w-8 shrink-0 place-items-center rounded-lg text-text-muted hover:bg-fill-hover hover:text-text-high"
        >
          <X size={16} aria-hidden />
        </button>
      </header>

      <div className="flex min-h-0 flex-1 flex-col gap-4 overflow-y-auto px-4 py-4">
        {myIssues.length > 0 && (
          <Notice tone="amber">
            {myIssues.map((m) => (
              <div key={m}>{m}</div>
            ))}
          </Notice>
        )}

        {node.type === 'trigger' && <TriggerForm cfg={cfg} set={set} onPatch={onPatch} flowId={flowId} />}
        {node.type === 'action' && <ActionForm cfg={cfg} onPatch={onPatch} set={set} />}
        {node.type === 'wait' && <WaitForm cfg={cfg} set={set} />}
        {node.type === 'followup' && <FollowUpForm cfg={cfg} set={set} onPatch={onPatch} />}
        {node.type === 'decision' && <DecisionForm cfg={cfg} set={set} />}
        {node.type === 'handover' && <HandoverForm cfg={cfg} set={set} />}
        {node.type === 'update' && <UpdateForm cfg={cfg} set={set} />}
        {node.type === 'end' && <EndForm cfg={cfg} set={set} />}
        {node.type === 'stage' && (
          <Notice>
            A legacy section from an older flow. It no longer does anything — runs pass straight through it. You can
            delete it; the steps inside stay.
          </Notice>
        )}

        <div className="border-t border-line pt-4">
          <Field label="Name (optional)" help="Your own label for this step, shown under its summary.">
            <TextInput value={str(node.data.label)} onChange={onLabel} placeholder="e.g. Welcome message" ariaLabel="Step name" />
          </Field>
        </div>
      </div>

      {onDelete && (
        <footer className="flex items-center justify-between gap-2 border-t border-line bg-surface px-4 py-2.5">
          <span className="text-[11.5px] text-text-muted">Changes save automatically.</span>
          <Button variant="danger" size="sm" onClick={onDelete}>
            <Trash2 size={13} aria-hidden /> Delete step
          </Button>
        </footer>
      )}
    </aside>
  )
}

type SetFn = (key: string, value: FlowConfig[string]) => void

// ---- WHEN ---------------------------------------------------------------------------

function TriggerForm({ cfg, set, onPatch, flowId }: { cfg: FlowConfig; set: SetFn; onPatch: (p: FlowConfig) => void; flowId: string }) {
  const { lookups } = useFlowView()
  const when = str(cfg.when) || 'Days after due date'
  const legacy = !TRIGGERS.some((t) => t.value === when)
  const options: SelectOption[] = [
    ...TRIGGERS.map((t) => ({ value: t.value, label: t.label })),
    ...(legacy ? [{ value: when, label: `${LEGACY_TRIGGERS[when] ?? when} (legacy)`, group: 'No longer offered' }] : []),
  ]
  const contacts = Array.isArray(cfg.contacts) ? cfg.contacts.map(String) : []
  const contactLabels =
    cfg.contactLabels && typeof cfg.contactLabels === 'object' && !Array.isArray(cfg.contactLabels)
      ? (cfg.contactLabels as Record<string, string>)
      : {}
  const audience = (str(cfg.audience) || (contacts.length ? 'some' : str(cfg.onlyTag) ? 'tag' : 'all')) as 'all' | 'tag' | 'some'
  const repeat = str(cfg.repeat) || (when === 'Message received' ? 'Every time' : 'Once')
  const repeatOptions: SelectOption[] = [
    ...REPEAT_OPTIONS,
    ...(LEGACY_REPEATS[repeat] ? [{ value: repeat, label: LEGACY_REPEATS[repeat] }] : []),
  ]

  return (
    <>
      <Field label="Start this flow when">
        <Select value={when} onChange={(v) => set('when', v)} options={options} size="md" ariaLabel="Trigger" />
      </Field>
      {legacy && (
        <Notice tone="amber">
          This starting point is from an older version. It still runs, but it is no longer offered — pick one of the new
          ones to modernise the flow.
        </Notice>
      )}

      {(when === 'Tag added' || when === 'Contact has tag') && (
        <Field label="Tag" help="Each tagged contact enters once, as soon as the tag is on them.">
          <TextInput value={str(cfg.tag)} onChange={(v) => set('tag', v)} placeholder="e.g. New customer" ariaLabel="Tag" />
        </Field>
      )}

      {when === 'Message received' && (
        <>
          <Field label="On" help="Which channel the message has to arrive on.">
            <Select
              value={str(cfg.channel)}
              onChange={(v) => set('channel', v)}
              options={MESSAGE_CHANNELS}
              size="md"
              ariaLabel="Channel"
            />
          </Field>
          <Field label="Only when it contains (optional)" help="Separate words with commas — any of them matches. Leave empty for every message.">
            <TextInput value={str(cfg.keyword)} onChange={(v) => set('keyword', v)} placeholder="price, quote, booking" ariaLabel="Keywords" />
          </Field>
        </>
      )}

      {when === 'Date field' && (
        <>
          <Field label="Date field" help="A customer custom field holding a date (YYYY-MM-DD).">
            <FieldPicker value={str(cfg.field)} onChange={(v) => set('field', v)} />
          </Field>
          <Field label="When">
            <div className="flex flex-wrap items-center gap-2">
              {cfg.dateWhen !== 'on' && cfg.dateWhen && (
                <NumberInput value={cfg.days as number} onChange={(v) => set('days', v)} min={1} ariaLabel="Days" className="!w-20" />
              )}
              <div className="min-w-[180px] flex-1">
                <Select
                  value={str(cfg.dateWhen) || 'on'}
                  onChange={(v) => onPatch({ dateWhen: v, days: v === 'on' ? 0 : Number(cfg.days) || 1 })}
                  options={[
                    { value: 'before', label: 'days before the date' },
                    { value: 'on', label: 'on the date' },
                    { value: 'after', label: 'days after the date' },
                  ]}
                  size="md"
                  ariaLabel="Before, on or after"
                />
              </div>
            </div>
          </Field>
          <SwitchRow
            label="Every year"
            help="Match the day and month only — for birthdays, anniversaries and renewals."
            on={bool(cfg.yearly)}
            onChange={(v) => set('yearly', v)}
          />
        </>
      )}

      {when === 'Scheduled' && (
        <>
          <Field label="Time" help="Organization time. Contacts enter once per day at this time.">
            <TextInput type="time" value={str(cfg.time) || '09:00'} onChange={(v) => set('time', v)} ariaLabel="Time" />
          </Field>
          <Field label="On these days" help="Leave empty for every day.">
            <MultiSelect
              value={Array.isArray(cfg.weekdays) ? cfg.weekdays.map(String) : []}
              onChange={(v) => set('weekdays', v)}
              options={WEEKDAYS}
              size="md"
              placeholder="Every day"
              maxChips={7}
              ariaLabel="Weekdays"
            />
          </Field>
          {audience === 'all' && (
            <Notice tone="amber">A schedule with no audience filter runs for every contact, every day. Narrow it below.</Notice>
          )}
        </>
      )}

      {when === 'Manual / API' && <ManualStart flowId={flowId} />}

      {(when === 'Record status is' || when.includes('due date')) && (
        <Field label={when === 'Record status is' ? 'Record status' : 'Days'}>
          {when === 'Record status is' ? (
            <TextInput value={str(cfg.status)} onChange={(v) => set('status', v)} ariaLabel="Record status" />
          ) : (
            <NumberInput value={cfg.days as number} onChange={(v) => set('days', v)} ariaLabel="Days" />
          )}
        </Field>
      )}

      {when !== 'Manual / API' && (
        <div className="flex flex-col gap-3 rounded-lg border border-line bg-well p-3">
          <Field label="Who can enter">
            <Segmented
              ariaLabel="Audience"
              value={audience}
              onChange={(v) => {
                if (v === 'all') onPatch({ audience: 'all', contacts: [], contactLabels: undefined, onlyTag: '' })
                if (v === 'tag') onPatch({ audience: 'tag', contacts: [], contactLabels: undefined })
                if (v === 'some') onPatch({ audience: 'some', onlyTag: '' })
              }}
              options={[
                { value: 'all', label: 'Everyone' },
                { value: 'tag', label: 'Tagged' },
                { value: 'some', label: 'Chosen' },
              ]}
            />
          </Field>
          {audience === 'tag' && (
            <TextInput value={str(cfg.onlyTag)} onChange={(v) => set('onlyTag', v)} placeholder="Only contacts tagged…" ariaLabel="Only contacts tagged" />
          )}
          {audience === 'some' && (
            <>
              <ContactScopePicker
                selected={contacts}
                labels={contactLabels}
                onChange={(ids, labels) => onPatch({ contacts: ids, contactLabels: labels })}
              />
              {contacts.length === 0 && (
                <div className="text-[11.5px] text-[color:var(--amber-ink)]">Pick at least one — an empty list means everyone.</div>
              )}
            </>
          )}
        </div>
      )}

      {when !== 'Scheduled' && when !== 'Manual / API' && when !== 'Contact created' && (
        <Field label="If it happens again">
          <Select value={repeat} onChange={(v) => set('repeat', v)} options={repeatOptions} size="md" ariaLabel="Repeat" />
        </Field>
      )}
      {lookups.fields.length === 0 && when === 'Date field' && (
        <div className="text-[11.5px] text-text-muted">No customer date fields yet — add one under Custom Fields.</div>
      )}
    </>
  )
}

function ManualStart({ flowId }: { flowId: string }) {
  const [ids, setIds] = useState<string[]>([])
  const [labels, setLabels] = useState<Record<string, string>>({})
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)
  const [busy, setBusy] = useState(false)
  return (
    <div className="flex flex-col gap-2 rounded-lg border border-line bg-well p-3">
      <div className="text-[12px] font-semibold text-text-mid">Start it now for…</div>
      <ContactScopePicker
        selected={ids}
        labels={labels}
        onChange={(i, l) => {
          setIds(i)
          setLabels(l)
        }}
      />
      <div className="flex items-center justify-between gap-2">
        <span className="text-[11.5px] text-text-muted">
          The flow must be saved and live. Integrations use POST /api/flows/{flowId}/start.
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
              setMsg({ ok: true, text: `Started for ${r.started}${r.skipped ? ` · ${r.skipped} already in it` : ''}.` })
              setIds([])
              setLabels({})
            }
          }}
        >
          <Play size={12} aria-hidden /> Start
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
  const { lookups } = useFlowView()
  if (!lookups.fields.length) return <TextInput value={value} onChange={onChange} placeholder="field key, e.g. renewal_date" ariaLabel="Field" />
  const options: SelectOption[] = lookups.fields.map((f) => ({ value: f.key, label: f.label || f.key, hint: f.key }))
  if (value && !options.some((o) => o.value === value)) options.push({ value, label: `${value} (not a field)` })
  return <Select value={value} onChange={onChange} options={options} size="md" searchable ariaLabel="Field" placeholder="Choose a field" />
}

// ---- SEND / AI / CALL / APP -----------------------------------------------------------------

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
  const deliver = channels.filter((c) => c !== AI_MESSAGE)
  const allLinked = bool(cfg.allLinked ?? cfg.waAllNumbers)
  const opts = ai ? SEND_CHANNELS.filter((c) => c.value !== 'SMS') : [...SEND_CHANNELS, { value: TEAM_PUSH, label: 'Team notification (in-app)' }]
  return (
    <>
      <Field
        label="Send on"
        help={ai ? 'Leave empty to reply on their latest conversation’s channel.' : 'Pick every channel this message should go out on.'}
      >
        <MultiSelect
          value={deliver}
          onChange={(v) => onPatch({ channels: ai ? [AI_MESSAGE, ...v] : v })}
          options={opts}
          size="md"
          placeholder={ai ? 'Their latest channel' : 'Choose a channel'}
          ariaLabel="Channels"
          maxChips={3}
        />
      </Field>
      {(deliver.includes('WhatsApp') || deliver.includes('Email')) && (
        <Field
          label="Send to"
          help={allLinked ? 'Every WhatsApp number and email address linked to the contact (duplicates removed).' : 'The phone number / email on the contact.'}
        >
          <Segmented
            ariaLabel="Send to"
            value={allLinked ? 'all' : 'primary'}
            onChange={(v) => onPatch({ allLinked: v === 'all' ? true : undefined, waAllNumbers: undefined })}
            options={[
              { value: 'primary', label: 'Their main contact' },
              { value: 'all', label: 'Everything linked' },
            ]}
          />
        </Field>
      )}
      {deliver.includes('WhatsApp') && (
        <WaTemplateConfig
          config={cfg}
          onPatch={onPatch}
          modes={ai ? ['text', 'auto'] : ['text', 'template', 'auto']}
          textLabel={ai ? 'AI text' : undefined}
        />
      )}
      {deliver.includes('Email') && (
        <Field label="Email subject">
          <TextInput value={str(cfg.subject)} onChange={(v) => onPatch({ subject: v })} placeholder="e.g. A quick update from {{business_name}}" ariaLabel="Email subject" />
        </Field>
      )}
    </>
  )
}

function ActionForm({ cfg, onPatch, set }: { cfg: FlowConfig; onPatch: (p: FlowConfig) => void; set: SetFn }) {
  const { lookups } = useFlowView()
  const channels = channelsOf(cfg)
  const type = actionType(cfg)
  const mixed =
    type === 'send' && channels.some((c) => (CALL_CHANNELS as readonly string[]).includes(c) || (INTEGRATION_ACTIONS as readonly string[]).includes(c))

  const switchType = (t: ActionType) => {
    if (t === type) return
    if (t === 'send') onPatch({ channels: ['WhatsApp'] })
    if (t === 'ai') onPatch({ channels: [AI_MESSAGE, 'WhatsApp'] })
    if (t === 'call') onPatch({ channels: ['AI voice call'], callVia: str(cfg.callVia) || 'Phone (SIP)' })
    if (t === 'app') onPatch({ channels: ['Create calendar event'] })
  }

  return (
    <>
      <Field label="This step">
        <Segmented
          ariaLabel="Step type"
          value={type}
          onChange={switchType}
          options={[
            { value: 'send', label: 'Message' },
            { value: 'ai', label: 'AI message' },
            { value: 'call', label: 'Call' },
            { value: 'app', label: 'App' },
          ]}
        />
      </Field>

      {mixed && (
        <>
          <Notice tone="amber">This older step mixes several kinds of channel. It still runs as saved; edit the channels here.</Notice>
          <Field label="Channels">
            <MultiSelect
              value={channels}
              onChange={(v) => onPatch({ channels: v })}
              options={[...SEND_CHANNELS, { value: TEAM_PUSH, label: 'Team notification (in-app)' }, ...CALL_CHANNELS.map((c) => ({ value: c, label: c })), ...INTEGRATION_ACTIONS.map((c) => ({ value: c, label: c, group: 'App actions' }))]}
              size="md"
              ariaLabel="Channels"
            />
          </Field>
        </>
      )}

      {type === 'send' && !mixed && (
        <>
          <DeliveryFields cfg={cfg} onPatch={onPatch} channels={channels} ai={false} />
          <VariableTextarea
            label="Message"
            value={str(cfg.script)}
            onChange={(v) => set('script', v)}
            placeholder="Hi {{first_name}}, …"
            help={cfg.waMode === 'template' ? 'Used on the other channels; WhatsApp sends the template.' : undefined}
          />
          <SwitchRow
            label="Send like a person"
            help="A short typing pause, and long text split into a few shorter messages."
            on={bool(cfg.humanize)}
            onChange={(v) => set('humanize', v || undefined)}
          />
        </>
      )}

      {type === 'send' && mixed && (
        <VariableTextarea label="Message / instruction" value={str(cfg.script)} onChange={(v) => set('script', v)} />
      )}

      {type === 'ai' && (
        <>
          <Field label="Skill" help="Which of your AI skills writes this message.">
            <SkillPicker value={str(cfg.skill)} skills={lookups.skills} loaded={lookups.skillsLoaded} onChange={(v) => set('skill', v)} />
          </Field>
          <VariableTextarea
            label="What should the message do?"
            value={str(cfg.script)}
            onChange={(v) => set('script', v)}
            placeholder="e.g. Thank them for signing up and ask what brought them to us."
            help="A brief for the AI. It writes the actual words, in the contact’s language, using what it knows about them."
          />
          <DeliveryFields cfg={cfg} onPatch={onPatch} channels={channels} ai />
          <SwitchRow
            label="Send like a person"
            help="A short typing pause, and long text split into a few shorter messages."
            on={bool(cfg.humanize)}
            onChange={(v) => set('humanize', v || undefined)}
          />
        </>
      )}

      {type === 'call' && (
        <>
          <Field label="Call type">
            <Segmented
              ariaLabel="Call type"
              value={channels.includes('Script call') ? 'Script call' : 'AI voice call'}
              onChange={(v) => onPatch({ channels: [v] })}
              options={[
                { value: 'AI voice call', label: 'AI voice call', hint: 'A real conversation' },
                { value: 'Script call', label: 'Script call', hint: 'Reads a script, hangs up' },
              ]}
            />
          </Field>
          <Field label="Call over">
            <Segmented
              ariaLabel="Call over"
              value={str(cfg.callVia).startsWith('WhatsApp') ? 'WhatsApp (Baileys)' : 'Phone (SIP)'}
              onChange={(v) => set('callVia', v)}
              options={[
                { value: 'Phone (SIP)', label: 'Phone line' },
                { value: 'WhatsApp (Baileys)', label: 'WhatsApp' },
              ]}
            />
          </Field>
          {channels.includes('Script call') ? (
            <Field label="Script" help="Manage scripts under Script Templates.">
              <Select
                value={str(cfg.scriptTemplateId)}
                onChange={(v) => set('scriptTemplateId', v)}
                options={[
                  ...lookups.scripts.map((s) => ({ value: String(s.id), label: s.name })),
                  ...(str(cfg.scriptTemplateId) && !lookups.scripts.some((s) => String(s.id) === str(cfg.scriptTemplateId))
                    ? [{ value: str(cfg.scriptTemplateId), label: `#${str(cfg.scriptTemplateId)} (missing)` }]
                    : []),
                ]}
                size="md"
                placeholder="Choose a script"
                ariaLabel="Script"
              />
            </Field>
          ) : (
            <>
              <Field label="Skill">
                <SkillPicker value={str(cfg.skill)} skills={lookups.skills} loaded={lookups.skillsLoaded} onChange={(v) => set('skill', v)} />
              </Field>
              <VariableTextarea
                label="What should the AI cover?"
                value={str(cfg.script)}
                onChange={(v) => set('script', v)}
                placeholder="e.g. Confirm tomorrow’s appointment time and offer to reschedule."
              />
            </>
          )}
          {(channels.includes('Script call') || str(cfg.callVia).startsWith('WhatsApp')) && (
            <Field label="Voice">
              <Select
                value={str(cfg.voice)}
                onChange={(v) => set('voice', v)}
                options={[
                  { value: '', label: 'Default voice' },
                  ...lookups.voices.map((v) => ({ value: v.id, label: v.name, hint: 'ElevenLabs' })),
                  ...(str(cfg.voice) && !lookups.voices.some((v) => v.id === str(cfg.voice))
                    ? [{ value: str(cfg.voice), label: `${str(cfg.voice)} (unavailable)` }]
                    : []),
                ]}
                size="md"
                ariaLabel="Voice"
              />
            </Field>
          )}
        </>
      )}

      {type === 'app' && (
        <>
          <Field label="Action" help="Runs against the connection under Integrations; with nothing connected the step logs a skip and the flow carries on.">
            <Select
              value={channels[0] ?? ''}
              onChange={(v) => onPatch({ channels: [v] })}
              options={INTEGRATION_ACTIONS.map((a) => ({ value: a, label: a }))}
              size="md"
              ariaLabel="App action"
            />
          </Field>
          <AppFields action={channels[0] ?? ''} cfg={cfg} set={set} />
          <VariableTextarea
            label={channels[0] === 'Send via Gmail' ? 'Email body' : 'Text / description'}
            value={str(cfg.script)}
            onChange={(v) => set('script', v)}
          />
        </>
      )}
    </>
  )
}

function AppFields({ action, cfg, set }: { action: string; cfg: FlowConfig; set: SetFn }) {
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
          {action === 'Create calendar event' && text('eventTitle', 'Event title', 'Falls back to the text below.')}
          {text('eventStart', 'Start', 'An ISO date-time (2026-08-01T10:00). Empty = tomorrow, same time.', '2026-08-01T10:00')}
          <Field label="Length (minutes)">
            <NumberInput value={cfg.eventMinutes as number} onChange={(v) => set('eventMinutes', v)} min={5} ariaLabel="Length in minutes" />
          </Field>
          {text('calendarId', 'Calendar id (optional)', 'Empty = the connected account’s default calendar.')}
        </>
      )
    case 'Upload file to Drive':
      return text('fileName', 'File name', 'The text below becomes the file body.')
    case 'Append row to Sheet':
      return (
        <>
          {text('spreadsheetId', 'Spreadsheet id (optional)', 'Empty = the one set on the integration.')}
          {text('sheetRange', 'Range', undefined, 'Sheet1!A:Z')}
        </>
      )
    case 'Send via Gmail':
      return text('subject', 'Subject')
    case 'Update order status':
      return text('orderStatus', 'New status', 'The contact’s primary record moves to this status.', 'shipped')
    case 'Create Stripe payment link':
      return text('successUrl', 'After paying, send them to', undefined, 'https://…')
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
  return (
    <div className="flex items-center gap-2">
      <NumberInput value={amount as number} onChange={onAmount} min={0} step={1} ariaLabel={`${label} amount`} className="!w-24" />
      <div className="min-w-0 flex-1">
        <Select value={str(unit) || 'days'} onChange={onUnit} options={DURATION_UNITS} size="md" ariaLabel={`${label} unit`} />
      </div>
    </div>
  )
}

function WaitForm({ cfg, set }: { cfg: FlowConfig; set: SetFn }) {
  const mode = cfg.mode === 'reply' ? 'reply' : 'time'
  return (
    <>
      <Field label="Wait for">
        <Segmented
          ariaLabel="Wait for"
          value={mode}
          onChange={(v) => set('mode', v)}
          options={[
            { value: 'time', label: 'A set time' },
            { value: 'reply', label: 'A reply' },
          ]}
        />
      </Field>
      {mode === 'time' ? (
        <Field label="How long">
          <DurationRow amount={cfg.amount} unit={cfg.unit} onAmount={(v) => set('amount', v)} onUnit={(v) => set('unit', v)} label="Wait" />
        </Field>
      ) : (
        <>
          <Field label="Give up after" help="Leave empty to wait as long as it takes.">
            <DurationRow
              amount={cfg.timeoutAmount}
              unit={cfg.timeoutUnit}
              onAmount={(v) => set('timeoutAmount', v)}
              onUnit={(v) => set('timeoutUnit', v)}
              label="Give up after"
            />
          </Field>
          <Notice>
            The flow moves on either way. Add an <strong>Ask &amp; branch → They replied</strong> step next to do
            something different when no reply came.
          </Notice>
        </>
      )}
    </>
  )
}

function FollowUpForm({ cfg, set, onPatch }: { cfg: FlowConfig; set: SetFn; onPatch: (p: FlowConfig) => void }) {
  const { lookups } = useFlowView()
  const channels = channelsOf(cfg)
  const ai = channels.includes(AI_MESSAGE)
  const max = Number(cfg.maxFollowUps) || 2
  return (
    <>
      <Field label="Wait before each follow-up">
        <DurationRow
          amount={cfg.waitAmount}
          unit={cfg.waitUnit}
          onAmount={(v) => set('waitAmount', v)}
          onUnit={(v) => set('waitUnit', v)}
          label="Wait before each follow-up"
        />
      </Field>
      <Field label="Follow up at most">
        <Select
          value={String(max)}
          onChange={(v) => set('maxFollowUps', Number(v))}
          options={[1, 2, 3, 4, 5].map((n) => ({ value: String(n), label: `${n} time${n === 1 ? '' : 's'}` }))}
          size="md"
          ariaLabel="Maximum follow-ups"
        />
      </Field>
      <Field label="Each follow-up is">
        <Segmented
          ariaLabel="Follow-up content"
          value={ai ? 'ai' : 'text'}
          onChange={(v) =>
            onPatch({ channels: v === 'ai' ? [AI_MESSAGE, ...channels.filter((c) => c !== AI_MESSAGE)] : channels.filter((c) => c !== AI_MESSAGE) })
          }
          options={[
            { value: 'text', label: 'My message' },
            { value: 'ai', label: 'AI writes it' },
          ]}
        />
      </Field>
      {ai && (
        <Field label="Skill">
          <SkillPicker value={str(cfg.skill)} skills={lookups.skills} loaded={lookups.skillsLoaded} onChange={(v) => set('skill', v)} />
        </Field>
      )}
      <VariableTextarea
        label={ai ? 'What should the follow-up say?' : 'Follow-up message'}
        value={str(cfg.script)}
        onChange={(v) => set('script', v)}
        placeholder={ai ? 'e.g. Gently check they saw our last message; offer help.' : 'Just checking you saw my last message, {{first_name}}.'}
        help={ai ? 'The AI writes a fresh nudge each time, aware of how many it has sent.' : 'Sent the same each time.'}
      />
      <DeliveryFields cfg={cfg} onPatch={onPatch} channels={channels} ai={ai} />
      <div className="flex flex-col gap-3 rounded-lg border border-line bg-well p-3">
        <div className="text-[12px] font-semibold text-text-mid">Stop following up when</div>
        <SwitchRow label="They reply" help="Always — any reply ends the follow-ups." on onChange={() => {}} locked />
        <SwitchRow
          label="The conversation is resolved"
          on={bool(cfg.stopOnResolved)}
          onChange={(v) => set('stopOnResolved', v)}
        />
        <Field label="A tag is added (optional)">
          <TextInput value={str(cfg.stopTag)} onChange={(v) => set('stopTag', v)} placeholder="e.g. booked" ariaLabel="Stop tag" />
        </Field>
      </div>
      <SwitchRow
        label="Send like a person"
        help="A short typing pause, and long text split into a few shorter messages."
        on={bool(cfg.humanize)}
        onChange={(v) => set('humanize', v || undefined)}
      />
      <Notice>Follow-ups never go out during quiet hours — they wait until the window opens.</Notice>
    </>
  )
}

// ---- IF / HAND OVER / UPDATE / END -------------------------------------------------------

function DecisionForm({ cfg, set }: { cfg: FlowConfig; set: SetFn }) {
  const basis = str(cfg.basedOn) || "Contact's answer"
  const legacy = !DECISIONS.some((d) => d.value === basis)
  const options: SelectOption[] = [
    ...DECISIONS.map((d) => ({ value: d.value, label: d.label, hint: undefined })),
    ...(legacy ? [{ value: basis, label: `${LEGACY_DECISIONS[basis] ?? basis} (legacy)`, group: 'No longer offered' }] : []),
  ]
  const hint = DECISIONS.find((d) => d.value === basis)?.hint
  return (
    <>
      <Field label="Check whether" help={hint}>
        <Select value={basis} onChange={(v) => set('basedOn', v)} options={options} size="md" ariaLabel="Check" />
      </Field>
      {basis === 'Reply contains' && (
        <Field label="Any of these words" help="Comma-separated, not case-sensitive.">
          <TextInput value={str(cfg.keyword)} onChange={(v) => set('keyword', v)} placeholder="price, quote, buy" ariaLabel="Words" />
        </Field>
      )}
      {(basis === 'Has tag' || basis === 'Contact tag') && (
        <Field label="Tag">
          <TextInput value={str(cfg.tag)} onChange={(v) => set('tag', v)} placeholder="e.g. vip" ariaLabel="Tag" />
        </Field>
      )}
      {basis === 'Custom field equals' && (
        <>
          <Field label="Field">
            <FieldPicker value={str(cfg.field)} onChange={(v) => set('field', v)} />
          </Field>
          <Field label="Equals" help="Not case-sensitive. Leave empty for “is filled in”.">
            <TextInput value={str(cfg.value)} onChange={(v) => set('value', v)} ariaLabel="Value" />
          </Field>
        </>
      )}
      {basis === 'Record status' && (
        <Field label="Record status">
          <TextInput value={str(cfg.status)} onChange={(v) => set('status', v)} ariaLabel="Record status" />
        </Field>
      )}
      {(basis === 'They replied' || basis === 'Reply contains' || basis === 'They said yes') && (
        <Notice>Looks at the reply that arrived during the last Wait (or the message that started the flow).</Notice>
      )}
      <Notice>
        YES and NO each get their own path — build them side by side in the Steps view. A path with no steps simply
        ends the flow.
      </Notice>
    </>
  )
}

function HandoverForm({ cfg, set }: { cfg: FlowConfig; set: SetFn }) {
  const { lookups } = useFlowView()
  const team = str(cfg.team)
  const options: SelectOption[] = [
    { value: '', label: 'Admins (no team)' },
    ...lookups.teams.map((t) => ({ value: String(t.id), label: t.name })),
  ]
  if (team && !options.some((o) => o.value === team)) options.push({ value: team, label: `${team} (not a team)` })
  return (
    <>
      <Field label="Hand over to" help="Teams are managed under Teams.">
        <Select value={team} onChange={(v) => set('team', v)} options={options} size="md" ariaLabel="Team" />
      </Field>
      <Field label="Priority">
        <Segmented
          ariaLabel="Priority"
          value={(str(cfg.priority) || 'Normal') as 'Normal' | 'High' | 'Urgent'}
          onChange={(v) => set('priority', v)}
          options={[
            { value: 'Normal', label: 'Normal' },
            { value: 'High', label: 'High' },
            { value: 'Urgent', label: 'Urgent' },
          ]}
        />
      </Field>
      <VariableTextarea label="Why — a note for the team" rows={3} value={str(cfg.reason)} onChange={(v) => set('reason', v)} />
      <Notice>
        Their latest conversation moves to the team, the AI stops replying in it, and a private note explains why. The
        team is notified; High and Urgent also alert admins.
      </Notice>
    </>
  )
}

function UpdateForm({ cfg, set }: { cfg: FlowConfig; set: SetFn }) {
  const op = str(cfg.op) || 'add_tag'
  return (
    <>
      <Field label="Change">
        <Segmented
          ariaLabel="Change"
          value={op as 'add_tag' | 'remove_tag' | 'set_field'}
          onChange={(v) => set('op', v)}
          options={[
            { value: 'add_tag', label: 'Add tag' },
            { value: 'remove_tag', label: 'Remove tag' },
            { value: 'set_field', label: 'Set field' },
          ]}
        />
      </Field>
      {op === 'set_field' ? (
        <>
          <Field label="Field">
            <FieldPicker value={str(cfg.field)} onChange={(v) => set('field', v)} />
          </Field>
          <Field label="Value" help="May use {{variables}}, e.g. {{first_name}}.">
            <input value={str(cfg.value)} onChange={(e) => set('value', e.target.value)} className={inputCls} aria-label="Value" />
          </Field>
        </>
      ) : (
        <Field label="Tag">
          <TextInput value={str(cfg.tag)} onChange={(v) => set('tag', v)} placeholder="e.g. qualified" ariaLabel="Tag" />
        </Field>
      )}
    </>
  )
}

function EndForm({ cfg, set }: { cfg: FlowConfig; set: SetFn }) {
  return (
    <>
      <Field label="Outcome" help="A short code shown on each finished run, e.g. BOOKED, NO_REPLY.">
        <TextInput value={str(cfg.logCode)} onChange={(v) => set('logCode', v.toUpperCase().replace(/\s+/g, '_'))} placeholder="DONE" ariaLabel="Outcome" />
      </Field>
      <SwitchRow
        label="Remember the outcome"
        help="Adds a line to the contact's AI memory, so chats and calls know how this ended."
        on={bool(cfg.updateMemory)}
        onChange={(v) => set('updateMemory', v ? 'Yes' : 'No')}
      />
    </>
  )
}
