'use client'

/**
 * The user manual — PUBLIC, like the rest of /help. Its chapters ARE the page
 * guides (lib/tours.ts + the `guide` messages), so the manual and the in-app
 * guide say the same thing; each chapter's "Show me" opens that page and
 * starts its guide (`?tour=<id>`).
 *
 * Take it away three ways: Download PDF (the browser's print, styled below),
 * Download HTML (one self-contained file, in the reader's language, to host
 * anywhere), or embed this page on a website with the iframe snippet —
 * `?embed=1` drops the download/embed chrome.
 */

import { useEffect, useMemo, useRef, useState } from 'react'
import Link from 'next/link'
import { BookOpenText, Check, Copy, Download, PlayCircle, Printer } from 'lucide-react'
import { LanguageSwitcher } from '@/components/i18n/LanguageSwitcher'
import { TOURS, stepKey, tourKey, type TourDef } from '@/lib/tours'
import { useFormat, useT, type MessageKey } from '@/i18n'

const GROUPS: TourDef['group'][] = ['start', 'work', 'ai', 'setup']

export default function ManualPage() {
  const t = useT()
  const f = useFormat()
  const articleRef = useRef<HTMLElement>(null)
  const [origin, setOrigin] = useState('')
  const [embed, setEmbed] = useState(false)
  const [copied, setCopied] = useState(false)

  useEffect(() => {
    setOrigin(window.location.origin)
    setEmbed(new URLSearchParams(window.location.search).get('embed') === '1')
  }, [])

  const chapters = useMemo(
    () => GROUPS.map((g) => ({ group: g, tours: TOURS.filter((x) => x.group === g) })).filter((g) => g.tours.length > 0),
    [],
  )
  const snippet = `<iframe src="${origin}/help/manual?embed=1" title="${t('guide.manual.title')}" style="width:100%;height:900px;border:0" loading="lazy"></iframe>`

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(snippet)
      setCopied(true)
      window.setTimeout(() => setCopied(false), 1600)
    } catch {
      /* clipboard blocked — the snippet is selectable */
    }
  }

  // One self-contained HTML file of the manual as it reads right now (language included).
  const downloadHtml = () => {
    const body = articleRef.current?.innerHTML ?? ''
    const html = `<!doctype html>
<html lang="${document.documentElement.lang || 'en'}"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${t('guide.manual.title')}</title>
<style>
body{font-family:system-ui,-apple-system,"Segoe UI",Roboto,sans-serif;color:#1f1d1a;background:#fbf8f2;margin:0;line-height:1.6}
main{max-width:780px;margin:0 auto;padding:40px 20px}
h1{font-family:Georgia,serif;font-size:32px;margin:0 0 6px} h2{font-family:Georgia,serif;font-size:22px;margin:36px 0 4px}
h3{font-size:15px;margin:18px 0 2px} p{margin:4px 0 10px;color:#4a463f} ol{padding-left:22px}
a{color:#c4521a} .eyebrow{text-transform:uppercase;letter-spacing:.08em;font-size:11px;font-weight:700;color:#8a8478;margin-top:30px}
nav ul{columns:2;padding-left:18px} .noexport{display:none!important} li{margin:6px 0}
</style></head><body><main>${body}</main></body></html>`
    const url = URL.createObjectURL(new Blob([html], { type: 'text/html' }))
    const a = document.createElement('a')
    a.href = url
    a.download = 'chatbranch-manual.html'
    a.click()
    URL.revokeObjectURL(url)
  }

  return (
    <main className="mx-auto max-w-[860px] px-5 py-10 print:max-w-none print:px-0 print:py-0">
      {!embed && (
        <div className="mb-6 flex flex-wrap items-center gap-2 print:hidden">
          <Link href="/help" className="text-[12.5px] font-semibold text-text-muted hover:text-text-high">
            ← {t('help.title')}
          </Link>
          <div className="ml-auto flex flex-wrap items-center gap-2">
            <LanguageSwitcher />
            <button
              type="button"
              onClick={() => window.print()}
              className="inline-flex h-9 items-center gap-1.5 rounded-lg border border-line-strong bg-card px-3 text-[13px] font-semibold text-text-high hover:bg-fill-hover"
            >
              <Printer size={14} aria-hidden /> {t('guide.manual.downloadPdf')}
            </button>
            <button
              type="button"
              onClick={downloadHtml}
              className="inline-flex h-9 items-center gap-1.5 rounded-lg bg-primary px-3 text-[13px] font-semibold text-on-primary hover:opacity-90"
            >
              <Download size={14} aria-hidden /> {t('guide.manual.downloadHtml')}
            </button>
          </div>
        </div>
      )}

      <article ref={articleRef}>
        <header className="mb-6">
          <div className="mb-2 flex items-center gap-2">
            <span className="noexport grid h-9 w-9 place-items-center rounded-xl bg-brand-400/15 text-[var(--brand-ink)] print:hidden">
              <BookOpenText size={18} aria-hidden />
            </span>
            <h1 className="display text-[30px] leading-tight text-text-heading">{t('guide.manual.title')}</h1>
          </div>
          <p className="text-[14px] text-text-mid">{t('guide.manual.subtitle')}</p>
          <p className="mt-1 text-[11.5px] text-text-faint">{t('guide.manual.generated', { date: f.date(new Date()) })}</p>
        </header>

        <nav aria-label={t('guide.manual.contents')} className="mb-8 rounded-xl border border-line bg-card p-4 print:border-0 print:p-0">
          <div className="eyebrow mb-2">{t('guide.manual.contents')}</div>
          <ul className="grid gap-x-6 gap-y-1 text-[13.5px] sm:grid-cols-2">
            {chapters.flatMap(({ tours }) => tours).map((tour, i) => (
              <li key={tour.id}>
                <a href={`#${tour.id}`} className="text-text-high hover:underline">
                  {i + 1}. {t(tourKey(tour.id, 'title'))}
                </a>
              </li>
            ))}
          </ul>
        </nav>

        {(() => {
          let n = 0
          return chapters.map(({ group, tours }) => (
            <section key={group} className="mb-4">
              <div className="eyebrow mt-8">{t(`guide.manual.groups.${group}` as MessageKey)}</div>
              {tours.map((tour) => {
                n += 1
                return (
                  <section key={tour.id} id={tour.id} className="mt-5 scroll-mt-6 break-inside-avoid-page">
                    <div className="flex flex-wrap items-baseline justify-between gap-2">
                      <h2 className="display text-[22px] leading-tight text-text-heading">
                        {n}. {t(tourKey(tour.id, 'title'))}
                      </h2>
                      {!embed && (
                        <a
                          href={`${tour.href}?tour=${tour.id}`}
                          className="noexport inline-flex items-center gap-1 text-[12.5px] font-semibold text-[var(--brand-ink)] hover:underline print:hidden"
                        >
                          <PlayCircle size={13} aria-hidden /> {t('guide.manual.showMe')}
                        </a>
                      )}
                    </div>
                    <p className="mt-1 text-[13.5px] text-text-mid">{t(tourKey(tour.id, 'intro'))}</p>
                    <ol className="mt-3 flex flex-col gap-3">
                      {tour.steps.map((_, i) => (
                        <li key={i} className="flex gap-3">
                          <span className="grid h-6 w-6 shrink-0 place-items-center rounded-full bg-fill text-[12px] font-semibold text-text-high">
                            {i + 1}
                          </span>
                          <div className="min-w-0">
                            <h3 className="text-[14px] font-semibold text-text-heading">{t(stepKey(tour.id, i + 1, 'title'))}</h3>
                            <p className="text-[13.5px] leading-relaxed text-text-mid">{t(stepKey(tour.id, i + 1, 'body'))}</p>
                          </div>
                        </li>
                      ))}
                    </ol>
                  </section>
                )
              })}
            </section>
          ))
        })()}

        <p className="mt-10 border-t border-line pt-4 text-[12.5px] text-text-muted">{t('guide.manual.footer')}</p>
      </article>

      {!embed && (
        <section className="mt-10 rounded-xl border border-line bg-card p-5 print:hidden">
          <h2 className="display text-[20px] leading-tight text-text-heading">{t('guide.manual.embed')}</h2>
          <p className="mt-1 text-[13px] text-text-muted">{t('guide.manual.embedHelp')}</p>
          <div className="mt-3 flex items-start gap-2">
            <pre className="min-w-0 flex-1 overflow-x-auto whitespace-pre-wrap break-all rounded-lg bg-well p-3 font-mono text-[11.5px] text-text-mid">
              {snippet}
            </pre>
            <button
              type="button"
              onClick={copy}
              className="inline-flex h-9 shrink-0 items-center gap-1.5 rounded-lg border border-line-strong px-3 text-[12.5px] font-semibold text-text-high hover:bg-fill-hover"
            >
              {copied ? <Check size={13} aria-hidden /> : <Copy size={13} aria-hidden />}
              {copied ? t('guide.manual.copied') : t('guide.manual.copy')}
            </button>
          </div>
          <p className="mt-2 text-[12px] text-text-muted">{t('guide.manual.standaloneHelp')}</p>
        </section>
      )}
    </main>
  )
}
