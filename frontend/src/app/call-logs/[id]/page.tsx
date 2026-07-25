'use client'

import { useParams, useRouter } from 'next/navigation'
import {
  ArrowLeft,
  ChevronRight,
  Mic,
  Phone,
  MessageSquare,
  Shield,
  User,
  TrendingUp,
  CheckCircle,
  type LucideIcon,
} from 'lucide-react'
import { useApp } from '@/lib/AppContext'
import { CALL_LOGS, CALL_TRANSCRIPTS, primaryRecord } from '@/lib/mocks'
import { StatusBadge, LogStatusBadge } from '@/components/ui/Badges'
import { Button } from '@/components/ui/Button'

const TYPE_COLORS: Record<string, string> = { 'ai-call': '#3B82F6', 'bot-call': '#06B6D4', message: '#8B5CF6' }
const TYPE_ICONS: Record<string, LucideIcon> = { 'ai-call': Mic, 'bot-call': Phone, message: MessageSquare }
const TYPE_LABELS: Record<string, string> = {
  'ai-call': 'AI Responsive Call',
  'bot-call': 'Automated Bot Call',
  message: 'AI Message',
}

const SENTIMENT: Record<string, string> = {
  completed: 'Cooperative',
  voicemail: 'Unreachable',
  'no-answer': 'Unreachable',
  'callback-requested': 'Hesitant',
  read: 'Engaged',
  delivered: 'Neutral',
  active: 'Cooperative',
}
const BEHAVIOR: Record<number, string[]> = {
  1: ['Acknowledged the issue', 'Requested a callback', 'Agreed on next step'],
  2: ['Cooperative', 'Confirmed resolved', 'Fast response'],
  3: ['Unreachable', 'Voicemail only'],
  4: ['No answer', 'Multiple attempts'],
  5: ['Answered call', 'Requested callback'],
  7: ['Cooperative', 'Discussing options'],
}
const FOLLOWUP: Record<number, string> = { 1: 'Review by 15 Jul', 2: 'Resolved', 5: 'Callback at 14:00' }
const RESOLUTION_RATE: Record<number, number> = { 1: 72, 2: 91, 3: 40, 4: 55, 5: 64 }

function Metric({ k, v, c }: { k: string; v: string; c?: string }) {
  return (
    <div>
      <div className="mb-[3px] text-[10px] text-text-muted">{k}</div>
      <div className="text-[13px] font-bold" style={{ color: c || 'var(--text-high)' }}>
        {v}
      </div>
    </div>
  )
}

function CardTitle({ Icon, t }: { Icon: LucideIcon; t: string }) {
  return (
    <div className="mb-3.5 flex items-center gap-2">
      <Icon size={15} strokeWidth={1.75} color="#93C5FD" />
      <div className="text-[13px] font-bold text-text-high">{t}</div>
    </div>
  )
}

export default function CallDetailPage() {
  const router = useRouter()
  const params = useParams()
  const { contacts } = useApp()
  const id = Number(params.id)
  const log = CALL_LOGS.find((l) => l.id === id)

  if (!log) return <div className="text-text-muted">No call selected.</div>

  const d = contacts.find((x) => x.name === log.contact)
  const record = d ? primaryRecord(d) : null
  const color = TYPE_COLORS[log.type] || '#64748B'
  const Icon = TYPE_ICONS[log.type] || Phone

  const sentiment = SENTIMENT[log.status] || 'Neutral'
  const sentColor =
    sentiment === 'Cooperative' || sentiment === 'Engaged'
      ? '#6EE7B7'
      : sentiment === 'Hesitant' || sentiment === 'Neutral'
      ? '#FCD34D'
      : '#FCA5A5'
  const tags = BEHAVIOR[log.id] || ['Contacted']
  const followup = FOLLOWUP[log.id] || 'None recorded'
  const resolutionRate = RESOLUTION_RATE[log.id] ?? 55
  const transcript = CALL_TRANSCRIPTS[log.id] || [{ who: 'ai' as const, t: 'Call connected. ' + log.summary }]

  return (
    <div className="flex flex-col gap-4">
      {/* Breadcrumb */}
      <div className="flex items-center gap-3">
        <button
          onClick={() => router.push('/call-logs')}
          className="flex items-center gap-1.5 rounded-[7px] border border-line bg-fill px-2.5 py-1.5 text-[12px] font-semibold text-text-muted"
        >
          <ArrowLeft size={14} strokeWidth={1.75} color="#64748B" />
          Call Logs
        </button>
        <ChevronRight size={12} strokeWidth={1.75} color="#374151" />
        <span className="text-[12px] text-text-muted">{log.contact}</span>
      </div>

      {/* Header */}
      <div className="flex flex-wrap items-center gap-4 rounded-xl border border-line bg-card px-6 py-5">
        <div
          className="flex h-12 w-12 shrink-0 items-center justify-center rounded-[13px]"
          style={{ background: color + '18' }}
        >
          <Icon size={22} strokeWidth={1.75} color={color} />
        </div>
        <div className="min-w-0 flex-1">
          <div className="mb-1 flex flex-wrap items-center gap-2.5">
            <div className="text-[18px] font-extrabold tracking-[-0.5px] text-text-primary">
              {log.contact}
            </div>
            <LogStatusBadge status={log.status} />
          </div>
          <div className="text-[12px] text-text-muted">
            {TYPE_LABELS[log.type] || ''} · {log.phone} · {log.datetime}
          </div>
        </div>
        <div className="text-right">
          <div className="mb-0.5 text-[10px] text-text-muted">Duration</div>
          <div className="text-[22px] font-extrabold tracking-[-0.5px] text-text-primary">
            {log.duration || '—'}
          </div>
        </div>
      </div>

      {/* Two columns */}
      <div className="flex flex-wrap items-start gap-3.5">
        {/* Left — summary + transcript */}
        <div className="flex min-w-0 flex-[1_1_460px] flex-col gap-3.5">
          <div className="rounded-xl border border-line bg-card px-5 py-[18px]">
            <CardTitle Icon={Shield} t="AI Summary" />
            {/* the summary is the page's most important read — big and bold */}
            <div className="text-[15px] font-semibold leading-[1.65] text-text-high">{log.summary}</div>
            <div className="mt-3.5 flex items-center justify-between border-t border-line pt-3.5">
              <span className="text-[11px] text-text-muted">Follow-up</span>
              <span
                className="text-[12px] font-bold"
                style={{ color: followup === 'None recorded' ? 'var(--text-muted)' : '#6EE7B7' }}
              >
                {followup}
              </span>
            </div>
          </div>
          <div className="rounded-xl border border-line bg-card px-5 py-[18px]">
            <CardTitle Icon={MessageSquare} t="Full Transcript" />
            <div className="flex flex-col gap-3">
              {transcript.map((l, i) => {
                const ai = l.who === 'ai'
                return (
                  <div
                    key={i}
                    className="flex items-end gap-2.5"
                    style={{ flexDirection: ai ? 'row' : 'row-reverse' }}
                  >
                    <div
                      className="flex h-[26px] w-[26px] shrink-0 items-center justify-center rounded-full text-[9px] font-extrabold"
                      style={{
                        color: ai ? '#93C5FD' : 'var(--text-high)',
                        background: ai ? 'rgba(59,130,246,0.18)' : 'var(--fill)',
                      }}
                    >
                      {ai ? 'AI' : 'CT'}
                    </div>
                    <div className="max-w-[74%]">
                      <div
                        className="text-[12.5px] leading-[1.55] text-text-high"
                        style={{
                          padding: '10px 13px',
                          borderRadius: ai ? '4px 12px 12px 12px' : '12px 4px 12px 12px',
                          background: ai ? 'rgba(59,130,246,0.12)' : 'var(--fill)',
                          border: `1px solid ${ai ? 'rgba(59,130,246,0.2)' : 'var(--line)'}`,
                        }}
                      >
                        {l.t}
                      </div>
                      {l.kb && (
                        <div className="mt-1.5 flex items-center gap-1.5 text-[10px] font-semibold text-green-300">
                          <Shield size={11} strokeWidth={1.75} color="#6EE7B7" />
                          Answered from KB: {l.kb}
                        </div>
                      )}
                    </div>
                  </div>
                )
              })}
            </div>
          </div>
        </div>

        {/* Right — contact / behavior / performance */}
        <div className="flex flex-[0_0_320px] flex-col gap-3.5">
          {/* Contact */}
          <div className="rounded-xl border border-line bg-card px-5 py-[18px]">
            <CardTitle Icon={User} t="Contact" />
            {d ? (
              <div>
                <div className="mb-3.5 grid grid-cols-2 gap-3">
                  <Metric k="Record" v={record ? `${record.type} · ${record.status}` : '—'} />
                  <Metric k="Due" v={record?.due_date || '—'} />
                  <Metric k="Account" v={'#CT-' + String(d.id).padStart(4, '0')} />
                  <Metric k="Tags" v={d.tags.length ? d.tags.join(', ') : '—'} />
                </div>
                <div className="flex flex-wrap gap-1.5">
                  <StatusBadge status={d.status} />
                </div>
                <Button
                  variant="secondary"
                  style={{ marginTop: 14, width: '100%', justifyContent: 'center', fontSize: 12 }}
                  onClick={() => router.push(`/contacts/${d.id}`)}
                >
                  Open Contact Profile
                </Button>
              </div>
            ) : (
              <div className="text-[12px] text-text-muted">Contact record not found.</div>
            )}
          </div>

          {/* Call & behavior */}
          <div className="rounded-xl border border-line bg-card px-5 py-[18px]">
            <CardTitle Icon={TrendingUp} t="Call & Behavior" />
            <div className="mb-3.5 grid grid-cols-2 gap-3">
              <Metric k="Sentiment" v={sentiment} c={sentColor} />
              <Metric k="Direction" v="Outbound" />
              <Metric k="AI talk ratio" v="46%" />
              <Metric k="Contact" v="54%" />
            </div>
            <div className="mb-2 text-[10px] font-bold uppercase tracking-[0.06em] text-text-muted">
              Observed behavior
            </div>
            <div className="flex flex-wrap gap-1.5">
              {tags.map((t) => (
                <span
                  key={t}
                  className="rounded-full border border-purple/[0.22] bg-purple/[0.13] px-2.5 py-1 text-[11px] font-semibold text-purple-300"
                >
                  {t}
                </span>
              ))}
            </div>
          </div>

          {/* Past performance */}
          <div className="rounded-xl border border-line bg-card px-5 py-[18px]">
            <CardTitle Icon={CheckCircle} t="Past Success Rate" />
            <div className="mb-1.5 flex items-end gap-2">
              <div className="text-[30px] font-extrabold leading-none tracking-[-1px] text-text-primary">
                {resolutionRate}%
              </div>
              <div className="pb-1 text-[11px] font-semibold text-green-300">resolution rate</div>
            </div>
            <div className="mb-4 h-[7px] overflow-hidden rounded-[10px] bg-fill">
              <div
                className="h-full rounded-[10px]"
                style={{ width: resolutionRate + '%', background: 'linear-gradient(90deg,#2563EB,#10B981)' }}
              />
            </div>
            <div className="grid grid-cols-2 gap-3">
              <Metric k="Total contacts" v={String((d?.history?.length || 1) + 2)} />
              <Metric k="Commitments kept" v={log.id === 2 ? '2 / 2' : '1 / 2'} />
              <Metric k="Avg. response" v="1.4 days" />
              <Metric k="Best channel" v={log.type === 'message' ? 'WhatsApp' : 'AI Call'} />
            </div>
          </div>
        </div>
      </div>
    </div>
  )
}
