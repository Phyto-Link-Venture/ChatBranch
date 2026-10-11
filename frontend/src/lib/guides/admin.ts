import type { GuideDef } from './types'

/** How-to guides in the "admin" group — words in i18n/messages/<locale>/manual/admin.ts. */
export const adminGuides: GuideDef[] = [
  { id: 'inviteTeammate', group: 'admin', href: '/users', steps: 8, tips: 3, before: true, trouble: true, who: 'admin' },
  { id: 'rolesPermissions', group: 'admin', href: '/permissions', steps: 8, tips: 3, before: true, trouble: true, who: 'superadmin' },
  { id: 'entitiesBranches', group: 'admin', href: '/entities', steps: 8, tips: 3, before: true, trouble: true, who: 'admin' },
  { id: 'teams', group: 'admin', href: '/teams', steps: 8, tips: 3, before: true, trouble: true, who: 'admin' },
  { id: 'tags', group: 'admin', href: '/tags', steps: 9, tips: 3, before: true, trouble: true, who: 'admin' },
  { id: 'customFields', group: 'admin', href: '/settings/custom-fields', tour: 'settings', steps: 8, tips: 3, before: true, trouble: true, who: 'admin' },
  { id: 'bindings', group: 'admin', href: '/bindings', steps: 7, tips: 3, before: true, trouble: true, who: 'admin' },
  { id: 'apiKeysMcp', group: 'admin', href: '/developers', steps: 9, tips: 3, before: true, trouble: true, who: 'admin' },
  { id: 'aiBill', group: 'admin', href: '/billing', tour: 'billing', steps: 9, tips: 3, before: true, trouble: true, who: 'admin' },
  { id: 'activityLog', group: 'admin', href: '/activity', steps: 7, tips: 3, before: true, trouble: true, who: 'everyone' },
  { id: 'orgSettings', group: 'admin', href: '/settings/notifications', tour: 'settings', steps: 7, tips: 3, before: true, trouble: true, who: 'admin' },
  { id: 'testLab', group: 'admin', href: '/test-lab', steps: 7, tips: 3, before: true, trouble: true, who: 'superadmin' },
  { id: 'requireTwoStep', group: 'admin', href: '/settings/security', tour: 'settings', steps: 4, tips: 2, before: true, trouble: true, who: 'superadmin' },
]
