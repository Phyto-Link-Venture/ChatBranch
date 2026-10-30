'use client'

import { useCallback, useEffect, useRef, useState } from 'react'
import { Mic, Send, Trash2 } from 'lucide-react'
import { useT } from '@/i18n'

/** WhatsApp voice notes are short by nature; this also bounds the upload. */
const MAX_SECONDS = 5 * 60

/**
 * The containers browsers actually record, best first. Opus is what WhatsApp's
 * voice note is made of, so an Ogg/Opus recording (Firefox) needs no
 * re-containering at all; Chrome's webm/opus and Safari's mp4/aac are converted
 * server-side. The last entry is "whatever this browser picked".
 */
const PREFERRED = [
  { mime: 'audio/ogg;codecs=opus', ext: 'ogg' },
  { mime: 'audio/ogg', ext: 'ogg' },
  { mime: 'audio/webm;codecs=opus', ext: 'webm' },
  { mime: 'audio/webm', ext: 'webm' },
  { mime: 'audio/mp4', ext: 'm4a' },
]

function pickFormat(): { mime: string; ext: string } | null {
  if (typeof MediaRecorder === 'undefined') return null
  for (const f of PREFERRED) if (MediaRecorder.isTypeSupported?.(f.mime)) return f
  return { mime: '', ext: 'webm' } // let the browser decide; the server sniffs it
}

const mmss = (total: number) =>
  `${Math.floor(total / 60)}:${String(total % 60).padStart(2, '0')}`

/**
 * Record a voice note in the browser and hand it to the page to send.
 *
 * Deliberately a recorder and nothing else: it owns the microphone, the timer
 * and the blob, and the page owns the upload — so a failed send shows up in the
 * page's own error line next to the composer, where every other send error
 * already appears.
 *
 * The microphone is released on stop, on discard AND on unmount: leaving a
 * track live keeps the browser's recording indicator on, which looks like the
 * dashboard is listening when it is not.
 */
export function VoiceRecorder({
  disabled,
  onRecorded,
  onError,
}: {
  disabled?: boolean
  /** Send it. Throwing (or rejecting) leaves the recorder idle; the page reports why. */
  onRecorded: (file: File) => Promise<void>
  onError: (message: string) => void
}) {
  const t = useT()
  const [state, setState] = useState<'idle' | 'recording' | 'sending'>('idle')
  const [seconds, setSeconds] = useState(0)
  const recorder = useRef<MediaRecorder | null>(null)
  const chunks = useRef<Blob[]>([])
  const stream = useRef<MediaStream | null>(null)
  const ticker = useRef<ReturnType<typeof setInterval> | null>(null)
  const discarded = useRef(false)

  const release = useCallback(() => {
    if (ticker.current) clearInterval(ticker.current)
    ticker.current = null
    stream.current?.getTracks().forEach((track) => track.stop())
    stream.current = null
    recorder.current = null
    chunks.current = []
  }, [])

  // the mic must not stay open when the chat is closed mid-recording
  useEffect(() => release, [release])

  const start = async () => {
    const format = pickFormat()
    if (format === null || typeof navigator === 'undefined' || !navigator.mediaDevices?.getUserMedia) {
      onError(t('bubble.voice.unsupported'))
      return
    }
    let media: MediaStream
    try {
      media = await navigator.mediaDevices.getUserMedia({ audio: true })
    } catch {
      // the one failure worth naming: the person said no, or the browser blocked it
      onError(t('bubble.voice.denied'))
      return
    }
    discarded.current = false
    chunks.current = []
    stream.current = media
    const rec = new MediaRecorder(media, format.mime ? { mimeType: format.mime } : undefined)
    recorder.current = rec
    rec.ondataavailable = (e) => {
      if (e.data.size > 0) chunks.current.push(e.data)
    }
    rec.onstop = () => {
      const parts = chunks.current
      const type = rec.mimeType || format.mime || 'audio/webm'
      const ext = PREFERRED.find((f) => type.startsWith(f.mime.split(';')[0]))?.ext ?? format.ext
      release()
      if (discarded.current || parts.length === 0) {
        setState('idle')
        setSeconds(0)
        return
      }
      const file = new File(parts, `voice-note.${ext}`, { type: type.split(';')[0] })
      setState('sending')
      void onRecorded(file)
        .catch(() => onError(t('bubble.voice.failed')))
        .finally(() => {
          setState('idle')
          setSeconds(0)
        })
    }
    rec.start()
    setSeconds(0)
    setState('recording')
    ticker.current = setInterval(() => {
      setSeconds((s) => {
        const next = s + 1
        if (next >= MAX_SECONDS) {
          // keep what was said rather than throwing it away at the limit
          onError(t('bubble.voice.maxLength'))
          try { rec.stop() } catch { /* already stopping */ }
        }
        return next
      })
    }, 1000)
  }

  const stop = () => {
    discarded.current = false
    try { recorder.current?.stop() } catch { setState('idle') }
  }

  const discard = () => {
    discarded.current = true
    try { recorder.current?.stop() } catch { release(); setState('idle') }
  }

  if (state === 'recording')
    return (
      <div
        className="flex shrink-0 items-center gap-1 rounded-lg bg-well px-1.5 py-1"
        role="group"
        aria-label={t('bubble.voice.recording')}
      >
        <span className="h-2 w-2 shrink-0 animate-pulse rounded-full bg-red-400" aria-hidden />
        <span className="font-mono text-[11px] tabular-nums text-text-high" aria-live="off">
          {mmss(seconds)}
        </span>
        <button
          onClick={discard}
          title={t('bubble.voice.cancel')}
          aria-label={t('bubble.voice.cancel')}
          className="grid h-7 w-7 shrink-0 place-items-center rounded-md text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
        >
          <Trash2 size={14} aria-hidden />
        </button>
        <button
          onClick={stop}
          title={t('bubble.voice.stop')}
          aria-label={t('bubble.voice.stop')}
          className="grid h-7 w-7 shrink-0 place-items-center rounded-md bg-primary text-on-primary transition-opacity hover:opacity-90"
        >
          <Send size={13} aria-hidden />
        </button>
      </div>
    )

  return (
    <button
      onClick={start}
      disabled={disabled || state === 'sending'}
      title={state === 'sending' ? t('bubble.voice.sending') : t('bubble.voice.record')}
      aria-label={t('bubble.voice.record')}
      className="grid h-8 w-8 shrink-0 place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high disabled:opacity-35"
    >
      <Mic size={15} aria-hidden />
    </button>
  )
}
