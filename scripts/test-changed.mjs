#!/usr/bin/env node
// The fast local loop: run only the API test classes a change touches. CI runs the
// whole suite on every push (3 shards × SQLite + PostgreSQL) and the deploy waits
// for it, so this is the check to run before pushing, not a replacement for CI.
//
//   npm run test:api:changed                    # changes vs origin/master + uncommitted
//   npm run test:api:changed -- --base HEAD~2   # vs another commit
//   npm run test:api:changed -- --list          # print the classes, run nothing
//
// Picks: changed test files; test files that name a type declared in a changed
// API source file (or, for a non-.cs file, in its folder); PermissionCatalogTests
// for dashboard changes; McpCoverageTests for docs/mcp.md. A change to the wiring
// every test shares — or one that would pick most of the suite anyway — runs it all.
import { execFileSync, spawnSync } from 'node:child_process'
import { readFileSync, readdirSync, existsSync } from 'node:fs'
import path from 'node:path'

const root = execFileSync('git', ['rev-parse', '--show-toplevel']).toString().trim()
const argv = process.argv.slice(2)
const base = argv.includes('--base') ? argv[argv.indexOf('--base') + 1] : 'origin/master'
const listOnly = argv.includes('--list')
const testsDir = path.join(root, 'backend-csharp/tests/ChatBranch.Tests')
const srcDir = path.join(root, 'backend-csharp/src')

const git = (...a) => {
  try { return execFileSync('git', a, { cwd: root, stdio: ['ignore', 'pipe', 'ignore'] }).toString() } catch { return '' }
}
const changed = [...new Set([
  ...git('diff', '--name-only', `${base}...HEAD`).split('\n'),
  ...git('diff', '--name-only', 'HEAD').split('\n'),
  ...git('ls-files', '--others', '--exclude-standard').split('\n'),
].filter(Boolean))]

// files every API test host goes through: a change there can break any test
const SHARED = [
  /^backend-csharp\/src\/ChatBranch\.Api\/Program\.cs$/,
  /^backend-csharp\/src\/ChatBranch\.Data\/(AppDbContext|Seed|ScopingService)\.cs$/,
  /^backend-csharp\/src\/ChatBranch\.Data\/DatabaseSetup\.cs$/,
  /\.csproj$/,
  /^backend-csharp\/tests\/ChatBranch\.Tests\/(BoundedParallelFramework|ApiSmokeTests|TestDb)\.cs$/,
]

const read = (f) => { try { return readFileSync(f, 'utf8') } catch { return '' } }
const testFiles = readdirSync(testsDir).filter((f) => f.endsWith('.cs')).map((f) => path.join(testsDir, f))
const testText = new Map(testFiles.map((f) => [f, read(f)]))
const classesIn = (text) => /\[(Fact|Theory)\b/.test(text)
  ? [...text.matchAll(/^\s*public (?:sealed |partial )*class (\w+)/gm)].map((m) => m[1]) : []
const typesIn = (text) => [...text.matchAll(/\b(?:class|record|interface|enum|struct)\s+(\w{4,})/g)].map((m) => m[1])

let everything = changed.find((f) => SHARED.some((re) => re.test(f)))
const picked = new Set()
const names = new Set()
for (const f of changed) {
  const abs = path.join(root, f)
  if (f.startsWith('backend-csharp/tests/ChatBranch.Tests/') && f.endsWith('.cs')) {
    classesIn(read(abs)).forEach((c) => picked.add(c))
    // a shared rig (BookingRig, TransportFakes…) reaches every class that uses its types
    typesIn(read(abs)).forEach((t) => names.add(t))
  } else if (f.startsWith('backend-csharp/src/')) {
    if (f.endsWith('.cs')) typesIn(read(abs)).forEach((t) => names.add(t))
    else {
      const dir = path.dirname(abs)
      if (existsSync(dir)) readdirSync(dir).filter((x) => x.endsWith('.cs'))
        .forEach((x) => typesIn(read(path.join(dir, x))).forEach((t) => names.add(t)))
    }
  } else if (f.startsWith('frontend/src/')) picked.add('PermissionCatalogTests')
  else if (f === 'docs/mcp.md') picked.add('McpCoverageTests')
}
if (names.size > 0) {
  const words = new RegExp(`\\b(${[...names].map((n) => n.replace(/[^\w]/g, '')).join('|')})\\b`)
  for (const [file, text] of testText) if (words.test(text)) classesIn(text).forEach((c) => picked.add(c))
}

const total = new Set([...testText.values()].flatMap(classesIn)).size
if (!everything && picked.size > total * 0.6) everything = `${picked.size} of ${total} classes`

if (changed.length === 0) { console.log(`Nothing changed against ${base}.`); process.exit(0) }
if (everything) console.log(`Running the whole suite (${everything}).`)
else if (picked.size === 0) { console.log('No API test touches these changes.'); process.exit(0) }
else console.log(`Running ${picked.size} of ${total} test classes:\n  ${[...picked].sort().join('\n  ')}`)
if (listOnly) process.exit(0)

const filter = everything ? [] : ['--filter', [...picked].sort().map((c) => `FullyQualifiedName~ChatBranch.Tests.${c}.`).join('|')]
const r = spawnSync('dotnet', ['test', testsDir, ...filter], { cwd: root, stdio: 'inherit' })
process.exit(r.status ?? 1)
