'use client'

/**
 * /social — "Social posts" (Automate): write a post once and publish it now or
 * schedule it to the social accounts connected through Zernio (Channels →
 * Social accounts), then follow it in a list or a week / month calendar with
 * its status and per-account results; Retry a failed one, Delete one.
 *
 * GET /api/social/accounts?entity=, GET /api/social/posts?entity=&status=&from=&to=,
 * POST /api/social/posts/{id}/retry, DELETE /api/social/posts/{id}. Writing needs
 * `social.publish`; the page itself `social.view` (nav.ts).
 */

import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import Link from 'next/link'
import { AlertCircle, CheckCircle, Loader2, RefreshCw, Share2 } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Modal } from '@/components/ui/Modal'
import { PageHeader } from '@/components/ui/PageHeader'
import { Select } from '@/components/ui/Select'
import { Segmented } from '@/components/chats/Segmented'
import { useAccess } from '@/lib/useAccess'
import { useEntityLevelQuery, useEntityQuery } from '@/lib/EntityContext'
import { dayStartIso, getBusinessTimeZone, getTimeZoneState, todayKey } from '@/lib/timeZone'
import { errMessage, isNotSetUp, socialApi, withQuery, type SocialAccount, type SocialPost } from '@/lib/social'
import { useT } from '@/i18n'
import { PostComposer, draftFromPost, emptyDraft, type ComposerDraft } from './PostComposer'
import { PostCard, STATUS_META, postTime } from './PostParts'
import { PostCalendar, calendarRange } from './PostCalendar'

type View = 'list' | 'week' | 'month'

export function SocialPostsPage() {
  const t = useT()
  const { ready, can } = useAccess()
  const canPublish = can('social.publish')
  const entityQ = useEntityQuery()
  const levelQ = useEntityLevelQuery()

  const [accounts, setAccounts] = useState<SocialAccount[] | null>(null)
  const [posts, setPosts] = useState<SocialPost[] | null>(null)
  const [notSetUp, setNotSetUp] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [view, setView] = useState<View>('list')
  const [status, setStatus] = useState('')
  const [today, setToday] = useState('')
  const [anchor, setAnchor] = useState('')
  const [zone, setZone] = useState<string | undefined>(undefined)
  const [draft, setDraft] = useState<ComposerDraft>(emptyDraft)
  const [busy, setBusy] = useState('')
  const [opened, setOpened] = useState<SocialPost | null>(null)
  const [toDelete, setToDelete] = useState<SocialPost | null>(null)
  const [deleteError, setDeleteError] = useState<string | null>(null)
  const composerRef = useRef<HTMLDivElement>(null)

  // today + the business zone are read after mount (localStorage / the server)
  useEffect(() => {
    const k = todayKey()
    setToday(k)
    setAnchor(k)
    const org = getTimeZoneState().org
    if (org) setZone(org)
    getBusinessTimeZone()
      .then((b) => setZone(b.time_zone || undefined))
      .catch(() => {})
  }, [])

  const loadAccounts = useCallback(async () => {
    try {
      const d = await socialApi<SocialAccount[] | { accounts?: SocialAccount[] }>(withQuery('social/accounts', entityQ))
      setAccounts(Array.isArray(d) ? d : (d.accounts ?? []))
      setNotSetUp(false)
    } catch (e) {
      setAccounts([])
      if (isNotSetUp(e)) setNotSetUp(true)
      else setError(errMessage(e))
    }
  }, [entityQ])

  const loadPosts = useCallback(async () => {
    const range = view === 'list' || !anchor ? null : calendarRange(view, anchor)
    const q = new URLSearchParams()
    if (status) q.set('status', status)
    if (range) {
      const from = dayStartIso(range.start)
      const to = dayStartIso(range.end, 1)
      if (from) q.set('from', from)
      if (to) q.set('to', to)
    }
    const qs = [entityQ, q.toString()].filter(Boolean).join('&')
    try {
      const d = await socialApi<{ posts?: SocialPost[] } | SocialPost[]>(withQuery('social/posts', qs))
      setPosts(Array.isArray(d) ? d : (d.posts ?? []))
    } catch (e) {
      setPosts([])
      if (isNotSetUp(e)) setNotSetUp(true)
      else setError(errMessage(e))
    }
  }, [entityQ, status, view, anchor])

  useEffect(() => {
    if (ready) void loadAccounts()
  }, [ready, loadAccounts])
  useEffect(() => {
    if (ready) void loadPosts()
  }, [ready, loadPosts])

  // a post being published or scheduled soon changes on its own — look again every 30 s
  const live = (posts ?? []).some((p) => p.status === 'publishing' || p.status === 'scheduled')
  useEffect(() => {
    if (!live) return
    const id = window.setInterval(() => void loadPosts(), 30_000)
    return () => window.clearInterval(id)
  }, [live, loadPosts])

  const retry = async (p: SocialPost) => {
    setBusy(`retry-${p.id}`)
    setError(null)
    setNotice(null)
    try {
      await socialApi(`social/posts/${encodeURIComponent(String(p.id))}/retry`, { method: 'POST' })
      setNotice(t('social.posts.retrying'))
      setOpened(null)
      await loadPosts()
    } catch (e) {
      setError(errMessage(e))
    } finally {
      setBusy('')
    }
  }

  const remove = async () => {
    if (!toDelete) return
    setBusy(`rm-${toDelete.id}`)
    setDeleteError(null)
    try {
      await socialApi(`social/posts/${encodeURIComponent(String(toDelete.id))}?confirm=true`, {
        method: 'DELETE',
        body: JSON.stringify({ confirm: true }),
      })
      setNotice(t('social.posts.deleted'))
      setToDelete(null)
      setOpened(null)
      await loadPosts()
    } catch (e) {
      setDeleteError(errMessage(e))
    } finally {
      setBusy('')
    }
  }

  const edit = (p: SocialPost) => {
    setOpened(null)
    setDraft(draftFromPost(p, zone))
    composerRef.current?.scrollIntoView({ behavior: 'smooth', block: 'start' })
  }

  const sorted = useMemo(
    () => [...(posts ?? [])].sort((a, b) => postTime(b).localeCompare(postTime(a))),
    [posts],
  )

  const statusOptions = [
    { value: '', label: t('social.posts.allStatuses') },
    ...Object.entries(STATUS_META)
      .filter(([k]) => k !== 'deleted')
      .map(([k, m]) => ({ value: k, label: t(m.lbl) })),
  ]

  const cardProps = {
    accounts: accounts ?? [],
    canPublish,
    busy,
    onRetry: retry,
    onDelete: (p: SocialPost) => {
      setDeleteError(null)
      setToDelete(p)
    },
    onEdit: edit,
  }

  return (
    <div className="flex shrink-0 flex-col gap-4 lg:min-h-0 lg:flex-1 lg:shrink">
      <PageHeader
        eyebrow={t('shell.groups.automate')}
        title={t('social.page.title')}
        subtitle={t('social.page.subtitle')}
        actions={
          <Button variant="secondary" size="sm" onClick={() => void Promise.all([loadAccounts(), loadPosts()])}>
            <RefreshCw size={13} aria-hidden /> {t('common.actions.refresh')}
          </Button>
        }
      />

      <div className="flex flex-col gap-5 pb-6 lg:min-h-0 lg:flex-1 lg:overflow-y-auto">
        {notice && (
          <div role="status" className="flex items-center gap-1.5 text-[12.5px] font-semibold" style={{ color: 'var(--green-ink)' }}>
            <CheckCircle size={13} aria-hidden /> {notice}
          </div>
        )}
        {error && !toDelete && (
          <div
            role="alert"
            className="flex items-start gap-2 rounded-lg border border-red/35 bg-red/10 px-3 py-2 text-[12.5px]"
            style={{ color: 'var(--red-ink)' }}
          >
            <AlertCircle size={13} className="mt-0.5 shrink-0" aria-hidden /> {error}
          </div>
        )}

        {accounts === null ? (
          <div className="flex items-center gap-2 py-8 text-[12.5px] text-text-muted">
            <Loader2 size={14} className="animate-spin" aria-hidden /> {t('common.status.loading')}
          </div>
        ) : notSetUp ? (
          <div data-tour="social-empty" className="flex max-w-[720px] flex-col items-start gap-2 rounded-xl border border-dashed border-line-strong px-5 py-7">
            <span className="flex items-center gap-2 display text-[19px] text-text-heading">
              <Share2 size={16} aria-hidden /> {t('social.zernio.notSetUpTitle')}
            </span>
            <p className="text-[12.5px] leading-relaxed text-text-muted">{t('social.zernio.notSetUpBody')}</p>
            {can('integrations.view') && (
              <Link href="/integrations" className="text-[12.5px] font-semibold text-brand-300 hover:underline">
                {t('social.zernio.openIntegrations')}
              </Link>
            )}
          </div>
        ) : (
          <>
            {accounts.length === 0 && (
              <div data-tour="social-empty" className="flex max-w-[720px] flex-col items-start gap-2 rounded-xl border border-dashed border-line-strong px-5 py-6">
                <span className="display text-[19px] text-text-heading">{t('social.page.noAccountsTitle')}</span>
                <p className="text-[12.5px] leading-relaxed text-text-muted">{t('social.page.noAccountsBody')}</p>
                <Link href="/channels#social-accounts" className="text-[12.5px] font-semibold text-brand-300 hover:underline">
                  {t('social.page.connectAccounts')}
                </Link>
              </div>
            )}

            <div className="grid grid-cols-1 gap-5 xl:grid-cols-[minmax(360px,440px)_1fr]">
              <div ref={composerRef} className="scroll-mt-4">
                <PostComposer
                  accounts={accounts}
                  draft={draft}
                  onDraft={setDraft}
                  zone={zone}
                  canPublish={canPublish}
                  levelQuery={levelQ}
                  onSaved={(msg) => {
                    setNotice(msg)
                    setError(null)
                    void loadPosts()
                  }}
                />
              </div>

              <section data-tour="social-posts" aria-labelledby="social-posts-h" className="flex min-w-0 flex-col gap-3">
                <div className="flex flex-wrap items-end gap-3">
                  <h2 id="social-posts-h" className="display mr-auto text-[19px] leading-tight text-text-heading">
                    {t('social.posts.title')}
                  </h2>
                  <div className="w-[240px]">
                    <Segmented
                      value={view}
                      onChange={setView}
                      options={[
                        { value: 'list', label: t('social.posts.viewList') },
                        { value: 'week', label: t('social.posts.viewWeek') },
                        { value: 'month', label: t('social.posts.viewMonth') },
                      ]}
                      label={t('social.posts.view')}
                      showLabel={false}
                      dataTour="social-view"
                    />
                  </div>
                  <Select
                    value={status}
                    onChange={setStatus}
                    options={statusOptions}
                    ariaLabel={t('common.fields.status')}
                    size="sm"
                    className="w-[170px]"
                  />
                </div>

                {posts === null ? (
                  <div className="flex items-center gap-2 py-6 text-[12.5px] text-text-muted">
                    <Loader2 size={14} className="animate-spin" aria-hidden /> {t('common.status.loading')}
                  </div>
                ) : view === 'list' ? (
                  sorted.length === 0 ? (
                    <div className="rounded-xl border border-dashed border-line-strong px-5 py-6 text-[12.5px] text-text-muted">
                      {t('social.posts.empty')}
                    </div>
                  ) : (
                    <div className="flex flex-col gap-2.5">
                      {sorted.map((p) => (
                        <PostCard key={String(p.id)} post={p} {...cardProps} />
                      ))}
                    </div>
                  )
                ) : (
                  anchor && (
                    <PostCalendar
                      view={view}
                      anchor={anchor}
                      today={today}
                      posts={sorted}
                      accounts={accounts}
                      onAnchor={setAnchor}
                      onOpen={setOpened}
                    />
                  )
                )}
              </section>
            </div>
          </>
        )}
      </div>

      {/* a post opened from the calendar */}
      <Modal open={!!opened && !toDelete} onClose={() => setOpened(null)} title={t('social.posts.postTitle')} size="md">
        {opened && <PostCard post={opened} {...cardProps} />}
      </Modal>

      <Modal
        open={!!toDelete}
        onClose={() => busy === '' && setToDelete(null)}
        title={t('social.posts.deleteTitle')}
        size="sm"
        footer={
          <>
            <Button variant="secondary" onClick={() => setToDelete(null)} disabled={busy !== ''}>
              {t('common.actions.cancel')}
            </Button>
            <Button variant="danger" onClick={remove} disabled={busy !== ''}>
              {busy.startsWith('rm-') && <Loader2 size={13} className="animate-spin" aria-hidden />}
              {t('common.actions.delete')}
            </Button>
          </>
        }
      >
        {deleteError && (
          <div
            role="alert"
            className="mb-3 rounded-lg border border-red/35 bg-red/10 px-3 py-2 text-[12.5px] font-semibold"
            style={{ color: 'var(--red-ink)' }}
          >
            {deleteError}
          </div>
        )}
        <p className="text-[13px] leading-relaxed text-text-mid">
          {toDelete && (toDelete.status === 'published' || toDelete.status === 'partial')
            ? t('social.posts.deleteBodyPublished')
            : t('social.posts.deleteBody')}
        </p>
      </Modal>
    </div>
  )
}
