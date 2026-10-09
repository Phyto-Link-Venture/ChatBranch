'use client'

/**
 * /templates — one Templates module for everything an agent or a flow sends
 * from a library:
 *   Messages      quick replies (CannedResponse), shared or per channel
 *   Call scripts  verbatim scripts for one-way bot calls (ScriptTemplate)
 *   WhatsApp      Meta-approved templates per WhatsApp Business account
 * The tab lives in the URL (?tab=messages|scripts|whatsapp) and the WhatsApp
 * tab keeps its account there too (?account_id=), so links and refreshes land
 * on the same view.
 */

import { useCallback, useState } from 'react'
import { useRouter, useSearchParams } from 'next/navigation'
import { PageHeader } from '@/components/ui/PageHeader'
import { ChannelLogo } from '@/components/ui/ChannelLogo'
import { SharedTemplatesSettings } from '@/components/settings/SharedTemplatesSettings'
import { ScriptTemplatesSettings } from '@/components/settings/ScriptTemplatesSettings'
import { TemplatesSettings } from '@/components/settings/TemplatesSettings'
import { useT, type MessageKey } from '@/i18n'

type Tab = 'messages' | 'scripts' | 'whatsapp'

const TABS: { id: Tab; lbl: MessageKey; logo?: string }[] = [
  { id: 'messages', lbl: 'library.module.tabMessages' },
  { id: 'scripts', lbl: 'library.module.tabScripts', logo: 'voice' },
  { id: 'whatsapp', lbl: 'library.module.tabWhatsApp', logo: 'whatsapp' },
]

function parseTab(raw: string | null, hasAccount: boolean): Tab {
  const t = (raw || '').toLowerCase()
  if (t === 'scripts' || t === 'script' || t === 'script-templates') return 'scripts'
  if (t === 'whatsapp' || t === 'meta' || t === 'wa') return 'whatsapp'
  if (t === 'messages' || t === 'quick-replies') return 'messages'
  // an old deep link to a WhatsApp account's templates
  return hasAccount ? 'whatsapp' : 'messages'
}

export function TemplatesModule() {
  const t = useT()
  const params = useSearchParams()
  const router = useRouter()
  const accountId = params.get('account_id') ?? ''
  const tab = parseTab(params.get('tab'), !!accountId)
  /** ?id=<template id> opens that template's editor (search results link here). */
  const openId = params.get('id') ?? ''
  const [counts, setCounts] = useState<Partial<Record<Tab, number>>>({})

  const go = (next: Tab, extra?: Record<string, string>) => {
    const q = new URLSearchParams({ tab: next, ...(extra ?? {}) })
    router.replace(`/templates?${q.toString()}`, { scroll: false })
  }
  // drop ?id once the editor has opened, so closing it does not reopen it
  const opened = useCallback(() => {
    const q = new URLSearchParams(window.location.search)
    if (!q.has('id')) return
    q.delete('id')
    router.replace(`/templates?${q.toString()}`, { scroll: false })
  }, [router])

  const setMessages = useCallback((n: number) => setCounts((c) => ({ ...c, messages: n })), [])
  const setScripts = useCallback((n: number) => setCounts((c) => ({ ...c, scripts: n })), [])

  return (
    <div className="flex min-h-0 flex-1 flex-col gap-4">
      <PageHeader eyebrow={t('shell.groups.library')} title={t('shell.nav.templates')} subtitle={t('library.module.subtitle')} />

      <div
        role="tablist"
        aria-label={t('library.module.tabsAria')}
        className="-mx-1 flex gap-1 overflow-x-auto border-b border-line px-1"
      >
        {TABS.map((tb) => {
          const active = tb.id === tab
          return (
            <button
              key={tb.id}
              role="tab"
              type="button"
              aria-selected={active}
              onClick={() => go(tb.id)}
              className={`-mb-px inline-flex shrink-0 cursor-pointer items-center gap-1.5 whitespace-nowrap border-b-2 px-3 py-2.5 text-[13px] font-semibold transition-colors ${
                active
                  ? 'border-text-primary text-text-heading'
                  : 'border-transparent text-text-muted hover:text-text-high'
              }`}
            >
              {tb.logo && <ChannelLogo channel={tb.logo} size={14} variant={tb.logo === 'voice' ? 'mono' : 'mark'} title="" />}
              {t(tb.lbl)}
              {counts[tb.id] != null && (
                <span className="rounded-full bg-fill px-1.5 text-[11px] font-semibold text-text-muted">{counts[tb.id]}</span>
              )}
            </button>
          )
        })}
      </div>

      <div className="min-h-0 flex-1 overflow-y-auto pb-6" role="tabpanel">
        {tab === 'messages' && <SharedTemplatesSettings onCount={setMessages} openId={openId} onOpened={opened} />}
        {tab === 'scripts' && <ScriptTemplatesSettings onCount={setScripts} openId={openId} onOpened={opened} />}
        {tab === 'whatsapp' && (
          <TemplatesSettings accountId={accountId} onAccountChange={(id) => go('whatsapp', id ? { account_id: id } : {})} />
        )}
      </div>
    </div>
  )
}
