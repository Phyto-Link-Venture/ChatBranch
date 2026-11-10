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
 * The safety rules (HARD_RULES) always lead, organization-wide; only a holder of
 * `settings.ai_hard_rules.manage` (superadmin by default) edits them.
 * Server: GET/PUT /api/aicore/persona (settings.ai.manage; an outlet must be
 * in the caller's scope; `hard_rules` needs its own capability).
 *
 * Layout: the editable parts (persona, house rules, then the tucked-away safety
 * rules) on the left; on wide screens what the AI actually receives stays
 * pinned on the right, in prompt order, so an edit and its effect sit together.
 */

import Link from 'next/link'
import { useCallback, useEffect, useState, type ReactNode } from 'react'
import {
  ArrowUpRight,
  ChevronDown,
  ListChecks,
  Loader2,
  Lock,
  RotateCcw,
  Save,
  ShieldCheck,
  UserRound,
  type LucideIcon,
} from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Card } from '@/components/ui/Card'
import { aicore } from '@/components/skills/skillsApi'
import { useEntity, useEntityLevelQuery } from '@/lib/EntityContext'
import { MultiEntityNotice } from '@/components/shell/MultiEntityNotice'
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
  hard_rules_default: string
  hard_rules_custom: boolean
  hard_rules_max_chars: number
  can_edit_hard_rules: boolean
  house_rules: { own: string[]; inherited: { rule: string; from: Level }[]; max_chars: number }
  preamble: string
  entity: string | null
  entity_name: string
  can_manage: boolean
}

type Where = 'persona' | 'rules' | 'safety'

const PERSONA_MAX = 8000

const fieldCls =
  'w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[13px] leading-relaxed text-text-primary outline-none transition-colors placeholder:text-text-faint focus:border-brand-400 disabled:bg-fill-subtle disabled:text-text-mid'

const ruleLines = (s: string) =>
  s
    .split('\n')
    .map((l) => l.trim())
    .filter(Boolean)

/** A section card: icon, title, a status on the right, help text, then the body. */
function Section({
  id,
  tour,
  Icon,
  title,
  status,
  help,
  children,
}: {
  id: string
  tour: string
  Icon: LucideIcon
  title: string
  status?: ReactNode
  help: string
  children: ReactNode
}) {
  return (
    <Card className="scroll-mt-4 p-5" data-tour={tour}>
      <section id={id} aria-labelledby={`${id}-title`}>
        <div className="flex items-start gap-3">
          <span className="mt-0.5 flex h-8 w-8 shrink-0 items-center justify-center rounded-lg bg-fill-subtle text-text-mid">
            <Icon size={16} aria-hidden />
          </span>
          <div className="min-w-0 flex-1">
            <div className="flex flex-wrap items-baseline justify-between gap-x-3 gap-y-1">
              <h2 id={`${id}-title`} className="display text-[19px] leading-tight text-text-heading">
                {title}
              </h2>
              {status && <div className="text-[12px] text-text-muted">{status}</div>}
            </div>
            <p className="mt-1 text-[12.5px] leading-relaxed text-text-muted">{help}</p>
          </div>
        </div>
        <div className="mt-4">{children}</div>
      </section>
    </Card>
  )
}

export function PersonaRules() {
  const t = useT()
  const entityQ = useEntityLevelQuery()
  const { levelEntity } = useEntity()
  const [data, setData] = useState<PersonaPayload | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [own, setOwn] = useState(false)
  const [persona, setPersona] = useState('')
  const [rules, setRules] = useState('')
  const [safety, setSafety] = useState('')
  const [safetyOpen, setSafetyOpen] = useState(false)
  const [busy, setBusy] = useState<Where | null>(null)
  const [msg, setMsg] = useState<{ ok: boolean; text: string; where: Where } | null>(null)

  const apply = (d: PersonaPayload) => {
    setData(d)
    // the organization level always "owns" its persona; an outlet owns one only when it set it
    setOwn(d.entity == null || d.own_persona.length > 0)
    setPersona(d.own_persona || d.persona)
    setRules(d.house_rules.own.join('\n'))
    setSafety(d.hard_rules)
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

  const save = async (where: Where, body: Record<string, unknown>) => {
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
  const levelName = isOrg ? t('ai.persona.theOrganization') : data.entity_name || levelEntity?.name || ''
  const sourceName = (l: Level) =>
    l.scope === 'default'
      ? t('ai.persona.builtInDefault')
      : l.scope === 'organization'
        ? t('ai.persona.theOrganization')
        : l.entity_name
  const readOnly = !data.can_manage
  const personaDirty = own ? persona.trim() !== (data.own_persona || (isOrg ? data.persona : '')).trim() : data.own_persona.length > 0
  const rulesDirty = rules.trim() !== data.house_rules.own.join('\n').trim()
  const safetyDirty = safety.trim() !== data.hard_rules.trim()
  const rulesInForce = data.house_rules.inherited.length + ruleLines(rules).length
  // never hide something that needs attention: an unsaved edit or a save result
  const safetyShown = safetyOpen || safetyDirty || msg?.where === 'safety'

  const Status = ({ where }: { where: Where }) =>
    msg && msg.where === where ? (
      <span role={msg.ok ? 'status' : 'alert'} className={`text-[12px] ${msg.ok ? 'text-[var(--green-ink)]' : 'text-[var(--red-ink)]'}`}>
        {msg.text}
      </span>
    ) : null
  const Unsaved = ({ on }: { on: boolean }) =>
    on ? (
      <span className="inline-flex items-center gap-1.5 text-[12px] text-[var(--amber-ink,var(--brand-ink))]">
        <span className="h-1.5 w-1.5 rounded-full bg-current" aria-hidden />
        {t('ai.persona.unsaved')}
      </span>
    ) : null
  const Count = ({ n, max }: { n: number; max: number }) => (
    <span className={`text-[11.5px] tabular-nums ${n > max * 0.9 ? 'text-[var(--red-ink)]' : 'text-text-faint'}`}>
      {t('ai.persona.chars', { n, max })}
    </span>
  )
  const SaveIcon = ({ where }: { where: Where }) =>
    busy === where ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Save size={13} aria-hidden />

  // the summary chips at the top double as jump links to their section
  const summary: { href: string; Icon: LucideIcon; label: string; value: string }[] = [
    { href: '#persona', Icon: UserRound, label: t('ai.persona.personaTitle'), value: sourceName(data.source) },
    { href: '#house-rules', Icon: ListChecks, label: t('ai.persona.rulesTitle'), value: String(rulesInForce) },
    {
      href: '#safety',
      Icon: ShieldCheck,
      label: t('ai.persona.safetyTitle'),
      value: data.hard_rules_custom ? t('ai.persona.safetyCustom') : t('ai.persona.safetyDefault'),
    },
  ]

  return (
    <div className="flex flex-col gap-4">
      <MultiEntityNotice />

      {/* ---- where we are + what's in force ---- */}
      <div className="flex flex-col gap-3 rounded-xl border border-line bg-fill-subtle px-4 py-3 lg:flex-row lg:items-center lg:justify-between">
        <div className="min-w-0">
          <p className="text-[13px] text-text-high">
            {t('ai.persona.editingFor', { level: levelName })}
            {readOnly && (
              <span className="ml-2 inline-flex items-center gap-1 rounded-md bg-fill px-1.5 py-0.5 align-middle text-[11px] font-semibold text-text-muted">
                <Lock size={11} aria-hidden /> {t('ai.persona.readOnly')}
              </span>
            )}
          </p>
          {!isOrg && <p className="mt-0.5 text-[12px] text-text-muted">{t('ai.persona.switcherHint')}</p>}
        </div>
        <ul className="flex flex-wrap gap-2">
          {summary.map((s) => (
            <li key={s.href}>
              <a
                href={s.href}
                onClick={() => s.href === '#safety' && setSafetyOpen(true)}
                className="inline-flex items-center gap-1.5 rounded-full border border-line bg-card px-2.5 py-1 text-[12px] text-text-mid transition-colors hover:border-line-strong hover:text-text-high"
              >
                <s.Icon size={12} aria-hidden />
                <span className="text-text-muted">{s.label}</span>
                <span className="font-semibold text-text-high">{s.value}</span>
              </a>
            </li>
          ))}
        </ul>
      </div>

      <div className="grid items-start gap-4 xl:grid-cols-[minmax(0,1fr)_minmax(340px,420px)]">
        <div className="flex min-w-0 flex-col gap-4">
          {/* ---- persona: nearest level wins ---- */}
          <Section
            id="persona"
            tour="persona-card"
            Icon={UserRound}
            title={t('ai.persona.personaTitle')}
            status={t('ai.persona.inUse', { source: sourceName(data.source) })}
            help={t('ai.persona.personaHelp')}
          >
            {!isOrg && (
              <div className="mb-3">
                <div
                  role="radiogroup"
                  aria-label={t('ai.persona.ownToggle', { entity: levelName })}
                  className="inline-flex rounded-lg border border-line bg-fill-subtle p-0.5"
                >
                  {[
                    { v: false, label: t('ai.persona.modeInherit') },
                    { v: true, label: t('ai.persona.modeOwn') },
                  ].map((o) => (
                    <button
                      key={String(o.v)}
                      type="button"
                      role="radio"
                      aria-checked={own === o.v}
                      disabled={readOnly}
                      onClick={() => {
                        setOwn(o.v)
                        if (o.v && !persona.trim()) setPersona(data.inherited_persona)
                      }}
                      className={`rounded-md px-3 py-1.5 text-[12.5px] font-semibold transition-colors disabled:cursor-not-allowed ${
                        own === o.v ? 'bg-card text-text-high shadow-sm' : 'text-text-muted hover:text-text-high'
                      }`}
                    >
                      {o.label}
                    </button>
                  ))}
                </div>
                <p className="mt-1.5 text-[12px] text-text-muted">
                  {own ? t('ai.persona.ownOn') : t('ai.persona.ownOff', { source: sourceName(data.inherited_source) })}
                </p>
              </div>
            )}

            <textarea
              aria-label={t('ai.persona.personaTitle')}
              rows={9}
              maxLength={PERSONA_MAX}
              value={own ? persona : data.inherited_persona}
              onChange={(e) => setPersona(e.target.value)}
              disabled={readOnly || !own}
              className={`${fieldCls} resize-y`}
            />
            <div className="mt-1 flex items-center justify-between gap-2">
              <span className="text-[12px] text-text-muted">
                {!own && t('ai.persona.inheritedFrom', { source: sourceName(data.inherited_source) })}
              </span>
              {own && <Count n={persona.length} max={PERSONA_MAX} />}
            </div>
            {!readOnly && (
              <div className="mt-3 flex flex-wrap items-center gap-2">
                <Button
                  size="sm"
                  disabled={!personaDirty || busy != null}
                  onClick={() => save('persona', { persona: own ? persona : '' })}
                >
                  <SaveIcon where="persona" />
                  {own ? t('ai.persona.savePersona') : t('ai.persona.useInherited')}
                </Button>
                {isOrg && !data.is_default && (
                  <Button size="sm" variant="ghost" disabled={busy != null} onClick={() => save('persona', { persona: '' })}>
                    <RotateCcw size={13} aria-hidden /> {t('ai.persona.resetDefault')}
                  </Button>
                )}
                <Unsaved on={personaDirty && busy == null} />
                <Status where="persona" />
              </div>
            )}
          </Section>

          {/* ---- house rules: they add up ---- */}
          <Section
            id="house-rules"
            tour="rules-card"
            Icon={ListChecks}
            title={t('ai.persona.rulesTitle')}
            status={t('ai.persona.rulesInForce', { count: rulesInForce })}
            help={t('ai.persona.rulesHelp')}
          >
            {data.house_rules.inherited.length > 0 && (
              <div className="mb-4">
                <div className="eyebrow mb-1.5 flex items-center gap-1.5">
                  <Lock size={11} aria-hidden /> {t('ai.persona.inheritedRules')}
                </div>
                <ol className="flex flex-col divide-y divide-line overflow-hidden rounded-lg border border-line">
                  {data.house_rules.inherited.map((r, i) => (
                    <li key={i} className="flex flex-wrap items-start gap-x-3 gap-y-1 bg-fill-subtle px-3 py-2 text-[13px] text-text-mid">
                      <span className="w-4 shrink-0 text-right text-[11.5px] tabular-nums text-text-faint">{i + 1}</span>
                      <span className="min-w-0 flex-1">{r.rule}</span>
                      <span className="shrink-0 rounded-md bg-fill px-1.5 py-0.5 text-[10.5px] font-semibold text-text-muted">
                        {t('ai.persona.fromLevel', { source: sourceName(r.from) })}
                      </span>
                    </li>
                  ))}
                </ol>
              </div>
            )}

            <label htmlFor="house-rules-own" className="block text-[12.5px] font-semibold text-text-high">
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
            <div className="mt-1 flex items-center justify-between gap-2">
              <span className="text-[12px] text-text-muted">{t('ai.persona.onePerLine')}</span>
              <Count n={rules.length} max={data.house_rules.max_chars} />
            </div>
            {!readOnly && (
              <div className="mt-3 flex flex-wrap items-center gap-2">
                <Button size="sm" disabled={!rulesDirty || busy != null} onClick={() => save('rules', { house_rules: rules })}>
                  <SaveIcon where="rules" />
                  {t('ai.persona.saveRules')}
                </Button>
                <Unsaved on={rulesDirty && busy == null} />
                <Status where="rules" />
              </div>
            )}
          </Section>

          {/* ---- safety rules: organization-wide, always first in the prompt; tucked away here ---- */}
          <Section
            id="safety"
            tour="safety-card"
            Icon={ShieldCheck}
            title={t('ai.persona.safetyTitle')}
            status={
              <span className="inline-flex items-center gap-1">
                {!data.can_edit_hard_rules && <Lock size={11} aria-hidden />}
                {data.hard_rules_custom ? t('ai.persona.safetyCustom') : t('ai.persona.safetyDefault')}
              </span>
            }
            help={t('ai.persona.safetyHelp')}
          >
            <button
              type="button"
              aria-expanded={safetyShown}
              aria-controls="safety-body"
              onClick={() => setSafetyOpen(!safetyShown)}
              className="inline-flex items-center gap-1 text-[12.5px] font-semibold text-[var(--brand-ink)] hover:underline"
            >
              <ChevronDown size={14} aria-hidden className={`transition-transform ${safetyShown ? 'rotate-180' : ''}`} />
              {safetyShown ? t('ai.persona.safetyHide') : t('ai.persona.safetyShow')}
            </button>
            {safetyShown && (
              <div id="safety-body" className="mt-3">
                <textarea
                  aria-label={t('ai.persona.safetyTitle')}
                  rows={5}
                  maxLength={data.hard_rules_max_chars || 4000}
                  value={safety}
                  onChange={(e) => setSafety(e.target.value)}
                  disabled={!data.can_edit_hard_rules}
                  className={`${fieldCls} resize-y`}
                />
                {data.can_edit_hard_rules ? (
                  <div className="mt-3 flex flex-wrap items-center gap-2">
                    <Button
                      size="sm"
                      disabled={!safetyDirty || !safety.trim() || busy != null}
                      onClick={() => save('safety', { hard_rules: safety })}
                    >
                      <SaveIcon where="safety" />
                      {t('ai.persona.saveSafety')}
                    </Button>
                    {data.hard_rules_custom && (
                      <Button size="sm" variant="ghost" disabled={busy != null} onClick={() => save('safety', { hard_rules: '' })}>
                        <RotateCcw size={13} aria-hidden /> {t('ai.persona.resetDefault')}
                      </Button>
                    )}
                    <span className="text-[12px] text-text-muted">{t('ai.persona.safetyWarn')}</span>
                    <Status where="safety" />
                  </div>
                ) : (
                  <p className="mt-1.5 flex items-center gap-1.5 text-[12px] text-text-muted">
                    <Lock size={12} aria-hidden /> {t('ai.persona.safetyLocked')}
                  </p>
                )}
              </div>
            )}
          </Section>
        </div>

        {/* ---- what the AI actually receives, pinned beside the editors ---- */}
        <aside className="min-w-0 xl:sticky xl:top-4">
          <Card className="p-5" data-tour="preamble-card">
            <div className="flex items-baseline justify-between gap-3">
              <h2 className="display text-[19px] leading-tight text-text-heading">{t('ai.persona.previewTitle')}</h2>
            </div>
            <p className="mt-1 text-[12.5px] leading-relaxed text-text-muted">{t('ai.persona.previewHelp')}</p>
            <ol className="mt-3 flex flex-col gap-1" aria-label={t('ai.persona.promptOrder')}>
              {[
                { Icon: ShieldCheck, label: t('ai.persona.safetyTitle'), href: '#safety' },
                { Icon: ListChecks, label: t('ai.persona.rulesTitle'), href: '#house-rules' },
                { Icon: UserRound, label: t('ai.persona.personaTitle'), href: '#persona' },
              ].map((s, i) => (
                <li key={s.href}>
                  <a
                    href={s.href}
                    onClick={() => s.href === '#safety' && setSafetyOpen(true)}
                    className="flex items-center gap-2 rounded-md px-1.5 py-1 text-[12.5px] text-text-mid hover:bg-fill-subtle hover:text-text-high"
                  >
                    <span className="flex h-5 w-5 items-center justify-center rounded-full bg-fill text-[11px] font-semibold tabular-nums text-text-muted">
                      {i + 1}
                    </span>
                    <s.Icon size={13} aria-hidden />
                    {s.label}
                  </a>
                </li>
              ))}
            </ol>
            {(personaDirty || rulesDirty || safetyDirty) && (
              <p className="mt-2 text-[12px] text-text-muted">{t('ai.persona.previewStale')}</p>
            )}
            <pre className="mt-3 max-h-[50vh] overflow-auto whitespace-pre-wrap break-words rounded-lg bg-well p-3 font-mono text-[12px] leading-relaxed text-text-mid">
              {data.preamble}
            </pre>
            <Link
              href="/ai-playground"
              className="mt-3 inline-flex items-center gap-1 text-[12.5px] font-semibold text-[var(--brand-ink)] hover:underline"
            >
              {t('ai.persona.tryIt')} <ArrowUpRight size={12} aria-hidden />
            </Link>
          </Card>
        </aside>
      </div>
    </div>
  )
}
