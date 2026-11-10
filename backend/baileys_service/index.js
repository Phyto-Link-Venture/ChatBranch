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
 *   POST /accounts/:id/start       -> begin/refresh pairing; {} emits a QR,
 *                                     {phone} asks WhatsApp for an 8-character
 *                                     pairing code for that number instead
 *   POST /accounts/:id/send        -> { to, text, quoted_id?, quoted_from_me?, quoted_text? } -> { id }
 *                                     (quoted_* = a reply quoting that message; messages.js)
 *   POST /accounts/:id/react       -> { to, msg_id, from_me?, emoji } -> { id }  ("" removes ours)
 *   POST /accounts/:id/send-media  -> { to, media_type: image|video|audio|document|sticker,
 *                                     file_b64, mime?, filename?, caption? } -> { id }
 *                                     (≤16 MB, base64 in the body — never a path/URL; media.js.
 *                                     media_type sticker = a 512x512 image/webp, no caption)
 *   POST /accounts/:id/logout      -> unpair + wipe local auth state
 *   POST /accounts/:id/call        -> { to, room, livekit_url, token, max_seconds }
 *                                     place a voice call bridged to LiveKit (calls.js)
 *   POST /accounts/:id/call-end    -> hang up the active call
 *   GET  /accounts/:id/call-status -> { status, call_id, room?, wa_id? }
 *
 * On a fresh pair the number's existing 1:1 chats are replayed by WhatsApp and
 * pushed to POST /api/whatsapp/baileys/history (text and placeholders only — see
 * history.js), as far back as the inbox's import_history_days setting asks.
 *
 * Every request needs the X-Baileys-Secret header (BAILEYS_SECRET, shared
 * with Django). Inbound messages are pushed to Django at
 * POST /api/whatsapp/baileys/inbound with the same secret.
 *
 * ⚠ Baileys speaks WhatsApp's unofficial protocol — it violates WhatsApp's
 * ToS and numbers can get banned. Use a dedicated/spare number.
 */
import { makeWASocket, useMultiFileAuthState, DisconnectReason, fetchLatestBaileysVersion, downloadMediaMessage, Browsers } from '@whiskeysockets/baileys'
import * as Baileys from '@whiskeysockets/baileys'
import { toDataURL } from 'qrcode'
import http from 'http'
import { appendFileSync, existsSync, mkdirSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'fs'
import path from 'path'
import { fileURLToPath } from 'url'
import pino from 'pino'
import { buildMediaMessage, MediaRequestError, MAX_SEND_MEDIA_BODY, readJsonBodyCapped } from './media.js'
import { buildContact, buildDelete, buildEdit, buildLocation, buildPoll, buildQuoted, buildReact, chatKind, editOf, groupSender, KeyStore, mediaKind, meJids, mentionsMe, messageSeconds, parseInbound, phoneSent, postWithRetry, recallOf, setNormalizer, targetJid, unwrap } from './messages.js'
import { createPresence, DEFAULT_IDLE_MS } from './presence.js'
import { normalizePairingCode, pairingPhone } from './pairing.js'
import { chunk as historyChunks, HISTORY_CHOICES, historyRows, historyWindowDays, wantsFullSync } from './history.js'

const HERE = path.dirname(fileURLToPath(import.meta.url))

// Baileys' own wrapper walk (ephemeral / view-once / edited / album children /
// status mentions / lottie stickers), when this version exports it; messages.js
// walks the same set itself afterwards either way.
setNormalizer(Baileys.normalizeMessageContent)

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
// Loopback by default: only the API on this machine may call the gateway. In
// Docker the API is another container, so compose sets BAILEYS_HOST=0.0.0.0
// (the port is not published — it is reachable on the compose network only).
const HOST = process.env.BAILEYS_HOST || '127.0.0.1'
const SECRET = process.env.BAILEYS_SECRET || 'baileys-local-dev'
// The API this gateway pushes inbound events to. Named DJANGO_URL when the
// backend was Django; that name is still accepted so existing runbooks and
// process managers keep working after the port to the C# API.
const API_URL = (process.env.API_URL || process.env.DJANGO_URL || 'http://127.0.0.1:8090')
  .replace(/\/$/, '')
const AUTH_ROOT = path.join(HERE, '..', 'data', 'whatsapp', 'baileys')
// "Keep phone notifications": how long a number stays online after its last send / typing
const PRESENCE_IDLE_MS = Number(process.env.BAILEYS_PRESENCE_IDLE_MS || DEFAULT_IDLE_MS)

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
  if (!accounts.has(id)) {
    accounts.set(id, {
      sock: null, state: 'stopped', qr: null, me: null, error: null, presence: null, gen: 0,
      // "Link with phone number": the number we asked WhatsApp about, and the
      // 8-character code the person types on the phone (null = pairing by QR).
      pairPhone: null, pairingCode: null,
    })
  }
  return accounts.get(id)
}

// ---- per-number settings (kept next to the session, so they survive a restart) ----
// keep_phone_notifications: stay offline between sends so the phone still buzzes
// (presence.js) — set by the API from the number's settings (POST /accounts/:id/settings).
// import_history_days: how far back the number's existing chats are imported when it
// links (0 = none, 30 / 90, -1 = everything WhatsApp sends — history.js). Read at
// socket creation, so the API hands it over before each pairing attempt.
const settingsFile = (id) => path.join(AUTH_ROOT, String(id), 'settings.json')

function loadSettings(id) {
  try { return JSON.parse(readFileSync(settingsFile(id), 'utf8')) || {} } catch { return {} }
}

function saveSettings(id, settings) {
  mkdirSync(path.join(AUTH_ROOT, String(id)), { recursive: true })
  writeFileSync(settingsFile(id), JSON.stringify(settings))
}

// Envelopes that carry no user content (session setup, receipts-of-sorts).
// An inbound whose message object is empty or protocol-only must be skipped,
// not shown as a "[message]" bubble.
// (reactionMessage is handled on its own: pushed as a reaction, never a bubble)
const PROTOCOL_KEYS = new Set(['protocolMessage', 'senderKeyDistributionMessage',
                               'messageContextInfo', 'reactionMessage'])
const hasContent = (msg) => !!msg && Object.keys(msg).some((k) => !PROTOCOL_KEYS.has(k))

// Recent messages by id (inbound + what we sent), so a reply can quote the real
// message; an id we no longer hold is rebuilt from the API's text (messages.js).
const recentMessages = new KeyStore(2000)
// ids this gateway sent itself (ChatBranch's own sends) — never synced back as "sent from the phone"
const ownSends = new KeyStore(5000)

// Downloadable envelope kinds (image / video / video note / audio / document /
// sticker) live in messages.js (MEDIA_KINDS, mediaKind) so the selftest covers them.
const MAX_MEDIA_BYTES = 20 * 1024 * 1024

// ---- group metadata (subject + participants) ----
// Fetched once per group per hour per number: the inbound push names the group,
// and Baileys reads the participant list on every group send (cachedGroupMetadata)
// — without a cache each send would be a metadata query, a rate-limit/ban risk.
// A membership change drops the entry; a rename updates the subject in place.
const GROUP_META_TTL_MS = 60 * 60 * 1000
const groupCache = new Map() // `${account}|${jid}` -> { meta, at }
const groupKey = (id, jid) => `${id}|${jid}`

function cachedGroup(id, jid, fresh = true) {
  const hit = groupCache.get(groupKey(id, jid))
  if (!hit) return undefined
  if (fresh && Date.now() - hit.at > GROUP_META_TTL_MS) return undefined
  return hit.meta
}

/** The group's metadata — cached, else asked of WhatsApp; a failed query falls
 *  back to whatever we last knew (possibly stale), else null. */
async function groupInfo(id, jid) {
  const fresh = cachedGroup(id, jid)
  if (fresh) return fresh
  const sock = accounts.get(id)?.sock
  try {
    const meta = sock ? await sock.groupMetadata(jid) : null
    if (meta) {
      groupCache.set(groupKey(id, jid), { meta, at: Date.now() })
      return meta
    }
  } catch (e) {
    logEvent('groupMetaFailed', { acct: id, jid, error: String(e?.message || e) })
  }
  return cachedGroup(id, jid, false) || null
}

/** Decrypt+download an inbound attachment (only this process holds the Signal
 *  session keys) and hand the bytes to the API, which stores them next to the
 *  Cloud downloads and attaches them to the just-pushed message. The API's answer
 *  is checked: a non-2xx is retried (up to 3 tries with backoff — the push can
 *  race the /inbound one) and a final failure is logged as mediaPushFailed with
 *  its status; the chat then shows "not available" for the attachment. */
async function pushInboundMedia(id, m, waId, media, fromMe = false, sender = null) {
  try {
    const declared = Number(media.node?.fileLength || 0)
    if (declared > MAX_MEDIA_BYTES) {
      logEvent('mediaSkip', { acct: id, id: m.key?.id, kind: media.key, bytes: declared })
      return
    }
    const sock = acct(id).sock
    // download from the UNWRAPPED content: Baileys' own walk skips some wrappers
    // (a lottie sticker's inner stickerMessage) that messages.js opens
    const inner = unwrap(m.message)
    const buffer = await downloadMediaMessage(inner ? { ...m, message: inner } : m, 'buffer', {},
      { logger, reuploadRequest: sock ? sock.updateMediaMessage : undefined })
    if (!buffer || buffer.length > MAX_MEDIA_BYTES) return
    const mime = String(media.node?.mimetype || '').split(';')[0].trim()
    const mimeExt = mime.includes('/') ? mime.split('/')[1].replace(/[^a-z0-9]/gi, '').slice(0, 8) : ''
    const filename = media.node?.fileName || `${m.key?.id || 'media'}.${mimeExt || media.ext}`
    const send = () => {
      // a fresh form per try (a body stream is consumed once)
      const form = new FormData()
      form.append('wa_id', waId)
      // the receiving number: the API files the file in THAT number's company's conversation
      form.append('account_id', id)
      form.append('msg_id', m.key?.id || '')
      form.append('media_type', media.type)
      // the customer's own file name (documents) — a photo or voice note has none
      form.append('file_name', String(media.node?.fileName || ''))
      // a file the person sent from the phone: the API files it as their outbound message
      if (fromMe) form.append('from_me', '1')
      // a group: who in the group sent the file (the thread itself is the group)
      if (sender) {
        form.append('sender_id', sender.id || '')
        form.append('sender_name', sender.name || '')
      }
      form.append('file', new Blob([buffer], { type: mime || 'application/octet-stream' }), filename)
      // no explicit Content-Type header — fetch sets the multipart boundary
      return fetch(`${API_URL}/api/whatsapp/baileys/media`, {
        method: 'POST', headers: { 'X-Baileys-Secret': SECRET }, body: form,
      })
    }
    const r = await postWithRetry(send, { attempts: 3, baseDelayMs: 1000 })
    if (r.ok) {
      logEvent('mediaPush', { acct: id, id: m.key?.id, kind: media.key, bytes: buffer.length, attempts: r.attempts })
    } else {
      logEvent('mediaPushFailed', { acct: id, id: m.key?.id, kind: media.key, bytes: buffer.length,
        status: r.status, attempts: r.attempts, error: r.error })
      console.error(`[${id}] media push failed (HTTP ${r.status || 'network'}): ${r.error}`)
    }
  } catch (e) {
    logEvent('mediaPushFailed', { acct: id, id: m.key?.id, kind: media.key, status: 0, error: String(e?.message || e) })
    console.error(`[${id}] media download/push failed:`, e.message)
  }
}

async function pushInbound(id, m) {
  const parsed = parseInbound(m.message)
  if (!parsed.reaction && !hasContent(unwrap(m.message))) return // session-setup / protocol-only envelope
  const jid = m.key?.remoteJid || ''
  const kind = chatKind(jid)
  if (kind === 'group') return pushGroupInbound(id, m, jid, parsed)
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
    return // broadcast / status / channels
  }
  // a reaction lands on the message it targets — the API never makes a bubble of it
  const body = parsed.reaction
    ? { account_id: id, wa_id: waId, reaction: parsed.reaction }
    : {
        account_id: id,
        wa_id: waId,
        name: m.pushName || '',
        text: parsed.text,
        msg_id: m.key?.id || '',
        reply_to: parsed.replyTo,
      }
  if (!parsed.reaction) recentMessages.remember(m)
  // the API answers non-2xx when it could not take the message (a restart, a
  // wrong secret): retried twice, then logged — the media push still follows,
  // and creates the row itself when the text never landed
  const r = await postWithRetry(() => fetch(`${API_URL}/api/whatsapp/baileys/inbound`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Baileys-Secret': SECRET },
    body: JSON.stringify(body),
  }), { attempts: 3, baseDelayMs: 500 })
  if (!r.ok) {
    logEvent('inboundPushFailed', { acct: id, id: m.key?.id, status: r.status, attempts: r.attempts, error: r.error })
    console.error(`[${id}] inbound push failed (HTTP ${r.status || 'network'}): ${r.error}`)
  }
  if (parsed.reaction) return
  // attachment (image/pdf/sticker/…): download + attach AFTER the text push
  // so the message row exists to hang the media on; fire-and-forget
  const media = mediaKind(unwrap(m.message))
  if (media) pushInboundMedia(id, m, waId, media)
}

/**
 * A message in a GROUP the number belongs to. The thread is the group itself
 * (wa_id = the full …@g.us jid, never digits — a digits key would match a random
 * contact by phone tail); who wrote it rides in `sender`, the group's name in
 * `group.subject`, and `mentioned` says whether it was addressed to us (an
 * @mention or a reply to one of our messages) — the API answers only then.
 */
async function pushGroupInbound(id, m, jid, parsed) {
  const sock = accounts.get(id)?.sock
  const sender = groupSender(m)
  const meta = await groupInfo(id, jid)
  const subject = String(meta?.subject || '')
  const body = parsed.reaction
    ? { account_id: id, wa_id: jid, reaction: parsed.reaction, sender }
    : {
        account_id: id,
        wa_id: jid,
        name: subject,
        text: parsed.text,
        msg_id: m.key?.id || '',
        reply_to: parsed.replyTo,
        group: { jid, subject },
        sender,
        mentioned: mentionsMe(m.message, meJids(sock?.user)),
      }
  if (!parsed.reaction) recentMessages.remember(m)
  const r = await postWithRetry(() => fetch(`${API_URL}/api/whatsapp/baileys/inbound`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Baileys-Secret': SECRET },
    body: JSON.stringify(body),
  }), { attempts: 3, baseDelayMs: 500 })
  if (!r.ok) {
    logEvent('inboundPushFailed', { acct: id, id: m.key?.id, group: jid, status: r.status, attempts: r.attempts, error: r.error })
    console.error(`[${id}] group inbound push failed (HTTP ${r.status || 'network'}): ${r.error}`)
  }
  if (parsed.reaction) return
  const media = mediaKind(unwrap(m.message))
  if (media) pushInboundMedia(id, m, jid, media, false, sender)
}

/**
 * A message the person sent from the PHONE (or WhatsApp Web) — another device of this
 * number. Without this the API never saw it: the chat looked unanswered, the AI could
 * answer the customer a second time and an open handover stayed open. Pushed to
 * /from-phone (the API stores it as their reply; ChatBranch's own sends never come
 * here — messages.js phoneSent), then its file, like an inbound one.
 */
async function pushFromPhone(id, m) {
  if (!hasContent(unwrap(m.message))) return
  const jid = m.key?.remoteJid || ''
  let waId = ''
  if (jid.endsWith('@s.whatsapp.net')) {
    waId = digits(jid.split('@')[0])
  } else {
    // a lid chat: the customer's phone from the envelope, else a mapping an inbound taught us
    // (never senderPn — on our own message that is OUR number)
    const alt = String(m.key?.remoteJidAlt || '')
    waId = alt.includes('@s.whatsapp.net') ? digits(alt.split('@')[0]) : phoneForLid(jid) || ''
    if (waId && alt) rememberLid(waId, jid)
  }
  if (!waId) {
    logEvent('fromPhoneSkip', { acct: id, id: m.key?.id, jid, why: 'unknown lid chat' })
    return
  }
  if (waId === digits(acct(id).me || '')) return // a note to self is not a conversation
  const parsed = parseInbound(m.message)
  recentMessages.remember(m)
  const r = await postWithRetry(() => fetch(`${API_URL}/api/whatsapp/baileys/from-phone`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Baileys-Secret': SECRET },
    body: JSON.stringify({
      account_id: id, wa_id: waId, text: parsed.text, msg_id: m.key?.id || '',
      reply_to: parsed.replyTo, ts: messageSeconds(m),
    }),
  }), { attempts: 3, baseDelayMs: 500 })
  if (r.ok) logEvent('fromPhone', { acct: id, id: m.key?.id, to: waId })
  else {
    logEvent('fromPhonePushFailed', { acct: id, id: m.key?.id, status: r.status, attempts: r.attempts, error: r.error })
    console.error(`[${id}] from-phone push failed (HTTP ${r.status || 'network'}): ${r.error}`)
  }
  const media = mediaKind(unwrap(m.message))
  if (media) pushInboundMedia(id, m, waId, media, true)
}

/**
 * "Delete for everyone" in a 1:1 chat — by the customer, or (fromMe) on this number's
 * own phone. Pushed to /recall: the API keeps the message marked as deleted or erases
 * it, per the inbox's "keep recalled messages" switch (Channels/MessageRecalls).
 */
async function pushRecall(id, m, recall) {
  const jid = m.key?.remoteJid || ''
  let waId = ''
  if (jid.endsWith('@s.whatsapp.net')) {
    waId = digits(jid.split('@')[0])
  } else {
    // a lid chat: the phone from the envelope (never senderPn on our own message — that is
    // OUR number), else a mapping an earlier inbound taught us
    const alt = String((recall.fromMe ? '' : m.key?.senderPn || m.key?.participantPn || '') || m.key?.remoteJidAlt || '')
    waId = alt.includes('@s.whatsapp.net') ? digits(alt.split('@')[0]) : phoneForLid(jid) || ''
  }
  if (!waId) {
    logEvent('recallSkip', { acct: id, id: recall.id, jid, why: 'unknown lid chat' })
    return
  }
  const r = await postWithRetry(() => fetch(`${API_URL}/api/whatsapp/baileys/recall`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Baileys-Secret': SECRET },
    body: JSON.stringify({ account_id: id, wa_id: waId, msg_id: recall.id, from_me: recall.fromMe }),
  }), { attempts: 3, baseDelayMs: 500 })
  if (r.ok) logEvent('recall', { acct: id, id: recall.id, by: recall.fromMe ? 'us' : 'customer' })
  else logEvent('recallPushFailed', { acct: id, id: recall.id, status: r.status, attempts: r.attempts, error: r.error })
}

/**
 * Push a replayed batch of the number's EXISTING chats to the API, which files
 * them as a backfill (Channels/WhatsAppHistoryImport.cs): the words arrive, the
 * side effects do not — no AI reply, no flows, no automation rules, no
 * notifications, no reopening a conversation someone had finished.
 *
 * Only importable rows are sent (history.js: 1:1 chats, text and placeholders,
 * inside the window the inbox asked for), in batches the API accepts, and the
 * answer is checked like every other push — a failure is logged with its status
 * rather than lost, because nothing replays a history replay.
 */
async function pushHistory(id, messages, days, detail = {}) {
  const rows = historyRows(messages, { parse: parseInbound, unwrap, phoneForLid, days })
  logEvent('history', { acct: id, replayed: (messages || []).length, importable: rows.length, ...detail })
  if (!rows.length) return
  let stored = 0
  for (const batch of historyChunks(rows, 500)) {
    const r = await postWithRetry(() => fetch(`${API_URL}/api/whatsapp/baileys/history`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Baileys-Secret': SECRET },
      body: JSON.stringify({ account_id: id, messages: batch }),
    }), { attempts: 3, baseDelayMs: 500 })
    if (!r.ok) {
      logEvent('historyPushFailed', { acct: id, rows: batch.length, status: r.status, error: r.error })
      return
    }
    stored += batch.length
  }
  console.log(`[${id}] imported ${stored} message(s) of existing history`)
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
    await fetch(`${API_URL}/api/whatsapp/baileys/status-event`, {
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

/**
 * Bring a number's session up. `phone` = pair by PHONE NUMBER (WhatsApp's
 * "Link with phone number": the person types an 8-character code instead of
 * scanning) — digits only, E.164 without the +; omitted = pair by QR. It is
 * only asked for on a session that is not registered yet; a session that
 * already has credentials just reconnects.
 */
async function startAccount(id, phone = null) {
  const a = acct(id)
  if (a.state === 'connected' || a.state === 'starting') return
  a.state = 'starting'
  a.error = null
  a.pairingCode = null
  a.pairPhone = phone || null
  // Each start OWNS the account from here on. An older socket's events — and
  // above all its 3s reconnect timer — must not write to the account once a
  // newer attempt has taken over, which is exactly what happens when a person
  // presses Pair again or switches between a QR and a phone number.
  const gen = (a.gen = (a.gen || 0) + 1)
  if (a.sock) {
    try { a.sock.end(new Error('pairing restarted')) } catch { /* already closed */ }
    a.sock = null
  }
  const dir = path.join(AUTH_ROOT, String(id))
  mkdirSync(dir, { recursive: true })
  const { state, saveCreds } = await useMultiFileAuthState(dir)
  // A session that is already registered reconnects with its stored keys —
  // WhatsApp refuses a pairing code for one, so never ask.
  if (state.creds?.registered) a.pairPhone = null
  // WhatsApp rejects/ignores stale protocol versions silently (session hangs
  // in "starting" with no QR) — always pair with the current one.
  const { version } = await fetchLatestBaileysVersion()

  const settings = loadSettings(id)
  const keepPhone = !!settings.keep_phone_notifications
  // How much of the number's own history to import. Only "everything" asks WhatsApp
  // for a full sync (tens of thousands of messages on a busy number); a day window
  // rides on the recent history a new companion is sent anyway.
  const historyDays = historyWindowDays(settings)
  const sock = makeWASocket({
    version,
    auth: state,
    logger,
    printQRInTerminal: false,
    // Must be a REAL os/browser pair. "Link with phone number" sends it to the
    // phone as the companion's platform, and a made-up OS name ("ChatBranch")
    // makes the phone reject the code with "couldn't link device — get a new code".
    browser: Browsers.ubuntu('Chrome'),
    syncFullHistory: wantsFullSync(historyDays),
    // "Keep phone notifications": never announce online on connect (presence.js)
    markOnlineOnConnect: !keepPhone,
    // group sends read the participant list from here instead of querying it each time
    cachedGroupMetadata: async (jid) => cachedGroup(id, jid),
  })
  a.sock = sock
  a.presence?.stop()
  a.presence = createPresence({ send: (kind) => sock.sendPresenceUpdate(kind), idleMs: PRESENCE_IDLE_MS })

  // watchdog: a healthy pairing emits a QR within seconds — surface a hang
  setTimeout(() => {
    if (a.gen !== gen) return   // a newer attempt owns the account
    if (a.state === 'starting') {
      a.state = 'error'
      a.error = 'No response from WhatsApp within 30s — check the gateway console/network and press Pair to retry.'
      try { sock.end(new Error('pairing watchdog timeout')) } catch { /* already closed */ }
    }
  }, 30000)

  sock.ev.on('creds.update', saveCreds)

  // group membership changed: the cached participant list is wrong — drop it
  sock.ev.on('group-participants.update', ({ id: gid }) => {
    if (gid) groupCache.delete(groupKey(id, gid))
  })
  // a rename (or other setting change): keep the cached entry, take the new subject
  sock.ev.on('groups.update', (updates) => {
    for (const u of updates || []) {
      const hit = u?.id ? groupCache.get(groupKey(id, u.id)) : null
      if (hit && typeof u.subject === 'string') hit.meta = { ...hit.meta, subject: u.subject }
    }
  })

  // The chats this number already had. WhatsApp replays them on connect (and in
  // further batches as it catches up); each batch is filtered and pushed as a
  // BACKFILL — the API stores the words and runs nothing else (no AI, no flows,
  // no automations, no notifications).
  if (historyDays !== 0) {
    sock.ev.on('messaging-history.set', async ({ messages, syncType, progress }) => {
      if (a.gen !== gen) return   // a newer pairing attempt owns this account now
      try {
        await pushHistory(id, messages, historyDays, { syncType, progress })
      } catch (e) {
        logEvent('historyPushFailed', { acct: id, error: e.message })
      }
    })
  }

  sock.ev.on('connection.update', async ({ connection, lastDisconnect, qr }) => {
    if (a.gen !== gen) return   // this socket has been superseded — stay quiet
    if (connection) logEvent('connection', { acct: id, connection })
    // Once a pairing code is on screen, WhatsApp keeps rotating QR refs every
    // ~20s on the same socket. Those must NOT replace the code: flipping the
    // state back to "qr" made the dashboard drop the code mid-typing.
    if (qr && a.pairingCode) return
    if (qr) {
      // Pairing by phone number rides on the same event: requestPairingCode needs
      // the handshake to be up, and the first QR is the proof that it is. If
      // WhatsApp refuses the number we keep the QR we already have rather than
      // leaving the person with nothing.
      if (a.pairPhone && !a.pairingCode) {
        try {
          a.pairingCode = normalizePairingCode(await sock.requestPairingCode(a.pairPhone))
          a.qr = null
          a.state = 'pairing_code'
          console.log(`[${id}] pairing code ready for +${a.pairPhone}`)
          return
        } catch (e) {
          a.pairPhone = null
          a.error = `WhatsApp would not give a pairing code for that number (${e.message}) — scan the QR instead.`
          logEvent('pairingCodeFailed', { acct: id, error: e.message })
        }
      }
      a.qr = await toDataURL(qr)
      a.state = 'qr'
      console.log(`[${id}] QR ready — scan from the dashboard`)
    }
    if (connection === 'open') {
      a.state = 'connected'
      a.qr = null
      a.pairingCode = null
      a.pairPhone = null
      a.me = digits((sock.user?.id || '').split(':')[0])
      console.log(`[${id}] connected as +${a.me}`)
      // announce presence — companions that never do get their sends deprioritized;
      // a "keep phone notifications" number stays quiet and comes online per send instead
      a.presence.configure(!!loadSettings(id).keep_phone_notifications).catch(() => {})
      pushStatusEvent(id, 'connected', '')
    }
    if (connection === 'close') {
      a.presence?.stop()
      // the VoIP session (calls.js) is bound to this dead socket — end any
      // live call now; the reconnect below hands calls a fresh socket later
      if (_calls) {
        try { _calls.destroySession(id) } catch { /* no session for this account */ }
      }
      const code = lastDisconnect?.error?.output?.statusCode
      if (code === DisconnectReason.loggedOut) {
        a.state = 'logged_out'
        a.qr = null
        a.pairingCode = null
        a.pairPhone = null
        a.me = null
        rmSync(dir, { recursive: true, force: true })
        console.log(`[${id}] logged out — auth state wiped`)
        pushStatusEvent(id, 'logged_out', `${disconnectReasonName(code)} (${code})`)
      } else if (code === 408 && !state.creds?.registered) {
        // Never paired and the QR / pairing code ran out unused. Reconnecting here
        // only mints new QRs nobody sees, forever — stop and wait for Pair.
        // (`registered`, not `me`: requestPairingCode already fills creds.me, and
        // reconnecting on those half-made creds ends in a 401 that wipes them.)
        a.state = 'qr_expired'
        a.qr = null
        a.pairingCode = null
        a.pairPhone = null
        a.error = null
        // drop the half-made pairing creds so the next Pair starts clean
        rmSync(dir, { recursive: true, force: true })
        console.log(`[${id}] QR not scanned in time — waiting for Pair`)
        pushStatusEvent(id, 'qr_expired', 'not paired: the QR code was not scanned in time')
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
    // a group: the thread is keyed by the group jid itself
    else if (chatKind(jid) === 'group') waId = jid
    if (!key?.fromMe || !key?.id || !waId) return
    try {
      await fetch(`${API_URL}/api/whatsapp/baileys/message-status`, {
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
      // "delete for everyone" (either side) — never a bubble, never "sent from the phone"
      const recall = recallOf(m, type)
      if (recall) {
        pushRecall(id, m, recall)
        continue
      }
      // a re-wording (either side) — the same row, never a second bubble
      const edit = editOf(m, type)
      if (edit) {
        pushEdit(id, m, edit)
        continue
      }
      if (m.key?.fromMe) {
        // typed on the phone / WhatsApp Web: the person's own reply (never our own sends)
        if (phoneSent(m, type, (mid) => !!ownSends.get(mid))) pushFromPhone(id, m)
        continue
      }
      pushInbound(id, m)
    }
  })
}

/**
 * An edit WhatsApp reported — made on our own phone / WhatsApp Web, or by the
 * customer on their own message. Pushed like a recall and for the same reason:
 * the inbox must show what the two of them now see, not the wording that was
 * replaced. Never a new bubble.
 */
async function pushEdit(id, m, edit) {
  const jid = m.key?.remoteJid || ''
  let waId = ''
  if (jid.endsWith('@s.whatsapp.net')) {
    waId = digits(jid.split('@')[0])
  } else {
    // a lid chat: the phone from the envelope (never senderPn on our own message —
    // that is OUR number), else a mapping an earlier inbound taught us
    const alt = String((edit.fromMe ? '' : m.key?.senderPn || m.key?.participantPn || '') || m.key?.remoteJidAlt || '')
    waId = alt.includes('@s.whatsapp.net') ? digits(alt.split('@')[0]) : phoneForLid(jid) || ''
  }
  if (!waId) {
    logEvent('editSkip', { acct: id, id: edit.id, jid, why: 'unknown lid chat' })
    return
  }
  const r = await postWithRetry(() => fetch(`${API_URL}/api/whatsapp/baileys/edited`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Baileys-Secret': SECRET },
    body: JSON.stringify({ account_id: id, wa_id: waId, msg_id: edit.id, from_me: edit.fromMe, text: edit.text }),
  }), { attempts: 3, baseDelayMs: 500 })
  if (r.ok) logEvent('edited', { acct: id, id: edit.id, by: edit.fromMe ? 'us' : 'customer' })
  else logEvent('editPushFailed', { acct: id, id: edit.id, status: r.status, attempts: r.attempts, error: r.error })
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

/** The connected account + the chat jid a send to `to` must target (lid-aware). */
async function resolveTarget(id, to, rawJid = null) {
  const a = accounts.get(id)
  if (!a || a.state !== 'connected') throw new Error(`account ${id} is not connected (state: ${a?.state || 'stopped'})`)
  // a group jid needs no lookup (and has no phone to look up)
  let jid = targetJid(to, rawJid)
  const group = chatKind(jid) === 'group'
  const d = group ? jid : digits(to)
  // warm the metadata cache: Baileys reads the participant list from it for the send
  if (group) await groupInfo(id, jid)
  if (!jid) {
    const check = await checkNumber(id, d)
    if (check.exists === false) throw new Error(`+${d} is not on WhatsApp — check the number`)
    jid = check.jid || `${d}@s.whatsapp.net`
  }
  return { a, d, jid }
}

/** Brief "typing…" before a send — reads human, and companion sessions that
 *  never emit presence look like bots to WhatsApp's scoring. */
async function humanPause(a, jid) {
  await a.presence?.active()
  try {
    await a.sock.presenceSubscribe(jid)
    await a.sock.sendPresenceUpdate('composing', jid)
    await new Promise((r) => setTimeout(r, 900 + Math.floor(Math.random() * 1800)))
    await a.sock.sendPresenceUpdate('paused', jid)
  } catch { /* presence is best-effort */ }
}

async function sendText(id, to, text, rawJid = null, quote = null) {
  const { a, d, jid } = await resolveTarget(id, to, rawJid)
  await humanPause(a, jid)
  const quoted = quote ? buildQuoted({ jid, ...quote }, recentMessages) : null
  const result = await a.sock.sendMessage(jid, { text }, quoted ? { quoted } : undefined)
  if (result) { recentMessages.remember(result); ownSends.remember(result) }
  logEvent('send', { acct: id, to: d, jid, id: result?.key?.id || '', quoted: quote?.id || '' })
  return result?.key?.id || ''
}

/** An emoji reaction on a message of the chat ("" removes ours). No typing pause:
 *  a reaction is a tap, not a message. */
async function sendReaction(id, to, msgId, fromMe, emoji, rawJid = null, participant = '') {
  const { a, d, jid } = await resolveTarget(id, to, rawJid)
  await a.presence?.active()
  const result = await a.sock.sendMessage(jid, buildReact({ jid, id: msgId, fromMe, emoji, participant }))
  if (result) ownSends.remember(result)
  logEvent('react', { acct: id, to: d, jid, target: msgId, emoji: emoji || '(removed)' })
  return result?.key?.id || ''
}

/**
 * "Delete for everyone" on a message WE sent. WhatsApp keeps its own window
 * (about two days) and refuses past it — that refusal is returned as a failure
 * rather than smoothed over, because the message is still on the customer's
 * phone and the operator needs to know.
 */
async function revokeMessage(id, to, msgId, fromMe, rawJid = null) {
  const { a, d, jid } = await resolveTarget(id, to, rawJid)
  await a.presence?.active()
  const result = await a.sock.sendMessage(jid, buildDelete({ jid, id: msgId, fromMe }))
  if (result) ownSends.remember(result)
  logEvent('revoke', { acct: id, to: d, jid, target: msgId })
  return result?.key?.id || ''
}

/**
 * Edit a TEXT message we sent. WhatsApp allows it for about 15 minutes; the
 * edit arrives as its own message, so the id returned is the EDIT's — the
 * original keeps its own id, which is what the backend's row is keyed on.
 */
async function editMessage(id, to, msgId, fromMe, text, rawJid = null) {
  const { a, d, jid } = await resolveTarget(id, to, rawJid)
  await a.presence?.active()
  const result = await a.sock.sendMessage(jid, buildEdit({ jid, id: msgId, fromMe, text }))
  if (result) ownSends.remember(result)
  logEvent('edit', { acct: id, to: d, jid, target: msgId })
  return result?.key?.id || ''
}

/**
 * A true WhatsApp FORWARD: the original message re-sent into another chat with
 * WhatsApp's own "Forwarded" marking. That needs the whole original, not just
 * its key, so it only works while the gateway still holds it (the last ~2000
 * messages in either direction, lost on restart). When it does not, this
 * REFUSES — re-sending the text or the file would arrive unmarked and be a
 * different thing from what the operator asked for.
 */
async function forwardMessage(id, to, msgId, rawJid = null) {
  const original = recentMessages.get(msgId)
  if (!original) {
    const e = new Error('that message is no longer held by the gateway, so it cannot be forwarded')
    e.status = 409
    throw e
  }
  const { a, d, jid } = await resolveTarget(id, to, rawJid)
  await humanPause(a, jid)
  const result = await a.sock.sendMessage(jid, { forward: original })
  if (result) { recentMessages.remember(result); ownSends.remember(result) }
  logEvent('forward', { acct: id, to: d, jid, source: msgId, id: result?.key?.id || '' })
  return result?.key?.id || ''
}

/** A pinned location, a contact card or a poll — one builder each, one send. */
async function sendBuilt(id, to, content, what, rawJid = null) {
  const { a, d, jid } = await resolveTarget(id, to, rawJid)
  await humanPause(a, jid)
  const result = await a.sock.sendMessage(jid, content)
  if (result) ownSends.remember(result)
  logEvent(what, { acct: id, to: d, jid, id: result?.key?.id || '' })
  return result?.key?.id || ''
}

/** An image / video / audio / document already validated by media.js
 *  buildMediaMessage — Baileys encrypts and uploads it to WhatsApp's CDN. */
async function sendMedia(id, to, built, rawJid = null) {
  const { a, d, jid } = await resolveTarget(id, to, rawJid)
  await humanPause(a, jid)
  const result = await a.sock.sendMessage(jid, built.content)
  if (result) ownSends.remember(result)
  logEvent('sendMedia', { acct: id, to: d, jid, kind: built.mediaType, bytes: built.bytes, id: result?.key?.id || '' })
  return result?.key?.id || ''
}

/** Presence for a chat — the backend shows "typing…" while the AI composes a
 *  reply (state: composing | paused | available). Best-effort by design. */
async function sendPresence(id, to, state) {
  const a = accounts.get(id)
  if (!a || a.state !== 'connected') throw new Error(`account ${id} is not connected (state: ${a?.state || 'stopped'})`)
  const d = digits(to)
  const jid = targetJid(to, null) || lidHints.get(d) || `${d}@s.whatsapp.net`
  const kind = ['composing', 'paused', 'available'].includes(state) ? state : 'composing'
  await a.presence?.active()
  try { await a.sock.presenceSubscribe(jid) } catch { /* optional */ }
  await a.sock.sendPresenceUpdate(kind, jid)
  return { ok: true, state: kind }
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
    await fetch(`${API_URL}/api/whatsapp/baileys/call-event`, {
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
    const m = req.url.match(/^\/accounts\/([\w-]+)\/(status|start|send|send-media|react|revoke|edit|forward|send-location|send-contact|send-poll|presence|logout|check|call|call-end|call-status|settings)$/)
    if (!m) return json(res, 404, { detail: 'not found' })
    const [, id, action] = m
    try {
      if (action === 'status') {
        const a = accounts.get(id)
        const keep = !!loadSettings(id).keep_phone_notifications
        return json(res, 200, a
          ? { state: a.state, qr: a.qr, pairing_code: a.pairingCode, pairing_phone: a.pairPhone, me: a.me, error: a.error, keep_phone_notifications: keep, online: a.presence ? a.presence.online : null }
          : { state: 'stopped', qr: null, pairing_code: null, pairing_phone: null, me: null, error: null, keep_phone_notifications: keep, online: null })
      }
      if (action === 'settings') {
        // GET: the number's settings; POST {keep_phone_notifications?, import_history_days?}:
        // store, and apply presence now (the history window is read at the next pairing,
        // since syncFullHistory is a socket option)
        const stored = loadSettings(id)
        if (req.method === 'GET') {
          return json(res, 200, {
            keep_phone_notifications: !!stored.keep_phone_notifications,
            import_history_days: historyWindowDays(stored),
          })
        }
        if (req.method !== 'POST') return json(res, 405, { detail: 'GET or POST' })
        const body = await readBody(req)
        const keep = body.keep_phone_notifications
        const histRaw = body.import_history_days
        const hasKeep = typeof keep === 'boolean'
        const hasHist = histRaw !== undefined && histRaw !== null
        if (!hasKeep && !hasHist) {
          return json(res, 400, {
            detail: 'keep_phone_notifications (true/false) or import_history_days (0 | 30 | 90 | -1) is required',
          })
        }
        const next = { ...stored }
        if (hasKeep) next.keep_phone_notifications = keep
        if (hasHist) {
          const days = Number.parseInt(String(histRaw), 10)
          if (!HISTORY_CHOICES.includes(days)) {
            return json(res, 400, { detail: 'import_history_days must be one of 0, 30, 90, -1' })
          }
          next.import_history_days = days
        }
        saveSettings(id, next)
        const a = accounts.get(id)
        if (hasKeep && a?.state === 'connected' && a.presence) await a.presence.configure(keep)
        logEvent('settings', {
          acct: id,
          keep_phone_notifications: !!next.keep_phone_notifications,
          import_history_days: historyWindowDays(next),
        })
        return json(res, 200, {
          ok: true,
          keep_phone_notifications: !!next.keep_phone_notifications,
          import_history_days: historyWindowDays(next),
        })
      }
      if (action === 'call-status') {
        // idle until calls.js has ever been loaded — don't boot WASM for a poll
        return json(res, 200, _calls ? (await callsModule()).callStatus(id) : { status: 'idle', call_id: null })
      }
      if (req.method !== 'POST') return json(res, 405, { detail: 'POST required' })
      if (action === 'start') {
        // {} = pair by QR; {phone: "+60123456789"} = pair by phone number, which
        // WhatsApp answers with an 8-character code the person types on the phone.
        const { phone } = await readBody(req)
        let msisdn = null
        if (phone !== undefined && phone !== null && String(phone).trim() !== '') {
          msisdn = pairingPhone(phone)
          if (!msisdn) {
            return json(res, 400, {
              detail: 'phone must be a full international number with its country code (digits only, no leading 0).',
            })
          }
        }
        await startAccount(id, msisdn)
        const a = acct(id)
        return json(res, 200, { ok: true, state: a.state, pairing_code: a.pairingCode, pairing_phone: a.pairPhone })
      }
      if (action === 'send') {
        const { to, text, raw_jid, quoted_id, quoted_from_me, quoted_text, quoted_participant } = await readBody(req)
        if (!to || !text) return json(res, 400, { detail: 'to and text are required' })
        // in a group, quoting someone else's message also names its author (quoted_participant)
        const quote = typeof quoted_id === 'string' && quoted_id
          ? {
              id: quoted_id,
              fromMe: !!quoted_from_me,
              text: typeof quoted_text === 'string' ? quoted_text : '',
              participant: typeof quoted_participant === 'string' ? quoted_participant : '',
            }
          : null
        return json(res, 200, { id: await sendText(id, to, text, raw_jid || null, quote) })
      }
      if (action === 'react') {
        const { to, msg_id, from_me, emoji, raw_jid, participant } = await readBody(req)
        if (!to || !msg_id) return json(res, 400, { detail: 'to and msg_id are required' })
        if (emoji != null && typeof emoji !== 'string') return json(res, 400, { detail: 'emoji must be a string' })
        return json(res, 200, { id: await sendReaction(id, to, msg_id, !!from_me, emoji || '', raw_jid || null,
          typeof participant === 'string' ? participant : '') })
      }
      if (action === 'revoke') {
        const { to, msg_id, from_me, raw_jid } = await readBody(req)
        if (!to || !msg_id) return json(res, 400, { detail: 'to and msg_id are required' })
        return json(res, 200, { id: await revokeMessage(id, to, msg_id, from_me !== false, raw_jid || null) })
      }
      if (action === 'edit') {
        const { to, msg_id, from_me, text, raw_jid } = await readBody(req)
        if (!to || !msg_id) return json(res, 400, { detail: 'to and msg_id are required' })
        if (typeof text !== 'string' || text.trim() === '') return json(res, 400, { detail: 'text is required' })
        return json(res, 200, { id: await editMessage(id, to, msg_id, from_me !== false, text, raw_jid || null) })
      }
      if (action === 'forward') {
        const { to, msg_id, raw_jid } = await readBody(req)
        if (!to || !msg_id) return json(res, 400, { detail: 'to and msg_id are required' })
        return json(res, 200, { id: await forwardMessage(id, to, msg_id, raw_jid || null) })
      }
      if (action === 'send-location' || action === 'send-contact' || action === 'send-poll') {
        const body = await readBody(req)
        if (!body.to) return json(res, 400, { detail: 'to is required' })
        let content
        try {
          content = action === 'send-location'
            ? buildLocation(body)
            : action === 'send-contact'
              ? buildContact(body)
              : buildPoll(body)
        } catch (e) {
          // the builders refuse bad input by throwing — that is the operator's mistake
          return json(res, 400, { detail: e.message })
        }
        const what = action.replace('send-', '')
        return json(res, 200, { id: await sendBuilt(id, body.to, content, what, body.raw_jid || null) })
      }
      if (action === 'send-media') {
        // capped read: a file rides in this body (base64, ≤16 MB) — never a path or URL
        const read = await readJsonBodyCapped(req, MAX_SEND_MEDIA_BODY)
        if (read.tooLarge) return json(res, 413, { detail: 'file too large (max 16 MB)' })
        const { to, raw_jid } = read.body
        if (!to) return json(res, 400, { detail: 'to is required' })
        let built
        try {
          built = buildMediaMessage(read.body)
        } catch (e) {
          if (e instanceof MediaRequestError) return json(res, e.status, { detail: e.message })
          throw e
        }
        return json(res, 200, { id: await sendMedia(id, to, built, raw_jid || null) })
      }
      if (action === 'presence') {
        const { to, state } = await readBody(req)
        if (!to) return json(res, 400, { detail: 'to is required' })
        return json(res, 200, await sendPresence(id, to, state))
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
      // a handler may say the caller asked for something impossible (409) rather
      // than the provider having failed (502) — forwarding a message we no longer hold
      return json(res, e?.status || 502, { detail: e.message })
    }
  })
  .listen(PORT, HOST, () => {
    console.log(`Baileys gateway on http://${HOST}:${PORT} (auth state: ${AUTH_ROOT})`)
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
