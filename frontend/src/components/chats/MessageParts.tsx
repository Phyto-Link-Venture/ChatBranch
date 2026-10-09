'use client'

/**
 * The small pieces the conversation column is built from — avatar, delivery
 * ticks, media attachments, call recordings and transcripts. Pulled out of
 * app/chats/page.tsx so the page holds state and layout only.
 */

import { useEffect, useRef, useState, type ReactNode } from 'react'
import { AlertCircle, Check, CheckCheck, Clock, FileText, Film, Loader2, Music, PhoneCall, type LucideIcon } from 'lucide-react'
import { authHeaders } from '@/lib/auth'
import { ChannelLogo } from '@/components/ui/ChannelLogo'
import { channelName, initials, logoKey, resolveMediaUrl, timeShort, type Channel, type UiMedia } from './inboxTypes'
import { Trans, useFormat, useI18n, useT, intlTag, type MessageKey, type TFunction } from '@/i18n'

/** Honesty marker: features not yet validated against the live provider. */
export function BetaChip() {
  const t = useT()
  return (
    <span
      className="shrink-0 rounded border border-amber/40 px-1 py-px text-[8.5px] font-bold uppercase tracking-wide text-amber-300"
      title={t('chats.parts.betaHint')}
    >
      {t('chats.parts.beta')}
    </span>
  )
}

/** Contact avatar: initials on the rail tone (ink when `strong`), with the
 *  channel's real logo tucked into the corner. */
export function Avatar({
  name,
  channel,
  size = 36,
  strong = false,
}: {
  name: string
  channel?: Channel
  size?: number
  strong?: boolean
}) {
  const t = useT()
  const badge = Math.max(14, Math.round(size * 0.44))
  return (
    <span className="relative inline-block shrink-0" style={{ width: size, height: size }}>
      <span
        className={`grid h-full w-full place-items-center rounded-full font-semibold ${
          strong ? 'bg-primary text-on-primary' : 'bg-[var(--nav-active-bg)] text-text-high'
        }`}
        style={{ fontSize: Math.max(10, size * 0.34) }}
        aria-hidden
      >
        {initials(name)}
      </span>
      {channel && (
        <span
          className="absolute -bottom-0.5 -right-0.5 grid place-items-center rounded-full bg-card"
          style={{ width: badge, height: badge, boxShadow: '0 0 0 1.5px var(--card)' }}
        >
          <ChannelLogo channel={logoKey(channel)} size={badge - 3} title={channelName(t, channel)} />
        </span>
      )}
    </span>
  )
}

/** Which channel a bubble came from — shown in the combined per-contact view. */
export function ChannelBadge({ channel }: { channel: Channel }) {
  const t = useT()
  return (
    <span className="mb-1 flex w-fit items-center gap-1 text-[9.5px] font-bold uppercase tracking-[0.08em] opacity-75">
      <ChannelLogo channel={logoKey(channel)} size={10} variant="mono" title="" /> {channelName(t, channel)}
    </span>
  )
}

export function StatusTick({ status }: { status: string }) {
  const t = useT()
  if (status === 'read') return <CheckCheck size={13} className="cb-tick-read" aria-label={t('chats.parts.tickRead')} />
  if (status === 'delivered') return <CheckCheck size={13} aria-label={t('chats.parts.tickDelivered')} />
  if (status === 'failed') return <AlertCircle size={12} className="cb-tick-failed" aria-label={t('chats.parts.tickFailed')} />
  if (status === 'sandbox') return <Clock size={11} aria-label={t('chats.parts.tickSandbox')} />
  return <Check size={13} aria-label={t('chats.parts.tickSent')} />
}

/** A centred, muted line between hairlines — day separators and system notes. */
export function CenterNote({ children, title }: { children: ReactNode; title?: string }) {
  return (
    <div className="cb-center-note" title={title}>
      <span>{children}</span>
    </div>
  )
}

const MEDIA_CHIP_ICONS: Record<Exclude<UiMedia['type'], 'image'>, LucideIcon> = {
  document: FileText,
  audio: Music,
  video: Film,
}

/** Fetch an auth-gated URL as a blob and expose it as an object URL. */
function useBlobUrl(url: string) {
  const [src, setSrc] = useState<string | null>(null)
  const [failed, setFailed] = useState(false)
  useEffect(() => {
    let cancelled = false
    let objectUrl: string | null = null
    setSrc(null)
    setFailed(false)
    fetch(resolveMediaUrl(url), { headers: authHeaders() })
      .then((r) => {
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

/** Bubble attachment — the media URL needs the bearer token. */
export function MediaAttachment({ media }: { media: UiMedia }) {
  const t = useT()
  const { src: url, failed } = useBlobUrl(media.url)

  if (media.type === 'image' && !failed) {
    return url ? (
      <a href={url} target="_blank" rel="noreferrer" title={t('chats.parts.openFullSize')} className="mb-1 block">
        {/* object URL — next/image can't optimize blobs */}
        {/* eslint-disable-next-line @next/next/no-img-element */}
        <img src={url} alt={t('chats.parts.imageAlt')} className="block max-h-64 max-w-full rounded-lg object-cover" />
      </a>
    ) : (
      <span className="mb-1 grid h-28 w-44 place-items-center rounded-lg bg-fill">
        <Loader2 size={15} className="animate-spin opacity-60" aria-hidden />
      </span>
    )
  }

  const ChipIcon = media.type === 'image' ? FileText : MEDIA_CHIP_ICONS[media.type]
  return (
    <span className="mb-1 flex items-center gap-2 rounded-lg border px-2.5 py-1.5" style={{ borderColor: 'color-mix(in srgb, currentColor 22%, transparent)' }}>
      <ChipIcon size={14} className="shrink-0 opacity-75" aria-hidden />
      <span className="text-[11.5px] font-semibold opacity-90">
        {['image', 'document', 'audio', 'video'].includes(media.type)
          ? t(`chats.parts.media.${media.type}` as MessageKey)
          : media.type}
      </span>
      {url ? (
        <a
          href={url}
          target="_blank"
          rel="noreferrer"
          className="ml-1 text-[10.5px] font-bold uppercase tracking-wide underline underline-offset-2"
        >
          {t('common.actions.open')}
        </a>
      ) : failed ? (
        <span className="text-[10.5px] opacity-60">{t('chats.parts.unavailable')}</span>
      ) : (
        <Loader2 size={12} className="animate-spin opacity-60" aria-hidden />
      )}
    </span>
  )
}

/** Call-recording audio player (the WAV endpoint is auth-gated). */
export function CallRecordingPlayer({
  url,
  onTime,
  audioEl,
}: {
  url: string
  onTime?: (seconds: number) => void
  audioEl?: React.MutableRefObject<HTMLAudioElement | null>
}) {
  const t = useT()
  const { src, failed } = useBlobUrl(url)
  if (failed) return <span className="text-[11px] text-text-faint">{t('chats.parts.recordingUnavailable')}</span>
  if (!src)
    return (
      <span className="flex items-center gap-1.5 text-[11px] text-text-faint">
        <Loader2 size={12} className="animate-spin" aria-hidden /> {t('common.status.loading')}
      </span>
    )
  // eslint-disable-next-line jsx-a11y/media-has-caption
  return (
    <audio
      ref={(el) => {
        if (audioEl) audioEl.current = el
      }}
      controls
      src={src}
      className="w-full"
      style={{ height: 34 }}
      onTimeUpdate={onTime ? (e) => onTime(e.currentTarget.currentTime) : undefined}
    />
  )
}

/** Call-transcript timeline entry — collapsed to a line by default. */
export function CallTranscriptChip({ text, time }: { text: string; time: string }) {
  const t = useT()
  const [open, setOpen] = useState(false)
  const lines = text.split('\n').slice(1).filter((l) => l.trim()) // drop the header line
  return (
    <div className="my-1.5 flex flex-col items-center">
      <CenterNote>
        <button type="button" onClick={() => setOpen(!open)} className="inline-flex cursor-pointer items-center gap-1.5 hover:text-text-high">
          <PhoneCall size={11} aria-hidden />{' '}
          {t('chats.parts.transcriptChip', {
            count: lines.length,
            action: t(open ? 'chats.parts.hide' : 'chats.parts.view'),
            time,
          })}
        </button>
      </CenterNote>
      {open && (
        <div className="mt-1 max-w-[85%] space-y-0.5 rounded-lg border border-line bg-card p-2 text-left text-[12px] leading-snug">
          {lines.map((ln, i) => (
            <div key={i} className={ln.startsWith('AI:') ? 'text-brand-300' : 'text-text-mid'}>
              {ln}
            </div>
          ))}
        </div>
      )}
    </div>
  )
}

/** Human label for the persisted end-cause codes. */
const END_CAUSES: Record<string, MessageKey> = {
  agent: 'chats.parts.endedBy.agent',
  contact: 'chats.parts.endedBy.contact',
  operator: 'chats.parts.endedBy.operator',
  'system:silence': 'chats.parts.endedBy.silence',
  'system:no_answer': 'chats.parts.endedBy.noAnswer',
  'system:max_duration': 'chats.parts.endedBy.maxDuration',
}
const endedByLabel = (t: TFunction, v: string) => (END_CAUSES[v] ? t(END_CAUSES[v]) : v)

/** A transcript line's clock (with seconds) in the current language. */
function useTurnClock() {
  const { locale } = useI18n()
  const fmt = new Intl.DateTimeFormat(intlTag(locale), { hour: '2-digit', minute: '2-digit', second: '2-digit' })
  return (ts: number) => fmt.format(new Date(ts * 1000))
}

export interface CallRec {
  name: string
  url: string
  created: string
  size?: number
  /** ts = epoch seconds when the turn was logged (newer calls only). */
  transcript?: { role: string; text: string; ts?: number }[]
  meta?: { initiated_by?: string; ended_by?: string; account?: string; rec_started_at?: number }
}

/** Recording start in epoch ms: meta.rec_started_at (the WAV's true t=0), then
 *  the filename ms ({wa_id}-{ms}.wav), then the created date. */
function recStartMs(rec: CallRec): number {
  if (rec.meta?.rec_started_at) return rec.meta.rec_started_at
  const stem = rec.name.replace(/\.[^.]+$/, '')
  const ms = Number(stem.split('-').pop())
  return Number.isFinite(ms) && ms > 1e12 ? ms : Date.parse(rec.created)
}

/** A turn's position in the recording (seconds), corrected for WHEN each side
 *  is logged: assistant text at generation (audio ~1s later), user text at
 *  transcription-final (~2.5s after they started speaking). */
function turnOffsetSec(t: { role: string; ts?: number }, startMs: number): number | null {
  if (!t.ts) return null
  const raw = (t.ts * 1000 - startMs) / 1000 + (t.role === 'assistant' ? 1.0 : -2.5)
  return Math.max(0, raw)
}

/** One past call: the recording player plus its transcript (hidden until clicked). */
export function CallEntry({ rec, withTranscript = true }: { rec: CallRec; withTranscript?: boolean }) {
  const t = useT()
  const f = useFormat()
  const turnClock = useTurnClock()
  const [showT, setShowT] = useState(false)
  const [pos, setPos] = useState(-1) // playback position (s); -1 = not playing
  const activeLineRef = useRef<HTMLDivElement | null>(null)
  const audioRef = useRef<HTMLAudioElement | null>(null)
  const turns = withTranscript ? (rec.transcript ?? []) : []

  const startMs = recStartMs(rec)
  const activeIdx =
    pos < 0
      ? -1
      : turns.reduce((acc, turn, i) => {
          const off = turnOffsetSec(turn, startMs)
          return off !== null && off <= pos ? i : acc
        }, -1)

  useEffect(() => {
    activeLineRef.current?.scrollIntoView({ block: 'nearest', behavior: 'smooth' })
  }, [activeIdx])

  const seekTo = (turn: { role: string; ts?: number }) => {
    const off = turnOffsetSec(turn, startMs)
    const audio = audioRef.current
    if (off === null || !audio) return
    audio.currentTime = off
    setPos(off)
    void audio.play().catch(() => {})
  }
  return (
    <div className="rounded-lg border border-line bg-card p-2.5">
      {(rec.meta?.initiated_by || rec.meta?.ended_by) && (
        <div className="mb-1 text-[10.5px] leading-snug text-text-muted">
          {rec.meta?.initiated_by && (
            <Trans
              k={rec.meta?.account ? 'chats.parts.calledByVia' : 'chats.parts.calledBy'}
              values={{
                who: <span className="font-semibold text-text-high">{rec.meta.initiated_by}</span>,
                account: rec.meta?.account ?? '',
              }}
            />
          )}
          {rec.meta?.initiated_by && rec.meta?.ended_by && ' · '}
          {rec.meta?.ended_by && (
            <Trans
              k="chats.parts.endedByLine"
              values={{ who: <span className="font-semibold text-text-high">{endedByLabel(t, rec.meta.ended_by)}</span> }}
            />
          )}
        </div>
      )}
      <div className="mb-1 flex items-center justify-between gap-2">
        <span className="text-[10.5px] text-text-muted">{f.dateTime(rec.created)}</span>
        {turns.length > 0 && (
          <button
            type="button"
            onClick={() => setShowT(!showT)}
            className="shrink-0 text-[10.5px] font-semibold text-brand-300 hover:underline"
          >
            {showT ? t('chats.parts.hideTranscript') : t('chats.parts.transcriptN', { count: turns.length })}
          </button>
        )}
      </div>
      <CallRecordingPlayer url={rec.url} audioEl={audioRef} onTime={turns.some((turn) => turn.ts) ? (s) => setPos(s) : undefined} />
      {showT && turns.length > 0 && (
        <div className="mt-1.5 max-h-48 space-y-0.5 overflow-y-auto rounded-md border border-line bg-well p-2 text-[12px] leading-snug">
          {turns.map((turn, i) => (
            <div
              key={i}
              ref={i === activeIdx ? activeLineRef : undefined}
              onClick={turn.ts ? () => seekTo(turn) : undefined}
              title={turn.ts ? t('chats.parts.playFromHere') : undefined}
              className={`flex gap-1.5 rounded px-1 py-0.5 transition-colors ${i === activeIdx ? 'bg-brand-400/15' : ''} ${
                turn.ts ? 'cursor-pointer hover:bg-fill-hover' : ''
              } ${turn.role === 'assistant' ? 'text-brand-300' : 'text-text-mid'}`}
            >
              {turn.ts && <span className="shrink-0 pt-px font-mono text-[10px] text-text-faint">{turnClock(turn.ts)}</span>}
              <span>
                <span className="font-semibold">{t(turn.role === 'assistant' ? 'chats.parts.ai' : 'chats.parts.customer')}:</span>{' '}
                {turn.text}
              </span>
            </div>
          ))}
        </div>
      )}
    </div>
  )
}

export interface SipCall {
  call_id: string
  started_at: string
  ended_at: string
  summary: string
  outcome: string
  followup_date: string | null
  needs_human: boolean
  recording: string | null
  transcript: { role: string; text: string; ts?: number }[]
}

/** A past SIP phone call rendered inline in the message stream. */
export function SipCallCard({ rec }: { rec: SipCall }) {
  const t = useT()
  const f = useFormat()
  const turnClock = useTurnClock()
  const [open, setOpen] = useState(false)
  return (
    <div className="my-2 flex justify-center">
      <div className="w-[85%] max-w-[440px]">
        <CenterNote>
          <span className="inline-flex items-center gap-1.5">
            <PhoneCall size={11} aria-hidden />{' '}
            {t('chats.parts.aiVoiceCallLine', {
              outcome: rec.outcome.replace(/_/g, ' '),
              time: timeShort(rec.ended_at || rec.started_at, f),
            })}
          </span>
        </CenterNote>
        <div className="mt-1 rounded-lg border border-line bg-card p-2.5">
          {rec.summary && <div className="text-[12px] leading-snug text-text-mid">{rec.summary}</div>}
          {rec.recording && (
            <div className="mt-1.5">
              <CallRecordingPlayer url={`calls/recording/${rec.call_id}`} />
            </div>
          )}
          {rec.transcript.length > 0 && (
            <button
              type="button"
              onClick={() => setOpen(!open)}
              className="mt-1 text-[10.5px] font-semibold text-brand-300 hover:underline"
            >
              {open ? t('chats.parts.hideTranscript') : t('chats.parts.transcriptN', { count: rec.transcript.length })}
            </button>
          )}
          {open && (
            <div className="mt-1.5 max-h-56 space-y-0.5 overflow-y-auto rounded-md border border-line bg-well p-2 text-[12px] leading-snug">
              {rec.transcript.map((turn, i) => (
                <div key={i} className={`flex gap-1.5 ${turn.role === 'assistant' ? 'text-brand-300' : 'text-text-mid'}`}>
                  {turn.ts && <span className="shrink-0 pt-px font-mono text-[10px] text-text-faint">{turnClock(turn.ts)}</span>}
                  <span>
                    <span className="font-semibold">{t(turn.role === 'assistant' ? 'chats.parts.ai' : 'chats.parts.caller')}:</span>{' '}
                    {turn.text}
                  </span>
                </div>
              ))}
            </div>
          )}
        </div>
      </div>
    </div>
  )
}
