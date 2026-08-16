'use client'

import { Zap } from 'lucide-react'
import { AreaPage } from '@/components/shell/AreaPage'
import { AutomationsSettings } from '@/components/settings/AutomationsSettings'

export default function AutomationsPage() {
  return (
    <AreaPage
      title="Automations"
      subtitle="When this happens to a conversation, do that"
      Icon={Zap}
    >
      <AutomationsSettings />
    </AreaPage>
  )
}
