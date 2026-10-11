'use client'

import { API } from '@/lib/apiBase'
import { useEffect, useState } from 'react'
import { Megaphone, X } from 'lucide-react'
import { useApp } from '@/lib/AppContext'
import { authHeaders } from '@/lib/auth'
import { Select } from '@/components/ui/Select'
import { useT } from '@/i18n'

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
  const t = useT()
  const { calls, dialScriptCall } = useApp()
  const [open, setOpen] = useState(false)
  // null = the default script, in the dashboard's language ({{vars}} stay as they are)
  const [edited, setScript] = useState<string | null>(null)
  const script = edited ?? t('chats.call.defaultScript')
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
        title={t('chats.call.scriptCallHint')}
        aria-label={t('chats.call.startScriptCall')}
        className="grid h-8 w-8 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-brand-300 disabled:cursor-not-allowed disabled:opacity-40"
      >
        <Megaphone size={15} aria-hidden />
      </button>
      {open && (
        <div className="absolute right-0 top-9 z-20 w-72 rounded-xl border border-line bg-card p-2.5 shadow-lg">
          <div className="mb-1.5 flex items-center justify-between">
            <span className="text-[11px] font-bold uppercase tracking-wide text-text-muted">{t('chats.call.scriptCall')}</span>
            <button
              onClick={() => setOpen(false)}
              aria-label={t('common.actions.close')}
              className="cursor-pointer text-text-faint hover:text-text-high"
            >
              <X size={13} aria-hidden />
            </button>
          </div>
          {templates.length > 0 && (
            <Select
              value={templateId}
              onChange={(id) => {
                setTemplateId(id)
                const tpl = templates.find((x) => String(x.id) === id)
                if (tpl) setScript(tpl.content)
              }}
              options={[
                { value: '', label: t('chats.call.customScript'), hint: t('chats.call.editBelow') },
                ...templates.map((tpl) => ({ value: String(tpl.id), label: tpl.name })),
              ]}
              ariaLabel={t('chats.call.scriptTemplateAria')}
              className="mb-1.5 w-full"
            />
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
            <Megaphone size={13} aria-hidden /> {t('chats.call.callReadScript')}
          </button>
        </div>
      )}
    </span>
  )
}
