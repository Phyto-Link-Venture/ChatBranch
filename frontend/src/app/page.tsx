import { LegacyRedirect } from '@/components/shell/LegacyRedirect'

/** The Dashboard is the home page. next.config.js redirects `/` on the server;
 *  this client fallback only runs if that is ever bypassed. A render-time
 *  `redirect()` must not be used here: RouteGuard mounts pages only after the
 *  session is read on the client, and a redirect thrown during that client
 *  render crashes the App Router ("Rendered more hooks…"). */
export default function Home() {
  return <LegacyRedirect to="/dashboard" />
}
