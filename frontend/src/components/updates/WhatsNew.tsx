'use client'

import Link from 'next/link'
import { useMemo, useState } from 'react'
import { Loader2 } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Card } from '@/components/ui/Card'
import { RELEASES, type ChangeKind, type Localized } from '@/content/releases'
import { compareVersions, majorOf, saveUpdates, useUpdates } from '@/lib/updates'
import { useFormat, useI18n, useT } from '@/i18n'

const KIND_ORDER: ChangeKind[] = ['new', 'improved', 'fixed', 'removed']
const KIND_CHIP: Record<ChangeKind, string> = {
  new: 'bg-brand-400/15 text-brand-300',
  improved: 'bg-blue/12 text-blue-300',
  fixed: 'bg-green/12 text-green-300',
  removed: 'bg-fill text-text-mid',
}
type Filter = 'all' | 'new' | 'improved' | 'fixed'

/**
 * /whats-new — every version's changes, newest first, new features leading.
 * Signed-in only (the shell guards every non-public route). Marks the version
 * the caller's company runs; a newer one its admin hasn't turned on yet shows
 * "Not turned on yet", with the button for an admin who manages one company.
 */
export function WhatsNew() {
  const t = useT()
  const fmt = useFormat()
  const { locale } = useI18n()
  const { status, loaded, reload } = useUpdates()
  const [filter, setFilter] = useState<Filter>('all')
  const [busy, setBusy] = useState(false)
  const [note, setNote] = useState<{ ok: boolean; text: string } | null>(null)

  const pick = (l: Localized) => (locale === 'zh' ? l.zh : locale === 'ms' ? l.ms : l.en)
  const mine = status?.mine ?? null
  // an admin of exactly one company can turn a held version on from here
  const own = status?.can_manage && status.companies.length === 1 ? status.companies[0] : null

  const releases = useMemo(
    () =>
      RELEASES.map((r) => ({
        ...r,
        changes: [...r.changes]
          .filter((c) => filter === 'all' || c.kind === filter)
          .sort((a, b) => KIND_ORDER.indexOf(a.kind) - KIND_ORDER.indexOf(b.kind)),
      })),
    [filter],
  )

  const turnOn = async (version: string) => {
    if (!own) return
    setBusy(true)
    setNote(null)
    try {
      await saveUpdates({ company: own.id, approve: version })
      setNote({ ok: true, text: t('updates.page.turnedOn', { version, company: own.name }) })
      reload()
    } catch (e) {
      setNote({ ok: false, text: t('updates.settings.saveFailed', { error: (e as Error).message }) })
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="flex max-w-[860px] flex-col gap-5">
      <p className="text-[14px] text-text-mid">
        {t('updates.page.intro')}{' '}
        {status
          ? status.mine_company
            ? t('updates.page.yourVersion', { company: status.mine_company, version: status.mine })
            : t('updates.page.latestVersion', { version: status.mine })
          : loaded
            ? t('updates.page.loadFailed')
            : null}
        {status?.can_manage && (
          <>
            {' '}
            <Link href="/settings/updates" className="font-semibold text-[color:var(--brand-ink)] hover:underline">
              {t('updates.page.settings')}
            </Link>
          </>
        )}
      </p>

      <div className="flex flex-wrap gap-2" role="group" aria-label={t('updates.page.title')}>
        {(['all', 'new', 'improved', 'fixed'] as Filter[]).map((f) => (
          <button
            key={f}
            type="button"
            aria-pressed={filter === f}
            onClick={() => setFilter(f)}
            className={`min-h-[40px] rounded-full border px-3.5 text-[13px] font-semibold transition-colors ${
              filter === f ? 'border-transparent bg-primary text-on-primary' : 'border-line-strong text-text-mid hover:bg-fill-hover'
            }`}
          >
            {t(`updates.page.filters.${f}`)}
          </button>
        ))}
      </div>

      {note && (
        <p role="status" className={`text-[13px] font-semibold ${note.ok ? 'text-green-300' : 'text-red-300'}`}>
          {note.text}
        </p>
      )}

      <ol className="flex flex-col gap-4">
        {releases.map((r) => {
          const isMine = mine === r.version
          const waiting = mine != null && majorOf(r.version) === majorOf(mine) && compareVersions(r.version, mine) > 0
          return (
            <li key={r.version}>
              <Card className={`p-[calc(1rem*var(--sp))] ${waiting ? 'opacity-80' : ''}`}>
                <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
                  <h2 className="display text-[22px] leading-tight text-text-heading">{r.version}</h2>
                  <span className="text-[15px] font-semibold text-text-high">{pick(r.title)}</span>
                  <span className="text-[12.5px] text-text-muted">{fmt.date(`${r.date}T12:00:00Z`)}</span>
                  {isMine && (
                    <span className="rounded-full bg-brand-400/15 px-2 py-0.5 text-[11.5px] font-bold text-brand-300">
                      {t('updates.page.yours')}
                    </span>
                  )}
                  {waiting && (
                    <span className="rounded-full bg-amber/15 px-2 py-0.5 text-[11.5px] font-bold text-amber-300" title={t('updates.page.waitingHint')}>
                      {t('updates.page.waiting')}
                    </span>
                  )}
                </div>
                {r.changes.length === 0 ? (
                  <p className="mt-2 text-[13px] text-text-muted">{t('updates.page.empty')}</p>
                ) : (
                  <ul className="mt-3 flex flex-col gap-2.5">
                    {r.changes.map((c, i) => (
                      <li key={i} className="flex items-start gap-2.5 text-[14px] leading-relaxed text-text-primary">
                        <span className={`mt-0.5 shrink-0 rounded px-1.5 py-0.5 text-[11px] font-bold ${KIND_CHIP[c.kind]}`}>
                          {t(`updates.page.kinds.${c.kind}`)}
                        </span>
                        <span className="min-w-0">{pick(c.text)}</span>
                      </li>
                    ))}
                  </ul>
                )}
                {waiting && own?.minor_mode === 'hold' && (
                  <div className="mt-3">
                    <Button variant="primary" size="sm" disabled={busy} onClick={() => void turnOn(r.version)}>
                      {busy && <Loader2 size={13} className="animate-spin" aria-hidden />}
                      {t('updates.page.turnOn', { version: r.version })}
                    </Button>
                  </div>
                )}
              </Card>
            </li>
          )
        })}
      </ol>
    </div>
  )
}
