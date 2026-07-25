/**
 * Per-account VoIP engine host — ONE worker_thread per linked WhatsApp number.
 *
 * WHY THIS EXISTS: baileys-caller's WasmEngine keeps its callback registry in
 * STATIC class state:
 *     static #globalCallbackListeners = new Map()
 *     static #globalCallbacksRegistered = false
 *     if (!_a.#globalCallbacksRegistered) this.#registerGlobalCallbacks()
 * Only the FIRST engine constructed in a JS realm ever registers. A second
 * engine in the same process gets no media callbacks at all and fails SILENTLY
 * — observed live as "one call connects, the other sits on reconnecting"
 * (2026-07-23), and the reason the gateway was previously limited to one call
 * at a time. Statics are per-realm and every worker_thread is its own realm, so
 * hosting each account's engine in its own worker gives each one a private
 * registry. Verified by poc-worker-engine.mjs, nested pthread pool included.
 *
 * SPLIT OF RESPONSIBILITY — the socket cannot leave the main thread, so:
 *   main   : HTTP, Baileys socket, SignalingBridge, LiveKit room, greet gate,
 *            pacer, WAV recording, call state machine
 *   worker : WasmEngine + RelayRtcTransport + the capture buffer it mallocs
 * Audio crosses as transferable Float32Arrays (zero-copy). The WASM heap
 * pointer from engine.malloc() is meaningless outside this realm, so the
 * capture buffer is allocated and written HERE and never sent across.
 */
import { parentPort, workerData } from 'worker_threads'
import { createHmac } from 'crypto'
import path from 'path'
import { fileURLToPath, pathToFileURL } from 'url'
import { existsSync } from 'fs'

const HERE = path.dirname(fileURLToPath(import.meta.url))
const ACCOUNT = workerData?.accountId ?? '?'

function callerModuleUrl(file) {
  const p = path.join(HERE, 'node_modules', 'baileys-caller', 'dist', file)
  if (!existsSync(p)) {
    throw new Error('baileys-caller is not installed — run `npm install` in backend/baileys_service')
  }
  return pathToFileURL(p).href
}

// ---- crypto callbacks (byte-identical to calls.js — the engine rejects any
// deviation, and a wrong HKDF shows up only as a call that never connects) ----
const SHA256_LEN = 32

function computeHkdf(key, salt, info, length) {
  const effectiveSalt = salt && salt.length > 0 ? Buffer.from(salt) : Buffer.alloc(SHA256_LEN, 0)
  const prk = createHmac('sha256', effectiveSalt).update(key).digest()
  const blocks = Math.ceil(length / SHA256_LEN)
  const okm = Buffer.alloc(blocks * SHA256_LEN)
  let prev = Buffer.alloc(0)
  for (let i = 1; i <= blocks; i += 1) {
    prev = createHmac('sha256', prk).update(prev).update(info).update(Buffer.from([i])).digest()
    prev.copy(okm, (i - 1) * SHA256_LEN)
  }
  return new Uint8Array(okm.buffer, okm.byteOffset, length)
}

function computeHmacSha256(data, key) {
  const result = createHmac('sha256', Buffer.from(key)).update(data).digest()
  return new Uint8Array(result.buffer, result.byteOffset, result.byteLength)
}

const post = (type, payload) => parentPort.postMessage({ type, ...payload })
const log = (msg) => post('log', { msg })

let engine = null
let relay = null
let capture = null   // { ptr, chunkSamples } — WASM heap, worker-local

async function init(msg) {
  const [{ WasmEngine }, { RelayRtcTransport }] = await Promise.all([
    import(callerModuleUrl('wasm-engine.mjs')),
    import(callerModuleUrl('relay-transport.mjs')),
  ])

  relay = new RelayRtcTransport({
    onTransportMessage: (data, ip, port) => engine?.handleOnTransportMessage(data, ip, port),
    onIceRtt: (rttMs, ip, port) => engine?.updateIceRtt(rttMs, ip, port),
  })

  engine = new WasmEngine({
    callbacks: {
      // Signaling must go out over the account's socket, which lives on main.
      onSignalingXmpp: (peerJid, callId, xmlPayload) =>
        post('signalingXmpp', { peerJid, callId, xmlPayload }),
      onCallEvent: (eventType, eventData) => {
        // Relay-list updates are engine->relay and both live here; handling
        // them locally keeps the ICE path off the main thread entirely.
        if (eventType === 156 && eventData) {
          try { relay?.updateRelayList(JSON.parse(eventData)) } catch { /* malformed */ }
        }
        post('callEvent', { eventType, eventData })
      },
      sendDataToRelay: (data, ip, port) => relay.send(data, ip, port),
      onAudioCaptureInit: (config) => {
        const chunkSamples = (config.framesPerChunk || 320) * (config.channels || 1)
        // Allocate in THIS realm's WASM heap — the pointer never crosses.
        capture = { ptr: engine.malloc(chunkSamples * Float32Array.BYTES_PER_ELEMENT), chunkSamples }
        post('captureInit', { config, chunkSamples })
      },
      onAudioCaptureStart: () => post('captureStart', {}),
      onAudioCaptureStop: () => {
        if (capture) {
          try { engine.free(capture.ptr) } catch { /* engine already torn down */ }
          capture = null
        }
        post('captureStop', {})
      },
      onAudioPlaybackData: (audioData) => {
        // Copy out of the engine's view before transferring: the engine reuses
        // its playback buffer, so a transferred view would be neutered under it.
        const copy = Float32Array.from(audioData)
        parentPort.postMessage({ type: 'playback', audio: copy }, [copy.buffer])
      },
      cryptoHkdf: computeHkdf,
      hmacSha256: computeHmacSha256,
    },
  })

  await engine.initialize()
  engine.initVoipStack(msg.selfPnJid, msg.meUserJid, msg.selfLidJid)
  await engine.waitForVoipStackReady()
  try { engine.updateNetworkMedium(2, 0) } catch { /* best-effort QoS hint */ }
  post('ready', {})
}

// SignalingBridge on main calls these four on its attached "engine"; main
// forwards them here. Fire-and-forget in the library, so no reply is needed.
const SIGNALING_METHODS = new Set([
  'handleSignalingAck', 'handleSignalingOffer',
  'handleSignalingMessage', 'handleSignalingReceipt',
])

parentPort.on('message', async (msg) => {
  try {
    switch (msg.type) {
      case 'init':
        await init(msg)
        break

      case 'startCall':
        engine.startCall(msg.options)
        break

      case 'endCall':
        try { engine?.endCall(msg.code ?? 0, msg.flag ?? true) } catch { /* already gone */ }
        break

      case 'signaling': {
        if (!engine || !SIGNALING_METHODS.has(msg.method)) break
        const fn = engine[msg.method]
        if (typeof fn === 'function') fn.call(engine, ...(msg.args ?? []))
        break
      }

      case 'uplink': {
        // One paced 20ms chunk of agent audio, sized by the engine's own
        // capture config. Dropping it when capture is not up is correct: the
        // engine has no buffer to write into at that point.
        if (!engine || !capture) break
        engine.sendAudioData(msg.audio, capture.ptr)
        break
      }

      case 'destroy':
        try { engine?.destroy() } catch { /* best-effort */ }
        engine = null; relay = null; capture = null
        post('destroyed', {})
        break

      default:
        log(`unknown message type ${msg.type}`)
    }
  } catch (e) {
    // Never let a rejected handler kill the worker — a dead worker takes the
    // whole account's calling ability with it.
    post('workerError', { where: msg?.type, message: e?.message, stack: e?.stack })
  }
})

process.on('unhandledRejection', (e) =>
  post('workerError', { where: 'unhandledRejection', message: e?.message, stack: e?.stack }))
process.on('uncaughtException', (e) =>
  post('workerError', { where: 'uncaughtException', message: e?.message, stack: e?.stack }))

log(`call worker up for account ${ACCOUNT}`)
