'use client'

/**
 * AI review — "AI to monitor AI". On demand only: an admin clicks Review now,
 * a stronger model (Settings → AI keys & models → AI reviewer model) checks a
 * sample of the AI's recent replies and suggests changes to the team's setup.
 * The "Reviewer model" picker (GET /api/ai/review/options) preselects that
 * default; another configured model is sent as `model` for that run only, and
 * a settings.ai_keys.manage holder can "Make this the default" (PUT
 * /api/settings/ai-providers `ai_reviewer_model`).
 * Shows the reviewer that would run, the latest (or picked) run's summary and
 * suggestions, its findings (issue cards with one-click "Add as lesson / to
 * knowledge / house rule"), and past runs. A run that is still going is
 * polled every few seconds. Errors render inside the card.
 */

import { useCallback, useEffect, useMemo, useState } from 'react'
import Link from 'next/link'
import { AlertCircle, CheckCircle2, ExternalLink, Loader2, Sparkles } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Card, CardTitle } from '@/components/ui/Card'
import { Advanced } from '@/components/ui/Advanced'
import { Modal } from '@/components/ui/Modal'
import { EntityPicker } from '@/components/ui/EntityPicker'
import { useEntity } from '@/lib/EntityContext'
import { useChannelLabel } from '@/components/ui/ChannelLogo'
import { hasCap } from '@/lib/auth'
import { Select, type SelectOption } from '@/components/ui/Select'
import {
  parseReviewerModel,
  reviewApi,
  type ApplyAs,
  type ReviewFinding,
  type ReviewRun,
  type ReviewRunDetail,
  type Reviewer,
  type ReviewerOptions,
} from '@/lib/aiReview'
import { useFormat, useT, type MessageKey } from '@/i18n'

const SEVERITY_CLS: Record<string, string> = {
  high: 'bg-red/10 text-[color:var(--red-ink)] border-red/30',
  medium: 'bg-amber/10 text-[color:var(--amber-ink)] border-amber/30',
  low: 'bg-fill text-text-mid border-line',
}

const STATUS_KEY: Record<string, MessageKey> = {
  auto_sent: 'ai.draftStatus.autoSent',
  sent: 'ai.draftStatus.sent',
  approved: 'ai.draftStatus.approved',
  edited: 'ai.draftStatus.edited',
  rejected: 'ai.draftStatus.rejected',
}

const inputCls =
  'h-9 w-full rounded-lg border border-line-strong bg-card px-3 text-[12.5px] text-text-primary outline-none transition-colors focus:border-brand-400'

const isoDay = (d: Date) => d.toISOString().slice(0, 10)

/** The picker's "Custom…" entry (never a real model value: those carry a ':'). */
const CUSTOM = '__custom__'

function ErrorLine({ text }: { text: string }) {
  return (
    <div
      role="alert"
      className="flex items-start gap-2 rounded-lg border border-red/30 bg-red/10 px-3 py-2 text-[12px]"
      style={{ color: 'var(--red-ink)' }}
    >
      <AlertCircle size={13} className="mt-0.5 shrink-0" aria-hidden /> {text}
    </div>
  )
}

export function AiReviewPanel({ entityQuery }: { entityQuery: string }) {
  const t = useT()
  const f = useFormat()
  const channelLabel = useChannelLabel()
  const [canRun, setCanRun] = useState(false)
  const [canKnowledge, setCanKnowledge] = useState(false)
  const [runs, setRuns] = useState<ReviewRun[]>([])
  const [reviewer, setReviewer] = useState<Reviewer | null>(null)
  const [detail, setDetail] = useState<ReviewRunDetail | null>(null)
  const [selected, setSelected] = useState<number | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [runError, setRunError] = useState<string | null>(null)
  const [starting, setStarting] = useState(false)
  const [days, setDays] = useState<7 | 30 | 0>(7)
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const [limit, setLimit] = useState('')
  const [issuesOnly, setIssuesOnly] = useState(true)
  const [applying, setApplying] = useState<number | null>(null)
  const [applyMsg, setApplyMsg] = useState<Record<number, { ok: boolean; text: string }>>({})
  // the confirm dialog: what is added, as what, at which level of the tree
  const { entities } = useEntity()
  const [confirm, setConfirm] = useState<{ finding: ReviewFinding; as: ApplyAs; text: string; level: string | null } | null>(
    null,
  )
  const [confirmError, setConfirmError] = useState<string | null>(null)
  // the reviewer model for the next run: a suggestion ("provider:model"), or CUSTOM + free text
  const [modelOpts, setModelOpts] = useState<ReviewerOptions | null>(null)
  const [modelError, setModelError] = useState<string | null>(null)
  const [pick, setPick] = useState('')
  const [customModel, setCustomModel] = useState('')
  const [savingDefault, setSavingDefault] = useState(false)
  const [defaultMsg, setDefaultMsg] = useState<{ ok: boolean; text: string } | null>(null)

  // capabilities are read after mount (hydration-safe)
  useEffect(() => {
    setCanRun(hasCap('ai.behaviour.manage'))
    setCanKnowledge(hasCap('settings.ai.manage'))
  }, [])

  const loadModels = useCallback(async () => {
    try {
      const o = await reviewApi.options()
      setModelOpts(o)
      setModelError(null)
      // the default is preselected (and stays the pick until the person changes it)
      setPick((cur) => cur || o.default.value)
      return o
    } catch (e) {
      setModelError((e as Error).message)
      return null
    }
  }, [])

  useEffect(() => {
    if (canRun) void loadModels()
  }, [canRun, loadModels])

  const modelOptions = useMemo<SelectOption[]>(() => {
    if (!modelOpts) return []
    const out: SelectOption[] = []
    for (const p of modelOpts.providers) {
      const group = p.configured ? p.label : t('ai.reviewer.model.notConfigured', { provider: p.label })
      for (const m of p.models) {
        out.push({
          value: m.value,
          label: m.model,
          hint: m.is_default ? t('ai.reviewer.model.defaultTag') : undefined,
          group,
          disabled: !p.configured,
        })
      }
    }
    out.push({ value: CUSTOM, label: t('ai.reviewer.model.custom'), group: t('ai.reviewer.model.customGroup') })
    return out
  }, [modelOpts, t])

  // what the next run sends: undefined = the default (the server resolves it, fallbacks included)
  const chosen = pick === CUSTOM ? customModel.trim() : pick
  const isDefault = !!modelOpts && chosen === modelOpts.default.value
  const chosenProvider = chosen ? parseReviewerModel(chosen).provider : ''
  const chosenProviderInfo = modelOpts?.providers.find((p) => p.id === chosenProvider)
  // a custom id on a provider with no key: the server would refuse it (400) — say so first
  const chosenMissing = !!modelOpts && !!chosen && !isDefault && chosenProviderInfo?.configured !== true
  const someMissing = !!modelOpts && modelOpts.providers.some((p) => !p.configured)
  const customEmpty = pick === CUSTOM && chosen === ''

  async function makeDefault() {
    if (!chosen || chosenMissing) return
    setSavingDefault(true)
    setDefaultMsg(null)
    try {
      await reviewApi.setDefault(chosen)
      const o = await loadModels()
      if (o) setPick(o.default.value)
      setCustomModel('')
      setDefaultMsg({ ok: true, text: t('ai.reviewer.model.savedDefault') })
      void loadList()
    } catch (e) {
      setDefaultMsg({ ok: false, text: (e as Error).message })
    } finally {
      setSavingDefault(false)
    }
  }

  const loadList = useCallback(async () => {
    try {
      const d = await reviewApi.list(entityQuery)
      setRuns(d.runs)
      setReviewer(d.reviewer)
      setLoadError(null)
      return d
    } catch (e) {
      setLoadError((e as Error).message)
      return null
    }
  }, [entityQuery])

  const loadDetail = useCallback(async (id: number) => {
    try {
      const d = await reviewApi.get(id)
      setDetail(d.run)
      setLoadError(null)
    } catch (e) {
      setLoadError((e as Error).message)
    }
  }, [])

  // first load + whenever the entity view changes: the latest run opens
  useEffect(() => {
    setDetail(null)
    setSelected(null)
    void loadList().then((d) => {
      if (d && d.runs.length > 0) setSelected(d.runs[0].id)
    })
  }, [loadList])

  useEffect(() => {
    if (selected != null) void loadDetail(selected)
  }, [selected, loadDetail])

  // a run still going: poll it (and the list) until it finishes
  const running = detail?.status === 'running'
  useEffect(() => {
    if (!running || detail == null) return
    const id = detail.id
    const timer = window.setInterval(() => {
      void loadDetail(id)
      void loadList()
    }, 4000)
    return () => window.clearInterval(timer)
  }, [running, detail, loadDetail, loadList])

  const max = reviewer?.max_limit ?? 100
  const size = Math.min(max, Math.max(1, Number(limit) || reviewer?.default_limit || 30))
  const customised = (days === 0 ? 1 : 0) + (limit.trim() !== '' ? 1 : 0)

  async function start() {
    setRunError(null)
    setStarting(true)
    try {
      const entity = new URLSearchParams(entityQuery).get('entity') || undefined
      // the default is not sent: the server resolves it (and its fallback) itself
      const model = chosen && !isDefault ? chosen : undefined
      const body =
        days === 0
          ? { from: from || undefined, to: to || undefined, entity, limit: size, model }
          : { days, entity, limit: size, model }
      const d = await reviewApi.start(body)
      setDetail(d.run)
      setSelected(d.run.id)
      setIssuesOnly(true)
      void loadList()
    } catch (e) {
      setRunError((e as Error).message)
    } finally {
      setStarting(false)
    }
  }

  // Opens the confirm dialog — nothing is added until the person checks the
  // wording and the level. Default level: the conversation's own entity.
  function apply(finding: ReviewFinding, as: ApplyAs) {
    const own = finding.entity_id != null ? entities.find((e) => e.id === finding.entity_id)?.slug ?? null : null
    setConfirmError(null)
    setConfirm({ finding, as, text: finding.fix.text, level: own })
  }

  async function confirmApply() {
    if (!confirm) return
    const { finding, as, text, level } = confirm
    if (!text.trim()) {
      setConfirmError(t('ai.reviewer.confirm.empty'))
      return
    }
    setApplying(finding.id)
    setConfirmError(null)
    try {
      const r = await reviewApi.apply(finding.id, as, text.trim(), level ?? '')
      setDetail((cur) =>
        cur ? { ...cur, findings: cur.findings.map((x) => (x.id === finding.id ? r.finding : x)) } : cur,
      )
      setApplyMsg((m) => ({ ...m, [finding.id]: { ok: true, text: r.result?.message || t('ai.reviewer.added') } }))
      setConfirm(null)
    } catch (e) {
      // the error stays INSIDE the dialog (a page-level line would sit under the scrim)
      setConfirmError((e as Error).message)
    } finally {
      setApplying(null)
    }
  }

  const findings = useMemo(
    () => (detail ? (issuesOnly ? detail.findings.filter((x) => x.verdict === 'issue') : detail.findings) : []),
    [detail, issuesOnly],
  )
  const modelName = (r: { provider: string; model: string }) => (r.model ? r.model : r.provider)

  return (
    <div className="flex flex-col gap-4">
      {/* ---- start a review ------------------------------------------------------ */}
      <Card className="p-4 sm:p-5" data-tour="ai-review-start">
        <div className="flex items-start gap-3">
          <span className="grid h-8 w-8 shrink-0 place-items-center rounded-lg bg-fill">
            <Sparkles size={15} className="text-text-mid" aria-hidden />
          </span>
          <div className="min-w-0 flex-1">
            <h2 className="display text-[18px] leading-tight text-text-heading">{t('ai.reviewer.intro')}</h2>
            <p className="mt-1 max-w-[720px] text-[12.5px] leading-relaxed text-text-muted">{t('ai.reviewer.introMore')}</p>
          </div>
        </div>

        {loadError && (
          <div className="mt-3">
            <ErrorLine text={t('ai.reviewer.loadError', { error: loadError })} />
          </div>
        )}

        {canRun ? (
          <div className="mt-4 flex flex-col gap-3">
            <div className="flex flex-wrap items-center gap-2">
              <span className="text-[12px] font-semibold text-text-high">{t('ai.reviewer.period')}</span>
              {([7, 30] as const).map((n) => (
                <button
                  key={n}
                  type="button"
                  aria-pressed={days === n}
                  onClick={() => setDays(n)}
                  className={`h-8 rounded-lg border px-3 text-[12.5px] font-semibold transition-colors ${
                    days === n ? 'border-text-primary bg-fill text-text-heading' : 'border-line-strong bg-card text-text-mid hover:bg-fill-hover'
                  }`}
                >
                  {n === 7 ? t('ai.reviewer.last7') : t('ai.reviewer.last30')}
                </button>
              ))}
              <Button className="ml-auto" onClick={start} disabled={starting || running || chosenMissing || customEmpty}>
                {starting || running ? <Loader2 size={14} className="animate-spin" aria-hidden /> : <Sparkles size={14} aria-hidden />}
                {starting || running ? t('ai.reviewer.running') : t('ai.reviewer.reviewNow')}
              </Button>
            </div>
            {/* ---- the reviewer model: the default preselected, any configured one for this run ---- */}
            {modelOpts && (
              <div className="flex flex-col gap-1.5" data-tour="ai-review-model">
                <div className="flex flex-wrap items-center gap-2">
                  <span className="text-[12px] font-semibold text-text-high">{t('ai.reviewer.model.label')}</span>
                  <Select
                    value={pick}
                    onChange={(v) => {
                      setPick(v)
                      setDefaultMsg(null)
                    }}
                    options={modelOptions}
                    ariaLabel={t('ai.reviewer.model.label')}
                    className="w-full sm:w-[320px]"
                    searchable
                  />
                  {pick === CUSTOM && (
                    <input
                      value={customModel}
                      onChange={(e) => {
                        setCustomModel(e.target.value)
                        setDefaultMsg(null)
                      }}
                      maxLength={modelOpts.max_chars}
                      placeholder={t('ai.reviewer.model.customPlaceholder')}
                      aria-label={t('ai.reviewer.model.customPlaceholder')}
                      className={`${inputCls} sm:w-[300px]`}
                    />
                  )}
                  {modelOpts.can_set_default && !isDefault && chosen !== '' && !chosenMissing && (
                    <Button size="sm" variant="ghost" onClick={makeDefault} disabled={savingDefault}>
                      {savingDefault ? t('ai.reviewer.model.savingDefault') : t('ai.reviewer.model.makeDefault')}
                    </Button>
                  )}
                </div>
                <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-[11.5px] text-text-muted">
                  {pick === CUSTOM && <span>{t('ai.reviewer.model.customHint')}</span>}
                  {!isDefault && chosen !== '' && !chosenMissing && <span>{t('ai.reviewer.model.thisRunOnly')}</span>}
                  {!modelOpts.can_set_default && (
                    <span>
                      {t('ai.reviewer.model.defaultIs', { model: modelOpts.default.model })}{' '}
                      {t('ai.reviewer.model.defaultWhere')}
                    </span>
                  )}
                  {someMissing && !chosenMissing && <span>{t('ai.reviewer.model.someMissing')}</span>}
                </div>
                {chosenMissing && (
                  <p className="text-[11.5px]" style={{ color: 'var(--amber-ink)' }}>
                    {t('ai.reviewer.model.pickedMissing', { provider: chosenProviderInfo?.label ?? chosenProvider })}
                  </p>
                )}
                {defaultMsg &&
                  (defaultMsg.ok ? (
                    <p className="flex items-center gap-1.5 text-[11.5px]" style={{ color: 'var(--green-ink)' }}>
                      <CheckCircle2 size={12} aria-hidden /> {defaultMsg.text}
                    </p>
                  ) : (
                    <ErrorLine text={defaultMsg.text} />
                  ))}
              </div>
            )}
            {modelError && <ErrorLine text={t('ai.reviewer.model.optionsError', { error: modelError })} />}
            <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-[11.5px] text-text-muted">
              {reviewer && (!modelOpts || isDefault) && (
                <span>
                  {reviewer.mode === 'model'
                    ? t('ai.reviewer.willUse', { model: modelName(reviewer) })
                    : t('ai.reviewer.willUseHeuristic')}
                </span>
              )}
              <span>{t('ai.reviewer.upTo', { count: size })}</span>
              {reviewer?.note && reviewer.mode === 'model' && (isDefault || !modelOpts) && <span>{reviewer.note}</span>}
            </div>
            {(starting || running) && <p className="text-[11.5px] text-text-muted">{t('ai.reviewer.runningHint')}</p>}
            {runError && <ErrorLine text={runError} />}
            <Advanced
              label={t('ai.reviewer.advancedLabel')}
              hint={t('ai.reviewer.advancedHint')}
              count={customised}
              forceOpen={customised > 0}
            >
              <div className="grid gap-3 px-3 pb-3 sm:grid-cols-3">
                <label className="block">
                  <span className="mb-1 block text-[11px] font-bold text-text-high">{t('ai.reviewer.from')}</span>
                  <input
                    type="date"
                    value={from}
                    max={to || isoDay(new Date())}
                    onChange={(e) => {
                      setFrom(e.target.value)
                      setDays(0)
                    }}
                    className={inputCls}
                  />
                </label>
                <label className="block">
                  <span className="mb-1 block text-[11px] font-bold text-text-high">{t('ai.reviewer.to')}</span>
                  <input
                    type="date"
                    value={to}
                    min={from || undefined}
                    max={isoDay(new Date())}
                    onChange={(e) => {
                      setTo(e.target.value)
                      setDays(0)
                    }}
                    className={inputCls}
                  />
                </label>
                <label className="block">
                  <span className="mb-1 block text-[11px] font-bold text-text-high">{t('ai.reviewer.howMany', { max })}</span>
                  <input
                    type="number"
                    min={1}
                    max={max}
                    value={limit}
                    placeholder={String(reviewer?.default_limit ?? 30)}
                    onChange={(e) => setLimit(e.target.value)}
                    className={inputCls}
                  />
                </label>
              </div>
            </Advanced>
          </div>
        ) : (
          <p className="mt-3 text-[12px] text-text-muted">{t('ai.reviewer.noAccessRun')}</p>
        )}
      </Card>

      {/* ---- the picked run ---------------------------------------------------------- */}
      {detail && (
        <Card className="p-4 sm:p-5" data-tour="ai-review-summary">
          <CardTitle
            right={
              <span className="text-text-muted">
                {t('ai.reviewer.periodLabel', { from: f.date(detail.period_from), to: f.date(detail.period_to) })}
              </span>
            }
          >
            {runs[0]?.id === detail.id ? t('ai.reviewer.latest') : t('ai.reviewer.viewingRun', { date: f.dateTime(detail.created_at) })}
          </CardTitle>
          <div className="mt-2 flex flex-wrap items-center gap-2 text-[11.5px] text-text-muted">
            <span className="rounded-full border border-line bg-fill px-2 py-0.5 font-semibold text-text-mid">
              {detail.mode === 'model'
                ? t('ai.reviewer.reviewedBy', { model: modelName(detail) })
                : t('ai.reviewer.heuristicBadge')}
              {detail.requested_model && (
                <span className="ml-1 font-normal text-text-muted">· {t('ai.reviewer.model.pickedForRun')}</span>
              )}
            </span>
            <span>{t('ai.reviewer.ranBy', { date: f.dateTime(detail.created_at), user: detail.created_by || '—' })}</span>
            {detail.status === 'done' && (
              <span>{t('ai.reviewer.counts', { reviewed: detail.reviewed, issues: detail.issues, high: detail.high_severity })}</span>
            )}
            {detail.prompt_tokens + detail.completion_tokens > 0 && (
              <span>{t('ai.reviewer.tokens', { tokens: f.number(detail.prompt_tokens + detail.completion_tokens) })}</span>
            )}
          </div>

          {detail.status === 'running' && (
            <p className="mt-3 flex items-center gap-2 text-[12.5px] text-text-mid">
              <Loader2 size={14} className="animate-spin" aria-hidden /> {t('ai.reviewer.stillRunning')}
            </p>
          )}
          {detail.status === 'failed' && (
            <div className="mt-3">
              <ErrorLine text={t('ai.reviewer.failedRun', { error: detail.error || '—' })} />
            </div>
          )}
          {detail.mode === 'heuristic' && detail.status === 'done' && (
            <p className="mt-3 rounded-lg border border-line bg-fill-subtle px-3 py-2 text-[12px] leading-relaxed text-text-mid">
              {t('ai.reviewer.heuristicNote')}
            </p>
          )}
          {detail.note && detail.mode === 'model' && <p className="mt-2 text-[11.5px] text-text-muted">{detail.note}</p>}

          {detail.status === 'done' && (
            <div className="mt-4 grid gap-4 lg:grid-cols-2">
              <div>
                <div className="eyebrow mb-1.5">{t('ai.reviewer.summaryTitle')}</div>
                <p className="text-[13px] leading-relaxed text-text-high">{detail.summary.summary}</p>
                {(detail.summary.top_problems ?? []).length > 0 && (
                  <>
                    <div className="eyebrow mb-1.5 mt-3">{t('ai.reviewer.topProblems')}</div>
                    <ul className="flex flex-col gap-1 text-[12.5px] text-text-mid">
                      {(detail.summary.top_problems ?? []).map((p, i) => (
                        <li key={i} className="flex items-baseline justify-between gap-3">
                          <span>{p.category ? t(`ai.reviewer.category.${p.category}` as MessageKey) : p.problem}</span>
                          {p.count > 0 && (
                            <span className="shrink-0 text-text-muted">{t('ai.reviewer.problemCount', { count: p.count })}</span>
                          )}
                        </li>
                      ))}
                    </ul>
                  </>
                )}
              </div>
              <div data-tour="ai-review-suggestions">
                <div className="eyebrow mb-1.5">{t('ai.reviewer.suggestionsTitle')}</div>
                {(detail.summary.suggestions ?? []).length === 0 ? (
                  <p className="text-[12.5px] text-text-muted">{t('ai.reviewer.noSuggestions')}</p>
                ) : (
                  <ol className="flex flex-col gap-2">
                    {(detail.summary.suggestions ?? []).map((s, i) => (
                      <li key={i} className="rounded-lg border border-line bg-fill-subtle px-3 py-2">
                        <div className="text-[10.5px] font-bold uppercase tracking-wide text-text-muted">
                          {t(`ai.reviewer.fixType.${s.type}` as MessageKey)}
                        </div>
                        <div className="text-[12.5px] leading-relaxed text-text-high">{s.text}</div>
                        {s.why && <div className="mt-0.5 text-[11px] text-text-muted">{s.why}</div>}
                      </li>
                    ))}
                  </ol>
                )}
              </div>
            </div>
          )}
        </Card>
      )}

      {/* ---- findings ------------------------------------------------------------------- */}
      {detail && detail.status === 'done' && (
        <Card className="p-4 sm:p-5" data-tour="ai-review-findings">
          <CardTitle
            right={
              <div role="group" className="flex gap-1">
                {[true, false].map((only) => (
                  <button
                    key={String(only)}
                    type="button"
                    aria-pressed={issuesOnly === only}
                    onClick={() => setIssuesOnly(only)}
                    className={`h-7 rounded-md px-2.5 text-[12px] font-semibold transition-colors ${
                      issuesOnly === only ? 'bg-fill text-text-heading' : 'text-text-muted hover:bg-fill-hover'
                    }`}
                  >
                    {only
                      ? `${t('ai.reviewer.showIssues')} (${detail.counts.issues})`
                      : `${t('ai.reviewer.showAll')} (${detail.counts.all})`}
                  </button>
                ))}
              </div>
            }
          >
            {t('ai.reviewer.findingsTitle')}
          </CardTitle>
          {findings.length === 0 ? (
            <p className="mt-3 text-[12.5px] text-text-muted">
              {detail.counts.all === 0 ? t('ai.reviewer.noFindings') : t('ai.reviewer.noIssues')}
            </p>
          ) : (
            <ul className="mt-3 flex flex-col gap-3">
              {findings.map((x) => {
                const msg = applyMsg[x.id]
                const canApply = canRun && x.verdict === 'issue' && x.fix.type !== 'none' && x.fix.text && !x.applied
                return (
                  <li key={x.id} className="rounded-xl border border-line p-3.5">
                    <div className="flex flex-wrap items-center gap-1.5">
                      {x.verdict === 'issue' ? (
                        <span className={`rounded-full border px-2 py-0.5 text-[11px] font-bold ${SEVERITY_CLS[x.severity || 'low']}`}>
                          {t(`ai.reviewer.severity.${x.severity || 'low'}` as MessageKey)}
                        </span>
                      ) : (
                        <span className="inline-flex items-center gap-1 rounded-full border border-green/30 bg-green/10 px-2 py-0.5 text-[11px] font-bold text-[color:var(--green-ink)]">
                          <CheckCircle2 size={11} aria-hidden /> {t('ai.reviewer.good')}
                        </span>
                      )}
                      {x.categories.map((c) => (
                        <span key={c} className="rounded-full bg-fill px-2 py-0.5 text-[11px] font-medium text-text-mid">
                          {t(`ai.reviewer.category.${c}` as MessageKey)}
                        </span>
                      ))}
                      <span className="ml-auto text-[11px] text-text-muted">
                        {channelLabel(x.channel)}
                        {STATUS_KEY[x.draft_status] ? ` · ${t(STATUS_KEY[x.draft_status])}` : ''}
                        {x.source === 'heuristic' ? ` · ${t('ai.reviewer.automaticCheck')}` : ''}
                      </span>
                    </div>
                    <div className="mt-2.5 grid gap-2 md:grid-cols-2">
                      <div className="rounded-lg border border-line bg-fill-subtle px-3 py-2">
                        <div className="text-[10.5px] font-bold uppercase tracking-wide text-text-muted">{t('ai.reviewer.customer')}</div>
                        <p className="mt-0.5 whitespace-pre-line break-words text-[12.5px] text-text-high">{x.customer_excerpt || '—'}</p>
                      </div>
                      <div className="rounded-lg border border-line bg-card px-3 py-2">
                        <div className="text-[10.5px] font-bold uppercase tracking-wide text-text-muted">{t('ai.reviewer.aiReply')}</div>
                        <p className="mt-0.5 whitespace-pre-line break-words text-[12.5px] text-text-high">{x.reply_excerpt}</p>
                      </div>
                    </div>
                    {x.explanation && <p className="mt-2.5 text-[12.5px] leading-relaxed text-text-high">{x.explanation}</p>}
                    {x.evidence && <p className="mt-1 text-[11.5px] text-text-muted">{t('ai.reviewer.evidence', { evidence: x.evidence })}</p>}
                    {x.fix.type !== 'none' && x.fix.text && (
                      <div className="mt-2.5 rounded-lg border border-dashed border-line-strong px-3 py-2">
                        <div className="text-[10.5px] font-bold uppercase tracking-wide text-text-muted">
                          {t('ai.reviewer.fix')} · {t(`ai.reviewer.fixType.${x.fix.type}` as MessageKey)}
                        </div>
                        <p className="mt-0.5 text-[12.5px] text-text-high">{x.fix.text}</p>
                      </div>
                    )}
                    {msg && (
                      <div className="mt-2">
                        {msg.ok ? (
                          <p className="text-[12px] text-[color:var(--green-ink)]">{msg.text}</p>
                        ) : (
                          <ErrorLine text={msg.text} />
                        )}
                      </div>
                    )}
                    <div className="mt-2.5 flex flex-wrap items-center gap-2">
                      {x.applied && x.applied_as && (
                        <span className="inline-flex items-center gap-1 text-[12px] font-semibold text-[color:var(--green-ink)]">
                          <CheckCircle2 size={13} aria-hidden /> {t(`ai.reviewer.appliedAs.${x.applied_as}` as MessageKey)}
                        </span>
                      )}
                      {canApply && (
                        <>
                          <Button size="sm" variant="secondary" disabled={applying === x.id} onClick={() => apply(x, 'lesson')}>
                            {t('ai.reviewer.addLesson')}
                          </Button>
                          {canKnowledge && (
                            <Button size="sm" variant="secondary" disabled={applying === x.id} onClick={() => apply(x, 'knowledge')}>
                              {t('ai.reviewer.addKnowledge')}
                            </Button>
                          )}
                          {canKnowledge && (
                            <Button size="sm" variant="secondary" disabled={applying === x.id} onClick={() => apply(x, 'house_rule')}>
                              {t('ai.reviewer.addRule')}
                            </Button>
                          )}
                        </>
                      )}
                      {x.links.chat && (
                        <Link
                          href={x.links.chat}
                          className="ml-auto inline-flex items-center gap-1 text-[12px] font-semibold text-text-mid hover:text-text-high"
                        >
                          <ExternalLink size={12} aria-hidden /> {t('ai.common.openChat')}
                        </Link>
                      )}
                    </div>
                  </li>
                )
              })}
            </ul>
          )}
        </Card>
      )}

      {/* ---- past runs --------------------------------------------------------------------- */}
      <Card className="p-4 sm:p-5" data-tour="ai-review-runs">
        <CardTitle>{t('ai.reviewer.pastRuns')}</CardTitle>
        {runs.length === 0 ? (
          <p className="mt-2 text-[12.5px] text-text-muted">{t('ai.reviewer.noRuns')}</p>
        ) : (
          <ul className="mt-2 divide-y divide-line">
            {runs.map((r) => (
              <li key={r.id} className="flex flex-wrap items-center gap-x-3 gap-y-1 py-2 text-[12.5px]">
                <span className="font-semibold text-text-high">{f.dateTime(r.created_at)}</span>
                <span className="text-text-muted">
                  {t('ai.reviewer.periodLabel', { from: f.date(r.period_from), to: f.date(r.period_to) })}
                </span>
                <span className="text-text-muted">
                  {r.status === 'done'
                    ? t('ai.reviewer.counts', { reviewed: r.reviewed, issues: r.issues, high: r.high_severity })
                    : t(`ai.reviewer.status.${r.status}` as MessageKey)}
                </span>
                <span className="text-text-faint">
                  {r.mode === 'model' ? modelName(r) : t('ai.reviewer.heuristicBadge')}
                  {r.requested_model && ` · ${t('ai.reviewer.model.pickedForRun')}`}
                </span>
                {selected === r.id ? (
                  <span className="ml-auto text-[12px] font-semibold text-text-mid">{t('ai.reviewer.showing')}</span>
                ) : (
                  <Button size="sm" variant="ghost" className="ml-auto" onClick={() => setSelected(r.id)}>
                    {t('ai.reviewer.view')}
                  </Button>
                )}
              </li>
            ))}
          </ul>
        )}
      </Card>
      <Modal
        open={!!confirm}
        onClose={() => setConfirm(null)}
        title={confirm ? t(`ai.reviewer.confirm.title.${confirm.as}` as MessageKey) : ''}
        subtitle={t('ai.reviewer.confirm.subtitle')}
        footer={
          <>
            <Button variant="secondary" onClick={() => setConfirm(null)}>
              {t('common.actions.cancel')}
            </Button>
            <Button onClick={confirmApply} disabled={!confirm || applying === confirm.finding.id || !confirm.text.trim()}>
              {t('ai.reviewer.confirm.add')}
            </Button>
          </>
        }
      >
        {confirm && (
          <div className="flex flex-col gap-3">
            {confirmError && <ErrorLine text={confirmError} />}
            <label className="flex flex-col gap-1">
              <span className="text-[12px] font-semibold text-text-mid">{t('ai.reviewer.confirm.text')}</span>
              <textarea
                value={confirm.text}
                onChange={(e) => setConfirm({ ...confirm, text: e.target.value })}
                rows={4}
                maxLength={2000}
                className="w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[13px] text-text-primary outline-none focus:border-brand-400"
              />
            </label>
            <div className="flex flex-col gap-1">
              <span className="text-[12px] font-semibold text-text-mid">{t('ai.reviewer.confirm.level')}</span>
              <EntityPicker
                value={confirm.level}
                onChange={(slug) => setConfirm({ ...confirm, level: slug })}
                ariaLabel={t('ai.reviewer.confirm.level')}
              />
              <span className="text-[11.5px] text-text-muted">{t('ai.reviewer.confirm.levelHint')}</span>
            </div>
          </div>
        )}
      </Modal>
    </div>
  )
}
