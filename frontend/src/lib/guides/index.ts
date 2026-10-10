import { startGuides } from './start'
import { channelsGuides } from './channels'
import { workGuides } from './work'
import { aiGuides } from './ai'
import { automateGuides } from './automate'
import { adminGuides } from './admin'
import type { GuideDef, GuideGroup } from './types'

export * from './types'
export { FAQ_IDS, GLOSSARY_IDS } from './help'

export const GUIDE_GROUPS: GuideGroup[] = ['start', 'channels', 'work', 'ai', 'automate', 'admin']

export const GUIDES: GuideDef[] = [
  ...startGuides,
  ...channelsGuides,
  ...workGuides,
  ...aiGuides,
  ...automateGuides,
  ...adminGuides,
]

export const guideById = (id: string) => GUIDES.find((g) => g.id === id)

/** Guides done on this page (the Guide menu lists them). */
export const guidesFor = (path: string) =>
  GUIDES.filter((g) => {
    const h = g.href.split(/[?#]/)[0]
    return h === path || (h !== '/' && path.startsWith(h + '/'))
  })

/**
 * The first-day checklist at the top of the manual, in order. Ids that no
 * guide defines are skipped, so the list can be written before every guide exists.
 */
export const QUICK_START = [
  'firstSignIn',
  'connectFirstChannel',
  'aiPersona',
  'aiKnowledge',
  'aiPlayground',
  'aiTurnOn',
  'replyToChat',
  'inviteTeammate',
]
