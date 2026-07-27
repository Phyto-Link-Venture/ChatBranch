'use client'

import { useCallback, useEffect, useState, type ReactNode } from 'react'
import { AlertTriangle, CheckCircle, Loader2, Route, Send } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Select } from '@/components/ui/Select'
import { roleHeaders, getRole } from '@/lib/role'
import { expireSessionOn401 } from '@/lib/auth'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8000/api'

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
    const t = setInterval(load, 60_000)
    return () => clearInterval(t)
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
        setResults((m) => ({ ...m, [a.account_id]: { ok: true, text: `${n} queued` } }))
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
    if (el) el.scrollIntoView({ behavior: 'smooth', block: 'start' })
    else window.location.assign('/settings/assignments')
  }

  if (downs.length === 0) return null

  return (
    <div className={`flex flex-col gap-2 ${className}`} role="alert">
      {downs.map((a) => {
        const r = results[a.account_id]
        return (
          <div
            key={a.account_id}
            className="flex flex-wrap items-center gap-3 rounded-xl border border-red-500/25 bg-red-500/10 px-4 py-3"
          >
            <AlertTriangle size={16} className="shrink-0 text-red-300" aria-hidden />
            <div className="min-w-0 flex-1 text-[12.5px] leading-relaxed">
              <span className="font-bold" style={{ color: 'var(--red-ink)' }}>
                「{a.name}」 is down
              </span>{' '}
              <span className="text-text-mid">
                — {a.affected} contact{a.affected === 1 ? '' : 's'} affected.
              </span>
              {r && (
                <span
                  className="ml-2 font-semibold"
                  style={{ color: r.ok ? 'var(--green-ink)' : 'var(--red-ink)' }}
                >
                  {r.ok ? `${r.text} — recovery templates on the way.` : r.text}
                  {!r.ok && r.needsTemplate && (
                    <>
                      {' '}
                      <button
                        onClick={goRules}
                        className="cursor-pointer font-semibold underline underline-offset-2"
                        style={{ color: 'var(--red-ink)' }}
                      >
                        Configure in Routing Rules
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
                Send recovery templates
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
}

/** Controlled version of the settings-page toggle (that one is uncontrolled). */
function Toggle({
  on,
  onChange,
  disabled = false,
  ariaLabel,
}: {
  on: boolean
  onChange: (v: boolean) => void
  disabled?: boolean
  ariaLabel: string
}) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={on}
      aria-label={ariaLabel}
      disabled={disabled}
      onClick={() => onChange(!on)}
      className="relative h-[22px] w-[38px] shrink-0 cursor-pointer rounded-full transition-colors disabled:cursor-not-allowed disabled:opacity-50"
      style={{ background: on ? '#3B82F6' : 'var(--fill-hover)' }}
    >
      <span
        className="absolute top-[3px] h-4 w-4 rounded-full bg-white transition-all"
        style={{ left: on ? 18 : 3 }}
      />
    </button>
  )
}

function RuleRow({ label, sub, control }: { label: string; sub: ReactNode; control: ReactNode }) {
  return (
    <div className="flex items-center justify-between gap-4 border-b border-line py-3.5">
      <div className="min-w-0">
        <div className="mb-0.5 text-[13px] font-semibold text-text-high">{label}</div>
        <div className="max-w-[520px] text-[11px] leading-relaxed text-text-muted">{sub}</div>
      </div>
      <div className="shrink-0">{control}</div>
    </div>
  )
}

/**
 * Settings → Account Assignment → Routing Rules: how reminder (business) and
 * conversation (personal) numbers cooperate. GET/PUT /api/whatsapp/routing-rules
 * (PUT admin+). Renders defensively with the contract defaults while the
 * backend lands in parallel — a 404 just means "defaults, save retries live".
 */
export function RoutingRulesSettings() {
  const [rules, setRules] = useState<RoutingRules | null>(null)
  const [endpointUp, setEndpointUp] = useState(true)
  const [templates, setTemplates] = useState<string[]>([])
  const [canManage, setCanManage] = useState(false)
  const [busy, setBusy] = useState(false)
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)

  useEffect(() => setCanManage(getRole() !== 'agent'), [])

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
        const t = await api<{ templates: { name: string; status: string }[] }>(`templates${qs}`)
        setTemplates([
          ...new Set(t.templates.filter((x) => x.status === 'APPROVED').map((x) => x.name)),
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
      setMsg({ ok: true, text: 'Routing rules saved — live immediately.' })
    } catch (e) {
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    } finally {
      setBusy(false)
    }
  }

  const templateOptions = [
    { value: '', label: 'None', hint: '(recovery sends disabled)' },
    ...templates.map((n) => ({ value: n, label: n })),
    ...(rules.recovery_template && !templates.includes(rules.recovery_template)
      ? [{ value: rules.recovery_template, label: rules.recovery_template, hint: '(not in the approved list)' }]
      : []),
  ]

  return (
    <div id="routing-rules" className="mt-6 scroll-mt-4 border-t border-line pt-4">
      <div className="mb-1 flex items-center gap-2">
        <Route size={14} className="text-brand-300" aria-hidden />
        <span className="text-[12.5px] font-bold text-text-high">Routing rules</span>
      </div>
      <p className="text-[11px] leading-relaxed text-text-muted">
        How reminder (business) and conversation (personal) numbers cooperate: AI reply pacing,
        the reminder-number redirect, ban recovery and the cold-text guard.
      </p>

      <RuleRow
        label="AI reply pacing"
        sub={
          <>
            <span className="font-semibold text-text-mid">Queued</span> sends one message at a
            time per account with human-like delays (recommended). Outside working hours — quiet
            hours in App Keys — queued replies wait for the working window.{' '}
            <span className="font-semibold text-text-mid">Instant</span> replies immediately.
          </>
        }
        control={
          <Select
            value={rules.ai_reply_mode}
            onChange={(v) => set('ai_reply_mode', v as RoutingRules['ai_reply_mode'])}
            options={[
              { value: 'queued', label: 'Queued', hint: '(recommended)' },
              { value: 'instant', label: 'Instant' },
            ]}
            className="w-[200px]"
            size="md"
            disabled={!canManage}
            ariaLabel="AI reply pacing"
          />
        }
      />

      <RuleRow
        label="Business-number chatbot"
        sub={
          <>
            Default Chatbot state for reminder (business) numbers.{' '}
            <span className="font-semibold text-text-mid">Off</span>: the business number stays a
            template/notification channel — inbound replies get the redirect below, and the bot
            only answers chats where you tick Chatbot on.{' '}
            <span className="font-semibold text-text-mid">On</span>: the bot converses on the
            business number by default (replies are instant — queued pacing only protects
            personal numbers). Per-chat Chatbot ticks always win over this default.
          </>
        }
        control={
          <Toggle
            on={rules.reminder_ai_default === 'on'}
            onChange={(v) => set('reminder_ai_default', v ? 'on' : 'off')}
            disabled={!canManage}
            ariaLabel="Business-number chatbot default"
          />
        }
      />

      <RuleRow
        label="Business-number redirect"
        sub="When a contact replies to a reminder (business) number and the chatbot is off for that chat, auto-send one message per 24h pointing them somewhere a conversation can happen."
        control={
          <Toggle
            on={rules.business_redirect}
            onChange={(v) => set('business_redirect', v)}
            disabled={!canManage}
            ariaLabel="Business-number redirect"
          />
        }
      />

      {rules.business_redirect && (
        <RuleRow
          label="Redirect to"
          sub={
            <>
              <span className="font-semibold text-text-mid">Conversation number</span> sends a
              wa.me link to the contact&rsquo;s assigned conversation (personal) number.{' '}
              <span className="font-semibold text-text-mid">Customer support</span> tells them
              this line is automated and to contact your human support instead.{' '}
              <span className="font-semibold text-text-mid">Your template</span> answers with an
              approved template you pick below.
            </>
          }
          control={
            <Select
              value={rules.business_redirect_target}
              onChange={(v) =>
                set('business_redirect_target', v as RoutingRules['business_redirect_target'])
              }
              options={[
                { value: 'conversation', label: 'Conversation number', hint: '(recommended)' },
                { value: 'support', label: 'Customer support' },
                { value: 'template', label: 'Your template' },
              ]}
              className="w-[240px]"
              size="md"
              disabled={!canManage}
              ariaLabel="Redirect target"
            />
          }
        />
      )}

      {rules.business_redirect && rules.business_redirect_target === 'template' && (
        <RuleRow
          label="Auto-reply template"
          sub="Sent from the business number itself when a contact replies to it (once per 24h). Leave on None and the redirect falls back to the wa.me conversation-number handoff."
          control={
            <Select
              value={rules.business_redirect_template}
              onChange={(v) => set('business_redirect_template', v)}
              options={[
                { value: '', label: 'None', hint: '(falls back to wa.me handoff)' },
                ...templates.map((n) => ({ value: n, label: n })),
                ...(rules.business_redirect_template &&
                !templates.includes(rules.business_redirect_template)
                  ? [{
                      value: rules.business_redirect_template,
                      label: rules.business_redirect_template,
                      hint: '(not in the approved list)',
                    }]
                  : []),
              ]}
              className="w-[280px]"
              size="md"
              disabled={!canManage}
              ariaLabel="Business auto-reply template"
            />
          }
        />
      )}

      {rules.business_redirect && rules.business_redirect_target === 'support' && (
        <RuleRow
          label="Support contact"
          sub="Included in the auto-reply so the contact knows where a human answers — a phone number, email or link. Empty sends generic wording."
          control={
            <input
              value={rules.support_contact}
              onChange={(e) => set('support_contact', e.target.value)}
              maxLength={200}
              placeholder="e.g. +60 3-1234 5678"
              disabled={!canManage}
              aria-label="Support contact"
              className="h-9 w-[240px] rounded-lg border border-line bg-fill px-3 text-[12.5px] text-text-high outline-none placeholder:text-text-faint focus:border-brand-400/50 disabled:cursor-not-allowed disabled:opacity-50"
            />
          }
        />
      )}

      <RuleRow
        label="Ban recovery"
        sub="How recovery starts when a conversation number goes down with contacts assigned: one click from the alert/notification, or fully automatic."
        control={
          <Select
            value={rules.recovery_mode}
            onChange={(v) => set('recovery_mode', v as RoutingRules['recovery_mode'])}
            options={[
              { value: 'one_click', label: 'One-click from notification', hint: '(recommended)' },
              { value: 'auto', label: 'Automatic' },
            ]}
            className="w-[280px]"
            size="md"
            disabled={!canManage}
            ariaLabel="Ban recovery mode"
          />
        }
      />

      <RuleRow
        label="Recovery template"
        sub="Sent via the business number to affected contacts when a conversation number goes down, pointing them to the replacement number. Approved templates only."
        control={
          <Select
            value={rules.recovery_template}
            onChange={(v) => set('recovery_template', v)}
            options={templateOptions}
            className="w-[280px]"
            size="md"
            disabled={!canManage}
            ariaLabel="Recovery template"
          />
        }
      />

      <RuleRow
        label="Cold-text guard"
        sub="First-contact messages to contacts who never wrote in are WhatsApp's top ban trigger for conversation numbers. Block stops them (recommended), Warn flags them for confirmation, Allow sends anyway — at your own ban risk."
        control={
          <Select
            value={rules.cold_text_guard}
            onChange={(v) => set('cold_text_guard', v as RoutingRules['cold_text_guard'])}
            options={[
              { value: 'block', label: 'Block', hint: '(recommended)' },
              { value: 'warn', label: 'Warn' },
              { value: 'allow', label: 'Allow' },
            ]}
            className="w-[200px]"
            size="md"
            disabled={!canManage}
            ariaLabel="Cold-text guard"
          />
        }
      />

      <div className="mt-4 flex flex-wrap items-center gap-2">
        {canManage ? (
          <Button variant="primary" onClick={save} disabled={busy}>
            {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <CheckCircle size={13} aria-hidden />}{' '}
            Save routing rules
          </Button>
        ) : (
          <span className="text-[11px] text-text-muted">Routing rules are managed by admins.</span>
        )}
        {msg && (
          <span className={`text-[12px] font-semibold ${msg.ok ? 'text-green-300' : 'text-red-300'}`}>
            {msg.text}
          </span>
        )}
      </div>
      {!endpointUp && (
        <p className="mt-2 text-[11px] text-text-muted">
          Routing-rules endpoint not reachable yet — showing defaults; saving retries against the
          live API.
        </p>
      )}
    </div>
  )
}
