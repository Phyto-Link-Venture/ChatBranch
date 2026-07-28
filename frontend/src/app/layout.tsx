import type { Metadata } from 'next'
import { Plus_Jakarta_Sans } from 'next/font/google'
import './globals.css'
import { AppProvider } from '@/lib/AppContext'
import { EntityProvider } from '@/lib/EntityContext'
import { Shell } from '@/components/shell/Shell'

const jakarta = Plus_Jakarta_Sans({
  subsets: ['latin'],
  weight: ['300', '400', '500', '600', '700', '800'],
  variable: '--font-jakarta',
  display: 'swap',
})

export const metadata: Metadata = {
  title: 'ChatBranch',
  description: 'ChatBranch — multi-channel conversation platform',
}

export default function RootLayout({
  children,
}: {
  children: React.ReactNode
}) {
  return (
    <html lang="en" className="dark" suppressHydrationWarning>
      <head>
        {/* Set the theme class before first paint so there's no flash. Class
            strategy (html.dark / html.light) — matches Tailwind's
            darkMode:'class'. Reads the persisted choice; defaults to dark
            (the navy design is the showcase look). suppressHydrationWarning
            on <html>: the script legitimately swaps the class before React
            hydrates when a 'light' choice is stored. */}
        <script
          dangerouslySetInnerHTML={{
            __html: `(function(){try{var t=localStorage.getItem('theme');var c=document.documentElement.classList;if(t==='light'){c.remove('dark');c.add('light');}else{c.remove('light');c.add('dark');}}catch(e){}})();`,
          }}
        />
      </head>
      <body className={`${jakarta.variable} font-sans`}>
        <EntityProvider>
          <AppProvider>
            <Shell>{children}</Shell>
          </AppProvider>
        </EntityProvider>
      </body>
    </html>
  )
}
