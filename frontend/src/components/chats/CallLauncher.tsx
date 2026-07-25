'use client'

import { useState } from 'react'
import { Phone, PhoneCall, X } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Select } from '@/components/ui/Select'
import { useApp } from '@/lib/AppContext'
import { PERSONA_LABELS } from '@/lib/personas'
import { LANGUAGES } from '@/lib/languages'

/** Slim outbound-call launcher for the Chats page: place a one-off manual call
 *  (raw number) or fan out a batch to selected contacts — the two things the
 *  /live-calls dial bar did that don't belong to any single chat thread. Uses the
 *  same AppContext seams, so calls appear in the fleet + each contact's chat. */
export default function CallLauncher() {
  const { contacts, voices, dialManual, launchBatch, calls, queuedCount, maxConcurrent, openCampaign } = useApp()
  const [open, setOpen] = useState(false)
  const [mode, setMode] = useState<'contacts' | 'manual'>('contacts')
  const [batchSel, setBatchSel] = useState<number[]>([])
  const [number, setNumber] = useState('+60')
  const [name, setName] = useState('')
  const [lang, setLang] = useState('en')
  const [voiceId, setVoiceId] = useState('')

  const liveCount = Object.values(calls).filter((c) =>
    ['dialing', 'ringing', 'live'].includes(c.status),
  ).length

  const manual = number.trim()
  const validManual = manual.startsWith('+') && manual.length >= 8
  const canCall = mode === 'manual' ? validManual : batchSel.length > 0

  const toggle = (id: number) =>
    setBatchSel((p) => (p.includes(id) ? p.filter((x) => x !== id) : [...p, id]))

  const start = () => {
    const v = voices.find((x) => x.id === voiceId)
    if (mode === 'manual') {
      if (!validManual) return
      dialManual(manual, name.trim(), lang, v?.id, v?.provider)
    } else {
      if (!batchSel.length) return
      launchBatch(batchSel, v?.id, v?.provider)
    }
    setBatchSel([])
    setOpen(false)
  }

  return (
    <>
      <button
        onClick={() => setOpen(true)}
        title="Place an outbound call"
        aria-label="New call"
        className="grid h-8 w-8 place-items-center rounded-lg border border-line bg-fill text-text-muted hover:text-text-high"
      >
        <PhoneCall size={15} aria-hidden />
      </button>

      {open && (
        <div
          onClick={() => setOpen(false)}
          className="fixed inset-0 z-50 flex animate-fadeIn items-center justify-center p-6"
          style={{ background: 'rgba(4,8,18,0.72)' }}
        >
          <div
            onClick={(e) => e.stopPropagation()}
            className="flex w-full max-w-[460px] flex-col overflow-hidden rounded-2xl border border-line-strong bg-surface"
            style={{ boxShadow: '0 24px 60px rgba(0,0,0,0.5)' }}
          >
            <div className="flex items-center gap-2.5 border-b border-line px-5 py-4">
              <Phone size={15} className="text-brand-300" aria-hidden />
              <div className="min-w-0 flex-1">
                <div className="text-[14px] font-extrabold tracking-[-0.3px] text-text-primary">Place a call</div>
                <div className="mt-0.5 text-[11px] text-text-muted">
                  {liveCount} / {maxConcurrent} live{queuedCount ? ` · ${queuedCount} queued` : ''}
                </div>
              </div>
              <button
                onClick={() => setOpen(false)}
                aria-label="Close"
                className="grid h-[30px] w-[30px] place-items-center rounded-lg border border-line bg-fill text-text-muted hover:text-text-high"
              >
                <X size={15} aria-hidden />
              </button>
            </div>

            <div className="flex flex-col gap-3 px-5 py-4">
              <div className="flex overflow-hidden rounded-md border border-line-strong">
                {(['contacts', 'manual'] as const).map((m) => (
                  <button
                    key={m}
                    onClick={() => setMode(m)}
                    className="flex-1 px-2.5 py-1.5 text-[12px] font-semibold"
                    style={{
                      background: mode === m ? 'rgba(16,185,129,0.15)' : 'var(--fill)',
                      color: mode === m ? 'var(--brand-ink)' : 'var(--text-mid)',
                    }}
                  >
                    {m === 'contacts' ? 'Contacts (batch)' : 'Manual number'}
                  </button>
                ))}
              </div>

              {voices.length > 0 && (
                <Select
                  value={voiceId}
                  onChange={setVoiceId}
                  options={[
                    { value: '', label: 'Default voice' },
                    ...voices.map((v) => ({ value: v.id, label: `${v.name} — ${PERSONA_LABELS[v.persona] ?? v.persona}` })),
                  ]}
                  ariaLabel="Agent voice"
                />
              )}

              {mode === 'manual' ? (
                <>
                  <input
                    value={number}
                    onChange={(e) => setNumber(e.target.value)}
                    placeholder="+60123456789"
                    className="w-full rounded-lg border border-line-strong bg-card px-3 py-2 font-mono text-[12.5px] text-text-primary outline-none focus:border-brand-400"
                  />
                  <input
                    value={name}
                    onChange={(e) => setName(e.target.value)}
                    placeholder="Name (optional)"
                    className="w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[12.5px] text-text-primary outline-none focus:border-brand-400"
                  />
                  <Select value={lang} onChange={setLang} options={LANGUAGES} ariaLabel="Call language" />
                </>
              ) : (
                <div className="max-h-[280px] overflow-y-auto rounded-lg border border-line">
                  <div className="flex items-center justify-between px-3 py-2 text-[11px] font-bold">
                    <button onClick={() => setBatchSel(contacts.map((c) => c.id))} className="text-brand-300">
                      Select all
                    </button>
                    <button onClick={() => setBatchSel([])} className="text-text-muted">
                      Clear
                    </button>
                  </div>
                  {contacts.map((c) => (
                    <label key={c.id} className="flex cursor-pointer items-center gap-2.5 px-3 py-1.5 hover:bg-fill">
                      <input
                        type="checkbox"
                        checked={batchSel.includes(c.id)}
                        onChange={() => toggle(c.id)}
                        className="h-3.5 w-3.5 accent-brand-400"
                      />
                      <div className="min-w-0 flex-1">
                        <div className="truncate text-[12px] font-semibold text-text-high">{c.name}</div>
                        <div className="truncate text-[10.5px] text-text-muted">{c.phone}</div>
                      </div>
                    </label>
                  ))}
                </div>
              )}
            </div>

            <div className="flex items-center gap-2 border-t border-line px-5 py-3.5">
              <Button
                variant="ghost"
                onClick={() => {
                  setOpen(false)
                  openCampaign()
                }}
                style={{ marginRight: 'auto' }}
              >
                Advanced campaign…
              </Button>
              <Button variant="ghost" onClick={() => setOpen(false)}>
                Cancel
              </Button>
              <Button variant="success" onClick={start} disabled={!canCall} style={{ opacity: canCall ? 1 : 0.5 }}>
                <PhoneCall size={13} aria-hidden />{' '}
                {mode === 'manual'
                  ? liveCount >= maxConcurrent
                    ? 'Queue call'
                    : 'Call'
                  : `Call ${batchSel.length || ''}`.trim()}
              </Button>
            </div>
          </div>
        </div>
      )}
    </>
  )
}
