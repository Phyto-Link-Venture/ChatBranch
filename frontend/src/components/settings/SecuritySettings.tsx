'use client'

import { useEffect, useState } from 'react'
import { AlertCircle, Check, Loader2, ShieldCheck } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Card, CardTitle } from '@/components/ui/Card'
import { useAccess } from '@/lib/useAccess'
import { refreshIdentity } from '@/lib/auth'
import { securityApi, type SecuritySettings as Settings, type TwoFactorPolicy } from '@/lib/security'
import { useT, type MessageKey } from '@/i18n'

const POLICY_LABEL: Record<TwoFactorPolicy, MessageKey> = {
  off: 'settings.security.policy.off',
  admins: 'settings.security.policy.admins',
  everyone: 'settings.security.policy.everyone',
}

/**
 * Settings → Organization → Security (security.manage — superadmin by default,
 * never a company admin): who must use two-step sign-in (Setting `require_2fa`,
 * off | admins | everyone), how many already do, and the protections the server
 * enforces from its environment (read-only here — docs/security.md).
 */
export function SecuritySettings() {
  const t = useT()
  const { ready, can } = useAccess()
  const [data, setData] = useState<Settings | null>(null)
  const [policy, setPolicy] = useState<TwoFactorPolicy>('off')
  const [busy, setBusy] = useState(false)
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)
  const [loadError, setLoadError] = useState('')

  useEffect(() => {
    securityApi<Settings>('settings/security')
      .then((d) => {
        setData(d)
        setPolicy(d.require_2fa)
      })
      .catch(() => setLoadError(t('settings.security.loadFailed')))
  }, [t])

  const save = async () => {
    setBusy(true)
    setMsg(null)
    try {
      const d = await securityApi<Settings>('settings/security', {
        method: 'PUT',
        body: JSON.stringify({ require_2fa: policy }),
      })
      setData(d)
      setPolicy(d.require_2fa)
      setMsg({ ok: true, text: t('settings.security.saved') })
      // a policy that now covers the person saving it takes them to setup
      await refreshIdentity()
    } catch (e) {
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    } finally {
      setBusy(false)
    }
  }

  if (loadError) return <p className="py-6 text-[12.5px] text-text-muted">{loadError}</p>
  if (!data) {
    return (
      <div className="flex items-center gap-2 py-6 text-[12.5px] text-text-muted">
        <Loader2 size={14} className="animate-spin" aria-hidden /> {t('common.status.loading')}
      </div>
    )
  }

  const canEdit = ready && can('security.manage')
  const p = data.protections

  return (
    <div className="flex flex-col gap-5">
      <Card className="p-4 sm:p-5">
        <CardTitle>{t('settings.security.policyTitle')}</CardTitle>
        <p className="mt-1 text-[12.5px] text-text-muted">{t('settings.security.policyIntro')}</p>
        <fieldset className="mt-4 flex flex-col gap-2" disabled={!canEdit || busy}>
          <legend className="mb-1 text-[11.5px] font-bold text-text-high">{t('settings.security.policyLabel')}</legend>
          {data.policies.map((option) => (
            <label
              key={option}
              className={`flex cursor-pointer items-center gap-3 rounded-lg border px-3 py-2.5 text-[13px] transition-colors ${
                policy === option ? 'border-text-primary bg-fill-subtle text-text-heading' : 'border-line text-text-high hover:bg-fill-hover'
              }`}
            >
              <input
                type="radio"
                name="require_2fa"
                value={option}
                checked={policy === option}
                onChange={() => setPolicy(option)}
                className="h-4 w-4 accent-[color:var(--primary)]"
              />
              {t(POLICY_LABEL[option])}
            </label>
          ))}
        </fieldset>
        <div className="mt-3 flex flex-wrap gap-x-6 gap-y-1 text-[12px] text-text-mid">
          <span>{t('settings.security.usersEnrolled', { n: data.users_enrolled, total: data.users })}</span>
          <span>{t('settings.security.adminsEnrolled', { n: data.admins_enrolled, total: data.admins })}</span>
        </div>
        {policy !== 'off' && policy !== data.require_2fa && (
          <p className="mt-2 text-[12px] text-[color:var(--amber-ink)]">{t('settings.security.selfNote')}</p>
        )}
        {msg && (
          <div
            role={msg.ok ? 'status' : 'alert'}
            className={`mt-3 flex items-start gap-2 rounded-lg border px-3 py-2 text-[12.5px] ${
              msg.ok
                ? 'border-green/25 bg-green/10 text-[color:var(--green-ink)]'
                : 'border-red/25 bg-red/10 text-[color:var(--red-ink)]'
            }`}
          >
            {msg.ok ? <Check size={14} className="mt-0.5 shrink-0" aria-hidden /> : <AlertCircle size={14} className="mt-0.5 shrink-0" aria-hidden />}
            {msg.text}
          </div>
        )}
        <div className="mt-4 flex justify-end">
          <Button size="sm" onClick={() => void save()} disabled={!canEdit || busy || policy === data.require_2fa}>
            {busy && <Loader2 size={13} className="animate-spin" aria-hidden />}
            {t('settings.security.save')}
          </Button>
        </div>
      </Card>

      <Card className="p-4 sm:p-5">
        <CardTitle>{t('settings.security.protectionsTitle')}</CardTitle>
        <p className="mt-1 text-[12.5px] text-text-muted">{t('settings.security.protectionsIntro')}</p>
        <ul className="mt-3 flex flex-col gap-2 text-[12.5px] text-text-high">
          <li className="flex items-start gap-2">
            <ShieldCheck size={14} className="mt-0.5 shrink-0 text-[color:var(--green-ink)]" aria-hidden />
            {t('settings.security.lockout', {
              minutes: p.lockout_minutes,
              count: p.login_max_failures,
              ip: p.login_max_failures_per_ip,
            })}
          </li>
          <li className="flex items-start gap-2">
            <ShieldCheck
              size={14}
              className={`mt-0.5 shrink-0 ${p.rate_limit ? 'text-[color:var(--green-ink)]' : 'text-[color:var(--amber-ink)]'}`}
              aria-hidden
            />
            {p.rate_limit ? t('settings.security.rateLimitOn') : t('settings.security.rateLimitOff')}
          </li>
          <li className="flex items-start gap-2">
            <ShieldCheck size={14} className="mt-0.5 shrink-0 text-[color:var(--green-ink)]" aria-hidden />
            {p.real_install
              ? t('settings.security.passwordRule', { min: p.password_min_length })
              : t('settings.security.demoMode')}
          </li>
        </ul>
      </Card>
    </div>
  )
}
