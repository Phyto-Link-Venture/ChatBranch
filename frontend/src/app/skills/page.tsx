'use client'

import { Sparkles } from 'lucide-react'
import { AreaPage } from '@/components/shell/AreaPage'
import { SkillsSettings } from '@/components/settings/SkillsSettings'
import { useT } from '@/i18n'

/**
 * AI skills are authored content (what the assistant can do, and when) rather
 * than system configuration — a Library page in the sidebar, not Settings.
 */
export default function SkillsPage() {
  const t = useT()
  return (
    <AreaPage
      title={t('shell.nav.skills')}
      eyebrow={t('shell.groups.library')}
      subtitle={t('ai.pages.skillsSubtitle')}
      Icon={Sparkles}
    >
      <SkillsSettings />
    </AreaPage>
  )
}
