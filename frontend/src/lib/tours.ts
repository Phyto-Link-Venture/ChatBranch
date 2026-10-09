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

export const TOURS: TourDef[] = [
  {
    id: 'shell',
    href: '/analytics',
    group: 'start',
    match: () => false, // started from the manual or the Guide menu, never automatically
    steps: [{ target: 'nav' }, { target: 'search' }, { target: 'entity-switcher' }, { target: 'live-chip' }, { target: 'assistant' }, { target: 'guide' }],
  },
  {
    id: 'analytics',
    href: '/analytics',
    group: 'start',
    match: exact('/analytics'),
    steps: [{ target: 'page-header' }, { target: 'analytics-range' }, { target: 'analytics-kpis' }, {}],
  },
  {
    id: 'chats',
    href: '/chats',
    group: 'work',
    match: exact('/chats'),
    steps: [
      { target: 'chats-search' },
      { target: 'chats-tabs' },
      { target: 'chats-list' },
      { target: 'chats-ai' },
      { target: 'chats-composer' },
      { target: 'chats-info' },
    ],
  },
  {
    id: 'contacts',
    href: '/contacts',
    group: 'work',
    match: exact('/contacts'),
    steps: [{ target: 'page-header' }, { target: 'contacts-actions' }, { target: 'contacts-list' }],
  },
  {
    id: 'contact',
    href: '/contacts',
    group: 'work',
    match: (p) => /^\/contacts\/\d+$/.test(p),
    steps: [{ target: 'contact-header' }, { target: 'contact-summary' }, { target: 'contact-comms' }, { target: 'contact-ai' }, { target: 'contact-channels' }],
  },
  {
    id: 'journeys',
    href: '/journeys',
    group: 'work',
    match: prefix('/journeys'),
    steps: [{}, { target: 'journeys-list' }, { target: 'journeys-canvas' }, {}],
  },
  {
    id: 'aiMonitor',
    href: '/ai-monitor',
    group: 'ai',
    match: exact('/ai-monitor'),
    steps: [{ target: 'ai-master' }, { target: 'ai-monitor-tabs' }, {}, {}],
  },
  {
    id: 'playground',
    href: '/ai-playground',
    group: 'ai',
    match: exact('/ai-playground'),
    steps: [{ target: 'pg-setup' }, { target: 'pg-chat' }, { target: 'pg-inspector' }, {}],
  },
  {
    id: 'persona',
    href: '/ai-persona',
    group: 'ai',
    match: exact('/ai-persona'),
    steps: [{ target: 'persona-card' }, { target: 'rules-card' }, { target: 'preamble-card' }],
  },
  {
    id: 'knowledge',
    href: '/knowledge',
    group: 'ai',
    match: exact('/knowledge'),
    steps: [{ target: 'page-header' }, {}, {}],
  },
  {
    id: 'skills',
    href: '/skills',
    group: 'ai',
    match: exact('/skills'),
    steps: [{ target: 'page-header' }, {}, {}],
  },
  {
    id: 'channels',
    href: '/channels',
    group: 'setup',
    match: exact('/channels'),
    steps: [{ target: 'page-header' }, {}, {}],
  },
  {
    id: 'billing',
    href: '/billing',
    group: 'setup',
    match: exact('/billing'),
    steps: [{ target: 'billing-controls' }, { target: 'billing-statement' }, {}],
  },
  {
    id: 'settings',
    href: '/settings',
    group: 'setup',
    match: prefix('/settings'),
    steps: [{ target: 'settings-nav' }, {}],
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
