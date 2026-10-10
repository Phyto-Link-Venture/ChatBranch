/**
 * Page guides — the step-by-step tours the "Guide" button replays and the
 * public manual (/help/manual) prints as chapters. ONE list feeds both, so the
 * manual and the in-app guide never drift apart.
 *
 * A step points at an element carrying `data-tour="<target>"`; a step whose
 * target is missing on screen (a role that can't see that card, an empty
 * state) is skipped, and a step with no target shows as a centred card.
 * Words live in the `guide` message namespace, by convention:
 *   guide.tours.<id>.title / .intro
 *   guide.tours.<id>.steps.s<n>.title / .body   (n from 1)
 */

import type { MessageKey } from '@/i18n'

export interface TourStep {
  /** `data-tour` value of the element to spotlight; none = a centred card. */
  target?: string
}

export interface TourDef {
  id: string
  /** The page the tour runs on (the manual's "Show me" opens it). */
  href: string
  /** Does this tour belong to the current path? */
  match: (path: string) => boolean
  steps: TourStep[]
  /** Manual chapter group. */
  group: 'start' | 'work' | 'ai' | 'setup'
}

const exact = (p: string) => (path: string) => path === p
const prefix = (p: string) => (path: string) => path === p || path.startsWith(p + '/')

/** Steps from a list of targets; '' = a centred card. */
const steps = (...targets: string[]): TourStep[] => targets.map((t) => (t ? { target: t } : {}))

/** Settings pages that carry a tour of their own (the generic `settings` tour covers the rest). */
const SETTINGS_TOURS = ['account', 'appearance', 'connections', 'ai-providers', 'billing']

// Order matters: `tourFor` picks the FIRST match, so specific tours sit before
// any generic prefix one (the settings sub-pages before `settings`).
export const TOURS: TourDef[] = [
  {
    id: 'shell',
    href: '/dashboard',
    group: 'start',
    match: () => false, // started from the manual or the Guide menu, never automatically
    steps: steps('nav', 'search', 'entity-switcher', 'live-chip', 'assistant', 'guide'),
  },
  {
    id: 'analytics',
    href: '/dashboard',
    group: 'start',
    match: exact('/dashboard'),
    steps: steps('page-header', 'analytics-range', 'analytics-kpis', ''),
  },
  {
    id: 'notifications',
    href: '/notifications',
    group: 'start',
    match: exact('/notifications'),
    steps: steps('page-header', 'notif-categories', 'notif-toolbar', 'notif-list', 'notif-actions', 'notif-settings'),
  },
  {
    id: 'appearance',
    href: '/settings/appearance',
    group: 'start',
    match: exact('/settings/appearance'),
    steps: steps(
      'settings-page-header',
      'appearance-language',
      'appearance-preview',
      'appearance-theme',
      'appearance-mode',
      'appearance-size',
      'appearance-advanced',
      'appearance-footer',
    ),
  },
  {
    id: 'chats',
    href: '/chats',
    group: 'work',
    match: exact('/chats'),
    steps: steps('chats-search', 'chats-filter', 'chats-tabs', 'chats-list', 'chats-ai', 'chats-handover', 'chats-composer', 'chats-info'),
  },
  {
    id: 'contacts',
    href: '/contacts',
    group: 'work',
    match: exact('/contacts'),
    steps: steps('page-header', 'contacts-import', 'contacts-add', 'contacts-filters', 'contacts-list', 'contacts-row-actions'),
  },
  {
    id: 'contact',
    href: '/contacts',
    group: 'work',
    match: (p) => /^\/contacts\/\d+$/.test(p),
    steps: steps('contact-header', 'contact-summary', 'contact-comms', 'contact-ai', 'contact-channels'),
  },
  {
    id: 'bookings',
    href: '/bookings',
    group: 'work',
    match: exact('/bookings'),
    steps: steps('page-header', 'bookings-tabs', 'bookings-table', 'bookings-refresh', ''),
  },
  {
    id: 'assignments',
    href: '/assignments',
    group: 'work',
    match: exact('/assignments'),
    steps: steps('page-header', 'assign-toolbar', 'assign-accounts', 'assign-rebalance', 'routing-rules', 'routing-save'),
  },
  {
    id: 'journeys',
    href: '/journeys',
    group: 'work',
    match: prefix('/journeys'),
    steps: steps('', 'journeys-new', 'journeys-list', 'journeys-view', 'journeys-canvas', 'journeys-live', 'journeys-runs'),
  },
  {
    id: 'automations',
    href: '/automations',
    group: 'work',
    match: exact('/automations'),
    steps: steps('page-header', 'automations-intro', 'automations-add', 'automations-list', 'automations-rule-actions', ''),
  },
  {
    id: 'templates',
    href: '/templates',
    group: 'work',
    match: exact('/templates'),
    steps: steps('page-header', 'templates-tabs', 'templates-new', 'templates-panel', ''),
  },
  {
    id: 'tags',
    href: '/tags',
    group: 'work',
    match: exact('/tags'),
    steps: steps('page-header', 'tags-kinds', 'tags-new', 'tags-section', 'tags-row-actions'),
  },
  {
    id: 'callLogs',
    href: '/call-logs',
    group: 'work',
    match: exact('/call-logs'),
    steps: steps('calllogs-tabs', 'calllogs-list', 'calllogs-row', ''),
  },
  {
    id: 'aiMonitor',
    href: '/ai-monitor',
    group: 'ai',
    match: exact('/ai-monitor'),
    steps: steps(
      'ai-master',
      'ai-tab-review',
      'ai-tab-activity',
      'ai-tab-learning',
      'ai-tab-ai-review',
      'ai-tab-stats',
      'ai-tab-settings',
      'ai-playground-link',
    ),
  },
  {
    id: 'playground',
    href: '/ai-playground',
    group: 'ai',
    match: exact('/ai-playground'),
    steps: steps('pg-setup', 'pg-chat', 'pg-inspector', ''),
  },
  {
    id: 'persona',
    href: '/ai-persona',
    group: 'ai',
    match: exact('/ai-persona'),
    steps: steps('persona-card', 'rules-card', 'preamble-card'),
  },
  {
    id: 'knowledge',
    href: '/knowledge',
    group: 'ai',
    match: exact('/knowledge'),
    steps: steps('page-header', 'knowledge-add', 'knowledge-kinds', 'knowledge-list', 'knowledge-test'),
  },
  {
    id: 'skills',
    href: '/skills',
    group: 'ai',
    match: exact('/skills'),
    steps: steps('page-header', 'skills-new', 'skills-builtins', 'skills-prompt', 'skills-app', 'skills-tool', 'skills-check', 'skills-row-actions'),
  },
  {
    id: 'voice',
    href: '/persona',
    group: 'ai',
    match: exact('/persona'),
    steps: steps('voice-intro', 'voice-personas', 'voice-add', 'voice-filters', 'voice-grid', 'voice-pronunciation'),
  },
  {
    id: 'aiProviders',
    href: '/settings/ai-providers',
    group: 'ai',
    match: exact('/settings/ai-providers'),
    steps: steps(
      'settings-page-header',
      'aiproviders-status',
      'aiproviders-providers',
      'aiproviders-source',
      'aiproviders-advanced',
      'settings-masthead',
    ),
  },
  {
    id: 'channels',
    href: '/channels',
    group: 'setup',
    match: exact('/channels'),
    steps: steps(
      'page-header',
      'channels-add',
      'channels-connected',
      'channels-filter',
      'channels-card-actions',
      'channels-catalog',
      'channels-advanced',
    ),
  },
  {
    id: 'integrations',
    href: '/integrations',
    group: 'setup',
    match: exact('/integrations'),
    steps: steps('page-header', 'integrations-reach', 'integrations-providers', 'integrations-connect', 'integrations-accounts'),
  },
  {
    id: 'account',
    href: '/settings/account',
    group: 'start',
    match: exact('/settings/account'),
    steps: steps('settings-page-header', 'account-profile', 'account-password', 'account-sessions'),
  },
  {
    id: 'connections',
    href: '/settings/connections',
    group: 'setup',
    match: exact('/settings/connections'),
    steps: steps('settings-page-header', 'connections-card', 'connections-list', 'connections-actions', 'connections-order'),
  },
  {
    id: 'users',
    href: '/users',
    group: 'setup',
    match: exact('/users'),
    steps: steps('page-header', 'users-roles', 'users-add', 'users-table', 'users-row-actions'),
  },
  {
    id: 'teams',
    href: '/teams',
    group: 'setup',
    match: exact('/teams'),
    steps: steps('page-header', 'teams-intro', 'teams-add', 'teams-table', 'teams-row-actions'),
  },
  {
    id: 'entities',
    href: '/entities',
    group: 'setup',
    match: exact('/entities'),
    steps: steps('page-header', 'entities-intro', 'entities-counts', 'entities-add', 'entities-table'),
  },
  {
    id: 'bindings',
    href: '/bindings',
    group: 'setup',
    match: exact('/bindings'),
    steps: steps('page-header', 'bindings-kind', 'bindings-target', 'bindings-members', ''),
  },
  {
    id: 'permissions',
    href: '/permissions',
    group: 'setup',
    match: exact('/permissions'),
    steps: steps(
      'permissions-header',
      'permissions-roles',
      'permissions-add',
      'permissions-manage',
      'permissions-grid',
      'permissions-footnote',
    ),
  },
  {
    id: 'developers',
    href: '/developers',
    group: 'setup',
    match: exact('/developers'),
    steps: steps('page-header', 'dev-keys', 'dev-create-key', 'dev-connect', 'dev-mcp'),
  },
  {
    id: 'billing',
    href: '/billing',
    group: 'setup',
    match: exact('/billing'),
    steps: steps('billing-controls', 'billing-statement', '', 'billing-quotas'),
  },
  {
    id: 'billingSettings',
    href: '/settings/billing',
    group: 'setup',
    match: exact('/settings/billing'),
    steps: steps('settings-page-header', 'billingset-defaults', 'billingset-example', 'billingset-companies', 'billingset-save', 'billingset-locked'),
  },
  {
    id: 'activity',
    href: '/activity',
    group: 'setup',
    match: exact('/activity'),
    steps: steps('page-header', 'activity-range', 'activity-filters', 'activity-entries', 'activity-export'),
  },
  {
    id: 'testLab',
    href: '/test-lab',
    group: 'setup',
    match: exact('/test-lab'),
    steps: steps('page-header', 'testlab-run', 'testlab-scenarios', 'testlab-history', 'testlab-demo', 'testlab-reset'),
  },
  {
    // every other settings page (notification defaults, custom fields, keys…)
    id: 'settings',
    href: '/settings',
    group: 'setup',
    match: (p) => prefix('/settings')(p) && !SETTINGS_TOURS.some((id) => prefix(`/settings/${id}`)(p)),
    steps: steps('settings-nav', 'settings-page-header', 'settings-page', 'settings-masthead', ''),
  },
]

export const tourFor = (path: string): TourDef | undefined => TOURS.find((t) => t.match(path))
export const tourById = (id: string): TourDef | undefined => TOURS.find((t) => t.id === id)

export const tourKey = (id: string, part: 'title' | 'intro') => `guide.tours.${id}.${part}` as MessageKey
export const stepKey = (id: string, n: number, part: 'title' | 'body') =>
  `guide.tours.${id}.steps.s${n}.${part}` as MessageKey

// ---- "seen" memory: a per-viewer convenience (never relied on) -------------

const seenKey = (user: string, id: string) => `tour-seen:${user}:${id}`

export function tourSeen(user: string, id: string): boolean {
  try {
    return localStorage.getItem(seenKey(user, id)) === '1'
  } catch {
    return true // storage blocked: never auto-start, the Guide button still works
  }
}

export function markTourSeen(user: string, id: string) {
  try {
    localStorage.setItem(seenKey(user, id), '1')
  } catch {
    /* storage blocked */
  }
}

/** Whether guides start by themselves on a first visit — the Guide menu toggles it. */
const autoKey = (user: string) => `tour-auto:${user}`
export function toursAuto(user: string): boolean {
  try {
    return localStorage.getItem(autoKey(user)) !== '0'
  } catch {
    return false
  }
}
export function setToursAuto(user: string, on: boolean) {
  try {
    localStorage.setItem(autoKey(user), on ? '1' : '0')
  } catch {
    /* storage blocked */
  }
}
