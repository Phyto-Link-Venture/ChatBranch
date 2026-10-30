'use client'

import { useEffect, useRef, useState } from 'react'
import { Contact, MapPin, Plus, SquareStack, X } from 'lucide-react'
import { useT } from '@/i18n'

/** What the send-card route takes; the page posts it as-is. */
export interface CardPayload {
  kind: 'location' | 'contact' | 'poll'
  lat?: number
  lng?: number
  name?: string
  address?: string
  phones?: string[]
  organization?: string
  question?: string
  options?: string[]
  selectable_count?: number
}

const MAX_OPTIONS = 12

/**
 * The composer's location / contact card / poll forms.
 *
 * One popover with three tabs rather than three buttons, because they are the
 * same gesture ("send something that is not text") and the composer row is
 * already crowded. Each form refuses to submit until it has what WhatsApp
 * requires, so the operator finds out here rather than from a 400.
 */
export function CardComposer({
  disabled,
  onSend,
  onError,
}: {
  disabled?: boolean
  onSend: (payload: CardPayload) => Promise<void>
  onError: (message: string) => void
}) {
  const t = useT()
  const [open, setOpen] = useState(false)
  const [tab, setTab] = useState<'location' | 'contact' | 'poll'>('location')
  const [busy, setBusy] = useState(false)
  const rootRef = useRef<HTMLDivElement>(null)

  // location
  const [lat, setLat] = useState('')
  const [lng, setLng] = useState('')
  const [placeName, setPlaceName] = useState('')
  const [address, setAddress] = useState('')
  const [locating, setLocating] = useState(false)
  // contact
  const [contactName, setContactName] = useState('')
  const [phone, setPhone] = useState('')
  const [organization, setOrganization] = useState('')
  // poll
  const [question, setQuestion] = useState('')
  const [options, setOptions] = useState(['', ''])
  const [multiple, setMultiple] = useState(false)

  useEffect(() => {
    if (!open) return
    const onDown = (e: PointerEvent) => {
      if (rootRef.current && !rootRef.current.contains(e.target as Node)) setOpen(false)
    }
    document.addEventListener('pointerdown', onDown)
    return () => document.removeEventListener('pointerdown', onDown)
  }, [open])

  const reset = () => {
    setLat('')
    setLng('')
    setPlaceName('')
    setAddress('')
    setContactName('')
    setPhone('')
    setOrganization('')
    setQuestion('')
    setOptions(['', ''])
    setMultiple(false)
  }

  const locate = () => {
    if (typeof navigator === 'undefined' || !navigator.geolocation) {
      onError(t('bubble.card.locationDenied'))
      return
    }
    setLocating(true)
    navigator.geolocation.getCurrentPosition(
      (pos) => {
        setLat(pos.coords.latitude.toFixed(6))
        setLng(pos.coords.longitude.toFixed(6))
        setLocating(false)
      },
      () => {
        onError(t('bubble.card.locationDenied'))
        setLocating(false)
      },
      { timeout: 10_000 },
    )
  }

  const filledOptions = options.map((o) => o.trim()).filter(Boolean)
  const ready =
    tab === 'location'
      ? Number.isFinite(Number(lat)) && lat.trim() !== '' && Number.isFinite(Number(lng)) && lng.trim() !== ''
      : tab === 'contact'
        ? contactName.trim() !== '' && phone.trim() !== ''
        : question.trim() !== '' && new Set(filledOptions).size >= 2

  const send = async () => {
    if (!ready) return
    const payload: CardPayload =
      tab === 'location'
        ? {
            kind: 'location',
            lat: Number(lat),
            lng: Number(lng),
            name: placeName.trim() || undefined,
            address: address.trim() || undefined,
          }
        : tab === 'contact'
          ? {
              kind: 'contact',
              name: contactName.trim(),
              phones: [phone.trim()],
              organization: organization.trim() || undefined,
            }
          : {
              kind: 'poll',
              question: question.trim(),
              options: [...new Set(filledOptions)],
              selectable_count: multiple ? new Set(filledOptions).size : 1,
            }
    setBusy(true)
    try {
      await onSend(payload)
      reset()
      setOpen(false)
    } finally {
      setBusy(false)
    }
  }

  const field =
    'w-full rounded-md border border-line bg-surface px-2 py-1.5 text-[12.5px] text-text-primary outline-none focus:border-line-strong'

  return (
    <div ref={rootRef} className="relative shrink-0">
      <button
        onClick={() => setOpen((v) => !v)}
        disabled={disabled}
        title={t('bubble.card.open')}
        aria-label={t('bubble.card.open')}
        aria-expanded={open}
        className="grid h-8 w-8 place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high disabled:opacity-35"
      >
        <MapPin size={15} aria-hidden />
      </button>

      {open && (
        <div
          role="dialog"
          aria-label={t('bubble.card.open')}
          className="absolute bottom-10 left-0 z-30 w-[16rem] rounded-xl border border-line-strong bg-panel p-2 shadow-lg"
        >
          <div className="mb-2 flex items-center gap-1" role="tablist">
            {([
              ['location', MapPin, t('bubble.card.location')],
              ['contact', Contact, t('bubble.card.contact')],
              ['poll', SquareStack, t('bubble.card.poll')],
            ] as const).map(([id, Icon, label]) => (
              <button
                key={id}
                role="tab"
                aria-selected={tab === id}
                onClick={() => setTab(id)}
                className={`flex flex-1 items-center justify-center gap-1 rounded-md px-1.5 py-1 text-[11px] font-semibold transition-colors ${
                  tab === id ? 'bg-primary text-on-primary' : 'text-text-mid hover:bg-fill-hover'
                }`}
              >
                <Icon size={12} aria-hidden /> {label}
              </button>
            ))}
            <button
              onClick={() => setOpen(false)}
              title={t('bubble.card.close')}
              aria-label={t('bubble.card.close')}
              className="grid h-6 w-6 shrink-0 place-items-center rounded-md text-text-muted hover:bg-fill-hover"
            >
              <X size={12} aria-hidden />
            </button>
          </div>

          <div className="flex flex-col gap-1.5">
            {tab === 'location' && (
              <>
                <button
                  onClick={locate}
                  disabled={locating}
                  className="rounded-md bg-fill px-2 py-1.5 text-[11.5px] font-semibold text-text-high transition-colors hover:bg-fill-hover disabled:opacity-50"
                >
                  {locating ? t('bubble.card.locating') : t('bubble.card.useMyLocation')}
                </button>
                <div className="flex gap-1.5">
                  <input
                    value={lat}
                    onChange={(e) => setLat(e.target.value)}
                    inputMode="decimal"
                    placeholder={t('bubble.card.lat')}
                    aria-label={t('bubble.card.lat')}
                    className={field}
                  />
                  <input
                    value={lng}
                    onChange={(e) => setLng(e.target.value)}
                    inputMode="decimal"
                    placeholder={t('bubble.card.lng')}
                    aria-label={t('bubble.card.lng')}
                    className={field}
                  />
                </div>
                <input
                  value={placeName}
                  onChange={(e) => setPlaceName(e.target.value)}
                  placeholder={t('bubble.card.placeName')}
                  aria-label={t('bubble.card.placeName')}
                  className={field}
                />
                <input
                  value={address}
                  onChange={(e) => setAddress(e.target.value)}
                  placeholder={t('bubble.card.address')}
                  aria-label={t('bubble.card.address')}
                  className={field}
                />
              </>
            )}

            {tab === 'contact' && (
              <>
                <input
                  value={contactName}
                  onChange={(e) => setContactName(e.target.value)}
                  placeholder={t('bubble.card.contactName')}
                  aria-label={t('bubble.card.contactName')}
                  className={field}
                />
                <input
                  value={phone}
                  onChange={(e) => setPhone(e.target.value)}
                  inputMode="tel"
                  placeholder={t('bubble.card.phone')}
                  aria-label={t('bubble.card.phone')}
                  className={field}
                />
                <input
                  value={organization}
                  onChange={(e) => setOrganization(e.target.value)}
                  placeholder={t('bubble.card.organization')}
                  aria-label={t('bubble.card.organization')}
                  className={field}
                />
              </>
            )}

            {tab === 'poll' && (
              <>
                <input
                  value={question}
                  onChange={(e) => setQuestion(e.target.value)}
                  placeholder={t('bubble.card.question')}
                  aria-label={t('bubble.card.question')}
                  className={field}
                />
                {options.map((value, i) => (
                  <input
                    key={i}
                    value={value}
                    onChange={(e) => setOptions(options.map((o, j) => (j === i ? e.target.value : o)))}
                    placeholder={t('bubble.card.option', { n: i + 1 })}
                    aria-label={t('bubble.card.option', { n: i + 1 })}
                    className={field}
                  />
                ))}
                {options.length < MAX_OPTIONS && (
                  <button
                    onClick={() => setOptions([...options, ''])}
                    className="flex items-center gap-1 self-start rounded-md px-1 py-0.5 text-[11px] font-semibold text-text-mid transition-colors hover:bg-fill-hover"
                  >
                    <Plus size={11} aria-hidden /> {t('bubble.card.addOption')}
                  </button>
                )}
                <label className="flex items-center gap-1.5 text-[11px] text-text-mid">
                  <input type="checkbox" checked={multiple} onChange={(e) => setMultiple(e.target.checked)} />
                  {t('bubble.card.multiple')}
                </label>
              </>
            )}

            <div className="mt-0.5 flex justify-end gap-1">
              <button
                onClick={() => setOpen(false)}
                className="rounded-md px-2 py-1 text-[11.5px] font-semibold text-text-mid transition-colors hover:bg-fill-hover"
              >
                {t('bubble.card.cancel')}
              </button>
              <button
                onClick={() => void send()}
                disabled={!ready || busy}
                className="rounded-md bg-primary px-2.5 py-1 text-[11.5px] font-semibold text-on-primary transition-opacity hover:opacity-90 disabled:opacity-40"
              >
                {t('bubble.card.send')}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}
