import { AppKeysSettings } from '@/components/settings/AppKeysSettings'
import { ProviderKeysSettings } from '@/components/settings/ProviderKeysSettings'

/**
 * System-level credentials, super-admin only: the WhatsApp webhook verify
 * token and the integration Provider keys (Google OAuth client, Shopify /
 * Stripe webhook secrets). The provider keys used to live at the bottom of
 * the Integrations page — a one-time system setup mixed in with the per-user
 * Connect cards.
 */
export default function AppKeysSettingsPage() {
  return (
    <div className="flex flex-col gap-6">
      <AppKeysSettings />
      <div className="border-t border-line" />
      <ProviderKeysSettings />
    </div>
  )
}
