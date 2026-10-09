'use client'

import { Users } from 'lucide-react'
import { AreaPage } from '@/components/shell/AreaPage'
import { TeamsSettings } from '@/components/settings/TeamsSettings'
import { useT } from '@/i18n'

export default function TeamsPage() {
  const t = useT()
  return (
    <AreaPage
      title={t('shell.nav.teams')}
      eyebrow={t('shell.groups.admin')}
      subtitle={t('admin.teams.page.subtitle')}
      Icon={Users}
    >
      <TeamsSettings />
    </AreaPage>
  )
}
