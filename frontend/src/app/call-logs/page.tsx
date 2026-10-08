'use client'

import { useEffect, useState } from 'react'
import { Mic, Eye, X } from 'lucide-react'
import { CALL_LOGS } from '@/lib/mocks'
import { authHeaders } from '@/lib/auth'
import { ChannelLogo } from '@/components/ui/ChannelLogo'
import { tint } from '@/lib/color'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

/** One unified call-history row: SIP/AI phone calls from data/calls.json plus
 *  WhatsApp voice calls (both transports) from the recordings store. */
interface CallLogRow {
  id: string
  type: 'ai-call' | 'wa-call'
  contact: string
  phone: string
  ts: string
  outcome: string
  summary: string
  transcript: { role: string; text: string; ts?: number }[]
  wa_id?: string
}

const TABS = [
  { id: 'all', lbl: 'All calls' },
  { id: 'ai-call', lbl: 'Phone (SIP)' },
  { id: 'wa-call', lbl: 'WhatsApp' },
]
const TYPE_COLORS: Record<string, string> = { 'ai-call': 'rgb(var(--c-brand))', 'wa-call': '#25D366' }

const fmtWhen = (iso: string) => {
  if (!iso) return '—'
  const d = new Date(iso)
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleString()
}

export default function CallLogsPage() {
  const [logs, setLogs] = useState<CallLogRow[] | null>(null)
  const [tab, setTab] = useState('all')
  const [expanded, setExpanded] = useState<string | null>(null)

  useEffect(() => {
    fetch(`${API}/call-logs`, { headers: authHeaders() })
      .then((r) => (r.ok ? r.json() : Promise.reject(new Error(`HTTP ${r.status}`))))
      .then((d) => setLogs(d.logs ?? []))
      .catch(() =>
        // API down — fall back to the demo rows so the page still renders
        setLogs(
          CALL_LOGS.filter((l) => l.type !== 'message').map((l) => ({
            id: String(l.id),
            type: 'ai-call' as const,
            contact: l.contact,
            phone: l.phone,
            ts: l.datetime,
            outcome: l.status,
            summary: l.summary ?? '',
            transcript: [],
          })),
        ),
      )
  }, [])

  const rows = logs ?? []
  const filtered = tab === 'all' ? rows : rows.filter((l) => l.type === tab)

  return (
    <div className="flex flex-col gap-3.5">
      {/* Tabs */}
      <div className="flex items-center gap-1.5">
        {TABS.map((t) => {
          const active = tab === t.id
          const n = t.id === 'all' ? rows.length : rows.filter((l) => l.type === t.id).length
          return (
            <div
              key={t.id}
              onClick={() => setTab(t.id)}
              className="cursor-pointer rounded-[7px] px-3.5 py-1.5 text-[12px] font-semibold transition-all"
              style={{
                background: active ? 'rgba(255,90,31,0.12)' : 'var(--fill)',
                color: active ? 'var(--brand-ink)' : 'var(--text-muted)',
                border: `1px solid ${active ? 'rgba(255,90,31,0.25)' : 'var(--line)'}`,
              }}
            >
              {t.lbl} {n > 0 && <span className="opacity-70">{n}</span>}
            </div>
          )
        })}
      </div>

      {/* Log cards */}
      <div className="flex flex-col gap-2">
        {logs === null && (
          <div className="rounded-2xl border border-line bg-card px-4 py-8 text-center text-[12.5px] text-text-muted">
            Loading call history…
          </div>
        )}
        {logs !== null && filtered.length === 0 && (
          <div className="rounded-2xl border border-line bg-card px-4 py-8 text-center text-[12.5px] text-text-muted">
            No calls logged yet — place one from Chats.
          </div>
        )}
        {filtered.map((log) => {
          const isExp = expanded === log.id
          const color = TYPE_COLORS[log.type] || 'var(--text-muted)'
          return (
            <div
              key={log.id}
              className="lcard overflow-hidden rounded-2xl bg-card"
              style={{
                border: `1px solid ${isExp ? tint(color, '33') : 'var(--line)'}`,
                transition: 'border-color 0.2s, background 0.1s',
              }}
            >
              <div
                className="flex cursor-pointer items-center gap-3 px-[18px] py-3.5"
                onClick={() => setExpanded(isExp ? null : log.id)}
              >
                <div
                  className="flex h-9 w-9 shrink-0 items-center justify-center rounded-[9px]"
                  style={{ background: tint(color, '18') }}
                >
                  {log.type === 'wa-call' ? (
                    <ChannelLogo channel="whatsapp" size={16} />
                  ) : (
                    <Mic size={16} strokeWidth={1.75} style={{ color: color }} />
                  )}
                </div>
                <div className="min-w-0 flex-1">
                  <div className="mb-[3px] flex items-center gap-2">
                    <span className="text-[13px] font-bold text-text-high">{log.contact}</span>
                    {log.outcome && (
                      <span className="rounded-full bg-fill px-2 py-[2px] text-[10px] font-bold uppercase tracking-[0.04em] text-text-mid">
                        {log.outcome.replace(/_/g, ' ')}
                      </span>
                    )}
                  </div>
                  <div className="text-[11px] text-text-muted">
                    {log.phone} · {fmtWhen(log.ts)}
                  </div>
                </div>
                <div className="flex shrink-0 items-center gap-2.5">
                  {log.transcript.length > 0 && (
                    <span className="text-[11px] font-medium text-text-muted">{log.transcript.length} turns</span>
                  )}
                  {isExp ? (
                    <X size={14} strokeWidth={1.75} color="#8A8177" />
                  ) : (
                    <Eye size={14} strokeWidth={1.75} color="#8A8177" />
                  )}
                </div>
              </div>
              {isExp && (
                <div className="border-t border-line px-[18px] pb-4">
                  {log.summary && (
                    <>
                      <div className="my-2 mt-3 text-[11px] font-semibold uppercase tracking-[0.06em] text-text-muted">
                        AI Summary
                      </div>
                      {/* the summary is the row's most important read — big and bold */}
                      <div className="rounded-lg bg-fill-subtle p-3 text-[13.5px] font-semibold leading-[1.65] text-text-high">
                        {log.summary}
                      </div>
                    </>
                  )}
                  {log.transcript.length > 0 && (
                    <>
                      <div className="my-2 mt-3 text-[11px] font-semibold uppercase tracking-[0.06em] text-text-muted">
                        Transcript
                      </div>
                      <div className="max-h-64 space-y-0.5 overflow-y-auto rounded-lg bg-fill-subtle p-3 text-[12.5px] leading-snug">
                        {log.transcript.map((t, i) => (
                          <div key={i} className={t.role === 'assistant' ? 'text-brand-200' : 'text-text-mid'}>
                            <span className="font-semibold">{t.role === 'assistant' ? 'AI' : 'Customer'}:</span>{' '}
                            {t.text}
                          </div>
                        ))}
                      </div>
                    </>
                  )}
                  {!log.summary && log.transcript.length === 0 && (
                    <div className="mt-3 text-[12px] text-text-muted">No transcript captured for this call.</div>
                  )}
                </div>
              )}
            </div>
          )
        })}
      </div>
    </div>
  )
}
