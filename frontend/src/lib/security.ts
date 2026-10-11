/**
 * Sign-in security on the dashboard side: the caller's own two-step sign-in
 * (/api/auth/me/2fa*), the organization policy (Settings → Security,
 * /api/settings/security) and the translations of the server's stable refusal
 * codes (password policy, throttling). The server is the control — these only
 * call it and say its answers in the reader's language.
 */
import { API } from '@/lib/apiBase'
import { authHeaders, expireSessionOn401 } from './auth'
import type { TFunction } from '@/i18n'

/** A refusal with the server's stable `code` / `field` (and `min` for the password floor). */
export class SecurityError extends Error {
  constructor(
    message: string,
    public status: number,
    public code?: string,
    public field?: string,
    public min?: number,
    public retryAfter?: number,
  ) {
    super(message)
  }
}

export async function securityApi<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}/${path}`, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...authHeaders(), ...(init?.headers || {}) },
  })
  if (expireSessionOn401(res)) throw new SecurityError('Session expired — redirecting to login…', 401)
  const data = (await res.json().catch(() => ({}))) as {
    detail?: string
    code?: string
    field?: string
    min?: number
    retry_after?: number
  }
  if (!res.ok)
    throw new SecurityError(data.detail || `HTTP ${res.status}`, res.status, data.code, data.field, data.min, data.retry_after)
  return data as T
}

export interface TwoFactorStatus {
  enabled: boolean
  pending: boolean
  confirmed_at: string | null
  recovery_codes_left: number
  required: boolean
  policy: TwoFactorPolicy
  auth_source: 'session' | 'api'
}

export interface TwoFactorSetup {
  manual_key: string
  otpauth_uri: string
  qr_svg: string | null
  expires_at: string
  issuer: string
  account: string
}

export type TwoFactorPolicy = 'off' | 'admins' | 'everyone'

export interface SecuritySettings {
  require_2fa: TwoFactorPolicy
  policies: TwoFactorPolicy[]
  users: number
  users_enrolled: number
  admins: number
  admins_enrolled: number
  protections: {
    login_max_failures: number
    login_max_failures_per_ip: number
    lockout_minutes: number
    rate_limit: boolean
    password_min_length: number
    real_install: boolean
  }
}

export const twoFactor = {
  status: () => securityApi<TwoFactorStatus>('auth/me/2fa'),
  setup: (password?: string) =>
    securityApi<TwoFactorSetup>('auth/me/2fa/setup', { method: 'POST', body: JSON.stringify({ password: password ?? '' }) }),
  confirm: (code: string) =>
    securityApi<{ ok: boolean; recovery_codes: string[]; status: TwoFactorStatus }>('auth/me/2fa/confirm', {
      method: 'POST',
      body: JSON.stringify({ code }),
    }),
  newRecoveryCodes: (password: string, code: string) =>
    securityApi<{ ok: boolean; recovery_codes: string[]; status: TwoFactorStatus }>('auth/me/2fa/recovery-codes', {
      method: 'POST',
      body: JSON.stringify({ password, code }),
    }),
  disable: (password: string, code: string) =>
    securityApi<{ ok: boolean; status: TwoFactorStatus }>('auth/me/2fa/disable', {
      method: 'POST',
      body: JSON.stringify({ password, code }),
    }),
}

/** The QR code as an <img> source — the SVG never touches the DOM as markup. */
export const qrDataUrl = (svg: string) => `data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg)}`

/** A refused NEW password, in the reader's language (null = not a password-policy refusal). */
export function passwordProblem(t: TFunction, code: string | undefined, min: number | undefined): string | null {
  switch (code) {
    case 'password_too_short':
      return t('account.password.tooShort', { min: min ?? 10 })
    case 'password_common':
      return t('account.password.common')
    case 'password_is_username':
      return t('account.password.isUsername')
    case 'password_too_long':
      return t('account.password.tooLong')
    case 'password_unchanged':
      return t('account.password.same')
    default:
      return null
  }
}

/** Minutes to wait, from a refusal's Retry-After seconds. */
export const minutesFrom = (seconds: number | undefined) => Math.max(1, Math.ceil((seconds ?? 60) / 60))
