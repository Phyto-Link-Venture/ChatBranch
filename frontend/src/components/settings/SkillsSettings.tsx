'use client'

import { useCallback, useEffect, useRef, useState } from 'react'
import { AlertCircle, BookOpen, CheckCircle, Loader2, Lock, Plus, RefreshCw, Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { roleHeaders } from '@/lib/role'
import { useRole } from '@/lib/useRole'
import { expireSessionOn401 } from '@/lib/auth'
import { DEFAULT_AGENT_SKILLS } from '@/lib/flow/defaultAgentSkills'
import { useSettingsDirty } from './SettingsDirtyContext'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

interface Skill {
  id: string
  name: string
  useWhen: string
  instructions: string
}

async function api<T>(init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}/aicore/skills`, {
    headers: { 'Content-Type': 'application/json', ...roleHeaders() },
    ...init,
  })
  if (expireSessionOn401(res)) throw new Error('Session expired — redirecting to login…')
  const data = await res.json().catch(() => ({}))
  if (!res.ok) throw new Error((data as { detail?: string }).detail || `HTTP ${res.status}`)
  return data as T
}

const inputCls =
  'w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[12.5px] text-text-primary outline-none transition-colors focus:border-brand-400'

const freshId = () => `as-${Math.random().toString(36).slice(2, 9)}`

/**
 * Settings → AI Skills. `GET/PUT /api/aicore/skills` is the SOLE writer of
 * crm.AgentSkill; the flow-builder library sync reads them read-only. Before
 * Phase 8 the builder owned them and its PUT pruned rows missing from the
 * browser's copy — so a stale builder tab could silently delete a skill an
 * operator had just typed here.
 *
 * Skills join every prompt as a `SKILLS` section (`use_when` selects,
 * `instructions` is the body), so they steer chat on all six channels AND
 * live phone calls — not just flow steps.
 */
export function SkillsSettings() {
  const { role, ready } = useRole()
  const canManage = role === 'admin' || role === 'superadmin'

  const [skills, setSkills] = useState<Skill[] | null>(null)
  const [serverCopy, setServerCopy] = useState<Skill[]>([])
  const [loadError, setLoadError] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [openId, setOpenId] = useState<string | null>(null)

  const { setDirty, register } = useSettingsDirty()
  const draftRef = useRef<Skill[]>([])
  draftRef.current = skills ?? []

  const load = useCallback(async () => {
    try {
      const d = await api<{ skills: Skill[] }>()
      setSkills(d.skills)
      setServerCopy(d.skills)
      setLoadError(null)
    } catch (e) {
      setSkills(null)
      setLoadError(e instanceof Error ? e.message : String(e))
    }
  }, [])

  useEffect(() => {
    load()
  }, [load])

  const save = useCallback(async () => {
    const d = await api<{ skills: Skill[] }>({
      method: 'PUT',
      body: JSON.stringify({ skills: draftRef.current }),
    })
    setSkills(d.skills)
    setServerCopy(d.skills)
    setNotice('Skills saved — chats and calls use them immediately.')
  }, [])

  useEffect(() => {
    register({ save, discard: () => setSkills(serverCopy) })
    return () => register(null)
  }, [register, save, serverCopy])

  const edit = (id: string, patch: Partial<Skill>) => {
    setSkills((list) => (list ?? []).map((s) => (s.id === id ? { ...s, ...patch } : s)))
    setDirty(true)
    setNotice(null)
  }

  const add = () => {
    const id = freshId()
    setSkills((list) => [...(list ?? []), { id, name: 'New skill', useWhen: '', instructions: '' }])
    setOpenId(id)
    setDirty(true)
  }

  const remove = (id: string) => {
    const target = (skills ?? []).find((s) => s.id === id)
    if (!window.confirm(`Delete skill "${target?.name ?? ''}"? Flow steps referencing it lose the link.`)) return
    setSkills((list) => (list ?? []).filter((s) => s.id !== id))
    setDirty(true)
  }

  const loadStarters = () => {
    setSkills(
      DEFAULT_AGENT_SKILLS.map((s) => ({
        id: s.id,
        name: s.name,
        useWhen: s.useWhen ?? '',
        instructions: s.instructions ?? '',
      })),
    )
    setDirty(true)
    setNotice('Starter skills staged — press Save changes to keep them.')
  }

  if (!ready || (skills === null && !loadError)) {
    return (
      <div className="flex items-center gap-1.5 py-6 text-[12px] text-text-muted">
        <Loader2 size={13} className="animate-spin" aria-hidden /> Loading skills…
      </div>
    )
  }

  if (skills === null) {
    return (
      <div className="py-6 text-[12px]">
        <div
          className="flex items-start gap-2 rounded-lg border border-red-500/20 bg-red-500/10 px-3 py-2"
          style={{ color: 'var(--red-ink)' }}
        >
          <AlertCircle size={13} className="mt-0.5 shrink-0" aria-hidden />
          <span>Couldn&apos;t load skills — {loadError}</span>
        </div>
        <Button variant="ghost" onClick={load} style={{ marginTop: 10 }}>
          <RefreshCw size={13} aria-hidden /> Retry
        </Button>
      </div>
    )
  }

  return (
    <div className="flex flex-col gap-4 pt-3.5">
      <div className="flex items-start gap-2">
        <p className="min-w-0 flex-1 max-w-[720px] text-[11.5px] leading-relaxed text-text-mid">
          A skill is one instruction module: <span className="font-bold text-text-high">use when</span> selects it,{' '}
          <span className="font-bold text-text-high">instructions</span> is what the AI then does. They ride every
          reply on every channel and every voice call — not just flow steps — so an operator can add &ldquo;when
          someone asks about bulk pricing, collect quantity and company name, then hand to sales&rdquo; without
          touching the persona. The flow builder reads this list for its step picker; this page is the only place
          that writes it.
        </p>
        {canManage && (
          <div className="shrink-0">
            <Button onClick={add}>
              <Plus size={13} aria-hidden /> New skill
            </Button>
          </div>
        )}
      </div>

      {!canManage && (
        <div className="flex items-center gap-1.5 text-[11px] font-semibold text-text-muted">
          <Lock size={11} aria-hidden /> Read-only — only an admin can edit skills.
        </div>
      )}
      {error && (
        <div
          className="flex items-start gap-2 rounded-lg border border-red-500/20 bg-red-500/10 px-3 py-2 text-[12px]"
          style={{ color: 'var(--red-ink)' }}
        >
          <AlertCircle size={13} className="mt-0.5 shrink-0" aria-hidden /> {error}
        </div>
      )}
      {notice && (
        <div
          className="flex items-start gap-2 rounded-lg border border-brand-400/20 bg-brand-400/10 px-3 py-2 text-[12px]"
          style={{ color: 'var(--green-ink)' }}
        >
          <CheckCircle size={13} className="mt-0.5 shrink-0" aria-hidden /> {notice}
        </div>
      )}

      {skills.length === 0 && (
        <div className="rounded-xl border border-dashed border-line-strong px-4 py-6 text-center text-[12px] text-text-muted">
          No skills yet — the AI runs on its persona alone.
          {canManage && (
            <div className="mt-3">
              <Button variant="secondary" onClick={loadStarters}>
                <BookOpen size={13} aria-hidden /> Load starter skills
              </Button>
            </div>
          )}
        </div>
      )}

      <div className="flex flex-col gap-1.5">
        {skills.map((s) => {
          const open = openId === s.id
          return (
            <div key={s.id} className="rounded-xl border border-line bg-card">
              <div className="flex items-center gap-2.5 px-4 py-2.5">
                <BookOpen size={14} className="shrink-0 text-green-300" aria-hidden />
                <button
                  onClick={() => setOpenId(open ? null : s.id)}
                  className="min-w-0 flex-1 cursor-pointer text-left"
                >
                  <span className="block truncate text-[12px] font-bold text-text-high">{s.name}</span>
                  <span className="block truncate text-[11px] text-text-muted">
                    {s.useWhen || 'No trigger set — the AI decides when it applies.'}
                  </span>
                </button>
                {canManage && (
                  <button
                    onClick={() => remove(s.id)}
                    title={`Delete ${s.name}`}
                    aria-label={`Delete skill ${s.name}`}
                    className="grid h-7 w-7 shrink-0 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-red/15 hover:text-red-300"
                  >
                    <Trash2 size={13} aria-hidden />
                  </button>
                )}
              </div>
              {open && (
                <div className="border-t border-line px-4 py-3">
                  <label className="mb-2.5 block">
                    <span className="mb-1 block text-[10.5px] font-bold text-text-high">Name</span>
                    <input
                      value={s.name}
                      readOnly={!canManage}
                      onChange={(e) => edit(s.id, { name: e.target.value })}
                      className={inputCls}
                    />
                  </label>
                  <label className="mb-2.5 block">
                    <span className="mb-1 block text-[10.5px] font-bold text-text-high">Use when</span>
                    <textarea
                      rows={2}
                      value={s.useWhen}
                      readOnly={!canManage}
                      placeholder="e.g. the contact asks about bulk or wholesale pricing"
                      onChange={(e) => edit(s.id, { useWhen: e.target.value })}
                      className={`${inputCls} resize-y leading-[1.6]`}
                    />
                  </label>
                  <label className="block">
                    <span className="mb-1 block text-[10.5px] font-bold text-text-high">Instructions</span>
                    <textarea
                      rows={6}
                      value={s.instructions}
                      readOnly={!canManage}
                      placeholder="Collect the quantity and company name, then hand the conversation to sales."
                      onChange={(e) => edit(s.id, { instructions: e.target.value })}
                      className={`${inputCls} resize-y leading-[1.6]`}
                    />
                  </label>
                </div>
              )}
            </div>
          )
        })}
      </div>
    </div>
  )
}
