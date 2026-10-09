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
import { EntityPicker } from '@/components/ui/EntityPicker'
import { UPLOAD_ACCEPT, uploadKnowledge, uploadProblem, type UploadResult } from '@/lib/aiLearning'

const fieldCls =
  'w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[12.5px] text-text-primary outline-none transition-colors placeholder:text-text-faint focus:border-brand-400'

function size(n: number): string {
  if (n < 1024) return `${n} B`
  if (n < 1048576) return `${Math.round(n / 1024)} KB`
  return `${(n / 1048576).toFixed(1)} MB`
}

/** The "Upload document" button + its modal. */
export function KnowledgeUploadButton({
  kinds,
  defaultEntity,
  onUploaded,
}: {
  kinds: { id: string; label: string }[]
  defaultEntity: string | null
  onUploaded: (r: UploadResult) => void
}) {
  const [open, setOpen] = useState(false)
  return (
    <>
      <Button variant="secondary" onClick={() => setOpen(true)}>
        <Upload size={13} aria-hidden /> Upload document
      </Button>
      <UploadModal open={open} onClose={() => setOpen(false)} kinds={kinds} defaultEntity={defaultEntity} onUploaded={onUploaded} />
    </>
  )
}

function UploadModal({
  open,
  onClose,
  kinds,
  defaultEntity,
  onUploaded,
}: {
  open: boolean
  onClose: () => void
  kinds: { id: string; label: string }[]
  defaultEntity: string | null
  onUploaded: (r: UploadResult) => void
}) {
  const [file, setFile] = useState<File | null>(null)
  const [kind, setKind] = useState('fact')
  const [title, setTitle] = useState('')
  const [entity, setEntity] = useState<string | null>(defaultEntity)
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
    setEntity(defaultEntity)
  }, [open, defaultEntity])

  const busy = progress !== null

  const pick = (f: File | null | undefined) => {
    if (!f) return
    setDone(null)
    const problem = uploadProblem(f)
    setError(problem)
    setFile(problem ? null : f)
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
    const { promise, abort } = uploadKnowledge(file, { kind, title: title.trim(), entity }, (f) => setProgress(f))
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
      title="Upload a document"
      subtitle="The AI reads the text — price lists, policies, a product manual. The file itself isn’t kept."
      labelledById="kb-upload-title"
      closeOnScrim={!busy}
      footer={
        done ? (
          <>
            <Button size="sm" variant="secondary" onClick={reset}>
              Upload another
            </Button>
            <Button size="sm" variant="primary" onClick={close} autoFocus>
              Done
            </Button>
          </>
        ) : (
          <>
            <Button size="sm" variant="secondary" onClick={close}>
              {busy ? 'Cancel upload' : 'Cancel'}
            </Button>
            <Button size="sm" variant="primary" disabled={!file || busy} onClick={() => void upload()}>
              {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Upload size={13} aria-hidden />} Upload
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
            <div className="font-semibold text-text-heading">Added “{done.item?.title || done.item?.source_file || 'document'}”</div>
            <div className="text-text-mid">
              {(done.chars ?? 0).toLocaleString()} characters of text
              {typeof done.chunks === 'number' ? `, split into ${done.chunks} piece${done.chunks === 1 ? '' : 's'} for search` : ''}. The AI
              can use it from the next message.
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
              Drag a file here, or{' '}
              <button
                type="button"
                disabled={busy}
                onClick={() => inputRef.current?.click()}
                className="font-semibold text-text-heading underline underline-offset-2"
              >
                choose one
              </button>
            </div>
            <div className="text-[11.5px] text-text-muted">PDF, text, Markdown or CSV · up to 10 MB</div>
            <input
              ref={inputRef}
              type="file"
              accept={UPLOAD_ACCEPT}
              className="sr-only"
              tabIndex={-1}
              aria-label="Choose a document"
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
                  aria-label={`Remove ${file.name}`}
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
                <span>{pct < 100 ? 'Uploading…' : 'Reading the document…'}</span>
                <span className="tabular-nums text-text-faint">{pct}%</span>
              </div>
              <div className="h-1.5 w-full rounded-full bg-fill" role="progressbar" aria-valuenow={pct} aria-valuemin={0} aria-valuemax={100} aria-label="Upload progress">
                <div className="h-1.5 rounded-full bg-primary transition-[width]" style={{ width: `${pct}%` }} />
              </div>
            </div>
          )}

          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
            <label className="block min-w-0">
              <span className="mb-1 block text-[12px] font-semibold text-text-high">Type</span>
              <Select
                value={kind}
                onChange={setKind}
                disabled={busy}
                options={(kinds.length ? kinds : [{ id: 'fact', label: 'Business fact' }]).map((k) => ({ value: k.id, label: k.label }))}
                size="md"
                ariaLabel="Type"
                className="w-full"
              />
            </label>
            <label className="block min-w-0">
              <span className="mb-1 block text-[12px] font-semibold text-text-high">Title (optional)</span>
              <input
                value={title}
                disabled={busy}
                onChange={(e) => setTitle(e.target.value)}
                placeholder={file?.name ?? 'The file name'}
                className={fieldCls}
              />
            </label>
          </div>
          <label className="block">
            <span className="mb-1 block text-[12px] font-semibold text-text-high">Applies to</span>
            <EntityPicker ariaLabel="Applies to" value={entity} onChange={setEntity} disabled={busy} globalLabel="All entities (organization-wide)" className="w-full" />
          </label>
          <p className="text-[11.5px] leading-relaxed text-text-muted">
            Scanned PDFs (pictures of pages) have no text to read — export a text PDF or paste the content into a new entry instead.
          </p>
        </div>
      )}
    </Modal>
  )
}
