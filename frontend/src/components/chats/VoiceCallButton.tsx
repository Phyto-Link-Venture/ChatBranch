'use client'

import { Loader2, Phone, PhoneOff } from 'lucide-react'
import { useApp } from '@/lib/AppContext'
import { useT } from '@/i18n'

/** Header quick-action: start / end an AI phone call (SIP) to a linked contact.
 *  Distinct from the WhatsApp-call button next to it — this dials the contact's
 *  real number through the shared call seams (same voice agent). Voice picker +
 *  live monitor live in the info-panel VoiceCallPanel; this is one-tap dial. */
export default function VoiceCallButton({ contactId }: { contactId: number }) {
  const t = useT()
  const { calls, dialContact, hangupCall } = useApp()
  const call = Object.values(calls)
    .filter((c) => c.contactId === contactId)
    .sort((a, b) => b.startedAt - a.startedAt)[0]
  const status = call?.status
  const active = status === 'dialing' || status === 'ringing' || status === 'live'

  if (active) {
    return (
      <button
        onClick={() => hangupCall(call.room)}
        title={t(status === 'live' ? 'chats.call.endAiCall' : 'chats.call.cancelAiCall')}
        aria-label={t('chats.call.endAiCall')}
        className="grid h-8 w-8 cursor-pointer place-items-center rounded-lg bg-red/15 text-red-300 transition-colors hover:bg-red/25"
      >
        {status === 'live' ? <PhoneOff size={15} aria-hidden /> : <Loader2 size={15} className="animate-spin" aria-hidden />}
      </button>
    )
  }
  return (
    <button
      onClick={() => dialContact(contactId)}
      title={t('chats.call.aiCallSip')}
      aria-label={t('chats.call.startAiCall')}
      className="grid h-8 w-8 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-brand-300"
    >
      <Phone size={15} aria-hidden />
    </button>
  )
}
