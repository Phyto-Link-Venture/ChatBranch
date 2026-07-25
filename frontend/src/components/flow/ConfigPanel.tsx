'use client'

import { Fragment, useEffect, useState } from 'react'
import { useReactFlow } from '@xyflow/react'
import { X } from 'lucide-react'
import { CONFIG_SCHEMAS } from '@/lib/flow/nodeConfig'
import { fetchAgentVoices, fetchScriptTemplates, type AgentVoice, type ScriptTemplateOpt } from '@/lib/flow/api'
import ContactScopePicker from './ContactScopePicker'
import type { AgentSkill, ConfigField, ConfigFieldType, FlowConfig, FlowNode, NodeKind } from '@/lib/flow/types'
import { KindIcon, paletteByKind } from './nodes'
import VariableTextarea from './VariableTextarea'
import WaTemplateConfig from './WaTemplateConfig'

/**
 * n8n-style settings drawer: opens on the right when a node is clicked.
 * Renders the fields from CONFIG_SCHEMAS for that node's kind and writes
 * values into node.data.config via updateNodeData.
 *
 * `agentSkills` feeds the "Agent skill for this step" dropdown on Action
 * steps — the instruction module the AI loads to run that step.
 */
export default function ConfigPanel({
  node,
  agentSkills = [],
  onClose,
}: {
  node: FlowNode
  agentSkills?: AgentSkill[]
  onClose: () => void
}) {
  const { updateNodeData } = useReactFlow()
  const kind = node.type as NodeKind
  const meta = paletteByKind[kind]
  const schema = CONFIG_SCHEMAS[kind] ?? []
  const config = node.data.config ?? {}

  // ElevenLabs voices for any 'voiceselect' field (Action node's Agent voice).
  // Fetched once when the panel is for a node that could use it.
  const [voices, setVoices] = useState<AgentVoice[]>([])
  const hasVoiceField = schema.some((f) => f.type === 'voiceselect')
  useEffect(() => {
    if (!hasVoiceField) return
    let alive = true
    fetchAgentVoices().then((v) => alive && setVoices(v))
    return () => {
      alive = false
    }
  }, [hasVoiceField])

  // Script templates for the Action node's 'scripttemplateselect' field
  // (Settings → Script Templates manages the actual content).
  const [scriptTemplates, setScriptTemplates] = useState<ScriptTemplateOpt[]>([])
  const hasScriptTemplateField = schema.some((f) => f.type === 'scripttemplateselect')
  useEffect(() => {
    if (!hasScriptTemplateField) return
    let alive = true
    fetchScriptTemplates().then((t) => alive && setScriptTemplates(t))
    return () => {
      alive = false
    }
  }, [hasScriptTemplateField])

  const setField = (key: string, type: ConfigFieldType, raw: string | string[]) => {
    const value = type === 'number' ? (raw === '' ? '' : Number(raw)) : raw
    updateNodeData(node.id, { config: { ...config, [key]: value } })
  }

  /** Multi-key config write for the WhatsApp template block (mode + template
   *  identity + variables change together). */
  const patchConfig = (patch: FlowConfig) =>
    updateNodeData(node.id, { config: { ...config, ...patch } })

  const channelsRaw = config.channels
  const channels = Array.isArray(channelsRaw) ? channelsRaw : channelsRaw ? [String(channelsRaw)] : []
  /** Action step that may message over WhatsApp → offer text vs template. */
  const showWaBlock = kind === 'action' && channels.includes('WhatsApp')
  /** Channels where the contact can have several linked identities → offer the
   * primary-vs-all "Send to" switch. Voice always dials the primary number. */
  const showSendTo =
    kind === 'action' && channels.some((c) => c === 'WhatsApp' || c === 'Email')
  // allLinked is the channel-generic key; waAllNumbers is the legacy
  // WhatsApp-only spelling from flows saved before the switch went generic.
  const allLinked = Boolean(config.allLinked ?? config.waAllNumbers)

  return (
    <aside className="config-panel" style={{ '--kind': meta.color } as React.CSSProperties}>
      <header className="config-header">
        <span className="config-icon">
          <KindIcon kind={kind} size={15} />
        </span>
        <div className="config-title">
          <span className="config-kind">{meta.title}</span>
          <span className="config-hint">Settings for this step</span>
        </div>
        <button className="config-close" onClick={onClose} title="Close" aria-label="Close settings">
          <X size={16} aria-hidden />
        </button>
      </header>

      <div className="config-body">
        <label className="config-field">
          <span>Text on the node</span>
          <textarea
            rows={3}
            value={node.data.label ?? ''}
            onChange={(e) => updateNodeData(node.id, { label: e.target.value })}
          />
        </label>

        {schema.map((field) => {
          // The scriptTemplateId picker only matters when Script call is on.
          if (field.key === 'scriptTemplateId' && !channels.includes('Script call')) return null
          // Agent voice matters for a Script call, or a WhatsApp (Baileys) AI
          // voice call — hide it otherwise so the panel stays uncluttered.
          if (field.key === 'voice') {
            const isWaVoiceCall =
              channels.includes('AI voice call') &&
              String(config.callVia ?? '').startsWith('WhatsApp')
            if (!channels.includes('Script call') && !isWaVoiceCall) return null
          }
          const rendered = renderField(field)
          // The send-target switch + WhatsApp text/template block belong to
          // the channel choice: they slot in right under the Channels field.
          if (field.key === 'channels' && (showSendTo || showWaBlock)) {
            return (
              <Fragment key={field.key}>
                {rendered}
                {showSendTo && (
                  <div className="config-field">
                    <span>Send to</span>
                    <div
                      className="wa-mode-switch"
                      role="radiogroup"
                      aria-label="Send to which linked contacts"
                    >
                      <button
                        type="button"
                        role="radio"
                        aria-checked={!allLinked}
                        className={`wa-mode-btn${!allLinked ? ' on' : ''}`}
                        onClick={() => patchConfig({ allLinked: undefined, waAllNumbers: undefined })}
                      >
                        Primary contact
                        <small>CRM phone / email</small>
                      </button>
                      <button
                        type="button"
                        role="radio"
                        aria-checked={allLinked}
                        className={`wa-mode-btn${allLinked ? ' on' : ''}`}
                        onClick={() => patchConfig({ allLinked: true, waAllNumbers: undefined })}
                      >
                        All linked contacts
                        <small>every identity on file</small>
                      </button>
                    </div>
                    <small>
                      {allLinked
                        ? 'Every WhatsApp number and email address linked to the contact gets this message (duplicates removed; opt-out and window rules still apply per contact). AI voice calls always dial the primary number.'
                        : 'Only the contact’s primary CRM contact (phone number / email on file) gets this message.'}
                    </small>
                  </div>
                )}
                {showWaBlock && <WaTemplateConfig config={config} onPatch={patchConfig} />}
              </Fragment>
            )
          }
          return rendered
        })}
      </div>

      <footer className="config-footer">Changes save automatically and travel with Export JSON.</footer>
    </aside>
  )

  function renderField(field: ConfigField) {
    return field.type === 'textarea' && field.variables ? (
      <VariableTextarea
        key={field.key}
        label={field.label}
        help={field.help}
        value={String(config[field.key] ?? '')}
        onChange={(v) => setField(field.key, field.type, v)}
      />
    ) : (
      <label key={field.key} className="config-field">
        <span>{field.label}</span>
        {field.type === 'skillselect' ? (
          <select
            value={String(config[field.key] ?? '')}
            onChange={(e) => setField(field.key, field.type, e.target.value)}
          >
            <option value="">— none —</option>
            {!!config[field.key] &&
              !agentSkills.some((s) => s.name === config[field.key]) && (
                <option value={String(config[field.key])}>{String(config[field.key])} (missing)</option>
              )}
            {agentSkills.map((s) => (
              <option key={s.id} value={s.name}>
                {s.name}
              </option>
            ))}
          </select>
        ) : field.type === 'scripttemplateselect' ? (
          <select
            value={String(config[field.key] ?? '')}
            onChange={(e) => setField(field.key, field.type, e.target.value)}
          >
            <option value="">— choose a script —</option>
            {!!config[field.key] &&
              !scriptTemplates.some((t) => String(t.id) === String(config[field.key])) && (
                <option value={String(config[field.key])}>#{String(config[field.key])} (missing)</option>
              )}
            {scriptTemplates.map((t) => (
              <option key={t.id} value={t.id}>
                {t.name}
              </option>
            ))}
          </select>
        ) : field.type === 'voiceselect' ? (
          <select
            value={String(config[field.key] ?? '')}
            onChange={(e) => setField(field.key, field.type, e.target.value)}
          >
            <option value="">Native default (OpenAI)</option>
            {!!config[field.key] &&
              !voices.some((v) => v.id === config[field.key]) && (
                <option value={String(config[field.key])}>{String(config[field.key])} (unavailable)</option>
              )}
            {voices.map((v) => (
              <option key={v.id} value={v.id}>
                {v.name} (ElevenLabs)
              </option>
            ))}
          </select>
        ) : field.type === 'contactselect' ? (
          <ContactScopePicker
            selected={(() => {
              const raw = config[field.key]
              return Array.isArray(raw) ? raw.map(String) : raw ? [String(raw)] : []
            })()}
            labels={(() => {
              const raw = config.contactLabels
              return raw && typeof raw === 'object' && !Array.isArray(raw)
                ? (raw as Record<string, string>)
                : {}
            })()}
            onChange={(ids, labels) =>
              patchConfig({ [field.key]: ids, contactLabels: labels })
            }
          />
        ) : field.type === 'multiselect' ? (
          <div className="multi-options" role="group" aria-label={field.label}>
            {field.options?.map((opt) => {
              const raw = config[field.key]
              const arr = Array.isArray(raw) ? raw : raw ? [String(raw)] : []
              const on = arr.includes(opt)
              return (
                <button
                  type="button"
                  key={opt}
                  className={`multi-chip${on ? ' on' : ''}`}
                  aria-pressed={on}
                  onClick={() =>
                    setField(field.key, field.type, on ? arr.filter((x) => x !== opt) : [...arr, opt])
                  }
                >
                  {on ? '✓ ' : ''}
                  {opt}
                </button>
              )
            })}
          </div>
        ) : field.type === 'select' ? (
          <select
            value={String(config[field.key] ?? '')}
            onChange={(e) => setField(field.key, field.type, e.target.value)}
          >
            <option value="" disabled>
              Choose…
            </option>
            {field.options?.map((opt) => (
              <option key={opt} value={opt}>
                {opt}
              </option>
            ))}
          </select>
        ) : field.type === 'textarea' ? (
          <textarea
            rows={4}
            value={String(config[field.key] ?? '')}
            onChange={(e) => setField(field.key, field.type, e.target.value)}
          />
        ) : (
          <input
            type={field.type === 'number' ? 'number' : 'text'}
            value={String(config[field.key] ?? '')}
            onChange={(e) => setField(field.key, field.type, e.target.value)}
          />
        )}
        {field.help && <small>{field.help}</small>}
      </label>
    )
  }
}
