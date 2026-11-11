'use client'

/**
 * "Contacts on this phone" — the picker behind the import wizard's third source.
 *
 * The shape is deliberate, and the store rules are the reason:
 *
 *   1 **Disclosure** — what is read, what is uploaded, what never happens. Shown
 *     BEFORE the OS prompt (Google Play's Prominent Disclosure; Apple's purpose
 *     string says the same thing in the system dialog) and before a single
 *     contact is read.
 *   2 **Permission** — asked on a tap. A refusal is final on both platforms, so a
 *     `denied` lands on a screen that explains how to turn it back on in Settings.
 *   3 **Pick** — search, tick, select-all-visible, a running count, and the name /
 *     phone / email of every row on show, so nothing is uploaded unseen.
 *
 * Only the ticked contacts leave the phone, and they leave through the importer
 * the wizard already uses — the next steps (column mapping, duplicate handling,
 * the preview counts) are the same ones a .csv upload goes through, which is why
 * this component ends at "use these" and hands rows upward.
 *
 * In a plain browser the whole feature is hidden (`deviceContactsSupported()`);
 * in Chrome on Android the browser's own picker stands in for steps 2 and 3, and
 * the chosen contacts still land in the review list below.
 */

import { useMemo, useState } from 'react'
import { AlertTriangle, Check, Loader2, Search, Smartphone } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { useT } from '@/i18n'
import {
  checkDeviceAccess,
  contactLabel,
  deviceContactsSource,
  isImportable,
  MAX_DEVICE_CONTACTS,
  pickWebContacts,
  readDeviceContacts,
  requestDeviceAccess,
  searchText,
  toImportRows,
  type DeviceContact,
} from '@/lib/deviceContacts'
import { errText } from '@/lib/contactIdentity'

type Stage = 'disclosure' | 'loading' | 'blocked' | 'pick'

export function DeviceContactsPicker({
  busy,
  onUse,
}: {
  /** The wizard is talking to the server — keep hands off the buttons. */
  busy: boolean
  /** The ticked contacts, as importer rows. */
  onUse: (rows: Record<string, string>[]) => void
}) {
  const t = useT()
  const source = deviceContactsSource()
  const [stage, setStage] = useState<Stage>('disclosure')
  const [contacts, setContacts] = useState<DeviceContact[]>([])
  const [chosen, setChosen] = useState<Set<string>>(new Set())
  const [query, setQuery] = useState('')
  const [limited, setLimited] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const visible = useMemo(() => {
    const q = query.trim().toLowerCase()
    if (q.length === 0) return contacts
    return contacts.filter((c) => searchText(c).includes(q))
  }, [contacts, query])
  const pickable = useMemo(() => visible.filter(isImportable), [visible])
  const allVisibleOn = pickable.length > 0 && pickable.every((c) => chosen.has(c.id))
  const tooMany = chosen.size > MAX_DEVICE_CONTACTS

  /** Everything the person ticked, in the order the list shows them. */
  const selected = useMemo(() => contacts.filter((c) => chosen.has(c.id)), [contacts, chosen])

  const load = async () => {
    setError(null)
    setStage('loading')
    try {
      if (source === 'web') {
        // The browser asks for permission itself and returns only the selection.
        const picked = await pickWebContacts()
        if (picked.length === 0) {
          setStage('disclosure')
          setError(t('contacts.device.webCancelled'))
          return
        }
        setContacts(picked)
        setChosen(new Set(picked.filter(isImportable).map((c) => c.id)))
        setStage('pick')
        return
      }
      let access = await checkDeviceAccess()
      if (access === 'prompt') access = await requestDeviceAccess()
      if (access === 'denied' || access === 'unsupported') {
        setStage('blocked')
        return
      }
      setLimited(access === 'limited')
      const all = await readDeviceContacts()
      setContacts(all)
      setChosen(new Set())
      setStage('pick')
    } catch (e) {
      // The plugin gates every read itself and reports a refusal as a rejected
      // call rather than a permission state, so ask the OS before blaming the read.
      if (source === 'native' && (await checkDeviceAccess()) === 'denied') {
        setStage('blocked')
        return
      }
      setError(errText(e))
      setStage(contacts.length > 0 ? 'pick' : 'disclosure')
    }
  }

  const toggle = (id: string) => {
    const next = new Set(chosen)
    if (next.has(id)) next.delete(id)
    else next.add(id)
    setChosen(next)
  }

  const selectAllVisible = () => {
    const next = new Set(chosen)
    if (allVisibleOn) for (const c of pickable) next.delete(c.id)
    else for (const c of pickable) next.add(c.id)
    setChosen(next)
  }

  return (
    <div className="flex flex-col gap-3">
      {error && (
        <div role="alert" className="flex items-start gap-2 rounded-lg border border-amber/30 bg-amber/10 px-3 py-2 text-[12px] text-amber-300">
          <AlertTriangle size={13} className="mt-0.5 shrink-0" aria-hidden />
          <span className="min-w-0">{error}</span>
        </div>
      )}

      {/* 1 — prominent disclosure, before the OS is ever asked */}
      {stage === 'disclosure' && (
        <>
          <div className="flex items-start gap-3 rounded-xl border border-line bg-fill-subtle px-4 py-3.5">
            <span className="grid h-9 w-9 shrink-0 place-items-center rounded-lg bg-fill text-text-high">
              <Smartphone size={16} aria-hidden />
            </span>
            <div className="min-w-0">
              <div className="text-[13px] font-semibold text-text-high">{t('contacts.device.disclosureTitle')}</div>
              <p className="mt-0.5 text-[11.5px] leading-relaxed text-text-muted">{t('contacts.device.disclosureLead')}</p>
            </div>
          </div>
          <ul className="flex flex-col gap-2">
            {(
              [
                ['contacts.device.readsTitle', 'contacts.device.readsBody'],
                ['contacts.device.uploadsTitle', 'contacts.device.uploadsBody'],
                ['contacts.device.neverTitle', 'contacts.device.neverBody'],
              ] as const
            ).map(([title, body]) => (
              <li key={title} className="flex items-start gap-2 rounded-lg border border-line bg-card px-3 py-2.5">
                <Check size={13} className="mt-0.5 shrink-0" style={{ color: 'var(--green-ink)' }} aria-hidden />
                <span className="min-w-0">
                  <span className="block text-[12.5px] font-semibold text-text-high">{t(title)}</span>
                  <span className="block text-[11.5px] leading-relaxed text-text-muted">{t(body)}</span>
                </span>
              </li>
            ))}
          </ul>
          <p className="text-[11px] leading-relaxed text-text-muted">{t('contacts.device.consentNote')}</p>
          {source === 'web' && <p className="text-[11px] leading-relaxed text-text-muted">{t('contacts.device.webNote')}</p>}
          <div>
            <Button size="sm" onClick={() => void load()} disabled={busy}>
              {t('contacts.device.choose')}
            </Button>
          </div>
        </>
      )}

      {/* reading the address book */}
      {stage === 'loading' && (
        <div className="flex items-center gap-1.5 py-6 text-[12.5px] text-text-mid">
          <Loader2 size={14} className="animate-spin" aria-hidden /> {t('contacts.device.reading')}
        </div>
      )}

      {/* 2 — refused, and only Settings can undo it */}
      {stage === 'blocked' && (
        <div className="flex flex-col gap-2.5 rounded-xl border border-line bg-fill-subtle px-4 py-3.5">
          <div className="text-[13px] font-semibold text-text-high">{t('contacts.device.blockedTitle')}</div>
          <p className="text-[11.5px] leading-relaxed text-text-muted">{t('contacts.device.blockedBody')}</p>
          <p className="text-[11.5px] leading-relaxed text-text-mid">
            {t(isApple() ? 'contacts.device.blockedIos' : 'contacts.device.blockedAndroid')}
          </p>
          <div className="flex flex-wrap gap-2">
            <Button variant="secondary" size="sm" onClick={() => void load()} disabled={busy}>
              {t('contacts.device.retry')}
            </Button>
          </div>
        </div>
      )}

      {/* 3 — pick, with everything that will be uploaded on show */}
      {stage === 'pick' && (
        <>
          {limited && (
            <p className="rounded-lg border border-line bg-fill-subtle px-3 py-2 text-[11.5px] leading-relaxed text-text-muted">
              {t('contacts.device.limited')}
            </p>
          )}
          <label className="relative flex items-center">
            <Search size={13} className="pointer-events-none absolute left-3 text-text-muted" aria-hidden />
            <input
              type="search"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              placeholder={t('contacts.device.searchPlaceholder')}
              aria-label={t('contacts.device.searchPlaceholder')}
              className="h-9 w-full rounded-lg border border-line-strong bg-card pl-8 pr-3 text-[13px] text-text-primary outline-none transition-colors focus:border-text-primary"
            />
          </label>

          <div className="flex flex-wrap items-center justify-between gap-2">
            <span className="text-[12px] font-semibold text-text-mid">
              {t('contacts.device.selectedOf', { count: chosen.size, total: contacts.length })}
            </span>
            <div className="flex flex-wrap gap-2">
              <Button variant="secondary" size="sm" onClick={selectAllVisible} disabled={pickable.length === 0}>
                {allVisibleOn
                  ? t('contacts.device.clearVisible', { count: pickable.length })
                  : t('contacts.device.selectVisible', { count: pickable.length })}
              </Button>
              {chosen.size > 0 && (
                <Button variant="ghost" size="sm" onClick={() => setChosen(new Set())}>
                  {t('contacts.device.clearAll')}
                </Button>
              )}
            </div>
          </div>

          {contacts.length === 0 ? (
            <p className="py-4 text-center text-[12.5px] text-text-muted">{t('contacts.device.empty')}</p>
          ) : visible.length === 0 ? (
            <p className="py-4 text-center text-[12.5px] text-text-muted">{t('contacts.device.noMatch', { query: query.trim() })}</p>
          ) : (
            <div className="max-h-[46vh] overflow-y-auto rounded-xl border border-line">
              <ul className="divide-y divide-line">
                {visible.map((c) => {
                  const can = isImportable(c)
                  return (
                    <li key={c.id}>
                      <label
                        className={`flex items-start gap-2.5 px-3 py-2.5 ${
                          can ? 'cursor-pointer hover:bg-fill-hover' : 'cursor-not-allowed opacity-60'
                        }`}
                      >
                        <input
                          type="checkbox"
                          checked={chosen.has(c.id)}
                          disabled={!can}
                          onChange={() => toggle(c.id)}
                          className="mt-0.5 h-3.5 w-3.5 accent-emerald-500"
                        />
                        <span className="min-w-0 flex-1">
                          <span className="block truncate text-[12.5px] font-semibold text-text-high">{contactLabel(c)}</span>
                          <span className="block truncate text-[11px] text-text-muted">
                            {can
                              ? [...c.phones, ...c.emails].join(' · ')
                              : t('contacts.device.noReach')}
                          </span>
                          {c.company && <span className="block truncate text-[11px] text-text-faint">{c.company}</span>}
                        </span>
                      </label>
                    </li>
                  )
                })}
              </ul>
            </div>
          )}

          {tooMany && (
            <p role="alert" className="text-[11.5px] font-semibold text-amber-300">
              {t('contacts.device.tooMany', { count: MAX_DEVICE_CONTACTS })}
            </p>
          )}
          {/* The wizard's footer drives Back / Cancel; the one action that belongs
              to the picker is the one that uploads something. */}
          <div className="flex items-center gap-2">
            <Button
              size="sm"
              onClick={() => onUse(toImportRows(selected))}
              disabled={busy || chosen.size === 0 || tooMany}
            >
              {busy && <Loader2 size={13} className="animate-spin" aria-hidden />}
              {t('contacts.device.useSelected', { count: chosen.size })}
            </Button>
            <span className="text-[11px] text-text-muted">{t('contacts.device.nextHint')}</span>
          </div>
        </>
      )}
    </div>
  )
}

/** Which Settings path to spell out. Cosmetic only — a wrong guess still reads sensibly. */
function isApple(): boolean {
  if (typeof navigator === 'undefined') return false
  try {
    const cap = (window as unknown as { Capacitor?: { getPlatform?: () => string } }).Capacitor
    if (cap?.getPlatform?.() === 'ios') return true
  } catch {
    /* fall back to the user agent */
  }
  return /iPhone|iPad|iPod/.test(navigator.userAgent)
}
