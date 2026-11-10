import { AppKeysSettings } from '@/components/settings/AppKeysSettings'
import { ProviderKeysSettings } from '@/components/settings/ProviderKeysSettings'
import { ZernioCard } from '@/components/settings/ZernioCard'

/**
 * System-level credentials, super-admin only: the WhatsApp webhook verify
 * token and the integration Provider keys (Google OAuth client, Shopify /
 * Stripe webhook secrets) and the managed-connections provider (Zernio: the
 * API key, webhook and entity profiles behind the Channels page's "Managed
 * connection" method and the social networks). The provider keys used to live
 * at the bottom of the Integrations (now Apps) page — a one-time system setup
 * mixed in with the per-user Connect cards. `#managed-connections` deep-links
 * the Zernio card (the only place the provider's name is shown).
 */
export default function AppKeysSettingsPage() {
  return (
    <div className="flex flex-col gap-6">
      <AppKeysSettings />
      <div className="border-t border-line" />
      <ProviderKeysSettings />
      <div className="border-t border-line" />
      <div id="managed-connections" className="scroll-mt-4">
        <ZernioCard />
      </div>
    </div>
  )
}
