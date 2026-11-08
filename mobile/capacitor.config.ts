import type { CapacitorConfig } from '@capacitor/cli'

/**
 * The ChatBranch phone app.
 *
 * The dashboard is a Next.js SERVER application (`output: 'standalone'`, every
 * page client-fetched), so there is no static bundle to ship inside the app and
 * no honest way to make one: `/contacts/[id]` and friends cannot be exported
 * without knowing every id. The app therefore LOADS the deployment
 * (`server.url`) and adds what a browser cannot give:
 *
 *   * notifications when the app is closed (Firebase → APNs / FCM, registered
 *     by the dashboard through Capacitor's plugin registry — see
 *     `frontend/src/lib/push.ts`),
 *   * a native splash screen and status bar,
 *   * the Android back button and notification taps routed inside the app
 *     (`frontend/src/components/shell/NativeBridge.tsx`).
 *
 * `CHATBRANCH_URL` is the deployment the app opens. It MUST be https: a store
 * build with cleartext allowed would ship a downgrade attack, and both stores
 * ask about it. Set it per build:
 *
 *   CHATBRANCH_URL=https://app.yourdomain.com npm run sync
 *
 * `www/` is only the offline card — Capacitor needs a local web root, and that
 * page is what shows when the deployment cannot be reached.
 */

const url = process.env.CHATBRANCH_URL?.trim() || ''

if (url && !url.startsWith('https://')) {
  throw new Error(
    `CHATBRANCH_URL must be an https:// address (got "${url}"). A store build must not load the dashboard over cleartext.`,
  )
}

const config: CapacitorConfig = {
  // Change this to your own reverse-DNS id before the first store submission —
  // it is permanent for the life of the listing.
  appId: process.env.APP_ID?.trim() || 'com.chatbranch.app',
  appName: process.env.APP_NAME?.trim() || 'ChatBranch',
  webDir: 'www',
  // No cleartext anywhere: the dashboard is https and so is the API.
  server: url
    ? { url, cleartext: false, androidScheme: 'https' }
    : { androidScheme: 'https' },
  ios: {
    // the webview's own background while a page paints, so a theme switch
    // never flashes white on a dark phone
    backgroundColor: '#FAF7F2',
    // The dashboard already renders with `viewport-fit=cover` and pads for the
    // notch and home bar itself (`env(safe-area-inset-*)`, e.g. the bottom tab
    // bar), so WebKit must NOT add its own inset on top — that pads twice.
    contentInset: 'never',
  },
  android: {
    backgroundColor: '#FAF7F2',
    // a release build must not trust a debug proxy's certificates
    allowMixedContent: false,
  },
  plugins: {
    PushNotifications: {
      // the app asks for permission itself, from Settings → My notifications
      presentationOptions: ['badge', 'sound', 'alert'],
    },
    SplashScreen: {
      // The dashboard hides it as soon as the shell has painted (NativeBridge),
      // which is sooner than any timer. The duration is only the safety net: a
      // phone that cannot reach the deployment shows the offline card, which has
      // no dashboard to hide the splash — without this it would stare at the
      // splash for ever.
      launchAutoHide: true,
      launchShowDuration: 3000,
      backgroundColor: '#FAF7F2',
      androidSplashResourceName: 'splash',
      showSpinner: false,
    },
  },
}

export default config
