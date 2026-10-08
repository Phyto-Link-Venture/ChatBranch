'use client'

import { useEffect, useMemo, useRef, useState } from 'react'
import { Plus, Search, X } from 'lucide-react'
import { authHeaders } from '@/lib/auth'
import { inputCls } from './fields'

/**
 * Pick specific contacts — searched SERVER-side (GET /api/v1/contacts?search=,
 * RBAC-scoped and paginated) because a contact book can run to hundreds of
 * thousands of rows; only one page of matches is ever rendered.
 *
 * `selected` holds contact ids as strings (the trigger's `contacts` key, which
 * the engine reads — empty = everyone); `labels` {id: name} is display-only.
 */

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'
const PAGE_SIZE = 20

interface Row {
  id: number
  name: string
  phone?: string
}

export default function ContactScopePicker({
  selected,
  labels,
  onChange,
  placeholder = 'Search contacts by name or phone…',
}: {
  selected: string[]
  labels: Record<string, string>
  onChange: (ids: string[], labels: Record<string, string>) => void
  placeholder?: string
}) {
  const [query, setQuery] = useState('')
  const [rows, setRows] = useState<Row[]>([])
  const [count, setCount] = useState(0)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [open, setOpen] = useState(false)
  const reqId = useRef(0)

  useEffect(() => {
    if (!open) return
    const mine = ++reqId.current
    const t = setTimeout(async () => {
      setBusy(true)
      setError('')
      try {
        const url = `${API}/v1/contacts?page_size=${PAGE_SIZE}${query.trim() ? `&search=${encodeURIComponent(query.trim())}` : ''}`
        const res = await fetch(url, { headers: authHeaders() })
        if (!res.ok) throw new Error(`HTTP ${res.status}`)
        const data = await res.json()
        if (mine !== reqId.current) return
        setRows(Array.isArray(data.results) ? data.results : [])
        setCount(typeof data.count === 'number' ? data.count : 0)
      } catch (e) {
        if (mine === reqId.current) {
          setError(e instanceof Error ? e.message : String(e))
          setRows([])
        }
      } finally {
        if (mine === reqId.current) setBusy(false)
      }
    }, 250)
    return () => clearTimeout(t)
  }, [query, open])

  const unpicked = useMemo(() => rows.filter((r) => !selected.includes(String(r.id))), [rows, selected])

  const add = (row: Row) => {
    const id = String(row.id)
    if (selected.includes(id)) return
    onChange([...selected, id], { ...labels, [id]: row.name })
  }
  const remove = (id: string) => {
    const next = { ...labels }
    delete next[id]
    onChange(selected.filter((x) => x !== id), next)
  }

  return (
    <div className="flex flex-col gap-2">
      {selected.length > 0 && (
        <div className="flex flex-wrap gap-1">
          {selected.map((id) => (
            <span key={id} className="inline-flex items-center gap-1 rounded-md border border-line-strong bg-card py-0.5 pl-2 pr-1 text-[12px] text-text-high">
              {labels[id] || `#${id}`}
              <button
                type="button"
                aria-label={`Remove ${labels[id] || id}`}
                onClick={() => remove(id)}
                className="grid h-4 w-4 place-items-center rounded text-text-muted hover:bg-fill-hover hover:text-text-high"
              >
                <X size={11} aria-hidden />
              </button>
            </span>
          ))}
        </div>
      )}
      <div className="relative">
        <Search size={13} className="pointer-events-none absolute left-2.5 top-1/2 -translate-y-1/2 text-text-faint" aria-hidden />
        <input
          type="search"
          value={query}
          placeholder={placeholder}
          onFocus={() => setOpen(true)}
          onChange={(e) => {
            setQuery(e.target.value)
            setOpen(true)
          }}
          className={`${inputCls} pl-8`}
          aria-label="Search contacts"
        />
      </div>
      {open && (
        <div className="rounded-lg border border-line bg-card">
          <div className="max-h-[200px] overflow-y-auto py-1">
            {busy && rows.length === 0 ? (
              <div className="px-3 py-2 text-[12px] text-text-muted">Searching…</div>
            ) : error ? (
              <div className="px-3 py-2 text-[12px] text-[color:var(--red-ink)]">Contact search unavailable — {error}</div>
            ) : unpicked.length === 0 ? (
              <div className="px-3 py-2 text-[12px] text-text-muted">No matches.</div>
            ) : (
              unpicked.map((r) => (
                <button
                  key={r.id}
                  type="button"
                  onClick={() => add(r)}
                  className="flex w-full items-center gap-2 px-3 py-1.5 text-left text-[12.5px] text-text-high hover:bg-fill-hover"
                >
                  <Plus size={12} className="shrink-0 text-text-muted" aria-hidden />
                  <span className="min-w-0 flex-1 truncate">{r.name}</span>
                  {r.phone && <span className="shrink-0 text-[11.5px] text-text-muted">{r.phone}</span>}
                </button>
              ))
            )}
          </div>
          <div className="flex items-center justify-between border-t border-line px-3 py-1.5 text-[11px] text-text-muted">
            <span>{count > rows.length ? `Showing ${rows.length} of ${count.toLocaleString()} — type to narrow` : `${count.toLocaleString()} match${count === 1 ? '' : 'es'}`}</span>
            <button type="button" className="font-semibold hover:text-text-high" onClick={() => setOpen(false)}>
              Done
            </button>
          </div>
        </div>
      )}
    </div>
  )
}
