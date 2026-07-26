'use client'

import { Bell } from 'lucide-react'
import { AreaPage } from '@/components/shell/AreaPage'
import { NotificationsSettings } from '@/components/settings/NotificationsSettings'

export default function NotificationsPage() {
  return (
    <AreaPage title="Notifications" subtitle="Global mute switches for the in-app alert bell" Icon={Bell}>
      <NotificationsSettings />
    </AreaPage>
  )
}