'use client'

/**
 * Learning bits for an AI-monitor draft card: the 👍/👎 (with the 👎 popover)
 * and the "Based on" line naming the knowledge items and lessons the reply
 * was grounded in. Both render nothing on a server that doesn't send the
 * feedback / grounding fields, so the cards look exactly as before there.
 */

import Link from 'next/link'
import { useMemo, useState } from 'react'
import { BookOpen, GraduationCap } from 'lucide-react'
import { draftFeedback, draftGrounding, type MyFeedback } from '@/lib/aiLearning'
import type { Draft } from '@/lib/aiMonitor'
import { ThumbButtons, useFeedbackController } from './FeedbackThumbs'

/** 👍 / 👎 for a draft — anything with text: pending, sent, auto-sent, shadow… */
export function DraftFeedback({ d, className = '' }: { d: Draft; className?: string }) {
  const supported = 'my_feedback' in d || 'grounding' in d
  const [value, setValue] = useState<MyFeedback | null>(() => draftFeedback(d))
  const target = useMemo(() => ({ draft_id: d.id }), [d.id])
  const c = useFeedbackController({ target, value, onChange: setValue, aiText: d.content })
  if (!supported || !d.content?.trim()) return null
  return (
    <span className={`inline-flex items-center gap-2 ${className}`}>
      <span className="text-[11.5px] text-text-muted">Was this a good reply?</span>
      <ThumbButtons c={c} size="sm" label="the AI’s reply" />
      {c.saved && (
        <span role="status" className="text-[11.5px] text-[color:var(--green-ink)]">
          {c.saved}
        </span>
      )}
      {c.popover}
    </span>
  )
}

function pct(score: number | undefined): string {
  if (typeof score !== 'number' || !Number.isFinite(score)) return ''
  return score <= 1 ? `${Math.round(score * 100)}% match` : `score ${score.toFixed(2)}`
}

/** "Based on: Opening hours · Refund policy · Lesson #12" */
export function GroundingLine({ d }: { d: Draft }) {
  const items = draftGrounding(d)
  if (!items.length) return null
  // one entry per source (a long item may have contributed several chunks)
  const seen = new Set<string>()
  const unique = items.filter((g) => {
    const k = `${g.type}:${g.id}`
    if (seen.has(k)) return false
    seen.add(k)
    return true
  })
  return (
    <div className="flex flex-wrap items-center gap-1 text-[11.5px] text-text-muted" aria-label="What this reply was based on">
      <span className="font-semibold text-text-mid">Based on</span>
      {unique.slice(0, 6).map((g) => {
        const lesson = g.type === 'lesson'
        const Icon = lesson ? GraduationCap : BookOpen
        const label = lesson ? `Lesson #${g.id}` : g.title || `Knowledge #${g.id}`
        const tip = [lesson && g.title ? g.title : '', pct(g.score), g.chunk ? `“${g.chunk.slice(0, 280)}${g.chunk.length > 280 ? '…' : ''}”` : '']
          .filter(Boolean)
          .join('\n')
        return (
          <Link
            key={`${g.type}:${g.id}`}
            href={lesson ? `/ai-monitor?tab=learning&lesson=${g.id}` : '/knowledge'}
            title={tip || label}
            className="inline-flex max-w-[220px] items-center gap-1 truncate rounded-md border border-line bg-card px-1.5 py-0.5 text-text-mid transition-colors hover:bg-fill-hover hover:text-text-high"
          >
            <Icon size={11} aria-hidden />
            <span className="truncate">{label}</span>
          </Link>
        )
      })}
      {unique.length > 6 && <span>+{unique.length - 6} more</span>}
    </div>
  )
}
