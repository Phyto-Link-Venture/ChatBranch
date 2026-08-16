'use client'

import { useEffect, useState } from 'react'
import { authHeaders, expireSessionOn401 } from '@/lib/auth'

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
    <div className="rounded-xl border border-line bg-fill-subtle px-3.5 py-2.5">
      <div className="text-[10.5px] font-semibold uppercase tracking-wide text-text-faint">{label}</div>
      <div className="mt-0.5 text-[17px] font-bold text-text-high">{value}</div>
      {sub && <div className="text-[10.5px] text-text-muted">{sub}</div>}
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
      <div className="mb-1.5 text-[11.5px] font-bold text-text-high">{title}</div>
      {rows.length === 0 ? (
        <div className="text-[11px] text-text-faint">Nothing recorded in this window yet.</div>
      ) : (
        <table className="w-full text-[11.5px]">
          <tbody>
            {rows.map((r) => (
              <tr key={r.key} className="border-b border-line last:border-0">
                <td className="max-w-[180px] truncate py-1.5 pr-2 text-text-mid">
                  {r.label}
                  {r.flag && <span className="ml-1.5 text-[10px] text-amber-300">{r.flag}</span>}
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
    <div className="rounded-2xl border border-line bg-card px-5 py-[18px]">
      <div className="mb-0.5">
        <span className="text-[13px] font-bold text-text-high">AI usage &amp; estimated cost</span>
      </div>
      <div className="mb-4 text-[11px] text-text-muted">
        Every LLM call metered (replies, summaries, voice calls). Costs are estimates from the
        price table — verify against your provider&rsquo;s billing before invoicing.
      </div>

      <div className="mb-4 grid grid-cols-2 gap-2.5 md:grid-cols-4">
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
    </div>
  )
}
