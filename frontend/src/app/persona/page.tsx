'use client'

import { useEffect, useMemo, useRef, useState } from 'react'
import {
  Mic,
  User,
  CheckCircle,
  Play,
  Pause,
  Loader2,
  RefreshCw,
  Pencil,
  Trash2,
  X,
} from 'lucide-react'
import { useApp } from '@/lib/AppContext'
import { SectionLabel } from '@/components/ui/Card'
import { buildOpeningLine } from '@/lib/personaLine'
import { stopPreview, subscribePlaying } from '@/lib/voice'
import { PERSONAS, PERSONA_LABELS } from '@/lib/personas'
import { PronunciationSettings } from '@/components/settings/PronunciationSettings'
import type { Voice } from '@/lib/mocks'
import { tint } from '@/lib/color'

function errMessage(err: unknown): string {
  return err instanceof Error && err.message ? err.message : 'Preview unavailable.'
}
const LANGUAGE_LABELS: Record<string, string> = { en: 'English', ms: 'Malay', zh: 'Chinese', unknown: 'Unknown' }
const SOURCE_LABELS: Record<string, string> = { openai: 'OpenAI', elevenlabs: 'ElevenLabs', gemini: 'Gemini' }
const CLONE_LANGUAGES = [
  { id: 'en', label: 'English' },
  { id: 'ms', label: 'Malay' },
  { id: 'zh', label: 'Chinese' },
]
const EDIT_LANGUAGES = [...CLONE_LANGUAGES, { id: 'unknown', label: 'Unknown' }]

function VoiceFilterSelect({
  value,
  onChange,
  opts,
}: {
  value: string
  onChange: (v: string) => void
  opts: { value: string; label: string }[]
}) {
  return (
    <select
      value={value}
      onChange={(e) => onChange(e.target.value)}
      className="cursor-pointer rounded-[7px] border border-line-strong bg-well px-2.5 py-1.5 text-[12px] font-semibold text-text-high outline-none"
    >
      {opts.map((o) => (
        <option key={o.value} value={o.value}>
          {o.label}
        </option>
      ))}
    </select>
  )
}

// Free-text fields (gender, accent) come from ElevenLabs labels or manual entry with
// inconsistent casing ("female" vs "Female") -- normalize so they don't split into
// duplicate filter options that are really the same tag.
function titleCase(s: string): string {
  return s.replace(/\w\S*/g, (w) => w[0].toUpperCase() + w.slice(1).toLowerCase())
}

function voiceFilterOpts(voices: Voice[], field: 'gender' | 'accent' | 'language' | 'provider' | 'persona', allLabel: string, labelMap?: Record<string, string>) {
  const norm = (v: string) => (labelMap ? v : titleCase(v))
  const vals = Array.from(new Set(voices.map((v) => norm(v[field])))).sort()
  return [{ value: '', label: allLabel }, ...vals.map((v) => ({ value: v, label: labelMap?.[v] ?? v }))]
}
const SAMPLE_LINES: Record<string, string> = {
  en: buildOpeningLine('professional'),
  ms: 'Selamat tengah hari, saya menghubungi daripada ChatBranch berkaitan akaun anda. Bolehkah saya mengambil sedikit masa anda?',
  zh: '下午好,这里是ChatBranch,想和您聊聊您的账户。请问现在方便占用您几分钟时间吗?',
}
const VOICE_WAVES = ['wave1', 'wave3', 'wave2', 'wave4', 'wave2', 'wave3', 'wave1', 'wave2']
const RECORDING_SCRIPTS: Record<string, string> = {
  en: 'Good afternoon. My name is Aisha, and I’m calling from ChatBranch regarding your account. I’m here to help make sure everything is on track. We can go over the latest updates and any questions you might have. Please let me know if now is a convenient time to talk. Thank you for your time, and please feel free to ask me any questions.',
  ms: 'Selamat tengah hari. Nama saya Aisha, dan saya menghubungi daripada ChatBranch berkaitan akaun anda. Saya di sini untuk membantu memastikan semuanya berjalan lancar. Kita boleh semak kemas kini terkini dan sebarang soalan yang anda ada. Sila beritahu saya jika sekarang adalah masa yang sesuai untuk bercakap. Terima kasih atas masa anda, dan jangan segan untuk bertanya sebarang soalan.',
  zh: '下午好。我叫Aisha,是ChatBranch打来关于您账户的电话。我在这里是想确保一切进展顺利。我们可以聊聊最新的进展以及您可能有的任何问题。请告诉我现在是否方便通话。谢谢您的时间,如果您有任何问题,请随时提出。',
}

export default function PersonaPage() {
  const {
    voices,
    selectedVoice,
    setSelectedVoice,
    previewVoice,
    cloneVoice,
    importVoice,
    updateVoice,
    deleteVoice,
  } = useApp()
  const [playingId, setPlayingId] = useState<string | null>(null)
  const [loadingId, setLoadingId] = useState<string | null>(null)
  const [previewErrId, setPreviewErrId] = useState<string | null>(null)
  const [previewErrMsg, setPreviewErrMsg] = useState<string | null>(null)
  const [recording, setRecording] = useState(false)
  const [busy, setBusy] = useState(false)
  const [cloneErr, setCloneErr] = useState<string | null>(null)
  const [showAddVoice, setShowAddVoice] = useState(false)
  const [importId, setImportId] = useState('')
  const [importName, setImportName] = useState('')
  const [cloneLanguage, setCloneLanguage] = useState('en')
  const [previewLangTab, setPreviewLangTab] = useState('en')
  const [clonePersona, setClonePersona] = useState(PERSONAS[0].id)
  const [genderFilter, setGenderFilter] = useState('')
  const [languageFilter, setLanguageFilter] = useState('')
  const [accentFilter, setAccentFilter] = useState('')
  const [sourceFilter, setSourceFilter] = useState('')
  const [personaFilter, setPersonaFilter] = useState('')
  const [editingId, setEditingId] = useState<string | null>(null)
  const [editName, setEditName] = useState('')
  const [editGender, setEditGender] = useState('')
  const [editAccent, setEditAccent] = useState('')
  const [editLanguage, setEditLanguage] = useState('en')
  const [editPersona, setEditPersona] = useState(PERSONAS[0].id)
  const [voiceActionBusy, setVoiceActionBusy] = useState(false)
  const [voiceActionErrId, setVoiceActionErrId] = useState<string | null>(null)
  const [voiceActionErrMsg, setVoiceActionErrMsg] = useState<string | null>(null)
  const fileRef = useRef<HTMLInputElement>(null)
  const recRef = useRef<MediaRecorder | null>(null)
  const chunksRef = useRef<Blob[]>([])
  useEffect(
    () =>
      subscribePlaying((id) => {
        setPlayingId(id)
        if (id) setLoadingId(null)
      }),
    [],
  )

  const filteredVoices = useMemo(
    () =>
      voices.filter(
        (v) =>
          (!genderFilter || titleCase(v.gender) === genderFilter) &&
          (!languageFilter || v.language === languageFilter) &&
          (!accentFilter || titleCase(v.accent) === accentFilter) &&
          (!sourceFilter || v.provider === sourceFilter) &&
          (!personaFilter || v.persona === personaFilter),
      ),
    [voices, genderFilter, languageFilter, accentFilter, sourceFilter, personaFilter],
  )
  const hasVoiceFilters = Boolean(genderFilter || languageFilter || accentFilter || sourceFilter || personaFilter)
  const clearVoiceFilters = () => {
    setGenderFilter('')
    setLanguageFilter('')
    setAccentFilter('')
    setSourceFilter('')
    setPersonaFilter('')
  }

  async function submitClone(blob: Blob) {
    const name = window.prompt('Name this voice:')?.trim()
    if (!name) return
    setBusy(true)
    setCloneErr(null)
    try {
      await cloneVoice(name, blob, cloneLanguage, clonePersona)
      setShowAddVoice(false)
    } catch {
      setCloneErr('Clone failed - check ELEVENLABS_API_KEY and voice-slot limit.')
    } finally {
      setBusy(false)
    }
  }

  async function submitImport() {
    const id = importId.trim()
    if (!id) return
    setBusy(true)
    setCloneErr(null)
    try {
      await importVoice(id, importName.trim() || undefined)
      setShowAddVoice(false)
      setImportId('')
      setImportName('')
    } catch {
      setCloneErr('Import failed - check the voice ID and ELEVENLABS_API_KEY.')
    } finally {
      setBusy(false)
    }
  }

  function onFile(e: React.ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0]
    if (file) void submitClone(file)
    e.target.value = ''
  }

  function startEdit(v: Voice) {
    setEditingId(v.id)
    setEditName(v.name)
    setEditGender(titleCase(v.gender))
    setEditAccent(titleCase(v.accent))
    setEditLanguage(v.language)
    setEditPersona(v.persona)
    setVoiceActionErrId(null)
  }

  async function saveEdit(voiceId: string) {
    setVoiceActionBusy(true)
    setVoiceActionErrId(null)
    try {
      await updateVoice(voiceId, { name: editName, gender: editGender, accent: editAccent, language: editLanguage, persona: editPersona })
      setEditingId(null)
    } catch {
      setVoiceActionErrId(voiceId)
      setVoiceActionErrMsg('Update failed.')
    } finally {
      setVoiceActionBusy(false)
    }
  }

  async function handleDeleteVoice(v: Voice) {
    if (!window.confirm(`Delete "${v.name}"? This also removes it from ElevenLabs.`)) return
    setVoiceActionBusy(true)
    setVoiceActionErrId(null)
    try {
      await deleteVoice(v.id)
    } catch {
      setVoiceActionErrId(v.id)
      setVoiceActionErrMsg('Delete failed - check ELEVENLABS_API_KEY.')
    } finally {
      setVoiceActionBusy(false)
    }
  }

  async function toggleRecord() {
    if (recording) {
      recRef.current?.stop()
      return
    }
    setCloneErr(null)
    try {
      const stream = await navigator.mediaDevices.getUserMedia({ audio: true })
      const rec = new MediaRecorder(stream)
      chunksRef.current = []
      rec.ondataavailable = (event) => {
        if (event.data.size) chunksRef.current.push(event.data)
      }
      rec.onstop = () => {
        stream.getTracks().forEach((track) => track.stop())
        recRef.current = null
        setRecording(false)
        void submitClone(new Blob(chunksRef.current, { type: rec.mimeType || 'audio/webm' }))
      }
      recRef.current = rec
      rec.start()
      setRecording(true)
    } catch {
      setCloneErr('Microphone access was unavailable. Check browser permission and try again.')
    }
  }

  return (
    <div className="flex flex-col gap-4">
      {/* Intro */}
      <div
        className="flex items-start gap-3 rounded-2xl px-[18px] py-3.5"
        style={{ background: 'rgba(255,90,31,0.06)', border: '1px solid rgba(255,90,31,0.16)' }}
      >
        <div className="flex h-8 w-8 shrink-0 items-center justify-center rounded-[9px] bg-brand-400/15">
          <Mic size={16} strokeWidth={1.75} color="#D9480F" />
        </div>
        <div>
          <div className="mb-0.5 text-[13px] font-bold text-text-high">Voice &amp; Calls</div>
          <div className="text-[12px] leading-[1.5] text-text-mid">
            Browse, clone, import, and manage the voices available for AI Responsive Calls.
          </div>
        </div>
      </div>

      {/* Persona legend — the four agent personalities a voice can carry */}
      <div className="grid grid-cols-2 gap-2.5 lg:grid-cols-4">
        {PERSONAS.map((p) => (
          <div key={p.id} className="rounded-2xl border border-line bg-card p-4">
            <div className="mb-2 grid h-9 w-9 place-items-center rounded-xl" style={{ background: tint(p.color, '1A') }}>
              <p.Icon size={16} strokeWidth={1.85} style={{ color: p.color }} />
            </div>
            <div className="text-[12.5px] font-bold text-text-high">{p.label}</div>
            <div className="mt-0.5 text-[11px] leading-snug text-text-muted">{p.sub}</div>
          </div>
        ))}
      </div>

      {/* Voice Library — pick a preset or clone your own; selection drives the config below */}
      <div id="voice-library" className="rounded-2xl border border-line bg-card px-5 py-[22px]">
        <div className="mb-2.5 flex items-center justify-between">
          <SectionLabel>Voice Library</SectionLabel>
          <button
            onClick={() => setShowAddVoice(true)}
            className="rounded-md border border-brand-400/20 bg-brand-400/[0.12] px-2.5 py-1 text-[11px] font-semibold text-brand-200"
          >
            + Add voice
          </button>
        </div>
        <div className="mb-4 text-[12px] leading-[1.6] text-text-mid">
          Click a voice to select it, or add your own recording to clone a custom persona voice.
        </div>
        <details className="mb-4 rounded-lg border border-line bg-fill-subtle p-3 text-left">
          <summary className="cursor-pointer text-[10px] font-bold uppercase tracking-[0.06em] text-brand-200">
            Sample preview line
          </summary>
          <div className="mt-2 flex items-center gap-1.5">
            {CLONE_LANGUAGES.map((l) => (
              <button
                key={l.id}
                onClick={() => setPreviewLangTab(l.id)}
                className="rounded-full px-2.5 py-1 text-[10px] font-semibold"
                style={{
                  border: `1px solid ${previewLangTab === l.id ? 'rgba(255,90,31,0.5)' : 'var(--line)'}`,
                  background: previewLangTab === l.id ? 'rgba(255,90,31,0.12)' : 'transparent',
                  color: previewLangTab === l.id ? 'var(--brand-ink)' : 'var(--text-muted)',
                }}
              >
                {l.label}
              </button>
            ))}
          </div>
          <blockquote className="mt-2 text-[11px] leading-[1.65] text-text-mid">
            &ldquo;{SAMPLE_LINES[previewLangTab] ?? SAMPLE_LINES.en}&rdquo;
          </blockquote>
          <div className="mt-2 text-[10px] text-text-muted">
            Each voice&rsquo;s Preview button speaks this line in its own language.
          </div>
        </details>
        {showAddVoice && (
          <div
            onClick={() => setShowAddVoice(false)}
            className="fixed inset-0 z-50 flex animate-fadeIn items-center justify-center p-6 backdrop-blur-sm"
            style={{ background: 'rgba(4,8,18,0.72)' }}
          >
            <div
              onClick={(e) => e.stopPropagation()}
              className="flex max-h-[86vh] w-full max-w-[480px] flex-col overflow-hidden rounded-2xl border border-line-strong bg-well"
              style={{ boxShadow: '0 24px 60px rgba(0,0,0,0.5)' }}
            >
              <div className="flex items-center justify-between border-b border-line px-[22px] py-[18px]">
                <div className="text-[15px] font-extrabold tracking-[-0.4px] text-text-primary">Add a voice</div>
                <button
                  onClick={() => setShowAddVoice(false)}
                  className="flex h-[30px] w-[30px] shrink-0 items-center justify-center rounded-lg border border-line bg-fill text-text-muted"
                >
                  <X size={15} strokeWidth={1.75} />
                </button>
              </div>
              <div className="flex flex-1 flex-col items-center gap-2 overflow-y-auto p-[22px] text-center">
                <div className="flex h-11 w-11 items-center justify-center rounded-xl bg-brand-400/15">
                  <Mic size={20} strokeWidth={1.75} color="#D9480F" />
                </div>
                <div className="text-[13px] font-bold text-text-high">Add your own voice</div>
                <div className="text-[11px] text-text-muted">MP3 or WAV · 30-60s of clear speech recommended</div>
                <details open className="mt-1 w-full rounded-lg border border-line bg-fill-subtle p-3 text-left">
                  <summary className="mb-1.5 cursor-pointer text-[10px] font-bold uppercase tracking-[0.06em] text-brand-200">
                    Recording script · {LANGUAGE_LABELS[cloneLanguage] ?? cloneLanguage} · Read aloud
                  </summary>
                  <blockquote className="text-[11px] leading-[1.65] text-text-mid">
                    &ldquo;{RECORDING_SCRIPTS[cloneLanguage] ?? RECORDING_SCRIPTS.en}&rdquo;
                  </blockquote>
                  <div className="mt-2 text-[10px] text-text-muted">
                    Speak naturally in a quiet room and keep the microphone 15-20 cm away.
                  </div>
                </details>
                <div className="mt-1 flex flex-wrap items-center justify-center gap-2">
                  <select
                    value={cloneLanguage}
                    onChange={(e) => setCloneLanguage(e.target.value)}
                    disabled={busy || recording}
                    className="cursor-pointer rounded-md border border-line-strong bg-well px-2.5 py-1.5 text-[11px] font-semibold text-text-high outline-none disabled:cursor-not-allowed disabled:opacity-50"
                  >
                    {CLONE_LANGUAGES.map((l) => (
                      <option key={l.id} value={l.id}>
                        {l.label}
                      </option>
                    ))}
                  </select>
                  <select
                    value={clonePersona}
                    onChange={(e) => setClonePersona(e.target.value)}
                    disabled={busy || recording}
                    className="cursor-pointer rounded-md border border-line-strong bg-well px-2.5 py-1.5 text-[11px] font-semibold text-text-high outline-none disabled:cursor-not-allowed disabled:opacity-50"
                  >
                    {PERSONAS.map((p) => (
                      <option key={p.id} value={p.id}>
                        {p.label}
                      </option>
                    ))}
                  </select>
                  <button
                    disabled={busy || recording}
                    onClick={() => fileRef.current?.click()}
                    className="rounded-md border border-brand-400/20 bg-brand-400/[0.12] px-3 py-1.5 text-[11px] font-semibold text-brand-200 disabled:cursor-not-allowed disabled:opacity-50"
                  >
                    {busy ? 'Processing...' : 'Upload file'}
                  </button>
                  <button
                    disabled={busy}
                    onClick={() => void toggleRecord()}
                    className="rounded-md border border-brand-400/20 bg-brand-400/[0.12] px-3 py-1.5 text-[11px] font-semibold text-brand-200 disabled:cursor-not-allowed disabled:opacity-50"
                  >
                    {recording ? 'Stop recording' : 'Record'}
                  </button>
                </div>
                <input ref={fileRef} type="file" accept="audio/*" hidden onChange={onFile} />
                <div className="mt-1 text-[10px] text-text-muted">
                  By adding a voice you confirm you have the speaker&rsquo;s consent to clone it.
                </div>
                <div className="mt-3 flex w-full items-center gap-1.5 text-[10px] font-semibold uppercase tracking-[0.06em] text-text-muted">
                  <span className="h-px flex-1 bg-line" />
                  or import an existing ElevenLabs voice
                  <span className="h-px flex-1 bg-line" />
                </div>
                <div className="mt-2 flex w-full flex-wrap items-center gap-2">
                  <input
                    value={importId}
                    onChange={(e) => setImportId(e.target.value)}
                    placeholder="ElevenLabs voice ID"
                    className="flex-[1_1_160px] rounded-md border border-line-strong bg-well px-2.5 py-1.5 text-[11px] text-text-high outline-none"
                  />
                  <input
                    value={importName}
                    onChange={(e) => setImportName(e.target.value)}
                    placeholder="Name (optional)"
                    className="flex-[1_1_120px] rounded-md border border-line-strong bg-well px-2.5 py-1.5 text-[11px] text-text-high outline-none"
                  />
                  <button
                    disabled={busy || !importId.trim()}
                    onClick={() => void submitImport()}
                    className="rounded-md border border-brand-400/20 bg-brand-400/[0.12] px-3 py-1.5 text-[11px] font-semibold text-brand-200 disabled:cursor-not-allowed disabled:opacity-50"
                  >
                    {busy ? 'Processing...' : 'Import'}
                  </button>
                </div>
                {cloneErr && <div className="mt-1 text-[10px] font-semibold text-red-300">{cloneErr}</div>}
              </div>
            </div>
          </div>
        )}
        <div className="mb-3 flex flex-wrap items-center gap-2">
          <VoiceFilterSelect value={genderFilter} onChange={setGenderFilter} opts={voiceFilterOpts(voices, 'gender', 'All genders')} />
          <VoiceFilterSelect value={languageFilter} onChange={setLanguageFilter} opts={voiceFilterOpts(voices, 'language', 'All languages', LANGUAGE_LABELS)} />
          <VoiceFilterSelect value={accentFilter} onChange={setAccentFilter} opts={voiceFilterOpts(voices, 'accent', 'All accents')} />
          <VoiceFilterSelect value={sourceFilter} onChange={setSourceFilter} opts={voiceFilterOpts(voices, 'provider', 'All sources', SOURCE_LABELS)} />
          <VoiceFilterSelect value={personaFilter} onChange={setPersonaFilter} opts={voiceFilterOpts(voices, 'persona', 'All personas', PERSONA_LABELS)} />
          {hasVoiceFilters && (
            <button
              onClick={clearVoiceFilters}
              className="rounded-[7px] border border-line-strong bg-well px-2.5 py-1.5 text-[12px] font-semibold text-text-muted"
            >
              Clear
            </button>
          )}
          <span className="ml-auto text-[12px] font-semibold text-text-muted">
            {filteredVoices.length} of {voices.length}
          </span>
        </div>
        <div className="grid grid-cols-1 gap-2.5 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4">
          {filteredVoices.map((v) => {
            const sel = selectedVoice === v.id
            const ready = v.status === 'ready'
            const isGemini = v.id === 'puck'
            const editing = editingId === v.id
            return (
              <div
                key={v.id}
                onClick={() => !editing && ready && setSelectedVoice(v.id)}
                className="relative flex h-full flex-col rounded-[11px] p-3.5 transition-all"
                style={{
                  border: `1.5px solid ${sel ? 'rgba(255,90,31,0.5)' : 'var(--line)'}`,
                  background: sel ? 'rgba(255,90,31,0.08)' : 'var(--fill-subtle)',
                  cursor: editing ? 'default' : ready ? 'pointer' : 'default',
                  opacity: ready ? 1 : 0.7,
                }}
              >
                {!editing && (
                  <div className="absolute right-2.5 top-2.5 flex items-center gap-1">
                    {v.tag ? (
                      <span className="rounded-full bg-green/15 px-1.5 py-px text-[9px] font-bold uppercase text-green-300">
                        {v.tag}
                      </span>
                    ) : (
                      <span className="rounded-full bg-purple/15 px-1.5 py-px text-[9px] font-bold uppercase text-purple-300">
                        {SOURCE_LABELS[v.provider] ?? v.provider}
                      </span>
                    )}
                    {v.custom && (
                      <>
                        <button
                          onClick={(e) => {
                            e.stopPropagation()
                            startEdit(v)
                          }}
                          className="flex h-6 w-6 items-center justify-center rounded-full bg-fill-hover"
                          aria-label="Edit voice"
                        >
                          <Pencil size={12} strokeWidth={2} color="#8A8177" />
                        </button>
                        <button
                          onClick={(e) => {
                            e.stopPropagation()
                            void handleDeleteVoice(v)
                          }}
                          className="flex h-6 w-6 items-center justify-center rounded-full bg-fill-hover"
                          aria-label="Delete voice"
                        >
                          <Trash2 size={12} strokeWidth={2} color="#C0392B" />
                        </button>
                      </>
                    )}
                  </div>
                )}
                <div className="mb-2.5 flex items-start gap-2.5 pr-[132px]">
                  <div
                    className="relative flex h-[34px] w-[34px] shrink-0 items-center justify-center rounded-[9px]"
                    style={{ background: v.custom ? 'rgba(122,78,140,0.18)' : 'rgba(255,90,31,0.15)' }}
                  >
                    {v.custom ? (
                      <User size={15} strokeWidth={1.75} color="#7A4E8C" />
                    ) : (
                      <Mic size={15} strokeWidth={1.75} color="#D9480F" />
                    )}
                    {sel && !editing && (
                      <div className="absolute -bottom-1 -right-1 flex h-4 w-4 items-center justify-center rounded-full bg-card">
                        <CheckCircle size={14} strokeWidth={2} color="#D9480F" />
                      </div>
                    )}
                  </div>
                  <div className="min-w-0 flex-1">
                    {editing ? (
                      <input
                        value={editName}
                        onChange={(e) => setEditName(e.target.value)}
                        onClick={(e) => e.stopPropagation()}
                        className="w-full rounded-md border border-line-strong bg-well px-2 py-1 text-[12px] font-bold text-text-high outline-none"
                        placeholder="Name"
                      />
                    ) : (
                      <>
                        <div className="text-[13px] font-bold leading-snug text-text-high">{v.name}</div>
                        <div className="whitespace-nowrap text-[10px] text-text-muted">{titleCase(v.gender)} · {titleCase(v.accent)}</div>
                        <div className="whitespace-nowrap text-[10px] text-text-muted">
                          {LANGUAGE_LABELS[v.language] ?? v.language} · {PERSONA_LABELS[v.persona] ?? v.persona}
                        </div>
                      </>
                    )}
                  </div>
                </div>
                {editing ? (
                  <div onClick={(e) => e.stopPropagation()} className="flex flex-col gap-1.5">
                    <div className="flex gap-1.5">
                      <input
                        value={editGender}
                        onChange={(e) => setEditGender(e.target.value)}
                        className="w-1/2 rounded-md border border-line-strong bg-well px-2 py-1 text-[11px] text-text-high outline-none"
                        placeholder="Gender"
                      />
                      <input
                        value={editAccent}
                        onChange={(e) => setEditAccent(e.target.value)}
                        className="w-1/2 rounded-md border border-line-strong bg-well px-2 py-1 text-[11px] text-text-high outline-none"
                        placeholder="Accent"
                      />
                    </div>
                    <select
                      value={editLanguage}
                      onChange={(e) => setEditLanguage(e.target.value)}
                      className="cursor-pointer rounded-md border border-line-strong bg-well px-2 py-1 text-[11px] text-text-high outline-none"
                    >
                      {EDIT_LANGUAGES.map((l) => (
                        <option key={l.id} value={l.id}>
                          {l.label}
                        </option>
                      ))}
                    </select>
                    <select
                      value={editPersona}
                      onChange={(e) => setEditPersona(e.target.value)}
                      className="cursor-pointer rounded-md border border-line-strong bg-well px-2 py-1 text-[11px] text-text-high outline-none"
                    >
                      {PERSONAS.map((p) => (
                        <option key={p.id} value={p.id}>
                          {p.label}
                        </option>
                      ))}
                    </select>
                    <div className="mt-1 flex gap-1.5">
                      <button
                        disabled={voiceActionBusy}
                        onClick={() => void saveEdit(v.id)}
                        className="flex-1 rounded-md border border-brand-400/20 bg-brand-400/[0.12] px-2 py-1 text-[11px] font-semibold text-brand-200 disabled:cursor-not-allowed disabled:opacity-50"
                      >
                        {voiceActionBusy ? 'Saving...' : 'Save'}
                      </button>
                      <button
                        disabled={voiceActionBusy}
                        onClick={() => setEditingId(null)}
                        className="flex-1 rounded-md border border-line-strong bg-well px-2 py-1 text-[11px] font-semibold text-text-muted disabled:cursor-not-allowed disabled:opacity-50"
                      >
                        Cancel
                      </button>
                    </div>
                  </div>
                ) : (
                  <div className="mt-auto flex items-center justify-between">
                    <div className="flex h-4 items-end gap-0.5">
                      {VOICE_WAVES.map((w, i) => (
                        <div
                          key={i}
                          className="w-[2.5px] rounded-sm"
                          style={{
                            background: playingId === v.id ? 'rgb(var(--c-brand))' : ready ? 'rgb(var(--c-brand))' : 'var(--text-mid)',
                            height: playingId === v.id ? undefined : 6,
                            animation: playingId === v.id ? `${w} ${1 + i * 0.07}s ease-in-out infinite` : 'none',
                          }}
                        />
                      ))}
                    </div>
                    <div className="flex items-center gap-1.5">
                      {!isGemini && ready ? (
                        <button
                          disabled={loadingId === v.id}
                          onClick={(e) => {
                            e.stopPropagation()
                            if (playingId === v.id) {
                              stopPreview()
                            } else {
                              setPreviewErrId(null)
                              setLoadingId(v.id)
                              previewVoice(v.id, SAMPLE_LINES[v.language] ?? SAMPLE_LINES.en)
                                .catch((err) => {
                                  setPreviewErrId(v.id)
                                  setPreviewErrMsg(errMessage(err))
                                })
                                .finally(() => setLoadingId((id) => (id === v.id ? null : id)))
                            }
                          }}
                          className="flex h-6 w-6 items-center justify-center rounded-full bg-brand-400/15 disabled:cursor-not-allowed"
                          aria-label={loadingId === v.id ? 'Loading preview' : playingId === v.id ? 'Stop preview' : 'Play preview'}
                        >
                          {loadingId === v.id ? (
                            <Loader2 size={12} strokeWidth={2} color="#D9480F" className="animate-spin" />
                          ) : playingId === v.id ? (
                            <Pause size={12} strokeWidth={2} color="#D9480F" />
                          ) : (
                            <Play size={12} strokeWidth={2} color="#D9480F" />
                          )}
                        </button>
                      ) : isGemini ? (
                        <span className="text-[9px] font-semibold text-text-muted">Gemini voice</span>
                      ) : null}
                      {ready ? (
                        <span className="text-[10px] font-bold text-green-300">Ready</span>
                      ) : (
                        <span className="inline-flex items-center gap-1.5 text-[10px] font-bold text-amber-300" style={{ color: 'var(--amber-ink)' }}>
                          <Loader2 size={11} strokeWidth={2.5} color="#B7791F" className="animate-spin" />
                          Processing
                        </span>
                      )}
                    </div>
                  </div>
                )}
                {voiceActionErrId === v.id && (
                  <div className="mt-2 text-[10px] font-semibold text-red-300">{voiceActionErrMsg}</div>
                )}
                {previewErrId === v.id && (
                  <div className="mt-2 text-[10px] font-semibold text-red-300">{previewErrMsg}</div>
                )}
              </div>
            )
          })}
        </div>
        {filteredVoices.length === 0 && (
          <div className="rounded-[11px] border border-dashed border-line bg-fill-subtle px-4 py-8 text-center text-[12px] text-text-muted">
            {voices.length === 0
              ? 'No voices available yet — add one to get started.'
              : 'No voices match these filters.'}
            {hasVoiceFilters && (
              <button onClick={clearVoiceFilters} className="ml-1.5 font-semibold text-brand-300 underline">
                Clear filters
              </button>
            )}
          </div>
        )}
        <div className="mt-4 flex items-center gap-1.5 text-[11px] text-text-muted">
          <RefreshCw size={12} strokeWidth={1.75} color="#8A8177" />
          Selected voices are assignable per campaign in Playbook and Live Calling.
        </div>
      </div>

      {/* Pronunciation lives with the voices it shapes (ElevenLabs respellings
          applied to every TTS call) — moved here from Settings. */}
      <div id="pronunciation" className="rounded-[14px] border border-line bg-card p-4">
        <SectionLabel>Pronunciation</SectionLabel>
        <PronunciationSettings />
      </div>
    </div>
  )
}
