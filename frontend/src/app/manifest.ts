import type { MetadataRoute } from 'next'

/** Web app manifest (served at /manifest.webmanifest, linked automatically).
 *  Colours are Paper & Ink's paper; icons are rendered from the Logo mark
 *  (components/shell/Logo.tsx → public/icons/). */
export default function manifest(): MetadataRoute.Manifest {
  return {
    id: '/',
    name: 'ChatBranch',
    short_name: 'ChatBranch',
    description: 'One inbox for WhatsApp, email, social, web chat and voice — with AI that helps.',
    start_url: '/analytics',
    scope: '/',
    display: 'standalone',
    orientation: 'any',
    background_color: '#FAF7F2',
    theme_color: '#FAF7F2',
    categories: ['business', 'productivity'],
    icons: [
      { src: '/icons/icon-192.png', sizes: '192x192', type: 'image/png', purpose: 'any' },
      { src: '/icons/icon-512.png', sizes: '512x512', type: 'image/png', purpose: 'any' },
      { src: '/icons/maskable-512.png', sizes: '512x512', type: 'image/png', purpose: 'maskable' },
    ],
    shortcuts: [
      {
        name: 'Chats',
        short_name: 'Chats',
        description: 'Open the inbox',
        url: '/chats',
        icons: [{ src: '/icons/icon-192.png', sizes: '192x192', type: 'image/png' }],
      },
      {
        name: 'Contacts',
        short_name: 'Contacts',
        description: 'Find a customer',
        url: '/contacts',
        icons: [{ src: '/icons/icon-192.png', sizes: '192x192', type: 'image/png' }],
      },
    ],
  }
}
