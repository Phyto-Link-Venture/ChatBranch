/** Response shape of GET /api/analytics/summary (see backend calls/views.py
 *  analytics_summary docstring — keys are snake_case on the wire). */

export interface FunnelStats {
  sent: number
  delivered: number
  read: number
  replied: number
  delivered_rate: number
  read_rate: number
  reply_rate: number
}

export interface EngagementStats {
  contacted: number
  resolved: number
}

export interface RecordStatusBucket {
  status: string
  count: number
  amount: number
}

export interface AccountVolume {
  account_id: string | null
  name: string
  channel: string | null
  provider: string | null
  health_status: string
  outbound: number
  inbound: number
}

export interface CallOutcomes {
  total: number
  needs_human: number
  outcomes: { outcome: string; count: number }[]
}

export interface HealthEvent {
  actor: string
  target: string
  detail: string
  created_at: string
}

export interface DailyPoint {
  date: string
  outbound: number
  inbound: number
  calls: number
  record_events: number
}

export interface SessionStats {
  avg_call_seconds: number
  calls_counted: number
  chat_sessions: number
  avg_chat_messages: number
  avg_chat_session_seconds: number
}

/** One outlet's headline figures in the All-entities (franchise) view. */
export interface EntityBreakdown {
  entity: string | null // slug; null = the "Unassigned" bucket
  name: string
  outbound: number
  inbound: number
  contacts: number
  calls: number
}

/** Median wait from a customer's message to the first reply after it. */
export interface FirstResponse {
  median_seconds: number | null
  samples: number
}

/** The window of equal length just before this one (KPI deltas). */
export interface PriorWindow {
  since: string
  sent: number
  read_rate: number
  reply_rate: number
  first_response_median_seconds: number | null
}

export interface AnalyticsSummary {
  days: number
  since: string
  generated_at: string
  /** Slug these figures are scoped to; null = every entity. */
  entity?: string | null
  entity_name?: string
  funnel: FunnelStats
  engagement: EngagementStats
  records_by_status: RecordStatusBucket[]
  accounts: AccountVolume[]
  call_outcomes: CallOutcomes
  health_events: HealthEvent[]
  daily: DailyPoint[]
  sessions?: SessionStats // absent on older backends — tiles show em-dashes
  /** Per-outlet rows — only sent in the All-entities view. */
  by_entity?: EntityBreakdown[]
  first_response?: FirstResponse // absent on older backends
  prior?: PriorWindow
}
