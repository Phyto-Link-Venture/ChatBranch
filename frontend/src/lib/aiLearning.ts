/**
 * Client + types for AI learning: 👍/👎 feedback (/api/ai/feedback), lessons
 * (/api/ai/lessons), the learning status / stats (/api/ai/learning/*) and the
 * knowledge-base retrieval helpers (upload + preview under /api/aicore/knowledge).
 *
 * Every helper here is written to FAIL SOFT: the reads used to paint optional
 * UI (thumbs on bubbles, the "Based on" line, stats) resolve to an empty value
 * instead of throwing, so a server without these endpoints simply shows less.
 * Writes still throw an ApiError so the caller can show the reason in place.
 */
import type { MessageKey, TFunction } from '@/i18n'
import { channelLabel } from '@/components/ui/ChannelLogo'
import { authHeaders, expireSessionOn401 } from './auth'
import { ApiError, aiApi, type Draft } from './aiMonitor'

/** A channel's name for the AI pages: brand names as they are; email / website widget / voice calls translated. */
export function aiChannelLabel(c: string, t: TFunction): string {
  if (c === 'email') return t('ai.channels.email')
  if (c === 'website') return t('ai.channels.website')
  if (c === 'voice') return t('ai.channels.voice')
  return channelLabel(c)
}

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

/* ---------- feedback ---------- */

export type FeedbackReason = 'wrong_info' | 'didnt_answer' | 'tone' | 'too_long' | 'should_handover' | 'other'

/** The 👎 reasons, in display order — ids are the wire values, labels message keys. */
export const FEEDBACK_REASONS: { id: FeedbackReason; label: MessageKey }[] = [
  { id: 'wrong_info', label: 'ai.feedback.reasons.wrongInfo' },
  { id: 'didnt_answer', label: 'ai.feedback.reasons.didntAnswer' },
  { id: 'tone', label: 'ai.feedback.reasons.tone' },
  { id: 'too_long', label: 'ai.feedback.reasons.tooLong' },
  { id: 'should_handover', label: 'ai.feedback.reasons.shouldHandover' },
  { id: 'other', label: 'ai.feedback.reasons.other' },
]

export function reasonLabel(id: string, t: TFunction): string {
  const r = FEEDBACK_REASONS.find((x) => x.id === id)
  return r ? t(r.label) : id.replace(/_/g, ' ')
}

/** The caller's own rating of one AI message / draft. */
export interface MyFeedback {
  id?: number
  rating: 1 | -1
  reasons: string[]
  note: string
  correction: string
}

export interface FeedbackWrite {
  message_id?: number
  draft_id?: number
  rating: 1 | -1
  reasons?: string[]
  note?: string
  correction?: string
}

export interface FeedbackSaved {
  id: number
  rating: 1 | -1
  reasons: string[]
  note: string
  correction: string
  lesson_id: number | null
  lesson_status: LessonStatus | null
}

export type FeedbackTarget = { message_id: number } | { draft_id: number }

function normFeedback(f: Partial<MyFeedback> | null | undefined): MyFeedback | null {
  if (!f || (f.rating !== 1 && f.rating !== -1)) return null
  return {
    id: typeof f.id === 'number' ? f.id : undefined,
    rating: f.rating,
    reasons: Array.isArray(f.reasons) ? f.reasons.filter((r): r is string => typeof r === 'string') : [],
    note: typeof f.note === 'string' ? f.note : '',
    correction: typeof f.correction === 'string' ? f.correction : '',
  }
}

export const feedbackApi = {
  /** Rate (or re-rate — one rating per user per message/draft). Throws on failure. */
  submit: async (target: FeedbackTarget, body: Omit<FeedbackWrite, 'message_id' | 'draft_id'>): Promise<FeedbackSaved> => {
    const r = await aiApi<{ feedback: FeedbackSaved }>('ai/feedback', {
      method: 'POST',
      body: JSON.stringify({ ...target, ...body }),
    })
    return r.feedback
  },
  /** Remove the caller's own rating. Throws on failure. */
  remove: (id: number) => aiApi<unknown>(`ai/feedback/${id}`, { method: 'DELETE' }),
  /**
   * The caller's ratings for these messages, keyed by message id. Resolves
   * `null` (not `{}`) when the endpoint is missing or refused, so a caller can
   * tell "nothing rated yet" from "this server can't take ratings".
   */
  mine: async (messageIds: number[]): Promise<Record<number, MyFeedback> | null> => {
    if (!messageIds.length) return {}
    try {
      const r = await aiApi<{ feedback?: Record<string, Partial<MyFeedback>> }>(
        `ai/feedback?message_ids=${messageIds.join(',')}`,
      )
      const out: Record<number, MyFeedback> = {}
      for (const [k, v] of Object.entries(r.feedback ?? {})) {
        const n = normFeedback(v)
        if (n && Number.isFinite(Number(k))) out[Number(k)] = n
      }
      return out
    } catch {
      return null
    }
  },
}

/** The database id of a chat message payload: `pk` (WhatsApp payloads) or the
 *  `db-<id>` form every channel uses for a message with no provider id. */
export function messagePk(m: { id: string | number; pk?: unknown }): number | null {
  if (typeof m.pk === 'number' && Number.isFinite(m.pk)) return m.pk
  if (typeof m.pk === 'string' && /^\d+$/.test(m.pk)) return Number(m.pk)
  if (typeof m.id === 'number') return m.id
  const match = /^db-(\d+)$/.exec(m.id)
  return match ? Number(match[1]) : null
}

/* ---------- grounding (what a reply was based on) ---------- */

export interface Grounding {
  type: 'knowledge' | 'lesson' | string
  id: number
  title: string
  chunk?: string
  score?: number
}

/** AI-monitor draft fields this feature adds (absent on older servers). */
export type LearningDraft = Draft & { my_feedback?: Partial<MyFeedback> | null; grounding?: Grounding[] | null }

export function draftFeedback(d: Draft): MyFeedback | null {
  return normFeedback((d as LearningDraft).my_feedback ?? null)
}

/** `grounding` on the payload, else the copy the gate kept in the draft context. */
export function draftGrounding(d: Draft): Grounding[] {
  const top = (d as LearningDraft).grounding
  const ctx = d.context?.grounding
  const list = Array.isArray(top) && top.length ? top : Array.isArray(ctx) ? (ctx as Grounding[]) : []
  return list.filter((g) => g && typeof g === 'object' && (g.type === 'knowledge' || g.type === 'lesson' || g.title))
}

/* ---------- lessons ---------- */

export type LessonKind = 'example' | 'correction' | 'rule'
export type LessonStatus = 'suggested' | 'active' | 'archived' | 'dismissed'

export const LESSON_KIND_LABEL: Record<LessonKind, MessageKey> = {
  example: 'ai.lessonKind.example',
  correction: 'ai.lessonKind.correction',
  rule: 'ai.lessonKind.rule',
}

export const LESSON_STATUS_LABEL: Record<LessonStatus, MessageKey> = {
  suggested: 'ai.lessonStatus.suggested',
  active: 'ai.lessonStatus.active',
  archived: 'ai.lessonStatus.archived',
  dismissed: 'ai.lessonStatus.dismissed',
}

export interface LessonFeedbackRef {
  id: number
  rating: number
  kind: 'explicit' | 'approved' | 'edited' | 'rejected' | string
  user: string
  created_at: string
  thread: ThreadRef
}

export interface Lesson {
  id: number
  kind: LessonKind
  status: LessonStatus
  question: string
  answer: string
  avoid: string
  note: string
  channels: string[]
  entity: string | null
  entity_name: string
  source_count: number
  uses: number
  last_used_at: string | null
  created_by: string
  approved_by: string
  created_at: string
  updated_at: string
  feedback: LessonFeedbackRef[]
  /** What the CALLER may do with this row (a person bound to entities never edits
   * or deletes an organization-wide or an inherited ancestor's lesson). */
  can_edit?: boolean
  can_delete?: boolean
}

export interface LessonList {
  lessons: Lesson[]
  counts: Record<LessonStatus, number>
}

export interface LessonWrite {
  kind?: LessonKind
  status?: LessonStatus
  question?: string
  answer?: string
  avoid?: string
  note?: string
  channels?: string[]
  entity?: string | null
}

export const lessonsApi = {
  list: (params: { status?: LessonStatus; kind?: string; q?: string }, entityQuery: string) => {
    const p = new URLSearchParams()
    if (params.status) p.set('status', params.status)
    if (params.kind) p.set('kind', params.kind)
    if (params.q) p.set('q', params.q)
    const qs = [p.toString(), entityQuery].filter(Boolean).join('&')
    return aiApi<LessonList>(`ai/lessons${qs ? `?${qs}` : ''}`)
  },
  create: (body: LessonWrite) =>
    aiApi<{ lesson: Lesson } | Lesson>('ai/lessons', { method: 'POST', body: JSON.stringify(body) }),
  update: (id: number, body: LessonWrite) =>
    aiApi<{ lesson: Lesson } | Lesson>(`ai/lessons/${id}`, { method: 'PATCH', body: JSON.stringify(body) }),
  remove: (id: number) => aiApi<unknown>(`ai/lessons/${id}`, { method: 'DELETE' }),
  toKnowledge: (id: number) => aiApi<{ knowledge_id: number }>(`ai/lessons/${id}/to-knowledge`, { method: 'POST' }),
}

/* ---------- learning status + stats ---------- */

export type LearningMode = 'review' | 'auto' | 'off'

export const LEARNING_MODES: { id: LearningMode; label: MessageKey; help: MessageKey }[] = [
  { id: 'review', label: 'ai.learning.modes.review', help: 'ai.learning.modes.reviewHelp' },
  { id: 'auto', label: 'ai.learning.modes.auto', help: 'ai.learning.modes.autoHelp' },
  { id: 'off', label: 'ai.learning.modes.off', help: 'ai.learning.modes.offHelp' },
]

export interface EmbeddingStatus {
  provider: string
  model: string
  configured: boolean
  indexed: number
  pending: number
  last_error: string
}

export interface LearningStatus {
  mode: LearningMode | string
  embedding: EmbeddingStatus
}

export interface KnowledgeGap {
  topic: string
  count: number
  example: string
  thread: ThreadRef
  last_at: string | null
}

export interface LearningStats {
  totals: {
    rated: number
    up: number
    down: number
    up_rate: number | null
    approved: number
    edited: number
    rejected: number
  }
  daily: { day: string; up: number; down: number }[]
  reasons: { reason: string; count: number }[]
  gaps: KnowledgeGap[]
}

export const learningApi = {
  /** null when the server has no learning status (older API) or refused. */
  status: async (entityQuery = ''): Promise<LearningStatus | null> => {
    try {
      const s = await aiApi<Partial<LearningStatus>>(`ai/learning/status${entityQuery ? `?${entityQuery}` : ''}`)
      return {
        mode: s.mode ?? 'review',
        embedding: {
          provider: s.embedding?.provider ?? '',
          model: s.embedding?.model ?? '',
          configured: !!s.embedding?.configured,
          indexed: Number(s.embedding?.indexed ?? 0) || 0,
          pending: Number(s.embedding?.pending ?? 0) || 0,
          last_error: s.embedding?.last_error ?? '',
        },
      }
    } catch {
      return null
    }
  },
  reindex: () => aiApi<{ queued?: number; detail?: string }>('ai/learning/reindex', { method: 'POST' }),
  stats: (days: number, entityQuery: string) =>
    aiApi<LearningStats>(`ai/learning/stats?days=${days}${entityQuery ? `&${entityQuery}` : ''}`),
}

/** Human name for an embedding provider id (brand names stay as they are). */
export function embeddingProviderLabel(p: string, t: TFunction): string {
  const names: Record<string, string> = {
    openai: 'OpenAI',
    google: 'Google Gemini',
    local: t('ai.embeddings.providers.local'),
    none: t('ai.embeddings.providers.offLabel'),
    '': t('ai.embeddings.providers.keyword'),
    keyword: t('ai.embeddings.providers.keyword'),
  }
  return names[p] ?? p
}

/* ---------- conversation links ---------- */

/** A thread reference as the learning endpoints send it: "channel:key", or an
 *  object carrying channel + key (optionally a name). */
export type ThreadRef = string | { channel?: string; key?: string; name?: string; id?: number } | null | undefined

export function threadHref(t: ThreadRef): string | null {
  if (!t) return null
  if (typeof t === 'string') return t.includes(':') ? `/chats?thread=${encodeURIComponent(t)}` : null
  if (t.channel && t.key) return `/chats?thread=${encodeURIComponent(`${t.channel}:${t.key}`)}`
  return null
}

export function threadName(t: ThreadRef): string {
  if (!t) return ''
  if (typeof t === 'string') return t.split(':').slice(1).join(':')
  return t.name || t.key || ''
}

/* ---------- knowledge: upload + retrieval preview ---------- */

export const UPLOAD_ACCEPT = '.pdf,.txt,.md,.csv'
export const UPLOAD_MAX_BYTES = 10 * 1024 * 1024

/** Why a file can't be uploaded (translated), or null when it's fine. */
export function uploadProblem(file: File, t: TFunction, formatMb: (mb: number) => string): string | null {
  const ext = file.name.toLowerCase().split('.').pop() ?? ''
  if (!['pdf', 'txt', 'md', 'csv'].includes(ext)) return t('ai.upload.errors.notSupported', { name: file.name })
  if (file.size > UPLOAD_MAX_BYTES)
    return t('ai.upload.errors.tooBig', { name: file.name, size: formatMb(file.size / 1048576) })
  if (file.size === 0) return t('ai.upload.errors.empty', { name: file.name })
  return null
}

export interface UploadResult {
  item: { id: number; title: string; kind: string; source_file?: string } & Record<string, unknown>
  chunks: number
  chars: number
}

/**
 * Multipart upload with progress (fetch can't report upload progress).
 * `onProgress` gets 0..1 while the bytes go up; the promise settles when the
 * server has extracted the text. Rejects with an ApiError carrying `detail`.
 */
export function uploadKnowledge(
  file: File,
  /** `entities` = "Available to" slugs ([] = everyone); `entity` = the older single pick. */
  fields: { kind?: string; title?: string; entity?: string | null; entities?: string[] },
  t: TFunction,
  onProgress?: (fraction: number) => void,
): { promise: Promise<UploadResult>; abort: () => void } {
  const xhr = new XMLHttpRequest()
  const promise = new Promise<UploadResult>((resolve, reject) => {
    const form = new FormData()
    form.append('file', file, file.name)
    if (fields.kind) form.append('kind', fields.kind)
    if (fields.title) form.append('title', fields.title)
    if (fields.entities) form.append('entities', fields.entities.join(','))
    else if (fields.entity) form.append('entity', fields.entity)
    xhr.open('POST', `${API}/aicore/knowledge/upload`)
    for (const [k, v] of Object.entries(authHeaders())) xhr.setRequestHeader(k, v)
    xhr.upload.onprogress = (e) => {
      if (e.lengthComputable && onProgress) onProgress(e.loaded / e.total)
    }
    xhr.onload = () => {
      let body: unknown = {}
      try {
        body = JSON.parse(xhr.responseText || '{}')
      } catch {
        body = {}
      }
      if (xhr.status === 401) {
        expireSessionOn401(new Response(null, { status: 401 }))
        reject(new ApiError(t('common.errors.sessionExpired'), 401, body))
        return
      }
      if (xhr.status >= 200 && xhr.status < 300) resolve(body as UploadResult)
      else {
        const detail = (body as { detail?: string }).detail
        const msg =
          detail ||
          (xhr.status === 404 || xhr.status === 405
            ? t('ai.upload.errors.noUploads')
            : xhr.status === 413
              ? t('ai.upload.errors.tooLarge')
              : xhr.status === 403
                ? t('ai.upload.errors.noPermission')
                : t('ai.upload.errors.failedHttp', { status: String(xhr.status) }))
        reject(new ApiError(msg, xhr.status, body))
      }
    }
    xhr.onerror = () => reject(new ApiError(t('ai.upload.errors.unreachable'), 0, null))
    xhr.onabort = () => reject(new ApiError(t('ai.upload.errors.cancelled'), 0, null))
    xhr.send(form)
  })
  return { promise, abort: () => xhr.abort() }
}

export interface RetrievalHit {
  type: 'knowledge' | 'lesson' | string
  id: number
  title: string
  chunk: string
  score: number
  semantic: number | null
  keyword: number | null
}

export interface RetrievalPreview {
  block: string
  chars: number
  budget: number
  hits: RetrievalHit[]
  /** false when the server only returns the block (no scored hits). */
  scored: boolean
  /** true = matched by meaning (embeddings), false = keywords only, null = not reported. */
  semantic: boolean | null
  provider: string
  model: string
  /** Any other prompt blocks the server returns (e.g. the learned-lessons block). */
  extraBlocks: { key: string; text: string }[]
}

/** POST /api/aicore/knowledge/preview {query} — the blocks the AI would get + the scored hits. */
export async function previewRetrieval(query: string, channel: string, entity: string | null): Promise<RetrievalPreview> {
  const res = await fetch(`${API}/aicore/knowledge/preview`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
    // `text` is what older servers bind; `query` is the spec name — send both.
    body: JSON.stringify({ query, text: query, channel, entity: entity ?? undefined }),
    cache: 'no-store',
  })
  if (expireSessionOn401(res)) throw new ApiError('Session expired', 401, null)
  const data = (await res.json().catch(() => ({}))) as Record<string, unknown>
  if (!res.ok) throw new ApiError((data.detail as string) || `HTTP ${res.status}`, res.status, data)
  const hits = Array.isArray(data.hits) ? (data.hits as Partial<RetrievalHit>[]) : []
  const known = new Set(['block', 'full_prompt_block', 'chars', 'budget', 'items', 'hits', 'detail', 'grounding'])
  const extraBlocks = Object.entries(data)
    .filter(([k, v]) => !known.has(k) && typeof v === 'string' && v.trim().length > 0 && /block|lesson|learn/i.test(k))
    .map(([key, text]) => ({ key, text: text as string }))
  return {
    block: typeof data.block === 'string' ? data.block : '',
    chars: Number(data.chars ?? 0) || 0,
    budget: Number(data.budget ?? 1800) || 1800,
    hits: hits.map((h, i) => ({
      type: h.type ?? 'knowledge',
      id: typeof h.id === 'number' ? h.id : i,
      title: h.title ?? '',
      chunk: h.chunk ?? '',
      score: typeof h.score === 'number' ? h.score : 0,
      semantic: typeof h.semantic === 'number' ? h.semantic : null,
      keyword: typeof h.keyword === 'number' ? h.keyword : null,
    })),
    scored: Array.isArray(data.hits),
    semantic: typeof data.semantic === 'boolean' ? data.semantic : null,
    provider: typeof data.provider === 'string' ? data.provider : '',
    model: typeof data.model === 'string' ? data.model : '',
    extraBlocks,
  }
}
