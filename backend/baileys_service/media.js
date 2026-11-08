/**
 * Outbound media for the Baileys gateway — the pure half of
 * POST /accounts/:id/send-media (index.js does the socket work).
 *
 * The API sends the file INSIDE the request as base64 — never a path or a URL:
 * the gateway must not become a way to read files off this machine or fetch
 * arbitrary URLs on someone's behalf. Everything here is synchronous and
 * dependency-free so `node media.js --selftest` (and calls.js --selftest) can
 * check it offline.
 *
 *   body: { to, media_type: image|video|audio|document|sticker, file_b64, mime?,
 *           filename?, caption?, ptt? }   (ptt: an audio/ogg file as a VOICE NOTE)
 *   →     { content } ready for sock.sendMessage(jid, content)
 *
 * A STICKER is its own media_type rather than an image, because WhatsApp will only
 * show one for a 512x512 WebP under its own size ceiling and carries no caption for
 * it. The API shapes the picture (ChatBranch.Data/Channels/StickerImage.cs) before
 * it gets here; this end re-checks the format and the cap, because the gateway is a
 * separate service and must not depend on its caller being right.
 */
import path from 'path'
import { fileURLToPath, pathToFileURL } from 'url'

/** WhatsApp's own ceiling for images/video/audio sent from a phone. */
export const MAX_SEND_MEDIA_BYTES = 16 * 1024 * 1024
/** The JSON body that can carry that much base64 (4/3) plus the small envelope. */
export const MAX_SEND_MEDIA_BODY = Math.ceil(MAX_SEND_MEDIA_BYTES / 3) * 4 + 64 * 1024
export const MEDIA_TYPES = ['image', 'video', 'audio', 'document', 'sticker']
/** WhatsApp's own ceiling for an animated sticker (a still one is far smaller). */
export const MAX_STICKER_BYTES = 500 * 1024

const DEFAULT_MIME = {
  image: 'image/jpeg',
  video: 'video/mp4',
  audio: 'audio/ogg',
  document: 'application/octet-stream',
  sticker: 'image/webp',
}
const DEFAULT_EXT = { image: 'jpg', video: 'mp4', audio: 'ogg', document: 'bin', sticker: 'webp' }
const MIME_RE = /^[a-z0-9][a-z0-9.+-]{0,62}\/[a-z0-9][a-z0-9.+-]{0,62}$/i
const B64_RE = /^[A-Za-z0-9+/]*={0,2}$/
const MAX_CAPTION = 1024

/** A request the caller got wrong — index.js answers it with this status. */
export class MediaRequestError extends Error {
  constructor(status, message) {
    super(message)
    this.status = status
  }
}

/** RIFF....WEBP — a sticker's real format, checked by magic rather than by its name. */
export function isWebp(buffer) {
  return buffer.length >= 12
    && buffer.toString('latin1', 0, 4) === 'RIFF'
    && buffer.toString('latin1', 8, 12) === 'WEBP'
}

/** A file name safe to show the recipient: no directories, no odd characters. */
export function safeFileName(raw, mediaType) {
  const base = String(raw || '').replace(/\\/g, '/').split('/').pop() || ''
  const cleaned = base.replace(/[^A-Za-z0-9._ -]/g, '_').replace(/^[.\s_]+/, '').trim().slice(-120)
  return cleaned || `file.${DEFAULT_EXT[mediaType] || 'bin'}`
}

/**
 * Validate the request body and build the Baileys message content. Throws a
 * MediaRequestError (400 / 413) for anything it will not send.
 */
export function buildMediaMessage(body) {
  if (!body || typeof body !== 'object' || Array.isArray(body)) {
    throw new MediaRequestError(400, 'a JSON object body is required')
  }
  for (const key of ['path', 'file_path', 'url', 'file_url']) {
    if (key in body) {
      throw new MediaRequestError(400, 'server paths and URLs are not accepted — send the file as file_b64')
    }
  }
  const mediaType = String(body.media_type || '').toLowerCase()
  if (!MEDIA_TYPES.includes(mediaType)) {
    throw new MediaRequestError(400, `media_type must be one of ${MEDIA_TYPES.join(', ')}`)
  }
  if (typeof body.file_b64 !== 'string' || body.file_b64.length === 0) {
    throw new MediaRequestError(400, 'file_b64 (the file, base64-encoded) is required')
  }
  const b64 = body.file_b64.replace(/\s+/g, '')
  if (b64.length > Math.ceil(MAX_SEND_MEDIA_BYTES / 3) * 4) {
    throw new MediaRequestError(413, 'file too large (max 16 MB)')
  }
  if (b64.length % 4 !== 0 || !B64_RE.test(b64)) {
    throw new MediaRequestError(400, 'file_b64 is not valid base64')
  }
  const buffer = Buffer.from(b64, 'base64')
  if (buffer.length === 0) throw new MediaRequestError(400, 'the file is empty')
  if (buffer.length > MAX_SEND_MEDIA_BYTES) throw new MediaRequestError(413, 'file too large (max 16 MB)')

  let mimetype = String(body.mime || '').split(';')[0].trim().toLowerCase()
  if (!mimetype) mimetype = DEFAULT_MIME[mediaType]
  if (!MIME_RE.test(mimetype)) throw new MediaRequestError(400, 'mime is not a valid media type')
  if (mediaType === 'sticker') {
    // a sticker travels as image/webp — there is no "sticker/" media type
    if (mimetype !== 'image/webp') throw new MediaRequestError(400, 'a sticker must be image/webp')
    if (buffer.length > MAX_STICKER_BYTES) {
      throw new MediaRequestError(413, `sticker too large (max ${MAX_STICKER_BYTES / 1024} KB)`)
    }
    if (!isWebp(buffer)) throw new MediaRequestError(400, 'a sticker must be a WebP image')
  } else if (mediaType !== 'document' && !mimetype.startsWith(`${mediaType}/`)) {
    throw new MediaRequestError(400, `mime ${mimetype} does not match media_type ${mediaType}`)
  }

  const caption = typeof body.caption === 'string' ? body.caption.trim().slice(0, MAX_CAPTION) : ''
  const fileName = safeFileName(body.filename, mediaType)

  let content
  // a sticker has no caption and no file name: WhatsApp shows neither
  if (mediaType === 'sticker') return { content: { sticker: buffer, mimetype }, mediaType, bytes: buffer.length }
  if (mediaType === 'image') content = { image: buffer, mimetype }
  else if (mediaType === 'video') content = { video: buffer, mimetype }
  else if (mediaType === 'audio') {
    // no captions on audio. ptt=true makes it a voice note — WhatsApp only shows one for
    // Ogg/Opus, so the mimetype carries the codec Baileys must announce
    const ptt = body.ptt === true
    if (ptt && mimetype !== 'audio/ogg') throw new MediaRequestError(400, 'a voice note (ptt) must be audio/ogg (Opus)')
    content = { audio: buffer, mimetype: ptt ? 'audio/ogg; codecs=opus' : mimetype, ptt }
  }
  else content = { document: buffer, mimetype, fileName }
  if (caption && mediaType !== 'audio') content.caption = caption
  return { content, mediaType, bytes: buffer.length }
}

/**
 * Read a JSON body, refusing more than maxBytes BEFORE buffering it all —
 * the plain readBody in index.js has no cap, and this route carries files.
 * Resolves { body } or { tooLarge: true }; a malformed body is {}.
 */
export function readJsonBodyCapped(req, maxBytes) {
  return new Promise((resolve) => {
    const declared = Number(req.headers?.['content-length'] || 0)
    if (declared > maxBytes) {
      req.resume?.()
      resolve({ tooLarge: true })
      return
    }
    const chunks = []
    let size = 0
    let done = false
    req.on('data', (c) => {
      if (done) return
      size += c.length
      if (size > maxBytes) {
        done = true
        chunks.length = 0
        resolve({ tooLarge: true })
        return
      }
      chunks.push(c)
    })
    req.on('end', () => {
      if (done) return
      done = true
      try { resolve({ body: JSON.parse(Buffer.concat(chunks).toString('utf8') || '{}') }) } catch { resolve({ body: {} }) }
    })
    req.on('error', () => {
      if (!done) { done = true; resolve({ body: {} }) }
    })
  })
}

// ---- offline selftest (node media.js --selftest; also run by calls.js --selftest) ----

function expectError(fn, status, fragment) {
  try {
    fn()
  } catch (e) {
    if (!(e instanceof MediaRequestError)) throw new Error(`expected MediaRequestError, got ${e}`)
    if (e.status !== status) throw new Error(`expected status ${status}, got ${e.status} (${e.message})`)
    if (fragment && !e.message.includes(fragment)) throw new Error(`expected "${fragment}" in "${e.message}"`)
    return
  }
  throw new Error(`expected a ${status} refusal`)
}

function check(cond, what) {
  if (!cond) throw new Error(`media selftest failed: ${what}`)
}

export function selftest() {
  const png = Buffer.from([0x89, 0x50, 0x4e, 0x47, 1, 2, 3])
  const b64 = png.toString('base64')

  const img = buildMediaMessage({ to: '60123', media_type: 'image', file_b64: b64, mime: 'image/png', caption: ' hi ' })
  check(Buffer.isBuffer(img.content.image) && img.content.image.equals(png), 'image buffer round-trips')
  check(img.content.mimetype === 'image/png' && img.content.caption === 'hi', 'image mimetype + trimmed caption')
  check(img.bytes === png.length, 'byte count')

  const doc = buildMediaMessage({ media_type: 'document', file_b64: b64, mime: 'application/pdf',
                                  filename: '../../etc/pass wd.pdf', caption: 'invoice' })
  check(doc.content.document && doc.content.fileName === 'pass wd.pdf', 'document keeps a base name only')
  check(doc.content.caption === 'invoice', 'document caption')

  const audio = buildMediaMessage({ media_type: 'audio', file_b64: b64, caption: 'dropped' })
  check(audio.content.audio && audio.content.mimetype === 'audio/ogg' && !('caption' in audio.content),
    'audio defaults its mime and carries no caption')
  check(audio.content.ptt === false, 'audio is a file, not a voice note')

  const voice = buildMediaMessage({ media_type: 'audio', file_b64: b64, mime: 'audio/ogg', ptt: true })
  check(voice.content.ptt === true && voice.content.mimetype === 'audio/ogg; codecs=opus',
    'ptt audio is a voice note announced as ogg/opus')
  expectError(() => buildMediaMessage({ media_type: 'audio', file_b64: b64, mime: 'audio/mpeg', ptt: true }), 400, 'ogg')
  check(buildMediaMessage({ media_type: 'audio', file_b64: b64, ptt: 'yes' }).content.ptt === false,
    'only a literal true makes a voice note')

  const video = buildMediaMessage({ media_type: 'VIDEO', file_b64: b64, mime: 'video/mp4; codecs=avc1' })
  check(video.content.video && video.content.mimetype === 'video/mp4', 'video mime parameters stripped')
  check(safeFileName('', 'image') === 'file.jpg' && safeFileName('..\\a\\b.txt', 'document') === 'b.txt',
    'file name fallback + windows separators')

  // ---- stickers: a 512x512 WebP, no caption, its own ceiling ----
  const webp = Buffer.concat([Buffer.from('RIFF'), Buffer.alloc(4), Buffer.from('WEBP'), Buffer.alloc(32)])
  const sticker = buildMediaMessage({ media_type: 'sticker', file_b64: webp.toString('base64'),
                                      mime: 'image/webp', caption: 'ignored', filename: 'x.webp' })
  check(Buffer.isBuffer(sticker.content.sticker) && sticker.content.sticker.equals(webp), 'sticker buffer round-trips')
  check(sticker.content.mimetype === 'image/webp', 'sticker announced as image/webp')
  check(!('caption' in sticker.content) && !('fileName' in sticker.content),
    'a sticker carries neither a caption nor a file name')
  check(isWebp(webp) && !isWebp(png), 'webp recognised by its RIFF magic, not its name')
  // the API shapes the picture, but this end still refuses what WhatsApp would drop
  expectError(() => buildMediaMessage({ media_type: 'sticker', file_b64: webp.toString('base64'),
                                        mime: 'image/png' }), 400, 'image/webp')
  expectError(() => buildMediaMessage({ media_type: 'sticker', file_b64: b64, mime: 'image/webp' }), 400, 'WebP image')
  const fatSticker = Buffer.concat([Buffer.from('RIFF'), Buffer.alloc(4), Buffer.from('WEBP'),
                                    Buffer.alloc(MAX_STICKER_BYTES + 1)])
  expectError(() => buildMediaMessage({ media_type: 'sticker', file_b64: fatSticker.toString('base64'),
                                        mime: 'image/webp' }), 413, 'sticker too large')

  expectError(() => buildMediaMessage({ media_type: 'image', path: '/etc/passwd' }), 400, 'not accepted')
  expectError(() => buildMediaMessage({ media_type: 'image', url: 'http://x/y.jpg' }), 400, 'not accepted')
  expectError(() => buildMediaMessage({ media_type: 'gif', file_b64: b64 }), 400, 'media_type')
  expectError(() => buildMediaMessage({ media_type: 'image' }), 400, 'file_b64')
  expectError(() => buildMediaMessage({ media_type: 'image', file_b64: 'not base64!' }), 400, 'base64')
  expectError(() => buildMediaMessage({ media_type: 'image', file_b64: b64, mime: 'application/pdf' }), 400, 'match')
  expectError(() => buildMediaMessage({ media_type: 'image', file_b64: b64, mime: 'text/<script>' }), 400, 'valid')
  expectError(() => buildMediaMessage([]), 400, 'object')
  const tooBig = 'A'.repeat(Math.ceil(MAX_SEND_MEDIA_BYTES / 3) * 4 + 4)
  expectError(() => buildMediaMessage({ media_type: 'document', file_b64: tooBig }), 413, '16 MB')
  check(MAX_SEND_MEDIA_BODY > Math.ceil(MAX_SEND_MEDIA_BYTES / 3) * 4, 'body cap fits a 16 MB file')
  return true
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href
    && process.argv.includes('--selftest')) {
  selftest()
  console.log(`media selftest OK (${path.basename(fileURLToPath(import.meta.url))})`)
}
