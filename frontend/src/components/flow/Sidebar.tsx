'use client'

import Link from 'next/link'
import { useState, type DragEvent } from 'react'
import { ChevronRight, Copy, GripVertical, Pencil, Plus, Trash2 } from 'lucide-react'
import type { AgentSkill, NodeKind, WorkflowDef } from '@/lib/flow/types'
import { PALETTE, KindIcon } from './nodes'

/**
 * Left panel, holding two ideas:
 * - WORKFLOWS: the visual flows on the canvas (one per scenario)
 * - Blocks: the draggable step palette
 * - AGENT SKILLS: the instruction modules the AI loads — click one to VIEW it;
 *   Action steps reference them by name. Editing is in Settings → AI Skills,
 *   the sole writer of crm.AgentSkill (this page's library sync used to prune
 *   those rows, which could delete a skill typed in Settings from a stale tab).
 *
 * The dragged block kind travels via the native dataTransfer API;
 * FlowBuilder.tsx reads it back in onDrop.
 */
export default function Sidebar({
  workflows,
  activeWorkflowId,
  onSelectWorkflow,
  onCreateWorkflow,
  onRenameWorkflow,
  onDuplicateWorkflow,
  onDeleteWorkflow,
  agentSkills,
  selectedAgentSkillId,
  onSelectAgentSkill,
}: {
  workflows: WorkflowDef[]
  activeWorkflowId: string
  onSelectWorkflow: (id: string) => void
  onCreateWorkflow: () => void
  onRenameWorkflow: (id: string, name: string) => void
  onDuplicateWorkflow: (id: string) => void
  onDeleteWorkflow: (id: string) => void
  agentSkills: AgentSkill[]
  selectedAgentSkillId: string | null
  onSelectAgentSkill: (id: string) => void
}) {
  const [editingId, setEditingId] = useState<string | null>(null)
  const [skillsOpen, setSkillsOpen] = useState(true)
  const lastWorkflow = workflows.length <= 1

  const onDragStart = (event: DragEvent, kind: NodeKind) => {
    event.dataTransfer.setData('application/reactflow', kind)
    event.dataTransfer.effectAllowed = 'move'
  }

  const commitRename = (id: string, value: string) => {
    onRenameWorkflow(id, value)
    setEditingId(null)
  }

  return (
    <aside className="fb-sidebar">
      <div className="fb-section-label">Workflows</div>
      <p className="sub">One flow per scenario — shipped defaults to start from. Create as many as you need.</p>

      <div className="skill-list">
        {workflows.map((w) => (
          <div
            key={w.id}
            className={`skill-item${w.id === activeWorkflowId ? ' active' : ''}`}
            title={w.hint ?? w.name}
            onClick={() => onSelectWorkflow(w.id)}
          >
            {editingId === w.id ? (
              <input
                className="skill-name-input"
                autoFocus
                defaultValue={w.name}
                aria-label="Workflow name"
                onClick={(e) => e.stopPropagation()}
                onFocus={(e) => e.target.select()}
                onBlur={(e) => commitRename(w.id, e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === 'Enter') (e.target as HTMLInputElement).blur()
                  if (e.key === 'Escape') setEditingId(null)
                }}
              />
            ) : (
              <span
                className="skill-name"
                onDoubleClick={(e) => {
                  e.stopPropagation()
                  setEditingId(w.id)
                }}
              >
                {w.active && (
                  <span
                    className="wf-live-dot"
                    title="Live — the engine runs this flow automatically"
                    aria-label="Live"
                  />
                )}
                {w.name}
              </span>
            )}
            <span className="skill-actions">
              <button
                title="Rename"
                aria-label={`Rename ${w.name}`}
                onClick={(e) => {
                  e.stopPropagation()
                  setEditingId(w.id)
                }}
              >
                <Pencil size={12} aria-hidden />
              </button>
              <button
                title="Duplicate"
                aria-label={`Duplicate ${w.name}`}
                onClick={(e) => {
                  e.stopPropagation()
                  onDuplicateWorkflow(w.id)
                }}
              >
                <Copy size={12} aria-hidden />
              </button>
              <button
                title={lastWorkflow ? 'Keep at least one workflow in the library' : 'Delete'}
                aria-label={`Delete ${w.name}`}
                disabled={lastWorkflow}
                onClick={(e) => {
                  e.stopPropagation()
                  onDeleteWorkflow(w.id)
                }}
              >
                <Trash2 size={12} aria-hidden />
              </button>
            </span>
          </div>
        ))}
        <button className="skill-add" onClick={onCreateWorkflow}>
          <Plus size={13} aria-hidden /> New workflow
        </button>
      </div>

      <div className="fb-section-label" style={{ marginTop: 20 }}>
        Blocks
      </div>
      <p className="sub">Drag a block onto the canvas to add a step.</p>

      {PALETTE.map(({ kind, title, color, hint }) => (
        <div
          key={kind}
          className="palette-item"
          style={{ '--kind': color } as React.CSSProperties}
          draggable
          onDragStart={(e) => onDragStart(e, kind)}
        >
          <span className="palette-icon">
            <KindIcon kind={kind} size={15} />
          </span>
          <div>
            <strong>{title}</strong>
            <small>{hint}</small>
          </div>
          <span className="palette-grip" aria-hidden>
            <GripVertical size={13} />
          </span>
        </div>
      ))}

      <button
        className="fb-section-label section-toggle"
        style={{ marginTop: 20 }}
        aria-expanded={skillsOpen}
        onClick={() => setSkillsOpen(!skillsOpen)}
      >
        <ChevronRight size={12} className={`var-caret${skillsOpen ? ' open' : ''}`} aria-hidden />
        Agent skills ({agentSkills.length})
      </button>
      <p className="sub">
        The instruction modules the AI loads — attach one to any Action step via its settings.
        Read-only here; they reach every channel and every call, so they are edited in Settings.
      </p>

      {skillsOpen && (
        <div className="skill-list">
          {agentSkills.map((s) => (
            <div
              key={s.id}
              className={`skill-item${s.id === selectedAgentSkillId ? ' active' : ''}`}
              title={s.useWhen || s.name}
              onClick={() => onSelectAgentSkill(s.id)}
            >
              <span className="skill-name">{s.name}</span>
            </div>
          ))}
          <Link href="/settings/skills" className="skill-restore">
            Manage in Settings → AI Skills
          </Link>
        </div>
      )}

      <div className="help">
        <div className="fb-section-label">How to use</div>
        <ul>
          <li>
            <strong>Workflows:</strong> click to switch, double-click the name to rename. Your work saves to
            the server automatically (and to this browser as a fallback).
          </li>
          <li>
            <strong>Go live</strong> (toolbar) arms the current workflow: the engine then runs it by itself —
            triggers fire on schedule (due-date based), and customer replies move it along. A green dot marks live flows.
          </li>
          <li>
            <strong>Agent skills</strong> are the AI&apos;s knowledge modules. Click one to edit its
            &quot;use when&quot; and instructions; pick it on an
            Action step under <em>Agent skill for this step</em>.
          </li>
          <li>
            <strong>Click a node</strong> to open its settings panel and configure it.
          </li>
          <li>
            In a script field, pick from <em>Insert a variable</em> — e.g. <kbd>{'{{amount}}'}</kbd> —
            and the real value is filled in at call time.
          </li>
          <li>Drag from a node&apos;s bottom dot to another node&apos;s top dot to connect.</li>
          <li>
            <strong>Stage sections</strong> are day-range lanes: drop steps inside and they move with the
            stage. Select one to resize it; click it to set the stage &amp; day range.
          </li>
          <li>
            <strong>Click a line</strong> to configure it: label, when it fires (wait N days / on reply / on
            record update), solid or dashed.
          </li>
          <li>Double-click a node&apos;s text to rewrite it.</li>
          <li>
            Select a node/edge and press <kbd>Del</kbd> to remove it.
          </li>
          <li>
            <em>Export flow</em> saves the active workflow as JSON; <em>Import flow</em> adds a JSON file as
            a new workflow.
          </li>
        </ul>
      </div>
    </aside>
  )
}
