'use client'

import { useCallback, useEffect, useRef, useState } from 'react'
import { Image as ImageIcon, Loader2, Sticker, Upload, X } from 'lucide-react'
import { API } from '@/lib/apiBase'
import { authHeaders, expireSessionOn401 } from '@/lib/auth'
import { useEntityQuery } from '@/lib/EntityContext'
import { useT } from '@/i18n'

/** What the send-sticker route takes; the page posts it as-is. */
export interface StickerPayload {
  file_id?: string
  file_base64?: string
  file_name?: string
}

/** A picture in the media library the operator can turn into a sticker. */
interface LibraryPicture {
  file_id: string
  name: string
  size: number
}

/** A size a person reads, not a byte count. */
function size(bytes: number): string {
  if (bytes >= 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
  if (bytes >= 1024) return `${Math.round(bytes / 1024)} KB`
  return `${bytes} B`
}

/** What a browser can hand us that WhatsApp can become a sticker from. */
const ACCEPT = '.png,.jpg,.jpeg,.webp,.gif,.mp4'
const EXTS = ['png', 'jpg', 'jpeg', 'webp', 'gif', 'mp4']
/** Plenty for a picture the server is about to shrink to 512×512 anyway. */
const MAX_BYTES = 10 * 1024 * 1024

/**
 * The composer's sticker button.
 *
 * Sending a sticker is not sending an image: the server shapes the picture onto a
 * 512×512 transparent square, compresses it until it fits WhatsApp's own ceiling and
 * stamps the pack, then sends it as a real sticker — no bubble, no caption. All this
 * end does is choose the picture, from the device or from the media library (where a
 * sticker somebody already made is one click away rather than another upload).
 *
 * WhatsApp-only, and the page decides that: an official number is refused by the
 * server with the reason, which the composer shows like any other send error.
 */
export function StickerComposer({
  disabled,
  onSend,
  onError,
}: {
  disabled?: boolean
  onSend: (payload: StickerPayload) => Promise<void>
  onError: (message: string) => void
}) {
  const t = useT()
  const entity = useEntityQuery()
  const [open, setOpen] = useState(false)
  const [busy, setBusy] = useState(false)
  const [pictures, setPictures] = useState<LibraryPicture[] | null>(null)
  const rootRef = useRef<HTMLDivElement>(null)
  const picker = useRef<HTMLInputElement>(null)

  useEffect(() => {
    if (!open) return
    const onDown = (e: PointerEvent) => {
      if (!rootRef.current?.contains(e.target as Node)) setOpen(false)
    }
    document.addEventListener('pointerdown', onDown)
    return () => document.removeEventListener('pointerdown', onDown)
  }, [open])

  const loadLibrary = useCallback(async () => {
    try {
      const params = new URLSearchParams(entity)
      params.set('kind', 'image')
      const res = await fetch(`${API}/media/library?${params}`, { headers: authHeaders() })
      if (expireSessionOn401(res)) return
      if (!res.ok) {
        setPictures([])
        return
      }
      const body = (await res.json()) as { files?: LibraryPicture[] }
      setPictures(body.files ?? [])
    } catch {
      setPictures([]) // the library is a convenience; uploading still works
    }
  }, [entity])

  useEffect(() => {
    if (open && pictures === null) void loadLibrary()
  }, [open, pictures, loadLibrary])

  const send = async (payload: StickerPayload) => {
    setBusy(true)
    try {
      await onSend(payload)
      setOpen(false)
    } finally {
      setBusy(false)
    }
  }

  const onPick = async (file: File | undefined) => {
    if (!file) return
    const ext = file.name.toLowerCase().split('.').pop() || ''
    if (!EXTS.includes(ext)) {
      onError(t('bubble.sendSticker.wrongType'))
      return
    }
    if (file.size > MAX_BYTES) {
      onError(t('bubble.sendSticker.tooBig'))
      return
    }
    const base64 = await new Promise<string>((resolve, reject) => {
      const reader = new FileReader()
      reader.onload = () => resolve(String(reader.result).split(',')[1] ?? '')
      reader.onerror = () => reject(new Error('read failed'))
      reader.readAsDataURL(file)
    }).catch(() => '')
    if (!base64) {
      onError(t('bubble.sendSticker.readFailed'))
      return
    }
    await send({ file_base64: base64, file_name: file.name })
  }

  return (
    <div ref={rootRef} className="relative shrink-0">
      <button
        onClick={() => setOpen((v) => !v)}
        disabled={disabled}
        title={t('bubble.sendSticker.open')}
        aria-label={t('bubble.sendSticker.open')}
        aria-expanded={open}
        className="grid h-8 w-8 place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high disabled:opacity-35"
      >
        <Sticker size={15} aria-hidden />
      </button>

      {open && (
        <div
          role="dialog"
          aria-label={t('bubble.sendSticker.open')}
          className="absolute bottom-10 left-0 z-30 w-[17rem] rounded-xl border border-line-strong bg-panel p-2 shadow-lg"
        >
          <div className="mb-2 flex items-center justify-between gap-2">
            <span className="text-[11px] font-semibold text-text-high">{t('bubble.sendSticker.title')}</span>
            <button
              onClick={() => setOpen(false)}
              title={t('bubble.card.close')}
              aria-label={t('bubble.card.close')}
              className="grid h-6 w-6 shrink-0 place-items-center rounded-md text-text-muted hover:bg-fill-hover"
            >
              <X size={12} aria-hidden />
            </button>
          </div>

          <p className="mb-2 text-[10px] leading-snug text-text-muted">{t('bubble.sendSticker.help')}</p>

          <button
            onClick={() => picker.current?.click()}
            disabled={busy}
            className="mb-2 flex w-full items-center justify-center gap-1.5 rounded-lg bg-primary px-2 py-1.5 text-[11px] font-semibold text-on-primary transition-opacity hover:opacity-90 disabled:opacity-40"
          >
            {busy ? <Loader2 size={12} className="animate-spin" aria-hidden /> : <Upload size={12} aria-hidden />}
            {t('bubble.sendSticker.upload')}
          </button>
          <input
            ref={picker}
            type="file"
            accept={ACCEPT}
            onChange={(e) => {
              const file = e.target.files?.[0]
              e.target.value = '' // allow re-picking the same file
              void onPick(file)
            }}
            className="hidden"
            aria-hidden
            tabIndex={-1}
          />

          {pictures === null ? (
            <p className="py-2 text-center text-[10px] text-text-muted">{t('common.status.loading')}</p>
          ) : pictures.length === 0 ? (
            <p className="py-1 text-[10px] leading-snug text-text-muted">{t('bubble.sendSticker.libraryEmpty')}</p>
          ) : (
            <>
              <p className="mb-1 text-[10px] font-semibold uppercase tracking-wide text-text-faint">
                {t('bubble.sendSticker.fromLibrary')}
              </p>
              {/*
                Names, not thumbnails: the library serves a file through an
                authenticated route as an attachment, so an <img src> would be a 401
                and would not render inline even signed in. A list is honest and works.
              */}
              <ul className="max-h-40 overflow-y-auto">
                {pictures.map((p) => (
                  <li key={p.file_id}>
                    <button
                      onClick={() => void send({ file_id: p.file_id })}
                      disabled={busy}
                      title={p.name}
                      className="flex w-full items-center gap-1.5 rounded-md px-1.5 py-1 text-left text-[11px] text-text-mid transition-colors hover:bg-fill-hover hover:text-text-high disabled:opacity-40"
                    >
                      <ImageIcon size={11} className="shrink-0 text-text-faint" aria-hidden />
                      <span className="truncate">{p.name}</span>
                      <span className="ml-auto shrink-0 text-[10px] text-text-faint">{size(p.size)}</span>
                    </button>
                  </li>
                ))}
              </ul>
            </>
          )}
        </div>
      )}
    </div>
  )
}
