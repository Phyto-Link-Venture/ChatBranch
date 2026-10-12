// Manglish overlay for the `account` namespace (see ./index.ts): only the strings it
// rewrites — every other key falls back to English.
import type { Messages } from '../en'
import type { Overlay } from '../../types'

export const account: Overlay<Messages['account']> = {
  loadFailed: 'Cannot load your account — is the API running?',
  apiKeyNote: 'You’re signed in with an API key. To change your account, sign in to the dashboard with your password lah.',
  profile: {
    intro: 'Your name is what colleagues see in chats, notes and the sidebar. Your username stays the same forever.',
    usernameHint: 'Used for signing in and in the activity log — this one cannot change.',
    saved: 'Profile saved liao.',
    emailTaken: 'Another user is already using that email address.',
    invalidEmail: 'That one doesn’t look like an email address leh.',
  },
  password: {
    intro: 'Change your password and every other device gets signed out. Don’t worry, this one stays signed in.',
    hint: 'At least {min} characters. A few random words mixed with numbers or symbols — strong, and still easy to remember.',
    required: 'Key in your current password first.',
    wrong: 'Your current password is wrong leh.',
    tooShort: 'The new password must have at least {min} characters.',
    mismatch: 'The two new passwords don’t match — check again.',
    same: 'The new password cannot be the same as the current one.',
    changed_one: 'Password changed liao — {count} other device signed out.',
    changed_other: 'Password changed liao — {count} other devices signed out.',
    tooMany: 'Too many wrong passwords already. Wait a few minutes, then try again.',
    policyHint: 'At least {min} characters, cannot be your username, cannot be a well-known password.',
    common: 'That password is too common — attackers try it first one. Choose something harder to guess.',
    isUsername: 'Password cannot be the same as your username.',
    tooLong: 'That password is too long lah.',
  },
  twoFactor: {
    intro:
      'On top of your password, sign-in will ask for a code from an authenticator app on your phone (Google Authenticator, Microsoft Authenticator, 1Password…), so a stolen password alone is not enough.',
    required: 'Your organization says everyone must use two-step sign-in.',
    manual: 'Cannot scan? Add an account in the app and type in this key:',
    passwordForNewDevice: 'Key in your password to move two-step sign-in to a new phone.',
    recoveryIntro:
      'Each code can sign you in once if you lose your phone. Keep them somewhere safe, like a password manager — they won’t be shown again ah.',
    enabled: 'Two-step sign-in is on liao.',
    disabled: 'Two-step sign-in is off now.',
    codesReplaced: 'New recovery codes made — the old ones cannot be used anymore.',
    newCodesIntro: 'Key in your password and a code from the app (or a recovery code) to make new recovery codes.',
    turnOffIntro: 'Key in your password and a code from the app (or a recovery code) to switch two-step sign-in off.',
    invalidCode: 'That code is not correct leh. Check your phone’s clock is correct, then try the newest code.',
    wrongPassword: 'Wrong password — try again.',
    requiredCantDisable:
      'Your organization requires two-step sign-in, so cannot turn it off. To change phones, use “Move to a new phone”.',
    tooMany: 'Too many failed tries already. Wait a few minutes, then try again.',
    expiredSetup: 'That setup already expired — start again.',
  },
  sessions: {
    intro: 'Every browser or phone you’re signed in on counts as one device.',
    count_one: 'You’re signed in on {count} device only — this one.',
    count_other: 'You’re signed in on {count} devices, this one included.',
    signedOut_one: 'Done, signed out {count} other device.',
    signedOut_other: 'Done, signed out {count} other devices.',
    none: 'No other devices signed in — only this one.',
  },
}
