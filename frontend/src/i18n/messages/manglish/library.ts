// Manglish overlay for the `library` namespace (see ./index.ts): only the strings it
// rewrites — every other key falls back to English.
import type { Messages } from '../en'
import type { Overlay } from '../../types'

export const library: Overlay<Messages['library']> = {}
