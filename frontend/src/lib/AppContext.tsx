'use client'

// Global app state for the dashboard POC. Reconstructed after being lost to a
// `.gitignore` rule. Holds in-memory mock state (no backend) shared by all pages.

import { API } from '@/lib/apiBase'
import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from 'react'
import {
  CONTACTS,
  FEED,
  VOICES,
  LIVE_TRANSCRIPT,
  type Contact,
  type FeedItem,
  type Voice,
  type TranscriptLine,
} from './mocks'
import { useEntity } from './EntityContext'
import { authHeaders, expireSessionOn401, getAuth, userHasCap } from './auth'

// Max simultaneous live calls. The rest of a batch queues and starts as slots
// free up. Cap it to your SIP trunk's concurrent-channel limit.
const MAX_CONCURRENT = Number(process.env.NEXT_PUBLIC_MAX_CONCURRENT) || 3

export type CallSummary = {
  summary: string
  outcome: 'resolved' | 'scheduled' | 'info_provided' | 'declined' | 'unreachable' | string
  followup_date: string | null
  followup_action: string | null
  needs_human: boolean
  objection: string | null
  sentiment_score: number | null
}

export type CallState = 'dialing' | 'ringing' | 'live' | 'ended' | 'error'

// One concurrent outbound call. `room` is the LiveKit room id (also the backend
// lookup key); while a call is still being placed it holds a temporary key.
export type LiveCall = {
  room: string
  contactId?: number // absent for manual dials
  name: string
  phone: string
  status: CallState
  statusText: string
  turns: TranscriptLine[]
  summary: CallSummary | null
  humanControl: boolean
  extraLines: { t: string; human: boolean }[]
  startedAt: number // ms epoch, drives the per-call timer
}

// A queued dial request waiting for a free concurrency slot.
type DialReq = {
  contact_id?: number
  phone?: string
  name?: string
  language?: string
  voiceId?: string
  voiceProvider?: string
  /** Script-call mode: the agent reads this verbatim then hangs up — no
   *  listening, no back-and-forth. {{name}}/{{amount}}/etc render server-side. */
  script?: string
}

const ACTIVE: CallState[] = ['dialing', 'ringing', 'live']

// A call that's still 'ringing' with no transcript after this long has almost
// certainly ended without the backend flagging done (declined at telephony, or
// an agent that died mid-call). Self-heal the tile so it never sticks on
// "Ringing…" forever. Past the ~60s SIP answer window + margin.
const RING_TIMEOUT_MS = 90_000

type AppContextValue = {
  // Data
  contacts: Contact[]
  activities: FeedItem[]
  /** Re-fetch the contact list from the API (after creating/importing one). */
  reloadContacts: () => Promise<void>

  // Voice / persona
  voices: Voice[]
  selectedVoice: string
  /** Pick a voice for THIS browser only (e.g. one campaign). */
  setSelectedVoice: (id: string) => void
  /** Save the organization's call voice (Voice & calls; admins): every call without a voice uses it. */
  saveDefaultVoice: (id: string) => Promise<'saved' | 'local'>
  previewVoice: (voiceId: string, text: string) => Promise<void>
  cloneVoice: (name: string, audio: Blob, language: string, persona: string, gender?: string) => Promise<void>
  importVoice: (elevenVoiceId: string, name?: string) => Promise<void>
  updateVoice: (voiceId: string, patch: { name?: string; gender?: string; accent?: string; language?: string; persona?: string }) => Promise<void>
  deleteVoice: (voiceId: string) => Promise<void>

  // Persona / launch config
  launchTone: string
  launchPersona: string
  launchGender: string
  launchMethod: string
  launchVoiceProvider: string
  setLaunchTone: (t: string) => void
  setLaunchPersona: (id: string) => void
  setLaunchGender: (id: string) => void
  setLaunchMethod: (id: string) => void
  setLaunchVoiceProvider: (v: string) => void

  // Campaign modal
  campaignModal: boolean
  campaignStep: number
  campaignSel: number[]
  openCampaign: () => void
  closeCampaign: () => void
  setCampaignStep: (step: number) => void
  toggleCampaign: (id: number) => void
  launchCampaign: () => void

  // Live-call demo display bits (mock, used when no real call is selected)
  liveContactId: number
  liveLineCount: number
  liveSeconds: number
  responseDraft: string
  setResponseDraft: (v: string) => void
  injectResponse: (text: string, targetRoom?: string) => void
  currentTranscript: () => TranscriptLine[]

  // Real outbound calling — multiple concurrent calls (native Django API)
  calls: Record<string, LiveCall>
  queuedCount: number
  selectedRoom: string | null
  selectCall: (room: string | null) => void
  maxConcurrent: number
  dialContact: (id: number, voiceId?: string, voiceProvider?: string) => void
  dialManual: (phone: string, name?: string, language?: string, voiceId?: string, voiceProvider?: string) => void
  launchBatch: (ids: number[], voiceId?: string, voiceProvider?: string) => void
  /** One-way "read this script and hang up" call — no LLM conversation. */
  dialScriptCall: (id: number, script: string, voiceId?: string, voiceProvider?: string) => void
  sendSay: (room: string, text: string, mode: 'guide' | 'takeover') => Promise<void>
  hangupCall: (room: string) => Promise<void>
  escalateCall: (room: string) => Promise<void>
  setHumanControl: (room: string, v: boolean) => void
  resetCall: (room?: string) => void
}

const AppCtx = createContext<AppContextValue | null>(null)

/** Any of these opens GET /api/contacts (the server's read gate). */
const CONTACT_LIST_CAPS = ['contacts.view', 'chats.view', 'users.manage']

export function AppProvider({ children }: { children: ReactNode }) {
  // Data — loaded from the Django /contacts endpoint, with the
  // bundled mock list as an offline fallback so the UI still renders if the API is down.
  const [contacts, setContacts] = useState<Contact[]>(CONTACTS)
  const [activities] = useState<FeedItem[]>(FEED)

  // Active entity from the topbar switcher scopes the contact list server-side
  // (?entity=<slug>). null/undefined = whole tenant (no narrowing).
  const { activeEntity } = useEntity()
  const entitySlug = activeEntity?.slug ?? null
  const contactsUrl = entitySlug
    ? `${API}/contacts?entity=${encodeURIComponent(entitySlug)}`
    : `${API}/contacts`

  useEffect(() => {
    let cancelled = false
    // Public pages (the Help Center) mount this provider with no session —
    // calling a bearer-gated endpoint there is a guaranteed 401 in the console.
    const auth = getAuth()
    if (!auth) return
    // The server only lists contacts to the Contacts page, the inbox and the
    // Users page's picker (contacts.view | chats.view | users.manage) — a role
    // with none of them would just collect a 403 on every page.
    if (!CONTACT_LIST_CAPS.some((c) => userHasCap(auth, c))) return
    // /api/contacts is bearer-gated: WITHOUT the header it 401s and the catch
    // below silently keeps the demo mocks, so the page showed ten invented
    // people over a live database. Only a transport failure may fall back now.
    fetch(contactsUrl, { headers: authHeaders() })
      .then((r) => {
        if (expireSessionOn401(r)) throw new Error('Session expired — redirecting to login…')
        return r.ok ? r.json() : Promise.reject(new Error(`HTTP ${r.status}`))
      })
      .then((d) => {
        // An EMPTY list is a real answer (fresh production DB) — show it.
        // The bundled mocks only survive when the API itself is unreachable.
        if (!cancelled && Array.isArray(d.contacts)) setContacts(d.contacts)
      })
      .catch(() => {
        /* keep mock fallback */
      })
    return () => {
      cancelled = true
    }
  }, [contactsUrl])

  const reloadContacts = useCallback(async () => {
    try {
      const r = await fetch(contactsUrl, { headers: authHeaders() })
      if (expireSessionOn401(r) || !r.ok) return
      const d = await r.json()
      if (Array.isArray(d.contacts)) setContacts(d.contacts)
    } catch {
      /* API down — keep the current list */
    }
  }, [contactsUrl])

  // Voice / persona
  const [voices, setVoices] = useState<Voice[]>(VOICES)
  const [selectedVoice, setSelectedVoice] = useState<string>(VOICES[0]?.id ?? 'marin')

  useEffect(() => {
    fetch(`${API}/voices`)
      .then((r) => (r.ok ? r.json() : Promise.reject(new Error(`HTTP ${r.status}`))))
      .then((d) => {
        if (Array.isArray(d.voices) && d.voices.length) setVoices(d.voices)
        // the organization's saved voice (Voice & calls) is the starting pick after every reload
        if (typeof d.default_voice === 'string' && d.default_voice) setSelectedVoice(d.default_voice)
      })
      .catch(() => {
        /* keep VOICES mock when API is down */
      })
  }, [])

  const saveDefaultVoice = useCallback(async (id: string): Promise<'saved' | 'local'> => {
    setSelectedVoice(id)
    try {
      const resp = await fetch(`${API}/voice/default-voice`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json', ...authHeaders() },
        body: JSON.stringify({ voice: id }),
      })
      return resp.ok ? 'saved' : 'local'
    } catch {
      return 'local'
    }
  }, [])

  const previewVoice = useCallback(
    (voiceId: string, text: string) => import('@/lib/voice').then((m) => m.playPreview(voiceId, text)),
    [],
  )

  const cloneVoice = useCallback(async (name: string, audio: Blob, language: string, persona: string, gender?: string) => {
    const fd = new FormData()
    fd.append('name', name)
    fd.append('language', language)
    fd.append('persona', persona)
    if (gender) fd.append('gender', gender)
    fd.append('audio', audio, audio instanceof File ? audio.name : 'sample.webm')
    const resp = await fetch(`${API}/voice/clone`, { method: 'POST', body: fd, headers: authHeaders() })
    if (!resp.ok) throw new Error(`clone failed (${resp.status})`)
    const { voice } = await resp.json()
    setVoices((prev) => [...prev, voice])
  }, [])

  const importVoice = useCallback(async (elevenVoiceId: string, name?: string) => {
    const resp = await fetch(`${API}/voice/import`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', ...authHeaders() },
      body: JSON.stringify({ voiceId: elevenVoiceId, name }),
    })
    if (!resp.ok) throw new Error(`import failed (${resp.status})`)
    const { voice } = await resp.json()
    setVoices((prev) => (prev.some((v) => v.id === voice.id) ? prev : [...prev, voice]))
  }, [])

  const updateVoice = useCallback(
    async (voiceId: string, patch: { name?: string; gender?: string; accent?: string; language?: string; persona?: string }) => {
      const resp = await fetch(`${API}/voice/${voiceId}`, {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json', ...authHeaders() },
        body: JSON.stringify(patch),
      })
      if (!resp.ok) throw new Error(`update failed (${resp.status})`)
      const { voice } = await resp.json()
      setVoices((prev) => prev.map((v) => (v.id === voiceId ? voice : v)))
    },
    [],
  )

  const deleteVoice = useCallback(
    async (voiceId: string) => {
      const resp = await fetch(`${API}/voice/${voiceId}`, { method: 'DELETE', headers: authHeaders() })
      if (!resp.ok) throw new Error(`delete failed (${resp.status})`)
      setVoices((prev) => prev.filter((v) => v.id !== voiceId))
      setSelectedVoice((prev) => (prev === voiceId ? VOICES[0]?.id ?? 'marin' : prev))
    },
    [],
  )

  // Persona / launch config
  const [launchTone, setLaunchTone] = useState('professional')
  const [launchPersona, setLaunchPersona] = useState('advisor')
  const [launchGender, setLaunchGender] = useState('female')
  const [launchMethod, setLaunchMethod] = useState('ai-call')
  const [launchVoiceProvider, setLaunchVoiceProvider] = useState('openai')

  // Live-call demo display bits (mock, shown when no real call is selected)
  const [liveContactId] = useState(4)
  const [liveLineCount] = useState(LIVE_TRANSCRIPT.length)
  const [liveSeconds] = useState(47)
  const [responseDraft, setResponseDraft] = useState('')

  // ── Real outbound calls — a map keyed by room, plus a waiting queue ─────────
  const [calls, setCalls] = useState<Record<string, LiveCall>>({})
  const [queue, setQueue] = useState<DialReq[]>([])
  const [selectedRoom, setSelectedRoom] = useState<string | null>(null)
  const seqRef = useRef(0) // unique temp keys for calls still being placed

  const selectCall = useCallback((room: string | null) => setSelectedRoom(room), [])

  // Place one dial. A placeholder entry is inserted synchronously (so it counts
  // toward the concurrency cap immediately, before the network round-trip), then
  // re-keyed to the real room id once the backend returns it.
  const startDial = useCallback(
    (req: DialReq) => {
      const tmpKey = `pending-${++seqRef.current}`
      // Tile label: from the CRM row for a contact dial (backfilled once `contacts`
      // loads), else the manual input.
      const label =
        req.contact_id != null
          ? { name: '', phone: '', contactId: req.contact_id as number | undefined }
          : { name: req.name || 'Contact', phone: req.phone || '', contactId: undefined }
      const placeholder: LiveCall = {
        room: tmpKey,
        contactId: label.contactId,
        name: label.name,
        phone: label.phone,
        status: 'dialing',
        statusText: 'Dialing…',
        turns: [],
        summary: null,
        humanControl: false,
        extraLines: [],
        startedAt: Date.now(),
      }
      setCalls((prev) => ({ ...prev, [tmpKey]: placeholder }))
      setSelectedRoom((prev) => prev ?? tmpKey)

      // Send exactly what the caller gave — no silent fallback to the global
      // picker here. A dial with no voiceId/voiceProvider must reach the
      // backend that way, so it falls through to the contact's OWN remembered
      // voice (data/memory/{id}.md) instead of always overwriting it with
      // whatever the campaign picker happens to be set to (see launchCampaign,
      // the one call site that legitimately wants the global picker applied).
      fetch(`${API}/calls/dial`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', ...authHeaders() },
        body: JSON.stringify(req),
      })
        .then(async (res) => {
          const data = await res.json()
          if (!res.ok) throw new Error(data.detail || `HTTP ${res.status}`)
          const room: string = data.room
          setCalls((prev) => {
            const entry = prev[tmpKey]
            if (!entry) return prev
            const { [tmpKey]: _drop, ...rest } = prev
            return {
              ...rest,
              [room]: { ...entry, room, status: 'ringing', statusText: '📲 Ringing — answer the phone' },
            }
          })
          setSelectedRoom((prev) => (prev === tmpKey ? room : prev))
        })
        .catch((e: unknown) => {
          const msg = e instanceof Error ? e.message : String(e)
          setCalls((prev) =>
            prev[tmpKey]
              ? { ...prev, [tmpKey]: { ...prev[tmpKey], status: 'error', statusText: '❌ ' + msg } }
              : prev,
          )
        })
    },
    [],
  )

  // Enqueue dials; the pump effect starts them as slots free up.
  const launchBatch = useCallback((ids: number[], voiceId?: string, voiceProvider?: string) => {
    setQueue((prev) => {
      const seen = new Set(prev.filter((r) => r.contact_id != null).map((r) => r.contact_id))
      const add = ids.filter((id) => !seen.has(id)).map((id) => ({ contact_id: id, voiceId, voiceProvider }))
      return [...prev, ...add]
    })
  }, [])

  const dialContact = useCallback(
    (id: number, voiceId?: string, voiceProvider?: string) => launchBatch([id], voiceId, voiceProvider),
    [launchBatch],
  )
  const dialManual = useCallback(
    (phone: string, name?: string, language?: string, voiceId?: string, voiceProvider?: string) =>
      setQueue((prev) => [...prev, { phone, name: name ?? '', language: language ?? 'en', voiceId, voiceProvider }]),
    [],
  )
  const dialScriptCall = useCallback(
    (id: number, script: string, voiceId?: string, voiceProvider?: string) =>
      setQueue((prev) => [...prev, { contact_id: id, script, voiceId, voiceProvider }]),
    [],
  )

  // Pump: whenever a slot is free and the queue is non-empty, start the next dial.
  const activeCount = useMemo(
    () => Object.values(calls).filter((c) => ACTIVE.includes(c.status)).length,
    [calls],
  )
  useEffect(() => {
    if (activeCount >= MAX_CONCURRENT || queue.length === 0) return
    const [next, ...rest] = queue
    setQueue(rest)
    startDial(next)
  }, [activeCount, queue, startDial])

  // Backfill contact name/phone on tiles once the contact list has loaded.
  useEffect(() => {
    setCalls((prev) => {
      let changed = false
      const out: Record<string, LiveCall> = {}
      for (const [k, c] of Object.entries(prev)) {
        if (c.contactId != null && (!c.name || !c.phone)) {
          const d = contacts.find((x) => x.id === c.contactId)
          if (d) {
            out[k] = { ...c, name: c.name || d.name, phone: c.phone || d.phone }
            changed = true
            continue
          }
        }
        out[k] = c
      }
      return changed ? out : prev
    })
  }, [contacts, calls])

  // Poll the live transcript for every non-finished call. One interval reads the
  // latest calls from a ref so it doesn't churn on every state update.
  const callsRef = useRef(calls)
  callsRef.current = calls
  useEffect(() => {
    const tick = async () => {
      const active = Object.values(callsRef.current).filter(
        (c) => c.status === 'ringing' || c.status === 'live',
      )
      if (!active.length) return
      await Promise.all(
        active.map(async (c) => {
          try {
            const r = await fetch(`${API}/calls/${encodeURIComponent(c.room)}/live`, { headers: authHeaders() })
            if (!r.ok) return
            const live = await r.json()
            const turns: TranscriptLine[] = Array.isArray(live.turns)
              ? live.turns.map(
                  (t: { role?: string; who?: string; text?: string; t?: string; kb?: string }) => ({
                    who: (t.who ?? (t.role === 'assistant' ? 'ai' : 'contact')) as 'ai' | 'contact',
                    t: t.text ?? t.t ?? '',
                    kb: t.kb,
                  }),
                )
              : []
            let summary: CallSummary | null = null
            if (live.done) {
              const s = await fetch(`${API}/calls/${encodeURIComponent(c.room)}`, { headers: authHeaders() })
              if (s.ok) summary = await s.json()
            }
            setCalls((prev) => {
              const cur = prev[c.room]
              if (!cur) return prev
              // self-heal a call stuck ringing with nothing to show
              const stale =
                cur.status === 'ringing' &&
                turns.length === 0 &&
                Date.now() - (cur.startedAt || Date.now()) > RING_TIMEOUT_MS
              const status: CallState = live.done || stale ? 'ended' : turns.length ? 'live' : cur.status
              const statusText = live.done
                ? '✅ Call ended'
                : stale
                  ? '📵 No answer'
                  : turns.length
                    ? '🔴 Live — call in progress'
                    : cur.statusText
              return { ...prev, [c.room]: { ...cur, turns, summary: summary ?? cur.summary, status, statusText } }
            })
          } catch {
            /* keep polling */
          }
        }),
      )
    }
    const id = setInterval(tick, 1500)
    tick()
    return () => clearInterval(id)
  }, [])

  const sendSay = useCallback(async (room: string, text: string, mode: 'guide' | 'takeover') => {
    try {
      await fetch(`${API}/calls/${encodeURIComponent(room)}/say`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', ...authHeaders() },
        body: JSON.stringify({ text, mode }),
      })
    } catch {
      /* best effort */
    }
  }, [])

  const hangupCall = useCallback(async (room: string) => {
    setCalls((prev) =>
      prev[room] ? { ...prev, [room]: { ...prev[room], statusText: '☎ Hanging up…' } } : prev,
    )
    try {
      await fetch(`${API}/calls/${encodeURIComponent(room)}/hangup`, { method: 'POST', headers: authHeaders() })
    } catch {
      /* ignore */
    }
  }, [])

  const escalateCall = useCallback(async (room: string) => {
    setCalls((prev) =>
      prev[room] ? { ...prev, [room]: { ...prev[room], humanControl: true } } : prev,
    )
    try {
      await fetch(`${API}/calls/${encodeURIComponent(room)}/escalate`, { method: 'POST', headers: authHeaders() })
    } catch {
      /* ignore */
    }
  }, [])

  const setHumanControl = useCallback((room: string, v: boolean) => {
    setCalls((prev) => (prev[room] ? { ...prev, [room]: { ...prev[room], humanControl: v } } : prev))
  }, [])

  // resetCall(room) drops one call; resetCall() clears all finished calls.
  const resetCall = useCallback((room?: string) => {
    setCalls((prev) => {
      const out: Record<string, LiveCall> = {}
      for (const [k, c] of Object.entries(prev)) {
        if (room ? k === room : c.status === 'ended' || c.status === 'error') continue
        out[k] = c
      }
      return out
    })
    setSelectedRoom((prev) => {
      if (room) return prev === room ? null : prev
      return prev && (calls[prev]?.status === 'ended' || calls[prev]?.status === 'error') ? null : prev
    })
  }, [calls])

  // Campaign modal — selecting contacts here launches a real concurrent batch.
  const [campaignModal, setCampaignModal] = useState(false)
  const [campaignStep, setCampaignStep] = useState(1)
  const [campaignSel, setCampaignSel] = useState<number[]>([])
  const openCampaign = useCallback(() => {
    setCampaignStep(1)
    setCampaignSel([])
    setCampaignModal(true)
  }, [])
  const closeCampaign = useCallback(() => setCampaignModal(false), [])
  const toggleCampaign = useCallback((id: number) => {
    setCampaignSel((prev) => (prev.includes(id) ? prev.filter((x) => x !== id) : [...prev, id]))
  }, [])
  const launchCampaign = useCallback(() => {
    // Explicit here on purpose: startDial no longer silently injects the
    // global picker onto every dial (that was clobbering each contact's own
    // remembered voice, data/memory/{id}.md, on every plain one-tap call).
    // A campaign launch is the one place that SHOULD apply the operator's
    // globally-selected voice to every dial in the batch.
    launchBatch(campaignSel, selectedVoice, launchVoiceProvider)
    setCampaignModal(false)
  }, [campaignSel, launchBatch, selectedVoice, launchVoiceProvider])

  // Steer/take-over a call. Defaults to the drilled-in call; pass `targetRoom`
  // to guide a specific panel in the multi-call monitor grid.
  const injectResponse = useCallback(
    (text: string, targetRoom?: string) => {
      if (!text.trim()) return
      const room = targetRoom ?? selectedRoom
      const call = room ? calls[room] : null
      if (room && call && ACTIVE.includes(call.status)) {
        sendSay(room, text, call.humanControl ? 'takeover' : 'guide')
        setCalls((prev) =>
          prev[room]
            ? { ...prev, [room]: { ...prev[room], extraLines: [...prev[room].extraLines, { t: text, human: prev[room].humanControl }] } }
            : prev,
        )
      }
      if (!targetRoom) setResponseDraft('')
    },
    [selectedRoom, calls, sendSay],
  )

  // Selected call's turns when one is live/selected; otherwise the mock demo transcript.
  const currentTranscript = useCallback((): TranscriptLine[] => {
    const call = selectedRoom ? calls[selectedRoom] : null
    return call ? call.turns : LIVE_TRANSCRIPT
  }, [selectedRoom, calls])

  const value = useMemo<AppContextValue>(
    () => ({
      contacts, activities, reloadContacts,
      voices, selectedVoice, setSelectedVoice, saveDefaultVoice, previewVoice, cloneVoice, importVoice, updateVoice, deleteVoice,
      launchTone, launchPersona, launchGender, launchMethod, launchVoiceProvider,
      setLaunchTone, setLaunchPersona, setLaunchGender, setLaunchMethod,
      setLaunchVoiceProvider,
      campaignModal, campaignStep, campaignSel, openCampaign, closeCampaign, setCampaignStep, toggleCampaign, launchCampaign,
      liveContactId, liveLineCount, liveSeconds, responseDraft, setResponseDraft, injectResponse, currentTranscript,
      calls, queuedCount: queue.length, selectedRoom, selectCall, maxConcurrent: MAX_CONCURRENT,
      dialContact, dialManual, dialScriptCall, launchBatch, sendSay, hangupCall, escalateCall, setHumanControl, resetCall,
    }),
    [
      contacts, activities, reloadContacts,
      voices, selectedVoice, saveDefaultVoice, previewVoice, cloneVoice, importVoice, updateVoice, deleteVoice,
      launchTone, launchPersona, launchGender, launchMethod, launchVoiceProvider,
      campaignModal, campaignStep, campaignSel, openCampaign, closeCampaign, toggleCampaign, launchCampaign,
      liveContactId, liveLineCount, liveSeconds, responseDraft, injectResponse, currentTranscript,
      calls, queue.length, selectedRoom, selectCall,
      dialContact, dialManual, dialScriptCall, launchBatch, sendSay, hangupCall, escalateCall, setHumanControl, resetCall,
    ],
  )

  return <AppCtx.Provider value={value}>{children}</AppCtx.Provider>
}

export function useApp(): AppContextValue {
  const ctx = useContext(AppCtx)
  if (!ctx) throw new Error('useApp must be used within <AppProvider>')
  return ctx
}
