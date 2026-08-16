'use client'

import { BookOpen } from 'lucide-react'
import { AreaPage } from '@/components/shell/AreaPage'
import { KnowledgeSettings } from '@/components/settings/KnowledgeSettings'

/**
 * Knowledge Base is a CONTENT library the operator maintains (opening hours,
 * policies, notices) — not system configuration, so it lives in the sidebar
 * next to the other libraries rather than under Settings.
 */
export default function KnowledgePage() {
  return (
    <AreaPage
      title="Knowledge Base"
      subtitle="What the AI knows about your business — facts, policies, FAQs and notices"
      Icon={BookOpen}
    >
      <KnowledgeSettings />
    </AreaPage>
  )
}
