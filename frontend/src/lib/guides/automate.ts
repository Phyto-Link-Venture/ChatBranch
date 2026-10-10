import type { GuideDef } from './types'

/** How-to guides in the "automate" group — words in i18n/messages/<locale>/manual/automate.ts. */
export const automateGuides: GuideDef[] = [
  { id: 'journeyFromTemplate', group: 'automate', href: '/journeys', tour: 'journeys', steps: 9, tips: 3, before: true, trouble: true, who: 'admin' },
  { id: 'journeyEdit', group: 'automate', href: '/journeys', tour: 'journeys', steps: 11, tips: 4, trouble: true, who: 'admin' },
  { id: 'journeyTriggers', group: 'automate', href: '/journeys', tour: 'journeys', steps: 10, tips: 3, trouble: true, who: 'admin' },
  { id: 'journeyDecisions', group: 'automate', href: '/journeys', tour: 'journeys', steps: 10, tips: 3, trouble: true, who: 'admin' },
  { id: 'journeyRuns', group: 'automate', href: '/journeys', tour: 'journeys', steps: 9, tips: 3, trouble: true, who: 'admin' },
  { id: 'automationRule', group: 'automate', href: '/automations', steps: 11, tips: 4, before: true, trouble: true, who: 'admin' },
  { id: 'accountAssignment', group: 'automate', href: '/assignments', steps: 11, tips: 3, before: true, trouble: true, who: 'admin' },
]
