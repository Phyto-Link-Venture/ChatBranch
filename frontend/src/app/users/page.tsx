'use client'

import { Users } from 'lucide-react'
import { AreaPage } from '@/components/shell/AreaPage'
import { UsersSettings } from '@/components/settings/UsersSettings'
import { useT } from '@/i18n'

export default function UsersPage() {
  const t = useT()
  return (
    <AreaPage title={t('shell.nav.users')} eyebrow={t('shell.groups.admin')} subtitle={t('admin.users.page.subtitle')} Icon={Users}>
      <UsersSettings />
    </AreaPage>
  )
}