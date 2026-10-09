'use client'

/**
 * Test lab (superadmin) — a catalog of scripted, realistic use cases that run
 * end to end through the real pipelines inside a fenced "Test lab" entity, and
 * report pass/fail with links to what they made. Runs one scenario per request
 * so results stream in; every request appends to the same run in the history.
 * Also loads the demo dataset and resets the fenced data (confirmed in a modal).
 * Scenario titles, steps and check names come from the server in English.
 */

import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import Link from 'next/link'
import {
  CheckCircle2,
  ChevronDown,
  ChevronRight,
  CircleDashed,
  Database,
  ExternalLink,
  FlaskConical,
  Loader2,
  Play,
  RotateCcw,
  Square,
  Trash2,
  XCircle,
  MinusCircle,
} from 'lucide-react'
import { PageHeader } from '@/components/ui/PageHeader'
import { Card, CardTitle } from '@/components/ui/Card'
import { Button } from '@/components/ui/Button'
import { Modal } from '@/components/ui/Modal'
import { useFormat, useT } from '@/i18n'
import {
  LabError,
  getCatalog,
  getRun,
  getRuns,
  getStatus,
  loadDemo,
  resetLab,
  runScenarios,
  type LabCatalog,
  type LabResult,
  type LabRun,
  type LabScenario,
  type LabStatus,
} from '@/lib/testLab'

type RowState = 'queued' | 'running'

const errText = (e: unknown) => (e instanceof Error ? e.message : String(e))

export default function TestLabPage() {
  const t = useT()
  const f = useFormat()
  const [catalog, setCatalog] = useState<LabCatalog | null>(null)
  const [off, setOff] = useState(false)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [status, setStatus] = useState<LabStatus | null>(null)
  const [runs, setRuns] = useState<LabRun[]>([])
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [results, setResults] = useState<Record<string, LabResult>>({})
  const [rowState, setRowState] = useState<Record<string, RowState>>({})
  const [expanded, setExpanded] = useState<Set<string>>(new Set())
  const [progress, setProgress] = useState<{ done: number; total: number; title: string } | null>(null)
  const [stopping, setStopping] = useState(false)
  const stopRef = useRef(false)
  const [finished, setFinished] = useState<string | null>(null)
  const [notice, setNotice] = useState<{ ok: boolean; text: string; links?: { label: string; url: string }[] } | null>(null)
  const [viewing, setViewing] = useState<LabRun | null>(null)
  const [resetOpen, setResetOpen] = useState(false)
  const [resetError, setResetError] = useState<string | null>(null)
  const [busy, setBusy] = useState<'' | 'demo' | 'reset'>('')

  const refreshSide = useCallback(async () => {
    try {
      const [s, r] = await Promise.all([getStatus(), getRuns()])
      setStatus(s)
      setRuns(r)
    } catch {
      /* the side panel is best effort */
    }
  }, [])

  useEffect(() => {
    let alive = true
    getCatalog()
      .then((c) => {
        if (!alive) return
        setCatalog(c)
        void refreshSide()
      })
      .catch((e) => {
        if (!alive) return
        if (e instanceof LabError && e.status === 404) setOff(true)
        else setLoadError(errText(e))
      })
    return () => {
      alive = false
    }
  }, [refreshSide])

  const all = useMemo(() => catalog?.categories.flatMap((c) => c.scenarios) ?? [], [catalog])
  const running = progress != null

  const run = async (ids: string[]) => {
    if (ids.length === 0 || running) return
    stopRef.current = false
    setStopping(false)
    setFinished(null)
    setNotice(null)
    setViewing(null)
    setRowState(Object.fromEntries(ids.map((id) => [id, 'queued' as RowState])))
    setResults((prev) => {
      const next = { ...prev }
      for (const id of ids) delete next[id]
      return next
    })
    let runId: number | undefined
    const tally = { passed: 0, failed: 0, skipped: 0 }
    try {
      for (let i = 0; i < ids.length; i++) {
        if (stopRef.current) break
        const sc = all.find((s) => s.id === ids[i])
        setProgress({ done: i + 1, total: ids.length, title: sc?.title ?? ids[i] })
        setRowState((s) => ({ ...s, [ids[i]]: 'running' }))
        const r = await runScenarios([ids[i]], runId)
        runId = r.run.id
        const res = r.results[0]
        if (res.passed) tally.passed++
        else if (res.skipped) tally.skipped++
        else tally.failed++
        setResults((prev) => ({ ...prev, [res.id]: res }))
        if (!res.passed && !res.skipped) setExpanded((e) => new Set(e).add(res.id))
        setRowState((s) => {
          const next = { ...s }
          delete next[ids[i]]
          return next
        })
      }
      setFinished(t('testlab.progress.done', tally))
    } catch (e) {
      setNotice({ ok: false, text: t('testlab.errors.action', { error: errText(e) }) })
    } finally {
      setProgress(null)
      setRowState({})
      void refreshSide()
    }
  }

  const showRun = async (r: LabRun) => {
    try {
      const data = await getRun(r.id)
      setResults(Object.fromEntries(data.results.map((x) => [x.id, x])))
      setViewing(r)
      setFinished(null)
    } catch (e) {
      setNotice({ ok: false, text: t('testlab.errors.action', { error: errText(e) }) })
    }
  }

  const demo = async () => {
    setBusy('demo')
    setNotice(null)
    try {
      const d = await loadDemo()
      setNotice(
        d.already_loaded
          ? { ok: true, text: t('testlab.demo.already') }
          : {
              ok: true,
              text: t('testlab.demo.loaded', {
                contacts: d.created.contacts ?? 0,
                conversations: d.created.conversations ?? 0,
                bookings: d.created.bookings ?? 0,
              }),
              links: d.links,
            },
      )
    } catch (e) {
      setNotice({ ok: false, text: t('testlab.errors.action', { error: errText(e) }) })
    } finally {
      setBusy('')
      void refreshSide()
    }
  }

  const reset = async () => {
    setBusy('reset')
    setResetError(null)
    try {
      const r = await resetLab()
      const count = Object.values(r.deleted).reduce((a, b) => a + b, 0)
      setResetOpen(false)
      setResults({})
      setViewing(null)
      setNotice({ ok: true, text: t('testlab.reset.done', { count: f.number(count) }) })
    } catch (e) {
      setResetError(t('testlab.errors.action', { error: errText(e) }))
    } finally {
      setBusy('')
      void refreshSide()
    }
  }

  const toggle = (id: string) =>
    setSelected((s) => {
      const next = new Set(s)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })

  const toggleCategory = (ids: string[]) =>
    setSelected((s) => {
      const next = new Set(s)
      const allIn = ids.every((id) => next.has(id))
      for (const id of ids) {
        if (allIn) next.delete(id)
        else next.add(id)
      }
      return next
    })

  const toggleOpen = (id: string) =>
    setExpanded((s) => {
      const next = new Set(s)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })

  const header = (
    <PageHeader
      eyebrow={
        <span className="inline-flex items-center gap-1.5">
          <FlaskConical size={12} strokeWidth={2.2} aria-hidden />
          {t('testlab.eyebrow')}
        </span>
      }
      title={t('testlab.title')}
      subtitle={t('testlab.subtitle')}
      actions={
        catalog && (
          <>
            <Button variant="secondary" size="sm" onClick={() => void demo()} disabled={running || busy !== ''}>
              {busy === 'demo' ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Database size={13} aria-hidden />}
              {t('testlab.actions.loadDemo')}
            </Button>
            <Button
              variant="danger"
              size="sm"
              onClick={() => {
                setResetError(null)
                setResetOpen(true)
              }}
              disabled={running || busy !== ''}
            >
              <Trash2 size={13} aria-hidden />
              {t('testlab.actions.reset')}
            </Button>
          </>
        )
      }
    />
  )

  if (off || loadError || !catalog) {
    return (
      <div className="flex flex-col gap-4">
        {header}
        <Card className="p-5">
          {off ? (
            <>
              <CardTitle>{t('testlab.off.title')}</CardTitle>
              <p className="mt-2 text-[13px] text-text-muted">{t('testlab.off.body')}</p>
            </>
          ) : loadError ? (
            <p role="alert" className="text-[13px] text-[color:var(--red-ink)]">
              {t('testlab.errors.load', { error: loadError })}
            </p>
          ) : (
            <p className="flex items-center gap-2 text-[13px] text-text-muted">
              <Loader2 size={14} className="animate-spin" aria-hidden /> {t('common.status.loading')}
            </p>
          )}
        </Card>
      </div>
    )
  }

  return (
    <div className="flex flex-col gap-4">
      {header}

      {/* status + the run bar */}
      <Card className="flex flex-col gap-3 p-4">
        <div className="flex flex-wrap items-center gap-x-4 gap-y-1 text-[12.5px] text-text-muted">
          <span>
            {status?.exists
              ? t('testlab.status.data', {
                  contacts: f.number(status.counts.contacts),
                  conversations: f.number(status.counts.conversations),
                  entity: status.entity_name ?? 'Test lab',
                })
              : t('testlab.status.none')}
          </span>
          {status?.demo_loaded && (
            <span className="rounded-full border border-line px-2 py-0.5 text-[11.5px] text-text-mid">
              {t('testlab.status.demo')}
            </span>
          )}
          <span>{catalog.has_ai_key ? t('testlab.status.aiReal') : t('testlab.status.aiSandbox')}</span>
          <code className="rounded bg-well px-1.5 py-0.5 text-[11.5px] text-text-mid">{t('testlab.status.cli')}</code>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <Button size="sm" onClick={() => void run(all.map((s) => s.id))} disabled={running}>
            <Play size={13} aria-hidden />
            {t('testlab.actions.runAll')}
          </Button>
          <Button
            size="sm"
            variant="secondary"
            onClick={() => void run(all.filter((s) => selected.has(s.id)).map((s) => s.id))}
            disabled={running || selected.size === 0}
          >
            {t('testlab.actions.runSelected', { count: selected.size })}
          </Button>
          {running && (
            <Button
              size="sm"
              variant="ghost"
              onClick={() => {
                stopRef.current = true
                setStopping(true)
              }}
              disabled={stopping}
            >
              <Square size={12} aria-hidden />
              {t('testlab.actions.stop')}
            </Button>
          )}
        </div>
        {progress && (
          <div aria-live="polite">
            <div className="mb-1 flex items-center gap-2 text-[12.5px] text-text-mid">
              <Loader2 size={13} className="animate-spin" aria-hidden />
              {stopping
                ? t('testlab.progress.stopping')
                : t('testlab.progress.running', { done: progress.done, total: progress.total, title: progress.title })}
            </div>
            <div className="h-1.5 overflow-hidden rounded-full bg-fill">
              <div
                className="h-full rounded-full bg-primary transition-[width]"
                style={{ width: `${Math.round(((progress.done - 1) / progress.total) * 100)}%` }}
              />
            </div>
          </div>
        )}
        {finished && !running && (
          <p aria-live="polite" className="text-[13px] font-semibold text-text-high">
            {finished}
          </p>
        )}
        {viewing && (
          <p className="text-[12.5px] text-text-muted">
            {t('testlab.history.showing', { id: viewing.id, when: f.dateTime(viewing.created_at) })}
          </p>
        )}
        {notice && (
          <div
            role={notice.ok ? 'status' : 'alert'}
            className={`rounded-lg border px-3 py-2 text-[12.5px] ${
              notice.ok
                ? 'border-green/30 bg-green/10 text-[color:var(--green-ink)]'
                : 'border-red/30 bg-red/10 text-[color:var(--red-ink)]'
            }`}
          >
            {notice.text}
            {notice.links && notice.links.length > 0 && (
              <span className="mt-1 flex flex-wrap gap-x-3 gap-y-1">
                {notice.links.map((l) => (
                  <Link key={l.url} href={l.url} className="inline-flex items-center gap-1 underline">
                    {l.label} <ExternalLink size={11} aria-hidden />
                  </Link>
                ))}
              </span>
            )}
          </div>
        )}
      </Card>

      <div className="grid min-w-0 gap-4 lg:grid-cols-[minmax(0,1fr)_300px]">
        <div className="flex min-w-0 flex-col gap-4">
          {catalog.categories.map((cat) => {
            const ids = cat.scenarios.map((s) => s.id)
            const allIn = ids.every((id) => selected.has(id))
            return (
              <Card key={cat.id} className="min-w-0 p-4">
                <div className="flex flex-wrap items-start justify-between gap-2">
                  <div className="min-w-0">
                    <CardTitle>{cat.label}</CardTitle>
                    <p className="mt-0.5 text-[12.5px] text-text-muted">{cat.covers}</p>
                  </div>
                  <label className="flex shrink-0 cursor-pointer items-center gap-2 text-[12px] text-text-mid">
                    <input
                      type="checkbox"
                      className="h-4 w-4 accent-[rgb(var(--c-brand))]"
                      checked={allIn}
                      onChange={() => toggleCategory(ids)}
                      disabled={running}
                    />
                    {t('testlab.actions.selectCategory', { category: cat.label })}
                  </label>
                </div>
                <ul className="mt-3 divide-y divide-line">
                  {cat.scenarios.map((s) => (
                    <ScenarioRow
                      key={s.id}
                      scenario={s}
                      result={results[s.id]}
                      state={rowState[s.id]}
                      checked={selected.has(s.id)}
                      open={expanded.has(s.id)}
                      disabled={running}
                      onCheck={() => toggle(s.id)}
                      onOpen={() => toggleOpen(s.id)}
                      onRun={() => void run([s.id])}
                    />
                  ))}
                </ul>
              </Card>
            )
          })}
        </div>

        <Card className="h-fit min-w-0 p-4">
          <CardTitle>{t('testlab.history.title')}</CardTitle>
          <p className="mt-0.5 text-[12px] text-text-muted">{t('testlab.history.subtitle')}</p>
          {runs.length === 0 ? (
            <p className="mt-3 text-[12.5px] text-text-faint">{t('testlab.history.empty')}</p>
          ) : (
            <ul className="mt-3 flex flex-col gap-2">
              {runs.map((r) => (
                <li key={r.id}>
                  <button
                    type="button"
                    onClick={() => void showRun(r)}
                    disabled={running}
                    className={`w-full rounded-lg border px-3 py-2 text-left transition-colors hover:bg-fill-hover ${
                      viewing?.id === r.id ? 'border-line-strong bg-fill-subtle' : 'border-line'
                    }`}
                  >
                    <div className="flex items-center justify-between gap-2 text-[12.5px] font-semibold text-text-high">
                      <span>#{r.id}</span>
                      <span
                        className={
                          r.failed > 0 ? 'text-[color:var(--red-ink)]' : 'text-[color:var(--green-ink)]'
                        }
                      >
                        {r.passed}/{r.total}
                      </span>
                    </div>
                    <div className="mt-0.5 text-[11.5px] text-text-muted">
                      {t('testlab.history.summary', { passed: r.passed, failed: r.failed, skipped: r.skipped })}
                    </div>
                    <div className="mt-0.5 text-[11.5px] text-text-faint">
                      {f.dateTime(r.created_at)} · {sourceLabel(t, r.source)} · {t('testlab.history.by', { user: r.started_by })}
                    </div>
                  </button>
                </li>
              ))}
            </ul>
          )}
        </Card>
      </div>

      <Modal
        open={resetOpen}
        onClose={() => busy !== 'reset' && setResetOpen(false)}
        title={t('testlab.reset.title')}
        size="sm"
        footer={
          <>
            <Button variant="ghost" size="sm" onClick={() => setResetOpen(false)} disabled={busy === 'reset'}>
              {t('common.actions.cancel')}
            </Button>
            <Button variant="danger" size="sm" onClick={() => void reset()} disabled={busy === 'reset'}>
              {busy === 'reset' ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <RotateCcw size={13} aria-hidden />}
              {t('testlab.reset.confirm')}
            </Button>
          </>
        }
      >
        {resetError && (
          <div role="alert" className="mb-3 rounded-lg border border-red/30 bg-red/10 px-3 py-2 text-[12.5px] text-[color:var(--red-ink)]">
            {resetError}
          </div>
        )}
        <p className="text-[13px] leading-relaxed text-text-mid">{t('testlab.reset.body')}</p>
      </Modal>
    </div>
  )
}

function sourceLabel(t: ReturnType<typeof useT>, source: string) {
  switch (source) {
    case 'page':
      return t('testlab.history.sources.page')
    case 'cli':
      return t('testlab.history.sources.cli')
    case 'test':
      return t('testlab.history.sources.test')
    default:
      return t('testlab.history.sources.api')
  }
}

function ResultChip({ result, state }: { result?: LabResult; state?: RowState }) {
  const t = useT()
  const base = 'inline-flex shrink-0 items-center gap-1 rounded-full border px-2 py-0.5 text-[11.5px] font-semibold'
  if (state === 'running')
    return (
      <span className={`${base} border-line text-text-mid`}>
        <Loader2 size={11} className="animate-spin" aria-hidden /> {t('testlab.result.running')}
      </span>
    )
  if (state === 'queued')
    return (
      <span className={`${base} border-line text-text-faint`}>
        <CircleDashed size={11} aria-hidden /> {t('testlab.result.queued')}
      </span>
    )
  if (!result) return <span className={`${base} border-transparent text-text-faint`}>{t('testlab.result.notRun')}</span>
  if (result.passed)
    return (
      <span className={`${base} border-green/30 bg-green/10 text-[color:var(--green-ink)]`}>
        <CheckCircle2 size={11} aria-hidden /> {t('testlab.result.passed')}
      </span>
    )
  if (result.skipped)
    return (
      <span className={`${base} border-amber/30 bg-amber/10 text-[color:var(--amber-ink)]`}>
        <MinusCircle size={11} aria-hidden /> {t('testlab.result.skipped')}
      </span>
    )
  return (
    <span className={`${base} border-red/30 bg-red/10 text-[color:var(--red-ink)]`}>
      <XCircle size={11} aria-hidden /> {t('testlab.result.failed')}
    </span>
  )
}

function ScenarioRow({
  scenario: s,
  result,
  state,
  checked,
  open,
  disabled,
  onCheck,
  onOpen,
  onRun,
}: {
  scenario: LabScenario
  result?: LabResult
  state?: RowState
  checked: boolean
  open: boolean
  disabled: boolean
  onCheck: () => void
  onOpen: () => void
  onRun: () => void
}) {
  const t = useT()
  const f = useFormat()
  const okChecks = result?.checks.filter((c) => c.ok).length ?? 0
  return (
    <li className="py-3">
      <div className="flex items-start gap-3">
        <input
          type="checkbox"
          aria-label={s.title}
          className="mt-1 h-4 w-4 shrink-0 accent-[rgb(var(--c-brand))]"
          checked={checked}
          onChange={onCheck}
          disabled={disabled}
        />
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            <span className="text-[13.5px] font-semibold text-text-high">{s.title}</span>
            {s.needs_ai_key && (
              <span className="rounded-full border border-line px-1.5 py-px text-[10.5px] font-semibold uppercase tracking-wide text-text-muted">
                {t('testlab.badge.aiKey')}
              </span>
            )}
          </div>
          <p className="mt-0.5 text-[12.5px] leading-relaxed text-text-muted">{s.proves}</p>
          {result && (
            <div className="mt-1 flex flex-wrap items-center gap-x-3 gap-y-0.5 text-[11.5px] text-text-faint">
              <span>{t('testlab.result.checks', { ok: okChecks, total: result.checks.length })}</span>
              <span>{t('testlab.result.seconds', { seconds: f.number(result.duration_ms / 1000, { maximumFractionDigits: 1 }) })}</span>
              {result.brain && (
                <span>{result.brain === 'configured' ? t('testlab.result.brainReal') : t('testlab.result.brainLab')}</span>
              )}
              <span className="font-mono">{s.id}</span>
            </div>
          )}
          {result && result.links.length > 0 && (
            <div className="mt-1.5 flex flex-wrap gap-x-3 gap-y-1">
              {result.links.map((l) => (
                <Link
                  key={l.url + l.label}
                  href={l.url}
                  className="inline-flex items-center gap-1 text-[12px] font-semibold text-[color:var(--brand-ink)] hover:underline"
                >
                  {l.label} <ExternalLink size={11} aria-hidden />
                </Link>
              ))}
            </div>
          )}
          {open && (
            <div className="mt-2 grid gap-3 rounded-lg border border-line bg-well p-3 text-[12.5px] sm:grid-cols-2">
              <div>
                <div className="eyebrow mb-1">{t('testlab.detail.steps')}</div>
                <ol className="list-decimal space-y-0.5 pl-4 text-text-mid">
                  {s.steps.map((x) => (
                    <li key={x}>{x}</li>
                  ))}
                </ol>
              </div>
              <div>
                <div className="eyebrow mb-1">{t('testlab.detail.expect')}</div>
                <ul className="list-disc space-y-0.5 pl-4 text-text-mid">
                  {s.expect.map((x) => (
                    <li key={x}>{x}</li>
                  ))}
                </ul>
              </div>
              {result && (
                <div className="sm:col-span-2">
                  <div className="eyebrow mb-1">{t('testlab.detail.checks')}</div>
                  {result.skipped && (
                    <p className="mb-1 text-[color:var(--amber-ink)]">{t('testlab.detail.skipped', { reason: result.skip_reason })}</p>
                  )}
                  {result.error && (
                    <p role="alert" className="mb-1 text-[color:var(--red-ink)]">
                      {t('testlab.detail.error')}: {result.error}
                    </p>
                  )}
                  <ul className="space-y-1">
                    {result.checks.map((c, i) => (
                      <li key={i} className="flex items-start gap-1.5">
                        {c.ok ? (
                          <CheckCircle2 size={13} className="mt-0.5 shrink-0 text-[color:var(--green-ink)]" aria-hidden />
                        ) : (
                          <XCircle size={13} className="mt-0.5 shrink-0 text-[color:var(--red-ink)]" aria-hidden />
                        )}
                        <span className={c.ok ? 'text-text-mid' : 'font-semibold text-text-high'}>
                          {c.name}
                          {c.detail && <span className="font-normal text-text-faint"> — {c.detail}</span>}
                        </span>
                      </li>
                    ))}
                  </ul>
                </div>
              )}
            </div>
          )}
        </div>
        <div className="flex shrink-0 flex-col items-end gap-1.5">
          <ResultChip result={result} state={state} />
          <div className="flex items-center gap-1">
            <Button variant="ghost" size="sm" onClick={onRun} disabled={disabled} aria-label={`${t('testlab.actions.runOne')} ${s.title}`}>
              <Play size={12} aria-hidden />
            </Button>
            <Button variant="ghost" size="sm" onClick={onOpen} aria-expanded={open}>
              {open ? <ChevronDown size={13} aria-hidden /> : <ChevronRight size={13} aria-hidden />}
              <span className="hidden sm:inline">{open ? t('testlab.actions.hideDetails') : t('testlab.actions.details')}</span>
            </Button>
          </div>
        </div>
      </div>
    </li>
  )
}
