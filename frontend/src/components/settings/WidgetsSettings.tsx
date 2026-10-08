'use client'

/**
 * Website-chat pieces of the Inboxes setup (/channels).
 *
 * A widget is a channel account with channel="website"; its token is PUBLIC
 * by design (it ships in a script tag on someone else's page) and grants
 * nothing but "start or continue a conversation here". Rotating it
 * invalidates every existing embed, which is the point when a snippet ends up
 * somewhere it shouldn't. These are the parts only the widget has: how the
 * bubble looks (the "Design the widget" step) and the paste-ready snippet.
 */

import { useState } from 'react'
import { Check, Copy } from 'lucide-react'
import { inputCls } from './WhatsAppSettings'

export interface WidgetLook {
  title: string
  greeting: string
  /** A plain hex — it is painted on the customer's site, where our CSS
   *  variables do not exist. */
  accent: string
  placeholder: string
}

export const WIDGET_DEFAULTS: WidgetLook = {
  title: 'Chat with us',
  greeting: 'Hi! How can we help?',
  accent: '#ff5a1f',
  placeholder: 'Type your message…',
}

const HEX = /^#[0-9a-f]{6}$/i

/** Inline validation for the look fields — null when fine. */
export function widgetLookErrors(look: WidgetLook): Partial<Record<keyof WidgetLook, string>> {
  const errors: Partial<Record<keyof WidgetLook, string>> = {}
  if (!look.title.trim()) errors.title = 'Give the bubble a header title.'
  if (look.accent.trim() && !HEX.test(look.accent.trim())) errors.accent = 'Use a hex colour like #ff5a1f.'
  return errors
}

export function WidgetLookFields({
  value,
  onChange,
  errors = {},
  disabled,
}: {
  value: WidgetLook
  onChange: (next: WidgetLook) => void
  errors?: Partial<Record<keyof WidgetLook, string>>
  disabled?: boolean
}) {
  const set = (patch: Partial<WidgetLook>) => onChange({ ...value, ...patch })
  const err = (k: keyof WidgetLook) =>
    errors[k] ? (
      <span className="mt-1 block text-[11.5px] font-semibold" style={{ color: 'var(--red-ink)' }} role="alert">
        {errors[k]}
      </span>
    ) : null
  const swatch = HEX.test(value.accent.trim()) ? value.accent.trim() : WIDGET_DEFAULTS.accent
  return (
    <div className="flex flex-col gap-4">
      <label className="block">
        <span className="mb-1 block text-[12px] font-semibold text-text-high">Header title</span>
        <input
          className={inputCls}
          value={value.title}
          disabled={disabled}
          onChange={(e) => set({ title: e.target.value })}
          placeholder={WIDGET_DEFAULTS.title}
          aria-invalid={!!errors.title}
        />
        {err('title') ?? (
          <span className="mt-1 block text-[11.5px] text-text-muted">Shown at the top of the open chat panel.</span>
        )}
      </label>
      <label className="block">
        <span className="mb-1 block text-[12px] font-semibold text-text-high">Greeting</span>
        <input
          className={inputCls}
          value={value.greeting}
          disabled={disabled}
          onChange={(e) => set({ greeting: e.target.value })}
          placeholder={WIDGET_DEFAULTS.greeting}
        />
        <span className="mt-1 block text-[11.5px] text-text-muted">
          Shown before the visitor types — it is not sent as a message, so it never opens a conversation by itself.
        </span>
      </label>
      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
        <label className="block">
          <span className="mb-1 block text-[12px] font-semibold text-text-high">Accent colour</span>
          <div className="flex items-center gap-2">
            <input
              type="color"
              value={swatch}
              disabled={disabled}
              onChange={(e) => set({ accent: e.target.value })}
              aria-label="Pick the accent colour"
              className="h-9 w-11 shrink-0 cursor-pointer rounded-lg border border-line-strong bg-card p-0.5"
            />
            <input
              className={`${inputCls} font-mono`}
              value={value.accent}
              disabled={disabled}
              onChange={(e) => set({ accent: e.target.value })}
              aria-invalid={!!errors.accent}
              aria-label="Accent colour hex"
            />
          </div>
          {err('accent') ?? (
            <span className="mt-1 block text-[11.5px] text-text-muted">The bubble and the send button.</span>
          )}
        </label>
        <label className="block">
          <span className="mb-1 block text-[12px] font-semibold text-text-high">Input placeholder</span>
          <input
            className={inputCls}
            value={value.placeholder}
            disabled={disabled}
            onChange={(e) => set({ placeholder: e.target.value })}
            placeholder={WIDGET_DEFAULTS.placeholder}
          />
        </label>
      </div>
    </div>
  )
}

/** The paste-ready snippet, with a copy button. */
export function EmbedSnippet({ value }: { value: string }) {
  const [copied, setCopied] = useState(false)
  return (
    <div className="rounded-lg border border-line bg-well">
      <div className="flex items-center justify-between gap-2 border-b border-line px-3 py-1.5">
        <span className="eyebrow !text-[10px]">Paste before &lt;/body&gt;</span>
        <button
          type="button"
          onClick={async () => {
            try {
              await navigator.clipboard.writeText(value)
              setCopied(true)
              setTimeout(() => setCopied(false), 1500)
            } catch {
              /* clipboard blocked — the snippet is selectable */
            }
          }}
          className="inline-flex cursor-pointer items-center gap-1 rounded-md px-2 py-1 text-[11.5px] font-semibold text-text-mid transition-colors hover:bg-fill-hover hover:text-text-high"
        >
          {copied ? <Check size={12} className="text-green-300" aria-hidden /> : <Copy size={12} aria-hidden />}
          {copied ? 'Copied' : 'Copy'}
        </button>
      </div>
      <pre className="select-all whitespace-pre-wrap break-all px-3 py-2.5 font-mono text-[11.5px] leading-relaxed text-text-high">
        {value}
      </pre>
    </div>
  )
}
