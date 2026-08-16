'use client'

import { useEffect, useMemo, useRef, useState } from 'react'
import { X } from 'lucide-react'
import { authHeaders } from '@/lib/auth'

/**
 * Trigger scope picker: "every contact" (the default) or a named few.
 *
 * Deliberately NOT a checkbox list of the contact book — the book can run to
 * hundreds of thousands of rows, so neither chips nor a full <select> is
 * viable. This searches SERVER-side (GET /api/v1/contacts?search=, which is
 * RBAC-scoped and paginated) and only ever renders one page of matches.
 *
 * Storage contract:
 *   customers      string[] of customer ids — the ONLY key flows/engine reads.
 *                  Empty/absent = every contact, so flows saved before this
 *                  field existed keep working unchanged.
 *   contactLabels {id: name} kept purely so the panel can show what is
 *                  selected without re-fetching every id. The engine ignores
 *                  it; a rename shows the stale label until re-picked.
 */

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'
const PAGE_SIZE = 50

interface Row {
  id: number
  name: string
  phone?: string
}

export default function ContactScopePicker({
  selected,
  labels,
  onChange,
}: {
  selected: string[]
  labels: Record<string, string>
  onChange: (ids: string[], labels: Record<string, string>) => void
}) {
  // Mode is explicit state, NOT derived from selected.length — otherwise
  // choosing "only selected" could never stick, because the list starts empty.
  const [mode, setMode] = useState<'all' | 'some'>(selected.length ? 'some' : 'all')
  const scoped = mode === 'some'
  const [query, setQuery] = useState('')
  const [rows, setRows] = useState<Row[]>([])
  const [count, setCount] = useState(0)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const reqId = useRef(0)

  // Debounced server-side search — only while the "specific contacts" mode is
  // on, so the default (all contacts) costs no requests at all.
  useEffect(() => {
    if (!scoped) return
    const mine = ++reqId.current
    const t = setTimeout(async () => {
      setBusy(true)
      setError('')
      try {
        const url = `${API}/v1/contacts?page_size=${PAGE_SIZE}${
          query.trim() ? `&search=${encodeURIComponent(query.trim())}` : ''
        }`
        const res = await fetch(url, { headers: authHeaders() })
        if (!res.ok) throw new Error(`HTTP ${res.status}`)
        const data = await res.json()
        if (mine !== reqId.current) return // a newer keystroke won
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
  }, [query, scoped])

  const add = (row: Row) => {
    const id = String(row.id)
    if (selected.includes(id)) return
    onChange([...selected, id], { ...labels, [id]: row.name })
  }

  const remove = (id: string) => {
    const nextLabels = { ...labels }
    delete nextLabels[id]
    onChange(selected.filter((x) => x !== id), nextLabels)
  }

  const unpicked = useMemo(
    () => rows.filter((r) => !selected.includes(String(r.id))),
    [rows, selected],
  )

  return (
    <div className="contact-scope">
      <select
        value={mode}
        onChange={(e) => {
          const next = e.target.value as 'all' | 'some'
          setMode(next)
          setQuery('')
          if (next === 'all') onChange([], {}) // back to the default scope
        }}
      >
        <option value="all">All contacts (default)</option>
        <option value="some">Only selected contacts…</option>
      </select>

      {scoped && (
        <>
          <div className="contact-scope-chips">
            {selected.map((id) => (
              <span key={id} className="multi-chip on">
                {labels[id] || `#${id}`}
                <button type="button" aria-label={`Remove ${labels[id] || id}`} onClick={() => remove(id)}>
                  <X size={11} aria-hidden />
                </button>
              </span>
            ))}
          </div>
          <input
            type="search"
            value={query}
            placeholder="Search contacts by name or phone…"
            onChange={(e) => setQuery(e.target.value)}
          />
          <select
            value=""
            onChange={(e) => {
              const row = unpicked.find((r) => String(r.id) === e.target.value)
              if (row) add(row)
            }}
          >
            <option value="" disabled>
              {busy ? 'Searching…' : unpicked.length ? 'Add a contact…' : 'No matches'}
            </option>
            {unpicked.map((r) => (
              <option key={r.id} value={String(r.id)}>
                {r.name}
                {r.phone ? ` — ${r.phone}` : ''}
              </option>
            ))}
          </select>
          <div className="config-help">
            {selected.length === 0 ? (
              // An empty list is "all contacts" to the engine, so this mode with
              // nothing picked would silently enrol everyone — say so loudly.
              <strong>Pick at least one contact — an empty list still means ALL contacts.</strong>
            ) : error ? (
              `Contact search unavailable — ${error}`
            ) : count > rows.length ? (
              `Showing ${rows.length} of ${count.toLocaleString()} matches — type to narrow.`
            ) : (
              `${count.toLocaleString()} match${count === 1 ? '' : 'es'}.`
            )}
          </div>
        </>
      )}
    </div>
  )
}
