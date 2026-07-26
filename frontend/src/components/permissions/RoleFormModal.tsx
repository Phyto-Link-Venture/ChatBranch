'use client'

import { useEffect, useState } from 'react'
import { AlertCircle, Loader2, Pencil, Plus } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Modal } from '@/components/ui/Modal'

/**
 * Add / rename dialog for a role (spec §B Level 1). `mode` picks the copy and
 * icon; `initialLabel` seeds the field for rename. The parent supplies the
 * async `onSubmit` and surfaces server errors via `error`.
 */
export function RoleFormModal({
  open,
  mode,
  initialLabel = '',
  busy,
  error,
  onSubmit,
  onClose,
}: {
  open: boolean
  mode: 'add' | 'rename'
  initialLabel?: string
  busy: boolean
  error: string | null
  onSubmit: (label: string) => void
  onClose: () => void
}) {
  const [label, setLabel] = useState(initialLabel)

  useEffect(() => {
    if (open) setLabel(initialLabel)
  }, [open, initialLabel])

  const submit = () => {
    const v = label.trim()
    if (v) onSubmit(v)
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={mode === 'add' ? 'Add custom role' : 'Rename role'}
      subtitle={
        mode === 'add'
          ? 'A new role starts with no access. Grant capabilities in the card grid, then save.'
          : 'Give this custom role a clearer name. Its slug and capabilities stay the same.'
      }
      size="sm"
      footer={
        <>
          <Button variant="ghost" onClick={onClose} disabled={busy}>
            Cancel
          </Button>
          <Button variant="primary" onClick={submit} disabled={busy || !label.trim()}>
            {busy ? (
              <Loader2 size={13} className="animate-spin" aria-hidden />
            ) : mode === 'add' ? (
              <Plus size={13} aria-hidden />
            ) : (
              <Pencil size={13} aria-hidden />
            )}{' '}
            {mode === 'add' ? 'Create role' : 'Save name'}
          </Button>
        </>
      }
    >
      <label className="block">
        <span className="mb-1 block text-[11px] font-bold text-text-high">Role name</span>
        <input
          value={label}
          onChange={(e) => setLabel(e.target.value)}
          onKeyDown={(e) => e.key === 'Enter' && submit()}
          placeholder="e.g. Team Lead"
          autoFocus
          className={`w-full rounded-lg border bg-card px-3 py-2 text-[12.5px] text-text-primary outline-none transition-colors focus:border-brand-400 ${
            error ? 'border-red-500/60' : 'border-line-strong'
          }`}
        />
        {error ? (
          <span
            className="mt-1 flex items-center gap-1 text-[10.5px] font-semibold"
            style={{ color: 'var(--red-ink)' }}
          >
            <AlertCircle size={11} className="shrink-0" aria-hidden /> {error}
          </span>
        ) : (
          <span className="mt-1 block text-[10.5px] leading-relaxed text-text-muted">
            {mode === 'add'
              ? 'A url-safe slug is generated from the name. Custom roles are treated as agent-level for assignment.'
              : 'Only the display name changes.'}
          </span>
        )}
      </label>
    </Modal>
  )
}
