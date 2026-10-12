// Manglish overlay for the `help` namespace (see ./index.ts): only the strings it
// rewrites — every other key falls back to English.
import type { Messages } from '../en'
import type { Overlay } from '../../types'

export const help: Overlay<Messages['help']> = {
  subtitle: 'Answers to the questions people always ask us.',
  unavailable: 'Sorry ah, the help center is down for a while. Try again in a bit?',
  noMatch: 'Nothing matched “{q}” leh.',
  empty: 'No articles published yet.',
  missing: 'That article doesn’t exist leh (or it’s not published).',
}
