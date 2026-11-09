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
import { Select } from '@/components/ui/Select'
import { Advanced } from '@/components/ui/Advanced'
import { useT, type MessageKey, type TFunction } from '@/i18n'
import { buildOpeningLine } from '@/lib/personaLine'
import { PreviewError, stopPreview, subscribePlaying } from '@/lib/voice'
import { PERSONAS, PERSONA_LABEL_KEYS } from '@/lib/personas'
import { PronunciationSettings } from '@/components/settings/PronunciationSettings'
import type { Voice } from '@/lib/mocks'
import { tint } from '@/lib/color'

function errMessage(t: TFunction, err: unknown): string {
  if (err instanceof PreviewError) return err.message || t('voice.library.previewFailed', { status: err.status })
  return err instanceof Error && err.message ? err.message : t('voice.library.previewUnavailable')
}
/** A voice's language code → the word for it (the code itself is what the API stores). */
const LANGUAGE_KEYS: Record<string, MessageKey> = {
  en: 'voice.languages.en',
  ms: 'voice.languages.ms',
  zh: 'voice.languages.zh',
  unknown: 'common.status.unknown',
}
const SOURCE_LABELS: Record<string, string> = { openai: 'OpenAI', elevenlabs: 'ElevenLabs', gemini: 'Gemini' }
const CLONE_LANGUAGES = ['en', 'ms', 'zh']
const EDIT_LANGUAGES = [...CLONE_LANGUAGES, 'unknown']

function VoiceFilterSelect({
  value,
  onChange,
  opts,
  ariaLabel,
}: {
  value: string
  onChange: (v: string) => void
  opts: { value: string; label: string }[]
  ariaLabel: string
}) {
  return <Select value={value} onChange={onChange} options={opts} className="min-w-[130px]" ariaLabel={ariaLabel} />
}

// Free-text fields (gender, accent) come from ElevenLabs labels or manual entry with
// inconsistent casing ("female" vs "Female") -- normalize so they don't split into
// duplicate filter options that are really the same tag.
// Null-safe: a cloned voice may have no gender / accent yet.
function titleCase(s: string | null | undefined): string {
  return (s ?? '').replace(/\w\S*/g, (w) => w[0].toUpperCase() + w.slice(1).toLowerCase())
}

function voiceFilterOpts(voices: Voice[], field: 'gender' | 'accent' | 'language' | 'provider' | 'persona', allLabel: string, labelMap?: Record<string, string>) {
  const norm = (v: string) => (labelMap ? v : titleCase(v))
  const vals = Array.from(new Set(voices.map((v) => norm(v[field] ?? '')))).filter(Boolean).sort()
  return [{ value: '', label: allLabel }, ...vals.map((v) => ({ value: v, label: labelMap?.[v] ?? v }))]
}
// What each voice SAYS when previewed — in the voice's own language (spoken
// content, not interface text, so these stay as written).
const SAMPLE_LINES: Record<string, string> = {
  en: buildOpeningLine('professional'),
  ms: 'Selamat tengah hari, saya menghubungi daripada ChatBranch berkaitan akaun anda. Bolehkah saya mengambil sedikit masa anda?',
  zh: '下午好,这里是ChatBranch,想和您聊聊您的账户。请问现在方便占用您几分钟时间吗?',
}
const VOICE_WAVES = ['wave1', 'wave3', 'wave2', 'wave4', 'wave2', 'wave3', 'wave1', 'wave2']
// The passage to read aloud when cloning, in the language being cloned.
const RECORDING_SCRIPTS: Record<string, string> = {
  en: 'Good afternoon. My name is Aisha, and I’m calling from ChatBranch regarding your account. I’m here to help make sure everything is on track. We can go over the latest updates and any questions you might have. Please let me know if now is a convenient time to talk. Thank you for your time, and please feel free to ask me any questions.',
  ms: 'Selamat tengah hari. Nama saya Aisha, dan saya menghubungi daripada ChatBranch berkaitan akaun anda. Saya di sini untuk membantu memastikan semuanya berjalan lancar. Kita boleh semak kemas kini terkini dan sebarang soalan yang anda ada. Sila beritahu saya jika sekarang adalah masa yang sesuai untuk bercakap. Terima kasih atas masa anda, dan jangan segan untuk bertanya sebarang soalan.',
  zh: '下午好。我叫Aisha,是ChatBranch打来关于您账户的电话。我在这里是想确保一切进展顺利。我们可以聊聊最新的进展以及您可能有的任何问题。请告诉我现在是否方便通话。谢谢您的时间,如果您有任何问题,请随时提出。',
}

/** Brand names, not translated. */
const CLONE_PROVIDER_NAMES: Record<string, string> = {
  elevenlabs: 'ElevenLabs',
  voicestudio: 'VoiceStudio (self-hosted)',
}

/** The API's own refusal text (AppContext throws it), or null when only a status came back. */
function serverWords(e: unknown): string | null {
  const m = e instanceof Error ? e.message : ''
  return m && !/^(clone|import|delete) failed \(\d+\)$/.test(m) ? m : null
}

export default function PersonaPage() {
  const t = useT()
  const {
    voices,
    selectedVoice,
    saveDefaultVoice,
    previewVoice,
    cloneProviders,
    cloneVoice,
    importVoice,
    updateVoice,
    deleteVoice,
  } = useApp()
  const [playingId, setPlayingId] = useState<string | null>(null)
  // picking a voice saves it for every call (admins) — or for this browser only
  const [voiceSaved, setVoiceSaved] = useState<'saved' | 'local' | null>(null)
  const pickVoice = async (id: string) => {
    if (id === selectedVoice) return
    setVoiceSaved(await saveDefaultVoice(id))
  }
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
  // ElevenLabs or the self-hosted VoiceStudio — whichever is set up ('' = the first one)
  const [cloneProvider, setCloneProvider] = useState('')
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

  const languageName = (code: string) => (LANGUAGE_KEYS[code] ? t(LANGUAGE_KEYS[code]) : code)
  const personaName = (id: string) => (PERSONA_LABEL_KEYS[id] ? t(PERSONA_LABEL_KEYS[id]) : id)
  const languageLabels = Object.fromEntries(Object.keys(LANGUAGE_KEYS).map((k) => [k, languageName(k)]))
  const personaLabels = Object.fromEntries(PERSONAS.map((p) => [p.id, t(p.lbl)]))

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

  async function submitClone(blob: Blob | Blob[]) {
    const name = window.prompt(t('voice.library.namePrompt'))?.trim()
    if (!name) return
    setBusy(true)
    setCloneErr(null)
    try {
      await cloneVoice(name, blob, cloneLanguage, clonePersona, undefined, cloneProvider || cloneProviders[0])
      setShowAddVoice(false)
    } catch (e) {
      setCloneErr(serverWords(e) ?? t('voice.library.cloneFailed'))
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
    } catch (e) {
      setCloneErr(serverWords(e) ?? t('voice.library.importFailed'))
    } finally {
      setBusy(false)
    }
  }

  // several clips of the same speaker → one voice (ElevenLabs uses them all, up to 25)
  function onFile(e: React.ChangeEvent<HTMLInputElement>) {
    const files = Array.from(e.target.files ?? []).slice(0, 25)
    if (files.length) void submitClone(files)
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
      setVoiceActionErrMsg(t('voice.library.updateFailed'))
    } finally {
      setVoiceActionBusy(false)
    }
  }

  async function handleDeleteVoice(v: Voice) {
    if (!window.confirm(t('voice.library.confirmDelete', { name: v.name }))) return
    setVoiceActionBusy(true)
    setVoiceActionErrId(null)
    try {
      await deleteVoice(v.id)
    } catch (e) {
      setVoiceActionErrId(v.id)
      setVoiceActionErrMsg(serverWords(e) ?? t('voice.library.deleteFailed'))
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
      setCloneErr(t('voice.library.micUnavailable'))
    }
  }

  return (
    <div className="flex flex-col gap-4">
      {/* Intro */}
      <div
        data-tour="voice-intro"
        className="flex items-start gap-3 rounded-2xl px-[18px] py-3.5"
        style={{ background: tint('rgb(var(--c-brand))', 0.06), border: `1px solid ${tint('rgb(var(--c-brand))', 0.16)}` }}
      >
        <div className="flex h-8 w-8 shrink-0 items-center justify-center rounded-[9px] bg-brand-400/15">
          <Mic size={16} strokeWidth={1.75} color="#D9480F" />
        </div>
        <div className="min-w-0">
          <div className="mb-0.5 text-[13px] font-bold text-text-high">{t('shell.nav.voice')}</div>
          <div className="text-[12px] leading-[1.5] text-text-mid">{t('voice.page.intro')}</div>
        </div>
      </div>

      {/* Persona legend — the four agent personalities a voice can carry */}
      <div data-tour="voice-personas" className="grid grid-cols-2 gap-2.5 lg:grid-cols-4">
        {PERSONAS.map((p) => (
          <div key={p.id} className="min-w-0 rounded-2xl border border-line bg-card p-4">
            <div className="mb-2 grid h-9 w-9 place-items-center rounded-xl" style={{ background: tint(p.color, '1A') }}>
              <p.Icon size={16} strokeWidth={1.85} style={{ color: p.color }} />
            </div>
            <div className="text-[12.5px] font-bold text-text-high">{t(p.lbl)}</div>
            <div className="mt-0.5 text-[11px] leading-snug text-text-muted">{t(p.sub)}</div>
          </div>
        ))}
      </div>

      {/* Voice Library — pick a preset or clone your own; selection drives the config below */}
      <div id="voice-library" className="rounded-2xl border border-line bg-card px-5 py-[22px]">
        <div className="mb-2.5 flex items-center justify-between gap-2">
          <SectionLabel>{t('voice.library.title')}</SectionLabel>
          <button
            data-tour="voice-add"
            onClick={() => setShowAddVoice(true)}
            className="shrink-0 rounded-md border border-brand-400/20 bg-brand-400/[0.12] px-2.5 py-1 text-[11px] font-semibold text-brand-200"
          >
            {t('voice.library.addVoice')}
          </button>
        </div>
        <div className="mb-4 text-[12px] leading-[1.6] text-text-mid">{t('voice.library.hint')}</div>
        <details className="mb-4 rounded-lg border border-line bg-fill-subtle p-3 text-left">
          <summary className="cursor-pointer text-[10px] font-bold uppercase tracking-[0.06em] text-brand-200">
            {t('voice.library.sampleLine')}
          </summary>
          <div className="mt-2 flex items-center gap-1.5">
            {CLONE_LANGUAGES.map((code) => (
              <button
                key={code}
                onClick={() => setPreviewLangTab(code)}
                className="rounded-full px-2.5 py-1 text-[10px] font-semibold"
                style={{
                  border: `1px solid ${previewLangTab === code ? tint('rgb(var(--c-brand))', 0.5) : 'var(--line)'}`,
                  background: previewLangTab === code ? tint('rgb(var(--c-brand))', 0.12) : 'transparent',
                  color: previewLangTab === code ? 'var(--brand-ink)' : 'var(--text-muted)',
                }}
              >
                {languageName(code)}
              </button>
            ))}
          </div>
          <blockquote className="mt-2 text-[11px] leading-[1.65] text-text-mid">
            &ldquo;{SAMPLE_LINES[previewLangTab] ?? SAMPLE_LINES.en}&rdquo;
          </blockquote>
          <div className="mt-2 text-[10px] text-text-muted">{t('voice.library.sampleLineHint')}</div>
        </details>
        {showAddVoice && (
          <div
            onClick={() => setShowAddVoice(false)}
            className="fixed inset-0 z-50 flex animate-fadeIn items-center justify-center p-6 backdrop-blur-sm"
            style={{ background: 'rgba(4,8,18,0.72)' }}
          >
            <div
              onClick={(e) => e.stopPropagation()}
              role="dialog"
              aria-modal="true"
              aria-label={t('voice.addVoice.title')}
              className="flex max-h-[86vh] w-full max-w-[480px] flex-col overflow-hidden rounded-2xl border border-line-strong bg-well"
              style={{ boxShadow: '0 24px 60px rgba(0,0,0,0.5)' }}
            >
              <div className="flex items-center justify-between border-b border-line px-[22px] py-[18px]">
                <div className="text-[15px] font-extrabold tracking-[-0.4px] text-text-primary">{t('voice.addVoice.title')}</div>
                <button
                  onClick={() => setShowAddVoice(false)}
                  aria-label={t('common.actions.close')}
                  className="flex h-[30px] w-[30px] shrink-0 items-center justify-center rounded-lg border border-line bg-fill text-text-muted"
                >
                  <X size={15} strokeWidth={1.75} />
                </button>
              </div>
              <div className="flex flex-1 flex-col items-center gap-2 overflow-y-auto p-[22px] text-center">
                <div className="flex h-11 w-11 items-center justify-center rounded-xl bg-brand-400/15">
                  <Mic size={20} strokeWidth={1.75} color="#D9480F" />
                </div>
                <div className="text-[13px] font-bold text-text-high">{t('voice.addVoice.ownVoice')}</div>
                <div className="text-[11px] text-text-muted">{t('voice.addVoice.fileHint')}</div>
                <details open className="mt-1 w-full rounded-lg border border-line bg-fill-subtle p-3 text-left">
                  <summary className="mb-1.5 cursor-pointer text-[10px] font-bold uppercase tracking-[0.06em] text-brand-200">
                    {t('voice.addVoice.recordingScript', { language: languageName(cloneLanguage) })}
                  </summary>
                  <blockquote className="text-[11px] leading-[1.65] text-text-mid">
                    &ldquo;{RECORDING_SCRIPTS[cloneLanguage] ?? RECORDING_SCRIPTS.en}&rdquo;
                  </blockquote>
                  <div className="mt-2 text-[10px] text-text-muted">{t('voice.addVoice.recordingTip')}</div>
                </details>
                <div className="mt-1 flex flex-wrap items-center justify-center gap-2">
                  <Select
                    value={cloneLanguage}
                    onChange={setCloneLanguage}
                    disabled={busy || recording}
                    options={CLONE_LANGUAGES.map((code) => ({ value: code, label: languageName(code) }))}
                    ariaLabel={t('voice.library.voiceLanguage')}
                    className="w-[130px]"
                  />
                  {cloneProviders.length > 1 && (
                    <Select
                      value={cloneProvider || cloneProviders[0]}
                      onChange={setCloneProvider}
                      disabled={busy || recording}
                      options={cloneProviders.map((p) => ({ value: p, label: CLONE_PROVIDER_NAMES[p] ?? p }))}
                      ariaLabel={t('voice.library.cloneWith')}
                      className="w-[170px]"
                    />
                  )}
                  <Select
                    value={clonePersona}
                    onChange={setClonePersona}
                    disabled={busy || recording}
                    options={PERSONAS.map((p) => ({ value: p.id, label: t(p.lbl) }))}
                    ariaLabel={t('voice.library.voicePersona')}
                    className="w-[170px]"
                  />
                  <button
                    disabled={busy || recording}
                    onClick={() => fileRef.current?.click()}
                    className="rounded-md border border-brand-400/20 bg-brand-400/[0.12] px-3 py-1.5 text-[11px] font-semibold text-brand-200 disabled:cursor-not-allowed disabled:opacity-50"
                  >
                    {busy ? t('voice.addVoice.processing') : t('voice.addVoice.uploadFile')}
                  </button>
                  <button
                    disabled={busy}
                    onClick={() => void toggleRecord()}
                    className="rounded-md border border-brand-400/20 bg-brand-400/[0.12] px-3 py-1.5 text-[11px] font-semibold text-brand-200 disabled:cursor-not-allowed disabled:opacity-50"
                  >
                    {recording ? t('voice.addVoice.stopRecording') : t('voice.addVoice.record')}
                  </button>
                </div>
                <input ref={fileRef} type="file" accept="audio/*" multiple hidden onChange={onFile} />
                <div className="mt-1 text-[10px] text-text-muted">{t('voice.addVoice.consent')}</div>
                <Advanced
                  className="mt-3 w-full text-left"
                  label={t('voice.addVoice.importTitle')}
                  forceOpen={!!importId.trim() || !!importName.trim()}
                >
                  <div className="flex w-full flex-wrap items-center gap-2">
                    <input
                      value={importId}
                      onChange={(e) => setImportId(e.target.value)}
                      placeholder={t('voice.addVoice.voiceIdPlaceholder')}
                      aria-label={t('voice.addVoice.voiceIdPlaceholder')}
                      className="flex-[1_1_160px] rounded-md border border-line-strong bg-well px-2.5 py-1.5 text-[11px] text-text-high outline-none"
                    />
                    <input
                      value={importName}
                      onChange={(e) => setImportName(e.target.value)}
                      placeholder={t('voice.addVoice.namePlaceholder')}
                      aria-label={t('voice.addVoice.namePlaceholder')}
                      className="flex-[1_1_120px] rounded-md border border-line-strong bg-well px-2.5 py-1.5 text-[11px] text-text-high outline-none"
                    />
                    <button
                      disabled={busy || !importId.trim()}
                      onClick={() => void submitImport()}
                      className="rounded-md border border-brand-400/20 bg-brand-400/[0.12] px-3 py-1.5 text-[11px] font-semibold text-brand-200 disabled:cursor-not-allowed disabled:opacity-50"
                    >
                      {busy ? t('voice.addVoice.processing') : t('voice.addVoice.import')}
                    </button>
                  </div>
                </Advanced>
                {cloneErr && <div className="mt-1 text-[10px] font-semibold text-red-300">{cloneErr}</div>}
              </div>
            </div>
          </div>
        )}
        <div data-tour="voice-filters" className="mb-3 flex flex-wrap items-center gap-2">
          <VoiceFilterSelect
            value={genderFilter}
            onChange={setGenderFilter}
            opts={voiceFilterOpts(voices, 'gender', t('voice.filters.allGenders'))}
            ariaLabel={t('voice.filters.gender')}
          />
          <VoiceFilterSelect
            value={languageFilter}
            onChange={setLanguageFilter}
            opts={voiceFilterOpts(voices, 'language', t('voice.filters.allLanguages'), languageLabels)}
            ariaLabel={t('common.fields.language')}
          />
          <VoiceFilterSelect
            value={accentFilter}
            onChange={setAccentFilter}
            opts={voiceFilterOpts(voices, 'accent', t('voice.filters.allAccents'))}
            ariaLabel={t('voice.filters.accent')}
          />
          <VoiceFilterSelect
            value={sourceFilter}
            onChange={setSourceFilter}
            opts={voiceFilterOpts(voices, 'provider', t('voice.filters.allSources'), SOURCE_LABELS)}
            ariaLabel={t('voice.filters.source')}
          />
          <VoiceFilterSelect
            value={personaFilter}
            onChange={setPersonaFilter}
            opts={voiceFilterOpts(voices, 'persona', t('voice.filters.allPersonas'), personaLabels)}
            ariaLabel={t('voice.filters.persona')}
          />
          {hasVoiceFilters && (
            <button
              onClick={clearVoiceFilters}
              className="rounded-[7px] border border-line-strong bg-well px-2.5 py-1.5 text-[12px] font-semibold text-text-muted"
            >
              {t('common.actions.clear')}
            </button>
          )}
          <span className="ml-auto text-[12px] font-semibold text-text-muted">
            {t('voice.filters.shownOf', { shown: filteredVoices.length, total: voices.length })}
          </span>
        </div>
        <div data-tour="voice-grid" className="grid grid-cols-1 gap-2.5 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4">
          {filteredVoices.map((v) => {
            const sel = selectedVoice === v.id
            const ready = v.status === 'ready'
            const isGemini = v.id === 'puck'
            const editing = editingId === v.id
            return (
              <div
                key={v.id}
                onClick={() => !editing && ready && void pickVoice(v.id)}
                className="relative flex h-full flex-col rounded-[11px] p-3.5 transition-all"
                style={{
                  border: `1.5px solid ${sel ? tint('rgb(var(--c-brand))', 0.5) : 'var(--line)'}`,
                  background: sel ? tint('rgb(var(--c-brand))', 0.08) : 'var(--fill-subtle)',
                  cursor: editing ? 'default' : ready ? 'pointer' : 'default',
                  opacity: ready ? 1 : 0.7,
                }}
              >
                {!editing && (
                  <div className="absolute right-2.5 top-2.5 flex items-center gap-1">
                    {/* the DEFAULT badge follows the organization's saved voice (the
                        preset's own "Default" tag only named the factory default) */}
                    {sel || (v.tag && v.tag !== 'Default') ? (
                      <span className="rounded-full bg-green/15 px-1.5 py-px text-[9px] font-bold uppercase text-green-300">
                        {sel ? t('voice.chatPersona.default') : v.tag}
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
                          aria-label={t('voice.library.editVoice')}
                        >
                          <Pencil size={12} strokeWidth={2} color="#8A8177" />
                        </button>
                        <button
                          onClick={(e) => {
                            e.stopPropagation()
                            void handleDeleteVoice(v)
                          }}
                          className="flex h-6 w-6 items-center justify-center rounded-full bg-fill-hover"
                          aria-label={t('voice.library.deleteVoice')}
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
                    style={{ background: v.custom ? 'rgba(122,78,140,0.18)' : tint('rgb(var(--c-brand))', 0.15) }}
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
                        placeholder={t('common.fields.name')}
                        aria-label={t('common.fields.name')}
                      />
                    ) : (
                      <>
                        <div className="text-[13px] font-bold leading-snug text-text-high">{v.name}</div>
                        <div className="truncate text-[10px] text-text-muted">
                          {[titleCase(v.gender), titleCase(v.accent)].filter(Boolean).join(' · ') || '—'}
                        </div>
                        <div className="truncate text-[10px] text-text-muted">
                          {languageName(v.language)} · {personaName(v.persona)}
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
                        placeholder={t('voice.filters.gender')}
                        aria-label={t('voice.filters.gender')}
                      />
                      <input
                        value={editAccent}
                        onChange={(e) => setEditAccent(e.target.value)}
                        className="w-1/2 rounded-md border border-line-strong bg-well px-2 py-1 text-[11px] text-text-high outline-none"
                        placeholder={t('voice.filters.accent')}
                        aria-label={t('voice.filters.accent')}
                      />
                    </div>
                    <Select
                      value={editLanguage}
                      onChange={setEditLanguage}
                      options={EDIT_LANGUAGES.map((code) => ({ value: code, label: languageName(code) }))}
                      ariaLabel={t('voice.library.voiceLanguage')}
                    />
                    <Select
                      value={editPersona}
                      onChange={setEditPersona}
                      options={PERSONAS.map((p) => ({ value: p.id, label: t(p.lbl) }))}
                      ariaLabel={t('voice.library.voicePersona')}
                    />
                    <div className="mt-1 flex gap-1.5">
                      <button
                        disabled={voiceActionBusy}
                        onClick={() => void saveEdit(v.id)}
                        className="flex-1 rounded-md border border-brand-400/20 bg-brand-400/[0.12] px-2 py-1 text-[11px] font-semibold text-brand-200 disabled:cursor-not-allowed disabled:opacity-50"
                      >
                        {voiceActionBusy ? t('common.status.saving') : t('common.actions.save')}
                      </button>
                      <button
                        disabled={voiceActionBusy}
                        onClick={() => setEditingId(null)}
                        className="flex-1 rounded-md border border-line-strong bg-well px-2 py-1 text-[11px] font-semibold text-text-muted disabled:cursor-not-allowed disabled:opacity-50"
                      >
                        {t('common.actions.cancel')}
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
                                  setPreviewErrMsg(errMessage(t, err))
                                })
                                .finally(() => setLoadingId((id) => (id === v.id ? null : id)))
                            }
                          }}
                          className="flex h-6 w-6 items-center justify-center rounded-full bg-brand-400/15 disabled:cursor-not-allowed"
                          aria-label={
                            loadingId === v.id
                              ? t('voice.library.loadingPreview')
                              : playingId === v.id
                                ? t('voice.library.stopPreview')
                                : t('voice.library.playPreview')
                          }
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
                        <span className="text-[9px] font-semibold text-text-muted">{t('voice.library.geminiVoice')}</span>
                      ) : null}
                      {ready ? (
                        <span className="text-[10px] font-bold text-green-300">{t('voice.library.ready')}</span>
                      ) : (
                        <span className="inline-flex items-center gap-1.5 text-[10px] font-bold text-amber-300" style={{ color: 'var(--amber-ink)' }}>
                          <Loader2 size={11} strokeWidth={2.5} color="#B7791F" className="animate-spin" />
                          {t('voice.library.processing')}
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
            {voices.length === 0 ? t('voice.library.noVoices') : t('voice.library.noMatches')}
            {hasVoiceFilters && (
              <button onClick={clearVoiceFilters} className="ml-1.5 font-semibold text-brand-300 underline">
                {t('voice.filters.clearFilters')}
              </button>
            )}
          </div>
        )}
        <div className="mt-4 flex items-center gap-1.5 text-[11px] text-text-muted">
          <RefreshCw size={12} strokeWidth={1.75} className="shrink-0 text-text-muted" />
          {t('voice.library.assignable')}
        </div>
        {voiceSaved && (
          <p role="status" className={`mt-1.5 text-[11.5px] ${voiceSaved === 'saved' ? 'text-[var(--green-ink)]' : 'text-text-muted'}`}>
            {voiceSaved === 'saved' ? t('voice.library.savedForAll') : t('voice.library.savedLocal')}
          </p>
        )}
      </div>

      {/* Pronunciation lives with the voices it shapes (ElevenLabs respellings
          applied to every TTS call) — moved here from Settings. */}
      <div id="pronunciation" data-tour="voice-pronunciation" className="rounded-[14px] border border-line bg-card p-4">
        <SectionLabel>{t('voice.pronunciation.title')}</SectionLabel>
        <PronunciationSettings />
      </div>
    </div>
  )
}
