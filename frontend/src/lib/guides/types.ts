import type { MessageKey } from '@/i18n'

/**
 * Step-by-step how-to guides — the "baby guide" half of the manual
 * (/help/manual). Where a page TOUR (lib/tours.ts) explains what is on a
 * screen, a GUIDE walks one task end to end: before you start → numbered steps
 * → how you know it worked → tips → if something goes wrong.
 *
 * A guide is data here; its words live in the `manual` namespace:
 *   manual.guides.<id>.title / .summary
 *   manual.guides.<id>.before            (when `before`)
 *   manual.guides.<id>.steps.s<n>.title / .body   (n = 1…steps)
 *   manual.guides.<id>.done
 *   manual.guides.<id>.tips.t<n>         (n = 1…tips)
 *   manual.guides.<id>.trouble           (when `trouble`)
 * Every locale must carry exactly the same keys (a missing one is a type error).
 */
export type GuideGroup = 'start' | 'channels' | 'work' | 'ai' | 'automate' | 'admin'

export interface GuideDef {
  id: string
  group: GuideGroup
  /** Where the task is done — "Open this page" in the manual, and the page whose Guide menu lists it. */
  href: string
  /** A page tour (lib/tours.ts id) that shows the same screen — "Show me" in the manual. */
  tour?: string
  steps: number
  tips: number
  before?: boolean
  trouble?: boolean
  /** Who can do it, in plain words (manual.who.<who>): everyone | admin | superadmin. */
  who?: 'everyone' | 'admin' | 'superadmin'
}

const base = (id: string) => `manual.guides.${id}`
export const guideKey = (id: string, part: 'title' | 'summary' | 'before' | 'done' | 'trouble') =>
  `${base(id)}.${part}` as MessageKey
export const guideStepKey = (id: string, n: number, part: 'title' | 'body') => `${base(id)}.steps.s${n}.${part}` as MessageKey
export const guideTipKey = (id: string, n: number) => `${base(id)}.tips.t${n}` as MessageKey
