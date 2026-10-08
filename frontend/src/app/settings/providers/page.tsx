import { LegacyRedirect } from '@/components/shell/LegacyRedirect'

// Service Providers were retired — how a contact relates to the business
// (boss, employee, driver…) is a tag now. Old links land on the tag registry.
export default function LegacyProvidersSettingsPage() {
  return <LegacyRedirect to="/tags" />
}
