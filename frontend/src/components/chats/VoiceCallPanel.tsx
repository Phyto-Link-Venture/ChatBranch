'use client'

import { useEffect, useMemo, useState } from 'react'
import { Loader2, PhoneCall, PhoneOff, Send, Sparkles, UserRound } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Select } from '@/components/ui/Select'
import { useApp } from '@/lib/AppContext'
import { PERSONA_LABELS } from '@/lib/personas'

/** In-chat SIP phone-call panel for a linked contact. Dials the contact's real
 *  number through the shared multi-call seams in AppContext (same voice agent as
 *  the /live-calls page) and surfaces the live transcript + steer controls right
 *  in the info panel — the first step of folding /live-calls into /chats. */
export default function VoiceCallPanel({ contactId }: { contactId: number }) {
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
        <PhoneCall size={11} aria-hidden /> AI voice call
        {active && <span className="ml-auto font-semibold normal-case text-text-mid">{timer}</span>}
      </div>

      <div className="space-y-1.5">
        {active && (
          <div className="space-y-1.5 rounded-lg border border-line bg-app/40 p-2">
            <div className="flex items-center justify-between">
              <span className="text-[10px] font-semibold uppercase tracking-[0.08em] text-text-muted">
                Live transcript
              </span>
              <span className="text-[10px] text-text-faint">
                {call.status === 'live' ? 'in call' : call.statusText}
              </span>
            </div>
            <div className="max-h-44 space-y-1 overflow-y-auto pr-1 text-[12px] leading-snug">
              {call.turns.length === 0 && call.extraLines.length === 0 ? (
                <div className="flex items-center gap-1.5 py-1 text-text-faint">
                  <Loader2 size={12} className="animate-spin" aria-hidden />
                  {call.status === 'live' ? 'Waiting for the agent to speak…' : 'Connecting the call…'}
                </div>
              ) : (
                <>
                  {call.turns.map((t, i) => (
                    <div key={i} className={t.who === 'ai' ? 'text-brand-200' : 'text-text-mid'}>
                      <span className="font-semibold">{t.who === 'ai' ? 'AI' : 'Caller'}:</span> {t.t}
                    </div>
                  ))}
                  {call.extraLines.map((l, i) => (
                    <div key={'x' + i} className={l.human ? 'text-amber-300' : 'text-brand-200'}>
                      <span className="font-semibold">{l.human ? 'You' : 'AI'}:</span> {l.t}
                    </div>
                  ))}
                </>
              )}
            </div>
            <textarea
              value={draft}
              onChange={(e) => setDraft(e.target.value)}
              rows={2}
              placeholder={human ? 'Type words for the agent to say verbatim…' : 'Steer the AI (Guide) or say verbatim (Takeover)…'}
              className="w-full resize-none rounded-md border border-line bg-app px-2.5 py-1.5 text-[12px] text-text-strong placeholder:text-text-faint focus:border-brand-400 focus:outline-none"
            />
            <div className="flex gap-1.5">
              <Button variant="secondary" onClick={() => send('guide')} disabled={!draft.trim()} style={{ flex: 1 }}>
                <Sparkles size={13} aria-hidden /> Guide
              </Button>
              <Button variant="secondary" onClick={() => send('takeover')} disabled={!draft.trim()} style={{ flex: 1 }}>
                <Send size={13} aria-hidden /> Takeover
              </Button>
            </div>
            <div className="flex gap-1.5">
              <Button variant="secondary" onClick={() => setHumanControl(call.room, !human)} style={{ flex: 1 }}>
                <UserRound size={13} aria-hidden /> {human ? 'Return AI' : 'Take over'}
              </Button>
              <Button variant="secondary" onClick={() => escalateCall(call.room)} style={{ flex: 1 }}>
                <UserRound size={13} aria-hidden /> Escalate
              </Button>
            </div>
            <Button variant="danger" onClick={() => hangupCall(call.room)} style={{ width: '100%' }}>
              <PhoneOff size={13} aria-hidden /> End call
            </Button>
          </div>
        )}

        {ended && call.summary && (
          <div className="rounded-lg border border-line bg-fill px-3 py-2 text-[11px] leading-relaxed text-text-mid">
            Outcome: <b className="text-text-high">{call.summary.outcome.replace(/_/g, ' ')}</b>
            {call.summary.followup_date ? ` · follow-up ${call.summary.followup_date}` : ''}
            {call.summary.needs_human ? ' · needs human' : ''}
          </div>
        )}

        {!active && (
          <>
            {voices.length > 0 && (
              <Select
                value={voiceId}
                onChange={setVoiceId}
                options={[
                  { value: '', label: 'Default voice' },
                  ...voices.map((v) => ({ value: v.id, label: `${v.name} — ${PERSONA_LABELS[v.persona] ?? v.persona}` })),
                ]}
                ariaLabel="Agent voice for the call"
              />
            )}
            <Button variant="success" onClick={startCall} style={{ width: '100%' }}>
              <PhoneCall size={13} aria-hidden /> Call phone (SIP)
            </Button>
            <p className="text-[10.5px] leading-relaxed text-text-muted">
              Dials the contact&rsquo;s real number and runs the AI voice agent — watch it live above once it connects.
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
