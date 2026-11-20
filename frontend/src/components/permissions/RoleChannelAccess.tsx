'use client'

import { useCallback, useEffect, useMemo, useState } from 'react'
import { AlertCircle, Check, Loader2, Radio } from 'lucide-react'
import { API } from '@/lib/apiBase'
import { authHeaders } from '@/lib/auth'
import { useT } from '@/i18n'
import { readPolicy, type ChannelAccessSnapshot, type ChannelPolicy, type PolicyChannel } from '@/lib/channelAccess'

/**
 * Permissions → a role → "Channel access": which channels, and which connection
 * methods of each, people holding the role may connect and use. "Every channel" =
 * no limit (null); "Only these" = the ticked channels + methods (e.g. SMS, or
 * WhatsApp official numbers only). Saved on its own through
 * PUT /api/auth/roles/{slug}/channels — the capability matrix keeps its own Save.
 */
export function RoleChannelAccess({ role, roleLabel }: { role: string; roleLabel: string }) {
  const t = useT()
  const [catalog, setCatalog] = useState<PolicyChannel[] | null>(null)
  const [stored, setStored] = useState<ChannelPolicy>(null)
  const [draft, setDraft] = useState<ChannelPolicy>(null)
  const [error, setError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const [saved, setSaved] = useState(false)

  const load = useCallback(async () => {
    setError(null)
    try {
      const res = await fetch(`${API}/auth/channel-access`, { headers: authHeaders() })
      const body = (await res.json().catch(() => ({}))) as Partial<ChannelAccessSnapshot> & { detail?: string }
      if (!res.ok) throw new Error(body.detail || `HTTP ${res.status}`)
      const policy = readPolicy(body.roles?.[role])
      setCatalog(body.catalog ?? [])
      setStored(policy)
      setDraft(policy)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }, [role])

  useEffect(() => {
    setSaved(false)
    void load()
  }, [load])

  const dirty = useMemo(() => JSON.stringify(norm(draft)) !== JSON.stringify(norm(stored)), [draft, stored])

  const setMethods = (channel: string, methods: string[]) => {
    setSaved(false)
    setDraft((d) => {
      const next = { ...(d ?? {}) }
      if (methods.length) next[channel] = methods
      else delete next[channel]
      return next
    })
  }

  const save = async () => {
    setSaving(true)
    setError(null)
    try {
      const res = await fetch(`${API}/auth/roles/${encodeURIComponent(role)}/channels`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json', ...authHeaders() },
        body: JSON.stringify({ channels: draft }),
      })
      const body = (await res.json().catch(() => ({}))) as { channels?: unknown; detail?: string }
      if (!res.ok) throw new Error(body.detail || `HTTP ${res.status}`)
      const policy = readPolicy(body.channels)
      setStored(policy)
      setDraft(policy)
      setSaved(true)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setSaving(false)
    }
  }

  const limited = draft !== null
  const picked = limited ? Object.keys(draft).length : 0

  return (
    <section
      data-tour="permissions-channels"
      aria-labelledby="role-channels-h"
      className="flex flex-col gap-3 rounded-xl border border-line bg-card p-4"
    >
      <div className="flex flex-wrap items-start gap-3">
        <div className="grid h-[30px] w-[30px] shrink-0 place-items-center rounded-lg border border-line bg-fill">
          <Radio size={15} strokeWidth={1.9} className="text-brand-300" aria-hidden />
        </div>
        <div className="min-w-0 flex-1">
          <h3 id="role-channels-h" className="text-[14px] font-bold text-text-heading">
            {t('admin.permissions.channels.title')}
          </h3>
          <p className="mt-0.5 text-[11.5px] leading-relaxed text-text-muted">
            {t('admin.permissions.channels.hint', { role: roleLabel })}
          </p>
        </div>
        <div className="flex items-center gap-2">
          {saved && !dirty && (
            <span className="flex items-center gap-1 text-[12px] font-semibold text-brand-300">
              <Check size={14} strokeWidth={2.25} aria-hidden /> {t('admin.permissions.channels.saved')}
            </span>
          )}
          {dirty && (
            <>
              <button
                type="button"
                onClick={() => setDraft(stored)}
                disabled={saving}
                className="rounded-xl border border-line bg-fill px-3 py-1.5 text-[12px] font-semibold text-text-muted transition-colors hover:bg-fill-hover disabled:opacity-50"
              >
                {t('common.actions.discard')}
              </button>
              <button
                type="button"
                onClick={() => void save()}
                disabled={saving || (limited && picked === 0)}
                className="flex items-center gap-1.5 rounded-xl px-3.5 py-1.5 text-[12px] font-bold text-white disabled:opacity-60"
                style={{ background: 'var(--primary)' }}
              >
                {saving && <Loader2 size={13} className="animate-spin" aria-hidden />}
                {t('admin.permissions.channels.save')}
              </button>
            </>
          )}
        </div>
      </div>

      {error && (
        <div
          role="alert"
          className="flex items-start gap-2 rounded-lg border border-red-500/20 bg-red-500/10 px-3 py-2 text-[12px]"
          style={{ color: 'var(--red-ink)' }}
        >
          <AlertCircle size={13} className="mt-0.5 shrink-0" aria-hidden /> {error}
        </div>
      )}

      {catalog === null && !error ? (
        <div className="flex items-center gap-1.5 text-[12px] text-text-muted">
          <Loader2 size={13} className="animate-spin" aria-hidden /> {t('admin.permissions.page.loading')}
        </div>
      ) : catalog ? (
        <>
          <div role="radiogroup" aria-label={t('admin.permissions.channels.title')} className="flex flex-wrap gap-2">
            {[
              { on: !limited, label: t('admin.permissions.channels.every'), pick: () => setDraft(null) },
              { on: limited, label: t('admin.permissions.channels.only'), pick: () => setDraft(draft ?? {}) },
            ].map((o) => (
              <button
                key={o.label}
                type="button"
                role="radio"
                aria-checked={o.on}
                onClick={() => {
                  setSaved(false)
                  o.pick()
                }}
                className={`rounded-xl border px-3 py-1.5 text-[12px] font-semibold transition-colors ${
                  o.on
                    ? 'border-text-primary bg-fill text-text-high'
                    : 'border-line text-text-muted hover:bg-fill-hover'
                }`}
              >
                {o.label}
              </button>
            ))}
          </div>

          {limited && (
            <>
              {picked === 0 && (
                <p className="text-[11.5px] font-semibold text-amber-300">{t('admin.permissions.channels.none')}</p>
              )}
              <div className="grid grid-cols-1 gap-2.5 md:grid-cols-2 xl:grid-cols-3">
                {catalog.map((c) => {
                  const on = draft?.[c.channel] ?? []
                  const official = c.methods.filter((m) => m.official).map((m) => m.id)
                  const hasUnofficial = c.methods.some((m) => !m.official)
                  return (
                    <fieldset key={c.channel} className="min-w-0 rounded-lg border border-line p-3">
                      <legend className="sr-only">{c.label}</legend>
                      <label className="flex cursor-pointer items-center gap-2 text-[12.5px] font-bold text-text-high">
                        <input
                          type="checkbox"
                          checked={on.length > 0}
                          ref={(el) => {
                            if (el) el.indeterminate = on.length > 0 && on.length < c.methods.length
                          }}
                          onChange={(e) => setMethods(c.channel, e.target.checked ? c.methods.map((m) => m.id) : [])}
                        />
                        {c.label}
                        {hasUnofficial && official.length > 0 && (
                          <button
                            type="button"
                            onClick={(e) => {
                              e.preventDefault()
                              setMethods(c.channel, official)
                            }}
                            className="ml-auto rounded-md border border-line px-1.5 py-0.5 text-[10.5px] font-semibold text-text-muted hover:bg-fill-hover"
                          >
                            {t('admin.permissions.channels.officialOnly')}
                          </button>
                        )}
                      </label>
                      {c.methods.length > 1 && (
                        <div className="mt-2 flex flex-col gap-1.5 pl-6">
                          {c.methods.map((m) => (
                            <label key={m.id} className="flex cursor-pointer items-center gap-2 text-[12px] text-text-primary">
                              <input
                                type="checkbox"
                                checked={on.includes(m.id)}
                                onChange={(e) =>
                                  setMethods(
                                    c.channel,
                                    c.methods
                                      .map((x) => x.id)
                                      .filter((id) => (id === m.id ? e.target.checked : on.includes(id))),
                                  )
                                }
                              />
                              <span className="min-w-0 truncate">{m.label}</span>
                              {!m.official && (
                                <span className="shrink-0 rounded-full bg-amber-400/10 px-1.5 py-0.5 text-[9.5px] font-bold uppercase tracking-[0.04em] text-amber-300">
                                  {t('admin.permissions.channels.unofficial')}
                                </span>
                              )}
                            </label>
                          ))}
                        </div>
                      )}
                    </fieldset>
                  )
                })}
              </div>
            </>
          )}
        </>
      ) : null}
    </section>
  )
}

/** A comparable form: sorted keys + sorted methods (null stays null). */
function norm(p: ChannelPolicy) {
  if (!p) return null
  return Object.keys(p)
    .sort()
    .map((k) => [k, [...p[k]].sort()])
}
