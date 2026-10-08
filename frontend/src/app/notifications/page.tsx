'use client'

import { Bell } from 'lucide-react'
import { AreaPage } from '@/components/shell/AreaPage'
import { NotificationsInbox } from '@/components/notifications/NotificationsInbox'

/**
 * Everyone's notification inbox — the full history behind the header bell.
 * (The organization-wide mute switches that used to live here are in
 * Settings → Organization → Notification defaults.)
 */
export default function NotificationsPage() {
  return (
    <AreaPage
      title="Notifications"
      subtitle="Everything that needed your attention — filter, open, tidy up."
      Icon={Bell}
      eyebrow="Inbox"
    >
      <NotificationsInbox />
    </AreaPage>
  )
}
