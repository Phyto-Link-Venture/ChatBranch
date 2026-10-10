'use client'

import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react'
import {
  AlertCircle,
  BookOpen,
  CalendarClock,
  CheckCircle,
  Clock,
  Eye,
  Globe,
  Loader2,
  Lock,
  Pencil,
  Plus,
  RefreshCw,
  ShieldCheck,
  Trash2,
  Wrench,
} from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Card } from '@/components/ui/Card'
import { ChannelLogo } from '@/components/ui/ChannelLogo'
import { Modal } from '@/components/ui/Modal'
import { Select } from '@/components/ui/Select'
import { Toggle } from '@/components/ui/Toggle'
import { useEntity, useEntityQuery } from '@/lib/EntityContext'
import { authHeaders } from '@/lib/auth'
import { useRole } from '@/lib/useRole'
import { DEFAULT_AGENT_SKILLS } from '@/lib/flow/defaultAgentSkills'
import { ScopeBadge } from './EntityScopeSelect'
import { SkillWizard, type WizardInit } from '@/components/skills/SkillWizard'
import { ReadySkillsGallery } from '@/components/skills/ReadySkillsGallery'
import {
  KIND_META,
  ON_FAIL_OPTIONS,
  aicore,
  appSkillSubtitle,
  calendarConfig,
  channelOptions,
  deleteSkill,
  fetchApps,
  pretty,
  saveSkill,
  toWire,
  writeScope,
  type AppCatalogEntry,
  type Builtin,
  type BuiltinsPayload,
  type Skill,
  type SkillKind,
} from '@/components/skills/skillsApi'
import { Trans, useT, type MessageKey, type TFunction } from '@/i18n'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

const KIND_ICON: Record<SkillKind, typeof BookOpen> = {
  prompt: BookOpen,
  tool: Wrench,
  check: ShieldCheck,
  app: CalendarClock,
}

/** Section order on the page — connected apps sit right under instructions. */
const KIND_ORDER: SkillKind[] = ['prompt', 'app', 'tool', 'check']

const channelLabel = (c: string, t: TFunction) => channelOptions(t).find((o) => o.value === c)?.label ?? c

/** The built-in tools by name — the server's English label/description for one this build doesn't know. */
const BUILTIN_TEXT: Record<string, { label: MessageKey; description: MessageKey }> = {
  current_time: { label: 'ai.builtins.currentTime.label', description: 'ai.builtins.currentTime.description' },
  current_week: { label: 'ai.builtins.currentWeek.label', description: 'ai.builtins.currentWeek.description' },
  date_add: { label: 'ai.builtins.dateAdd.label', description: 'ai.builtins.dateAdd.description' },
  days_between: { label: 'ai.builtins.daysBetween.label', description: 'ai.builtins.daysBetween.description' },
  convert_timezone: { label: 'ai.builtins.convertTimezone.label', description: 'ai.builtins.convertTimezone.description' },
  request_handover: { label: 'ai.builtins.requestHandover.label', description: 'ai.builtins.requestHandover.description' },
}

const builtinText = (b: Builtin, t: TFunction) => {
  const k = BUILTIN_TEXT[b.name]
  return k ? { label: t(k.label), description: t(k.description) } : { label: b.label, description: b.description }
}

/**
 * AI skills — what the assistant can do, in four kinds:
 *
 *  * Instructions (prompt): plain-language guidance in the prompt's SKILLS
 *    block, selected by its "use when".
 *  * Tools: JavaScript the model CALLS for an exact answer (opening hours,
 *    dates, validations) — run in a sandbox, never trusted to the model.
 *  * Reply checks: JavaScript that vets every AI reply before it is sent; a
 *    failing reply is rewritten with the reason, then handed to a human or
 *    blocked.
 *  * Connected apps: settings, not code — Calendar booking gives the AI
 *    availability / book / cancel tools over Google Calendar or ChatBranch's
 *    own bookings (catalog: GET /api/aicore/skills/apps).
 *
 * Plus the read-only built-ins (current time / week, date maths, timezone
 * conversion) that every conversation gets as tools.
 *
 * `GET/PUT /api/aicore/skills` is the SOLE writer of AgentSkill rows; every
 * change here re-reads the affected scope and writes just that change back.
 * The flow builder reads this list for its step picker.
 */
export function SkillsSettings() {
  const t = useT()
  const entityQ = useEntityQuery()
  const { activeEntity } = useEntity()
  const { role, ready } = useRole()
  const canManage = role === 'admin' || role === 'superadmin'
  const isSuper = role === 'superadmin'

  const [skills, setSkills] = useState<Skill[] | null>(null)
  const [builtins, setBuiltins] = useState<BuiltinsPayload | null>(null)
  // connected-app catalog — only for the app rows' calendar labels (fail-soft)
  const [apps, setApps] = useState<AppCatalogEntry[] | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [wizard, setWizard] = useState<WizardInit | null>(null)
  const [readOnlyWizard, setReadOnlyWizard] = useState(false)
  const [confirm, setConfirm] = useState<Skill | null>(null)
  const [busyId, setBusyId] = useState<string | null>(null)

  const load = useCallback(async () => {
    try {
      const [d, b, a] = await Promise.all([
        aicore<{ skills: Skill[] }>(`skills${entityQ && `?${entityQ}`}`),
        aicore<BuiltinsPayload>('skills/builtins').catch(() => null),
        fetchApps(),
      ])
      setSkills(d.skills)
      setBuiltins(b)
      setApps(a)
      setLoadError(null)
    } catch (e) {
      setSkills(null)
      setLoadError(e instanceof Error ? e.message : String(e))
    }
  }, [entityQ])

  useEffect(() => {
    load()
  }, [load])

  const byKind = useMemo(() => {
    const out: Record<SkillKind, Skill[]> = { prompt: [], tool: [], check: [], app: [] }
    for (const s of skills ?? []) out[(s.kind in out ? s.kind : 'prompt') as SkillKind].push(s)
    return out
  }, [skills])

  const defaultScope = activeEntity?.slug ?? null

  const open = (init: WizardInit, readOnly = false) => {
    setError(null)
    setNotice(null)
    setReadOnlyWizard(readOnly)
    setWizard(init)
  }

  const toggle = async (s: Skill, enabled: boolean) => {
    setBusyId(s.id)
    setError(null)
    setNotice(null)
    try {
      await saveSkill({ ...toWire(s), enabled }, s.entity ?? null)
      setNotice(enabled ? t('ai.skills.toggledOn', { name: s.name }) : t('ai.skills.toggledOff', { name: s.name }))
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusyId(null)
    }
  }

  const remove = async (s: Skill) => {
    setBusyId(s.id)
    setError(null)
    try {
      await deleteSkill(s.id, s.entity ?? null)
      setNotice(t('ai.skills.deleted', { name: s.name }))
      setConfirm(null)
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
      setConfirm(null)
    } finally {
      setBusyId(null)
    }
  }

  const loadStarters = async () => {
    setError(null)
    try {
      await writeScope(defaultScope, (rows) => [
        ...rows,
        ...DEFAULT_AGENT_SKILLS.filter((d) => !rows.some((r) => r.id === d.id)).map((d, i) => ({
          id: d.id,
          name: d.name,
          useWhen: d.useWhen ?? '',
          instructions: d.instructions ?? '',
          position: rows.length + i,
          kind: 'prompt' as const,
          description: '',
          code: '',
          parameters: null,
          tests: [],
          enabled: true,
          channels: [],
          onFail: 'retry' as const,
        })),
      ])
      setNotice(t('ai.skills.startersAdded'))
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  if (!ready || (skills === null && !loadError)) {
    return (
      <div className="flex items-center gap-1.5 py-6 text-[12.5px] text-text-muted">
        <Loader2 size={13} className="animate-spin" aria-hidden /> {t('ai.skills.loading')}
      </div>
    )
  }

  if (skills === null) {
    return (
      <div className="py-6 text-[12.5px]">
        <Banner tone="error">{t('ai.skills.loadError', { error: loadError ?? '' })}</Banner>
        <Button variant="ghost" onClick={load} className="mt-2.5">
          <RefreshCw size={13} aria-hidden /> {t('common.actions.retry')}
        </Button>
      </div>
    )
  }

  const rowProps = {
    canManage,
    busyId,
    activeEntityName: activeEntity?.name ?? null,
    apps,
    onToggle: toggle,
    onEdit: (s: Skill) => open({ mode: 'edit', skill: s }, !canManage),
    onDelete: (s: Skill) => setConfirm(s),
  }

  return (
    <div className="flex flex-col gap-7 pb-10 pt-4">
      <div className="flex flex-wrap items-start gap-x-6 gap-y-3">
        <p className="min-w-0 max-w-[720px] flex-1 text-[13px] leading-relaxed text-text-mid">
          <Trans
            k="ai.skills.intro"
            values={{
              instruction: <b className="font-semibold text-text-high">{t('ai.skills.introInstruction')}</b>,
              tool: <b className="font-semibold text-text-high">{t('ai.skills.introTool')}</b>,
              check: <b className="font-semibold text-text-high">{t('ai.skills.introCheck')}</b>,
              app: <b className="font-semibold text-text-high">{t('ai.skills.introApp')}</b>,
            }}
          />
        </p>
        {canManage && (
          <Button data-tour="skills-new" onClick={() => open({ mode: 'new' })}>
            <Plus size={14} aria-hidden /> {t('ai.skills.newSkill')}
          </Button>
        )}
      </div>

      {!canManage && (
        <div className="flex items-center gap-1.5 text-[12px] font-semibold text-text-muted">
          <Lock size={12} aria-hidden /> {t('ai.skills.readOnly')}
        </div>
      )}
      {error && <Banner tone="error">{error}</Banner>}
      {notice && <Banner tone="ok">{notice}</Banner>}

      <BuiltinsSection data={builtins} canEditTimezone={isSuper} onTimezoneSaved={load} />

      {/* ready-made apps (Documents PDF, QR, payment links, Sheets) + instruction templates */}
      <ReadySkillsGallery
        skills={skills}
        canManage={canManage}
        onAdd={(pick) => open('app' in pick ? { mode: 'new', kindHint: 'app', app: pick.app } : { mode: 'new', kindHint: 'prompt', template: pick.template })}
      />

      {KIND_ORDER.map((kind) => (
        <Section
          key={kind}
          kind={kind}
          count={byKind[kind].length}
          action={
            canManage && (
              <Button variant="secondary" size="sm" onClick={() => open({ mode: 'new', kindHint: kind })}>
                <Plus size={13} aria-hidden /> {t(KIND_META[kind].label)}
              </Button>
            )
          }
        >
          {byKind[kind].length === 0 ? (
            <Empty kind={kind} canManage={canManage} onNew={() => open({ mode: 'new', kindHint: kind })} onStarters={loadStarters} />
          ) : (
            <Card className="divide-y divide-line overflow-hidden">
              {byKind[kind].map((s) => (
                <SkillRow key={`${s.entity ?? ''}:${s.id}`} skill={s} {...rowProps} />
              ))}
            </Card>
          )}
        </Section>
      ))}

      {wizard && (
        <SkillWizard
          init={wizard}
          defaultScope={defaultScope}
          readOnly={readOnlyWizard}
          orgTimezone={builtins?.timezone}
          onClose={() => setWizard(null)}
          onSaved={(msg) => {
            setNotice(msg)
            load()
          }}
        />
      )}

      <Modal
        open={!!confirm}
        onClose={() => setConfirm(null)}
        title={t('ai.skills.deleteTitle')}
        size="sm"
        footer={
          <>
            <Button variant="ghost" onClick={() => setConfirm(null)}>
              {t('common.actions.cancel')}
            </Button>
            <Button variant="danger" onClick={() => confirm && remove(confirm)} disabled={busyId === confirm?.id}>
              {busyId === confirm?.id && <Loader2 size={13} className="animate-spin" aria-hidden />}{' '}
              {t('common.actions.delete')}
            </Button>
          </>
        }
      >
        <p className="text-[13px] leading-relaxed text-text-mid">
          <Trans
            k={confirm?.entity ? 'ai.skills.deleteBodyEntity' : 'ai.skills.deleteBody'}
            values={{
              name: <b className="font-semibold text-text-high">{confirm?.name}</b>,
              entity: confirm?.entity_name || confirm?.entity || '',
            }}
          />
        </p>
      </Modal>
    </div>
  )
}

// ---- sections -----------------------------------------------------------------

function Section({
  kind,
  count,
  action,
  children,
}: {
  kind: SkillKind
  count: number
  action?: ReactNode
  children: ReactNode
}) {
  const t = useT()
  const Icon = KIND_ICON[kind]
  return (
    <section data-tour={`skills-${kind}`} aria-labelledby={`skills-${kind}`}>
      <div className="mb-3 flex flex-wrap items-end gap-x-4 gap-y-2 border-b border-line pb-2">
        <div className="min-w-0">
          <div className="eyebrow flex items-center gap-1.5">
            <Icon size={11} strokeWidth={2.2} aria-hidden /> {t(KIND_META[kind].count, { count })}
          </div>
          <h2 id={`skills-${kind}`} className="display mt-0.5 text-[21px] leading-tight text-text-heading">
            {t(KIND_META[kind].plural)}
          </h2>
          <p className="mt-0.5 text-[12.5px] text-text-muted">{t(KIND_META[kind].blurb)}</p>
        </div>
        {action && <div className="ml-auto">{action}</div>}
      </div>
      {children}
    </section>
  )
}

function Empty({
  kind,
  canManage,
  onNew,
  onStarters,
}: {
  kind: SkillKind
  canManage: boolean
  onNew: () => void
  onStarters: () => void
}) {
  const t = useT()
  const text: Record<SkillKind, MessageKey> = {
    prompt: 'ai.skills.empty.prompt',
    tool: 'ai.skills.empty.tool',
    check: 'ai.skills.empty.check',
    app: 'ai.skills.empty.app',
  }
  return (
    <div className="rounded-xl border border-dashed border-line-strong px-4 py-6 text-center">
      <p className="text-[12.5px] text-text-muted">{t(text[kind])}</p>
      {canManage && (
        <div className="mt-3 flex flex-wrap justify-center gap-2">
          <Button variant="secondary" size="sm" onClick={onNew}>
            <Plus size={13} aria-hidden /> {kind === 'app' ? t('ai.skills.setupCalendar') : t('ai.skills.describeOne')}
          </Button>
          {kind === 'prompt' && (
            <Button variant="ghost" size="sm" onClick={onStarters}>
              <BookOpen size={13} aria-hidden /> {t('ai.skills.loadStarters')}
            </Button>
          )}
        </div>
      )}
    </div>
  )
}

function SkillRow({
  skill: s,
  canManage,
  busyId,
  activeEntityName,
  apps,
  onToggle,
  onEdit,
  onDelete,
}: {
  skill: Skill
  canManage: boolean
  busyId: string | null
  activeEntityName: string | null
  apps: AppCatalogEntry[] | null
  onToggle: (s: Skill, on: boolean) => void
  onEdit: (s: Skill) => void
  onDelete: (s: Skill) => void
}) {
  const t = useT()
  const Icon = KIND_ICON[s.kind] ?? BookOpen
  const appCfg = s.kind === 'app' ? calendarConfig(s.config) : null
  const summary = s.useWhen || s.description || s.instructions || t('ai.skills.noTrigger')
  const tests = Array.isArray(s.tests) ? s.tests.length : 0
  const onFail = ON_FAIL_OPTIONS.find((o) => o.value === s.onFail)
  const busy = busyId === s.id
  return (
    <div className={`flex flex-wrap items-start gap-x-3 gap-y-2 px-4 py-3.5 ${s.enabled ? '' : 'bg-fill-subtle'}`}>
      <span
        className={`mt-0.5 grid h-7 w-7 shrink-0 place-items-center rounded-lg border border-line ${
          s.enabled ? 'text-text-high' : 'text-text-faint'
        }`}
        aria-hidden
      >
        <Icon size={14} />
      </span>
      <div className="min-w-0 flex-1 basis-[240px]">
        <div className="flex flex-wrap items-center gap-x-2 gap-y-1">
          <button
            type="button"
            onClick={() => onEdit(s)}
            className={`text-left text-[13.5px] font-semibold hover:underline ${s.enabled ? 'text-text-heading' : 'text-text-muted'}`}
          >
            {s.name}
          </button>
          {s.kind === 'tool' && s.tool_name && (
            <code className="rounded-md bg-fill px-1.5 py-0.5 font-mono text-[11px] text-text-mid">{s.tool_name}()</code>
          )}
          {!s.enabled && (
            <span className="rounded-md border border-line-strong px-1.5 py-0.5 text-[10.5px] font-semibold text-text-muted">
              {t('common.status.off')}
            </span>
          )}
          {activeEntityName ? (
            <ScopeBadge scope={s.scope} entityName={s.entity_name || activeEntityName} />
          ) : (
            s.entity && (
              <span className="rounded-md bg-brand-500/15 px-1.5 py-0.5 text-[10px] font-semibold text-[color:var(--brand-ink)]">
                {s.entity_name || s.entity}
              </span>
            )
          )}
        </div>
        {appCfg && (
          <p className="mt-0.5 flex items-start gap-1.5 text-[12px] font-medium leading-snug text-text-mid">
            {appCfg.source === 'google' ? (
              <ChannelLogo channel="google_calendar" size={12} title="" className="mt-[2px]" />
            ) : (
              <CalendarClock size={12} className="mt-[2px] shrink-0 text-text-muted" aria-hidden />
            )}
            <span className="min-w-0">{appSkillSubtitle(s.config, apps, t)}</span>
          </p>
        )}
        <p className="mt-0.5 line-clamp-2 text-[12.5px] leading-relaxed text-text-muted">{summary}</p>
        <div className="mt-1.5 flex flex-wrap items-center gap-x-3 gap-y-1 text-[11.5px] text-text-muted">
          {s.channels.length ? (
            <span className="inline-flex flex-wrap items-center gap-x-2 gap-y-1">
              {s.channels.map((c) => (
                <span key={c} className="inline-flex items-center gap-1">
                  <ChannelLogo channel={c} size={12} title="" /> {channelLabel(c, t)}
                </span>
              ))}
            </span>
          ) : (
            <span className="inline-flex items-center gap-1">
              <Globe size={11} aria-hidden /> {t('ai.common.allChannels')}
            </span>
          )}
          {(s.kind === 'tool' || s.kind === 'check') && (
            <span className="inline-flex items-center gap-1">
              <CheckCircle size={11} aria-hidden /> {t('ai.skills.tests', { count: tests })}
            </span>
          )}
          {appCfg && (
            <span>
              {appCfg.actions.length
                ? appCfg.actions
                    .map((a) =>
                      a === 'availability'
                        ? t('ai.skills.actionAvailability')
                        : a === 'book'
                          ? t('ai.skills.actionBook')
                          : t('ai.skills.actionCancel'),
                    )
                    .join(' · ')
                : t('ai.skills.noActions')}
            </span>
          )}
          {s.kind === 'check' && onFail && <span>{t('ai.skills.onFailLine', { action: t(onFail.label) })}</span>}
        </div>
      </div>
      <div data-tour="skills-row-actions" className="ml-auto flex shrink-0 items-center gap-1">
        {busy ? (
          <Loader2 size={14} className="mx-3 animate-spin text-text-muted" aria-hidden />
        ) : (
          <Toggle
            on={s.enabled}
            disabled={!canManage}
            onChange={(on) => onToggle(s, on)}
            label={s.enabled ? t('ai.skills.switchOff', { name: s.name }) : t('ai.skills.switchOn', { name: s.name })}
          />
        )}
        <IconButton
          label={canManage ? t('ai.skills.editName', { name: s.name }) : t('ai.skills.viewName', { name: s.name })}
          onClick={() => onEdit(s)}
        >
          {canManage ? <Pencil size={13} aria-hidden /> : <Eye size={13} aria-hidden />}
        </IconButton>
        {canManage && (
          <IconButton label={t('ai.skills.deleteName', { name: s.name })} onClick={() => onDelete(s)} danger>
            <Trash2 size={13} aria-hidden />
          </IconButton>
        )}
      </div>
    </div>
  )
}

function IconButton({
  label,
  onClick,
  danger,
  children,
}: {
  label: string
  onClick: () => void
  danger?: boolean
  children: ReactNode
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      title={label}
      aria-label={label}
      className={`grid h-8 w-8 place-items-center rounded-lg text-text-muted transition-colors ${
        danger ? 'hover:bg-red/10 hover:text-[color:var(--red-ink)]' : 'hover:bg-fill-hover hover:text-text-high'
      }`}
    >
      {children}
    </button>
  )
}

// ---- built-ins ----------------------------------------------------------------

function BuiltinsSection({
  data,
  canEditTimezone,
  onTimezoneSaved,
}: {
  data: BuiltinsPayload | null
  canEditTimezone: boolean
  onTimezoneSaved: () => void
}) {
  const t = useT()
  return (
    <section data-tour="skills-builtins" aria-labelledby="skills-builtin">
      <div className="mb-3 flex flex-wrap items-end gap-x-4 gap-y-2 border-b border-line pb-2">
        <div className="min-w-0">
          <div className="eyebrow flex items-center gap-1.5">
            <Lock size={11} strokeWidth={2.2} aria-hidden /> {t('ai.skills.alwaysOn')}
          </div>
          <h2 id="skills-builtin" className="display mt-0.5 text-[21px] leading-tight text-text-heading">
            {t('ai.skills.builtinsTitle')}
          </h2>
          <p className="mt-0.5 text-[12.5px] text-text-muted">{t('ai.skills.builtinsBody')}</p>
        </div>
        {data && <TimezoneControl data={data} canEdit={canEditTimezone} onSaved={onTimezoneSaved} />}
      </div>
      {!data ? (
        <p className="text-[12.5px] text-text-muted">{t('ai.skills.builtinsUnavailable')}</p>
      ) : (
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-3">
          {data.builtins.map((b) => (
            <Card key={b.name} className="flex flex-col p-4">
              <div className="flex items-start justify-between gap-2">
                <div className="min-w-0">
                  <div className="display text-[16px] leading-tight text-text-heading">{builtinText(b, t).label}</div>
                  <code className="mt-0.5 block font-mono text-[11px] text-text-muted">{b.name}()</code>
                </div>
                <CalendarClock size={15} className="shrink-0 text-text-faint" aria-hidden />
              </div>
              <p className="mt-2 flex-1 text-[12.5px] leading-relaxed text-text-mid">{builtinText(b, t).description}</p>
              <details className="group mt-3 border-t border-line pt-2">
                <summary className="cursor-pointer list-none text-[11.5px] font-semibold text-text-muted hover:text-text-high">
                  <span className="group-open:hidden">{t('ai.skills.showExample')}</span>
                  <span className="hidden group-open:inline">{t('ai.skills.hideExample')}</span>
                </summary>
                <pre className="mt-2 max-h-48 overflow-auto rounded-md bg-well p-2.5 font-mono text-[11px] leading-[1.5] text-text-mid">
                  {`${b.name}(${JSON.stringify(b.example_args)})\n→ ${pretty(b.example)}`}
                </pre>
              </details>
            </Card>
          ))}
        </div>
      )}
    </section>
  )
}

/** The business timezone the built-ins and `ctx.now` use (Setting org_timezone, superadmin). */
function TimezoneControl({
  data,
  canEdit,
  onSaved,
}: {
  data: BuiltinsPayload
  canEdit: boolean
  onSaved: () => void
}) {
  const t = useT()
  const [editing, setEditing] = useState(false)
  const [value, setValue] = useState(data.timezone)
  const [zones, setZones] = useState<string[]>([])
  const [busy, setBusy] = useState(false)
  const [err, setErr] = useState<string | null>(null)

  useEffect(() => {
    const intl = Intl as unknown as { supportedValuesOf?: (k: string) => string[] }
    setZones(intl.supportedValuesOf?.('timeZone') ?? [])
  }, [])

  const local = data.now.slice(11, 16)
  const save = async () => {
    setBusy(true)
    setErr(null)
    try {
      const res = await fetch(`${API}/settings`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json', ...authHeaders() },
        body: JSON.stringify({ key: 'org_timezone', value }),
      })
      if (!res.ok) throw new Error(`HTTP ${res.status}`)
      setEditing(false)
      onSaved()
    } catch (e) {
      setErr(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  if (editing)
    return (
      <div className="ml-auto flex flex-wrap items-center gap-2">
        <Select
          value={value}
          onChange={setValue}
          searchable
          ariaLabel={t('ai.skills.timezoneAria')}
          className="min-w-[220px]"
          options={(zones.includes(value) ? zones : [value, ...zones]).map((z) => ({ value: z, label: z }))}
        />
        <Button size="sm" onClick={save} disabled={busy}>
          {busy && <Loader2 size={12} className="animate-spin" aria-hidden />} {t('common.actions.save')}
        </Button>
        <Button size="sm" variant="ghost" onClick={() => setEditing(false)}>
          {t('common.actions.cancel')}
        </Button>
        {err && <span className="text-[11.5px] text-[color:var(--red-ink)]">{err}</span>}
      </div>
    )
  return (
    <div className="ml-auto flex items-center gap-2 text-[12px] text-text-muted">
      <Clock size={12} aria-hidden />
      <span>
        <span className="font-semibold text-text-high">{data.timezone}</span> {t('ai.skills.nowAt', { time: local })}
      </span>
      {canEdit && (
        <button
          type="button"
          onClick={() => {
            setValue(data.timezone)
            setEditing(true)
          }}
          className="font-semibold text-[color:var(--brand-ink)] hover:underline"
        >
          {t('ai.skills.change')}
        </button>
      )}
    </div>
  )
}

function Banner({ tone, children }: { tone: 'error' | 'ok'; children: ReactNode }) {
  return (
    <div
      role={tone === 'error' ? 'alert' : 'status'}
      className={`flex items-start gap-2 rounded-lg border px-3 py-2 text-[12.5px] ${
        tone === 'error'
          ? 'border-red/30 bg-red/10 text-[color:var(--red-ink)]'
          : 'border-green/30 bg-green/10 text-[color:var(--green-ink)]'
      }`}
    >
      {tone === 'error' ? (
        <AlertCircle size={14} className="mt-0.5 shrink-0" aria-hidden />
      ) : (
        <CheckCircle size={14} className="mt-0.5 shrink-0" aria-hidden />
      )}
      <span className="min-w-0">{children}</span>
    </div>
  )
}
