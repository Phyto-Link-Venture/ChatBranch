'use client'

import { useEffect, useState } from 'react'
import { Megaphone, X } from 'lucide-react'
import { useApp } from '@/lib/AppContext'
import { authHeaders } from '@/lib/auth'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

const DEFAULT_SCRIPT =
  'Hello {{name}}, this is an automated courtesy call from {{company}} regarding your ' +
  '{{record_title}}, currently {{record_status}}. ' +
  'Please check the link sent to you for full details. ' +
  'Thank you, and have a good day.'

interface ScriptTemplate {
  id: number
  name: string
  content: string
}

/** Header quick-action: dial a contact, read a fixed script verbatim, hang up —
 *  no listening, no LLM conversation. Distinct from VoiceCallButton's full
 *  two-way AI call sitting right next to it. Shares the same `calls` seam
 *  (dialScriptCall queues through the normal dial pipeline), so once placed,
 *  VoiceCallButton's own spinner/hangup control already reflects it. */
export default function ScriptCallButton({ contactId }: { contactId: number }) {
  const { calls, dialScriptCall } = useApp()
  const [open, setOpen] = useState(false)
  const [script, setScript] = useState(DEFAULT_SCRIPT)
  const [templates, setTemplates] = useState<ScriptTemplate[]>([])
  const [templateId, setTemplateId] = useState('')
  const inCall = Object.values(calls).some(
    (c) => c.contactId === contactId && ['dialing', 'ringing', 'live'].includes(c.status),
  )

  useEffect(() => {
    if (!open) return
    fetch(`${API}/calls/script-templates`, { headers: authHeaders() })
      .then((r) => (r.ok ? r.json() : { templates: [] }))
      .then((d) => setTemplates(Array.isArray(d.templates) ? d.templates : []))
      .catch(() => setTemplates([]))
  }, [open])

  return (
    <span className="relative">
      <button
        onClick={() => setOpen((v) => !v)}
        disabled={inCall}
        title="Script call — reads a fixed script, then hangs up"
        aria-label="Start script call"
        className="grid h-8 w-8 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-brand-300 disabled:cursor-not-allowed disabled:opacity-40"
      >
        <Megaphone size={15} aria-hidden />
      </button>
      {open && (
        <div className="absolute right-0 top-9 z-20 w-72 rounded-xl border border-line bg-card p-2.5 shadow-lg">
          <div className="mb-1.5 flex items-center justify-between">
            <span className="text-[11px] font-bold uppercase tracking-wide text-text-muted">Script call</span>
            <button onClick={() => setOpen(false)} className="cursor-pointer text-text-faint hover:text-text-high">
              <X size={13} aria-hidden />
            </button>
          </div>
          {templates.length > 0 && (
            <select
              value={templateId}
              onChange={(e) => {
                const id = e.target.value
                setTemplateId(id)
                const t = templates.find((x) => String(x.id) === id)
                if (t) setScript(t.content)
              }}
              className="mb-1.5 w-full rounded-lg border border-line bg-fill px-2 py-1.5 text-[12px] text-text-high outline-none focus:border-brand"
            >
              <option value="">— custom / edit below —</option>
              {templates.map((t) => (
                <option key={t.id} value={t.id}>
                  {t.name}
                </option>
              ))}
            </select>
          )}
          <textarea
            value={script}
            onChange={(e) => setScript(e.target.value)}
            rows={5}
            className="w-full resize-none rounded-lg border border-line bg-fill px-2 py-1.5 text-[12px] text-text-high outline-none focus:border-brand"
          />
          <p className="mt-1 text-[10px] text-text-faint">
            {'{{name}} {{record_title}} {{record_status}} {{link}} {{company}}'}
          </p>
          <button
            onClick={() => {
              dialScriptCall(contactId, script)
              setOpen(false)
            }}
            disabled={!script.trim()}
            className="mt-2 flex w-full cursor-pointer items-center justify-center gap-1.5 rounded-lg bg-brand px-2.5 py-1.5 text-[12px] font-semibold text-white transition-opacity hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-40"
          >
            <Megaphone size={13} aria-hidden /> Call &amp; read script
          </button>
        </div>
      )}
    </span>
  )
}
