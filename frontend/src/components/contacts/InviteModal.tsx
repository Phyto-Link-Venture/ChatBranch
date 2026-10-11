'use client'

/**
 * "Invite to another app" — mint a short invite code for this contact and get
 * a link (+ QR) per app: wa.me with a prefilled "(ref:CODE)", t.me ?start,
 * m.me ?ref, ig.me (the code rides as text) and mailto. When the customer
 * opens the link and writes, the inbound binder links that new conversation
 * to this contact. "Send via" drops the invite into an existing chat's
 * composer (/chats?thread=…&draft=…).
 */

import { useEffect, useState } from 'react'
import { useRouter } from 'next/navigation'
import { Check, Copy, Loader2, Send } from 'lucide-react'
import { Modal } from '@/components/ui/Modal'
import { Button } from '@/components/ui/Button'
import { ChannelLogo } from '@/components/ui/ChannelLogo'
import { INVITE_CHANNELS, cApi, errText, svgDataUri, type InviteResult } from '@/lib/contactIdentity'
import { useFormat, useT } from '@/i18n'

const LOGO: Record<string, string> = { facebook: 'messenger' }

export interface InviteThread {
  channel: string
  key: string
  /** which company's conversation (a customer has one per company) */
  thread_id?: number | null
  label: string
}

export function InviteModal({
  open,
  onClose,
  contactId,
  contactName,
  available,
  threads,
}: {
  open: boolean
  onClose: () => void
  contactId: number
  contactName: string
  /** Channels with a connected account (the hub's invite_channels). */
  available: string[]
  /** Chats this contact already has — "Send via" targets. */
  threads: InviteThread[]
}) {
  const t = useT()
  const f = useFormat()
  const router = useRouter()
  const [picked, setPicked] = useState<string[]>([])
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [result, setResult] = useState<InviteResult | null>(null)
  const [copied, setCopied] = useState<string | null>(null)

  useEffect(() => {
    if (!open) return
    setPicked(INVITE_CHANNELS.map((c) => c.value).filter((c) => available.includes(c)))
    setResult(null)
    setError(null)
    setCopied(null)
  }, [open, available])

  const toggle = (ch: string) => setPicked((cur) => (cur.includes(ch) ? cur.filter((x) => x !== ch) : [...cur, ch]))

  const create = async () => {
    setBusy(true)
    setError(null)
    try {
      const r = await cApi<InviteResult>(`contacts/${contactId}/invites`, {
        method: 'POST',
        body: JSON.stringify({ channels: picked }),
      })
      setResult(r)
    } catch (e) {
      setError(errText(e))
    } finally {
      setBusy(false)
    }
  }

  const copy = async (id: string, text: string) => {
    try {
      await navigator.clipboard.writeText(text)
      setCopied(id)
      setTimeout(() => setCopied((c) => (c === id ? null : c)), 1600)
    } catch {
      setError(t('contacts.invite.copyFailed'))
    }
  }

  const firstName = contactName.split(' ')[0] || ''
  const allText = result
    ? [
        firstName ? t('contacts.invite.greeting', { name: firstName }) : t('contacts.invite.greetingNoName'),
        ...result.links.map((l) => l.text),
      ].join('\n')
    : ''

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={t('contacts.invite.title')}
      subtitle={t('contacts.invite.subtitle', { name: contactName })}
      size="lg"
      footer={
        <>
          <Button variant="secondary" size="sm" onClick={onClose}>
            {t(result ? 'common.actions.done' : 'common.actions.cancel')}
          </Button>
          {result ? (
            <Button variant="secondary" size="sm" onClick={() => setResult(null)}>
              {t('contacts.invite.newCode')}
            </Button>
          ) : (
            <Button size="sm" onClick={create} disabled={busy || picked.length === 0}>
              {busy && <Loader2 size={13} className="animate-spin" aria-hidden />}
              {t('contacts.invite.create', { count: Math.max(1, picked.length) })}
            </Button>
          )}
        </>
      }
    >
      {error && (
        <div role="alert" className="mb-3 rounded-lg border border-red/30 bg-red/10 px-3 py-2 text-[12px] font-semibold text-red-300">
          {error}
        </div>
      )}

      {!result ? (
        <div className="flex flex-col gap-3">
          <p className="text-[12.5px] leading-relaxed text-text-mid">{t('contacts.invite.intro')}</p>
          <div className="eyebrow">{t('contacts.invite.apps')}</div>
          <div className="flex flex-wrap gap-2">
            {INVITE_CHANNELS.map((c) => {
              const on = picked.includes(c.value)
              const connected = available.includes(c.value)
              return (
                <button
                  key={c.value}
                  type="button"
                  aria-pressed={on}
                  onClick={() => toggle(c.value)}
                  className={`inline-flex items-center gap-2 rounded-lg border px-3 py-2 text-[12.5px] font-semibold transition-colors ${
                    on ? 'border-text-primary bg-[var(--nav-active-bg)] text-text-heading' : 'border-line bg-card text-text-mid hover:bg-fill-hover'
                  }`}
                >
                  <ChannelLogo channel={LOGO[c.value] ?? c.value} size={15} title="" />
                  {c.value === 'email' ? t('chats.channels.email') : c.label}
                  {!connected && <span className="text-[10.5px] font-normal text-text-faint">{t('contacts.invite.notConnected')}</span>}
                  {on && <Check size={12} aria-hidden />}
                </button>
              )
            })}
          </div>
          <p className="text-[11.5px] text-text-muted">
            {t('contacts.invite.noAccountHint')}
          </p>
        </div>
      ) : (
        <div className="flex flex-col gap-4">
          <div className="flex flex-wrap items-center justify-between gap-3 rounded-xl border border-line bg-fill-subtle px-4 py-3">
            <div>
              <div className="eyebrow">{t('contacts.invite.code')}</div>
              <div className="font-mono text-[22px] font-bold tracking-[0.18em] text-text-heading">{result.code}</div>
              {result.expires_at && (
                <div className="text-[11.5px] text-text-muted">
                  {t('contacts.invite.validUntil', { date: f.date(result.expires_at) })}
                </div>
              )}
            </div>
            <Button variant="secondary" size="sm" onClick={() => copy('all', allText)}>
              {copied === 'all' ? <Check size={13} aria-hidden /> : <Copy size={13} aria-hidden />}
              {t('contacts.invite.copyMessage')}
            </Button>
          </div>

          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
            {result.links.map((l) => (
              <div key={l.channel} className="flex min-w-0 flex-col gap-2 rounded-xl border border-line bg-card p-3">
                <div className="flex items-center gap-2">
                  <ChannelLogo channel={LOGO[l.channel] ?? l.channel} size={22} variant="tile" title="" />
                  <div className="min-w-0 flex-1">
                    <div className="text-[13px] font-semibold text-text-high">{l.label}</div>
                    {l.account_name && (
                      <div className="truncate text-[11px] text-text-muted">{t('contacts.invite.via', { name: l.account_name })}</div>
                    )}
                  </div>
                </div>
                {l.qr_svg && (
                  // eslint-disable-next-line @next/next/no-img-element
                  <img
                    src={svgDataUri(l.qr_svg)}
                    alt={t('contacts.invite.qrAlt', { app: l.label })}
                    className="mx-auto h-36 w-36 rounded-lg bg-white p-1.5"
                  />
                )}
                {l.url ? (
                  <div className="flex items-center gap-1.5">
                    <code className="min-w-0 flex-1 truncate rounded-md bg-fill px-2 py-1 text-[11px] text-text-mid">{l.url}</code>
                    <button
                      type="button"
                      onClick={() => copy(l.channel, l.url!)}
                      aria-label={t('contacts.invite.copyLinkAria', { app: l.label })}
                      title={t('contacts.invite.copyLink')}
                      className="grid h-7 w-7 shrink-0 place-items-center rounded-md border border-line text-text-muted hover:text-text-high"
                    >
                      {copied === l.channel ? <Check size={12} aria-hidden /> : <Copy size={12} aria-hidden />}
                    </button>
                  </div>
                ) : (
                  <p className="rounded-md bg-fill px-2 py-1.5 text-[11.5px] text-text-mid">{l.text}</p>
                )}
                {l.note && <p className="text-[11px] leading-relaxed text-text-muted">{l.note}</p>}
              </div>
            ))}
          </div>

          {threads.length > 0 && (
            <div>
              <div className="eyebrow mb-1.5">{t('contacts.invite.sendInChat')}</div>
              <div className="flex flex-wrap gap-2">
                {threads.map((th) => (
                  <Button
                    key={`${th.channel}:${th.key}#${th.thread_id ?? ''}`}
                    variant="secondary"
                    size="sm"
                    onClick={() =>
                      router.push(
                        `/chats?thread=${encodeURIComponent(`${th.channel}:${th.key}`)}${th.thread_id ? `&thread_id=${th.thread_id}` : ''}&draft=${encodeURIComponent(allText)}`,
                      )
                    }
                  >
                    <Send size={12} aria-hidden />
                    {th.label}
                  </Button>
                ))}
              </div>
              <p className="mt-1.5 text-[11px] text-text-muted">{t('contacts.invite.sendHint')}</p>
            </div>
          )}
        </div>
      )}
    </Modal>
  )
}
