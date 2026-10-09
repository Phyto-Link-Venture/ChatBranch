'use client'

import { Suspense, useCallback, useMemo, useState, useEffect } from 'react'
import { useRouter, useSearchParams } from 'next/navigation'
import { Loader2, Pencil, Plus, Trash2, Upload } from 'lucide-react'
import { useApp } from '@/lib/AppContext'
import { useEntity } from '@/lib/EntityContext'
import { authHeaders, expireSessionOn401, hasCap } from '@/lib/auth'
import { initials, primaryRecord } from '@/lib/mocks'
import { StatusBadge } from '@/components/ui/Badges'
import { Button } from '@/components/ui/Button'
import { Modal } from '@/components/ui/Modal'
import { PageHeader } from '@/components/ui/PageHeader'
import { PhoneInput } from '@/components/ui/PhoneInput'
import { Select } from '@/components/ui/Select'
import { EntityPicker } from '@/components/ui/EntityPicker'
import { DataTable, type Column } from '@/components/ui/DataTable'
import { ContactImportModal } from '@/components/contacts/ContactImportModal'
import { CustomFieldsInputs } from '@/components/CustomFieldsInputs'
import { fetchCustomFields, type FieldDef } from '@/lib/customFields'
import { useT } from '@/i18n'
import { RECORD_TYPE_OPTS, languageOptions, recordTypeLabel } from '@/lib/contactIdentity'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

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
        className="h-9 w-full rounded-lg border border-line-strong bg-card px-3 text-[13px] text-text-primary outline-none transition-colors focus:border-text-primary"
      />
    </label>
  )
}

function ContactModal({
  open,
  editing,
  onClose,
  onSaved,
}: {
  open: boolean
  editing: Contact | null
  onClose: () => void
  onSaved: () => void
}) {
  const t = useT()
  const { reloadContacts } = useApp()
  const { activeEntity, entities } = useEntity()
  const [name, setName] = useState('')
  const [phone, setPhone] = useState('')
  const [language, setLanguage] = useState('en')
  const [email, setEmail] = useState('')
  const [company, setCompany] = useState('')
  const [tags, setTags] = useState('')
  const [notes, setNotes] = useState('')
  // Which entities the new contact joins (slugs). Defaults to the current
  // entity; the operator can tick any subset (e.g. 2 of 3).
  const [selectedEntities, setSelectedEntities] = useState<string[]>([])
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  // Custom Fields (Settings → Custom Fields): the per-vertical labels defined
  // for Customers. Definitions come from the server; the VALUES live in this
  // contact's free-form `attributes` JSON, which the AI already reads.
  const [customerFields, setCustomerFields] = useState<FieldDef[]>([])
  const [attributes, setAttributes] = useState<Record<string, unknown>>({})

  useEffect(() => {
    fetchCustomFields()
      .then((s) => setCustomerFields(s.customer))
      .catch(() => setCustomerFields([]))
  }, [])

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
      // Keep the whole attributes object — the custom-field inputs edit their
      // own keys inside it, so unrelated keys survive a save untouched.
      setAttributes({ ...(editing?.attributes ?? {}) })
      // Editing: start from the contact's CURRENT memberships so a save can't
      // silently drop them. Creating: default to the entity you're viewing.
      setSelectedEntities(
        editing
          ? contactEntityRefs(editing, entities).map((e) => e.slug)
          : activeEntity
            ? [activeEntity.slug]
            : [],
      )
      setError(null)
      setBusy(false)
    }
    // `entities` only supplies display names for slug→ref resolution; it must
    // not re-run (and reset) the form while the modal is open.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, editing, activeEntity])

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
        tags: tags.split(',').map((x) => x.trim()).filter(Boolean),
        notes: notes.trim(),
        // Custom Field values — merged onto the contact's `attributes` JSON.
        attributes,
        // Entity membership on create: the ticked entities (multi-select).
        // Ignored on edit — PATCH doesn't touch entity links.
        // Entity membership: sent on create AND edit — the backend syncs the
        // links to exactly this list (empty = belongs to no entity).
        entity_slugs: selectedEntities,
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
      onSaved()
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
      title={t(editing ? 'contacts.form.titleEdit' : 'contacts.form.titleNew')}
      subtitle={t(editing ? 'contacts.form.subtitleEdit' : 'contacts.form.subtitleNew')}
      size="lg"
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>
            {t('common.actions.cancel')}
          </Button>
          <Button variant="primary" onClick={save} disabled={busy || !name.trim() || !phone.trim()}>
            {busy && <Loader2 size={13} className="animate-spin" aria-hidden />}{' '}
            {t(editing ? 'contacts.form.saveChanges' : 'contacts.form.addContact')}
          </Button>
        </>
      }
    >
      {error && (
        <div role="alert" className="mb-3 rounded-lg border border-red/30 bg-red/10 px-3 py-2 text-[12.5px] font-semibold text-red-300">
          {error}
        </div>
      )}
      <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
        <AddField
          label={t('common.fields.name')}
          value={name}
          onChange={setName}
          placeholder={t('contacts.form.namePlaceholder')}
          required
        />
        <label className="block">
          <span className="mb-1 block text-[11px] font-bold text-text-high">
            {t('common.fields.phone')}
            <span className="text-red-300"> *</span>
          </span>
          {/* Country picker + national number — the stored value stays E.164. */}
          <PhoneInput value={phone} onChange={setPhone} ariaLabel={t('contacts.form.phoneAria')} />
        </label>
        <label className="block">
          <span className="mb-1 block text-[11px] font-bold text-text-high">{t('common.fields.language')}</span>
          <Select
            value={language}
            onChange={setLanguage}
            options={languageOptions(t)}
            size="md"
            ariaLabel={t('common.fields.language')}
          />
        </label>
        <AddField
          label={t('contacts.form.emailOptional')}
          value={email}
          onChange={setEmail}
          placeholder={t('contacts.form.emailPlaceholder')}
        />
        <AddField
          label={t('contacts.form.companyOptional')}
          value={company}
          onChange={setCompany}
          placeholder={t('contacts.form.companyPlaceholder')}
        />
        <AddField
          label={t('contacts.form.tagsComma')}
          value={tags}
          onChange={setTags}
          placeholder={t('contacts.form.tagsPlaceholder')}
        />
        <AddField label={t('contacts.form.notesOptional')} value={notes} onChange={setNotes} />
      </div>

      {customerFields.length > 0 && (
        <div className="mt-3 rounded-lg border border-line bg-fill-subtle px-3 py-2.5">
          <div className="mb-2 text-[11px] font-bold text-text-high">{t('contacts.form.customFields')}</div>
          <CustomFieldsInputs
            fields={customerFields}
            values={attributes}
            onChange={setAttributes}
            disabled={busy}
          />
          <p className="mt-1.5 text-[10.5px] text-text-faint">
            {t('contacts.form.customFieldsHint')}
          </p>
        </div>
      )}

      {entities.length > 0 && (
        <div className="mt-3 rounded-lg border border-line bg-fill-subtle px-3 py-2.5">
          <div className="mb-1.5 flex items-center justify-between">
            <span className="text-[11px] font-bold text-text-high">{t('contacts.form.entities')}</span>
            <div className="flex gap-2 text-[10.5px] font-semibold">
              <button
                type="button"
                onClick={() => setSelectedEntities(entities.map((e) => e.slug))}
                className="text-text-high hover:underline"
              >
                {t('common.status.all')}
              </button>
              <span className="text-line-strong">·</span>
              <button
                type="button"
                onClick={() =>
                  setSelectedEntities(
                    editing
                      ? contactEntityRefs(editing, entities).map((e) => e.slug)
                      : activeEntity
                        ? [activeEntity.slug]
                        : [],
                  )
                }
                className="text-text-muted hover:underline"
              >
                {t('common.actions.reset')}
              </button>
            </div>
          </div>
          <div className="flex flex-wrap gap-1.5">
            {entities.map((e) => {
              const on = selectedEntities.includes(e.slug)
              return (
                <button
                  key={e.slug}
                  type="button"
                  onClick={() =>
                    setSelectedEntities((cur) =>
                      cur.includes(e.slug) ? cur.filter((s) => s !== e.slug) : [...cur, e.slug],
                    )
                  }
                  aria-pressed={on}
                  className={`rounded-full border px-2.5 py-1 text-[11.5px] font-medium transition-colors ${
                    on
                      ? 'border-primary bg-primary text-on-primary'
                      : 'border-line-strong bg-card text-text-mid hover:bg-fill-hover'
                  }`}
                >
                  {e.name}
                </button>
              )
            })}
          </div>
          <p className="mt-1.5 text-[10.5px] text-text-faint">
            {t('contacts.form.entitiesHint')}
          </p>
        </div>
      )}
    </Modal>
  )
}

type Contact = ReturnType<typeof useApp>['contacts'][number]

/** Small "type · status" chip for the contact's primary record. */
function RecordChip({ d }: { d: Contact }) {
  const t = useT()
  const r = primaryRecord(d)
  if (!r) return <span className="text-[11.5px] text-text-muted">—</span>
  return (
    <span className="inline-flex items-center gap-1 rounded-full border border-line px-2 py-0.5 text-[11px] text-text-mid">
      <span>{recordTypeLabel(t, r.type)}</span> · {r.status}
    </span>
  )
}

/**
 * Contact.entities arrives either as slugs (/api/contacts) or as {slug,name}
 * objects (/api/v1/contacts/<id>). Normalize to {slug,name}, resolving display
 * names from the entity list when only a slug is known.
 */
function contactEntityRefs(
  contact: Pick<Contact, 'entities'>,
  known: { slug: string; name: string }[],
): { slug: string; name: string }[] {
  return (contact.entities ?? []).map((e) =>
    typeof e === 'string' ? { slug: e, name: known.find((k) => k.slug === e)?.name ?? e } : e,
  )
}

function TagChips({ tags }: { tags: string[] }) {
  if (!tags.length) return <span className="text-[11.5px] text-text-muted">—</span>
  return (
    <div className="flex flex-wrap gap-1">
      {tags.slice(0, 3).map((tag) => (
        <span key={tag} className="rounded-full border border-line-strong px-2 py-0.5 text-[11px] text-text-mid">
          {tag}
        </span>
      ))}
      {tags.length > 3 && <span className="text-[11px] text-text-muted">+{tags.length - 3}</span>}
    </div>
  )
}

function ContactsList() {
  const t = useT()
  const router = useRouter()
  const searchParams = useSearchParams()
  const { reloadContacts } = useApp()
  const { activeEntity, entities: entityList } = useEntity()

  // Filters: entity (defaults to the top-bar entity), contact tag, record type.
  // Search is the table's own box, seeded by a `?q=` deep link ("Search all").
  const [entityFilter, setEntityFilter] = useState<string | null>(activeEntity?.slug ?? null)
  const [tag, setTag] = useState(searchParams.get('tag') ?? '')
  const [recordType, setRecordType] = useState('')
  const q = searchParams.get('q') ?? undefined
  useEffect(() => setEntityFilter(activeEntity?.slug ?? null), [activeEntity?.slug])
  useEffect(() => {
    const fromUrl = searchParams.get('tag')
    if (fromUrl !== null) setTag(fromUrl)
  }, [searchParams])

  const [rows, setRows] = useState<Contact[]>([])
  const [loading, setLoading] = useState(true)
  const [modalOpen, setModalOpen] = useState(false)
  const [editing, setEditing] = useState<Contact | null>(null)
  const [notice, setNotice] = useState<{ ok: boolean; text: string } | null>(null)
  const [importOpen, setImportOpen] = useState(false)
  // contacts.manage gates add/edit/delete (read after mount — localStorage)
  const [canManage, setCanManage] = useState(false)
  useEffect(() => setCanManage(hasCap('contacts.manage')), [])

  // Server-side narrowing: ?entity= (the entity and its branches) and ?tag=.
  // The tag is ALSO applied here, so the list is right before the API knows it.
  const load = useCallback(async () => {
    const p = new URLSearchParams({ limit: '2000' })
    if (entityFilter) p.set('entity', entityFilter)
    if (tag) p.set('tag', tag)
    try {
      const res = await fetch(`${API}/contacts?${p}`, { headers: authHeaders() })
      if (expireSessionOn401(res) || !res.ok) return
      const d = await res.json()
      if (Array.isArray(d.contacts)) setRows(d.contacts)
    } catch {
      /* API down — keep the last list */
    } finally {
      setLoading(false)
    }
  }, [entityFilter, tag])
  useEffect(() => {
    load()
  }, [load])

  // Tag choices: every tag in use across the caller's contacts.
  const [allTags, setAllTags] = useState<string[]>([])
  useEffect(() => {
    fetch(`${API}/contacts?limit=2000`, { headers: authHeaders() })
      .then((r) => (r.ok ? r.json() : { contacts: [] }))
      .then((d: { contacts?: { tags?: string[] }[] }) =>
        setAllTags(Array.from(new Set((d.contacts ?? []).flatMap((c) => c.tags ?? []))).sort((a, b) => a.localeCompare(b))),
      )
      .catch(() => setAllTags([]))
  }, [rows.length])

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
    if (!window.confirm(t('contacts.list.confirmDelete', { name: d.name }))) return
    setNotice(null)
    try {
      // DELETE /api/v1/contacts/<id> — a 409 (e.g. blocked by linked data)
      // returns its reason in the v1 error envelope, surfaced verbatim.
      const res = await fetch(`${API}/v1/contacts/${d.id}`, { method: 'DELETE', headers: authHeaders() })
      if (!res.ok) {
        const data = await res.json().catch(() => ({}))
        throw new Error((data as { error?: { message?: string } }).error?.message || `HTTP ${res.status}`)
      }
      await Promise.all([load(), reloadContacts()])
      setNotice({ ok: true, text: t('contacts.list.deleted', { name: d.name }) })
    } catch (e) {
      setNotice({ ok: false, text: e instanceof Error ? e.message : String(e) })
    }
  }

  const filtered = useMemo(
    () =>
      rows.filter(
        (d) =>
          (!tag || (d.tags ?? []).some((x) => x.toLowerCase() === tag.toLowerCase())) &&
          (!recordType || primaryRecord(d)?.type === recordType),
      ),
    [rows, tag, recordType],
  )

  const hasFilters = Boolean(tag || recordType || entityFilter !== (activeEntity?.slug ?? null))
  const clear = () => {
    setTag('')
    setRecordType('')
    setEntityFilter(activeEntity?.slug ?? null)
  }

  const contactColumns: Column<Contact>[] = [
    {
      key: 'name',
      header: t('contacts.list.colContact'),
      sortable: true,
      value: (d) => d.name,
      render: (d) => (
        <div className="flex items-center gap-2.5">
          <div className="grid h-8 w-8 shrink-0 place-items-center rounded-full bg-[var(--nav-active-bg)] text-[11px] font-semibold text-text-high">
            {initials(d.name)}
          </div>
          <div className="min-w-0">
            <div className="truncate text-[13px] font-semibold text-text-heading">{d.name}</div>
            {d.email && <div className="truncate text-[11.5px] text-text-muted">{d.email}</div>}
          </div>
        </div>
      ),
    },
    {
      key: 'phone',
      header: t('common.fields.phone'),
      sortable: true,
      width: 150,
      value: (d) => d.phone,
      render: (d) => <span className="font-mono text-[12px] text-text-mid">{d.phone}</span>,
    },
    {
      key: 'tags',
      header: t('common.fields.tags'),
      width: 170,
      value: (d) => d.tags.join(' '),
      render: (d) => <TagChips tags={d.tags} />,
    },
    {
      // Entity membership at a glance.
      key: 'entities',
      header: t('contacts.list.colEntities'),
      width: 170,
      value: (d) =>
        contactEntityRefs(d, entityList)
          .map((e) => e.name)
          .join(' '),
      render: (d) => {
        const list = contactEntityRefs(d, entityList)
        if (list.length === 0) return <span className="text-[11.5px] text-text-muted">—</span>
        return (
          <div className="flex flex-wrap gap-1">
            {list.slice(0, 2).map((e) => (
              <span key={e.slug} className="rounded-full bg-fill px-2 py-0.5 text-[11px] text-text-mid">
                {e.name}
              </span>
            ))}
            {list.length > 2 && <span className="text-[11px] text-text-muted">+{list.length - 2}</span>}
          </div>
        )
      },
    },
    {
      key: 'record',
      header: t('contacts.list.colRecord'),
      width: 170,
      value: (d) => {
        const type = primaryRecord(d)?.type
        return type ? recordTypeLabel(t, type) : ''
      },
      render: (d) => <RecordChip d={d} />,
    },
    {
      key: 'status',
      header: t('common.fields.status'),
      sortable: true,
      width: 110,
      value: (d) => d.status,
      render: (d) => <StatusBadge status={d.status} />,
    },
    {
      key: 'actions',
      header: t('contacts.list.colActions'),
      srHeader: true,
      align: 'right',
      width: canManage ? 150 : 80,
      render: (d) => (
        <div className="flex items-center justify-end gap-1">
          <Button
            variant="ghost"
            size="sm"
            onClick={(ev) => {
              ev.stopPropagation()
              router.push(`/contacts/${d.id}`)
            }}
          >
            {t('common.actions.view')}
          </Button>
          {canManage && (
            <>
              <button
                onClick={(ev) => {
                  ev.stopPropagation()
                  openEdit(d)
                }}
                title={t('contacts.list.editTitle', { name: d.name })}
                aria-label={t('contacts.list.editAria', { name: d.name })}
                className="grid h-8 w-8 shrink-0 place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
              >
                <Pencil size={13} aria-hidden />
              </button>
              <button
                onClick={(ev) => {
                  ev.stopPropagation()
                  deleteContact(d)
                }}
                title={t('contacts.list.deleteTitle', { name: d.name })}
                aria-label={t('contacts.list.deleteAria', { name: d.name })}
                className="grid h-8 w-8 shrink-0 place-items-center rounded-lg text-text-muted transition-colors hover:bg-red/10 hover:text-red-300"
              >
                <Trash2 size={13} aria-hidden />
              </button>
            </>
          )}
        </div>
      ),
    },
  ]

  const entityName = entityList.find((e) => e.slug === entityFilter)?.name

  return (
    <div className="flex min-h-0 flex-1 flex-col gap-4">
      <PageHeader
        eyebrow={t('shell.groups.work')}
        title={t('shell.nav.contacts')}
        subtitle={
          loading
            ? t('common.status.loading')
            : entityName
              ? t('contacts.list.countIn', { count: filtered.length, entity: entityName })
              : t('contacts.list.countAll', { count: filtered.length })
        }
        actions={
          canManage && (
            <span data-tour="contacts-actions" className="flex flex-wrap items-center gap-2">
              <Button variant="secondary" onClick={() => setImportOpen(true)}>
                <Upload size={14} aria-hidden /> {t('contacts.list.import')}
              </Button>
              <Button variant="primary" onClick={openCreate}>
                <Plus size={14} aria-hidden /> {t('contacts.list.addContact')}
              </Button>
            </span>
          )
        }
      />

      <ContactImportModal
        open={importOpen}
        onClose={() => setImportOpen(false)}
        onDone={async () => {
          await Promise.all([load(), reloadContacts()])
        }}
      />

      {notice && !modalOpen && (
        <div
          role={notice.ok ? 'status' : 'alert'}
          className={`rounded-lg border px-3 py-2 text-[12.5px] font-semibold ${
            notice.ok ? 'border-green/30 bg-green/10 text-green-300' : 'border-red/30 bg-red/10 text-red-300'
          }`}
        >
          {notice.text}
        </div>
      )}

      <ContactModal open={modalOpen} editing={editing} onClose={() => setModalOpen(false)} onSaved={load} />

      <div data-tour="contacts-list" className="min-h-0 flex-1">
        <DataTable
          columns={contactColumns}
          rows={filtered}
          rowKey={(d) => d.id}
          searchKeys={['name', 'phone', 'tags', 'entities', 'record']}
          searchPlaceholder={t('contacts.list.searchPlaceholder')}
          initialQuery={q}
          pageSize={25}
          empty={
            hasFilters
              ? t('contacts.list.emptyFiltered')
              : entityName
                ? t('contacts.list.emptyIn', { entity: entityName })
                : t('contacts.list.empty')
          }
          onRowClick={(d) => router.push(`/contacts/${d.id}`)}
          toolbarRight={
            <div className="flex flex-wrap items-center gap-2">
              <EntityPicker
                value={entityFilter}
                onChange={setEntityFilter}
                globalLabel={t('contacts.list.allEntities')}
                size="sm"
                ariaLabel={t('contacts.list.filterEntity')}
                className="w-[170px]"
              />
              <Select
                value={tag}
                onChange={setTag}
                options={[{ value: '', label: t('contacts.list.anyTag') }, ...allTags.map((x) => ({ value: x, label: x }))]}
                ariaLabel={t('contacts.list.filterTag')}
                className="w-[140px]"
                searchable={allTags.length > 8}
              />
              <Select
                value={recordType}
                onChange={setRecordType}
                options={RECORD_TYPE_OPTS.map((o) => ({ value: o.value, label: t(o.label) }))}
                ariaLabel={t('contacts.list.filterRecord')}
                className="w-[140px]"
              />
              {hasFilters && (
                <Button variant="ghost" size="sm" onClick={clear}>
                  {t('common.actions.clear')}
                </Button>
              )}
            </div>
          }
        />
      </div>
    </div>
  )
}

/** useSearchParams (the ?q= / ?tag= deep links) needs a Suspense boundary. */
export default function ContactsPage() {
  return (
    <Suspense fallback={null}>
      <ContactsList />
    </Suspense>
  )
}
