/**
 * Voice calls for Baileys accounts — WhatsApp Web's VoIP stack + LiveKit.
 *
 * Composes baileys-caller's internals (SignalingBridge / WasmEngine /
 * RelayRtcTransport, MIT © ShellTear) around the gateway's EXISTING account
 * socket. We deliberately do NOT use its top-level VoipClient: that would
 * open a second WhatsApp Web connection with the same creds and the server
 * would kick one of them (stream conflict). One socket per account does both
 * messaging and calls — exactly like WhatsApp Web in a browser.
 *
 * Audio path (all in-process, no ffmpeg):
 *   contact -> WASM onAudioPlaybackData (f32 mono) -> LiveKit AudioSource
 *   agent    -> LiveKit AudioStream -> ring buffer -> paced engine.sendAudioData
 *
 * The AI voice agent is dispatched into the LiveKit room by Django before it
 * asks us to place the call; we join as "whatsapp_contact" so the agent
 * treats a Baileys call exactly like a Cloud API bridge call.
 *
 * ⚠ Same warning as index.js, doubly so for calls: this is WhatsApp's
 * unofficial protocol. Automated outbound calls violate WhatsApp's ToS and
 * get numbers banned — use a dedicated number and keep volumes low.
 */
import path from 'path'
import { existsSync, mkdirSync, writeFileSync } from 'fs'
import { fileURLToPath, pathToFileURL } from 'url'
import { randomBytes, createHmac } from 'crypto'
import { Worker } from 'worker_threads'

const HERE = path.dirname(fileURLToPath(import.meta.url))

// ---- call recording (local stereo WAV: L=contact, R=agent) ------------------
// Both audio directions are captured off the existing real-time pacer (no I/O in
// the hot path — the file is written once at call end), so this adds no latency.
const RECORD_ENABLED = process.env.BAILEYS_RECORD_CALLS !== '0' // on by default
const RECORD_DIR = path.join(HERE, '..', 'data', 'whatsapp', 'calls', 'recordings')

// ---- contact noise gate ------------------------------------------------------
// Frames quieter than BAILEYS_GATE_DB (dBFS, negative; "0" disables) are muted
// BEFORE they reach LiveKit, so line hiss / distant voices never hit the agent's
// VAD+STT. The gate stays open BAILEYS_GATE_HOLD_MS after the last loud frame so
// natural mid-sentence dips aren't chopped. The recording keeps the RAW audio —
// what you hear in the WAV is the line, not what the agent was given.
const GATE_DB = Number(process.env.BAILEYS_GATE_DB ?? -30)
const GATE_ENABLED = Number.isFinite(GATE_DB) && GATE_DB < 0
const GATE_HOLD_MS = Math.max(0, Number(process.env.BAILEYS_GATE_HOLD_MS ?? 400))
// BAILEYS_GATE_DEBUG=1: print the contact's peak level (dBFS) once per second
// during a call — speak normally, then stay quiet, and read the two numbers to
// pick a BAILEYS_GATE_DB threshold between them.
const GATE_DEBUG = process.env.BAILEYS_GATE_DEBUG === '1'
// Energy floor that counts as "the callee's audio is really flowing", used to
// open the greeting gate. It has to beat WhatsApp's RINGING comfort frames,
// which are NOT digital silence: measured on a dead-media call (2026-07-23,
// +601133199692) the contact channel peaked at -42.6 dBFS with a whole-call
// RMS of -78.3 dBFS while nobody was ever on the line. The original -70 floor
// sat below that ring noise, so the gate opened at ~6.6s, the agent greeted a
// dead line, burned its greeting retries and quit as system:no_answer at 12s.
// Keep this ABOVE ring noise but well below the noise gate (BAILEYS_GATE_DB,
// default -30): it only has to prove "a live mic is attached", not detect speech.
//
// A single loud frame is the wrong test in BOTH directions — raise the floor
// far enough to clear a ring-noise spike and a callee in a quiet room stops
// tripping it at all (their open mic idles well under -45), which costs a full
// fallback of dead air before the greeting. So require SUSTAINED energy
// instead: an attached mic streams continuously, ring noise spikes sporadically.
// Measured 2026-07-23 at a 20ms window: dead line 0/610 windows active, live
// callee 141/2037 — the two separate cleanly on persistence, not peak level.
// Absolute floor — a sanity bound only; the real test is the ADAPTIVE one below.
const GREET_AUDIO_DB = Number(process.env.BAILEYS_GREET_AUDIO_DB ?? -55)
const GREET_AUDIO_FRAMES = Math.max(1, Number(process.env.BAILEYS_GREET_AUDIO_FRAMES ?? 5))
// Any FIXED threshold has now failed in both directions on real lines: -70 and
// -55 both opened on ring-side comfort noise (agent audio at 0.2-0.4s while the
// callee only appeared at 11-15s, 2026-07-23), and raising it far enough to
// clear that noise would miss a callee in a quiet room. So calibrate per call:
// WhatsApp's ringing comfort noise is STEADY, which means the quietest frames of
// the opening stretch are this line's own noise floor. Require speech to clear
// that measured floor by a wide margin before calling it a pickup.
const GREET_FLOOR_FRAMES = Math.max(1, Number(process.env.BAILEYS_GREET_FLOOR_FRAMES ?? 25))
const GREET_MARGIN_DB = Math.max(3, Number(process.env.BAILEYS_GREET_MARGIN_DB ?? 15))

function frameDb(f32) {
  let sum = 0
  for (let i = 0; i < f32.length; i++) sum += f32[i] * f32[i]
  const rms = Math.sqrt(sum / (f32.length || 1))
  return rms > 0 ? 20 * Math.log10(rms) : -Infinity
}

function writeStereoWav(filepath, dataBuffers, sampleRate) {
  const data = Buffer.concat(dataBuffers)
  const header = Buffer.alloc(44)
  header.write('RIFF', 0)
  header.writeUInt32LE(36 + data.length, 4)
  header.write('WAVE', 8)
  header.write('fmt ', 12)
  header.writeUInt32LE(16, 16)
  header.writeUInt16LE(1, 20) // PCM
  header.writeUInt16LE(2, 22) // stereo
  header.writeUInt32LE(sampleRate, 24)
  header.writeUInt32LE(sampleRate * 2 * 2, 28) // byte rate = rate * channels * bytes
  header.writeUInt16LE(4, 32) // block align = channels * bytes
  header.writeUInt16LE(16, 34) // bits per sample
  header.write('data', 36)
  header.writeUInt32LE(data.length, 40)
  mkdirSync(path.dirname(filepath), { recursive: true })
  writeFileSync(filepath, Buffer.concat([header, data]))
}

// baileys-caller's exports map only exposes its VoipClient entry point, so the
// composable internals are imported by file path from the installed package.
function callerModuleUrl(file) {
  const root = path.join(HERE, 'node_modules', 'baileys-caller')
  if (!existsSync(path.join(root, 'dist', file))) {
    throw new Error('baileys-caller is not installed — run `npm install` in backend/baileys_service')
  }
  return pathToFileURL(path.join(root, 'dist', file)).href
}

let mods = null // lazy: the WASM engine + wrtc + livekit are heavy
async function loadModules() {
  if (mods) return mods
  const [{ SignalingBridge }, { WasmEngine }, { RelayRtcTransport }, { CallState }, lk] =
    await Promise.all([
      import(callerModuleUrl('signaling.mjs')),
      import(callerModuleUrl('wasm-engine.mjs')),
      import(callerModuleUrl('relay-transport.mjs')),
      import(callerModuleUrl('types.mjs')),
      import('@livekit/rtc-node'),
    ])
  mods = { SignalingBridge, WasmEngine, RelayRtcTransport, CallState, lk }
  return mods
}

// ---- crypto callbacks the WASM engine needs (from baileys-caller index.mts) --

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

const toBareJid = (jid) => {
  if (!jid) return jid
  const at = jid.indexOf('@')
  if (at < 0) return jid
  return `${jid.slice(0, at).split(':')[0]}@${jid.slice(at + 1)}`
}

const isCallReceiptNode = (node) => {
  if (node?.tag !== 'receipt') return false
  const child = Array.isArray(node.content) ? node.content[0] : null
  return !!(child?.attrs?.['call-id'] || child?.attrs?.call_id)
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

// ---- per-account VoIP session ------------------------------------------------

/** account id -> { sock, engine, signaling, relay } */
const sessions = new Map()
/** account id -> the single live call on THAT account.
 *  WhatsApp allows one voice call per number, but each account owns its own
 *  socket, SignalingBridge and WasmEngine (see `sessions`), so calls on
 *  DIFFERENT accounts are independent and run concurrently. This replaced a
 *  gateway-wide singleton, which rejected a second contact's call even when
 *  another account was free — the engine callbacks below also routed audio to
 *  whichever call happened to be global, so they now resolve per account. */
const activeCalls = new Map()

// One worker_thread per account hosts that account's WasmEngine, so each gets a
// private copy of the engine's STATIC callback registry. In a single realm only
// the first engine ever registers its callbacks and every later account fails
// silently — one call connects, the rest sit on "reconnecting" (2026-07-23).
// BAILEYS_CALL_WORKERS=0 restores the old in-process path (one working call at
// a time, gateway-wide) if a worker ever misbehaves in the field.
const CALL_WORKERS = process.env.BAILEYS_CALL_WORKERS !== '0'

/** Spawn this account's engine worker and hang a same-shaped proxy off the
 *  session, so nothing downstream knows the engine moved off-thread. */
async function startEngineWorker(session, accountId, sock) {
  const worker = new Worker(new URL('./call-worker.mjs', import.meta.url), {
    workerData: { accountId },
  })
  session.worker = worker
  worker.unref()  // a live worker must not hold the gateway open on shutdown

  let onReady, onFailed
  const ready = new Promise((res, rej) => { onReady = res; onFailed = rej })

  worker.on('message', (msg) => {
    switch (msg.type) {
      case 'ready':        onReady(); break
      case 'log':          console.log(`[${accountId}] worker: ${msg.msg}`); break
      case 'signalingXmpp':
        session.signaling.sendSignaling(msg.peerJid, msg.callId, msg.xmlPayload); break
      case 'callEvent':    session.onCallEvent?.(msg.eventType, msg.eventData); break
      case 'captureInit':  activeCalls.get(accountId)?._captureInit(msg.config); break
      case 'captureStart': activeCalls.get(accountId)?._captureStart(); break
      case 'captureStop':  activeCalls.get(accountId)?._captureStop(); break
      case 'playback':     session.onPlayback?.(msg.audio); break
      case 'workerError':
        console.error(`[${accountId}] worker error in ${msg.where}: ${msg.message}`)
        if (msg.stack) console.error(msg.stack)
        onFailed(new Error(msg.message))
        break
      default: break
    }
  })
  worker.on('error', (e) => {
    console.error(`[${accountId}] engine worker crashed:`, e.message)
    onFailed(e)
    const live = activeCalls.get(accountId)
    if (live && !live.ended) live.end('worker_crash')
  })
  worker.on('exit', (code) => {
    if (code !== 0) console.error(`[${accountId}] engine worker exited (${code})`)
    // A dead worker means a dead engine: drop the session so the next dial
    // rebuilds it rather than posting into a terminated thread.
    if (sessions.get(accountId) === session) sessions.delete(accountId)
  })

  // Same method names the in-process engine exposes, so BridgedCall and
  // SignalingBridge both keep working untouched. malloc/free are no-ops: the
  // WASM heap pointer is worker-local, allocated there and never sent across.
  const send = (type, payload, transfer) => worker.postMessage({ type, ...payload }, transfer)
  session.engine = {
    startCall: (options) => send('startCall', { options }),
    endCall: (code, flag) => send('endCall', { code, flag }),
    malloc: () => 0,
    free: () => {},
    // NOT transferred: the pacer reuses `chunk` for the WAV recording on the
    // very next line, and transferring would neuter it mid-tick.
    sendAudioData: (chunk) => send('uplink', { audio: chunk }),
    handleSignalingAck: (...args) => send('signaling', { method: 'handleSignalingAck', args }),
    handleSignalingOffer: (...args) => send('signaling', { method: 'handleSignalingOffer', args }),
    handleSignalingMessage: (...args) => send('signaling', { method: 'handleSignalingMessage', args }),
    handleSignalingReceipt: (...args) => send('signaling', { method: 'handleSignalingReceipt', args }),
    destroy: () => send('destroy', {}),
  }

  send('init', {
    selfPnJid: sock.authState.creds.me?.id,
    meUserJid: toBareJid(sock.authState.creds.me?.id),
    selfLidJid: sock.authState.creds.me?.lid,
  })
  await ready
  console.log(`[${accountId}] engine worker ready (concurrent calling enabled)`)
}

/** Build (or reuse) the VoIP stack bound to this account's live socket.
 *  Rebuilt whenever the gateway reconnects and hands us a new sock object. */
async function ensureSession(accountId, sock) {
  const { SignalingBridge, WasmEngine, RelayRtcTransport } = await loadModules()
  const existing = sessions.get(accountId)
  if (existing && existing.sock === sock) return existing
  if (existing) destroySession(accountId)

  const session = { sock, engine: null, signaling: null, relay: null, worker: null,
                    onPlayback: null, onCallEvent: null }
  session.signaling = new SignalingBridge({ sock })
  await session.signaling.init()

  if (CALL_WORKERS) {
    // The engine + relay live in a dedicated worker_thread so this account gets
    // its OWN copy of WasmEngine's static callback registry (see
    // call-worker.mjs). session.engine is a proxy with the same method names,
    // so BridgedCall — pacer, recording, greet gate — is unchanged.
    await startEngineWorker(session, accountId, sock)
  } else {
    session.relay = new RelayRtcTransport({
      onTransportMessage: (data, ip, port) => session.engine?.handleOnTransportMessage(data, ip, port),
      onIceRtt: (rttMs, ip, port) => session.engine?.updateIceRtt(rttMs, ip, port),
    })
    session.engine = new WasmEngine({
      callbacks: {
        onSignalingXmpp: (peerJid, callId, xmlPayload) => session.signaling.sendSignaling(peerJid, callId, xmlPayload),
        onCallEvent: (eventType, eventData) => session.onCallEvent?.(eventType, eventData),
        sendDataToRelay: (data, ip, port) => session.relay.send(data, ip, port),
        onAudioCaptureInit: (config) => activeCalls.get(accountId)?._captureInit(config),
        onAudioCaptureStart: () => activeCalls.get(accountId)?._captureStart(),
        onAudioCaptureStop: () => activeCalls.get(accountId)?._captureStop(),
        onAudioPlaybackData: (audioData) => session.onPlayback?.(audioData),
        cryptoHkdf: computeHkdf,
        hmacSha256: computeHmacSha256,
      },
    })
    await session.engine.initialize()
    const selfPnJid = sock.authState.creds.me?.id
    const selfLidJid = sock.authState.creds.me?.lid
    session.engine.initVoipStack(selfPnJid, toBareJid(selfPnJid), selfLidJid)
    await session.engine.waitForVoipStackReady()
    try { session.engine.updateNetworkMedium(2, 0) } catch { /* best-effort QoS hint */ }
  }
  session.signaling.attachEngine(session.engine)

  sock.ws.on('CB:call', (node) => {
    // Remote hangup, belt and braces: the WASM engine is supposed to emit a
    // call_state event when the peer ends, but field calls show the terminate
    // stanza arriving with no engine reaction — so the agent kept talking
    // until the operator hit End. The stanza is definitive; act on it directly.
    try {
      const kids = Array.isArray(node?.content) ? node.content : []
      // Only this account's call — a terminate on one number must never tear
      // down a call running concurrently on another.
      const live = activeCalls.get(accountId)
      for (const child of kids) {
        if (child?.tag !== 'terminate' && child?.tag !== 'reject') continue
        const cid = String(child.attrs?.['call-id'] ?? child.attrs?.call_id ?? '')
        if (live && !live.ended && (!cid || cid === live.callId)) {
          console.log(`[${accountId}] peer ${child.tag} (call ${cid || '?'}) — ending bridged call`)
          live.end('remote_end')
        }
      }
    } catch { /* a malformed node must never break signaling */ }
    session.signaling.processIncomingCall(node, session.engine, activeCalls.get(accountId)?.callId ?? '')
  })
  sock.ws.on('CB:receipt', (node) => {
    if (!isCallReceiptNode(node)) return
    session.signaling.processIncomingReceipt(node, session.engine, activeCalls.get(accountId)?.callId ?? '')
  })

  sessions.set(accountId, session)
  return session
}

export function destroySession(accountId) {
  const s = sessions.get(accountId)
  if (!s) return
  const live = activeCalls.get(accountId)
  if (live && !live.ended) live.end('gateway_reset')
  try { s.relay?.closeAll() } catch { /* teardown */ }
  try { s.engine?.destroy() } catch { /* teardown */ }
  // The worker owns this account's engine + relay; terminating it releases the
  // WASM heap and its pthread pool. Give destroy() a moment to run first.
  if (s.worker) {
    const w = s.worker
    s.worker = null
    setTimeout(() => { w.terminate().catch(() => {}) }, 250)
  }
  sessions.delete(accountId)
}

// ---- one bridged call ----------------------------------------------------------

class BridgedCall {
  constructor({ accountId, waId, session, room, onEvent, maxSeconds, livekitUrl, token }) {
    this.accountId = accountId
    this.waId = waId
    this.callId = ('00' + randomBytes(16).toString('hex').slice(2)).toUpperCase()
    this.session = session
    this.roomName = room || null
    this.onEvent = onEvent
    // LiveKit join happens EARLY (during ringing) so the room/track setup is
    // already paid for when the callee answers — the agent is told not to
    // greet yet via the participant attribute wa.callStatus=ringing, flipped
    // to "active" in _onAnswered(). Audio into the room is gated on
    // this.active so the agent never hears ringback.
    this.livekitUrl = livekitUrl
    this.token = token
    // Join state is a promise, not a bare boolean: the old `joined` flag was
    // set optimistically BEFORE the (slow, awaited) connect, so if that connect
    // then failed AFTER _onAnswered had already seen the flag true and skipped
    // its own join, nobody ever rejoined — the LiveKit room had no contact and
    // the whole call was one-way / dead ("user never entered the room", field
    // reports). _ensureJoined() dedupes an in-flight attempt and RETRIES a
    // failed one, so both the early (ringing) and pickup paths converge on a
    // real join.
    this._joined = false
    this._joinPromise = null
    this.active = false
    this.status = 'connecting'
    this.startedAt = Date.now()
    this.ended = false
    this.lkRoom = null
    this.lkSource = null
    this.playQueue = Promise.resolve()
    this.uplink = [] // Float32Array chunks of agent speech
    this.uplinkSamples = 0
    this.capture = null // { ptr, sampleRate, channels, framesPerChunk }
    this.pacer = null
    this.readers = []
    // recording: contact audio buffered here, mixed with the agent chunk each
    // pacer tick into stereo s16 blocks (recChunks), flushed to a WAV on end.
    this.rec = RECORD_ENABLED
    this.recChunks = []
    this.recContact = [] // Float32Array queue of contact playback audio
    this.recContactSamples = 0
    this.recName = `${this.waId}-${this.startedAt}.wav`
    this.gateOpenUntil = 0 // noise gate: epoch ms until which contact audio passes
    // Greeting gate: wa.callStatus flips to "active" (agent speaks) only once
    // the first contact audio frame arrives AFTER engine-Active — the engine
    // reports Active while the callee's phone is still bringing its audio
    // session up, and a greeting fired then plays into dead air (field case:
    // greeting at t=0-3s of the recording, first contact audio at t≈7s, the
    // callee never heard any opening). Timer fallback so the agent is never
    // muted forever if the engine delivers no early frames.
    this.greetGateOpen = false
    this.greetGateTimer = null
    this.greetAudioRun = 0   // consecutive above-floor contact frames
    // One-way-audio detector: WhatsApp's inbound media stream sometimes never
    // (or barely) flows — the contact talks into the void and the agent hears
    // nothing (field case 2026-07-21: 60s call, ONE 1s audio burst). Track the
    // last inbound frame and log loudly when the stream stalls mid-call, so
    // "contact speaks, no transcript" is diagnosable from the gateway log.
    this.lastPlaybackAt = 0
    this.mediaWatch = null
    this.mediaStallLogged = false
    this.endTimer = setTimeout(() => this.end('timeout'), Math.max(30, maxSeconds || 300) * 1000)
  }

  // -- WASM engine audio callbacks (wired via ensureSession) --

  _captureInit(config) {
    const chunkSamples = (config.framesPerChunk || 320) * (config.channels || 1)
    this.capture = {
      sampleRate: config.sampleRate || 16000,
      channels: config.channels || 1,
      framesPerChunk: config.framesPerChunk || 320,
      chunkSamples,
      ptr: this.session.engine.malloc(chunkSamples * Float32Array.BYTES_PER_ELEMENT),
    }
    this.capturedRate = this.capture.sampleRate // survives _captureStop for the WAV
  }

  _captureStart() {
    if (!this.capture || this.pacer) return
    // Replaces baileys-caller's file/ffmpeg AudioFeeder: emit one chunk of
    // agent audio (or silence) every chunk-duration, self-correcting drift.
    const chunkMs = (this.capture.framesPerChunk / this.capture.sampleRate) * 1000
    let next = Date.now()
    const tick = () => {
      if (this.ended || !this.capture) return
      const chunk = this._pullUplink(this.capture.chunkSamples)
      try { this.session.engine.sendAudioData(chunk, this.capture.ptr) } catch { /* engine gone */ }
      // record this time slice: contact (left) + agent (right), same clock
      if (this.rec) this._recordSlice(this._drainContact(this.capture.chunkSamples), chunk)
      next += chunkMs
      this.pacer = setTimeout(tick, Math.max(0, next - Date.now()))
    }
    tick()
  }

  /** Pull n contact samples (silence-padded) — the recording's left channel. */
  _drainContact(n) {
    const out = new Float32Array(n)
    let offset = 0
    while (offset < n && this.recContact.length > 0) {
      const head = this.recContact[0]
      const take = Math.min(n - offset, head.length)
      out.set(head.subarray(0, take), offset)
      offset += take
      this.recContactSamples -= take
      if (take === head.length) this.recContact.shift()
      else this.recContact[0] = head.subarray(take)
    }
    return out
  }

  /** Interleave contact(L)+agent(R) into one stereo s16 block. */
  _recordSlice(left, right) {
    // Anchor for transcript<->audio sync: the WAV's t=0 is THIS moment (first
    // paced chunk), not the call's construction time in the filename — the
    // difference is the whole ring duration, which skewed the UI highlight.
    if (!this.recStartedAt) this.recStartedAt = Date.now()
    const n = right.length
    const buf = Buffer.alloc(n * 4)
    for (let i = 0; i < n; i++) {
      const l = Math.max(-1, Math.min(1, left[i] || 0))
      const r = Math.max(-1, Math.min(1, right[i] || 0))
      buf.writeInt16LE(Math.round(l < 0 ? l * 0x8000 : l * 0x7fff), i * 4)
      buf.writeInt16LE(Math.round(r < 0 ? r * 0x8000 : r * 0x7fff), i * 4 + 2)
    }
    this.recChunks.push(buf)
  }

  _captureStop() {
    if (this.pacer) { clearTimeout(this.pacer); this.pacer = null }
    if (this.capture) {
      try { this.session.engine.free(this.capture.ptr) } catch { /* engine gone */ }
      this.capture = null
    }
  }

  _pullUplink(n) {
    const out = new Float32Array(n) // zero-filled = silence
    let offset = 0
    while (offset < n && this.uplink.length > 0) {
      const head = this.uplink[0]
      const take = Math.min(n - offset, head.length)
      out.set(head.subarray(0, take), offset)
      offset += take
      this.uplinkSamples -= take
      if (take === head.length) this.uplink.shift()
      else this.uplink[0] = head.subarray(take)
    }
    return out
  }

  _pushUplink(f32) {
    this.uplink.push(f32)
    this.uplinkSamples += f32.length
    // Bound agent->contact latency: if the agent's audio arrives faster than
    // WhatsApp drains it, a standing backlog delays every reply the caller
    // hears. Cap at ~1s (was 4s) — drop the oldest to catch up if it builds.
    const cap = (this.capture?.sampleRate || 16000) * 1 // ~1s
    while (this.uplinkSamples > cap && this.uplink.length > 1) {
      this.uplinkSamples -= this.uplink.shift().length
    }
  }

  // -- LiveKit leg --

  async joinRoom(livekitUrl, token) {
    const { lk } = await loadModules()
    const { Room, RoomEvent, AudioSource, LocalAudioTrack, TrackPublishOptions, TrackSource, TrackKind, AudioStream, AudioFrame } = lk
    this.AudioFrame = AudioFrame
    this.lkRoom = new Room()

    // agent -> contact: subscribe and feed the capture pacer. Registered
    // BEFORE connect(): the agent is dispatched seconds before we join, so
    // its track already exists — with autoSubscribe the TrackSubscribed
    // event for it can fire during connect()/publish awaits, and a handler
    // attached after them misses it: call connects, agent speaks, customer
    // hears SILENCE (field case 2026-07-22, agent channel flat zero in the
    // recording). A post-connect sweep below catches the already-subscribed.
    const consumedTracks = new Set()
    const consume = (rtcTrack) => {
      if (!rtcTrack || rtcTrack.kind !== TrackKind.KIND_AUDIO) return
      const sid = rtcTrack.sid || ''
      if (sid && consumedTracks.has(sid)) return
      if (sid) consumedTracks.add(sid)
      const rate = this.capture?.sampleRate || 16000
      const stream = new AudioStream(rtcTrack, { sampleRate: rate, numChannels: 1 })
      const reader = stream.getReader()
      this.readers.push(reader)
      ;(async () => {
        try {
          for (;;) {
            const { done, value } = await reader.read()
            if (done || this.ended) break
            const data = value.data // Int16Array
            const f32 = new Float32Array(data.length)
            for (let i = 0; i < data.length; i++) f32[i] = data[i] / 0x8000
            this._pushUplink(f32)
          }
        } catch { /* reader released on teardown */ }
      })()
    }
    this.lkRoom.on(RoomEvent.TrackSubscribed, (rtcTrack) => consume(rtcTrack))
    this.lkRoom.on(RoomEvent.Disconnected, () => this.end('room_closed'))

    await this.lkRoom.connect(livekitUrl, token, { autoSubscribe: true, dynacast: false })

    // contact -> agent: publish what the WASM stack plays back to us
    const PLAYBACK_RATE = 16000 // baileys-caller delivers 16 kHz mono f32
    // queueSize (ms) is buffering latency before the contact's audio reaches
    // the agent — the SDK default is 1000ms, far too much for a live call.
    // 200ms keeps jitter tolerance while cutting ~0.8s off reply latency.
    this.lkSource = new AudioSource(PLAYBACK_RATE, 1, 200)
    const track = LocalAudioTrack.createAudioTrack('wa-audio', this.lkSource)
    const publishOpts = new TrackPublishOptions()
    publishOpts.source = TrackSource.SOURCE_MICROPHONE
    await this.lkRoom.localParticipant.publishTrack(track, publishOpts)
    this.session.onPlayback = (f32) => {
      if (this.ended || !this.lkSource || !this.active) return // no ringback to the agent
      // Open the greeting gate on the first frame carrying REAL ENERGY, not
      // merely the first frame to arrive. WhatsApp streams all-zero comfort
      // frames while the callee's phone is still ringing, so "a frame showed
      // up" proved nothing — the gate opened at t=0, the agent greeted a
      // ringing line, burned all its GREETING_RETRIES, and hung up as
      // system:no_answer at ~17s just as the human picked up at ~15-21s
      // (field case 2026-07-23: recording had agent audio at t=0.0s and the
      // customer channel digitally silent until t=15.0s).
      if (!this.greetGateOpen) {
        // Learn THIS line's noise floor from its quietest frames, then demand a
        // sustained run well above it — steady ring noise can never clear its
        // own floor by GREET_MARGIN_DB, but a live mic does immediately.
        const db = frameDb(f32)
        if (Number.isFinite(db)) {
          this.greetFloorDb = this.greetFloorDb === undefined
            ? db : Math.min(this.greetFloorDb, db)
          this.greetFloorFrames = (this.greetFloorFrames ?? 0) + 1
        }
        const calibrated = (this.greetFloorFrames ?? 0) >= GREET_FLOOR_FRAMES
        const bar = Math.max((this.greetFloorDb ?? -Infinity) + GREET_MARGIN_DB, GREET_AUDIO_DB)
        const loud = calibrated && Number.isFinite(db) && db > bar
        this.greetAudioRun = loud ? (this.greetAudioRun ?? 0) + 1 : 0
        if (this.greetAudioRun >= GREET_AUDIO_FRAMES) {
          console.log(`[${this.accountId}] pickup detected: ${db.toFixed(1)} dBFS over ` +
                      `floor ${this.greetFloorDb.toFixed(1)} (bar ${bar.toFixed(1)})`)
          this._openGreetGate('first_audio')
        }
      }
      this.lastPlaybackAt = Date.now()
      if (this.mediaStallLogged) {
        this.mediaStallLogged = false
        console.log(`[${this.accountId}] inbound audio RESUMED after stall`)
      }
      const pcm = new Int16Array(f32.length)
      for (let i = 0; i < f32.length; i++) {
        const s = Math.max(-1, Math.min(1, f32[i]))
        pcm[i] = s < 0 ? s * 0x8000 : s * 0x7fff
      }
      // tee a copy into the recording's contact channel (before the pacer reads it)
      if (this.rec) {
        this.recContact.push(f32.slice(0))
        this.recContactSamples += f32.length
        const cap = PLAYBACK_RATE * 2 // don't let it grow unbounded if the pacer stalls
        while (this.recContactSamples > cap && this.recContact.length > 1) {
          this.recContactSamples -= this.recContact.shift().length
        }
      }
      // noise gate: mute sub-threshold frames for the agent (recording above
      // already tee'd the raw audio; only the LiveKit leg is gated)
      if (GATE_ENABLED || GATE_DEBUG) {
        const now = Date.now()
        const db = frameDb(f32)
        if (GATE_DEBUG) {
          this.dbgMax = Math.max(this.dbgMax ?? -Infinity, db)
          if (now - (this.dbgAt ?? 0) >= 1000) {
            const peak = this.dbgMax === -Infinity ? 'silence' : `${this.dbgMax.toFixed(1)} dBFS`
            // "OFF" here means only THIS transport-local gate is off, which is
            // the normal configuration — the agent's universal VOICE_GATE_DB
            // filters instead, and gating the same audio twice is a bug. Say so
            // explicitly: the old wording ("no filtering") read as a fault and
            // sent us chasing a gateway problem that did not exist (2026-07-23).
            const gate = GATE_ENABLED
              ? `gate ${now <= this.gateOpenUntil ? 'OPEN (passing)' : 'CLOSED (muting)'} @ ${GATE_DB} dBFS`
              : 'gateway gate off (agent VOICE_GATE_DB filters downstream)'
            console.log(`[${this.accountId}] contact peak ${peak} — ${gate}`)
            this.dbgMax = -Infinity
            this.dbgAt = now
          }
        }
        if (GATE_ENABLED) {
          if (db >= GATE_DB) this.gateOpenUntil = now + GATE_HOLD_MS
          else if (now > this.gateOpenUntil) pcm.fill(0)
        }
      }
      const frame = new AudioFrame(pcm, PLAYBACK_RATE, 1, pcm.length)
      this.playQueue = this.playQueue.then(() => this.lkSource.captureFrame(frame)).catch(() => {})
    }

    // Sweep tracks that were subscribed before/during the awaits above —
    // consumedTracks dedupes against the live TrackSubscribed handler.
    try {
      for (const participant of this.lkRoom.remoteParticipants.values()) {
        for (const pub of participant.trackPublications.values()) {
          if (pub.track) consume(pub.track)
        }
      }
    } catch (e) {
      console.error('[calls] existing-track sweep failed:', e.message)
    }
  }

  // Idempotent LiveKit join. Dedupes an in-flight attempt (both the ringing
  // and the pickup path call this) and RETRIES one that failed, so a transient
  // early-join error can never strand the contact outside the room. Resolves
  // true once actually connected; false if it gave up this time.
  async _ensureJoined() {
    if (this.ended || this._joined) return this._joined === true
    if (!this.livekitUrl || !this.token) return false
    if (!this._joinPromise) {
      this._joinPromise = this.joinRoom(this.livekitUrl, this.token).then(
        () => { this._joined = true },
        (e) => {
          this._joinPromise = null       // allow a later retry
          const room = this.lkRoom       // discard the half-open Room
          this.lkRoom = null
          if (room) room.disconnect().catch(() => {})
          console.error(`[${this.accountId}] LiveKit join failed:`, e.message)
          throw e
        })
    }
    try { await this._joinPromise } catch { return false }
    return this._joined === true
  }

  // Join the LiveKit room while the phone is still RINGING — the expensive
  // parts (ws connect, track publish/subscribe) are done before pickup, so
  // answering only costs the model's first response (~1-2s instead of ~8s).
  // The agent won't greet until wa.callStatus flips to "active". A failure
  // here is non-fatal: _onAnswered() retries via the same _ensureJoined().
  async _joinEarly() {
    if (!(await this._ensureJoined())) return
    await this._setCallStatus('ringing')
  }

  // Returns whether the attribute actually landed. The caller MUST care: the
  // agent greets only when it sees wa.callStatus=active, so a write that is
  // quietly skipped (room not connected yet) or throws leaves the agent mute
  // for the whole call — observed 2026-07-23 on +601162134250: 40.8s answered
  // call, contact audio flowing, zero agent turns and a digitally silent
  // agent channel, because this used to no-op and return void.
  async _setCallStatus(value) {
    try {
      const lp = this.lkRoom?.localParticipant
      if (!lp || typeof lp.setAttributes !== 'function') {
        console.warn(`[${this.accountId}] setAttributes(${value}) skipped — not in the room yet`)
        return false
      }
      await lp.setAttributes({ 'wa.callStatus': value })
      return true
    } catch (e) {
      console.error(`[${this.accountId}] setAttributes(${value}) failed:`, e.message)
      return false
    }
  }

  // Callee picked up: ungate audio and make sure we're in the room. The
  // greeting gate (wa.callStatus=active) opens on the first contact audio
  // frame — see _openGreetGate — NOT here: engine-Active precedes the
  // callee's audio session being ready.
  async _onAnswered() {
    this.active = true
    // Guarantee we are actually in the room. _ensureJoined() awaits an
    // in-flight early join, retries a failed one, and no-ops if already in — so
    // whatever happened during ringing, the contact ends up joined here. A
    // single retry covers the case where the early attempt we just awaited was
    // itself the one that failed.
    if (!(await this._ensureJoined()) && !this.ended) await this._ensureJoined()
    // Safety net only — it must NOT be the normal path (the energy-based
    // first_audio trigger opens the gate on a healthy call). Kept well clear of
    // the ~7s the callee's phone can take to bring its audio session up after
    // engine-Active, but far below the old 25s: on a WhatsApp media stall (no
    // inbound frames ever) this is all that lets the agent speak, and 25s of
    // dead air had callers hanging up first. The greeting RETRIES then re-greet
    // every few seconds, so an early open into a still-settling path self-heals.
    const fallbackMs = Math.max(500, Number(process.env.BAILEYS_GREET_GATE_MS ?? 10000))
    this.greetGateTimer = setTimeout(() => this._openGreetGate('timeout'), fallbackMs)
    const answeredAt = Date.now()
    this.mediaWatch = setInterval(() => {
      if (this.ended) return
      const last = this.lastPlaybackAt || answeredAt
      const stalled = Date.now() - last
      if (stalled > 8000 && !this.mediaStallLogged) {
        this.mediaStallLogged = true
        console.warn(`[${this.accountId}] ⚠ ONE-WAY AUDIO: no inbound frames for ${Math.round(stalled / 1000)}s — contact audio is not reaching us (WhatsApp media stall); a redial usually fixes it`)
      }
    }, 5000)
    this.onEvent('connected')
  }

  // Flip the agent's greeting gate exactly once — from the first contact
  // audio frame (proof the media path is live end-to-end) or the fallback.
  _openGreetGate(why) {
    if (this.greetGateOpen || this.ended) return
    this.greetGateOpen = true
    if (this.greetGateTimer) { clearTimeout(this.greetGateTimer); this.greetGateTimer = null }
    console.log(`[${this.accountId}] greeting gate open (${why})`)
    // Fire-and-forget from a SYNC caller, so it must never reject: since Node
    // 15 an unhandled rejection terminates the whole gateway, which would kill
    // every other account's socket over one bad call.
    this._publishActive().catch((e) =>
      console.error(`[${this.accountId}] _publishActive crashed:`, e?.stack || e))
  }

  // Opening the gate is worthless unless wa.callStatus=active actually reaches
  // the room — that attribute IS the agent's cue to speak. The gate latches
  // open on the first call, so a single dropped write used to mute the agent
  // permanently with nothing retrying. Keep trying (re-joining if the room is
  // missing) until it lands or the call ends.
  async _publishActive() {
    for (let attempt = 0; attempt < 10 && !this.ended; attempt++) {
      if (await this._setCallStatus('active')) return
      await this._ensureJoined()   // room may not be up yet — that's the usual cause
      await sleep(500)
    }
    if (!this.ended) {
      console.error(`[${this.accountId}] could not publish wa.callStatus=active — ` +
                    'the agent will stay silent for this call')
    }
  }

  // -- signaling → status --

  async place() {
    const { CallState } = await loadModules()
    const { signaling, engine, sock } = this.session
    this.session.onCallEvent = (eventType, eventData) => {
      if (eventType === 16 && eventData) {
        try {
          const info = JSON.parse(eventData).call_info ?? {}
          const state = Number(info.call_state ?? 0)
          console.log(`[${this.accountId}] engine call_state=${state} (status ${this.status})`)
          if (state === CallState.PreacceptReceived && this.status === 'connecting') {
            this.status = 'ringing'
            this.onEvent('ringing')
          } else if (state === CallState.Active && this.status !== 'in_call') {
            this.status = 'in_call'
            this._onAnswered() // join LiveKit NOW (callee picked up) → agent greets
          } else if (state === CallState.Idle || state === CallState.Ending) {
            this.end('remote_end')
          }
        } catch { /* malformed engine event */ }
      } else if (eventType === 156 && eventData) {
        try { this.session.relay?.updateRelayList(JSON.parse(eventData)) } catch { /* ignore */ }
      } else if (eventType === 2) {
        this.end('remote_end')
      }
    }

    const targetPnJid = `${this.waId}@s.whatsapp.net`
    const peerLid = await signaling.resolveLid(targetPnJid)
    if (!peerLid) throw new Error(`could not resolve call routing (LID) for +${this.waId}`)
    for (const jid of [targetPnJid, peerLid]) {
      try { await sock.presenceSubscribe(jid) } catch { /* best-effort */ }
    }
    await sleep(750)
    const peerDeviceJids = await signaling.discoverPeerDevices(peerLid)
    const deviceList = peerDeviceJids.length ? peerDeviceJids : [toBareJid(peerLid)]
    await signaling.ensureSessionsForPeers(deviceList)
    await sleep(500)
    await signaling.issueTcToken(peerLid)
    const tcToken = await signaling.ensureTcToken(peerLid, targetPnJid)
    engine.startCall({
      peerJid: peerLid,
      peerPn: targetPnJid,
      peerList: deviceList,
      callId: this.callId,
      isVideo: false,
      isLidCall: true,
      isFromDialer: false,
      extraData: tcToken,
    })
  }

  end(reason = 'hangup') {
    if (this.ended) return
    this.ended = true
    this.status = 'ended'
    clearTimeout(this.endTimer)
    if (this.greetGateTimer) clearTimeout(this.greetGateTimer)
    if (this.mediaWatch) clearInterval(this.mediaWatch)
    try { this.session.engine?.endCall(0, true) } catch { /* engine gone */ }
    this._captureStop()
    this.session.onPlayback = null
    this.session.onCallEvent = null
    for (const r of this.readers) { try { r.cancel() } catch { /* torn down */ } }
    this.readers = []
    const room = this.lkRoom
    this.lkRoom = null
    this.lkSource = null
    if (room) room.disconnect().catch(() => {})
    if (activeCalls.get(this.accountId) === this) activeCalls.delete(this.accountId)
    // flush the recording (best-effort — must never break call teardown)
    let recording = null
    if (this.rec && this.recChunks.length) {
      try {
        writeStereoWav(path.join(RECORD_DIR, this.recName), this.recChunks, this.capturedRate || 16000)
        recording = this.recName
      } catch (e) {
        console.error(`[${this.accountId}] recording write failed:`, e.message)
      }
    }
    this.recChunks = []
    this.recContact = []
    this.onEvent('ended', { reason, recording, rec_started_at: this.recStartedAt || null })
  }

  snapshot() {
    return {
      call_id: this.callId,
      wa_id: this.waId,
      account_id: this.accountId,
      status: this.status,
      room: this.roomName,
      started_at: new Date(this.startedAt).toISOString(),
    }
  }
}

// ---- public API used by index.js -----------------------------------------------

/**
 * Place an outbound WhatsApp voice call and bridge it into a LiveKit room.
 * `onEvent(event, detail)` fires with ringing | connected | ended.
 * Returns the call snapshot; throws on any setup failure.
 */
export async function startCall({ accountId, sock, to, room, livekitUrl, token, maxSeconds, onEvent }) {
  // Per ACCOUNT, not per gateway: WhatsApp permits one call per number, so a
  // second contact can be dialled concurrently as long as it lands on a
  // different Baileys account (routing picks one — see whatsapp/routing.py).
  const live = activeCalls.get(accountId)
  if (live && !live.ended) {
    throw new Error(`account ${accountId} already has a live call (+${live.waId}) — one call at a time per WhatsApp number`)
  }
  const session = await ensureSession(accountId, sock)
  const call = new BridgedCall({
    accountId,
    waId: String(to).replace(/\D/g, ''),
    session,
    room,
    onEvent: (event, detail) => onEvent(event, { ...detail, ...call.snapshot() }),
    maxSeconds,
    livekitUrl,
    token,
  })
  activeCalls.set(accountId, call)
  // Join LiveKit in parallel with the dial (deliberately not awaited) — the
  // wa.callStatus=ringing attribute keeps the agent quiet until pickup.
  call._joinEarly()
  try {
    await call.place()
  } catch (e) {
    // Django gets this failure synchronously (HTTP error) — a pushed "ended"
    // event for a call it never registered would just add noise to the thread
    call.onEvent = () => {}
    call.end('failed')
    throw e
  }
  return call.snapshot()
}

/** End the active call for this account (no-op result if nothing is live). */
export function endCall(accountId) {
  const live = activeCalls.get(accountId)
  if (live && !live.ended) {
    const snap = live.snapshot()
    live.end('hangup')
    return { ...snap, status: 'ended' }
  }
  return { status: 'idle' }
}

/** Current call state for this account — Django uses this for stale recovery. */
export function callStatus(accountId) {
  const live = activeCalls.get(accountId)
  if (live && !live.ended) return live.snapshot()
  return { status: 'idle', call_id: null }
}

// ---- offline selftest (node calls.js --selftest) -------------------------------
// Counterpart of `python -m whatsapp.bridge --selftest`: proves the heavy
// native pieces load on this machine (baileys-caller WASM engine + worker
// pool, @livekit/rtc-node bindings) without touching WhatsApp or LiveKit.

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href
    && process.argv.includes('--selftest')) {
  // index.js's outbound-media payload builder (pure; no WhatsApp needed)
  const { selftest: mediaSelftest } = await import('./media.js')
  mediaSelftest()
  console.log('send-media payload builder OK')
  // quoted replies, reactions and inbound placeholders (pure; no WhatsApp needed)
  const { selftest: messagesSelftest } = await import('./messages.js')
  messagesSelftest()
  console.log('quote / reaction / inbound parsing OK')
  const { WasmEngine, lk } = await loadModules()
  console.log('modules loaded: baileys-caller internals + @livekit/rtc-node')
  if (!lk.Room || !lk.AudioSource || !lk.AudioFrame || !lk.AudioStream) {
    throw new Error('@livekit/rtc-node is missing expected exports')
  }
  const engine = new WasmEngine({ callbacks: { cryptoHkdf: computeHkdf, hmacSha256: computeHmacSha256 } })
  await engine.initialize()
  console.log('WASM VoIP engine initialized')
  engine.destroy()
  console.log('selftest OK')
  process.exit(0)
}
