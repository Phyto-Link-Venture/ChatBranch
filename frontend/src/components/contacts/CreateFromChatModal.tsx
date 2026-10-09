'use client'

/**
 * "Create contact" from an unknown customer's chat — prefilled with what the
 * customer told the "who are you?" exchange (else what the chat itself knows:
 * a WhatsApp number, an email sender). POST
 * /api/contacts/identify/{channel}/{key}/contact creates the contact, links
 * the conversation, closes the request and — when the customer was waiting —
 * lets the AI answer them once.
 */

import { useEffect, useState } from 'react'
import { Loader2, UserPlus } from 'lucide-react'
import { Modal } from '@/components/ui/Modal'
import { Button } from '@/components/ui/Button'
import { EntityPicker } from '@/components/ui/EntityPicker'
import { cApi, errText, threadPath, type IdentifyState } from '@/lib/contactIdentity'

const inputCls =
  'h-9 w-full rounded-lg border border-line-strong bg-card px-3 text-[13px] text-text-high placeholder:text-text-faint focus:border-text-primary focus:outline-none'

export function CreateFromChatModal({
  open,
  onClose,
  channel,
  threadKey,
  onCreated,
}: {
  open: boolean
  onClose: () => void
  channel: string
  threadKey: string
  onCreated: (contactId: number) => void
}) {
  const [name, setName] = useState('')
  const [phone, setPhone] = useState('')
  const [email, setEmail] = useState('')
  const [entity, setEntity] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (!open) return
    setError(null)
    setEntity(null)
    setLoading(true)
    let alive = true
    cApi<IdentifyState>(`contacts/identify/${threadPath(channel, threadKey)}`)
      .then((s) => {
        if (!alive) return
        setName(s.suggested.name)
        setPhone(s.suggested.phone)
        setEmail(s.suggested.email)
      })
      .catch(() => {
        if (alive) {
          setName('')
          setPhone('')
          setEmail('')
        }
      })
      .finally(() => alive && setLoading(false))
    return () => {
      alive = false
    }
  }, [open, channel, threadKey])

  const save = async () => {
    if (!name.trim()) {
      setError('Give the contact a name.')
      return
    }
    setBusy(true)
    setError(null)
    try {
      const r = await cApi<{ contact_id: number }>(`contacts/identify/${threadPath(channel, threadKey)}/contact`, {
        method: 'POST',
        body: JSON.stringify({ name: name.trim(), phone: phone.trim(), email: email.trim(), entity: entity ?? '' }),
      })
      onCreated(r.contact_id)
      onClose()
    } catch (e) {
      setError(errText(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title="Create contact from this chat"
      subtitle="The conversation is linked to the new contact straight away."
      size="sm"
      footer={
        <>
          <Button variant="secondary" size="sm" onClick={onClose}>
            Cancel
          </Button>
          <Button size="sm" onClick={save} disabled={busy || loading}>
            {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <UserPlus size={13} aria-hidden />}
            Create &amp; link
          </Button>
        </>
      }
    >
      {error && (
        <div role="alert" className="mb-3 rounded-lg border border-red/30 bg-red/10 px-3 py-2 text-[12px] font-semibold text-red-300">
          {error}
        </div>
      )}
      <div className="flex flex-col gap-3">
        <label className="flex flex-col gap-1">
          <span className="text-[11.5px] font-semibold text-text-mid">Name</span>
          <input className={inputCls} value={name} onChange={(e) => setName(e.target.value)} autoFocus disabled={loading} />
        </label>
        <label className="flex flex-col gap-1">
          <span className="text-[11.5px] font-semibold text-text-mid">Phone</span>
          <input
            className={inputCls}
            value={phone}
            onChange={(e) => setPhone(e.target.value)}
            placeholder="+60 12 345 6789"
            inputMode="tel"
            disabled={loading}
          />
        </label>
        <label className="flex flex-col gap-1">
          <span className="text-[11.5px] font-semibold text-text-mid">Email</span>
          <input
            className={inputCls}
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            placeholder="name@example.com"
            inputMode="email"
            disabled={loading}
          />
        </label>
        <label className="flex flex-col gap-1">
          <span className="text-[11.5px] font-semibold text-text-mid">Entity</span>
          <EntityPicker value={entity} onChange={setEntity} globalLabel="The inbox's entity" ariaLabel="Entity" />
        </label>
      </div>
    </Modal>
  )
}

/** The small trigger the chat info panel shows under "Link to a contact…". */
export function CreateFromChat({
  channel,
  threadKey,
  onCreated,
}: {
  channel: string
  threadKey: string
  onCreated: (contactId: number) => void
}) {
  const [open, setOpen] = useState(false)
  return (
    <>
      <button
        type="button"
        onClick={() => setOpen(true)}
        className="mt-2 inline-flex items-center gap-1 rounded-md px-1 py-0.5 text-[11.5px] font-semibold text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
      >
        <UserPlus size={12} aria-hidden /> Create from this chat
      </button>
      <CreateFromChatModal
        open={open}
        onClose={() => setOpen(false)}
        channel={channel}
        threadKey={threadKey}
        onCreated={onCreated}
      />
    </>
  )
}
