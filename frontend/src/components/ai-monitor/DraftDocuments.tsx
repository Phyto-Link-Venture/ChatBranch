'use client'

import { useState } from 'react'
import { ExternalLink, FileText, Loader2, QrCode } from 'lucide-react'
import { openAuthed } from '@/components/skills/readySkills'
import { useFormat, useT, type MessageKey } from '@/i18n'
import type { DraftDocument } from '@/lib/aiMonitor'

const STATUS: Record<string, MessageKey> = {
  pending: 'readySkills.draft.status.pending',
  sent: 'readySkills.draft.status.sent',
  void: 'readySkills.draft.status.void',
  failed: 'readySkills.draft.status.failed',
  issued: 'readySkills.draft.status.issued',
}

const INK: Record<string, string> = {
  pending: 'text-[color:var(--amber-ink)]',
  sent: 'text-[color:var(--green-ink)]',
  void: 'text-text-muted',
  failed: 'text-[color:var(--red-ink)]',
  issued: 'text-text-mid',
}

/**
 * The files an AI reply carries (Documents PDFs, QR codes): made while the
 * reply was written, SENT right after its text when it is delivered. A held
 * reply's files go out only if a person approves it; rejecting voids them. Each
 * line opens the real file (an auth-gated download, in a new tab).
 */
export function DraftDocuments({
  d,
  compact = false,
}: {
  d: { documents?: DraftDocument[]; status: string }
  compact?: boolean
}) {
  const t = useT()
  const f = useFormat()
  const [opening, setOpening] = useState<number | null>(null)
  const list = Array.isArray(d.documents) ? d.documents : []
  if (!list.length) return null
  const held = d.status === 'pending' || d.status === 'shadow'

  const open = async (doc: DraftDocument) => {
    setOpening(doc.id)
    try {
      await openAuthed(doc.url)
    } catch {
      /* the line stays; the browser shows nothing new */
    } finally {
      setOpening(null)
    }
  }

  return (
    <ul aria-label={t('readySkills.draft.label')} className="flex flex-col gap-1">
      {list.map((doc) => {
        const Icon = doc.type === 'qr' ? QrCode : FileText
        return (
          <li
            key={doc.id}
            className={`flex flex-wrap items-center gap-1.5 text-[12.5px] leading-snug ${
              compact ? '' : 'rounded-lg border border-line bg-fill-subtle px-2.5 py-1.5'
            }`}
          >
            <Icon size={13} className={`shrink-0 ${INK[doc.status] ?? 'text-text-muted'}`} aria-hidden />
            <span className="min-w-0 text-text-high">
              <b className="font-semibold">{doc.label}</b>
              {doc.total != null && (
                <span className="text-text-muted"> · {f.currency(doc.total, doc.currency || 'MYR')}</span>
              )}
              <span className={`ml-1.5 ${INK[doc.status] ?? 'text-text-muted'}`}>
                {held && doc.status === 'pending' ? t('readySkills.draft.status.held') : t(STATUS[doc.status] ?? STATUS.pending)}
              </span>
            </span>
            <button
              type="button"
              onClick={() => open(doc)}
              className="ml-auto inline-flex items-center gap-1 text-[12px] font-semibold text-text-mid hover:text-text-heading"
            >
              {opening === doc.id ? <Loader2 size={12} className="animate-spin" aria-hidden /> : <ExternalLink size={12} aria-hidden />}
              {t('readySkills.draft.open')}
            </button>
          </li>
        )
      })}
    </ul>
  )
}
