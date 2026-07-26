'use client'

import { Megaphone } from 'lucide-react'
import { AreaPage } from '@/components/shell/AreaPage'
import { ScriptTemplatesSettings } from '@/components/settings/ScriptTemplatesSettings'

export default function ScriptTemplatesPage() {
  return (
    <AreaPage title="Script Templates" subtitle="Verbatim scripts read on one-way bot calls" Icon={Megaphone}>
      <ScriptTemplatesSettings />
    </AreaPage>
  )
}