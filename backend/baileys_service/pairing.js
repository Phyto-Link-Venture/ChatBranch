/**
 * Pairing a WhatsApp number — the pure half of "Link with phone number"
 * (index.js does the socket work).
 *
 *   pairingPhone(raw)           -> digits of an E.164 number without the "+",
 *                                  or "" when it cannot be one
 *   normalizePairingCode(code)  -> WhatsApp's 8-character code, upper-cased and
 *                                  stripped of the spacing different clients add
 *
 * WhatsApp offers two ways to link a companion: scanning a QR, or typing an
 * 8-character code the server hands out for ONE number (Baileys'
 * `sock.requestPairingCode`). A person holding the phone cannot always point its
 * camera at the dashboard — a code they read out is the way in on a phone-only
 * setup, which is why both live side by side.
 *
 * Synchronous and dependency-free, so `node pairing.js --selftest` (also run by
 * `node calls.js --selftest`) checks it offline.
 */
import path from 'path'
import { fileURLToPath, pathToFileURL } from 'url'

/** Shortest / longest a real MSISDN is (E.164 allows 15 digits; 7 is the short end). */
export const PHONE_MIN_DIGITS = 7
export const PHONE_MAX_DIGITS = 15

/**
 * The number to ask WhatsApp about, as Baileys wants it: digits only, country
 * code included, no "+", no spaces or dashes. "" when what came in cannot be a
 * phone number — the caller then refuses rather than asking WhatsApp about
 * nonsense (which costs a pairing attempt and can rate-limit the number).
 * A leading "00" international prefix is dropped; a leading "+" is already gone
 * with the non-digits.
 */
export function pairingPhone(raw) {
  let d = String(raw ?? '').replace(/\D/g, '')
  if (d.startsWith('00')) d = d.slice(2)
  if (d.length < PHONE_MIN_DIGITS || d.length > PHONE_MAX_DIGITS) return ''
  if (d.startsWith('0')) return ''   // a national trunk prefix, not a country code
  return d
}

/**
 * WhatsApp's pairing code as the dashboard should show it: 8 characters, upper
 * case, no separator (the UI groups it). Baileys returns it plain, but some
 * builds hand back "ABCD-EFGH" — accept either, and pass anything unexpected
 * through untouched rather than mangling a code the person has to type.
 */
export function normalizePairingCode(code) {
  const s = String(code ?? '').trim().toUpperCase()
  const bare = s.replace(/[^A-Z0-9]/g, '')
  return bare.length === 8 ? bare : s
}

// ---- offline selftest (node pairing.js --selftest; also run by calls.js --selftest) ----

function check(cond, what) {
  if (!cond) throw new Error(`pairing selftest failed: ${what}`)
}

export function selftest() {
  check(pairingPhone('+60 12-345 6789') === '60123456789', 'a typed Malaysian number is cleaned')
  check(pairingPhone('0060123456789') === '60123456789', 'a 00 international prefix is dropped')
  check(pairingPhone('60123456789') === '60123456789', 'already-clean digits pass through')
  check(pairingPhone('0123456789') === '', 'a national 0-prefixed number is refused (no country code)')
  check(pairingPhone('12345') === '', 'too short is refused')
  check(pairingPhone('1234567890123456') === '', 'too long is refused')
  check(pairingPhone('') === '' && pairingPhone(null) === '' && pairingPhone('abc') === '',
    'empty / nonsense is refused')

  check(normalizePairingCode('abcd1234') === 'ABCD1234', 'a code is upper-cased')
  check(normalizePairingCode('ABCD-1234') === 'ABCD1234', 'a separator is dropped')
  check(normalizePairingCode(' abcd 1234 ') === 'ABCD1234', 'spacing is dropped')
  check(normalizePairingCode('WEIRD') === 'WEIRD', 'an unexpected length is passed through')
  check(normalizePairingCode(null) === '', 'nothing stays nothing')
  return true
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href
    && process.argv.includes('--selftest')) {
  selftest()
  console.log(`pairing selftest OK (${path.basename(fileURLToPath(import.meta.url))})`)
}
