import { LegacyRedirect } from '@/components/shell/LegacyRedirect'

/** The home page used to be called Analytics; it is the Dashboard now. Old
 *  links and bookmarks land here and move on. next.config.js does this on the
 *  server; see app/page.tsx for why this is not a render-time `redirect()`. */
export default function AnalyticsRedirect() {
  return <LegacyRedirect to="/dashboard" />
}
