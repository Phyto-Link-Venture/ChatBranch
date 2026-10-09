'use client'

import Link from 'next/link'
import { ExternalLink } from 'lucide-react'
import { Select, type SelectOption } from '@/components/ui/Select'
import { useT, type MessageKey } from '@/i18n'
import type { AgentSkill } from '@/lib/flow/types'

/** Skill kinds from the Skills page: a PROMPT skill is a brief the AI writes
 * with — the only kind a step can pick. Tools and reply checks run on their
 * own whenever the AI writes, so they are listed for reference only. */
const KIND_LABEL: Record<string, MessageKey> = {
  prompt: 'flows.skillPicker.kindPrompt',
  tool: 'flows.skillPicker.kindTool',
  check: 'flows.skillPicker.kindCheck',
  app: 'flows.skillPicker.kindApp',
}

const isPrompt = (kind?: string) => !kind || kind === 'prompt'

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
  const t = useT()
  const groupName = (kind?: string) => {
    const key = KIND_LABEL[kind || 'prompt']
    return key ? t(key) : kind ? kind.charAt(0).toUpperCase() + kind.slice(1) : undefined
  }
  const byId = skills.find((s) => s.id === value)
  const byName = !byId && value ? skills.find((s) => s.name === value) : undefined
  const options: SelectOption[] = [
    { value: '', label: t('flows.skillPicker.none') },
    ...[...skills.filter((s) => isPrompt(s.kind)), ...skills.filter((s) => !isPrompt(s.kind))].map((s) => ({
      value: s.id,
      label: s.name,
      group: groupName(s.kind),
      hint: s.enabled === false ? t('flows.skillPicker.off') : undefined,
      disabled: !isPrompt(s.kind),
    })),
  ]
  if (value && !byId && !byName) options.push({ value, label: t('flows.skillPicker.notFound', { value }) })
  const shown = byName ? byName.id : value

  return (
    <div className="flex flex-col gap-1.5">
      <Select
        value={shown}
        onChange={onChange}
        options={options}
        size="md"
        searchable
        ariaLabel={t('flows.skillPicker.aria')}
        placeholder={loaded ? t('flows.skillPicker.pick') : t('flows.skillPicker.loading')}
      />
      <div className="flex flex-wrap items-center justify-between gap-2 text-[11.5px] text-text-muted">
        <span className="min-w-0">
          {byName
            ? t('flows.skillPicker.savedByName', { name: value })
            : byId?.useWhen
              ? t('flows.skillPicker.useWhen', { text: byId.useWhen })
              : loaded && skills.length === 0
                ? t('flows.skillPicker.noSkills')
                : t('flows.skillPicker.shapes')}
        </span>
        <Link href="/skills" className="inline-flex items-center gap-1 font-semibold text-text-mid hover:text-text-high">
          {t('flows.skillPicker.manage')} <ExternalLink size={11} aria-hidden />
        </Link>
      </div>
    </div>
  )
}
