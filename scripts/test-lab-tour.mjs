#!/usr/bin/env node
// Test lab browser tour — drives the dashboard the way a person would: sign in,
// open /test-lab, Run all, Load demo data, click into the conversations and
// contacts it made, optionally Reset. Saves a screenshot per step.
//
//   node scripts/test-lab-tour.mjs [--reset] [--skip-run] [--out dir]
//
// Needs playwright-core (not a repo dependency: `npm i -g playwright-core`, or
// point PLAYWRIGHT_CORE at an installed copy) and a Chromium (CHROMIUM_PATH, else
// the one playwright installed). Env: TESTLAB_WEB (default http://localhost:3000),
// TESTLAB_USER / TESTLAB_PASS (default superadmin / super123), HEADED=1 to watch.
// Exit 1 when the page reports a failed scenario or a step throws.
import { mkdirSync } from 'node:fs'
import { resolve, join } from 'node:path'
import { pathToFileURL } from 'node:url'

const WEB = (process.env.TESTLAB_WEB || 'http://localhost:3000').replace(/\/+$/, '')
const USER = process.env.TESTLAB_USER || 'superadmin'
const PASS = process.env.TESTLAB_PASS || 'super123'
const argv = process.argv.slice(2)
const flag = (f) => argv.includes(f)
const outIdx = argv.indexOf('--out')
const OUT = resolve(outIdx >= 0 ? argv[outIdx + 1] : 'test-lab-tour')

async function loadPlaywright() {
  const spec = process.env.PLAYWRIGHT_CORE
  try {
    if (spec) return await import(pathToFileURL(join(resolve(spec), 'index.mjs')).href)
    return await import('playwright-core')
  } catch {
    console.error('test-lab-tour: playwright-core not found — npm i -g playwright-core, or set PLAYWRIGHT_CORE=<dir>')
    process.exit(2)
  }
}

const { chromium } = await loadPlaywright()
mkdirSync(OUT, { recursive: true })
const browser = await chromium.launch({
  headless: !process.env.HEADED,
  ...(process.env.CHROMIUM_PATH ? { executablePath: process.env.CHROMIUM_PATH } : {}),
})
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } })
const consoleErrors = []
page.on('pageerror', (e) => consoleErrors.push(String(e)))
let n = 0
const shot = async (name) => {
  const file = join(OUT, `${String(++n).padStart(2, '0')}-${name}.png`)
  await page.screenshot({ path: file, fullPage: false })
  console.log(`  screenshot ${file}`)
}
const step = (s) => console.log(`- ${s}`)
let failed = false

try {
  step(`sign in at ${WEB}/login as ${USER}`)
  await page.goto(`${WEB}/login`)
  await page.locator('input[autocomplete="username"]').fill(USER)
  await page.locator('input[autocomplete="current-password"]').fill(PASS)
  await page.locator('button[type="submit"]').click()
  await page.waitForURL((u) => !u.pathname.startsWith('/login'), { timeout: 30_000 })

  step('open the Test lab')
  await page.goto(`${WEB}/test-lab`)
  await page.getByRole('button', { name: 'Run all' }).waitFor({ timeout: 60_000 })
  await shot('test-lab')

  if (!flag('--skip-run')) {
    step('Run all (one scenario at a time)')
    await page.getByRole('button', { name: 'Run all' }).click()
    const done = page.getByText(/^Finished: \d+ passed, \d+ failed, \d+ skipped\.$/)
    await done.waitFor({ timeout: 15 * 60_000 })
    const text = (await done.textContent()) || ''
    console.log(`  ${text}`)
    if (!/ 0 failed/.test(text)) failed = true
    await shot('run-all')
  }

  step('Load demo data')
  await page.getByRole('button', { name: 'Load demo data' }).click()
  await page.getByText(/Demo data (loaded|is already loaded)/).first().waitFor({ timeout: 120_000 })
  await shot('demo-loaded')

  const hrefs = await page
    .locator('a[href^="/chats?thread="], a[href^="/contacts/"]')
    .evaluateAll((as) => [...new Set(as.map((a) => a.getAttribute('href')))].slice(0, 6))
  for (const href of hrefs) {
    step(`open ${href}`)
    await page.goto(`${WEB}${href}`)
    await page.waitForLoadState('networkidle', { timeout: 30_000 }).catch(() => {})
    await shot(href.startsWith('/chats') ? 'conversation' : 'contact')
  }

  if (flag('--reset')) {
    step('Reset test data')
    await page.goto(`${WEB}/test-lab`)
    await page.getByRole('button', { name: 'Reset test data' }).click()
    await page.getByRole('button', { name: 'Delete test data' }).click()
    await page.getByText(/^Test data deleted/).waitFor({ timeout: 120_000 })
    await shot('reset')
  }
} catch (e) {
  failed = true
  console.error(`test-lab-tour: ${e.message}`)
  await shot('error').catch(() => {})
} finally {
  await browser.close()
}

if (consoleErrors.length) {
  console.log(`page errors (${consoleErrors.length}):`)
  for (const e of consoleErrors.slice(0, 10)) console.log(`  ${e}`)
}
console.log(failed ? 'tour: FAILED' : 'tour: ok')
process.exit(failed ? 1 : 0)
