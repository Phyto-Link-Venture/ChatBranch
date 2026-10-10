'use client'

import { useCallback, useEffect, useState, type ReactNode } from 'react'
import { AlertTriangle, CheckCircle, Loader2, Route, Send } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Card } from '@/components/ui/Card'
import { Select } from '@/components/ui/Select'
import { Toggle } from '@/components/ui/Toggle'
import { TagListInput } from '@/components/ui/TagListInput'
import { Advanced } from '@/components/ui/Advanced'
import { roleHeaders, getRole } from '@/lib/role'
import { expireSessionOn401 } from '@/lib/auth'
import { useAccess } from '@/lib/useAccess'
import { Trans, useT } from '@/i18n'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}/whatsapp/${path}`, {
    headers: { 'Content-Type': 'application/json', ...roleHeaders() },
    ...init,
  })
  const data = await res.json().catch(() => ({}))
  if (expireSessionOn401(res)) throw new Error('Session expired — redirecting to login…')
  if (!res.ok) throw new Error((data as { detail?: string }).detail || `HTTP ${res.status}`)
  return data as T
}

// ── Recovery banner ───────────────────────────────────────────────────────────

interface RecoveryAccount {
  account_id: string
  name: string
  health: string
  affected: number
}

/**
 * Alarm banner for conversation numbers that are DOWN with contacts assigned —
 * GET /api/whatsapp/recovery. Renders nothing while the endpoint is missing
 * (backend landing in parallel), unreachable, or the list is empty, so it is
 * safe to mount anywhere (Account Assignment tab + dashboard health card).
 * [Send recovery templates] POSTs /recovery/<account_id> (admin+): the
 * business number templates every affected contact toward the replacement
 * conversation number. 409 = no recovery template configured → link to the
 * Routing Rules card.
 */
export function RecoveryBanner({ className = '' }: { className?: string }) {
  const t = useT()
  const [downs, setDowns] = useState<RecoveryAccount[]>([])
  const [canManage, setCanManage] = useState(false)
  const [busyId, setBusyId] = useState<string | null>(null)
  const [results, setResults] = useState<
    Record<string, { ok: boolean; text: string; needsTemplate?: boolean }>
  >({})

  useEffect(() => setCanManage(getRole() !== 'agent'), [])

  const load = useCallback(async () => {
    try {
      const d = await api<{ accounts: RecoveryAccount[] }>('recovery')
      setDowns(Array.isArray(d.accounts) ? d.accounts : [])
    } catch {
      setDowns([]) // endpoint mid-flight / API down — no banner, never crash
    }
  }, [])

  useEffect(() => {
    load()
    const timer = setInterval(load, 60_000)
    return () => clearInterval(timer)
  }, [load])

  const send = async (a: RecoveryAccount) => {
    setBusyId(a.account_id)
    try {
      // raw fetch (not api()) — the 409 branch needs the status code
      const res = await fetch(`${API}/whatsapp/recovery/${encodeURIComponent(a.account_id)}`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', ...roleHeaders() },
      })
      if (expireSessionOn401(res)) return
      const data = (await res.json().catch(() => ({}))) as { queued?: number; detail?: string }
      if (res.ok) {
        const n = data.queued ?? 0
        setResults((m) => ({ ...m, [a.account_id]: { ok: true, text: t('admin.routing.recovery.queued', { count: n }) } }))
      } else {
        setResults((m) => ({
          ...m,
          [a.account_id]: {
            ok: false,
            text: data.detail || `HTTP ${res.status}`,
            needsTemplate: res.status === 409,
          },
        }))
      }
    } catch (e) {
      setResults((m) => ({
        ...m,
        [a.account_id]: { ok: false, text: e instanceof Error ? e.message : String(e) },
      }))
    } finally {
      setBusyId(null)
    }
  }

  /** Jump to the Routing Rules card — same page when present, else Settings. */
  const goRules = () => {
    const el = document.getElementById('routing-rules')
    if (el) {
      // the recovery template sits under Advanced — open it on the way there
      window.dispatchEvent(new Event('routing-rules:open'))
      el.scrollIntoView({ behavior: 'smooth', block: 'start' })
    } else window.location.assign('/assignments#routing-rules')
  }

  if (downs.length === 0) return null

  return (
    <div className={`flex flex-col gap-2 ${className}`} role="alert">
      {downs.map((a) => {
        const r = results[a.account_id]
        return (
          <div
            key={a.account_id}
            className="flex flex-wrap items-center gap-3 rounded-xl border border-red/25 bg-red/10 px-4 py-3"
          >
            <AlertTriangle size={16} className="shrink-0 text-[color:var(--red-ink)]" aria-hidden />
            <div className="min-w-0 flex-1 text-[12.5px] leading-relaxed">
              <span className="font-bold" style={{ color: 'var(--red-ink)' }}>
                {t('admin.routing.recovery.isDown', { name: a.name })}
              </span>{' '}
              <span className="text-text-mid">{t('admin.routing.recovery.affected', { count: a.affected })}</span>
              {r && (
                <span
                  className="ml-2 font-semibold"
                  style={{ color: r.ok ? 'var(--green-ink)' : 'var(--red-ink)' }}
                >
                  {r.ok ? t('admin.routing.recovery.onTheWay', { queued: r.text }) : r.text}
                  {!r.ok && r.needsTemplate && (
                    <>
                      {' '}
                      <button
                        onClick={goRules}
                        className="cursor-pointer font-semibold underline underline-offset-2"
                        style={{ color: 'var(--red-ink)' }}
                      >
                        {t('admin.routing.recovery.configure')}
                      </button>
                    </>
                  )}
                </span>
              )}
            </div>
            {canManage && (
              <Button variant="danger" onClick={() => send(a)} disabled={busyId !== null}>
                {busyId === a.account_id ? (
                  <Loader2 size={13} className="animate-spin" aria-hidden />
                ) : (
                  <Send size={13} aria-hidden />
                )}{' '}
                {t('admin.routing.recovery.send')}
              </Button>
            )}
          </div>
        )
      })}
    </div>
  )
}

// ── Routing Rules card ────────────────────────────────────────────────────────

interface RoutingRules {
  ai_reply_mode: 'queued' | 'instant'
  reminder_ai_default: 'off' | 'on'
  business_redirect: boolean
  business_redirect_target: 'conversation' | 'support' | 'template'
  support_contact: string
  business_redirect_template: string
  recovery_mode: 'one_click' | 'auto'
  recovery_template: string
  cold_text_guard: 'block' | 'warn' | 'allow'
  // org-wide words that ADD to the built-in STOP / START keywords
  opt_out_keywords: string[]
  opt_in_keywords: string[]
}

const DEFAULTS: RoutingRules = {
  ai_reply_mode: 'queued',
  reminder_ai_default: 'off',
  business_redirect: true,
  business_redirect_target: 'conversation',
  support_contact: '',
  business_redirect_template: '',
  recovery_mode: 'one_click',
  recovery_template: '',
  cold_text_guard: 'block',
  opt_out_keywords: [],
  opt_in_keywords: [],
}

function RuleRow({
  label,
  sub,
  control,
  flush = false,
}: {
  label: string
  sub: ReactNode
  control: ReactNode
  /** inside <Advanced>: no own border / padding (the section spaces its rows) */
  flush?: boolean
}) {
  return (
    <div
      className={`flex flex-wrap items-center justify-between gap-x-6 gap-y-2.5 ${
        flush ? '' : 'border-t border-line px-4 py-3.5 sm:px-5'
      }`}
    >
      <div className="min-w-[220px] flex-1">
        <div className="mb-0.5 text-[13.5px] font-semibold text-text-high">{label}</div>
        <div className="max-w-[560px] text-[12px] leading-relaxed text-text-muted">{sub}</div>
      </div>
      <div className="max-w-full shrink-0">{control}</div>
    </div>
  )
}

/**
 * Account assignment → Routing rules: how reminder (business) and
 * conversation (personal) numbers cooperate. GET/PUT /api/whatsapp/routing-rules
 * (PUT admin+). Renders defensively with the contract defaults while the
 * backend lands in parallel — a 404 just means "defaults, save retries live".
 */
export function RoutingRulesSettings() {
  const t = useT()
  const [rules, setRules] = useState<RoutingRules | null>(null)
  const [endpointUp, setEndpointUp] = useState(true)
  const [templates, setTemplates] = useState<string[]>([])
  const { ready, can } = useAccess()
  const canManage = ready && can('account_assignment.manage')
  const [busy, setBusy] = useState(false)
  // the recovery banner's "Configure" (or a #routing-rules link) needs what's under Advanced
  const [jumpedHere, setJumpedHere] = useState(false)
  useEffect(() => {
    if (window.location.hash === '#routing-rules') setJumpedHere(true)
    const open = () => setJumpedHere(true)
    window.addEventListener('routing-rules:open', open)
    return () => window.removeEventListener('routing-rules:open', open)
  }, [])
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)

  useEffect(() => {
    api<Partial<RoutingRules>>('routing-rules')
      .then((d) => setRules({ ...DEFAULTS, ...d }))
      .catch(() => {
        setRules({ ...DEFAULTS })
        setEndpointUp(false)
      })
  }, [])

  // Recovery-template picker — APPROVED templates from the reminder (business)
  // account that would send them (recovery sends go out via the business
  // number); falls back to any cloud account, then the sandbox set.
  useEffect(() => {
    ;(async () => {
      try {
        const a = await api<{
          accounts: { id: string; provider: string; role?: string; waba_id?: string; configured: boolean }[]
        }>('accounts')
        const cloud = a.accounts.filter((x) => x.provider === 'cloud')
        const src =
          cloud.find((x) => (x.role ?? 'reminder') === 'reminder' && x.configured && x.waba_id) ||
          cloud.find((x) => x.waba_id) ||
          cloud[0]
        const qs = src ? `?account_id=${encodeURIComponent(src.id)}` : ''
        const tpl = await api<{ templates: { name: string; status: string }[] }>(`templates${qs}`)
        setTemplates([
          ...new Set(tpl.templates.filter((x) => x.status === 'APPROVED').map((x) => x.name)),
        ])
      } catch {
        setTemplates([]) // picker still works — the saved value stays selectable
      }
    })()
  }, [])

  if (rules === null) return null // first GET in flight

  const set = <K extends keyof RoutingRules>(k: K, v: RoutingRules[K]) =>
    setRules({ ...rules, [k]: v })

  const save = async () => {
    setBusy(true)
    setMsg(null)
    try {
      const next = await api<Partial<RoutingRules>>('routing-rules', {
        method: 'PUT',
        body: JSON.stringify(rules),
      })
      setRules({ ...DEFAULTS, ...rules, ...next })
      setEndpointUp(true)
      setMsg({ ok: true, text: t('admin.routing.saved') })
    } catch (e) {
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    } finally {
      setBusy(false)
    }
  }

  const templateOptions = [
    { value: '', label: t('common.status.none'), hint: t('admin.routing.recoveryDisabledHint') },
    ...templates.map((n) => ({ value: n, label: n })),
    ...(rules.recovery_template && !templates.includes(rules.recovery_template)
      ? [{ value: rules.recovery_template, label: rules.recovery_template, hint: t('admin.routing.notApprovedHint') }]
      : []),
  ]

  // customised advanced rules (vs the contract defaults) — never hidden
  const advancedCount = [
    rules.reminder_ai_default !== DEFAULTS.reminder_ai_default,
    (rules.support_contact ?? '').trim() !== '',
    rules.recovery_mode !== DEFAULTS.recovery_mode,
    !!rules.recovery_template,
    rules.cold_text_guard !== DEFAULTS.cold_text_guard,
    (rules.opt_out_keywords?.length ?? 0) > 0,
    (rules.opt_in_keywords?.length ?? 0) > 0,
  ].filter(Boolean).length
  // the support number is required once the redirect points at it
  const advancedForceOpen =
    jumpedHere || advancedCount > 0 || (rules.business_redirect && rules.business_redirect_target === 'support')

  return (
    <section id="routing-rules" data-tour="routing-rules" className="scroll-mt-4">
      <Card className="overflow-hidden">
        <div className="flex items-start gap-3 px-4 pb-3.5 pt-4 sm:px-5">
          <span className="grid h-8 w-8 shrink-0 place-items-center rounded-lg bg-fill text-text-high">
            <Route size={15} aria-hidden />
          </span>
          <div className="min-w-0">
            <h2 className="display text-[19px] leading-tight text-text-heading">{t('admin.routing.title')}</h2>
            <p className="mt-0.5 text-[12.5px] leading-relaxed text-text-muted">{t('admin.routing.subtitle')}</p>
          </div>
        </div>

        <RuleRow
          label={t('admin.routing.pacing.label')}
          sub={
            <Trans
              k="admin.routing.pacing.sub"
              values={{
                queued: <span className="font-semibold text-text-mid">{t('admin.routing.pacing.queued')}</span>,
                instant: <span className="font-semibold text-text-mid">{t('admin.routing.pacing.instant')}</span>,
              }}
            />
          }
          control={
            <Select
              value={rules.ai_reply_mode}
              onChange={(v) => set('ai_reply_mode', v as RoutingRules['ai_reply_mode'])}
              options={[
                { value: 'queued', label: t('admin.routing.pacing.queued'), hint: t('admin.routing.recommended') },
                { value: 'instant', label: t('admin.routing.pacing.instant') },
              ]}
              className="w-[200px] max-w-full"
              size="md"
              disabled={!canManage}
              ariaLabel={t('admin.routing.pacing.label')}
            />
          }
        />

        <RuleRow
          label={t('admin.routing.redirect.label')}
          sub={t('admin.routing.redirect.sub')}
          control={
            <Toggle
              on={rules.business_redirect}
              onChange={(v) => set('business_redirect', v)}
              disabled={!canManage}
              label={t('admin.routing.redirect.label')}
            />
          }
        />

        {rules.business_redirect && (
          <RuleRow
            label={t('admin.routing.target.label')}
            sub={
              <Trans
                k="admin.routing.target.sub"
                values={{
                  conversation: <span className="font-semibold text-text-mid">{t('admin.routing.target.conversation')}</span>,
                  support: <span className="font-semibold text-text-mid">{t('admin.routing.target.support')}</span>,
                  template: <span className="font-semibold text-text-mid">{t('admin.routing.target.template')}</span>,
                }}
              />
            }
            control={
              <Select
                value={rules.business_redirect_target}
                onChange={(v) =>
                  set('business_redirect_target', v as RoutingRules['business_redirect_target'])
                }
                options={[
                  { value: 'conversation', label: t('admin.routing.target.conversation'), hint: t('admin.routing.recommended') },
                  { value: 'support', label: t('admin.routing.target.support') },
                  { value: 'template', label: t('admin.routing.target.template') },
                ]}
                className="w-[240px] max-w-full"
                size="md"
                disabled={!canManage}
                ariaLabel={t('admin.routing.target.aria')}
              />
            }
          />
        )}

        {rules.business_redirect && rules.business_redirect_target === 'template' && (
          <RuleRow
            label={t('admin.routing.autoReply.label')}
            sub={t('admin.routing.autoReply.sub')}
            control={
              <Select
                value={rules.business_redirect_template}
                onChange={(v) => set('business_redirect_template', v)}
                options={[
                  { value: '', label: t('common.status.none'), hint: t('admin.routing.autoReply.noneHint') },
                  ...templates.map((n) => ({ value: n, label: n })),
                  ...(rules.business_redirect_template &&
                  !templates.includes(rules.business_redirect_template)
                    ? [{
                        value: rules.business_redirect_template,
                        label: rules.business_redirect_template,
                        hint: t('admin.routing.notApprovedHint'),
                      }]
                    : []),
                ]}
                className="w-[280px] max-w-full"
                size="md"
                disabled={!canManage}
                ariaLabel={t('admin.routing.autoReply.aria')}
              />
            }
          />
        )}

        <div className="border-t border-line px-4 py-3.5 sm:px-5">
          <Advanced count={advancedCount} forceOpen={advancedForceOpen}>
            <RuleRow
              flush
              label={t('admin.routing.bot.label')}
              sub={
                <Trans
                  k="admin.routing.bot.sub"
                  values={{
                    off: <span className="font-semibold text-text-mid">{t('common.status.off')}</span>,
                    on: <span className="font-semibold text-text-mid">{t('common.status.on')}</span>,
                  }}
                />
              }
              control={
                <Toggle
                  on={rules.reminder_ai_default === 'on'}
                  onChange={(v) => set('reminder_ai_default', v ? 'on' : 'off')}
                  disabled={!canManage}
                  label={t('admin.routing.bot.aria')}
                />
              }
            />

            {rules.business_redirect && rules.business_redirect_target === 'support' && (
              <RuleRow
                flush
                label={t('admin.routing.support.label')}
                sub={t('admin.routing.support.sub')}
                control={
                  <input
                    value={rules.support_contact}
                    onChange={(e) => set('support_contact', e.target.value)}
                    maxLength={200}
                    placeholder={t('admin.routing.support.placeholder')}
                    disabled={!canManage}
                    aria-label={t('admin.routing.support.label')}
                    className="h-9 w-[240px] max-w-full rounded-lg border border-line bg-fill px-3 text-[12.5px] text-text-high outline-none placeholder:text-text-faint focus:border-brand-400/50 disabled:cursor-not-allowed disabled:opacity-50"
                  />
                }
              />
            )}

            <RuleRow
              flush
              label={t('admin.routing.ban.label')}
              sub={t('admin.routing.ban.sub')}
              control={
                <Select
                  value={rules.recovery_mode}
                  onChange={(v) => set('recovery_mode', v as RoutingRules['recovery_mode'])}
                  options={[
                    { value: 'one_click', label: t('admin.routing.ban.oneClick'), hint: t('admin.routing.recommended') },
                    { value: 'auto', label: t('admin.routing.ban.auto') },
                  ]}
                  className="w-[280px] max-w-full"
                  size="md"
                  disabled={!canManage}
                  ariaLabel={t('admin.routing.ban.aria')}
                />
              }
            />

            <RuleRow
              flush
              label={t('admin.routing.recoveryTemplate.label')}
              sub={t('admin.routing.recoveryTemplate.sub')}
              control={
                <Select
                  value={rules.recovery_template}
                  onChange={(v) => set('recovery_template', v)}
                  options={templateOptions}
                  className="w-[280px] max-w-full"
                  size="md"
                  disabled={!canManage}
                  ariaLabel={t('admin.routing.recoveryTemplate.label')}
                />
              }
            />

            <RuleRow
              flush
              label={t('admin.routing.cold.label')}
              sub={t('admin.routing.cold.sub')}
              control={
                <Select
                  value={rules.cold_text_guard}
                  onChange={(v) => set('cold_text_guard', v as RoutingRules['cold_text_guard'])}
                  options={[
                    { value: 'block', label: t('admin.routing.cold.block'), hint: t('admin.routing.recommended') },
                    { value: 'warn', label: t('admin.routing.cold.warn') },
                    { value: 'allow', label: t('admin.routing.cold.allow') },
                  ]}
                  className="w-[200px] max-w-full"
                  size="md"
                  disabled={!canManage}
                  ariaLabel={t('admin.routing.cold.label')}
                />
              }
            />

            <RuleRow
              flush
              label={t('admin.routing.optOut.label')}
              sub={t('admin.routing.optOut.sub')}
              control={
                <div className="w-[280px] max-w-full">
                  <TagListInput
                    value={rules.opt_out_keywords}
                    onChange={(v) => set('opt_out_keywords', v)}
                    placeholder={t('admin.routing.optOut.placeholder')}
                    addLabel={t('common.actions.add')}
                    disabled={!canManage}
                  />
                </div>
              }
            />

            <RuleRow
              flush
              label={t('admin.routing.optIn.label')}
              sub={t('admin.routing.optIn.sub')}
              control={
                <div className="w-[280px] max-w-full">
                  <TagListInput
                    value={rules.opt_in_keywords}
                    onChange={(v) => set('opt_in_keywords', v)}
                    placeholder={t('admin.routing.optIn.placeholder')}
                    addLabel={t('common.actions.add')}
                    disabled={!canManage}
                  />
                </div>
              }
            />
          </Advanced>
        </div>

        <div data-tour="routing-save" className="flex flex-wrap items-center gap-2 border-t border-line bg-surface px-4 py-3 sm:px-5">
          {canManage ? (
            <Button variant="primary" onClick={save} disabled={busy}>
              {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <CheckCircle size={13} aria-hidden />}{' '}
              {t('admin.routing.save')}
            </Button>
          ) : (
            <span className="text-[12px] text-text-muted">
              {t('admin.routing.needsPermission')}
            </span>
          )}
          {msg && (
            <span
              role={msg.ok ? 'status' : 'alert'}
              className="text-[12px] font-semibold"
              style={{ color: msg.ok ? 'var(--green-ink)' : 'var(--red-ink)' }}
            >
              {msg.text}
            </span>
          )}
        </div>
        {!endpointUp && (
          <p className="border-t border-line px-4 py-2 text-[11.5px] text-text-muted sm:px-5">
            {t('admin.routing.endpointDown')}
          </p>
        )}
      </Card>
    </section>
  )
}
