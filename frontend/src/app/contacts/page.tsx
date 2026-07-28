'use client'

import { useMemo, useState, useEffect } from 'react'
import { useRouter } from 'next/navigation'
import { LayoutGrid, List, Loader2, Pencil, Plus, Trash2 } from 'lucide-react'
import { useApp } from '@/lib/AppContext'
import { useBrand } from '@/lib/BrandContext'
import { authHeaders, hasCap } from '@/lib/auth'
import { initials, methodLabel, primaryRecord, type ContactStatus } from '@/lib/mocks'
import { StatusBadge } from '@/components/ui/Badges'
import { Button } from '@/components/ui/Button'
import { Modal } from '@/components/ui/Modal'
import { DataTable, type Column } from '@/components/ui/DataTable'
import { LANGUAGES } from '@/lib/languages'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8000/api'

/* Kanban columns — one per contact status, in workflow order. */
const KANBAN: { key: ContactStatus; lbl: string; color: string }[] = [
  { key: 'active', lbl: 'Active', color: '#60A5FA' },
  { key: 'pending', lbl: 'Pending', color: '#FBBF24' },
  { key: 'message-sent', lbl: 'Msg Sent', color: '#A78BFA' },
  { key: 'resolved', lbl: 'Resolved', color: '#34D399' },
  { key: 'failed', lbl: 'Failed', color: '#F87171' },
]

const STATUS_OPTS = [
  { value: '', label: 'All statuses' },
  { value: 'active', label: 'Active' },
  { value: 'pending', label: 'Pending' },
  { value: 'message-sent', label: 'Msg Sent' },
  { value: 'resolved', label: 'Resolved' },
  { value: 'failed', label: 'Failed' },
]
const RECORD_TYPE_OPTS = [
  { value: '', label: 'Any record type' },
  { value: 'order', label: 'Order' },
  { value: 'shipment', label: 'Shipment' },
  { value: 'invoice', label: 'Invoice' },
  { value: 'booking', label: 'Booking' },
  { value: 'ticket', label: 'Ticket' },
  { value: 'custom', label: 'Custom' },
]

function FilterSelect({
  value,
  onChange,
  opts,
}: {
  value: string
  onChange: (v: string) => void
  opts: { value: string; label: string }[]
}) {
  return (
    <select
      value={value}
      onChange={(e) => onChange(e.target.value)}
      className="cursor-pointer rounded-lg border border-line-strong bg-well px-2.5 py-1.5 text-[12px] font-semibold text-text-high outline-none"
    >
      {opts.map((o) => (
        <option key={o.value} value={o.value}>
          {o.label}
        </option>
      ))}
    </select>
  )
}

function AddField({
  label, value, onChange, placeholder, type = 'text', required = false,
}: {
  label: string
  value: string
  onChange: (v: string) => void
  placeholder?: string
  type?: string
  required?: boolean
}) {
  return (
    <label className="block">
      <span className="mb-1 block text-[11px] font-bold text-text-high">
        {label}
        {required && <span className="text-red-300"> *</span>}
      </span>
      <input
        type={type}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        placeholder={placeholder}
        className="w-full rounded-lg border border-line-strong bg-well px-3 py-2 text-[12.5px] text-text-primary outline-none transition-colors focus:border-brand-400"
      />
    </label>
  )
}

function ContactModal({
  open,
  editing,
  onClose,
}: {
  open: boolean
  editing: Contact | null
  onClose: () => void
}) {
  const { reloadContacts } = useApp()
  const { activeBrand } = useBrand()
  const [name, setName] = useState('')
  const [phone, setPhone] = useState('')
  const [language, setLanguage] = useState('en')
  const [email, setEmail] = useState('')
  const [company, setCompany] = useState('')
  const [tags, setTags] = useState('')
  const [notes, setNotes] = useState('')
  const [allBrands, setAllBrands] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  // Reset / prefill the form each time the modal (re)opens.
  useEffect(() => {
    if (open) {
      setName(editing?.name ?? '')
      setPhone(editing?.phone ?? '')
      setLanguage(editing?.language ?? 'en')
      setEmail(editing?.email ?? '')
      setCompany(editing?.company ?? '')
      setTags((editing?.tags ?? []).join(', '))
      setNotes(editing && typeof editing.attributes.notes === 'string' ? editing.attributes.notes : '')
      setAllBrands(false)
      setError(null)
      setBusy(false)
    }
  }, [open, editing])

  const save = async () => {
    setBusy(true)
    setError(null)
    try {
      const body = JSON.stringify({
        name: name.trim(),
        phone: phone.trim(),
        language,
        email: email.trim(),
        company_name: company.trim(),
        tags: tags.split(',').map((t) => t.trim()).filter(Boolean),
        notes: notes.trim(),
        // Brand membership on create: the current brand by default, or every
        // brand in the tenant when the operator toggles "all brands". (Ignored
        // on edit — PATCH doesn't touch brand links.)
        ...(editing
          ? {}
          : allBrands
            ? { all_brands: true }
            : activeBrand
              ? { brand: activeBrand.slug }
              : {}),
      })
      // PATCH /api/v1/contacts/<id> for edits (backend accepting the extra
      // fields lands in parallel; language already persists today), POST to
      // create.
      const res = await fetch(editing ? `${API}/v1/contacts/${editing.id}` : `${API}/v1/contacts`, {
        method: editing ? 'PATCH' : 'POST',
        headers: { 'Content-Type': 'application/json', ...authHeaders() },
        body,
      })
      const data = await res.json().catch(() => ({}))
      if (!res.ok) {
        throw new Error(
          (data as { error?: { message?: string } }).error?.message || `HTTP ${res.status}`,
        )
      }
      await reloadContacts()
      onClose()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={editing ? 'Edit contact' : 'New contact'}
      subtitle={editing ? 'Update this contact’s details.' : "Add a person to this brand's contact list."}
      size="lg"
      footer={
        <>
          {error && <span className="mr-auto text-[12px] font-semibold text-red-300">{error}</span>}
          <Button variant="secondary" onClick={onClose} style={{ padding: '8px 14px', fontSize: 12.5 }}>
            Cancel
          </Button>
          <Button
            variant="primary"
            onClick={save}
            disabled={busy || !name.trim() || !phone.trim()}
            style={{ padding: '8px 14px', fontSize: 12.5 }}
          >
            {busy && <Loader2 size={13} className="animate-spin" aria-hidden />} {editing ? 'Save changes' : 'Add contact'}
          </Button>
        </>
      }
    >
      <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
        <AddField label="Name" value={name} onChange={setName} placeholder="Ahmad Fauzi" required />
        <AddField label="Phone (E.164)" value={phone} onChange={setPhone} placeholder="+60 12-345 6789" required />
        <label className="block">
          <span className="mb-1 block text-[11px] font-bold text-text-high">Language</span>
          <select
            value={language}
            onChange={(e) => setLanguage(e.target.value)}
            className="w-full cursor-pointer rounded-lg border border-line-strong bg-well px-3 py-2 text-[12.5px] text-text-primary outline-none"
          >
            {LANGUAGES.map((l) => (
              <option key={l.value} value={l.value}>
                {l.label}
              </option>
            ))}
          </select>
        </label>
        <AddField label="Email (optional)" value={email} onChange={setEmail} placeholder="contact@example.com" />
        <AddField label="Company (optional)" value={company} onChange={setCompany} placeholder="Acme Sdn Bhd" />
        <AddField label="Tags (comma-separated)" value={tags} onChange={setTags} placeholder="vip, wholesale" />
        <AddField label="Notes (optional)" value={notes} onChange={setNotes} />
      </div>

      {!editing && activeBrand && (
        <label className="mt-3 flex cursor-pointer items-start gap-2 rounded-lg border border-line bg-fill-subtle px-3 py-2.5">
          <input
            type="checkbox"
            checked={allBrands}
            onChange={(e) => setAllBrands(e.target.checked)}
            className="mt-0.5 accent-brand-500"
          />
          <span className="text-[11.5px] leading-[1.5] text-text-muted">
            <span className="font-semibold text-text-high">Add to all brands</span> in this tenant.
            {' '}By default this contact is added to{' '}
            <span className="font-semibold text-text-high">{activeBrand.name}</span> only.
          </span>
        </label>
      )}
    </Modal>
  )
}

type Contact = ReturnType<typeof useApp>['contacts'][number]

/** Small "type · status" chip for the contact's primary record, or nothing
 *  when the contact has no linked records yet. */
function RecordChip({ d }: { d: Contact }) {
  const r = primaryRecord(d)
  if (!r) return <span className="text-[10.5px] text-text-faint">No records</span>
  return (
    <span className="inline-flex items-center gap-1 rounded-full bg-fill px-2 py-0.5 text-[10.5px] font-semibold text-text-mid">
      {r.type} · {r.status}
      {r.due_date && <span className="text-text-faint">· {r.due_date}</span>}
    </span>
  )
}

function TagChips({ tags }: { tags: string[] }) {
  if (!tags.length) return <span className="text-[10.5px] text-text-faint">—</span>
  return (
    <div className="flex flex-wrap gap-1">
      {tags.slice(0, 3).map((t) => (
        <span key={t} className="rounded-full bg-brand-400/10 px-2 py-0.5 text-[10px] font-semibold text-brand-300">
          {t}
        </span>
      ))}
      {tags.length > 3 && <span className="text-[10px] text-text-faint">+{tags.length - 3}</span>}
    </div>
  )
}

/* One kanban card. */
function KanbanCard({ d, onOpen }: { d: Contact; onOpen: () => void }) {
  return (
    <div
      onClick={onOpen}
      className="lcard cursor-pointer rounded-xl border border-line bg-well px-3 py-2.5 transition-colors hover:border-line-strong"
    >
      <div className="flex items-center gap-2.5">
        <div
          className="grid h-[30px] w-[30px] shrink-0 place-items-center rounded-full text-[11px] font-bold text-brand-200"
          style={{ background: 'linear-gradient(135deg,#1E3A5F,#2563EB)' }}
        >
          {initials(d.name)}
        </div>
        <div className="min-w-0 flex-1">
          <div className="truncate text-[12.5px] font-semibold text-text-high">{d.name}</div>
          <div className="text-[10.5px] text-text-muted">{d.phone}</div>
        </div>
      </div>
      <div className="mt-2 flex items-center justify-between gap-2">
        <RecordChip d={d} />
      </div>
      <div className="mt-2">
        <TagChips tags={d.tags} />
      </div>
    </div>
  )
}

export default function ContactsPage() {
  const router = useRouter()
  const { contacts, reloadContacts } = useApp()
  const { activeBrand } = useBrand()

  const [view, setView] = useState<'board' | 'list'>('board')
  const [status, setStatus] = useState('')
  const [recordType, setRecordType] = useState('')
  const [modalOpen, setModalOpen] = useState(false)
  const [editing, setEditing] = useState<Contact | null>(null)
  const [notice, setNotice] = useState<{ ok: boolean; text: string } | null>(null)
  // contacts.manage gates add/edit/delete (read after mount — localStorage)
  const [canManage, setCanManage] = useState(false)
  useEffect(() => setCanManage(hasCap('contacts.manage')), [])

  const openCreate = () => {
    setEditing(null)
    setNotice(null)
    setModalOpen(true)
  }
  const openEdit = (d: Contact) => {
    setEditing(d)
    setNotice(null)
    setModalOpen(true)
  }
  const deleteContact = async (d: Contact) => {
    if (!window.confirm(`Delete contact "${d.name}"? Their conversations stay but the record is removed.`)) return
    setNotice(null)
    try {
      // DELETE /api/v1/contacts/<id> — a 409 (e.g. blocked by linked data)
      // returns its reason in the v1 error envelope, surfaced verbatim.
      const res = await fetch(`${API}/v1/contacts/${d.id}`, { method: 'DELETE', headers: authHeaders() })
      if (!res.ok) {
        const data = await res.json().catch(() => ({}))
        throw new Error((data as { error?: { message?: string } }).error?.message || `HTTP ${res.status}`)
      }
      await reloadContacts()
      setNotice({ ok: true, text: `Contact ${d.name} deleted.` })
    } catch (e) {
      setNotice({ ok: false, text: e instanceof Error ? e.message : String(e) })
    }
  }

  useEffect(() => {
    try {
      const v = localStorage.getItem('contacts-view')
      if (v === 'list' || v === 'board') setView(v)
    } catch {
      /* ignore */
    }
  }, [])
  const setViewPersist = (v: 'board' | 'list') => {
    setView(v)
    try {
      localStorage.setItem('contacts-view', v)
    } catch {
      /* ignore */
    }
  }

  const filtered = useMemo(
    () =>
      contacts.filter(
        (d) =>
          (!status || d.status === status) &&
          (!recordType || primaryRecord(d)?.type === recordType),
      ),
    [contacts, status, recordType],
  )

  const hasFilters = Boolean(status || recordType)
  const clear = () => {
    setStatus('')
    setRecordType('')
  }

  const contactColumns: Column<Contact>[] = [
    {
      key: 'name',
      header: 'Contact',
      sortable: true,
      value: (d) => d.name,
      render: (d) => (
        <div className="flex items-center gap-2.5">
          <div
            className="grid h-[30px] w-[30px] shrink-0 place-items-center rounded-full text-[11px] font-bold text-brand-200"
            style={{ background: 'linear-gradient(135deg,#1E3A5F,#2563EB)' }}
          >
            {initials(d.name)}
          </div>
          <div>
            <div className="text-[13px] font-semibold text-text-high">{d.name}</div>
            {d.method && <span className="text-[10px] font-semibold text-text-muted">{methodLabel(d.method)}</span>}
          </div>
        </div>
      ),
    },
    {
      key: 'phone',
      header: 'Phone',
      sortable: true,
      width: 140,
      value: (d) => d.phone,
      render: (d) => <span className="font-mono text-[12px] text-text-muted">{d.phone}</span>,
    },
    {
      key: 'record',
      header: 'Record',
      width: 200,
      value: (d) => primaryRecord(d)?.type ?? '',
      render: (d) => <RecordChip d={d} />,
    },
    {
      key: 'status',
      header: 'Status',
      sortable: true,
      width: 110,
      value: (d) => d.status,
      render: (d) => <StatusBadge status={d.status} />,
    },
    {
      key: 'tags',
      header: 'Tags',
      width: 150,
      value: (d) => d.tags.join(' '),
      render: (d) => <TagChips tags={d.tags} />,
    },
    {
      key: 'actions',
      header: 'Actions',
      align: 'right',
      width: canManage ? 160 : 90,
      render: (d) => (
        <div className="flex items-center justify-end gap-1">
          <button
            onClick={(ev) => {
              ev.stopPropagation()
              router.push(`/contacts/${d.id}`)
            }}
            className="cursor-pointer rounded-md border border-brand-400/20 bg-brand-400/[0.12] px-2 py-[5px] text-[11px] font-semibold transition-colors hover:bg-brand-400/20"
            style={{ color: 'var(--brand-ink)' }}
          >
            View
          </button>
          {canManage && (
            <>
              <button
                onClick={(ev) => {
                  ev.stopPropagation()
                  openEdit(d)
                }}
                title={`Edit ${d.name}`}
                aria-label={`Edit contact ${d.name}`}
                className="grid h-7 w-7 shrink-0 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
              >
                <Pencil size={13} aria-hidden />
              </button>
              <button
                onClick={(ev) => {
                  ev.stopPropagation()
                  deleteContact(d)
                }}
                title={`Delete ${d.name}`}
                aria-label={`Delete contact ${d.name}`}
                className="grid h-7 w-7 shrink-0 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-red/15 hover:text-red-300"
              >
                <Trash2 size={13} aria-hidden />
              </button>
            </>
          )}
        </div>
      ),
    },
  ]

  return (
    <div className="flex min-h-0 flex-1 flex-col gap-3.5">
      {/* Header: title + count + view toggle */}
      <div className="flex flex-wrap items-center gap-3">
        <div>
          <h1 className="text-[20px] font-extrabold tracking-[-0.5px] text-text-heading">Contacts</h1>
          <div className="mt-0.5 text-[12px] text-text-muted">
            {contacts.length} {contacts.length === 1 ? 'contact' : 'contacts'}
            {activeBrand && <span className="text-text-faint"> · {activeBrand.name}</span>}
          </div>
        </div>

        {/* Board/List segmented toggle */}
        <div className="flex items-center gap-0.5 rounded-xl border border-line bg-well p-0.5">
          {(
            [
              { key: 'board', lbl: 'Board', Icon: LayoutGrid },
              { key: 'list', lbl: 'List', Icon: List },
            ] as const
          ).map((v) => {
            const on = view === v.key
            return (
              <button
                key={v.key}
                onClick={() => setViewPersist(v.key)}
                className="flex items-center gap-1.5 rounded-lg px-3 py-1.5 text-[12px] font-semibold transition-colors"
                style={{
                  background: on ? 'var(--nav-active-bg)' : 'transparent',
                  color: on ? 'var(--nav-active-text)' : 'var(--text-muted)',
                }}
              >
                <v.Icon size={14} strokeWidth={1.9} />
                {v.lbl}
              </button>
            )
          })}
        </div>

        <div className="ml-auto flex flex-wrap items-center gap-2">
          <FilterSelect value={status} onChange={setStatus} opts={STATUS_OPTS} />
          <FilterSelect value={recordType} onChange={setRecordType} opts={RECORD_TYPE_OPTS} />
          {hasFilters && (
            <button
              onClick={clear}
              className="rounded-lg border border-line-strong bg-well px-2.5 py-1.5 text-[12px] font-semibold text-text-muted transition-colors hover:bg-fill-hover"
            >
              Clear
            </button>
          )}
          {canManage && (
            <Button variant="primary" onClick={openCreate} style={{ padding: '6px 12px', fontSize: 12 }}>
              <Plus size={13} strokeWidth={2} aria-hidden /> Add contact
            </Button>
          )}
        </div>
      </div>

      {notice && (
        <div className={`text-[12px] font-semibold ${notice.ok ? 'text-green-300' : 'text-red-300'}`}>
          {notice.text}
        </div>
      )}

      <ContactModal open={modalOpen} editing={editing} onClose={() => setModalOpen(false)} />

      {view === 'board' ? (
        /* Kanban — horizontally scrolling columns, one per status */
        <div className="flex min-h-0 flex-1 gap-3 overflow-x-auto pb-2">
          {KANBAN.map((col) => {
            const cards = filtered.filter((d) => d.status === col.key)
            return (
              <div key={col.key} className="flex w-[260px] shrink-0 flex-col rounded-2xl border border-line bg-card">
                <div className="flex items-center gap-2 border-b border-line px-3.5 py-3">
                  <span className="h-2.5 w-2.5 shrink-0 rounded-full" style={{ background: col.color }} />
                  <span className="text-[12.5px] font-bold text-text-high">{col.lbl}</span>
                  <span className="ml-auto rounded-full bg-fill px-2 py-px text-[11px] font-bold text-text-muted">
                    {cards.length}
                  </span>
                </div>
                <div className="flex min-h-0 flex-1 flex-col gap-2 overflow-y-auto p-2.5">
                  {cards.length === 0 ? (
                    <div className="px-2 py-6 text-center text-[11px] text-text-faint">None</div>
                  ) : (
                    cards.map((d) => (
                      <KanbanCard key={d.id} d={d} onOpen={() => router.push(`/contacts/${d.id}`)} />
                    ))
                  )}
                </div>
              </div>
            )
          })}
        </div>
      ) : (
        /* List — searchable / sortable / paginated table */
        <div className="min-h-0 flex-1 overflow-y-auto">
          <DataTable
            columns={contactColumns}
            rows={filtered}
            rowKey={(d) => d.id}
            searchKeys={['name', 'phone', 'record', 'tags']}
            searchPlaceholder="Search contacts…"
            empty="No contacts match these filters."
            onRowClick={(d) => router.push(`/contacts/${d.id}`)}
          />
        </div>
      )}
    </div>
  )
}
