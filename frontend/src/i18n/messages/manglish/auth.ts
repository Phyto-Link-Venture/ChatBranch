// Manglish overlay for the `auth` namespace (see ./index.ts): only the strings it
// rewrites — every other key falls back to English.
import type { Messages } from '../en'
import type { Overlay } from '../../types'

export const auth: Overlay<Messages['auth']> = {
  login: {
    title: 'Eh, welcome back!',
    subtitle: 'Sign in and carry on with your conversations where you left off.',
    expired: 'Your session expired liao — sign in again please.',
    provisioned: 'Accounts are given out by your administrator — cannot sign in? Ask them lah.',
    tagline: 'One inbox for WhatsApp, email, social, web chat and voice — no more jumping between apps.',
    invalid: 'Username and password don’t match leh.',
    locked_one: 'Too many wrong tries already. Wait {count} minute, then try again.',
    locked_other: 'Too many wrong tries already. Wait {count} minutes, then try again.',
    rateLimited: 'Too many sign-in tries from this network. Rest a while, then try again.',
    passwordSet: 'Your password is set liao — sign in with your username and the new password.',
  },
  twoFactor: {
    title: 'Key in your code',
    subtitle: 'Open your authenticator app and key in the 6-digit code it shows for {issuer}.',
    invalid: 'That code not correct leh. Check that your phone’s clock is right, then try the newest code.',
    expired: 'This sign-in expired already — key in your password again.',
  },
  setPassword: {
    subtitle: 'For the username {username}. You’ll sign in with that and this password.',
    rules: {
      notCommon: 'Not a password everybody knows',
    },
    validUntil: 'This link can only be used once, until {date}.',
    checking: 'Checking your link, wait ah…',
    invalidTitle: 'Alamak, this link cannot be used any more',
    invalidBody:
      'Maybe it was used already, replaced by a newer one, or expired. Ask your administrator to send you a new one.',
    mismatch: 'The two passwords are not the same leh.',
    rateLimited: 'Too many tries from this network. Rest a while, then try again.',
  },
  setup: {
    passwordIntro:
      'This account still has the password it came installed with — anyone who read the installation guide also knows it. Choose your own first before you continue.',
    twoFactorIntro:
      'Your organization wants a code from an authenticator app at every sign-in. Set it up now — one minute only.',
  },
}
