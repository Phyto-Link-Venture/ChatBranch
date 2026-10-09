'use client'

import Link from 'next/link'
import { ExternalLink } from 'lucide-react'
import { Select, type SelectOption } from '@/components/ui/Select'
import type { AgentSkill } from '@/lib/flow/types'

/** Skill kinds from the Skills page: a PROMPT skill is a brief the AI writes
 * with — the only kind a step can pick. Tools and reply checks run on their
 * own whenever the AI writes, so they are listed for reference only. */
const KIND_LABEL: Record<string, string> = {
  prompt: 'Prompt skills',
  tool: 'Tools — used automatically',
  check: 'Reply checks — run automatically',
  app: 'Connected apps — used automatically',
}

const isPrompt = (kind?: string) => !kind || kind === 'prompt'

const groupName = (kind?: string) =>
  KIND_LABEL[kind || 'prompt'] ?? (kind ? kind.charAt(0).toUpperCase() + kind.slice(1) : undefined)

/**
 * Which AI skill guides this step. Reads the real skills (GET
 * /api/aicore/skills — the Skills page is their only editor) and stores the
 * skill's id in the step's `skill` config. Flows saved before this picker
 * stored the skill's NAME: such a value still resolves, and is shown as such.
 */
export default function SkillPicker({
  value,
  skills,
  loaded,
  onChange,
}: {
  value: string
  skills: AgentSkill[]
  loaded: boolean
  onChange: (id: string) => void
}) {
  const byId = skills.find((s) => s.id === value)
  const byName = !byId && value ? skills.find((s) => s.name === value) : undefined
  const options: SelectOption[] = [
    { value: '', label: 'No skill — the default assistant' },
    ...[...skills.filter((s) => isPrompt(s.kind)), ...skills.filter((s) => !isPrompt(s.kind))].map((s) => ({
      value: s.id,
      label: s.name,
      group: groupName(s.kind),
      hint: s.enabled === false ? '(off)' : undefined,
      disabled: !isPrompt(s.kind),
    })),
  ]
  if (value && !byId && !byName) options.push({ value, label: `${value} (not found)` })
  const shown = byName ? byName.id : value

  return (
    <div className="flex flex-col gap-1.5">
      <Select
        value={shown}
        onChange={onChange}
        options={options}
        size="md"
        searchable
        ariaLabel="AI skill"
        placeholder={loaded ? 'Pick a skill' : 'Loading skills…'}
      />
      <div className="flex flex-wrap items-center justify-between gap-2 text-[11.5px] text-text-muted">
        <span>
          {byName
            ? `Saved by name (“${value}”) in an older flow — pick it again to store its id.`
            : byId?.useWhen
              ? `Use when: ${byId.useWhen}`
              : loaded && skills.length === 0
                ? 'No skills yet.'
                : 'The skill’s instructions shape what the AI writes.'}
        </span>
        <Link href="/skills" className="inline-flex items-center gap-1 font-semibold text-text-mid hover:text-text-high">
          Manage skills <ExternalLink size={11} aria-hidden />
        </Link>
      </div>
    </div>
  )
}
