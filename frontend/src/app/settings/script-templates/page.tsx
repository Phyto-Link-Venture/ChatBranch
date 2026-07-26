import { LegacyRedirect } from '@/components/shell/LegacyRedirect'

// Moved to a top-level route (upgrade §1). Old link redirects.
export default function LegacyScriptTemplatesSettingsPage() {
  return <LegacyRedirect to="/script-templates" />
}
