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
export function buildQuoted({ jid, id, fromMe = false, text = '' }, store) {
  if (!id) return null
  const known = store?.get(id)
  if (known) return known
  return { key: { remoteJid: jid, id, fromMe: !!fromMe }, message: { conversation: String(text || '').slice(0, 500) } }
}

/** `{ react }` content — an empty emoji removes our reaction. */
export function buildReact({ jid, id, fromMe = false, emoji = '' }) {
  if (!id) throw new Error('msg_id is required')
  return { react: { text: String(emoji || ''), key: { remoteJid: jid, id, fromMe: !!fromMe } } }
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
export function buildPoll({ question, options, selectableCount = 1 }) {
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
  check(recallOf({ ...revoke('customer'), message: { protocolMessage: { type: 14, key: { id: 'E1' } } } }, 'notify', now) === null,
    'an edit (MESSAGE_EDIT) is not a recall')
  check(recallOf({ ...revoke('customer'), message: { protocolMessage: { type: 5 } } }, 'notify', now) === null,
    'a history-sync notice is not a recall')
  check(recallOf({ ...revoke('customer'), key: { id: 'RV2', remoteJid: '1203@g.us' } }, 'notify', now) === null,
    'a group recall is not ours to file')
  check(recallOf(mine('P8'), 'notify', now) === null, 'an ordinary message is not a recall')
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
  for (const bad of [{ question: '', options: ['a', 'b'] }, { question: 'q', options: ['only'] },
    { question: 'q', options: Array.from({ length: 13 }, (_, i) => `o${i}`) }]) {
    threw = false
    try { buildPoll(bad) } catch { threw = true }
    check(threw, `poll ${JSON.stringify(bad).slice(0, 40)} is refused`)
  }
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
