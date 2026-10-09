import { authHeaders } from './auth'

// One shared <audio> element so starting a preview stops the previous one.
const API = process.env.NEXT_PUBLIC_API_URL ?? 'http://localhost:8090/api'

let audio: HTMLAudioElement | null = null
let currentUrl: string | null = null
let playingId: string | null = null
let token = 0
let controller: AbortController | null = null
const listeners = new Set<(id: string | null) => void>()

function emit(id: string | null) {
  playingId = id
  listeners.forEach((cb) => cb(id))
}

function ensureAudio(): HTMLAudioElement {
  if (!audio) {
    audio = new Audio()
    audio.addEventListener('ended', () => emit(null))
    audio.addEventListener('error', () => emit(null))
  }
  return audio
}

export function subscribePlaying(cb: (id: string | null) => void): () => void {
  listeners.add(cb)
  cb(playingId)
  return () => listeners.delete(cb)
}

export function stopPreview(): void {
  token++
  controller?.abort()
  controller = null
  if (audio) {
    audio.pause()
    audio.currentTime = 0
  }
  if (currentUrl) {
    URL.revokeObjectURL(currentUrl)
    currentUrl = null
  }
  emit(null)
}

/** A preview the API refused: `detail` is the server's own words (shown as
 * sent); without one the page words it from `status`. */
export class PreviewError extends Error {
  status: number
  constructor(status: number, detail: string) {
    super(detail)
    this.status = status
  }
}

export async function playPreview(voiceId: string, text: string): Promise<void> {
  stopPreview()
  const myToken = token
  const el = ensureAudio()
  controller = new AbortController()
  try {
    const resp = await fetch(`${API}/voice/preview`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', ...authHeaders() },
      body: JSON.stringify({ voiceId, text }),
      signal: controller.signal,
    })
    if (!resp.ok) {
      if (myToken !== token) return
      emit(null)
      let detail = ''
      try {
        const body = await resp.json()
        if (body?.detail) detail = String(body.detail)
      } catch {
        /* body wasn't JSON */
      }
      throw new PreviewError(resp.status, detail)
    }
    const blob = await resp.blob()
    if (myToken !== token) return
    const url = URL.createObjectURL(blob)
    if (myToken !== token) {
      URL.revokeObjectURL(url)
      return
    }
    currentUrl = url
    el.src = currentUrl
    emit(voiceId)
    await el.play()
  } catch (err) {
    if (err instanceof DOMException && err.name === 'AbortError') {
      return
    }
    if (myToken === token) emit(null)
    throw err
  }
}
