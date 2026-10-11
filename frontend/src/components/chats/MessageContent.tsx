'use client'

/**
 * What goes inside a bubble: the quoted message (if it is a reply), then the
 * attachment / card / text. Shared locations ("[location] lat,lng label") are a
 * card with "Open in Maps"; shared contact cards ("[contact] Name · +phone")
 * show the name + number with "Add as contact"; a sticker renders small with
 * no bubble behind it (`isSticker` — the page adds `cb-bubble-sticker`).
 */
import { useState } from 'react'
import Link from 'next/link'
import { ExternalLink, MapPin, UserPlus, UserRound } from 'lucide-react'
import { useT } from '@/i18n'
import { useAccess } from '@/lib/useAccess'
import { MediaAttachment, MediaMissing } from './MediaPlayers'
import { QuotedBlock } from './QuotedBlock'
import { api, type UiMessage } from './inboxTypes'
import './messageExtras.css'

const LOCATION_RE = /^\[location\]\s+(-?\d{1,3}(?:\.\d+)?),\s*(-?\d{1,3}(?:\.\d+)?)\s*(.*)$/s
const CONTACT_RE = /^\[contact\]\s+(.+?)(?:\s+·\s+(\+?[\d\s().-]{4,}))?$/s

/** The text a media message carries when it has no caption ("[image]", "[audio]"…,
 *  email's "[attachment] name") — never shown under the attachment itself. */
const PLACEHOLDER_RE = /^\[(image|photo|video|audio|voice|document|file|sticker|media)\]$/i
const ATTACHMENT_RE = /^\[attachment\]\s/
const placeholderKind = (text: string): string | null => {
  const hit = PLACEHOLDER_RE.exec(text.trim())
  if (!hit) return null
  const k = hit[1].toLowerCase()
  return k === 'photo' ? 'image' : k === 'voice' ? 'audio' : k === 'file' || k === 'media' ? 'document' : k
}

export const isSticker = (m: UiMessage) => m.text === '[sticker]' && (!m.media || m.media.type === 'image')

export function parseLocation(text: string): { lat: number; lng: number; label: string } | null {
  const hit = LOCATION_RE.exec(text.trim())
  if (!hit) return null
  const lat = Number(hit[1])
  const lng = Number(hit[2])
  if (!Number.isFinite(lat) || !Number.isFinite(lng) || Math.abs(lat) > 90 || Math.abs(lng) > 180) return null
  return { lat, lng, label: hit[3].trim() }
}

export function parseContactCard(text: string): { name: string; phone: string } | null {
  const hit = CONTACT_RE.exec(text.trim())
  return hit ? { name: hit[1].trim(), phone: (hit[2] ?? '').trim() } : null
}

function LocationCard({ lat, lng, label }: { lat: number; lng: number; label: string }) {
  const t = useT()
  const href = `https://www.google.com/maps/search/?api=1&query=${encodeURIComponent(`${lat},${lng}`)}`
  return (
    <span className="cb-card">
      <span className="cb-card-icon">
        <MapPin size={15} aria-hidden />
      </span>
      <span className="min-w-0 flex-1">
        <span className="block text-[12.5px] font-semibold">{label || t('bubble.location.title')}</span>
        <span className="block text-[11px] tabular-nums opacity-70">
          {lat.toFixed(5)}, {lng.toFixed(5)}
        </span>
        <a href={href} target="_blank" rel="noreferrer" className="cb-card-link">
          <ExternalLink size={11} aria-hidden /> {t('bubble.location.open')}
        </a>
      </span>
    </span>
  )
}

function ContactCard({ name, phone }: { name: string; phone: string }) {
  const t = useT()
  const access = useAccess()
  const [state, setState] = useState<'idle' | 'adding' | 'added'>('idle')
  const [contactId, setContactId] = useState<number | null>(null)
  const [error, setError] = useState<string | null>(null)
  const canAdd = access.ready && access.can('contacts.manage') && !!phone

  const add = async () => {
    setState('adding')
    setError(null)
    try {
      const r = await api<{ id?: number; contact?: { id?: number } }>('contacts', {
        method: 'POST',
        body: JSON.stringify({ name: name || phone, phone }),
      })
      setContactId(r.id ?? r.contact?.id ?? null)
      setState('added')
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
      setState('idle')
    }
  }

  return (
    <span className="cb-card">
      <span className="cb-card-icon">
        <UserRound size={15} aria-hidden />
      </span>
      <span className="min-w-0 flex-1">
        <span className="block text-[10px] font-bold uppercase tracking-wide opacity-60">{t('bubble.contactCard.title')}</span>
        <span className="block text-[12.5px] font-semibold">{name}</span>
        {phone && <span className="block text-[11.5px] tabular-nums opacity-80">{phone}</span>}
        {state === 'added' ? (
          contactId ? (
            <Link href={`/contacts/${contactId}`} className="cb-card-link">
              <UserRound size={11} aria-hidden /> {t('bubble.contactCard.open')}
            </Link>
          ) : (
            <span className="cb-card-link">{t('bubble.contactCard.added')}</span>
          )
        ) : (
          canAdd && (
            <button type="button" onClick={add} disabled={state === 'adding'} className="cb-card-link">
              <UserPlus size={11} aria-hidden /> {state === 'adding' ? t('bubble.contactCard.adding') : t('bubble.contactCard.add')}
            </button>
          )
        )}
        {error && (
          <span role="alert" className="block text-[11px] text-red-300">
            {error}
          </span>
        )}
      </span>
    </span>
  )
}

export default function MessageContent({
  m,
  contactName,
  onJump,
}: {
  m: UiMessage
  contactName: string
  onJump: (pk: number) => void
}) {
  const t = useT()
  const quote = m.reply_to ? <QuotedBlock q={m.reply_to} contactName={contactName} onJump={onJump} /> : null
  if (isSticker(m)) {
    return (
      <>
        {quote}
        {m.media ? <MediaAttachment media={m.media} /> : <span className="opacity-70">{t('bubble.sticker')}</span>}
      </>
    )
  }
  const kind = placeholderKind(m.text)
  if (m.media) {
    // the attachment speaks for itself — a placeholder never sits under it
    const caption = kind || ATTACHMENT_RE.test(m.text) ? '' : m.text
    return (
      <>
        {quote}
        <MediaAttachment media={m.media} />
        {m.media.type === 'audio' && m.transcript && m.transcript !== '…' && !caption && (
          <span className="mt-1 block text-[12.5px] leading-snug">
            <span className="mr-1 text-[10.5px] font-semibold uppercase tracking-wide opacity-60">
              {t('bubble.transcript')}
            </span>
            {m.transcript}
          </span>
        )}
        {caption}
      </>
    )
  }
  if (kind) {
    return (
      <>
        {quote}
        <MediaMissing kind={kind} ts={m.ts} inbound={m.dir === 'in'} />
      </>
    )
  }
  const location = parseLocation(m.text)
  const card = !location ? parseContactCard(m.text) : null
  return (
    <>
      {quote}
      {location ? <LocationCard {...location} /> : card ? <ContactCard {...card} /> : m.text}
    </>
  )
}
