'use client'

import { useEffect, useState } from 'react'
import { authHeaders, expireSessionOn401 } from '@/lib/auth'
import { Card, CardTitle } from '@/components/ui/Card'

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

const FEATURE_LABELS: Record<string, string> = {
  chat_reply: 'Chat replies',
  voice_call: 'Voice calls',
  summary: 'AI summaries',
  timeline: 'Timeline summaries',
  playground: 'Playground',
}

const tok = (r: UsageTokens) =>
  r.prompt_tokens + r.completion_tokens + r.audio_prompt_tokens + r.audio_completion_tokens

const num = (n: number) =>
  n >= 1_000_000 ? `${(n / 1_000_000).toFixed(2)}M` : n >= 10_000 ? `${(n / 1000).toFixed(1)}k` : n.toLocaleString('en-US')

const usd = (n: number) => `$${n >= 100 ? n.toFixed(0) : n >= 1 ? n.toFixed(2) : n.toFixed(4)}`

function MiniStat({ label, value, sub }: { label: string; value: string; sub?: string }) {
  return (
    <div className="min-w-0 bg-card px-4 py-3">
      <div className="text-[12px] font-semibold text-text-muted">{label}</div>
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
  return (
    <div>
      <div className="eyebrow mb-1.5">{title}</div>
      {rows.length === 0 ? (
        <div className="text-[12px] text-text-muted">Nothing recorded in this window yet.</div>
      ) : (
        <table className="w-full text-[11.5px]">
          <tbody>
            {rows.map((r) => (
              <tr key={r.key} className="border-b border-line last:border-0">
                <td className="max-w-[180px] truncate py-1.5 pr-2 text-text-mid">
                  {r.label}
                  {r.flag && <span className="ml-1.5 text-[10.5px] text-[color:var(--amber-ink)]">{r.flag}</span>}
                </td>
                <td className="py-1.5 pr-2 text-right tabular-nums text-text-muted">{num(r.tokens)} tok</td>
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
  const [data, setData] = useState<AiUsage | null>(null)
  const [failed, setFailed] = useState(false)

  useEffect(() => {
    let cancelled = false
    fetch(`${API}/analytics/usage?days=${days}`, { headers: authHeaders() })
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
  }, [days])

  if (failed || data === null) return null

  const t = data.totals
  const audioTokens = t.audio_prompt_tokens + t.audio_completion_tokens
  return (
    <Card className="px-5 py-[18px]">
      <CardTitle>AI usage &amp; estimated cost</CardTitle>
      <div className="mb-4 mt-1 text-[12px] text-text-muted">
        Every LLM call metered (replies, summaries, voice calls). Costs are estimates from the
        price table — verify against your provider&rsquo;s billing before invoicing.
      </div>

      <div className="mb-5 grid grid-cols-2 gap-px overflow-hidden rounded-lg border border-line bg-line md:grid-cols-4">
        <MiniStat label="Est. cost" value={usd(t.est_cost_usd)} sub={`last ${data.days} days`} />
        <MiniStat label="Total tokens" value={num(tok(t))} sub={`${num(t.prompt_tokens)} in · ${num(t.completion_tokens)} out`} />
        <MiniStat label="Audio tokens" value={num(audioTokens)} sub="realtime voice calls" />
        <MiniStat label="AI requests" value={t.requests.toLocaleString('en-US')} sub="LLM invocations" />
      </div>

      <div className="grid gap-5 lg:grid-cols-3">
        <UsageTable
          title="By feature"
          rows={data.by_feature.map((r) => ({
            key: r.feature,
            label: FEATURE_LABELS[r.feature] ?? r.feature,
            tokens: tok(r),
            cost: r.est_cost_usd,
          }))}
        />
        <UsageTable
          title="By model"
          rows={data.by_model.map((r) => ({
            key: `${r.provider}/${r.model}`,
            label: r.model || '(unknown model)',
            tokens: tok(r),
            cost: r.est_cost_usd,
            flag: r.priced ? undefined : 'unpriced',
          }))}
        />
        <UsageTable
          title="Top contacts (billing)"
          rows={data.by_contact.map((r) => ({
            key: String(r.contact_id ?? 'none'),
            label: r.name || '(no contact)',
            tokens: tok(r),
            cost: r.est_cost_usd,
          }))}
        />
      </div>
    </Card>
  )
}
