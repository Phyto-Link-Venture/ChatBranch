'use client'

import { useCallback, useEffect, useMemo, useState } from 'react'
import {
  AlertCircle,
  CheckCircle,
  ChevronDown,
  ChevronRight,
  Link2,
  Loader2,
  Lock,
  Plus,
  RefreshCw,
  Trash2,
  X,
} from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { PhoneInput } from '@/components/ui/PhoneInput'
import { CustomFieldsInputs } from '@/components/CustomFieldsInputs'
import { fetchCustomFields, formatFieldValue, type FieldDef } from '@/lib/customFields'
import { roleHeaders } from '@/lib/role'
import { useRole } from '@/lib/useRole'
import { expireSessionOn401 } from '@/lib/auth'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

interface Link {
  id: number
  contact_id: number
  contact_name: string
  relationship: string
  active: boolean
  since: string | null
}

interface Resource {
  id: number
  name: string
  email: string
  phone: string
  active: boolean
  config: Record<string, unknown>
  /** Custom Fields values — the per-vertical labels from Settings → Custom Fields. */
  attributes?: Record<string, unknown>
  links_count: number
}

interface ContactLite {
  id: number | string
  name: string
  phone?: string
}

async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}/${path}`, {
    headers: { 'Content-Type': 'application/json', ...roleHeaders() },
    ...init,
  })
  if (expireSessionOn401(res)) throw new Error('Session expired — redirecting to login…')
  const data = await res.json().catch(() => ({}))
  if (!res.ok) throw new Error((data as { detail?: string }).detail || `HTTP ${res.status}`)
  return data as T
}

const inputCls =
  'w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[12.5px] text-text-primary outline-none transition-colors focus:border-brand-400'


/**
 * The provider's filled-in Custom Fields as one line of chips ("Language:
 * Malay"), so an admin reads a vertical's own labels off the list without
 * opening the form. Empty values are skipped; all-empty renders nothing.
 */
function FieldChips({ fields, values }: { fields: FieldDef[]; values?: Record<string, unknown> }) {
  const filled = fields
    .map((f) => ({ key: f.key, label: f.label, text: formatFieldValue(f, values?.[f.key]) }))
    .filter((c) => c.text !== '')
  if (filled.length === 0) return null
  return (
    <span className="mt-1 flex flex-wrap gap-1">
      {filled.map((c) => (
        <span
          key={c.key}
          className="rounded-full border border-line bg-fill px-2 py-0.5 text-[10px] text-text-muted"
        >
          <span className="font-bold text-text-mid">{c.label}:</span> {c.text}
        </span>
      ))}
    </span>
  )
}

type Draft = {
  id?: number
  name?: string
  email?: string
  phone?: string
  active?: boolean
  attributes?: Record<string, unknown>
}

/**
 * Settings → Service Providers. The delivery side of the business — the
 * teachers / coaches / practitioners customers book — and the binding that
 * says which customers belong to each. The per-vertical word ("teacher") is
 * set by the `resource_label` app setting; the model is generic.
 *
 * Calendar/Sheet references are NOT edited here: they belong to a signed-in
 * user's own integration connection (Integrations), not to the provider
 * record. Existing stored values are left untouched — this form simply omits
 * the `config` key.
 */
export function ProvidersSettings() {
  const { role, ready } = useRole()
  const canManage = role === 'admin' || role === 'superadmin'

  const [resources, setResources] = useState<Resource[] | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [draft, setDraft] = useState<Draft | null>(null)
  const [busy, setBusy] = useState(false)
  const [providerFields, setProviderFields] = useState<FieldDef[]>([])

  const [expanded, setExpanded] = useState<number | null>(null)
  const [links, setLinks] = useState<Record<number, Link[]>>({})
  const [contacts, setContacts] = useState<ContactLite[] | null>(null)
  const [query, setQuery] = useState('')
  const [binding, setBinding] = useState<number | string | null>(null)

  const load = useCallback(async () => {
    try {
      const d = await api<{ resources: Resource[] }>('resources')
      setResources(d.resources)
      setLoadError(null)
    } catch (e) {
      setResources(null)
      setLoadError(e instanceof Error ? e.message : String(e))
    }
  }, [])

  useEffect(() => {
    load()
  }, [load])

  // Custom Fields are optional decoration — a missing/failing endpoint must
  // never take the providers page down with it.
  useEffect(() => {
    let alive = true
    fetchCustomFields()
      .then((s) => {
        if (alive) setProviderFields(s.provider)
      })
      .catch(() => {
        if (alive) setProviderFields([])
      })
    return () => {
      alive = false
    }
  }, [])

  const submit = async () => {
    if (!draft) return
    setBusy(true)
    setError(null)
    try {
      const body = {
        name: draft.name || '',
        email: draft.email || '',
        phone: draft.phone || '',
        active: draft.active ?? true,
        // No `config` key: the Google Sheet / calendar references are an
        // INTEGRATION concern owned by a signed-in user, not a property of the
        // provider record, so this form no longer edits them. Omitting the key
        // leaves anything already stored untouched.
        attributes: draft.attributes ?? {},
      }
      if (draft.id) {
        await api(`resources/${draft.id}`, { method: 'PATCH', body: JSON.stringify(body) })
        setNotice('Provider updated.')
      } else {
        await api('resources', { method: 'POST', body: JSON.stringify(body) })
        setNotice('Provider added.')
      }
      setDraft(null)
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  const startEdit = (r: Resource) =>
    setDraft({
      id: r.id,
      name: r.name,
      email: r.email,
      phone: r.phone,
      active: r.active,
      attributes: r.attributes ?? {},
    })

  const remove = async (r: Resource) => {
    if (!window.confirm(`Remove "${r.name}"? Their bindings are removed too.`)) return
    try {
      await api(`resources/${r.id}`, { method: 'DELETE' })
      setNotice(`"${r.name}" removed.`)
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  const loadLinks = useCallback(async (id: number) => {
    try {
      const d = await api<{ links: Link[] }>(`resources/${id}/links`)
      setLinks((prev) => ({ ...prev, [id]: d.links }))
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }, [])

  const toggleExpand = async (r: Resource) => {
    if (expanded === r.id) {
      setExpanded(null)
      return
    }
    setExpanded(r.id)
    setQuery('')
    await loadLinks(r.id)
    if (contacts === null) {
      try {
        const d = await api<{ contacts: ContactLite[] }>('contacts')
        setContacts(Array.isArray(d.contacts) ? d.contacts : [])
      } catch {
        setContacts([])
      }
    }
  }

  const addLink = async (r: Resource, contactId: number | string) => {
    setBinding(contactId)
    try {
      await api(`resources/${r.id}/links`, {
        method: 'POST',
        body: JSON.stringify({ contact_id: contactId }),
      })
      setQuery('')
      await loadLinks(r.id)
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBinding(null)
    }
  }

  const removeLink = async (r: Resource, link: Link) => {
    try {
      await api(`resources/${r.id}/links/${link.id}`, { method: 'DELETE' })
      await loadLinks(r.id)
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  const matches = useMemo(() => {
    const q = query.trim().toLowerCase()
    if (!q || contacts === null) return []
    return contacts
      .filter((c) => c.name?.toLowerCase().includes(q) || (c.phone || '').includes(q))
      .slice(0, 8)
  }, [query, contacts])

  if (!ready || (resources === null && !loadError)) {
    return (
      <div className="flex items-center gap-1.5 py-6 text-[12px] text-text-muted">
        <Loader2 size={13} className="animate-spin" aria-hidden /> Loading providers…
      </div>
    )
  }

  if (resources === null) {
    return (
      <div className="py-6 text-[12px]">
        <div
          className="flex items-start gap-2 rounded-lg border border-red-500/20 bg-red-500/10 px-3 py-2"
          style={{ color: 'var(--red-ink)' }}
        >
          <AlertCircle size={13} className="mt-0.5 shrink-0" aria-hidden />
          <span>Couldn&apos;t load providers — {loadError}</span>
        </div>
        <Button variant="ghost" onClick={load} style={{ marginTop: 10 }}>
          <RefreshCw size={13} aria-hidden /> Retry
        </Button>
      </div>
    )
  }

  return (
    <div className="flex flex-col gap-4 pt-3.5">
      <div className="flex items-start gap-2">
        <p className="min-w-0 max-w-[720px] flex-1 text-[11.5px] leading-relaxed text-text-mid">
          The people customers book — <span className="font-bold text-text-high">teachers, coaches,
          practitioners</span> — and which customers belong to each. Calendars and availability
          sheets come from the signed-in user&apos;s own connection under{' '}
          <span className="font-bold text-text-high">Integrations</span>, not from this record.
        </p>
        {canManage && (
          <div className="shrink-0">
            <Button onClick={() => setDraft({ active: true })}>
              <Plus size={13} aria-hidden /> Add provider
            </Button>
          </div>
        )}
      </div>

      {!canManage && (
        <div className="flex items-center gap-1.5 text-[11px] font-semibold text-text-muted">
          <Lock size={11} aria-hidden /> Read-only — only an admin can manage providers.
        </div>
      )}
      {error && (
        <div
          className="flex items-start gap-2 rounded-lg border border-red-500/20 bg-red-500/10 px-3 py-2 text-[12px]"
          style={{ color: 'var(--red-ink)' }}
        >
          <AlertCircle size={13} className="mt-0.5 shrink-0" aria-hidden /> {error}
        </div>
      )}
      {notice && (
        <div
          className="flex items-start gap-2 rounded-lg border border-brand-400/20 bg-brand-400/10 px-3 py-2 text-[12px]"
          style={{ color: 'var(--green-ink)' }}
        >
          <CheckCircle size={13} className="mt-0.5 shrink-0" aria-hidden /> {notice}
        </div>
      )}

      {draft && (
        <div className="rounded-xl border border-line bg-fill-subtle p-4">
          <div className="mb-3 flex items-center justify-between">
            <span className="text-[12.5px] font-bold text-text-high">
              {draft.id ? `Edit "${draft.name}"` : 'Add provider'}
            </span>
            <button
              onClick={() => setDraft(null)}
              aria-label="Close form"
              className="grid h-7 w-7 cursor-pointer place-items-center rounded-md text-text-muted hover:bg-fill-hover"
            >
              <X size={14} aria-hidden />
            </button>
          </div>

          <div className="grid gap-3 md:grid-cols-3">
            <label className="block md:col-span-1">
              <span className="mb-1 block text-[10.5px] font-bold text-text-high">Name</span>
              <input
                value={draft.name || ''}
                placeholder="Ms Lee"
                onChange={(e) => setDraft({ ...draft, name: e.target.value })}
                className={inputCls}
              />
            </label>
            <label className="block">
              <span className="mb-1 block text-[10.5px] font-bold text-text-high">Email (optional)</span>
              <input
                value={draft.email || ''}
                placeholder="lee@studio.com"
                onChange={(e) => setDraft({ ...draft, email: e.target.value })}
                className={inputCls}
              />
            </label>
            <label className="block">
              <span className="mb-1 block text-[10.5px] font-bold text-text-high">Phone (optional)</span>
              {/* Country picker + national number — stored as E.164. */}
              <PhoneInput
                value={draft.phone || ''}
                onChange={(phone) => setDraft({ ...draft, phone })}
                ariaLabel="Provider phone number"
              />
            </label>
          </div>

          {providerFields.length > 0 && (
            <div className="mt-3 border-t border-line pt-3">
              <div className="mb-2 text-[10.5px] font-bold uppercase tracking-[0.04em] text-text-muted">
                Custom fields
              </div>
              <CustomFieldsInputs
                fields={providerFields}
                values={draft.attributes ?? {}}
                onChange={(attrs) => setDraft({ ...draft, attributes: attrs })}
              />
            </div>
          )}

          <label className="mt-3 flex items-center gap-2 text-[11.5px] text-text-mid">
            <input
              type="checkbox"
              checked={draft.active ?? true}
              onChange={(e) => setDraft({ ...draft, active: e.target.checked })}
            />
            Active
          </label>

          <div className="mt-3 flex items-center gap-2">
            <Button onClick={submit} disabled={busy || !(draft.name || '').trim()}>
              {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <CheckCircle size={13} aria-hidden />}
              {draft.id ? 'Save changes' : 'Add provider'}
            </Button>
            <Button variant="ghost" onClick={() => setDraft(null)} disabled={busy}>
              Cancel
            </Button>
          </div>
        </div>
      )}

      <div className="flex flex-col gap-1.5">
        {resources.length === 0 && (
          <div className="rounded-xl border border-dashed border-line-strong px-4 py-6 text-center text-[12px] text-text-muted">
            No providers yet — add a teacher/coach, then bind the customers they serve.
          </div>
        )}
        {resources.map((r) => (
          <div key={r.id} className="rounded-xl border border-line bg-card px-4 py-2.5">
            <div className="flex items-center gap-2.5">
              <button
                onClick={() => toggleExpand(r)}
                aria-label={`Toggle bound customers for ${r.name}`}
                className="grid h-7 w-7 shrink-0 cursor-pointer place-items-center rounded-lg text-text-muted hover:bg-fill-hover"
              >
                {expanded === r.id ? <ChevronDown size={14} aria-hidden /> : <ChevronRight size={14} aria-hidden />}
              </button>
              <button onClick={() => canManage && startEdit(r)} className="min-w-0 flex-1 cursor-pointer text-left">
                <span className="flex items-center gap-2">
                  <span className="truncate text-[12px] font-bold text-text-high">{r.name}</span>
                  {!r.active && <span className="shrink-0 text-[10px] font-bold text-text-muted">Off</span>}
                </span>
                <span className="block truncate text-[11px] text-text-muted">
                  {r.links_count} bound {r.links_count === 1 ? 'customer' : 'customers'}
                </span>
                <FieldChips fields={providerFields} values={r.attributes} />
              </button>
              {canManage && (
                <button
                  onClick={() => remove(r)}
                  title={`Remove ${r.name}`}
                  aria-label={`Remove ${r.name}`}
                  className="grid h-7 w-7 shrink-0 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-red/15 hover:text-red-300"
                >
                  <Trash2 size={13} aria-hidden />
                </button>
              )}
            </div>

            {expanded === r.id && (
              <div className="mt-2 border-t border-line pt-2.5">
                <div className="mb-1.5 text-[10.5px] font-bold uppercase tracking-[0.04em] text-text-muted">
                  Bound customers
                </div>
                <div className="flex flex-col gap-1">
                  {(links[r.id] || []).length === 0 && (
                    <span className="text-[11px] text-text-muted">No customers bound yet.</span>
                  )}
                  {(links[r.id] || []).map((ln) => (
                    <div key={ln.id} className="flex items-center gap-2 text-[11.5px]">
                      <Link2 size={12} className="shrink-0 text-text-muted" aria-hidden />
                      <span className="min-w-0 flex-1 truncate text-text-mid">{ln.contact_name}</span>
                      {ln.relationship && (
                        <span className="shrink-0 text-[10px] text-text-muted">{ln.relationship}</span>
                      )}
                      {canManage && (
                        <button
                          onClick={() => removeLink(r, ln)}
                          aria-label={`Unbind ${ln.contact_name}`}
                          className="grid h-6 w-6 shrink-0 cursor-pointer place-items-center rounded text-text-muted hover:bg-red/15 hover:text-red-300"
                        >
                          <X size={12} aria-hidden />
                        </button>
                      )}
                    </div>
                  ))}
                </div>

                {canManage && (
                  <div className="relative mt-2">
                    <input
                      value={query}
                      placeholder="Bind a customer — type a name or phone…"
                      onChange={(e) => setQuery(e.target.value)}
                      className={inputCls}
                    />
                    {matches.length > 0 && (
                      <div className="absolute z-10 mt-1 w-full overflow-hidden rounded-lg border border-line bg-card shadow-lg">
                        {matches.map((c) => (
                          <button
                            key={c.id}
                            onClick={() => addLink(r, c.id)}
                            disabled={binding !== null}
                            className="flex w-full cursor-pointer items-center gap-2 px-3 py-2 text-left text-[11.5px] text-text-mid hover:bg-fill-hover disabled:opacity-50"
                          >
                            {binding === c.id ? (
                              <Loader2 size={12} className="shrink-0 animate-spin text-text-muted" aria-hidden />
                            ) : (
                              <Plus size={12} className="shrink-0 text-text-muted" aria-hidden />
                            )}
                            <span className="truncate">{c.name}</span>
                            {c.phone && <span className="shrink-0 text-[10px] text-text-muted">{c.phone}</span>}
                          </button>
                        ))}
                      </div>
                    )}
                  </div>
                )}
              </div>
            )}
          </div>
        ))}
      </div>
    </div>
  )
}
