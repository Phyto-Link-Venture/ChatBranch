'use client'

import { useCallback, useEffect, useRef, useState, type ChangeEvent } from 'react'
import { useEntityQuery } from '@/lib/EntityContext'
import {
  AlertCircle,
  Ban,
  Bot,
  Check,
  CheckCheck,
  CheckCircle2,
  ChevronLeft,
  Clock,
  Facebook,
  FileText,
  Film,
  Info,
  Instagram,
  LayoutTemplate,
  Layers,
  Link2,
  Loader2,
  Lock,
  Mail,
  Globe,
  MessageCircle,
  Music,
  Paperclip,
  PhoneCall,
  PhoneOff,
  RefreshCw,
  RotateCcw,
  Search,
  Send,
  Sparkles,
  StickyNote,
  Tag,
  UserRound,
  Users,
  X,
  type LucideIcon,
} from 'lucide-react'
import { Card } from '@/components/ui/Card'
import { Button } from '@/components/ui/Button'
import { Select } from '@/components/ui/Select'
import ChannelHub from '@/components/ChannelHub'
import VoiceCallPanel from '@/components/chats/VoiceCallPanel'
import VoiceCallButton from '@/components/chats/VoiceCallButton'
import ScriptCallButton from '@/components/chats/ScriptCallButton'
import ActiveCallsStrip from '@/components/chats/ActiveCallsStrip'
import WhatsAppIcon from '@/components/chats/WhatsAppIcon'
import { authHeaders, expireSessionOn401 } from '@/lib/auth'
import { getRole } from '@/lib/role'
import { useApp } from '@/lib/AppContext'
import { CONVERSATION_TAGS, tagMeta } from '@/lib/tags'
import { subscribeChatStream } from '@/lib/chatStream'
import './chats.css'

// Native Django API base. Override with NEXT_PUBLIC_API_URL (same as AppContext).
const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

type Channel = 'whatsapp' | 'facebook' | 'instagram' | 'telegram' | 'email' | 'website'
type ThreadStatus = 'open' | 'pending' | 'snoozed' | 'resolved'

const CHANNEL_META: Record<Channel, { lbl: string; color: string; Icon: LucideIcon; placeholder: string; beta?: boolean }> = {
  whatsapp: { lbl: 'WhatsApp', color: '#10B981', Icon: MessageCircle, placeholder: '+60 phone number' },
  facebook: { lbl: 'Messenger', color: '#3B82F6', Icon: Facebook, placeholder: 'Messenger PSID', beta: true },
  instagram: { lbl: 'Instagram', color: '#E1306C', Icon: Instagram, placeholder: 'Instagram IGSID', beta: true },
  telegram: { lbl: 'Telegram', color: '#229ED9', Icon: Send, placeholder: 'Telegram chat ID' },
  email: { lbl: 'Email', color: '#F59E0B', Icon: Mail, placeholder: 'customer@email.com' },
  // The site widget: the visitor's browser is the transport, so there is no
  // address to start a conversation FROM our side — they always begin it.
  website: { lbl: 'Website', color: '#8B5CF6', Icon: Globe, placeholder: 'started by the visitor' },
}

const THREAD_STATUSES: ThreadStatus[] = ['open', 'pending', 'snoozed', 'resolved']

/** Let a mouse wheel scroll a horizontal chip row (no touchpad / shift needed). */
function wheelToHorizontal(e: React.WheelEvent<HTMLDivElement>) {
  if (e.deltaY === 0) return
  e.currentTarget.scrollLeft += e.deltaY
}

/** Honesty marker: features not yet validated against the live provider. */
function BetaChip() {
  return (
    <span className="shrink-0 rounded bg-amber/15 px-1 py-px text-[8.5px] font-bold uppercase tracking-wide text-amber-300" title="Not yet validated against the live provider">
      Beta
    </span>
  )
}
const CHANNELS: Channel[] = ['whatsapp', 'facebook', 'instagram', 'telegram', 'email', 'website']

interface UiMedia {
  type: 'image' | 'document' | 'audio' | 'video'
  url: string
}

interface UiMessage {
  id: string
  dir: 'in' | 'out'
  text: string
  ts: string
  status: string
  source: 'customer' | 'agent' | 'ai' | 'system'
  private?: boolean
  media?: UiMedia | null
  /** Which provider account carried this message (sender number for outbound). */
  account_id?: string | null
  account_name?: string
}

interface UiAgent {
  username: string
  name: string
  role: string
}

interface CannedResponse {
  id: number
  short_code: string
  content: string
  /** Content with {{vars}} filled from the linked contact (raw when unlinked). */
  rendered: string
  created_by: string
}

/** Lightweight row for the "Link to a contact" picker — GET /api/messaging/contacts. */
interface LinkContact {
  id: number
  name: string
  phone: string
  email: string
}

interface ThreadSummary {
  summary: string
  heuristic: boolean
  message_count: number
  as_of: string
  generated_at: string
}

interface SummaryState {
  summary: ThreadSummary | null
  stale: boolean
  message_count: number
}

/** One contact's messages merged across every linked channel. */
interface CombinedConversation {
  contact: LinkContact
  channels: { channel: Channel; key: string; name: string }[]
  messages: (UiMessage & { channel: Channel; key: string })[]
}

interface WaCall {
  permission: string
  call_id: string | null
  status: string
}

interface WaTemplate {
  name: string
  language: string
  category: string
  status: string
  body: string
  variables: number
  bindings?: string[]
  resolved?: string[] // bound slots pre-filled from the thread's CRM contact
}

interface UiThreadSummary {
  channel: Channel
  key: string
  name: string
  sub: string
  ai_enabled: boolean
  call?: WaCall
  /** Chatwoot-style conversation management (messaging channels only). */
  status?: ThreadStatus
  assignee?: string
  team_id?: number | null
  labels?: string[]
  /** CRM identity link — same contact across channels (all channels). */
  contact_id?: number | null
  contact_name?: string
  /** WhatsApp only (additive backend fields — absent on other channels). */
  account_id?: string | null
  provider?: 'cloud' | 'baileys'
  window_open?: boolean
  window_expires_at?: string | null
  opted_out?: boolean
  last_message: string
  /** direction + delivery state of the newest message — the list shows
   *  WhatsApp-style ticks when it's ours (out). */
  last_dir?: string
  last_status?: string
  last_ts: string
  /** Synthetic row for a contact with NO conversation yet — surfaced so every
   *  contact appears in the inbox. Opening one creates the thread on demand. */
  pending?: boolean
  /** Set when this row stands in for several per-channel threads of the same
   *  linked contact, collapsed into one inbox entry — see dedupedRows. */
  _mergedChannels?: Channel[]
}

interface UiThread extends Omit<UiThreadSummary, 'last_message' | 'last_ts'> {
  messages: UiMessage[]
}

interface InboxStatus {
  whatsapp_sandbox: boolean
  email_configured: boolean
  imap_configured: boolean
  facebook_configured: boolean
  instagram_configured: boolean
  telegram_configured: boolean
  ai_available: boolean
}

async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}/${path}`, {
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
    ...init,
  })
  const data = await res.json().catch(() => ({}))
  if (expireSessionOn401(res)) throw new Error('Session expired — redirecting to login…')
  if (!res.ok) throw new Error((data as { detail?: string }).detail || `HTTP ${res.status}`)
  return data as T
}

/** Multipart POST — no Content-Type header so the browser sets the boundary. */
async function apiUpload<T>(path: string, form: FormData): Promise<T> {
  const res = await fetch(`${API}/${path}`, { method: 'POST', headers: { ...authHeaders() }, body: form })
  const data = await res.json().catch(() => ({}))
  if (expireSessionOn401(res)) throw new Error('Session expired — redirecting to login…')
  if (!res.ok) throw new Error((data as { detail?: string }).detail || `HTTP ${res.status}`)
  return data as T
}

/** Media URLs are authenticated GETs — absolute, host-relative or api-relative. */
const resolveMediaUrl = (u: string) =>
  u.startsWith('http') ? u : u.startsWith('/') ? `${API.replace(/\/api$/, '')}${u}` : `${API}/${u}`

/** Per-thread API prefix — WhatsApp lives in its own app. */
const threadPath = (t: { channel: Channel; key: string }) =>
  t.channel === 'whatsapp' ? `whatsapp/threads/${t.key}` : `messaging/threads/${t.channel}/${t.key}`

const timeShort = (ts: string) =>
  ts ? new Date(ts).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) : ''

const timeAgo = (ts: string) => {
  const mins = Math.max(0, Math.round((Date.now() - new Date(ts).getTime()) / 60000))
  if (mins < 1) return 'just now'
  if (mins < 60) return `${mins}m ago`
  const hours = Math.round(mins / 60)
  return hours < 24 ? `${hours}h ago` : `${Math.round(hours / 24)}d ago`
}

const dayLabel = (ts: string) => {
  const d = new Date(ts)
  const today = new Date()
  const yest = new Date(today)
  yest.setDate(today.getDate() - 1)
  if (d.toDateString() === today.toDateString()) return 'Today'
  if (d.toDateString() === yest.toDateString()) return 'Yesterday'
  return d.toLocaleDateString([], { day: 'numeric', month: 'short', year: 'numeric' })
}

/** Deterministic gradient per contact (Whatomate's getAvatarGradient pattern). */
function avatarClass(name: string): string {
  let h = 0
  for (let i = 0; i < name.length; i++) h = (h * 31 + name.charCodeAt(i)) | 0
  return `wa-grad-${Math.abs(h) % 6}`
}

const initials = (name: string) =>
  name
    .replace(/[^\p{L}\p{N} +]/gu, '')
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((w) => w[0]?.toUpperCase() ?? '')
    .join('') || '#'

/** Which channel a bubble came from — shown in the combined per-contact view. */
function ChannelBadge({ channel }: { channel: Channel }) {
  const meta = CHANNEL_META[channel]
  return (
    <span
      className="mb-0.5 flex w-fit items-center gap-1 rounded-full px-1.5 py-px text-[8.5px] font-bold uppercase tracking-wide"
      style={{ background: `${meta.color}26`, color: meta.color }}
    >
      <meta.Icon size={9} aria-hidden /> {meta.lbl}
    </span>
  )
}

function StatusTick({ status }: { status: string }) {
  if (status === 'read') return <CheckCheck size={13} className="wa-tick-read" aria-label="Read" />
  if (status === 'delivered') return <CheckCheck size={13} aria-label="Delivered" />
  if (status === 'failed') return <AlertCircle size={12} className="wa-tick-failed" aria-label="Failed" />
  if (status === 'sandbox') return <Clock size={11} aria-label="Sandbox — not sent" />
  return <Check size={13} aria-label="Sent" />
}

function Avatar({ name, channel, size = 36 }: { name: string; channel?: Channel; size?: number }) {
  const meta = channel ? CHANNEL_META[channel] : null
  return (
    <span className="relative inline-block shrink-0" style={{ width: size, height: size }}>
      <span
        className={`wa-avatar ${avatarClass(name)}`}
        style={{ width: size, height: size, fontSize: size * 0.34 }}
        aria-hidden
      >
        {initials(name)}
      </span>
      {meta && (
        <span
          className="absolute -bottom-0.5 -right-0.5 grid place-items-center rounded-full text-white"
          style={{ width: size * 0.42, height: size * 0.42, background: meta.color, boxShadow: '0 0 0 2px var(--card)' }}
          title={meta.lbl}
        >
          <meta.Icon size={size * 0.26} aria-hidden />
        </span>
      )}
    </span>
  )
}

const MEDIA_CHIP_ICONS: Record<Exclude<UiMedia['type'], 'image'>, LucideIcon> = {
  document: FileText,
  audio: Music,
  video: Film,
}

/** Call-recording audio player — the WAV endpoint is auth-gated, so fetch it as
 *  a blob with authHeaders and play via an object URL (same pattern as media). */
function CallRecordingPlayer({
  url,
  onTime,
  audioEl,
}: {
  url: string
  onTime?: (seconds: number) => void
  audioEl?: React.MutableRefObject<HTMLAudioElement | null>
}) {
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

  if (failed) return <span className="text-[11px] text-text-faint">recording unavailable</span>
  if (!src)
    return (
      <span className="flex items-center gap-1.5 text-[11px] text-text-faint">
        <Loader2 size={12} className="animate-spin" aria-hidden /> loading…
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

/** Call-transcript timeline entry — collapsed to a chip by default; the full
 *  turns only render when the operator clicks to expand it. */
function CallTranscriptChip({ text, time }: { text: string; time: string }) {
  const [open, setOpen] = useState(false)
  const lines = text.split('\n').slice(1).filter((l) => l.trim()) // drop the header line
  return (
    <div className="my-1.5 flex flex-col items-center">
      <button type="button" onClick={() => setOpen(!open)} className="wa-system-chip cursor-pointer hover:opacity-80">
        📞 Call transcript · {lines.length} lines · {open ? 'hide' : 'view'} · {time}
      </button>
      {open && (
        <div className="mt-1 max-w-[85%] space-y-0.5 rounded-lg border border-line bg-fill p-2 text-left text-[12px] leading-snug">
          {lines.map((ln, i) => (
            <div key={i} className={ln.startsWith('AI:') ? 'text-brand-200' : 'text-text-mid'}>
              {ln}
            </div>
          ))}
        </div>
      )}
    </div>
  )
}

/** Human label for the persisted end-cause codes. */
const endedByLabel = (v: string) =>
  v === 'agent'
    ? 'AI agent (wrapped up)'
    : v === 'contact'
      ? 'contact hung up'
      : v === 'system:silence'
        ? 'auto — silent line'
        : v === 'system:no_answer'
          ? 'auto — no response to greeting'
          : v === 'system:max_duration'
            ? 'auto — time limit'
            : v

interface CallRec {
  name: string
  url: string
  created: string
  /** ts = epoch seconds when the turn was logged (newer calls only). */
  transcript?: { role: string; text: string; ts?: number }[]
  meta?: { initiated_by?: string; ended_by?: string; account?: string; rec_started_at?: number }
}

/** Recording start in epoch ms. Preferred: meta.rec_started_at — the moment
 *  the gateway paced its FIRST audio chunk (the WAV's true t=0). Fallbacks:
 *  the filename ms ({wa_id}-{ms}.wav = call construction, which LEADS the
 *  recording by the ring time), then the created date. */
function recStartMs(rec: CallRec): number {
  if (rec.meta?.rec_started_at) return rec.meta.rec_started_at
  const stem = rec.name.replace(/\.[^.]+$/, '')
  const ms = Number(stem.split('-').pop())
  return Number.isFinite(ms) && ms > 1e12 ? ms : Date.parse(rec.created)
}

const turnClock = (ts: number) =>
  new Date(ts * 1000).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' })

/** A turn's position in the recording (seconds), corrected for WHEN each side
 *  is logged: assistant text at generation (audio follows ~1s later with the
 *  streaming TTS), user text at transcription-final (~2.5s after they started
 *  speaking — VAD tail + STT). Calibrated against a real recording
 *  (2026-07-21); rough constants, but they pull the highlight/seek target
 *  onto the right utterance. */
function turnOffsetSec(t: { role: string; ts?: number }, startMs: number): number | null {
  if (!t.ts) return null
  const raw = (t.ts * 1000 - startMs) / 1000 + (t.role === 'assistant' ? 1.0 : -2.5)
  return Math.max(0, raw)
}

/** One past call: the recording player plus its transcript (hidden until the
 *  operator clicks). withTranscript=false renders the player only — the
 *  sidebar keeps recordings, the transcript lives IN the chat timeline. */
function CallEntry({ rec, withTranscript = true }: { rec: CallRec; withTranscript?: boolean }) {
  const [showT, setShowT] = useState(false)
  const [pos, setPos] = useState(-1) // playback position (s); -1 = not playing
  const activeLineRef = useRef<HTMLDivElement | null>(null)
  const audioRef = useRef<HTMLAudioElement | null>(null)
  const turns = withTranscript ? (rec.transcript ?? []) : []

  // Which turn playback has reached: the last turn whose corrected offset is
  // <= the play position (see turnOffsetSec for the per-role correction).
  const startMs = recStartMs(rec)
  const activeIdx =
    pos < 0
      ? -1
      : turns.reduce((acc, t, i) => {
          const off = turnOffsetSec(t, startMs)
          return off !== null && off <= pos ? i : acc
        }, -1)

  useEffect(() => {
    activeLineRef.current?.scrollIntoView({ block: 'nearest', behavior: 'smooth' })
  }, [activeIdx])

  // Click a line -> jump playback there and play, so a long call can be
  // reviewed from any point of the conversation directly.
  const seekTo = (t: { role: string; ts?: number }) => {
    const off = turnOffsetSec(t, startMs)
    const audio = audioRef.current
    if (off === null || !audio) return
    audio.currentTime = off
    setPos(off) // highlight immediately, before the first timeupdate fires
    void audio.play().catch(() => {})
  }
  return (
    <div className="rounded-lg border border-line bg-fill p-2">
      {(rec.meta?.initiated_by || rec.meta?.ended_by) && (
        <div className="mb-1 text-[10px] leading-snug text-text-faint">
          {rec.meta?.initiated_by && (
            <>
              Called by <span className="font-semibold text-text-muted">{rec.meta.initiated_by}</span>
              {rec.meta?.account ? ` via ${rec.meta.account}` : ''}
            </>
          )}
          {rec.meta?.initiated_by && rec.meta?.ended_by && ' · '}
          {rec.meta?.ended_by && (
            <>
              Ended by <span className="font-semibold text-text-muted">{endedByLabel(rec.meta.ended_by)}</span>
            </>
          )}
        </div>
      )}
      <div className="mb-1 flex items-center justify-between gap-2">
        <span className="text-[10.5px] text-text-muted">{new Date(rec.created).toLocaleString()}</span>
        {turns.length > 0 && (
          <button
            type="button"
            onClick={() => setShowT(!showT)}
            className="shrink-0 text-[10.5px] font-semibold text-brand-300 hover:underline"
          >
            {showT ? 'Hide transcript' : `Transcript (${turns.length})`}
          </button>
        )}
      </div>
      <CallRecordingPlayer
        url={rec.url}
        audioEl={audioRef}
        onTime={turns.some((t) => t.ts) ? (s) => setPos(s) : undefined}
      />
      {showT && turns.length > 0 && (
        <div className="mt-1.5 max-h-48 space-y-0.5 overflow-y-auto rounded-md border border-line bg-app/40 p-2 text-[12px] leading-snug">
          {turns.map((t, i) => (
            <div
              key={i}
              ref={i === activeIdx ? activeLineRef : undefined}
              onClick={t.ts ? () => seekTo(t) : undefined}
              title={t.ts ? 'Play from here' : undefined}
              className={`flex gap-1.5 rounded px-1 py-0.5 transition-colors ${
                i === activeIdx ? 'bg-brand-400/15' : ''
              } ${t.ts ? 'cursor-pointer hover:bg-fill-hover' : ''} ${
                t.role === 'assistant' ? 'text-brand-200' : 'text-text-mid'
              }`}
            >
              {t.ts && (
                <span className="shrink-0 pt-px font-mono text-[10px] text-text-faint">
                  {turnClock(t.ts)}
                </span>
              )}
              <span>
                <span className="font-semibold">{t.role === 'assistant' ? 'AI' : 'Customer'}:</span>{' '}
                {t.text}
              </span>
            </div>
          ))}
        </div>
      )}
    </div>
  )
}

interface SipCall {
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

/** A past SIP phone call rendered inline in the message stream — chip + summary
 *  + recording player (calls placed after the recorder shipped) + expandable
 *  transcript. */
function SipCallCard({ rec }: { rec: SipCall }) {
  const [open, setOpen] = useState(false)
  return (
    <div className="my-2 flex justify-center">
      <div className="w-[85%] max-w-[440px]">
        <div className="mb-1 text-center">
          <span className="wa-system-chip">
            📞 AI voice call · {rec.outcome.replace(/_/g, ' ')} · {timeShort(rec.ended_at || rec.started_at)}
          </span>
        </div>
        <div className="rounded-lg border border-line bg-fill p-2">
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
              {open ? 'Hide transcript' : `Transcript (${rec.transcript.length})`}
            </button>
          )}
          {open && (
            <div className="mt-1.5 max-h-56 space-y-0.5 overflow-y-auto rounded-md border border-line bg-app/40 p-2 text-[12px] leading-snug">
              {rec.transcript.map((t, i) => (
                <div key={i} className={`flex gap-1.5 ${t.role === 'assistant' ? 'text-brand-200' : 'text-text-mid'}`}>
                  {t.ts && (
                    <span className="shrink-0 pt-px font-mono text-[10px] text-text-faint">{turnClock(t.ts)}</span>
                  )}
                  <span>
                    <span className="font-semibold">{t.role === 'assistant' ? 'AI' : 'Caller'}:</span> {t.text}
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

/** Bubble attachment — the media URL needs the bearer token, so fetch as a
 *  blob with authHeaders and render/open via an object URL. */
function MediaAttachment({ media }: { media: UiMedia }) {
  const [url, setUrl] = useState<string | null>(null)
  const [failed, setFailed] = useState(false)

  useEffect(() => {
    let cancelled = false
    let objectUrl: string | null = null
    setUrl(null)
    setFailed(false)
    fetch(resolveMediaUrl(media.url), { headers: authHeaders() })
      .then((r) => {
        if (!r.ok) throw new Error(`HTTP ${r.status}`)
        return r.blob()
      })
      .then((blob) => {
        if (cancelled) return
        objectUrl = URL.createObjectURL(blob)
        setUrl(objectUrl)
      })
      .catch(() => {
        if (!cancelled) setFailed(true)
      })
    return () => {
      cancelled = true
      if (objectUrl) URL.revokeObjectURL(objectUrl)
    }
  }, [media.url])

  if (media.type === 'image' && !failed) {
    return url ? (
      <a href={url} target="_blank" rel="noreferrer" title="Open full size" className="mb-1 block">
        {/* object URL — next/image can't optimize blobs */}
        {/* eslint-disable-next-line @next/next/no-img-element */}
        <img src={url} alt="Image attachment" className="block max-h-64 max-w-full rounded-lg object-cover" />
      </a>
    ) : (
      <span className="mb-1 grid h-28 w-44 place-items-center rounded-lg bg-black/15">
        <Loader2 size={15} className="animate-spin opacity-60" aria-hidden />
      </span>
    )
  }

  const ChipIcon = media.type === 'image' ? FileText : MEDIA_CHIP_ICONS[media.type]
  return (
    <span className="mb-1 flex items-center gap-2 rounded-lg bg-black/15 px-2.5 py-1.5">
      <ChipIcon size={14} className="shrink-0 opacity-75" aria-hidden />
      <span className="text-[11.5px] font-semibold capitalize opacity-90">{media.type}</span>
      {url ? (
        <a
          href={url}
          target="_blank"
          rel="noreferrer"
          className="ml-1 rounded bg-black/20 px-1.5 py-px text-[10.5px] font-bold uppercase tracking-wide underline-offset-2 hover:underline"
        >
          Open
        </a>
      ) : failed ? (
        <span className="text-[10.5px] opacity-60">unavailable</span>
      ) : (
        <Loader2 size={12} className="animate-spin opacity-60" aria-hidden />
      )}
    </span>
  )
}

/** Fill {{1}}…{{N}} with values; unfilled slots keep their placeholder. */
const renderTemplateBody = (body: string, vars: string[]) =>
  body.replace(/\{\{(\d+)\}\}/g, (raw, n) => vars[Number(n) - 1]?.trim() || raw)

/** Template picker — approved templates re-open a closed 24h window. */
function TemplatePickerModal({
  thread,
  onClose,
  onSent,
}: {
  thread: { key: string; account_id?: string | null }
  onClose: () => void
  onSent: (m: UiMessage) => void
}) {
  const [data, setData] = useState<{ templates: WaTemplate[]; sandbox: boolean } | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [sel, setSel] = useState<WaTemplate | null>(null)
  const [vars, setVars] = useState<string[]>([])
  const [sending, setSending] = useState(false)
  const [sendError, setSendError] = useState<string | null>(null)

  useEffect(() => {
    // wa_id lets the backend resolve bound {{n}} slots from this thread's
    // CRM contact, so the inputs arrive pre-filled.
    const params = new URLSearchParams({ wa_id: thread.key })
    if (thread.account_id) params.set('account_id', thread.account_id)
    api<{ templates: WaTemplate[]; sandbox: boolean }>(`whatsapp/templates?${params}`)
      .then(setData)
      .catch((e) => setLoadError(e instanceof Error ? e.message : String(e)))
  }, [thread.account_id, thread.key])

  const pick = (t: WaTemplate) => {
    setSel(t)
    setVars(Array.from({ length: t.variables }, (_, i) => t.resolved?.[i] || ''))
    setSendError(null)
  }

  const send = async () => {
    if (!sel) return
    setSending(true)
    setSendError(null)
    try {
      const r = await api<{ message: UiMessage }>(`whatsapp/threads/${thread.key}/send-template`, {
        method: 'POST',
        body: JSON.stringify({ name: sel.name, language: sel.language, variables: vars }),
      })
      onSent(r.message)
      onClose()
    } catch (e) {
      setSendError(e instanceof Error ? e.message : String(e))
    } finally {
      setSending(false)
    }
  }

  const canSend = !!sel && vars.every((v) => v.trim()) && !sending

  return (
    <div
      onClick={onClose}
      className="fixed inset-0 z-50 flex animate-fadeIn items-center justify-center p-6 backdrop-blur-sm"
      style={{ background: 'rgba(4,8,18,0.72)' }}
    >
      <div
        onClick={(e) => e.stopPropagation()}
        className="flex max-h-[80vh] w-full max-w-[520px] flex-col overflow-hidden rounded-2xl border border-line-strong bg-surface"
        style={{ boxShadow: '0 24px 60px rgba(0,0,0,0.5)' }}
      >
        <div className="flex items-center gap-2.5 border-b border-line px-5 py-4">
          {sel && (
            <button
              onClick={() => setSel(null)}
              title="Back to templates"
              aria-label="Back to template list"
              className="grid h-[30px] w-[30px] shrink-0 cursor-pointer place-items-center rounded-lg border border-line bg-fill text-text-muted hover:text-text-high"
            >
              <ChevronLeft size={15} aria-hidden />
            </button>
          )}
          <div className="min-w-0 flex-1">
            <div className="text-[14px] font-extrabold tracking-[-0.3px] text-text-primary">
              {sel ? sel.name : 'Send a template'}
            </div>
            <div className="mt-0.5 text-[11px] text-text-muted">
              {sel
                ? `${sel.language} · ${sel.category.toLowerCase()}${sel.variables ? ` · ${sel.variables} variable${sel.variables > 1 ? 's' : ''}` : ''}`
                : 'Approved templates can re-open a closed 24h conversation window.'}
            </div>
          </div>
          <button
            onClick={onClose}
            title="Close"
            aria-label="Close template picker"
            className="grid h-[30px] w-[30px] shrink-0 cursor-pointer place-items-center rounded-lg border border-line bg-fill text-text-muted hover:text-text-high"
          >
            <X size={15} aria-hidden />
          </button>
        </div>

        <div className="min-h-0 flex-1 overflow-y-auto">
          {!data && !loadError && (
            <div className="flex items-center justify-center gap-2 py-10 text-[12px] text-text-muted">
              <Loader2 size={14} className="animate-spin" aria-hidden /> Loading templates…
            </div>
          )}
          {loadError && <div className="px-5 py-8 text-center text-[12px] text-red-300">{loadError}</div>}
          {data && !sel && (
            <div className="py-1.5">
              {data.templates.length === 0 && (
                <div className="px-5 py-8 text-center text-[12px] text-text-muted">
                  No templates on this account — create one in Settings → Message Templates (live templates need Meta approval).
                </div>
              )}
              {data.templates.map((t) => (
                <button
                  key={`${t.name}-${t.language}`}
                  onClick={() => pick(t)}
                  className="block w-full cursor-pointer px-5 py-2.5 text-left transition-colors hover:bg-fill"
                >
                  <span className="flex items-center gap-1.5">
                    <span className="truncate text-[12.5px] font-bold text-text-high">{t.name}</span>
                    <span className="shrink-0 rounded-full bg-fill px-1.5 py-px text-[9px] font-bold uppercase text-text-muted">
                      {t.category}
                    </span>
                    <span
                      className={`shrink-0 rounded-full px-1.5 py-px text-[9px] font-bold uppercase ${
                        t.status.toUpperCase() === 'APPROVED' ? 'bg-green/15 text-green-300' : 'bg-amber/15 text-amber-300'
                      }`}
                    >
                      {t.status}
                    </span>
                    {data.sandbox && (
                      <span className="shrink-0 rounded-full bg-amber/15 px-1.5 py-px text-[9px] font-bold uppercase text-amber-300">
                        sandbox
                      </span>
                    )}
                  </span>
                  <span className="mt-0.5 block truncate text-[11.5px] text-text-muted">{t.body}</span>
                </button>
              ))}
            </div>
          )}
          {data && sel && (
            <div className="flex flex-col gap-3 px-5 py-4">
              {vars.map((v, i) => {
                const bound = !!sel.bindings?.[i]
                return (
                  <label key={i} className="block">
                    <span className="mb-1 flex items-center gap-1.5 font-mono text-[11px] font-bold text-text-high">
                      {`{{${i + 1}}}`}
                      {bound && (
                        <span
                          className="rounded-full px-1.5 py-px font-sans text-[9px] font-bold uppercase"
                          style={{ color: 'var(--brand-ink)', background: 'rgba(59,130,246,0.1)' }}
                          title="Filled from the contact record — edit if needed"
                        >
                          auto-filled
                        </span>
                      )}
                    </span>
                    <input
                      value={v}
                      onChange={(e) => setVars(vars.map((x, j) => (j === i ? e.target.value : x)))}
                      placeholder={`Value for {{${i + 1}}}`}
                      className="w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[12.5px] text-text-primary outline-none transition-colors focus:border-brand-400"
                    />
                  </label>
                )
              })}
              <div>
                <div className="mb-1.5 text-[10.5px] font-bold uppercase tracking-[0.08em] text-text-muted">Preview</div>
                <div className="whitespace-pre-wrap rounded-lg border border-line bg-fill px-3 py-2.5 text-[12.5px] leading-relaxed text-text-primary">
                  {renderTemplateBody(sel.body, vars)}
                </div>
              </div>
              {sendError && <div className="text-[11.5px] font-semibold text-red-300">{sendError}</div>}
            </div>
          )}
        </div>

        {data && sel && (
          <div className="flex items-center justify-end gap-2 border-t border-line px-5 py-3.5">
            <Button variant="ghost" onClick={onClose} disabled={sending}>
              Cancel
            </Button>
            <Button variant="primary" onClick={send} disabled={!canSend} style={{ opacity: canSend ? 1 : 0.5 }}>
              {sending ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Send size={13} aria-hidden />} Send
              template
            </Button>
          </div>
        )}
      </div>
    </div>
  )
}

export default function ChatsPage() {
  const { contacts, calls, hangupCall, sendSay: sendSipSay, escalateCall: escalateSipCall, setHumanControl } = useApp()
  const [status, setStatus] = useState<InboxStatus | null>(null)
  const [threads, setThreads] = useState<UiThreadSummary[]>([])
  const [active, setActive] = useState<UiThread | null>(null)
  const [activeId, setActiveId] = useState<{ channel: Channel; key: string } | null>(null)
  // Which entity scope the loaded inbox belongs to. Without it, the
  // out-of-scope check below would fire against the PREVIOUS outlet's list
  // while the new one is still in flight and close a perfectly valid chat.
  const [loadedScope, setLoadedScope] = useState<string | null>(null)
  const [filter, setFilter] = useState<'all' | Channel>('all')
  const [statusFilter, setStatusFilter] = useState<'all' | ThreadStatus>('all')
  const [assigneeFilter, setAssigneeFilter] = useState('all') // 'all' | 'none' | username
  // Teams own a conversation before any one agent does — the picker below the
  // assignee routes it, optionally rotating to the next member.
  const [teams, setTeams] = useState<{ id: number; name: string; auto_assign: boolean }[]>([])
  const [aiFilter, setAiFilter] = useState<'all' | 'on' | 'off'>('all')
  const [labelFilter, setLabelFilter] = useState('all')
  const [search, setSearch] = useState('')
  const [draft, setDraft] = useState('')
  const [composerMode, setComposerMode] = useState<'reply' | 'note'>('reply')
  const [agents, setAgents] = useState<UiAgent[]>([])
  const [canned, setCanned] = useState<CannedResponse[]>([])
  const [linkContacts, setLinkContacts] = useState<LinkContact[]>([])
  const [linkSel, setLinkSel] = useState('')
  const [combinedOn, setCombinedOn] = useState(false)
  // "Voice" chip in the channel toggle: filters the merged timeline down to
  // just call history (any transport), instead of switching/dialing.
  const [voiceOnly, setVoiceOnly] = useState(false)
  const [combined, setCombined] = useState<CombinedConversation | null>(null)
  const [summaryState, setSummaryState] = useState<SummaryState | null>(null)
  // Which thread's summary is generating, keyed by API path — NOT a bare
  // boolean: switching chats mid-generation used to skip the reset (the
  // cancelled guard in finally) and wedge "updating…" on every thread with
  // the refresh button disabled for good.
  const [summarizingFor, setSummarizingFor] = useState<string | null>(null)
  const summaryPathRef = useRef<string | null>(null)
  const summarizing =
    summarizingFor !== null && activeId !== null &&
    summarizingFor === `messaging/threads/${activeId.channel}/${activeId.key}/summary`
  const [summaryOpen, setSummaryOpen] = useState(true)
  const lastAutoGen = useRef(0)
  const [infoOpen, setInfoOpen] = useState(true)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [pendingFile, setPendingFile] = useState<File | null>(null)
  const [tplOpen, setTplOpen] = useState(false)
  const [endedFlash, setEndedFlash] = useState(false)
  const [callLive, setCallLive] = useState<{ turns: { role: string; text: string }[]; done: boolean }>({ turns: [], done: false })
  const [sayDraft, setSayDraft] = useState('')
  const [sipSayDraft, setSipSayDraft] = useState('')
  // optimistic "Calling…" — flips true the instant Start is clicked, before the
  // (slow) call/start request returns, so the button gives immediate feedback
  const [starting, setStarting] = useState(false)
  const [callRecordings, setCallRecordings] = useState<
    { name: string; url: string; created: string; size: number; transcript?: { role: string; text: string }[]; meta?: { initiated_by?: string; ended_by?: string; account?: string } }[]
  >([])
  // Past AI voice calls for the open thread's contact (data/calls.json, any
  // transport) — interleaved into the "All channels" merged timeline only.
  const [sipCalls, setSipCalls] = useState<SipCall[]>([])
  // '' = native realtime voice; otherwise a dashboard ElevenLabs voice id
  const [callVoice, setCallVoice] = useState('')
  // sending-account switcher: which of our WhatsApp numbers carries this chat
  const [waAccounts, setWaAccounts] = useState<{ id: string; name: string; provider?: string }[]>([])
  const [canManage, setCanManage] = useState(false)
  const scrollRef = useRef<HTMLDivElement>(null)
  const callScrollRef = useRef<HTMLDivElement>(null)
  const fileRef = useRef<HTMLInputElement>(null)
  const prevCallStatus = useRef<string | undefined>(undefined)

  // Entity scope from the topbar switcher — the inbox shows the selected
  // outlet's conversations (plus untagged ones on shared numbers, which
  // somebody still has to answer).
  const entityQ = useEntityQuery()

  const refreshThreads = useCallback(async () => {
    try {
      const [wa, msg] = await Promise.all([
        api<{ threads: { wa_id: string; phone: string; name: string; ai_enabled: boolean; assignee?: string; call: WaCall; account_id?: string | null; window_open?: boolean; window_expires_at?: string | null; opted_out?: boolean; contact_id?: number | null; contact_name?: string; labels?: string[]; last_message: string; last_ts: string }[] }>(`whatsapp/threads${entityQ && `?${entityQ}`}`),
        api<{ threads: { key: string; channel: Channel; address: string; name: string; ai_enabled: boolean; status: ThreadStatus; assignee: string; labels: string[]; contact_id?: number | null; contact_name?: string; last_message: string; last_ts: string }[] }>(`messaging/threads${entityQ && `?${entityQ}`}`),
      ])
      const merged: UiThreadSummary[] = [
        ...wa.threads.map((t) => ({
          channel: 'whatsapp' as Channel,
          key: t.wa_id,
          // prefer the CRM name — a thread created by phone (dial / pending
          // contact row) stores the number as its name, but it's matched to a
          // contact, so show their real name in the list + header
          name: t.contact_name || t.name,
          sub: t.phone,
          ai_enabled: t.ai_enabled,
          assignee: t.assignee,
          call: t.call,
          account_id: t.account_id,
          window_open: t.window_open,
          window_expires_at: t.window_expires_at,
          opted_out: t.opted_out,
          contact_id: t.contact_id,
          contact_name: t.contact_name,
          labels: t.labels,
          last_message: t.last_message,
          last_dir: (t as { last_dir?: string }).last_dir,
          last_status: (t as { last_status?: string }).last_status,
          last_ts: t.last_ts,
        })),
        ...msg.threads.map((t) => ({
          channel: t.channel,
          key: t.key,
          name: t.contact_name || t.name,
          sub: t.address,
          ai_enabled: t.ai_enabled,
          status: t.status,
          assignee: t.assignee,
          labels: t.labels,
          contact_id: t.contact_id,
          contact_name: t.contact_name,
          last_message: t.last_message,
          last_ts: t.last_ts,
        })),
      ].sort((a, b) => (b.last_ts || '').localeCompare(a.last_ts || ''))
      setThreads(merged)
      setLoadedScope(entityQ)
    } catch {
      /* API down — page shows the empty state */
    }
    // entityQ is a dependency so switching outlets re-fetches the inbox
  }, [entityQ])

  const refreshActive = useCallback(async () => {
    // Deselecting must also drop the loaded conversation. `active` is what the
    // right-hand pane renders from, so leaving it behind keeps a thread on
    // screen that the current entity filter no longer contains.
    if (!activeId) {
      setActive(null)
      return
    }
    try {
      if (activeId.channel === 'whatsapp') {
        const t = await api<{ wa_id: string; phone: string; name: string; ai_enabled: boolean; assignee?: string; call: WaCall; account_id?: string | null; window_open?: boolean; window_expires_at?: string | null; opted_out?: boolean; contact_id?: number | null; contact_name?: string; labels?: string[]; messages: UiMessage[] }>(
          `whatsapp/threads/${activeId.key}`,
        )
        setActive({
          channel: 'whatsapp',
          key: t.wa_id,
          name: t.contact_name || t.name,
          sub: t.phone,
          ai_enabled: t.ai_enabled,
          assignee: t.assignee,
          call: t.call,
          account_id: t.account_id,
          window_open: t.window_open,
          window_expires_at: t.window_expires_at,
          opted_out: t.opted_out,
          contact_id: t.contact_id,
          contact_name: t.contact_name,
          labels: t.labels,
          messages: t.messages,
        })
      } else {
        const t = await api<{ key: string; channel: Channel; address: string; name: string; ai_enabled: boolean; status: ThreadStatus; assignee: string; labels: string[]; contact_id?: number | null; contact_name?: string; messages: UiMessage[] }>(
          `messaging/threads/${activeId.channel}/${activeId.key}`,
        )
        setActive({
          channel: t.channel,
          key: t.key,
          name: t.contact_name || t.name,
          sub: t.address,
          ai_enabled: t.ai_enabled,
          status: t.status,
          assignee: t.assignee,
          labels: t.labels,
          contact_id: t.contact_id,
          contact_name: t.contact_name,
          messages: t.messages,
        })
      }
    } catch {
      /* keep last good state while polling */
    }
  }, [activeId])

  // deep link: /chats?channel=whatsapp&key=60123456789 opens that thread
  useEffect(() => {
    const params = new URLSearchParams(window.location.search)
    const channel = params.get('channel') as Channel | null
    const key = params.get('key')
    if (channel && key && CHANNELS.includes(channel)) {
      setActiveId({ channel, key })
    }
  }, [])

  useEffect(() => {
    Promise.all([
      api<{ sandbox: boolean; ai_available: boolean }>('whatsapp/status'),
      api<{ email_configured: boolean; imap_configured: boolean; facebook_configured: boolean; instagram_configured: boolean; telegram_configured: boolean }>('messaging/status'),
    ])
      .then(([wa, msg]) =>
        setStatus({
          whatsapp_sandbox: wa.sandbox,
          ai_available: wa.ai_available,
          email_configured: msg.email_configured,
          imap_configured: msg.imap_configured,
          facebook_configured: msg.facebook_configured,
          instagram_configured: msg.instagram_configured,
          telegram_configured: msg.telegram_configured,
        }),
      )
      .catch(() => setStatus(null))
    // assignee dropdown + canned-response picker data (static per visit)
    api<{ agents: UiAgent[] }>('messaging/agents')
      .then((r) => setAgents(r.agents))
      .catch(() => setAgents([]))
    api<{ contacts: LinkContact[] }>('messaging/contacts')
      .then((r) => setLinkContacts(r.contacts))
      .catch(() => setLinkContacts([]))
    refreshThreads()
  }, [refreshThreads])

  useEffect(() => {
    refreshActive()
  }, [refreshActive])

  // Push instead of poll: the backend's SSE stream says when anything in the
  // inbox changed, and only then do we re-fetch what's on screen. Refs keep
  // the single subscription alive across activeId changes.
  const refreshRef = useRef({ refreshThreads, refreshActive })
  refreshRef.current = { refreshThreads, refreshActive }
  useEffect(
    () =>
      subscribeChatStream(() => {
        refreshRef.current.refreshThreads()
        refreshRef.current.refreshActive()
      }),
    [],
  )

  // inbound email: nudge the IMAP sync while the inbox is open (server throttles)
  useEffect(() => {
    const sync = () => api('messaging/email/sync', { method: 'POST', body: '{}' }).catch(() => {})
    sync()
    const t = setInterval(sync, 60000)
    return () => clearInterval(t)
  }, [])

  // quick replies: re-render {{vars}} against the linked contact of the open chat
  const quickReplyContactId = active?.contact_id ?? null
  useEffect(() => {
    const qs = quickReplyContactId ? `?contact_id=${quickReplyContactId}` : ''
    api<{ canned: CannedResponse[] }>(`messaging/canned${qs}`)
      .then((r) => setCanned(r.canned))
      .catch(() => setCanned([]))
  }, [quickReplyContactId])

  // always-on chat summary: load the CACHED summary and only auto-generate the
  // first time (when none exists). Once cached it stays put — a "stale" flag
  // shows when it's behind, and the manual refresh button re-runs the AI. This
  // keeps AI usage low instead of re-summarizing on every new message.
  useEffect(() => {
    if (!activeId) {
      setSummaryState(null)
      summaryPathRef.current = null
      return
    }
    let cancelled = false
    lastAutoGen.current = 0
    const path = `messaging/threads/${activeId.channel}/${activeId.key}/summary`
    summaryPathRef.current = path
    const check = async () => {
      try {
        const s = await api<SummaryState>(path)
        if (cancelled) return
        setSummaryState(s)
        // Only auto-generate when there's NO cached summary yet. A stale (but
        // present) summary is kept as-is — the user refreshes it on demand, so
        // the AI isn't re-run on every new message.
        const needsRegen = s.message_count > 0 && !s.summary
        if (needsRegen && Date.now() - lastAutoGen.current > 300000) {
          lastAutoGen.current = Date.now()
          setSummarizingFor(path)
          try {
            const r = await api<SummaryState>(path, { method: 'POST', body: '{}' })
            if (!cancelled) setSummaryState(r)
          } catch {
            /* AI/provider hiccup — cached version stays */
          } finally {
            // clear OWN key only, unconditionally — even after a thread switch
            setSummarizingFor((v) => (v === path ? null : v))
          }
        }
      } catch {
        /* thread may not exist yet */
      }
    }
    check()
    // stale-ness only moves when messages do — ride the change stream
    const unsubscribe = subscribeChatStream(check)
    return () => {
      cancelled = true
      unsubscribe()
    }
  }, [activeId])

  const regenSummary = async () => {
    if (!activeId || summarizing) return
    const path = `messaging/threads/${activeId.channel}/${activeId.key}/summary`
    setSummarizingFor(path)
    try {
      const r = await api<SummaryState>(path, { method: 'POST', body: '{}' })
      // apply only if this thread is still the open one — a quick chat switch
      // mid-refresh must not stamp the old thread's summary onto the new chat
      if (summaryPathRef.current === path) setSummaryState(r)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setSummarizingFor((v) => (v === path ? null : v))
    }
  }

  // combined cross-channel view: poll the linked contact's merged conversation
  const activeContactId = active?.contact_id ?? null
  useEffect(() => {
    if (!activeContactId) {
      setCombined(null)
      return
    }
    let cancelled = false
    const load = () =>
      api<CombinedConversation>(`messaging/contacts/${activeContactId}/conversation`)
        .then((c) => {
          if (!cancelled) setCombined(c)
        })
        .catch(() => {})
    load()
    const t = setInterval(load, 2500)
    return () => {
      cancelled = true
      clearInterval(t)
    }
  }, [activeContactId])

  useEffect(() => {
    scrollRef.current?.scrollTo({ top: scrollRef.current.scrollHeight })
  }, [active?.messages.length, combinedOn, combined?.messages.length])

  // per-thread composer/call-pill state resets on switch
  useEffect(() => {
    setPendingFile(null)
    setTplOpen(false)
    setEndedFlash(false)
    setStarting(false)
    setComposerMode('reply')
    setLinkSel('')
    setCombinedOn(false)
    prevCallStatus.current = undefined
  }, [activeId?.channel, activeId?.key])

  // flash a "Call ended" pill for a few seconds after an active call drops
  useEffect(() => {
    const s = active?.call?.status
    const prev = prevCallStatus.current
    prevCallStatus.current = s
    if (prev && ['connecting', 'ringing', 'in_call'].includes(prev) && (!s || !['connecting', 'ringing', 'in_call'].includes(s))) {
      setEndedFlash(true)
      const t = setTimeout(() => setEndedFlash(false), 5000)
      return () => clearTimeout(t)
    }
  }, [active?.call?.status])

  // Poll the live transcript while a WhatsApp call is up — same file store the
  // /live-calls page reads, so the operator can watch the AI on a WhatsApp call.
  useEffect(() => {
    const status = active?.channel === 'whatsapp' ? active?.call?.status : undefined
    const key = active?.key
    if (!key || !status || !['connecting', 'ringing', 'in_call'].includes(status)) {
      setCallLive({ turns: [], done: false })
      return
    }
    let stopped = false
    const poll = async () => {
      try {
        const d = await api<{ turns: { role: string; text: string }[]; done: boolean }>(
          `whatsapp/threads/${key}/call/live`,
        )
        if (!stopped) setCallLive({ turns: d.turns ?? [], done: !!d.done })
      } catch {
        /* transient poll error — keep the last transcript */
      }
    }
    poll()
    const t = setInterval(poll, 1500)
    return () => {
      stopped = true
      clearInterval(t)
    }
  }, [active?.channel, active?.key, active?.call?.status])

  // keep the live transcript scrolled to the newest turn
  useEffect(() => {
    callScrollRef.current?.scrollTo({ top: callScrollRef.current.scrollHeight })
  }, [callLive.turns.length])

  // load call recordings for the active WhatsApp thread; refetch when a call's
  // status changes (a fresh recording appears after the call ends)
  useEffect(() => {
    if (active?.channel !== 'whatsapp' || !active?.key) {
      setCallRecordings([])
      return
    }
    let cancelled = false
    api<{ recordings: { name: string; url: string; created: string; size: number; transcript?: { role: string; text: string }[] }[] }>(
      `whatsapp/threads/${active.key}/call/recordings`,
    )
      .then((d) => {
        if (!cancelled) setCallRecordings(d.recordings || [])
      })
      .catch(() => {})
    return () => {
      cancelled = true
    }
  }, [active?.channel, active?.key, active?.call?.status])

  // Past SIP calls for the open contact — poll while the thread is open so a
  // call that ends (or is placed elsewhere) surfaces in the stream within ~8s.
  // AppContext owns live state; this is just the persisted history.
  const sipContactId = active?.contact_id ?? null
  useEffect(() => {
    if (sipContactId == null) {
      setSipCalls([])
      return
    }
    let cancelled = false
    const load = () =>
      api<{ calls: SipCall[] }>(`contacts/${sipContactId}/calls`)
        .then((d) => {
          if (!cancelled) setSipCalls(d.calls ?? [])
        })
        .catch(() => {})
    load()
    const t = setInterval(load, 8000)
    return () => {
      cancelled = true
      clearInterval(t)
    }
  }, [sipContactId])

  // WhatsApp calls use the agent voice last saved from the (now-removed)
  // in-panel picker, so an earlier pick still applies.
  useEffect(() => {
    try {
      const saved = localStorage.getItem('wa-call-voice')
      // ignore stale "ilmu:*" picks from before the picker was trimmed to
      // ElevenLabs + default (the brain choice moved to Settings → AI)
      if (saved && !saved.startsWith('ilmu:')) setCallVoice(saved)
    } catch {
      /* localStorage unavailable */
    }
  }, [])

  const run = async (fn: () => Promise<unknown>) => {
    setBusy(true)
    setError(null)
    try {
      await fn()
      await Promise.all([refreshActive(), refreshThreads()])
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  /** Append a just-sent message so it shows before the next poll. */
  const appendToActive = (m: UiMessage) =>
    setActive((prev) =>
      prev && !prev.messages.some((x) => x.id === m.id) ? { ...prev, messages: [...prev.messages, m] } : prev,
    )

  const sendMessage = () =>
    run(async () => {
      if (!active) return
      const text = draft.trim()
      const isWa = active.channel === 'whatsapp'
      if (!isWa && composerMode === 'note') {
        if (!text) return
        setDraft('')
        const r = await api<{ message: UiMessage }>(`${threadPath(active)}/note`, {
          method: 'POST',
          body: JSON.stringify({ text }),
        })
        appendToActive(r.message)
        return
      }
      if (isWa && pendingFile) {
        const form = new FormData()
        form.append('file', pendingFile)
        if (text) form.append('caption', text)
        const r = await apiUpload<{ message: UiMessage }>(`whatsapp/threads/${active.key}/send-media`, form)
        setPendingFile(null)
        setDraft('')
        appendToActive(r.message)
        return
      }
      if (!text) return
      const override = isWa && active.opted_out === true
      if (override && !window.confirm('Customer opted out — send anyway as a manual override?')) return
      setDraft('')
      await api(`${threadPath(active)}/send`, {
        method: 'POST',
        body: JSON.stringify(override ? { text, force: true } : { text }),
      })
    })

  const onPickFile = (e: ChangeEvent<HTMLInputElement>) => {
    const f = e.target.files?.[0]
    e.target.value = '' // allow re-picking the same file
    if (!f) return
    const ext = f.name.toLowerCase().split('.').pop() || ''
    if (!['jpg', 'jpeg', 'png', 'webp', 'pdf', 'mp4', 'ogg', 'mp3'].includes(ext)) {
      setError('Unsupported file type — use jpg, png, webp, pdf, mp4, ogg or mp3.')
      return
    }
    if (f.size > 16 * 1024 * 1024) {
      setError('File too large — WhatsApp media is limited to 16 MB.')
      return
    }
    setError(null)
    setPendingFile(f)
  }

  const toggleAi = () =>
    run(() =>
      active
        ? api(`${threadPath(active)}/ai`, { method: 'POST', body: JSON.stringify({ enabled: !active.ai_enabled }) })
        : Promise.resolve(),
    )

  // one-time: our WhatsApp account pool (for the sending-account switcher +
  // per-bubble "via" labels) and whether this operator may switch accounts
  useEffect(() => {
    setCanManage(getRole() !== 'agent')
    api<{ accounts: { id: string; name: string; provider?: string }[] }>('whatsapp/accounts')
      .then((r) => setWaAccounts((r.accounts || []).filter((a) => /^\d+$/.test(a.id))))
      .catch(() => {})
  }, [])

  const switchAccount = (accountId: string) =>
    run(() =>
      active && active.channel === 'whatsapp' && accountId
        ? api(`whatsapp/threads/${active.key}/account`, {
            method: 'POST',
            body: JSON.stringify({ account_id: accountId }),
          })
        : Promise.resolve(),
    )

  // Chatwoot-style conversation management (messaging channels only)
  const setThreadStatus = (s: ThreadStatus) =>
    run(() =>
      active
        ? api(`${threadPath(active)}/status`, { method: 'POST', body: JSON.stringify({ status: s }) })
        : Promise.resolve(),
    )

  useEffect(() => {
    api<{ teams: { id: number; name: string; auto_assign: boolean }[] }>(`teams${entityQ && `?${entityQ}`}`)
      .then((d) => setTeams(d.teams ?? []))
      .catch(() => setTeams([])) // the picker just hides when teams are unavailable
  }, [entityQ])

  /** Route this conversation to a team; "" clears it. */
  const assignTeam = (raw: string) =>
    run(async () => {
      if (!active) return
      await api('teams/assign', {
        method: 'POST',
        body: JSON.stringify({
          channel: active.channel,
          key: active.key,
          team_id: raw ? Number(raw) : 0,
          // rotate to the next member only when nobody is on it yet
          assign: !!raw && !active.assignee,
        }),
      })
      await refreshThreads()
    })

  const assignTo = (username: string) =>
    run(() =>
      active
        ? // always the messaging app: assignment is channel-agnostic (shared
          // crm.Thread), and the whatsapp app has no /assign endpoint
          api(`messaging/threads/${active.channel}/${active.key}/assign`, {
            method: 'POST',
            body: JSON.stringify({ assignee: username }),
          })
        : Promise.resolve(),
    )

  const saveLabels = (labels: string[]) =>
    run(() =>
      active
        ? api(`${threadPath(active)}/labels`, { method: 'POST', body: JSON.stringify({ labels }) })
        : Promise.resolve(),
    )

  // identity link — channel-agnostic, so it goes via the messaging app even for WhatsApp
  const linkContact = (contactId: number | null) =>
    run(() =>
      active
        ? api(`messaging/threads/${active.channel}/${active.key}/link`, {
            method: 'POST',
            body: JSON.stringify({ contact_id: contactId }),
          })
        : Promise.resolve(),
    )

  const startCall = async () => {
    setStarting(true) // instant feedback; cleared once the call status takes over
    try {
      await run(() =>
        api(`whatsapp/threads/${active?.key}/call/start`, {
          method: 'POST',
          body: JSON.stringify(callVoice ? { voice_provider: 'elevenlabs', voice_id: callVoice } : {}),
        }),
      )
      // pull the thread now — the backend wrote call.status before responding,
      // and waiting for the next SSE tick would blink the pill off for a beat
      await Promise.all([refreshActive(), refreshThreads()])
    } finally {
      setStarting(false)
    }
  }
  const endCall = () => run(() => api(`whatsapp/threads/${active?.key}/call/end`, { method: 'POST' }))
  const sendSay = (mode: 'guide' | 'takeover') => {
    const text = sayDraft.trim()
    if (!text) return
    run(async () => {
      await api(`whatsapp/threads/${active?.key}/call/say`, { method: 'POST', body: JSON.stringify({ mode, text }) })
      setSayDraft('')
    })
  }
  const escalateCall = () => run(() => api(`whatsapp/threads/${active?.key}/call/escalate`, { method: 'POST' }))

  const inCall = active?.channel === 'whatsapp' && ['in_call', 'ringing', 'connecting'].includes(active?.call?.status ?? '')
  // This contact's current SIP ("AI phone call") — the header VoiceCallButton
  // dials it; the live transcript renders in the middle chat column below.
  const sipCall = Object.values(calls)
    .filter((c) => c.contactId === active?.contact_id)
    .sort((a, b) => b.startedAt - a.startedAt)[0]
  const sipCallActive = !!sipCall && ['dialing', 'ringing', 'live'].includes(sipCall.status)
  const activeIsWa = active?.channel === 'whatsapp'
  const optedOut = activeIsWa && active?.opted_out === true

  // call-status pill for the thread header (connecting/ringing pulse; ended flashes
  // briefly). `starting` shows "Calling…" the instant the button is clicked — the
  // backend's connecting status takes over once the SSE refetch lands.
  const callStatus = activeIsWa ? active?.call?.status : undefined
  const callPill =
    (callStatus && ['connecting', 'ringing', 'in_call'].includes(callStatus)) || (activeIsWa && starting)
      ? {
          label:
            callStatus === 'in_call'
              ? 'On call'
              : callStatus === 'ringing'
                ? 'Ringing…'
                : callStatus === 'connecting'
                  ? 'Connecting…'
                  : 'Calling…',
          pulse: callStatus !== 'in_call',
          cls: 'bg-green/15 text-green-300',
        }
      : endedFlash
        ? { label: 'Call ended', pulse: false, cls: 'bg-fill text-text-muted' }
        : null

  const channelLive = (c: Channel) =>
    !status
      ? false
      : c === 'whatsapp'
        ? !status.whatsapp_sandbox
        : c === 'email'
          ? status.email_configured
          : c === 'facebook'
            ? status.facebook_configured
            : c === 'telegram'
              ? status.telegram_configured
              : status.instagram_configured

  // Channels worth SHOWING: linked/configured ones always; unlinked (sandbox)
  // channels only when they already hold conversations. A fresh install with
  // nothing configured falls back to all channels so a first chat can start.
  const visibleChannels = CHANNELS.filter((c) => channelLive(c) || threads.some((t) => t.channel === c))
  const channelOptions = visibleChannels.length > 0 ? visibleChannels : CHANNELS

  const activeSandbox = active ? !channelLive(active.channel) : false
  // 24h customer-service window (live WhatsApp accounts only — sandbox is always open)
  const windowClosed = activeIsWa && !activeSandbox && active?.window_open === false

  // Every contact appears in the inbox: real threads first, then a synthetic
  // "no conversation yet" row for each contact not yet covered by a thread
  // (matched by CRM id or WhatsApp phone key). Opening one creates the thread
  // on demand (see openRow) — surfaced as WhatsApp-by-phone, the same surface
  // the dial hook already uses for voice calls.
  const pendingRows: UiThreadSummary[] = (() => {
    const covered = new Set(threads.map((t) => t.contact_id).filter(Boolean) as number[])
    const waKeys = new Set(threads.filter((t) => t.channel === 'whatsapp').map((t) => t.key))
    const rows: UiThreadSummary[] = []
    for (const d of contacts) {
      const dig = (d.phone || '').replace(/\D/g, '')
      if (!dig || covered.has(d.id) || waKeys.has(dig)) continue
      rows.push({
        channel: 'whatsapp',
        key: dig,
        name: d.name,
        sub: d.phone,
        ai_enabled: false,
        contact_id: d.id,
        last_message: 'No conversation yet',
        last_ts: '',
        pending: true,
      })
    }
    return rows.sort((a, b) => a.name.localeCompare(b.name))
  })()
  const allRows = [...threads, ...pendingRows]

  // Switching outlets closes a conversation that is no longer in scope. The
  // list refetches correctly on its own, but the CONVERSATION PANE kept showing
  // the previous outlet's chat — so filtering to a branch with no contacts
  // still displayed someone else's conversation, header, contact card and all.
  // A stable key for "what is currently in the inbox", so the effect below
  // depends on the CONTENT rather than the array identity (rebuilt each render).
  const rowKeys = allRows.map((r) => `${r.channel}:${r.key}`).join('|')
  useEffect(() => {
    if (!activeId || loadedScope !== entityQ) return
    if (!rowKeys.split('|').includes(`${activeId.channel}:${activeId.key}`)) setActiveId(null)
  }, [activeId, rowKeys, loadedScope, entityQ])

  // Same linked contact across several channels is ONE inbox entry, not one
  // per channel — collapse by contact_id (only when not already filtered to
  // a single channel, since that view is explicitly per-channel). Picks the
  // most-recently-active row as the representative; unlinked rows pass through.
  const dedupedRows: UiThreadSummary[] = (() => {
    if (filter !== 'all') return allRows
    const groups = new Map<number, UiThreadSummary[]>()
    const singles: UiThreadSummary[] = []
    for (const t of allRows) {
      if (t.contact_id != null) {
        const g = groups.get(t.contact_id)
        if (g) g.push(t)
        else groups.set(t.contact_id, [t])
      } else {
        singles.push(t)
      }
    }
    const merged: UiThreadSummary[] = []
    for (const rows of groups.values()) {
      if (rows.length === 1) {
        merged.push(rows[0])
        continue
      }
      const top = [...rows].sort((a, b) => (b.last_ts || '').localeCompare(a.last_ts || ''))[0]
      merged.push({
        ...top,
        labels: Array.from(new Set(rows.flatMap((r) => r.labels ?? []))),
        _mergedChannels: Array.from(new Set(rows.map((r) => r.channel))),
      })
    }
    return [...merged, ...singles]
  })()

  const visible = dedupedRows.filter(
    (t) =>
      (filter === 'all' || t.channel === filter) &&
      // WhatsApp threads carry no status — treat them as always open
      (statusFilter === 'all' || (t.status ?? 'open') === statusFilter) &&
      (assigneeFilter === 'all' ||
        (assigneeFilter === 'none' ? !t.assignee : t.assignee === assigneeFilter)) &&
      (aiFilter === 'all' || (aiFilter === 'on') === !!t.ai_enabled) &&
      (labelFilter === 'all' || (t.labels ?? []).includes(labelFilter)) &&
      (!search.trim() || t.name.toLowerCase().includes(search.toLowerCase()) || t.sub.includes(search.trim())),
  )

  // keep the filter valid when unlinked channels disappear from the page
  useEffect(() => {
    if (filter !== 'all' && !channelOptions.includes(filter)) setFilter('all')
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [channelOptions.join(',')])

  // canned-response picker: type "/" (Chatwoot's shortcut) in reply mode
  const cannedQuery = composerMode === 'reply' && draft.startsWith('/') ? draft.slice(1).toLowerCase() : null
  const cannedMatches =
    cannedQuery === null
      ? []
      : canned.filter((c) => c.short_code.startsWith(cannedQuery) || c.content.toLowerCase().includes(cannedQuery)).slice(0, 6)

  // combined mode swaps the single thread for the contact's merged timeline
  type TimelineItem = UiMessage & {
    channel?: Channel
    key?: string
    /** A past voice call interleaved into the chat at its time — player +
     *  expandable transcript live IN the conversation, not the sidebar. */
    _callRec?: (typeof callRecordings)[number]
    /** A past SIP phone call (transcript-only, no served recording). */
    _sipCall?: SipCall
  }
  const baseMessages: TimelineItem[] = voiceOnly
    ? []
    : combinedOn && combined
      ? combined.messages
      : active?.messages ?? []
  const oldestLoadedTs = baseMessages[0]?.ts
  const fullyLoaded = baseMessages.length < 50 // page size — older pages exist past 50
  // Calls stay in the channel they happened on: the WhatsApp thread shows its
  // own recordings; every AI call (any transport) shows once in "All channels".
  // Other single-channel views (Telegram/email/…) carry only their own traffic.
  // The Voice filter pulls WhatsApp-transport recordings in regardless of view.
  const callItems: TimelineItem[] =
    voiceOnly || (!combinedOn && active?.channel === 'whatsapp')
      ? callRecordings
          .filter((r) => fullyLoaded || !oldestLoadedTs || r.created >= oldestLoadedTs)
          .map((r) => ({
            id: `call-${r.name}`,
            dir: 'out' as const,
            source: 'system' as const,
            text: '📞 Voice call',
            ts: r.created,
            status: 'sent',
            _callRec: r,
          }))
      : []
  const sipCallItems: TimelineItem[] = (combinedOn && sipContactId != null ? sipCalls : [])
    .map((r) => {
      const ts = r.ended_at || r.started_at
      return { id: `sip-${r.call_id}`, dir: 'out' as const, source: 'system' as const, text: '📞 AI voice call', ts, status: 'sent', _sipCall: r }
    })
    .filter((m) => fullyLoaded || !oldestLoadedTs || m.ts >= oldestLoadedTs)
  const extraItems = [...callItems, ...sipCallItems]
  const displayMessages: TimelineItem[] = extraItems.length
    ? [...baseMessages, ...extraItems].sort((a, b) => (a.ts < b.ts ? -1 : a.ts > b.ts ? 1 : 0))
    : baseMessages
  const grouped: { day: string; items: TimelineItem[] }[] = []
  for (const m of displayMessages) {
    const day = dayLabel(m.ts)
    const last = grouped[grouped.length - 1]
    if (last && last.day === day) last.items.push(m)
    else grouped.push({ day, items: [m] })
  }

  // Conversation-context switches the customer can't see from our side:
  // (a) sender-account change within a thread (failover / manual switch) —
  //     the customer gets a brand-new chat from an unknown number;
  // (b) in the merged contact timeline, moving to the customer's OTHER
  //     number/channel — one stream to us, separate chats to them.
  // Both get an amber divider at the exact switch point.
  const msgAccountIds = new Set(displayMessages.filter((m) => m.account_id).map((m) => m.account_id))
  // label bubbles whenever attribution is meaningful: the thread spans several
  // sender accounts, or the workspace simply runs more than one number
  const multiAccount = msgAccountIds.size > 1 || waAccounts.length > 1
  const acctLabel = (m: UiMessage) => m.account_name || `account #${m.account_id}`
  const switchNotices = new Map<string, string>()
  {
    let prevContact: string | null = null
    let prevAcct: string | null = null
    for (const m of displayMessages) {
      const contact = combinedOn && m.channel && m.key ? `${m.channel}:${m.key}` : null
      if (contact !== null) {
        if (prevContact !== null && contact !== prevContact) {
          const addr = m.channel === 'whatsapp' ? `+${m.key}` : m.key
          switchNotices.set(
            m.id,
            `Now chatting with this contact's other contact — ${CHANNEL_META[m.channel!]?.lbl ?? m.channel} ${addr}. ` +
              `On the customer's side this is a separate chat with no shared history.`,
          )
        }
        prevContact = contact
      }
      if (m.account_id) {
        if (prevAcct !== null && m.account_id !== prevAcct && !switchNotices.has(m.id)) {
          const provider = waAccounts.find((a) => a.id === m.account_id)?.provider
          const seenBefore = displayMessages.some(
            (x) => x !== m && x.account_id === m.account_id && x.ts < m.ts,
          )
          switchNotices.set(
            m.id,
            provider === 'baileys'
              ? `Switched sender number — now via ${acctLabel(m)} (personal number, no template needed).` +
                  (seenBefore
                    ? ' This number has chatted with the customer before.'
                    : ' Cold first contact from this number carries ban risk — safest is a customer-initiated reply first.')
              : seenBefore
                ? `Switched sender number — now via ${acctLabel(m)}. The customer knows this number; if they ` +
                  `haven't replied to it within 24h, open with an approved template.`
                : `Switched sender number — now via ${acctLabel(m)}. Entity-new number for this customer ` +
                  `(their side shows a new chat) — open with an approved template or have them reply first.`,
          )
        }
        prevAcct = m.account_id
      }
    }
  }

  // Fleet strip → jump to a live call's contact thread (no-op if that contact has
  // no chat thread yet, e.g. a manual/voice-only dial).
  const openContactThread = (contactId: number) => {
    const t = threads.find((x) => x.contact_id === contactId)
    if (t) setActiveId({ channel: t.channel, key: t.key })
  }

  // Open an inbox row. A pending contact row has no thread yet — create it on
  // demand (idempotent get-or-create by phone), then open + refresh so it turns
  // into a real row. Real rows open directly.
  const openRow = (t: UiThreadSummary) => {
    setError(null)
    // A merged row stands for several channels — land on the combined view so
    // the merge is visible immediately instead of just one underlying thread.
    if ((t._mergedChannels?.length ?? 0) > 1) setCombinedOn(true)
    if (!t.pending) {
      setActiveId({ channel: t.channel, key: t.key })
      return
    }
    run(async () => {
      const r = await api<{ wa_id: string }>('whatsapp/threads', {
        method: 'POST',
        body: JSON.stringify({ phone: t.sub }),
      })
      setActiveId({ channel: 'whatsapp', key: r.wa_id })
      await refreshThreads()
    })
  }

  return (
    <div className="flex h-full min-h-0 flex-col gap-2.5 p-2.5">
      {/* the old "Sandbox: X not linked" banner is gone on purpose — unlinked
          channels are simply hidden from the page (visibleChannels) instead of
          being announced on every visit */}
      <ActiveCallsStrip onOpenContact={openContactThread} activeContactId={active?.contact_id} />

      {/* 3-column row: scrolls horizontally rather than crushing the middle
          pane below its min-width */}
      <Card className="flex min-h-0 flex-1 overflow-hidden lg:overflow-x-auto">
        {/* ── pane 1: unified conversation list (~240px) ──
            Mobile: full-width, hidden once a conversation is open. */}
        <div
          className={`min-h-0 w-full shrink-0 flex-col border-r border-line lg:w-[260px] ${
            active ? 'hidden lg:flex' : 'flex'
          }`}
        >
          <div className="border-b border-line p-2">
            <div className="flex items-center gap-1.5">
              <div className="relative min-w-0 flex-1">
                <Search
                  size={13}
                  className="pointer-events-none absolute left-2.5 top-1/2 -translate-y-1/2 text-text-faint"
                  aria-hidden
                />
                <input
                  value={search}
                  onChange={(e) => setSearch(e.target.value)}
                  placeholder="Search conversations"
                  aria-label="Search conversations"
                  className="h-8 w-full rounded-lg border border-line bg-fill pl-8 pr-2 text-[12.5px] text-text-primary outline-none placeholder:text-text-faint focus:border-green-600"
                />
              </div>
            </div>
            {/* channel filter chips — only linked channels or ones with chats */}
            <div onWheel={wheelToHorizontal} className="no-scrollbar mt-1.5 flex gap-1 overflow-x-auto">
              {(['all', ...channelOptions] as const).map((c) => {
                const activeChip = filter === c
                const n = c === 'all' ? allRows.length : allRows.filter((t) => t.channel === c).length
                return (
                  <button
                    key={c}
                    onClick={() => {
                      setFilter(c)
                      // filtering away the open chat's channel closes it too —
                      // otherwise the old conversation (and its call transcript)
                      // stays on screen while the list shows another channel
                      if (c !== 'all' && activeId && activeId.channel !== c) {
                        setActiveId(null)
                        setActive(null)
                      }
                    }}
                    className="flex shrink-0 cursor-pointer items-center gap-1 rounded-full px-2 py-[3px] text-[10.5px] font-semibold transition-colors"
                    style={{
                      background: activeChip ? 'rgba(59,130,246,0.14)' : 'var(--fill)',
                      color: activeChip ? 'var(--brand-ink)' : 'var(--text-muted)',
                      border: `1px solid ${activeChip ? 'rgba(59,130,246,0.3)' : 'var(--line)'}`,
                    }}
                  >
                    {c === 'all' ? 'All' : CHANNEL_META[c].lbl} {n > 0 && <span>{n}</span>}
                  </button>
                )
              })}
            </div>
            {/* Chatwoot-style status filter (open / pending / snoozed / resolved) */}
            <div onWheel={wheelToHorizontal} className="no-scrollbar mt-1.5 flex gap-1 overflow-x-auto">
              {(['all', ...THREAD_STATUSES] as const).map((s) => {
                const activeChip = statusFilter === s
                const n =
                  s === 'all' ? threads.length : threads.filter((t) => (t.status ?? 'open') === s).length
                return (
                  <button
                    key={s}
                    onClick={() => setStatusFilter(s)}
                    className="flex shrink-0 cursor-pointer items-center gap-1 rounded-full px-2 py-[3px] text-[10.5px] font-semibold capitalize transition-colors"
                    style={{
                      background: activeChip ? 'rgba(245,158,11,0.14)' : 'var(--fill)',
                      color: activeChip ? 'var(--amber-ink)' : 'var(--text-muted)',
                      border: `1px solid ${activeChip ? 'rgba(245,158,11,0.3)' : 'var(--line)'}`,
                    }}
                  >
                    {s === 'all' ? 'Any status' : s} {s !== 'all' && n > 0 && <span>{n}</span>}
                  </button>
                )
              })}
            </div>
            {/* tag filter — colored chips (like the status row), one at a time */}
            <div onWheel={wheelToHorizontal} className="no-scrollbar mt-1.5 flex gap-1 overflow-x-auto">
              {[{ slug: 'all', label: 'All tags', color: '' }, ...CONVERSATION_TAGS].map((tg) => {
                const activeChip = labelFilter === tg.slug
                const n = tg.slug === 'all' ? threads.length : threads.filter((t) => (t.labels ?? []).includes(tg.slug)).length
                return (
                  <button
                    key={tg.slug}
                    onClick={() => setLabelFilter(tg.slug)}
                    className="flex shrink-0 cursor-pointer items-center gap-1 rounded-full px-2 py-[3px] text-[10.5px] font-semibold transition-colors"
                    style={{
                      background: activeChip ? (tg.color ? `${tg.color}26` : 'var(--fill-hover)') : 'var(--fill)',
                      color: activeChip ? (tg.color || 'var(--text-high)') : 'var(--text-muted)',
                      border: `1px solid ${activeChip ? (tg.color ? `${tg.color}55` : 'var(--line-strong)') : 'var(--line)'}`,
                    }}
                  >
                    {tg.color && <span className="h-1.5 w-1.5 rounded-full" style={{ background: tg.color }} aria-hidden />}
                    {tg.label} {tg.slug !== 'all' && n > 0 && <span>{n}</span>}
                  </button>
                )
              })}
            </div>
            {/* assignee + AI filters — who's working it, and whether the bot is on */}
            <div className="mt-1.5 flex items-center gap-1.5">
              <Select
                value={assigneeFilter}
                onChange={setAssigneeFilter}
                options={[
                  { value: 'all', label: 'Any assignee' },
                  { value: 'none', label: 'Unassigned' },
                  ...agents.map((a) => ({ value: a.username, label: a.name })),
                ]}
                ariaLabel="Filter by assignee"
                className="min-w-0 flex-1"
              />
              <Select
                value={aiFilter}
                onChange={(v) => setAiFilter(v as 'all' | 'on' | 'off')}
                options={[
                  { value: 'all', label: 'AI: any' },
                  { value: 'on', label: 'AI on' },
                  { value: 'off', label: 'AI off' },
                ]}
                ariaLabel="Filter by AI state"
                className="w-[96px] shrink-0"
              />
            </div>
          </div>

          <div className="min-h-0 flex-1 overflow-y-auto py-1">
            {visible.length === 0 && (
              <div className="px-4 py-8 text-center text-[12px] text-text-muted">
                {threads.length === 0
                  ? 'No conversations yet — start one above.'
                  : 'No conversations match.'}
              </div>
            )}
            {visible.map((t) => (
              <button
                key={`${t.channel}-${t.key}${t.pending ? '-pending' : ''}`}
                onClick={() => openRow(t)}
                className={`flex w-full cursor-pointer items-center gap-2.5 border-l-2 px-3 py-2 text-left transition-colors ${
                  activeId && t.channel === activeId.channel && t.key === activeId.key
                    ? 'border-brand bg-[var(--nav-active-bg)]'
                    : 'border-transparent hover:bg-fill'
                }`}
              >
                <Avatar name={t.name} channel={t.channel} />
                <span className="min-w-0 flex-1">
                  <span className="flex items-baseline justify-between gap-2">
                    <span className="truncate text-[13px] font-semibold text-text-high">{t.name}</span>
                    <span className="shrink-0 text-[10.5px] text-text-faint">{timeShort(t.last_ts)}</span>
                  </span>
                  {(t._mergedChannels?.length ?? 0) > 1 && (
                    <span className="mt-0.5 flex items-center gap-1" title="Same contact, merged across channels">
                      {t._mergedChannels!.map((c) => {
                        const meta = CHANNEL_META[c]
                        return (
                          <span
                            key={c}
                            className="grid h-3.5 w-3.5 shrink-0 place-items-center rounded-full"
                            style={{ background: `${meta.color}26`, color: meta.color }}
                          >
                            <meta.Icon size={8} aria-hidden />
                          </span>
                        )
                      })}
                    </span>
                  )}
                  {(t.labels ?? []).length > 0 && (
                    <span className="mt-0.5 flex items-center gap-1">
                      {(t.labels ?? []).slice(0, 6).map((l) => {
                        const m = tagMeta(l)
                        return (
                          <span
                            key={l}
                            className="h-1.5 w-1.5 shrink-0 rounded-full"
                            style={{ background: m.color }}
                            title={m.label}
                          />
                        )
                      })}
                    </span>
                  )}
                  <span className="mt-0.5 flex items-center gap-1.5">
                    {t.ai_enabled && (
                      <span className="flex shrink-0 items-center gap-0.5 rounded-full bg-green/15 px-1.5 py-px text-[9px] font-bold uppercase text-green-300">
                        <Bot size={9} aria-hidden /> AI
                      </span>
                    )}
                    {t.status && t.status !== 'open' && (
                      <span
                        className={`flex shrink-0 items-center rounded-full px-1.5 py-px text-[9px] font-bold uppercase ${
                          t.status === 'resolved' ? 'bg-green/15 text-green-300' : 'bg-amber/15 text-amber-300'
                        }`}
                      >
                        {t.status}
                      </span>
                    )}
                    {t.call && ['in_call', 'ringing', 'connecting'].includes(t.call.status) && (
                      <span className="flex shrink-0 items-center gap-0.5 rounded-full bg-green/15 px-1.5 py-px text-[9px] font-bold uppercase text-green-300">
                        <PhoneCall size={9} aria-hidden /> {t.call.status === 'in_call' ? 'On call' : t.call.status}
                      </span>
                    )}
                    {t.last_dir === 'out' && t.last_status && (
                      <span className="shrink-0 text-text-faint">
                        <StatusTick status={t.last_status} />
                      </span>
                    )}
                    <span className="truncate text-[11.5px] text-text-muted">{t.last_message}</span>
                  </span>
                </span>
              </button>
            ))}
          </div>
        </div>

        {/* ── pane 2: conversation (flex, min 360px on desktop) ──
            Mobile: full-width, shown only when a conversation is open. */}
        <div
          className={`wa-chat-bg min-w-0 flex-1 flex-col lg:min-w-[360px] ${
            active ? 'flex' : 'hidden lg:flex'
          }`}
        >
          {!active ? (
            <div className="grid flex-1 place-content-center text-center">
              <div
                className="mx-auto mb-4 grid h-16 w-16 place-items-center rounded-2xl text-white"
                style={{ background: 'linear-gradient(135deg,#3B82F6,#2563EB)', boxShadow: '0 8px 24px rgba(59,130,246,0.25)' }}
              >
                <Send size={28} aria-hidden />
              </div>
              <div className="text-[15px] font-semibold text-text-high">Select a conversation</div>
              <div className="mt-1 max-w-[320px] text-[12.5px] text-text-muted">
                One inbox for WhatsApp, Messenger, Instagram, Telegram and Email — pick a contact or
                start a new conversation.
              </div>
            </div>
          ) : (
            <>
              {/* header */}
              <div className="flex h-14 shrink-0 items-center gap-2.5 border-b border-line bg-surface px-4">
                {/* Mobile: back to the conversation list. */}
                <button
                  onClick={() => {
                    setActiveId(null)
                    setActive(null)
                  }}
                  aria-label="Back to conversations"
                  title="Back"
                  className="-ml-1 grid h-8 w-8 shrink-0 place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high lg:hidden"
                >
                  <ChevronLeft size={18} aria-hidden />
                </button>
                <Avatar name={active.name} channel={active.channel} size={32} />
                <div className="min-w-0 flex-1">
                  <div className="flex items-center gap-1.5">
                    <span className="truncate text-[13.5px] font-semibold text-text-heading">{active.name}</span>
                    {optedOut && (
                      <span
                        className="flex shrink-0 items-center gap-0.5 rounded-full bg-red/15 px-1.5 py-px text-[9px] font-bold uppercase text-red-300"
                        title="Customer opted out of messaging — sends require a manual override"
                      >
                        <Ban size={9} aria-hidden /> Customer opted out
                      </span>
                    )}
                  </div>
                  <div className="flex items-center gap-1.5 truncate text-[11px] text-text-muted">
                    {CHANNEL_META[active.channel].lbl}
                    {CHANNEL_META[active.channel].beta && <BetaChip />} · {active.sub}
                  </div>
                </div>
                {/* AI cluster: on/off pill toggle (same toggleAi handler as
                    the info-panel switch) */}
                {active.channel !== 'email' && (
                  <div className="flex shrink-0 items-center gap-1.5">
                    <button
                      onClick={toggleAi}
                      disabled={busy || optedOut}
                      role="switch"
                      aria-checked={!!active.ai_enabled && !optedOut}
                      title={optedOut ? 'Customer opted out — AI disabled' : active.ai_enabled ? 'Turn AI off' : 'Turn AI on'}
                      aria-label="Toggle AI auto-reply"
                      className="flex h-7 shrink-0 cursor-pointer items-center gap-1.5 rounded-full border px-2 text-[10px] font-bold uppercase tracking-wide transition-colors disabled:cursor-not-allowed disabled:opacity-50"
                      style={{
                        background: active.ai_enabled && !optedOut ? 'var(--nav-active-bg)' : 'var(--fill)',
                        color: active.ai_enabled && !optedOut ? 'var(--nav-active-text)' : 'var(--text-muted)',
                        borderColor: active.ai_enabled && !optedOut ? 'rgba(59,130,246,0.35)' : 'var(--line)',
                      }}
                    >
                      AI
                      <span
                        className="relative inline-flex h-3.5 w-6 items-center rounded-full transition-colors"
                        style={{ background: active.ai_enabled && !optedOut ? '#3B82F6' : 'var(--line-strong)' }}
                      >
                        <span
                          className="absolute h-2.5 w-2.5 rounded-full bg-white transition-transform"
                          style={{ transform: active.ai_enabled && !optedOut ? 'translateX(12px)' : 'translateX(2px)' }}
                        />
                      </span>
                    </button>
                  </div>
                )}
                {callPill && (
                  <span
                    className={`flex shrink-0 items-center gap-1 rounded-full px-2 py-0.5 text-[10px] font-bold uppercase tracking-wide ${callPill.cls} ${
                      callPill.pulse ? 'animate-pulse' : ''
                    }`}
                  >
                    <PhoneCall size={10} aria-hidden /> {callPill.label}
                  </span>
                )}
                {active.channel !== 'whatsapp' &&
                  (active.status !== 'resolved' ? (
                    <button
                      onClick={() => setThreadStatus('resolved')}
                      disabled={busy}
                      title="Mark conversation resolved"
                      aria-label="Mark conversation resolved"
                      className="flex h-8 shrink-0 cursor-pointer items-center gap-1 rounded-lg bg-green/15 px-2.5 text-[11px] font-bold text-green-300 transition-colors hover:bg-green/25"
                    >
                      <CheckCircle2 size={13} aria-hidden /> Resolve
                    </button>
                  ) : (
                    <button
                      onClick={() => setThreadStatus('open')}
                      disabled={busy}
                      title="Re-open conversation"
                      aria-label="Re-open conversation"
                      className="flex h-8 shrink-0 cursor-pointer items-center gap-1 rounded-lg bg-fill px-2.5 text-[11px] font-bold text-text-mid transition-colors hover:bg-fill-hover"
                    >
                      <RotateCcw size={13} aria-hidden /> Reopen
                    </button>
                  ))}
                {active.channel === 'whatsapp' &&
                  (inCall ? (
                    <button
                      onClick={endCall}
                      disabled={busy}
                      title="End WhatsApp call"
                      aria-label="End WhatsApp call"
                      className="grid h-8 w-8 cursor-pointer place-items-center rounded-lg bg-red/15 text-red-300 transition-colors hover:bg-red/25"
                    >
                      <PhoneOff size={15} aria-hidden />
                    </button>
                  ) : starting ? (
                    <span
                      title="Calling…"
                      aria-label="Calling…"
                      className="grid h-8 w-8 place-items-center rounded-lg text-green-300"
                    >
                      <Loader2 size={15} className="animate-spin" aria-hidden />
                    </span>
                  ) : (
                    <button
                      onClick={startCall}
                      disabled={busy}
                      title="Start WhatsApp call"
                      aria-label="Start WhatsApp call"
                      className="grid h-8 w-8 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-green-300"
                    >
                      <WhatsAppIcon size={16} />
                    </button>
                  ))}
                {/* AI phone call (SIP) — any linked contact, distinct from the
                    WhatsApp-transport call above. */}
                {active.contact_id != null && <VoiceCallButton contactId={active.contact_id} />}
                {/* One-way script call: dial, read a fixed script, hang up — no conversation. */}
                {active.contact_id != null && <ScriptCallButton contactId={active.contact_id} />}
                <button
                  onClick={() => setInfoOpen(!infoOpen)}
                  title="Contact info"
                  aria-label="Toggle contact info panel"
                  aria-pressed={infoOpen}
                  className={`grid h-8 w-8 cursor-pointer place-items-center rounded-lg transition-colors ${
                    infoOpen ? 'bg-fill-hover text-text-high' : 'text-text-muted hover:bg-fill-hover'
                  }`}
                >
                  <Info size={15} aria-hidden />
                </button>
              </div>

              {/* linked contact: channel toggle (single channel ⇄ combined view ⇄
                  voice-only history). Voice is a FILTER, not a dial button —
                  starting/ending a call is the phone icon up in the header. */}
              {active.contact_id != null && combined && (
                <div onWheel={wheelToHorizontal} className="no-scrollbar flex items-center gap-1.5 overflow-x-auto border-b border-line bg-surface px-4 py-1.5">
                  <span className="shrink-0 text-[10.5px] font-bold uppercase tracking-[0.08em] text-text-muted">
                    {combined.contact.name} on
                  </span>
                  <button
                    onClick={() => {
                      setCombinedOn(true)
                      setVoiceOnly(false)
                    }}
                    className="flex shrink-0 cursor-pointer items-center gap-1 rounded-full px-2 py-[3px] text-[10.5px] font-semibold transition-colors"
                    style={{
                      background: combinedOn && !voiceOnly ? 'rgba(59,130,246,0.14)' : 'var(--fill)',
                      color: combinedOn && !voiceOnly ? 'var(--brand-ink)' : 'var(--text-muted)',
                      border: `1px solid ${combinedOn && !voiceOnly ? 'rgba(59,130,246,0.3)' : 'var(--line)'}`,
                    }}
                  >
                    <Layers size={10} aria-hidden /> All channels
                  </button>
                  {combined.channels.map((c) => {
                    const isCurrent =
                      !voiceOnly && !combinedOn && activeId?.channel === c.channel && activeId?.key === c.key
                    const meta = CHANNEL_META[c.channel]
                    return (
                      <button
                        key={`${c.channel}-${c.key}`}
                        onClick={() => {
                          setCombinedOn(false)
                          setVoiceOnly(false)
                          if (activeId?.channel !== c.channel || activeId?.key !== c.key)
                            setActiveId({ channel: c.channel, key: c.key })
                        }}
                        className="flex shrink-0 cursor-pointer items-center gap-1 rounded-full px-2 py-[3px] text-[10.5px] font-semibold transition-colors"
                        style={{
                          background: isCurrent ? `${meta.color}26` : 'var(--fill)',
                          color: isCurrent ? meta.color : 'var(--text-muted)',
                          border: `1px solid ${isCurrent ? `${meta.color}55` : 'var(--line)'}`,
                        }}
                      >
                        <meta.Icon size={10} aria-hidden /> {meta.lbl}
                      </button>
                    )
                  })}
                  {/* Voice — filters the merged timeline to call history only
                      (any transport); does not dial. */}
                  <button
                    onClick={() => {
                      setCombinedOn(true)
                      setVoiceOnly(true)
                    }}
                    title="Show call history only"
                    className="flex shrink-0 cursor-pointer items-center gap-1 rounded-full px-2 py-[3px] text-[10.5px] font-semibold transition-colors"
                    style={{
                      background: voiceOnly ? 'rgba(59,130,246,0.14)' : 'var(--fill)',
                      color: voiceOnly ? 'var(--brand-ink)' : 'var(--text-muted)',
                      border: `1px solid ${voiceOnly ? 'rgba(59,130,246,0.3)' : 'var(--line)'}`,
                    }}
                  >
                    <PhoneCall size={10} aria-hidden /> Voice
                  </button>
                </div>
              )}

              {/* always-on AI summary — pinned above the conversation */}
              {summaryState && summaryState.message_count > 0 && (
                <div className="shrink-0 border-b border-line bg-surface px-4 py-2">
                  <div className="flex items-center gap-1.5">
                    <Sparkles size={12} className="shrink-0" style={{ color: 'var(--brand-ink)' }} aria-hidden />
                    <span className="text-[10.5px] font-bold uppercase tracking-[0.08em] text-text-muted">
                      Summary
                    </span>
                    {summaryState.summary?.heuristic && (
                      <span
                        className="rounded bg-fill px-1.5 py-px text-[8.5px] font-bold uppercase text-text-muted"
                        title="No AI key configured — basic summary"
                      >
                        basic
                      </span>
                    )}
                    {summarizing && (
                      <span className="flex items-center gap-1 text-[10px] text-text-muted">
                        <Loader2 size={10} className="animate-spin" aria-hidden /> updating…
                      </span>
                    )}
                    <div className="flex-1" />
                    {summaryState.summary && !summarizing && (
                      <span className="text-[10px] text-text-faint">
                        {summaryState.stale ? 'may be outdated · ' : ''}
                        {timeAgo(summaryState.summary.generated_at)}
                      </span>
                    )}
                    <button
                      onClick={regenSummary}
                      disabled={summarizing}
                      title="Refresh summary"
                      aria-label="Refresh summary"
                      className="grid h-5 w-5 cursor-pointer place-items-center rounded text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high disabled:cursor-not-allowed disabled:opacity-40"
                    >
                      <RefreshCw size={11} className={summarizing ? 'animate-spin' : ''} aria-hidden />
                    </button>
                    <button
                      onClick={() => setSummaryOpen(!summaryOpen)}
                      title={summaryOpen ? 'Collapse summary' : 'Expand summary'}
                      aria-label={summaryOpen ? 'Collapse summary' : 'Expand summary'}
                      aria-expanded={summaryOpen}
                      className="grid h-5 w-5 cursor-pointer place-items-center rounded text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
                    >
                      <ChevronLeft
                        size={11}
                        className={`transition-transform ${summaryOpen ? '-rotate-90' : 'rotate-180'}`}
                        aria-hidden
                      />
                    </button>
                  </div>
                  {summaryOpen && (
                    /* the summary is the operator's fastest read of the case — big and bold */
                    <p className="mt-1 text-[14px] font-semibold leading-[1.6] text-text-high">
                      {summaryState.summary?.summary ??
                        (summarizing ? 'Generating the first summary…' : 'No summary yet — press refresh.')}
                    </p>
                  )}
                </div>
              )}

              {/* live WhatsApp call monitor — pinned in the middle chat column
                  (not the side panel) while the AI voice agent is on the line;
                  same store /live-calls reads, so this mirrors that page's panel. */}
              {inCall && (
                <div className="shrink-0 space-y-1.5 border-b border-line bg-app/40 p-2.5">
                  <div className="flex items-center justify-between">
                    <span className="text-[10px] font-semibold uppercase tracking-[0.08em] text-text-muted">
                      Live transcript
                    </span>
                    {callLive.done && <span className="text-[10px] text-text-faint">agent finished</span>}
                  </div>
                  <div ref={callScrollRef} className="max-h-36 space-y-1 overflow-y-auto pr-1 text-[12px] leading-snug">
                    {callLive.turns.length === 0 ? (
                      <div className="flex items-center gap-1.5 py-1 text-text-faint">
                        <Loader2 size={12} className="animate-spin" aria-hidden />
                        {active.call?.status === 'in_call' ? 'Waiting for the agent to speak…' : 'Connecting the call…'}
                      </div>
                    ) : (
                      callLive.turns.map((t, i) => (
                        <div key={i} className={t.role === 'assistant' ? 'text-brand-200' : 'text-text-mid'}>
                          <span className="font-semibold">{t.role === 'assistant' ? 'AI' : 'Caller'}:</span> {t.text}
                        </div>
                      ))
                    )}
                  </div>
                  <div className="flex gap-1.5">
                    <textarea
                      value={sayDraft}
                      onChange={(e) => setSayDraft(e.target.value)}
                      rows={1}
                      placeholder="Steer the AI (Guide) or type words for it to say verbatim (Takeover)…"
                      className="min-w-0 flex-1 resize-none rounded-md border border-line bg-app px-2.5 py-1.5 text-[12px] text-text-strong placeholder:text-text-faint focus:border-brand-400 focus:outline-none"
                    />
                    <Button variant="secondary" onClick={() => sendSay('guide')} disabled={busy || !sayDraft.trim()}>
                      <Sparkles size={13} aria-hidden /> Guide
                    </Button>
                    <Button variant="secondary" onClick={() => sendSay('takeover')} disabled={busy || !sayDraft.trim()}>
                      <Send size={13} aria-hidden /> Takeover
                    </Button>
                    <Button variant="secondary" onClick={escalateCall} disabled={busy}>
                      <UserRound size={13} aria-hidden /> Escalate
                    </Button>
                  </div>
                </div>
              )}

              {/* live SIP ("AI phone call") monitor — pinned in the middle chat
                  column while the contact's real phone is on the line; same
                  multi-call state the /live-calls page reads. */}
              {sipCallActive && sipCall && (
                <div className="shrink-0 space-y-1.5 border-b border-line bg-app/40 p-2.5">
                  <div className="flex items-center justify-between">
                    <span className="text-[10px] font-semibold uppercase tracking-[0.08em] text-text-muted">
                      Live transcript — AI phone call
                    </span>
                    <span className="text-[10px] text-text-faint">
                      {sipCall.status === 'live' ? 'in call' : sipCall.statusText}
                    </span>
                  </div>
                  <div className="max-h-36 space-y-1 overflow-y-auto pr-1 text-[12px] leading-snug">
                    {sipCall.turns.length === 0 && sipCall.extraLines.length === 0 ? (
                      <div className="flex items-center gap-1.5 py-1 text-text-faint">
                        <Loader2 size={12} className="animate-spin" aria-hidden />
                        {sipCall.status === 'live' ? 'Waiting for the agent to speak…' : 'Connecting the call…'}
                      </div>
                    ) : (
                      <>
                        {sipCall.turns.map((t, i) => (
                          <div key={i} className={t.who === 'ai' ? 'text-brand-200' : 'text-text-mid'}>
                            <span className="font-semibold">{t.who === 'ai' ? 'AI' : 'Caller'}:</span> {t.t}
                          </div>
                        ))}
                        {sipCall.extraLines.map((l, i) => (
                          <div key={'x' + i} className={l.human ? 'text-amber-300' : 'text-brand-200'}>
                            <span className="font-semibold">{l.human ? 'You' : 'AI'}:</span> {l.t}
                          </div>
                        ))}
                      </>
                    )}
                  </div>
                  <div className="flex gap-1.5">
                    <textarea
                      value={sipSayDraft}
                      onChange={(e) => setSipSayDraft(e.target.value)}
                      rows={1}
                      placeholder={
                        sipCall.humanControl
                          ? 'Type words for the agent to say verbatim…'
                          : 'Steer the AI (Guide) or type words for it to say verbatim (Takeover)…'
                      }
                      className="min-w-0 flex-1 resize-none rounded-md border border-line bg-app px-2.5 py-1.5 text-[12px] text-text-strong placeholder:text-text-faint focus:border-brand-400 focus:outline-none"
                    />
                    <Button
                      variant="secondary"
                      onClick={() => {
                        if (!sipSayDraft.trim()) return
                        sendSipSay(sipCall.room, sipSayDraft.trim(), 'guide')
                        setSipSayDraft('')
                      }}
                      disabled={!sipSayDraft.trim()}
                    >
                      <Sparkles size={13} aria-hidden /> Guide
                    </Button>
                    <Button
                      variant="secondary"
                      onClick={() => {
                        if (!sipSayDraft.trim()) return
                        sendSipSay(sipCall.room, sipSayDraft.trim(), 'takeover')
                        setSipSayDraft('')
                      }}
                      disabled={!sipSayDraft.trim()}
                    >
                      <Send size={13} aria-hidden /> Takeover
                    </Button>
                    <Button variant="secondary" onClick={() => setHumanControl(sipCall.room, !sipCall.humanControl)}>
                      <UserRound size={13} aria-hidden /> {sipCall.humanControl ? 'Return AI' : 'Take over'}
                    </Button>
                    <Button variant="secondary" onClick={() => escalateSipCall(sipCall.room)}>
                      <UserRound size={13} aria-hidden /> Escalate
                    </Button>
                    <Button variant="danger" onClick={() => hangupCall(sipCall.room)}>
                      <PhoneOff size={13} aria-hidden /> End
                    </Button>
                  </div>
                </div>
              )}

              {/* messages with day separators */}
              <div ref={scrollRef} className="min-h-0 flex-1 overflow-y-auto px-4 py-3">
                {displayMessages.length === 0 && (
                  <div className="py-10 text-center text-[12px] text-text-muted">
                    No messages yet — {active.channel === 'email' ? 'compose the first email below.' : 'say hello below.'}
                  </div>
                )}
                {grouped.map((g) => (
                  <div key={g.day}>
                    <div className="my-2.5 text-center">
                      <span className="wa-day-chip">{g.day}</span>
                    </div>
                    {g.items.map((m) => (
                      <div key={`${m.channel ?? ''}${m.id}`}>
                        {switchNotices.has(m.id) && (
                          <div className="my-2.5 text-center">
                            <span className="inline-block max-w-[85%] rounded-md border border-amber-500/30 bg-amber-500/10 px-2.5 py-1 text-[10.5px] leading-relaxed" style={{ color: 'var(--amber-ink)' }}>
                              {switchNotices.get(m.id)}
                            </span>
                          </div>
                        )}
                        {m._sipCall ? (
                          <SipCallCard rec={m._sipCall} />
                        ) : m._callRec ? (
                          <div className="my-2 flex justify-center">
                            <div className="w-[85%] max-w-[440px]">
                              <div className="mb-1 text-center">
                                <span className="wa-system-chip">📞 Voice call · {timeShort(m.ts)}</span>
                              </div>
                              <CallEntry rec={m._callRec} />
                            </div>
                          </div>
                        ) : m.source === 'system' ? (
                          m.text.startsWith('📞 Call transcript') ? (
                            <CallTranscriptChip text={m.text} time={timeShort(m.ts)} />
                          ) : (
                            <div className="my-1.5 text-center">
                              <span className="wa-system-chip">
                                {m.text} · {timeShort(m.ts)}
                              </span>
                            </div>
                          )
                        ) : (
                          <div className={`mb-1.5 flex ${m.dir === 'out' ? 'justify-end' : 'justify-start'}`}>
                            <div
                              className={`wa-bubble ${
                                m.private ? 'wa-bubble-note' : m.dir === 'out' ? 'wa-bubble-out' : 'wa-bubble-in'
                              }`}
                            >
                              {combinedOn && m.channel && <ChannelBadge channel={m.channel} />}
                              {m.private && (
                                <span className="wa-bubble-source">
                                  <Lock size={8} className="mr-0.5 inline" aria-hidden /> Private note — not sent
                                </span>
                              )}
                              {!m.private && m.dir === 'out' && m.source === 'ai' && (
                                <span className="wa-bubble-source">AI assistant</span>
                              )}
                              {m.media && <MediaAttachment media={m.media} />}
                              {m.text}
                              <span className="wa-bubble-time">
                                {(multiAccount || combinedOn) && !m.private && m.account_id && (
                                  <span title={`${m.dir === 'out' ? 'Sent from' : 'Received on'} ${acctLabel(m)}`}>
                                    via {acctLabel(m)} ·
                                  </span>
                                )}
                                <span>{timeShort(m.ts)}</span>
                                {m.dir === 'out' && !m.private && <StatusTick status={m.status} />}
                              </span>
                            </div>
                          </div>
                        )}
                      </div>
                    ))}
                  </div>
                ))}
              </div>

              {error && (
                <div className="border-t border-red/20 bg-red/10 px-4 py-1.5 text-[11.5px] text-red-300">{error}</div>
              )}

              {/* composer (single channel) — combined view is read-only */}
              {combinedOn ? (
                <div className="shrink-0 p-3">
                  <div className="flex items-center gap-2 rounded-xl border border-dashed border-line-strong bg-fill px-3 py-2.5 text-[12px] text-text-muted">
                    <Layers size={13} className="shrink-0" aria-hidden />
                    Combined view of every channel — pick a channel above to reply.
                  </div>
                </div>
              ) : (
              <div className="shrink-0 p-3">
                {active.channel !== 'whatsapp' && (
                  <div className="mb-2 flex gap-1">
                    <button
                      onClick={() => setComposerMode('reply')}
                      className={`flex cursor-pointer items-center gap-1 rounded-lg px-2.5 py-1 text-[11px] font-bold transition-colors ${
                        composerMode === 'reply' ? 'bg-fill-hover text-text-high' : 'text-text-muted hover:bg-fill'
                      }`}
                    >
                      <Send size={11} aria-hidden /> Reply
                    </button>
                    <button
                      onClick={() => setComposerMode('note')}
                      className={`flex cursor-pointer items-center gap-1 rounded-lg px-2.5 py-1 text-[11px] font-bold transition-colors ${
                        composerMode === 'note' ? 'bg-amber/15 text-amber-300' : 'text-text-muted hover:bg-fill'
                      }`}
                    >
                      <StickyNote size={11} aria-hidden /> Private note
                    </button>
                  </div>
                )}
                {cannedMatches.length > 0 && (
                  <div className="mb-2 overflow-hidden rounded-xl border border-line bg-surface">
                    <div className="border-b border-line px-3 py-1.5 text-[10px] font-bold uppercase tracking-[0.08em] text-text-muted">
                      Canned responses
                    </div>
                    {cannedMatches.map((c) => (
                      <button
                        key={c.id}
                        onClick={() => setDraft(c.rendered || c.content)}
                        className="block w-full cursor-pointer px-3 py-1.5 text-left transition-colors hover:bg-fill"
                      >
                        <span className="font-mono text-[11px] font-bold text-text-high">/{c.short_code}</span>
                        <span className="ml-2 text-[11.5px] text-text-muted">
                          {(c.rendered || c.content).length > 80
                            ? `${(c.rendered || c.content).slice(0, 80)}…`
                            : c.rendered || c.content}
                        </span>
                      </button>
                    ))}
                  </div>
                )}
                {/* contact-bound quick replies — one tap drafts a personalized message */}
                {!draft && !pendingFile && canned.length > 0 && !windowClosed &&
                  (active.channel === 'whatsapp' || composerMode === 'reply') && (
                    <div className="no-scrollbar mb-2 flex gap-1.5 overflow-x-auto">
                      {canned.slice(0, 8).map((c) => (
                        <button
                          key={c.id}
                          onClick={() => setDraft(c.rendered || c.content)}
                          title={c.rendered || c.content}
                          className="shrink-0 cursor-pointer rounded-full border border-line bg-fill px-2.5 py-1 text-[11px] font-semibold text-text-mid transition-colors hover:border-brand-400/40 hover:text-text-high"
                        >
                          {c.short_code.replace(/-/g, ' ')}
                        </button>
                      ))}
                    </div>
                  )}
                {active.channel === 'whatsapp' && pendingFile && (
                  <div className="mb-2 flex items-center gap-2 rounded-lg border border-line bg-fill px-3 py-1.5 text-[12px] text-text-mid">
                    <Paperclip size={13} className="shrink-0 text-text-muted" aria-hidden />
                    <span className="min-w-0 flex-1 truncate">
                      {pendingFile.name} · {(pendingFile.size / 1024 / 1024).toFixed(1)} MB
                    </span>
                    <button
                      onClick={() => setPendingFile(null)}
                      title="Remove attachment"
                      aria-label="Remove attachment"
                      className="grid h-6 w-6 shrink-0 cursor-pointer place-items-center rounded-md text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
                    >
                      <X size={12} aria-hidden />
                    </button>
                  </div>
                )}
                <div
                  className={`flex items-center gap-2 rounded-xl border p-1.5 pl-2 ${
                    composerMode === 'note' && active.channel !== 'whatsapp'
                      ? 'border-dashed border-amber/40 bg-amber/5'
                      : 'border-line bg-fill'
                  }`}
                >
                  {active.channel === 'whatsapp' && (
                    <>
                      <button
                        onClick={() => setTplOpen(true)}
                        disabled={busy}
                        title="Send a message template"
                        aria-label="Send a message template"
                        className={`grid h-8 w-8 shrink-0 cursor-pointer place-items-center rounded-lg transition-colors disabled:cursor-not-allowed disabled:opacity-35 ${
                          windowClosed
                            ? 'bg-green text-white hover:opacity-90'
                            : 'text-text-muted hover:bg-fill-hover hover:text-text-high'
                        }`}
                      >
                        <LayoutTemplate size={15} aria-hidden />
                      </button>
                      <button
                        onClick={() => fileRef.current?.click()}
                        disabled={busy || windowClosed}
                        title={windowClosed ? 'Window closed — media needs an open 24h window' : 'Attach a file'}
                        aria-label="Attach a file"
                        className="grid h-8 w-8 shrink-0 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high disabled:cursor-not-allowed disabled:opacity-35"
                      >
                        <Paperclip size={15} aria-hidden />
                      </button>
                      <input
                        ref={fileRef}
                        type="file"
                        accept=".jpg,.jpeg,.png,.webp,.pdf,.mp4,.ogg,.mp3"
                        onChange={onPickFile}
                        className="hidden"
                        aria-hidden
                        tabIndex={-1}
                      />
                    </>
                  )}
                  <input
                    value={draft}
                    onChange={(e) => setDraft(e.target.value)}
                    onKeyDown={(e) => e.key === 'Enter' && !e.shiftKey && sendMessage()}
                    disabled={windowClosed}
                    placeholder={
                      windowClosed
                        ? '24h window closed — send a template to re-open'
                        : composerMode === 'note' && active.channel !== 'whatsapp'
                          ? 'Write a private note — agents only, never sent'
                          : pendingFile
                            ? 'Add a caption (optional)'
                            : active.channel === 'email'
                              ? 'Write an email…'
                              : 'Type a message (/ for canned responses)'
                    }
                    aria-label="Message text"
                    className="min-w-0 flex-1 bg-transparent pl-1 text-[13px] text-text-primary outline-none placeholder:text-text-faint disabled:cursor-not-allowed disabled:opacity-50"
                  />
                  <button
                    onClick={sendMessage}
                    disabled={busy || windowClosed || (!draft.trim() && !pendingFile)}
                    title={composerMode === 'note' && active.channel !== 'whatsapp' ? 'Save private note' : 'Send'}
                    aria-label={composerMode === 'note' && active.channel !== 'whatsapp' ? 'Save private note' : 'Send message'}
                    className={`grid h-9 w-9 shrink-0 cursor-pointer place-items-center rounded-full text-white transition-opacity hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-35 ${
                      composerMode === 'note' && active.channel !== 'whatsapp' ? 'bg-amber' : 'bg-green'
                    }`}
                  >
                    {composerMode === 'note' && active.channel !== 'whatsapp' ? (
                      <StickyNote size={14} aria-hidden />
                    ) : (
                      <Send size={14} aria-hidden />
                    )}
                  </button>
                </div>
                {windowClosed && (
                  <div className="mt-1.5 flex items-center gap-1.5 text-[11px] text-amber-300">
                    <Clock size={12} className="shrink-0" aria-hidden />
                    24h window closed — send a template to re-open the conversation.
                  </div>
                )}
              </div>
              )}
            </>
          )}
        </div>

        {/* ── pane 3: contact info ── */}
        {active && infoOpen && (
          <div className="hidden w-[256px] shrink-0 flex-col overflow-y-auto border-l border-line bg-surface xl:flex">
            <div className="flex flex-col items-center border-b border-line px-4 py-5 text-center">
              <Avatar name={active.name} channel={active.channel} size={64} />
              <div className="mt-2.5 text-[14px] font-bold text-text-heading">{active.name}</div>
              <div className="flex items-center justify-center gap-1.5 text-[11.5px] text-text-muted">
                {CHANNEL_META[active.channel].lbl}
                {CHANNEL_META[active.channel].beta && <BetaChip />} · {active.sub}
              </div>
            </div>

            <div className="space-y-4 p-4">
              <div>
                <div className="mb-1.5 flex items-center gap-1.5 text-[10.5px] font-bold uppercase tracking-[0.08em] text-text-muted">
                  <Link2 size={11} aria-hidden /> Contact
                </div>
                {active.contact_id != null ? (
                  <div className="flex items-center justify-between gap-2 rounded-lg border border-line bg-fill px-3 py-2">
                    <span className="min-w-0 truncate text-[12.5px] font-semibold text-text-high">
                      {active.contact_name || `#${active.contact_id}`}
                    </span>
                    <button
                      onClick={() => linkContact(null)}
                      disabled={busy}
                      title="Unlink contact"
                      aria-label="Unlink contact"
                      className="shrink-0 cursor-pointer rounded-md px-1.5 py-0.5 text-[10.5px] font-bold text-text-muted transition-colors hover:bg-red/15 hover:text-red-300"
                    >
                      Unlink
                    </button>
                  </div>
                ) : (
                  <div className="flex items-center gap-1.5">
                    <Select
                      value={linkSel}
                      onChange={setLinkSel}
                      options={linkContacts.map((c) => ({
                        value: String(c.id),
                        label: c.name,
                        hint: `(${c.phone})`,
                      }))}
                      placeholder="Link to a contact…"
                      ariaLabel="Link to contact"
                      className="min-w-0 flex-1"
                    />
                    <button
                      onClick={() => linkSel && linkContact(Number(linkSel))}
                      disabled={busy || !linkSel}
                      title="Link contact"
                      aria-label="Link contact"
                      className="grid h-8 w-8 shrink-0 cursor-pointer place-items-center rounded-lg border border-line bg-fill text-text-muted transition-colors hover:text-text-high disabled:cursor-not-allowed disabled:opacity-35"
                    >
                      <Link2 size={13} aria-hidden />
                    </button>
                  </div>
                )}
                <p className="mt-1.5 text-[10.5px] leading-relaxed text-text-muted">
                  {active.contact_id != null
                    ? 'All of this contact’s channels merge in the "All channels" view above the chat.'
                    : 'Linking joins this chat with the same person’s other channels (WhatsApp threads link by phone automatically, email by address).'}
                </p>
              </div>

              {/* Communication hub — only for threads with a linked contact */}
              {active.contact_id != null && (
                <div>
                  <div className="mb-1.5 flex items-center gap-1.5 text-[10.5px] font-bold uppercase tracking-[0.08em] text-text-muted">
                    <Layers size={11} aria-hidden /> Channels
                  </div>
                  <ChannelHub contactId={active.contact_id} />
                </div>
              )}

              {active.channel === 'whatsapp' && waAccounts.length > 0 && (
                <div>
                  <div className="mb-1.5 text-[10.5px] font-bold uppercase tracking-[0.08em] text-text-muted">
                    Sending account
                  </div>
                  <div className="flex items-center justify-between gap-2 rounded-lg border border-line bg-fill px-3 py-2">
                    <span className="flex min-w-0 items-center gap-1.5 text-[12.5px] font-semibold text-text-high">
                      <PhoneCall size={13} className="shrink-0 text-green-300" aria-hidden />
                      <span className="truncate">
                        {waAccounts.find((a) => a.id === String(active.account_id ?? ''))?.name ??
                          (active.account_id ? `account #${active.account_id}` : 'Auto')}
                      </span>
                    </span>
                    {canManage && waAccounts.length > 1 && (
                      <Select
                        value={String(active.account_id ?? '')}
                        onChange={(v) => switchAccount(v)}
                        options={[
                          ...(!active.account_id ? [{ value: '', label: 'Auto' }] : []),
                          ...waAccounts.map((a) => ({ value: a.id, label: a.name })),
                        ]}
                        disabled={busy}
                        ariaLabel="Switch sending account"
                        className="w-36 shrink-0"
                      />
                    )}
                  </div>
                  <p className="mt-1.5 text-[10.5px] leading-relaxed text-text-muted">
                    This chat sends from the number above. Switching starts a new chat on the
                    customer&rsquo;s side — free text unlocks after they reply to the new number
                    (use a template to open it).
                  </p>
                </div>
              )}

              {active.channel === 'email' && (
                <div className="rounded-lg border border-line bg-fill px-3 py-2 text-[11px] leading-relaxed text-text-mid">
                  <Mail size={12} className="mb-0.5 inline text-amber-300" aria-hidden />{' '}
                  {status?.imap_configured
                    ? 'Two-way email — sends go via SMTP and replies are pulled from the IMAP inbox about once a minute.'
                    : 'Outbound-only — replies arrive in your mailbox, not here. Set the IMAP host in Settings → Channels to pull them in.'}
                </div>
              )}

              {teams.length > 0 && (
                <div>
                  <div className="mb-1.5 flex items-center gap-1.5 text-[10.5px] font-bold uppercase tracking-[0.08em] text-text-muted">
                    <Users size={11} aria-hidden /> Team
                  </div>
                  <Select
                    value={active.team_id ? String(active.team_id) : ''}
                    onChange={assignTeam}
                    options={[
                      { value: '', label: 'Unassigned' },
                      ...teams.map((t) => ({ value: String(t.id), label: t.name })),
                    ]}
                    disabled={busy}
                    size="md"
                    ariaLabel="Owning team"
                  />
                  <p className="mt-1.5 text-[10.5px] leading-relaxed text-text-muted">
                    Who owns this conversation. Picking a team with nobody on it also hands it to the next
                    member in rotation.
                  </p>
                </div>
              )}

              {/* assignment is channel-agnostic — WhatsApp included */}
              <div>
                <div className="mb-1.5 flex items-center gap-1.5 text-[10.5px] font-bold uppercase tracking-[0.08em] text-text-muted">
                  <UserRound size={11} aria-hidden /> Assignee
                </div>
                <Select
                  value={active.assignee ?? ''}
                  onChange={assignTo}
                  options={[
                    { value: '', label: 'Unassigned' },
                    ...agents.map((a) => ({ value: a.username, label: a.name, hint: `(${a.role})` })),
                    // keep an assignee visible even if their profile disappears
                    ...(active.assignee && !agents.some((a) => a.username === active.assignee)
                      ? [{ value: active.assignee, label: active.assignee }]
                      : []),
                  ]}
                  disabled={busy}
                  size="md"
                  ariaLabel="Assigned agent"
                />
                <p className="mt-1.5 text-[10.5px] leading-relaxed text-text-muted">
                  The teammate working this conversation.
                </p>
              </div>

              {active.channel !== 'whatsapp' && (
                <div>
                  <div className="mb-1.5 text-[10.5px] font-bold uppercase tracking-[0.08em] text-text-muted">
                    Status
                  </div>
                  <Select
                    value={active.status ?? 'open'}
                    onChange={(v) => setThreadStatus(v as ThreadStatus)}
                    options={THREAD_STATUSES.map((s) => ({
                      value: s,
                      label: s.charAt(0).toUpperCase() + s.slice(1),
                    }))}
                    disabled={busy}
                    size="md"
                    ariaLabel="Conversation status"
                  />
                  <p className="mt-1.5 text-[10.5px] leading-relaxed text-text-muted">
                    A new customer message re-opens resolved or snoozed conversations.
                  </p>
                </div>
              )}

              {/* Tags — fixed conversation tag set, works on every channel */}
              <div>
                <div className="mb-1.5 flex items-center gap-1.5 text-[10.5px] font-bold uppercase tracking-[0.08em] text-text-muted">
                  <Tag size={11} aria-hidden /> Tags
                </div>
                <div className="flex flex-wrap gap-1">
                  {CONVERSATION_TAGS.map((tg) => {
                    const on = (active.labels ?? []).includes(tg.slug)
                    return (
                      <button
                        key={tg.slug}
                        onClick={() =>
                          saveLabels(
                            on
                              ? (active.labels ?? []).filter((l) => l !== tg.slug)
                              : [...(active.labels ?? []), tg.slug],
                          )
                        }
                        disabled={busy}
                        title={on ? `Remove ${tg.label}` : `Add ${tg.label}`}
                        className="flex items-center gap-1 rounded-full px-2 py-0.5 text-[10.5px] font-semibold transition-colors disabled:opacity-50"
                        style={
                          on
                            ? { color: '#fff', background: tg.color, border: `1px solid ${tg.color}` }
                            : { color: tg.color, background: tg.color + '18', border: `1px solid ${tg.color}55` }
                        }
                      >
                        <span
                          className="h-1.5 w-1.5 rounded-full"
                          style={{ background: on ? '#fff' : tg.color }}
                        />
                        {tg.label}
                      </button>
                    )
                  })}
                </div>
                {/* legacy free-text labels not in the registry — still removable */}
                {(active.labels ?? []).some((l) => !CONVERSATION_TAGS.some((t) => t.slug === l)) && (
                  <div className="mt-1.5 flex flex-wrap gap-1">
                    {(active.labels ?? [])
                      .filter((l) => !CONVERSATION_TAGS.some((t) => t.slug === l))
                      .map((label) => (
                        <span
                          key={label}
                          className="flex items-center gap-1 rounded-full bg-fill px-2 py-0.5 text-[10.5px] font-semibold text-text-mid"
                        >
                          {label}
                          <button
                            onClick={() => saveLabels((active.labels ?? []).filter((l) => l !== label))}
                            disabled={busy}
                            title={`Remove ${label}`}
                            aria-label={`Remove ${label}`}
                            className="grid h-3 w-3 cursor-pointer place-items-center rounded-full hover:bg-fill-hover"
                          >
                            <X size={8} aria-hidden />
                          </button>
                        </span>
                      ))}
                  </div>
                )}
              </div>

              <div>
                <div className="mb-1.5 text-[10.5px] font-bold uppercase tracking-[0.08em] text-text-muted">
                  Conversation
                </div>
                <div className="rounded-lg border border-line bg-fill px-3 py-2 text-[11.5px] leading-relaxed text-text-mid">
                  {active.messages.filter((m) => m.source !== 'system').length} messages ·{' '}
                  {active.messages.filter((m) => m.source === 'ai').length} AI replies
                  {active.messages.some((m) => m.private) && (
                    <> · {active.messages.filter((m) => m.private).length} private notes</>
                  )}
                  {activeSandbox && <div className="mt-1 text-amber-300">Sandbox — nothing sent externally.</div>}
                </div>
              </div>
            </div>
          </div>
        )}
      </Card>

      {tplOpen && active && active.channel === 'whatsapp' && (
        <TemplatePickerModal
          thread={active}
          onClose={() => setTplOpen(false)}
          onSent={(m) => {
            appendToActive(m)
            refreshActive()
            refreshThreads()
          }}
        />
      )}
    </div>
  )
}
