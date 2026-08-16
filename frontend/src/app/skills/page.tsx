'use client'

import { Sparkles } from 'lucide-react'
import { AreaPage } from '@/components/shell/AreaPage'
import { SkillsSettings } from '@/components/settings/SkillsSettings'

/**
 * AI Skills are authored content (what the assistant can do, and when) rather
 * than system configuration — sidebar, not Settings.
 */
export default function SkillsPage() {
  return (
    <AreaPage
      title="AI Skills"
      subtitle="Named abilities the assistant applies — each with its own trigger and instructions"
      Icon={Sparkles}
    >
      <SkillsSettings />
    </AreaPage>
  )
}
