// Manglish — the dashboard in friendly Malaysian English. An OVERLAY on the
// English source (Overlay<T>): each namespace file rewrites only the strings
// where the voice matters (help text, notices, empty states, confirmations,
// errors); anything it leaves out — and every new key — reads in English.
// Rules (GLOSSARY.md "Manglish"): labels people click or the manual quotes stay
// as in English, words that reach customers or the AI stay as in English,
// {placeholders} and <tags> exactly as in English, plural pairs together.
import type { Messages } from '../en'
import type { Overlay } from '../../types'
import { common } from './common'
import { shell } from './shell'
import { ui } from './ui'
import { auth } from './auth'
import { settings } from './settings'
import { admin } from './admin'
import { chats } from './chats'
import { bubble } from './bubble'
import { contacts } from './contacts'
import { bookings } from './bookings'
import { flows } from './flows'
import { library } from './library'
import { voice } from './voice'
import { ai } from './ai'
import { channels } from './channels'
import { analytics } from './analytics'
import { review } from './review'
import { notifications } from './notifications'
import { assistant } from './assistant'
import { help } from './help'
import { testlab } from './testlab'
import { playground } from './playground'
import { aiTasks } from './aiTasks'
import { readySkills } from './readySkills'
import { docLayout } from './docLayout'
import { billing } from './billing'
import { guide } from './guide'
import { account } from './account'

export const manglish: Overlay<Messages> = {
  common,
  shell,
  ui,
  auth,
  settings,
  admin,
  chats,
  bubble,
  contacts,
  bookings,
  flows,
  library,
  voice,
  ai,
  channels,
  analytics,
  review,
  notifications,
  assistant,
  help,
  testlab,
  playground,
  aiTasks,
  readySkills,
  docLayout,
  billing,
  guide,
  account,
}
