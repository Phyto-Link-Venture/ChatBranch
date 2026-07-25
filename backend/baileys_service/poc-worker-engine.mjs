/**
 * Proof of concept for the per-account worker refactor.
 *
 * Hypothesis: baileys-caller's WasmEngine cannot run twice in ONE JS realm,
 * because its callback registry is static:
 *     static #globalCallbackListeners = new Map()
 *     static #globalCallbacksRegistered = false
 *     if (!_a.#globalCallbacksRegistered) this.#registerGlobalCallbacks()
 * The second engine skips registration entirely, so its media callbacks never
 * fire -> "reconnecting" on whichever account dials second.
 *
 * Statics are per-REALM, and each worker_thread is its own realm, so one worker
 * per account should give each engine its own registry. The risk this PoC
 * checks is nesting: the engine spawns its own pthread Worker pool, so we would
 * be creating workers INSIDE a worker.
 *
 * Run:  node poc-worker-engine.mjs
 */
import { Worker, isMainThread, parentPort, workerData } from 'worker_threads'
import path from 'path'
import { fileURLToPath, pathToFileURL } from 'url'
import { existsSync } from 'fs'

const HERE = path.dirname(fileURLToPath(import.meta.url))

function callerModuleUrl(file) {
  const p = path.join(HERE, 'node_modules', 'baileys-caller', 'dist', file)
  if (!existsSync(p)) throw new Error(`missing ${p} — run npm install`)
  return pathToFileURL(p).href
}

// ---- crypto callbacks the engine requires (mirrors calls.js) ----
import { createHmac, hkdfSync } from 'crypto'
const computeHkdf = (key, salt, info, length) =>
  new Uint8Array(hkdfSync('sha256', Buffer.from(key), Buffer.from(salt), Buffer.from(info), length))
const computeHmacSha256 = (data, key) =>
  new Uint8Array(createHmac('sha256', Buffer.from(key)).update(Buffer.from(data)).digest())

if (!isMainThread) {
  const id = workerData.id
  try {
    const { WasmEngine } = await import(callerModuleUrl('wasm-engine.mjs'))
    const engine = new WasmEngine({
      callbacks: { cryptoHkdf: computeHkdf, hmacSha256: computeHmacSha256 },
    })
    await engine.initialize()
    // The real question: did THIS realm register its own global callbacks?
    // If the static guard were shared, the 2nd engine would silently skip it.
    parentPort.postMessage({ id, ok: true, msg: 'engine initialized in worker' })
    engine.destroy()
  } catch (e) {
    parentPort.postMessage({ id, ok: false, msg: `${e.message}` })
  }
} else {
  // --- control: two engines in ONE realm (expected to expose the collision) ---
  console.log('--- control: two engines in the SAME realm ---')
  const { WasmEngine } = await import(callerModuleUrl('wasm-engine.mjs'))
  const a = new WasmEngine({ callbacks: { cryptoHkdf: computeHkdf, hmacSha256: computeHmacSha256 } })
  await a.initialize()
  const b = new WasmEngine({ callbacks: { cryptoHkdf: computeHkdf, hmacSha256: computeHmacSha256 } })
  await b.initialize()
  // Read the static guard reflectively: if it is already true when engine B
  // initializes, B never registered its own callbacks.
  console.log('  both constructed+initialized in one realm (no throw)')
  console.log('  NOTE: no throw != working — B skipped #registerGlobalCallbacks')
  a.destroy(); b.destroy()

  console.log('\n--- test: one engine per worker_thread (2 workers) ---')
  const results = await Promise.all([1, 2].map((id) => new Promise((resolve) => {
    const w = new Worker(new URL(import.meta.url), { workerData: { id } })
    w.once('message', (m) => { resolve(m); w.terminate() })
    w.once('error', (e) => resolve({ id, ok: false, msg: `worker error: ${e.message}` }))
    w.once('exit', (c) => resolve({ id, ok: false, msg: `worker exited early (${c})` }))
  })))
  for (const r of results) console.log(`  worker ${r.id}: ${r.ok ? 'OK' : 'FAIL'} — ${r.msg}`)
  const allOk = results.every((r) => r.ok)
  console.log(`\nVERDICT: ${allOk ? 'worker-per-account is VIABLE' : 'worker isolation FAILED'}`)
  process.exit(allOk ? 0 : 1)
}
