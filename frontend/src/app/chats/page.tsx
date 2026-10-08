'use client'

import { Suspense, useCallback, useEffect, useMemo, useRef, useState, type ChangeEvent } from 'react'
import { useSearchParams } from 'next/navigation'
import {
  Ban,
  CheckCircle2,
  ChevronLeft,
  Clock,
  Info,
  Layers,
  LayoutTemplate,
  Loader2,
  Lock,
  Paperclip,
  PhoneCall,
  PhoneOff,
  RefreshCw,
  RotateCcw,
  Send,
  Sparkles,
  StickyNote,
  UserRound,
  X,
} from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { ChannelLogo } from '@/components/ui/ChannelLogo'
import { useEntity } from '@/lib/EntityContext'
import VoiceCallButton from '@/components/chats/VoiceCallButton'
import ScriptCallButton from '@/components/chats/ScriptCallButton'
import ActiveCallsStrip from '@/components/chats/ActiveCallsStrip'
import ChatListPane, { type ViewTab } from '@/components/chats/ChatListPane'
import ChatFilterModal from '@/components/chats/ChatFilterModal'
import ChatInfoPanel, { type LinkContact } from '@/components/chats/ChatInfoPanel'
import SavedViewsManager from '@/components/chats/SavedViewsManager'
import TemplatePickerModal from '@/components/chats/TemplatePickerModal'
import {
  BetaChip,
  CallEntry,
  CallTranscriptChip,
  CenterNote,
  ChannelBadge,
  MediaAttachment,
  SipCallCard,
  StatusTick,
  type CallRec,
  type SipCall,
} from '@/components/chats/MessageParts'
import {
  CHANNELS,
  CHANNEL_META,
  api,
  apiUpload,
  dayLabel,
  logoKey,
  rowToSummary,
  threadPath,
  timeAgo,
  timeShort,
  type Channel,
  type InboxRow,
  type ThreadStatus,
  type UiAccount,
  type UiAgent,
  type UiMessage,
  type UiTeam,
  type UiThread,
  type UiThreadSummary,
  type WaCall,
} from '@/components/chats/inboxTypes'
import {
  EMPTY_FILTERS,
  activeChips,
  filtersToParams,
  normalizeFilters,
  sameFilters,
  type FilterChip,
  type InboxFilters,
} from '@/components/chats/inboxFilters'
import { createView, deleteView, listViews, reorderViews, updateView, type SavedView } from '@/lib/savedViews'
import { getRole } from '@/lib/role'
import { useApp } from '@/lib/AppContext'
import { tagMeta } from '@/lib/tags'
import PendingDraftBanner from '@/components/chats/PendingDraftBanner'
import { subscribeChatStream } from '@/lib/chatStream'
import './chats.css'

/** First page of the list; "Load more" pages by the same step. */
const PAGE = 50

interface CannedResponse {
  id: number
  short_code: string
  content: string
  /** Content with {{vars}} filled from the linked contact (raw when unlinked). */
  rendered: string
  created_by: string
}

interface ThreadSummaryBrief {
  summary: string
  heuristic: boolean
  message_count: number
  as_of: string
  generated_at: string
}

interface SummaryState {
  summary: ThreadSummaryBrief | null
  stale: boolean
  message_count: number
}

/** One contact's messages merged across every linked channel. */
interface CombinedConversation {
  contact: LinkContact
  channels: { channel: Channel; key: string; name: string }[]
  messages: (UiMessage & { channel: Channel; key: string })[]
}

interface InboxStatus {
  whatsapp_sandbox: boolean
  email_configured: boolean
  imap_configured: boolean
  facebook_configured: boolean
  instagram_configured: boolean
  telegram_configured: boolean
  ai_available: boolean
}

/** Built-in tabs ahead of the operator's own saved views. */
const BUILTIN_VIEWS: { id: string; name: string; filters: InboxFilters }[] = [
  { id: 'all', name: 'All', filters: EMPTY_FILTERS },
  { id: 'mine', name: 'Mine', filters: { ...EMPTY_FILTERS, assignees: ['me'] } },
  { id: 'unassigned', name: 'Unassigned', filters: { ...EMPTY_FILTERS, assignees: ['unassigned'] } },
]
const VIEW_KEY = 'chats-view'

/** Count of active filter dimensions (the Filter button's badge). */
const countActive = (chips: FilterChip[]) => chips.length

/** Parse `channel:key` (the deep-link form) into a thread id. */
function parseThreadRef(ref: string | null): { channel: Channel; key: string } | null {
  if (!ref) return null
  const i = ref.indexOf(':')
  if (i <= 0) return null
  const channel = ref.slice(0, i) as Channel
  const key = ref.slice(i + 1)
  return CHANNELS.includes(channel) && key ? { channel, key } : null
}

function ChatsPage() {
  const { contacts, calls, hangupCall, sendSay: sendSipSay, escalateCall: escalateSipCall, setHumanControl } = useApp()
  const { activeEntity, entities } = useEntity()
  const topEntity = activeEntity?.slug ?? null
  const searchParams = useSearchParams()

  const [status, setStatus] = useState<InboxStatus | null>(null)
  // ── list state (server-filtered) ──
  const [filters, setFilters] = useState<InboxFilters>(EMPTY_FILTERS)
  const [search, setSearch] = useState('')
  const [q, setQ] = useState('') // debounced search
  const [threads, setThreads] = useState<UiThreadSummary[]>([])
  const [total, setTotal] = useState<number | null>(null)
  const [nextCursor, setNextCursor] = useState<string | null>(null)
  const [listLoading, setListLoading] = useState(true)
  const [loadingMore, setLoadingMore] = useState(false)
  const [filterOpen, setFilterOpen] = useState(false)
  const [views, setViews] = useState<SavedView<InboxFilters>[]>([])
  const [activeView, setActiveView] = useState<string>('all')
  const [manageOpen, setManageOpen] = useState(false)
  const listSeq = useRef(0)
  const loadedCount = useRef(PAGE)

  // ── conversation state ──
  const [active, setActive] = useState<UiThread | null>(null)
  const [activeId, setActiveId] = useState<{ channel: Channel; key: string } | null>(null)
  const [draft, setDraft] = useState('')
  const [composerMode, setComposerMode] = useState<'reply' | 'note'>('reply')
  const [agents, setAgents] = useState<UiAgent[]>([])
  const [teams, setTeams] = useState<UiTeam[]>([])
  const [allAccounts, setAllAccounts] = useState<UiAccount[]>([])
  const [canned, setCanned] = useState<CannedResponse[]>([])
  const [linkContacts, setLinkContacts] = useState<LinkContact[]>([])
  const [linkSel, setLinkSel] = useState('')
  const [combinedOn, setCombinedOn] = useState(false)
  // "Voice" chip: filters the merged timeline down to call history only
  const [voiceOnly, setVoiceOnly] = useState(false)
  const [combined, setCombined] = useState<CombinedConversation | null>(null)
  const [summaryState, setSummaryState] = useState<SummaryState | null>(null)
  // Which thread's summary is generating, keyed by API path — NOT a bare
  // boolean: switching chats mid-generation used to wedge "updating…".
  const [summarizingFor, setSummarizingFor] = useState<string | null>(null)
  const summaryPathRef = useRef<string | null>(null)
  const summarizing =
    summarizingFor !== null &&
    activeId !== null &&
    summarizingFor === `messaging/threads/${activeId.channel}/${activeId.key}/summary`
  const [summaryOpen, setSummaryOpen] = useState(true)
  const lastAutoGen = useRef(0)
  const [infoOpen, setInfoOpen] = useState(true)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [pendingFile, setPendingFile] = useState<File | null>(null)
  const [tplOpen, setTplOpen] = useState(false)
  const [endedFlash, setEndedFlash] = useState(false)
  const [callLive, setCallLive] = useState<{ turns: { role: string; text: string }[]; done: boolean }>({ turns: [], done: false })
  const [sayDraft, setSayDraft] = useState('')
  const [sipSayDraft, setSipSayDraft] = useState('')
  // optimistic "Calling…" — flips true the instant Start is clicked
  const [starting, setStarting] = useState(false)
  const [callRecordings, setCallRecordings] = useState<CallRec[]>([])
  // Past AI voice calls for the open thread's contact (any transport)
  const [sipCalls, setSipCalls] = useState<SipCall[]>([])
  // '' = native realtime voice; otherwise a dashboard ElevenLabs voice id
  const [callVoice, setCallVoice] = useState('')
  const [canManage, setCanManage] = useState(false)
  const scrollRef = useRef<HTMLDivElement>(null)
  const callScrollRef = useRef<HTMLDivElement>(null)
  const fileRef = useRef<HTMLInputElement>(null)
  const prevCallStatus = useRef<string | undefined>(undefined)

  const waAccounts = useMemo(() => allAccounts.filter((a) => (a.channel ?? 'whatsapp') === 'whatsapp'), [allAccounts])

  // ── search debounce ──
  useEffect(() => {
    const t = setTimeout(() => setQ(search), 280)
    return () => clearTimeout(t)
  }, [search])

  // ── deep links: /chats?thread=<channel>:<key>, /chats?q=<text>, legacy ?channel=&key= ──
  const spThread = searchParams.get('thread')
  const spChannel = searchParams.get('channel')
  const spKey = searchParams.get('key')
  const spQ = searchParams.get('q')
  useEffect(() => {
    const ref = parseThreadRef(spThread) ?? (spChannel && spKey ? parseThreadRef(`${spChannel}:${spKey}`) : null)
    if (ref) setActiveId((cur) => (cur && cur.channel === ref.channel && cur.key === ref.key ? cur : ref))
  }, [spThread, spChannel, spKey])
  useEffect(() => {
    if (spQ !== null) {
      setSearch(spQ)
      setQ(spQ)
    }
  }, [spQ])

  // ── the list request ──
  const listParams = useMemo(() => filtersToParams(filters, { q, entity: topEntity }).toString(), [filters, q, topEntity])

  const refreshThreads = useCallback(async () => {
    const seq = ++listSeq.current
    const limit = Math.min(200, Math.max(PAGE, loadedCount.current))
    try {
      const d = await api<{ threads: InboxRow[]; count: number; next_cursor: string | null }>(
        `inbox/threads?${listParams}${listParams ? '&' : ''}limit=${limit}`,
      )
      if (seq !== listSeq.current) return // a newer request superseded this one
      setThreads(d.threads.map(rowToSummary))
      setTotal(d.count)
      setNextCursor(d.next_cursor)
    } catch {
      /* API down — the list keeps its last good state */
    } finally {
      if (seq === listSeq.current) setListLoading(false)
    }
  }, [listParams])

  // A new filter set starts from the first page again.
  useEffect(() => {
    loadedCount.current = PAGE
    setListLoading(true)
    refreshThreads()
  }, [refreshThreads])

  const loadMore = async () => {
    if (!nextCursor) return
    setLoadingMore(true)
    const seq = listSeq.current
    try {
      const d = await api<{ threads: InboxRow[]; count: number; next_cursor: string | null }>(
        `inbox/threads?${listParams}${listParams ? '&' : ''}limit=${PAGE}&cursor=${encodeURIComponent(nextCursor)}`,
      )
      if (seq !== listSeq.current) return
      setThreads((prev) => {
        const seen = new Set(prev.map((t) => `${t.channel}:${t.key}`))
        const merged = [...prev, ...d.threads.map(rowToSummary).filter((t) => !seen.has(`${t.channel}:${t.key}`))]
        loadedCount.current = merged.length
        return merged
      })
      setTotal(d.count)
      setNextCursor(d.next_cursor)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setLoadingMore(false)
    }
  }

  const refreshActive = useCallback(async () => {
    // Deselecting must also drop the loaded conversation, or the pane keeps
    // showing a thread the current scope no longer contains.
    if (!activeId) {
      setActive(null)
      return
    }
    try {
      if (activeId.channel === 'whatsapp') {
        const t = await api<{
          wa_id: string
          phone: string
          name: string
          ai_enabled: boolean
          status?: ThreadStatus
          assignee?: string
          team_id?: number | null
          call: WaCall
          account_id?: string | null
          provider?: 'cloud' | 'baileys'
          window_open?: boolean
          window_expires_at?: string | null
          opted_out?: boolean
          contact_id?: number | null
          contact_name?: string
          labels?: string[]
          messages: UiMessage[]
        }>(`whatsapp/threads/${activeId.key}`)
        setActive({
          channel: 'whatsapp',
          key: t.wa_id,
          name: t.contact_name || t.name,
          // a WhatsApp id is bare digits — show it as the phone number it is
          sub: /^\d+$/.test(t.phone) ? `+${t.phone}` : t.phone,
          ai_enabled: t.ai_enabled,
          status: t.status ?? 'open',
          assignee: t.assignee,
          team_id: t.team_id ?? null,
          call: t.call,
          account_id: t.account_id,
          provider: t.provider,
          window_open: t.window_open,
          window_expires_at: t.window_expires_at,
          opted_out: t.opted_out,
          contact_id: t.contact_id,
          contact_name: t.contact_name,
          labels: t.labels,
          messages: t.messages,
        })
      } else {
        const t = await api<{
          key: string
          channel: Channel
          address: string
          name: string
          ai_enabled: boolean
          status: ThreadStatus
          assignee: string
          team_id?: number | null
          labels: string[]
          contact_id?: number | null
          contact_name?: string
          messages: UiMessage[]
        }>(`messaging/threads/${activeId.channel}/${activeId.key}`)
        setActive({
          channel: t.channel,
          key: t.key,
          name: t.contact_name || t.name,
          sub: t.address,
          ai_enabled: t.ai_enabled,
          status: t.status,
          assignee: t.assignee,
          team_id: t.team_id ?? null,
          labels: t.labels,
          contact_id: t.contact_id,
          contact_name: t.contact_name,
          messages: t.messages,
        })
      }
    } catch {
      /* keep last good state while polling */
    }
  }, [activeId])

  // ── static-per-visit data: channel status, agents, link picker, accounts, views ──
  useEffect(() => {
    Promise.all([
      api<{ sandbox: boolean; ai_available: boolean }>('whatsapp/status'),
      api<{
        email_configured: boolean
        imap_configured: boolean
        facebook_configured: boolean
        instagram_configured: boolean
        telegram_configured: boolean
      }>('messaging/status'),
    ])
      .then(([wa, msg]) =>
        setStatus({
          whatsapp_sandbox: wa.sandbox,
          ai_available: wa.ai_available,
          email_configured: msg.email_configured,
          imap_configured: msg.imap_configured,
          facebook_configured: msg.facebook_configured,
          instagram_configured: msg.instagram_configured,
          telegram_configured: msg.telegram_configured,
        }),
      )
      .catch(() => setStatus(null))
    api<{ agents: UiAgent[] }>('messaging/agents')
      .then((r) => setAgents(r.agents))
      .catch(() => setAgents([]))
    api<{ contacts: LinkContact[] }>('messaging/contacts')
      .then((r) => setLinkContacts(r.contacts))
      .catch(() => setLinkContacts([]))
    // our provider accounts, every channel: the sending-account switcher, the
    // per-bubble "via" labels and the filter modal's account list
    api<{ accounts: UiAccount[] }>('whatsapp/accounts?channel=all')
      .then((r) => setAllAccounts((r.accounts || []).filter((a) => /^\d+$/.test(a.id))))
      .catch(() => {})
    setCanManage(getRole() !== 'agent')
    try {
      setCallVoice((v) => {
        const saved = localStorage.getItem('wa-call-voice')
        // ignore stale "ilmu:*" picks from before the picker was trimmed
        return saved && !saved.startsWith('ilmu:') ? saved : v
      })
    } catch {
      /* localStorage unavailable */
    }
  }, [])

  // saved views + the last tab this viewer used
  const reloadViews = useCallback(async () => {
    try {
      const list = await listViews<InboxFilters>('chats')
      setViews(list.map((v) => ({ ...v, filters: normalizeFilters(v.filters) })))
      return list
    } catch {
      return []
    }
  }, [])
  useEffect(() => {
    let cancelled = false
    reloadViews().then((list) => {
      if (cancelled || spQ !== null) return
      let last: string | null = null
      try {
        last = localStorage.getItem(VIEW_KEY)
      } catch {
        /* ignore */
      }
      if (!last || last === 'all') return
      const builtin = BUILTIN_VIEWS.find((b) => b.id === last)
      const saved = list.find((v) => `v:${v.id}` === last)
      if (builtin) {
        setFilters(builtin.filters)
        setActiveView(builtin.id)
      } else if (saved) {
        setFilters(normalizeFilters(saved.filters))
        setActiveView(last)
      }
    })
    return () => {
      cancelled = true
    }
    // run once per visit — spQ only decides whether a deep link wins
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [reloadViews])

  useEffect(() => {
    api<{ teams: UiTeam[] }>(`teams${topEntity ? `?entity=${encodeURIComponent(topEntity)}` : ''}`)
      .then((d) => setTeams(d.teams ?? []))
      .catch(() => setTeams([])) // the picker just hides when teams are unavailable
  }, [topEntity])

  useEffect(() => {
    refreshActive()
  }, [refreshActive])

  // Push instead of poll: the SSE stream says when anything in the inbox
  // changed, and only then do we re-fetch what's on screen.
  const refreshRef = useRef({ refreshThreads, refreshActive })
  refreshRef.current = { refreshThreads, refreshActive }
  useEffect(
    () =>
      subscribeChatStream(() => {
        refreshRef.current.refreshThreads()
        refreshRef.current.refreshActive()
      }),
    [],
  )

  // inbound email: nudge the IMAP sync while the inbox is open (server throttles)
  useEffect(() => {
    const sync = () => api('messaging/email/sync', { method: 'POST', body: '{}' }).catch(() => {})
    sync()
    const t = setInterval(sync, 60000)
    return () => clearInterval(t)
  }, [])

  // Switching the entity scope (top bar, or the filter's entity pick) closes a
  // conversation the new scope no longer contains — asked of the server by id,
  // so a chat that is merely below the loaded page is not closed by mistake.
  const scopeKey = JSON.stringify(filters.entities ?? (topEntity ? [topEntity] : []))
  const scopeRef = useRef(scopeKey)
  useEffect(() => {
    if (scopeRef.current === scopeKey) return
    scopeRef.current = scopeKey
    if (!activeId) return
    const p = new URLSearchParams({ count_only: '1', thread: `${activeId.channel}:${activeId.key}` })
    const ents: string[] = JSON.parse(scopeKey)
    if (ents.length) p.set('entity', ents.join(','))
    api<{ count: number }>(`inbox/threads?${p}`)
      .then((d) => {
        if (d.count === 0) {
          setActiveId(null)
          setActive(null)
        }
      })
      .catch(() => {})
  }, [scopeKey, activeId])

  // quick replies: rendered against the linked contact AND limited to the
  // templates allowed on this conversation's channel
  const quickReplyContactId = active?.contact_id ?? null
  const quickReplyChannel = active?.channel ?? null
  useEffect(() => {
    const p = new URLSearchParams()
    if (quickReplyContactId) p.set('contact_id', String(quickReplyContactId))
    if (quickReplyChannel) p.set('channel', quickReplyChannel)
    const qs = p.toString()
    api<{ canned: CannedResponse[] }>(`messaging/canned${qs ? `?${qs}` : ''}`)
      .then((r) => setCanned(r.canned))
      .catch(() => setCanned([]))
  }, [quickReplyContactId, quickReplyChannel])

  // always-on chat summary: load the CACHED summary, auto-generate only when
  // none exists yet; a "stale" flag shows when it's behind.
  useEffect(() => {
    if (!activeId) {
      setSummaryState(null)
      summaryPathRef.current = null
      return
    }
    let cancelled = false
    lastAutoGen.current = 0
    const path = `messaging/threads/${activeId.channel}/${activeId.key}/summary`
    summaryPathRef.current = path
    const check = async () => {
      try {
        const s = await api<SummaryState>(path)
        if (cancelled) return
        setSummaryState(s)
        const needsRegen = s.message_count > 0 && !s.summary
        if (needsRegen && Date.now() - lastAutoGen.current > 300000) {
          lastAutoGen.current = Date.now()
          setSummarizingFor(path)
          try {
            const r = await api<SummaryState>(path, { method: 'POST', body: '{}' })
            if (!cancelled) setSummaryState(r)
          } catch {
            /* AI/provider hiccup — cached version stays */
          } finally {
            setSummarizingFor((v) => (v === path ? null : v))
          }
        }
      } catch {
        /* thread may not exist yet */
      }
    }
    check()
    const unsubscribe = subscribeChatStream(check)
    return () => {
      cancelled = true
      unsubscribe()
    }
  }, [activeId])

  const regenSummary = async () => {
    if (!activeId || summarizing) return
    const path = `messaging/threads/${activeId.channel}/${activeId.key}/summary`
    setSummarizingFor(path)
    try {
      const r = await api<SummaryState>(path, { method: 'POST', body: '{}' })
      if (summaryPathRef.current === path) setSummaryState(r)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setSummarizingFor((v) => (v === path ? null : v))
    }
  }

  // combined cross-channel view: poll the linked contact's merged conversation
  const activeContactId = active?.contact_id ?? null
  useEffect(() => {
    if (!activeContactId) {
      setCombined(null)
      return
    }
    let cancelled = false
    const load = () =>
      api<CombinedConversation>(`messaging/contacts/${activeContactId}/conversation`)
        .then((c) => {
          if (!cancelled) setCombined(c)
        })
        .catch(() => {})
    load()
    const t = setInterval(load, 2500)
    return () => {
      cancelled = true
      clearInterval(t)
    }
  }, [activeContactId])

  useEffect(() => {
    scrollRef.current?.scrollTo({ top: scrollRef.current.scrollHeight })
  }, [active?.messages.length, combinedOn, combined?.messages.length])

  // per-thread composer/call-pill state resets on switch
  useEffect(() => {
    setPendingFile(null)
    setTplOpen(false)
    setEndedFlash(false)
    setStarting(false)
    setComposerMode('reply')
    setLinkSel('')
    setCombinedOn(false)
    setVoiceOnly(false)
    prevCallStatus.current = undefined
  }, [activeId?.channel, activeId?.key])

  // flash a "Call ended" pill for a few seconds after an active call drops
  useEffect(() => {
    const s = active?.call?.status
    const prev = prevCallStatus.current
    prevCallStatus.current = s
    if (prev && ['connecting', 'ringing', 'in_call'].includes(prev) && (!s || !['connecting', 'ringing', 'in_call'].includes(s))) {
      setEndedFlash(true)
      const t = setTimeout(() => setEndedFlash(false), 5000)
      return () => clearTimeout(t)
    }
  }, [active?.call?.status])

  // Poll the live transcript while a WhatsApp call is up.
  useEffect(() => {
    const st = active?.channel === 'whatsapp' ? active?.call?.status : undefined
    const key = active?.key
    if (!key || !st || !['connecting', 'ringing', 'in_call'].includes(st)) {
      setCallLive({ turns: [], done: false })
      return
    }
    let stopped = false
    const poll = async () => {
      try {
        const d = await api<{ turns: { role: string; text: string }[]; done: boolean }>(`whatsapp/threads/${key}/call/live`)
        if (!stopped) setCallLive({ turns: d.turns ?? [], done: !!d.done })
      } catch {
        /* transient poll error — keep the last transcript */
      }
    }
    poll()
    const t = setInterval(poll, 1500)
    return () => {
      stopped = true
      clearInterval(t)
    }
  }, [active?.channel, active?.key, active?.call?.status])

  useEffect(() => {
    callScrollRef.current?.scrollTo({ top: callScrollRef.current.scrollHeight })
  }, [callLive.turns.length])

  // call recordings for the open WhatsApp thread; refetch when a call ends
  useEffect(() => {
    if (active?.channel !== 'whatsapp' || !active?.key) {
      setCallRecordings([])
      return
    }
    let cancelled = false
    api<{ recordings: CallRec[] }>(`whatsapp/threads/${active.key}/call/recordings`)
      .then((d) => {
        if (!cancelled) setCallRecordings(d.recordings || [])
      })
      .catch(() => {})
    return () => {
      cancelled = true
    }
  }, [active?.channel, active?.key, active?.call?.status])

  // Past SIP calls for the open contact — polled so a finished call surfaces.
  const sipContactId = active?.contact_id ?? null
  useEffect(() => {
    if (sipContactId == null) {
      setSipCalls([])
      return
    }
    let cancelled = false
    const load = () =>
      api<{ calls: SipCall[] }>(`contacts/${sipContactId}/calls`)
        .then((d) => {
          if (!cancelled) setSipCalls(d.calls ?? [])
        })
        .catch(() => {})
    load()
    const t = setInterval(load, 8000)
    return () => {
      cancelled = true
      clearInterval(t)
    }
  }, [sipContactId])

  const run = async (fn: () => Promise<unknown>) => {
    setBusy(true)
    setError(null)
    try {
      await fn()
      await Promise.all([refreshActive(), refreshThreads()])
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  /** Append a just-sent message so it shows before the next refresh. */
  const appendToActive = (m: UiMessage) =>
    setActive((prev) => (prev && !prev.messages.some((x) => x.id === m.id) ? { ...prev, messages: [...prev.messages, m] } : prev))

  const sendMessage = () =>
    run(async () => {
      if (!active) return
      const text = draft.trim()
      const isWa = active.channel === 'whatsapp'
      if (composerMode === 'note') {
        if (!text) return
        setDraft('')
        // notes are channel-agnostic (one crm.Thread row), so every channel —
        // WhatsApp included — goes through the messaging app's note route
        const r = await api<{ message: UiMessage }>(`messaging/threads/${active.channel}/${active.key}/note`, {
          method: 'POST',
          body: JSON.stringify({ text }),
        })
        appendToActive(r.message)
        return
      }
      if (isWa && pendingFile) {
        const form = new FormData()
        form.append('file', pendingFile)
        if (text) form.append('caption', text)
        const r = await apiUpload<{ message: UiMessage }>(`whatsapp/threads/${active.key}/send-media`, form)
        setPendingFile(null)
        setDraft('')
        appendToActive(r.message)
        return
      }
      if (!text) return
      const override = isWa && active.opted_out === true
      if (override && !window.confirm('Customer opted out — send anyway as a manual override?')) return
      setDraft('')
      await api(`${threadPath(active)}/send`, {
        method: 'POST',
        body: JSON.stringify(override ? { text, force: true } : { text }),
      })
    })

  const onPickFile = (e: ChangeEvent<HTMLInputElement>) => {
    const f = e.target.files?.[0]
    e.target.value = '' // allow re-picking the same file
    if (!f) return
    const ext = f.name.toLowerCase().split('.').pop() || ''
    if (!['jpg', 'jpeg', 'png', 'webp', 'pdf', 'mp4', 'ogg', 'mp3'].includes(ext)) {
      setError('Unsupported file type — use jpg, png, webp, pdf, mp4, ogg or mp3.')
      return
    }
    if (f.size > 16 * 1024 * 1024) {
      setError('File too large — WhatsApp media is limited to 16 MB.')
      return
    }
    setError(null)
    setPendingFile(f)
  }

  const toggleAi = () =>
    run(() =>
      active
        ? api(`${threadPath(active)}/ai`, { method: 'POST', body: JSON.stringify({ enabled: !active.ai_enabled }) })
        : Promise.resolve(),
    )

  const switchAccount = (accountId: string) =>
    run(() =>
      active && active.channel === 'whatsapp' && accountId
        ? api(`whatsapp/threads/${active.key}/account`, { method: 'POST', body: JSON.stringify({ account_id: accountId }) })
        : Promise.resolve(),
    )

  // Conversation status — every channel, WhatsApp included.
  const setThreadStatus = (s: ThreadStatus) =>
    run(() => (active ? api(`${threadPath(active)}/status`, { method: 'POST', body: JSON.stringify({ status: s }) }) : Promise.resolve()))

  /** Route this conversation to a team; "" clears it. */
  const assignTeam = (raw: string) =>
    run(async () => {
      if (!active) return
      await api('teams/assign', {
        method: 'POST',
        body: JSON.stringify({
          channel: active.channel,
          key: active.key,
          team_id: raw ? Number(raw) : 0,
          // rotate to the next member only when nobody is on it yet
          assign: !!raw && !active.assignee,
        }),
      })
    })

  // assignment is channel-agnostic (shared crm.Thread) — always the messaging app
  const assignTo = (username: string) =>
    run(() =>
      active
        ? api(`messaging/threads/${active.channel}/${active.key}/assign`, {
            method: 'POST',
            body: JSON.stringify({ assignee: username }),
          })
        : Promise.resolve(),
    )

  const saveLabels = (labels: string[]) =>
    run(() => (active ? api(`${threadPath(active)}/labels`, { method: 'POST', body: JSON.stringify({ labels }) }) : Promise.resolve()))

  // identity link — channel-agnostic, so it goes via the messaging app even for WhatsApp
  const linkContact = (contactId: number | null) =>
    run(() =>
      active
        ? api(`messaging/threads/${active.channel}/${active.key}/link`, {
            method: 'POST',
            body: JSON.stringify({ contact_id: contactId }),
          })
        : Promise.resolve(),
    )

  const startCall = async () => {
    setStarting(true) // instant feedback; cleared once the call status takes over
    try {
      await run(() =>
        api(`whatsapp/threads/${active?.key}/call/start`, {
          method: 'POST',
          body: JSON.stringify(callVoice ? { voice_provider: 'elevenlabs', voice_id: callVoice } : {}),
        }),
      )
    } finally {
      setStarting(false)
    }
  }
  const endCall = () => run(() => api(`whatsapp/threads/${active?.key}/call/end`, { method: 'POST' }))
  const sendSay = (mode: 'guide' | 'takeover') => {
    const text = sayDraft.trim()
    if (!text) return
    run(async () => {
      await api(`whatsapp/threads/${active?.key}/call/say`, { method: 'POST', body: JSON.stringify({ mode, text }) })
      setSayDraft('')
    })
  }
  const escalateCall = () => run(() => api(`whatsapp/threads/${active?.key}/call/escalate`, { method: 'POST' }))

  // ── saved views ──
  const applyFilters = (f: InboxFilters, viewId?: string) => {
    setFilters(f)
    if (viewId) {
      setActiveView(viewId)
      try {
        localStorage.setItem(VIEW_KEY, viewId)
      } catch {
        /* ignore */
      }
    }
  }
  const selectTab = (id: string) => {
    const builtin = BUILTIN_VIEWS.find((b) => b.id === id)
    if (builtin) return applyFilters(builtin.filters, id)
    const saved = views.find((v) => `v:${v.id}` === id)
    if (saved) applyFilters(normalizeFilters(saved.filters), id)
  }
  const activeSaved = views.find((v) => `v:${v.id}` === activeView)
  const tabs: ViewTab[] = [
    ...BUILTIN_VIEWS.map((b) => ({ id: b.id, name: b.name, active: sameFilters(filters, b.filters) })),
    ...views.map((v) => {
      const id = `v:${v.id}`
      const on = activeView === id
      return { id, name: v.name, active: on, modified: on && !sameFilters(filters, normalizeFilters(v.filters)) }
    }),
  ]
  // a built-in tab wins the highlight only when nothing saved is selected;
  // with nothing highlighted, a saved view whose filters are on screen lights up
  if (tabs.some((t) => t.id.startsWith('v:') && t.active)) tabs.forEach((t) => !t.id.startsWith('v:') && (t.active = false))
  else if (!tabs.some((t) => t.active)) {
    const match = views.find((v) => sameFilters(filters, normalizeFilters(v.filters)))
    const tab = match && tabs.find((t) => t.id === `v:${match.id}`)
    if (tab) tab.active = true
  }

  const saveView = async (name: string, f: InboxFilters) => {
    const v = await createView<InboxFilters>('chats', name, f)
    await reloadViews()
    applyFilters(f, `v:${v.id}`)
  }
  const saveViewChanges = async () => {
    if (!activeSaved) return
    try {
      await updateView<InboxFilters>(activeSaved.id, { filters })
      await reloadViews()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  // ── chip lookups ──
  const chips = activeChips(filters, {
    accountName: (id) => allAccounts.find((a) => a.id === id)?.name || `#${id}`,
    agentName: (u) => agents.find((a) => a.username === u)?.name || u,
    teamName: (id) => teams.find((t) => String(t.id) === id)?.name || `#${id}`,
    entityName: (slug) => entities.find((e) => e.slug === slug)?.name || slug,
    labelName: (slug) => tagMeta(slug).label,
  })
  // Contact-tag choices for the filter: every tag in use across the caller's
  // contacts (not only the top-bar entity's), plus whatever is loaded here.
  const [orgTags, setOrgTags] = useState<string[]>([])
  useEffect(() => {
    api<{ contacts: { tags?: string[] }[] }>('contacts?limit=2000')
      .then((d) => setOrgTags(Array.from(new Set(d.contacts.flatMap((c) => c.tags ?? [])))))
      .catch(() => setOrgTags([]))
  }, [])
  const contactTags = useMemo(
    () => Array.from(new Set([...orgTags, ...contacts.flatMap((c) => c.tags ?? [])])),
    [orgTags, contacts],
  )

  // ── derived conversation state ──
  const inCall = active?.channel === 'whatsapp' && ['in_call', 'ringing', 'connecting'].includes(active?.call?.status ?? '')
  // This contact's current SIP ("AI phone call")
  const sipCall = Object.values(calls)
    .filter((c) => c.contactId === active?.contact_id)
    .sort((a, b) => b.startedAt - a.startedAt)[0]
  const sipCallActive = !!sipCall && ['dialing', 'ringing', 'live'].includes(sipCall.status)
  const activeIsWa = active?.channel === 'whatsapp'
  const optedOut = activeIsWa && active?.opted_out === true

  const callStatus = activeIsWa ? active?.call?.status : undefined
  const callPill =
    (callStatus && ['connecting', 'ringing', 'in_call'].includes(callStatus)) || (activeIsWa && starting)
      ? {
          label:
            callStatus === 'in_call' ? 'On call' : callStatus === 'ringing' ? 'Ringing…' : callStatus === 'connecting' ? 'Connecting…' : 'Calling…',
          pulse: callStatus !== 'in_call',
          cls: 'bg-green/15 text-green-300',
        }
      : endedFlash
        ? { label: 'Call ended', pulse: false, cls: 'bg-fill text-text-muted' }
        : null

  const channelLive = (c: Channel) =>
    !status
      ? false
      : c === 'whatsapp'
        ? !status.whatsapp_sandbox
        : c === 'email'
          ? status.email_configured
          : c === 'facebook'
            ? status.facebook_configured
            : c === 'telegram'
              ? status.telegram_configured
              : c === 'instagram'
                ? status.instagram_configured
                : true

  const activeSandbox = active ? !channelLive(active.channel) : false
  // 24h customer-service window (live WhatsApp accounts only — sandbox is always open)
  const windowClosed = activeIsWa && !activeSandbox && active?.window_open === false

  const activeFilterCount = countActive(chips)
  const unfiltered = activeFilterCount === 0 && !q.trim()

  // Every contact appears in the inbox: real threads first, then a synthetic
  // "no conversation yet" row for each contact not yet covered by a thread —
  // only in the unfiltered list (and once it is fully loaded), since a filter
  // describes conversations and these are not conversations yet.
  const pendingRows: UiThreadSummary[] = useMemo(() => {
    if (!unfiltered || nextCursor || listLoading) return []
    const covered = new Set(threads.map((t) => t.contact_id).filter(Boolean) as number[])
    const waKeys = new Set(threads.filter((t) => t.channel === 'whatsapp').map((t) => t.key))
    const rows: UiThreadSummary[] = []
    for (const d of contacts) {
      const dig = (d.phone || '').replace(/\D/g, '')
      if (!dig || covered.has(d.id) || waKeys.has(dig)) continue
      rows.push({
        channel: 'whatsapp',
        key: dig,
        name: d.name,
        sub: d.phone,
        ai_enabled: false,
        contact_id: d.id,
        last_message: 'No conversation yet',
        last_ts: '',
        pending: true,
      })
    }
    return rows.sort((a, b) => a.name.localeCompare(b.name))
  }, [unfiltered, nextCursor, listLoading, threads, contacts])

  // Same linked contact across several channels is ONE inbox entry (unless the
  // list is narrowed to a single channel, which is explicitly per-channel).
  const visibleRows: UiThreadSummary[] = useMemo(() => {
    const all = [...threads, ...pendingRows]
    if (filters.channels.length === 1) return all
    const groups = new Map<number, UiThreadSummary[]>()
    const order: (number | UiThreadSummary)[] = []
    for (const t of all) {
      if (t.contact_id != null && !t.pending) {
        const g = groups.get(t.contact_id)
        if (g) g.push(t)
        else {
          groups.set(t.contact_id, [t])
          order.push(t.contact_id)
        }
      } else order.push(t)
    }
    return order.map((o) => {
      if (typeof o !== 'number') return o
      const rows = groups.get(o)!
      if (rows.length === 1) return rows[0]
      // the server's order already put the representative first
      return {
        ...rows[0],
        labels: Array.from(new Set(rows.flatMap((r) => r.labels ?? []))),
        waiting: rows.some((r) => r.waiting),
        _mergedChannels: Array.from(new Set(rows.map((r) => r.channel))),
      }
    })
  }, [threads, pendingRows, filters.channels.length])

  // canned-response picker: type "/" (Chatwoot's shortcut) in reply mode
  const cannedQuery = composerMode === 'reply' && draft.startsWith('/') ? draft.slice(1).toLowerCase() : null
  const cannedMatches =
    cannedQuery === null
      ? []
      : canned.filter((c) => c.short_code.startsWith(cannedQuery) || c.content.toLowerCase().includes(cannedQuery)).slice(0, 6)

  // combined mode swaps the single thread for the contact's merged timeline
  type TimelineItem = UiMessage & {
    channel?: Channel
    key?: string
    /** A past voice call interleaved into the chat at its time. */
    _callRec?: CallRec
    /** A past SIP phone call. */
    _sipCall?: SipCall
  }
  const baseMessages: TimelineItem[] = voiceOnly ? [] : combinedOn && combined ? combined.messages : active?.messages ?? []
  const oldestLoadedTs = baseMessages[0]?.ts
  const fullyLoaded = baseMessages.length < 50 // page size — older pages exist past 50
  // Calls stay in the channel they happened on: the WhatsApp thread shows its
  // own recordings; every AI call (any transport) shows once in "All channels".
  const callItems: TimelineItem[] =
    voiceOnly || (!combinedOn && active?.channel === 'whatsapp')
      ? callRecordings
          .filter((r) => fullyLoaded || !oldestLoadedTs || r.created >= oldestLoadedTs)
          .map((r) => ({
            id: `call-${r.name}`,
            dir: 'out' as const,
            source: 'system' as const,
            text: 'Voice call',
            ts: r.created,
            status: 'sent',
            _callRec: r,
          }))
      : []
  const sipCallItems: TimelineItem[] = (combinedOn && sipContactId != null ? sipCalls : [])
    .map((r) => {
      const ts = r.ended_at || r.started_at
      return { id: `sip-${r.call_id}`, dir: 'out' as const, source: 'system' as const, text: 'AI voice call', ts, status: 'sent', _sipCall: r }
    })
    .filter((m) => fullyLoaded || !oldestLoadedTs || m.ts >= oldestLoadedTs)
  const extraItems = [...callItems, ...sipCallItems]
  const displayMessages: TimelineItem[] = extraItems.length
    ? [...baseMessages, ...extraItems].sort((a, b) => (a.ts < b.ts ? -1 : a.ts > b.ts ? 1 : 0))
    : baseMessages
  const grouped: { day: string; items: TimelineItem[] }[] = []
  for (const m of displayMessages) {
    const day = dayLabel(m.ts)
    const last = grouped[grouped.length - 1]
    if (last && last.day === day) last.items.push(m)
    else grouped.push({ day, items: [m] })
  }

  // Context switches the customer can't see from our side: a sender-account
  // change within a thread, and (merged view) moving to the contact's OTHER
  // number/channel. Both get a divider at the exact switch point.
  const msgAccountIds = new Set(displayMessages.filter((m) => m.account_id).map((m) => m.account_id))
  const multiAccount = msgAccountIds.size > 1 || waAccounts.length > 1
  const acctLabel = (m: UiMessage) => m.account_name || `account #${m.account_id}`
  const switchNotices = new Map<string, string>()
  {
    let prevContact: string | null = null
    let prevAcct: string | null = null
    for (const m of displayMessages) {
      const contact = combinedOn && m.channel && m.key ? `${m.channel}:${m.key}` : null
      if (contact !== null) {
        if (prevContact !== null && contact !== prevContact) {
          const addr = m.channel === 'whatsapp' ? `+${m.key}` : m.key
          switchNotices.set(
            m.id,
            `Now chatting with this contact's other contact — ${CHANNEL_META[m.channel!]?.lbl ?? m.channel} ${addr}. ` +
              `On the customer's side this is a separate chat with no shared history.`,
          )
        }
        prevContact = contact
      }
      if (m.account_id) {
        if (prevAcct !== null && m.account_id !== prevAcct && !switchNotices.has(m.id)) {
          const provider = waAccounts.find((a) => a.id === m.account_id)?.provider
          const seenBefore = displayMessages.some((x) => x !== m && x.account_id === m.account_id && x.ts < m.ts)
          switchNotices.set(
            m.id,
            provider === 'baileys'
              ? `Switched sender number — now via ${acctLabel(m)} (personal number, no template needed).` +
                  (seenBefore
                    ? ' This number has chatted with the customer before.'
                    : ' Cold first contact from this number carries ban risk — safest is a customer-initiated reply first.')
              : seenBefore
                ? `Switched sender number — now via ${acctLabel(m)}. The customer knows this number; if they ` +
                  `haven't replied to it within 24h, open with an approved template.`
                : `Switched sender number — now via ${acctLabel(m)}. A new number for this customer ` +
                  `(their side shows a new chat) — open with an approved template or have them reply first.`,
          )
        }
        prevAcct = m.account_id
      }
    }
  }

  // Fleet strip → jump to a live call's contact thread.
  const openContactThread = (contactId: number) => {
    const t = threads.find((x) => x.contact_id === contactId)
    if (t) setActiveId({ channel: t.channel, key: t.key })
  }

  // Open an inbox row. A pending contact row has no thread yet — create it on
  // demand (idempotent get-or-create by phone), then open it.
  const openRow = (t: UiThreadSummary) => {
    setError(null)
    if ((t._mergedChannels?.length ?? 0) > 1) setCombinedOn(true)
    if (!t.pending) {
      setActiveId({ channel: t.channel, key: t.key })
      return
    }
    run(async () => {
      const r = await api<{ wa_id: string }>('whatsapp/threads', { method: 'POST', body: JSON.stringify({ phone: t.sub }) })
      setActiveId({ channel: 'whatsapp', key: r.wa_id })
    })
  }

  const noteMode = composerMode === 'note'

  return (
    <div className="flex h-full min-h-0 flex-col">
      <ActiveCallsStrip onOpenContact={openContactThread} activeContactId={active?.contact_id} />

      <div className="flex min-h-0 flex-1 overflow-hidden">
        <ChatListPane
          rows={visibleRows}
          total={total}
          loading={listLoading}
          hasMore={!!nextCursor}
          loadingMore={loadingMore}
          onLoadMore={loadMore}
          activeId={activeId}
          onOpen={openRow}
          search={search}
          onSearch={setSearch}
          activeFilterCount={activeFilterCount}
          chips={chips}
          onRemoveChip={(c) => setFilters((f) => c.clear(f))}
          onClearFilters={() => {
            setSearch('')
            setQ('')
            applyFilters({ ...EMPTY_FILTERS, sort: filters.sort }, 'all')
          }}
          onOpenFilters={() => setFilterOpen(true)}
          tabs={tabs}
          onSelectTab={selectTab}
          onManageViews={() => setManageOpen(true)}
          onSaveViewChanges={activeSaved ? saveViewChanges : undefined}
          hidden={!!active}
        />

        {/* ── conversation (paper) ── */}
        <section
          aria-label="Conversation"
          className={`cb-paper min-w-0 flex-1 flex-col lg:min-w-[380px] ${active ? 'flex' : 'hidden lg:flex'}`}
        >
          {!active ? (
            <div className="grid flex-1 place-content-center px-6 text-center">
              <div className="display text-[26px] text-text-heading">Pick a conversation</div>
              <div className="mx-auto mt-2 max-w-[340px] text-[13px] leading-relaxed text-text-muted">
                One inbox for WhatsApp, Messenger, Instagram, Telegram, email and your website — use Filter to narrow it
                down, and save the filters you reach for as a view.
              </div>
            </div>
          ) : (
            <>
              {/* header */}
              <div className="flex shrink-0 flex-wrap items-center gap-x-3 gap-y-2 border-b border-line px-4 py-3 sm:px-5">
                <button
                  onClick={() => {
                    setActiveId(null)
                    setActive(null)
                  }}
                  aria-label="Back to conversations"
                  title="Back"
                  className="-ml-1 grid h-8 w-8 shrink-0 place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high lg:hidden"
                >
                  <ChevronLeft size={18} aria-hidden />
                </button>
                <div className="min-w-[170px] flex-1">
                  <div className="flex items-center gap-2">
                    <h2 className="display truncate text-[21px] leading-tight text-text-heading">{active.name}</h2>
                    {optedOut && (
                      <span
                        className="inline-flex shrink-0 items-center gap-1 rounded-full bg-red/10 px-2 py-px text-[10.5px] font-semibold text-red-300"
                        title="Customer opted out of messaging — sends require a manual override"
                      >
                        <Ban size={10} aria-hidden /> Opted out
                      </span>
                    )}
                    {active.status && active.status !== 'open' && (
                      <span className="shrink-0 rounded-full border border-line-strong px-2 py-px text-[10.5px] capitalize text-text-mid">
                        {active.status}
                      </span>
                    )}
                  </div>
                  <div className="mt-0.5 flex min-w-0 items-center gap-1.5 text-[12.5px] text-text-muted">
                    <ChannelLogo channel={logoKey(active.channel)} size={13} />
                    <span className="truncate">
                      {active.sub}
                      {active.channel === 'whatsapp' && active.account_id && waAccounts.length > 0 && (
                        <> · via {waAccounts.find((a) => a.id === String(active.account_id))?.name ?? `#${active.account_id}`}</>
                      )}
                    </span>
                    {CHANNEL_META[active.channel].beta && <BetaChip />}
                  </div>
                </div>
                <div className="flex shrink-0 flex-wrap items-center gap-1.5">
                  {/* AI auto-reply on/off (email has no auto-reply) */}
                  {active.channel !== 'email' && (
                    <button
                      onClick={toggleAi}
                      disabled={busy || optedOut}
                      role="switch"
                      aria-checked={!!active.ai_enabled && !optedOut}
                      title={optedOut ? 'Customer opted out — AI disabled' : active.ai_enabled ? 'Turn AI off' : 'Turn AI on'}
                      aria-label="Toggle AI auto-reply"
                      className={`flex h-8 shrink-0 items-center gap-1.5 rounded-full border px-2.5 text-[11px] font-semibold transition-colors disabled:cursor-not-allowed disabled:opacity-50 ${
                        active.ai_enabled && !optedOut
                          ? 'border-line-strong bg-[var(--nav-active-bg)] text-[color:var(--nav-active-text)]'
                          : 'border-line bg-card text-text-muted'
                      }`}
                    >
                      AI
                      <span
                        className={`relative inline-flex h-3.5 w-6 items-center rounded-full transition-colors ${
                          active.ai_enabled && !optedOut ? 'bg-primary' : 'bg-fill-hover'
                        }`}
                      >
                        <span
                          className={`absolute h-2.5 w-2.5 rounded-full transition-transform ${
                            active.ai_enabled && !optedOut ? 'bg-accent' : 'bg-card'
                          }`}
                          style={{ transform: active.ai_enabled && !optedOut ? 'translateX(12px)' : 'translateX(2px)' }}
                        />
                      </span>
                    </button>
                  )}
                  {callPill && (
                    <span
                      className={`flex shrink-0 items-center gap-1 rounded-full px-2 py-0.5 text-[10.5px] font-semibold ${callPill.cls} ${
                        callPill.pulse ? 'animate-pulse' : ''
                      }`}
                    >
                      <PhoneCall size={10} aria-hidden /> {callPill.label}
                    </span>
                  )}
                  {/* status — every channel, WhatsApp included */}
                  {active.status !== 'resolved' ? (
                    <Button variant="secondary" size="sm" onClick={() => setThreadStatus('resolved')} disabled={busy} title="Mark conversation resolved">
                      <CheckCircle2 size={13} aria-hidden /> Resolve
                    </Button>
                  ) : (
                    <Button variant="secondary" size="sm" onClick={() => setThreadStatus('open')} disabled={busy} title="Re-open conversation">
                      <RotateCcw size={13} aria-hidden /> Reopen
                    </Button>
                  )}
                  {active.channel === 'whatsapp' &&
                    (inCall ? (
                      <button
                        onClick={endCall}
                        disabled={busy}
                        title="End WhatsApp call"
                        aria-label="End WhatsApp call"
                        className="grid h-8 w-8 place-items-center rounded-lg bg-red/10 text-red-300 transition-colors hover:bg-red/20"
                      >
                        <PhoneOff size={15} aria-hidden />
                      </button>
                    ) : starting ? (
                      <span title="Calling…" aria-label="Calling…" className="grid h-8 w-8 place-items-center rounded-lg text-text-muted">
                        <Loader2 size={15} className="animate-spin" aria-hidden />
                      </span>
                    ) : (
                      <button
                        onClick={startCall}
                        disabled={busy}
                        title="Start WhatsApp call"
                        aria-label="Start WhatsApp call"
                        className="grid h-8 w-8 place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
                      >
                        <ChannelLogo channel="whatsapp" size={15} variant="mono" title="" />
                      </button>
                    ))}
                  {/* AI phone call (SIP) — any linked contact */}
                  {active.contact_id != null && <VoiceCallButton contactId={active.contact_id} />}
                  {/* One-way script call: dial, read a fixed script, hang up */}
                  {active.contact_id != null && <ScriptCallButton contactId={active.contact_id} />}
                  <button
                    onClick={() => setInfoOpen(!infoOpen)}
                    title="Conversation details"
                    aria-label="Toggle conversation details"
                    aria-pressed={infoOpen}
                    className={`hidden h-8 w-8 place-items-center rounded-lg transition-colors xl:grid ${
                      infoOpen ? 'bg-fill-hover text-text-high' : 'text-text-muted hover:bg-fill-hover'
                    }`}
                  >
                    <Info size={15} aria-hidden />
                  </button>
                </div>
              </div>

              {/* linked contact: single channel ⇄ combined view ⇄ voice-only history */}
              {active.contact_id != null && combined && (
                <div className="no-scrollbar flex shrink-0 items-center gap-1.5 overflow-x-auto border-b border-line px-4 py-2 sm:px-5">
                  <span className="eyebrow shrink-0 pr-1">{combined.contact.name} on</span>
                  <button
                    onClick={() => {
                      setCombinedOn(true)
                      setVoiceOnly(false)
                    }}
                    className={`cb-switch ${combinedOn && !voiceOnly ? 'is-on' : ''}`}
                  >
                    <Layers size={11} aria-hidden /> All channels
                  </button>
                  {combined.channels.map((c) => {
                    const isCurrent = !voiceOnly && !combinedOn && activeId?.channel === c.channel && activeId?.key === c.key
                    return (
                      <button
                        key={`${c.channel}-${c.key}`}
                        onClick={() => {
                          setCombinedOn(false)
                          setVoiceOnly(false)
                          if (activeId?.channel !== c.channel || activeId?.key !== c.key) setActiveId({ channel: c.channel, key: c.key })
                        }}
                        className={`cb-switch ${isCurrent ? 'is-on' : ''}`}
                      >
                        <ChannelLogo channel={logoKey(c.channel)} size={11} variant={isCurrent ? 'mono' : 'mark'} title="" />{' '}
                        {CHANNEL_META[c.channel]?.lbl ?? c.channel}
                      </button>
                    )
                  })}
                  <button
                    onClick={() => {
                      setCombinedOn(true)
                      setVoiceOnly(true)
                    }}
                    title="Show call history only"
                    className={`cb-switch ${voiceOnly ? 'is-on' : ''}`}
                  >
                    <PhoneCall size={11} aria-hidden /> Voice
                  </button>
                </div>
              )}

              {/* always-on AI summary — pinned above the conversation */}
              {summaryState && summaryState.message_count > 0 && (
                <div className="shrink-0 border-b border-line bg-surface px-4 py-2.5 sm:px-5">
                  <div className="flex items-center gap-1.5">
                    <Sparkles size={12} className="shrink-0 text-brand-300" aria-hidden />
                    <span className="eyebrow">Summary</span>
                    {summaryState.summary?.heuristic && (
                      <span className="rounded border border-line px-1.5 py-px text-[9px] font-bold uppercase text-text-muted" title="No AI key configured — basic summary">
                        basic
                      </span>
                    )}
                    {summarizing && (
                      <span className="flex items-center gap-1 text-[11px] text-text-muted">
                        <Loader2 size={10} className="animate-spin" aria-hidden /> updating…
                      </span>
                    )}
                    <div className="flex-1" />
                    {summaryState.summary && !summarizing && (
                      <span className="text-[11px] text-text-muted">
                        {summaryState.stale ? 'may be outdated · ' : ''}
                        {timeAgo(summaryState.summary.generated_at)}
                      </span>
                    )}
                    <button
                      onClick={regenSummary}
                      disabled={summarizing}
                      title="Refresh summary"
                      aria-label="Refresh summary"
                      className="grid h-6 w-6 place-items-center rounded text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high disabled:opacity-40"
                    >
                      <RefreshCw size={11} className={summarizing ? 'animate-spin' : ''} aria-hidden />
                    </button>
                    <button
                      onClick={() => setSummaryOpen(!summaryOpen)}
                      title={summaryOpen ? 'Collapse summary' : 'Expand summary'}
                      aria-label={summaryOpen ? 'Collapse summary' : 'Expand summary'}
                      aria-expanded={summaryOpen}
                      className="grid h-6 w-6 place-items-center rounded text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
                    >
                      <ChevronLeft size={11} className={`transition-transform ${summaryOpen ? '-rotate-90' : 'rotate-180'}`} aria-hidden />
                    </button>
                  </div>
                  {summaryOpen && (
                    <p className="mt-1 text-[13.5px] leading-[1.6] text-text-high">
                      {summaryState.summary?.summary ?? (summarizing ? 'Generating the first summary…' : 'No summary yet — press refresh.')}
                    </p>
                  )}
                </div>
              )}

              {/* live WhatsApp call monitor */}
              {inCall && (
                <div className="shrink-0 space-y-1.5 border-b border-line bg-well p-3">
                  <div className="flex items-center justify-between">
                    <span className="eyebrow">Live transcript</span>
                    {callLive.done && <span className="text-[11px] text-text-muted">agent finished</span>}
                  </div>
                  <div ref={callScrollRef} className="max-h-36 space-y-1 overflow-y-auto pr-1 text-[12.5px] leading-snug">
                    {callLive.turns.length === 0 ? (
                      <div className="flex items-center gap-1.5 py-1 text-text-muted">
                        <Loader2 size={12} className="animate-spin" aria-hidden />
                        {active.call?.status === 'in_call' ? 'Waiting for the agent to speak…' : 'Connecting the call…'}
                      </div>
                    ) : (
                      callLive.turns.map((t, i) => (
                        <div key={i} className={t.role === 'assistant' ? 'text-brand-300' : 'text-text-mid'}>
                          <span className="font-semibold">{t.role === 'assistant' ? 'AI' : 'Caller'}:</span> {t.text}
                        </div>
                      ))
                    )}
                  </div>
                  <div className="flex flex-wrap gap-1.5">
                    <textarea
                      value={sayDraft}
                      onChange={(e) => setSayDraft(e.target.value)}
                      rows={1}
                      placeholder="Steer the AI (Guide) or type words for it to say verbatim (Takeover)…"
                      className="min-w-[200px] flex-1 resize-none rounded-lg border border-line-strong bg-card px-2.5 py-1.5 text-[12.5px] text-text-primary placeholder:text-text-faint focus:border-text-primary focus:outline-none"
                    />
                    <Button variant="secondary" size="sm" onClick={() => sendSay('guide')} disabled={busy || !sayDraft.trim()}>
                      <Sparkles size={13} aria-hidden /> Guide
                    </Button>
                    <Button variant="secondary" size="sm" onClick={() => sendSay('takeover')} disabled={busy || !sayDraft.trim()}>
                      <Send size={13} aria-hidden /> Takeover
                    </Button>
                    <Button variant="secondary" size="sm" onClick={escalateCall} disabled={busy}>
                      <UserRound size={13} aria-hidden /> Escalate
                    </Button>
                  </div>
                </div>
              )}

              {/* live SIP ("AI phone call") monitor */}
              {sipCallActive && sipCall && (
                <div className="shrink-0 space-y-1.5 border-b border-line bg-well p-3">
                  <div className="flex items-center justify-between">
                    <span className="eyebrow">Live transcript — AI phone call</span>
                    <span className="text-[11px] text-text-muted">{sipCall.status === 'live' ? 'in call' : sipCall.statusText}</span>
                  </div>
                  <div className="max-h-36 space-y-1 overflow-y-auto pr-1 text-[12.5px] leading-snug">
                    {sipCall.turns.length === 0 && sipCall.extraLines.length === 0 ? (
                      <div className="flex items-center gap-1.5 py-1 text-text-muted">
                        <Loader2 size={12} className="animate-spin" aria-hidden />
                        {sipCall.status === 'live' ? 'Waiting for the agent to speak…' : 'Connecting the call…'}
                      </div>
                    ) : (
                      <>
                        {sipCall.turns.map((t, i) => (
                          <div key={i} className={t.who === 'ai' ? 'text-brand-300' : 'text-text-mid'}>
                            <span className="font-semibold">{t.who === 'ai' ? 'AI' : 'Caller'}:</span> {t.t}
                          </div>
                        ))}
                        {sipCall.extraLines.map((l, i) => (
                          <div key={'x' + i} className={l.human ? 'text-amber-300' : 'text-brand-300'}>
                            <span className="font-semibold">{l.human ? 'You' : 'AI'}:</span> {l.t}
                          </div>
                        ))}
                      </>
                    )}
                  </div>
                  <div className="flex flex-wrap gap-1.5">
                    <textarea
                      value={sipSayDraft}
                      onChange={(e) => setSipSayDraft(e.target.value)}
                      rows={1}
                      placeholder={
                        sipCall.humanControl ? 'Type words for the agent to say verbatim…' : 'Steer the AI (Guide) or type words for it to say verbatim (Takeover)…'
                      }
                      className="min-w-[200px] flex-1 resize-none rounded-lg border border-line-strong bg-card px-2.5 py-1.5 text-[12.5px] text-text-primary placeholder:text-text-faint focus:border-text-primary focus:outline-none"
                    />
                    <Button
                      variant="secondary"
                      size="sm"
                      onClick={() => {
                        if (!sipSayDraft.trim()) return
                        sendSipSay(sipCall.room, sipSayDraft.trim(), 'guide')
                        setSipSayDraft('')
                      }}
                      disabled={!sipSayDraft.trim()}
                    >
                      <Sparkles size={13} aria-hidden /> Guide
                    </Button>
                    <Button
                      variant="secondary"
                      size="sm"
                      onClick={() => {
                        if (!sipSayDraft.trim()) return
                        sendSipSay(sipCall.room, sipSayDraft.trim(), 'takeover')
                        setSipSayDraft('')
                      }}
                      disabled={!sipSayDraft.trim()}
                    >
                      <Send size={13} aria-hidden /> Takeover
                    </Button>
                    <Button variant="secondary" size="sm" onClick={() => setHumanControl(sipCall.room, !sipCall.humanControl)}>
                      <UserRound size={13} aria-hidden /> {sipCall.humanControl ? 'Return AI' : 'Take over'}
                    </Button>
                    <Button variant="secondary" size="sm" onClick={() => escalateSipCall(sipCall.room)}>
                      <UserRound size={13} aria-hidden /> Escalate
                    </Button>
                    <Button variant="danger" size="sm" onClick={() => hangupCall(sipCall.room)}>
                      <PhoneOff size={13} aria-hidden /> End
                    </Button>
                  </div>
                </div>
              )}

              {/* messages with day separators */}
              <div ref={scrollRef} className="min-h-0 flex-1 overflow-y-auto px-4 py-4 sm:px-6">
                {displayMessages.length === 0 && (
                  <div className="py-10 text-center text-[13px] text-text-muted">
                    No messages yet — {active.channel === 'email' ? 'compose the first email below.' : 'say hello below.'}
                  </div>
                )}
                {grouped.map((g) => (
                  <div key={g.day}>
                    <div className="my-3">
                      <CenterNote>{g.day}</CenterNote>
                    </div>
                    {g.items.map((m) => (
                      <div key={`${m.channel ?? ''}${m.id}`}>
                        {switchNotices.has(m.id) && (
                          <div className="my-3 text-center">
                            <span className="inline-block max-w-[85%] rounded-lg border border-amber/40 bg-amber/10 px-3 py-1.5 text-[11.5px] leading-relaxed text-amber-300">
                              {switchNotices.get(m.id)}
                            </span>
                          </div>
                        )}
                        {m._sipCall ? (
                          <SipCallCard rec={m._sipCall} />
                        ) : m._callRec ? (
                          <div className="my-2 flex justify-center">
                            <div className="w-[85%] max-w-[440px]">
                              <CenterNote>
                                <span className="inline-flex items-center gap-1.5">
                                  <PhoneCall size={11} aria-hidden /> Voice call · {timeShort(m.ts)}
                                </span>
                              </CenterNote>
                              <div className="mt-1">
                                <CallEntry rec={m._callRec} />
                              </div>
                            </div>
                          </div>
                        ) : m.source === 'system' && !m.private ? (
                          m.text.startsWith('📞 Call transcript') || m.text.startsWith('Call transcript') ? (
                            <CallTranscriptChip text={m.text} time={timeShort(m.ts)} />
                          ) : (
                            <div className="my-2">
                              <CenterNote>
                                {m.text} · {timeShort(m.ts)}
                              </CenterNote>
                            </div>
                          )
                        ) : (
                          <div className={`mb-2 flex ${m.dir === 'out' ? 'justify-end' : 'justify-start'}`}>
                            <div className={`cb-bubble ${m.private ? 'cb-bubble-note' : m.dir === 'out' ? 'cb-bubble-out' : 'cb-bubble-in'}`}>
                              {combinedOn && m.channel && <ChannelBadge channel={m.channel} />}
                              {m.private && (
                                <span className="cb-bubble-source">
                                  <Lock size={8} className="mr-0.5 inline" aria-hidden /> Private note — not sent
                                </span>
                              )}
                              {!m.private && m.dir === 'out' && m.source === 'ai' && <span className="cb-bubble-source">AI assistant</span>}
                              {m.media && <MediaAttachment media={m.media} />}
                              {m.text}
                              <span className="cb-bubble-time">
                                {(multiAccount || combinedOn) && !m.private && m.account_id && (
                                  <span title={`${m.dir === 'out' ? 'Sent from' : 'Received on'} ${acctLabel(m)}`}>via {acctLabel(m)} ·</span>
                                )}
                                <span>{timeShort(m.ts)}</span>
                                {m.dir === 'out' && !m.private && <StatusTick status={m.status} />}
                              </span>
                            </div>
                          </div>
                        )}
                      </div>
                    ))}
                  </div>
                ))}
              </div>

              {error && (
                <div role="alert" className="flex items-center gap-2 border-t border-red/30 bg-red/10 px-4 py-1.5 text-[12px] text-red-300">
                  <span className="min-w-0 flex-1">{error}</span>
                  <button onClick={() => setError(null)} aria-label="Dismiss" className="shrink-0 hover:text-text-high">
                    <X size={12} aria-hidden />
                  </button>
                </div>
              )}

              {/* composer (single channel) — the combined view is read-only */}
              {combinedOn ? (
                <div className="shrink-0 border-t border-line p-3">
                  <div className="flex items-center gap-2 rounded-lg border border-dashed border-line-strong px-3 py-2.5 text-[12.5px] text-text-muted">
                    <Layers size={13} className="shrink-0" aria-hidden />
                    Combined view of every channel — pick a channel above to reply.
                  </div>
                </div>
              ) : (
                <>
                {activeId && (
                  <PendingDraftBanner
                    channel={activeId.channel}
                    threadKey={activeId.key}
                    refreshToken={active?.messages.length}
                  />
                )}
                <div className="shrink-0 border-t border-line px-3 pb-3 pt-2 sm:px-4">
                  <div className="mb-2 flex gap-1" role="tablist" aria-label="Composer mode">
                    <button
                      role="tab"
                      aria-selected={!noteMode}
                      onClick={() => setComposerMode('reply')}
                      className={`flex items-center gap-1 rounded-md px-2.5 py-1 text-[12px] font-semibold transition-colors ${
                        !noteMode ? 'bg-[var(--nav-active-bg)] text-text-heading' : 'text-text-muted hover:bg-fill'
                      }`}
                    >
                      <Send size={11} aria-hidden /> Reply
                    </button>
                    <button
                      role="tab"
                      aria-selected={noteMode}
                      onClick={() => setComposerMode('note')}
                      className={`flex items-center gap-1 rounded-md px-2.5 py-1 text-[12px] font-semibold transition-colors ${
                        noteMode ? 'bg-amber/15 text-amber-300' : 'text-text-muted hover:bg-fill'
                      }`}
                    >
                      <StickyNote size={11} aria-hidden /> Private note
                    </button>
                  </div>
                  {cannedMatches.length > 0 && (
                    <div className="mb-2 overflow-hidden rounded-lg border border-line bg-card shadow-pop">
                      <div className="eyebrow border-b border-line px-3 py-1.5">Templates</div>
                      {cannedMatches.map((c) => (
                        <button
                          key={c.id}
                          onClick={() => setDraft(c.rendered || c.content)}
                          className="block w-full px-3 py-1.5 text-left transition-colors hover:bg-fill"
                        >
                          <span className="font-mono text-[11.5px] font-bold text-text-high">/{c.short_code}</span>
                          <span className="ml-2 text-[12px] text-text-muted">
                            {(c.rendered || c.content).length > 80 ? `${(c.rendered || c.content).slice(0, 80)}…` : c.rendered || c.content}
                          </span>
                        </button>
                      ))}
                    </div>
                  )}
                  {/* contact-bound quick replies — one tap drafts a personalized message */}
                  {!draft && !pendingFile && canned.length > 0 && !windowClosed && !noteMode && (
                    <div className="no-scrollbar mb-2 flex gap-1.5 overflow-x-auto">
                      {canned.slice(0, 8).map((c) => (
                        <button
                          key={c.id}
                          onClick={() => setDraft(c.rendered || c.content)}
                          title={c.rendered || c.content}
                          className="shrink-0 rounded-full border border-line-strong bg-card px-2.5 py-1 text-[11.5px] text-text-mid transition-colors hover:border-text-primary hover:text-text-high"
                        >
                          {c.short_code.replace(/-/g, ' ')}
                        </button>
                      ))}
                    </div>
                  )}
                  {active.channel === 'whatsapp' && pendingFile && (
                    <div className="mb-2 flex items-center gap-2 rounded-lg border border-line bg-card px-3 py-1.5 text-[12.5px] text-text-mid">
                      <Paperclip size={13} className="shrink-0 text-text-muted" aria-hidden />
                      <span className="min-w-0 flex-1 truncate">
                        {pendingFile.name} · {(pendingFile.size / 1024 / 1024).toFixed(1)} MB
                      </span>
                      <button
                        onClick={() => setPendingFile(null)}
                        title="Remove attachment"
                        aria-label="Remove attachment"
                        className="grid h-6 w-6 shrink-0 place-items-center rounded-md text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
                      >
                        <X size={12} aria-hidden />
                      </button>
                    </div>
                  )}
                  <div
                    className={`flex items-center gap-1.5 rounded-xl border p-1.5 pl-2 ${
                      noteMode ? 'border-dashed border-amber/50 bg-amber/5' : 'border-line-strong bg-card'
                    }`}
                  >
                    {active.channel === 'whatsapp' && !noteMode && (
                      <>
                        <button
                          onClick={() => setTplOpen(true)}
                          disabled={busy}
                          title="Send a message template"
                          aria-label="Send a message template"
                          className={`grid h-8 w-8 shrink-0 place-items-center rounded-lg transition-colors disabled:opacity-35 ${
                            windowClosed ? 'bg-primary text-on-primary hover:opacity-90' : 'text-text-muted hover:bg-fill-hover hover:text-text-high'
                          }`}
                        >
                          <LayoutTemplate size={15} aria-hidden />
                        </button>
                        <button
                          onClick={() => fileRef.current?.click()}
                          disabled={busy || windowClosed}
                          title={windowClosed ? 'Window closed — media needs an open 24h window' : 'Attach a file'}
                          aria-label="Attach a file"
                          className="grid h-8 w-8 shrink-0 place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high disabled:opacity-35"
                        >
                          <Paperclip size={15} aria-hidden />
                        </button>
                        <input
                          ref={fileRef}
                          type="file"
                          accept=".jpg,.jpeg,.png,.webp,.pdf,.mp4,.ogg,.mp3"
                          onChange={onPickFile}
                          className="hidden"
                          aria-hidden
                          tabIndex={-1}
                        />
                      </>
                    )}
                    <input
                      value={draft}
                      onChange={(e) => setDraft(e.target.value)}
                      onKeyDown={(e) => e.key === 'Enter' && !e.shiftKey && sendMessage()}
                      disabled={windowClosed && !noteMode}
                      placeholder={
                        noteMode
                          ? 'Write a private note — agents only, never sent'
                          : windowClosed
                            ? '24h window closed — send a template to re-open'
                            : pendingFile
                              ? 'Add a caption (optional)'
                              : active.channel === 'email'
                                ? 'Write an email…'
                                : 'Type a message (/ for templates)'
                      }
                      aria-label="Message text"
                      className="min-w-0 flex-1 bg-transparent pl-1 text-[13.5px] text-text-primary outline-none placeholder:text-text-faint disabled:cursor-not-allowed disabled:opacity-50"
                    />
                    <button
                      onClick={sendMessage}
                      disabled={busy || (windowClosed && !noteMode) || (!draft.trim() && !pendingFile)}
                      title={noteMode ? 'Save private note' : 'Send'}
                      aria-label={noteMode ? 'Save private note' : 'Send message'}
                      className={`grid h-9 w-9 shrink-0 place-items-center rounded-lg transition-opacity hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-35 ${
                        noteMode ? 'bg-amber text-[color:var(--on-accent)]' : 'bg-primary text-on-primary'
                      }`}
                    >
                      {noteMode ? <StickyNote size={14} aria-hidden /> : <Send size={14} aria-hidden />}
                    </button>
                  </div>
                  {windowClosed && !noteMode && (
                    <div className="mt-1.5 flex items-center gap-1.5 text-[11.5px] text-amber-300">
                      <Clock size={12} className="shrink-0" aria-hidden />
                      24h window closed — send a template to re-open the conversation.
                    </div>
                  )}
                </div>
                </>
              )}
            </>
          )}
        </section>

        {active && infoOpen && (
          <ChatInfoPanel
            active={active}
            busy={busy}
            sandbox={activeSandbox}
            imapConfigured={!!status?.imap_configured}
            linkContacts={linkContacts}
            linkSel={linkSel}
            onLinkSel={setLinkSel}
            onLink={linkContact}
            waAccounts={waAccounts}
            canManage={canManage}
            onSwitchAccount={switchAccount}
            teams={teams}
            onAssignTeam={assignTeam}
            agents={agents}
            onAssign={assignTo}
            onSetStatus={setThreadStatus}
            onSaveLabels={saveLabels}
          />
        )}
      </div>

      <ChatFilterModal
        open={filterOpen}
        onClose={() => setFilterOpen(false)}
        initial={filters}
        q={q}
        topEntity={topEntity}
        agents={agents}
        teams={teams}
        accounts={allAccounts}
        contactTags={contactTags}
        onApply={(f) => setFilters(f)}
        onSaveView={saveView}
      />

      <SavedViewsManager
        open={manageOpen}
        onClose={() => setManageOpen(false)}
        views={views}
        onRename={async (id, name) => {
          await updateView(id, { name })
          await reloadViews()
        }}
        onDelete={async (id) => {
          await deleteView(id)
          if (activeView === `v:${id}`) applyFilters(EMPTY_FILTERS, 'all')
          await reloadViews()
        }}
        onReorder={async (ids) => {
          await reorderViews('chats', ids)
          await reloadViews()
        }}
      />

      {tplOpen && active && active.channel === 'whatsapp' && (
        <TemplatePickerModal
          thread={active}
          onClose={() => setTplOpen(false)}
          onSent={(m) => {
            appendToActive(m)
            refreshActive()
            refreshThreads()
          }}
        />
      )}
    </div>
  )
}

/** useSearchParams needs a Suspense boundary under the app router. */
export default function ChatsRoute() {
  return (
    <Suspense fallback={null}>
      <ChatsPage />
    </Suspense>
  )
}
