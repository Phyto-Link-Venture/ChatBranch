'use client'

import { useEffect, useState } from 'react'
import { useRouter } from 'next/navigation'
import {
  Mic,
  MessageSquare,
  FileText,
  CheckCircle,
  Zap,
  AlertCircle,
  Settings,
  ScrollText,
  type LucideIcon,
} from 'lucide-react'
import { ACTIVITY } from '@/lib/mocks'
import { authHeaders } from '@/lib/auth'
import { ReviewTabs } from '@/components/shell/ReviewTabs'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8000/api'

interface AuditEntry {
  id: number
  actor: string
  action: string
  target: string
  detail: string
  ip: string
  created_at: string
}

const auditTime = (ts: string) =>
  new Date(ts).toLocaleString([], { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' })

const TYPE_CFG: Record<string, { lbl: string; color: string; Icon: LucideIcon }> = {
  call: { lbl: 'Calls', color: '#3B82F6', Icon: Mic },
  message: { lbl: 'Messages', color: '#8B5CF6', Icon: MessageSquare },
  record: { lbl: 'Records', color: '#10B981', Icon: FileText },
  resolve: { lbl: 'Resolved', color: '#10B981', Icon: CheckCircle },
  campaign: { lbl: 'Campaigns', color: '#06B6D4', Icon: Zap },
  escalation: { lbl: 'Escalations', color: '#EF4444', Icon: AlertCircle },
  config: { lbl: 'Config', color: '#F59E0B', Icon: Settings },
}

const FILTERS = [
  { id: 'all', lbl: 'All' },
  ...Object.keys(TYPE_CFG).map((k) => ({ id: k, lbl: TYPE_CFG[k].lbl })),
]

export default function ActivityPage() {
  const router = useRouter()
  const [filter, setFilter] = useState('all')
  const [audit, setAudit] = useState<AuditEntry[] | null>(null)

  // real audit trail — superadmin sees all actors, admin their own; agents 403 (section hidden)
  useEffect(() => {
    fetch(`${API}/auth/audit`, { headers: authHeaders() })
      .then((r) => (r.ok ? r.json() : null))
      .then((d) => setAudit(d?.entries ?? null))
      .catch(() => setAudit(null))
  }, [])

  const rows = ACTIVITY.filter((a) => filter === 'all' || a.type === filter)
  const groups: { date: string; items: typeof ACTIVITY }[] = []
  rows.forEach((a) => {
    let g = groups.find((x) => x.date === a.date)
    if (!g) {
      g = { date: a.date, items: [] }
      groups.push(g)
    }
    g.items.push(a)
  })

  return (
    <div className="flex flex-col gap-3.5">
      <ReviewTabs />
      {/* Audit trail — live system + user actions from the database */}
      {audit !== null && (
        <div className="overflow-hidden rounded-2xl border border-line bg-card">
          <div className="flex items-center gap-2 border-b border-line px-5 py-3.5">
            <div className="grid h-7 w-7 place-items-center rounded-lg bg-brand/15">
              <ScrollText size={14} strokeWidth={1.75} className="text-brand-300" />
            </div>
            <div className="flex-1">
              <span className="text-[13px] font-bold text-text-high">Audit Trail</span>
              <span className="ml-2 text-[11px] text-text-muted">
                live — sign-ins, sends, calls & settings changes, recorded per user
              </span>
            </div>
            <span className="rounded-full bg-fill px-2 py-px text-[11px] font-bold text-text-mid">
              {audit.length}
            </span>
          </div>
          {audit.length === 0 ? (
            <div className="px-5 py-6 text-center text-[12px] text-text-muted">
              No audit entries yet — actions will appear here as they happen.
            </div>
          ) : (
            <div className="max-h-[300px] overflow-y-auto">
              {audit.map((e) => (
                <div key={e.id} className="flex items-center gap-3 border-b border-line px-5 py-2">
                  <span className="w-[110px] shrink-0 text-[10.5px] text-text-faint">
                    {auditTime(e.created_at)}
                  </span>
                  <span className="w-[90px] shrink-0 truncate text-[11.5px] font-semibold text-text-high">
                    {e.actor}
                  </span>
                  <span className="shrink-0 rounded bg-fill px-1.5 py-px font-mono text-[10.5px] text-brand-300">
                    {e.action}
                  </span>
                  <span className="min-w-0 flex-1 truncate text-[11.5px] text-text-mid">
                    {e.target}
                    {e.detail && <span className="text-text-muted"> — {e.detail}</span>}
                  </span>
                  {e.ip && <span className="shrink-0 font-mono text-[10px] text-text-faint">{e.ip}</span>}
                </div>
              ))}
            </div>
          )}
        </div>
      )}

      {/* Filter chips */}
      <div className="flex flex-wrap gap-2">
        {FILTERS.map((f) => {
          const sel = filter === f.id
          const c = TYPE_CFG[f.id]?.color || '#10B981'
          return (
            <div
              key={f.id}
              onClick={() => setFilter(f.id)}
              className="cursor-pointer rounded-lg px-[13px] py-1.5 text-[12px] font-semibold"
              style={{
                color: sel ? 'var(--text-high)' : 'var(--text-muted)',
                background: sel ? c + '1F' : 'var(--fill-subtle)',
                border: `1px solid ${sel ? c + '55' : 'var(--line)'}`,
              }}
            >
              {f.lbl}
            </div>
          )
        })}
      </div>

      {groups.map((g) => (
        <div key={g.date} className="overflow-hidden rounded-2xl border border-line bg-card">
          <div className="border-b border-line px-5 py-3 text-[11px] font-bold uppercase tracking-[0.06em] text-text-muted">
            {g.date}
          </div>
          {g.items.map((a, idx) => {
            const cfg = TYPE_CFG[a.type] || TYPE_CFG.config
            const clickable = a.target != null
            return (
              <div
                key={a.id}
                className={clickable ? 'trow' : ''}
                onClick={clickable ? () => router.push(`/contacts/${a.target}`) : undefined}
                style={{
                  display: 'flex',
                  alignItems: 'flex-start',
                  gap: 13,
                  padding: '14px 20px',
                  borderBottom: idx < g.items.length - 1 ? '1px solid var(--line)' : 'none',
                  cursor: clickable ? 'pointer' : 'default',
                }}
              >
                <div
                  className="flex h-[34px] w-[34px] shrink-0 items-center justify-center rounded-[9px]"
                  style={{ background: cfg.color + '18' }}
                >
                  <cfg.Icon size={16} strokeWidth={1.75} color={cfg.color} />
                </div>
                <div className="min-w-0 flex-1">
                  <div className="mb-0.5 flex items-center gap-2">
                    <span className="text-[13px] font-bold text-text-high">{a.title}</span>
                    <span
                      className="rounded-full px-[7px] py-px text-[9px] font-bold uppercase tracking-[0.04em]"
                      style={{ color: cfg.color, background: cfg.color + '18' }}
                    >
                      {cfg.lbl}
                    </span>
                  </div>
                  <div className="text-[12px] leading-[1.5] text-text-mid">{a.detail}</div>
                  <div className="mt-[3px] text-[10.5px] text-text-faint">{a.actor}</div>
                </div>
                <div className="shrink-0 font-mono text-[11px] font-semibold text-text-muted">
                  {a.time}
                </div>
              </div>
            )
          })}
        </div>
      ))}
    </div>
  )
}
