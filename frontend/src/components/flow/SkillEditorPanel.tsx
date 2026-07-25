'use client'

import Link from 'next/link'
import { BookOpen, Lock, X } from 'lucide-react'
import type { AgentSkill } from '@/lib/flow/types'

/**
 * READ-ONLY viewer for an AGENT SKILL — the instruction module the AI loads.
 * Opens on the right when a skill is clicked in the sidebar, same pattern as
 * the node panel.
 *
 * Editing lives in Settings → AI Skills, which is the SOLE writer of
 * crm.AgentSkill. This page's library sync used to upsert and PRUNE those
 * rows, so a stale builder tab could silently delete a skill an operator had
 * just written. Read here, write there.
 */
export default function SkillEditorPanel({
  skill,
  onClose,
}: {
  skill: AgentSkill
  onClose: () => void
}) {
  return (
    <aside className="config-panel" style={{ '--kind': '#3B82F6' } as React.CSSProperties}>
      <header className="config-header">
        <span className="config-icon">
          <BookOpen size={15} aria-hidden />
        </span>
        <div className="config-title">
          <span className="config-kind">Agent skill</span>
          <span className="config-hint">What the AI knows &amp; follows for this job</span>
        </div>
        <button className="config-close" onClick={onClose} title="Close" aria-label="Close viewer">
          <X size={16} aria-hidden />
        </button>
      </header>

      <div className="config-body">
        <label className="config-field">
          <span>Skill name</span>
          <input type="text" value={skill.name} readOnly />
          <small>Action steps reference this name from their &ldquo;Agent skill&rdquo; dropdown.</small>
        </label>

        <label className="config-field">
          <span>Use when (the trigger)</span>
          <textarea rows={2} value={skill.useWhen ?? ''} readOnly />
        </label>

        <label className="config-field">
          <span>Instructions</span>
          <textarea rows={10} value={skill.instructions ?? ''} readOnly />
          <small>The rules the AI follows when a step runs this skill.</small>
        </label>

        <Link href="/settings/skills" className="fb-btn">
          Edit in Settings → AI Skills
        </Link>
      </div>

      <footer className="config-footer">
        <Lock size={11} aria-hidden style={{ verticalAlign: '-1px', marginRight: 4 }} />
        Skills are managed in Settings → AI Skills — they reach every channel and every call, not
        just flow steps.
      </footer>
    </aside>
  )
}
