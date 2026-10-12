'use client'

import { useEffect, useMemo, useState } from 'react'
import { CircleAlert, Clock, Globe2, Loader2 } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Card, CardTitle } from '@/components/ui/Card'
import { Select } from '@/components/ui/Select'
import { useAccess } from '@/lib/useAccess'
import {
  ORG_CHOICE,
  deviceTimeZone,
  getBusinessTimeZone,
  saveBusinessTimeZone,
  saveTimeZoneChoice,
  useTimeZoneState,
  validZone,
  type BusinessTimeZone,
} from '@/lib/timeZone'
import { useFormat, useT } from '@/i18n'

type Mode = 'device' | 'business' | 'other'

/** Every zone this browser knows ("UTC" first), with its offset now: "Asia/Kuala_Lumpur (GMT+8)". */
function useZoneOptions(extra: string[]) {
  const [zones, setZones] = useState<string[]>([])
  useEffect(() => {
    const intl = Intl as unknown as { supportedValuesOf?: (k: string) => string[] }
    setZones(intl.supportedValuesOf?.('timeZone') ?? [])
  }, [])
  return useMemo(() => {
    const all = Array.from(new Set(['UTC', ...extra.filter(validZone), ...zones]))
    const now = new Date()
    return all.map((z) => {
      let offset = ''
      try {
        offset =
          new Intl.DateTimeFormat('en', { timeZone: z, timeZoneName: 'shortOffset' })
            .formatToParts(now)
            .find((p) => p.type === 'timeZoneName')?.value ?? ''
      } catch {
        /* an id this browser can't read */
      }
      return { value: z, label: z.replace(/_/g, ' '), hint: offset }
    })
  }, [zones, extra])
}

/**
 * Settings → Time zone (every signed-in person). Two clocks:
 *  - "Show times in" — the PERSON's choice of the zone the dashboard formats every
 *    time in (`useFormat`): this device's (default), the business zone, or another;
 *    kept on their account (`PUT /api/auth/preferences {time_zone}`).
 *  - "Business time zone" — the organization's clock (Setting `org_timezone`: the
 *    AI, bookings, journeys, quiet hours, report days), editable with
 *    `settings.appearance.org` (`PUT /api/settings/appearance {time_zone}`), read
 *    by everyone else. A server that never had one set says so — up front.
 */
export function TimeZoneSettings() {
  const t = useT()
  const f = useFormat()
  const { ready, can } = useAccess()
  const mine = useTimeZoneState()
  const [business, setBusiness] = useState<BusinessTimeZone | null>(null)
  const [loadError, setLoadError] = useState('')
  const [device, setDevice] = useState('UTC')
  const [now, setNow] = useState(() => Date.now())

  // personal choice
  const [mode, setMode] = useState<Mode>('device')
  const [other, setOther] = useState('')
  const [mineBusy, setMineBusy] = useState(false)
  const [mineMsg, setMineMsg] = useState<{ ok: boolean; text: string } | null>(null)

  // business zone editor
  const [editing, setEditing] = useState(false)
  const [draft, setDraft] = useState('')
  const [bizBusy, setBizBusy] = useState(false)
  const [bizMsg, setBizMsg] = useState<{ ok: boolean; text: string } | null>(null)

  useEffect(() => {
    setDevice(deviceTimeZone())
    const id = window.setInterval(() => setNow(Date.now()), 30_000)
    return () => window.clearInterval(id)
  }, [])

  useEffect(() => {
    getBusinessTimeZone()
      .then(setBusiness)
      .catch(() => setLoadError(t('settings.timeZone.loadFailed')))
  }, [t])

  // the stored choice → the radio (re-read when another tab or a sign-in sync changes it)
  useEffect(() => {
    if (!mine.choice) setMode('device')
    else if (mine.choice === ORG_CHOICE) setMode('business')
    else {
      setMode('other')
      setOther(mine.choice)
    }
  }, [mine.choice])

  const canManage = ready && can('settings.appearance.org') && !!business?.can_manage
  const zoneOptions = useZoneOptions(useMemo(() => [device, business?.time_zone ?? '', other], [device, business, other]))
  const at = (zone: string) => f.time(now, zone)

  const saveMine = async (choice: string) => {
    setMineBusy(true)
    setMineMsg(null)
    try {
      await saveTimeZoneChoice(choice)
      setMineMsg({ ok: true, text: t('settings.timeZone.saved') })
    } catch (e) {
      setMineMsg({ ok: false, text: t('settings.timeZone.saveFailed', { error: e instanceof Error ? e.message : String(e) }) })
    } finally {
      setMineBusy(false)
    }
  }

  const pick = (next: Mode) => {
    setMode(next)
    if (next === 'device') void saveMine('')
    else if (next === 'business') void saveMine(ORG_CHOICE)
    else if (other) void saveMine(other)
  }

  const saveBusiness = async (zone: string) => {
    setBizBusy(true)
    setBizMsg(null)
    try {
      const next = await saveBusinessTimeZone(zone)
      setBusiness(next)
      setEditing(false)
      setBizMsg({ ok: true, text: t('settings.timeZone.savedBusiness', { zone: next.time_zone }) })
    } catch (e) {
      setBizMsg({ ok: false, text: t('settings.timeZone.saveFailed', { error: e instanceof Error ? e.message : String(e) }) })
    } finally {
      setBizBusy(false)
    }
  }

  if (loadError) return <p className="py-6 text-[12.5px] text-text-muted">{loadError}</p>
  if (!business) {
    return (
      <div className="flex items-center gap-2 py-6 text-[12.5px] text-text-muted">
        <Loader2 size={14} className="animate-spin" aria-hidden /> {t('common.status.loading')}
      </div>
    )
  }

  const options: { id: Mode; title: string; hint: string }[] = [
    { id: 'device', title: t('settings.timeZone.device'), hint: t('settings.timeZone.deviceHint', { zone: device }) },
    {
      id: 'business',
      title: t('settings.timeZone.business'),
      hint: t('settings.timeZone.businessHint', { zone: business.time_zone }),
    },
    { id: 'other', title: t('settings.timeZone.other'), hint: t('settings.timeZone.otherHint') },
  ]
  const shownIn = mode === 'device' ? device : mode === 'business' ? business.time_zone : other || device
  const sourceText =
    business.time_zone_source === 'setting'
      ? t('settings.timeZone.sourceSetting')
      : business.time_zone_source === 'env'
        ? t('settings.timeZone.sourceEnv')
        : t('settings.timeZone.sourceServer', { zone: business.time_zone })
  const unset = business.time_zone_source !== 'setting'

  return (
    <div className="flex flex-col gap-5">
      {/* the person's own clock */}
      <Card className="p-4 sm:p-5" data-tour="timezone-mine">
        <CardTitle>{t('settings.timeZone.mineTitle')}</CardTitle>
        <p className="mt-1 max-w-[640px] text-[12.5px] text-text-muted">{t('settings.timeZone.mineIntro')}</p>
        <div role="radiogroup" aria-label={t('settings.timeZone.mineTitle')} className="mt-4 flex flex-col gap-2">
          {options.map((o) => {
            const on = mode === o.id
            return (
              <div
                key={o.id}
                className={`rounded-lg border px-3 py-2.5 transition-colors ${on ? 'border-text-primary bg-fill-subtle' : 'border-line'}`}
              >
                <button
                  type="button"
                  role="radio"
                  aria-checked={on}
                  disabled={mineBusy}
                  onClick={() => pick(o.id)}
                  className="flex w-full cursor-pointer items-start gap-2.5 text-left disabled:cursor-wait"
                >
                  <span
                    aria-hidden
                    className={`mt-[3px] grid h-3.5 w-3.5 shrink-0 place-items-center rounded-full border ${on ? 'border-text-primary' : 'border-line-strong'}`}
                  >
                    {on && <span className="h-1.5 w-1.5 rounded-full bg-text-primary" />}
                  </span>
                  <span className="min-w-0">
                    <span className="block text-[13px] font-semibold text-text-high">{o.title}</span>
                    <span className="block text-[12px] text-text-muted">{o.hint}</span>
                  </span>
                </button>
                {o.id === 'other' && on && (
                  <div className="mt-2 pl-6">
                    <Select
                      value={other}
                      onChange={(v) => {
                        setOther(v)
                        if (v) void saveMine(v)
                      }}
                      searchable
                      disabled={mineBusy}
                      ariaLabel={t('settings.timeZone.pickAria')}
                      className="w-full max-w-[360px]"
                      options={zoneOptions}
                    />
                  </div>
                )}
              </div>
            )
          })}
        </div>
        <p className="mt-3 flex items-center gap-1.5 text-[12px] text-text-muted">
          <Clock size={12} aria-hidden />
          {t('settings.timeZone.example', { time: `${f.dateTime(now, shownIn)} (${shownIn})` })}
        </p>
        {mineMsg && (
          <p
            role={mineMsg.ok ? 'status' : 'alert'}
            className={`mt-2 text-[12px] ${mineMsg.ok ? 'text-[color:var(--green-ink)]' : 'text-[color:var(--red-ink)]'}`}
          >
            {mineMsg.text}
          </p>
        )}
      </Card>

      {/* the organization's clock */}
      <Card className="p-4 sm:p-5" data-tour="timezone-business">
        <CardTitle>{t('settings.timeZone.businessTitle')}</CardTitle>
        <p className="mt-1 max-w-[640px] text-[12.5px] text-text-muted">{t('settings.timeZone.businessIntro')}</p>

        <div className="mt-4 flex flex-wrap items-center gap-x-3 gap-y-2">
          <Globe2 size={15} className="text-text-faint" aria-hidden />
          <span className="text-[13px] text-text-high">
            {t('settings.timeZone.current', { zone: business.time_zone, time: at(business.time_zone) })}
          </span>
          {canManage && !editing && (
            <Button
              size="sm"
              variant="secondary"
              onClick={() => {
                setDraft(unset && validZone(device) ? device : business.time_zone)
                setEditing(true)
                setBizMsg(null)
              }}
            >
              {t('settings.timeZone.change')}
            </Button>
          )}
        </div>

        <p className={`mt-2 flex items-start gap-1.5 text-[12px] ${unset ? 'text-[color:var(--amber-ink)]' : 'text-text-muted'}`}>
          {unset && <CircleAlert size={13} className="mt-0.5 shrink-0" aria-hidden />}
          <span>{sourceText}</span>
        </p>

        {canManage && unset && !editing && validZone(device) && device !== business.time_zone && (
          <div className="mt-3">
            <Button size="sm" onClick={() => void saveBusiness(device)} disabled={bizBusy}>
              {bizBusy && <Loader2 size={12} className="animate-spin" aria-hidden />}
              {t('settings.timeZone.useZone', { zone: device })}
            </Button>
          </div>
        )}

        {canManage && editing && (
          <div className="mt-3 flex flex-wrap items-center gap-2">
            <Select
              value={draft}
              onChange={setDraft}
              searchable
              disabled={bizBusy}
              ariaLabel={t('settings.timeZone.pickAria')}
              className="w-full max-w-[360px]"
              options={zoneOptions}
            />
            <Button size="sm" onClick={() => void saveBusiness(draft)} disabled={bizBusy || !draft}>
              {bizBusy && <Loader2 size={12} className="animate-spin" aria-hidden />}
              {t('common.actions.save')}
            </Button>
            <Button size="sm" variant="ghost" onClick={() => setEditing(false)} disabled={bizBusy}>
              {t('common.actions.cancel')}
            </Button>
          </div>
        )}

        {!canManage && ready && <p className="mt-2 text-[12px] text-text-muted">{t('settings.timeZone.readOnly')}</p>}

        {bizMsg && (
          <p
            role={bizMsg.ok ? 'status' : 'alert'}
            className={`mt-2 text-[12px] ${bizMsg.ok ? 'text-[color:var(--green-ink)]' : 'text-[color:var(--red-ink)]'}`}
          >
            {bizMsg.text}
          </p>
        )}
      </Card>
    </div>
  )
}
