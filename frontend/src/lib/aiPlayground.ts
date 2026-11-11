/**
 * The AI playground (/ai-playground): client + types for /api/ai/playground/*
 * and the browser cache of a session's transcript and setup.
 *
 * The server is stateless — every reply call sends the whole transcript — so
 * the browser owns the conversation. It survives a reload in localStorage
 * under `playground:<username>`; sign-out, an expired session and every login
 * clear every `playground:*` key (lib/playgroundCache.ts, called from lib/auth).
 */
import { aiApi } from './aiMonitor'
import { PLAYGROUND_PREFIX } from './playgroundCache'

// ---- wire types ----------------------------------------------------------------------

export interface PlaygroundProvider {
  id: string
  label: string
  default_model: string
}

export interface PlaygroundField {
  key: string
  label: string
  kind: 'text' | 'select' | 'multiselect' | 'number' | 'boolean' | 'date' | 'json'
  options: string[]
}

export interface PlaygroundOptions {
  entity: { slug: string; name: string } | null
  sandbox: boolean
  can_override: boolean
  can_teach: boolean
  can_import: boolean
  can_manage_keys: boolean
  providers: PlaygroundProvider[]
  default_route: { provider: string; label: string; model: string } | null
  persona: string | null
  persona_is_default: boolean | null
  channels: string[]
  languages: string[]
  policy: {
    review_mode: string
    first_n: number
    autosend_minutes: number
    split: string
    split_max_chars: number
    /** The human-like touches production uses here (AI monitor → Settings → Human-like). */
    emoji_style?: string
    reactions?: boolean
    quote_replies?: string
  }
  ai: { enabled: boolean; rule: string; reason: string; forced: boolean }
  learning_mode: string
  teach_status: 'active' | 'suggested'
  custom_fields: PlaygroundField[]
  limits: { turns: number; chars: number; per_minute: number; persona: number }
}

export type Verdict = 'send' | 'hold' | 'handover' | 'blocked' | 'ai_off'

export interface PlaygroundReply {
  reply: string
  draft: string
  bubbles: string[]
  action: 'send' | 'handover' | 'block' | 'empty' | 'error' | string
  reason: string
  /** The emoji reaction production would put on a customer message — `message_index` is the
   *  position in the transcript this call sent. Nothing is performed here. */
  reaction?: { message_index: number; emoji: string; text: string } | null
  /** The customer message the reply's first bubble would quote. */
  quote?: { message_index: number; text: string } | null
  /** The quote each bubble would carry (a reply in parts quotes per bubble); aligned with `bubbles`. */
  bubble_quotes?: ({ message_index: number; text: string } | null)[]
  /** The touches this run used (the setting, or this run's override; off where the channel has none). */
  touches?: { emoji_style: string; reactions: boolean; quotes: boolean }
  production: {
    verdict: Verdict
    why: string
    rule: string | null
    kind: string | null
    review_mode: string
    shadow: boolean
    flags: string[]
    ai: { enabled: boolean; rule: string; reason: string; forced: boolean }
  }
  grounding: { type: 'knowledge' | 'lesson' | string; id: number; title: string; chunk: string; score: number }[]
  hits: {
    type: string
    id: number
    title: string
    part: string | null
    score: number
    semantic: number
    keyword: number
    used: boolean
  }[]
  tools: { name: string; args: string; result: string; ok: boolean; dry_run: boolean }[]
  tools_offered: string[]
  bookings_previewed: {
    type: 'book' | 'cancel' | string
    label: string
    start: string
    timezone: string
    calendar: string
    booking_id: number | null
  }[]
  /** Documents / QR codes the reply would send — rendered for real into a throw-away preview. */
  documents_previewed?: {
    type: string
    label: string
    number: string
    currency: string
    total: number | null
    url: string
    file_name: string
  }[]
  checks: { name: string; passed: boolean; reason: string; fix: string; error: string | null; attempt: number }[]
  checks_configured: string[]
  retries: number
  provider: string
  model: string
  sandbox: boolean
  provider_error: string | null
  tokens: { prompt: number; completion: number }
  cost_estimate: { usd: number; priced: boolean }
  latency_ms: number
  contact: { id: number | null; name: string; made_up: boolean } | null
  options: {
    provider: string | null
    model: string | null
    persona: boolean
    knowledge: boolean
    lessons: boolean
    skills: boolean
    memory: boolean
  }
  prompt: { system: string; messages: { role: string; content: string }[] } | null
}

export interface ImportedConversation {
  thread: string
  /** which company's conversation (a customer has one per company) */
  thread_id?: number | null
  name: string
  channel: string
  entity: { slug: string; name: string } | null
  account: { id: number; name: string } | null
  contact: { id: number; name: string; language: string; tags: string[] } | null
  messages: { role: 'customer' | 'ai'; text: string }[]
  total: number
}

// ---- client state ------------------------------------------------------------------------

/** One set-up the chat runs with. `persona: null` = the stored persona. */
export interface Variant {
  provider: string
  model: string
  persona: string | null
  knowledge: boolean
  lessons: boolean
  skills: boolean
  memory: boolean
  showPrompt: boolean
  /** Human-like touches for this run — '' = the saved setting. */
  emojiStyle: '' | 'off' | 'light' | 'expressive'
  reactions: '' | 'on' | 'off'
  quotes: '' | 'auto' | 'off'
}

export const DEFAULT_VARIANT: Variant = {
  provider: '',
  model: '',
  persona: null,
  knowledge: true,
  lessons: true,
  skills: true,
  memory: true,
  showPrompt: false,
  emojiStyle: '',
  reactions: '',
  quotes: '',
}

/** Anything beyond the defaults (the server refuses it without ai.behaviour.manage). */
export const isOverride = (v: Variant) =>
  !!v.provider || !!v.model || v.persona != null || !v.knowledge || !v.lessons || !v.skills || !v.memory || v.showPrompt
  || !!v.emojiStyle || !!v.reactions || !!v.quotes

export interface MadeUpProfile {
  name: string
  /** en | ms | zh — "" follows the operator's own UI language. */
  language: string
  tags: string[]
  fields: Record<string, string | number | boolean | string[]>
}

export interface Setup {
  entity: string | null
  channel: string
  mode: 'made_up' | 'contact'
  profile: MadeUpProfile
  contact: { id: number; name: string } | null
  /** An imported real conversation (its AI switch / inbox apply). */
  imported: { thread: string; threadId?: number | null; name: string; accountId: number | null } | null
}

export const DEFAULT_SETUP: Setup = {
  entity: null,
  channel: 'whatsapp',
  mode: 'made_up',
  profile: { name: '', language: '', tags: [], fields: {} },
  contact: null,
  imported: null,
}

/** One generated AI turn in one column. */
export interface ColumnResult {
  pending?: boolean
  error?: string
  reply?: PlaygroundReply
  at?: number
}

export type Entry =
  | { id: string; kind: 'customer'; text: string }
  /** An outbound message copied from a real conversation — the same in every column. */
  | { id: string; kind: 'history'; text: string }
  /** An AI turn the playground generated — one result per column. */
  | { id: string; kind: 'ai'; results: (ColumnResult | null)[] }

export interface PlaygroundState {
  setup: Setup
  variants: Variant[]
  entries: Entry[]
}

export const newId = () => Math.random().toString(36).slice(2, 10) + Date.now().toString(36).slice(-4)

/** What the AI wrote in a column (the reply, or the draft it was not allowed to send). */
export const resultText = (r: ColumnResult | null | undefined) => r?.reply?.reply || r?.reply?.draft || ''

/**
 * The transcript a column sends for the AI turn at `upTo` (exclusive): customer
 * turns, copied history and this column's own earlier AI turns — falling back
 * to the first column's words where this one has none (compare switched on
 * mid-conversation).
 */
export function transcriptFor(entries: Entry[], upTo: number, column: number): { role: 'customer' | 'ai'; text: string }[] {
  const out: { role: 'customer' | 'ai'; text: string }[] = []
  for (const e of entries.slice(0, upTo)) {
    if (e.kind === 'customer') out.push({ role: 'customer', text: e.text })
    else if (e.kind === 'history') out.push({ role: 'ai', text: e.text })
    else {
      const text = resultText(e.results[column]) || resultText(e.results[0])
      if (text) out.push({ role: 'ai', text })
    }
  }
  return out
}

/** The entry ids behind {@link transcriptFor}'s turns, position for position. */
export function transcriptEntryIds(entries: Entry[], upTo: number, column: number): string[] {
  const out: string[] = []
  for (const e of entries.slice(0, upTo)) {
    if (e.kind !== 'ai') out.push(e.id)
    else if (resultText(e.results[column]) || resultText(e.results[0])) out.push(e.id)
  }
  return out
}

/**
 * The reactions the AI turns would put on customer messages, by entry id —
 * mapped back from the transcript positions each reply was generated against.
 */
export function plannedReactions(entries: Entry[]): Map<string, { emoji: string; column: number }[]> {
  const out = new Map<string, { emoji: string; column: number }[]>()
  entries.forEach((e, i) => {
    if (e.kind !== 'ai') return
    e.results.forEach((r, column) => {
      const reaction = r?.reply?.reaction
      if (!reaction) return
      const id = transcriptEntryIds(entries, i, column)[reaction.message_index]
      if (id) out.set(id, [...(out.get(id) ?? []), { emoji: reaction.emoji, column }])
    })
  })
  return out
}

// ---- API -----------------------------------------------------------------------------------

export const fetchOptions = (entity: string | null) =>
  aiApi<PlaygroundOptions>(`ai/playground/options${entity ? `?entity=${encodeURIComponent(entity)}` : ''}`)

/** The made-up customer's language: their own, else the operator's UI language. */
export const profileLanguage = (setup: Setup, uiLocale: string) =>
  setup.profile.language || (['en', 'ms', 'zh'].includes(uiLocale) ? uiLocale : 'en')

export function requestReply(setup: Setup, variant: Variant, messages: { role: string; text: string }[],
  uiLocale = 'en') {
  const options: Record<string, unknown> = {}
  if (variant.provider) options.provider = variant.provider
  if (variant.provider && variant.model) options.model = variant.model
  if (variant.persona != null) options.persona = variant.persona
  for (const k of ['knowledge', 'lessons', 'skills', 'memory'] as const) if (!variant[k]) options[k] = false
  if (variant.showPrompt) options.show_prompt = true
  if (variant.emojiStyle) options.emoji_style = variant.emojiStyle
  if (variant.reactions) options.reactions = variant.reactions === 'on'
  if (variant.quotes) options.quote_replies = variant.quotes
  const fields: Record<string, unknown> = {}
  for (const [k, v] of Object.entries(setup.profile.fields))
    if (v !== '' && !(Array.isArray(v) && v.length === 0)) fields[k] = v
  const body: Record<string, unknown> = {
    entity: setup.entity,
    channel: setup.channel,
    messages,
    options,
  }
  if (setup.mode === 'contact' && setup.contact) body.contact_id = setup.contact.id
  else body.profile = { ...setup.profile, language: profileLanguage(setup, uiLocale), fields }
  if (setup.imported) {
    body.thread = setup.imported.thread
    if (setup.imported.threadId) body.thread_id = setup.imported.threadId
    if (setup.imported.accountId) body.account_id = setup.imported.accountId
  }
  return aiApi<PlaygroundReply>('ai/playground/reply', { method: 'POST', body: JSON.stringify(body) })
}

export const importConversation = (channel: string, key: string, take = 20, threadId?: number | null) =>
  aiApi<ImportedConversation>('ai/playground/import', {
    method: 'POST',
    body: JSON.stringify({ channel, key, take, thread_id: threadId ?? undefined }),
  })

/** The note a lesson taught here carries (stored text stays English, like audit text). */
export const LESSON_NOTE = 'Taught in the AI playground'

// ---- browser cache ---------------------------------------------------------------------------

const keyFor = (username: string) => `${PLAYGROUND_PREFIX}${username}`

export function readPlayground(username: string): PlaygroundState | null {
  try {
    const raw = window.localStorage.getItem(keyFor(username))
    if (!raw) return null
    const d = JSON.parse(raw) as Partial<PlaygroundState>
    if (!d || !Array.isArray(d.entries) || !Array.isArray(d.variants) || !d.setup) return null
    // a reply that was still pending when the page closed never arrived
    const entries = d.entries.map((e) =>
      e.kind === 'ai'
        ? { ...e, results: e.results.map((r) => (r?.pending ? { error: 'interrupted' } : r)) }
        : e,
    ) as Entry[]
    return {
      setup: { ...DEFAULT_SETUP, ...d.setup, profile: { ...DEFAULT_SETUP.profile, ...d.setup.profile } },
      variants: d.variants.slice(0, 2).map((v) => ({ ...DEFAULT_VARIANT, ...v })),
      entries,
    }
  } catch {
    return null
  }
}

export function writePlayground(username: string, state: PlaygroundState): void {
  try {
    // the full prompt is big and re-requested on demand: keep it out of the cache
    const slim: PlaygroundState = {
      ...state,
      entries: state.entries.map((e) =>
        e.kind === 'ai'
          ? {
              ...e,
              results: e.results.map((r) =>
                r?.reply ? { ...r, reply: { ...r.reply, prompt: null, hits: r.reply.hits.slice(0, 10) } } : r,
              ),
            }
          : e,
      ),
    }
    window.localStorage.setItem(keyFor(username), JSON.stringify(slim))
  } catch {
    /* storage full / blocked — the page still works in memory */
  }
}

// ---- export ----------------------------------------------------------------------------------

function download(name: string, text: string, type: string) {
  const blob = new Blob([text], { type })
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = name
  document.body.appendChild(a)
  a.click()
  a.remove()
  window.setTimeout(() => URL.revokeObjectURL(url), 1000)
}

const stamp = () => new Date().toISOString().slice(0, 16).replace(/[:T]/g, '-')

export function exportJson(state: PlaygroundState) {
  download(`ai-playground-${stamp()}.json`, JSON.stringify({ exported_at: new Date().toISOString(), ...state }, null, 2),
    'application/json')
}

/**
 * The transcript as Markdown with each AI turn's inspector summary. The
 * labels are passed in already translated (the file is for people).
 */
export function exportMarkdown(
  state: PlaygroundState,
  labels: {
    title: string
    customer: string
    ai: string
    history: string
    column: (i: number) => string
    production: string
    /** The verdict in words (ProductionChip's verdictText). */
    verdict: (p: PlaygroundReply['production']) => string
    basedOn: string
    tools: string
    checks: string
    model: string
    tokens: string
  },
) {
  const lines: string[] = [`# ${labels.title}`, '', `_${new Date().toISOString()}_`, '']
  for (const e of state.entries) {
    if (e.kind === 'customer') lines.push(`**${labels.customer}:** ${e.text}`, '')
    else if (e.kind === 'history') lines.push(`**${labels.history}:** ${e.text}`, '')
    else
      e.results.forEach((r, i) => {
        if (!r?.reply) return
        const p = r.reply
        const who = state.variants.length > 1 ? `${labels.ai} (${labels.column(i)})` : labels.ai
        lines.push(`**${who}:** ${p.bubbles.length ? p.bubbles.join('\n\n') : p.draft}`, '')
        lines.push(`- ${labels.production}: ${labels.verdict(p.production)}`)
        if (p.grounding.length)
          lines.push(`- ${labels.basedOn}: ${p.grounding.map((g) => `${g.title} (${g.score})`).join('; ')}`)
        if (p.tools.length) lines.push(`- ${labels.tools}: ${p.tools.map((t) => `${t.name}${t.ok ? '' : ' ✗'}`).join(', ')}`)
        if (p.checks.length)
          lines.push(`- ${labels.checks}: ${p.checks.map((c) => `${c.name} ${c.passed ? '✓' : '✗'}`).join(', ')}`)
        lines.push(`- ${labels.model}: ${p.provider} · ${p.model}`)
        lines.push(`- ${labels.tokens}: ${p.tokens.prompt} / ${p.tokens.completion} · ${p.latency_ms} ms`, '')
      })
  }
  download(`ai-playground-${stamp()}.md`, lines.join('\n'), 'text/markdown')
}
