/**
 * Baileys gateway — the unofficial WhatsApp Web transport for ChatBranch.
 *
 * A small Node sidecar (same pattern as the voice-agent worker): Django talks
 * to it over local HTTP, it talks to WhatsApp via @whiskeysockets/baileys.
 * One WhatsApp Web session per ChannelAccount row with provider="baileys";
 * auth state persists under backend/data/whatsapp/baileys/<account_id>/ so
 * sessions survive restarts (scan the QR once).
 *
 *   GET  /accounts/:id/status      -> { state, qr?, me?, error? }
 *   POST /accounts/:id/start       -> begin/refresh pairing (emits a QR)
 *   POST /accounts/:id/send        -> { to, text } -> { id }
 *   POST /accounts/:id/logout      -> unpair + wipe local auth state
 *   POST /accounts/:id/call        -> { to, room, livekit_url, token, max_seconds }
 *                                     place a voice call bridged to LiveKit (calls.js)
 *   POST /accounts/:id/call-end    -> hang up the active call
 *   GET  /accounts/:id/call-status -> { status, call_id, room?, wa_id? }
 *
 * Every request needs the X-Baileys-Secret header (BAILEYS_SECRET, shared
 * with Django). Inbound messages are pushed to Django at
 * POST /api/whatsapp/baileys/inbound with the same secret.
 *
 * ⚠ Baileys speaks WhatsApp's unofficial protocol — it violates WhatsApp's
 * ToS and numbers can get banned. Use a dedicated/spare number.
 */
import { makeWASocket, useMultiFileAuthState, DisconnectReason, fetchLatestBaileysVersion, downloadMediaMessage } from '@whiskeysockets/baileys'
import { toDataURL } from 'qrcode'
import http from 'http'
import { appendFileSync, existsSync, mkdirSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'fs'
import path from 'path'
import { fileURLToPath } from 'url'
import pino from 'pino'

const HERE = path.dirname(fileURLToPath(import.meta.url))

// backend/.env is the shared config file — Django, the voice agent and the
// bridge all load it via python-dotenv, but node gets only shell env. Load it
// here too (before any const reads process.env) so BAILEYS_* keys apply.
// Shell-exported variables win; `KEY = value` spacing and quotes are tolerated.
try {
  for (const line of readFileSync(path.join(HERE, '..', '.env'), 'utf8').split(/\r?\n/)) {
    if (line.trim().startsWith('#')) continue
    const m = line.match(/^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*?)\s*$/)
    if (!m) continue
    let val = m[2]
    if ((val.startsWith('"') && val.endsWith('"')) || (val.startsWith("'") && val.endsWith("'"))) {
      val = val.slice(1, -1)
    }
    if (!(m[1] in process.env)) process.env[m[1]] = val
  }
} catch { /* no .env — code defaults apply */ }

// ---------------------------------------------------------------- crash guard
// Node terminates the process on an unhandled rejection (default since v15) and
// on an uncaught exception. For THIS process that default is far too blunt: the
// gateway holds every paired account's WhatsApp socket, so one bad async path in
// one call would drop every number at once — and re-pairing a Baileys number
// costs a QR scan. Observed 2026-07-23: the gateway vanished mid-call and the
// next dial failed with "gateway unreachable", with no trace left behind.
//
// Deliberate trade-off: we log loudly and STAY UP. An uncaught exception can
// leave state inconsistent, so this is not a licence to ignore them — the log
// line is the bug report. Set BAILEYS_CRASH_EXIT=1 to restore Node's default.
const CRASH_EXIT = process.env.BAILEYS_CRASH_EXIT === '1'
for (const [event, label] of [['unhandledRejection', 'UNHANDLED REJECTION'],
                              ['uncaughtException', 'UNCAUGHT EXCEPTION']]) {
  process.on(event, (err) => {
    console.error(`[gateway] ⚠ ${label} — the gateway stayed up, but this is a bug:`)
    console.error(err?.stack || err)
    if (CRASH_EXIT) process.exit(1)
  })
}

const PORT = Number(process.env.BAILEYS_PORT || 3020)
const SECRET = process.env.BAILEYS_SECRET || 'baileys-local-dev'
const DJANGO_URL = (process.env.DJANGO_URL || 'http://127.0.0.1:8000').replace(/\/$/, '')
const AUTH_ROOT = path.join(HERE, '..', 'data', 'whatsapp', 'baileys')

const logger = pino({ level: process.env.BAILEYS_LOG_LEVEL || 'silent' })

/** Append-only diagnostic trail (data/whatsapp/baileys/events.log) — every
 *  upsert and connection change, so "message never arrived" is debuggable
 *  without watching the console. */
function logEvent(kind, detail) {
  try {
    mkdirSync(AUTH_ROOT, { recursive: true })
    appendFileSync(path.join(AUTH_ROOT, 'events.log'),
      `${new Date().toISOString()} ${kind} ${JSON.stringify(detail)}\n`)
  } catch { /* diagnostics must never break messaging */ }
}

/** id -> { sock, state, qr, me, error } */
const accounts = new Map()

/** LID reply routing: WhatsApp's privacy addressing hosts some chats on a
 *  `<lid>@lid` identity. Replies MUST target that lid chat — sends to the
 *  peer's phone jid are accepted by the server but never delivered. Learned
 *  from every lid inbound and persisted so restarts don't break replies. */
const lidHints = new Map() // phone/lid digits -> full lid jid
const HINTS_PATH = () => path.join(AUTH_ROOT, 'lid-hints.json')

function loadHints() {
  try {
    for (const [k, v] of Object.entries(JSON.parse(readFileSync(HINTS_PATH(), 'utf8')))) lidHints.set(k, v)
  } catch { /* first run */ }
}

function rememberLid(digitsKey, lidJid) {
  if (lidHints.get(digitsKey) === lidJid) return
  lidHints.set(digitsKey, lidJid)
  try {
    mkdirSync(AUTH_ROOT, { recursive: true })
    writeFileSync(HINTS_PATH(), JSON.stringify(Object.fromEntries(lidHints), null, 1))
  } catch { /* hints are best-effort */ }
}

const digits = (v) => String(v || '').replace(/\D/g, '')

/** Reverse hint lookup: which phone digits were previously learned for this
 *  lid jid. Skips the self-referential lid->lid rows the raw-digits fallback
 *  writes, so an unresolved lid never "resolves" to itself. */
function phoneForLid(lidJid) {
  const lidDigits = digits(String(lidJid).split('@')[0])
  for (const [key, jid] of lidHints) {
    if (jid === lidJid && key !== lidDigits) return key
  }
  return null
}

function acct(id) {
  if (!accounts.has(id)) accounts.set(id, { sock: null, state: 'stopped', qr: null, me: null, error: null })
  return accounts.get(id)
}

// Envelopes that carry no user content (session setup, receipts-of-sorts).
// An inbound whose message object is empty or protocol-only must be skipped,
// not shown as a "[message]" bubble.
const PROTOCOL_KEYS = new Set(['protocolMessage', 'senderKeyDistributionMessage',
                               'messageContextInfo', 'reactionMessage'])
const hasContent = (msg) => !!msg && Object.keys(msg).some((k) => !PROTOCOL_KEYS.has(k))

function extractText(msg) {
  if (!msg) return '[message]'
  return (
    msg.conversation ||
    msg.extendedTextMessage?.text ||
    msg.imageMessage?.caption ||
    msg.videoMessage?.caption ||
    (msg.imageMessage && '[image]') ||
    (msg.videoMessage && '[video]') ||
    (msg.audioMessage && '[audio]') ||
    (msg.documentMessage && '[document]') ||
    (msg.stickerMessage && '[sticker]') ||
    (msg.locationMessage && '[location]') ||
    '[message]'
  )
}

// Downloadable envelope kinds -> our media_type + a fallback extension.
// Stickers are webp images (animated webp still renders in <img>).
const MEDIA_KINDS = [
  ['imageMessage', 'image', 'jpg'],
  ['videoMessage', 'video', 'mp4'],
  ['audioMessage', 'audio', 'ogg'],
  ['documentMessage', 'document', 'bin'],
  ['stickerMessage', 'image', 'webp'],
]
const MAX_MEDIA_BYTES = 20 * 1024 * 1024

function mediaKind(msg) {
  for (const [key, type, ext] of MEDIA_KINDS) {
    if (msg?.[key]) return { key, type, ext, node: msg[key] }
  }
  return null
}

/** Decrypt+download an inbound attachment (only this process holds the Signal
 *  session keys) and hand the bytes to Django, which stores them next to the
 *  Cloud downloads and attaches them to the just-pushed message. Best-effort:
 *  a failure leaves the "[image]"-style text placeholder in the chat. */
async function pushInboundMedia(id, m, waId, media) {
  try {
    const declared = Number(media.node?.fileLength || 0)
    if (declared > MAX_MEDIA_BYTES) {
      logEvent('mediaSkip', { acct: id, id: m.key?.id, kind: media.key, bytes: declared })
      return
    }
    const sock = acct(id).sock
    const buffer = await downloadMediaMessage(m, 'buffer', {},
      { logger, reuploadRequest: sock ? sock.updateMediaMessage : undefined })
    if (!buffer || buffer.length > MAX_MEDIA_BYTES) return
    const mime = String(media.node?.mimetype || '').split(';')[0].trim()
    const mimeExt = mime.includes('/') ? mime.split('/')[1].replace(/[^a-z0-9]/gi, '').slice(0, 8) : ''
    const filename = media.node?.fileName || `${m.key?.id || 'media'}.${mimeExt || media.ext}`
    const form = new FormData()
    form.append('wa_id', waId)
    form.append('msg_id', m.key?.id || '')
    form.append('media_type', media.type)
    form.append('file', new Blob([buffer], { type: mime || 'application/octet-stream' }), filename)
    // no explicit Content-Type header — fetch sets the multipart boundary
    await fetch(`${DJANGO_URL}/api/whatsapp/baileys/media`, {
      method: 'POST', headers: { 'X-Baileys-Secret': SECRET }, body: form,
    })
    logEvent('mediaPush', { acct: id, id: m.key?.id, kind: media.key, bytes: buffer.length })
  } catch (e) {
    console.error(`[${id}] media push failed:`, e.message)
  }
}

async function pushInbound(id, m) {
  if (!hasContent(m.message)) return // session-setup / protocol-only envelope
  const jid = m.key?.remoteJid || ''
  let waId = ''
  if (jid.endsWith('@s.whatsapp.net')) {
    waId = digits(jid.split('@')[0])
  } else if (jid.endsWith('@lid')) {
    // key the thread by the real phone number when Baileys exposes it, but
    // ALWAYS route replies back to the lid chat the message arrived on
    const alt = m.key?.senderPn || m.key?.remoteJidAlt || m.key?.participantPn || ''
    // Not every envelope carries the alt jid — before keying by raw lid
    // digits (which spawns a phantom "+<lid>" thread beside the real chat),
    // recover the phone from a mapping an earlier alt-carrying inbound saved.
    waId = String(alt).includes('@s.whatsapp.net')
      ? digits(String(alt).split('@')[0])
      : phoneForLid(jid) || digits(jid.split('@')[0])
    rememberLid(waId, jid)
  } else {
    return // groups/broadcast/status
  }
  const body = {
    account_id: id,
    wa_id: waId,
    name: m.pushName || '',
    text: extractText(m.message),
    msg_id: m.key?.id || '',
  }
  try {
    await fetch(`${DJANGO_URL}/api/whatsapp/baileys/inbound`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Baileys-Secret': SECRET },
      body: JSON.stringify(body),
    })
  } catch (e) {
    console.error(`[${id}] inbound push failed:`, e.message)
  }
  // attachment (image/pdf/sticker/…): download + attach AFTER the text push
  // so the message row exists to hang the media on; fire-and-forget
  const media = mediaKind(m.message)
  if (media) pushInboundMedia(id, m, waId, media)
}

/** Reverse DisconnectReason lookup: 401 -> "loggedOut", 403 -> "forbidden"… */
function disconnectReasonName(code) {
  for (const [name, value] of Object.entries(DisconnectReason)) {
    if (value === code) return name
  }
  return 'unknown'
}

/** Push a connection lifecycle event to Django's channel-health monitor
 *  (same secret mechanism as pushInbound). Django maps: connected -> healthy,
 *  disconnected -> degraded, logged_out / forbidden(403) -> banned. */
async function pushStatusEvent(id, event, reason = '') {
  logEvent('status', { acct: id, event, reason })
  try {
    await fetch(`${DJANGO_URL}/api/whatsapp/baileys/status-event`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Baileys-Secret': SECRET },
      // `number` = this account's OWN paired MSISDN (captured as a.me on
      // connect). Django has no other way to learn it — Baileys accounts carry
      // no external_id — and it backs the {{account_number}} placeholder so a
      // template can name the number the contact is actually being contacted
      // from, instead of hardcoding one.
      body: JSON.stringify({ account_id: id, event, reason, number: acct(id).me || '' }),
    })
  } catch (e) {
    console.error(`[${id}] status-event push failed:`, e.message)
  }
}

async function startAccount(id) {
  const a = acct(id)
  if (a.state === 'connected' || a.state === 'starting') return
  a.state = 'starting'
  a.error = null
  const dir = path.join(AUTH_ROOT, String(id))
  mkdirSync(dir, { recursive: true })
  const { state, saveCreds } = await useMultiFileAuthState(dir)
  // WhatsApp rejects/ignores stale protocol versions silently (session hangs
  // in "starting" with no QR) — always pair with the current one.
  const { version } = await fetchLatestBaileysVersion()

  const sock = makeWASocket({
    version,
    auth: state,
    logger,
    printQRInTerminal: false,
    browser: ['ChatBranch', 'Chrome', '1.0'],
    syncFullHistory: false,
  })
  a.sock = sock

  // watchdog: a healthy pairing emits a QR within seconds — surface a hang
  setTimeout(() => {
    if (a.state === 'starting') {
      a.state = 'error'
      a.error = 'No response from WhatsApp within 30s — check the gateway console/network and press Pair to retry.'
      try { sock.end(new Error('pairing watchdog timeout')) } catch { /* already closed */ }
    }
  }, 30000)

  sock.ev.on('creds.update', saveCreds)

  sock.ev.on('connection.update', async ({ connection, lastDisconnect, qr }) => {
    if (connection) logEvent('connection', { acct: id, connection })
    if (qr) {
      a.qr = await toDataURL(qr)
      a.state = 'qr'
      console.log(`[${id}] QR ready — scan from the dashboard`)
    }
    if (connection === 'open') {
      a.state = 'connected'
      a.qr = null
      a.me = digits((sock.user?.id || '').split(':')[0])
      console.log(`[${id}] connected as +${a.me}`)
      // announce presence — companions that never do get their sends deprioritized
      sock.sendPresenceUpdate('available').catch(() => {})
      pushStatusEvent(id, 'connected', '')
    }
    if (connection === 'close') {
      // the VoIP session (calls.js) is bound to this dead socket — end any
      // live call now; the reconnect below hands calls a fresh socket later
      if (_calls) {
        try { _calls.destroySession(id) } catch { /* no session for this account */ }
      }
      const code = lastDisconnect?.error?.output?.statusCode
      if (code === DisconnectReason.loggedOut) {
        a.state = 'logged_out'
        a.qr = null
        a.me = null
        rmSync(dir, { recursive: true, force: true })
        console.log(`[${id}] logged out — auth state wiped`)
        pushStatusEvent(id, 'logged_out', `${disconnectReasonName(code)} (${code})`)
      } else {
        a.state = 'disconnected'
        a.error = lastDisconnect?.error?.message || `closed (${code})`
        console.log(`[${id}] disconnected (${code}) — reconnecting in 3s`)
        pushStatusEvent(id, 'disconnected', `${disconnectReasonName(code)} (${code ?? '?'})`)
        setTimeout(() => startAccount(id).catch((e) => { a.state = 'error'; a.error = e.message }), 3000)
      }
    }
  })

  // delivery ground truth: 2=server ack, 3=delivered to device, 4=read.
  // A send stuck at 2 forever = WhatsApp accepted it but delivered nowhere.
  // Forward 3/4/5 to Django so chat ticks advance (sent → delivered → read)
  // exactly like the Cloud webhook's `statuses` events — before this, the
  // receipts were logged here and the dashboard's ticks froze at "sent".
  const pushMessageStatus = async (key, statusLabel) => {
    const jid = String(key?.remoteJid || '')
    let waId = ''
    if (jid.endsWith('@s.whatsapp.net')) waId = digits(jid.split('@')[0])
    // lid chat: the receipt belongs to the phone-keyed thread the send
    // originated from; without a learned mapping there is no such thread
    else if (jid.endsWith('@lid')) waId = phoneForLid(jid) || ''
    if (!key?.fromMe || !key?.id || !waId) return
    try {
      await fetch(`${DJANGO_URL}/api/whatsapp/baileys/message-status`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'X-Baileys-Secret': SECRET },
        body: JSON.stringify({ wa_id: waId, msg_id: key.id, status: statusLabel }),
      })
    } catch (e) {
      console.error(`[${id}] message-status push failed:`, e.message)
    }
  }
  sock.ev.on('messages.update', (updates) => {
    for (const u of updates) {
      if (u.update?.status !== undefined) {
        logEvent('receipt', { acct: id, id: u.key?.id, jid: u.key?.remoteJid, status: u.update.status })
        const s = Number(u.update.status)
        if (s >= 4) pushMessageStatus(u.key, 'read')
        else if (s === 3) pushMessageStatus(u.key, 'delivered')
      }
    }
  })

  sock.ev.on('message-receipt.update', (updates) => {
    for (const u of updates) {
      logEvent('receipt2', { acct: id, id: u.key?.id, jid: u.key?.remoteJid,
                             type: u.receipt?.receiptTimestamp ? 'delivered' : u.receipt?.readTimestamp ? 'read' : 'other' })
    }
  })

  sock.ev.on('messages.upsert', ({ messages, type }) => {
    for (const m of messages) {
      logEvent('upsert', {
        acct: id, type, fromMe: !!m.key?.fromMe, jid: m.key?.remoteJid,
        senderPn: m.key?.senderPn, alt: m.key?.remoteJidAlt,
        msgKinds: Object.keys(m.message || {}), pushName: m.pushName,
      })
    }
    if (type !== 'notify') return
    for (const m of messages) {
      if (m.key?.fromMe) continue
      pushInbound(id, m)
    }
  })
}

async function checkNumber(id, to) {
  const a = accounts.get(id)
  if (!a || a.state !== 'connected') throw new Error(`account ${id} is not connected (state: ${a?.state || 'stopped'})`)
  const d = digits(to)
  if (lidHints.has(d)) return { exists: true, jid: lidHints.get(d) } // known inbound peer
  let results = null
  try {
    results = await a.sock.onWhatsApp(`${d}@s.whatsapp.net`)
  } catch {
    return { exists: null, jid: `${d}@s.whatsapp.net` } // lookup failed — inconclusive
  }
  // Unregistered numbers come back as an EMPTY list, not {exists:false}.
  const hit = Array.isArray(results) ? results.find((r) => r && r.exists !== false) : null
  if (!hit) return { exists: false, jid: null }
  // LID-migrated accounts: deliver to the lid jid — sends to the phone jid
  // are accepted by the server but never reach the device.
  const lid = typeof hit.lid === 'string' && hit.lid.includes('@') ? hit.lid : null
  if (lid) rememberLid(d, lid)
  return { exists: true, jid: lid || hit.jid || `${d}@s.whatsapp.net` }
}

async function sendText(id, to, text, rawJid = null) {
  const a = accounts.get(id)
  if (!a || a.state !== 'connected') throw new Error(`account ${id} is not connected (state: ${a?.state || 'stopped'})`)
  const d = digits(to)
  let jid = rawJid
  if (!jid) {
    const check = await checkNumber(id, d)
    if (check.exists === false) throw new Error(`+${d} is not on WhatsApp — check the number`)
    jid = check.jid || `${d}@s.whatsapp.net`
  }
  // brief "typing…" before the send — reads human, and companion sessions
  // that never emit presence look like bots to WhatsApp's scoring
  try {
    await a.sock.presenceSubscribe(jid)
    await a.sock.sendPresenceUpdate('composing', jid)
    await new Promise((r) => setTimeout(r, 900 + Math.floor(Math.random() * 1800)))
    await a.sock.sendPresenceUpdate('paused', jid)
  } catch { /* presence is best-effort */ }
  const result = await a.sock.sendMessage(jid, { text })
  logEvent('send', { acct: id, to: d, jid, id: result?.key?.id || '' })
  return result?.key?.id || ''
}

async function logout(id) {
  const a = accounts.get(id)
  try {
    (await callsModule())?.destroySession(id) // ends any live call first
  } catch { /* calls module never loaded */ }
  try {
    await a?.sock?.logout()
  } catch { /* already disconnected */ }
  rmSync(path.join(AUTH_ROOT, String(id)), { recursive: true, force: true })
  accounts.delete(id)
}

// ---- voice calls (calls.js — baileys-caller VoIP stack + LiveKit bridge) ------

let _calls = null
/** Lazy-load calls.js: the WASM engine + LiveKit native module take seconds
 *  and most gateway restarts never place a call. */
async function callsModule() {
  if (!_calls) _calls = await import('./calls.js')
  return _calls
}

/** Push a call lifecycle event to Django (same pattern as pushInbound). */
async function pushCallEvent(id, event, detail) {
  logEvent('call', { acct: id, event, ...detail })
  try {
    await fetch(`${DJANGO_URL}/api/whatsapp/baileys/call-event`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Baileys-Secret': SECRET },
      body: JSON.stringify({ account_id: id, event, ...detail }),
    })
  } catch (e) {
    console.error(`[${id}] call-event push failed:`, e.message)
  }
}

async function startVoiceCall(id, body) {
  const a = accounts.get(id)
  if (!a || a.state !== 'connected') {
    throw new Error(`account ${id} is not connected (state: ${a?.state || 'stopped'}) — pair it first`)
  }
  const { to, room, livekit_url, token, max_seconds } = body
  if (!to) throw new Error('to is required')
  const calls = await callsModule()
  return calls.startCall({
    accountId: id,
    sock: a.sock,
    to,
    room,
    livekitUrl: livekit_url,
    token,
    maxSeconds: max_seconds,
    onEvent: (event, detail) => pushCallEvent(id, event, detail),
  })
}

// ---- HTTP API --------------------------------------------------------------

const json = (res, code, body) => {
  res.writeHead(code, { 'Content-Type': 'application/json' })
  res.end(JSON.stringify(body))
}

const readBody = (req) =>
  new Promise((resolve) => {
    let raw = ''
    req.on('data', (c) => (raw += c))
    req.on('end', () => {
      try { resolve(JSON.parse(raw || '{}')) } catch { resolve({}) }
    })
  })

http
  .createServer(async (req, res) => {
    if (req.headers['x-baileys-secret'] !== SECRET) return json(res, 403, { detail: 'bad secret' })
    const m = req.url.match(/^\/accounts\/([\w-]+)\/(status|start|send|logout|check|call|call-end|call-status)$/)
    if (!m) return json(res, 404, { detail: 'not found' })
    const [, id, action] = m
    try {
      if (action === 'status') {
        const a = accounts.get(id)
        return json(res, 200, a
          ? { state: a.state, qr: a.qr, me: a.me, error: a.error }
          : { state: 'stopped', qr: null, me: null, error: null })
      }
      if (action === 'call-status') {
        // idle until calls.js has ever been loaded — don't boot WASM for a poll
        return json(res, 200, _calls ? (await callsModule()).callStatus(id) : { status: 'idle', call_id: null })
      }
      if (req.method !== 'POST') return json(res, 405, { detail: 'POST required' })
      if (action === 'start') {
        await startAccount(id)
        return json(res, 200, { ok: true, state: acct(id).state })
      }
      if (action === 'send') {
        const { to, text, raw_jid } = await readBody(req)
        if (!to || !text) return json(res, 400, { detail: 'to and text are required' })
        return json(res, 200, { id: await sendText(id, to, text, raw_jid || null) })
      }
      if (action === 'check') {
        const { to } = await readBody(req)
        if (!to) return json(res, 400, { detail: 'to is required' })
        return json(res, 200, await checkNumber(id, to))
      }
      if (action === 'call') {
        return json(res, 200, await startVoiceCall(id, await readBody(req)))
      }
      if (action === 'call-end') {
        return json(res, 200, _calls ? (await callsModule()).endCall(id) : { status: 'idle' })
      }
      if (action === 'logout') {
        await logout(id)
        return json(res, 200, { ok: true })
      }
    } catch (e) {
      return json(res, 502, { detail: e.message })
    }
  })
  .listen(PORT, '127.0.0.1', () => {
    console.log(`Baileys gateway on http://127.0.0.1:${PORT} (auth state: ${AUTH_ROOT})`)
    loadHints()
    // resume sessions that were paired before the restart
    if (existsSync(AUTH_ROOT)) {
      for (const id of readdirSync(AUTH_ROOT)) {
        if (existsSync(path.join(AUTH_ROOT, id, 'creds.json'))) {
          console.log(`[${id}] resuming saved session`)
          startAccount(id).catch((e) => console.error(`[${id}] resume failed:`, e.message))
        }
      }
    }
  })
