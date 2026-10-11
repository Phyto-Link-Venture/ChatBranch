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
 *
 * The widget speaks the visitor's language: its own texts (Send, Close, the
 * default title/greeting/placeholder, errors) come from a dictionary inside
 * /widget.js, picked by the `language` set here (auto = the visitor's
 * browser). Title, greeting and placeholder left blank use those built-in,
 * translated texts; filled in, they are shown exactly as written.
 */

import { useState } from 'react'
import { Check, Copy } from 'lucide-react'
import { Select } from '@/components/ui/Select'
import { LOCALES, useT, type TFunction } from '@/i18n'
import type { WidgetLanguage } from '@/components/channels/inboxes'
import { absoluteApiUrl } from '@/lib/apiBase'
import { inputCls } from './WhatsAppSettings'

export interface WidgetLook {
  title: string
  greeting: string
  /** A plain hex — it is painted on the customer's site, where our CSS
   *  variables do not exist. */
  accent: string
  placeholder: string
  /** auto = the visitor's browser language; else a fixed one. */
  language: WidgetLanguage
}

/** Blank texts = the widget's built-in ones, in the visitor's language. */
export const WIDGET_DEFAULTS: WidgetLook = {
  title: '',
  greeting: '',
  accent: '#ff5a1f',
  placeholder: '',
  language: 'auto',
}

const HEX = /^#[0-9a-f]{6}$/i

/** Inline validation for the look fields — empty when fine. */
export function widgetLookErrors(look: WidgetLook, t: TFunction): Partial<Record<keyof WidgetLook, string>> {
  const errors: Partial<Record<keyof WidgetLook, string>> = {}
  if (look.accent.trim() && !HEX.test(look.accent.trim())) errors.accent = t('channels.widget.accentInvalid')
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
  const t = useT()
  const set = (patch: Partial<WidgetLook>) => onChange({ ...value, ...patch })
  const err = (k: keyof WidgetLook) =>
    errors[k] ? (
      <span className="mt-1 block text-[11.5px] font-semibold" style={{ color: 'var(--red-ink)' }} role="alert">
        {errors[k]}
      </span>
    ) : null
  const hint = (text: string) => <span className="mt-1 block text-[11.5px] leading-relaxed text-text-muted">{text}</span>
  const label = (text: string) => <span className="mb-1 block text-[12px] font-semibold text-text-high">{text}</span>
  const swatch = HEX.test(value.accent.trim()) ? value.accent.trim() : WIDGET_DEFAULTS.accent
  return (
    <div className="flex flex-col gap-4">
      <label className="block">
        {label(t('channels.widget.title'))}
        <input
          className={inputCls}
          value={value.title}
          disabled={disabled}
          onChange={(e) => set({ title: e.target.value })}
          placeholder={t('channels.widget.defaultTitle')}
          aria-invalid={!!errors.title}
        />
        {err('title') ?? hint(t('channels.widget.titleHint'))}
      </label>
      <label className="block">
        {label(t('channels.widget.greeting'))}
        <input
          className={inputCls}
          value={value.greeting}
          disabled={disabled}
          onChange={(e) => set({ greeting: e.target.value })}
          placeholder={t('channels.widget.defaultGreeting')}
        />
        {hint(t('channels.widget.greetingHint'))}
      </label>
      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
        <label className="block">
          {label(t('channels.widget.accent'))}
          <div className="flex items-center gap-2">
            <input
              type="color"
              value={swatch}
              disabled={disabled}
              onChange={(e) => set({ accent: e.target.value })}
              aria-label={t('channels.widget.accentPick')}
              className="h-9 w-11 shrink-0 cursor-pointer rounded-lg border border-line-strong bg-card p-0.5"
            />
            <input
              className={`${inputCls} font-mono`}
              value={value.accent}
              disabled={disabled}
              onChange={(e) => set({ accent: e.target.value })}
              aria-invalid={!!errors.accent}
              aria-label={t('channels.widget.accentHex')}
            />
          </div>
          {err('accent') ?? hint(t('channels.widget.accentHint'))}
        </label>
        <label className="block">
          {label(t('channels.widget.placeholder'))}
          <input
            className={inputCls}
            value={value.placeholder}
            disabled={disabled}
            onChange={(e) => set({ placeholder: e.target.value })}
            placeholder={t('channels.widget.defaultPlaceholder')}
          />
        </label>
      </div>
      <div>
        {label(t('channels.widget.language'))}
        <Select
          size="md"
          value={value.language}
          disabled={disabled}
          onChange={(v) => set({ language: v as WidgetLanguage })}
          ariaLabel={t('channels.widget.language')}
          options={[
            { value: 'auto', label: t('channels.widget.languageAuto') },
            ...LOCALES.map((l) => ({ value: l.code, label: l.native })),
          ]}
        />
        {hint(t('channels.widget.languageHint'))}
      </div>
    </div>
  )
}

/**
 * The paste-ready tag for a widget token. Built here from the dashboard's API base
 * made absolute (`absoluteApiUrl`), so a build with a relative `/api` base hands
 * out `https://<this domain>/api/widget.js` — the script must load from our
 * origin on the customer's site. Call it in the browser (after mount).
 */
export function widgetEmbedSnippet(token: string): string {
  return `<script src="${absoluteApiUrl('widget.js')}" data-token="${token}" async></script>`
}

/** The paste-ready snippet, with a copy button. */
export function EmbedSnippet({ value }: { value: string }) {
  const t = useT()
  const [copied, setCopied] = useState(false)
  return (
    <div className="rounded-lg border border-line bg-well">
      <div className="flex items-center justify-between gap-2 border-b border-line px-3 py-1.5">
        <span className="eyebrow !text-[10px]">{t('channels.widget.pasteBefore', { tag: '</body>' })}</span>
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
          {copied ? t('common.actions.copied') : t('common.actions.copy')}
        </button>
      </div>
      <pre className="select-all whitespace-pre-wrap break-all px-3 py-2.5 font-mono text-[11.5px] leading-relaxed text-text-high">
        {value}
      </pre>
    </div>
  )
}
