'use client'

import { useEffect, useState } from 'react'
import Link from 'next/link'
import { BookOpen, Search } from 'lucide-react'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

/**
 * Public Help Center index. No auth, no operator chrome — this is what a
 * customer sees. Articles come from the same knowledge base the AI reads, so an
 * answer only has to be written once.
 */

interface ArticleSummary {
  slug: string
  title: string
  question: string
  excerpt: string
  category: string
  kind: string
}

interface Category {
  name: string
  articles: ArticleSummary[]
}

export default function HelpIndexPage() {
  const [categories, setCategories] = useState<Category[] | null>(null)
  const [total, setTotal] = useState(0)
  const [q, setQ] = useState('')
  const [failed, setFailed] = useState(false)

  useEffect(() => {
    const url = `${API}/help/articles${q.trim() ? `?q=${encodeURIComponent(q.trim())}` : ''}`
    const id = setTimeout(() => {
      fetch(url)
        .then((r) => (r.ok ? r.json() : Promise.reject(new Error(String(r.status)))))
        .then((d) => {
          setCategories(d.categories ?? [])
          setTotal(d.total ?? 0)
          setFailed(false)
        })
        .catch(() => {
          setCategories([])
          setFailed(true)
        })
    }, 200) // debounce: the search box refetches as you type
    return () => clearTimeout(id)
  }, [q])

  return (
    <main className="mx-auto max-w-[820px] px-5 py-12">
      <header className="mb-8">
        <div className="mb-2 flex items-center gap-2">
          <div className="grid h-9 w-9 place-items-center rounded-xl bg-brand-400/15 text-brand-300">
            <BookOpen size={18} aria-hidden />
          </div>
          <h1 className="text-[26px] font-extrabold tracking-[-0.6px] text-text-heading">Help Center</h1>
        </div>
        <p className="text-[13px] text-text-muted">Answers to the questions we get most.</p>
      </header>

      <label className="mb-8 flex items-center gap-2 rounded-xl border border-line-strong bg-card px-3 py-2.5">
        <Search size={15} className="shrink-0 text-text-muted" aria-hidden />
        <input
          value={q}
          onChange={(e) => setQ(e.target.value)}
          placeholder="Search help articles…"
          aria-label="Search help articles"
          className="w-full bg-transparent text-[13.5px] text-text-primary outline-none placeholder:text-text-faint"
        />
      </label>

      {categories === null ? (
        <p className="text-[13px] text-text-muted">Loading…</p>
      ) : failed ? (
        <p className="text-[13px] text-text-muted">
          The help center is temporarily unavailable. Please try again shortly.
        </p>
      ) : total === 0 ? (
        <p className="text-[13px] text-text-muted">
          {q.trim() ? `Nothing matched “${q.trim()}”.` : 'No published articles yet.'}
        </p>
      ) : (
        <div className="flex flex-col gap-8">
          {categories.map((c) => (
            <section key={c.name}>
              <h2 className="mb-3 text-[11px] font-bold uppercase tracking-[0.08em] text-text-faint">
                {c.name}
              </h2>
              <div className="flex flex-col gap-2">
                {c.articles.map((a) => (
                  <Link
                    key={a.slug}
                    href={`/help/${a.slug}`}
                    className="block rounded-xl border border-line bg-card px-4 py-3 transition-colors hover:border-brand-400/40"
                  >
                    <span className="block text-[14px] font-bold text-text-high">{a.title}</span>
                    <span className="mt-0.5 block text-[12.5px] leading-relaxed text-text-muted">
                      {a.question || a.excerpt}
                    </span>
                  </Link>
                ))}
              </div>
            </section>
          ))}
        </div>
      )}
    </main>
  )
}
