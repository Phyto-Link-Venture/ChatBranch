'use client'

/**
 * Zernio-connected social accounts as cards in /channels' ONE Connected list
 * (they used to have their own "Social accounts (via Zernio)" section).
 *
 * GET /api/social/accounts?entity= lists them; DELETE /api/social/accounts/{id}
 * disconnects. Connecting happens in the "Add inbox" stepper
 * (components/channels/ZernioConnectPanel). Needs `social.manage` to change.
 *
 * Outlets: each card binds its account to an outlet (PATCH /api/social/accounts/{id}
 * {entity}, null = organization level, which every outlet inherits); a Facebook
 * account lists its Pages, each with its own outlet (PATCH …/pages/{pageId}
 * {entity}, null = follow the account) and the Page that receives comments and
 * messages (POST …/pages/{pageId}/select). Without `social.manage` the outlet
 * shows as a chip.
 */

import { useCallback, useEffect, useState } from 'react'
import { Check, ExternalLink, Inbox, Loader2, Plug, Settings2, Unplug } from 'lucide-react'
import type { ReactNode } from 'react'
import { Button } from '@/components/ui/Button'
import { EntityPicker } from '@/components/ui/EntityPicker'
import { ChannelLogo } from '@/components/ui/ChannelLogo'
import { Modal } from '@/components/ui/Modal'
import { useAccess } from '@/lib/useAccess'
import { useEntity, useEntityQuery, type Entity } from '@/lib/EntityContext'
import {
  accountHandle,
  errMessage,
  isNotSetUp,
  platformId,
  platformLabel,
  socialApi,
  withQuery,
  type SocialAccount,
  type SocialPage,
} from '@/lib/social'
import { useT, type MessageKey } from '@/i18n'
import { CapChip, HealthDot, MethodBadge, OutletChip, cardBtn } from './parts'
import type { HealthStatus } from '@/lib/channelHealth'
import { ZERNIO_BADGE } from './catalog'

/** The capability chips an account shows, in order. */
const CAPS: { key: keyof SocialAccount; lbl: MessageKey }[] = [
  { key: 'can_publish', lbl: 'social.accounts.capPublish' },
  { key: 'can_comments', lbl: 'social.accounts.capComments' },
  { key: 'can_reviews', lbl: 'social.accounts.capReviews' },
  { key: 'can_dm', lbl: 'social.accounts.capDm' },
]

/** Saving state of one outlet control (an account's, or one of its Pages'). */
type SaveState = { state: 'saving' | 'saved' | 'error'; error?: string }

/** The entity with this slug and every entity under it. */
function subtree(entities: Entity[], slug: string): Entity[] {
  const keep = new Set([slug])
  for (let grew = true; grew; ) {
    grew = false
    for (const e of entities)
      if (e.parent && keep.has(e.parent) && !keep.has(e.slug)) {
        keep.add(e.slug)
        grew = true
      }
  }
  return entities.filter((e) => keep.has(e.slug))
}

function SaveNote({ s }: { s?: SaveState }) {
  const t = useT()
  if (!s) return null
  if (s.state === 'saving')
    return (
      <span role="status" className="inline-flex items-center gap-1 text-[11px] text-text-muted">
        <Loader2 size={11} className="animate-spin" aria-hidden /> {t('social.accounts.saving')}
      </span>
    )
  if (s.state === 'saved')
    return (
      <span role="status" className="inline-flex items-center gap-1 text-[11px] font-semibold" style={{ color: 'var(--green-ink)' }}>
        <Check size={11} aria-hidden /> {t('social.accounts.saved')}
      </span>
    )
  return (
    <span role="alert" className="text-[11px] font-semibold" style={{ color: 'var(--red-ink)' }}>
      {s.error || t('social.accounts.saveFailed')}
    </span>
  )
}

/** The Zernio accounts for the entity switcher's view, with their outlet edits and disconnect. */
export function useSocialAccounts() {
  const t = useT()
  const { ready, can } = useAccess()
  const canManage = can('social.manage')
  const canView = can('social.view') || can('channels.view')
  const { entities } = useEntity()
  const entityQ = useEntityQuery()

  const [accounts, setAccounts] = useState<SocialAccount[] | null>(null)
  const [notSetUp, setNotSetUp] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [saves, setSaves] = useState<Record<string, SaveState>>({})
  const [toRemove, setToRemove] = useState<SocialAccount | null>(null)
  const [removeError, setRemoveError] = useState<string | null>(null)
  const [removing, setRemoving] = useState(false)

  const load = useCallback(async () => {
    try {
      const d = await socialApi<SocialAccount[] | { accounts?: SocialAccount[] }>(withQuery('social/accounts', entityQ))
      setAccounts(Array.isArray(d) ? d : (d.accounts ?? []))
      setNotSetUp(false)
      setError(null)
    } catch (e) {
      setAccounts([])
      // Zernio not set up (or an older backend) is a quiet state, not an error
      if (isNotSetUp(e)) setNotSetUp(true)
      else setError(errMessage(e))
    }
  }, [entityQ])

  useEffect(() => {
    if (!ready) return
    if (canView) void load()
    else setAccounts([])
  }, [ready, canView, load])

  /** Run one outlet change: saving → saved / error, then refresh the list. */
  const save = async (key: string, path: string, init: RequestInit) => {
    setSaves((m) => ({ ...m, [key]: { state: 'saving' } }))
    try {
      await socialApi(path, init)
      setSaves((m) => ({ ...m, [key]: { state: 'saved' } }))
      await load()
      window.setTimeout(
        () =>
          setSaves((m) => {
            if (m[key]?.state !== 'saved') return m
            const rest = { ...m }
            delete rest[key]
            return rest
          }),
        2500,
      )
    } catch (e) {
      setSaves((m) => ({ ...m, [key]: { state: 'error', error: errMessage(e) } }))
    }
  }

  const assignAccount = (a: SocialAccount, slug: string | null) =>
    save(`acct-${a.id}`, `social/accounts/${encodeURIComponent(a.id)}`, {
      method: 'PATCH',
      body: JSON.stringify({ entity: slug }),
    })

  const assignPage = (a: SocialAccount, p: SocialPage, slug: string | null) =>
    save(`page-${a.id}-${p.id}`, `social/accounts/${encodeURIComponent(a.id)}/pages/${encodeURIComponent(p.id)}`, {
      method: 'PATCH',
      body: JSON.stringify({ entity: slug }),
    })

  const selectPage = (a: SocialAccount, p: SocialPage) =>
    save(`page-${a.id}-${p.id}`, `social/accounts/${encodeURIComponent(a.id)}/pages/${encodeURIComponent(p.id)}/select`, {
      method: 'POST',
    })

  const orgName = t('social.accounts.orgLevel')
  const outletName = (x: { entity_id?: number | null; entity_name?: string | null }) =>
    x.entity_id == null ? orgName : (x.entity_name ?? entities.find((e) => e.id === x.entity_id)?.name ?? `#${x.entity_id}`)

  /** Disconnect the account in `toRemove`; resolves to its handle when done. */
  const disconnect = async (): Promise<string | null> => {
    if (!toRemove) return null
    setRemoving(true)
    setRemoveError(null)
    try {
      await socialApi(`social/accounts/${encodeURIComponent(toRemove.id)}?confirm=true`, {
        method: 'DELETE',
        body: JSON.stringify({ confirm: true }),
      })
      const name = accountHandle(toRemove)
      setToRemove(null)
      await load()
      return name
    } catch (e) {
      setRemoveError(errMessage(e))
      return null
    } finally {
      setRemoving(false)
    }
  }

  return {
    accounts,
    notSetUp,
    error,
    load,
    canManage,
    saves,
    assignAccount,
    assignPage,
    selectPage,
    orgName,
    outletName,
    toRemove,
    askRemove: (a: SocialAccount | null) => {
      setRemoveError(null)
      setToRemove(a)
    },
    removeError,
    removing,
    disconnect,
  }
}

export type SocialAccountsState = ReturnType<typeof useSocialAccounts>

/**
 * The native DM inbox behind the same managed account (Instagram / Messenger DMs:
 * a second ChannelAccount sharing zernio_account_id). It is shown INSIDE the
 * account's card — one card per account — with the inbox's own Settings / Test.
 * Its outlet follows the account's (the backend moves both together), and
 * Disconnect removes both.
 */
export interface LinkedInbox {
  health: { status: HealthStatus; detail: string } | null
  /** Team · AI on/off, already translated. */
  meta: string[]
  /** The Test result line (or a spinner), when a test ran. */
  test?: ReactNode
  testing?: boolean
  onSettings?: () => void
  onTest: () => void
}

/** One Zernio account in the Connected list: method badge, outlet picker, Facebook Pages, capabilities. */
export function SocialAccountCard({ a, s, inbox }: { a: SocialAccount; s: SocialAccountsState; inbox?: LinkedInbox }) {
  const t = useT()
  const { entities, bound } = useEntity()
  const { canManage, saves } = s
  const pid = platformId(a.platform)
  const canEditOutlet = canManage && (!bound || a.entity_id != null)
  return (
    <article className="flex min-w-0 flex-col gap-3 rounded-xl border border-line bg-card p-4">
      <div className="flex items-start gap-3">
        <span className="relative shrink-0">
          {a.avatar ? (
            // eslint-disable-next-line @next/next/no-img-element
            <img src={a.avatar} alt="" className="h-9 w-9 rounded-full object-cover" />
          ) : (
            <ChannelLogo channel={pid} variant="tile" size={36} title="" />
          )}
          {a.avatar && (
            <span className="absolute -bottom-1 -right-1 grid place-items-center rounded-full bg-card p-px">
              <ChannelLogo channel={pid} size={15} title={platformLabel(a.platform)} />
            </span>
          )}
        </span>
        <div className="min-w-0 flex-1">
          <div className="flex min-w-0 flex-wrap items-center gap-1.5">
            <span className="truncate text-[14px] font-bold text-text-heading">{accountHandle(a)}</span>
            <MethodBadge label={ZERNIO_BADGE} zernio />
          </div>
          <div className="truncate text-[12px] text-text-muted">
            {platformLabel(a.platform)}
            {a.display_name && a.username ? ` · ${a.display_name}` : ''}
          </div>
        </div>
      </div>

      <div className="flex flex-wrap items-center gap-x-2 gap-y-1 text-[12px] text-text-muted">
        {a.active === false ? (
          <HealthDot status="disconnected" detail={t('social.accounts.inactive')} />
        ) : a.active === true ? (
          <HealthDot status="healthy" />
        ) : (
          <span>{t('channels.card.notMonitored')}</span>
        )}
      </div>

      {/* outlet: which outlet the account belongs to (organization = every outlet) */}
      <div data-tour="channels-card-outlet" className="flex flex-col gap-1">
        <span className="text-[10.5px] font-bold uppercase tracking-wide text-text-faint">{t('social.accounts.outlet')}</span>
        {canEditOutlet ? (
          <div className="flex flex-wrap items-center gap-2">
            <EntityPicker
              value={a.entity_slug ?? (a.entity_id != null ? (entities.find((e) => e.id === a.entity_id)?.slug ?? null) : null)}
              onChange={(slug) => void s.assignAccount(a, slug)}
              allowGlobal={!bound}
              globalLabel={s.orgName}
              size="sm"
              className="min-w-0 flex-1"
              ariaLabel={t('social.accounts.outletFor', { name: accountHandle(a) })}
              disabled={saves[`acct-${a.id}`]?.state === 'saving'}
            />
            <SaveNote s={saves[`acct-${a.id}`]} />
          </div>
        ) : (
          <OutletChip name={s.outletName(a)} />
        )}
      </div>

      {/* a Facebook login's Pages: each one posts for its own outlet */}
      {pid === 'facebook' && a.pages && a.pages.length > 0 && (
        <div className="flex flex-col gap-1.5">
          <span className="text-[10.5px] font-bold uppercase tracking-wide text-text-faint">
            {t('social.accounts.pagesTitle', { count: a.pages.length })}
          </span>
          <ul className="flex flex-col gap-1.5">
            {a.pages.map((p) => {
              const key = `page-${a.id}-${p.id}`
              const accountSlug = a.entity_slug ?? null
              const choices = accountSlug ? subtree(entities, accountSlug) : entities
              const followLabel = t('social.accounts.pageFollows', { name: s.outletName(a) })
              return (
                <li key={p.id} className="flex flex-col gap-1 rounded-lg border border-line px-2.5 py-2">
                  <div className="flex flex-wrap items-center gap-1.5">
                    <span className="min-w-0 truncate text-[12.5px] font-semibold text-text-high">{p.name || p.id}</span>
                    {p.username && <span className="truncate text-[11px] text-text-muted">@{p.username}</span>}
                    {p.selected ? (
                      <span
                        className="ml-auto inline-flex shrink-0 items-center gap-1 rounded-full bg-green/15 px-2 py-px text-[10px] font-bold"
                        style={{ color: 'var(--green-ink)' }}
                        title={t('social.accounts.pageInboxHint')}
                      >
                        <Inbox size={10} aria-hidden /> {t('social.accounts.pageInbox')}
                      </span>
                    ) : (
                      canEditOutlet && (
                        <button
                          type="button"
                          onClick={() => void s.selectPage(a, p)}
                          disabled={saves[key]?.state === 'saving'}
                          title={t('social.accounts.pageInboxHint')}
                          className="ml-auto shrink-0 cursor-pointer text-[11px] font-semibold text-brand-300 hover:underline disabled:cursor-not-allowed disabled:opacity-50"
                        >
                          {t('social.accounts.pageUseForInbox')}
                        </button>
                      )
                    )}
                  </div>
                  {canManage && (!bound || p.effective_entity_id != null) ? (
                    <div className="flex flex-wrap items-center gap-2">
                      <EntityPicker
                        value={p.entity_id != null ? (p.entity_slug ?? null) : null}
                        onChange={(slug) => void s.assignPage(a, p, slug === accountSlug ? null : slug)}
                        entities={choices}
                        globalLabel={followLabel}
                        size="sm"
                        className="min-w-0 flex-1"
                        ariaLabel={t('social.accounts.outletFor', { name: p.name || p.id })}
                        disabled={saves[key]?.state === 'saving'}
                      />
                      <SaveNote s={saves[key]} />
                    </div>
                  ) : (
                    <OutletChip name={s.outletName({ entity_id: p.effective_entity_id, entity_name: p.entity_name })} />
                  )}
                </li>
              )
            })}
          </ul>
        </div>
      )}

      {inbox && (
        <div className="flex flex-col gap-1.5 rounded-lg border border-line px-2.5 py-2">
          <div className="flex flex-wrap items-center gap-1.5">
            <Inbox size={12} className="text-text-muted" aria-hidden />
            <span className="text-[12.5px] font-semibold text-text-high" title={t('social.accounts.dmInboxHint')}>
              {t('social.accounts.dmInbox')}
            </span>
          </div>
          <div className="flex flex-wrap items-center gap-x-2 gap-y-1 text-[12px] text-text-muted">
            {inbox.health ? (
              <HealthDot status={inbox.health.status} detail={inbox.health.detail} />
            ) : (
              <span>{t('channels.card.notMonitored')}</span>
            )}
            {inbox.meta.map((m) => (
              <span key={m} className="inline-flex items-center gap-2">
                <span aria-hidden>·</span>
                <span className="truncate">{m}</span>
              </span>
            ))}
          </div>
          {inbox.test}
          <div className="flex flex-wrap items-center gap-2">
            {inbox.onSettings && (
              <button type="button" className={cardBtn} onClick={inbox.onSettings}>
                <Settings2 size={13} aria-hidden /> {t('channels.card.settings')}
              </button>
            )}
            <button type="button" className={cardBtn} onClick={inbox.onTest} disabled={inbox.testing}>
              <Plug size={13} aria-hidden /> {t('channels.card.test')}
            </button>
          </div>
        </div>
      )}

      {CAPS.some((c) => a[c.key] || (inbox && c.key === 'can_dm')) && (
        <div className="flex flex-wrap gap-1" aria-label={t('channels.card.capabilities')}>
          {CAPS.filter((c) => a[c.key] || (inbox && c.key === 'can_dm')).map((c) => (
            <CapChip key={c.key} label={t(c.lbl)} />
          ))}
        </div>
      )}

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
          <Button variant="ghost" size="sm" className="ml-auto" onClick={() => s.askRemove(a)}>
            <Unplug size={12} aria-hidden /> {t('common.actions.disconnect')}
          </Button>
        )}
      </div>
    </article>
  )
}

/** "Disconnect @handle?" — errors render inside the modal. */
export function SocialDisconnectModal({ s, onDone }: { s: SocialAccountsState; onDone: (name: string) => void }) {
  const t = useT()
  const { toRemove, removing, removeError } = s
  return (
    <Modal
      open={!!toRemove}
      onClose={() => !removing && s.askRemove(null)}
      title={toRemove ? t('social.accounts.disconnectTitle', { name: accountHandle(toRemove) }) : ''}
      size="sm"
      footer={
        <>
          <Button variant="secondary" onClick={() => s.askRemove(null)} disabled={removing}>
            {t('common.actions.cancel')}
          </Button>
          <Button
            variant="danger"
            disabled={removing}
            onClick={async () => {
              const name = await s.disconnect()
              if (name) onDone(name)
            }}
          >
            {removing && <Loader2 size={13} className="animate-spin" aria-hidden />}
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
  )
}
