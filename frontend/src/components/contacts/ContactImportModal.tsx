'use client'

import { useEffect, useRef, useState } from 'react'
import { CheckCircle, FileSpreadsheet, Loader2, Upload } from 'lucide-react'
import { Modal } from '@/components/ui/Modal'
import { Button } from '@/components/ui/Button'
import { authHeaders } from '@/lib/auth'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8000/api'

interface ImportResult {
  preview: boolean
  headers: string[]
  mapping: Record<string, string>
  fields: string[]
  total: number
  created: number
  updated: number
  skipped: number
  errors: { row: number; reason: string }[]
}

const FIELD_LABELS: Record<string, string> = {
  name: 'Name',
  phone: 'Phone',
  email: 'Email',
  company_name: 'Company',
  language: 'Language',
  external_id: 'External ID',
  notes: 'Notes',
  tags: 'Tags',
}
const REQUIRED = new Set(['name', 'phone'])

async function post(file: File, commit: boolean, mapping?: Record<string, string>): Promise<ImportResult> {
  const fd = new FormData()
  fd.append('file', file)
  if (mapping) fd.append('mapping', JSON.stringify(mapping))
  // NB: no Content-Type header — the browser sets the multipart boundary.
  const res = await fetch(`${API}/contacts/import${commit ? '' : '?preview=1'}`, {
    method: 'POST',
    headers: authHeaders(),
    body: fd,
  })
  const data = await res.json().catch(() => ({}))
  if (!res.ok) throw new Error((data as { detail?: string }).detail || `HTTP ${res.status}`)
  return data as ImportResult
}

/**
 * Bulk-import contacts from an Excel/CSV file. Picks a file → previews the
 * detected column mapping + created/updated/skipped counts (writes nothing) →
 * lets the operator correct the mapping → commits.
 */
export function ContactImportModal({
  open,
  onClose,
  onDone,
}: {
  open: boolean
  onClose: () => void
  onDone: () => Promise<void> | void
}) {
  const [file, setFile] = useState<File | null>(null)
  const [preview, setPreview] = useState<ImportResult | null>(null)
  const [mapping, setMapping] = useState<Record<string, string>>({})
  const [done, setDone] = useState<ImportResult | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const inputRef = useRef<HTMLInputElement>(null)

  useEffect(() => {
    if (!open) {
      setFile(null); setPreview(null); setMapping({}); setDone(null)
      setBusy(false); setError(null)
    }
  }, [open])

  const pick = async (f: File) => {
    setError(null); setDone(null); setFile(f); setBusy(true)
    try {
      const r = await post(f, false)
      setPreview(r)
      setMapping(r.mapping)
    } catch (e) {
      setPreview(null)
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  const commit = async () => {
    if (!file) return
    setBusy(true); setError(null)
    try {
      const r = await post(file, true, mapping)
      setDone(r)
      await onDone()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  const mappingOk = Boolean(mapping.name && mapping.phone)

  return (
    <Modal
      open={open}
      onClose={onClose}
      title="Import contacts"
      subtitle="Upload an Excel (.xlsx) or CSV file. Rows are matched by external ID, then phone."
      size="lg"
      footer={
        <>
          {error && <span className="mr-auto text-[12px] font-semibold text-red-300">{error}</span>}
          <Button variant="secondary" onClick={onClose} style={{ padding: '8px 14px', fontSize: 12.5 }}>
            {done ? 'Close' : 'Cancel'}
          </Button>
          {preview && !done && (
            <Button
              variant="primary"
              onClick={commit}
              disabled={busy || !mappingOk}
              style={{ padding: '8px 14px', fontSize: 12.5 }}
            >
              {busy && <Loader2 size={13} className="animate-spin" aria-hidden />}
              Import {preview.total} {preview.total === 1 ? 'row' : 'rows'}
            </Button>
          )}
        </>
      }
    >
      {/* file picker */}
      {!done && (
        <button
          onClick={() => inputRef.current?.click()}
          className="flex w-full cursor-pointer flex-col items-center gap-2 rounded-xl border border-dashed border-line-strong bg-fill-subtle px-4 py-6 text-center transition-colors hover:bg-fill-hover"
        >
          <Upload size={20} className="text-text-muted" aria-hidden />
          <span className="text-[12.5px] font-semibold text-text-high">
            {file ? file.name : 'Choose an .xlsx or .csv file'}
          </span>
          <span className="text-[11px] text-text-muted">
            Columns like Name, Phone, Email, Company are detected automatically.
          </span>
          <input
            ref={inputRef}
            type="file"
            accept=".xlsx,.csv"
            className="hidden"
            onChange={(e) => {
              const f = e.target.files?.[0]
              if (f) void pick(f)
              e.target.value = ''
            }}
          />
        </button>
      )}

      {busy && !done && (
        <div className="mt-4 flex items-center gap-1.5 text-[12px] text-text-muted">
          <Loader2 size={13} className="animate-spin" aria-hidden /> Reading the file…
        </div>
      )}

      {/* mapping + preview counts */}
      {preview && !done && !busy && (
        <div className="mt-4 flex flex-col gap-3">
          <div className="grid grid-cols-3 gap-2">
            <Stat label="To create" value={preview.created} tone="green" />
            <Stat label="To update" value={preview.updated} tone="brand" />
            <Stat label="Skipped" value={preview.skipped} tone="muted" />
          </div>

          <div>
            <div className="mb-1.5 text-[11px] font-bold uppercase tracking-[0.04em] text-text-muted">
              Column mapping
            </div>
            <div className="flex flex-col gap-1.5">
              {preview.fields.map((f) => (
                <div key={f} className="flex items-center gap-2">
                  <span className="w-28 shrink-0 text-[12px] text-text-mid">
                    {FIELD_LABELS[f] || f}
                    {REQUIRED.has(f) && <span className="text-red-300"> *</span>}
                  </span>
                  <select
                    value={mapping[f] || ''}
                    onChange={(e) => setMapping({ ...mapping, [f]: e.target.value })}
                    className="flex-1 cursor-pointer rounded-lg border border-line-strong bg-well px-2.5 py-1.5 text-[12px] text-text-primary outline-none"
                  >
                    <option value="">— none —</option>
                    {preview.headers.filter(Boolean).map((h) => (
                      <option key={h} value={h}>
                        {h}
                      </option>
                    ))}
                  </select>
                </div>
              ))}
            </div>
            {!mappingOk && (
              <div className="mt-1.5 text-[11px] font-semibold text-amber-300">
                Name and Phone must be mapped to import.
              </div>
            )}
          </div>

          {preview.errors.length > 0 && (
            <div className="rounded-lg border border-line bg-fill-subtle p-2.5 text-[11px] text-text-muted">
              {preview.errors.length} row{preview.errors.length === 1 ? '' : 's'} will be skipped —
              e.g. row {preview.errors[0].row}: {preview.errors[0].reason}.
            </div>
          )}
        </div>
      )}

      {/* done */}
      {done && (
        <div className="mt-2 flex flex-col items-center gap-2 py-4 text-center">
          <CheckCircle size={22} style={{ color: 'var(--green-ink)' }} aria-hidden />
          <div className="text-[13px] font-bold text-text-high">Import complete</div>
          <div className="flex items-center gap-3 text-[12px] text-text-mid">
            <span className="flex items-center gap-1">
              <FileSpreadsheet size={13} aria-hidden /> {done.created} created
            </span>
            <span>· {done.updated} updated</span>
            {done.skipped > 0 && <span>· {done.skipped} skipped</span>}
          </div>
        </div>
      )}
    </Modal>
  )
}

function Stat({ label, value, tone }: { label: string; value: number; tone: 'green' | 'brand' | 'muted' }) {
  const color =
    tone === 'green' ? 'var(--green-ink)' : tone === 'brand' ? 'var(--brand-ink)' : 'var(--text-muted)'
  return (
    <div className="rounded-lg border border-line bg-card px-3 py-2 text-center">
      <div className="text-[18px] font-extrabold" style={{ color }}>
        {value}
      </div>
      <div className="text-[10.5px] font-semibold uppercase tracking-[0.04em] text-text-muted">{label}</div>
    </div>
  )
}
