'use client'

/**
 * Attachments inside a chat bubble — every kind visible without leaving the
 * chat:
 *
 *   image / sticker → the picture (click = full size in a new tab)
 *   audio (voice notes) → an inline player: play / pause, seek, time, speed
 *   video (incl. round video notes) → <video controls playsInline>
 *   document → a file card: icon, name, size, Download (+ Open for PDFs)
 *
 * Sources: the payload's short-lived SIGNED links (`stream_url`, `download_url`,
 * `alt_url` — anonymous at the door, Range works, so audio / video stream and
 * seek without downloading the whole file first). A link is kept for its
 * lifetime per attachment (`stableSigned`) so the live stream re-rendering the
 * thread never restarts a playing voice note. Without a signed link (an older
 * API) the bearer route is fetched into a blob, and a 401 signs the person out.
 *
 * Ogg/Opus voice notes don't play in older Safari / iOS, AMR in no browser:
 * when the browser says it can't, the player switches to the server's M4A
 * rendering (`alt_url`, servers with ffmpeg), else offers the download.
 */

import { useEffect, useRef, useState } from 'react'
import { Download, ExternalLink, FileText, Film, Image as ImageIcon, Loader2, Music, Pause, Play } from 'lucide-react'
import { authHeaders, expireSessionOn401 } from '@/lib/auth'
import { useFormat, useT, type MessageKey } from '@/i18n'
import { resolveMediaUrl, type UiMedia } from './inboxTypes'

const HAIRLINE = { borderColor: 'color-mix(in srgb, currentColor 22%, transparent)' }
const SOFT_FILL = { background: 'color-mix(in srgb, currentColor 10%, transparent)' }

/** Fetch an auth-gated URL as a blob and expose it as an object URL (null url = idle). */
export function useBlobUrl(url: string | null) {
  const [src, setSrc] = useState<string | null>(null)
  const [failed, setFailed] = useState(false)
  useEffect(() => {
    if (!url) return
    let cancelled = false
    let objectUrl: string | null = null
    setSrc(null)
    setFailed(false)
    fetch(resolveMediaUrl(url), { headers: authHeaders() })
      .then((r) => {
        if (expireSessionOn401(r)) throw new Error('Session expired')
        if (!r.ok) throw new Error(`HTTP ${r.status}`)
        return r.blob()
      })
      .then((blob) => {
        if (cancelled) return
        objectUrl = URL.createObjectURL(blob)
        setSrc(objectUrl)
      })
      .catch(() => {
        if (!cancelled) setFailed(true)
      })
    return () => {
      cancelled = true
      if (objectUrl) URL.revokeObjectURL(objectUrl)
    }
  }, [url])
  return { src, failed }
}

/** Seconds since the epoch at which a signed link stops working (0 = unknown). */
function expiryOf(signed: string): number {
  const token = signed.split('/').pop() ?? ''
  const exp = Number(token.split('.')[2])
  return Number.isFinite(exp) ? exp : 0
}

/** One signed link per attachment while it has ≥5 minutes left — a re-rendered
 *  thread hands out a fresh token, which must not swap a playing src. */
const signedCache = new Map<string, string>()
function stableSigned(key: string, fresh: string | undefined): string | undefined {
  if (!fresh) return undefined
  const prev = signedCache.get(key)
  if (prev && expiryOf(prev) - Date.now() / 1000 > 300) return prev
  signedCache.set(key, fresh)
  return fresh
}

interface MediaSources {
  /** What <img>/<audio>/<video> loads (null while a blob is on its way). */
  src: string | null
  /** Where Download points (an attachment with the original name). */
  download: string | null
  /** The M4A rendering, when the server offers one. */
  alt: string | null
  failed: boolean
  /** A signed link failed: try the payload's newest one, once. */
  refresh: () => boolean
}

function useMediaSources(media: UiMedia, needBlob: boolean): MediaSources {
  const [base, setBase] = useState(() => stableSigned(media.url, media.stream_url))
  const [retried, setRetried] = useState(false)
  useEffect(() => {
    setBase(stableSigned(media.url, media.stream_url))
    setRetried(false)
    // a fresh token for the SAME attachment only matters through refresh()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [media.url])
  const blob = useBlobUrl(!base && needBlob ? media.url : null)
  if (base) {
    const abs = resolveMediaUrl(base)
    return {
      src: abs,
      download: `${abs}?download=1`,
      alt: media.alt_url ? `${abs}?format=m4a` : null,
      failed: false,
      refresh: () => {
        if (retried || !media.stream_url || media.stream_url === base) return false
        signedCache.set(media.url, media.stream_url)
        setBase(media.stream_url)
        setRetried(true)
        return true
      },
    }
  }
  return { src: blob.src, download: blob.src, alt: null, failed: blob.failed, refresh: () => false }
}

/** "1.2 MB" in the person's language. */
function useSize() {
  const f = useFormat()
  return (bytes?: number | null) => {
    if (!bytes || bytes <= 0) return ''
    const [n, unit] =
      bytes >= 1024 * 1024 ? [bytes / (1024 * 1024), 'megabyte'] : bytes >= 1024 ? [bytes / 1024, 'kilobyte'] : [bytes, 'byte']
    return f.number(n, { style: 'unit', unit, unitDisplay: unit === 'byte' ? 'narrow' : 'short', maximumFractionDigits: unit === 'byte' ? 0 : 1 })
  }
}

const kindLabel = (t: ReturnType<typeof useT>, type: string) =>
  ['image', 'document', 'audio', 'video', 'sticker'].includes(type) ? t(`chats.parts.media.${type}` as MessageKey) : type

function DownloadLink({ href, name, className = '' }: { href: string | null; name?: string; className?: string }) {
  const t = useT()
  if (!href) return null
  return (
    <a
      href={href}
      download={name || true}
      target="_blank"
      rel="noreferrer"
      className={`inline-flex items-center gap-1 text-[10.5px] font-bold uppercase tracking-wide underline underline-offset-2 ${className}`}
      title={t('common.actions.download')}
    >
      <Download size={11} aria-hidden /> {t('common.actions.download')}
    </a>
  )
}

/** A file card: what it is, how big, Download (+ Open for what the browser shows itself). */
export function FileCard({ media, note }: { media: UiMedia; note?: string }) {
  const t = useT()
  const size = useSize()
  const { src, download, failed } = useMediaSources(media, true)
  const mime = (media.mime || '').toLowerCase()
  const Icon = media.type === 'audio' ? Music : media.type === 'video' ? Film : media.type === 'image' ? ImageIcon : FileText
  const ext = (media.name || '').includes('.') ? (media.name || '').split('.').pop()!.toUpperCase().slice(0, 5) : ''
  const meta = [ext, size(media.size)].filter(Boolean).join(' · ')
  return (
    <span className="mb-1 flex max-w-[300px] items-center gap-2.5 rounded-lg border px-2.5 py-2" style={HAIRLINE}>
      <span className="grid h-9 w-9 shrink-0 place-items-center rounded-md" style={SOFT_FILL}>
        <Icon size={17} className="opacity-80" aria-hidden />
      </span>
      <span className="min-w-0 flex-1">
        <span className="block truncate text-[12.5px] font-semibold" title={media.name}>
          {media.name || kindLabel(t, media.type)}
        </span>
        {meta && <span className="block text-[10.5px] tabular-nums opacity-70">{meta}</span>}
        {note && <span className="block text-[10.5px] leading-snug opacity-75">{note}</span>}
        <span className="mt-0.5 flex flex-wrap items-center gap-3">
          {failed ? (
            <span className="text-[10.5px] opacity-60">{t('chats.parts.unavailable')}</span>
          ) : !download ? (
            <Loader2 size={12} className="animate-spin opacity-60" aria-hidden />
          ) : (
            <>
              <DownloadLink href={download} name={media.name} />
              {(mime === 'application/pdf' || mime === 'text/plain') && src && (
                <a
                  href={src}
                  target="_blank"
                  rel="noreferrer"
                  className="inline-flex items-center gap-1 text-[10.5px] font-bold uppercase tracking-wide underline underline-offset-2"
                >
                  <ExternalLink size={11} aria-hidden /> {t('common.actions.open')}
                </a>
              )}
            </>
          )}
        </span>
      </span>
    </span>
  )
}

function ImageAttachment({ media }: { media: UiMedia }) {
  const t = useT()
  const { src, failed, refresh } = useMediaSources(media, true)
  const [loaded, setLoaded] = useState(false)
  const [broken, setBroken] = useState(false)
  // not renderable here (HEIC on a non-Apple browser, a Lottie sticker…) → the file card
  if (failed || broken) return <FileCard media={media} />
  if (!src)
    return (
      <span className="mb-1 grid h-28 w-44 place-items-center rounded-lg bg-fill">
        <Loader2 size={15} className="animate-spin opacity-60" aria-hidden />
      </span>
    )
  return (
    <a href={src} target="_blank" rel="noreferrer" title={t('chats.parts.openFullSize')} className="mb-1 block">
      {/* a signed / object URL — next/image can't optimize either */}
      {/* eslint-disable-next-line @next/next/no-img-element */}
      <img
        src={src}
        alt={media.name || t('chats.parts.imageAlt')}
        loading="lazy"
        onLoad={() => setLoaded(true)}
        onError={() => {
          if (!refresh()) setBroken(true)
        }}
        className={`block max-h-64 max-w-full rounded-lg object-cover ${loaded ? '' : 'min-h-28 min-w-44 bg-fill'}`}
      />
    </a>
  )
}

function VideoAttachment({ media }: { media: UiMedia }) {
  const t = useT()
  const { src, download, failed, refresh } = useMediaSources(media, true)
  const [broken, setBroken] = useState(false)
  if (failed || broken) return <FileCard media={media} note={t('chats.parts.player.videoFailed')} />
  if (!src)
    return (
      <span className="mb-1 grid h-36 w-56 place-items-center rounded-lg bg-fill">
        <Loader2 size={15} className="animate-spin opacity-60" aria-hidden />
      </span>
    )
  return (
    <span className="mb-1 block">
      {/* customer video: no captions exist for it */}
      {/* eslint-disable-next-line jsx-a11y/media-has-caption */}
      <video
        controls
        playsInline
        preload="metadata"
        // #t=0.1: iOS paints the first frame instead of a black box (no poster)
        src={src.startsWith('blob:') ? src : `${src}#t=0.1`}
        onError={() => {
          if (!refresh()) setBroken(true)
        }}
        className="block max-h-72 w-full max-w-[320px] rounded-lg bg-fill"
      />
      <span className="mt-0.5 flex items-center justify-end gap-2">
        <DownloadLink href={download} name={media.name} className="opacity-70" />
      </span>
    </span>
  )
}

const SPEEDS = [1, 1.5, 2]

const clock = (s: number) => {
  if (!Number.isFinite(s) || s < 0) return '0:00'
  const m = Math.floor(s / 60)
  const sec = Math.floor(s % 60)
  return `${m}:${sec.toString().padStart(2, '0')}`
}

/** Can this browser play the attachment's own format? (after mount only) */
function canPlay(mime: string): boolean {
  const base = mime.split(';')[0].trim().toLowerCase()
  if (!base) return true
  const probe = document.createElement('audio')
  const query = base === 'audio/ogg' || base === 'audio/opus' ? 'audio/ogg; codecs=opus' : base
  return probe.canPlayType(query) !== ''
}

/** The voice-note player: play / pause, seek, elapsed / length, speed, download. */
export function AudioAttachment({ media }: { media: UiMedia }) {
  const t = useT()
  const { src, download, alt, failed, refresh } = useMediaSources(media, true)
  const audio = useRef<HTMLAudioElement | null>(null)
  const [chosen, setChosen] = useState<string | null>(null)
  const [unsupported, setUnsupported] = useState(false)
  const [broken, setBroken] = useState(false)
  const [playing, setPlaying] = useState(false)
  const [pos, setPos] = useState(0)
  const [dur, setDur] = useState(0)
  const [speed, setSpeed] = useState(1)

  // pick the source once the browser can be asked (never during SSR)
  useEffect(() => {
    if (!src) return
    if (src.startsWith('blob:') || canPlay(media.mime || '')) {
      setChosen(src)
      setUnsupported(false)
    } else if (alt) {
      setChosen(alt)
      setUnsupported(false)
    } else {
      setChosen(null)
      setUnsupported(true)
    }
  }, [src, alt, media.mime])

  useEffect(() => {
    if (audio.current) audio.current.playbackRate = speed
  }, [speed, chosen])

  if (failed) return <FileCard media={media} />
  if (unsupported) return <FileCard media={media} note={t('chats.parts.player.unsupported')} />
  if (broken) return <FileCard media={media} note={t('chats.parts.player.failed')} />

  const toggle = () => {
    const el = audio.current
    if (!el) return
    if (el.paused) void el.play().catch(() => {})
    else el.pause()
  }
  const known = Number.isFinite(dur) && dur > 0

  return (
    <span className="mb-1 flex w-[260px] max-w-full items-center gap-2 rounded-lg border px-2 py-1.5" style={HAIRLINE}>
      <button
        type="button"
        onClick={toggle}
        disabled={!chosen}
        aria-label={t(playing ? 'chats.parts.player.pause' : 'chats.parts.player.play')}
        title={t(playing ? 'chats.parts.player.pause' : 'chats.parts.player.play')}
        className="grid h-8 w-8 shrink-0 place-items-center rounded-full transition-opacity hover:opacity-80 disabled:opacity-40"
        style={SOFT_FILL}
      >
        {!chosen ? (
          <Loader2 size={14} className="animate-spin" aria-hidden />
        ) : playing ? (
          <Pause size={14} aria-hidden />
        ) : (
          <Play size={14} className="translate-x-px" aria-hidden />
        )}
      </button>
      <span className="min-w-0 flex-1">
        <input
          type="range"
          min={0}
          max={known ? dur : 1}
          step={0.05}
          value={known ? Math.min(pos, dur) : 0}
          disabled={!known}
          onChange={(e) => {
            const v = Number(e.target.value)
            if (audio.current) audio.current.currentTime = v
            setPos(v)
          }}
          aria-label={t('chats.parts.player.seek')}
          className="block h-1.5 w-full cursor-pointer"
          style={{ accentColor: 'currentColor' }}
        />
        <span className="mt-0.5 flex justify-between text-[10px] tabular-nums opacity-70">
          <span>{clock(pos)}</span>
          <span>{known ? clock(dur) : ''}</span>
        </span>
      </span>
      <button
        type="button"
        onClick={() => setSpeed(SPEEDS[(SPEEDS.indexOf(speed) + 1) % SPEEDS.length])}
        aria-label={t('chats.parts.player.speed')}
        title={t('chats.parts.player.speed')}
        className="shrink-0 rounded px-1 text-[10.5px] font-bold tabular-nums opacity-80 hover:opacity-100"
        style={SOFT_FILL}
      >
        {speed}×
      </button>
      {download && (
        <a
          href={download}
          download={media.name || true}
          target="_blank"
          rel="noreferrer"
          aria-label={t('common.actions.download')}
          title={t('common.actions.download')}
          className="shrink-0 opacity-70 hover:opacity-100"
        >
          <Download size={13} aria-hidden />
        </a>
      )}
      {chosen && (
        // customer voice note: no captions exist for it
        // eslint-disable-next-line jsx-a11y/media-has-caption
        <audio
          ref={audio}
          src={chosen}
          preload="metadata"
          className="hidden"
          onPlay={() => setPlaying(true)}
          onPause={() => setPlaying(false)}
          onEnded={() => {
            setPlaying(false)
            setPos(0)
          }}
          onTimeUpdate={(e) => setPos(e.currentTarget.currentTime)}
          onLoadedMetadata={(e) => setDur(e.currentTarget.duration)}
          onDurationChange={(e) => setDur(e.currentTarget.duration)}
          onError={() => {
            // the original failed: the M4A rendering next, then a fresh link, then the download
            if (alt && chosen !== alt) setChosen(alt)
            else if (!refresh()) setBroken(true)
          }}
        />
      )}
    </span>
  )
}

/** Bubble attachment, by kind. */
export function MediaAttachment({ media }: { media: UiMedia }) {
  if (media.type === 'image') return <ImageAttachment media={media} />
  if (media.type === 'video') return <VideoAttachment media={media} />
  if (media.type === 'audio') return <AudioAttachment media={media} />
  return <FileCard media={media} />
}

/** The attachment a message announced ("[image]") never arrived: a short line, not the raw marker.
 *  A brand-new inbound one is probably still on its way, so it says "downloading…" for a minute. */
export function MediaMissing({ kind, ts, inbound }: { kind: string; ts: string; inbound: boolean }) {
  const t = useT()
  const age = Date.now() - Date.parse(ts)
  const fresh = inbound && Number.isFinite(age) && age < 60_000
  const [, tick] = useState(0)
  useEffect(() => {
    if (!fresh) return
    const id = window.setTimeout(() => tick((n) => n + 1), Math.max(1000, 60_000 - age))
    return () => window.clearTimeout(id)
  }, [fresh, age])
  const label = kindLabel(t, kind)
  return (
    <span className="flex items-center gap-1.5 text-[12px] italic opacity-70">
      {fresh && <Loader2 size={11} className="animate-spin" aria-hidden />}
      {t(fresh ? 'chats.parts.mediaLoading' : 'chats.parts.mediaMissing', { kind: label })}
    </span>
  )
}
