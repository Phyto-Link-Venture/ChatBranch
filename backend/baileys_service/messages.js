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
 *
 * Synchronous and dependency-free, so `node messages.js --selftest` (also run by
 * `node calls.js --selftest`) checks it offline.
 */
import path from 'path'
import { fileURLToPath, pathToFileURL } from 'url'

/** Wrappers WhatsApp puts around the real content (disappearing / view-once / edits). */
const WRAPPERS = ['ephemeralMessage', 'viewOnceMessage', 'viewOnceMessageV2', 'viewOnceMessageV2Extension',
  'documentWithCaptionMessage']

export function unwrap(msg) {
  let m = msg
  for (let i = 0; i < 4 && m && typeof m === 'object'; i++) {
    const key = WRAPPERS.find((k) => m[k]?.message)
    if (!key) break
    m = m[key].message
  }
  return m || null
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
  return true
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href
    && process.argv.includes('--selftest')) {
  selftest()
  console.log(`messages selftest OK (${path.basename(fileURLToPath(import.meta.url))})`)
}
