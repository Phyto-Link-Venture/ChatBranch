'use client'

import { useEffect, useMemo, useState } from 'react'
import { Loader2, PhoneCall, PhoneOff, Send, Sparkles, UserRound } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Select } from '@/components/ui/Select'
import { useApp } from '@/lib/AppContext'
import { PERSONA_LABELS } from '@/lib/personas'
import { Trans, useT } from '@/i18n'

/** In-chat SIP phone-call panel for a linked contact. Dials the contact's real
 *  number through the shared multi-call seams in AppContext (same voice agent as
 *  the /live-calls page) and surfaces the live transcript + steer controls right
 *  in the info panel — the first step of folding /live-calls into /chats. */
export default function VoiceCallPanel({ contactId }: { contactId: number }) {
  const t = useT()
  const { calls, voices, dialContact, sendSay, hangupCall, escalateCall, setHumanControl } = useApp()
  const [draft, setDraft] = useState('')
  const [voiceId, setVoiceId] = useState('')
  const [now, setNow] = useState(0)

  // This contact's current SIP call, if any (most recent wins).
  const call = useMemo(
    () =>
      Object.values(calls)
        .filter((c) => c.contactId === contactId)
        .sort((a, b) => b.startedAt - a.startedAt)[0],
    [calls, contactId],
  )

  const active = !!call && ['dialing', 'ringing', 'live'].includes(call.status)
  const ended = !!call && ['ended', 'error'].includes(call.status)

  // Tick only while a call is live so the timer advances without idle churn.
  useEffect(() => {
    if (!active) return
    const id = setInterval(() => setNow(Date.now()), 1000)
    return () => clearInterval(id)
  }, [active])

  const human = !!call?.humanControl
  const secs = call ? Math.max(0, Math.floor((now - call.startedAt) / 1000)) : 0
  const timer = `${Math.floor(secs / 60)}:${String(secs % 60).padStart(2, '0')}`

  const send = (mode: 'guide' | 'takeover') => {
    if (!call || !draft.trim()) return
    sendSay(call.room, draft.trim(), mode)
    setDraft('')
  }

  const startCall = () => {
    const v = voices.find((x) => x.id === voiceId)
    dialContact(contactId, v?.id, v?.provider)
  }

  return (
    <div>
      <div className="mb-1.5 flex items-center gap-1.5 text-[10.5px] font-bold uppercase tracking-[0.08em] text-text-muted">
        <PhoneCall size={11} aria-hidden /> {t('chats.call.aiVoiceCall')}
        {active && <span className="ml-auto font-semibold normal-case text-text-mid">{timer}</span>}
      </div>

      <div className="space-y-1.5">
        {active && (
          <div className="space-y-1.5 rounded-lg border border-line bg-app/40 p-2">
            <div className="flex items-center justify-between">
              <span className="text-[10px] font-semibold uppercase tracking-[0.08em] text-text-muted">
                {t('chats.call.liveTranscript')}
              </span>
              <span className="text-[10px] text-text-faint">
                {call.status === 'live'
                  ? t('chats.call.inCall')
                  : call.status === 'dialing'
                    ? t('chats.call.dialing')
                    : call.status === 'ringing'
                      ? t('chats.call.ringingPhone')
                      : call.statusText}
              </span>
            </div>
            <div className="max-h-44 space-y-1 overflow-y-auto pr-1 text-[12px] leading-snug">
              {call.turns.length === 0 && call.extraLines.length === 0 ? (
                <div className="flex items-center gap-1.5 py-1 text-text-faint">
                  <Loader2 size={12} className="animate-spin" aria-hidden />
                  {t(call.status === 'live' ? 'chats.call.waitingAgent' : 'chats.call.connectingCall')}
                </div>
              ) : (
                <>
                  {call.turns.map((turn, i) => (
                    <div key={i} className={turn.who === 'ai' ? 'text-brand-200' : 'text-text-mid'}>
                      <span className="font-semibold">{t(turn.who === 'ai' ? 'chats.parts.ai' : 'chats.parts.caller')}:</span>{' '}
                      {turn.t}
                    </div>
                  ))}
                  {call.extraLines.map((l, i) => (
                    <div key={'x' + i} className={l.human ? 'text-amber-300' : 'text-brand-200'}>
                      <span className="font-semibold">{t(l.human ? 'chats.parts.you' : 'chats.parts.ai')}:</span> {l.t}
                    </div>
                  ))}
                </>
              )}
            </div>
            <textarea
              value={draft}
              onChange={(e) => setDraft(e.target.value)}
              rows={2}
              placeholder={t(human ? 'chats.call.steerHuman' : 'chats.call.steer')}
              className="w-full resize-none rounded-md border border-line bg-app px-2.5 py-1.5 text-[12px] text-text-strong placeholder:text-text-faint focus:border-brand-400 focus:outline-none"
            />
            <div className="flex gap-1.5">
              <Button variant="secondary" onClick={() => send('guide')} disabled={!draft.trim()} style={{ flex: 1 }}>
                <Sparkles size={13} aria-hidden /> {t('chats.call.guide')}
              </Button>
              <Button variant="secondary" onClick={() => send('takeover')} disabled={!draft.trim()} style={{ flex: 1 }}>
                <Send size={13} aria-hidden /> {t('chats.call.takeover')}
              </Button>
            </div>
            <div className="flex gap-1.5">
              <Button variant="secondary" onClick={() => setHumanControl(call.room, !human)} style={{ flex: 1 }}>
                <UserRound size={13} aria-hidden /> {t(human ? 'chats.call.returnAi' : 'chats.call.takeOver')}
              </Button>
              <Button variant="secondary" onClick={() => escalateCall(call.room)} style={{ flex: 1 }}>
                <UserRound size={13} aria-hidden /> {t('chats.call.escalate')}
              </Button>
            </div>
            <Button variant="danger" onClick={() => hangupCall(call.room)} style={{ width: '100%' }}>
              <PhoneOff size={13} aria-hidden /> {t('chats.call.endCall')}
            </Button>
          </div>
        )}

        {ended && call.summary && (
          <div className="rounded-lg border border-line bg-fill px-3 py-2 text-[11px] leading-relaxed text-text-mid">
            <Trans
              k="chats.call.outcome"
              values={{ outcome: <b className="text-text-high">{call.summary.outcome.replace(/_/g, ' ')}</b> }}
            />
            {call.summary.followup_date ? ` · ${t('chats.call.followUp', { date: call.summary.followup_date })}` : ''}
            {call.summary.needs_human ? ` · ${t('chats.call.needsHuman')}` : ''}
          </div>
        )}

        {!active && (
          <>
            {voices.length > 0 && (
              <Select
                value={voiceId}
                onChange={setVoiceId}
                options={[
                  { value: '', label: t('chats.call.defaultVoice') },
                  ...voices.map((v) => ({ value: v.id, label: `${v.name} — ${PERSONA_LABELS[v.persona] ?? v.persona}` })),
                ]}
                ariaLabel={t('chats.call.agentVoiceForCall')}
              />
            )}
            <Button variant="success" onClick={startCall} style={{ width: '100%' }}>
              <PhoneCall size={13} aria-hidden /> {t('chats.call.callPhoneSip')}
            </Button>
            <p className="text-[10.5px] leading-relaxed text-text-muted">
              {t('chats.call.dialHint')}
            </p>
          </>
        )}

        {/* past-call history deliberately NOT listed here — every SIP call is
            interleaved into the chat timeline at the moment it happened, so a
            sidebar copy just grows unbounded (same rule as WhatsApp recordings) */}
      </div>
    </div>
  )
}
