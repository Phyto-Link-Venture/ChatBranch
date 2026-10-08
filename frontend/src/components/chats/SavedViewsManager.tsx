'use client'

import { useEffect, useState } from 'react'
import { ArrowDown, ArrowUp, Trash2 } from 'lucide-react'
import { Modal } from '@/components/ui/Modal'
import { Button } from '@/components/ui/Button'
import type { SavedView } from '@/lib/savedViews'

/**
 * Rename, reorder and delete the operator's own saved views. Each action is
 * applied straight away (the parent calls the API and reloads), so there is no
 * half-saved state to lose by closing the dialog.
 */
export default function SavedViewsManager<F>({
  open,
  onClose,
  views,
  onRename,
  onDelete,
  onReorder,
}: {
  open: boolean
  onClose: () => void
  views: SavedView<F>[]
  onRename: (id: number, name: string) => Promise<void>
  onDelete: (id: number) => Promise<void>
  onReorder: (ids: number[]) => Promise<void>
}) {
  const [names, setNames] = useState<Record<number, string>>({})
  const [busy, setBusy] = useState(false)
  const [msg, setMsg] = useState<string | null>(null)

  useEffect(() => {
    if (open) {
      setNames(Object.fromEntries(views.map((v) => [v.id, v.name])))
      setMsg(null)
    }
  }, [open, views])

  const run = async (fn: () => Promise<void>) => {
    setBusy(true)
    setMsg(null)
    try {
      await fn()
    } catch (e) {
      setMsg(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  const move = (i: number, dir: -1 | 1) => {
    const ids = views.map((v) => v.id)
    const j = i + dir
    if (j < 0 || j >= ids.length) return
    ;[ids[i], ids[j]] = [ids[j], ids[i]]
    run(() => onReorder(ids))
  }

  const commitName = (v: SavedView<F>) => {
    const next = (names[v.id] ?? '').trim()
    if (!next || next === v.name) {
      setNames((n) => ({ ...n, [v.id]: v.name }))
      return
    }
    run(() => onRename(v.id, next))
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      size="sm"
      labelledById="views-manager-title"
      title="Saved views"
      subtitle="Your own tabs above the chat list — nobody else sees them."
      footer={
        <Button variant="secondary" onClick={onClose}>
          Done
        </Button>
      }
    >
      {msg && (
        <div role="alert" className="mb-3 rounded-lg border border-red/30 bg-red/10 px-3 py-2 text-[12.5px] font-semibold text-red-300">
          {msg}
        </div>
      )}
      {views.length === 0 ? (
        <p className="py-6 text-center text-[13px] text-text-muted">
          No saved views yet. Set some filters, then use <span className="font-semibold text-text-high">Save as view</span> in the filter
          pop-up.
        </p>
      ) : (
        <ul className="divide-y divide-line rounded-lg border border-line">
          {views.map((v, i) => (
            <li key={v.id} className="flex items-center gap-1.5 px-2.5 py-2">
              <input
                value={names[v.id] ?? v.name}
                onChange={(e) => setNames((n) => ({ ...n, [v.id]: e.target.value }))}
                onBlur={() => commitName(v)}
                onKeyDown={(e) => e.key === 'Enter' && (e.currentTarget as HTMLInputElement).blur()}
                maxLength={60}
                disabled={busy}
                aria-label={`Name of view ${v.name}`}
                className="h-8 min-w-0 flex-1 rounded-md border border-transparent bg-transparent px-2 text-[13px] text-text-high outline-none hover:border-line focus:border-text-primary"
              />
              <button
                type="button"
                onClick={() => move(i, -1)}
                disabled={busy || i === 0}
                aria-label={`Move ${v.name} up`}
                className="grid h-7 w-7 place-items-center rounded-md text-text-muted hover:bg-fill-hover hover:text-text-high disabled:opacity-30"
              >
                <ArrowUp size={13} aria-hidden />
              </button>
              <button
                type="button"
                onClick={() => move(i, 1)}
                disabled={busy || i === views.length - 1}
                aria-label={`Move ${v.name} down`}
                className="grid h-7 w-7 place-items-center rounded-md text-text-muted hover:bg-fill-hover hover:text-text-high disabled:opacity-30"
              >
                <ArrowDown size={13} aria-hidden />
              </button>
              <button
                type="button"
                onClick={() => window.confirm(`Delete the view "${v.name}"?`) && run(() => onDelete(v.id))}
                disabled={busy}
                aria-label={`Delete ${v.name}`}
                className="grid h-7 w-7 place-items-center rounded-md text-text-muted hover:bg-red/10 hover:text-red-300 disabled:opacity-30"
              >
                <Trash2 size={13} aria-hidden />
              </button>
            </li>
          ))}
        </ul>
      )}
    </Modal>
  )
}
