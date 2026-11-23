'use client'

import { useCallback, useEffect, useRef, useState } from 'react'
import { AlertCircle, Check, Copy, Loader2, Trash2, Upload } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Card } from '@/components/ui/Card'
import { PageHeader } from '@/components/ui/PageHeader'
import { API } from '@/lib/apiBase'
import { authHeaders, expireSessionOn401 } from '@/lib/auth'
import { useAccess } from '@/lib/useAccess'
import { useEntityQuery } from '@/lib/EntityContext'
import { useFormat, useT } from '@/i18n'

/**
 * The media library — files uploaded once and sent by their id afterwards.
 *
 * It exists because an AI (or any API client) sending a file had to spell the whole
 * thing out as base64, which costs minutes and forces a photo to be shrunk until it
 * is barely worth sending. A link solves it for anything already online; this solves
 * the rest: drop the file in here from a laptop or a phone, and sending it is a
 * sixteen-character id.
 *
 * Nothing here is clever: upload, find, copy the id, delete. Uploads are kept 30
 * days, which the page says out loud so nobody treats it as storage.
 */

interface LibraryFile {
  file_id: string
  name: string
  mime: string
  kind: string
  size: number
  created_at: string
  expires_at: string
  last_sent_at: string | null
  url: string
}

/** A size a person reads, not a byte count. */
function size(bytes: number): string {
  if (bytes >= 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
  if (bytes >= 1024) return `${Math.round(bytes / 1024)} KB`
  return `${bytes} B`
}

const KIND_LABEL: Record<string, string> = {
  image: 'image',
  video: 'video',
  audio: 'audio',
  document: 'document',
}

export function MediaLibrary() {
  const t = useT()
  const f = useFormat()
  const { can } = useAccess()
  const entity = useEntityQuery()
  const [files, setFiles] = useState<LibraryFile[] | null>(null)
  const [search, setSearch] = useState('')
  // what the list request uses: the box, settled for 280 ms (one request per pause, not per keystroke)
  const [searchQuery, setSearchQuery] = useState('')
  useEffect(() => {
    const timer = setTimeout(() => setSearchQuery(search), 280)
    return () => clearTimeout(timer)
  }, [search])
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [copied, setCopied] = useState('')
  const [dragging, setDragging] = useState(false)
  const picker = useRef<HTMLInputElement>(null)

  const mayUpload = can('chats.send')

  const load = useCallback(async () => {
    try {
      const params = new URLSearchParams(entity)
      if (searchQuery.trim()) params.set('q', searchQuery.trim())
      const query = params.toString()
      const res = await fetch(`${API}/media/library${query ? `?${query}` : ''}`, { headers: authHeaders() })
      if (expireSessionOn401(res)) return
      if (!res.ok) throw new Error(`HTTP ${res.status}`)
      const data = (await res.json()) as { files: LibraryFile[] }
      setFiles(data.files ?? [])
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
      setFiles([])
    }
    // the entity picker is part of what the list means, so it belongs in here
  }, [searchQuery, entity])

  useEffect(() => {
    void load()
  }, [load])

  const upload = async (chosen: FileList | File[]) => {
    const list = Array.from(chosen)
    if (list.length === 0) return
    setBusy(true)
    setError('')
    try {
      for (const file of list) {
        const form = new FormData()
        form.append('file', file)
        const res = await fetch(`${API}/media/library`, {
          method: 'POST',
          headers: authHeaders(),
          body: form,
        })
        if (expireSessionOn401(res)) return
        if (!res.ok) {
          const body = (await res.json().catch(() => ({}))) as { detail?: string }
          throw new Error(body.detail || `HTTP ${res.status}`)
        }
      }
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
      if (picker.current) picker.current.value = ''
    }
  }

  const remove = async (fileId: string) => {
    setBusy(true)
    setError('')
    try {
      const res = await fetch(`${API}/media/library/${fileId}`, {
        method: 'DELETE',
        headers: authHeaders(),
      })
      if (expireSessionOn401(res)) return
      if (!res.ok) throw new Error(`HTTP ${res.status}`)
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  const copy = async (fileId: string) => {
    try {
      await navigator.clipboard.writeText(fileId)
      setCopied(fileId)
      setTimeout(() => setCopied(''), 1500)
    } catch {
      /* clipboard blocked — the id is selectable */
    }
  }

  return (
    <div className="flex flex-col gap-4">
      <PageHeader title={t('library.media.title')} subtitle={t('library.media.subtitle')} />

      {/* Card takes no drag handlers, so the drop zone is the div around it. */}
      {mayUpload && (
        <div
          data-tour="media-upload"
          onDragOver={(e) => {
            e.preventDefault()
            setDragging(true)
          }}
          onDragLeave={() => setDragging(false)}
          onDrop={(e) => {
            e.preventDefault()
            setDragging(false)
            void upload(e.dataTransfer.files)
          }}
        >
        <Card className={`p-5 text-center transition-colors ${dragging ? 'border-primary bg-fill-subtle' : ''}`}>
          <Upload size={22} className="mx-auto text-text-faint" aria-hidden />
          <p className="mt-2 text-[13.5px] font-semibold text-text-high">{t('library.media.dropTitle')}</p>
          <p className="mx-auto mt-1 max-w-[460px] text-[12.5px] leading-relaxed text-text-muted">
            {t('library.media.dropHint')}
          </p>
          <input
            ref={picker}
            type="file"
            multiple
            className="hidden"
            onChange={(e) => void upload(e.target.files ?? [])}
          />
          <Button className="mt-3" size="sm" disabled={busy} onClick={() => picker.current?.click()}>
            {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Upload size={13} aria-hidden />}
            {t('library.media.choose')}
          </Button>
        </Card>
        </div>
      )}

      {error && (
        <div
          role="alert"
          className="flex items-start gap-2 rounded-lg border border-red/25 bg-red/10 px-3 py-2 text-[12.5px] text-[color:var(--red-ink)]"
        >
          <AlertCircle size={14} className="mt-0.5 shrink-0" aria-hidden /> {error}
        </div>
      )}

      <Card data-tour="media-list" className="p-0">
        <div className="flex items-center gap-2 border-b border-line px-4 py-3">
          <input
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            placeholder={t('library.media.searchPlaceholder')}
            aria-label={t('library.media.searchPlaceholder')}
            className="w-full max-w-[280px] rounded-lg border border-line-strong bg-card px-3 py-1.5 text-[12.5px] text-text-primary outline-none transition-colors placeholder:text-text-faint focus:border-text-primary"
          />
          <span className="ml-auto text-[11.5px] text-text-faint">{t('library.media.kept')}</span>
        </div>

        {files === null ? (
          <div className="flex items-center justify-center gap-2 py-12 text-[12.5px] text-text-muted">
            <Loader2 size={14} className="animate-spin" aria-hidden /> {t('common.status.loading')}
          </div>
        ) : files.length === 0 ? (
          <p className="px-4 py-12 text-center text-[12.5px] text-text-muted">
            {searchQuery.trim() ? t('library.media.noMatches') : t('library.media.empty')}
          </p>
        ) : (
          <ul className="divide-y divide-line">
            {files.map((file) => (
              <li key={file.file_id} className="flex flex-wrap items-center gap-3 px-4 py-3">
                <div className="min-w-0 flex-1">
                  <div className="truncate text-[13.5px] font-semibold text-text-high">{file.name}</div>
                  <div className="text-[12px] text-text-muted">
                    {KIND_LABEL[file.kind] ?? file.kind} · {size(file.size)}
                    {' · '}
                    {file.last_sent_at
                      ? t('library.media.lastSent', { when: f.relative(file.last_sent_at) })
                      : t('library.media.neverSent')}
                  </div>
                </div>
                <code className="rounded bg-fill px-2 py-1 font-mono text-[11.5px] text-text-mid">
                  {file.file_id}
                </code>
                <div className="flex shrink-0 gap-1">
                  <Button variant="ghost" size="sm" onClick={() => void copy(file.file_id)}>
                    {copied === file.file_id ? <Check size={13} aria-hidden /> : <Copy size={13} aria-hidden />}
                    {copied === file.file_id ? t('common.actions.copied') : t('library.media.copyId')}
                  </Button>
                  {mayUpload && (
                    <Button variant="ghost" size="sm" disabled={busy} onClick={() => void remove(file.file_id)}>
                      <Trash2 size={13} aria-hidden /> {t('common.actions.delete')}
                    </Button>
                  )}
                </div>
              </li>
            ))}
          </ul>
        )}
      </Card>
    </div>
  )
}
