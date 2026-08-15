'use client'

import { Briefcase } from 'lucide-react'
import { AreaPage } from '@/components/shell/AreaPage'
import { ProvidersSettings } from '@/components/settings/ProvidersSettings'

/**
 * Service Providers (the delivery side — teachers / coaches / practitioners)
 * are operational data managed day-to-day, like Contacts — so they live as a
 * top-level Work area, not buried in Settings. The Settings side keeps only
 * the *configuration* for them (Custom Fields / labels).
 */
export default function ProvidersPage() {
  return (
    <AreaPage
      title="Service Providers"
      subtitle="The delivery side — who your customers are matched to"
      Icon={Briefcase}
    >
      <ProvidersSettings />
    </AreaPage>
  )
}
