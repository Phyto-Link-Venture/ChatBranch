'use client'

import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import {
  AlertCircle,
  ArrowLeft,
  ArrowRight,
  BookOpen,
  CalendarClock,
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
import { Advanced } from '@/components/ui/Advanced'
import { Button } from '@/components/ui/Button'
import { Modal } from '@/components/ui/Modal'
import { MultiSelect, Select } from '@/components/ui/Select'
import { EntityPicker } from '@/components/ui/EntityPicker'
import { Toggle } from '@/components/ui/Toggle'
import { CodeEditor } from './CodeEditor'
import { TestCasesEditor, rowsToTests, testsToRows, type TestRow } from './TestCasesEditor'
import { AppSkillForm, calendarConfigProblem } from './AppSkillForm'
import { ToolAppForm } from './ToolAppForm'
import {
  fetchToolApps,
  isToolAppConfig,
  readyApp,
  toolAppConfig,
  toolAppProblem,
  type PromptTemplateId,
  type ToolAppCatalogEntry,
  type ToolAppConfig,
  type ToolAppId,
} from './readySkills'
import {
  KIND_META,
  ON_FAIL_OPTIONS,
  aicore,
  appLabel,
  calendarApp,
  calendarConfig,
  channelOptions,
  fetchApps,
  newSkillId,
  pretty,
  saveSkill,
  type AppCatalogEntry,
  type CalendarAppConfig,
  type DraftResponse,
  type OnFail,
  type Skill,
  type SkillKind,
  type TestResponse,
} from './skillsApi'
import { useFormat, useT, type MessageKey, type TFunction } from '@/i18n'

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
 *
 * A "Connected app" skill (kind `app` — Calendar booking) skips the AI
 * drafting: picking it (or opening the wizard with kindHint "app") goes
 * straight to Set up, a settings form (<AppSkillForm/>) with no code or tests.
 */

export type WizardInit =
  | {
      mode: 'new'
      kindHint?: SkillKind | ''
      /** A ready-made app (Documents, QR, Payments, Sheets): straight to Set up with its defaults. */
      app?: ToolAppId
      /** An instruction template from the gallery: straight to Review, prefilled. */
      template?: PromptTemplateId
    }
  | { mode: 'edit'; skill: Skill }

type Step = 'describe' | 'clarify' | 'review' | 'test'
/** The describe step's kind cards — code/prompt kinds only; an app is picked apart. */
type Hint = '' | Exclude<SkillKind, 'app'>

const input =
  'w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[13px] text-text-primary outline-none transition-colors focus:border-brand-400 disabled:opacity-70'

const EXAMPLES: { text: MessageKey; kind: Hint }[] = [
  { text: 'ai.wizard.examples.open', kind: '' },
  { text: 'ai.wizard.examples.ic', kind: 'check' },
  { text: 'ai.wizard.examples.delivery', kind: 'tool' },
  { text: 'ai.wizard.examples.weekday', kind: 'check' },
]

const KIND_CHOICES: { value: Hint; label: MessageKey; blurb: MessageKey; Icon: typeof Wand2 }[] = [
  { value: '', label: 'ai.wizard.kindChoices.auto', blurb: 'ai.wizard.kindChoices.autoBlurb', Icon: Wand2 },
  { value: 'tool', label: 'ai.skills.kinds.tool.label', blurb: 'ai.wizard.kindChoices.toolBlurb', Icon: Wrench },
  { value: 'check', label: 'ai.skills.kinds.check.label', blurb: 'ai.wizard.kindChoices.checkBlurb', Icon: ShieldCheck },
  { value: 'prompt', label: 'ai.wizard.kindChoices.prompt', blurb: 'ai.wizard.kindChoices.promptBlurb', Icon: BookOpen },
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
  /** Connected-app settings — app skills only. */
  config: CalendarAppConfig | null
  /** A ready-made app's settings (Documents, QR, Payments, Sheets) — instead of `config`. */
  toolConfig?: ToolAppConfig | null
}

const isCodeKind = (k: SkillKind): k is 'tool' | 'check' => k === 'tool' || k === 'check'

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
    rows: isCodeKind(s.kind) ? testsToRows(s.kind, s.tests ?? []) : [],
    onFail: s.onFail,
    scope: s.entity ?? null,
    channels: s.channels ?? [],
    enabled: s.enabled,
    position: s.position,
    config: s.kind === 'app' && !isToolAppConfig(s.config) ? calendarConfig(s.config) : null,
    toolConfig: s.kind === 'app' && isToolAppConfig(s.config) ? toolAppConfig(s.config) : null,
  }
}

/** A fresh ready-made app skill (Documents, QR, Payments, Sheets), words in the dashboard's language. */
function newToolAppForm(scope: string | null, app: ToolAppId, t: TFunction): Form {
  const meta = readyApp(app)
  return {
    id: newSkillId(),
    name: t(meta.name),
    kind: 'app',
    useWhen: t(meta.useWhen),
    description: t(meta.blurb),
    instructions: t(meta.instructions),
    code: '',
    params: '',
    rows: [],
    onFail: 'retry',
    scope,
    channels: [],
    enabled: true,
    position: 9999,
    config: null,
    toolConfig: toolAppConfig({}, app),
  }
}

/** An instruction (prompt) skill from the gallery's templates. */
function templateForm(scope: string | null, id: PromptTemplateId, t: TFunction): Form {
  return {
    id: newSkillId(),
    name: t(`readySkills.templates.${id}.name`),
    kind: 'prompt',
    useWhen: t(`readySkills.templates.${id}.useWhen`),
    description: '',
    instructions: t(`readySkills.templates.${id}.instructions`),
    code: '',
    params: '',
    rows: [],
    onFail: 'retry',
    scope,
    channels: [],
    enabled: true,
    position: 9999,
    config: null,
  }
}

/** A fresh Calendar booking skill, prefilled (in the dashboard's language) — the server's defaults when known. */
function newAppForm(scope: string | null, app: AppCatalogEntry, t: TFunction): Form {
  return {
    id: newSkillId(),
    name: t('ai.wizard.appDefaults.name'),
    kind: 'app',
    useWhen: t('ai.wizard.appDefaults.useWhen'),
    description: t('ai.wizard.appDefaults.description'),
    instructions: t('ai.wizard.appDefaults.instructions'),
    code: '',
    params: '',
    rows: [],
    onFail: 'retry',
    scope,
    channels: [],
    enabled: true,
    position: 9999,
    config: calendarConfig({}, app.defaults),
  }
}

export function SkillWizard({
  init,
  defaultScope,
  readOnly = false,
  orgTimezone,
  onClose,
  onSaved,
}: {
  init: WizardInit
  defaultScope: string | null
  readOnly?: boolean
  /** The organisation's timezone (shown as the app skills' default). */
  orgTimezone?: string
  onClose: () => void
  onSaved: (message: string) => void
}) {
  const t = useT()
  const editing = init.mode === 'edit'
  const original = init.mode === 'edit' ? init.skill : null
  // "New connected app" opens straight on Set up — there is nothing to describe;
  // a ready-made app or an instruction template from the gallery likewise
  const readyInit = init.mode === 'new' && (!!init.app || !!init.template)
  const directApp = init.mode === 'new' && (init.kindHint === 'app' || !!init.app)

  const [step, setStep] = useState<Step>(editing || directApp || readyInit ? 'review' : 'describe')
  const [description, setDescription] = useState('')
  const [hint, setHint] = useState<Hint>(init.mode === 'new' && init.kindHint !== 'app' ? init.kindHint ?? '' : '')
  const [qa, setQa] = useState<{ question: string; answer: string }[]>([])
  const [round, setRound] = useState(0) // index in qa where the current batch starts
  const [form, setForm] = useState<Form | null>(
    original
      ? formFromSkill(original)
      : init.mode === 'new' && init.app
        ? newToolAppForm(defaultScope, init.app, t)
        : init.mode === 'new' && init.template
          ? templateForm(defaultScope, init.template, t)
          : directApp
            ? newAppForm(defaultScope, calendarApp(null), t)
            : null,
  )
  // the describe step's "a code skill can't do this — use the ready-made app" offer
  const [suggestApp, setSuggestApp] = useState<ToolAppId | null>(null)
  const [toolApps, setToolApps] = useState<ToolAppCatalogEntry[] | null>(null)
  const [note, setNote] = useState<{ text: string; source: string } | null>(null)
  const [busy, setBusy] = useState<false | 'draft' | 'test' | 'save'>(false)
  const [error, setError] = useState<string | null>(null)
  const [run, setRun] = useState<TestResponse | null>(null)
  const [stale, setStale] = useState(true)

  // The connected-app catalog (Google connection status, defaults) — loaded
  // only once an app skill is on screen; offline → the built-in fallback.
  const isApp = form?.kind === 'app'
  const [apps, setApps] = useState<{ state: 'idle' | 'loading' | 'ok' | 'offline'; list: AppCatalogEntry[] | null }>({
    state: 'idle',
    list: null,
  })
  const appsRequested = useRef(false)
  const mounted = useRef(true)
  useEffect(() => {
    mounted.current = true
    return () => {
      mounted.current = false
    }
  }, [])
  useEffect(() => {
    // once per wizard: a ref, not the state, guards it — flipping the state to
    // "loading" must not cancel the request it just started
    if (!isApp || appsRequested.current) return
    appsRequested.current = true
    fetchToolApps().then((list) => mounted.current && setToolApps(list))
    setApps({ state: 'loading', list: null })
    fetchApps().then((list) => {
      if (!mounted.current) return
      setApps({ state: list ? 'ok' : 'offline', list })
      // a brand-new, untouched skill takes the server's defaults
      const untouched = JSON.stringify(calendarConfig({}))
      if (list && !editing)
        setForm((f) =>
          f && f.kind === 'app' && JSON.stringify(f.config) === untouched
            ? { ...f, config: calendarConfig({}, calendarApp(list).defaults) }
            : f,
        )
    })
  }, [isApp, editing])

  const startApp = () => {
    setError(null)
    setNote(null)
    setForm(newAppForm(defaultScope, calendarApp(apps.list), t))
    setStep('review')
  }

  const startToolApp = (app: ToolAppId) => {
    setError(null)
    setNote(null)
    setSuggestApp(null)
    setForm(newToolAppForm(defaultScope, app, t))
    setStep('review')
  }

  // Any edit to code/tests invalidates the last run.
  const patch = (p: Partial<Form>) => {
    setForm((f) => (f ? { ...f, ...p } : f))
    if ('code' in p || 'rows' in p || 'kind' in p) setStale(true)
  }

  const isCode = form ? isCodeKind(form.kind) : hint !== 'prompt'

  // ---- describe / clarify ----------------------------------------------------

  const draft = async (answers: { question: string; answer: string }[]) => {
    setBusy('draft')
    setError(null)
    try {
      const d = await aicore<DraftResponse>('skills/draft', {
        method: 'POST',
        body: JSON.stringify({ description, kind_hint: hint || null, answers }),
      })
      setSuggestApp(null)
      if (d.status === 'use_app') {
        // a code skill can't make files or reach the internet — offer the app that can
        setNote(d.note ? { text: d.note, source: d.source } : null)
        setSuggestApp(d.suggest_app ?? null)
        setStep('describe')
        return
      }
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
        rows: isCodeKind(kind) ? testsToRows(kind, s.tests ?? []) : [],
        onFail: s.on_fail || 'retry',
        scope: defaultScope,
        channels: [],
        enabled: true,
        position: 9999,
        config: null,
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
    if (!form || !isCodeKind(form.kind)) return { tests: [] as unknown[], error: null as string | null }
    return rowsToTests(form.kind, form.rows, t)
  }

  const paramsPayload = (): { value: unknown; error: string | null } => {
    if (!form || form.kind !== 'tool' || !form.params.trim()) return { value: null, error: null }
    try {
      const v = JSON.parse(form.params)
      if (!v || typeof v !== 'object' || Array.isArray(v))
        return { value: null, error: t('ai.wizard.paramsObject') }
      return { value: v, error: null }
    } catch {
      return { value: null, error: t('ai.wizard.paramsJson') }
    }
  }

  const runTests = async () => {
    if (!form || !isCodeKind(form.kind)) return
    const tp = testsPayload()
    const p = paramsPayload()
    if (tp.error || p.error) {
      setError(tp.error || p.error)
      return
    }
    setBusy('test')
    setError(null)
    setStep('test')
    try {
      const r = await aicore<TestResponse>('skills/test', {
        method: 'POST',
        body: JSON.stringify({ kind: form.kind, code: form.code, tests: tp.tests }),
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
      setError(t('ai.wizard.nameRequired'))
      return
    }
    const tp = testsPayload()
    const p = paramsPayload()
    const appProblem =
      form.kind === 'app' && form.toolConfig
        ? toolAppProblem(form.toolConfig, t)
        : form.kind === 'app' && form.config
          ? calendarConfigProblem(form.config, t)
          : null
    if (tp.error || p.error || appProblem) {
      setError(tp.error || p.error || appProblem)
      return
    }
    setBusy('save')
    setError(null)
    try {
      const code = isCodeKind(form.kind)
      await saveSkill(
        {
          id: form.id,
          name,
          useWhen: form.useWhen,
          instructions: code ? '' : form.instructions,
          position: form.position,
          kind: form.kind,
          description: form.description,
          code: code ? form.code : '',
          parameters: p.value,
          tests: tp.tests,
          enabled,
          channels: form.channels,
          onFail: form.onFail,
          ...(form.kind === 'app' && form.config ? { config: { ...form.config } } : {}),
          ...(form.kind === 'app' && form.toolConfig ? { config: { ...form.toolConfig } } : {}),
        },
        form.scope,
        original ? original.entity ?? null : undefined,
      )
      onSaved(
        enabled
          ? t('ai.wizard.savedOn', { name })
          : code
            ? t('ai.wizard.savedOffCode', { name })
            : t('ai.wizard.savedOff', { name }),
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
    if (isApp) {
      // an app is configured, not drafted: Describe (where it was picked) → Set up
      if (!editing && !directApp) out.push({ id: 'describe', label: t('ai.wizard.steps.describe') })
      out.push({ id: 'review', label: t('ai.wizard.steps.setUp') })
      return out
    }
    if (!editing && !readyInit)
      out.push(
        { id: 'describe', label: t('ai.wizard.steps.describe') },
        { id: 'clarify', label: t('ai.wizard.steps.clarify') },
      )
    out.push({ id: 'review', label: isCode ? t('ai.wizard.steps.reviewCode') : t('ai.wizard.steps.review') })
    if (isCode) out.push({ id: 'test', label: t('ai.wizard.steps.runTests') })
    return out
  }, [editing, isCode, isApp, directApp, readyInit, t])

  const title = editing
    ? readOnly
      ? form?.name ?? t('ai.wizard.skillFallback')
      : t('ai.wizard.editTitle', { name: original?.name ?? '' })
    : isApp && step === 'review'
      ? t('ai.wizard.newApp')
      : t('ai.wizard.newSkill')
  const subtitle: Record<Step, string> = {
    describe: t('ai.wizard.subtitles.describe'),
    clarify: t('ai.wizard.subtitles.clarify'),
    review: isApp
      ? t('ai.wizard.subtitles.reviewApp')
      : isCode
        ? t('ai.wizard.subtitles.reviewCode')
        : t('ai.wizard.subtitles.reviewPrompt'),
    test: t('ai.wizard.subtitles.test'),
  }

  const footer = (() => {
    if (readOnly)
      return (
        <Button variant="secondary" onClick={onClose}>
          {t('common.actions.close')}
        </Button>
      )
    const spinner = <Loader2 size={13} className="animate-spin" aria-hidden />
    const spin = busy === 'draft' && spinner
    if (step === 'describe')
      return (
        <>
          <Button variant="ghost" onClick={onClose}>
            {t('common.actions.cancel')}
          </Button>
          <Button onClick={() => draft([])} disabled={!!busy || description.trim().length < 4}>
            {spin || <Sparkles size={13} aria-hidden />} {t('common.actions.continue')}
          </Button>
        </>
      )
    if (step === 'clarify') {
      const current = qa.slice(round)
      return (
        <>
          <Button variant="ghost" onClick={() => setStep('describe')} disabled={!!busy}>
            <ArrowLeft size={13} aria-hidden /> {t('common.actions.back')}
          </Button>
          <Button
            variant="secondary"
            disabled={!!busy}
            onClick={() => draft(qa.map((x) => ({ ...x, answer: x.answer.trim() || t('ai.wizard.noPreference') })))}
          >
            {t('ai.wizard.skipDraft')}
          </Button>
          <Button onClick={() => draft(qa)} disabled={!!busy || current.some((x) => !x.answer.trim())}>
            {spin} {t('common.actions.continue')} <ArrowRight size={13} aria-hidden />
          </Button>
        </>
      )
    }
    if (step === 'review')
      return (
        <>
          {!editing && !(isApp && directApp) && !readyInit && (
            <Button
              variant="ghost"
              onClick={() => setStep(qa.length && !isApp ? 'clarify' : 'describe')}
              disabled={!!busy}
            >
              <ArrowLeft size={13} aria-hidden /> {t('common.actions.back')}
            </Button>
          )}
          <Button variant="ghost" onClick={onClose} disabled={!!busy}>
            {t('common.actions.cancel')}
          </Button>
          {isCode ? (
            <Button onClick={runTests} disabled={!!busy}>
              {busy === 'test' ? spinner : <Play size={13} aria-hidden />} {t('ai.wizard.runTests')}
            </Button>
          ) : (
            <Button onClick={() => save(form?.enabled ?? true)} disabled={!!busy}>
              {busy === 'save' ? spinner : <Check size={13} aria-hidden />} {t('common.actions.save')}
            </Button>
          )}
        </>
      )
    return (
      <>
        <Button variant="ghost" onClick={() => setStep('review')} disabled={!!busy}>
          <ArrowLeft size={13} aria-hidden /> {t('ai.wizard.backToCode')}
        </Button>
        <Button variant="secondary" onClick={() => save(false)} disabled={!!busy}>
          {t('ai.wizard.saveDisabled')}
        </Button>
        <Button
          onClick={() => save(true)}
          disabled={!!busy || !allPassed}
          title={allPassed ? undefined : t('ai.wizard.mustPass')}
        >
          {busy === 'save' ? spinner : <Check size={13} aria-hidden />} {t('ai.wizard.saveSwitchOn')}
        </Button>
      </>
    )
  })()

  // Review-step fields that move between the essentials and Advanced by kind.
  const kindField = form && (
    <Field label={t('ai.common.kind')}>
      {form.kind === 'app' ? (
        // an app skill holds settings, not code — it can't become another kind
        <div className="flex h-[38px] items-center gap-2 rounded-lg border border-line bg-fill-subtle px-3 text-[13px] text-text-high">
          <CalendarClock size={14} className="shrink-0 text-text-muted" aria-hidden />
          <span className="truncate">
            {t(KIND_META.app.label)} ·{' '}
            <span className="text-text-muted">
              {form.toolConfig ? t(readyApp(form.toolConfig.app).name) : appLabel(calendarApp(apps.list), t)}
            </span>
          </span>
        </div>
      ) : (
        <Select
          value={form.kind}
          disabled={readOnly}
          onChange={(v) => {
            const next = v as SkillKind
            const cur = form.kind
            if (next === cur) return
            // tool ↔ check: carry the test cases across in the new shape
            const carried =
              isCodeKind(cur) && isCodeKind(next) ? testsToRows(next, rowsToTests(cur, form.rows, t).tests) : form.rows
            patch({ kind: next, rows: isCodeKind(next) ? carried : [] })
          }}
          size="md"
          ariaLabel={t('ai.common.kind')}
          options={(['prompt', 'tool', 'check'] as SkillKind[]).map((k) => ({
            value: k,
            label: t(KIND_META[k].label),
            hint: k === 'prompt' ? t('ai.wizard.noCode') : undefined,
          }))}
        />
      )}
    </Field>
  )
  const channelsField = form && (
    <Field label={t('ai.common.channels')}>
      <MultiSelect
        value={form.channels}
        disabled={readOnly}
        onChange={(channels) => patch({ channels })}
        options={channelOptions(t)}
        placeholder={t('ai.common.allChannels')}
        size="md"
        ariaLabel={t('ai.common.channels')}
      />
    </Field>
  )
  const argsField = form && (
    <Field label={t('ai.wizard.args')} hint={t('ai.wizard.argsHint')}>
      <CodeEditor
        value={form.params}
        readOnly={readOnly}
        minRows={4}
        maxRows={12}
        ariaLabel={t('ai.wizard.argsAria')}
        onChange={(v) => patch({ params: v })}
      />
    </Field>
  )
  const testsField = form && (
    <Field label={t('ai.wizard.testCases')}>
      <TestCasesEditor
        kind={form.kind === 'check' ? 'check' : 'tool'}
        rows={form.rows}
        readOnly={readOnly}
        results={stale ? null : run?.results}
        onChange={(rows) => patch({ rows })}
      />
    </Field>
  )

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
            <span className="mb-1.5 block text-[12px] font-semibold text-text-high">{t('ai.wizard.whatShould')}</span>
            <textarea
              rows={4}
              value={description}
              autoFocus
              onChange={(e) => setDescription(e.target.value)}
              placeholder={t('ai.wizard.describePlaceholder')}
              className={`${input} resize-y leading-[1.6]`}
            />
          </label>
          {suggestApp && (
            // "create a PDF / invoice / QR…": code can't — the ready-made app can
            <div role="status" className="flex flex-wrap items-start gap-3 rounded-lg border border-line bg-fill-subtle px-3 py-2.5">
              <Info size={14} className="mt-0.5 shrink-0 text-text-muted" aria-hidden />
              <span className="min-w-0 flex-1 text-[12.5px] leading-relaxed text-text-mid">{note?.text ?? t('readySkills.gallery.codeLimit')}</span>
              <Button size="sm" onClick={() => startToolApp(suggestApp)}>
                {t('ai.wizard.setUp')} · {t(readyApp(suggestApp).name)} <ArrowRight size={13} aria-hidden />
              </Button>
            </div>
          )}
          <div className="flex flex-wrap gap-1.5">
            <span className="mr-1 self-center text-[11.5px] text-text-muted">{t('ai.wizard.try')}</span>
            {EXAMPLES.map((x) => (
              <button
                key={x.text}
                type="button"
                onClick={() => {
                  setDescription(t(x.text))
                  setHint(x.kind)
                }}
                className="rounded-full border border-line px-2.5 py-1 text-left text-[11.5px] text-text-mid transition-colors hover:border-line-strong hover:bg-fill-hover hover:text-text-high"
              >
                {t(x.text)}
              </button>
            ))}
          </div>
          <fieldset>
            <legend className="eyebrow mb-2">{t('ai.wizard.kindOfSkill')}</legend>
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
                      <Icon size={13} aria-hidden /> {t(label)}
                    </span>
                    <span className="text-[11.5px] leading-snug text-text-muted">{t(blurb)}</span>
                  </button>
                )
              })}
            </div>
            {/* the 4th kind — configured, not described, so it skips the drafting */}
            <button
              type="button"
              onClick={startApp}
              className="mt-2 flex w-full items-center gap-3 rounded-lg border border-line px-3 py-2.5 text-left transition-colors hover:border-line-strong hover:bg-fill-subtle"
            >
              <span className="grid h-8 w-8 shrink-0 place-items-center rounded-lg border border-line bg-card text-text-high" aria-hidden>
                <CalendarClock size={15} />
              </span>
              <span className="min-w-0 flex-1">
                <span className="block text-[12.5px] font-semibold text-text-heading">{t(KIND_META.app.label)}</span>
                <span className="block text-[11.5px] leading-snug text-text-muted">{t('ai.wizard.connectedAppBlurb')}</span>
              </span>
              <span className="inline-flex shrink-0 items-center gap-1 text-[12px] font-semibold text-text-mid">
                {t('ai.wizard.setUp')} <ArrowRight size={13} aria-hidden />
              </span>
            </button>
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
            <Field label={t('common.fields.name')}>
              <input
                value={form.name}
                disabled={readOnly}
                onChange={(e) => patch({ name: e.target.value })}
                className={input}
              />
              {form.kind === 'tool' && (
                <span className="mt-1 block text-[11px] text-text-muted">
                  {t('ai.wizard.callsAs')}{' '}
                  <code className="font-mono text-text-mid">
                    {form.name.toLowerCase().replace(/[^a-z0-9]+/g, '_').replace(/^_+|_+$/g, '') || 'skill'}()
                  </code>
                </span>
              )}
            </Field>
            {isCodeKind(form.kind) && kindField}
            <Field label={t('ai.wizard.useWhen')} hint={t('ai.wizard.useWhenHint')}>
              <input
                value={form.useWhen}
                disabled={readOnly}
                placeholder={
                  form.kind === 'check'
                    ? t('ai.wizard.useWhenCheck')
                    : form.kind === 'app'
                      ? t('ai.wizard.useWhenApp')
                      : t('ai.wizard.useWhenOther')
                }
                onChange={(e) => patch({ useWhen: e.target.value })}
                className={input}
              />
            </Field>
            <Field label={t('common.fields.description')} hint={t('ai.wizard.descriptionHint')}>
              <input
                value={form.description}
                disabled={readOnly}
                onChange={(e) => patch({ description: e.target.value })}
                className={input}
              />
            </Field>
          </div>

          {form.kind === 'prompt' ? (
            <Field label={t('ai.wizard.instructions')}>
              <textarea
                rows={7}
                value={form.instructions}
                disabled={readOnly}
                placeholder={t('ai.wizard.instructionsPlaceholder')}
                onChange={(e) => patch({ instructions: e.target.value })}
                className={`${input} resize-y leading-[1.6]`}
              />
            </Field>
          ) : form.kind === 'app' ? (
            <>
              <Field label={t('ai.wizard.instructions')} hint={t('ai.wizard.appInstructionsHint')}>
                <textarea
                  rows={4}
                  value={form.instructions}
                  disabled={readOnly}
                  onChange={(e) => patch({ instructions: e.target.value })}
                  className={`${input} resize-y leading-[1.6]`}
                />
              </Field>
              {form.toolConfig && (
                <div className="border-t border-line pt-4">
                  <ToolAppForm
                    config={form.toolConfig}
                    onChange={(toolConfig) => patch({ toolConfig })}
                    entry={toolApps?.find((a) => a.app === form.toolConfig?.app) ?? null}
                    readOnly={readOnly}
                  />
                </div>
              )}
              {form.config && (
                <div className="border-t border-line pt-4">
                  <AppSkillForm
                    config={form.config}
                    onChange={(config) => patch({ config })}
                    app={calendarApp(apps.list)}
                    appsState={apps.state === 'ok' ? 'ok' : apps.state === 'offline' ? 'offline' : 'loading'}
                    orgTimezone={orgTimezone}
                    readOnly={readOnly}
                  />
                </div>
              )}
            </>
          ) : (
            <>
              <Field
                label={t('ai.wizard.code')}
                hint={form.kind === 'tool' ? t('ai.wizard.codeHintTool') : t('ai.wizard.codeHintCheck')}
              >
                <CodeEditor
                  value={form.code}
                  readOnly={readOnly}
                  onChange={(v) => patch({ code: v })}
                  ariaLabel={t('ai.wizard.codeAria')}
                />
              </Field>
              {form.kind === 'check' && (
                <Field label={t('ai.wizard.whenFails')}>
                  <Select
                    value={form.onFail}
                    disabled={readOnly}
                    onChange={(v) => patch({ onFail: v as OnFail })}
                    size="md"
                    ariaLabel={t('ai.wizard.whenFails')}
                    options={ON_FAIL_OPTIONS.map((o) => ({ value: o.value, label: t(o.label), hint: t(o.hint) }))}
                  />
                </Field>
              )}
            </>
          )}

          <div className="grid grid-cols-1 gap-4 border-t border-line pt-4 md:grid-cols-[1fr_auto]">
            <Field label={t('ai.wizard.whoUses')}>
              <EntityPicker value={form.scope} disabled={readOnly} onChange={(scope) => patch({ scope })} />
            </Field>
            {!isCodeKind(form.kind) && (
              <Field label={t('common.status.active')}>
                <div className="flex h-[38px] items-center gap-2">
                  <Toggle
                    on={form.enabled}
                    disabled={readOnly}
                    onChange={(enabled) => patch({ enabled })}
                    label={t('ai.wizard.skillActive')}
                  />
                  <span className="text-[12px] text-text-muted">
                    {form.enabled ? t('common.status.on') : t('common.status.off')}
                  </span>
                </div>
              </Field>
            )}
          </div>

          {isCodeKind(form.kind) ? (
            <Advanced key="code" forceOpen>
              {form.kind === 'tool' && argsField}
              {testsField}
              {channelsField}
            </Advanced>
          ) : (
            <Advanced key="plain" count={form.channels.length ? 1 : 0} forceOpen={form.channels.length > 0}>
              {kindField}
              {channelsField}
            </Advanced>
          )}
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
  const t = useT()
  const f = useFormat()
  if (busy || !run)
    return (
      <div className="flex items-center gap-2 py-10 text-[13px] text-text-muted">
        <Loader2 size={15} className="animate-spin" aria-hidden /> {t('ai.wizard.running', { count: rows })}
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
              ? t('ai.wizard.notRun')
              : run.total === 0
                ? t('ai.wizard.noCases')
                : ok
                  ? t('ai.wizard.allPass', { count: run.total })
                  : t('ai.wizard.failOf', { failed: run.failed, count: run.total })}
          </div>
          <div className="text-[12px] text-text-mid">
            {!run.valid
              ? run.error
              : run.total === 0
                ? t('ai.wizard.addOne')
                : ok
                  ? t('ai.wizard.ready')
                  : t('ai.wizard.goBack')}
          </div>
        </div>
        <Button variant="secondary" size="sm" onClick={onRerun}>
          <FlaskConical size={13} aria-hidden /> {t('ai.wizard.runAgain')}
        </Button>
      </div>

      {run.results.length > 0 && (
        <ul className="divide-y divide-line rounded-lg border border-line">
          {run.results.map((r, i) => (
            <li key={i} className="flex flex-wrap items-start gap-x-3 gap-y-1 px-4 py-2.5">
              {r.pass ? (
                <CheckCircle2 size={14} className="mt-0.5 text-green-300" aria-label={t('ai.wizard.passed')} />
              ) : (
                <XCircle size={14} className="mt-0.5 text-red-300" aria-label={t('ai.wizard.failed')} />
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
                          <span className="text-text-faint">{t('ai.wizard.expected')} </span>
                          {JSON.stringify(r.expected)}
                        </span>
                        <span className="break-words">
                          <span className="text-text-faint">{t('ai.wizard.got')} </span>
                          {JSON.stringify(r.got)}
                        </span>
                      </>
                    )}
                  </div>
                )}
              </div>
              <span className="font-mono text-[11px] text-text-faint">
                {t('ai.wizard.ms', { ms: f.number(r.ms, { minimumFractionDigits: 1, maximumFractionDigits: 1 }) })}
              </span>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
