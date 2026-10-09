'use client'

import { useRouter } from 'next/navigation'
import { Mic, Phone, MessageSquare, X, Check, Zap } from 'lucide-react'
import { useApp } from '@/lib/AppContext'
import { primaryRecord } from '@/lib/mocks'
import { Button } from '@/components/ui/Button'
import { Select } from '@/components/ui/Select'
import { tint } from '@/lib/color'

const METHODS = [
  { id: 'ai-call', label: 'AI Responsive Call', sub: 'Real-time AI conversation', color: 'rgb(var(--c-brand))', Icon: Mic },
  { id: 'bot-call', label: 'Automated Bot Call', sub: 'Interactive IVR flow', color: 'rgb(var(--c-cyan))', Icon: Phone },
  { id: 'message', label: 'AI Message', sub: 'WhatsApp / SMS', color: 'rgb(var(--c-purple))', Icon: MessageSquare },
]
const TONES = ['Professional', 'Firm', 'Empathetic', 'Urgent']

export function CampaignModal() {
  const router = useRouter()
  const {
    campaignModal,
    campaignStep,
    campaignSel,
    launchMethod,
    launchTone,
    selectedVoice,
    voices,
    contacts,
    closeCampaign,
    setCampaignStep,
    toggleCampaign,
    setLaunchMethod,
    setLaunchTone,
    setSelectedVoice,
    launchCampaign,
  } = useApp()

  if (!campaignModal) return null

  const pool = contacts.filter((d) => d.status !== 'resolved')

  const doLaunch = () => {
    launchCampaign()
    router.push('/chats')
  }

  const label = (t: string) => (
    <div className="mb-2.5 text-[11px] font-semibold uppercase tracking-[0.06em] text-text-muted">
      {t}
    </div>
  )

  return (
    <div
      onClick={closeCampaign}
      className="fixed inset-0 z-50 flex animate-fadeIn items-center justify-center p-6 backdrop-blur-sm"
      style={{ background: 'rgba(4,8,18,0.72)' }}
    >
      <div
        onClick={(e) => e.stopPropagation()}
        className="flex max-h-[86vh] w-full max-w-[560px] flex-col overflow-hidden rounded-2xl border border-line-strong bg-surface"
        style={{ boxShadow: '0 24px 60px rgba(0,0,0,0.5)' }}
      >
        {/* Header */}
        <div className="flex items-center justify-between border-b border-line px-[22px] py-[18px]">
          <div>
            <div className="text-[15px] font-extrabold tracking-[-0.4px] text-text-primary">
              New Campaign
            </div>
            <div className="mt-0.5 text-[11px] text-text-muted">
              {campaignStep === 1
                ? 'Step 1 of 2 · Select contacts'
                : 'Step 2 of 2 · Configure & launch'}
            </div>
          </div>
          <button
            onClick={closeCampaign}
            className="flex h-[30px] w-[30px] shrink-0 items-center justify-center rounded-lg border border-line bg-fill text-text-muted"
          >
            <X size={15} strokeWidth={1.75} />
          </button>
        </div>

        {/* Body */}
        {campaignStep === 1 ? (
          <div className="flex-1 overflow-y-auto py-1.5">
            {pool.map((d) => {
              const sel = campaignSel.includes(d.id)
              const record = primaryRecord(d)
              return (
                <div
                  key={d.id}
                  className="trow flex cursor-pointer items-center gap-3 px-[22px] py-[11px]"
                  style={{ background: sel ? 'rgba(255,90,31,0.06)' : 'transparent' }}
                  onClick={() => toggleCampaign(d.id)}
                >
                  <div
                    className="flex h-[18px] w-[18px] shrink-0 items-center justify-center rounded-[5px]"
                    style={{
                      border: `1.5px solid ${sel ? 'rgb(var(--c-brand))' : 'var(--line-strong)'}`,
                      background: sel ? 'rgb(var(--c-brand))' : 'transparent',
                    }}
                  >
                    {sel && <Check size={11} strokeWidth={1.75} color="white" />}
                  </div>
                  <div className="min-w-0 flex-1">
                    <div className="text-[13px] font-semibold text-text-high">
                      {d.name}
                    </div>
                    <div className="text-[11px] text-text-muted">{d.phone}</div>
                  </div>
                  <div className="shrink-0 text-right">
                    {record && (
                      <div className="text-[11px] font-semibold text-text-mid">
                        {record.type} · {record.status}
                      </div>
                    )}
                  </div>
                </div>
              )
            })}
          </div>
        ) : (
          <div className="flex flex-1 flex-col gap-[18px] overflow-y-auto px-[22px] py-[18px]">
            <div>
              {label('Method')}
              <div className="flex flex-col gap-2">
                {METHODS.map((m) => {
                  const sel = launchMethod === m.id
                  return (
                    <div
                      key={m.id}
                      onClick={() => setLaunchMethod(m.id)}
                      className="flex cursor-pointer items-center gap-3 rounded-[10px] px-[13px] py-[11px]"
                      style={{
                        border: `1.5px solid ${sel ? tint(m.color, '55') : 'var(--line)'}`,
                        background: sel ? tint(m.color, '0D') : 'transparent',
                      }}
                    >
                      <div
                        className="flex h-8 w-8 shrink-0 items-center justify-center rounded-lg"
                        style={{ background: tint(m.color, '18') }}
                      >
                        <m.Icon size={15} strokeWidth={1.75} style={{ color: m.color }} />
                      </div>
                      <div className="flex-1">
                        <div className="text-[13px] font-bold text-text-high">
                          {m.label}
                        </div>
                        <div className="text-[11px] text-text-muted">{m.sub}</div>
                      </div>
                      <div
                        className="flex h-4 w-4 shrink-0 items-center justify-center rounded-full"
                        style={{
                          border: `2px solid ${sel ? m.color : 'var(--line-strong)'}`,
                        }}
                      >
                        {sel && (
                          <div
                            className="h-[7px] w-[7px] rounded-full"
                            style={{ background: m.color }}
                          />
                        )}
                      </div>
                    </div>
                  )
                })}
              </div>
            </div>

            {launchMethod === 'ai-call' && (
              <div>
                {label('Voice')}
                <Select
                  value={selectedVoice}
                  onChange={setSelectedVoice}
                  options={voices
                    .filter((v) => v.status === 'ready')
                    .map((v) => ({ value: v.id, label: v.name, hint: v.gender }))}
                  size="md"
                  ariaLabel="Voice"
                  placeholder="Pick a voice…"
                  className="w-full"
                />
              </div>
            )}

            <div>
              {label('Tone')}
              <div className="flex flex-wrap gap-2">
                {TONES.map((t) => {
                  const sel = launchTone === t.toLowerCase()
                  return (
                    <div
                      key={t}
                      onClick={() => setLaunchTone(t.toLowerCase())}
                      className="cursor-pointer rounded-lg px-3.5 py-[7px] text-[12px] font-semibold"
                      style={{
                        border: `1.5px solid ${sel ? 'rgba(255,90,31,0.5)' : 'var(--line)'}`,
                        background: sel ? 'rgba(255,90,31,0.12)' : 'transparent',
                        color: sel ? 'var(--brand-ink)' : 'var(--text-muted)',
                      }}
                    >
                      {t}
                    </div>
                  )
                })}
              </div>
            </div>
          </div>
        )}

        {/* Footer */}
        <div className="flex items-center justify-between gap-3 border-t border-line px-[22px] py-4">
          <div className="text-[12px] font-semibold text-text-muted">
            {campaignSel.length ? `${campaignSel.length} selected` : 'No contacts selected'}
          </div>
          <div className="flex gap-2">
            {campaignStep === 2 && (
              <Button variant="secondary" style={{ fontSize: 12 }} onClick={() => setCampaignStep(1)}>
                Back
              </Button>
            )}
            {campaignStep === 1 ? (
              <Button
                variant="primary"
                style={{
                  fontSize: 12,
                  opacity: campaignSel.length ? 1 : 0.5,
                  cursor: campaignSel.length ? 'pointer' : 'not-allowed',
                }}
                onClick={() => campaignSel.length && setCampaignStep(2)}
              >
                Next
              </Button>
            ) : (
              <button
                onClick={doLaunch}
                className="flex cursor-pointer items-center gap-[7px] rounded-lg px-[18px] py-[9px] text-[13px] font-bold text-white"
                style={{ background: 'var(--primary)' }}
              >
                <Zap size={15} strokeWidth={1.75} color="white" />
                Launch &amp; Go Live
              </button>
            )}
          </div>
        </div>
      </div>
    </div>
  )
}
