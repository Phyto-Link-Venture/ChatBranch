'use client'

/**
 * AI monitor — see (and, by policy, approve) what the AI says before it
 * reaches a customer, review everything it did, and tune how human it acts.
 * Tabs: Waiting for review · AI activity · Learning (lessons from 👍/👎 +
 * retrieval status) · Stats · Settings (the reusable
 * <AiBehaviourSettings />). The tab lives in ?tab= so a refresh or a link from
 * a notification keeps the place; lists refresh live off the inbox SSE stream.
 */

import { useCallback, useEffect, useState } from 'react'
import { ScanEye } from 'lucide-react'
import { AreaPage } from '@/components/shell/AreaPage'
import { Card } from '@/components/ui/Card'
import { ReviewQueue } from '@/components/ai-monitor/ReviewQueue'
import { ActivityList } from '@/components/ai-monitor/ActivityList'
import { StatsPanel } from '@/components/ai-monitor/StatsPanel'
import { AiBehaviourSettings } from '@/components/ai-monitor/AiBehaviourSettings'
import { LearningPanel } from '@/components/ai-learning/LearningPanel'
import { AiMasterSwitch } from '@/components/ai-controls/AiMasterSwitch'
import { HandoverStats } from '@/components/ai-controls/HandoverStats'
import { hasCap } from '@/lib/auth'
import { useEntity, useEntityQuery } from '@/lib/EntityContext'
import { subscribeChatStream } from '@/lib/chatStream'
import { aiApi, type BehaviourPayload, type DraftList } from '@/lib/aiMonitor'

type Tab = 'review' | 'activity' | 'learning' | 'stats' | 'settings'
const TABS: { id: Tab; label: string }[] = [
  { id: 'review', label: 'Waiting for review' },
  { id: 'activity', label: 'AI activity' },
  { id: 'learning', label: 'Learning' },
  { id: 'stats', label: 'Stats' },
  { id: 'settings', label: 'Settings' },
]

const MODE_LABEL: Record<string, string> = {
  auto: 'the AI sends straight away',
  review_flagged: 'only risky replies are held',
  review_all: 'every AI reply is held for a person',
  shadow: 'shadow mode — the AI drafts but never sends',
}

export default function AiMonitorPage() {
  const entityQ = useEntityQuery()
  const { activeEntity } = useEntity()
  const [tab, setTab] = useState<Tab>('review')
  const [access, setAccess] = useState<{ ready: boolean; view: boolean }>({ ready: false, view: false })
  const [pending, setPending] = useState<number | null>(null)
  const [modeLabel, setModeLabel] = useState<string | undefined>()

  // read the tab + capabilities after mount (localStorage / URL — hydration-safe)
  useEffect(() => {
    const t = new URLSearchParams(window.location.search).get('tab') as Tab | null
    if (t && TABS.some((x) => x.id === t)) setTab(t)
    setAccess({ ready: true, view: hasCap('ai.monitor') || hasCap('ai.behaviour.manage') })
  }, [])

  const choose = (t: Tab) => {
    setTab(t)
    const url = new URL(window.location.href)
    url.searchParams.set('tab', t)
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
    let t: number | undefined
    const unsub = subscribeChatStream(() => {
      window.clearTimeout(t)
      t = window.setTimeout(() => void loadCounts(), 500)
    })
    return () => {
      unsub()
      window.clearTimeout(t)
    }
  }, [access.view, loadCounts])

  useEffect(() => {
    if (!access.view) return
    aiApi<BehaviourPayload>(`ai/behaviour${entityQ ? `?${entityQ}` : ''}`)
      .then((p) => {
        const get = (k: string) => p.fields.find((f) => f.key === k)?.value ?? ''
        const mode = get('ai_review_mode')
        const autosend = Number(get('ai_review_autosend_minutes'))
        setModeLabel(
          (MODE_LABEL[mode] ?? mode) +
            ((mode === 'review_all' || mode === 'review_flagged') && autosend > 0
              ? `, sent automatically after ${autosend} min if nobody acts`
              : ''),
        )
      })
      .catch(() => setModeLabel(undefined))
  }, [access.view, entityQ, tab])

  return (
    <AreaPage
      title="AI monitor"
      eyebrow="Automate"
      Icon={ScanEye}
      subtitle={
        modeLabel
          ? `Right now ${modeLabel}${activeEntity ? ` (${activeEntity.name})` : ''}.`
          : 'See what the AI says before it reaches your customers.'
      }
    >
      {access.ready && !access.view ? (
        <Card className="px-5 py-6 text-[13px] text-text-muted">
          You don’t have access to the AI monitor. Ask an admin for the “See &amp; approve AI drafts” permission.
        </Card>
      ) : (
        <div className="flex flex-col gap-4">
          {access.ready && <AiMasterSwitch />}
          <div className="-mx-1 overflow-x-auto px-1">
            <div role="tablist" aria-label="AI monitor sections" className="flex min-w-max gap-5 border-b border-line">
              {TABS.map((t) => {
                const active = tab === t.id
                return (
                  <button
                    key={t.id}
                    role="tab"
                    id={`aim-tab-${t.id}`}
                    aria-selected={active}
                    aria-controls={`aim-panel-${t.id}`}
                    onClick={() => choose(t.id)}
                    className={`-mb-px flex items-center gap-1.5 border-b-2 pb-2 pt-1 text-[13.5px] font-semibold transition-colors ${
                      active ? 'border-text-primary text-text-heading' : 'border-transparent text-text-muted hover:text-text-high'
                    }`}
                  >
                    {t.label}
                    {t.id === 'review' && pending != null && pending > 0 && (
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
            {access.ready && tab === 'stats' && <StatsPanel entityQuery={entityQ} />}
            {access.ready && tab === 'stats' && <div className="mt-4"><HandoverStats entityQuery={entityQ} /></div>}
            {access.ready && tab === 'settings' && <AiBehaviourSettings initialEntity={activeEntity?.slug ?? null} />}
          </div>
        </div>
      )}
    </AreaPage>
  )
}
