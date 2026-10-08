'use client'

import { Link2 } from 'lucide-react'
import { AreaPage } from '@/components/shell/AreaPage'
import { BindingsWorkbench } from '@/components/bindings/BindingsWorkbench'

export default function BindingsPage() {
  return (
    <AreaPage
      title="Bindings"
      eyebrow="Admin"
      subtitle="Who sees which entity, who is in which team, what is tagged — in one place"
      Icon={Link2}
    >
      <BindingsWorkbench />
    </AreaPage>
  )
}
