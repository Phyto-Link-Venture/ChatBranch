'use client'

/**
 * Knowledge Base → "Upload document". Drop (or pick) a PDF, text, Markdown or
 * CSV file up to 10 MB; the server extracts its text into ONE knowledge item
 * (the file itself is never stored) and splits it into pieces for search.
 * Upload progress, the server's verdict ("no text found — it may be a scanned
 * image") and the result all render INSIDE the modal.
 */

import { useEffect, useRef, useState } from 'react'
import { CheckCircle, FileText, Loader2, Upload, X } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Modal } from '@/components/ui/Modal'
import { Select } from '@/components/ui/Select'
import { AvailabilityPicker } from '@/components/ui/AvailabilityPicker'
import { UPLOAD_ACCEPT, uploadKnowledge, uploadProblem, type UploadResult } from '@/lib/aiLearning'
import { Trans, useFormat, useT } from '@/i18n'

const fieldCls =
  'w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[12.5px] text-text-primary outline-none transition-colors placeholder:text-text-faint focus:border-brand-400'

/** The "Upload document" button + its modal. */
export function KnowledgeUploadButton({
  kinds,
  defaultEntities,
  onUploaded,
}: {
  kinds: { id: string; label: string }[]
  /** "Available to" a new upload starts with ([] = everyone). */
  defaultEntities: string[]
  onUploaded: (r: UploadResult) => void
}) {
  const t = useT()
  const [open, setOpen] = useState(false)
  return (
    <>
      <Button variant="secondary" onClick={() => setOpen(true)}>
        <Upload size={13} aria-hidden /> {t('ai.upload.button')}
      </Button>
      <UploadModal open={open} onClose={() => setOpen(false)} kinds={kinds} defaultEntities={defaultEntities} onUploaded={onUploaded} />
    </>
  )
}

function UploadModal({
  open,
  onClose,
  kinds,
  defaultEntities,
  onUploaded,
}: {
  open: boolean
  onClose: () => void
  kinds: { id: string; label: string }[]
  defaultEntities: string[]
  onUploaded: (r: UploadResult) => void
}) {
  const t = useT()
  const f = useFormat()
  const mb = (n: number) => f.number(n, { minimumFractionDigits: 1, maximumFractionDigits: 1 })
  const size = (n: number) =>
    n < 1024
      ? t('ai.units.bytes', { n })
      : n < 1048576
        ? t('ai.units.kb', { n: Math.round(n / 1024) })
        : t('ai.units.mb', { n: mb(n / 1048576) })
  const [file, setFile] = useState<File | null>(null)
  const [kind, setKind] = useState('fact')
  const [title, setTitle] = useState('')
  const [entities, setEntities] = useState<string[]>(defaultEntities)
  const [dragging, setDragging] = useState(false)
  const [progress, setProgress] = useState<number | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [done, setDone] = useState<UploadResult | null>(null)
  const abortRef = useRef<(() => void) | null>(null)
  const inputRef = useRef<HTMLInputElement>(null)

  const reset = () => {
    setFile(null)
    setTitle('')
    setProgress(null)
    setError(null)
    setDone(null)
    setDragging(false)
  }

  useEffect(() => {
    if (!open) return
    setFile(null)
    setTitle('')
    setProgress(null)
    setError(null)
    setDone(null)
    setDragging(false)
    setKind('fact')
    setEntities(defaultEntities)
  }, [open, defaultEntities])

  const busy = progress !== null

  const pick = (picked: File | null | undefined) => {
    if (!picked) return
    setDone(null)
    const problem = uploadProblem(picked, t, mb)
    setError(problem)
    setFile(problem ? null : picked)
  }

  const close = () => {
    abortRef.current?.()
    abortRef.current = null
    onClose()
  }

  const upload = async () => {
    if (!file || busy) return
    setError(null)
    setProgress(0)
    const { promise, abort } = uploadKnowledge(file, { kind, title: title.trim(), entities }, t, (p) => setProgress(p))
    abortRef.current = abort
    try {
      const r = await promise
      setDone(r)
      setFile(null)
      setTitle('')
      onUploaded(r)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      abortRef.current = null
      setProgress(null)
    }
  }

  const pct = progress == null ? 0 : Math.round(progress * 100)

  return (
    <Modal
      open={open}
      onClose={close}
      title={t('ai.upload.title')}
      subtitle={t('ai.upload.subtitle')}
      labelledById="kb-upload-title"
      closeOnScrim={!busy}
      footer={
        done ? (
          <>
            <Button size="sm" variant="secondary" onClick={reset}>
              {t('ai.upload.uploadAnother')}
            </Button>
            <Button size="sm" variant="primary" onClick={close} autoFocus>
              {t('common.actions.done')}
            </Button>
          </>
        ) : (
          <>
            <Button size="sm" variant="secondary" onClick={close}>
              {busy ? t('ai.upload.cancelUpload') : t('common.actions.cancel')}
            </Button>
            <Button size="sm" variant="primary" disabled={!file || busy} onClick={() => void upload()}>
              {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Upload size={13} aria-hidden />}{' '}
              {t('common.actions.upload')}
            </Button>
          </>
        )
      }
    >
      {error && (
        <div role="alert" className="mb-3 rounded-lg border border-red/30 bg-red/10 px-3 py-2 text-[12.5px] text-[color:var(--red-ink)]">
          {error}
        </div>
      )}

      {done ? (
        <div role="status" className="flex items-start gap-3 rounded-xl border border-line bg-well px-4 py-4">
          <CheckCircle size={18} className="mt-0.5 shrink-0 text-[color:var(--green-ink)]" aria-hidden />
          <div className="min-w-0 text-[13px] leading-relaxed text-text-high">
            <div className="font-semibold text-text-heading">
              {t('ai.upload.added', {
                title: done.item?.title || done.item?.source_file || t('ai.upload.documentFallback'),
              })}
            </div>
            <div className="text-text-mid">
              {typeof done.chunks === 'number'
                ? t('ai.upload.charsSplit', { chars: done.chars ?? 0, count: done.chunks })
                : t('ai.upload.charsOnly', { chars: done.chars ?? 0 })}
            </div>
          </div>
        </div>
      ) : (
        <div className="flex flex-col gap-3.5">
          <div
            onDragOver={(e) => {
              e.preventDefault()
              if (!busy) setDragging(true)
            }}
            onDragLeave={() => setDragging(false)}
            onDrop={(e) => {
              e.preventDefault()
              setDragging(false)
              if (!busy) pick(e.dataTransfer.files?.[0])
            }}
            className={`flex flex-col items-center justify-center gap-2 rounded-xl border-2 border-dashed px-4 py-7 text-center transition-colors ${
              dragging ? 'border-brand-400 bg-brand-400/10' : 'border-line-strong bg-fill-subtle'
            }`}
          >
            <Upload size={22} className="text-text-muted" aria-hidden />
            <div className="text-[13px] text-text-high">
              <Trans
                k="ai.upload.dragHere"
                values={{
                  choose: (
                    <button
                      type="button"
                      disabled={busy}
                      onClick={() => inputRef.current?.click()}
                      className="font-semibold text-text-heading underline underline-offset-2"
                    >
                      {t('ai.upload.chooseOne')}
                    </button>
                  ),
                }}
              />
            </div>
            <div className="text-[11.5px] text-text-muted">{t('ai.upload.formats')}</div>
            <input
              ref={inputRef}
              type="file"
              accept={UPLOAD_ACCEPT}
              className="sr-only"
              tabIndex={-1}
              aria-label={t('ai.upload.chooseDoc')}
              onChange={(e) => {
                pick(e.target.files?.[0])
                e.target.value = ''
              }}
            />
          </div>

          {file && (
            <div className="flex items-center gap-2.5 rounded-lg border border-line bg-card px-3 py-2">
              <FileText size={16} className="shrink-0 text-text-muted" aria-hidden />
              <span className="min-w-0 flex-1 truncate text-[12.5px] font-semibold text-text-high">{file.name}</span>
              <span className="shrink-0 text-[11.5px] tabular-nums text-text-muted">{size(file.size)}</span>
              {!busy && (
                <button
                  type="button"
                  onClick={() => setFile(null)}
                  aria-label={t('ai.upload.remove', { name: file.name })}
                  className="grid h-6 w-6 shrink-0 place-items-center rounded-md text-text-muted hover:bg-fill-hover hover:text-text-high"
                >
                  <X size={13} aria-hidden />
                </button>
              )}
            </div>
          )}

          {busy && (
            <div>
              <div className="mb-1 flex justify-between text-[12px] text-text-mid">
                <span>{pct < 100 ? t('ai.upload.uploading') : t('ai.upload.reading')}</span>
                <span className="tabular-nums text-text-faint">{f.percent(pct / 100)}</span>
              </div>
              <div
                className="h-1.5 w-full rounded-full bg-fill"
                role="progressbar"
                aria-valuenow={pct}
                aria-valuemin={0}
                aria-valuemax={100}
                aria-label={t('ai.upload.progressAria')}
              >
                <div className="h-1.5 rounded-full bg-primary transition-[width]" style={{ width: `${pct}%` }} />
              </div>
            </div>
          )}

          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
            <label className="block min-w-0">
              <span className="mb-1 block text-[12px] font-semibold text-text-high">{t('common.fields.type')}</span>
              <Select
                value={kind}
                onChange={setKind}
                disabled={busy}
                options={(kinds.length ? kinds : [{ id: 'fact', label: t('ai.knowledge.kinds.fact') }]).map((k) => ({
                  value: k.id,
                  label: k.label,
                }))}
                size="md"
                ariaLabel={t('common.fields.type')}
                className="w-full"
              />
            </label>
            <label className="block min-w-0">
              <span className="mb-1 block text-[12px] font-semibold text-text-high">{t('ai.common.titleOptional')}</span>
              <input
                value={title}
                disabled={busy}
                onChange={(e) => setTitle(e.target.value)}
                placeholder={file?.name ?? t('ai.upload.fileName')}
                className={fieldCls}
              />
            </label>
          </div>
          <AvailabilityPicker value={entities} onChange={setEntities} disabled={busy} />

          <p className="text-[11.5px] leading-relaxed text-text-muted">{t('ai.upload.scanned')}</p>
        </div>
      )}
    </Modal>
  )
}
