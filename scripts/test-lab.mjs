#!/usr/bin/env node
// Test lab CLI — runs the API's scripted use cases (POST /api/testlab/run) one
// by one against a running ChatBranch API and prints a pass/fail table.
//
//   npm run test:scenarios                         # every scenario
//   npm run test:scenarios -- --only stop-opts-out,widget-visitor
//   npm run test:scenarios -- --category ai        # one category (comma list ok)
//   npm run test:scenarios -- --list               # print the catalog, run nothing
//   npm run test:scenarios -- --reset              # delete the fenced test data first
//
// Env: TESTLAB_API (default http://localhost:8090/api), TESTLAB_USER /
// TESTLAB_PASS (default superadmin / super123 — the lab is superadmin-only),
// TESTLAB_REPORT (default test-lab-report.json in the current directory).
// Exit code 0 = every scenario that ran passed (skips are not failures), 1 = a
// failure or an error, 2 = bad arguments / cannot reach the API.
// No dependencies: Node 18+ (global fetch).
import { writeFileSync } from 'node:fs'
import { resolve } from 'node:path'

const API = (process.env.TESTLAB_API || 'http://localhost:8090/api').replace(/\/+$/, '')
const USER = process.env.TESTLAB_USER || 'superadmin'
const PASS = process.env.TESTLAB_PASS || 'super123'
const REPORT = resolve(process.env.TESTLAB_REPORT || 'test-lab-report.json')

function parseArgs(argv) {
  const out = { only: [], category: [], list: false, reset: false, help: false }
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i]
    const [flag, inline] = a.includes('=') ? a.split(/=(.*)/s, 2) : [a, undefined]
    const value = () => {
      const v = inline ?? argv[++i]
      if (!v) fail(2, `${flag} needs a value`)
      return v.split(',').map((s) => s.trim()).filter(Boolean)
    }
    if (flag === '--only') out.only.push(...value())
    else if (flag === '--category') out.category.push(...value())
    else if (flag === '--list') out.list = true
    else if (flag === '--reset') out.reset = true
    else if (flag === '-h' || flag === '--help') out.help = true
    else fail(2, `unknown argument ${a} (try --help)`)
  }
  return out
}

function fail(code, msg) {
  console.error(`test-lab: ${msg}`)
  process.exit(code)
}

const color = process.stdout.isTTY && !process.env.NO_COLOR
const paint = (code, s) => (color ? `\x1b[${code}m${s}\x1b[0m` : s)
const green = (s) => paint('32', s)
const red = (s) => paint('31', s)
const yellow = (s) => paint('33', s)
const dim = (s) => paint('2', s)

let token = ''
async function api(method, path, body) {
  let res
  try {
    res = await fetch(`${API}${path}`, {
      method,
      headers: {
        'Content-Type': 'application/json',
        ...(token ? { Authorization: `Bearer ${token}` } : {}),
      },
      body: body === undefined ? undefined : JSON.stringify(body),
    })
  } catch (e) {
    fail(2, `cannot reach ${API} — ${e.cause?.code || e.message}. Is the API running? (set TESTLAB_API)`)
  }
  const text = await res.text()
  let json = {}
  try {
    json = text ? JSON.parse(text) : {}
  } catch {
    json = { detail: text.slice(0, 200) }
  }
  if (!res.ok) {
    const err = new Error(json.detail || `HTTP ${res.status}`)
    err.status = res.status
    throw err
  }
  return json
}

const pad = (s, n) => (s.length >= n ? s.slice(0, n - 1) + '…' : s + ' '.repeat(n - s.length))

async function main() {
  const args = parseArgs(process.argv.slice(2))
  if (args.help) {
    console.log(
      'usage: npm run test:scenarios -- [--only id1,id2] [--category ai,routing] [--list] [--reset]\n' +
        'env: TESTLAB_API, TESTLAB_USER, TESTLAB_PASS, TESTLAB_REPORT',
    )
    return 0
  }

  try {
    const login = await api('POST', '/auth/login', { username: USER, password: PASS })
    token = login.token
  } catch (e) {
    fail(2, `sign-in as ${USER} failed — ${e.message}`)
  }
  if (!token) fail(2, 'sign-in returned no token')

  let catalog
  try {
    catalog = await api('GET', '/testlab/scenarios')
  } catch (e) {
    if (e.status === 404) fail(2, 'the Test lab is off on this server (TEST_LAB=0) or the API predates it')
    if (e.status === 403) fail(2, `${USER} is not a superadmin — the Test lab is superadmin-only`)
    fail(2, `loading the catalog failed — ${e.message}`)
  }

  const all = catalog.categories.flatMap((c) => c.scenarios.map((s) => ({ ...s, category: c.id })))
  const known = new Set(all.map((s) => s.id))
  const cats = new Set(catalog.categories.map((c) => c.id))
  const unknownIds = args.only.filter((id) => !known.has(id))
  if (unknownIds.length) fail(2, `unknown scenario id(s): ${unknownIds.join(', ')}`)
  const unknownCats = args.category.filter((c) => !cats.has(c))
  if (unknownCats.length) fail(2, `unknown category: ${unknownCats.join(', ')} (have: ${[...cats].join(', ')})`)

  let picked = all
  if (args.only.length) picked = picked.filter((s) => args.only.includes(s.id))
  if (args.category.length) picked = picked.filter((s) => args.category.includes(s.category))
  if (!picked.length) fail(2, 'nothing selected')

  if (args.list) {
    for (const c of catalog.categories) {
      const rows = picked.filter((s) => s.category === c.id)
      if (!rows.length) continue
      console.log(`\n${c.label} (${c.id})`)
      for (const s of rows) console.log(`  ${pad(s.id, 34)} ${s.proves}${s.needs_ai_key ? dim(' [AI key]') : ''}`)
    }
    return 0
  }

  if (args.reset) {
    const r = await api('POST', '/testlab/reset', {})
    const n = Object.values(r.deleted || {}).reduce((a, b) => a + b, 0)
    console.log(dim(`reset: deleted ${n} rows of test data`))
  }

  console.log(
    `Test lab — ${picked.length} scenario(s) against ${API}` +
      dim(catalog.has_ai_key ? ' (AI key configured)' : ' (no AI key: deterministic test brain)'),
  )

  const started = Date.now()
  const results = []
  let runId
  for (const [i, s] of picked.entries()) {
    const label = `[${String(i + 1).padStart(String(picked.length).length)}/${picked.length}] ${pad(s.id, 34)}`
    process.stdout.write(label)
    let r
    try {
      const out = await api('POST', '/testlab/run', { ids: [s.id], run_id: runId, source: 'cli' })
      runId = out.run.id
      r = out.results.find((x) => x.id === s.id) || out.results[out.results.length - 1]
    } catch (e) {
      r = { id: s.id, title: s.title, category: s.category, passed: false, skipped: false, error: e.message, checks: [], links: [], duration_ms: 0 }
    }
    results.push(r)
    const secs = `${((r.duration_ms || 0) / 1000).toFixed(1)}s`
    if (r.skipped) console.log(`${yellow('SKIP')} ${dim(secs)}  ${dim(r.skip_reason || '')}`)
    else if (r.passed) console.log(`${green('PASS')} ${dim(secs)}  ${dim(`${r.checks.length} checks`)}`)
    else {
      console.log(`${red('FAIL')} ${dim(secs)}`)
      if (r.error) console.log(`      ${red('error:')} ${r.error}`)
      for (const c of r.checks.filter((c) => !c.ok)) console.log(`      ${red('✗')} ${c.name}${c.detail ? dim(` — ${c.detail}`) : ''}`)
      for (const l of r.links || []) console.log(`      ${dim(`${l.label}: ${l.url}`)}`)
    }
  }

  const passed = results.filter((r) => r.passed).length
  const skipped = results.filter((r) => r.skipped).length
  const failed = results.length - passed - skipped
  const duration = Date.now() - started

  console.log('')
  console.log(`${pad('category', 12)} ${pad('passed', 8)} ${pad('failed', 8)} skipped`)
  for (const c of catalog.categories) {
    const rows = results.filter((r) => r.category === c.id)
    if (!rows.length) continue
    const p = rows.filter((r) => r.passed).length
    const sk = rows.filter((r) => r.skipped).length
    const f = rows.length - p - sk
    console.log(`${pad(c.id, 12)} ${pad(String(p), 8)} ${(f ? red : (x) => x)(pad(String(f), 8))} ${sk}`)
  }
  console.log('')
  const summary = `${passed} passed, ${failed} failed, ${skipped} skipped in ${(duration / 1000).toFixed(1)}s`
  console.log(failed ? red(summary) : green(summary))
  if (runId) console.log(dim(`run #${runId} — also listed on the dashboard's /test-lab page`))

  writeFileSync(
    REPORT,
    JSON.stringify(
      { api: API, run_id: runId ?? null, finished_at: new Date().toISOString(), duration_ms: duration, total: results.length, passed, failed, skipped, results },
      null,
      2,
    ),
  )
  console.log(dim(`report: ${REPORT}`))
  return failed ? 1 : 0
}

main().then(
  (code) => process.exit(code),
  (e) => fail(1, e.stack || e.message),
)
