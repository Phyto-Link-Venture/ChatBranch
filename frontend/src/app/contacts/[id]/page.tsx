'use client'

import { useCallback, useEffect, useState, type ReactNode } from 'react'
import Link from 'next/link'
import { useParams } from 'next/navigation'
import { ArrowLeft, Bot, ChevronDown, Loader2, MessageSquare, RefreshCw, Sparkles } from 'lucide-react'
import { useApp } from '@/lib/AppContext'
import { authHeaders, expireSessionOn401 } from '@/lib/auth'
import { LANGUAGES } from '@/lib/languages'
import { initials, primaryRecord, type Contact } from '@/lib/mocks'
import { StatusBadge } from '@/components/ui/Badges'
import { Button } from '@/components/ui/Button'
import { Select } from '@/components/ui/Select'
import { ChannelLogo, channelLabel } from '@/components/ui/ChannelLogo'
import ChannelHub from '@/components/ChannelHub'
import ContactEntitiesCard from '@/components/ContactEntitiesCard'
import ContactTagsCard from '@/components/contacts/ContactTagsCard'
import { fetchCustomFields, formatFieldValue, type FieldDef } from '@/lib/customFields'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

/** How many recent interactions load with the page — the rest on "Load full log". */
const RECENT_EVENTS = 12

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

const timeFull = (ts: string) =>
  ts ? new Date(ts).toLocaleString([], { day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' }) : ''

/** Our `facebook` channel is Messenger. */
const logoKey = (c: string) => (c === 'facebook' ? 'messenger' : c)

/** A paper card with an eyebrow heading and an optional right slot. */
function Panel({ title, right, children, className = '' }: { title: string; right?: ReactNode; children: ReactNode; className?: string }) {
  return (
    <section className={`rounded-xl border border-line bg-card p-4 ${className}`}>
      <div className="mb-3 flex items-center justify-between gap-2">
        <h2 className="eyebrow">{title}</h2>
        {right}
      </div>
      {children}
    </section>
  )
}

function Row({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex items-center justify-between gap-3 border-b border-line py-2 last:border-b-0">
      <span className="text-[12.5px] text-text-muted">{label}</span>
      <span className="min-w-0 truncate text-right text-[13px] text-text-high">{children}</span>
    </div>
  )
}

export default function ContactDetailPage() {
  const params = useParams()
  const id = Number(params.id)
  const { contacts, reloadContacts } = useApp()

  // The contact is read by id — not only from the top-bar entity's list, so a
  // link to a contact outside the current entity still opens.
  const [contact, setContact] = useState<Contact | null>(null)
  const [missing, setMissing] = useState(false)
  const loadContact = useCallback(async () => {
    try {
      const res = await fetch(`${API}/contacts/${id}`, { headers: authHeaders() })
      if (expireSessionOn401(res)) return
      if (res.status === 404) {
        setMissing(true)
        return
      }
      if (res.ok) {
        setContact(await res.json())
        setMissing(false)
      }
    } catch {
      /* API down — fall back to the loaded list below */
    }
  }, [id])
  useEffect(() => {
    loadContact()
  }, [loadContact])
  const d = contact ?? contacts.find((x) => x.id === id) ?? null

  const [langBusy, setLangBusy] = useState(false)
  const saveLanguage = async (language: string) => {
    setLangBusy(true)
    try {
      const res = await fetch(`${API}/v1/contacts/${id}`, {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json', ...authHeaders() },
        body: JSON.stringify({ language }),
      })
      if (res.ok) await Promise.all([loadContact(), reloadContacts()])
    } finally {
      setLangBusy(false)
    }
  }

  // Communications: the most recent few load with the page; the full log on demand.
  const [events, setEvents] = useState<TimelineEvent[]>([])
  const [total, setTotal] = useState(0)
  const [fullLoaded, setFullLoaded] = useState(false)
  const [logBusy, setLogBusy] = useState(false)
  const [summary, setSummary] = useState<AiSummary | null>(null)
  const [genBusy, setGenBusy] = useState(false)
  const [customerFields, setCustomerFields] = useState<FieldDef[]>([])

  useEffect(() => {
    fetchCustomFields()
      .then((s) => setCustomerFields(s.customer))
      .catch(() => setCustomerFields([]))
  }, [])

  const phone = d?.phone ?? ''
  const email = d?.email ?? ''
  const fetchLog = useCallback(
    async (full: boolean) => {
      if (!phone && !email) return
      if (full) setLogBusy(true)
      try {
        const p = new URLSearchParams()
        if (phone) p.set('phone', phone)
        if (email) p.set('email', email)
        if (!full) p.set('limit', String(RECENT_EVENTS))
        const res = await fetch(`${API}/messaging/timeline?${p}`, { headers: authHeaders() })
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
    [phone, email],
  )
  useEffect(() => {
    fetchLog(false)
  }, [fetchLog])

  if (!d) {
    return (
      <div className="py-16 text-center text-[13px] text-text-muted">
        {missing ? 'This contact doesn’t exist or isn’t in your scope.' : 'Loading contact…'}
        <div className="mt-3">
          <Link href="/contacts" className="font-semibold text-text-high hover:underline">
            Back to contacts
          </Link>
        </div>
      </div>
    )
  }

  const record = primaryRecord(d)
  const generate = async () => {
    setGenBusy(true)
    try {
      const res = await fetch(`${API}/messaging/timeline/summary`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', ...authHeaders() },
        body: JSON.stringify({ name: d.name, phone: d.phone, email: d.email, context: { record } }),
      })
      const data = await res.json().catch(() => ({}))
      if (res.ok) setSummary(data)
    } finally {
      setGenBusy(false)
    }
  }

  const filledFields = customerFields
    .map((f) => ({ f, v: formatFieldValue(f, d.attributes?.[f.key]) }))
    .filter((x) => x.v !== '')
  // newest first — the latest touchpoint is what an operator reads first
  const timeline = [...events].reverse()

  return (
    <div className="flex flex-col gap-4">
      <Link
        href="/contacts"
        className="inline-flex w-fit items-center gap-1.5 text-[12.5px] font-semibold text-text-muted transition-colors hover:text-text-high"
      >
        <ArrowLeft size={14} aria-hidden /> Contacts
      </Link>

      {/* header */}
      <section className="flex flex-wrap items-center gap-4 rounded-xl border border-line bg-card px-5 py-5 sm:px-6">
        <div className="grid h-14 w-14 shrink-0 place-items-center rounded-full bg-primary text-[18px] font-semibold text-on-primary">
          {initials(d.name)}
        </div>
        <div className="min-w-0 flex-1 basis-[220px]">
          <h1 className="display truncate text-[28px] leading-tight text-text-heading sm:text-[32px]">{d.name}</h1>
          <div className="mt-1 flex flex-wrap items-center gap-x-3 gap-y-1 text-[13px] text-text-mid">
            {d.phone && <span className="font-mono text-[12.5px]">{d.phone}</span>}
            {d.email && <span className="truncate">{d.email}</span>}
            {d.company && <span className="text-text-muted">{d.company}</span>}
          </div>
          <div className="mt-2 flex flex-wrap items-center gap-1.5">
            <StatusBadge status={d.status} />
            {d.tags.map((t) => (
              <span key={t} className="rounded-full border border-line-strong px-2 py-0.5 text-[11.5px] text-text-mid">
                {t}
              </span>
            ))}
          </div>
        </div>
        <Link
          href={`/chats?q=${encodeURIComponent(d.phone || d.email || d.name)}`}
          className="inline-flex h-9 shrink-0 items-center gap-1.5 rounded-lg border border-line-strong bg-card px-4 text-[13px] font-semibold text-text-high transition-colors hover:bg-fill-hover"
        >
          <MessageSquare size={14} aria-hidden /> Open in Chats
        </Link>
      </section>

      {/* AI summary */}
      <section className="rounded-xl border border-line bg-card px-5 py-4">
        <div className="mb-2 flex flex-wrap items-center gap-2">
          <Sparkles size={14} className="text-brand-300" aria-hidden />
          <h2 className="eyebrow">AI summary</h2>
          {summary && (
            <span className="text-[11.5px] text-text-muted">
              {summary.event_count} {summary.event_count === 1 ? 'interaction' : 'interactions'} · {timeFull(summary.generated_at)}
              {summary.heuristic && ' · basic (no AI key)'}
            </span>
          )}
          <Button variant="ghost" size="sm" onClick={generate} disabled={genBusy} className="ml-auto">
            {genBusy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <RefreshCw size={13} aria-hidden />}
            {summary ? 'Regenerate' : 'Generate summary'}
          </Button>
        </div>
        <p className="text-[14px] leading-[1.65] text-text-high">
          {summary
            ? summary.summary
            : 'No summary yet — generate one to condense every call, chat and email with this contact into a short brief.'}
        </p>
      </section>

      <div className="grid grid-cols-1 items-start gap-4 lg:grid-cols-[320px_minmax(0,1fr)]">
        {/* left column */}
        <div className="flex min-w-0 flex-col gap-4">
          <Panel title="Details">
            <Row label="ID">#{String(d.id).padStart(4, '0')}</Row>
            <Row label="Phone">{d.phone || '—'}</Row>
            <Row label="Email">{d.email || '—'}</Row>
            <Row label="Company">{d.company || '—'}</Row>
            <div className="flex items-center justify-between gap-3 pt-2">
              <span className="text-[12.5px] text-text-muted">Language</span>
              {/* the AI opens calls/chats in this language; applies from the next one */}
              <Select
                value={d.language ?? 'en'}
                onChange={saveLanguage}
                options={LANGUAGES.map((l) => ({ value: l.value, label: l.label }))}
                disabled={langBusy}
                ariaLabel="Contact language"
                className="w-[170px]"
              />
            </div>
          </Panel>

          {filledFields.length > 0 && (
            <Panel title="Custom fields">
              {filledFields.map(({ f, v }) => (
                <Row key={f.key} label={f.label}>
                  {v}
                </Row>
              ))}
            </Panel>
          )}

          {d.records.length > 0 && (
            <Panel title="Records">
              <ul className="flex flex-col">
                {d.records.slice(0, 6).map((r) => (
                  <li key={r.id} className="flex items-baseline justify-between gap-3 border-b border-line py-2 last:border-b-0">
                    <span className="min-w-0">
                      <span className="block truncate text-[13px] text-text-high">{r.title || r.external_id || `#${r.id}`}</span>
                      <span className="text-[11.5px] capitalize text-text-muted">{r.type}</span>
                    </span>
                    <span className="shrink-0 rounded-full border border-line px-2 py-0.5 text-[11px] text-text-mid">{r.status}</span>
                  </li>
                ))}
              </ul>
            </Panel>
          )}

          <Panel title="Channels">
            <ChannelHub contactId={d.id} allowAdd />
          </Panel>

          {/* Per-entity membership — link/unlink without deleting the profile */}
          <ContactEntitiesCard contactId={d.id} />

          {/* Registry tags (boss / employee / driver…) with their attribute values */}
          <ContactTagsCard contactId={d.id} />
        </div>

        {/* main column — the real communications timeline */}
        <section className="min-w-0 rounded-xl border border-line bg-card p-5">
          <div className="flex flex-wrap items-baseline gap-2">
            <h2 className="display text-[22px] leading-tight text-text-heading">Communications</h2>
            <span className="text-[12.5px] text-text-muted">
              {total === 0
                ? ''
                : !fullLoaded && total > events.length
                  ? `${events.length} most recent of ${total}`
                  : `${total} ${total === 1 ? 'interaction' : 'interactions'}`}
            </span>
            {!fullLoaded && total > events.length && (
              <Button variant="ghost" size="sm" onClick={() => fetchLog(true)} disabled={logBusy} className="ml-auto">
                {logBusy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <ChevronDown size={13} aria-hidden />}
                Load full log
              </Button>
            )}
          </div>
          <p className="mb-4 mt-1 text-[12.5px] text-text-muted">
            Every call, chat and email with this contact across all channels — newest first.
          </p>
          {timeline.length === 0 ? (
            <div className="rounded-lg border border-dashed border-line-strong px-6 py-10 text-center text-[13px] text-text-muted">
              Nothing yet. Voice calls, chats and emails with {d.name} appear here as they happen.
            </div>
          ) : (
            <ol className="relative">
              {timeline.map((e, i) => {
                const inbound = e.dir === 'in' && e.source === 'customer'
                return (
                  <li key={i} className="relative flex gap-3 pb-5 last:pb-0">
                    {i < timeline.length - 1 && <span className="absolute left-[13px] top-8 bottom-0 w-px bg-line" aria-hidden />}
                    <span className="relative z-[1] grid h-7 w-7 shrink-0 place-items-center rounded-full border border-line bg-card">
                      <ChannelLogo channel={logoKey(e.channel)} size={14} />
                    </span>
                    <div className="min-w-0 flex-1 pt-0.5">
                      <div className="mb-1 flex flex-wrap items-center gap-1.5 text-[11.5px]">
                        <span className="font-semibold text-text-high">{channelLabel(logoKey(e.channel))}</span>
                        <span className="text-text-muted">·</span>
                        <span className={inbound ? 'font-semibold text-[color:var(--green-ink)]' : 'text-text-muted'}>
                          {inbound ? 'from the customer' : e.source === 'ai' ? 'AI' : e.source === 'system' ? 'system' : 'to the customer'}
                        </span>
                        {e.source === 'ai' && <Bot size={11} className="text-text-muted" aria-hidden />}
                        {e.extra?.outcome && (
                          <span className="rounded-full border border-line px-1.5 text-[10.5px] text-text-mid">{e.extra.outcome.replace(/_/g, ' ')}</span>
                        )}
                        <span className="ml-auto shrink-0 text-text-muted">{timeFull(e.ts)}</span>
                      </div>
                      <div
                        className={`whitespace-pre-wrap rounded-lg px-3 py-2 text-[13px] leading-[1.55] ${
                          inbound ? 'border border-line bg-card text-text-high' : 'bg-well text-text-mid'
                        }`}
                      >
                        {e.text.length > 400 ? e.text.slice(0, 400) + '…' : e.text}
                      </div>
                    </div>
                  </li>
                )
              })}
            </ol>
          )}
        </section>
      </div>
    </div>
  )
}
