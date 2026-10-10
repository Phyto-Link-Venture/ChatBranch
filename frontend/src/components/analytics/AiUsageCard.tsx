'use client'

import { useEffect, useState } from 'react'
import { authHeaders, expireSessionOn401 } from '@/lib/auth'
import { Card, CardTitle } from '@/components/ui/Card'
import { useFormat, useT, type MessageKey } from '@/i18n'
import { useEntityQuery } from '@/lib/EntityContext'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

interface UsageTokens {
  prompt_tokens: number
  completion_tokens: number
  audio_prompt_tokens: number
  audio_completion_tokens: number
  requests: number
  est_cost_usd: number
}

interface AiUsage {
  days: number
  totals: UsageTokens
  by_feature: (UsageTokens & { feature: string })[]
  by_model: (UsageTokens & { model: string; provider: string; priced: boolean })[]
  by_contact: (UsageTokens & { contact_id: number | null; name: string })[]
  pricing_note: string
}

const FEATURE_LABELS: Record<string, MessageKey> = {
  chat_reply: 'analytics.usage.features.chatReply',
  voice_call: 'analytics.usage.features.voiceCall',
  summary: 'analytics.usage.features.summary',
  timeline: 'analytics.usage.features.timeline',
  playground: 'analytics.usage.features.playground',
}

const tok = (r: UsageTokens) =>
  r.prompt_tokens + r.completion_tokens + r.audio_prompt_tokens + r.audio_completion_tokens

/** Token counts (compact past 10k) and USD costs (more decimals for small sums), in the UI locale. */
function useUsageFormat() {
  const f = useFormat()
  return {
    num: (n: number) =>
      n >= 10_000 ? f.number(n, { notation: 'compact', maximumFractionDigits: n >= 1_000_000 ? 2 : 1 }) : f.number(n),
    usd: (n: number) =>
      f.number(n, {
        style: 'currency',
        currency: 'USD',
        minimumFractionDigits: n >= 100 ? 0 : n >= 1 ? 2 : 4,
        maximumFractionDigits: n >= 100 ? 0 : n >= 1 ? 2 : 4,
      }),
  }
}

function MiniStat({ label, value, sub }: { label: string; value: string; sub?: string }) {
  return (
    <div className="min-w-0 bg-card px-4 py-3">
      <div className="truncate text-[12px] font-semibold text-text-muted">{label}</div>
      <div className="display mt-1 text-[24px] leading-none text-text-heading">{value}</div>
      {sub && <div className="mt-1 truncate text-[11.5px] text-text-muted">{sub}</div>}
    </div>
  )
}

function UsageTable({
  title,
  rows,
}: {
  title: string
  rows: { key: string; label: string; tokens: number; cost: number; flag?: string }[]
}) {
  const t = useT()
  const { num, usd } = useUsageFormat()
  return (
    <div>
      <div className="eyebrow mb-1.5">{title}</div>
      {rows.length === 0 ? (
        <div className="text-[12px] text-text-muted">{t('analytics.usage.empty')}</div>
      ) : (
        <table className="w-full text-[11.5px]">
          <tbody>
            {rows.map((r) => (
              <tr key={r.key} className="border-b border-line last:border-0">
                <td className="max-w-[180px] truncate py-1.5 pr-2 text-text-mid">
                  {r.label}
                  {r.flag && <span className="ml-1.5 text-[10.5px] text-[color:var(--amber-ink)]">{r.flag}</span>}
                </td>
                <td className="py-1.5 pr-2 text-right tabular-nums text-text-muted">
                  {t('analytics.usage.tokensShort', { n: num(r.tokens) })}
                </td>
                <td className="py-1.5 text-right font-semibold tabular-nums text-text-high">{usd(r.cost)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  )
}

/**
 * Analytics → AI usage: the LLM metering/billing card. GET /api/analytics/usage
 * (crm.LlmUsage ledger). Dollar figures are ESTIMATES from the llm_prices
 * table — verify against provider billing before invoicing a customer.
 * Renders nothing while the endpoint is missing/unreachable, so the page
 * stays whole if the backend is older.
 */
export function AiUsageCard({ days }: { days: number }) {
  const tr = useT()
  const f = useFormat()
  const { num, usd } = useUsageFormat()
  const [data, setData] = useState<AiUsage | null>(null)
  const [failed, setFailed] = useState(false)
  // follows the entity switcher like every other Dashboard card
  const entityQ = useEntityQuery()

  useEffect(() => {
    let cancelled = false
    fetch(`${API}/analytics/usage?days=${days}${entityQ && `&${entityQ}`}`, { headers: authHeaders() })
      .then((r) => {
        if (expireSessionOn401(r)) return null
        if (!r.ok) throw new Error(`HTTP ${r.status}`)
        return r.json()
      })
      .then((d) => {
        if (cancelled || !d) return
        setData(d as AiUsage)
        setFailed(false)
      })
      .catch(() => !cancelled && setFailed(true))
    return () => {
      cancelled = true
    }
  }, [days, entityQ])

  if (failed || data === null) return null

  const t = data.totals
  const audioTokens = t.audio_prompt_tokens + t.audio_completion_tokens
  return (
    <Card className="px-5 py-[18px]">
      <CardTitle>{tr('analytics.usage.title')}</CardTitle>
      <div className="mb-4 mt-1 text-[12px] text-text-muted">{tr('analytics.usage.subtitle')}</div>

      <div className="mb-5 grid grid-cols-2 gap-px overflow-hidden rounded-lg border border-line bg-line md:grid-cols-4">
        <MiniStat
          label={tr('analytics.usage.estCost')}
          value={usd(t.est_cost_usd)}
          sub={tr('analytics.kpi.lastDays', { count: data.days })}
        />
        <MiniStat
          label={tr('analytics.usage.totalTokens')}
          value={num(tok(t))}
          sub={tr('analytics.usage.inOut', { in: num(t.prompt_tokens), out: num(t.completion_tokens) })}
        />
        <MiniStat label={tr('analytics.usage.audioTokens')} value={num(audioTokens)} sub={tr('analytics.usage.audioSub')} />
        <MiniStat label={tr('analytics.usage.requests')} value={f.number(t.requests)} sub={tr('analytics.usage.requestsSub')} />
      </div>

      <div className="grid gap-5 lg:grid-cols-3">
        <UsageTable
          title={tr('analytics.usage.byFeature')}
          rows={data.by_feature.map((r) => ({
            key: r.feature,
            label: FEATURE_LABELS[r.feature] ? tr(FEATURE_LABELS[r.feature]) : r.feature,
            tokens: tok(r),
            cost: r.est_cost_usd,
          }))}
        />
        <UsageTable
          title={tr('analytics.usage.byModel')}
          rows={data.by_model.map((r) => ({
            key: `${r.provider}/${r.model}`,
            label: r.model || tr('analytics.usage.unknownModel'),
            tokens: tok(r),
            cost: r.est_cost_usd,
            flag: r.priced ? undefined : tr('analytics.usage.unpriced'),
          }))}
        />
        <UsageTable
          title={tr('analytics.usage.topContacts')}
          rows={data.by_contact.map((r) => ({
            key: String(r.contact_id ?? 'none'),
            label: r.name || tr('analytics.usage.noContact'),
            tokens: tok(r),
            cost: r.est_cost_usd,
          }))}
        />
      </div>
    </Card>
  )
}
