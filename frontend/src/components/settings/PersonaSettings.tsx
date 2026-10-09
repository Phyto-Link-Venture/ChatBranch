'use client'

import { useCallback, useEffect, useState } from 'react'
import { AlertCircle, CheckCircle, Loader2, Lock, RefreshCw, RotateCcw, Save } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { useT, type TFunction } from '@/i18n'
import { roleHeaders, getRole } from '@/lib/role'
import { expireSessionOn401 } from '@/lib/auth'
import { useEntity, useEntityQuery } from '@/lib/EntityContext'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

interface PersonaPayload {
  persona: string
  is_default: boolean
  default: string
  /** Scope this persona came from: "entity" = this outlet overrides the
   *  organization's, "global" = it inherits, "" = the built-in default. */
  scope?: 'entity' | 'global' | ''
  entity?: string | null
}

/** Our own failure words are codes, worded at render; a server `detail` is shown as sent. */
const SESSION_EXPIRED = 'session-expired'
const NOT_AVAILABLE = 'persona-api-missing'

async function personaApi<T>(init?: RequestInit, entityQ = ''): Promise<T> {
  const res = await fetch(`${API}/aicore/persona${entityQ && `?${entityQ}`}`, {
    headers: { 'Content-Type': 'application/json', ...roleHeaders() },
    ...init,
  })
  if (expireSessionOn401(res)) throw new Error(SESSION_EXPIRED)
  const data = await res.json().catch(() => ({}))
  if (!res.ok) {
    throw new Error((data as { detail?: string }).detail || (res.status === 404 ? NOT_AVAILABLE : `HTTP ${res.status}`))
  }
  return data as T
}

function errWords(t: TFunction, msg: string): string {
  if (msg === SESSION_EXPIRED) return t('common.errors.sessionExpired')
  if (msg === NOT_AVAILABLE) return t('voice.chatPersona.apiMissing')
  return msg
}

/** Settings → Chat Persona: the AI agent's permanent operating
 *  instructions (personality, tone, conversation style,
 *  escalation rules). Superadmin edits via PUT /api/aicore/persona; an empty
 *  string resets to the built-in default. Everyone else gets a read-only view.
 *  Compliance hard rules are enforced server-side and cannot be overridden. */
export function PersonaSettings() {
  const t = useT()
  // Entity scope: with an outlet selected this edits THAT outlet's persona;
  // with "All entities" it edits the organization persona every outlet
  // inherits. Clearing an outlet's text reverts it to inherited.
  const entityQ = useEntityQuery()
  const { activeEntity } = useEntity()
  const [scope, setScope] = useState<'entity' | 'global' | ''>('')
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
      const data = await personaApi<PersonaPayload>(undefined, entityQ)
      const persona = typeof data.persona === 'string' ? data.persona : ''
      setServerPersona(persona)
      setText(persona)
      setIsDefault(!!data.is_default)
      setScope(data.scope ?? '')
    } catch (e) {
      setLoadError(e instanceof Error ? e.message : String(e))
    } finally {
      setLoading(false)
    }
  }, [entityQ])

  useEffect(() => {
    load()
  }, [load])

  const dirty = text !== serverPersona

  const put = async (value: string, successMsg: string) => {
    setSaving(true)
    setError(null)
    setNotice(null)
    try {
      await personaApi({ method: 'PUT', body: JSON.stringify({ persona: value }) }, entityQ)
      setNotice(successMsg)
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setSaving(false)
    }
  }

  const save = () => put(text, t('voice.chatPersona.saved'))

  const resetToDefault = () => {
    if (!window.confirm(t('voice.chatPersona.confirmReset'))) return
    put('', t('voice.chatPersona.resetDone'))
  }

  return (
    <div className="pt-3">
      <div className="mb-1.5 flex items-center gap-2">
        <span className="text-[13px] font-bold text-text-high">{t('voice.chatPersona.title')}</span>
        {!loading && !loadError && (
          <span
            className="rounded-full px-2 py-px text-[10px] font-bold"
            style={
              isDefault
                ? { color: 'var(--text-muted)', background: 'var(--fill)' }
                : { color: 'var(--brand-ink)', background: 'rgba(255,90,31,0.1)' }
            }
          >
            {isDefault ? t('voice.chatPersona.default') : t('voice.chatPersona.customized')}
          </span>
        )}
      </div>
      <p className="mb-4 max-w-[640px] text-[11.5px] leading-[1.6] text-text-muted">{t('voice.chatPersona.intro')}</p>

      {/* Scope banner — editing one outlet's persona vs the organization's. */}
      <div className="mb-4 flex flex-wrap items-center gap-x-3 gap-y-2 rounded-lg border border-line bg-fill-subtle px-3 py-2.5">
        <span className="text-[12px] font-semibold text-text-high">
          {activeEntity
            ? t('voice.chatPersona.editingEntity', { name: activeEntity.name })
            : t('voice.chatPersona.editingOrg')}
        </span>
        {activeEntity ? (
          <span className="text-[11px] text-text-muted">
            {scope === 'entity' ? t('voice.chatPersona.entityOwn') : t('voice.chatPersona.entityInherits')}
          </span>
        ) : (
          <span className="text-[11px] text-text-muted">{t('voice.chatPersona.orgHint')}</span>
        )}
      </div>

      {error && (
        <div
          className="mb-3 flex items-start gap-2 rounded-lg border border-red-500/20 bg-red-500/10 px-3 py-2 text-[12px]"
          style={{ color: 'var(--red-ink)' }}
        >
          <AlertCircle size={13} className="mt-0.5 shrink-0" aria-hidden /> {errWords(t, error)}
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
          <Loader2 size={14} className="animate-spin" aria-hidden /> {t('voice.chatPersona.loading')}
        </div>
      ) : loadError ? (
        <div>
          <div
            className="mb-3 flex items-start gap-2 rounded-lg border border-red-500/20 bg-red-500/10 px-3 py-2 text-[12px]"
            style={{ color: 'var(--red-ink)' }}
          >
            <AlertCircle size={13} className="mt-0.5 shrink-0" aria-hidden /> {errWords(t, loadError)}
          </div>
          <Button variant="ghost" onClick={load}>
            <RefreshCw size={13} aria-hidden /> {t('common.actions.retry')}
          </Button>
        </div>
      ) : (
        <div>
          {!isSuper && (
            <div className="mb-2 flex items-center gap-1.5 text-[11px] font-semibold text-text-muted">
              <Lock size={11} aria-hidden /> {t('voice.chatPersona.readOnly')}
            </div>
          )}
          <textarea
            value={text}
            onChange={(e) => setText(e.target.value)}
            readOnly={!isSuper}
            rows={16}
            spellCheck={false}
            aria-label={t('voice.chatPersona.instructionsAria')}
            placeholder={t('voice.chatPersona.placeholder')}
            className={`w-full resize-y rounded-lg border border-line-strong px-3 py-2.5 font-mono text-[12px] leading-[1.65] text-text-high outline-none transition-colors ${
              isSuper ? 'bg-card focus:border-brand-400' : 'cursor-default bg-fill-subtle'
            }`}
          />
          <div className="mt-1 flex items-center gap-2 text-[10.5px] text-text-muted">
            <span>{t('library.editor.characters', { count: text.length })}</span>
            {isSuper && dirty && (
              <span className="font-semibold" style={{ color: 'var(--amber-ink)' }}>
                {t('voice.chatPersona.unsaved')}
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
                {saving ? t('common.status.saving') : t('voice.chatPersona.save')}
              </Button>
              <Button variant="ghost" onClick={resetToDefault} disabled={saving || isDefault}>
                <RotateCcw size={13} aria-hidden /> {t('voice.chatPersona.reset')}
              </Button>
            </div>
          )}
        </div>
      )}
    </div>
  )
}
