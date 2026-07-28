'use client'

import { Users } from 'lucide-react'
import { AreaPage } from '@/components/shell/AreaPage'
import { UsersSettings } from '@/components/settings/UsersSettings'

export default function UsersPage() {
  return (
    <AreaPage title="Users & Roles" subtitle="Operator logins, roles and entity access" Icon={Users}>
      <UsersSettings />
    </AreaPage>
  )
}