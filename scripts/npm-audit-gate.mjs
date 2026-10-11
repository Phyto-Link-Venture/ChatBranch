#!/usr/bin/env node
// The npm half of the "Security" workflow (.github/workflows/security.yml).
//
//   node scripts/npm-audit-gate.mjs <package dir>          — fail on a new High / Critical advisory
//   node scripts/npm-audit-gate.mjs <package dir> --list   — print what `npm audit` reports, by package
//
// Runs `npm audit --omit=dev --audit-level=high --json` in the package directory
// (production dependencies only — what ships in the images) and fails when it
// reports a High or Critical advisory that is NOT accepted in
// .github/security/npm-audit-allowlist.json. An accepted advisory is one that
// can't be fixed with a safe patch / minor upgrade today (it needs a major
// upgrade, or upstream has no fix); each entry names the advisory ids, why it is
// accepted and a review-by date — after that date it fails again, so nothing is
// accepted forever. Accepted advisories are printed as warnings on every run.
import { execFileSync } from 'node:child_process'
import { readFileSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))
const repo = path.resolve(here, '..')
const dir = process.argv[2]
const listOnly = process.argv.includes('--list')
if (!dir) {
  console.error('usage: node scripts/npm-audit-gate.mjs <package dir> [--list]')
  process.exit(2)
}
const pkgDir = path.resolve(repo, dir)
const rel = path.relative(repo, pkgDir).split(path.sep).join('/')

let raw
try {
  raw = execFileSync('npm', ['audit', '--omit=dev', '--json'], { cwd: pkgDir, encoding: 'utf8', maxBuffer: 64 << 20 })
} catch (e) {
  // npm audit exits non-zero when it finds anything — the JSON is still on stdout
  raw = e.stdout
  if (!raw) {
    console.error(`npm audit failed in ${rel}: ${e.message}`)
    process.exit(2)
  }
}
const report = JSON.parse(raw)
if (report.error) {
  console.error(`npm audit failed in ${rel}: ${JSON.stringify(report.error)}`)
  process.exit(2)
}

// every advisory object behind every vulnerable package: {id, name, severity, title, url}
const advisories = new Map()
for (const vuln of Object.values(report.vulnerabilities || {}))
  for (const via of vuln.via || []) {
    if (typeof via !== 'object' || !via.url) continue
    const id = via.url.split('/').pop()
    advisories.set(`${via.name}:${id}`, { id, name: via.name, severity: via.severity, title: via.title, url: via.url })
  }

if (listOnly) {
  for (const a of [...advisories.values()].sort((x, y) => x.name.localeCompare(y.name)))
    console.log(`${a.severity.padEnd(9)} ${a.name.padEnd(28)} ${a.id}  ${a.title}`)
  process.exit(0)
}

const allow = JSON.parse(readFileSync(path.join(repo, '.github', 'security', 'npm-audit-allowlist.json'), 'utf8'))
const today = new Date().toISOString().slice(0, 10)
const entries = (allow.entries || []).filter((e) => e.path === rel)

let failed = 0
for (const a of advisories.values()) {
  if (!['high', 'critical'].includes(a.severity)) continue
  const entry = entries.find((e) => e.package === a.name && (e.advisories || []).includes(a.id))
  if (entry && entry.until >= today) {
    console.log(`::warning title=Accepted advisory (${rel})::${a.severity} ${a.name} ${a.id} — ${a.title}. ` +
      `Accepted until ${entry.until}: ${entry.reason}`)
    continue
  }
  failed++
  const why = entry ? `its acceptance expired on ${entry.until}` : 'not in .github/security/npm-audit-allowlist.json'
  console.log(`::error title=Vulnerable dependency (${rel})::${a.severity} ${a.name} ${a.id} — ${a.title} (${a.url}); ${why}`)
}
// stale entries are worth knowing about (the advisory is gone — drop the entry)
for (const e of entries)
  for (const id of e.advisories || [])
    if (![...advisories.values()].some((a) => a.name === e.package && a.id === id))
      console.log(`::notice title=Allowlist entry no longer needed (${rel})::${e.package} ${id}`)

if (failed > 0) {
  console.log(`\n${failed} High / Critical advisor${failed === 1 ? 'y' : 'ies'} in ${rel}. Upgrade (npm audit fix), or — ` +
    'only when no safe upgrade exists — accept it in .github/security/npm-audit-allowlist.json with a reason and a review-by date.')
  process.exit(1)
}
console.log(`${rel}: no unaccepted High / Critical advisories in production dependencies.`)
