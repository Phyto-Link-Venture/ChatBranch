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
import { Card, CardTitle } from '@/components/ui/Card'
import { EntityPicker } from '@/components/ui/EntityPicker'
import { SaveBar } from '@/components/ui/BindingGrid'
import { Select } from '@/components/ui/Select'
import { Toggle } from '@/components/ui/Toggle'
import { aiApi, type BehaviourField, type BehaviourPayload } from '@/lib/aiMonitor'

const OPTION_LABELS: Record<string, Record<string, string>> = {
  ai_review_mode: {
    auto: 'Send straight away',
    review_flagged: 'Review risky replies',
    review_all: 'Review every reply',
    shadow: 'Shadow mode — draft only, never send',
  },
  ai_split: {
    off: 'Don’t split',
    auto: 'Split replies longer than the limit',
    always: 'Always split at paragraph breaks',
  },
}

/** Fields that only matter when another one is set a certain way. */
const DEPENDS: Record<string, { on: string; when: (v: string) => boolean; note: string }> = {
  ai_review_autosend_minutes: {
    on: 'ai_review_mode',
    when: (v) => v === 'review_flagged' || v === 'review_all',
    note: 'Used when replies are held for review.',
  },
  ai_review_first_n: { on: 'ai_review_mode', when: (v) => v === 'review_flagged', note: 'Used in “Review risky replies”.' },
  ai_review_keywords: { on: 'ai_review_mode', when: (v) => v === 'review_flagged', note: 'Used in “Review risky replies”.' },
  ai_wait_max_seconds: { on: 'ai_wait_seconds', when: (v) => Number(v) > 0, note: 'Used when the wait is on.' },
  ai_ack_text: { on: 'ai_ack_skip', when: (v) => v === 'true', note: 'Used when acknowledgements are skipped.' },
  ai_typing_cps: { on: 'ai_typing', when: (v) => v === 'true', note: 'Used when typing is on.' },
  ai_typing_min_s: { on: 'ai_typing', when: (v) => v === 'true', note: 'Used when typing is on.' },
  ai_typing_max_s: { on: 'ai_typing', when: (v) => v === 'true', note: 'Used when typing is on.' },
  ai_split_max_chars: { on: 'ai_split', when: (v) => v !== 'off', note: 'Used when splitting is on.' },
  ai_followup_first_hours: { on: 'ai_followup', when: (v) => v === 'true', note: 'Used when follow-ups are on.' },
  ai_followup_between_hours: { on: 'ai_followup', when: (v) => v === 'true', note: 'Used when follow-ups are on.' },
  ai_followup_max: { on: 'ai_followup', when: (v) => v === 'true', note: 'Used when follow-ups are on.' },
  ai_followup_template: { on: 'ai_followup', when: (v) => v === 'true', note: 'Used when follow-ups are on.' },
  ai_followup_guidance: { on: 'ai_followup', when: (v) => v === 'true', note: 'Used when follow-ups are on.' },
}

const inputCls =
  'w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[13px] text-text-primary outline-none transition-colors focus:border-brand-400 disabled:opacity-60'

function show(field: BehaviourField, v: string): string {
  if (field.type === 'bool') return v === 'true' ? 'on' : 'off'
  if (field.type === 'select') return OPTION_LABELS[field.key]?.[v] ?? v
  return v === '' ? '(blank)' : v
}

export function AiBehaviourSettings({ initialEntity = null }: { initialEntity?: string | null }) {
  const [entity, setEntity] = useState<string | null>(initialEntity)
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
      setMsg({ ok: true, text: entity ? `Saved for ${data?.entity_name ?? entity}.` : 'Saved organization defaults.' })
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
      setMsg({ ok: true, text: key ? 'Back to the organization default.' : 'All overrides removed for this entity.' })
    } catch (e) {
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    }
  }

  const overrides = fields.filter((f) => f.scope === 'entity').length

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-end gap-3">
        <div className="min-w-[240px] flex-1 basis-[260px] sm:max-w-[360px]">
          <label className="mb-1 block text-[12px] font-semibold text-text-mid">Settings for</label>
          <EntityPicker
            value={entity}
            onChange={(v) => {
              if (changed.length && !window.confirm('Discard unsaved changes?')) return
              setEntity(v)
            }}
            globalLabel="Organization default (every entity)"
            ariaLabel="Entity whose AI behaviour you are editing"
          />
        </div>
        <p className="min-w-0 flex-1 basis-[260px] pb-1 text-[12.5px] leading-relaxed text-text-muted">
          {entity
            ? overrides
              ? `${overrides} setting${overrides === 1 ? '' : 's'} differ from the organization default here. Everything else inherits it.`
              : 'This entity inherits every organization default. Change a value to override it here only.'
            : 'These apply everywhere unless an entity overrides them.'}
          {entity && overrides > 0 && canManage && (
            <button
              type="button"
              onClick={() => void resetField()}
              className="ml-2 font-semibold text-text-mid underline underline-offset-2 hover:text-text-high"
            >
              Remove all overrides
            </button>
          )}
        </p>
      </div>

      {loadError && (
        <div role="alert" className="text-[12.5px] text-[color:var(--red-ink)]">
          Couldn’t load the settings: {loadError}
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
        <p className="text-[12.5px] text-text-muted">You can see these rules; changing them needs the “Edit AI behaviour settings” permission.</p>
      )}

      {!data && !loadError && (
        <div className="flex items-center gap-2 p-4 text-[13px] text-text-muted">
          <Loader2 size={15} className="animate-spin" aria-hidden /> Loading…
        </div>
      )}

      {data && (
        <div className="grid items-start gap-4 lg:grid-cols-[minmax(0,1fr)_340px]">
          <div className="flex min-w-0 flex-col gap-4">
            {data.groups.map((g) => (
              <Card key={g.id} className="px-5 py-4">
                <CardTitle>{g.title}</CardTitle>
                <p className="mb-3 mt-0.5 text-[12.5px] text-text-muted">{g.description}</p>
                <div className="flex flex-col divide-y divide-line">
                  {fields
                    .filter((f) => f.group === g.id)
                    .map((f) => (
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
                    ))}
                </div>
              </Card>
            ))}
            <SaveBar
              dirty={changed.length > 0}
              saving={saving}
              onSave={() => void save()}
              onDiscard={() => setDraft(Object.fromEntries(fields.map((f) => [f.key, f.value])))}
              summary={`${changed.length} change${changed.length === 1 ? '' : 's'}${entity ? ` for ${data.entity_name ?? entity}` : ''}`}
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
  const dep = DEPENDS[f.key]
  const inactive = dep ? !dep.when(all[dep.on] ?? '') : false
  const id = `ab-${f.key}`
  const step = f.key.includes('hours') ? 0.5 : 1

  const control =
    f.type === 'bool' ? (
      <Toggle on={value === 'true'} onChange={(v) => onChange(v ? 'true' : 'false')} disabled={disabled} label={f.label} />
    ) : f.type === 'select' ? (
      <Select
        value={value}
        onChange={onChange}
        disabled={disabled}
        size="md"
        ariaLabel={f.label}
        className="w-full sm:w-[300px]"
        options={(f.options ?? []).map((o) => ({ value: o, label: OPTION_LABELS[f.key]?.[o] ?? o }))}
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
            {f.label}
          </label>
          {f.help && <p className="mt-0.5 text-[12px] leading-relaxed text-text-muted">{f.help}</p>}
          {inactive && dep && <p className="mt-0.5 text-[11.5px] text-text-faint">{dep.note}</p>}
        </div>
        <div className={`shrink-0 ${wide ? 'w-full' : ''}`}>{control}</div>
      </div>
      {entity && (
        <div className="mt-1.5 flex flex-wrap items-center gap-2 text-[11.5px]">
          {f.scope === 'entity' ? (
            <>
              <span className="rounded-md bg-brand-400/10 px-1.5 py-0.5 font-semibold text-brand-300">Set for this entity</span>
              <span className="text-text-faint">Organization default: {show(f, f.inherited_value)}</span>
              {!disabled && (
                <button
                  type="button"
                  onClick={onReset}
                  className="inline-flex items-center gap-1 font-semibold text-text-mid hover:text-text-high"
                >
                  <RotateCcw size={11} aria-hidden /> Use default
                </button>
              )}
            </>
          ) : (
            <span className="text-text-faint">
              Inherits organization default{value !== f.value ? ' (overrides when saved)' : ''}
            </span>
          )}
        </div>
      )}
    </div>
  )
}

// ---- the live example ---------------------------------------------------------

type Row = { at: number; who: 'customer' | 'ai' | 'note'; text: string }

const SAMPLE = [
  { at: 0, text: 'Hi' },
  { at: 6, text: 'Do you deliver to Penang?' },
  { at: 11, text: 'How much is it?' },
]

function sampleReply(covers: number[]): string {
  const has = (i: number) => covers.includes(i)
  if (has(1) && has(2))
    return 'Yes, we deliver to Penang — orders placed before noon usually arrive within two working days.\n\nDelivery is RM 15, and free on orders over RM 150.'
  if (has(2)) return 'Delivery to Penang is RM 15, and free on orders over RM 150.'
  if (has(1)) return 'Yes, we deliver to Penang — orders placed before noon usually arrive within two working days.'
  return 'Hi! How can I help you today?'
}

function splitBubbles(text: string, mode: string, max: number): string[] {
  const paras = text.split(/\n\s*\n/).map((p) => p.trim()).filter(Boolean)
  if (mode === 'off' || paras.length < 2) return [text]
  if (mode === 'auto' && text.length <= max) return [text]
  return paras.slice(0, 3)
}

function riskyReasons(text: string, keywords: string): string[] {
  const out: string[] = []
  for (const k of keywords.split(/[,;\n]/).map((x) => x.trim()).filter(Boolean)) {
    const re = new RegExp(`\\b${k.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}\\b`, 'i')
    if (re.test(text)) out.push(`mentions “${k}”`)
  }
  if (/(RM|\$|USD|€|£)\s?\d/i.test(text)) out.push('mentions a money amount')
  return out
}

function clock(sec: number): string {
  const s = Math.round(sec)
  if (s >= 3600) return `${Math.floor(s / 3600)}h${String(Math.floor((s % 3600) / 60)).padStart(2, '0')}`
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`
}

function ExampleTimeline({ values: v }: { values: Record<string, string> }) {
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
      out.push({ at: m.at, who: 'customer', text: m.text })
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

    let t = 0
    replies.forEach((r, idx) => {
      const text = sampleReply(r.covers)
      t = Math.max(t, r.at)
      if (wait > 0)
        out.push({
          at: r.at,
          who: 'note',
          text:
            r.covers.length > 1
              ? `The customer seems done — the AI writes ONE reply to ${r.covers.length} messages`
              : 'The AI starts writing',
        })
      const reasons =
        mode === 'review_all'
          ? ['every reply is reviewed']
          : mode === 'review_flagged'
            ? [...riskyReasons(text, v.ai_review_keywords ?? ''), ...(idx < firstN ? [firstN === 1 ? 'the first reply in the conversation' : `one of the first ${firstN} replies`] : [])]
            : []
      if (mode === 'shadow') {
        out.push({ at: t, who: 'note', text: 'Shadow mode: the draft is recorded in the AI monitor and never sent' })
        return
      }
      if (reasons.length) {
        out.push({ at: t, who: 'note', text: `Held for review — ${reasons.join(', ')}` })
        if (autosend > 0) {
          t += autosend * 60
          out.push({ at: t, who: 'note', text: `Nobody acted for ${autosend} min, so it goes out on its own` })
        } else {
          out.push({ at: t, who: 'note', text: 'Waits until a person approves it (shown as if approved right away)' })
        }
      }
      const bubbles = splitBubbles(text, v.ai_split || 'off', num('ai_split_max_chars', 320))
      bubbles.forEach((b, bi) => {
        const d = typing ? delay(b) : bi === 0 ? 0 : 2
        if (typing) out.push({ at: t, who: 'note', text: `typing… (${Math.round(d)}s)` })
        t += d
        out.push({ at: t, who: 'ai', text: b })
      })
    })

    // an acknowledgement afterwards
    t += 60
    out.push({ at: t, who: 'customer', text: 'ok thanks' })
    if (on('ai_ack_skip')) {
      const ack = (v.ai_ack_text ?? '').trim()
      if (mode !== 'shadow' && ack) out.push({ at: t + Math.max(0, wait), who: 'ai', text: ack })
      else out.push({ at: t, who: 'note', text: 'Just an acknowledgement — no reply needed' })
    } else {
      out.push({ at: t + Math.max(0, wait), who: 'note', text: 'The AI replies to the acknowledgement too' })
    }
    return out
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [v])

  const followUps = useMemo(() => {
    if (!on('ai_followup')) return null
    const first = num('ai_followup_first_hours', 4)
    const between = num('ai_followup_between_hours', 24)
    const max = Math.max(0, Math.round(num('ai_followup_max', 2)))
    const out: { h: number; text: string }[] = []
    for (let i = 0; i < max; i++) out.push({ h: first + i * between, text: `Follow-up ${i + 1}, written by the AI` })
    return out
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [v])

  return (
    <Card className="px-5 py-4">
      <CardTitle>How it plays out</CardTitle>
      <p className="mb-3 mt-0.5 text-[12px] text-text-muted">A sample conversation with the settings on screen — updates as you change them.</p>
      <ol className="relative flex flex-col gap-2 border-l border-line pl-4" aria-label="Example conversation">
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
                    {r.who === 'customer' ? 'Customer' : 'AI'}
                  </span>
                  {r.text}
                </span>
              )}
            </div>
          </li>
        ))}
      </ol>
      <div className="mt-4 border-t border-line pt-3">
        <div className="eyebrow mb-1.5">If the AI asks something and hears nothing</div>
        {followUps === null ? (
          <p className="text-[12px] text-text-muted">Follow-ups are off — the conversation simply waits.</p>
        ) : followUps.length === 0 ? (
          <p className="text-[12px] text-text-muted">No follow-ups (the limit is 0).</p>
        ) : (
          <ul className="flex flex-col gap-1 text-[12px] text-text-mid">
            {followUps.map((f) => (
              <li key={f.h} className="flex gap-2">
                <span className="w-10 shrink-0 tabular-nums text-text-faint">+{f.h}h</span>
                {f.text}
              </li>
            ))}
            <li className="flex gap-2 text-text-muted">
              <span className="w-10 shrink-0" />
              Then it stops. Any reply, an opt-out, a teammate stepping in or quiet hours change the plan.
            </li>
          </ul>
        )}
      </div>
    </Card>
  )
}
