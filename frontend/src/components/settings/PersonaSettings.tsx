'use client'

import { useCallback, useEffect, useState } from 'react'
import { AlertCircle, CheckCircle, Loader2, Lock, RefreshCw, RotateCcw, Save } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { roleHeaders, getRole } from '@/lib/role'
import { expireSessionOn401 } from '@/lib/auth'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8000/api'

interface PersonaPayload {
  persona: string
  is_default: boolean
  default: string
}

async function personaApi<T>(init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}/aicore/persona`, {
    headers: { 'Content-Type': 'application/json', ...roleHeaders() },
    ...init,
  })
  if (expireSessionOn401(res)) throw new Error('Session expired — redirecting to login…')
  const data = await res.json().catch(() => ({}))
  if (!res.ok) {
    throw new Error(
      (data as { detail?: string }).detail ||
        (res.status === 404 ? 'Persona API not available on this backend yet.' : `HTTP ${res.status}`),
    )
  }
  return data as T
}

/** Settings → AI Persona: the AI agent's permanent operating
 *  instructions (personality, tone, conversation style,
 *  escalation rules). Superadmin edits via PUT /api/aicore/persona; an empty
 *  string resets to the built-in default. Everyone else gets a read-only view.
 *  Compliance hard rules are enforced server-side and cannot be overridden. */
export function PersonaSettings() {
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [serverPersona, setServerPersona] = useState('')
  const [text, setText] = useState('')
  const [isDefault, setIsDefault] = useState(true)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [isSuper, setIsSuper] = useState(false)

  // role read after mount (localStorage)
  useEffect(() => setIsSuper(getRole() === 'superadmin'), [])

  const load = useCallback(async () => {
    setLoading(true)
    setLoadError(null)
    try {
      const data = await personaApi<PersonaPayload>()
      const persona = typeof data.persona === 'string' ? data.persona : ''
      setServerPersona(persona)
      setText(persona)
      setIsDefault(!!data.is_default)
    } catch (e) {
      setLoadError(e instanceof Error ? e.message : String(e))
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    load()
  }, [load])

  const dirty = text !== serverPersona

  const put = async (value: string, successMsg: string) => {
    setSaving(true)
    setError(null)
    setNotice(null)
    try {
      await personaApi({ method: 'PUT', body: JSON.stringify({ persona: value }) })
      setNotice(successMsg)
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setSaving(false)
    }
  }

  const save = () => put(text, 'Persona saved — new calls and chats use it immediately.')

  const resetToDefault = () => {
    if (
      !window.confirm(
        'Reset the persona to the built-in default? Your customized instructions will be discarded.',
      )
    )
      return
    put('', 'Persona reset to the built-in default.')
  }

  return (
    <div className="pt-3">
      <div className="mb-1.5 flex items-center gap-2">
        <span className="text-[13px] font-bold text-text-high">AI Persona</span>
        {!loading && !loadError && (
          <span
            className="rounded-full px-2 py-px text-[10px] font-bold"
            style={
              isDefault
                ? { color: 'var(--text-muted)', background: 'var(--fill)' }
                : { color: 'var(--brand-ink)', background: 'rgba(59,130,246,0.1)' }
            }
          >
            {isDefault ? 'Default' : 'Customized'}
          </span>
        )}
      </div>
      <p className="mb-4 max-w-[640px] text-[11.5px] leading-[1.6] text-text-muted">
        These are the AI agent&apos;s permanent operating instructions — its personality, tone,
        conversation style, and escalation rules — applied to every call and chat.
        Compliance hard rules are enforced server-side and cannot be overridden here.
      </p>

      {error && (
        <div
          className="mb-3 flex items-start gap-2 rounded-lg border border-red-500/20 bg-red-500/10 px-3 py-2 text-[12px]"
          style={{ color: 'var(--red-ink)' }}
        >
          <AlertCircle size={13} className="mt-0.5 shrink-0" aria-hidden /> {error}
        </div>
      )}
      {notice && (
        <div
          className="mb-3 flex items-start gap-2 rounded-lg border border-brand-400/20 bg-brand-400/10 px-3 py-2 text-[12px]"
          style={{ color: 'var(--green-ink)' }}
        >
          <CheckCircle size={13} className="mt-0.5 shrink-0" aria-hidden /> {notice}
        </div>
      )}

      {loading ? (
        <div className="flex items-center gap-2 py-6 text-[12px] text-text-muted">
          <Loader2 size={14} className="animate-spin" aria-hidden /> Loading persona…
        </div>
      ) : loadError ? (
        <div>
          <div
            className="mb-3 flex items-start gap-2 rounded-lg border border-red-500/20 bg-red-500/10 px-3 py-2 text-[12px]"
            style={{ color: 'var(--red-ink)' }}
          >
            <AlertCircle size={13} className="mt-0.5 shrink-0" aria-hidden /> {loadError}
          </div>
          <Button variant="ghost" onClick={load}>
            <RefreshCw size={13} aria-hidden /> Retry
          </Button>
        </div>
      ) : (
        <div>
          {!isSuper && (
            <div className="mb-2 flex items-center gap-1.5 text-[11px] font-semibold text-text-muted">
              <Lock size={11} aria-hidden /> Read-only — only a superadmin can edit the persona.
            </div>
          )}
          <textarea
            value={text}
            onChange={(e) => setText(e.target.value)}
            readOnly={!isSuper}
            rows={16}
            spellCheck={false}
            aria-label="AI persona instructions"
            placeholder="Persona instructions…"
            className={`w-full resize-y rounded-lg border border-line-strong px-3 py-2.5 font-mono text-[12px] leading-[1.65] text-text-high outline-none transition-colors ${
              isSuper ? 'bg-card focus:border-brand-400' : 'cursor-default bg-fill-subtle'
            }`}
          />
          <div className="mt-1 flex items-center gap-2 text-[10.5px] text-text-muted">
            <span>{text.length.toLocaleString()} characters</span>
            {isSuper && dirty && (
              <span className="font-semibold" style={{ color: 'var(--amber-ink)' }}>
                Unsaved changes
              </span>
            )}
          </div>

          {isSuper && (
            <div className="mt-3 flex items-center gap-2">
              <Button onClick={save} disabled={!dirty || saving}>
                {saving ? (
                  <Loader2 size={13} className="animate-spin" aria-hidden />
                ) : (
                  <Save size={13} aria-hidden />
                )}
                {saving ? 'Saving…' : 'Save persona'}
              </Button>
              <Button variant="ghost" onClick={resetToDefault} disabled={saving || isDefault}>
                <RotateCcw size={13} aria-hidden /> Reset to default
              </Button>
            </div>
          )}
        </div>
      )}
    </div>
  )
}
