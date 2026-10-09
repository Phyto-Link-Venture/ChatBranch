'use client'

import { CheckCircle2, Plus, Trash2, XCircle } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Select } from '@/components/ui/Select'
import type { TestResult } from './skillsApi'
import { useT, type TFunction } from '@/i18n'

/**
 * Editable test cases for a code skill. Tool cases take JSON arguments and a
 * JSON expectation matched as a SUBSET of the result (only the keys you list
 * are compared); check cases take a reply and whether it should pass. "Fixed
 * time" pins the clock (`now`) so time-dependent skills test the same way
 * every day.
 */

export interface TestRow {
  name: string
  input: string
  expect: string
  now: string
  /** Fields this editor doesn't show (timezone, contact…) — kept verbatim. */
  rest: Record<string, unknown>
}

const isObj = (v: unknown): v is Record<string, unknown> => !!v && typeof v === 'object' && !Array.isArray(v)

export function testsToRows(kind: 'tool' | 'check', tests: unknown[]): TestRow[] {
  return (tests ?? []).filter(isObj).map((t) => {
    const { name, args, input, reply, expect, now, ...rest } = t
    let inputText = ''
    let expectText = ''
    if (kind === 'check') {
      const r = input ?? reply
      inputText = typeof r === 'string' ? r : r === undefined ? '' : JSON.stringify(r)
      expectText = expect === true ? 'pass' : expect === false ? 'fail' : expect === undefined ? '' : JSON.stringify(expect)
    } else {
      const a = args ?? input
      inputText = a === undefined ? '{}' : JSON.stringify(a)
      expectText = expect === undefined ? '' : JSON.stringify(expect)
    }
    return {
      name: typeof name === 'string' ? name : '',
      input: inputText,
      expect: expectText,
      now: typeof now === 'string' ? now : '',
      rest,
    }
  })
}

/** Rows → the JSON the API stores. Returns an error naming the bad row (in the dashboard's language). */
export function rowsToTests(kind: 'tool' | 'check', rows: TestRow[], t: TFunction): { tests: unknown[]; error: string | null } {
  const tests: unknown[] = []
  for (let i = 0; i < rows.length; i++) {
    const r = rows[i]
    const label = r.name.trim() || t('ai.tests.testN', { n: i + 1 })
    const tc: Record<string, unknown> = { ...r.rest, name: label }
    if (kind === 'check') {
      tc.input = r.input
      if (r.expect === 'pass') tc.expect = true
      else if (r.expect === 'fail') tc.expect = false
      else if (r.expect.trim()) {
        try {
          tc.expect = JSON.parse(r.expect)
        } catch {
          return { tests: [], error: t('ai.tests.errExpectJson', { label }) }
        }
      }
    } else {
      try {
        tc.args = r.input.trim() ? JSON.parse(r.input) : {}
      } catch {
        return { tests: [], error: t('ai.tests.errArgsJson', { label }) }
      }
      if (!isObj(tc.args)) return { tests: [], error: t('ai.tests.errArgsObject', { label }) }
      if (r.expect.trim()) {
        try {
          tc.expect = JSON.parse(r.expect)
        } catch {
          return { tests: [], error: t('ai.tests.errExpectedJson', { label }) }
        }
      }
    }
    if (r.now.trim()) tc.now = r.now.trim()
    tests.push(tc)
  }
  return { tests, error: null }
}

const cell =
  'w-full rounded-md border border-line-strong bg-card px-2 py-1.5 text-[12px] text-text-primary outline-none transition-colors focus:border-brand-400 disabled:opacity-70'

export function TestCasesEditor({
  kind,
  rows,
  onChange,
  results,
  readOnly = false,
}: {
  kind: 'tool' | 'check'
  rows: TestRow[]
  onChange: (rows: TestRow[]) => void
  /** Latest run, index-aligned with `rows` (omit when stale). */
  results?: TestResult[] | null
  readOnly?: boolean
}) {
  const t = useT()
  const edit = (i: number, patch: Partial<TestRow>) => onChange(rows.map((r, j) => (j === i ? { ...r, ...patch } : r)))
  const add = () =>
    onChange([
      ...rows,
      { name: '', input: kind === 'tool' ? '{}' : '', expect: kind === 'check' ? 'fail' : '', now: '', rest: {} },
    ])
  return (
    <div>
      <div className="overflow-x-auto rounded-lg border border-line">
        <table className="w-full min-w-[720px] border-collapse text-left">
          <thead>
            <tr className="border-b border-line bg-fill-subtle text-[10.5px] font-bold uppercase tracking-[0.06em] text-text-muted">
              <th className="w-7 px-2 py-2" aria-label={t('ai.tests.result')} />
              <th className="w-[22%] px-2 py-2">{t('ai.tests.test')}</th>
              <th className="px-2 py-2">{kind === 'tool' ? t('ai.tests.argsJson') : t('ai.tests.replyToCheck')}</th>
              <th className="w-[26%] px-2 py-2">{kind === 'tool' ? t('ai.tests.expectedKeys') : t('ai.tests.should')}</th>
              <th className="w-[17%] px-2 py-2">{t('ai.tests.fixedTime')}</th>
              <th className="w-9 px-2 py-2" aria-label={t('common.actions.remove')} />
            </tr>
          </thead>
          <tbody>
            {rows.length === 0 && (
              <tr>
                <td colSpan={6} className="px-3 py-4 text-center text-[12px] text-text-muted">
                  {t('ai.tests.empty')}
                </td>
              </tr>
            )}
            {rows.map((r, i) => {
              const res = results?.[i]
              return (
                <tr key={i} className="border-b border-line align-top last:border-b-0">
                  <td className="px-2 pt-3">
                    {res ? (
                      res.pass ? (
                        <CheckCircle2 size={14} className="text-green-300" aria-label={t('ai.wizard.passed')} />
                      ) : (
                        <XCircle size={14} className="text-red-300" aria-label={t('ai.wizard.failed')} />
                      )
                    ) : (
                      <span className="mt-1 block h-2 w-2 rounded-full bg-fill-hover" aria-hidden />
                    )}
                  </td>
                  <td className="px-2 py-2">
                    <input
                      value={r.name}
                      disabled={readOnly}
                      placeholder={t('ai.tests.testN', { n: i + 1 })}
                      aria-label={t('ai.tests.nameAria', { n: i + 1 })}
                      onChange={(e) => edit(i, { name: e.target.value })}
                      className={cell}
                    />
                  </td>
                  <td className="px-2 py-2">
                    {kind === 'tool' ? (
                      <input
                        value={r.input}
                        disabled={readOnly}
                        spellCheck={false}
                        aria-label={t('ai.tests.argsAria', { n: i + 1 })}
                        onChange={(e) => edit(i, { input: e.target.value })}
                        className={`${cell} font-mono`}
                      />
                    ) : (
                      <textarea
                        value={r.input}
                        disabled={readOnly}
                        rows={2}
                        aria-label={t('ai.tests.replyAria', { n: i + 1 })}
                        placeholder={t('ai.tests.replyPlaceholder')}
                        onChange={(e) => edit(i, { input: e.target.value })}
                        className={`${cell} resize-y leading-[1.5]`}
                      />
                    )}
                  </td>
                  <td className="px-2 py-2">
                    {kind === 'check' && (r.expect === 'pass' || r.expect === 'fail' || r.expect === '') ? (
                      <Select
                        value={r.expect || 'pass'}
                        onChange={(v) => edit(i, { expect: v })}
                        disabled={readOnly}
                        ariaLabel={t('ai.tests.expectAria', { n: i + 1 })}
                        options={[
                          { value: 'pass', label: t('ai.tests.pass') },
                          { value: 'fail', label: t('ai.tests.fail') },
                        ]}
                      />
                    ) : (
                      <input
                        value={r.expect}
                        disabled={readOnly}
                        spellCheck={false}
                        placeholder={kind === 'tool' ? '{"open": true}' : '{"ok": false}'}
                        aria-label={t('ai.tests.expectedAria', { n: i + 1 })}
                        onChange={(e) => edit(i, { expect: e.target.value })}
                        className={`${cell} font-mono`}
                      />
                    )}
                    {res && !res.pass && (
                      <div className="mt-1.5 break-words font-mono text-[11px] leading-[1.45] text-[color:var(--red-ink)]">
                        {res.error ? res.error : t('ai.tests.gotValue', { value: JSON.stringify(res.got) })}
                      </div>
                    )}
                  </td>
                  <td className="px-2 py-2">
                    <input
                      value={r.now}
                      disabled={readOnly}
                      spellCheck={false}
                      placeholder={t('ai.tests.nowPlaceholder')}
                      title={t('ai.tests.nowTitle')}
                      aria-label={t('ai.tests.fixedAria', { n: i + 1 })}
                      onChange={(e) => edit(i, { now: e.target.value })}
                      className={`${cell} font-mono`}
                    />
                  </td>
                  <td className="px-1 py-2">
                    {!readOnly && (
                      <button
                        type="button"
                        onClick={() => onChange(rows.filter((_, j) => j !== i))}
                        aria-label={t('ai.tests.removeAria', { n: i + 1 })}
                        className="grid h-7 w-7 place-items-center rounded-md text-text-muted transition-colors hover:bg-red/10 hover:text-[color:var(--red-ink)]"
                      >
                        <Trash2 size={13} aria-hidden />
                      </button>
                    )}
                  </td>
                </tr>
              )
            })}
          </tbody>
        </table>
      </div>
      {!readOnly && (
        <Button variant="ghost" size="sm" onClick={add} className="mt-2">
          <Plus size={13} aria-hidden /> {t('ai.tests.addCase')}
        </Button>
      )}
    </div>
  )
}
