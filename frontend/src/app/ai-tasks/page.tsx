'use client'

import { Bot } from 'lucide-react'
import { AreaPage } from '@/components/shell/AreaPage'
import { AiTasks } from '@/components/ai-tasks/AiTasks'
import { useT } from '@/i18n'

export default function AiTasksPage() {
  const t = useT()
  return (
    <AreaPage title={t('shell.nav.aiTasks')} subtitle={t('aiTasks.subtitle')} Icon={Bot} eyebrow={t('shell.groups.automate')}>
      <AiTasks />
    </AreaPage>
  )
}
