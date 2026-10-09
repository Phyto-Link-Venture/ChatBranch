import type { Messages } from '../en'
import type { Dict } from '../../types'
import { common } from './common'
import { shell } from './shell'
import { ui } from './ui'
import { auth } from './auth'
import { settings } from './settings'
import { admin } from './admin'
import { chats } from './chats'
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
import { billing } from './billing'
import { guide } from './guide'

export const zh: Dict<Messages> = {
  common,
  shell,
  ui,
  auth,
  settings,
  admin,
  chats,
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
  billing,
  guide,
}
