'use client'

/**
 * /channels → "Social accounts (via Zernio)": the social profiles connected for
 * the selected entity (each one also becomes an inbox for its comments, reviews
 * and DMs), a Connect button per network, and Disconnect.
 *
 * GET /api/social/accounts?entity=, POST /api/social/connect {platform, entity}
 * → {url} (Zernio's hosted connect page; it sends the person back to
 * /channels?zernio=connected), DELETE /api/social/accounts/{id}.
 * Connecting and disconnecting need `social.manage`.
 */

import { useCallback, useEffect, useRef, useState } from 'react'
import Link from 'next/link'
import { AlertCircle, CheckCircle, ExternalLink, Loader2, Plus, Share2, Unplug } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { ChannelLogo } from '@/components/ui/ChannelLogo'
import { Modal } from '@/components/ui/Modal'
import { useAccess } from '@/lib/useAccess'
import { useEntity, useEntityQuery } from '@/lib/EntityContext'
import {
  PLATFORMS,
  accountHandle,
  errMessage,
  isNotSetUp,
  platformId,
  platformLabel,
  socialApi,
  withQuery,
  type SocialAccount,
} from '@/lib/social'
import { useT, type MessageKey } from '@/i18n'

/** The capability chips an account shows, in order. */
const CAPS: { key: keyof SocialAccount; lbl: MessageKey }[] = [
  { key: 'can_publish', lbl: 'social.accounts.capPublish' },
  { key: 'can_comments', lbl: 'social.accounts.capComments' },
  { key: 'can_reviews', lbl: 'social.accounts.capReviews' },
  { key: 'can_dm', lbl: 'social.accounts.capDm' },
]

export function SocialAccountsSection() {
  const t = useT()
  const tRef = useRef(t)
  tRef.current = t
  const { ready, can } = useAccess()
  const canManage = can('social.manage')
  const { levelEntity, multipleEntities } = useEntity()
  const entityQ = useEntityQuery()

  const [accounts, setAccounts] = useState<SocialAccount[] | null>(null)
  const [notSetUp, setNotSetUp] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState('')
  const [toRemove, setToRemove] = useState<SocialAccount | null>(null)
  const [removeError, setRemoveError] = useState<string | null>(null)

  const load = useCallback(async () => {
    try {
      const d = await socialApi<SocialAccount[] | { accounts?: SocialAccount[] }>(withQuery('social/accounts', entityQ))
      setAccounts(Array.isArray(d) ? d : (d.accounts ?? []))
      setNotSetUp(false)
      setError(null)
    } catch (e) {
      setAccounts([])
      if (isNotSetUp(e)) setNotSetUp(true)
      else setError(errMessage(e))
    }
  }, [entityQ])

  useEffect(() => {
    if (ready) void load()
  }, [ready, load])

  // Zernio's connect page sends the person back with ?zernio=connected (or an error)
  useEffect(() => {
    if (typeof window === 'undefined') return
    const q = new URLSearchParams(window.location.search)
    const z = q.get('zernio')
    if (!z) return
    if (z === 'connected') setNotice(tRef.current('social.accounts.connectedNotice'))
    else setError(tRef.current('social.accounts.connectFailed', { reason: q.get('error') || z }))
    q.delete('zernio')
    q.delete('error')
    const rest = q.toString()
    window.history.replaceState({}, '', `${window.location.pathname}${rest ? `?${rest}` : ''}#social-accounts`)
    document.getElementById('social-accounts')?.scrollIntoView({ block: 'start' })
  }, [])

  const connect = async (platform: string) => {
    setError(null)
    setNotice(null)
    setBusy(`connect-${platform}`)
    try {
      const d = await socialApi<{ url?: string }>('social/connect', {
        method: 'POST',
        body: JSON.stringify({ platform, entity: levelEntity?.slug ?? undefined }),
      })
      if (!d.url) throw new Error(t('social.accounts.noUrl'))
      window.location.href = d.url
    } catch (e) {
      if (isNotSetUp(e)) setNotSetUp(true)
      else setError(errMessage(e))
      setBusy('')
    }
  }

  const disconnect = async () => {
    if (!toRemove) return
    setBusy(`rm-${toRemove.id}`)
    setRemoveError(null)
    try {
      await socialApi(`social/accounts/${encodeURIComponent(toRemove.id)}?confirm=true`, {
        method: 'DELETE',
        body: JSON.stringify({ confirm: true }),
      })
      setNotice(t('social.accounts.disconnected', { name: accountHandle(toRemove) }))
      setToRemove(null)
      await load()
    } catch (e) {
      setRemoveError(errMessage(e))
    } finally {
      setBusy('')
    }
  }

  return (
    <section id="social-accounts" data-tour="channels-social" className="flex scroll-mt-4 flex-col gap-3" aria-labelledby="social-accounts-h">
      <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
        <h2 id="social-accounts-h" className="display text-[22px] leading-tight text-text-heading">
          {t('social.accounts.title')}
        </h2>
        <span className="text-[12.5px] text-text-muted">{t('social.accounts.hint')}</span>
      </div>

      {notice && (
        <div role="status" className="flex items-center gap-1.5 text-[12.5px] font-semibold" style={{ color: 'var(--green-ink)' }}>
          <CheckCircle size={13} aria-hidden /> {notice}
        </div>
      )}
      {error && (
        <div
          role="alert"
          className="flex items-start gap-2 rounded-lg border border-red/35 bg-red/10 px-3 py-2 text-[12.5px]"
          style={{ color: 'var(--red-ink)' }}
        >
          <AlertCircle size={13} className="mt-0.5 shrink-0" aria-hidden /> {error}
        </div>
      )}

      {accounts === null ? (
        <div className="flex items-center gap-2 py-4 text-[12.5px] text-text-muted">
          <Loader2 size={14} className="animate-spin" aria-hidden /> {t('common.status.loading')}
        </div>
      ) : notSetUp ? (
        <div className="flex flex-col items-start gap-2 rounded-xl border border-dashed border-line-strong px-5 py-6">
          <span className="flex items-center gap-2 text-[15px] font-bold text-text-heading">
            <Share2 size={15} aria-hidden /> {t('social.zernio.notSetUpTitle')}
          </span>
          <p className="max-w-[600px] text-[12.5px] leading-relaxed text-text-muted">{t('social.zernio.notSetUpBody')}</p>
          {can('integrations.view') && (
            <Link href="/integrations" className="text-[12.5px] font-semibold text-brand-300 hover:underline">
              {t('social.zernio.openIntegrations')}
            </Link>
          )}
        </div>
      ) : (
        <>
          {accounts.length === 0 ? (
            <div className="rounded-xl border border-dashed border-line-strong px-5 py-5 text-[12.5px] text-text-muted">
              {t('social.accounts.empty')}
            </div>
          ) : (
            <div className="grid grid-cols-1 gap-3 md:grid-cols-2 min-[1360px]:grid-cols-3">
              {accounts.map((a) => (
                <div key={a.id} className="flex flex-col gap-2.5 rounded-xl border border-line bg-card p-4">
                  <div className="flex items-start gap-2.5">
                    <span className="relative shrink-0">
                      {a.avatar ? (
                        // eslint-disable-next-line @next/next/no-img-element
                        <img src={a.avatar} alt="" className="h-10 w-10 rounded-full object-cover" />
                      ) : (
                        <ChannelLogo channel={platformId(a.platform)} variant="tile" size={40} title="" />
                      )}
                      {a.avatar && (
                        <span className="absolute -bottom-1 -right-1 grid place-items-center rounded-full bg-card p-px">
                          <ChannelLogo channel={platformId(a.platform)} size={15} title={platformLabel(a.platform)} />
                        </span>
                      )}
                    </span>
                    <div className="min-w-0 flex-1">
                      <div className="flex items-center gap-1.5">
                        <span className="truncate text-[14px] font-bold text-text-heading">{accountHandle(a)}</span>
                        {a.active === false && (
                          <span className="shrink-0 rounded-full bg-amber/15 px-2 py-px text-[10px] font-bold text-amber-300">
                            {t('social.accounts.inactive')}
                          </span>
                        )}
                      </div>
                      <div className="truncate text-[12px] text-text-muted">
                        {platformLabel(a.platform)}
                        {a.display_name && a.username ? ` · ${a.display_name}` : ''}
                      </div>
                    </div>
                  </div>
                  <div className="flex flex-wrap gap-1">
                    {CAPS.filter((c) => a[c.key]).map((c) => (
                      <span key={c.key} className="rounded-full border border-line-strong px-2 py-px text-[10.5px] font-semibold text-text-mid">
                        {t(c.lbl)}
                      </span>
                    ))}
                  </div>
                  <div className="mt-auto flex flex-wrap items-center gap-2 pt-0.5">
                    {a.profile_url && (
                      <a
                        href={a.profile_url}
                        target="_blank"
                        rel="noreferrer"
                        className="inline-flex items-center gap-1 text-[12px] font-semibold text-text-mid hover:text-text-high"
                      >
                        <ExternalLink size={12} aria-hidden /> {t('social.accounts.viewProfile')}
                      </a>
                    )}
                    {canManage && (
                      <Button
                        variant="ghost"
                        size="sm"
                        className="ml-auto"
                        onClick={() => {
                          setRemoveError(null)
                          setToRemove(a)
                        }}
                      >
                        <Unplug size={12} aria-hidden /> {t('common.actions.disconnect')}
                      </Button>
                    )}
                  </div>
                </div>
              ))}
            </div>
          )}

          {/* Connect a network */}
          <div className="flex flex-col gap-2">
            <h3 className="eyebrow">{t('social.accounts.connectTitle')}</h3>
            {!canManage ? (
              <span className="text-[11.5px] text-text-muted">{t('social.accounts.needsPermission')}</span>
            ) : multipleEntities ? (
              <span className="text-[11.5px]" style={{ color: 'var(--amber-ink)' }}>
                {t('social.accounts.pickOne')}
              </span>
            ) : (
              <>
                <span className="text-[11.5px] text-text-faint">
                  {levelEntity
                    ? t('social.accounts.connectFor', { name: levelEntity.name })
                    : t('social.accounts.connectForOrg')}
                </span>
                <div className="flex flex-wrap gap-2">
                  {PLATFORMS.map((p) => (
                    <button
                      key={p.id}
                      type="button"
                      onClick={() => connect(p.id)}
                      disabled={busy !== ''}
                      className="inline-flex h-9 cursor-pointer items-center gap-2 rounded-lg border border-line-strong bg-card px-3 text-[12.5px] font-semibold text-text-high transition-colors hover:bg-fill-hover disabled:cursor-not-allowed disabled:opacity-50"
                    >
                      {busy === `connect-${p.id}` ? (
                        <Loader2 size={14} className="animate-spin" aria-hidden />
                      ) : (
                        <ChannelLogo channel={p.id} size={15} title="" />
                      )}
                      {p.label}
                      <Plus size={12} className="text-text-faint" aria-hidden />
                    </button>
                  ))}
                </div>
              </>
            )}
          </div>
        </>
      )}

      <Modal
        open={!!toRemove}
        onClose={() => busy === '' && setToRemove(null)}
        title={toRemove ? t('social.accounts.disconnectTitle', { name: accountHandle(toRemove) }) : ''}
        size="sm"
        footer={
          <>
            <Button variant="secondary" onClick={() => setToRemove(null)} disabled={busy !== ''}>
              {t('common.actions.cancel')}
            </Button>
            <Button variant="danger" onClick={disconnect} disabled={busy !== ''}>
              {busy.startsWith('rm-') && <Loader2 size={13} className="animate-spin" aria-hidden />}
              {t('common.actions.disconnect')}
            </Button>
          </>
        }
      >
        {removeError && (
          <div
            role="alert"
            className="mb-3 rounded-lg border border-red/35 bg-red/10 px-3 py-2 text-[12.5px] font-semibold"
            style={{ color: 'var(--red-ink)' }}
          >
            {removeError}
          </div>
        )}
        <p className="text-[13px] leading-relaxed text-text-mid">{t('social.accounts.disconnectBody')}</p>
      </Modal>
    </section>
  )
}
