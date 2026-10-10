'use client'

/**
 * AI monitor — see (and, by policy, approve) what the AI says before it
 * reaches a customer, review everything it did, and tune how human it acts.
 * Tabs: Waiting for review · AI activity · Learning (lessons from 👍/👎 +
 * retrieval status) · AI review (on-demand: a stronger model checks past
 * replies, <AiReviewPanel />) · Stats · Settings (the reusable
 * <AiBehaviourSettings />). The tab lives in ?tab= so a refresh or a link from
 * a notification keeps the place; lists refresh live off the inbox SSE stream.
 */

import { useCallback, useEffect, useState } from 'react'
import Link from 'next/link'
import { FlaskRound, ScanEye } from 'lucide-react'
import { AreaPage } from '@/components/shell/AreaPage'
import { Card } from '@/components/ui/Card'
import { ReviewQueue } from '@/components/ai-monitor/ReviewQueue'
import { ActivityList } from '@/components/ai-monitor/ActivityList'
import { StatsPanel } from '@/components/ai-monitor/StatsPanel'
import { AiBehaviourSettings } from '@/components/ai-monitor/AiBehaviourSettings'
import { LearningPanel } from '@/components/ai-learning/LearningPanel'
import { AiReviewPanel } from '@/components/ai-monitor/AiReviewPanel'
import { AiMasterSwitch } from '@/components/ai-controls/AiMasterSwitch'
import { MultiEntityNotice } from '@/components/shell/MultiEntityNotice'
import { HandoverStats } from '@/components/ai-controls/HandoverStats'
import { hasCap } from '@/lib/auth'
import { useEntity, useEntityQuery } from '@/lib/EntityContext'
import { subscribeChatStream } from '@/lib/chatStream'
import { aiApi, type BehaviourPayload, type DraftList } from '@/lib/aiMonitor'
import { useT, type MessageKey, type TFunction } from '@/i18n'

type Tab = 'review' | 'activity' | 'learning' | 'ai-review' | 'stats' | 'settings'
const TABS: { id: Tab; label: MessageKey }[] = [
  { id: 'review', label: 'ai.monitor.tabs.review' },
  { id: 'activity', label: 'ai.monitor.tabs.activity' },
  { id: 'learning', label: 'ai.monitor.tabs.learning' },
  { id: 'ai-review', label: 'ai.monitor.tabs.aiReview' },
  { id: 'stats', label: 'ai.monitor.tabs.stats' },
  { id: 'settings', label: 'shell.nav.settings' },
]

const MODE_LABEL: Record<string, MessageKey> = {
  auto: 'ai.monitor.mode.auto',
  review_flagged: 'ai.monitor.mode.reviewFlagged',
  review_all: 'ai.monitor.mode.reviewAll',
  shadow: 'ai.monitor.mode.shadow',
}

/** The review policy in plain words ("only risky replies are held, sent automatically after 10 min…"). */
function modeText(t: TFunction, mode: string, autosend: number): string {
  const base = MODE_LABEL[mode] ? t(MODE_LABEL[mode]) : mode
  return (mode === 'review_all' || mode === 'review_flagged') && autosend > 0
    ? t('ai.monitor.mode.withAutosend', { mode: base, minutes: autosend })
    : base
}

export default function AiMonitorPage() {
  const t = useT()
  const entityQ = useEntityQuery()
  const { activeEntity } = useEntity()
  const [tab, setTab] = useState<Tab>('review')
  const [access, setAccess] = useState<{ ready: boolean; view: boolean }>({ ready: false, view: false })
  const [pending, setPending] = useState<number | null>(null)
  // the policy as read from the server; worded at render so a language switch re-words it
  const [policy, setPolicy] = useState<{ mode: string; autosend: number } | null>(null)
  const modeLabel = policy ? modeText(t, policy.mode, policy.autosend) : undefined

  // read the tab + capabilities after mount (localStorage / URL — hydration-safe)
  useEffect(() => {
    const q = new URLSearchParams(window.location.search).get('tab') as Tab | null
    if (q && TABS.some((x) => x.id === q)) setTab(q)
    setAccess({ ready: true, view: hasCap('ai.monitor') || hasCap('ai.behaviour.manage') })
  }, [])

  const choose = (next: Tab) => {
    setTab(next)
    const url = new URL(window.location.href)
    url.searchParams.set('tab', next)
    window.history.replaceState(null, '', url.toString())
  }

  const loadCounts = useCallback(async () => {
    try {
      const d = await aiApi<DraftList>(`ai/monitor?status=pending&page_size=1${entityQ ? `&${entityQ}` : ''}`)
      setPending(d.total)
    } catch {
      setPending(null)
    }
  }, [entityQ])

  useEffect(() => {
    if (!access.view) return
    void loadCounts()
    let timer: number | undefined
    const unsub = subscribeChatStream(() => {
      window.clearTimeout(timer)
      timer = window.setTimeout(() => void loadCounts(), 500)
    })
    return () => {
      unsub()
      window.clearTimeout(timer)
    }
  }, [access.view, loadCounts])

  useEffect(() => {
    if (!access.view) return
    aiApi<BehaviourPayload>(`ai/behaviour${entityQ ? `?${entityQ}` : ''}`)
      .then((p) => {
        const get = (k: string) => p.fields.find((f) => f.key === k)?.value ?? ''
        setPolicy({ mode: get('ai_review_mode'), autosend: Number(get('ai_review_autosend_minutes')) })
      })
      .catch(() => setPolicy(null))
  }, [access.view, entityQ, tab])

  return (
    <AreaPage
      title={t('shell.nav.aiMonitor')}
      eyebrow={t('shell.groups.automate')}
      Icon={ScanEye}
      actions={
        access.view ? (
          <Link
            href="/ai-playground"
            data-tour="ai-playground-link"
            className="inline-flex h-8 items-center gap-1.5 rounded-lg border border-line-strong bg-card px-3 text-[12.5px] font-semibold text-text-high transition-colors hover:bg-fill-hover"
          >
            <FlaskRound size={13} aria-hidden /> {t('playground.monitorLink')}
          </Link>
        ) : undefined
      }
      subtitle={
        modeLabel
          ? activeEntity
            ? t('ai.monitor.rightNowEntity', { mode: modeLabel, entity: activeEntity.name })
            : t('ai.monitor.rightNow', { mode: modeLabel })
          : t('ai.monitor.subtitle')
      }
    >
      {access.ready && !access.view ? (
        <Card className="px-5 py-6 text-[13px] text-text-muted">
          {t('ai.monitor.noAccess')}
        </Card>
      ) : (
        <div className="flex flex-col gap-4">
          {access.ready && (
            <div data-tour="ai-master">
              <AiMasterSwitch />
            </div>
          )}
          <div className="-mx-1 overflow-x-auto px-1">
            <div role="tablist" data-tour="ai-monitor-tabs" aria-label={t('ai.monitor.tabsLabel')} className="flex min-w-max gap-5 border-b border-line">
              {TABS.map((x) => {
                const active = tab === x.id
                return (
                  <button
                    key={x.id}
                    data-tour={`ai-tab-${x.id}`}
                    role="tab"
                    id={`aim-tab-${x.id}`}
                    aria-selected={active}
                    aria-controls={`aim-panel-${x.id}`}
                    onClick={() => choose(x.id)}
                    className={`-mb-px flex items-center gap-1.5 border-b-2 pb-2 pt-1 text-[13.5px] font-semibold transition-colors ${
                      active ? 'border-text-primary text-text-heading' : 'border-transparent text-text-muted hover:text-text-high'
                    }`}
                  >
                    {t(x.label)}
                    {x.id === 'review' && pending != null && pending > 0 && (
                      <span className="rounded-full bg-accent px-1.5 text-[11px] font-bold leading-[18px] text-[color:var(--on-accent)]">
                        {pending}
                      </span>
                    )}
                  </button>
                )
              })}
            </div>
          </div>

          <div role="tabpanel" id={`aim-panel-${tab}`} aria-labelledby={`aim-tab-${tab}`} className="min-w-0">
            {access.ready && tab === 'review' && (
              <ReviewQueue
                entityQuery={entityQ}
                canDecide={hasCap('ai.monitor')}
                modeLabel={modeLabel}
                onOpenSettings={() => choose('settings')}
              />
            )}
            {access.ready && tab === 'activity' && <ActivityList entityQuery={entityQ} canDecide={hasCap('ai.monitor')} />}
            {access.ready && tab === 'learning' && (
              <LearningPanel entityQuery={entityQ} entitySlug={activeEntity?.slug ?? null} entityName={activeEntity?.name ?? null} />
            )}
            {access.ready && tab === 'ai-review' && <AiReviewPanel entityQuery={entityQ} />}
            {access.ready && tab === 'stats' && <StatsPanel entityQuery={entityQ} />}
            {access.ready && tab === 'stats' && <div className="mt-4"><HandoverStats entityQuery={entityQ} /></div>}
            {access.ready && tab === 'settings' && (
              <>
                <MultiEntityNotice className="mb-3" />
                <AiBehaviourSettings initialEntity={activeEntity?.slug ?? null} />
              </>
            )}
          </div>
        </div>
      )}
    </AreaPage>
  )
}
