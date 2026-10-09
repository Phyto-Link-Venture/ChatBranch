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
import { tint } from '@/lib/color'
import { useFormat, useT, type MessageKey } from '@/i18n'

const TYPE_COLORS: Record<string, string> = { 'ai-call': 'rgb(var(--c-brand))', 'bot-call': 'rgb(var(--c-cyan))', message: 'rgb(var(--c-purple))' }
const TYPE_ICONS: Record<string, LucideIcon> = { 'ai-call': Mic, 'bot-call': Phone, message: MessageSquare }
const TYPE_LABELS: Record<string, MessageKey> = {
  'ai-call': 'review.callDetail.types.aiCall',
  'bot-call': 'review.callDetail.types.botCall',
  message: 'review.callDetail.types.message',
}

type Sentiment = 'cooperative' | 'unreachable' | 'hesitant' | 'engaged' | 'neutral'

/** A static mapping from the call status — not an AI judgement. */
const SENTIMENT: Record<string, Sentiment> = {
  completed: 'cooperative',
  voicemail: 'unreachable',
  'no-answer': 'unreachable',
  'callback-requested': 'hesitant',
  read: 'engaged',
  delivered: 'neutral',
  active: 'cooperative',
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
      <Icon size={15} strokeWidth={1.75} color="#D9480F" />
      <div className="text-[13px] font-bold text-text-high">{t}</div>
    </div>
  )
}

export default function CallDetailPage() {
  const t = useT()
  const f = useFormat()
  const router = useRouter()
  const params = useParams()
  const { contacts } = useApp()
  const id = Number(params.id)
  const log = CALL_LOGS.find((l) => l.id === id)

  if (!log) return <div className="text-text-muted">{t('review.callDetail.none')}</div>

  const d = contacts.find((x) => x.name === log.contact)
  const record = d ? primaryRecord(d) : null
  const color = TYPE_COLORS[log.type] || 'var(--text-muted)'
  const Icon = TYPE_ICONS[log.type] || Phone

  const sentiment = SENTIMENT[log.status] || 'neutral'
  const sentColor =
    sentiment === 'cooperative' || sentiment === 'engaged'
      ? 'var(--green-ink)'
      : sentiment === 'hesitant' || sentiment === 'neutral'
      ? 'var(--amber-ink)'
      : 'var(--red-ink)'
  const tags = BEHAVIOR[log.id] || [t('review.callDetail.contacted')]
  const followup = FOLLOWUP[log.id]
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
          <ArrowLeft size={14} strokeWidth={1.75} color="#8A8177" />
          {t('shell.nav.callLogs')}
        </button>
        <ChevronRight size={12} strokeWidth={1.75} color="#8A8177" />
        <span className="text-[12px] text-text-muted">{log.contact}</span>
      </div>

      {/* Header */}
      <div className="flex flex-wrap items-center gap-4 rounded-xl border border-line bg-card px-6 py-5">
        <div
          className="flex h-12 w-12 shrink-0 items-center justify-center rounded-[13px]"
          style={{ background: tint(color, '18') }}
        >
          <Icon size={22} strokeWidth={1.75} style={{ color: color }} />
        </div>
        <div className="min-w-0 flex-1">
          <div className="mb-1 flex flex-wrap items-center gap-2.5">
            <div className="text-[18px] font-extrabold tracking-[-0.5px] text-text-primary">
              {log.contact}
            </div>
            <LogStatusBadge status={log.status} />
          </div>
          <div className="text-[12px] text-text-muted">
            {TYPE_LABELS[log.type] ? t(TYPE_LABELS[log.type]) : ''} · {log.phone} · {log.datetime}
          </div>
        </div>
        <div className="text-right">
          <div className="mb-0.5 text-[10px] text-text-muted">{t('review.callDetail.duration')}</div>
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
            <CardTitle Icon={Shield} t={t('review.callLogs.aiSummary')} />
            {/* the summary is the page's most important read — big and bold */}
            <div className="text-[15px] font-semibold leading-[1.65] text-text-high">{log.summary}</div>
            <div className="mt-3.5 flex items-center justify-between border-t border-line pt-3.5">
              <span className="text-[11px] text-text-muted">{t('review.callDetail.followUp')}</span>
              <span
                className="text-[12px] font-bold"
                style={{ color: !followup ? 'var(--text-muted)' : 'var(--green-ink)' }}
              >
                {followup || t('review.callDetail.noneRecorded')}
              </span>
            </div>
          </div>
          <div className="rounded-xl border border-line bg-card px-5 py-[18px]">
            <CardTitle Icon={MessageSquare} t={t('review.callDetail.fullTranscript')} />
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
                        color: ai ? 'var(--brand-ink)' : 'var(--text-high)',
                        background: ai ? 'rgba(255,90,31,0.18)' : 'var(--fill)',
                      }}
                    >
                      {ai ? t('review.callLogs.ai') : t('review.callDetail.contactInitials')}
                    </div>
                    <div className="max-w-[74%]">
                      <div
                        className="text-[12.5px] leading-[1.55] text-text-high"
                        style={{
                          padding: '10px 13px',
                          borderRadius: ai ? '4px 12px 12px 12px' : '12px 4px 12px 12px',
                          background: ai ? 'rgba(255,90,31,0.12)' : 'var(--fill)',
                          border: `1px solid ${ai ? 'rgba(255,90,31,0.2)' : 'var(--line)'}`,
                        }}
                      >
                        {l.t}
                      </div>
                      {l.kb && (
                        <div className="mt-1.5 flex items-center gap-1.5 text-[10px] font-semibold text-green-300">
                          <Shield size={11} strokeWidth={1.75} color="#2F7D4F" />
                          {t('review.callDetail.fromKb', { kb: l.kb })}
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
            <CardTitle Icon={User} t={t('review.callDetail.contact')} />
            {d ? (
              <div>
                <div className="mb-3.5 grid grid-cols-2 gap-3">
                  <Metric k={t('review.callDetail.record')} v={record ? `${record.type} · ${record.status}` : '—'} />
                  <Metric k={t('review.callDetail.due')} v={record?.due_date || '—'} />
                  <Metric k={t('review.callDetail.account')} v={'#CT-' + String(d.id).padStart(4, '0')} />
                  <Metric k={t('common.fields.tags')} v={d.tags.length ? f.list(d.tags) : '—'} />
                </div>
                <div className="flex flex-wrap gap-1.5">
                  <StatusBadge status={d.status} />
                </div>
                <Button
                  variant="secondary"
                  style={{ marginTop: 14, width: '100%', justifyContent: 'center', fontSize: 12 }}
                  onClick={() => router.push(`/contacts/${d.id}`)}
                >
                  {t('review.callDetail.openProfile')}
                </Button>
              </div>
            ) : (
              <div className="text-[12px] text-text-muted">{t('review.callDetail.notFound')}</div>
            )}
          </div>

          {/* Call & behavior */}
          <div className="rounded-xl border border-line bg-card px-5 py-[18px]">
            <CardTitle Icon={TrendingUp} t={t('review.callDetail.behaviour')} />
            <div className="mb-3.5 grid grid-cols-2 gap-3">
              <Metric k={t('review.callDetail.sentiment')} v={t(`review.callDetail.sentiments.${sentiment}`)} c={sentColor} />
              <Metric k={t('review.callDetail.direction')} v={t('review.callDetail.outbound')} />
              <Metric k={t('review.callDetail.aiTalk')} v={f.percent(0.46)} />
              <Metric k={t('review.callDetail.contact')} v={f.percent(0.54)} />
            </div>
            <div className="mb-2 text-[10px] font-bold uppercase tracking-[0.06em] text-text-muted">
              {t('review.callDetail.observed')}
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
            <CardTitle Icon={CheckCircle} t={t('review.callDetail.pastSuccess')} />
            <div className="mb-1.5 flex items-end gap-2">
              <div className="text-[30px] font-extrabold leading-none tracking-[-1px] text-text-primary">
                {f.percent(resolutionRate / 100)}
              </div>
              <div className="pb-1 text-[11px] font-semibold text-green-300">{t('review.callDetail.resolutionRate')}</div>
            </div>
            <div className="mb-4 h-[7px] overflow-hidden rounded-[10px] bg-fill">
              <div
                className="h-full rounded-[10px]"
                style={{ width: resolutionRate + '%', background: 'linear-gradient(90deg,rgb(var(--c-brand-400)),rgb(var(--c-green)))' }}
              />
            </div>
            <div className="grid grid-cols-2 gap-3">
              <Metric k={t('review.callDetail.totalContacts')} v={f.number((d?.history?.length || 1) + 2)} />
              <Metric k={t('review.callDetail.commitments')} v={log.id === 2 ? '2 / 2' : '1 / 2'} />
              <Metric k={t('review.callDetail.avgResponse')} v={t('review.callDetail.days', { count: 1.4 })} />
              <Metric
                k={t('review.callDetail.bestChannel')}
                v={log.type === 'message' ? 'WhatsApp' : t('review.callDetail.aiCall')}
              />
            </div>
          </div>
        </div>
      </div>
    </div>
  )
}
