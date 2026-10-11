import type { GuideDef } from './types'

/** How-to guides in the "ai" group — words in i18n/messages/<locale>/manual/ai.ts. */
export const aiGuides: GuideDef[] = [
  { id: 'aiPersona', group: 'ai', href: '/ai-persona', tour: 'persona', steps: 9, tips: 4, before: true, trouble: true, who: 'admin' },
  { id: 'aiKnowledge', group: 'ai', href: '/knowledge', tour: 'knowledge', steps: 10, tips: 4, before: true, trouble: true, who: 'admin' },
  { id: 'aiPlayground', group: 'ai', href: '/ai-playground', tour: 'playground', steps: 9, tips: 3, before: true, trouble: true, who: 'everyone' },
  { id: 'aiTurnOn', group: 'ai', href: '/ai-monitor', tour: 'aiMonitor', steps: 9, tips: 4, before: true, trouble: true, who: 'admin' },
  { id: 'aiReview', group: 'ai', href: '/ai-monitor?tab=review', tour: 'aiMonitor', steps: 10, tips: 4, before: true, trouble: true, who: 'everyone' },
  { id: 'aiTeach', group: 'ai', href: '/ai-monitor?tab=learning', tour: 'aiMonitor', steps: 9, tips: 3, before: true, trouble: true, who: 'everyone' },
  { id: 'aiReviewer', group: 'ai', href: '/ai-monitor?tab=ai-review', tour: 'aiMonitor', steps: 7, tips: 3, before: true, trouble: true, who: 'admin' },
  { id: 'aiSkills', group: 'ai', href: '/skills', tour: 'skills', steps: 9, tips: 4, before: true, trouble: true, who: 'admin' },
  { id: 'aiCalendar', group: 'ai', href: '/skills', tour: 'skills', steps: 11, tips: 3, before: true, trouble: true, who: 'admin' },
  { id: 'aiReadySkills', group: 'ai', href: '/skills', tour: 'skills', steps: 9, tips: 5, before: true, trouble: true, who: 'admin' },
  { id: 'aiHumanLike', group: 'ai', href: '/ai-monitor?tab=settings', tour: 'aiMonitor', steps: 10, tips: 4, before: true, trouble: true, who: 'admin' },
  { id: 'aiKeys', group: 'ai', href: '/settings/ai-providers', tour: 'settings', steps: 8, tips: 4, before: true, trouble: true, who: 'superadmin' },
  { id: 'aiVoice', group: 'ai', href: '/persona', steps: 8, tips: 3, before: true, trouble: true, who: 'admin' },
]
