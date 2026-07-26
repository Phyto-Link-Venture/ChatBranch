'use client'

import { MessageSquare } from 'lucide-react'
import { AreaPage } from '@/components/shell/AreaPage'
import { SharedTemplatesSettings } from '@/components/settings/SharedTemplatesSettings'

export default function TemplatesPage() {
  return (
    <AreaPage title="Message Templates" subtitle="Cross-channel shared replies inserted with /short-code" Icon={MessageSquare}>
      <SharedTemplatesSettings />
    </AreaPage>
  )
}