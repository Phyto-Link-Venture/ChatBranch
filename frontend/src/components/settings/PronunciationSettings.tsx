'use client'

import { useEffect, useState } from 'react'
import { Loader2, Plus, Trash2, AlertCircle, Volume2 } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Trans, useT } from '@/i18n'
import { authHeaders } from '@/lib/auth'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

type Term = { term: string; alias: string }

async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}/${path}`, {
    // the GET is anonymous, the save is admin+ — so send the session either way
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
    ...init,
  })
  const data = await res.json().catch(() => ({}))
  if (!res.ok) throw new Error((data as { detail?: string }).detail || `HTTP ${res.status}`)
  return data as T
}

/**
 * Term -> respelling overrides, synced to an ElevenLabs pronunciation
 * dictionary and applied to every ElevenLabs TTS call. Not real IPA --
 * eleven_multilingual_v2 (needed for ms/zh) only supports "alias" rules, not
 * phoneme/IPA rules, so the fix is a plain phonetic respelling instead.
 * OpenAI voices have no pronunciation-control API, so these terms never
 * affect marin/cedar/alloy.
 */
export function PronunciationSettings() {
  const t = useT()
  const [terms, setTerms] = useState<Term[]>([])
  const [loaded, setLoaded] = useState(false)
  const [busy, setBusy] = useState(false)
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)

  useEffect(() => {
    api<{ terms: Term[] }>('pronunciation')
      .then((d) => setTerms(d.terms))
      .catch(() => {})
      .finally(() => setLoaded(true))
  }, [])

  const setTerm = (i: number, patch: Partial<Term>) =>
    setTerms((prev) => prev.map((x, idx) => (idx === i ? { ...x, ...patch } : x)))
  const addTerm = () => setTerms((prev) => [...prev, { term: '', alias: '' }])
  const removeTerm = (i: number) => setTerms((prev) => prev.filter((_, idx) => idx !== i))

  // Local browser TTS (Web Speech API) -- no ElevenLabs/OpenAI call. Lets you
  // sanity-check either box's plain text with your browser's own voice before
  // saving; the real ElevenLabs pronunciation only comes from a voice's
  // Preview button once these terms are saved & synced.
  const speak = (text: string) => {
    if (!text.trim() || typeof window === 'undefined' || !window.speechSynthesis) return
    window.speechSynthesis.cancel()
    window.speechSynthesis.speak(new SpeechSynthesisUtterance(text.trim()))
  }

  const save = async () => {
    setBusy(true)
    setMsg(null)
    try {
      const clean = terms.filter((x) => x.term.trim() && x.alias.trim())
      const d = await api<{ terms: Term[]; synced?: boolean }>('pronunciation', {
        method: 'POST',
        body: JSON.stringify({ terms: clean }),
      })
      setTerms(d.terms)
      // the API says whether the ElevenLabs dictionary was actually updated
      setMsg({ ok: true, text: d.synced === false ? t('voice.pronunciation.savedNotSynced') : t('voice.pronunciation.savedSynced') })
    } catch (e) {
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    } finally {
      setBusy(false)
    }
  }

  if (!loaded) {
    return <div className="py-6 text-[12px] text-text-muted">{t('common.status.loading')}</div>
  }

  return (
    <div className="flex flex-col gap-4 pt-3.5">
      <div className="flex items-start gap-2 rounded-lg border border-line bg-fill-subtle px-3 py-2.5 text-[11px] leading-relaxed text-text-muted">
        <AlertCircle size={14} className="mt-px shrink-0 text-amber-300" aria-hidden />
        <span>
          <Trans
            k="voice.pronunciation.intro"
            values={{
              elevenOnly: <strong className="text-text-high">{t('voice.pronunciation.elevenOnly')}</strong>,
              respelling: <strong className="text-text-high">{t('voice.pronunciation.respelling')}</strong>,
              example: <span className="text-text-high">Doo-it Ghee-nee</span>,
              speaker: <Volume2 size={11} className="inline align-text-bottom" aria-hidden />,
            }}
          />
        </span>
      </div>

      <div className="flex flex-col gap-2">
        {terms.map((x, i) => (
          <div key={i} className="flex items-center gap-2">
            <input
              value={x.term}
              onChange={(e) => setTerm(i, { term: e.target.value })}
              placeholder={t('voice.pronunciation.termPlaceholder')}
              aria-label={t('voice.pronunciation.term')}
              className="min-w-0 flex-1 rounded-lg border border-line-strong bg-card px-3 py-2 text-[12.5px] text-text-primary outline-none focus:border-brand-400"
            />
            <input
              value={x.alias}
              onChange={(e) => setTerm(i, { alias: e.target.value })}
              placeholder={t('voice.pronunciation.aliasPlaceholder')}
              aria-label={t('voice.pronunciation.alias')}
              className="min-w-0 flex-1 rounded-lg border border-line-strong bg-card px-3 py-2 text-[12.5px] text-text-primary outline-none focus:border-brand-400"
            />
            <button
              onClick={() => speak(x.term)}
              disabled={!x.term.trim()}
              aria-label={t('voice.pronunciation.speakTerm')}
              title={t('voice.pronunciation.speakTermTitle')}
              className="flex h-8 w-8 shrink-0 items-center justify-center rounded-lg border border-line bg-fill text-text-muted disabled:cursor-not-allowed disabled:opacity-40"
            >
              <Volume2 size={13} aria-hidden />
            </button>
            <button
              onClick={() => speak(x.alias)}
              disabled={!x.alias.trim()}
              aria-label={t('voice.pronunciation.speakAlias')}
              title={t('voice.pronunciation.speakAliasTitle')}
              className="flex h-8 w-8 shrink-0 items-center justify-center rounded-lg border border-line bg-fill text-text-muted disabled:cursor-not-allowed disabled:opacity-40"
            >
              <Volume2 size={13} className="opacity-60" aria-hidden />
            </button>
            <button
              onClick={() => removeTerm(i)}
              aria-label={t('voice.pronunciation.removeTerm')}
              className="flex h-8 w-8 shrink-0 items-center justify-center rounded-lg border border-line bg-fill text-text-muted"
            >
              <Trash2 size={13} aria-hidden />
            </button>
          </div>
        ))}
        {terms.length === 0 && <div className="text-[12px] text-text-muted">{t('voice.pronunciation.empty')}</div>}
      </div>

      <button
        onClick={addTerm}
        className="flex w-fit items-center gap-1.5 rounded-md border border-brand-400/20 bg-brand-400/[0.12] px-2.5 py-1.5 text-[11px] font-semibold text-brand-200"
      >
        <Plus size={13} aria-hidden /> {t('voice.pronunciation.addTerm')}
      </button>

      <div className="flex flex-wrap items-center gap-2">
        <Button variant="primary" onClick={save} disabled={busy}>
          {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : null} {t('voice.pronunciation.saveSync')}
        </Button>
        {msg && (
          <span className={`text-[12px] font-semibold ${msg.ok ? 'text-green-300' : 'text-red-300'}`}>
            {msg.text}
          </span>
        )}
      </div>
    </div>
  )
}
