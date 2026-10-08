'use client'

import { Users } from 'lucide-react'
import { AreaPage } from '@/components/shell/AreaPage'
import { TeamsSettings } from '@/components/settings/TeamsSettings'

export default function TeamsPage() {
  return (
    <AreaPage
      title="Teams"
      eyebrow="Admin"
      subtitle="Who owns a conversation, and who it rotates to"
      Icon={Users}
    >
      <TeamsSettings />
    </AreaPage>
  )
}
