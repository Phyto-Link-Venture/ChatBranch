'use client'

import { Building2 } from 'lucide-react'
import { AreaPage } from '@/components/shell/AreaPage'
import { EntitiesSettings } from '@/components/settings/EntitiesSettings'

export default function EntitiesPage() {
  return (
    <AreaPage title="Entities" subtitle="Your organization and the branches, outlets and units under it" Icon={Building2}>
      <EntitiesSettings />
    </AreaPage>
  )
}