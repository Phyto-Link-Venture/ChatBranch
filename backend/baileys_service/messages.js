/**
 * Message shapes for the Baileys gateway — the pure half of quoted replies,
 * reactions and the inbound text placeholders (index.js does the socket work).
 *
 *   parseInbound(message)  -> { text, replyTo, reaction }
 *       reaction  = { msg_id, emoji }   (emoji "" = the customer removed it)
 *       replyTo   = the stanza id the message quotes ("" = not a reply)
 *       text      = what the inbox stores: the text / caption, or a placeholder
 *                   ("[image]", "[location] lat,lng label", "[contact] Name · +phone")
 *   KeyStore               -> a small LRU of recent WAMessages by id, so a quote
 *                             can carry the real quoted message
 *   buildQuoted(...)       -> the `quoted` option for sock.sendMessage
 *   buildReact(...)        -> the `{ react }` content for sock.sendMessage
 *   buildDelete/buildEdit  -> "delete for everyone" and an edit of our own message
 *   buildLocation/buildContact/buildPoll
 *                          -> the richer things an operator can send
 *   mediaKind(message)     -> which attachment an (unwrapped) message carries:
 *                             { key, type, ext, node } — image | video | audio | document
 *   postWithRetry(...)     -> POST to the API, checking res.ok, retrying with backoff
 *   chatKind / groupSender / meJids / mentionsMe / targetJid
 *                          -> group chats: which chats are filed, who wrote a group
 *                             message, whether it addressed us, where a send goes
 *
 * Synchronous and dependency-free, so `node messages.js --selftest` (also run by
 * `node calls.js --selftest`) checks it offline.
 */
import path from 'path'
import { fileURLToPath, pathToFileURL } from 'url'

/** Wrappers WhatsApp puts around the real content (disappearing / view-once /
 *  edits / album children / status mentions / animated stickers) — the same set
 *  Baileys' normalizeMessageContent walks, kept here so the selftest runs offline. */
const WRAPPERS = ['ephemeralMessage', 'viewOnceMessage', 'viewOnceMessageV2', 'viewOnceMessageV2Extension',
  'documentWithCaptionMessage', 'editedMessage', 'associatedChildMessage', 'groupStatusMessage',
  'groupStatusMessageV2', 'botInvokeMessage', 'lottieStickerMessage']

/** Baileys' own normalizer, when index.js hands it over (setNormalizer). */
let normalizer = null
export function setNormalizer(fn) {
  normalizer = typeof fn === 'function' ? fn : null
}

export function unwrap(msg) {
  let m = msg
  if (normalizer && m) {
    try { m = normalizer(m) || m } catch { /* fall back to our own walk */ }
  }
  for (let i = 0; i < 6 && m && typeof m === 'object'; i++) {
    const key = WRAPPERS.find((k) => m[k]?.message)
    if (!key) break
    m = m[key].message
  }
  return m || null
}

/** Downloadable envelope kinds -> our media_type + a fallback extension.
 *  Stickers are webp images (animated webp still renders in <img>); a round
 *  video note (ptvMessage) is a video. */
export const MEDIA_KINDS = [
  ['imageMessage', 'image', 'jpg'],
  ['videoMessage', 'video', 'mp4'],
  ['ptvMessage', 'video', 'mp4'],
  ['audioMessage', 'audio', 'ogg'],
  ['documentMessage', 'document', 'bin'],
  ['stickerMessage', 'image', 'webp'],
]

export function mediaKind(msg) {
  for (const [key, type, ext] of MEDIA_KINDS) {
    if (msg?.[key]) return { key, type, ext, node: msg[key] }
  }
  return null
}

/**
 * POST to the API and check the answer: a non-2xx (or a network error) is retried
 * up to `attempts` times with a growing pause — the /media push can race the
 * /inbound one — and a 4xx other than 404/409/429 is final (the request itself
 * is wrong; repeating it changes nothing). Resolves { ok, status, attempts, error }.
 */
export async function postWithRetry(send, { attempts = 3, baseDelayMs = 500, sleep } = {}) {
  const wait = sleep || ((ms) => new Promise((r) => setTimeout(r, ms)))
  let last = { ok: false, status: 0, attempts: 0, error: '' }
  for (let i = 1; i <= attempts; i++) {
    try {
      const res = await send()
      if (res.ok) return { ok: true, status: res.status, attempts: i, error: '' }
      let detail = ''
      try { detail = String((await res.text()) || '').slice(0, 200) } catch { /* no body */ }
      last = { ok: false, status: res.status, attempts: i, error: detail }
      if (res.status >= 400 && res.status < 500 && ![404, 408, 409, 429].includes(res.status)) return last
    } catch (e) {
      last = { ok: false, status: 0, attempts: i, error: String(e?.message || e) }
    }
    if (i < attempts) await wait(baseDelayMs * 2 ** (i - 1))
  }
  return last
}

/** The first TEL (and FN) of a vCard. */
export function vcardFields(vcard) {
  let name = ''
  let phone = ''
  for (const raw of String(vcard || '').split('\n')) {
    const line = raw.replace(/\r$/, '')
    const i = line.indexOf(':')
    if (i <= 0) continue
    const key = line.slice(0, i).toUpperCase()
    const value = line.slice(i + 1).trim()
    if (!name && (key === 'FN' || key.startsWith('FN;'))) name = value
    if (!phone && (key === 'TEL' || key.startsWith('TEL;') || key.includes('.TEL'))) phone = value
  }
  return { name, phone }
}

export function contactText(name, phone) {
  const n = String(name || '').trim().replace(/\n/g, ' ')
  const p = String(phone || '').trim()
  if (!n && !p) return '[contact card]'
  return p ? `[contact] ${n || p} · ${p}` : `[contact] ${n}`
}

/**
 * A poll, in the same shape our own outbound poll stores, so one bubble reads
 * the same whoever sent it. WhatsApp does not hand back the VOTES (each
 * `pollUpdateMessage` is encrypted against the poll's creation message, which
 * this gateway only holds until it restarts), so the question and the options
 * are all there is to show.
 */
function pollText(poll) {
  const q = String(poll?.name ?? '').trim()
  const options = (Array.isArray(poll?.options) ? poll.options : [])
    .map((o) => String(o?.optionName ?? '').trim())
    .filter(Boolean)
  if (!q && options.length === 0) return '[poll]'
  return options.length > 0 ? `[poll] ${q} \u2014 ${options.join(' / ')}` : `[poll] ${q}`
}

function locationText(loc) {
  const lat = Number(loc?.degreesLatitude)
  const lng = Number(loc?.degreesLongitude)
  if (!Number.isFinite(lat) || !Number.isFinite(lng)) return '[location]'
  const label = [loc.name, loc.address].filter((s) => typeof s === 'string' && s.trim()).join(', ')
  return `[location] ${lat},${lng}${label ? ` ${label}` : ''}`
}

/** The text the inbox stores for one (unwrapped) message. */
export function extractText(msg) {
  if (!msg) return '[message]'
  if (msg.locationMessage || msg.liveLocationMessage) return locationText(msg.locationMessage || msg.liveLocationMessage)
  const poll = msg.pollCreationMessage || msg.pollCreationMessageV2 || msg.pollCreationMessageV3
  if (poll) return pollText(poll)
  if (msg.contactMessage) {
    const { name, phone } = vcardFields(msg.contactMessage.vcard)
    return contactText(msg.contactMessage.displayName || name, phone)
  }
  if (msg.contactsArrayMessage?.contacts?.length) {
    const first = msg.contactsArrayMessage.contacts[0]
    const { name, phone } = vcardFields(first.vcard)
    return contactText(first.displayName || name, phone)
  }
  return (
    msg.conversation ||
    msg.extendedTextMessage?.text ||
    msg.imageMessage?.caption ||
    msg.videoMessage?.caption ||
    msg.documentMessage?.caption ||
    (msg.imageMessage && '[image]') ||
    (msg.videoMessage && '[video]') ||
    (msg.ptvMessage && '[video]') ||
    (msg.audioMessage && '[audio]') ||
    (msg.documentMessage && '[document]') ||
    (msg.stickerMessage && '[sticker]') ||
    msg.buttonsResponseMessage?.selectedDisplayText ||
    msg.listResponseMessage?.title ||
    '[message]'
  )
}

/** The stanza id a message quotes: contextInfo on whichever node carries it. */
export function quotedId(msg) {
  if (!msg || typeof msg !== 'object') return ''
  for (const node of Object.values(msg)) {
    const id = node && typeof node === 'object' ? node.contextInfo?.stanzaId : null
    if (typeof id === 'string' && id) return id
  }
  return ''
}

export function parseInbound(message) {
  const msg = unwrap(message)
  if (msg?.reactionMessage) {
    const r = msg.reactionMessage
    return { text: '', replyTo: '', reaction: { msg_id: r.key?.id || '', emoji: r.text || '' } }
  }
  return { text: extractText(msg), replyTo: quotedId(msg), reaction: null }
}

// ---- group chats ----

/** What kind of chat a jid is: 'direct' (a person — phone or lid), 'group' (@g.us),
 *  or null for what ChatBranch never files (status updates, broadcast lists, channels). */
export function chatKind(jid) {
  const j = String(jid || '')
  if (j === 'status@broadcast' || j.endsWith('@broadcast') || j.endsWith('@newsletter')) return null
  if (j.endsWith('@g.us')) return 'group'
  if (j.endsWith('@s.whatsapp.net') || j.endsWith('@lid')) return 'direct'
  return null
}

/** Who wrote a group message: their full participant jid (phone or lid), the name
 *  they show, and their phone digits when WhatsApp reveals them ('' otherwise). */
export function groupSender(m) {
  const key = m?.key || {}
  const alt = String(key.participantAlt || key.participantPn || '')
  return {
    id: String(key.participant || ''),
    name: String(m?.pushName || ''),
    phone: alt.endsWith('@s.whatsapp.net') ? alt.split('@')[0].split(':')[0].replace(/\D/g, '') : '',
  }
}

/** A jid without its device suffix: "6012:12@s.whatsapp.net" -> "6012@s.whatsapp.net". */
export function bareJid(jid) {
  const j = String(jid || '')
  const at = j.indexOf('@')
  if (at < 0) return j
  return j.slice(0, at).split(':')[0] + j.slice(at)
}

/** Our own jids (phone + lid) from sock.user, device suffix stripped. */
export function meJids(user) {
  return [user?.id, user?.lid].filter((j) => typeof j === 'string' && j.includes('@')).map(bareJid)
}

/** Was this group message addressed to us — an @mention of our number, or a reply
 *  quoting one of our messages? `me` = meJids(sock.user). */
export function mentionsMe(message, me) {
  const mine = new Set((me || []).map(bareJid))
  if (!mine.size) return false
  const msg = unwrap(message)
  if (!msg || typeof msg !== 'object') return false
  for (const node of Object.values(msg)) {
    const ctx = node && typeof node === 'object' ? node.contextInfo : null
    if (!ctx) continue
    if (Array.isArray(ctx.mentionedJid) && ctx.mentionedJid.some((j) => mine.has(bareJid(j)))) return true
    if (ctx.participant && mine.has(bareJid(ctx.participant))) return true
  }
  return false
}

/** The chat a send must target without a number lookup: the API's raw jid, else a
 *  group jid given as `to`; null = a phone number, resolved the usual way. */
export function targetJid(to, rawJid) {
  if (rawJid) return rawJid
  const t = String(to || '').trim()
  return t.endsWith('@g.us') ? t : null
}

/** How far back a message sent from the phone is still synced (seconds); older = history, skipped. */
export const PHONE_SYNC_WINDOW_S = 15 * 60

/** A WAMessage's timestamp in seconds (a protobuf Long or a number; 0 when absent). */
export function messageSeconds(m) {
  const ts = m?.messageTimestamp
  if (ts == null) return 0
  if (typeof ts === 'object') return Number(typeof ts.toNumber === 'function' ? ts.toNumber() : ts.low ?? 0)
  return Number(ts) || 0
}

/**
 * Was this message sent by a person on ANOTHER device of the account — the phone or
 * WhatsApp Web — so ChatBranch should record it as their reply? A live (`notify`)
 * upsert from our own number (fromMe), never one this gateway sent itself
 * (`isOwn(id)`: ChatBranch's own sends), in a 1:1 chat, not a reaction, and recent
 * (an older one is history replayed on connect). Content is checked by the caller.
 */
export function phoneSent(m, type, isOwn = () => false, nowS = Math.floor(Date.now() / 1000)) {
  if (type !== 'notify' || !m?.key?.fromMe || !m.key.id) return false
  if (isOwn(m.key.id)) return false
  const jid = String(m.key.remoteJid || '')
  if (!jid.endsWith('@s.whatsapp.net') && !jid.endsWith('@lid')) return false
  const ts = messageSeconds(m)
  if (ts > 0 && nowS - ts > PHONE_SYNC_WINDOW_S) return false
  return !parseInbound(m.message).reaction
}

/** proto.Message.ProtocolMessage.Type.REVOKE — WhatsApp's "delete for everyone". */
export const REVOKE = 0

/**
 * "Delete for everyone": `{id, fromMe}` of the message taken back — fromMe = it was
 * recalled from our own number (the phone / WhatsApp Web), else by the customer — or
 * null. Only a live (`notify`) recall in a 1:1 chat counts; an old one is history
 * replayed on connect. (A protocolMessage's type defaults to REVOKE = 0.)
 */
export function recallOf(m, type, nowS = Math.floor(Date.now() / 1000)) {
  if (type !== 'notify') return null
  const p = unwrap(m?.message)?.protocolMessage
  if (!p || !(p.type == null || p.type === REVOKE || p.type === 'REVOKE')) return null
  const id = p.key?.id
  if (!id) return null
  const jid = String(m.key?.remoteJid || '')
  if (!jid.endsWith('@s.whatsapp.net') && !jid.endsWith('@lid')) return null
  const ts = messageSeconds(m)
  if (ts > 0 && nowS - ts > PHONE_SYNC_WINDOW_S) return null
  return { id, fromMe: !!m.key?.fromMe }
}

/** proto.Message.ProtocolMessage.Type.MESSAGE_EDIT — WhatsApp's "edit". */
export const MESSAGE_EDIT = 14

/**
 * An edit WhatsApp is reporting: `{id, fromMe, text}` of the message re-worded —
 * fromMe = it was edited on our own number (the phone / WhatsApp Web), else the
 * customer edited theirs — or null.
 *
 * Same gate as a recall: only a live (`notify`) edit in a 1:1 chat inside the
 * sync window counts, so history replayed on connect never rewrites the chat.
 * The new wording is read with the ordinary `extractText`, so an edited caption
 * reads like any caption.
 */
export function editOf(m, type, nowS = Math.floor(Date.now() / 1000)) {
  if (type !== 'notify') return null
  const p = unwrap(m?.message)?.protocolMessage
  if (!p || !(p.type === MESSAGE_EDIT || p.type === 'MESSAGE_EDIT')) return null
  const id = p.key?.id
  if (!id) return null
  const jid = String(m.key?.remoteJid || '')
  if (!jid.endsWith('@s.whatsapp.net') && !jid.endsWith('@lid')) return null
  const ts = messageSeconds(m)
  if (ts > 0 && nowS - ts > PHONE_SYNC_WINDOW_S) return null
  const text = extractText(unwrap(p.editedMessage))
  // an edit we cannot read the new wording of would blank the bubble
  if (!text || text === '[message]') return null
  return { id, fromMe: p.key?.fromMe ?? !!m.key?.fromMe, text }
}

/** A bounded map of recent WAMessages by id (oldest evicted first). */
export class KeyStore {
  constructor(max = 2000) {
    this.max = max
    this.map = new Map()
  }

  remember(m) {
    const id = m?.key?.id
    if (!id) return
    this.map.delete(id)
    this.map.set(id, m)
    while (this.map.size > this.max) this.map.delete(this.map.keys().next().value)
  }

  get(id) {
    return id ? this.map.get(id) || null : null
  }
}

/**
 * The `quoted` option for sendMessage: the real WAMessage when we still hold it,
 * else a minimal one rebuilt from what the API sent (id, direction, text) — the
 * recipient's app shows the quote either way.
 */
export function buildQuoted({ jid, id, fromMe = false, text = '', participant = '' }, store) {
  if (!id) return null
  const known = store?.get(id)
  if (known) return known
  return { key: messageKey(jid, id, fromMe, participant), message: { conversation: String(text || '').slice(0, 500) } }
}

/** A message key; in a group, someone else's message also names its author
 *  (key.participant) — WhatsApp cannot resolve the quote / reaction without it. */
function messageKey(jid, id, fromMe, participant) {
  const key = { remoteJid: jid, id, fromMe: !!fromMe }
  if (participant && !fromMe) key.participant = String(participant)
  return key
}

/** `{ react }` content — an empty emoji removes our reaction. */
export function buildReact({ jid, id, fromMe = false, emoji = '', participant = '' }) {
  if (!id) throw new Error('msg_id is required')
  return { react: { text: String(emoji || ''), key: messageKey(jid, id, fromMe, participant) } }
}

/** `{ delete }` content — WhatsApp's "delete for everyone" on a message we sent. */
export function buildDelete({ jid, id, fromMe = true }) {
  if (!id) throw new Error('msg_id is required')
  return { delete: { remoteJid: jid, id, fromMe: !!fromMe } }
}

/**
 * `{ edit }` content — WhatsApp's edit of a message we already sent. Only a
 * TEXT message can be edited, and only inside WhatsApp's own window (about 15
 * minutes); past that WhatsApp refuses and the error comes back as a failure.
 */
export function buildEdit({ jid, id, fromMe = true, text }) {
  if (!id) throw new Error('msg_id is required')
  const body = typeof text === 'string' ? text : ''
  if (body.trim() === '') throw new Error('text is required')
  return { text: body, edit: { remoteJid: jid, id, fromMe: !!fromMe } }
}

/** `{ location }` content — a pinned point, optionally named. */
export function buildLocation({ lat, lng, name = '', address = '' }) {
  const la = Number(lat)
  const ln = Number(lng)
  if (!Number.isFinite(la) || la < -90 || la > 90) throw new Error('lat must be between -90 and 90')
  if (!Number.isFinite(ln) || ln < -180 || ln > 180) throw new Error('lng must be between -180 and 180')
  return {
    location: {
      degreesLatitude: la,
      degreesLongitude: ln,
      ...(name ? { name: String(name).slice(0, 200) } : {}),
      ...(address ? { address: String(address).slice(0, 400) } : {}),
    },
  }
}

/** One vCard line, with the characters vCard gives meaning to escaped. */
function vcardValue(text) {
  return String(text ?? '')
    .replace(/\\/g, '\\\\')
    .replace(/\n/g, '\\n')
    .replace(/,/g, '\\,')
    .replace(/;/g, '\\;')
    .slice(0, 300)
}

/**
 * `{ contacts }` content — a contact card. WhatsApp reads the `waid` on each
 * TEL line to make the number tappable, so the digits are carried twice: as
 * `waid` and as the displayed value.
 */
export function buildContact({ name, phones, organization = '' }) {
  const display = String(name ?? '').trim()
  if (display === '') throw new Error('name is required')
  const list = (Array.isArray(phones) ? phones : [phones])
    .map((p) => String(p ?? '').trim())
    .filter(Boolean)
    .slice(0, 10)
  if (list.length === 0) throw new Error('at least one phone is required')

  const lines = ['BEGIN:VCARD', 'VERSION:3.0', `FN:${vcardValue(display)}`]
  if (organization) lines.push(`ORG:${vcardValue(organization)};`)
  list.forEach((phone, i) => {
    const waid = phone.replace(/\D/g, '')
    lines.push(`item${i + 1}.TEL;type=CELL;type=VOICE${waid ? `;waid=${waid}` : ''}:${vcardValue(phone)}`)
  })
  lines.push('END:VCARD')
  const vcard = lines.join('\n')
  return { contacts: { displayName: display, contacts: [{ vcard }] } }
}

/**
 * `{ poll }` content. WhatsApp takes 2–12 options and `selectableCount` 1 (a
 * single choice) or more (multiple). Duplicate options are dropped, because
 * WhatsApp shows them as one and the votes then cannot be told apart.
 */
export function buildPoll(body) {
  const { question, options } = body ?? {}
  // the gateway's own routes are snake_case; the C# client sends camelCase
  const selectableCount = body?.selectable_count ?? body?.selectableCount ?? 1
  const name = String(question ?? '').trim()
  if (name === '') throw new Error('question is required')
  const values = []
  for (const raw of Array.isArray(options) ? options : []) {
    const value = String(raw ?? '').trim().slice(0, 100)
    if (value !== '' && !values.includes(value)) values.push(value)
  }
  if (values.length < 2) throw new Error('a poll needs at least 2 different options')
  if (values.length > 12) throw new Error('a poll takes at most 12 options')
  const pick = Number(selectableCount)
  const selectable = Number.isFinite(pick) && pick >= 1 ? Math.min(Math.trunc(pick), values.length) : 1
  return { poll: { name: name.slice(0, 255), values, selectableCount: selectable } }
}

// ---- offline selftest (node messages.js --selftest; also run by calls.js --selftest) ----

function check(cond, what) {
  if (!cond) throw new Error(`messages selftest failed: ${what}`)
}

export function selftest() {
  // reactions: a reaction is never text, an empty one is a removal
  const r = parseInbound({ reactionMessage: { key: { id: 'ABC', remoteJid: '6012@s.whatsapp.net' }, text: '👍' } })
  check(r.reaction && r.reaction.msg_id === 'ABC' && r.reaction.emoji === '👍' && r.text === '', 'reaction parsed')
  const gone = parseInbound({ reactionMessage: { key: { id: 'ABC' }, text: '' } })
  check(gone.reaction.emoji === '', 'reaction removal')
  // quotes: the stanza id from whichever node carries contextInfo
  const q = parseInbound({ extendedTextMessage: { text: 'yes that one', contextInfo: { stanzaId: 'Q1', quotedMessage: { conversation: 'which?' } } } })
  check(q.text === 'yes that one' && q.replyTo === 'Q1' && !q.reaction, 'quoted text reply')
  const qi = parseInbound({ imageMessage: { caption: 'this', contextInfo: { stanzaId: 'Q2' } } })
  check(qi.replyTo === 'Q2' && qi.text === 'this', 'quoted image reply')
  const eph = parseInbound({ ephemeralMessage: { message: { extendedTextMessage: { text: 'hi', contextInfo: { stanzaId: 'Q3' } } } } })
  check(eph.text === 'hi' && eph.replyTo === 'Q3', 'disappearing-message wrapper unwrapped')
  check(parseInbound({ conversation: 'plain' }).replyTo === '', 'no quote')
  // cards
  check(parseInbound({ locationMessage: { degreesLatitude: 3.1, degreesLongitude: 101.6, name: 'KLCC' } }).text
    === '[location] 3.1,101.6 KLCC', 'location card text')
  const vcard = 'BEGIN:VCARD\nVERSION:3.0\nFN:Ana Lim\nitem1.TEL;waid=60123:+60 12-345 6789\nEND:VCARD'
  check(parseInbound({ contactMessage: { displayName: 'Ana', vcard } }).text === '[contact] Ana · +60 12-345 6789',
    'contact card text')
  check(parseInbound({ stickerMessage: {} }).text === '[sticker]', 'sticker placeholder')
  // new kinds + wrappers: round video notes, animated stickers, edits, album children, status mentions
  check(parseInbound({ ptvMessage: { mimetype: 'video/mp4' } }).text === '[video]', 'video note placeholder')
  check(mediaKind(unwrap({ ptvMessage: { mimetype: 'video/mp4' } }))?.type === 'video', 'video note downloads as video')
  const lottie = unwrap({ lottieStickerMessage: { message: { stickerMessage: { mimetype: 'application/was' } } } })
  check(mediaKind(lottie)?.key === 'stickerMessage' && parseInbound({ lottieStickerMessage: { message: { stickerMessage: {} } } }).text === '[sticker]',
    'animated (lottie) sticker unwrapped')
  for (const w of ['editedMessage', 'associatedChildMessage', 'groupStatusMessage']) {
    const inner = unwrap({ [w]: { message: { imageMessage: { caption: 'x' } } } })
    check(mediaKind(inner)?.type === 'image', `${w} unwrapped`)
  }
  check(mediaKind(unwrap({ ephemeralMessage: { message: { audioMessage: { ptt: true } } } }))?.type === 'audio',
    'voice note in a disappearing chat')
  check(mediaKind({ conversation: 'hi' }) === null, 'text carries no media')
  setNormalizer((m) => m.wrapped?.message || m)
  check(mediaKind(unwrap({ wrapped: { message: { documentMessage: {} } } }))?.type === 'document', 'Baileys normalizer used first')
  setNormalizer(null)
  // the quote + react builders
  const store = new KeyStore(2)
  const real = { key: { id: 'R1', remoteJid: 'j', fromMe: false }, message: { conversation: 'real' } }
  store.remember(real)
  check(buildQuoted({ jid: 'j', id: 'R1' }, store) === real, 'a held message is quoted as itself')
  const rebuilt = buildQuoted({ jid: 'j@s', id: 'OLD', fromMe: true, text: 'earlier' }, store)
  check(rebuilt.key.id === 'OLD' && rebuilt.key.fromMe === true && rebuilt.message.conversation === 'earlier',
    'an unknown id is rebuilt from the API\'s text')
  check(buildQuoted({ jid: 'j', id: '' }, store) === null, 'no id, no quote')
  store.remember({ key: { id: 'R2' } })
  store.remember({ key: { id: 'R3' } })
  check(store.get('R1') === null && store.get('R3'), 'LRU evicts the oldest')
  const react = buildReact({ jid: 'j@s', id: 'M1', fromMe: false, emoji: '❤️' })
  check(react.react.text === '❤️' && react.react.key.id === 'M1' && react.react.key.remoteJid === 'j@s', 'react content')
  check(buildReact({ jid: 'j', id: 'M1' }).react.text === '', 'empty react removes')
  let threw = false
  try { buildReact({ jid: 'j', id: '' }) } catch { threw = true }
  check(threw, 'react needs a message id')
  // sent from the phone: our own number, live, not ours, 1:1, recent, not a reaction
  const now = 1_800_000_000
  const mine = (id, extra = {}) => ({
    key: { id, fromMe: true, remoteJid: '60123@s.whatsapp.net' },
    messageTimestamp: now - 5, message: { conversation: 'from my phone' }, ...extra,
  })
  check(phoneSent(mine('P1'), 'notify', () => false, now), 'a reply typed on the phone is synced')
  check(!phoneSent(mine('P1'), 'append', () => false, now), 'history / own echoes (append) are not')
  check(!phoneSent(mine('P1'), 'notify', (id) => id === 'P1', now), 'a message this gateway sent is not')
  check(!phoneSent({ ...mine('P2'), key: { id: 'P2', fromMe: false, remoteJid: '60123@s.whatsapp.net' } }, 'notify', () => false, now),
    'a customer message is not')
  check(!phoneSent({ ...mine('P3'), key: { id: 'P3', fromMe: true, remoteJid: '1203@g.us' } }, 'notify', () => false, now),
    'a group message is not')
  check(!phoneSent(mine('P4', { messageTimestamp: { low: now - 3600, toNumber: () => now - 3600 } }), 'notify', () => false, now),
    'an hour-old message (history) is not')
  check(phoneSent(mine('P5', { messageTimestamp: { low: now - 60 } }), 'notify', () => false, now), 'a Long timestamp is read')
  check(!phoneSent(mine('P6', { message: { reactionMessage: { key: { id: 'X' }, text: '👍' } } }), 'notify', () => false, now),
    'a reaction from the phone is not a message')
  check(phoneSent({ ...mine('P7'), key: { id: 'P7', fromMe: true, remoteJid: '9988@lid' } }, 'notify', () => false, now),
    'a lid chat counts')
  // "delete for everyone": the customer's, or one made on our own phone
  const revoke = (by, extra = {}) => ({
    key: { id: 'RV1', fromMe: by === 'us', remoteJid: '60123@s.whatsapp.net' }, messageTimestamp: now - 3,
    message: { protocolMessage: { type: 0, key: { id: 'GONE1', remoteJid: '60123@s.whatsapp.net' } } }, ...extra,
  })
  const rc = recallOf(revoke('customer'), 'notify', now)
  check(rc && rc.id === 'GONE1' && rc.fromMe === false, 'a customer recall names the message taken back')
  check(recallOf(revoke('us'), 'notify', now)?.fromMe === true, 'a recall from our phone is ours')
  check(recallOf({ ...revoke('customer'), message: { ephemeralMessage: { message: { protocolMessage: { type: 'REVOKE', key: { id: 'G2' } } } } } },
    'notify', now)?.id === 'G2', 'a recall inside a disappearing-message wrapper')
  check(recallOf(revoke('customer'), 'append', now) === null, 'history is not a recall')
  // a poll reads as a poll, whoever sent it (its votes never come back)
  check(parseInbound({ pollCreationMessage: { name: 'Which slot?', options: [{ optionName: '9am' }, { optionName: '2pm' }] } }).text
    === '[poll] Which slot? \u2014 9am / 2pm', 'poll text')
  check(parseInbound({ pollCreationMessageV3: { name: 'Pick one', options: [] } }).text === '[poll] Pick one',
    'a poll with no options still names its question')
  check(parseInbound({ pollCreationMessage: {} }).text === '[poll]', 'an unreadable poll is still marked as one')
  // an edit: made on our own phone, or by the customer on their own message
  const edited = (fromMe, extra = {}) => ({
    key: { id: 'EV1', fromMe, remoteJid: '60123@s.whatsapp.net' }, messageTimestamp: now - 2,
    message: { protocolMessage: { type: MESSAGE_EDIT, key: { id: 'OLD1', fromMe }, editedMessage: { conversation: 'the new wording' } } },
    ...extra,
  })
  const ev = editOf(edited(true), 'notify', now)
  check(ev && ev.id === 'OLD1' && ev.fromMe === true && ev.text === 'the new wording',
    'an edit on our phone names the message and its new wording')
  check(editOf(edited(false), 'notify', now)?.fromMe === false, "the customer's own edit is theirs")
  check(editOf(edited(true), 'append', now) === null, 'history is not an edit')
  check(editOf(revoke('customer'), 'notify', now) === null, 'a recall is not an edit')
  check(editOf(edited(true, { messageTimestamp: now - 3600 }), 'notify', now) === null, 'an hour-old edit is history')
  check(editOf({ ...edited(true), key: { id: 'EV2', fromMe: true, remoteJid: '1203@g.us' } }, 'notify', now) === null,
    'a group edit is not synced')
  check(editOf({ ...edited(true), message: { protocolMessage: { type: MESSAGE_EDIT, key: { id: 'OLD2' }, editedMessage: {} } } },
    'notify', now) === null, 'an edit whose new wording cannot be read is dropped, not blanked')
  check(editOf({ ...edited(true), message: { ephemeralMessage: { message: { protocolMessage: { type: MESSAGE_EDIT, key: { id: 'OLD3' }, editedMessage: { conversation: 'x' } } } } } },
    'notify', now)?.id === 'OLD3', 'an edit inside a disappearing-message wrapper')
  check(recallOf({ ...revoke('customer'), message: { protocolMessage: { type: 14, key: { id: 'E1' } } } }, 'notify', now) === null,
    'an edit (MESSAGE_EDIT) is not a recall')
  check(recallOf({ ...revoke('customer'), message: { protocolMessage: { type: 5 } } }, 'notify', now) === null,
    'a history-sync notice is not a recall')
  check(recallOf({ ...revoke('customer'), key: { id: 'RV2', remoteJid: '1203@g.us' } }, 'notify', now) === null,
    'a group recall is not ours to file')
  check(recallOf(mine('P8'), 'notify', now) === null, 'an ordinary message is not a recall')
  // group chats: kind, sender, mentions
  check(chatKind('60123@s.whatsapp.net') === 'direct' && chatKind('9988@lid') === 'direct', 'a person is direct')
  check(chatKind('120363000000@g.us') === 'group', 'a @g.us jid is a group')
  for (const j of ['status@broadcast', '12345@broadcast', '1203@newsletter', '', null]) {
    check(chatKind(j) === null, `${j} is never filed`)
  }
  const gm = { key: { remoteJid: '1203@g.us', participant: '9988@lid', participantAlt: '60123:3@s.whatsapp.net' }, pushName: 'Ana' }
  const gs = groupSender(gm)
  check(gs.id === '9988@lid' && gs.name === 'Ana' && gs.phone === '60123', 'group sender with a phone alt')
  check(groupSender({ key: { participant: '9988@lid', participantPn: '60124@s.whatsapp.net' } }).phone === '60124',
    'participantPn is read too')
  const lidOnly = groupSender({ key: { participant: '9988@lid', participantAlt: '7777@lid' } })
  check(lidOnly.phone === '' && lidOnly.name === '', 'a lid-only participant has no phone')
  const me = meJids({ id: '60111:12@s.whatsapp.net', lid: '5555:12@lid' })
  check(me.length === 2 && me[0] === '60111@s.whatsapp.net' && me[1] === '5555@lid', 'our jids lose the device suffix')
  check(meJids({ id: '60111@s.whatsapp.net' }).length === 1 && meJids(null).length === 0, 'missing lid / no user')
  check(mentionsMe({ extendedTextMessage: { text: '@60111 hi', contextInfo: { mentionedJid: ['60111@s.whatsapp.net'] } } }, me),
    'an @mention of our number')
  check(mentionsMe({ extendedTextMessage: { text: '@x', contextInfo: { mentionedJid: ['5555@lid'] } } }, me), 'an @mention of our lid')
  check(mentionsMe({ ephemeralMessage: { message: { imageMessage: { caption: 'x', contextInfo: { mentionedJid: ['60111:12@s.whatsapp.net'] } } } } }, me),
    'a mention inside a wrapper, with a device suffix')
  check(mentionsMe({ extendedTextMessage: { text: 'yes', contextInfo: { stanzaId: 'Q', participant: '5555@lid' } } }, me),
    'a reply quoting our message')
  check(!mentionsMe({ extendedTextMessage: { text: 'hey', contextInfo: { mentionedJid: ['60999@s.whatsapp.net'], participant: '60998@s.whatsapp.net' } } }, me),
    'mentioning / quoting someone else is not us')
  check(!mentionsMe({ conversation: 'plain' }, me) && !mentionsMe({ conversation: 'x' }, []), 'no context, no mention')
  return true
}

/** The outbound builders: what an operator can send beyond text and media. */
export function selftestOutbound() {
  const jid = '60123456789@s.whatsapp.net'

  // delete for everyone + edit address a message by its key
  check(buildDelete({ jid, id: 'M1' }).delete.id === 'M1', 'delete carries the message id')
  check(buildDelete({ jid, id: 'M1' }).delete.fromMe === true, 'we only delete our own')
  let threw = false
  try { buildDelete({ jid, id: '' }) } catch { threw = true }
  check(threw, 'delete without an id is refused')

  const edit = buildEdit({ jid, id: 'M2', text: 'fixed' })
  check(edit.text === 'fixed' && edit.edit.id === 'M2', 'edit carries the new text and the key')
  threw = false
  try { buildEdit({ jid, id: 'M2', text: '   ' }) } catch { threw = true }
  check(threw, 'an edit to blank text is refused')

  // location: the degrees are numbers, and out-of-range is refused
  const loc = buildLocation({ lat: '3.1578', lng: '101.7117', name: 'KLCC' })
  check(loc.location.degreesLatitude === 3.1578 && loc.location.degreesLongitude === 101.7117, 'location degrees')
  check(loc.location.name === 'KLCC' && loc.location.address === undefined, 'an empty address is left out')
  for (const bad of [{ lat: 91, lng: 0 }, { lat: 0, lng: 181 }, { lat: 'x', lng: 0 }]) {
    threw = false
    try { buildLocation(bad) } catch { threw = true }
    check(threw, `location ${JSON.stringify(bad)} is refused`)
  }

  // contact card: a tappable waid plus the number as typed
  const card = buildContact({ name: 'Ana Lim', phones: ['+60 12-345 6789'], organization: 'Acme' })
  check(card.displayName === undefined, 'the card is wrapped in contacts')
  check(card.contacts.displayName === 'Ana Lim', 'display name')
  const vcard = card.contacts.contacts[0].vcard
  check(vcard.includes('FN:Ana Lim') && vcard.includes('waid=60123456789'), 'vcard has the name and waid')
  check(vcard.includes('ORG:Acme;'), 'organization line')
  // a name with vCard's own punctuation cannot break the document
  const risky = buildContact({ name: 'Lim; Ana, Sdn\nBhd', phones: ['+60123'] }).contacts.contacts[0].vcard
  check(risky.includes('FN:Lim\\; Ana\\, Sdn\\nBhd'), 'vcard values are escaped')
  check(risky.split('\n').filter((l) => l.startsWith('FN:')).length === 1, 'a newline cannot add a vcard line')
  threw = false
  try { buildContact({ name: 'No Number', phones: [] }) } catch { threw = true }
  check(threw, 'a contact card needs a phone')

  // poll: 2–12 distinct options, selectableCount bounded by them
  const poll = buildPoll({ question: 'Which slot?', options: ['9am', '2pm', '9am', ''] })
  check(poll.poll.values.length === 2 && poll.poll.selectableCount === 1, 'duplicate and empty options dropped')
  check(buildPoll({ question: 'q', options: ['a', 'b', 'c'], selectableCount: 9 }).poll.selectableCount === 3,
    'selectableCount never exceeds the options')
  // the route's own snake_case and the C# client's camelCase mean the same thing
  check(buildPoll({ question: 'q', options: ['a', 'b', 'c'], selectable_count: 2 }).poll.selectableCount === 2,
    'selectable_count is read too')
  for (const bad of [{ question: '', options: ['a', 'b'] }, { question: 'q', options: ['only'] },
    { question: 'q', options: Array.from({ length: 13 }, (_, i) => `o${i}`) }]) {
    threw = false
    try { buildPoll(bad) } catch { threw = true }
    check(threw, `poll ${JSON.stringify(bad).slice(0, 40)} is refused`)
  }
  // groups: a jid as `to`, and the author of someone else's message on a quote / reaction
  check(targetJid('1203@g.us', null) === '1203@g.us', 'a group jid is its own target')
  check(targetJid('+60123', null) === null, 'a phone number is looked up')
  check(targetJid('1203@g.us', '9988@lid') === '9988@lid', 'the raw jid wins')
  const gq = buildQuoted({ jid: '1203@g.us', id: 'GQ', fromMe: false, text: 't', participant: '9988@lid' }, null)
  check(gq.key.participant === '9988@lid' && gq.key.remoteJid === '1203@g.us', 'a group quote names its author')
  check(!('participant' in buildQuoted({ jid: '1203@g.us', id: 'GQ2', fromMe: true, participant: '9988@lid' }, null).key),
    'our own message carries no participant')
  check(!('participant' in buildQuoted({ jid, id: 'Q', text: 't' }, null).key), '1:1 quotes unchanged')
  const gr = buildReact({ jid: '1203@g.us', id: 'GM', emoji: '👍', participant: '9988@lid' })
  check(gr.react.key.participant === '9988@lid', 'a group reaction names the author')
  check(!('participant' in buildReact({ jid: '1203@g.us', id: 'GM', fromMe: true, participant: '9988@lid' }).react.key),
    'reacting to our own group message carries no participant')
  return true
}

/** postWithRetry against a scripted fake fetch (async — calls.js awaits it). */
export async function selftestRetry() {
  const reply = (status) => ({ ok: status >= 200 && status < 300, status, text: async () => `s${status}` })
  const noSleep = async () => {}
  let calls = 0
  let r = await postWithRetry(async () => reply(++calls < 3 ? 404 : 200), { sleep: noSleep })
  check(r.ok && r.attempts === 3, 'retried until the row exists')
  calls = 0
  r = await postWithRetry(async () => { calls++; return reply(413) }, { sleep: noSleep })
  check(!r.ok && r.status === 413 && calls === 1 && r.error === 's413', 'a 413 is final, not retried')
  calls = 0
  r = await postWithRetry(async () => { calls++; return reply(500) }, { sleep: noSleep })
  check(!r.ok && r.status === 500 && calls === 3, 'a 500 is retried up to 3 times')
  calls = 0
  r = await postWithRetry(async () => { calls++; throw new Error('ECONNREFUSED') }, { sleep: noSleep })
  check(!r.ok && r.status === 0 && calls === 3 && r.error.includes('ECONNREFUSED'), 'network errors retried')
  return true
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href
    && process.argv.includes('--selftest')) {
  selftest()
  selftestOutbound()
  await selftestRetry()
  console.log(`messages selftest OK (${path.basename(fileURLToPath(import.meta.url))})`)
}
