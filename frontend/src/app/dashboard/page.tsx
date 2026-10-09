import { LegacyRedirect } from '@/components/shell/LegacyRedirect'

/** The Dashboard was folded into Analytics (the home page); old links and
 *  bookmarks land here and move on. next.config.js does this on the server;
 *  see app/page.tsx for why this is not a render-time `redirect()`. */
export default function DashboardRedirect() {
  return <LegacyRedirect to="/analytics" />
}
