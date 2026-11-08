'use client'

import { useCallback, useEffect, useState } from 'react'
import { AlertCircle, BellRing, Send, Smartphone, Trash2 } from 'lucide-react'
import { Card, CardTitle } from '@/components/ui/Card'
import { Button } from '@/components/ui/Button'
import { Toggle } from '@/components/ui/Toggle'
import { useFormat, useT } from '@/i18n'
import {
  disablePush,
  enablePush,
  inNativeApp,
  pushAbility,
  pushConfig,
  pushDevices,
  testPush,
  thisDeviceTail,
  type PushAbility,
  type PushConfig,
  type PushDevice,
} from '@/lib/push'

/**
 * Settings → My notifications → "Alerts on this device": the one place a person
 * turns on notifications that arrive when ChatBranch is CLOSED, and sees every
 * device of theirs that gets them.
 *
 * Permission is only ever asked for from the button here, never on page load —
 * a prompt nobody expected gets dismissed, and in Safari that blocks the site for
 * good. What the browser or OS has already decided is read (never changed), so the
 * card can say "blocked" with what to do about it instead of failing silently.
 */
export function PushDevicesCard() {
  const t = useT()
  const f = useFormat()
  const [config, setConfig] = useState<PushConfig | null>(null)
  const [ability, setAbility] = useState<PushAbility>('unsupported')
  const [devices, setDevices] = useState<PushDevice[]>([])
  const [tail, setTail] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')

  const load = useCallback(async () => {
    const [cfg, can, mine, own] = await Promise.all([
      pushConfig().catch(() => null),
      pushAbility(),
      pushDevices().then((r) => r.devices).catch(() => [] as PushDevice[]),
      thisDeviceTail(),
    ])
    setConfig(cfg)
    setAbility(can)
    setDevices(mine)
    setTail(own)
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  // this browser's own row, so the toggle reflects THIS device and not another
  const mine = tail ? devices.find((d) => d.endpoint_tail === tail) : undefined
  const native = inNativeApp()
  const on = ability === 'on' && (native ? devices.length > 0 : !!mine)
  const serverReady = native ? config?.app === true : config?.web === true

  const turn = async (want: boolean) => {
    setError('')
    setNotice('')
    setBusy(true)
    try {
      if (want) await enablePush()
      else await disablePush(mine?.id)
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
      await load()
    } finally {
      setBusy(false)
    }
  }

  const test = async () => {
    setError('')
    setNotice('')
    setBusy(true)
    try {
      const r = await testPush()
      setNotice(r.sent > 0
        ? t('settings.myNotifications.deviceTestSent', { count: r.sent })
        : t('settings.myNotifications.deviceTestNone', { reason: r.problems[0] ?? '' }))
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  const forget = async (id: number) => {
    setError('')
    setBusy(true)
    try {
      // forgetting THIS device should also drop its subscription, not just the row
      if (mine?.id === id) await disablePush(id)
      else await (await import('@/lib/push')).forgetPushDevice(id)
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  // Why this device cannot be turned on, in words the person can act on.
  const blocked = ability === 'unsupported'
    ? t('settings.myNotifications.deviceUnsupported')
    : ability === 'denied'
      ? t('settings.myNotifications.deviceDenied')
      : config && !serverReady
        ? native
          ? t('settings.myNotifications.deviceAppNotConfigured')
          : t('settings.myNotifications.deviceNotConfigured')
        : ''

  return (
    <Card className="p-4 sm:p-5">
      <CardTitle>{t('settings.myNotifications.deviceTitle')}</CardTitle>
      <p className="mt-1 text-[12.5px] text-text-muted">{t('settings.myNotifications.deviceHint')}</p>

      <div className="mt-3 flex items-center gap-3 border-t border-line pt-3">
        <span className="grid h-8 w-8 shrink-0 place-items-center rounded-lg bg-fill text-text-high">
          <BellRing size={15} aria-hidden />
        </span>
        <div className="min-w-0 flex-1">
          <div className="text-[13.5px] font-semibold text-text-high">
            {t('settings.myNotifications.deviceToggle')}
          </div>
          <div className="text-[12px] leading-snug text-text-muted">
            {blocked || (on
              ? t('settings.myNotifications.deviceOnHint')
              : t('settings.myNotifications.deviceOffHint'))}
          </div>
        </div>
        <Toggle
          on={on}
          disabled={busy || blocked.length > 0}
          onChange={(v) => void turn(v)}
          label={t('settings.myNotifications.deviceToggle')}
        />
      </div>

      {devices.length > 0 && (
        <div className="mt-4 border-t border-line pt-3">
          <div className="text-[12px] font-semibold uppercase tracking-wide text-text-faint">
            {t('settings.myNotifications.devicesTitle')}
          </div>
          <ul className="mt-2 divide-y divide-line">
            {devices.map((d) => (
              <li key={d.id} className="flex items-center gap-3 py-2.5">
                <Smartphone size={14} className="shrink-0 text-text-faint" aria-hidden />
                <div className="min-w-0 flex-1">
                  <div className="truncate text-[13px] font-medium text-text-high">
                    {d.label || d.platform}
                    {d.endpoint_tail === tail && (
                      <span className="ml-1.5 text-[11.5px] font-normal text-text-faint">
                        {t('settings.myNotifications.deviceThis')}
                      </span>
                    )}
                  </div>
                  <div className="truncate text-[11.5px] text-text-muted">
                    {d.last_error
                      ? d.last_error
                      : d.last_push_at
                        ? t('settings.myNotifications.deviceLastPush', { when: f.relative(d.last_push_at) })
                        : t('settings.myNotifications.deviceNever')}
                  </div>
                </div>
                <Button
                  variant="ghost"
                  size="sm"
                  disabled={busy}
                  onClick={() => void forget(d.id)}
                  aria-label={t('settings.myNotifications.deviceForget')}
                >
                  <Trash2 size={13} aria-hidden /> {t('settings.myNotifications.deviceForget')}
                </Button>
              </li>
            ))}
          </ul>
          <Button variant="secondary" size="sm" className="mt-3" disabled={busy} onClick={() => void test()}>
            <Send size={13} aria-hidden /> {t('settings.myNotifications.deviceTest')}
          </Button>
        </div>
      )}

      {notice && <p className="mt-3 text-[12.5px] text-text-mid">{notice}</p>}
      {error && (
        <div
          role="alert"
          className="mt-3 flex items-start gap-2 rounded-lg border border-red/25 bg-red/10 px-3 py-2 text-[12.5px] text-[color:var(--red-ink)]"
        >
          <AlertCircle size={14} className="mt-0.5 shrink-0" aria-hidden /> {error}
        </div>
      )}
    </Card>
  )
}
