'use client'

import { useEffect, useRef, useState } from 'react'
import { AlertCircle, ExternalLink, Loader2, MessageSquareReply, Phone, RefreshCw } from 'lucide-react'
import { authHeaders } from '@/lib/auth'
import type { FlowConfig } from '@/lib/flow/types'
import { Field, Segmented } from './fields'

/**
 * WhatsApp "message type" block inside a send / AI message / follow-up step —
 * shown only when WhatsApp is one of the step's channels. Free text only
 * delivers inside Meta's 24-hour service window; an approved TEMPLATE goes out
 * any time; "auto" sends the text while the window is open and the template
 * once it has closed.
 *
 * Writes the node-config contract the engine executes: waMode
 * ('text' | 'template' | 'auto'), templateName, templateLanguage,
 * templateVariables ({"1": "{{first_name}}"} — slot → value, rendered from the
 * CRM at send time).
 */

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

interface WaTemplateButton {
  type: string
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
  bindings?: string[]
  variables?: number
}

/** The placeholders most templates need — the full list lives in the message
 * field's "Insert a variable" picker. */
const QUICK_VARS = ['first_name', 'name', 'business_name', 'record_title', 'due_date', 'link', 'website']

const BUTTON_ICON: Record<string, typeof MessageSquareReply> = {
  QUICK_REPLY: MessageSquareReply,
  URL: ExternalLink,
  PHONE_NUMBER: Phone,
}

const PLACEHOLDER_RE = /(\{\{\s*\d+\s*\}\})/g

const variableCount = (body: string) =>
  Math.max(0, ...[...(body || '').matchAll(/\{\{\s*(\d+)\s*\}\}/g)].map((m) => parseInt(m[1], 10)))

/** One fetch per page visit — clicking between steps must not re-hit the API. */
let templatesCache: { templates: WaTemplate[]; sandbox: boolean } | null = null

function PreviewBody({ body, values }: { body: string; values: Record<string, string> }) {
  return (
    <>
      {body.split(PLACEHOLDER_RE).map((part, i) => {
        const m = part.match(/^\{\{\s*(\d+)\s*\}\}$/)
        if (!m) return <span key={i}>{part}</span>
        const v = (values[m[1]] ?? '').trim()
        return (
          <mark
            key={i}
            className={`rounded px-0.5 ${v ? 'bg-green/15 text-text-high' : 'bg-amber/20 text-[color:var(--amber-ink)]'}`}
            title={`Variable {{${m[1]}}}`}
          >
            {v || `{{${m[1]}}}`}
          </mark>
        )
      })}
    </>
  )
}

type Mode = 'text' | 'template' | 'auto'

const MODE_COPY: Record<Mode, { label: string; hint: string }> = {
  text: { label: 'Free text', hint: '24h window only' },
  template: { label: 'Template', hint: 'works any time' },
  auto: { label: 'Text, else template', hint: 'template after 24h' },
}

export default function WaTemplateConfig({
  config,
  onPatch,
  modes = ['text', 'template', 'auto'],
  textLabel,
}: {
  config: FlowConfig
  onPatch: (patch: FlowConfig) => void
  modes?: Mode[]
  /** what "text" means on this step, e.g. "AI text" */
  textLabel?: string
}) {
  const raw = (config.waMode as Mode) || 'text'
  const mode: Mode = modes.includes(raw) ? raw : modes[0]
  const templateName = typeof config.templateName === 'string' ? config.templateName : ''
  const templateLanguage = typeof config.templateLanguage === 'string' ? config.templateLanguage : ''
  const rawVars = config.templateVariables
  const values: Record<string, string> =
    rawVars && typeof rawVars === 'object' && !Array.isArray(rawVars) ? (rawVars as Record<string, string>) : {}

  const [data, setData] = useState<{ templates: WaTemplate[]; sandbox: boolean } | null>(templatesCache)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
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
      const res = await fetch(`${API}/whatsapp/templates`, { headers: authHeaders(), cache: 'no-store' })
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

  const needsTemplate = mode !== 'text'
  useEffect(() => {
    if (needsTemplate && !data) load()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [needsTemplate])

  const selected =
    data?.templates.find((t) => t.name === templateName && (!templateLanguage || t.language === templateLanguage)) ?? null

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
      vars[String(i)] = (same && values[String(i)]) || (binding ? `{{${binding}}}` : '')
    }
    onPatch({ templateName: t.name, templateLanguage: t.language, templateVariables: vars })
    setFocusSlot('1')
  }

  const setValue = (slot: string, value: string) => onPatch({ templateVariables: { ...values, [slot]: value } })

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
    <div className="flex flex-col gap-3 rounded-lg border border-line bg-well p-3">
      <Field
        label="WhatsApp message type"
        help={
          mode === 'text'
            ? 'Free text only delivers inside Meta’s 24-hour window (after the customer last wrote). Scheduled messages usually need a template.'
            : mode === 'template'
              ? 'An approved template can be sent at any time.'
              : 'Sends the text while the 24-hour window is open, and this template once it has closed.'
        }
      >
        <Segmented
          ariaLabel="WhatsApp message type"
          value={mode}
          onChange={(m) => onPatch({ waMode: m })}
          options={modes.map((m) => ({
            value: m,
            label: m === 'text' && textLabel ? textLabel : MODE_COPY[m].label,
            hint: MODE_COPY[m].hint,
          }))}
        />
      </Field>

      {needsTemplate && (
        <div className="flex flex-col gap-2">
          <div className="flex items-center justify-between">
            <span className="text-[12px] font-semibold text-text-mid">Template</span>
            <button
              type="button"
              onClick={() => load(true)}
              title="Reload templates"
              aria-label="Reload templates"
              disabled={loading}
              className="grid h-6 w-6 place-items-center rounded-md text-text-muted hover:bg-fill-hover hover:text-text-high"
            >
              <RefreshCw size={12} aria-hidden />
            </button>
          </div>

          {loading ? (
            <div className="flex items-center gap-1.5 text-[12px] text-text-muted">
              <Loader2 size={12} className="animate-spin" aria-hidden /> Loading templates…
            </div>
          ) : error ? (
            <div className="flex flex-wrap items-center gap-1.5 text-[12px] text-[color:var(--red-ink)]">
              <AlertCircle size={12} aria-hidden /> {error}
              <button type="button" className="font-semibold underline" onClick={() => load(true)}>
                Retry
              </button>
            </div>
          ) : (
            <div role="listbox" aria-label="WhatsApp templates" className="flex max-h-[220px] flex-col gap-1 overflow-y-auto">
              {templateName && data && !selected && (
                <div className="rounded-lg border border-amber/40 bg-amber/10 px-2.5 py-2 text-[12px] text-[color:var(--amber-ink)]">
                  {templateName} {templateLanguage && `· ${templateLanguage}`} — not in the current template list
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
                    disabled={!approved}
                    title={approved ? undefined : `Only APPROVED templates can be sent — this one is ${t.status || 'unreviewed'}.`}
                    onClick={() => pick(t)}
                    className={`flex flex-col gap-0.5 rounded-lg border px-2.5 py-2 text-left transition-colors disabled:opacity-50 ${
                      on ? 'border-text-primary bg-card' : 'border-line bg-card hover:border-line-strong'
                    }`}
                  >
                    <span className="flex flex-wrap items-center gap-1.5">
                      <span className="text-[12.5px] font-semibold text-text-high">{t.name}</span>
                      <span className="rounded bg-fill-hover px-1 text-[10.5px] text-text-muted">{t.language}</span>
                      {!approved && <span className="rounded bg-fill-hover px-1 text-[10.5px] text-text-muted">{t.status || '—'}</span>}
                    </span>
                    {t.body && <span className="line-clamp-2 text-[11.5px] text-text-muted">{t.body}</span>}
                  </button>
                )
              })}
              {data && data.templates.length === 0 && (
                <div className="text-[12px] text-text-muted">No templates yet — create one under Templates.</div>
              )}
              {data?.sandbox && (
                <div className="text-[11.5px] text-text-muted">
                  Sandbox — no WhatsApp account linked, so these are the local demo templates.
                </div>
              )}
            </div>
          )}

          {selected && (
            <div className="rounded-lg border border-line bg-card px-3 py-2 text-[12.5px] leading-relaxed text-text-high" aria-label="Template preview">
              <PreviewBody body={selected.body} values={values} />
              {(selected.buttons ?? []).length > 0 && (
                <div className="mt-2 flex flex-wrap gap-1">
                  {(selected.buttons ?? []).map((b, i) => {
                    const Icon = BUTTON_ICON[b.type] ?? MessageSquareReply
                    return (
                      <span key={i} className="inline-flex items-center gap-1 rounded-md border border-line px-1.5 py-0.5 text-[11px] text-text-mid">
                        <Icon size={10} aria-hidden /> {b.text}
                      </span>
                    )
                  })}
                </div>
              )}
            </div>
          )}

          {slots.length > 0 && (
            <div className="flex flex-col gap-1.5">
              {slots.map((slot) => (
                <label key={slot} className="flex items-center gap-2">
                  <code className="w-10 shrink-0 text-[11.5px] text-text-muted">{`{{${slot}}}`}</code>
                  <input
                    type="text"
                    ref={(el) => {
                      inputRefs.current[slot] = el
                    }}
                    value={values[slot] ?? ''}
                    placeholder="Value or {{first_name}}"
                    aria-label={`Value for template variable ${slot}`}
                    onFocus={() => setFocusSlot(slot)}
                    onChange={(e) => setValue(slot, e.target.value)}
                    className="h-8 w-full min-w-0 rounded-md border border-line-strong bg-card px-2 text-[12.5px] text-text-high outline-none focus:border-text-primary"
                  />
                </label>
              ))}
              <div className="flex flex-wrap items-center gap-1">
                <span className="text-[11px] text-text-muted">
                  Insert into {`{{${slots.includes(focusSlot) ? focusSlot : slots[0]}}}`}:
                </span>
                {QUICK_VARS.map((v) => (
                  <button
                    type="button"
                    key={v}
                    onClick={() => insert(`{{${v}}}`)}
                    className="rounded-md border border-line bg-card px-1.5 py-0.5 text-[11px] text-text-mid hover:border-line-strong"
                  >
                    {`{{${v}}}`}
                  </button>
                ))}
              </div>
            </div>
          )}
        </div>
      )}
    </div>
  )
}
