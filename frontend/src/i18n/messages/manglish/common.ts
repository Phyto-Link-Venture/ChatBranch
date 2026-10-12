// Manglish overlay for the `common` namespace (see ./index.ts): only the strings it
// rewrites — every other key falls back to English.
import type { Messages } from '../en'
import type { Overlay } from '../../types'

export const common: Overlay<Messages['common']> = {
  advanced: {
    hint: 'Optional one — the defaults are fine for most businesses.',
  },
  status: {
    loading: 'Loading, wait ah…',
    saved: 'Saved liao',
    noResults: 'Nothing found leh',
    empty: 'Nothing here yet leh',
  },
  errors: {
    generic: 'Aiyo, something went wrong. Try again can?',
    network: 'Cannot reach the API leh — is it running or not?',
    forbidden: 'Sorry ah, your account cannot do that one.',
    notFound: 'Cannot find this one leh.',
    sessionExpired: 'Your session expired liao — bringing you back to login…',
  },
  crash: {
    title: 'Alamak, this page got problem',
    body: 'Something on this page didn’t load properly. You can try again — the rest of the app is still working fine.',
    copied: 'Copied — send it to your administrator ya',
    updating: 'New version just came out — reloading, wait ah…',
    widget: 'This part of the page cannot load leh.',
  },
}
