'use client'

import { useEffect, useRef, useState } from 'react'
import { AlertCircle, ExternalLink, Loader2, MessageSquareReply, Phone, RefreshCw } from 'lucide-react'
import { authHeaders } from '@/lib/auth'
import type { FlowConfig } from '@/lib/flow/types'

/**
 * WhatsApp "Message type" block inside an Action node's settings — shown only
 * when the WhatsApp channel is selected. Free text needs Meta's 24h service
 * window; a pre-approved TEMPLATE can be sent anytime, which is what scheduled
 * reminders and status updates need.
 *
 * Writes the exact node-config contract the backend flow engine executes:
 *   waMode ('text' | 'template'), templateName, templateLanguage,
 *   templateVariables ({"1": "{{name}}", …} — position → value, values may
 *   hold {{placeholders}} rendered from the CRM at send time).
 *
 * Templates come from GET /api/whatsapp/templates (bearer-authed). Without a
 * linked account the endpoint serves the sandbox demo set — the picker works
 * with those too.
 */

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

interface WaTemplateButton {
  type: 'QUICK_REPLY' | 'URL' | 'PHONE_NUMBER' | string
  text: string
  url?: string
  phone_number?: string
}

interface WaTemplate {
  name: string
  language: string
  category?: string
  status: string
  body: string
  buttons?: WaTemplateButton[]
  /** Settings → Message Templates slot bindings ({{n}} → CRM field key). */
  bindings?: string[]
  /** Highest {{n}} index in the body (the server precomputes it). */
  variables?: number
}

/** CRM placeholders the flow engine can render into a template variable —
 *  the engine-fillable subset of lib/flow/variables.ts (variables_for()). */
const QUICK_VARS: { key: string; label: string }[] = [
  { key: 'name', label: 'Contact name' },
  { key: 'amount', label: 'Record amount' },
  { key: 'days_overdue', label: 'Days past due' },
  { key: 'due_date', label: 'Due date' },
  { key: 'link', label: 'Record link' },
  { key: 'company', label: 'Company name' },
  // Filled at SEND time from the account routing picks for this contact, not at
  // enqueue time like the rest — so the message names the number it actually
  // went out from even if health failover moves it.
  { key: 'whatsapp_assignment_account_number', label: 'Sending number (assigned account in settings -> account assignments)' },
  { key: 'whatsapp_assignment_account_name', label: 'Sending account name' },
]

const BUTTON_ICON: Record<string, typeof MessageSquareReply> = {
  QUICK_REPLY: MessageSquareReply,
  URL: ExternalLink,
  PHONE_NUMBER: Phone,
}

const PLACEHOLDER_RE = /(\{\{\s*\d+\s*\}\})/g

const variableCount = (body: string) =>
  Math.max(0, ...[...(body || '').matchAll(/\{\{\s*(\d+)\s*\}\}/g)].map((m) => parseInt(m[1], 10)))

/** One fetch per page visit — clicking between nodes must not re-hit the API. */
let templatesCache: { templates: WaTemplate[]; sandbox: boolean } | null = null

/** Body preview with {{n}} slots live-substituted from templateVariables. */
function PreviewBody({ body, values }: { body: string; values: Record<string, string> }) {
  return (
    <>
      {body.split(PLACEHOLDER_RE).map((part, i) => {
        const m = part.match(/^\{\{\s*(\d+)\s*\}\}$/)
        if (!m) return <span key={i}>{part}</span>
        const v = (values[m[1]] ?? '').trim()
        return (
          <mark key={i} className={`tpl-ph${v ? ' filled' : ''}`} title={`Variable {{${m[1]}}}`}>
            {v || `{{${m[1]}}}`}
          </mark>
        )
      })}
    </>
  )
}

export default function WaTemplateConfig({
  config,
  onPatch,
}: {
  config: FlowConfig
  onPatch: (patch: FlowConfig) => void
}) {
  const mode: 'text' | 'template' = config.waMode === 'template' ? 'template' : 'text'
  const templateName = typeof config.templateName === 'string' ? config.templateName : ''
  const templateLanguage = typeof config.templateLanguage === 'string' ? config.templateLanguage : ''
  const rawVars = config.templateVariables
  const values: Record<string, string> =
    rawVars && typeof rawVars === 'object' && !Array.isArray(rawVars)
      ? (rawVars as Record<string, string>)
      : {}

  const [data, setData] = useState<{ templates: WaTemplate[]; sandbox: boolean } | null>(templatesCache)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  /** Which {{n}} input the quick-insert chips target (last focused). */
  const [focusSlot, setFocusSlot] = useState('1')
  const inputRefs = useRef<Record<string, HTMLInputElement | null>>({})

  const load = async (force = false) => {
    if (templatesCache && !force) {
      setData(templatesCache)
      return
    }
    setLoading(true)
    setError(null)
    try {
      const res = await fetch(`${API}/whatsapp/templates`, {
        headers: authHeaders(),
        cache: 'no-store',
      })
      const body = await res.json().catch(() => ({}))
      if (!res.ok) throw new Error((body as { detail?: string }).detail || `HTTP ${res.status}`)
      templatesCache = {
        templates: Array.isArray(body.templates) ? body.templates : [],
        sandbox: !!body.sandbox,
      }
      setData(templatesCache)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    if (mode === 'template' && !data) load()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [mode])

  const selected =
    data?.templates.find(
      (t) => t.name === templateName && (!templateLanguage || t.language === templateLanguage),
    ) ?? null

  /** {{n}} slots to edit: from the picked template, else from saved values. */
  const slotCount = selected
    ? (selected.variables ?? variableCount(selected.body))
    : Math.max(0, ...Object.keys(values).map((k) => parseInt(k, 10) || 0))
  const slots = Array.from({ length: slotCount }, (_, i) => String(i + 1))

  const pick = (t: WaTemplate) => {
    const count = t.variables ?? variableCount(t.body)
    const same = t.name === templateName && t.language === templateLanguage
    const vars: Record<string, string> = {}
    for (let i = 1; i <= count; i++) {
      const binding = t.bindings?.[i - 1]
      // Re-picking the same template keeps typed values; a different template
      // starts from its Settings-page bindings (slot → CRM field) if any.
      vars[String(i)] = (same && values[String(i)]) || (binding ? `{{${binding}}}` : '')
    }
    onPatch({
      waMode: 'template',
      templateName: t.name,
      templateLanguage: t.language,
      templateVariables: vars,
    })
    setFocusSlot('1')
  }

  const setValue = (slot: string, value: string) =>
    onPatch({ templateVariables: { ...values, [slot]: value } })

  /** Insert a {{placeholder}} at the cursor of the last-focused slot input. */
  const insert = (token: string) => {
    const slot = slots.includes(focusSlot) ? focusSlot : slots[0]
    if (!slot) return
    const el = inputRefs.current[slot]
    const cur = values[slot] ?? ''
    const start = el?.selectionStart ?? cur.length
    const end = el?.selectionEnd ?? cur.length
    setValue(slot, cur.slice(0, start) + token + cur.slice(end))
    requestAnimationFrame(() => {
      if (!el) return
      el.focus()
      const pos = start + token.length
      el.setSelectionRange(pos, pos)
    })
  }

  return (
    <div className="wa-tpl-section">
      <div className="config-field">
        <span>WhatsApp message type</span>
        <div className="wa-mode-switch" role="radiogroup" aria-label="WhatsApp message type">
          <button
            type="button"
            role="radio"
            aria-checked={mode === 'text'}
            className={`wa-mode-btn${mode === 'text' ? ' on' : ''}`}
            onClick={() => onPatch({ waMode: 'text' })}
          >
            Free text
            <small>24h window only</small>
          </button>
          <button
            type="button"
            role="radio"
            aria-checked={mode === 'template'}
            className={`wa-mode-btn${mode === 'template' ? ' on' : ''}`}
            onClick={() => onPatch({ waMode: 'template' })}
          >
            Template
            <small>works anytime</small>
          </button>
        </div>
        <small>
          {mode === 'text'
            ? 'Free text only delivers inside Meta’s 24-hour service window (after the customer last wrote). Scheduled reminders outside it need a template.'
            : 'Pre-approved templates can be sent at any time — the right choice for automatic reminders.'}
        </small>
      </div>

      {mode === 'template' && (
        <div className="config-field">
          <span className="tpl-list-head">
            Template
            <button
              type="button"
              className="tpl-refresh"
              onClick={() => load(true)}
              title="Reload templates"
              aria-label="Reload templates"
              disabled={loading}
            >
              <RefreshCw size={11} aria-hidden />
            </button>
          </span>

          {loading ? (
            <div className="tpl-status">
              <Loader2 size={12} className="tpl-spin" aria-hidden /> Loading templates…
            </div>
          ) : error ? (
            <div className="tpl-status tpl-error">
              <AlertCircle size={12} aria-hidden /> {error}
              <button type="button" className="tpl-retry" onClick={() => load(true)}>
                Retry
              </button>
            </div>
          ) : (
            <>
              <div className="tpl-list" role="listbox" aria-label="WhatsApp templates">
                {templateName && data && !selected && (
                  <div className="tpl-row on tpl-missing" title="Saved on this step but not in the current template list">
                    <span className="tpl-row-head">
                      <span className="tpl-name">{templateName}</span>
                      {templateLanguage && <span className="tpl-tag">{templateLanguage}</span>}
                      <span className="tpl-tag">missing</span>
                    </span>
                  </div>
                )}
                {(data?.templates ?? []).map((t) => {
                  const approved = (t.status || '').toUpperCase() === 'APPROVED'
                  const on = selected === t
                  return (
                    <button
                      type="button"
                      key={`${t.name}:${t.language}`}
                      role="option"
                      aria-selected={on}
                      className={`tpl-row${on ? ' on' : ''}`}
                      disabled={!approved}
                      title={approved ? undefined : `Only APPROVED templates can be sent — this one is ${t.status || 'unreviewed'}.`}
                      onClick={() => pick(t)}
                    >
                      <span className="tpl-row-head">
                        <span className="tpl-name">{t.name}</span>
                        <span className="tpl-tag">{t.language}</span>
                        <span className={`tpl-tag tpl-status-${(t.status || '').toLowerCase()}`}>
                          {t.status || '—'}
                        </span>
                      </span>
                      {t.body && <span className="tpl-row-body">{t.body}</span>}
                    </button>
                  )
                })}
                {data && data.templates.length === 0 && (
                  <div className="tpl-status">No templates — create one in Settings → Message Templates.</div>
                )}
              </div>
              {data?.sandbox && (
                <small>Sandbox — no WhatsApp account linked, so these are the local demo templates. They send the same way once an account is connected.</small>
              )}
            </>
          )}

          {(selected || (templateName && slots.length > 0)) && (
            <>
              {selected && (
                <div className="tpl-preview" aria-label="Template preview">
                  <PreviewBody body={selected.body} values={values} />
                  {(selected.buttons ?? []).length > 0 && (
                    <div className="tpl-buttons" aria-label="Template buttons (fixed by the approved template)">
                      {(selected.buttons ?? []).map((b, i) => {
                        const Icon = BUTTON_ICON[b.type] ?? MessageSquareReply
                        return (
                          <span key={i} className="tpl-btn-chip" title={b.url || b.phone_number || 'Quick reply'}>
                            <Icon size={10} aria-hidden /> {b.text}
                          </span>
                        )
                      })}
                    </div>
                  )}
                </div>
              )}

              {slots.length > 0 && (
                <div className="tpl-vars">
                  {slots.map((slot) => (
                    <label key={slot} className="tpl-var-row">
                      <code className="tpl-var-slot">{`{{${slot}}}`}</code>
                      <input
                        type="text"
                        ref={(el) => {
                          inputRefs.current[slot] = el
                        }}
                        value={values[slot] ?? ''}
                        placeholder="Value or {{placeholder}}"
                        aria-label={`Value for template variable ${slot}`}
                        onFocus={() => setFocusSlot(slot)}
                        onChange={(e) => setValue(slot, e.target.value)}
                      />
                    </label>
                  ))}
                  <div className="tpl-quick" aria-label="Insert a CRM placeholder">
                    <span className="tpl-quick-label">Insert into {`{{${slots.includes(focusSlot) ? focusSlot : slots[0]}}}`}:</span>
                    {QUICK_VARS.map((v) => (
                      <button
                        type="button"
                        key={v.key}
                        className="tpl-quick-chip"
                        title={v.label}
                        onClick={() => insert(`{{${v.key}}}`)}
                      >
                        {`{{${v.key}}}`}
                      </button>
                    ))}
                  </div>
                  <small>
                    {'{{placeholders}}'} are filled from the customer record when the engine sends.
                  </small>
                </div>
              )}
            </>
          )}
        </div>
      )}
    </div>
  )
}
