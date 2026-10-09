'use client'

import { Building2 } from 'lucide-react'
import { AreaPage } from '@/components/shell/AreaPage'
import { EntitiesSettings } from '@/components/settings/EntitiesSettings'
import { useT } from '@/i18n'

export default function EntitiesPage() {
  const t = useT()
  return (
    <AreaPage
      title={t('shell.nav.entities')}
      eyebrow={t('shell.groups.admin')}
      subtitle={t('admin.entities.page.subtitle')}
      Icon={Building2}
    >
      <EntitiesSettings />
    </AreaPage>
  )
}