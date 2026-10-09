'use client'

/**
 * Import contacts — a 4-step wizard over POST /api/contacts/import (preview
 * and commit share the same pipeline, so the numbers shown are the numbers
 * written):
 *
 *   1 Source     — a file (.csv / .xlsx / .vcf from a phone) or the caller's
 *                  own Google Contacts (GET /api/contacts/import/google)
 *   2 Map        — one Select per column: contact field, custom field, tags,
 *                  entity, or "don't import"; sample values alongside
 *   3 Duplicates — skip / fill empty fields / overwrite, with live counts;
 *                  assign an entity, add tags, find their channels afterwards
 *   4 Import     — the result, and every skipped row as a downloadable CSV
 *
 * Errors render inside the modal (it portals over the page).
 */

import { useEffect, useMemo, useRef, useState } from 'react'
import Link from 'next/link'
import { AlertTriangle, CheckCircle, Download, FileSpreadsheet, Loader2, Upload, Users } from 'lucide-react'
import { Modal } from '@/components/ui/Modal'
import { Button } from '@/components/ui/Button'
import { Select, type SelectOption } from '@/components/ui/Select'
import { EntityPicker } from '@/components/ui/EntityPicker'
import { TagListInput } from '@/components/ui/TagListInput'
import { Toggle } from '@/components/ui/Toggle'
import { useEntity } from '@/lib/EntityContext'
import { cApi, downloadIssuesCsv, errText, type ImportResult } from '@/lib/contactIdentity'

type Step = 1 | 2 | 3 | 4
type Mode = 'skip' | 'update_empty' | 'overwrite'

const STEPS: { n: Step; label: string }[] = [
  { n: 1, label: 'Source' },
  { n: 2, label: 'Map columns' },
  { n: 3, label: 'Duplicates' },
  { n: 4, label: 'Import' },
]

const MODES: { value: Mode; title: string; body: string }[] = [
  { value: 'skip', title: 'Skip them', body: 'Contacts you already have stay exactly as they are.' },
  { value: 'update_empty', title: 'Fill empty fields', body: 'Keep what you have; add details that are missing (email, company…).' },
  { value: 'overwrite', title: 'Overwrite', body: 'The file wins: its values replace the ones on file.' },
]

const REACH = new Set(['phone', 'other_phones', 'email', 'other_emails'])

interface Options {
  mode: Mode
  entity: string | null
  tags: string[]
  discover: boolean
}

export function ContactImportModal({
  open,
  onClose,
  onDone,
}: {
  open: boolean
  onClose: () => void
  onDone: () => Promise<void> | void
}) {
  const [step, setStep] = useState<Step>(1)
  const [file, setFile] = useState<File | null>(null)
  const [googleRows, setGoogleRows] = useState<Record<string, string>[] | null>(null)
  const [preview, setPreview] = useState<ImportResult | null>(null)
  const [mapping, setMapping] = useState<Record<string, string>>({})
  const [opts, setOpts] = useState<Options>({ mode: 'update_empty', entity: null, tags: [], discover: true })
  const [done, setDone] = useState<ImportResult | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [needsGoogle, setNeedsGoogle] = useState(false)
  const inputRef = useRef<HTMLInputElement>(null)
  // imported contacts land in the outlet the operator is looking at (changeable in step 3)
  const { activeEntity } = useEntity()
  useEffect(() => {
    if (open) setOpts((o) => ({ ...o, entity: activeEntity?.slug ?? null }))
  }, [open, activeEntity])

  useEffect(() => {
    if (!open) {
      setStep(1)
      setFile(null)
      setGoogleRows(null)
      setPreview(null)
      setMapping({})
      setOpts({ mode: 'update_empty', entity: null, tags: [], discover: true })
      setDone(null)
      setBusy(null)
      setError(null)
      setNeedsGoogle(false)
    }
  }, [open])

  /** One call to the importer: the file (multipart) or the Google rows (JSON). */
  const run = async (commit: boolean, map: Record<string, string> | null, o: Options, src?: File | null) => {
    const f = src === undefined ? file : src
    const qs = commit ? '' : '?preview=1'
    if (f) {
      const fd = new FormData()
      fd.append('file', f)
      if (map) fd.append('column_mapping', JSON.stringify(map))
      fd.append('mode', o.mode)
      if (o.entity) fd.append('entity', o.entity)
      if (o.tags.length) fd.append('tags', JSON.stringify(o.tags))
      fd.append('discover', o.discover ? '1' : '0')
      return cApi<ImportResult>(`contacts/import${qs}`, { method: 'POST', body: fd })
    }
    return cApi<ImportResult>(`contacts/import${qs}`, {
      method: 'POST',
      body: JSON.stringify({
        rows: googleRows ?? [],
        source: 'google',
        column_mapping: map ?? undefined,
        mode: o.mode,
        entity: o.entity ?? undefined,
        tags: o.tags,
        discover: o.discover,
      }),
    })
  }

  const pickFile = async (f: File) => {
    setError(null)
    setNeedsGoogle(false)
    if (f.size > 10 * 1024 * 1024) {
      setError('That file is larger than 10 MB — split it into smaller files.')
      return
    }
    setFile(f)
    setGoogleRows(null)
    setBusy('Reading the file…')
    try {
      const r = await run(false, null, opts, f)
      setPreview(r)
      setMapping(r.column_mapping)
      setStep(2)
    } catch (e) {
      setFile(null)
      setError(errText(e))
    } finally {
      setBusy(null)
    }
  }

  const pickGoogle = async () => {
    setError(null)
    setNeedsGoogle(false)
    setBusy('Fetching your Google contacts…')
    try {
      const r = await cApi<ImportResult>('contacts/import/google')
      setFile(null)
      setGoogleRows(r.rows ?? [])
      setPreview(r)
      setMapping(r.column_mapping)
      if (r.warning) setError(r.warning)
      setStep(2)
    } catch (e) {
      const status = (e as { status?: number }).status
      if (status === 409) setNeedsGoogle(true)
      else setError(errText(e))
    } finally {
      setBusy(null)
    }
  }

  const refresh = async (map: Record<string, string>, o: Options, then?: Step) => {
    setBusy('Checking duplicates…')
    setError(null)
    try {
      const r = await run(false, map, o)
      setPreview(r)
      if (then) setStep(then)
    } catch (e) {
      setError(errText(e))
    } finally {
      setBusy(null)
    }
  }

  const commit = async () => {
    setBusy('Importing…')
    setError(null)
    setStep(4)
    try {
      const r = await run(true, mapping, opts)
      setDone(r)
      await onDone()
    } catch (e) {
      setError(errText(e))
      setStep(3)
    } finally {
      setBusy(null)
    }
  }

  const targetOptions: SelectOption[] = useMemo(() => {
    const t = preview?.targets ?? []
    return [
      { value: '', label: "Don't import" },
      ...t.map((x) => ({ value: x.value, label: x.label, group: x.group })),
    ]
  }, [preview])

  const mapped = Object.values(mapping)
  const reachable = mapped.some((t) => REACH.has(t))
  const named = mapped.some((t) => t === 'name' || t === 'first_name' || t === 'last_name')
  const sourceName = file ? file.name : googleRows ? 'Google Contacts' : ''

  const setOpt = (patch: Partial<Options>, recount = false) => {
    const next = { ...opts, ...patch }
    setOpts(next)
    if (recount) void refresh(mapping, next)
  }

  // ── footer ────────────────────────────────────────────────────────────────
  const footer = (
    <>
      {step > 1 && step < 4 && (
        <Button variant="ghost" size="sm" onClick={() => setStep((step - 1) as Step)} disabled={!!busy}>
          Back
        </Button>
      )}
      <span className="flex-1" />
      <Button variant="secondary" size="sm" onClick={onClose} disabled={busy === 'Importing…'}>
        {done ? 'Close' : 'Cancel'}
      </Button>
      {step === 2 && (
        <Button size="sm" onClick={() => refresh(mapping, opts, 3)} disabled={!!busy || !reachable}>
          {busy && <Loader2 size={13} className="animate-spin" aria-hidden />}
          Next
        </Button>
      )}
      {step === 3 && preview && (
        <Button size="sm" onClick={commit} disabled={!!busy || preview.created + preview.updated === 0}>
          {busy && <Loader2 size={13} className="animate-spin" aria-hidden />}
          Import {preview.created + preview.updated} {preview.created + preview.updated === 1 ? 'contact' : 'contacts'}
        </Button>
      )}
    </>
  )

  return (
    <Modal open={open} onClose={onClose} title="Import contacts" subtitle={sourceName || 'Bring in a contact book you already have.'} size="lg" footer={footer}>
      {error && (
        <div role="alert" className="mb-3 flex items-start gap-2 rounded-lg border border-red/30 bg-red/10 px-3 py-2 text-[12px] font-semibold text-red-300">
          <AlertTriangle size={13} className="mt-0.5 shrink-0" aria-hidden />
          <span className="min-w-0">{error}</span>
        </div>
      )}

      {/* stepper */}
      <ol className="mb-4 flex flex-wrap items-center gap-x-1 gap-y-1 text-[11.5px]" aria-label="Import steps">
        {STEPS.map((s, i) => (
          <li key={s.n} className="flex items-center gap-1">
            <span
              aria-current={s.n === step ? 'step' : undefined}
              className={`inline-flex items-center gap-1.5 rounded-full px-2 py-0.5 font-semibold ${
                s.n === step
                  ? 'bg-primary text-on-primary'
                  : s.n < step
                    ? 'bg-fill text-text-high'
                    : 'text-text-muted'
              }`}
            >
              <span className="tabular-nums">{s.n}</span> {s.label}
            </span>
            {i < STEPS.length - 1 && <span className="text-text-faint" aria-hidden>›</span>}
          </li>
        ))}
      </ol>

      {/* 1 — source */}
      {step === 1 && (
        <div className="flex flex-col gap-3">
          <button
            type="button"
            onClick={() => inputRef.current?.click()}
            onDragOver={(e) => e.preventDefault()}
            onDrop={(e) => {
              e.preventDefault()
              const f = e.dataTransfer.files?.[0]
              if (f) void pickFile(f)
            }}
            disabled={!!busy}
            className="flex w-full cursor-pointer flex-col items-center gap-2 rounded-xl border border-dashed border-line-strong bg-fill-subtle px-4 py-7 text-center transition-colors hover:bg-fill-hover disabled:cursor-wait"
          >
            <Upload size={20} className="text-text-muted" aria-hidden />
            <span className="text-[13px] font-semibold text-text-high">Upload a file</span>
            <span className="max-w-[420px] text-[11.5px] leading-relaxed text-text-muted">
              Excel (.xlsx), CSV, or a vCard (.vcf) exported from an iPhone or Android phone. Up to 20,000 contacts / 10 MB.
            </span>
            <input
              ref={inputRef}
              type="file"
              accept=".xlsx,.csv,.tsv,.txt,.vcf,.vcard,text/vcard,text/csv"
              className="hidden"
              onChange={(e) => {
                const f = e.target.files?.[0]
                if (f) void pickFile(f)
                e.target.value = ''
              }}
            />
          </button>
          <button
            type="button"
            onClick={pickGoogle}
            disabled={!!busy}
            className="flex w-full cursor-pointer items-center gap-3 rounded-xl border border-line bg-card px-4 py-3 text-left transition-colors hover:bg-fill-hover disabled:cursor-wait"
          >
            <span className="grid h-9 w-9 shrink-0 place-items-center rounded-lg bg-fill text-text-high">
              <Users size={16} aria-hidden />
            </span>
            <span className="min-w-0">
              <span className="block text-[13px] font-semibold text-text-high">Google Contacts</span>
              <span className="block text-[11.5px] text-text-muted">Your own Google address book (read-only).</span>
            </span>
          </button>
          {needsGoogle && (
            <div role="alert" className="rounded-lg border border-amber/30 bg-amber/10 px-3 py-2 text-[12px] text-amber-300">
              Connect Google first — open{' '}
              <Link href="/integrations" className="font-semibold underline underline-offset-2">
                Integrations → Google Contacts
              </Link>
              , allow access to your contacts, then come back here.
            </div>
          )}
          {busy && (
            <div className="flex items-center gap-1.5 text-[12px] text-text-muted">
              <Loader2 size={13} className="animate-spin" aria-hidden /> {busy}
            </div>
          )}
        </div>
      )}

      {/* 2 — mapping */}
      {step === 2 && preview && (
        <div className="flex flex-col gap-3">
          <p className="text-[12px] text-text-mid">
            {preview.total.toLocaleString()} row{preview.total === 1 ? '' : 's'} found. Check what each column holds — we
            guessed from the headings.
          </p>
          <div className="flex flex-col divide-y divide-line rounded-xl border border-line">
            {preview.columns.map((c) => (
              <div key={c.header} className="flex flex-col gap-1.5 px-3 py-2.5 sm:flex-row sm:items-center sm:gap-3">
                <div className="min-w-0 sm:w-[46%]">
                  <div className="truncate text-[12.5px] font-semibold text-text-high">{c.header}</div>
                  <div className="truncate text-[11px] text-text-muted">
                    {c.samples.length > 0 ? c.samples.join(' · ') : 'empty'}
                  </div>
                </div>
                <Select
                  value={mapping[c.header] ?? ''}
                  onChange={(v) => setMapping({ ...mapping, [c.header]: v })}
                  options={targetOptions}
                  ariaLabel={`What the column ${c.header} holds`}
                  className="min-w-0 flex-1"
                  size="md"
                />
              </div>
            ))}
          </div>
          {!reachable && (
            <div className="text-[11.5px] font-semibold text-amber-300">
              Map at least one phone or email column — a contact needs a way to be reached.
            </div>
          )}
          {reachable && !named && (
            <div className="text-[11.5px] text-text-muted">No name column — contacts will be named after their phone or email.</div>
          )}
        </div>
      )}

      {/* 3 — duplicates + options */}
      {step === 3 && preview && (
        <div className="flex flex-col gap-4">
          <div className="grid grid-cols-2 gap-2 sm:grid-cols-4">
            <Stat label="New" value={preview.created} tone="green" />
            <Stat label="Already here" value={preview.duplicates} tone="brand" />
            <Stat label="Merged in file" value={preview.merged} tone="muted" />
            <Stat label="Can't import" value={preview.errors_total} tone={preview.errors_total ? 'red' : 'muted'} />
          </div>

          <div>
            <div className="eyebrow mb-1.5">When a contact already exists</div>
            <div role="radiogroup" aria-label="Duplicate handling" className="grid gap-2 sm:grid-cols-3">
              {MODES.map((m) => {
                const on = opts.mode === m.value
                return (
                  <button
                    key={m.value}
                    type="button"
                    role="radio"
                    aria-checked={on}
                    onClick={() => setOpt({ mode: m.value }, true)}
                    disabled={!!busy}
                    className={`flex flex-col gap-0.5 rounded-xl border px-3 py-2.5 text-left transition-colors ${
                      on ? 'border-text-primary bg-[var(--nav-active-bg)]' : 'border-line bg-card hover:bg-fill-hover'
                    }`}
                  >
                    <span className="text-[12.5px] font-semibold text-text-high">{m.title}</span>
                    <span className="text-[11px] leading-snug text-text-muted">{m.body}</span>
                  </button>
                )
              })}
            </div>
            <p className="mt-1.5 text-[11.5px] text-text-muted">
              Matched by phone (last 8 digits) or email.{' '}
              {opts.mode === 'skip'
                ? `${preview.skipped} will be skipped.`
                : `${preview.updated} will be updated${preview.unchanged ? `, ${preview.unchanged} already match` : ''}.`}
            </p>
          </div>

          <div className="grid gap-3 sm:grid-cols-2">
            <label className="flex flex-col gap-1">
              <span className="text-[11.5px] font-semibold text-text-mid">Add every contact to</span>
              <EntityPicker
                value={opts.entity}
                onChange={(v) => setOpt({ entity: v })}
                globalLabel="No entity"
                ariaLabel="Entity for the imported contacts"
              />
            </label>
            <div className="flex flex-col gap-1">
              <span className="text-[11.5px] font-semibold text-text-mid">Tag every contact</span>
              <TagListInput value={opts.tags} onChange={(t) => setOpt({ tags: t })} placeholder="e.g. trade-fair-2026" />
            </div>
          </div>
          <div className="flex items-start justify-between gap-3 rounded-xl border border-line px-3 py-2.5">
            <div>
              <div className="text-[12.5px] font-semibold text-text-high">Find their channels afterwards</div>
              <div className="text-[11.5px] text-text-muted">
                Register WhatsApp, voice and email for each contact; WhatsApp numbers are checked in the background through a
                connected QR number.
              </div>
            </div>
            <Toggle on={opts.discover} onChange={(v) => setOpt({ discover: v })} label="Find their channels afterwards" />
          </div>

          {preview.created + preview.updated === 0 && (
            <p className="text-[12px] font-semibold text-text-mid">
              Nothing to import — every row is already in your contacts with these details.
            </p>
          )}
          <Issues result={preview} />
          {busy && (
            <div className="flex items-center gap-1.5 text-[12px] text-text-muted">
              <Loader2 size={13} className="animate-spin" aria-hidden /> {busy}
            </div>
          )}
        </div>
      )}

      {/* 4 — import */}
      {step === 4 && (
        <div className="flex flex-col items-center gap-3 py-4 text-center">
          {!done ? (
            <>
              <div className="h-1.5 w-full max-w-[360px] overflow-hidden rounded-full bg-fill">
                <div className="h-full w-1/3 animate-pulse rounded-full bg-primary" />
              </div>
              <div className="flex items-center gap-1.5 text-[12.5px] text-text-mid">
                <Loader2 size={14} className="animate-spin" aria-hidden /> Importing {preview?.total.toLocaleString()} rows…
              </div>
            </>
          ) : (
            <>
              <CheckCircle size={24} style={{ color: 'var(--green-ink)' }} aria-hidden />
              <div className="display text-[18px] text-text-heading">Import complete</div>
              <div className="flex flex-wrap items-center justify-center gap-x-3 gap-y-1 text-[12.5px] text-text-mid">
                <span className="flex items-center gap-1">
                  <FileSpreadsheet size={13} aria-hidden /> {done.created} created
                </span>
                <span>{done.updated} updated</span>
                {done.skipped > 0 && <span>{done.skipped} skipped</span>}
                {done.merged > 0 && <span>{done.merged} merged</span>}
                {done.errors_total > 0 && <span className="text-red-300">{done.errors_total} not imported</span>}
              </div>
              {done.queued_checks > 0 && (
                <p className="text-[11.5px] text-text-muted">
                  {done.queued_checks} WhatsApp number{done.queued_checks === 1 ? '' : 's'} queued for a check.
                </p>
              )}
              <div className="w-full text-left">
                <Issues result={done} />
              </div>
            </>
          )}
        </div>
      )}
    </Modal>
  )
}

function Issues({ result }: { result: ImportResult }) {
  if (result.errors_total === 0 && result.warnings_total === 0) return null
  const first = result.errors[0] ?? result.warnings[0]
  return (
    <div className="flex flex-col gap-2 rounded-lg border border-line bg-fill-subtle p-3 text-[11.5px] text-text-muted">
      <div>
        {result.errors_total > 0 && (
          <>
            {result.errors_total} row{result.errors_total === 1 ? '' : 's'} can&apos;t be imported
            {result.warnings_total > 0 ? ' · ' : ''}
          </>
        )}
        {result.warnings_total > 0 && (
          <>
            {result.warnings_total} warning{result.warnings_total === 1 ? '' : 's'}
          </>
        )}
        {first && (
          <>
            {' '}— e.g. row {first.row}: {first.reason}.
          </>
        )}
      </div>
      <div className="flex flex-wrap gap-2">
        {result.errors.length > 0 && (
          <Button variant="secondary" size="sm" onClick={() => downloadIssuesCsv(result.errors, 'import-errors.csv')}>
            <Download size={12} aria-hidden /> Errors (.csv)
          </Button>
        )}
        {result.warnings.length > 0 && (
          <Button variant="ghost" size="sm" onClick={() => downloadIssuesCsv(result.warnings, 'import-warnings.csv')}>
            <Download size={12} aria-hidden /> Warnings (.csv)
          </Button>
        )}
      </div>
    </div>
  )
}

function Stat({ label, value, tone }: { label: string; value: number; tone: 'green' | 'brand' | 'muted' | 'red' }) {
  const color =
    tone === 'green'
      ? 'var(--green-ink)'
      : tone === 'brand'
        ? 'var(--brand-ink)'
        : tone === 'red'
          ? 'var(--red-ink)'
          : 'var(--text-muted)'
  return (
    <div className="rounded-lg border border-line bg-card px-3 py-2 text-center">
      <div className="text-[18px] font-extrabold tabular-nums" style={{ color }}>
        {value.toLocaleString()}
      </div>
      <div className="text-[10.5px] font-semibold uppercase tracking-[0.04em] text-text-muted">{label}</div>
    </div>
  )
}
