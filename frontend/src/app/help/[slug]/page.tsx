'use client'

import { API } from '@/lib/apiBase'
import { useEffect, useState } from 'react'
import Link from 'next/link'
import { useParams } from 'next/navigation'
import { ArrowLeft } from 'lucide-react'
import { LanguageSwitcher } from '@/components/i18n/LanguageSwitcher'
import { useT } from '@/i18n'

interface Related {
  slug: string
  title: string
}

interface Article {
  slug: string
  title: string
  question: string
  content: string
  category: string
  updated_at: string
  related: Related[]
}

/** One public help article. Content is plain text, rendered with its line breaks. */
export default function HelpArticlePage() {
  const t = useT()
  const params = useParams<{ slug: string }>()
  const slug = typeof params?.slug === 'string' ? params.slug : ''
  const [article, setArticle] = useState<Article | null>(null)
  const [missing, setMissing] = useState(false)

  useEffect(() => {
    if (!slug) return
    fetch(`${API}/help/articles/${encodeURIComponent(slug)}`)
      .then((r) => (r.ok ? r.json() : Promise.reject(new Error(String(r.status)))))
      .then(setArticle)
      .catch(() => setMissing(true))
  }, [slug])

  return (
    <main className="mx-auto max-w-[720px] px-5 py-12">
      <Link
        href="/help"
        className="mb-6 inline-flex items-center gap-1.5 text-[12.5px] font-semibold text-text-muted transition-colors hover:text-text-high"
      >
        <ArrowLeft size={14} aria-hidden /> {t('help.allArticles')}
      </Link>

      {missing ? (
        <p className="text-[13px] text-text-muted">{t('help.missing')}</p>
      ) : !article ? (
        <p className="text-[13px] text-text-muted">{t('common.status.loading')}</p>
      ) : (
        <article>
          <span className="text-[11px] font-bold uppercase tracking-[0.08em] text-text-faint">
            {article.category}
          </span>
          <h1 className="mb-3 mt-1 text-[24px] font-extrabold leading-tight tracking-[-0.5px] text-text-heading">
            {article.title}
          </h1>
          {article.question && (
            <p className="mb-4 text-[14px] font-semibold text-text-mid">{article.question}</p>
          )}
          <div className="whitespace-pre-wrap text-[14px] leading-[1.75] text-text-primary">
            {article.content}
          </div>

          {article.related.length > 0 && (
            <section className="mt-10 border-t border-line pt-6">
              <h2 className="mb-3 text-[11px] font-bold uppercase tracking-[0.08em] text-text-faint">
                {t('help.related')}
              </h2>
              <div className="flex flex-col gap-1.5">
                {article.related.map((r) => (
                  <Link
                    key={r.slug}
                    href={`/help/${r.slug}`}
                    className="text-[13px] font-semibold text-brand-300 hover:underline"
                  >
                    {r.title}
                  </Link>
                ))}
              </div>
            </section>
          )}
        </article>
      )}
      <LanguageSwitcher className="mt-12" />
    </main>
  )
}
