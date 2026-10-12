// Manglish overlay for the `guide` namespace (see ./index.ts): only the strings it
// rewrites — every other key falls back to English.
import type { Messages } from '../en'
import type { Overlay } from '../../types'

export const guide: Overlay<Messages['guide']> = {}
