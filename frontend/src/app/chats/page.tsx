'use client'

import { Suspense, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type ChangeEvent } from 'react'
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
import ChatListPane, { rowRef, type ViewTab } from '@/components/chats/ChatListPane'
import BulkBar from '@/components/chats/BulkBar'
import HandoverBanner from '@/components/chats/HandoverBanner'
import { useLive } from '@/components/shell/liveStore'
import { useAccess } from '@/lib/useAccess'
import { useEntityQuery } from '@/lib/EntityContext'
import type { UiHandover } from '@/lib/aiControls'
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
  channelName,
  dayLabel,
  statusLabel,
  logoKey,
  rowToSummary,
  threadPath,
  timeAgo,
  timeShort,
  WINDOWED_CHANNELS,
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
import IdentityBanner from '@/components/chats/IdentityBanner'
import { subscribeChatStream } from '@/lib/chatStream'
import { AiBubbleFeedback, useAiFeedback } from '@/components/ai-learning/BubbleFeedback'
import { useFormat, useT, type MessageKey } from '@/i18n'
// quoted replies, reactions, the emoji picker and message cards (bubble extras)
import MessageActions from '@/components/chats/MessageActions'
import MessageContent, { isSticker } from '@/components/chats/MessageContent'
import { ReplyPreview, jumpToMessage } from '@/components/chats/QuotedBlock'
import { EmojiButton, insertAtCursor } from '@/components/chats/EmojiPicker'
import { REACTION_CHANNELS } from '@/components/chats/inboxTypes'
import './chats.css'

/** First page of the list; "Load more" pages by the same step. */
const PAGE = 50

/** Channels whose thread has a send-media route (the composer's paperclip). */
const MEDIA_CHANNELS: Channel[] = ['whatsapp', 'facebook', 'instagram', 'telegram', 'email', 'wechat', 'zalo']

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

/** Built-in tabs ahead of the operator's own saved views (names are translation keys). */
const BUILTIN_VIEWS: { id: string; name: MessageKey; filters: InboxFilters }[] = [
  { id: 'all', name: 'common.status.all', filters: EMPTY_FILTERS },
  { id: 'mine', name: 'chats.views.mine', filters: { ...EMPTY_FILTERS, assignees: ['me'] } },
  { id: 'unassigned', name: 'chats.views.unassigned', filters: { ...EMPTY_FILTERS, assignees: ['unassigned'] } },
  // conversations the AI (or a rule, a flow, a person) handed to a human, not yet answered
  { id: 'needs_human', name: 'chats.views.needsHuman', filters: { ...EMPTY_FILTERS, handover: 'open' } },
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
  const t = useT()
  const f = useFormat()
  const { contacts, calls, hangupCall, sendSay: sendSipSay, escalateCall: escalateSipCall, setHumanControl } = useApp()
  const { selectedEntities, entities } = useEntity()
  // the top bar's ticked entities ([] = all); the filter's own entity pick narrows within them
  const topEntities = useMemo(() => selectedEntities.map((e) => e.slug), [selectedEntities])
  const topEntity = topEntities.join(',') || null
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
  // ── multi-select (bulk bar) + the "Needs human" count ──
  const access = useAccess()
  const canBulk = access.ready && access.can('chats.manage')
  const canSendChats = access.ready && access.can('chats.send')
  const [selectMode, setSelectMode] = useState(false)
  const [selectedRefs, setSelectedRefs] = useState<Set<string>>(new Set())
  const [allMatching, setAllMatching] = useState(false)
  const { data: liveData } = useLive(useEntityQuery())
  const needsHumanCount = liveData?.needs_human_count ?? null

  // ── conversation state ──
  const [active, setActive] = useState<UiThread | null>(null)
  const [activeId, setActiveId] = useState<{ channel: Channel; key: string } | null>(null)
  const [draft, setDraft] = useState('')
  // the message the next reply quotes (WhatsApp / Telegram), cleared on send and on switching chats
  const [replyTo, setReplyTo] = useState<UiMessage | null>(null)
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
  // below xl the details panel is a drawer (assign, link a contact, labels… on phones too)
  const [infoDrawer, setInfoDrawer] = useState(false)
  useEffect(() => {
    if (!infoDrawer) return
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && setInfoDrawer(false)
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  }, [infoDrawer])
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
  const composerRef = useRef<HTMLTextAreaElement>(null)
  // the message box grows with its text (up to its max height), and shrinks back after sending
  useLayoutEffect(() => {
    const el = composerRef.current
    if (!el) return
    el.style.height = 'auto'
    el.style.height = `${Math.min(el.scrollHeight, 160)}px`
  }, [draft])
  const prevCallStatus = useRef<string | undefined>(undefined)

  const waAccounts = useMemo(() => allAccounts.filter((a) => (a.channel ?? 'whatsapp') === 'whatsapp'), [allAccounts])

  // ── search debounce ──
  useEffect(() => {
    const timer = setTimeout(() => setQ(search), 280)
    return () => clearTimeout(timer)
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
  // ?draft=<text> (a contact invite's "Send it in a chat") prefills the composer
  const spDraft = searchParams.get('draft')
  useEffect(() => {
    if (spDraft) setDraft(spDraft)
  }, [spDraft])

  // ── the list request ──
  const listParams = useMemo(() => filtersToParams(filters, { q, entities: topEntities }).toString(), [filters, q, topEntities])

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

  // a selection belongs to the list it was made in
  useEffect(() => {
    setSelectedRefs(new Set())
    setAllMatching(false)
  }, [listParams])

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
        const seen = new Set(prev.map((row) => `${row.channel}:${row.key}`))
        const merged = [...prev, ...d.threads.map(rowToSummary).filter((row) => !seen.has(`${row.channel}:${row.key}`))]
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
        const th = await api<{
          wa_id: string
          phone: string
          name: string
          ai_enabled: boolean
          ai_reason?: string
          ai_forced?: boolean
          handover?: UiHandover | null
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
          key: th.wa_id,
          name: th.contact_name || th.name,
          // a WhatsApp id is bare digits — show it as the phone number it is
          sub: /^\d+$/.test(th.phone) ? `+${th.phone}` : th.phone,
          ai_enabled: th.ai_enabled,
          ai_reason: th.ai_reason,
          ai_forced: th.ai_forced,
          handover: th.handover ?? null,
          status: th.status ?? 'open',
          assignee: th.assignee,
          team_id: th.team_id ?? null,
          call: th.call,
          account_id: th.account_id,
          provider: th.provider,
          window_open: th.window_open,
          window_expires_at: th.window_expires_at,
          opted_out: th.opted_out,
          contact_id: th.contact_id,
          contact_name: th.contact_name,
          labels: th.labels,
          messages: th.messages,
        })
      } else {
        const th = await api<{
          key: string
          channel: Channel
          address: string
          name: string
          ai_enabled: boolean
          ai_reason?: string
          ai_forced?: boolean
          handover?: UiHandover | null
          status: ThreadStatus
          assignee: string
          team_id?: number | null
          labels: string[]
          contact_id?: number | null
          contact_name?: string
          // Messenger / Instagram: Meta's messaging window (always open elsewhere)
          window_open?: boolean
          window_expires_at?: string | null
          human_window_open?: boolean
          human_window_expires_at?: string | null
          messages: UiMessage[]
        }>(`messaging/threads/${activeId.channel}/${activeId.key}`)
        setActive({
          channel: th.channel,
          key: th.key,
          name: th.contact_name || th.name,
          sub: th.address,
          ai_enabled: th.ai_enabled,
          ai_reason: th.ai_reason,
          ai_forced: th.ai_forced,
          handover: th.handover ?? null,
          status: th.status,
          assignee: th.assignee,
          team_id: th.team_id ?? null,
          labels: th.labels,
          contact_id: th.contact_id,
          contact_name: th.contact_name,
          window_open: th.window_open,
          window_expires_at: th.window_expires_at,
          human_window_open: th.human_window_open,
          human_window_expires_at: th.human_window_expires_at,
          messages: th.messages,
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
          // the newer channels' `<channel>_configured` flags ride along untyped
          ...(msg as Record<string, boolean>),
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
    const timer = setInterval(sync, 60000)
    return () => clearInterval(timer)
  }, [])

  // Switching the entity scope (top bar, or the filter's entity pick) closes a
  // conversation the new scope no longer contains — asked of the server by id,
  // so a chat that is merely below the loaded page is not closed by mistake.
  const scopeKey = JSON.stringify(filters.entities ?? topEntities)
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
    const timer = setInterval(load, 2500)
    return () => {
      cancelled = true
      clearInterval(timer)
    }
  }, [activeContactId])

  useEffect(() => {
    scrollRef.current?.scrollTo({ top: scrollRef.current.scrollHeight })
  }, [active?.messages.length, combinedOn, combined?.messages.length])

  // a quote belongs to the chat it was picked in
  useEffect(() => setReplyTo(null), [activeId])

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
      const timer = setTimeout(() => setEndedFlash(false), 5000)
      return () => clearTimeout(timer)
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
    const timer = setInterval(poll, 1500)
    return () => {
      stopped = true
      clearInterval(timer)
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
    const timer = setInterval(load, 8000)
    return () => {
      cancelled = true
      clearInterval(timer)
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
      if (pendingFile && MEDIA_CHANNELS.includes(active.channel)) {
        const form = new FormData()
        form.append('file', pendingFile)
        if (text) form.append('caption', text)
        // whatsapp/threads/{key}/send-media · messaging/threads/{channel}/{key}/send-media
        const r = await apiUpload<{ message: UiMessage }>(`${threadPath(active)}/send-media`, form)
        setPendingFile(null)
        setDraft('')
        appendToActive(r.message)
        return
      }
      if (!text) return
      const override = isWa && active.opted_out === true
      if (override && !window.confirm(t('chats.page.confirmOptedOut'))) return
      setDraft('')
      // a quoted reply carries the quoted message's pk (WhatsApp / Telegram)
      const quote = replyTo?.pk != null && REACTION_CHANNELS.includes(active.channel) ? { reply_to: replyTo.pk } : {}
      setReplyTo(null)
      await api(`${threadPath(active)}/send`, {
        method: 'POST',
        body: JSON.stringify(override ? { text, force: true, ...quote } : { text, ...quote }),
      })
    })

  /** Our emoji reaction on a message ("" removes it) — every channel goes through the messaging route. */
  const reactTo = (m: UiMessage, emoji: string) =>
    run(async () => {
      if (!active || m.pk == null) return
      await api(`messaging/threads/${active.channel}/${encodeURIComponent(active.key)}/messages/${m.pk}/react`, {
        method: 'POST',
        body: JSON.stringify({ emoji }),
      })
    })

  const onPickFile = (e: ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0]
    e.target.value = '' // allow re-picking the same file
    if (!file) return
    const ext = file.name.toLowerCase().split('.').pop() || ''
    if (!['jpg', 'jpeg', 'png', 'webp', 'pdf', 'mp4', 'ogg', 'mp3'].includes(ext)) {
      setError(t('chats.page.unsupportedFile'))
      return
    }
    if (file.size > 16 * 1024 * 1024) {
      setError(t('chats.page.fileTooLarge'))
      return
    }
    setError(null)
    setPendingFile(file)
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
  const applyFilters = (next: InboxFilters, viewId?: string) => {
    setFilters(next)
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
    ...BUILTIN_VIEWS.map((b) => ({
      id: b.id,
      name: t(b.name),
      active: sameFilters(filters, b.filters),
      count: b.id === 'needs_human' ? needsHumanCount : undefined,
    })),
    ...views.map((v) => {
      const id = `v:${v.id}`
      const on = activeView === id
      return { id, name: v.name, active: on, modified: on && !sameFilters(filters, normalizeFilters(v.filters)) }
    }),
  ]
  // a built-in tab wins the highlight only when nothing saved is selected;
  // with nothing highlighted, a saved view whose filters are on screen lights up
  if (tabs.some((tab) => tab.id.startsWith('v:') && tab.active))
    tabs.forEach((tab) => !tab.id.startsWith('v:') && (tab.active = false))
  else if (!tabs.some((tab) => tab.active)) {
    const match = views.find((v) => sameFilters(filters, normalizeFilters(v.filters)))
    const tab = match && tabs.find((x) => x.id === `v:${match.id}`)
    if (tab) tab.active = true
  }

  const saveView = async (name: string, next: InboxFilters) => {
    const v = await createView<InboxFilters>('chats', name, next)
    await reloadViews()
    applyFilters(next, `v:${v.id}`)
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
  const chips = activeChips(
    filters,
    {
      accountName: (id) => allAccounts.find((a) => a.id === id)?.name || `#${id}`,
      agentName: (u) => agents.find((a) => a.username === u)?.name || u,
      teamName: (id) => teams.find((tm) => String(tm.id) === id)?.name || `#${id}`,
      entityName: (slug) => entities.find((e) => e.slug === slug)?.name || slug,
      labelName: (slug) => tagMeta(slug).label,
    },
    t,
  )
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
          label: t(
            callStatus === 'in_call'
              ? 'chats.page.onCall'
              : callStatus === 'ringing'
                ? 'chats.page.ringing'
                : callStatus === 'connecting'
                  ? 'chats.page.connecting'
                  : 'chats.page.calling',
          ),
          pulse: callStatus !== 'in_call',
          cls: 'bg-green/15 text-green-300',
        }
      : endedFlash
        ? { label: t('chats.page.callEnded'), pulse: false, cls: 'bg-fill text-text-muted' }
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
                : // LINE, Viber, SMS, WeChat, TikTok, Douyin, Zalo: `<channel>_configured`
                  ((status as unknown as Record<string, boolean | undefined>)[`${c}_configured`] ?? true)

  const activeSandbox = active ? !channelLive(active.channel) : false
  // 24h customer-service window (live WhatsApp accounts only — sandbox is always open)
  const windowClosed = activeIsWa && !activeSandbox && active?.window_open === false
  // Messenger / Instagram (live pages): anything inside 24h, then only a person
  // for 7 days (sent with Meta's HUMAN_AGENT tag), then nothing until they write
  const activeIsSocial = active?.channel === 'facebook' || active?.channel === 'instagram'
  const socialClosed = activeIsSocial && !activeSandbox && active?.human_window_open === false
  const socialLate = activeIsSocial && !activeSandbox && !socialClosed && active?.window_open === false
  // WeChat 48 h, Zalo 7 days, TikTok / Douyin 48 h on a live account (ChannelWindow server-side)
  const platformClosed =
    !!active && WINDOWED_CHANNELS.includes(active.channel) && !activeSandbox && active.window_open === false
  const composerLocked = windowClosed || socialClosed || platformClosed
  const canAttach = !!active && MEDIA_CHANNELS.includes(active.channel)

  const activeFilterCount = countActive(chips)
  const unfiltered = activeFilterCount === 0 && !q.trim()

  // Every contact appears in the inbox: real threads first, then a synthetic
  // "no conversation yet" row for each contact not yet covered by a thread —
  // only in the unfiltered list (and once it is fully loaded), since a filter
  // describes conversations and these are not conversations yet.
  const pendingRows: UiThreadSummary[] = useMemo(() => {
    if (!unfiltered || nextCursor || listLoading) return []
    const covered = new Set(threads.map((row) => row.contact_id).filter(Boolean) as number[])
    const waKeys = new Set(threads.filter((row) => row.channel === 'whatsapp').map((row) => row.key))
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
        last_message: '', // rendered as "No conversation yet" by the list
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
    for (const row of all) {
      if (row.contact_id != null && !row.pending) {
        const g = groups.get(row.contact_id)
        if (g) g.push(row)
        else {
          groups.set(row.contact_id, [row])
          order.push(row.contact_id)
        }
      } else order.push(row)
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
        handover: rows.find((r) => r.handover?.open)?.handover ?? rows[0].handover,
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
  const aiFeedback = useAiFeedback(displayMessages)
  const grouped: { day: string; items: TimelineItem[] }[] = []
  for (const m of displayMessages) {
    const day = dayLabel(m.ts, f, t)
    const last = grouped[grouped.length - 1]
    if (last && last.day === day) last.items.push(m)
    else grouped.push({ day, items: [m] })
  }

  // Context switches the customer can't see from our side: a sender-account
  // change within a thread, and (merged view) moving to the contact's OTHER
  // number/channel. Both get a divider at the exact switch point.
  const msgAccountIds = new Set(displayMessages.filter((m) => m.account_id).map((m) => m.account_id))
  const multiAccount = msgAccountIds.size > 1 || waAccounts.length > 1
  const acctLabel = (m: UiMessage) => m.account_name || t('chats.page.accountN', { id: m.account_id ?? '' })
  const switchNotices = new Map<string, string>()
  {
    let prevContact: string | null = null
    let prevAcct: string | null = null
    for (const m of displayMessages) {
      const contact = combinedOn && m.channel && m.key ? `${m.channel}:${m.key}` : null
      if (contact !== null) {
        if (prevContact !== null && contact !== prevContact) {
          const addr = m.channel === 'whatsapp' ? `+${m.key}` : m.key
          switchNotices.set(m.id, t('chats.page.switchOtherContact', { channel: channelName(t, m.channel!), address: addr ?? '' }))
        }
        prevContact = contact
      }
      if (m.account_id) {
        if (prevAcct !== null && m.account_id !== prevAcct && !switchNotices.has(m.id)) {
          const provider = waAccounts.find((a) => a.id === m.account_id)?.provider
          const seenBefore = displayMessages.some((x) => x !== m && x.account_id === m.account_id && x.ts < m.ts)
          switchNotices.set(
            m.id,
            t(
              provider === 'baileys'
                ? seenBefore
                  ? 'chats.page.switchBaileysSeen'
                  : 'chats.page.switchBaileysCold'
                : seenBefore
                  ? 'chats.page.switchSeen'
                  : 'chats.page.switchCold',
              { account: acctLabel(m) },
            ),
          )
        }
        prevAcct = m.account_id
      }
    }
  }

  // Fleet strip → jump to a live call's contact thread.
  const openContactThread = (contactId: number) => {
    const row = threads.find((x) => x.contact_id === contactId)
    if (row) setActiveId({ channel: row.channel, key: row.key })
  }

  // Open an inbox row. A pending contact row has no thread yet — create it on
  // demand (idempotent get-or-create by phone), then open it.
  const openRow = (row: UiThreadSummary) => {
    setError(null)
    if ((row._mergedChannels?.length ?? 0) > 1) setCombinedOn(true)
    if (!row.pending) {
      setActiveId({ channel: row.channel, key: row.key })
      return
    }
    run(async () => {
      const r = await api<{ wa_id: string }>('whatsapp/threads', { method: 'POST', body: JSON.stringify({ phone: row.sub }) })
      setActiveId({ channel: 'whatsapp', key: r.wa_id })
    })
  }

  const noteMode = composerMode === 'note'

  // multi-select: a merged row (one contact, several channels) stands for all of its conversations
  const toggleSelect = (row: UiThreadSummary) => {
    const refs =
      (row._mergedChannels?.length ?? 0) > 1 && row.contact_id != null
        ? threads.filter((x) => x.contact_id === row.contact_id).map(rowRef)
        : [rowRef(row)]
    setAllMatching(false)
    setSelectedRefs((prev) => {
      const next = new Set(prev)
      const on = refs.every((r) => next.has(r))
      refs.forEach((r) => (on ? next.delete(r) : next.add(r)))
      return next
    })
  }
  const exitSelect = () => {
    setSelectMode(false)
    setSelectedRefs(new Set())
    setAllMatching(false)
  }
  const [meName, setMeName] = useState<string | null>(null)
  useEffect(() => setMeName(access.user?.username ?? null), [access.user])

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
          onRemoveChip={(c) => setFilters((cur) => c.clear(cur))}
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
          selectMode={selectMode}
          selected={selectedRefs}
          onToggleSelect={toggleSelect}
          onToggleSelectMode={canBulk ? () => (selectMode ? exitSelect() : setSelectMode(true)) : undefined}
          footer={
            selectMode && (selectedRefs.size > 0 || allMatching) ? (
              <BulkBar
                selected={Array.from(selectedRefs)}
                total={total}
                allMatching={allMatching}
                onAllMatching={setAllMatching}
                listParams={listParams}
                agents={agents}
                onClear={() => {
                  setSelectedRefs(new Set())
                  setAllMatching(false)
                }}
                onDone={() => {
                  refreshThreads()
                  refreshActive()
                }}
              />
            ) : null
          }
        />

        {/* ── conversation (paper) ── */}
        <section
          aria-label={t('chats.page.conversationAria')}
          className={`cb-paper min-w-0 flex-1 flex-col lg:min-w-[380px] ${active ? 'flex' : 'hidden lg:flex'}`}
        >
          {!active ? (
            <div className="grid flex-1 place-content-center px-6 text-center">
              <div className="display text-[26px] text-text-heading">{t('chats.page.pickConversation')}</div>
              <div className="mx-auto mt-2 max-w-[340px] text-[13px] leading-relaxed text-text-muted">
                {t('chats.page.pickHint')}
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
                  aria-label={t('chats.page.backAria')}
                  title={t('common.actions.back')}
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
                        title={t('chats.page.optedOutHint')}
                      >
                        <Ban size={10} aria-hidden /> {t('chats.list.optedOut')}
                      </span>
                    )}
                    {active.status && active.status !== 'open' && (
                      <span className="shrink-0 rounded-full border border-line-strong px-2 py-px text-[10.5px] text-text-mid">
                        {statusLabel(t, active.status)}
                      </span>
                    )}
                  </div>
                  <div className="mt-0.5 flex min-w-0 items-center gap-1.5 text-[12.5px] text-text-muted">
                    <ChannelLogo channel={logoKey(active.channel)} size={13} />
                    <span className="truncate">
                      {active.sub}
                      {active.channel === 'whatsapp' && active.account_id && waAccounts.length > 0 && (
                        <>
                          {' · '}
                          {t('chats.page.via', {
                            name: waAccounts.find((a) => a.id === String(active.account_id))?.name ?? `#${active.account_id}`,
                          })}
                        </>
                      )}
                    </span>
                    {CHANNEL_META[active.channel].beta && <BetaChip />}
                  </div>
                </div>
                <div className="flex shrink-0 flex-wrap items-center gap-1.5">
                  {/* phones & tablets: the same details as a drawer */}
                  <button
                    onClick={() => setInfoDrawer(true)}
                    title={t('chats.page.details')}
                    aria-label={t('chats.page.openDetails')}
                    className="inline-flex h-8 items-center gap-1 rounded-lg border border-line px-2 text-[11.5px] font-semibold text-text-high transition-colors hover:bg-fill-hover xl:hidden"
                  >
                    <Info size={14} aria-hidden /> <span className="hidden sm:inline">{t('chats.page.details')}</span>
                  </button>
                  {/* AI auto-reply on/off (email has no auto-reply) */}
                  {active.channel !== 'email' && (
                    <button
                      data-tour="chats-ai"
                      onClick={toggleAi}
                      disabled={busy || optedOut || !!active.ai_forced}
                      role="switch"
                      aria-checked={!!active.ai_enabled && !optedOut}
                      // the server's reason: "Off: AI is switched off for North Branch", "On: switched on for this chat"…
                      title={
                        active.ai_reason ||
                        t(optedOut ? 'chats.page.aiOptedOut' : active.ai_enabled ? 'chats.page.turnAiOff' : 'chats.page.turnAiOn')
                      }
                      aria-label={
                        active.ai_reason
                          ? t('chats.page.toggleAiAriaReason', { reason: active.ai_reason })
                          : t('chats.page.toggleAiAria')
                      }
                      className={`flex h-8 shrink-0 items-center gap-1.5 rounded-full border px-2.5 text-[11px] font-semibold transition-colors disabled:cursor-not-allowed disabled:opacity-50 ${
                        active.ai_enabled && !optedOut
                          ? 'border-line-strong bg-[var(--nav-active-bg)] text-[color:var(--nav-active-text)]'
                          : 'border-line bg-card text-text-muted'
                      }`}
                    >
                      {t('chats.page.aiLabel')}
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
                  {/* the AI only answers chats linked to a contact — say so instead of looking broken */}
                  {active.channel !== 'email' && active.ai_enabled && !optedOut && !active.contact_id && (
                    <span
                      title={t('chats.page.aiNeedsContactHint')}
                      className="inline-flex h-8 shrink-0 items-center rounded-full border border-line px-2.5 text-[11px] font-semibold text-[var(--amber-ink)]"
                    >
                      {t('chats.page.aiNeedsContact')}
                    </span>
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
                    <Button
                      variant="secondary"
                      size="sm"
                      onClick={() => setThreadStatus('resolved')}
                      disabled={busy}
                      title={t('chats.page.resolveTitle')}
                    >
                      <CheckCircle2 size={13} aria-hidden /> {t('chats.page.resolve')}
                    </Button>
                  ) : (
                    <Button
                      variant="secondary"
                      size="sm"
                      onClick={() => setThreadStatus('open')}
                      disabled={busy}
                      title={t('chats.page.reopenTitle')}
                    >
                      <RotateCcw size={13} aria-hidden /> {t('chats.page.reopen')}
                    </Button>
                  )}
                  {active.channel === 'whatsapp' &&
                    (inCall ? (
                      <button
                        onClick={endCall}
                        disabled={busy}
                        title={t('chats.page.endWaCall')}
                        aria-label={t('chats.page.endWaCall')}
                        className="grid h-8 w-8 place-items-center rounded-lg bg-red/10 text-red-300 transition-colors hover:bg-red/20"
                      >
                        <PhoneOff size={15} aria-hidden />
                      </button>
                    ) : starting ? (
                      <span title={t('chats.page.calling')} aria-label={t('chats.page.calling')} className="grid h-8 w-8 place-items-center rounded-lg text-text-muted">
                        <Loader2 size={15} className="animate-spin" aria-hidden />
                      </span>
                    ) : (
                      <button
                        onClick={startCall}
                        disabled={busy}
                        title={t('chats.page.startWaCall')}
                        aria-label={t('chats.page.startWaCall')}
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
                    title={t('chats.page.details')}
                    aria-label={t('chats.page.toggleDetails')}
                    aria-pressed={infoOpen}
                    className={`hidden h-8 w-8 place-items-center rounded-lg transition-colors xl:grid ${
                      infoOpen ? 'bg-fill-hover text-text-high' : 'text-text-muted hover:bg-fill-hover'
                    }`}
                  >
                    <Info size={15} aria-hidden />
                  </button>
                </div>
              </div>

              {/* unknown customer: "asking who they are" + the Create contact quick action */}
              {active.contact_id == null && (
                <IdentityBanner
                  channel={active.channel}
                  threadKey={active.key}
                  refreshToken={active.messages.length}
                  onCreated={() => void Promise.all([refreshActive(), refreshThreads()])}
                />
              )}

              {/* linked contact: single channel ⇄ combined view ⇄ voice-only history */}
              {active.contact_id != null && combined && (
                <div className="no-scrollbar flex shrink-0 items-center gap-1.5 overflow-x-auto border-b border-line px-4 py-2 sm:px-5">
                  <span className="eyebrow shrink-0 pr-1">{t('chats.page.contactOn', { name: combined.contact.name })}</span>
                  <button
                    onClick={() => {
                      setCombinedOn(true)
                      setVoiceOnly(false)
                    }}
                    className={`cb-switch ${combinedOn && !voiceOnly ? 'is-on' : ''}`}
                  >
                    <Layers size={11} aria-hidden /> {t('chats.page.allChannels')}
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
                        {channelName(t, c.channel)}
                      </button>
                    )
                  })}
                  <button
                    onClick={() => {
                      setCombinedOn(true)
                      setVoiceOnly(true)
                    }}
                    title={t('chats.page.voiceHint')}
                    className={`cb-switch ${voiceOnly ? 'is-on' : ''}`}
                  >
                    <PhoneCall size={11} aria-hidden /> {t('chats.page.voice')}
                  </button>
                </div>
              )}

              {/* always-on AI summary — pinned above the conversation */}
              {summaryState && summaryState.message_count > 0 && (
                <div className="shrink-0 border-b border-line bg-surface px-4 py-2.5 sm:px-5">
                  <div className="flex items-center gap-1.5">
                    <Sparkles size={12} className="shrink-0 text-brand-300" aria-hidden />
                    <span className="eyebrow">{t('chats.page.summary')}</span>
                    {summaryState.summary?.heuristic && (
                      <span
                        className="rounded border border-line px-1.5 py-px text-[9px] font-bold uppercase text-text-muted"
                        title={t('chats.page.basicHint')}
                      >
                        {t('chats.page.basic')}
                      </span>
                    )}
                    {summarizing && (
                      <span className="flex items-center gap-1 text-[11px] text-text-muted">
                        <Loader2 size={10} className="animate-spin" aria-hidden /> {t('chats.page.updating')}
                      </span>
                    )}
                    <div className="flex-1" />
                    {summaryState.summary && !summarizing && (
                      <span className="min-w-0 truncate text-[11px] text-text-muted">
                        {summaryState.stale
                          ? t('chats.page.mayBeOutdated', { ago: timeAgo(summaryState.summary.generated_at, f) })
                          : timeAgo(summaryState.summary.generated_at, f)}
                      </span>
                    )}
                    <button
                      onClick={regenSummary}
                      disabled={summarizing}
                      title={t('chats.page.refreshSummary')}
                      aria-label={t('chats.page.refreshSummary')}
                      className="grid h-6 w-6 place-items-center rounded text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high disabled:opacity-40"
                    >
                      <RefreshCw size={11} className={summarizing ? 'animate-spin' : ''} aria-hidden />
                    </button>
                    <button
                      onClick={() => setSummaryOpen(!summaryOpen)}
                      title={t(summaryOpen ? 'chats.page.collapseSummary' : 'chats.page.expandSummary')}
                      aria-label={t(summaryOpen ? 'chats.page.collapseSummary' : 'chats.page.expandSummary')}
                      aria-expanded={summaryOpen}
                      className="grid h-6 w-6 place-items-center rounded text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
                    >
                      <ChevronLeft size={11} className={`transition-transform ${summaryOpen ? '-rotate-90' : 'rotate-180'}`} aria-hidden />
                    </button>
                  </div>
                  {summaryOpen && (
                    <p className="mt-1 text-[13.5px] leading-[1.6] text-text-high">
                      {summaryState.summary?.summary ?? t(summarizing ? 'chats.page.generatingFirst' : 'chats.page.noSummary')}
                    </p>
                  )}
                </div>
              )}

              {/* live WhatsApp call monitor */}
              {inCall && (
                <div className="shrink-0 space-y-1.5 border-b border-line bg-well p-3">
                  <div className="flex items-center justify-between">
                    <span className="eyebrow">{t('chats.call.liveTranscript')}</span>
                    {callLive.done && <span className="text-[11px] text-text-muted">{t('chats.page.agentFinished')}</span>}
                  </div>
                  <div ref={callScrollRef} className="max-h-36 space-y-1 overflow-y-auto pr-1 text-[12.5px] leading-snug">
                    {callLive.turns.length === 0 ? (
                      <div className="flex items-center gap-1.5 py-1 text-text-muted">
                        <Loader2 size={12} className="animate-spin" aria-hidden />
                        {t(active.call?.status === 'in_call' ? 'chats.call.waitingAgent' : 'chats.call.connectingCall')}
                      </div>
                    ) : (
                      callLive.turns.map((turn, i) => (
                        <div key={i} className={turn.role === 'assistant' ? 'text-brand-300' : 'text-text-mid'}>
                          <span className="font-semibold">
                            {t(turn.role === 'assistant' ? 'chats.parts.ai' : 'chats.parts.caller')}:
                          </span>{' '}
                          {turn.text}
                        </div>
                      ))
                    )}
                  </div>
                  <div className="flex flex-wrap gap-1.5">
                    <textarea
                      value={sayDraft}
                      onChange={(e) => setSayDraft(e.target.value)}
                      rows={1}
                      placeholder={t('chats.page.steerWa')}
                      className="min-w-[200px] flex-1 resize-none rounded-lg border border-line-strong bg-card px-2.5 py-1.5 text-[12.5px] text-text-primary placeholder:text-text-faint focus:border-text-primary focus:outline-none"
                    />
                    <Button variant="secondary" size="sm" onClick={() => sendSay('guide')} disabled={busy || !sayDraft.trim()}>
                      <Sparkles size={13} aria-hidden /> {t('chats.call.guide')}
                    </Button>
                    <Button variant="secondary" size="sm" onClick={() => sendSay('takeover')} disabled={busy || !sayDraft.trim()}>
                      <Send size={13} aria-hidden /> {t('chats.call.takeover')}
                    </Button>
                    <Button variant="secondary" size="sm" onClick={escalateCall} disabled={busy}>
                      <UserRound size={13} aria-hidden /> {t('chats.call.escalate')}
                    </Button>
                  </div>
                </div>
              )}

              {/* live SIP ("AI phone call") monitor */}
              {sipCallActive && sipCall && (
                <div className="shrink-0 space-y-1.5 border-b border-line bg-well p-3">
                  <div className="flex items-center justify-between">
                    <span className="eyebrow">{t('chats.page.liveTranscriptSip')}</span>
                    <span className="text-[11px] text-text-muted">
                      {sipCall.status === 'live'
                        ? t('chats.call.inCall')
                        : sipCall.status === 'dialing'
                          ? t('chats.call.dialing')
                          : sipCall.status === 'ringing'
                            ? t('chats.call.ringingPhone')
                            : sipCall.statusText}
                    </span>
                  </div>
                  <div className="max-h-36 space-y-1 overflow-y-auto pr-1 text-[12.5px] leading-snug">
                    {sipCall.turns.length === 0 && sipCall.extraLines.length === 0 ? (
                      <div className="flex items-center gap-1.5 py-1 text-text-muted">
                        <Loader2 size={12} className="animate-spin" aria-hidden />
                        {t(sipCall.status === 'live' ? 'chats.call.waitingAgent' : 'chats.call.connectingCall')}
                      </div>
                    ) : (
                      <>
                        {sipCall.turns.map((turn, i) => (
                          <div key={i} className={turn.who === 'ai' ? 'text-brand-300' : 'text-text-mid'}>
                            <span className="font-semibold">{t(turn.who === 'ai' ? 'chats.parts.ai' : 'chats.parts.caller')}:</span>{' '}
                            {turn.t}
                          </div>
                        ))}
                        {sipCall.extraLines.map((l, i) => (
                          <div key={'x' + i} className={l.human ? 'text-amber-300' : 'text-brand-300'}>
                            <span className="font-semibold">{t(l.human ? 'chats.parts.you' : 'chats.parts.ai')}:</span> {l.t}
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
                      placeholder={t(sipCall.humanControl ? 'chats.call.steerHuman' : 'chats.page.steerWa')}
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
                      <Sparkles size={13} aria-hidden /> {t('chats.call.guide')}
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
                      <Send size={13} aria-hidden /> {t('chats.call.takeover')}
                    </Button>
                    <Button variant="secondary" size="sm" onClick={() => setHumanControl(sipCall.room, !sipCall.humanControl)}>
                      <UserRound size={13} aria-hidden /> {t(sipCall.humanControl ? 'chats.call.returnAi' : 'chats.call.takeOver')}
                    </Button>
                    <Button variant="secondary" size="sm" onClick={() => escalateSipCall(sipCall.room)}>
                      <UserRound size={13} aria-hidden /> {t('chats.call.escalate')}
                    </Button>
                    <Button variant="danger" size="sm" onClick={() => hangupCall(sipCall.room)}>
                      <PhoneOff size={13} aria-hidden /> {t('chats.page.end')}
                    </Button>
                  </div>
                </div>
              )}

              {/* messages with day separators */}
              <div ref={scrollRef} className="min-h-0 flex-1 overflow-y-auto px-4 py-4 sm:px-6">
                {displayMessages.length === 0 && (
                  <div className="py-10 text-center text-[13px] text-text-muted">
                    {t(active.channel === 'email' ? 'chats.page.noMessagesEmail' : 'chats.page.noMessages')}
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
                                  <PhoneCall size={11} aria-hidden /> {t('chats.page.voiceCallAt', { time: timeShort(m.ts, f) })}
                                </span>
                              </CenterNote>
                              <div className="mt-1">
                                <CallEntry rec={m._callRec} />
                              </div>
                            </div>
                          </div>
                        ) : m.source === 'system' && !m.private ? (
                          m.text.startsWith('📞 Call transcript') || m.text.startsWith('Call transcript') ? (
                            <CallTranscriptChip text={m.text} time={timeShort(m.ts, f)} />
                          ) : (
                            <div className="my-2">
                              <CenterNote>{t('chats.page.systemNote', { text: m.text, time: timeShort(m.ts, f) })}</CenterNote>
                            </div>
                          )
                        ) : (
                          <div className={`mb-2 flex ${m.dir === 'out' ? 'justify-end' : 'justify-start'}`}>
                            <MessageActions
                              m={m}
                              contactName={active.contact_name || active.name}
                              meName={meName}
                              canReply={!combinedOn && access.can('chats.send') && REACTION_CHANNELS.includes(active.channel)}
                              canReact={!combinedOn && access.can('chats.send') && REACTION_CHANNELS.includes(active.channel)}
                              onReply={(msg) => {
                                setComposerMode('reply')
                                setReplyTo(msg)
                                composerRef.current?.focus()
                              }}
                              onReact={reactTo}
                            >
                            <div
                              className={`cb-bubble ${m.private ? 'cb-bubble-note' : m.dir === 'out' ? 'cb-bubble-out' : 'cb-bubble-in'}${
                                isSticker(m) ? ' cb-bubble-sticker' : ''
                              }`}
                            >
                              {combinedOn && m.channel && <ChannelBadge channel={m.channel} />}
                              {m.private && (
                                <span className="cb-bubble-source">
                                  <Lock size={8} className="mr-0.5 inline" aria-hidden /> {t('chats.page.privateNote')}
                                </span>
                              )}
                              {!m.private && m.dir === 'out' && m.source === 'ai' && <AiBubbleFeedback m={m} store={aiFeedback} />}
                              <MessageContent m={m} contactName={active.contact_name || active.name} onJump={jumpToMessage} />
                              <span className="cb-bubble-time">
                                {(multiAccount || combinedOn) && !m.private && m.account_id && (
                                  <span title={t(m.dir === 'out' ? 'chats.page.sentFrom' : 'chats.page.receivedOn', { account: acctLabel(m) })}>
                                    {t('chats.page.viaAccount', { account: acctLabel(m) })}
                                  </span>
                                )}
                                <span>{timeShort(m.ts, f)}</span>
                                {m.dir === 'out' && !m.private && <StatusTick status={m.status} />}
                              </span>
                            </div>
                            </MessageActions>
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
                  <button onClick={() => setError(null)} aria-label={t('chats.page.dismiss')} className="shrink-0 hover:text-text-high">
                    <X size={12} aria-hidden />
                  </button>
                </div>
              )}

              {/* composer (single channel) — the combined view is read-only */}
              {combinedOn ? (
                <div className="shrink-0 border-t border-line p-3">
                  <div className="flex items-center gap-2 rounded-lg border border-dashed border-line-strong px-3 py-2.5 text-[12.5px] text-text-muted">
                    <Layers size={13} className="shrink-0" aria-hidden />
                    {t('chats.page.combinedReadOnly')}
                  </div>
                </div>
              ) : (
                <>
                {activeId && active && (
                  <HandoverBanner
                    channel={activeId.channel}
                    threadKey={activeId.key}
                    handover={active.handover}
                    assignee={active.assignee}
                    me={meName}
                    canAct={canSendChats}
                    onChanged={() => {
                      refreshActive()
                      refreshThreads()
                    }}
                  />
                )}
                {activeId && (
                  <PendingDraftBanner
                    channel={activeId.channel}
                    threadKey={activeId.key}
                    refreshToken={active?.messages.length}
                  />
                )}
                <div data-tour="chats-composer" className="shrink-0 border-t border-line px-3 pb-3 pt-2 sm:px-4">
                  <div className="mb-2 flex gap-1" role="tablist" aria-label={t('chats.page.composerModeAria')}>
                    <button
                      role="tab"
                      aria-selected={!noteMode}
                      onClick={() => setComposerMode('reply')}
                      className={`flex items-center gap-1 rounded-md px-2.5 py-1 text-[12px] font-semibold transition-colors ${
                        !noteMode ? 'bg-[var(--nav-active-bg)] text-text-heading' : 'text-text-muted hover:bg-fill'
                      }`}
                    >
                      <Send size={11} aria-hidden /> {t('chats.page.reply')}
                    </button>
                    <button
                      role="tab"
                      aria-selected={noteMode}
                      onClick={() => setComposerMode('note')}
                      className={`flex items-center gap-1 rounded-md px-2.5 py-1 text-[12px] font-semibold transition-colors ${
                        noteMode ? 'bg-amber/15 text-amber-300' : 'text-text-muted hover:bg-fill'
                      }`}
                    >
                      <StickyNote size={11} aria-hidden /> {t('chats.page.privateNoteTab')}
                    </button>
                  </div>
                  {cannedMatches.length > 0 && (
                    <div className="mb-2 overflow-hidden rounded-lg border border-line bg-card shadow-pop">
                      <div className="eyebrow border-b border-line px-3 py-1.5">{t('chats.page.quickReplies')}</div>
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
                  {!draft && !pendingFile && canned.length > 0 && !composerLocked && !noteMode && (
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
                  {replyTo && !noteMode && (
                    <ReplyPreview m={replyTo} contactName={active.contact_name || active.name} onCancel={() => setReplyTo(null)} />
                  )}
                  {canAttach && pendingFile && (
                    <div className="mb-2 flex items-center gap-2 rounded-lg border border-line bg-card px-3 py-1.5 text-[12.5px] text-text-mid">
                      <Paperclip size={13} className="shrink-0 text-text-muted" aria-hidden />
                      <span className="min-w-0 flex-1 truncate">
                        {t('chats.page.fileSize', {
                          name: pendingFile.name,
                          size: f.number(pendingFile.size / 1024 / 1024, { minimumFractionDigits: 1, maximumFractionDigits: 1 }),
                        })}
                      </span>
                      <button
                        onClick={() => setPendingFile(null)}
                        title={t('chats.page.removeAttachment')}
                        aria-label={t('chats.page.removeAttachment')}
                        className="grid h-6 w-6 shrink-0 place-items-center rounded-md text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
                      >
                        <X size={12} aria-hidden />
                      </button>
                    </div>
                  )}
                  <div
                    className={`flex items-end gap-1.5 rounded-xl border p-1.5 pl-2 ${
                      noteMode ? 'border-dashed border-amber/50 bg-amber/5' : 'border-line-strong bg-card'
                    }`}
                  >
                    {active.channel === 'whatsapp' && !noteMode && (
                      <button
                        onClick={() => setTplOpen(true)}
                        disabled={busy}
                        title={t('chats.page.sendTemplate')}
                        aria-label={t('chats.page.sendTemplate')}
                        className={`grid h-8 w-8 shrink-0 place-items-center rounded-lg transition-colors disabled:opacity-35 ${
                          windowClosed ? 'bg-primary text-on-primary hover:opacity-90' : 'text-text-muted hover:bg-fill-hover hover:text-text-high'
                        }`}
                      >
                        <LayoutTemplate size={15} aria-hidden />
                      </button>
                    )}
                    {canAttach && !noteMode && (
                      <>
                        <button
                          onClick={() => fileRef.current?.click()}
                          disabled={busy || composerLocked}
                          title={t(
                            windowClosed
                              ? 'chats.page.attachWindowClosed'
                              : socialClosed
                                ? 'chats.page.attachSocialClosed'
                                : 'chats.page.attachFile',
                          )}
                          aria-label={t('chats.page.attachFile')}
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
                    <EmojiButton
                      disabled={composerLocked && !noteMode}
                      onPick={(emoji) => insertAtCursor(composerRef.current, draft, emoji, setDraft)}
                    />
                    <textarea
                      ref={composerRef}
                      rows={1}
                      value={draft}
                      onChange={(e) => setDraft(e.target.value)}
                      // Enter sends; Shift+Enter starts a new line (and an IME composition is never sent mid-word)
                      onKeyDown={(e) => {
                        if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) {
                          e.preventDefault()
                          sendMessage()
                        }
                      }}
                      disabled={composerLocked && !noteMode}
                      placeholder={t(
                        noteMode
                          ? 'chats.page.phNote'
                          : windowClosed
                            ? 'chats.page.phWindowClosed'
                            : socialClosed || platformClosed
                              ? 'chats.page.attachSocialClosed'
                              : pendingFile
                                ? 'chats.page.phCaption'
                                : active.channel === 'email'
                                  ? 'chats.page.phEmail'
                                  : 'chats.page.phDefault',
                      )}
                      aria-label={t('chats.page.messageAria')}
                      title={t('chats.page.newLineHint')}
                      className="max-h-[160px] min-w-0 flex-1 resize-none self-center bg-transparent py-1.5 pl-1 text-[13.5px] leading-snug text-text-primary outline-none placeholder:text-text-faint disabled:cursor-not-allowed disabled:opacity-50"
                    />
                    <button
                      onClick={sendMessage}
                      disabled={busy || (composerLocked && !noteMode) || (!draft.trim() && !pendingFile)}
                      title={noteMode ? t('chats.page.saveNote') : t('common.actions.send')}
                      aria-label={t(noteMode ? 'chats.page.saveNote' : 'chats.page.sendMessage')}
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
                      {t('chats.page.windowClosedNote')}
                    </div>
                  )}
                  {(socialClosed || socialLate) && !noteMode && (
                    <div className="mt-1.5 flex items-center gap-1.5 text-[11.5px] text-amber-300">
                      <Clock size={12} className="shrink-0" aria-hidden />
                      {t(socialClosed ? 'chats.page.socialClosedNote' : 'chats.page.socialLateNote', {
                        channel: channelName(t, active.channel),
                      })}
                    </div>
                  )}
                  {platformClosed && !noteMode && (
                    <div className="mt-1.5 flex items-center gap-1.5 text-[11.5px] text-amber-300">
                      <Clock size={12} className="shrink-0" aria-hidden />
                      {t('chats.page.platformClosedNote', { channel: channelName(t, active.channel) })}
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

      {active && infoDrawer && (
        <div className="fixed inset-0 z-50 flex justify-end xl:hidden" role="dialog" aria-modal="true" aria-label={t('chats.info.aria')}>
          <button
            type="button"
            aria-label={t('common.actions.close')}
            onClick={() => setInfoDrawer(false)}
            className="absolute inset-0"
            style={{ background: 'var(--scrim)' }}
          />
          <div className="relative flex h-full w-full max-w-[380px] flex-col bg-surface shadow-pop">
            <div className="flex items-center justify-between border-b border-line px-4 py-3">
              <span className="text-[14px] font-semibold text-text-heading">{t('chats.page.details')}</span>
              <button
                type="button"
                onClick={() => setInfoDrawer(false)}
                aria-label={t('common.actions.close')}
                className="grid h-8 w-8 place-items-center rounded-lg text-text-muted hover:bg-fill-hover hover:text-text-high"
              >
                <X size={16} aria-hidden />
              </button>
            </div>
            <ChatInfoPanel
              drawer
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
          </div>
        </div>
      )}

      <ChatFilterModal
        open={filterOpen}
        onClose={() => setFilterOpen(false)}
        initial={filters}
        q={q}
        topEntities={topEntities}
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
