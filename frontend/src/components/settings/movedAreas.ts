/**
 * Areas promoted out of Settings into their own top-level sidebar routes
 * (upgrade §1). Maps a legacy Settings tab id → its new route so old
 * `/settings/<id>` links and `?tab=` deep-links keep resolving.
 */
export const MOVED_AREA_ROUTES: Record<string, string> = {
  channels: '/channels',
  integrations: '/integrations',
  users: '/users',
  entities: '/entities',
  templates: '/templates',
  'script-templates': '/script-templates',
  notif: '/notifications',
  whatsapp: '/channels', // pre-consolidation WhatsApp Accounts tab
  ai: '/channels', // AI Configuration retired — its knobs live on each channel account now
  voice: '/channels', // pre-consolidation Voice Persona tab (→ ai → channels)
}
