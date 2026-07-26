'use client'

import { Plug } from 'lucide-react'
import { AreaPage } from '@/components/shell/AreaPage'
import { IntegrationsSettings } from '@/components/settings/IntegrationsSettings'

export default function IntegrationsPage() {
  return (
    <AreaPage title="Integrations" subtitle="Connect Calendar, Drive, Gmail, Sheets, Shopify & Stripe" Icon={Plug}>
      <IntegrationsSettings />
    </AreaPage>
  )
}