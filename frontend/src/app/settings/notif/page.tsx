import { LegacyRedirect } from '@/components/shell/LegacyRedirect'

// The old Settings → Notifications tab (organization mute switches) lives at
// Settings → Organization → Notification defaults; /notifications is the inbox.
export default function LegacyNotifSettingsPage() {
  return <LegacyRedirect to="/settings/notifications" />
}
