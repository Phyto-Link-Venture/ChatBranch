import type { Metadata, Viewport } from 'next'
import '@fontsource-variable/fraunces/opsz.css'
import '@fontsource-variable/schibsted-grotesk/index.css'
import '@fontsource-variable/geist/index.css'
import '@fontsource-variable/nunito/index.css'
// Settings → Appearance fonts. @font-face only — a face downloads when
// something renders in it. Noto Sans SC (large) is loaded on demand by lib/theme.ts.
import '@fontsource-variable/inter/index.css'
import '@fontsource-variable/plus-jakarta-sans/index.css'
import '@fontsource-variable/manrope/index.css'
import '@fontsource-variable/dm-sans/index.css'
import '@fontsource-variable/ibm-plex-sans/index.css'
import '@fontsource-variable/space-grotesk/index.css'
import '@fontsource-variable/source-serif-4/index.css'
import '@fontsource-variable/lora/index.css'
import './globals.css'
import { AppProvider } from '@/lib/AppContext'
import { EntityProvider } from '@/lib/EntityContext'
import { I18nProvider } from '@/i18n'
import { Shell } from '@/components/shell/Shell'
import { ServiceWorkerRegistrar } from '@/components/shell/pwa'
import { NativeBridge } from '@/components/shell/NativeBridge'
import { PREPAINT_SCRIPT } from '@/lib/themeScript'

export const metadata: Metadata = {
  title: 'ChatBranch',
  description: 'ChatBranch — multi-channel conversation platform',
  applicationName: 'ChatBranch',
  // app/manifest.ts is linked automatically; these cover the icon tags the
  // manifest doesn't (browser tab, iOS home screen).
  icons: {
    icon: [
      { url: '/icons/favicon.svg', type: 'image/svg+xml' },
      { url: '/icons/favicon-32.png', sizes: '32x32', type: 'image/png' },
      { url: '/icons/favicon-16.png', sizes: '16x16', type: 'image/png' },
    ],
    apple: [{ url: '/icons/apple-touch-icon.png', sizes: '180x180' }],
  },
  appleWebApp: {
    capable: true,
    title: 'ChatBranch',
    statusBarStyle: 'default',
  },
  formatDetection: { telephone: false },
}

// Mobile-first: render at device width. `viewport-fit=cover` lets full-bleed
// panels reach the edges on notched devices. theme-color follows the paper in
// each mode (the shell retints it to the chosen theme at runtime).
export const viewport: Viewport = {
  width: 'device-width',
  initialScale: 1,
  viewportFit: 'cover',
  themeColor: [
    { media: '(prefers-color-scheme: light)', color: '#FAF7F2' },
    { media: '(prefers-color-scheme: dark)', color: '#1C1A17' },
  ],
}

export default function RootLayout({
  children,
}: {
  children: React.ReactNode
}) {
  return (
    <html lang="en" className="theme-paper light" suppressHydrationWarning>
      <head>
        {/* Apply the saved look (theme, mode, accent, text size…) before first
            paint so there's no flash; lib/theme.ts owns the format. The class
            swap is an intentional pre-hydration mutation, hence
            suppressHydrationWarning on <html>. */}
        <script dangerouslySetInnerHTML={{ __html: PREPAINT_SCRIPT }} />
      </head>
      <body className="font-sans">
        <ServiceWorkerRegistrar />
        {/* the phone app's native wiring (splash, notification taps, back
            button, status bar); does nothing in a browser */}
        <NativeBridge />
        <I18nProvider>
          <EntityProvider>
            <AppProvider>
              <Shell>{children}</Shell>
            </AppProvider>
          </EntityProvider>
        </I18nProvider>
      </body>
    </html>
  )
}
