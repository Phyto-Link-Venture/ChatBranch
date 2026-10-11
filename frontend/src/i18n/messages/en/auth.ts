// English source for the `auth` namespace (owner: channels-review): the sign-in
// page. Other locales mirror it key for key.
export const auth = {
  login: {
    eyebrow: 'Workspace',
    title: 'Welcome back',
    subtitle: 'Sign in to pick up your conversations.',
    username: 'Username',
    password: 'Password',
    showPassword: 'Show password',
    hidePassword: 'Hide password',
    expired: 'Your session expired — please sign in again.',
    signIn: 'Sign in',
    provisioned: "Accounts are provisioned by your administrator — ask them if you can't sign in.",
    tagline: 'One inbox for WhatsApp, email, social, web chat and voice.',
    invalid: 'That username and password don’t match.',
    locked_one: 'Too many failed attempts. Try again in {count} minute.',
    locked_other: 'Too many failed attempts. Try again in {count} minutes.',
    rateLimited: 'Too many sign-in attempts from this network. Wait a moment and try again.',
  },
  twoFactor: {
    eyebrow: 'Two-step sign-in',
    title: 'Enter your code',
    subtitle: 'Open your authenticator app and enter the 6-digit code it shows for {issuer}.',
    code: 'Code from the app',
    recovery: 'Recovery code',
    verify: 'Verify',
    useRecovery: 'Lost your phone? Use a recovery code',
    useApp: 'Use a code from the app instead',
    invalid: 'That code isn’t right. Check that your phone’s clock is correct and try the newest code.',
    expired: 'This sign-in has expired — enter your password again.',
    back: 'Back to sign-in',
  },
  setup: {
    eyebrow: 'Account setup',
    passwordTitle: 'Choose your own password',
    passwordIntro:
      'This account still has the password it was installed with, which anyone who has read the installation guide knows. Choose your own before you continue.',
    twoFactorTitle: 'Set up two-step sign-in',
    twoFactorIntro:
      'Your organization asks for a code from an authenticator app at every sign-in. Set it up now — it takes a minute.',
    signOut: 'Sign out',
    continue: 'Continue to ChatBranch',
  },
} as const
