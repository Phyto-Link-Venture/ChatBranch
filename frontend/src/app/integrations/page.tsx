'use client'

import { Plug } from 'lucide-react'
import { AreaPage } from '@/components/shell/AreaPage'
import { IntegrationsSettings } from '@/components/settings/IntegrationsSettings'
import { useT } from '@/i18n'

export default function IntegrationsPage() {
  const t = useT()
  return (
    <AreaPage
      title={t('shell.nav.integrations')}
      subtitle={t('channels.integrations.pageSubtitle')}
      Icon={Plug}
      eyebrow={t('shell.groups.channels')}
    >
      <IntegrationsSettings />
    </AreaPage>
  )
}
