/**
 * Sign-in emails on the dashboard side: Settings → System email
 * (/api/settings/system-email, app_keys.manage) and the shapes the Users page
 * reads about welcome emails (/api/v1/users `invites`, a user's `invite_link`).
 * The server is the control — these only call it.
 */
import { securityApi } from './security'

export type SecurityMode = 'starttls' | 'ssl' | 'none'
export type EmailSource = 'system' | 'default' | 'none'
/** Why a welcome email can't go out right now. */
export type InviteProblem = 'email_not_configured' | 'no_login_address' | 'localhost_login_address'

/** Can a welcome email go out right now (System email / default mail + a login address)? */
export interface InviteReadiness {
  ready: boolean
  problem: InviteProblem | null
  detail: string | null
  email_source: EmailSource
  login_url: string | null
  link_hours: number
}

export interface SystemEmailSettings {
  from_name: string
  from_email: string
  host: string
  port: number
  security: SecurityMode
  username: string
  password_masked: string
  has_password: boolean
  configured: boolean
  security_modes: SecurityMode[]
  source: EmailSource
  default_mail: { configured: boolean; from: string }
  invites: InviteReadiness
  my_email: string
}

export interface SystemEmailWrite {
  from_name: string
  from_email: string
  host: string
  port: number
  security: SecurityMode
  username: string
  /** A value containing "••" (the masked one sent back) keeps the stored password. */
  password: string
}

export const systemEmail = {
  get: () => securityApi<SystemEmailSettings>('settings/system-email'),
  save: (body: SystemEmailWrite) =>
    securityApi<SystemEmailSettings>('settings/system-email', { method: 'PUT', body: JSON.stringify(body) }),
  test: () =>
    securityApi<{ ok: boolean; to: string; source: EmailSource }>('settings/system-email/test', {
      method: 'POST',
      body: '{}',
    }),
}

/** A set-password link the person hasn't used yet (Users list). */
export interface InviteLink {
  status: 'pending' | 'expired'
  purpose: 'invite' | 'reset'
  sent_at: string
  expires_at: string
  sent_by: string
}

/** The `invite` outcome on a create / resend answer. */
export interface InviteOutcome {
  requested: boolean
  sent: boolean
  purpose?: 'invite' | 'reset'
  to?: string | null
  expires_at?: string | null
  code?: string | null
  error?: string | null
}
