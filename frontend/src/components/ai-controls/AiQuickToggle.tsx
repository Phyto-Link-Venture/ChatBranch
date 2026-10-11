'use client'

import { useCallback, useEffect, useState } from 'react'
import Link from 'next/link'
import { Bot, BotOff } from 'lucide-react'
import { Toggle } from '@/components/ui/Toggle'
import { useAccess } from '@/lib/useAccess'
import { useEntity } from '@/lib/EntityContext'
import { getAiControls, putAiControls, type AiControls } from '@/lib/aiControls'
import { useT } from '@/i18n'

/**
 * The AI master switch as a quick toggle in the top bar's Live popover, for the
 * people allowed to change it (ai.behaviour.manage). Scope = the top-bar entity
 * (or the whole organization). The full controls — schedule, never-AI tags —
 * live on the AI monitor.
 */
export function AiQuickToggle() {
  const t = useT()
  const access = useAccess()
  // the ticked entity — for a person bound to entities never the whole organization
  const { levelEntity } = useEntity()
  const slug = levelEntity?.slug ?? null
  const allowed = access.ready && access.can('ai.behaviour.manage')
  const [data, setData] = useState<AiControls | null>(null)
  const [busy, setBusy] = useState(false)
  const [err, setErr] = useState<string | null>(null)

  const load = useCallback(async () => {
    try {
      setData(await getAiControls(slug))
      setErr(null)
    } catch (e) {
      setErr(e instanceof Error ? e.message : String(e))
    }
  }, [slug])

  useEffect(() => {
    if (allowed) void load()
  }, [allowed, load])

  if (!allowed) return null

  const flip = async (v: boolean) => {
    setBusy(true)
    setErr(null)
    try {
      setData(await putAiControls(slug, { mode: v ? 'on' : 'off' }))
    } catch (e) {
      setErr(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  const on = !!data?.now_on
  return (
    <div className="border-b border-line px-4 py-2.5">
      <div className="flex items-center gap-3">
        <span
          className={`grid h-7 w-7 shrink-0 place-items-center rounded-full ${
            on ? 'bg-green/15 text-[color:var(--green-ink)]' : 'bg-red/10 text-[color:var(--red-ink)]'
          }`}
          aria-hidden
        >
          {on ? <Bot size={14} strokeWidth={1.9} /> : <BotOff size={14} strokeWidth={1.9} />}
        </span>
        <span className="min-w-0 flex-1">
          <span className="block truncate text-[13px] font-semibold text-text-high">{data?.label ?? 'AI'}</span>
          <Link href="/ai-monitor" className="text-[12px] text-text-muted hover:text-text-high hover:underline">
            {t('chats.aiControls.scheduleExceptions')}
          </Link>
        </span>
        <Toggle on={!!data && data.mode !== 'off'} disabled={!data || busy || !data.can_manage} onChange={flip} label={t('chats.aiControls.masterSwitch')} />
      </div>
      {err && (
        <div role="alert" className="mt-1.5 text-[12px] text-[color:var(--red-ink)]">
          {err}
        </div>
      )}
    </div>
  )
}
