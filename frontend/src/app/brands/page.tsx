'use client'

import { Building2 } from 'lucide-react'
import { AreaPage } from '@/components/shell/AreaPage'
import { BrandsSettings } from '@/components/settings/BrandsSettings'

export default function BrandsPage() {
  return (
    <AreaPage title="Brands & Tenants" subtitle="Organizations and the business units they serve" Icon={Building2}>
      <BrandsSettings />
    </AreaPage>
  )
}