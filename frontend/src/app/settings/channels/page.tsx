import { LegacyRedirect } from '@/components/shell/LegacyRedirect'

// Moved to a top-level route (upgrade §1). Old link redirects.
export default function LegacyChannelsSettingsPage() {
  return <LegacyRedirect to="/channels" />
}
