/**
 * Reading back the chats a number ALREADY had — the pure half of the history
 * import (index.js does the socket work and the pushing).
 *
 *   historyWindowDays(settings) -> 0 (off) | 30 | 90 | ALL, the inbox's setting
 *   wantsFullSync(days)         -> should the socket ask WhatsApp for everything?
 *   historyRow(m, opts)         -> one row for POST /api/whatsapp/baileys/history,
 *                                  or null when the message is not importable
 *   historyRows(list, opts)     -> the importable rows of a replay batch
 *   chunk(rows, size)           -> batches to push (the API caps one push)
 *
 * WhatsApp replays a linked device's recent history on `messaging-history.set`.
 * Only 1:1 chats are imported, only text and the placeholders parseInbound
 * already produces ("[image]", "[location] …") — historical MEDIA is not
 * downloaded: the files are often long gone from WhatsApp's servers, and the
 * inbox already renders a placeholder whose file never came. Reactions, protocol
 * envelopes, groups, broadcasts and status updates are skipped, as are messages
 * outside the window the inbox asked for.
 *
 * Synchronous and dependency-free (it takes the text parser as an option), so
 * `node history.js --selftest` (also run by `node calls.js --selftest`) checks
 * it offline.
 */
import path from 'path'
import { fileURLToPath, pathToFileURL } from 'url'

/** "Everything WhatsApp sends" — mirrors BaileysSettings.HistoryAll. */
export const HISTORY_ALL = -1

/** What the inbox may choose — mirrors BaileysSettings.HistoryChoices. */
export const HISTORY_CHOICES = [0, 30, 90, HISTORY_ALL]

/** The default when a number carries no setting: the last month. */
export const HISTORY_DEFAULT_DAYS = 30

/**
 * How many days of history this number imports, from the settings file the API
 * hands over before pairing. An absent setting means the default; anything that
 * is not one of the offered windows is treated as the default too, never as
 * "everything".
 */
export function historyWindowDays(settings) {
  const raw = settings?.import_history_days
  if (raw === undefined || raw === null || raw === '') return HISTORY_DEFAULT_DAYS
  const n = Number.parseInt(String(raw), 10)
  return HISTORY_CHOICES.includes(n) ? n : HISTORY_DEFAULT_DAYS
}

/**
 * Only "everything" asks WhatsApp for a FULL history sync — that can be tens of
 * thousands of messages on a busy number. A day window rides on the recent
 * history WhatsApp sends a new companion anyway, which is why a 90-day window
 * can still come back with less: WhatsApp serves what the phone still holds.
 */
export const wantsFullSync = (days) => days === HISTORY_ALL

/** jids that are not a one-to-one customer chat. */
const isDirect = (jid) => jid.endsWith('@s.whatsapp.net') || jid.endsWith('@lid')

/**
 * One importable row, or null. `opts`:
 *   parse(message)  -> { text, replyTo, reaction } (messages.js parseInbound)
 *   unwrap(message) -> the real content under WhatsApp's wrappers
 *   phoneForLid(jid)-> the phone an earlier message taught us for a @lid chat
 *   nowS, days      -> the window; days 0 imports nothing, HISTORY_ALL everything
 */
export function historyRow(m, opts = {}) {
  const { parse, unwrap = (x) => x, phoneForLid = () => '', nowS = Math.floor(Date.now() / 1000),
          days = HISTORY_DEFAULT_DAYS } = opts
  if (days === 0) return null
  const id = m?.key?.id
  const jid = String(m?.key?.remoteJid || '')
  if (!id || !isDirect(jid)) return null

  const ts = messageSeconds(m)
  if (!ts) return null
  if (days !== HISTORY_ALL && nowS - ts > days * 86400) return null

  const content = unwrap(m.message)
  if (!content) return null
  const parsed = parse ? parse(m.message) : { text: '', replyTo: '', reaction: null }
  // a reaction is not a bubble (and we never import one), and an envelope with no
  // content at all would import as an empty message
  if (parsed.reaction || !parsed.text) return null

  let waId = ''
  if (jid.endsWith('@s.whatsapp.net')) {
    waId = digits(jid.split('@')[0])
  } else {
    // a lid chat: the phone from the envelope (never senderPn on our own message —
    // that is OUR number), else a mapping an earlier message taught us
    const alt = String((m.key.fromMe ? '' : m.key.senderPn || m.key.participantPn || '')
      || m.key.remoteJidAlt || '')
    waId = alt.includes('@s.whatsapp.net') ? digits(alt.split('@')[0]) : digits(phoneForLid(jid) || '')
  }
  if (!waId) return null

  return {
    wa_id: waId,
    name: typeof m.pushName === 'string' ? m.pushName : '',
    msg_id: id,
    from_me: !!m.key.fromMe,
    ts,
    text: parsed.text,
    reply_to: parsed.replyTo || '',
  }
}

/** The importable rows of a replay batch, oldest first (so the inbox reads in order). */
export function historyRows(messages, opts = {}) {
  const rows = []
  for (const m of messages || []) {
    const row = historyRow(m, opts)
    if (row) rows.push(row)
  }
  rows.sort((a, b) => a.ts - b.ts)
  return rows
}

/** Split rows into pushes of at most `size` (the API caps one push at 500). */
export function chunk(rows, size = 500) {
  const out = []
  for (let i = 0; i < rows.length; i += size) out.push(rows.slice(i, i + size))
  return out
}

const digits = (s) => String(s ?? '').replace(/\D/g, '')

/** A WAMessage's timestamp in seconds (a protobuf Long or a number; 0 when absent). */
function messageSeconds(m) {
  const ts = m?.messageTimestamp
  if (ts == null) return 0
  if (typeof ts === 'object') return Number(typeof ts.toNumber === 'function' ? ts.toNumber() : ts.low ?? 0)
  return Number(ts) || 0
}

// ---- offline selftest (node history.js --selftest; also run by calls.js --selftest) ----

function check(cond, what) {
  if (!cond) throw new Error(`history selftest failed: ${what}`)
}

export function selftest() {
  const now = 1_700_000_000
  const parse = (msg) => ({
    text: msg?.conversation || (msg?.imageMessage ? '[image]' : ''),
    replyTo: '',
    reaction: msg?.reactionMessage ? { msg_id: 'x', emoji: '👍' } : null,
  })
  const base = (over = {}) => ({
    key: { id: 'M1', remoteJid: '60123456789@s.whatsapp.net', fromMe: false, ...(over.key || {}) },
    message: over.message === undefined ? { conversation: 'hello' } : over.message,
    messageTimestamp: over.messageTimestamp === undefined ? now - 3600 : over.messageTimestamp,
    pushName: over.pushName,
  })
  const opts = { parse, nowS: now, days: 30 }

  const row = historyRow(base({ pushName: 'Ali' }), opts)
  check(row && row.wa_id === '60123456789' && row.text === 'hello' && row.from_me === false,
    'a customer message imports')
  check(row.name === 'Ali' && row.ts === now - 3600, 'the name and WhatsApp\'s own time ride along')

  check(historyRow(base({ key: { fromMe: true } }), opts).from_me === true,
    'a message the number sent imports as ours')
  check(historyRow(base({ message: { imageMessage: {} } }), opts).text === '[image]',
    'media imports as its placeholder (the file is not fetched)')

  check(historyRow(base({ key: { remoteJid: '123-456@g.us' } }), opts) === null, 'a group is skipped')
  check(historyRow(base({ key: { remoteJid: 'status@broadcast' } }), opts) === null, 'status is skipped')
  check(historyRow(base({ key: { id: '' } }), opts) === null, 'no id = nothing to dedupe on')
  check(historyRow(base({ message: { reactionMessage: {} } }), opts) === null, 'a reaction is not a bubble')
  check(historyRow(base({ message: {} }), opts) === null, 'an empty envelope is skipped')
  check(historyRow(base({ messageTimestamp: 0 }), opts) === null, 'no timestamp = out of any window')
  check(historyRow(base({ messageTimestamp: now - 40 * 86400 }), opts) === null,
    'older than the window is skipped')
  check(historyRow(base({ messageTimestamp: now - 40 * 86400 }), { ...opts, days: 90 }) !== null,
    'a wider window takes it')
  check(historyRow(base({ messageTimestamp: now - 4000 * 86400 }), { ...opts, days: HISTORY_ALL }) !== null,
    '"everything" has no cutoff')
  check(historyRow(base(), { ...opts, days: 0 }) === null, 'off imports nothing')

  // a lid chat only imports once something has taught us the phone behind it
  const lid = base({ key: { remoteJid: '55555@lid', senderPn: '60999888777@s.whatsapp.net' } })
  check(historyRow(lid, opts).wa_id === '60999888777', 'a lid chat uses the phone in the envelope')
  check(historyRow(base({ key: { remoteJid: '55555@lid' } }), opts) === null,
    'an unmappable lid chat is skipped, not guessed')
  check(historyRow(base({ key: { remoteJid: '55555@lid' } }),
    { ...opts, phoneForLid: () => '60111222333@s.whatsapp.net' }).wa_id === '60111222333',
    'a known lid mapping is used')

  // protobuf Long timestamps
  check(historyRow(base({ messageTimestamp: { toNumber: () => now - 10 } }), opts).ts === now - 10,
    'a Long timestamp is read')

  const rows = historyRows([
    base({ key: { id: 'B' }, messageTimestamp: now - 100 }),
    base({ key: { id: 'A' }, messageTimestamp: now - 200 }),
    base({ key: { remoteJid: 'x@g.us' } }),
  ], opts)
  check(rows.length === 2 && rows[0].msg_id === 'A', 'a batch keeps only 1:1 chats, oldest first')

  check(historyWindowDays({}) === 30 && historyWindowDays({ import_history_days: '90' }) === 90,
    'the window comes from the settings file')
  check(historyWindowDays({ import_history_days: 0 }) === 0, 'off is respected')
  check(historyWindowDays({ import_history_days: -1 }) === HISTORY_ALL, 'everything is respected')
  check(historyWindowDays({ import_history_days: 7 }) === 30, 'an unknown window falls back to the default')
  check(!wantsFullSync(30) && !wantsFullSync(0) && wantsFullSync(HISTORY_ALL),
    'only "everything" asks WhatsApp for a full sync')

  check(chunk([1, 2, 3, 4, 5], 2).length === 3, 'rows are pushed in batches')
  check(chunk([], 2).length === 0, 'nothing to push, nothing to batch')
  return true
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href
    && process.argv.includes('--selftest')) {
  selftest()
  console.log(`history selftest OK (${path.basename(fileURLToPath(import.meta.url))})`)
}
