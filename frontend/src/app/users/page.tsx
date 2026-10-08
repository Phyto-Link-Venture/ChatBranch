'use client'

import { Users } from 'lucide-react'
import { AreaPage } from '@/components/shell/AreaPage'
import { UsersSettings } from '@/components/settings/UsersSettings'

export default function UsersPage() {
  return (
    <AreaPage title="Users & Roles" eyebrow="Admin" subtitle="Operator logins, their roles and the entities they can see" Icon={Users}>
      <UsersSettings />
    </AreaPage>
  )
}