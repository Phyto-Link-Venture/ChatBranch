'use client'

/**
 * Import contacts — a 4-step wizard over POST /api/contacts/import (preview
 * and commit share the same pipeline, so the numbers shown are the numbers
 * written):
 *
 *   1 Source     — a file (.csv / .xlsx / .vcf from a phone), the contacts on
 *                  THIS phone (the app's contact picker — DeviceContactsPicker,
 *                  hidden where the plugin or the browser picker is absent), or
 *                  the caller's own Google Contacts (GET /api/contacts/import/google)
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
import { AlertTriangle, CheckCircle, Download, FileSpreadsheet, Loader2, Smartphone, Upload, Users } from 'lucide-react'
import { Modal } from '@/components/ui/Modal'
import { Button } from '@/components/ui/Button'
import { Select, type SelectOption } from '@/components/ui/Select'
import { EntityPicker } from '@/components/ui/EntityPicker'
import { TagListInput } from '@/components/ui/TagListInput'
import { Advanced } from '@/components/ui/Advanced'
import { Toggle } from '@/components/ui/Toggle'
import { useEntity } from '@/lib/EntityContext'
import { cApi, downloadIssuesCsv, errText, type ImportResult } from '@/lib/contactIdentity'
import { DEVICE_IMPORT_SOURCE, deviceContactsSupported } from '@/lib/deviceContacts'
import { DeviceContactsPicker } from '@/components/contacts/DeviceContactsPicker'
import { Trans, useFormat, useT, type MessageKey } from '@/i18n'

type Step = 1 | 2 | 3 | 4
type Mode = 'skip' | 'update_empty' | 'overwrite'

const STEPS: { n: Step; label: MessageKey }[] = [
  { n: 1, label: 'contacts.import.steps.source' },
  { n: 2, label: 'contacts.import.steps.map' },
  { n: 3, label: 'contacts.import.steps.duplicates' },
  { n: 4, label: 'contacts.import.steps.import' },
]

const MODES: { value: Mode; title: MessageKey; body: MessageKey }[] = [
  { value: 'skip', title: 'contacts.import.modes.skipTitle', body: 'contacts.import.modes.skipBody' },
  { value: 'update_empty', title: 'contacts.import.modes.updateEmptyTitle', body: 'contacts.import.modes.updateEmptyBody' },
  { value: 'overwrite', title: 'contacts.import.modes.overwriteTitle', body: 'contacts.import.modes.overwriteBody' },
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
  const t = useT()
  const [step, setStep] = useState<Step>(1)
  const [file, setFile] = useState<File | null>(null)
  /**
   * Rows that reach the importer as JSON rather than as an upload: the caller's
   * Google Contacts, or the contacts they ticked on this phone. `source` is only
   * a label the server echoes back, but it is what tells the two apart here.
   */
  const [json, setJson] = useState<{ source: string; rows: Record<string, string>[] } | null>(null)
  /** Step 1 has handed over to the phone's contact picker. */
  const [deviceOpen, setDeviceOpen] = useState(false)
  /**
   * Is there an address book on this device at all? Checked in an effect, not
   * during render: the answer needs `window`, and a browser must not flash a
   * button it cannot honour.
   */
  const [deviceReady, setDeviceReady] = useState(false)
  useEffect(() => {
    setDeviceReady(deviceContactsSupported())
  }, [])
  const [preview, setPreview] = useState<ImportResult | null>(null)
  const [mapping, setMapping] = useState<Record<string, string>>({})
  const [opts, setOpts] = useState<Options>({ mode: 'update_empty', entity: null, tags: [], discover: true })
  const [done, setDone] = useState<ImportResult | null>(null)
  /** What is running right now (a translation key), or null. */
  const [busy, setBusy] = useState<MessageKey | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [needsGoogle, setNeedsGoogle] = useState(false)
  const inputRef = useRef<HTMLInputElement>(null)
  // imported contacts land in the outlet the operator is looking at (changeable in step 3)
  const { activeEntity } = useEntity()
  useEffect(() => {
    if (open) setOpts((o) => ({ ...o, entity: activeEntity?.slug ?? null }))
  }, [open, activeEntity])
  // Entity (default: the top bar's outlet) and tags (default none) sit under
  // Advanced in step 3 — opened when either differs from its default.
  const advancedCount = (opts.entity !== (activeEntity?.slug ?? null) ? 1 : 0) + (opts.tags.length > 0 ? 1 : 0)

  useEffect(() => {
    if (!open) {
      setStep(1)
      setFile(null)
      setJson(null)
      setDeviceOpen(false)
      setPreview(null)
      setMapping({})
      setOpts({ mode: 'update_empty', entity: null, tags: [], discover: true })
      setDone(null)
      setBusy(null)
      setError(null)
      setNeedsGoogle(false)
    }
  }, [open])

  /**
   * One call to the importer: the file (multipart), or a batch of rows (JSON)
   * from Google Contacts / this phone. `src` and `batch` let a freshly picked
   * source be used before its state has settled.
   */
  const run = async (
    commit: boolean,
    map: Record<string, string> | null,
    o: Options,
    src?: File | null,
    batch?: { source: string; rows: Record<string, string>[] },
  ) => {
    const upload = src === undefined ? file : src
    const qs = commit ? '' : '?preview=1'
    if (upload) {
      const fd = new FormData()
      fd.append('file', upload)
      if (map) fd.append('column_mapping', JSON.stringify(map))
      fd.append('mode', o.mode)
      if (o.entity) fd.append('entity', o.entity)
      if (o.tags.length) fd.append('tags', JSON.stringify(o.tags))
      fd.append('discover', o.discover ? '1' : '0')
      return cApi<ImportResult>(`contacts/import${qs}`, { method: 'POST', body: fd })
    }
    const rows = batch ?? json
    return cApi<ImportResult>(`contacts/import${qs}`, {
      method: 'POST',
      body: JSON.stringify({
        rows: rows?.rows ?? [],
        source: rows?.source ?? 'json',
        column_mapping: map ?? undefined,
        mode: o.mode,
        entity: o.entity ?? undefined,
        tags: o.tags,
        discover: o.discover,
      }),
    })
  }

  const pickFile = async (picked: File) => {
    setError(null)
    setNeedsGoogle(false)
    if (picked.size > 10 * 1024 * 1024) {
      setError(t('contacts.import.tooLarge'))
      return
    }
    setFile(picked)
    setJson(null)
    setBusy('contacts.import.reading')
    try {
      const r = await run(false, null, opts, picked)
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
    setBusy('contacts.import.fetchingGoogle')
    try {
      const r = await cApi<ImportResult>('contacts/import/google')
      setFile(null)
      setJson({ source: 'google', rows: r.rows ?? [] })
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

  /**
   * The contacts ticked in the phone's picker. Only these rows ever leave the
   * device, and they join the same pipeline as a file: preview first (nothing is
   * written), then the mapping and duplicate steps, then the import.
   */
  const usePicked = async (rows: Record<string, string>[]) => {
    setError(null)
    setNeedsGoogle(false)
    setBusy('contacts.import.reading')
    const batch = { source: DEVICE_IMPORT_SOURCE, rows }
    try {
      const r = await run(false, null, opts, null, batch)
      setFile(null)
      setJson(batch)
      setPreview(r)
      setMapping(r.column_mapping)
      setDeviceOpen(false)
      setStep(2)
    } catch (e) {
      setError(errText(e))
    } finally {
      setBusy(null)
    }
  }

  const refresh = async (map: Record<string, string>, o: Options, then?: Step) => {
    setBusy('contacts.import.checking')
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
    setBusy('contacts.import.importing')
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
    const targets = preview?.targets ?? []
    return [
      { value: '', label: t('contacts.import.dontImport') },
      ...targets.map((x) => ({ value: x.value, label: x.label, group: x.group })),
    ]
  }, [preview, t])

  const mapped = Object.values(mapping)
  const reachable = mapped.some((x) => REACH.has(x))
  const named = mapped.some((x) => x === 'name' || x === 'first_name' || x === 'last_name')
  const sourceName = file
    ? file.name
    : json?.source === DEVICE_IMPORT_SOURCE
      ? t('contacts.device.title')
      : json
        ? t('contacts.import.googleContacts')
        : ''

  const setOpt = (patch: Partial<Options>, recount = false) => {
    const next = { ...opts, ...patch }
    setOpts(next)
    if (recount) void refresh(mapping, next)
  }

  // ── footer ────────────────────────────────────────────────────────────────
  const footer = (
    <>
      {step === 1 && deviceOpen && (
        <Button variant="ghost" size="sm" onClick={() => setDeviceOpen(false)} disabled={!!busy}>
          {t('common.actions.back')}
        </Button>
      )}
      {step > 1 && step < 4 && (
        <Button variant="ghost" size="sm" onClick={() => setStep((step - 1) as Step)} disabled={!!busy}>
          {t('common.actions.back')}
        </Button>
      )}
      <span className="flex-1" />
      <Button variant="secondary" size="sm" onClick={onClose} disabled={busy === 'contacts.import.importing'}>
        {t(done ? 'common.actions.close' : 'common.actions.cancel')}
      </Button>
      {step === 2 && (
        <Button size="sm" onClick={() => refresh(mapping, opts, 3)} disabled={!!busy || !reachable}>
          {busy && <Loader2 size={13} className="animate-spin" aria-hidden />}
          {t('common.actions.next')}
        </Button>
      )}
      {step === 3 && preview && (
        <Button size="sm" onClick={commit} disabled={!!busy || preview.created + preview.updated === 0}>
          {busy && <Loader2 size={13} className="animate-spin" aria-hidden />}
          {t('contacts.import.importN', { count: preview.created + preview.updated })}
        </Button>
      )}
    </>
  )

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={t('contacts.import.title')}
      subtitle={sourceName || t('contacts.import.subtitle')}
      size="lg"
      footer={footer}
    >
      {error && (
        <div role="alert" className="mb-3 flex items-start gap-2 rounded-lg border border-red/30 bg-red/10 px-3 py-2 text-[12px] font-semibold text-red-300">
          <AlertTriangle size={13} className="mt-0.5 shrink-0" aria-hidden />
          <span className="min-w-0">{error}</span>
        </div>
      )}

      {/* stepper */}
      <ol className="mb-4 flex flex-wrap items-center gap-x-1 gap-y-1 text-[11.5px]" aria-label={t('contacts.import.stepsAria')}>
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
              <span className="tabular-nums">{s.n}</span> {t(s.label)}
            </span>
            {i < STEPS.length - 1 && <span className="text-text-faint" aria-hidden>›</span>}
          </li>
        ))}
      </ol>

      {/* 1 — source: the phone's own contacts, in place of the source list */}
      {step === 1 && deviceOpen && <DeviceContactsPicker busy={!!busy} onUse={(rows) => void usePicked(rows)} />}

      {/* 1 — source */}
      {step === 1 && !deviceOpen && (
        <div className="flex flex-col gap-3">
          <button
            type="button"
            onClick={() => inputRef.current?.click()}
            onDragOver={(e) => e.preventDefault()}
            onDrop={(e) => {
              e.preventDefault()
              const dropped = e.dataTransfer.files?.[0]
              if (dropped) void pickFile(dropped)
            }}
            disabled={!!busy}
            className="flex w-full cursor-pointer flex-col items-center gap-2 rounded-xl border border-dashed border-line-strong bg-fill-subtle px-4 py-7 text-center transition-colors hover:bg-fill-hover disabled:cursor-wait"
          >
            <Upload size={20} className="text-text-muted" aria-hidden />
            <span className="text-[13px] font-semibold text-text-high">{t('contacts.import.uploadFile')}</span>
            <span className="max-w-[420px] text-[11.5px] leading-relaxed text-text-muted">{t('contacts.import.uploadHint')}</span>
            <input
              ref={inputRef}
              type="file"
              accept=".xlsx,.csv,.tsv,.txt,.vcf,.vcard,text/vcard,text/csv"
              className="hidden"
              onChange={(e) => {
                const chosen = e.target.files?.[0]
                if (chosen) void pickFile(chosen)
                e.target.value = ''
              }}
            />
          </button>
          {/* Only where there is an address book to read: the phone app, or
              Chrome on Android. A plain browser never sees this. */}
          {deviceReady && (
            <button
              type="button"
              onClick={() => {
                setError(null)
                setNeedsGoogle(false)
                setDeviceOpen(true)
              }}
              disabled={!!busy}
              className="flex w-full cursor-pointer items-center gap-3 rounded-xl border border-line bg-card px-4 py-3 text-left transition-colors hover:bg-fill-hover disabled:cursor-wait"
            >
              <span className="grid h-9 w-9 shrink-0 place-items-center rounded-lg bg-fill text-text-high">
                <Smartphone size={16} aria-hidden />
              </span>
              <span className="min-w-0">
                <span className="block text-[13px] font-semibold text-text-high">{t('contacts.device.title')}</span>
                <span className="block text-[11.5px] text-text-muted">{t('contacts.device.hint')}</span>
              </span>
            </button>
          )}
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
              <span className="block text-[13px] font-semibold text-text-high">{t('contacts.import.googleContacts')}</span>
              <span className="block text-[11.5px] text-text-muted">{t('contacts.import.googleHint')}</span>
            </span>
          </button>
          {needsGoogle && (
            <div role="alert" className="rounded-lg border border-amber/30 bg-amber/10 px-3 py-2 text-[12px] text-amber-300">
              <Trans
                k="contacts.import.connectGoogle"
                values={{
                  link: (
                    <Link href="/settings/connections" className="font-semibold underline underline-offset-2">
                      {t('contacts.import.googleLink')}
                    </Link>
                  ),
                }}
              />
            </div>
          )}
          {busy && (
            <div className="flex items-center gap-1.5 text-[12px] text-text-muted">
              <Loader2 size={13} className="animate-spin" aria-hidden /> {t(busy)}
            </div>
          )}
        </div>
      )}

      {/* 2 — mapping */}
      {step === 2 && preview && (
        <div className="flex flex-col gap-3">
          <p className="text-[12px] text-text-mid">
            {t('contacts.import.rowsFound', { count: preview.total })}
          </p>
          <div className="flex flex-col divide-y divide-line rounded-xl border border-line">
            {preview.columns.map((c) => (
              <div key={c.header} className="flex flex-col gap-1.5 px-3 py-2.5 sm:flex-row sm:items-center sm:gap-3">
                <div className="min-w-0 sm:w-[46%]">
                  <div className="truncate text-[12.5px] font-semibold text-text-high">{c.header}</div>
                  <div className="truncate text-[11px] text-text-muted">
                    {c.samples.length > 0 ? c.samples.join(' · ') : t('contacts.import.emptyColumn')}
                  </div>
                </div>
                <Select
                  value={mapping[c.header] ?? ''}
                  onChange={(v) => setMapping({ ...mapping, [c.header]: v })}
                  options={targetOptions}
                  ariaLabel={t('contacts.import.columnAria', { header: c.header })}
                  className="min-w-0 flex-1"
                  size="md"
                />
              </div>
            ))}
          </div>
          {!reachable && (
            <div className="text-[11.5px] font-semibold text-amber-300">
              {t('contacts.import.needReach')}
            </div>
          )}
          {reachable && !named && (
            <div className="text-[11.5px] text-text-muted">{t('contacts.import.noName')}</div>
          )}
        </div>
      )}

      {/* 3 — duplicates + options */}
      {step === 3 && preview && (
        <div className="flex flex-col gap-4">
          <div className="grid grid-cols-2 gap-2 sm:grid-cols-4">
            <Stat label={t('contacts.import.statNew')} value={preview.created} tone="green" />
            <Stat label={t('contacts.import.statAlreadyHere')} value={preview.duplicates} tone="brand" />
            <Stat label={t('contacts.import.statMerged')} value={preview.merged} tone="muted" />
            <Stat
              label={t('contacts.import.statCantImport')}
              value={preview.errors_total}
              tone={preview.errors_total ? 'red' : 'muted'}
            />
          </div>

          <div>
            <div className="eyebrow mb-1.5">{t('contacts.import.whenExists')}</div>
            <div role="radiogroup" aria-label={t('contacts.import.dupAria')} className="grid gap-2 sm:grid-cols-3">
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
                    <span className="text-[12.5px] font-semibold text-text-high">{t(m.title)}</span>
                    <span className="text-[11px] leading-snug text-text-muted">{t(m.body)}</span>
                  </button>
                )
              })}
            </div>
            <p className="mt-1.5 text-[11.5px] text-text-muted">
              {t('contacts.import.matchedBy')}{' '}
              {opts.mode === 'skip'
                ? t('contacts.import.willSkip', { count: preview.skipped })
                : preview.unchanged
                  ? t('contacts.import.willUpdateMatch', { count: preview.updated, unchanged: preview.unchanged })
                  : t('contacts.import.willUpdate', { count: preview.updated })}
            </p>
          </div>

          <div className="flex items-start justify-between gap-3 rounded-xl border border-line px-3 py-2.5">
            <div>
              <div className="text-[12.5px] font-semibold text-text-high">{t('contacts.import.findChannels')}</div>
              <div className="text-[11.5px] text-text-muted">{t('contacts.import.findChannelsHint')}</div>
            </div>
            <Toggle on={opts.discover} onChange={(v) => setOpt({ discover: v })} label={t('contacts.import.findChannels')} />
          </div>
          <Advanced count={advancedCount} forceOpen={advancedCount > 0}>
            <div className="grid gap-3 sm:grid-cols-2">
              <label className="flex flex-col gap-1">
                <span className="text-[11.5px] font-semibold text-text-mid">{t('contacts.import.addTo')}</span>
                <EntityPicker
                  value={opts.entity}
                  onChange={(v) => setOpt({ entity: v })}
                  globalLabel={t('contacts.import.noEntity')}
                  ariaLabel={t('contacts.import.entityAria')}
                />
              </label>
              <div className="flex flex-col gap-1">
                <span className="text-[11.5px] font-semibold text-text-mid">{t('contacts.import.tagEvery')}</span>
                <TagListInput
                  value={opts.tags}
                  onChange={(tags) => setOpt({ tags })}
                  placeholder={t('contacts.import.tagPlaceholder')}
                />
              </div>
            </div>
          </Advanced>

          {preview.created + preview.updated === 0 && (
            <p className="text-[12px] font-semibold text-text-mid">
              {t('contacts.import.nothingToImport')}
            </p>
          )}
          <Issues result={preview} />
          {busy && (
            <div className="flex items-center gap-1.5 text-[12px] text-text-muted">
              <Loader2 size={13} className="animate-spin" aria-hidden /> {t(busy)}
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
                <Loader2 size={14} className="animate-spin" aria-hidden />{' '}
                {t('contacts.import.importingRows', { count: preview?.total ?? 0 })}
              </div>
            </>
          ) : (
            <>
              <CheckCircle size={24} style={{ color: 'var(--green-ink)' }} aria-hidden />
              <div className="display text-[18px] text-text-heading">{t('contacts.import.complete')}</div>
              <div className="flex flex-wrap items-center justify-center gap-x-3 gap-y-1 text-[12.5px] text-text-mid">
                <span className="flex items-center gap-1">
                  <FileSpreadsheet size={13} aria-hidden /> {t('contacts.import.created', { count: done.created })}
                </span>
                <span>{t('contacts.import.updated', { count: done.updated })}</span>
                {done.skipped > 0 && <span>{t('contacts.import.skipped', { count: done.skipped })}</span>}
                {done.merged > 0 && <span>{t('contacts.import.merged', { count: done.merged })}</span>}
                {done.errors_total > 0 && (
                  <span className="text-red-300">{t('contacts.import.notImported', { count: done.errors_total })}</span>
                )}
              </div>
              {done.queued_checks > 0 && (
                <p className="text-[11.5px] text-text-muted">{t('contacts.import.queuedChecks', { count: done.queued_checks })}</p>
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
  const t = useT()
  if (result.errors_total === 0 && result.warnings_total === 0) return null
  const first = result.errors[0] ?? result.warnings[0]
  return (
    <div className="flex flex-col gap-2 rounded-lg border border-line bg-fill-subtle p-3 text-[11.5px] text-text-muted">
      <div>
        {result.errors_total > 0 && (
          <>
            {t('contacts.import.issueRows', { count: result.errors_total })}
            {result.warnings_total > 0 ? ' · ' : ''}
          </>
        )}
        {result.warnings_total > 0 && t('contacts.import.issueWarnings', { count: result.warnings_total })}
        {first && <> {t('contacts.import.issueExample', { row: first.row, reason: first.reason })}</>}
      </div>
      <div className="flex flex-wrap gap-2">
        {result.errors.length > 0 && (
          <Button variant="secondary" size="sm" onClick={() => downloadIssuesCsv(result.errors, 'import-errors.csv')}>
            <Download size={12} aria-hidden /> {t('contacts.import.errorsCsv')}
          </Button>
        )}
        {result.warnings.length > 0 && (
          <Button variant="ghost" size="sm" onClick={() => downloadIssuesCsv(result.warnings, 'import-warnings.csv')}>
            <Download size={12} aria-hidden /> {t('contacts.import.warningsCsv')}
          </Button>
        )}
      </div>
    </div>
  )
}

function Stat({ label, value, tone }: { label: string; value: number; tone: 'green' | 'brand' | 'muted' | 'red' }) {
  const f = useFormat()
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
        {f.number(value)}
      </div>
      <div className="text-[10.5px] font-semibold uppercase tracking-[0.04em] text-text-muted">{label}</div>
    </div>
  )
}
