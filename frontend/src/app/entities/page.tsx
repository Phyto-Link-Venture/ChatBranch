'use client'

import { Building2 } from 'lucide-react'
import { AreaPage } from '@/components/shell/AreaPage'
import { EntitiesSettings } from '@/components/settings/EntitiesSettings'

export default function EntitiesPage() {
  return (
    <AreaPage title="Entities & Tenants" subtitle="Organizations and the business units they serve" Icon={Building2}>
      <EntitiesSettings />
    </AreaPage>
  )
}