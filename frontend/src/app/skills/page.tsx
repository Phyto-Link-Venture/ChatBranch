'use client'

import { Sparkles } from 'lucide-react'
import { AreaPage } from '@/components/shell/AreaPage'
import { SkillsSettings } from '@/components/settings/SkillsSettings'

/**
 * AI skills are authored content (what the assistant can do, and when) rather
 * than system configuration — a Library page in the sidebar, not Settings.
 */
export default function SkillsPage() {
  return (
    <AreaPage
      title="AI skills"
      eyebrow="Library"
      subtitle="Instructions the AI follows, tools it calls for exact answers, and checks every reply must pass"
      Icon={Sparkles}
    >
      <SkillsSettings />
    </AreaPage>
  )
}
