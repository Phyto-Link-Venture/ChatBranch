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

export interface AnalyticsSummary {
  days: number
  since: string
  generated_at: string
  funnel: FunnelStats
  engagement: EngagementStats
  records_by_status: RecordStatusBucket[]
  accounts: AccountVolume[]
  call_outcomes: CallOutcomes
  health_events: HealthEvent[]
  daily: DailyPoint[]
  sessions?: SessionStats // absent on older backends — tiles show em-dashes
}
