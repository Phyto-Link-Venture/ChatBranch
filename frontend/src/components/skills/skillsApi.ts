import { authHeaders, expireSessionOn401 } from '@/lib/auth'

/**
 * Client for /api/aicore/skills — the SOLE writer of AgentSkill rows — and the
 * code-skill authoring endpoints (draft / test / builtins).
 *
 * The PUT replaces ONE scope's library (organization-wide, or one entity's):
 * rows of that scope missing from the payload are deleted. So every write here
 * re-reads the scope first and changes only the row it means to — a stale tab
 * can never prune someone else's skills.
 */

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

export type SkillKind = 'prompt' | 'tool' | 'check'
export type OnFail = 'retry' | 'handover' | 'block'

export interface Skill {
  id: string
  name: string
  useWhen: string
  instructions: string
  position: number
  kind: SkillKind
  description: string
  code: string
  parameters: unknown | null
  tests: unknown[]
  enabled: boolean
  channels: string[]
  onFail: OnFail
  updatedAt?: string | null
  tool_name?: string | null
  /** Owning entity slug; null = organization-wide (inherited by every entity). */
  entity: string | null
  entity_name: string
  scope: 'global' | 'entity'
}

export interface Builtin {
  name: string
  label: string
  description: string
  parameters: unknown
  example_args: unknown
  example: unknown
}

export interface BuiltinsPayload {
  timezone: string
  now: string
  builtins: Builtin[]
}

export interface DraftSkill {
  name: string
  kind: SkillKind
  use_when: string
  description: string
  parameters: unknown
  code: string
  tests: unknown[]
  instructions: string
  on_fail: OnFail
}

export interface DraftResponse {
  status: 'questions' | 'draft'
  questions: string[]
  source: 'ai' | 'template'
  note: string
  skill: DraftSkill | null
}

export interface TestResult {
  name: string
  pass: boolean
  got: unknown
  expected: unknown
  error: string | null
  ms: number
}

export interface TestResponse {
  valid: boolean
  error: string | null
  total: number
  passed: number
  failed: number
  results: TestResult[]
}

export async function aicore<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}/aicore/${path}`, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...authHeaders(), ...(init?.headers ?? {}) },
  })
  if (expireSessionOn401(res)) throw new Error('Session expired — redirecting to login…')
  const data = await res.json().catch(() => ({}))
  if (!res.ok) throw new Error((data as { detail?: string }).detail || `HTTP ${res.status}`)
  return data as T
}

/** The wire shape PUT accepts (every field — absent fields would be kept). */
export function toWire(s: Skill) {
  return {
    id: s.id,
    name: s.name,
    useWhen: s.useWhen,
    instructions: s.instructions,
    position: s.position,
    kind: s.kind,
    description: s.description,
    code: s.code,
    parameters: (s.parameters ?? null) as unknown,
    tests: s.tests ?? [],
    enabled: s.enabled,
    channels: s.channels ?? [],
    onFail: s.onFail,
  }
}

export type WireSkill = ReturnType<typeof toWire>

const scopeQuery = (scope: string | null) => (scope ? `?entity=${encodeURIComponent(scope)}` : '')

/** One scope's OWN rows, freshly read. */
export async function readScope(scope: string | null): Promise<Skill[]> {
  const d = await aicore<{ skills: Skill[] }>(`skills${scopeQuery(scope)}`)
  return d.skills.filter((s) => (s.entity ?? null) === scope).sort((a, b) => a.position - b.position)
}

/** Re-read a scope, apply `mutate` to its own rows, write it back. */
export async function writeScope(scope: string | null, mutate: (rows: WireSkill[]) => WireSkill[]) {
  const own = (await readScope(scope)).map(toWire)
  await aicore('skills', { method: 'PUT', body: JSON.stringify({ entity: scope, skills: mutate(own) }) })
}

/** Insert or replace one skill in `scope`; when it moved, remove it from `from`. */
export async function saveSkill(skill: WireSkill, scope: string | null, from?: string | null) {
  await writeScope(scope, (rows) =>
    rows.some((r) => r.id === skill.id) ? rows.map((r) => (r.id === skill.id ? skill : r)) : [...rows, skill],
  )
  if (from !== undefined && from !== scope) await writeScope(from, (rows) => rows.filter((r) => r.id !== skill.id))
}

export async function deleteSkill(id: string, scope: string | null) {
  await writeScope(scope, (rows) => rows.filter((r) => r.id !== id))
}

export const newSkillId = () => `as-${Math.random().toString(36).slice(2, 9)}`

export const CHANNEL_OPTIONS = [
  { value: 'whatsapp', label: 'WhatsApp' },
  { value: 'email', label: 'Email' },
  { value: 'telegram', label: 'Telegram' },
  { value: 'facebook', label: 'Messenger' },
  { value: 'instagram', label: 'Instagram' },
  { value: 'website', label: 'Website widget' },
]

export const KIND_META: Record<SkillKind, { label: string; plural: string; blurb: string }> = {
  prompt: {
    label: 'Instruction',
    plural: 'Instructions',
    blurb: 'Plain-language guidance the AI follows when its trigger applies.',
  },
  tool: {
    label: 'Tool',
    plural: 'Tools',
    blurb: 'JavaScript the AI calls for an exact answer — hours, dates, lookups, validations.',
  },
  check: {
    label: 'Reply check',
    plural: 'Reply checks',
    blurb: 'JavaScript that validates every AI reply before it is sent.',
  },
}

export const ON_FAIL_OPTIONS: { value: OnFail; label: string; hint: string }[] = [
  { value: 'retry', label: 'Rewrite, then hand over', hint: 'the AI rewrites up to 2×; still failing → a human answers' },
  { value: 'handover', label: 'Hand over at once', hint: 'no rewrite — the chat is marked pending for a human' },
  { value: 'block', label: 'Rewrite, then send nothing', hint: 'the AI rewrites up to 2×; still failing → nothing is sent' },
]

export const pretty = (v: unknown) => {
  if (v === undefined) return ''
  try {
    return JSON.stringify(v, null, 2)
  } catch {
    return String(v)
  }
}
