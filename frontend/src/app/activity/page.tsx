'use client'

import { useEffect, useState } from 'react'
import { ScrollText } from 'lucide-react'
import { authHeaders } from '@/lib/auth'
import { ReviewTabs } from '@/components/shell/ReviewTabs'
import { DataTable, type Column } from '@/components/ui/DataTable'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8000/api'

/**
 * Activity = the real audit trail (crm.AuditLog): sign-ins, sends, calls,
 * account and settings changes, recorded per user.
 *
 * The page used to render a second, INVENTED feed under these rows — a
 * hardcoded list of demo events ("Ravi Kumar requested a human agent…") with
 * its own type filter. It read as live history over a real database, so it is
 * gone; anything shown here now came from the server.
 */

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

export default function ActivityPage() {
  const [audit, setAudit] = useState<AuditEntry[] | null>(null)
  const [error, setError] = useState<string | null>(null)

  // superadmin sees every actor, admin their own tenant; agents get a 403
  useEffect(() => {
    fetch(`${API}/auth/audit`, { headers: authHeaders() })
      .then((r) => (r.ok ? r.json() : Promise.reject(new Error(`HTTP ${r.status}`))))
      .then((d) => {
        setAudit(d?.entries ?? [])
        setError(null)
      })
      .catch((e) => {
        setAudit(null)
        setError(e instanceof Error ? e.message : String(e))
      })
  }, [])

  const columns: Column<AuditEntry>[] = [
    {
      key: 'created_at',
      header: 'Time',
      sortable: true,
      width: 150,
      value: (e) => e.created_at,
      render: (e) => <span className="text-[10.5px] text-text-faint">{auditTime(e.created_at)}</span>,
    },
    {
      key: 'actor',
      header: 'Actor',
      sortable: true,
      width: 120,
      value: (e) => e.actor,
      render: (e) => <span className="text-[11.5px] font-semibold text-text-high">{e.actor}</span>,
    },
    {
      key: 'action',
      header: 'Action',
      sortable: true,
      width: 170,
      value: (e) => e.action,
      render: (e) => (
        <span className="rounded bg-fill px-1.5 py-px font-mono text-[10.5px] text-brand-300">{e.action}</span>
      ),
    },
    {
      key: 'target',
      header: 'Target',
      value: (e) => `${e.target} ${e.detail}`,
      render: (e) => (
        <span className="text-[11.5px] text-text-mid">
          {e.target}
          {e.detail && <span className="text-text-muted"> — {e.detail}</span>}
        </span>
      ),
    },
    {
      key: 'ip',
      header: 'IP',
      sortable: true,
      width: 120,
      align: 'right',
      value: (e) => e.ip,
      render: (e) =>
        e.ip ? (
          <span className="font-mono text-[10px] text-text-faint">{e.ip}</span>
        ) : (
          <span className="text-text-faint">—</span>
        ),
    },
  ]

  return (
    <div className="flex flex-col gap-3.5">
      <ReviewTabs />

      <div className="flex flex-col gap-2">
        <div className="flex items-center gap-2">
          <div className="grid h-7 w-7 place-items-center rounded-lg bg-brand/15">
            <ScrollText size={14} strokeWidth={1.75} className="text-brand-300" />
          </div>
          <div className="flex-1">
            <span className="text-[13px] font-bold text-text-high">Audit Trail</span>
            <span className="ml-2 text-[11px] text-text-muted">
              sign-ins, sends, calls &amp; settings changes, recorded per user
            </span>
          </div>
          {audit !== null && (
            <span className="rounded-full bg-fill px-2 py-px text-[11px] font-bold text-text-mid">
              {audit.length}
            </span>
          )}
        </div>

        {audit === null ? (
          <div className="rounded-2xl border border-line bg-card px-5 py-6 text-[12px] text-text-muted">
            {error === 'HTTP 403'
              ? 'The audit trail is visible to admins.'
              : `Audit trail unavailable${error ? ` — ${error}` : ''}.`}
          </div>
        ) : (
          <DataTable
            columns={columns}
            rows={audit}
            rowKey={(e) => e.id}
            searchKeys={['actor', 'action', 'target']}
            searchPlaceholder="Search audit trail…"
            empty="No audit entries yet — actions will appear here as they happen."
            initialSort={{ key: 'created_at', dir: 'desc' }}
          />
        )}
      </div>
    </div>
  )
}
