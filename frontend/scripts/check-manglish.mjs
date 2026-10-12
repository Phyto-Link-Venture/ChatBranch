// Checks the Manglish overlay (src/i18n/messages/manglish) against the English
// source: every key exists in English, every {placeholder} and <tag> is kept,
// plural pairs (x_one / x_other) are rewritten together, nothing is empty.
// TypeScript already refuses unknown keys; this catches what types cannot.
//   node scripts/check-manglish.mjs            (all namespaces)
//   node scripts/check-manglish.mjs chats ai   (some)
import { createRequire } from 'module'
const require = createRequire(import.meta.url)
const createJiti = require('jiti')
const jiti = createJiti(import.meta.url, { interopDefault: true })
const { en } = jiti('../src/i18n/messages/en/index.ts')
const { manglish } = jiti('../src/i18n/messages/manglish/index.ts')

const only = process.argv.slice(2)
const problems = []
let rewritten = 0
let total = 0

const tokens = (s) => [...s.matchAll(/\{\w+\}|<\/?[a-zA-Z][\w-]*\s*\/?>/g)].map((m) => m[0]).sort().join(' ')

function countStrings(node) {
  if (typeof node === 'string') return 1
  return Object.values(node).reduce((n, v) => n + countStrings(v), 0)
}

function walk(over, src, path) {
  for (const [k, v] of Object.entries(over)) {
    const p = `${path}.${k}`
    if (!(k in src)) { problems.push(`${p}: not an English key`); continue }
    const s = src[k]
    if (typeof v === 'string') {
      if (typeof s !== 'string') { problems.push(`${p}: English has a group here`); continue }
      rewritten++
      if (!v.trim()) problems.push(`${p}: empty`)
      if (tokens(v) !== tokens(s)) problems.push(`${p}: placeholders/tags differ — English "${tokens(s)}", Manglish "${tokens(v)}"`)
      const m = k.match(/^(.*)_(one|other)$/)
      if (m) {
        const twin = `${m[1]}_${m[2] === 'one' ? 'other' : 'one'}`
        if (twin in src && !(twin in over)) problems.push(`${p}: rewrite its plural twin ${twin} too`)
      }
    } else if (typeof s === 'string') problems.push(`${p}: English has a string here`)
    else walk(v, s, p)
  }
}

for (const [ns, over] of Object.entries(manglish)) {
  if (only.length && !only.includes(ns)) continue
  total += countStrings(en[ns])
  walk(over, en[ns], ns)
}
for (const p of problems) console.log('✗ ' + p)
console.log(`${problems.length ? '✗' : '✓'} ${rewritten} of ${total} strings rewritten${only.length ? ` (${only.join(', ')})` : ''}, ${problems.length} problem(s)`)
process.exit(problems.length ? 1 : 0)
