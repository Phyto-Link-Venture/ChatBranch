'use client'

/**
 * "Unknown customers" in the inbox drawer's "Who answers it" step: what
 * happens when someone the contacts book doesn't know writes in — nothing
 * (the default), ask who they are, or ask and create the contact. Stored in
 * the account's config (identify_unknown / identify_message) through
 * GET/PUT /api/inboxes/{id}/identify, saved right after the account itself.
 */

import { useCallback, useEffect, useState } from 'react'
import { Select } from '@/components/ui/Select'
import { cApi, type IdentifyMode, type InboxIdentify } from '@/lib/contactIdentity'
import { useI18n, useT, type MessageKey } from '@/i18n'

export interface IdentifyDraft {
  mode: '' | IdentifyMode
  message: string
}

/** Load / hold / save the setting for one inbox (accountId null = not saved yet). */
export function useInboxIdentify(accountId: string | null) {
  const [value, setValue] = useState<IdentifyDraft>({ mode: '', message: '' })
  const [loaded, setLoaded] = useState<IdentifyDraft>({ mode: '', message: '' })
  const [defaults, setDefaults] = useState<{ en: string; ms: string } | null>(null)
  /** What an inbox without its own setting gets (the organization default). */
  const [inherited, setInherited] = useState<IdentifyMode>('off')

  useEffect(() => {
    const blank = { mode: '' as const, message: '' }
    setValue(blank)
    setLoaded(blank)
    if (!accountId || !/^\d+$/.test(accountId)) return
    let alive = true
    cApi<InboxIdentify>(`inboxes/${accountId}/identify`)
      .then((d) => {
        if (!alive) return
        const v = { mode: d.identify_unknown, message: d.identify_message }
        setValue(v)
        setLoaded(v)
        setDefaults(d.default_messages)
        if (!d.identify_unknown) setInherited(d.effective)
      })
      .catch(() => {
        /* no channels.view, or an env-managed account — keep the default */
      })
    return () => {
      alive = false
    }
  }, [accountId])

  /** Persist after the account save (only when something changed). */
  const save = useCallback(
    async (id: string) => {
      if (!/^\d+$/.test(id)) return
      if (value.mode === loaded.mode && value.message === loaded.message) return
      const d = await cApi<InboxIdentify>(`inboxes/${id}/identify`, {
        method: 'PUT',
        body: JSON.stringify({ identify_unknown: value.mode, identify_message: value.message }),
      })
      const v = { mode: d.identify_unknown, message: d.identify_message }
      setLoaded(v)
      setValue(v)
      setDefaults(d.default_messages)
    },
    [value, loaded],
  )

  return { value, setValue, defaults, inherited, save }
}

const MODE_LABEL: Record<IdentifyMode, MessageKey> = {
  off: 'channels.unknown.off',
  ask: 'channels.unknown.ask',
  ask_and_create: 'channels.unknown.askAndCreate',
}

export function UnknownCustomersField({
  value,
  onChange,
  defaults,
  inherited = 'off',
  disabled,
}: {
  value: IdentifyDraft
  onChange: (v: IdentifyDraft) => void
  defaults: { en: string; ms: string } | null
  /** The organization default an inbox without its own setting follows. */
  inherited?: IdentifyMode
  disabled?: boolean
}) {
  const t = useT()
  const { locale } = useI18n()
  const showInherit = inherited !== 'off'
  const selected = value.mode || (showInherit ? '' : 'off')
  const mode: IdentifyMode = value.mode || inherited
  return (
    <div>
      <span className="mb-1 block text-[12px] font-semibold text-text-high">{t('channels.unknown.title')}</span>
      <Select
        size="md"
        value={selected}
        onChange={(v) => onChange({ ...value, mode: v as '' | IdentifyMode })}
        ariaLabel={t('channels.unknown.aria')}
        disabled={disabled}
        options={[
          ...(showInherit
            ? [
                {
                  value: '',
                  label: t('channels.unknown.orgDefault'),
                  hint: t('channels.unknown.orgDefaultHint', { mode: t(MODE_LABEL[inherited]) }),
                },
              ]
            : []),
          { value: 'off', label: t(MODE_LABEL.off), hint: t('channels.unknown.offHint') },
          { value: 'ask', label: t(MODE_LABEL.ask), hint: t('channels.unknown.askHint') },
          { value: 'ask_and_create', label: t(MODE_LABEL.ask_and_create) },
        ]}
      />
      <p className="mt-1 text-[11.5px] leading-relaxed text-text-muted">
        {mode === 'off'
          ? t('channels.unknown.offHelp')
          : t(mode === 'ask_and_create' ? 'channels.unknown.askCreateHelp' : 'channels.unknown.askHelp')}
      </p>
      {mode !== 'off' && (
        <label className="mt-2 flex flex-col gap-1">
          <span className="text-[11.5px] font-semibold text-text-mid">{t('channels.unknown.wording')}</span>
          <textarea
            value={value.message}
            onChange={(e) => onChange({ ...value, message: e.target.value })}
            rows={2}
            maxLength={600}
            disabled={disabled}
            placeholder={(locale === 'ms' ? defaults?.ms : defaults?.en) ?? t('channels.unknown.wordingPlaceholder')}
            className="w-full resize-y rounded-lg border border-line-strong bg-card px-3 py-2 text-[12.5px] text-text-high placeholder:text-text-faint focus:border-text-primary focus:outline-none"
          />
        </label>
      )}
    </div>
  )
}
