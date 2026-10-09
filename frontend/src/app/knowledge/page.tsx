'use client'

import { BookOpen } from 'lucide-react'
import { AreaPage } from '@/components/shell/AreaPage'
import { KnowledgeSettings } from '@/components/settings/KnowledgeSettings'
import { useT } from '@/i18n'

/**
 * Knowledge Base is a CONTENT library the operator maintains (opening hours,
 * policies, notices) — not system configuration, so it lives in the sidebar
 * next to the other libraries rather than under Settings.
 */
export default function KnowledgePage() {
  const t = useT()
  return (
    <AreaPage
      title={t('shell.nav.knowledge')}
      eyebrow={t('shell.groups.library')}
      subtitle={t('ai.pages.knowledgeSubtitle')}
      Icon={BookOpen}
    >
      <KnowledgeSettings />
    </AreaPage>
  )
}
