'use client'

/**
 * Persona & house rules — the opening of every AI prompt, per level of the
 * entity tree (the sidebar's outlet switcher picks the level; "All entities" =
 * the organization). Two different inheritance rules, shown side by side:
 *
 * - The PERSONA is one value: the nearest level that sets one wins (outlet →
 *   parent → … → organization → the built-in default). An outlet either uses
 *   what it inherits or writes its own, which replaces it.
 * - HOUSE RULES add up: an outlet keeps every rule above it (read-only here)
 *   and adds its own lines.
 *
 * The safety rules (HARD_RULES) always lead and are not editable.
 * Server: GET/PUT /api/aicore/persona (settings.ai.manage; an outlet must be
 * in the caller's scope).
 */

import Link from 'next/link'
import { useCallback, useEffect, useState } from 'react'
import { ArrowUpRight, Loader2, Lock, RotateCcw, Save } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Card, CardTitle } from '@/components/ui/Card'
import { Toggle } from '@/components/ui/Toggle'
import { aicore } from '@/components/skills/skillsApi'
import { useEntity, useEntityQuery } from '@/lib/EntityContext'
import { useT } from '@/i18n'

interface Level {
  scope: 'entity' | 'parent' | 'organization' | 'default'
  entity: string | null
  entity_name: string
}

interface PersonaPayload {
  persona: string
  is_default: boolean
  source: Level
  own_persona: string
  inherited_persona: string
  inherited_source: Level
  hard_rules: string
  house_rules: { own: string[]; inherited: { rule: string; from: Level }[]; max_chars: number }
  preamble: string
  entity: string | null
  entity_name: string
  can_manage: boolean
}

const fieldCls =
  'w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[13px] leading-relaxed text-text-primary outline-none transition-colors placeholder:text-text-faint focus:border-brand-400 disabled:opacity-70'

export function PersonaRules() {
  const t = useT()
  const entityQ = useEntityQuery()
  const { activeEntity } = useEntity()
  const [data, setData] = useState<PersonaPayload | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [own, setOwn] = useState(false)
  const [persona, setPersona] = useState('')
  const [rules, setRules] = useState('')
  const [busy, setBusy] = useState<'persona' | 'rules' | null>(null)
  const [msg, setMsg] = useState<{ ok: boolean; text: string; where: 'persona' | 'rules' } | null>(null)

  const apply = (d: PersonaPayload) => {
    setData(d)
    // the organization level always "owns" its persona; an outlet owns one only when it set it
    setOwn(d.entity == null || d.own_persona.length > 0)
    setPersona(d.own_persona || d.persona)
    setRules(d.house_rules.own.join('\n'))
  }

  const load = useCallback(async () => {
    setLoadError(null)
    setMsg(null)
    try {
      apply(await aicore<PersonaPayload>(`persona${entityQ && `?${entityQ}`}`))
    } catch (e) {
      setData(null)
      setLoadError(e instanceof Error ? e.message : String(e))
    }
  }, [entityQ])

  useEffect(() => {
    void load()
  }, [load])

  const save = async (where: 'persona' | 'rules', body: Record<string, unknown>) => {
    setBusy(where)
    setMsg(null)
    try {
      const d = await aicore<PersonaPayload>('persona', {
        method: 'PUT',
        body: JSON.stringify({ ...body, entity: data?.entity ?? null }),
      })
      apply(d)
      setMsg({ ok: true, text: t('common.status.saved'), where })
    } catch (e) {
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e), where })
    } finally {
      setBusy(null)
    }
  }

  if (loadError) {
    return (
      <Card className="p-5">
        <p role="alert" className="text-[13px] text-[var(--red-ink)]">{loadError}</p>
      </Card>
    )
  }
  if (!data) {
    return (
      <div className="flex items-center gap-2 text-[13px] text-text-muted">
        <Loader2 size={14} className="animate-spin" aria-hidden /> {t('common.status.loading')}
      </div>
    )
  }

  const isOrg = data.entity == null
  const levelName = isOrg ? t('ai.persona.theOrganization') : data.entity_name || activeEntity?.name || ''
  const sourceName = (l: Level) =>
    l.scope === 'default'
      ? t('ai.persona.builtInDefault')
      : l.scope === 'organization'
        ? t('ai.persona.theOrganization')
        : l.entity_name
  const readOnly = !data.can_manage
  const personaDirty = own ? persona.trim() !== (data.own_persona || (isOrg ? data.persona : '')).trim() : data.own_persona.length > 0
  const rulesDirty = rules.trim() !== data.house_rules.own.join('\n').trim()
  const Status = ({ where }: { where: 'persona' | 'rules' }) =>
    msg && msg.where === where ? (
      <span role={msg.ok ? 'status' : 'alert'} className={`text-[12px] ${msg.ok ? 'text-[var(--green-ink)]' : 'text-[var(--red-ink)]'}`}>
        {msg.text}
      </span>
    ) : null

  return (
    <div className="flex flex-col gap-4">
      <p className="text-[13px] text-text-mid">
        {t('ai.persona.editingFor', { level: levelName })}{' '}
        {!isOrg && t('ai.persona.switcherHint')}
      </p>
      {readOnly && (
        <p className="flex items-center gap-1.5 text-[12.5px] text-text-muted">
          <Lock size={12} aria-hidden /> {t('ai.persona.readOnly')}
        </p>
      )}

      {/* ---- persona: nearest level wins ---- */}
      <Card className="p-5" data-tour="persona-card">
        <CardTitle
          right={
            <span className="text-text-muted">
              {t('ai.persona.inUse', { source: sourceName(data.source) })}
            </span>
          }
        >
          {t('ai.persona.personaTitle')}
        </CardTitle>
        <p className="mt-1 text-[12.5px] text-text-muted">{t('ai.persona.personaHelp')}</p>

        {!isOrg && (
          <div className="mt-4 flex items-center justify-between gap-3 rounded-lg border border-line px-3 py-2.5">
            <div>
              <div className="text-[13px] font-semibold text-text-high">{t('ai.persona.ownToggle', { entity: levelName })}</div>
              <div className="text-[12px] text-text-muted">
                {own ? t('ai.persona.ownOn') : t('ai.persona.ownOff', { source: sourceName(data.inherited_source) })}
              </div>
            </div>
            <Toggle
              on={own}
              disabled={readOnly}
              onChange={(v) => {
                setOwn(v)
                if (v && !persona.trim()) setPersona(data.inherited_persona)
              }}
              label={t('ai.persona.ownToggle', { entity: levelName })}
            />
          </div>
        )}

        <textarea
          aria-label={t('ai.persona.personaTitle')}
          rows={10}
          maxLength={8000}
          value={own ? persona : data.inherited_persona}
          onChange={(e) => setPersona(e.target.value)}
          disabled={readOnly || !own}
          className={`${fieldCls} mt-3 resize-y`}
        />
        {!own && (
          <p className="mt-1 text-[12px] text-text-muted">
            {t('ai.persona.inheritedFrom', { source: sourceName(data.inherited_source) })}
          </p>
        )}
        {!readOnly && (
          <div className="mt-3 flex flex-wrap items-center gap-2">
            <Button
              size="sm"
              disabled={!personaDirty || busy != null}
              onClick={() => save('persona', { persona: own ? persona : '' })}
            >
              {busy === 'persona' ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Save size={13} aria-hidden />}
              {own ? t('ai.persona.savePersona') : t('ai.persona.useInherited')}
            </Button>
            {isOrg && !data.is_default && (
              <Button size="sm" variant="ghost" disabled={busy != null} onClick={() => save('persona', { persona: '' })}>
                <RotateCcw size={13} aria-hidden /> {t('ai.persona.resetDefault')}
              </Button>
            )}
            <Status where="persona" />
          </div>
        )}
      </Card>

      {/* ---- house rules: they add up ---- */}
      <Card className="p-5" data-tour="rules-card">
        <CardTitle>{t('ai.persona.rulesTitle')}</CardTitle>
        <p className="mt-1 text-[12.5px] text-text-muted">{t('ai.persona.rulesHelp')}</p>

        {data.house_rules.inherited.length > 0 && (
          <div className="mt-4">
            <div className="eyebrow mb-1.5">{t('ai.persona.inheritedRules')}</div>
            <ul className="flex flex-col gap-1.5">
              {data.house_rules.inherited.map((r, i) => (
                <li key={i} className="flex flex-wrap items-start justify-between gap-2 rounded-lg bg-fill-subtle px-3 py-2 text-[13px] text-text-mid">
                  <span className="min-w-0 flex-1">{r.rule}</span>
                  <span className="shrink-0 rounded-md bg-fill px-1.5 py-0.5 text-[10.5px] font-semibold text-text-muted">
                    {t('ai.persona.fromLevel', { source: sourceName(r.from) })}
                  </span>
                </li>
              ))}
            </ul>
          </div>
        )}

        <label htmlFor="house-rules-own" className="mt-4 block text-[12.5px] font-semibold text-text-high">
          {isOrg ? t('ai.persona.orgRules') : t('ai.persona.ownRules', { entity: levelName })}
        </label>
        <textarea
          id="house-rules-own"
          rows={6}
          maxLength={data.house_rules.max_chars}
          value={rules}
          onChange={(e) => setRules(e.target.value)}
          disabled={readOnly}
          placeholder={t('ai.persona.rulesPlaceholder')}
          className={`${fieldCls} mt-1 resize-y`}
        />
        <p className="mt-1 text-[12px] text-text-muted">{t('ai.persona.onePerLine')}</p>
        {!readOnly && (
          <div className="mt-3 flex flex-wrap items-center gap-2">
            <Button size="sm" disabled={!rulesDirty || busy != null} onClick={() => save('rules', { house_rules: rules })}>
              {busy === 'rules' ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Save size={13} aria-hidden />}
              {t('ai.persona.saveRules')}
            </Button>
            <Status where="rules" />
          </div>
        )}
      </Card>

      {/* ---- what the AI actually receives ---- */}
      <Card className="p-5" data-tour="preamble-card">
        <CardTitle
          right={
            <Link href="/ai-playground" className="inline-flex items-center gap-1 font-semibold text-[var(--brand-ink)] hover:underline">
              {t('ai.persona.tryIt')} <ArrowUpRight size={12} aria-hidden />
            </Link>
          }
        >
          {t('ai.persona.previewTitle')}
        </CardTitle>
        <p className="mt-1 text-[12.5px] text-text-muted">{t('ai.persona.previewHelp')}</p>
        <pre className="mt-3 max-h-[360px] overflow-auto whitespace-pre-wrap break-words rounded-lg bg-well p-3 font-mono text-[12px] leading-relaxed text-text-mid">
          {data.preamble}
        </pre>
      </Card>
    </div>
  )
}
