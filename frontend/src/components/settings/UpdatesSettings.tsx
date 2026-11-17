'use client'

import Link from 'next/link'
import { useEffect, useMemo, useState } from 'react'
import { Loader2 } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Card, CardTitle } from '@/components/ui/Card'
import {
  compareVersions,
  fetchOverview,
  majorOf,
  saveUpdates,
  useUpdates,
  type CompanyUpdate,
  type UpdatesOverview,
} from '@/lib/updates'
import { useFormat, useT } from '@/i18n'

/**
 * Settings → Updates (updates.manage): when the caller's company gets new
 * versions. New features install automatically or wait for approval; fixes
 * always install; a major version is only taken when the admin moves. Super
 * admins (updates.overview) also see every company's version and the changes.
 */
export function UpdatesSettings() {
  const t = useT()
  const fmt = useFormat()
  const { status, loaded, reload } = useUpdates()
  const [companyId, setCompanyId] = useState<number | null>(null)
  const [busy, setBusy] = useState(false)
  const [note, setNote] = useState<{ ok: boolean; text: string } | null>(null)
  const [overview, setOverview] = useState<UpdatesOverview | null>(null)

  const companies = status?.companies ?? []
  const company: CompanyUpdate | undefined = companies.find((c) => c.id === companyId) ?? companies[0]
  const majors = useMemo(() => [...new Set((status?.releases ?? []).map((r) => majorOf(r.version)))].sort((a, b) => a - b), [status])

  useEffect(() => {
    if (status?.can_overview) void fetchOverview().then(setOverview)
  }, [status])

  const save = async (body: { minor_mode?: 'auto' | 'hold'; approve?: string; pinned_major?: number }) => {
    if (!company) return
    setBusy(true)
    setNote(null)
    try {
      await saveUpdates({ company: company.id, ...body })
      setNote({ ok: true, text: t('updates.settings.saved') })
      reload()
      if (status?.can_overview) void fetchOverview().then(setOverview)
    } catch (e) {
      setNote({ ok: false, text: t('updates.settings.saveFailed', { error: (e as Error).message }) })
    } finally {
      setBusy(false)
    }
  }

  if (!loaded) return <Loader2 size={18} className="animate-spin text-text-muted" aria-label="…" />

  const option = (value: 'auto' | 'hold', label: string, hint: string) => (
    <label
      className={`flex min-h-[44px] cursor-pointer items-start gap-3 rounded-lg border px-3.5 py-3 ${
        company?.minor_mode === value ? 'border-[color:var(--brand-ink)] bg-fill-subtle' : 'border-line hover:bg-fill-subtle'
      }`}
    >
      <input
        type="radio"
        name="minor-mode"
        id={`minor-${value}`}
        className="mt-1 h-4 w-4 accent-[color:var(--primary)]"
        checked={company?.minor_mode === value}
        disabled={busy}
        onChange={() => void save({ minor_mode: value })}
      />
      <span className="min-w-0">
        <span className="block text-[14px] font-semibold text-text-heading">{label}</span>
        <span className="block text-[12.5px] text-text-muted">{hint}</span>
      </span>
    </label>
  )

  const newerHeld =
    company && company.minor_mode === 'hold'
      ? (status?.releases ?? [])
          .filter((r) => majorOf(r.version) === company.pinned_major && compareVersions(r.version, company.version) > 0)
          .map((r) => r.version)
      : []
  const otherMajors = company ? majors.filter((m) => m !== company.pinned_major) : []

  return (
    <div className="flex max-w-[900px] flex-col gap-4">
      <p className="text-[13.5px] text-text-mid">
        {t('updates.settings.intro')}{' '}
        <Link href="/whats-new" className="font-semibold text-[color:var(--brand-ink)] hover:underline">
          {t('updates.settings.seeNotes')}
        </Link>
      </p>

      {!company ? (
        <p className="text-[13.5px] text-text-muted">{t('updates.settings.noCompanies')}</p>
      ) : (
        <Card className="flex flex-col gap-4 p-[calc(1.1rem*var(--sp))]">
          <div className="flex flex-wrap items-end gap-x-8 gap-y-3">
            {companies.length > 1 ? (
              <label className="flex flex-col gap-1 text-[12px] font-semibold text-text-muted" htmlFor="updates-company">
                {t('updates.settings.company')}
                <select
                  id="updates-company"
                  className="min-h-[40px] rounded-lg border border-line-strong bg-card px-3 text-[14px] text-text-primary"
                  value={company.id}
                  onChange={(e) => {
                    setCompanyId(Number(e.target.value))
                    setNote(null)
                  }}
                >
                  {companies.map((c) => (
                    <option key={c.id} value={c.id}>
                      {c.name}
                    </option>
                  ))}
                </select>
              </label>
            ) : (
              <Stat label={t('updates.settings.company')} value={company.name} />
            )}
            <Stat label={t('updates.settings.running')} value={company.version} />
            <Stat label={t('updates.settings.newest')} value={company.latest_in_major} />
          </div>

          <section className="flex flex-col gap-2" aria-labelledby="updates-features">
            <h3 id="updates-features" className="text-[14px] font-bold text-text-heading">
              {t('updates.settings.featuresTitle')}
            </h3>
            {option('auto', t('updates.settings.auto'), t('updates.settings.autoHint'))}
            {option('hold', t('updates.settings.hold'), t('updates.settings.holdHint', { version: company.version }))}
            {company.minor_mode === 'hold' &&
              (newerHeld.length > 0 ? (
                <div className="flex flex-wrap gap-2 pt-1">
                  {newerHeld.map((v) => (
                    <Button key={v} variant="secondary" size="sm" disabled={busy} onClick={() => void save({ approve: v })}>
                      {t('updates.settings.approve', { version: v })}
                    </Button>
                  ))}
                </div>
              ) : (
                <p className="text-[12.5px] text-text-muted">{t('updates.settings.upToDate', { major: company.pinned_major })}</p>
              ))}
          </section>

          <section className="flex flex-col gap-2 border-t border-line pt-4" aria-labelledby="updates-major">
            <h3 id="updates-major" className="text-[14px] font-bold text-text-heading">
              {t('updates.settings.majorTitle')}
            </h3>
            {otherMajors.length === 0 ? (
              <p className="text-[13px] text-text-mid">
                {t('updates.settings.majorStay', { major: company.pinned_major, next: company.pinned_major + 1 })}
              </p>
            ) : (
              <div className="flex flex-col gap-2">
                {otherMajors.some((m) => m > company.pinned_major) && (
                  <p className="text-[13px] text-text-mid">
                    {t('updates.settings.majorAvailable', {
                      major: Math.max(...otherMajors),
                      current: company.pinned_major,
                    })}
                  </p>
                )}
                <div className="flex flex-wrap gap-2">
                  {otherMajors.map((m) => (
                    <Button key={m} variant="secondary" size="sm" disabled={busy} onClick={() => void save({ pinned_major: m })}>
                      {m > company.pinned_major
                        ? t('updates.settings.majorMove', { major: m })
                        : t('updates.settings.majorBack', { major: m })}
                    </Button>
                  ))}
                </div>
              </div>
            )}
          </section>

          <div className="flex flex-wrap items-center justify-between gap-2 border-t border-line pt-3 text-[12.5px] text-text-muted">
            <span>
              {company.updated_by
                ? t('updates.settings.lastChange', { by: company.updated_by, date: fmt.dateTime(company.updated_at ?? '') })
                : t('updates.settings.defaults')}
            </span>
            {busy && <Loader2 size={14} className="animate-spin" aria-hidden />}
            {note && (
              <span role="status" className={`font-semibold ${note.ok ? 'text-green-300' : 'text-red-300'}`}>
                {note.text}
              </span>
            )}
          </div>
        </Card>
      )}

      {overview && (
        <Card className="flex flex-col gap-3 p-[calc(1.1rem*var(--sp))]">
          <CardTitle>{t('updates.settings.overviewTitle')}</CardTitle>
          <p className="text-[12.5px] text-text-muted">{t('updates.settings.overviewIntro')}</p>
          <div className="overflow-x-auto">
            <table className="w-full min-w-[560px] text-left text-[13px]">
              <thead className="text-[11.5px] uppercase tracking-[0.05em] text-text-muted">
                <tr className="border-b border-line">
                  <th className="py-2 pr-3 font-semibold">{t('updates.settings.colCompany')}</th>
                  <th className="py-2 pr-3 font-semibold">{t('updates.settings.colVersion')}</th>
                  <th className="py-2 pr-3 font-semibold">{t('updates.settings.colChoice')}</th>
                  <th className="py-2 font-semibold">{t('updates.settings.colChanged')}</th>
                </tr>
              </thead>
              <tbody>
                {overview.companies.map((c) => (
                  <tr key={c.id} className="border-b border-line last:border-0">
                    <td className="py-2 pr-3 font-semibold text-text-heading">{c.name}</td>
                    <td className="py-2 pr-3 tabular-nums">{c.version}</td>
                    <td className="py-2 pr-3">
                      {c.minor_mode === 'hold'
                        ? t('updates.settings.choiceHold', { version: c.held_version })
                        : t('updates.settings.choiceAuto')}
                    </td>
                    <td className="py-2 text-text-muted">
                      {c.updated_by ? `${c.updated_by} · ${fmt.dateTime(c.updated_at ?? '')}` : '—'}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <h3 className="pt-1 text-[13px] font-bold text-text-heading">{t('updates.settings.recent')}</h3>
          {overview.changes.length === 0 ? (
            <p className="text-[12.5px] text-text-muted">{t('updates.settings.noChanges')}</p>
          ) : (
            <ul className="flex flex-col gap-1.5 text-[12.5px] text-text-mid">
              {overview.changes.slice(0, 15).map((c, i) => (
                <li key={i}>
                  <span className="tabular-nums text-text-muted">{fmt.dateTime(c.at)}</span> · {c.by} ·{' '}
                  {c.company.replace(/^company:\d+\s*/, '')}: {c.change}
                </li>
              ))}
            </ul>
          )}
        </Card>
      )}
    </div>
  )
}

function Stat({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex flex-col gap-0.5">
      <span className="text-[12px] font-semibold text-text-muted">{label}</span>
      <span className="text-[16px] font-bold tabular-nums text-text-heading">{value}</span>
    </div>
  )
}
