'use client'

import { Bell } from 'lucide-react'
import { AreaPage } from '@/components/shell/AreaPage'
import { NotificationsInbox } from '@/components/notifications/NotificationsInbox'
import { useT } from '@/i18n'

/**
 * Everyone's notification inbox — the full history behind the header bell.
 * (The organization-wide mute switches that used to live here are in
 * Settings → Organization → Notification defaults.)
 */
export default function NotificationsPage() {
  const t = useT()
  return (
    <AreaPage
      title={t('shell.nav.notifications')}
      subtitle={t('notifications.page.subtitle')}
      Icon={Bell}
      eyebrow={t('notifications.page.eyebrow')}
    >
      <NotificationsInbox />
    </AreaPage>
  )
}
