'use client'

import { useMemo, useState, type ReactNode } from 'react'
import {
  AlertCircle,
  ArrowLeft,
  ArrowRight,
  BookOpen,
  Check,
  CheckCircle2,
  FlaskConical,
  Info,
  Loader2,
  Play,
  ShieldCheck,
  Sparkles,
  Wand2,
  Wrench,
  XCircle,
} from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Modal } from '@/components/ui/Modal'
import { MultiSelect, Select } from '@/components/ui/Select'
import { EntityPicker } from '@/components/ui/EntityPicker'
import { Toggle } from '@/components/ui/Toggle'
import { CodeEditor } from './CodeEditor'
import { TestCasesEditor, rowsToTests, testsToRows, type TestRow } from './TestCasesEditor'
import {
  CHANNEL_OPTIONS,
  KIND_META,
  ON_FAIL_OPTIONS,
  aicore,
  newSkillId,
  pretty,
  saveSkill,
  type DraftResponse,
  type OnFail,
  type Skill,
  type SkillKind,
  type TestResponse,
} from './skillsApi'

/**
 * The skill wizard: Describe → Clarify → Review → Test → Save.
 *
 *  Describe  the operator says what the skill should do, and (optionally) which
 *            kind it is; the server drafts with the AI — or, with no model
 *            configured, from its template library.
 *  Clarify   the questions the AI asked, answered inline; repeats until it has
 *            enough to write the code.
 *  Review    everything editable: name, trigger, the JavaScript, the tool's
 *            argument schema, the test cases, scope and channels.
 *  Test      the tests run in the same sandbox the AI uses. Saving switched ON
 *            needs every test passing; "save as disabled" is the way out.
 *
 * Editing an existing skill opens straight on Review.
 */

export type WizardInit = { mode: 'new'; kindHint?: SkillKind | '' } | { mode: 'edit'; skill: Skill }

type Step = 'describe' | 'clarify' | 'review' | 'test'
type Hint = '' | SkillKind

const input =
  'w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[13px] text-text-primary outline-none transition-colors focus:border-brand-400 disabled:opacity-70'

const EXAMPLES: { text: string; kind: Hint }[] = [
  { text: 'Tell customers whether we are open right now and when we open next', kind: '' },
  { text: 'Never repeat a customer’s full IC number back to them', kind: 'check' },
  { text: 'Work out the delivery date 3 business days from today', kind: 'tool' },
  { text: 'Make sure a reply never says the wrong day of the week', kind: 'check' },
]

const KIND_CHOICES: { value: Hint; label: string; blurb: string; Icon: typeof Wand2 }[] = [
  { value: '', label: 'Let the AI decide', blurb: 'Pick the best fit from your description', Icon: Wand2 },
  { value: 'tool', label: 'Tool', blurb: 'Code the AI calls for an exact answer', Icon: Wrench },
  { value: 'check', label: 'Reply check', blurb: 'Code that vets every reply before sending', Icon: ShieldCheck },
  { value: 'prompt', label: 'Instructions', blurb: 'Guidance in plain words — no code', Icon: BookOpen },
]

interface Form {
  id: string
  name: string
  kind: SkillKind
  useWhen: string
  description: string
  instructions: string
  code: string
  params: string
  rows: TestRow[]
  onFail: OnFail
  scope: string | null
  channels: string[]
  enabled: boolean
  position: number
}

function formFromSkill(s: Skill): Form {
  return {
    id: s.id,
    name: s.name,
    kind: s.kind,
    useWhen: s.useWhen,
    description: s.description,
    instructions: s.instructions,
    code: s.code,
    params: s.parameters ? pretty(s.parameters) : '',
    rows: s.kind === 'prompt' ? [] : testsToRows(s.kind, s.tests ?? []),
    onFail: s.onFail,
    scope: s.entity ?? null,
    channels: s.channels ?? [],
    enabled: s.enabled,
    position: s.position,
  }
}

export function SkillWizard({
  init,
  defaultScope,
  readOnly = false,
  onClose,
  onSaved,
}: {
  init: WizardInit
  defaultScope: string | null
  readOnly?: boolean
  onClose: () => void
  onSaved: (message: string) => void
}) {
  const editing = init.mode === 'edit'
  const original = init.mode === 'edit' ? init.skill : null

  const [step, setStep] = useState<Step>(editing ? 'review' : 'describe')
  const [description, setDescription] = useState('')
  const [hint, setHint] = useState<Hint>(init.mode === 'new' ? init.kindHint ?? '' : '')
  const [qa, setQa] = useState<{ question: string; answer: string }[]>([])
  const [round, setRound] = useState(0) // index in qa where the current batch starts
  const [form, setForm] = useState<Form | null>(original ? formFromSkill(original) : null)
  const [note, setNote] = useState<{ text: string; source: string } | null>(null)
  const [busy, setBusy] = useState<false | 'draft' | 'test' | 'save'>(false)
  const [error, setError] = useState<string | null>(null)
  const [run, setRun] = useState<TestResponse | null>(null)
  const [stale, setStale] = useState(true)

  // Any edit to code/tests invalidates the last run.
  const patch = (p: Partial<Form>) => {
    setForm((f) => (f ? { ...f, ...p } : f))
    if ('code' in p || 'rows' in p || 'kind' in p) setStale(true)
  }

  const isCode = form ? form.kind !== 'prompt' : hint !== 'prompt'

  // ---- describe / clarify ----------------------------------------------------

  const draft = async (answers: { question: string; answer: string }[]) => {
    setBusy('draft')
    setError(null)
    try {
      const d = await aicore<DraftResponse>('skills/draft', {
        method: 'POST',
        body: JSON.stringify({ description, kind_hint: hint || null, answers }),
      })
      if (d.status === 'questions') {
        setRound(answers.length)
        setQa([...answers, ...d.questions.map((q) => ({ question: q, answer: '' }))])
        setNote(d.note ? { text: d.note, source: d.source } : null)
        setStep('clarify')
        return
      }
      const s = d.skill!
      const kind = s.kind
      setForm({
        id: newSkillId(),
        name: s.name,
        kind,
        useWhen: s.use_when,
        description: s.description || description,
        instructions: s.instructions,
        code: s.code,
        params: kind === 'tool' && s.parameters ? pretty(s.parameters) : '',
        rows: kind === 'prompt' ? [] : testsToRows(kind, s.tests ?? []),
        onFail: s.on_fail || 'retry',
        scope: defaultScope,
        channels: [],
        enabled: true,
        position: 9999,
      })
      setNote(d.note ? { text: d.note, source: d.source } : null)
      setStale(true)
      setRun(null)
      setStep('review')
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  // ---- tests -------------------------------------------------------------------

  const testsPayload = () => {
    if (!form || form.kind === 'prompt') return { tests: [] as unknown[], error: null as string | null }
    return rowsToTests(form.kind, form.rows)
  }

  const paramsPayload = (): { value: unknown; error: string | null } => {
    if (!form || form.kind !== 'tool' || !form.params.trim()) return { value: null, error: null }
    try {
      const v = JSON.parse(form.params)
      if (!v || typeof v !== 'object' || Array.isArray(v))
        return { value: null, error: 'The parameters must be a JSON object (a JSON schema).' }
      return { value: v, error: null }
    } catch {
      return { value: null, error: 'The parameters are not valid JSON.' }
    }
  }

  const runTests = async () => {
    if (!form || form.kind === 'prompt') return
    const t = testsPayload()
    const p = paramsPayload()
    if (t.error || p.error) {
      setError(t.error || p.error)
      return
    }
    setBusy('test')
    setError(null)
    setStep('test')
    try {
      const r = await aicore<TestResponse>('skills/test', {
        method: 'POST',
        body: JSON.stringify({ kind: form.kind, code: form.code, tests: t.tests }),
      })
      setRun(r)
      setStale(false)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  const allPassed = !!run && !stale && run.valid && run.total > 0 && run.failed === 0

  // ---- save ----------------------------------------------------------------------

  const save = async (enabled: boolean) => {
    if (!form) return
    const name = form.name.trim()
    if (!name) {
      setError('Give the skill a name.')
      return
    }
    const t = testsPayload()
    const p = paramsPayload()
    if (t.error || p.error) {
      setError(t.error || p.error)
      return
    }
    setBusy('save')
    setError(null)
    try {
      await saveSkill(
        {
          id: form.id,
          name,
          useWhen: form.useWhen,
          instructions: form.kind === 'prompt' ? form.instructions : '',
          position: form.position,
          kind: form.kind,
          description: form.description,
          code: form.kind === 'prompt' ? '' : form.code,
          parameters: p.value,
          tests: t.tests,
          enabled,
          channels: form.channels,
          onFail: form.onFail,
        },
        form.scope,
        original ? original.entity ?? null : undefined,
      )
      onSaved(
        enabled
          ? `“${name}” saved — the AI uses it from the next message.`
          : `“${name}” saved switched off — turn it on once its tests pass.`,
      )
      onClose()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  // ---- chrome ----------------------------------------------------------------------

  const steps: { id: Step; label: string }[] = useMemo(() => {
    const out: { id: Step; label: string }[] = []
    if (!editing) out.push({ id: 'describe', label: 'Describe' }, { id: 'clarify', label: 'Clarify' })
    out.push({ id: 'review', label: isCode ? 'Review code' : 'Review' })
    if (isCode) out.push({ id: 'test', label: 'Run tests' })
    return out
  }, [editing, isCode])

  const title = editing ? (readOnly ? form?.name ?? 'Skill' : `Edit “${original?.name}”`) : 'New skill'
  const subtitle: Record<Step, string> = {
    describe: 'Say in your own words what the AI should be able to do. It will ask if anything is unclear.',
    clarify: 'A few questions so the code does exactly what you mean.',
    review: isCode
      ? 'Check the code and its test cases — everything here is editable.'
      : 'Check the wording and when it applies.',
    test: 'The tests run in the same sandbox the AI uses for real replies.',
  }

  const footer = (() => {
    if (readOnly)
      return (
        <Button variant="secondary" onClick={onClose}>
          Close
        </Button>
      )
    const spinner = <Loader2 size={13} className="animate-spin" aria-hidden />
    const spin = busy === 'draft' && spinner
    if (step === 'describe')
      return (
        <>
          <Button variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button onClick={() => draft([])} disabled={!!busy || description.trim().length < 4}>
            {spin || <Sparkles size={13} aria-hidden />} Continue
          </Button>
        </>
      )
    if (step === 'clarify') {
      const current = qa.slice(round)
      return (
        <>
          <Button variant="ghost" onClick={() => setStep('describe')} disabled={!!busy}>
            <ArrowLeft size={13} aria-hidden /> Back
          </Button>
          <Button
            variant="secondary"
            disabled={!!busy}
            onClick={() =>
              draft(qa.map((x) => ({ ...x, answer: x.answer.trim() || 'No preference — use a sensible default.' })))
            }
          >
            Skip — draft now
          </Button>
          <Button onClick={() => draft(qa)} disabled={!!busy || current.some((x) => !x.answer.trim())}>
            {spin} Continue <ArrowRight size={13} aria-hidden />
          </Button>
        </>
      )
    }
    if (step === 'review')
      return (
        <>
          {!editing && (
            <Button variant="ghost" onClick={() => setStep(qa.length ? 'clarify' : 'describe')} disabled={!!busy}>
              <ArrowLeft size={13} aria-hidden /> Back
            </Button>
          )}
          <Button variant="ghost" onClick={onClose} disabled={!!busy}>
            Cancel
          </Button>
          {isCode ? (
            <Button onClick={runTests} disabled={!!busy}>
              {busy === 'test' ? spinner : <Play size={13} aria-hidden />} Run tests
            </Button>
          ) : (
            <Button onClick={() => save(form?.enabled ?? true)} disabled={!!busy}>
              {busy === 'save' ? spinner : <Check size={13} aria-hidden />} Save
            </Button>
          )}
        </>
      )
    return (
      <>
        <Button variant="ghost" onClick={() => setStep('review')} disabled={!!busy}>
          <ArrowLeft size={13} aria-hidden /> Back to code
        </Button>
        <Button variant="secondary" onClick={() => save(false)} disabled={!!busy}>
          Save anyway as disabled
        </Button>
        <Button
          onClick={() => save(true)}
          disabled={!!busy || !allPassed}
          title={allPassed ? undefined : 'Every test must pass (and there must be at least one) to switch the skill on.'}
        >
          {busy === 'save' ? spinner : <Check size={13} aria-hidden />} Save &amp; switch on
        </Button>
      </>
    )
  })()

  return (
    <Modal open onClose={onClose} title={title} subtitle={subtitle[step]} size="xl" closeOnScrim={false} footer={footer}>
      {error && (
        <div
          role="alert"
          className="mb-4 flex items-start gap-2 rounded-lg border border-red/30 bg-red/10 px-3 py-2 text-[12.5px] text-[color:var(--red-ink)]"
        >
          <AlertCircle size={14} className="mt-0.5 shrink-0" aria-hidden /> <span className="min-w-0">{error}</span>
        </div>
      )}

      <Stepper steps={steps} current={step} />

      {step === 'describe' && (
        <div className="flex flex-col gap-5">
          <label className="block">
            <span className="mb-1.5 block text-[12px] font-semibold text-text-high">What should the AI be able to do?</span>
            <textarea
              rows={4}
              value={description}
              autoFocus
              onChange={(e) => setDescription(e.target.value)}
              placeholder="e.g. Tell customers whether we're open right now, and if not, when we open next."
              className={`${input} resize-y leading-[1.6]`}
            />
          </label>
          <div className="flex flex-wrap gap-1.5">
            <span className="mr-1 self-center text-[11.5px] text-text-muted">Try:</span>
            {EXAMPLES.map((x) => (
              <button
                key={x.text}
                type="button"
                onClick={() => {
                  setDescription(x.text)
                  setHint(x.kind)
                }}
                className="rounded-full border border-line px-2.5 py-1 text-left text-[11.5px] text-text-mid transition-colors hover:border-line-strong hover:bg-fill-hover hover:text-text-high"
              >
                {x.text}
              </button>
            ))}
          </div>
          <fieldset>
            <legend className="eyebrow mb-2">Kind of skill</legend>
            <div className="grid grid-cols-1 gap-2 sm:grid-cols-2 lg:grid-cols-4">
              {KIND_CHOICES.map(({ value, label, blurb, Icon }) => {
                const on = hint === value
                return (
                  <button
                    key={label}
                    type="button"
                    role="radio"
                    aria-checked={on}
                    onClick={() => setHint(value)}
                    className={`flex flex-col gap-1 rounded-lg border px-3 py-2.5 text-left transition-colors ${
                      on ? 'border-primary bg-fill' : 'border-line hover:border-line-strong hover:bg-fill-subtle'
                    }`}
                  >
                    <span className="flex items-center gap-1.5 text-[12.5px] font-semibold text-text-heading">
                      <Icon size={13} aria-hidden /> {label}
                    </span>
                    <span className="text-[11.5px] leading-snug text-text-muted">{blurb}</span>
                  </button>
                )
              })}
            </div>
          </fieldset>
        </div>
      )}

      {step === 'clarify' && (
        <div className="flex flex-col gap-4">
          <Quote>{description}</Quote>
          {note && <Note text={note.text} />}
          {qa.map((x, i) => {
            const past = i < round
            return (
              <label key={i} className="block">
                <span className={`mb-1.5 flex gap-2 text-[12.5px] font-semibold ${past ? 'text-text-muted' : 'text-text-high'}`}>
                  <span className="font-mono text-text-faint">Q{i + 1}</span> {x.question}
                </span>
                <textarea
                  rows={2}
                  value={x.answer}
                  autoFocus={i === round}
                  onChange={(e) => setQa(qa.map((y, j) => (j === i ? { ...y, answer: e.target.value } : y)))}
                  className={`${input} resize-y leading-[1.55]`}
                />
              </label>
            )
          })}
        </div>
      )}

      {(step === 'review' || step === 'test') && form && (
        <div className={step === 'test' ? 'hidden' : 'flex flex-col gap-5'}>
          {note && <Note text={note.text} />}
          <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
            <Field label="Name">
              <input
                value={form.name}
                disabled={readOnly}
                onChange={(e) => patch({ name: e.target.value })}
                className={input}
              />
              {form.kind === 'tool' && (
                <span className="mt-1 block text-[11px] text-text-muted">
                  The AI calls it as{' '}
                  <code className="font-mono text-text-mid">
                    {form.name.toLowerCase().replace(/[^a-z0-9]+/g, '_').replace(/^_+|_+$/g, '') || 'skill'}()
                  </code>
                </span>
              )}
            </Field>
            <Field label="Kind">
              <Select
                value={form.kind}
                disabled={readOnly}
                onChange={(v) => {
                  const next = v as SkillKind
                  if (next === form.kind) return
                  // tool ↔ check: carry the test cases across in the new shape
                  const carried =
                    form.kind !== 'prompt' && next !== 'prompt'
                      ? testsToRows(next, rowsToTests(form.kind, form.rows).tests)
                      : form.rows
                  patch({ kind: next, rows: next === 'prompt' ? [] : carried })
                }}
                size="md"
                ariaLabel="Kind"
                options={(['prompt', 'tool', 'check'] as SkillKind[]).map((k) => ({
                  value: k,
                  label: KIND_META[k].label,
                  hint: k === 'prompt' ? 'no code' : undefined,
                }))}
              />
            </Field>
            <Field label="Use when" hint="When the AI should reach for it.">
              <input
                value={form.useWhen}
                disabled={readOnly}
                placeholder={form.kind === 'check' ? 'every reply' : 'e.g. the customer asks if we are open'}
                onChange={(e) => patch({ useWhen: e.target.value })}
                className={input}
              />
            </Field>
            <Field label="Description" hint="What it does, in a sentence — the AI reads this too.">
              <input
                value={form.description}
                disabled={readOnly}
                onChange={(e) => patch({ description: e.target.value })}
                className={input}
              />
            </Field>
          </div>

          {form.kind === 'prompt' ? (
            <Field label="Instructions">
              <textarea
                rows={7}
                value={form.instructions}
                disabled={readOnly}
                placeholder="Collect the quantity and company name, then hand the conversation to sales."
                onChange={(e) => patch({ instructions: e.target.value })}
                className={`${input} resize-y leading-[1.6]`}
              />
            </Field>
          ) : (
            <>
              <Field
                label="Code"
                hint={
                  form.kind === 'tool'
                    ? 'Define function run(args, ctx) and return the answer. ctx has now, today, time, weekday, timezone, contact; dates.* has date helpers.'
                    : 'Define function check(reply, ctx) and return { ok: true } or { ok: false, reason, fix }.'
                }
              >
                <CodeEditor value={form.code} readOnly={readOnly} onChange={(v) => patch({ code: v })} ariaLabel="Skill code" />
              </Field>
              {form.kind === 'tool' && (
                <Field label="Arguments (JSON schema)" hint="What the AI passes when it calls the tool. Leave empty for none.">
                  <CodeEditor
                    value={form.params}
                    readOnly={readOnly}
                    minRows={4}
                    maxRows={12}
                    ariaLabel="Arguments schema"
                    onChange={(v) => patch({ params: v })}
                  />
                </Field>
              )}
              {form.kind === 'check' && (
                <Field label="When a reply fails">
                  <Select
                    value={form.onFail}
                    disabled={readOnly}
                    onChange={(v) => patch({ onFail: v as OnFail })}
                    size="md"
                    ariaLabel="When a reply fails"
                    options={ON_FAIL_OPTIONS.map((o) => ({ value: o.value, label: o.label, hint: o.hint }))}
                  />
                </Field>
              )}
              <Field label="Test cases">
                <TestCasesEditor
                  kind={form.kind}
                  rows={form.rows}
                  readOnly={readOnly}
                  results={stale ? null : run?.results}
                  onChange={(rows) => patch({ rows })}
                />
              </Field>
            </>
          )}

          <div className="grid grid-cols-1 gap-4 border-t border-line pt-4 md:grid-cols-[1fr_1fr_auto]">
            <Field label="Who uses it">
              <EntityPicker value={form.scope} disabled={readOnly} onChange={(scope) => patch({ scope })} />
            </Field>
            <Field label="Channels">
              <MultiSelect
                value={form.channels}
                disabled={readOnly}
                onChange={(channels) => patch({ channels })}
                options={CHANNEL_OPTIONS}
                placeholder="All channels"
                size="md"
                ariaLabel="Channels"
              />
            </Field>
            {form.kind === 'prompt' && (
              <Field label="Active">
                <div className="flex h-[38px] items-center gap-2">
                  <Toggle
                    on={form.enabled}
                    disabled={readOnly}
                    onChange={(enabled) => patch({ enabled })}
                    label="Skill active"
                  />
                  <span className="text-[12px] text-text-muted">{form.enabled ? 'On' : 'Off'}</span>
                </div>
              </Field>
            )}
          </div>
        </div>
      )}

      {step === 'test' && form && (
        <TestStep run={run} busy={busy === 'test'} stale={stale} onRerun={runTests} rows={form.rows.length} />
      )}
    </Modal>
  )
}

// ---- pieces -----------------------------------------------------------------------

function Stepper({ steps, current }: { steps: { id: Step; label: string }[]; current: Step }) {
  const at = steps.findIndex((s) => s.id === current)
  return (
    <ol className="mb-5 flex flex-wrap items-center gap-x-1 gap-y-2 border-b border-line pb-3">
      {steps.map((s, i) => {
        const done = i < at
        const on = i === at
        return (
          <li key={s.id} className="flex items-center gap-1">
            {i > 0 && <span className="mx-1.5 h-px w-5 bg-line-strong" aria-hidden />}
            <span
              className={`grid h-5 w-5 place-items-center rounded-full text-[10.5px] font-bold ${
                on
                  ? 'bg-primary text-on-primary'
                  : done
                    ? 'bg-green/15 text-[color:var(--green-ink)]'
                    : 'border border-line-strong text-text-muted'
              }`}
            >
              {done ? <Check size={11} strokeWidth={3} aria-hidden /> : i + 1}
            </span>
            <span
              className={`text-[12px] ${on ? 'font-semibold text-text-heading' : 'text-text-muted'}`}
              aria-current={on ? 'step' : undefined}
            >
              {s.label}
            </span>
          </li>
        )
      })}
    </ol>
  )
}

function Field({ label, hint, children }: { label: string; hint?: string; children: ReactNode }) {
  return (
    <div className="min-w-0">
      <div className="mb-1.5 text-[12px] font-semibold text-text-high">{label}</div>
      {children}
      {hint && <div className="mt-1 text-[11px] leading-snug text-text-muted">{hint}</div>}
    </div>
  )
}

function Quote({ children }: { children: ReactNode }) {
  return (
    <blockquote className="border-l-2 border-line-strong pl-3 text-[13px] italic leading-relaxed text-text-mid">
      {children}
    </blockquote>
  )
}

function Note({ text }: { text: string }) {
  return (
    <div className="flex items-start gap-2 rounded-lg border border-line bg-fill-subtle px-3 py-2 text-[12px] leading-relaxed text-text-mid">
      <Info size={13} className="mt-0.5 shrink-0 text-text-muted" aria-hidden /> <span>{text}</span>
    </div>
  )
}

function TestStep({
  run,
  busy,
  stale,
  rows,
  onRerun,
}: {
  run: TestResponse | null
  busy: boolean
  stale: boolean
  rows: number
  onRerun: () => void
}) {
  if (busy || !run)
    return (
      <div className="flex items-center gap-2 py-10 text-[13px] text-text-muted">
        <Loader2 size={15} className="animate-spin" aria-hidden /> Running {rows} test{rows === 1 ? '' : 's'} in the
        sandbox…
      </div>
    )
  const ok = run.valid && run.total > 0 && run.failed === 0 && !stale
  return (
    <div className="flex flex-col gap-4">
      <div
        className={`flex flex-wrap items-center gap-3 rounded-lg border px-4 py-3 ${
          ok ? 'border-green/40 bg-green/10' : 'border-red/30 bg-red/10'
        }`}
      >
        {ok ? (
          <CheckCircle2 size={18} className="text-green-300" aria-hidden />
        ) : (
          <XCircle size={18} className="text-red-300" aria-hidden />
        )}
        <div className="min-w-0 flex-1">
          <div className="display text-[17px] text-text-heading">
            {!run.valid
              ? 'The code does not run'
              : run.total === 0
                ? 'No test cases'
                : ok
                  ? `All ${run.total} tests pass`
                  : `${run.failed} of ${run.total} tests fail`}
          </div>
          <div className="text-[12px] text-text-mid">
            {!run.valid
              ? run.error
              : run.total === 0
                ? 'Add at least one test case so the skill can be switched on.'
                : ok
                  ? 'Ready to switch on.'
                  : 'Go back to the code, fix it (or the test), and run again — or save it switched off.'}
          </div>
        </div>
        <Button variant="secondary" size="sm" onClick={onRerun}>
          <FlaskConical size={13} aria-hidden /> Run again
        </Button>
      </div>

      {run.results.length > 0 && (
        <ul className="divide-y divide-line rounded-lg border border-line">
          {run.results.map((r, i) => (
            <li key={i} className="flex flex-wrap items-start gap-x-3 gap-y-1 px-4 py-2.5">
              {r.pass ? (
                <CheckCircle2 size={14} className="mt-0.5 text-green-300" aria-label="Passed" />
              ) : (
                <XCircle size={14} className="mt-0.5 text-red-300" aria-label="Failed" />
              )}
              <div className="min-w-0 flex-1">
                <div className="text-[12.5px] font-semibold text-text-high">{r.name}</div>
                {!r.pass && (
                  <div className="mt-1 grid gap-1 font-mono text-[11.5px] leading-[1.5] text-text-mid sm:grid-cols-2">
                    {r.error ? (
                      <span className="text-[color:var(--red-ink)] sm:col-span-2">{r.error}</span>
                    ) : (
                      <>
                        <span className="break-words">
                          <span className="text-text-faint">expected </span>
                          {JSON.stringify(r.expected)}
                        </span>
                        <span className="break-words">
                          <span className="text-text-faint">got </span>
                          {JSON.stringify(r.got)}
                        </span>
                      </>
                    )}
                  </div>
                )}
              </div>
              <span className="font-mono text-[11px] text-text-faint">{r.ms.toFixed(1)} ms</span>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
