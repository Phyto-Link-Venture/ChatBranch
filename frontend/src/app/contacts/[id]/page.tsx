'use client'

import { useCallback, useEffect, useState } from 'react'
import { useParams, useRouter } from 'next/navigation'
import {
  ArrowLeft,
  Bot,
  CheckCircle,
  ChevronDown,
  ChevronRight,
  Facebook,
  Instagram,
  Loader2,
  Mail,
  MessageCircle,
  MessageSquare,
  Mic,
  Phone,
  RefreshCw,
  Send,
  Sparkles,
  Workflow,
  type LucideIcon,
} from 'lucide-react'
import { useApp } from '@/lib/AppContext'
import { authHeaders } from '@/lib/auth'
import { LANGUAGES } from '@/lib/languages'
import { initials, methodLabel, primaryRecord } from '@/lib/mocks'
import { StatusBadge, Pill } from '@/components/ui/Badges'
import ChannelHub from '@/components/ChannelHub'
import ContactEntitiesCard from '@/components/ContactEntitiesCard'
import ContactTagsCard from '@/components/contacts/ContactTagsCard'
import { fetchCustomFields, formatFieldValue, type FieldDef } from '@/lib/customFields'
import { DEFAULT_WORKFLOWS } from '@/lib/flow/defaultWorkflows'
import type { FlowNode } from '@/lib/flow/types'
import { tint } from '@/lib/color'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

/** How many recent contacts load with the page — the rest on "Load full log". */
const RECENT_EVENTS = 5

const HIST_CFG: Record<string, { lbl: string; c: string; bg: string; Icon: LucideIcon }> = {
  'ai-call': { lbl: 'AI Call', c: 'var(--green-ink)', bg: 'rgba(47,125,79,0.15)', Icon: Mic },
  'bot-call': { lbl: 'Bot Call', c: 'var(--cyan-ink)', bg: 'rgba(46,125,116,0.15)', Icon: Phone },
  message: { lbl: 'Message', c: 'var(--purple-ink)', bg: 'rgba(122,78,140,0.15)', Icon: MessageSquare },
  resolved: { lbl: 'Resolved', c: 'var(--green-ink)', bg: 'rgba(47,125,79,0.15)', Icon: CheckCircle },
}

/* Live communication channels (the real stores) */
const CH_CFG: Record<string, { lbl: string; color: string; Icon: LucideIcon }> = {
  voice: { lbl: 'AI Voice Call', color: 'rgb(var(--c-brand))', Icon: Mic },
  whatsapp: { lbl: 'WhatsApp', color: 'rgb(var(--c-green))', Icon: MessageCircle },
  email: { lbl: 'Email', color: 'rgb(var(--c-amber))', Icon: Mail },
  facebook: { lbl: 'Messenger', color: 'rgb(var(--c-brand))', Icon: Facebook },
  instagram: { lbl: 'Instagram', color: '#E1306C', Icon: Instagram },
  telegram: { lbl: 'Telegram', color: '#229ED9', Icon: Send },
}

interface TimelineEvent {
  channel: string
  dir: 'in' | 'out'
  source: string
  text: string
  ts: string
  extra?: { outcome?: string; needs_human?: boolean; followup_date?: string | null }
}

interface AiSummary {
  summary: string
  heuristic: boolean
  event_count: number
  generated_at: string
}

/* Flow-builder stage lanes (defaults) — used to place the contact in the
   flow defined in the Flow Builder, based on their primary record's due date. */
const STAGES = [
  { lbl: 'Pre-due', min: 0, max: 0, color: 'rgb(var(--c-green))' },
  { lbl: 'Normal', min: 1, max: 7, color: 'rgb(var(--c-brand))' },
  { lbl: 'Serious', min: 8, max: 30, color: 'rgb(var(--c-amber))' },
  { lbl: 'Critical', min: 31, max: Infinity, color: 'rgb(var(--c-red))' },
]

/** The user's saved Flow Builder library (falls back to the shipped defaults). */
function loadWorkflowNodes(): FlowNode[] {
  try {
    const raw = localStorage.getItem('chatbranch-flow-builder-v1')
    if (raw) {
      const saved = JSON.parse(raw)
      const master =
        saved.workflows?.find((w: { id: string }) => w.id === 'master-journey') ?? saved.workflows?.[0]
      if (master?.nodes?.length) return master.nodes as FlowNode[]
    }
  } catch {
    /* corrupted save — use defaults */
  }
  return DEFAULT_WORKFLOWS[0].nodes
}

/** Steps of the contact's current stage, straight from the flow definition. */
function stageSteps(nodes: FlowNode[], daysPastDue: number): { stageLabel: string; steps: string[] } {
  const stages = nodes.filter((n) => n.type === 'stage')
  const current = stages.find((s) => {
    const cfg = s.data.config ?? {}
    if (cfg.rangeType === 'Days before due date') return daysPastDue <= 0
    const from = Number(cfg.fromDay ?? NaN)
    const to = cfg.toDay === '' || cfg.toDay === undefined ? Infinity : Number(cfg.toDay)
    return daysPastDue >= from && daysPastDue <= to
  })
  if (!current) return { stageLabel: '', steps: [] }
  const steps = nodes
    .filter((n) => n.parentId === current.id && n.type === 'action')
    .sort((a, b) => a.position.y - b.position.y)
    .map((n) => n.data.label)
  return { stageLabel: current.data.label, steps }
}

const timeFull = (ts: string) =>
  ts ? new Date(ts).toLocaleString([], { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' }) : ''

export default function ContactDetailPage() {
  const router = useRouter()
  const params = useParams()
  const { contacts, markResolved, reloadContacts } = useApp()
  const id = Number(params.id)
  const d = contacts.find((x) => x.id === id)
  const [langBusy, setLangBusy] = useState(false)

  const saveLanguage = async (language: string) => {
    setLangBusy(true)
    try {
      const res = await fetch(`${API}/v1/contacts/${id}`, {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json', ...authHeaders() },
        body: JSON.stringify({ language }),
      })
      if (res.ok) await reloadContacts()
    } finally {
      setLangBusy(false)
    }
  }

  /* Only the RECENT most-recent contacts load with the page; the full log is
     fetched on demand ("Load full log") and then kept. */
  const [events, setEvents] = useState<TimelineEvent[]>([])
  const [total, setTotal] = useState(0)
  const [fullLoaded, setFullLoaded] = useState(false)
  const [logBusy, setLogBusy] = useState(false)
  const [summary, setSummary] = useState<AiSummary | null>(null)
  const [genBusy, setGenBusy] = useState(false)
  const [flow, setFlow] = useState<{ stageLabel: string; steps: string[] }>({ stageLabel: '', steps: [] })
  // Custom Fields (Settings → Custom Fields) defined for Customers — values
  // live in this contact's `attributes` JSON and are edited from the contact
  // list's edit modal; here they are read-only chips.
  const [customerFields, setCustomerFields] = useState<FieldDef[]>([])

  useEffect(() => {
    fetchCustomFields()
      .then((s) => setCustomerFields(s.customer))
      .catch(() => setCustomerFields([]))
  }, [])

  const phone = d?.phone ?? ''
  const record = d ? primaryRecord(d) : null
  const daysPastDue = record?.due_date
    ? Math.max(0, Math.floor((Date.now() - new Date(record.due_date).getTime()) / 86_400_000))
    : 0

  const fetchLog = useCallback(
    async (full: boolean) => {
      if (!phone) return
      if (full) setLogBusy(true)
      try {
        const qs = full ? '' : `&limit=${RECENT_EVENTS}`
        const res = await fetch(
          `${API}/messaging/timeline?phone=${encodeURIComponent(phone)}${qs}`,
          { headers: authHeaders() },
        )
        const data = await res.json()
        if (res.ok) {
          setEvents(data.events ?? [])
          setTotal(data.total ?? (data.events ?? []).length)
          if (full) setFullLoaded(true)
          if (data.summary) setSummary(data.summary)
        }
      } catch {
        /* API down — cards show their empty states */
      } finally {
        if (full) setLogBusy(false)
      }
    },
    [phone],
  )

  useEffect(() => {
    fetchLog(false)
    setFlow(stageSteps(loadWorkflowNodes(), daysPastDue))
  }, [fetchLog, daysPastDue])

  if (!d) {
    return <div className="text-text-muted">No contact selected.</div>
  }

  const stage = STAGES.find((s) => daysPastDue >= s.min && daysPastDue <= s.max) ?? STAGES[0]

  const generate = async () => {
    setGenBusy(true)
    try {
      const res = await fetch(`${API}/messaging/timeline/summary`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', ...authHeaders() },
        body: JSON.stringify({
          name: d.name,
          phone: d.phone,
          context: { record },
        }),
      })
      const data = await res.json().catch(() => ({}))
      if (res.ok) setSummary(data)
    } finally {
      setGenBusy(false)
    }
  }

  return (
    <div className="flex flex-col gap-4">
      {/* Breadcrumb */}
      <div className="mb-1 flex items-center gap-3">
        <button
          onClick={() => router.push('/contacts')}
          className="flex cursor-pointer items-center gap-1.5 rounded-[7px] border border-line bg-fill px-2.5 py-1.5 text-[12px] font-semibold text-text-muted"
        >
          <ArrowLeft size={14} strokeWidth={1.75} color="#8A8177" />
          Contacts
        </button>
        <ChevronRight size={12} strokeWidth={1.75} color="#8A8177" />
        <span className="text-[12px] text-text-muted">{d.name}</span>
      </div>

      {/* Main info card */}
      <div className="rounded-2xl border border-line bg-card px-6 py-[22px]">
        <div className="flex flex-wrap items-start justify-between gap-3.5">
          <div className="flex items-center gap-3.5">
            <div
              className="flex h-12 w-12 shrink-0 items-center justify-center rounded-[14px] text-[16px] font-extrabold text-white"
              style={{ background: 'var(--primary)' }}
            >
              {initials(d.name)}
            </div>
            <div>
              <div className="mb-1 text-[17px] font-extrabold tracking-[-0.5px] text-text-primary">
                {d.name}
              </div>
              <div className="mb-1.5 text-[12px] text-text-muted">{d.phone}</div>
              <div className="flex flex-wrap gap-1.5">
                <StatusBadge status={d.status} />
                {d.tags.map((t) => (
                  <Pill key={t} color="#2F7D4F" bg="rgba(47,125,79,0.1)">
                    {t}
                  </Pill>
                ))}
                <Pill color={stage.color} bg={tint(stage.color, '1F')}>
                  {stage.lbl} stage
                </Pill>
              </div>
            </div>
          </div>
          <div className="text-right">
            {record ? (
              <>
                <div className="mb-1 text-[11px] text-text-muted">{record.type} · {record.title}</div>
                <div className="text-[22px] font-extrabold tracking-[-1px] text-text-primary">
                  {record.status}
                </div>
                {record.due_date && (
                  <div className="mt-0.5 text-[11px] text-text-faint">due {record.due_date}</div>
                )}
                {record.amount != null && (
                  <div className="mt-0.5 text-[11px] text-green-300">
                    {record.currency} {record.amount.toLocaleString('en-US')}
                  </div>
                )}
              </>
            ) : (
              <div className="text-[11px] text-text-muted">No linked records</div>
            )}
          </div>
        </div>
      </div>

      {/* AI case summary */}
      <div className="rounded-2xl border border-line bg-card px-5 py-4">
        <div className="mb-2 flex items-center gap-2">
          <div className="grid h-7 w-7 place-items-center rounded-lg bg-purple/15">
            <Sparkles size={14} strokeWidth={1.75} color="#7A4E8C" />
          </div>
          <div className="flex-1">
            <span className="text-[13px] font-bold text-text-high">AI Case Summary</span>
            {summary && (
              <span className="ml-2 text-[10.5px] text-text-faint">
                {summary.event_count} {summary.event_count === 1 ? 'interaction' : 'interactions'} ·{' '}
                {timeFull(summary.generated_at)}
                {summary.heuristic && ' · heuristic'}
              </span>
            )}
          </div>
          <button
            onClick={generate}
            disabled={genBusy}
            className="flex cursor-pointer items-center gap-1.5 rounded-lg border border-line bg-fill px-3 py-1.5 text-[12px] font-semibold text-text-mid transition-colors hover:bg-fill-hover disabled:opacity-50"
          >
            {genBusy ? (
              <Loader2 size={13} className="animate-spin" aria-hidden />
            ) : (
              <RefreshCw size={13} aria-hidden />
            )}
            {summary ? 'Regenerate' : 'Generate summary'}
          </button>
        </div>
        <p className="text-[12.5px] leading-[1.65] text-text-mid">
          {summary
            ? summary.summary
            : 'No summary yet — generate one to condense every call, chat and email into a case brief with a recommended next action.'}
        </p>
      </div>

      {/* Three columns: actions/details/flow · live communications · demo history */}
      <div className="flex items-start gap-3.5">
        <div className="flex flex-[0_0_250px] flex-col gap-2.5">
          <div className="rounded-2xl border border-line bg-card p-4">
            <div className="mb-3 text-[12px] font-bold uppercase tracking-[0.06em] text-text-mid">
              Actions
            </div>
            {d.status === 'resolved' ? (
              <div className="flex items-center gap-2 rounded-lg border border-green/20 bg-green/10 px-3 py-2.5 text-[12px] font-semibold text-green-300">
                <CheckCircle size={15} strokeWidth={1.75} color="#2F7D4F" />
                Case resolved
              </div>
            ) : (
              <button
                onClick={() => markResolved(d.id)}
                className="flex w-full cursor-pointer items-center justify-center gap-2 rounded-lg border border-green/30 bg-green/[0.12] px-3 py-[11px] text-[13px] font-bold text-green-300 transition-all"
              >
                <CheckCircle size={15} strokeWidth={1.75} color="#2F7D4F" />
                Mark Resolved
              </button>
            )}
          </div>

          {/* Position in the Flow Builder journey */}
          <div className="rounded-2xl border border-line bg-card p-4">
            <div className="mb-2.5 flex items-center justify-between">
              <span className="text-[12px] font-bold uppercase tracking-[0.06em] text-text-mid">
                Flow Progress
              </span>
              <button
                onClick={() => router.push('/flow-builder')}
                title="Open in Flow Builder"
                aria-label="Open in Flow Builder"
                className="grid h-6 w-6 cursor-pointer place-items-center rounded-md text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
              >
                <Workflow size={13} aria-hidden />
              </button>
            </div>
            {/* stage strip */}
            <div className="mb-2.5 flex gap-1">
              {STAGES.map((s) => {
                const active = s.lbl === stage.lbl
                return (
                  <div key={s.lbl} className="flex-1" title={s.lbl}>
                    <div
                      className="h-[5px] rounded-[3px]"
                      style={{ background: active ? s.color : 'var(--fill-hover)' }}
                    />
                    <div
                      className="mt-1 text-center text-[8.5px] font-bold uppercase tracking-tight"
                      style={{ color: active ? s.color : 'var(--text-faint)' }}
                    >
                      {s.lbl}
                    </div>
                  </div>
                )
              })}
            </div>
            {flow.steps.length > 0 ? (
              <>
                <div className="mb-1.5 text-[10.5px] text-text-muted">
                  Steps in this stage (from your flow):
                </div>
                {flow.steps.slice(0, 4).map((s, i) => (
                  <div key={i} className="mb-1.5 flex items-start gap-1.5 text-[11px] leading-snug text-text-mid">
                    <span
                      className="mt-px grid h-3.5 w-3.5 shrink-0 place-items-center rounded-full text-[8.5px] font-bold text-white"
                      style={{ background: stage.color }}
                    >
                      {i + 1}
                    </span>
                    <span className="min-w-0">{s.length > 58 ? s.slice(0, 58) + '…' : s}</span>
                  </div>
                ))}
              </>
            ) : (
              <div className="text-[11px] text-text-muted">
                No steps defined for this stage — add them in the Flow Builder.
              </div>
            )}
          </div>

          <div className="rounded-2xl border border-line bg-card p-4">
            <div className="mb-3 text-[12px] font-bold uppercase tracking-[0.06em] text-text-mid">
              Details
            </div>
            {[
              { label: 'ID', value: '#CT-' + String(d.id).padStart(4, '0') },
              { label: 'Company', value: d.company || '—' },
              { label: 'Method', value: methodLabel(d.method) },
            ].map((row) => (
              <div key={row.label} className="mb-2 flex justify-between">
                <span className="text-[11px] text-text-muted">{row.label}</span>
                <span className="text-[11px] font-semibold text-text-mid">{row.value}</span>
              </div>
            ))}
            <div className="flex items-center justify-between">
              <span className="text-[11px] text-text-muted">Language</span>
              {/* live-wired: the AI opens calls/chats in this language and it
                  hints STT, so changing it applies from the next call */}
              <select
                value={d.language ?? 'en'}
                disabled={langBusy}
                onChange={(e) => saveLanguage(e.target.value)}
                className="cursor-pointer rounded-md border border-line-strong bg-surface px-1.5 py-1 text-[11px] font-semibold text-text-mid outline-none disabled:opacity-50"
              >
                {LANGUAGES.map((l) => (
                  <option key={l.value} value={l.value}>
                    {l.label}
                  </option>
                ))}
              </select>
            </div>
          </div>

          {/* Custom fields — the per-vertical labels an admin defined for
              Customers, shown read-only (edit them from the contact list). */}
          {(() => {
            const filled = customerFields
              .map((f) => ({ f, v: formatFieldValue(f, d.attributes?.[f.key]) }))
              .filter((x) => x.v !== '')
            if (filled.length === 0) return null
            return (
              <div className="rounded-2xl border border-line bg-card p-4">
                <div className="mb-3 text-[12px] font-bold uppercase tracking-[0.06em] text-text-mid">
                  Custom Fields
                </div>
                <div className="flex flex-wrap gap-1.5">
                  {filled.map(({ f, v }) => (
                    <span
                      key={f.key}
                      className="rounded-full bg-fill px-2 py-0.5 text-[10.5px] font-semibold text-text-mid"
                    >
                      <span className="text-text-faint">{f.label}:</span> {v}
                    </span>
                  ))}
                </div>
              </div>
            )
          })()}

          {/* Communication hub — reachable channels, accounts, activity */}
          <div className="rounded-2xl border border-line bg-card p-4">
            <div className="mb-3 text-[12px] font-bold uppercase tracking-[0.06em] text-text-mid">
              Channels
            </div>
            <ChannelHub contactId={d.id} allowAdd />
          </div>

          {/* Per-brand membership — link/unlink without deleting the profile */}
          <ContactEntitiesCard contactId={d.id} />

          {/* Registry tags (boss / employee / driver…) with their attribute values */}
          <ContactTagsCard contactId={d.id} />
        </div>

        {/* Live communications — recent few by default, full log on demand */}
        <div className="min-w-0 flex-1 rounded-2xl border border-line bg-card p-5">
          <div className="mb-1 flex items-center gap-2">
            <span className="flex-1 text-[13px] font-bold text-text-high">Communications</span>
            {!fullLoaded && total > events.length && (
              <button
                onClick={() => fetchLog(true)}
                disabled={logBusy}
                className="flex cursor-pointer items-center gap-1.5 rounded-lg border border-line bg-fill px-3 py-1.5 text-[12px] font-semibold text-text-mid transition-colors hover:bg-fill-hover disabled:opacity-50"
              >
                {logBusy ? (
                  <Loader2 size={13} className="animate-spin" aria-hidden />
                ) : (
                  <ChevronDown size={13} aria-hidden />
                )}
                Load full log ({total})
              </button>
            )}
          </div>
          <div className="mb-4 text-[11px] text-text-muted">
            {!fullLoaded && total > events.length
              ? `Showing the ${events.length} most recent of ${total} interactions — load the full log to see everything.`
              : 'Every real interaction across voice, WhatsApp and email — with the customer’s responses.'}
          </div>
          {events.length === 0 ? (
            <div className="p-[30px] text-center text-[12px] text-text-faint">
              No live communications recorded yet for {d.phone}. Voice calls, WhatsApp chats and
              emails will appear here automatically.
            </div>
          ) : (
            <div>
              {events.map((e, i) => {
                const cfg = CH_CFG[e.channel] || CH_CFG.whatsapp
                const inbound = e.dir === 'in' && e.source === 'customer'
                return (
                  <div key={i} className="flex gap-3 pb-4">
                    <div className="flex shrink-0 flex-col items-center">
                      <div
                        className="grid h-8 w-8 shrink-0 place-items-center rounded-full"
                        style={{ background: tint(cfg.color, '22') }}
                      >
                        <cfg.Icon size={14} strokeWidth={1.75} style={{ color: cfg.color }} />
                      </div>
                      {i < events.length - 1 && (
                        <div className="mx-auto mt-1 min-h-[14px] w-px flex-1 bg-fill" />
                      )}
                    </div>
                    <div className="min-w-0 flex-1 pt-1">
                      <div className="mb-1 flex flex-wrap items-center gap-1.5">
                        <span
                          className="rounded px-[7px] py-px text-[10px] font-semibold"
                          style={{ color: cfg.color, background: tint(cfg.color, '1C') }}
                        >
                          {cfg.lbl}
                        </span>
                        {inbound ? (
                          <span className="rounded bg-green/15 px-[7px] py-px text-[10px] font-bold text-green-300">
                            Customer replied
                          </span>
                        ) : e.source === 'ai' ? (
                          <span className="flex items-center gap-0.5 rounded bg-purple/15 px-[7px] py-px text-[10px] font-semibold text-purple-300">
                            <Bot size={10} aria-hidden /> AI
                          </span>
                        ) : null}
                        {e.extra?.outcome && (
                          <span className="rounded bg-fill px-[7px] py-px text-[10px] font-semibold text-text-mid">
                            {e.extra.outcome}
                          </span>
                        )}
                        <span className="text-[10px] text-text-faint">{timeFull(e.ts)}</span>
                      </div>
                      <div className="text-[12px] leading-[1.5] text-text-mid">
                        {e.text.length > 220 ? e.text.slice(0, 220) + '…' : e.text}
                      </div>
                    </div>
                  </div>
                )
              })}
            </div>
          )}
        </div>

        {/* Demo history (mock data) */}
        <div className="min-w-0 flex-[0_0_300px] rounded-2xl border border-line bg-card p-5">
          <div className="mb-4 text-[13px] font-bold text-text-high">History (demo)</div>
          {d.history.length === 0 ? (
            <div className="p-[20px] text-center text-[12px] text-text-faint">No activity yet.</div>
          ) : (
            <div>
              {d.history.map((h, i) => {
                const cfg = HIST_CFG[h.type] || HIST_CFG.message
                return (
                  <div key={i} className="flex gap-3 pb-4">
                    <div className="flex shrink-0 flex-col items-center">
                      <div
                        className="grid h-8 w-8 shrink-0 place-items-center rounded-full"
                        style={{ background: cfg.bg }}
                      >
                        <cfg.Icon size={14} strokeWidth={1.75} style={{ color: cfg.c }} />
                      </div>
                      {i < d.history.length - 1 && (
                        <div className="mx-auto mt-1 min-h-[14px] w-px flex-1 bg-fill" />
                      )}
                    </div>
                    <div className="min-w-0 flex-1 pt-1">
                      <div className="mb-1 flex items-center gap-1.5">
                        <span
                          className="rounded px-[7px] py-px text-[10px] font-semibold"
                          style={{ color: cfg.c, background: cfg.bg }}
                        >
                          {cfg.lbl}
                        </span>
                        <span className="text-[10px] text-text-faint">{h.date}</span>
                      </div>
                      <div className="text-[12px] leading-[1.5] text-text-mid">{h.text}</div>
                    </div>
                  </div>
                )
              })}
            </div>
          )}
        </div>
      </div>
    </div>
  )
}
