#!/usr/bin/env node
/**
 * Contrast check for every theme × mode in src/app/globals.css.
 *
 *   node scripts/check-theme-contrast.mjs        (npm run check:contrast)
 *
 * Resolves the token cascade the way the browser does for <html class="theme-X
 * light|dark"> (`:root` → `:root.dark` → `:root.theme-X` → `:root.theme-X.dark`,
 * by specificity then source order) and checks that every TEXT ink reaches
 * 4.5:1 on every ground it is drawn on (app, panel, surface, card, well), that
 * the primary/accent foregrounds read on their fills, and the active nav item.
 * `--text-faint` is decoration (placeholders) and is only held to 3:1.
 * Exit 1 on any failure.
 */
import { readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const here = dirname(fileURLToPath(import.meta.url))
const css = readFileSync(join(here, '..', 'src', 'app', 'globals.css'), 'utf8').replace(/\/\*[\s\S]*?\*\//g, '')

// ── parse the :root blocks ───────────────────────────────────────────────────
const blocks = []
const re = /(^|\n)\s*(:root[^{]*)\{([^}]*)\}/g
let m
let order = 0
while ((m = re.exec(css))) {
  const selectors = m[2].split(',').map((s) => s.trim()).filter(Boolean)
  const vars = {}
  for (const decl of m[3].split(';')) {
    const i = decl.indexOf(':')
    if (i < 0) continue
    const k = decl.slice(0, i).trim()
    if (k.startsWith('--')) vars[k] = decl.slice(i + 1).trim()
  }
  for (const sel of selectors) {
    if (!/^:root(\.[\w-]+)*$/.test(sel)) continue // :root:not(...) etc. are not tokens
    const classes = sel.split('.').slice(1)
    blocks.push({ classes, vars, order: order++ })
  }
}

const themes = [...new Set(blocks.flatMap((b) => b.classes.filter((c) => c.startsWith('theme-')).map((c) => c.slice(6))))]
if (!themes.includes('paper')) themes.unshift('paper')

function resolve(theme, mode) {
  const have = new Set([`theme-${theme}`, mode])
  const hits = blocks
    .filter((b) => b.classes.every((c) => have.has(c)))
    .sort((a, b) => a.classes.length - b.classes.length || a.order - b.order)
  return Object.assign({}, ...hits.map((b) => b.vars))
}

// ── colour maths ─────────────────────────────────────────────────────────────
function parse(v) {
  if (!v) return null
  v = v.trim()
  let h = /^#([0-9a-f]{6})$/i.exec(v)
  if (h) {
    const n = parseInt(h[1], 16)
    return [(n >> 16) & 255, (n >> 8) & 255, n & 255]
  }
  h = /^#([0-9a-f]{3})$/i.exec(v)
  if (h) return h[1].split('').map((c) => parseInt(c + c, 16))
  const t = /^(\d+)\s+(\d+)\s+(\d+)$/.exec(v) // bare "R G B" triplet
  if (t) return [+t[1], +t[2], +t[3]]
  return null
}
const lum = ([r, g, b]) => {
  const ch = (x) => {
    const s = x / 255
    return s <= 0.03928 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4
  }
  return 0.2126 * ch(r) + 0.7152 * ch(g) + 0.0722 * ch(b)
}
const ratio = (a, b) => {
  const la = lum(a)
  const lb = lum(b)
  return (Math.max(la, lb) + 0.05) / (Math.min(la, lb) + 0.05)
}

// ── the checks ───────────────────────────────────────────────────────────────
const GROUNDS = ['--app', '--panel', '--surface', '--card', '--well']
const TEXT = [
  '--text-primary', '--text-heading', '--text-high', '--text-mid', '--text-muted',
  '--brand-ink', '--amber-ink', '--green-ink', '--red-ink', '--blue-ink', '--purple-ink', '--cyan-ink',
  '--c-brand-300', '--c-brand-200', '--c-green-300', '--c-amber-300', '--c-red-300', '--c-blue-300', '--c-purple-300', '--c-cyan-300',
]
const PAIRS = [
  ['--on-primary', '--primary', 4.5],
  ['--on-accent', '--accent', 4.5],
  ['--nav-active-text', '--nav-active-bg', 4.5],
]

let failures = 0
const rows = []
for (const theme of themes) {
  for (const mode of ['light', 'dark']) {
    const v = resolve(theme, mode)
    let worst = Infinity
    const fail = (msg) => {
      failures++
      console.log(`  ✗ ${theme}/${mode}: ${msg}`)
    }
    for (const g of GROUNDS) {
      const ground = parse(v[g])
      if (!ground) {
        fail(`${g} is not a solid colour (${v[g]})`)
        continue
      }
      for (const tk of TEXT) {
        const c = parse(v[tk])
        if (!c) {
          fail(`${tk} missing or unparsable (${v[tk]})`)
          continue
        }
        const r = ratio(c, ground)
        worst = Math.min(worst, r)
        if (r < 4.5) fail(`${tk} ${v[tk]} on ${g} ${v[g]} = ${r.toFixed(2)}:1 (< 4.5)`)
      }
      const faint = parse(v['--text-faint'])
      if (faint && ratio(faint, ground) < 2.2) fail(`--text-faint on ${g} = ${ratio(faint, ground).toFixed(2)}:1 (< 2.2)`)
    }
    for (const [fg, bg, min] of PAIRS) {
      const a = parse(v[fg])
      const b = parse(v[bg])
      if (!a || !b) {
        fail(`${fg}/${bg} missing`)
        continue
      }
      const r = ratio(a, b)
      if (r < min) fail(`${fg} ${v[fg]} on ${bg} ${v[bg]} = ${r.toFixed(2)}:1 (< ${min})`)
    }
    rows.push(`${(theme + '/' + mode).padEnd(18)} worst text ${worst.toFixed(2)}:1`)
  }
}
console.log(rows.join('\n'))
if (failures) {
  console.log(`\n${failures} contrast failure(s).`)
  process.exit(1)
}
console.log(`\nAll ${themes.length} themes × 2 modes pass.`)
