import type { Metadata, Viewport } from 'next'
import '@fontsource-variable/fraunces/opsz.css'
import '@fontsource-variable/schibsted-grotesk/index.css'
import '@fontsource-variable/geist/index.css'
import '@fontsource-variable/nunito/index.css'
import './globals.css'
import { AppProvider } from '@/lib/AppContext'
import { EntityProvider } from '@/lib/EntityContext'
import { Shell } from '@/components/shell/Shell'
import { PREPAINT_SCRIPT } from '@/lib/themeScript'

export const metadata: Metadata = {
  title: 'ChatBranch',
  description: 'ChatBranch — multi-channel conversation platform',
}

// Mobile-first: render at device width (no more desktop-width zoom-out on
// phones/tablets). `viewport-fit=cover` lets full-bleed panels reach the edges
// on notched devices.
export const viewport: Viewport = {
  width: 'device-width',
  initialScale: 1,
  viewportFit: 'cover',
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
        <EntityProvider>
          <AppProvider>
            <Shell>{children}</Shell>
          </AppProvider>
        </EntityProvider>
      </body>
    </html>
  )
}
