'use client'

import { Link2 } from 'lucide-react'
import { AreaPage } from '@/components/shell/AreaPage'
import { BindingsWorkbench } from '@/components/bindings/BindingsWorkbench'

export default function BindingsPage() {
  return (
    <AreaPage
      title="Bindings"
      subtitle="Bind contacts, users, teams, entities and providers in bulk"
      Icon={Link2}
    >
      <BindingsWorkbench />
    </AreaPage>
  )
}
