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

const LOGO: Record<string, string> = { facebook: 'messenger' }

export interface InviteThread {
  channel: string
  key: string
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
      setError('Copy failed — select the text and copy it by hand.')
    }
  }

  const allText = result
    ? [`Hi ${contactName.split(' ')[0] || ''}! You can also reach us here:`.replace(' !', '!'), ...result.links.map((l) => l.text)].join(
        '\n',
      )
    : ''

  const expires = result ? new Date(result.expires_at) : null

  return (
    <Modal
      open={open}
      onClose={onClose}
      title="Invite to another app"
      subtitle={`Links that connect ${contactName}'s other apps to this contact. One code, 30 days, once per app.`}
      size="lg"
      footer={
        <>
          <Button variant="secondary" size="sm" onClick={onClose}>
            {result ? 'Done' : 'Cancel'}
          </Button>
          {result ? (
            <Button variant="secondary" size="sm" onClick={() => setResult(null)}>
              New code
            </Button>
          ) : (
            <Button size="sm" onClick={create} disabled={busy || picked.length === 0}>
              {busy && <Loader2 size={13} className="animate-spin" aria-hidden />}
              Create invite link{picked.length > 1 ? 's' : ''}
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
          <p className="text-[12.5px] leading-relaxed text-text-mid">
            Messenger, Instagram, Telegram and most chat apps don&apos;t let anyone look a customer up by phone number.
            An invite link does it the other way round: the customer opens it, says hi, and that conversation joins
            this contact automatically.
          </p>
          <div className="eyebrow">Apps</div>
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
                  {c.label}
                  {!connected && <span className="text-[10.5px] font-normal text-text-faint">not connected</span>}
                  {on && <Check size={12} aria-hidden />}
                </button>
              )
            })}
          </div>
          <p className="text-[11.5px] text-text-muted">
            An app with no connected account still gets a code the customer can type into their first message.
          </p>
        </div>
      ) : (
        <div className="flex flex-col gap-4">
          <div className="flex flex-wrap items-center justify-between gap-3 rounded-xl border border-line bg-fill-subtle px-4 py-3">
            <div>
              <div className="eyebrow">Invite code</div>
              <div className="font-mono text-[22px] font-bold tracking-[0.18em] text-text-heading">{result.code}</div>
              {expires && (
                <div className="text-[11.5px] text-text-muted">
                  Valid until {expires.toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' })}
                </div>
              )}
            </div>
            <Button variant="secondary" size="sm" onClick={() => copy('all', allText)}>
              {copied === 'all' ? <Check size={13} aria-hidden /> : <Copy size={13} aria-hidden />}
              Copy message
            </Button>
          </div>

          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
            {result.links.map((l) => (
              <div key={l.channel} className="flex min-w-0 flex-col gap-2 rounded-xl border border-line bg-card p-3">
                <div className="flex items-center gap-2">
                  <ChannelLogo channel={LOGO[l.channel] ?? l.channel} size={22} variant="tile" title="" />
                  <div className="min-w-0 flex-1">
                    <div className="text-[13px] font-semibold text-text-high">{l.label}</div>
                    {l.account_name && <div className="truncate text-[11px] text-text-muted">via {l.account_name}</div>}
                  </div>
                </div>
                {l.qr_svg && (
                  // eslint-disable-next-line @next/next/no-img-element
                  <img
                    src={svgDataUri(l.qr_svg)}
                    alt={`QR code for the ${l.label} invite link`}
                    className="mx-auto h-36 w-36 rounded-lg bg-white p-1.5"
                  />
                )}
                {l.url ? (
                  <div className="flex items-center gap-1.5">
                    <code className="min-w-0 flex-1 truncate rounded-md bg-fill px-2 py-1 text-[11px] text-text-mid">{l.url}</code>
                    <button
                      type="button"
                      onClick={() => copy(l.channel, l.url!)}
                      aria-label={`Copy the ${l.label} link`}
                      title="Copy link"
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
              <div className="eyebrow mb-1.5">Send it in a chat</div>
              <div className="flex flex-wrap gap-2">
                {threads.map((t) => (
                  <Button
                    key={`${t.channel}:${t.key}`}
                    variant="secondary"
                    size="sm"
                    onClick={() =>
                      router.push(
                        `/chats?thread=${encodeURIComponent(`${t.channel}:${t.key}`)}&draft=${encodeURIComponent(allText)}`,
                      )
                    }
                  >
                    <Send size={12} aria-hidden />
                    {t.label}
                  </Button>
                ))}
              </div>
              <p className="mt-1.5 text-[11px] text-text-muted">Opens the chat with the invite in the composer — review it, then send.</p>
            </div>
          )}
        </div>
      )}
    </Modal>
  )
}
