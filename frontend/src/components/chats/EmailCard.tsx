'use client'

/**
 * An email as a mail app shows it: subject, From / To / Cc, the mail itself and
 * every attachment in one card. The HTML (`GET /api/messaging/messages/{pk}/email`)
 * was cleaned on the server in email mode; it is drawn in a SANDBOXED frame
 * (no scripts, links open a new tab) from a srcdoc. Remote images arrive as
 * `data-cb-src` and load only after "Show images" — like Outlook / Gmail, so a
 * tracking pixel never fires just because someone opened the chat. A text-only
 * mail shows its text. Long mails start folded at FOLDED_PX.
 */
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { ChevronDown, ChevronUp, ImageOff, Mail, Paperclip } from 'lucide-react'
import { useT } from '@/i18n'
import { MediaAttachment } from './MediaPlayers'
import { api, type UiMessage } from './inboxTypes'

const FOLDED_PX = 520

interface EmailView {
  html: string
  text: string
}

/** The frame's document: the mail, links to a new tab, light like the paper it was written for. */
function frameDoc(html: string, showImages: boolean): string {
  const body = showImages ? html.replace(/\sdata-cb-src=/gi, ' src=') : html
  return (
    '<!doctype html><html><head><meta charset="utf-8"><base target="_blank">' +
    '<style>html{color-scheme:light}body{margin:12px 14px;background:#fff;color:#1f1f1f;' +
    'font:14px/1.5 -apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;overflow-wrap:anywhere}' +
    'img{max-width:100%;height:auto}table{max-width:100%}pre{white-space:pre-wrap}</style></head><body>' +
    body +
    '</body></html>'
  )
}

export default function EmailCard({ m, parts }: { m: UiMessage; parts: UiMessage[] }) {
  const t = useT()
  const mail = m.email!
  const [view, setView] = useState<EmailView | null>(null)
  const [failed, setFailed] = useState(false)
  const [showImages, setShowImages] = useState(false)
  const [open, setOpen] = useState(false)
  const [height, setHeight] = useState(160)
  const frame = useRef<HTMLIFrameElement>(null)

  useEffect(() => {
    if (!mail.has_html || !m.pk) return
    let live = true
    api<EmailView>(`messaging/messages/${m.pk}/email`)
      .then((v) => live && setView(v))
      .catch(() => live && setFailed(true))
    return () => {
      live = false
    }
  }, [m.pk, mail.has_html])

  const doc = useMemo(() => (view?.html ? frameDoc(view.html, showImages) : ''), [view, showImages])
  const blocked = !!view?.html && /\sdata-cb-src=/i.test(view.html)

  // the sandbox keeps same-origin (never scripts), so the page can read the mail's height
  const measure = useCallback(() => {
    const d = frame.current?.contentDocument
    if (d?.documentElement) setHeight(Math.max(60, d.documentElement.scrollHeight + 2))
  }, [])
  useEffect(() => {
    const d = frame.current?.contentDocument
    if (!d?.body || typeof ResizeObserver === 'undefined') return
    const ro = new ResizeObserver(measure)
    ro.observe(d.body)
    return () => ro.disconnect()
  }, [doc, measure])

  const files = [m, ...parts].filter((x) => x.media)
  const folded = !open && height > FOLDED_PX

  return (
    <span className="cb-email">
      <span className="cb-email-head">
        <span className="cb-email-subject">
          <Mail size={13} aria-hidden className="shrink-0 opacity-60" />
          {mail.subject || t('bubble.email.noSubject')}
        </span>
        <span className="cb-email-line">
          <b>{t('bubble.email.from')}</b> {mail.from}
        </span>
        {mail.to && (
          <span className="cb-email-line" title={mail.to}>
            <b>{t('bubble.email.to')}</b> {mail.to}
          </span>
        )}
        {mail.cc && (
          <span className="cb-email-line" title={mail.cc}>
            <b>{t('bubble.email.cc')}</b> {mail.cc}
          </span>
        )}
      </span>

      {blocked && !showImages && (
        <span className="cb-email-notice">
          <ImageOff size={12} aria-hidden /> {t('bubble.email.imagesBlocked')}
          <button type="button" className="cb-email-link" onClick={() => setShowImages(true)}>
            {t('bubble.email.showImages')}
          </button>
        </span>
      )}

      {mail.has_html && !failed ? (
        view ? (
          <span className="cb-email-body" style={{ maxHeight: folded ? FOLDED_PX : undefined }}>
            <iframe
              ref={frame}
              title={mail.subject || t('bubble.email.noSubject')}
              srcDoc={doc}
              sandbox="allow-same-origin allow-popups allow-popups-to-escape-sandbox"
              referrerPolicy="no-referrer"
              onLoad={measure}
              style={{ height }}
              className="cb-email-frame"
            />
            {folded && <span className="cb-email-fade" aria-hidden />}
          </span>
        ) : (
          <span className="cb-email-text opacity-60">{t('bubble.email.loading')}</span>
        )
      ) : (
        // a text-only mail, or the view could not be fetched: the stored text
        <span className="cb-email-text">{m.text}</span>
      )}

      {height > FOLDED_PX && view && (
        <button type="button" className="cb-email-link cb-email-more" onClick={() => setOpen((o) => !o)}>
          {open ? <ChevronUp size={13} aria-hidden /> : <ChevronDown size={13} aria-hidden />}
          {t(open ? 'bubble.email.showLess' : 'bubble.email.showFull')}
        </button>
      )}

      {files.length > 0 && (
        <span className="cb-email-files">
          <span className="cb-email-line">
            <Paperclip size={12} aria-hidden className="mr-1 inline" />
            {t('bubble.email.attachments', { count: files.length })}
          </span>
          {files.map((x) => (
            <MediaAttachment key={x.id} media={x.media!} />
          ))}
        </span>
      )}
    </span>
  )
}
