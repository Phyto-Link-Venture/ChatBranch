'use client'

import { useEffect, useState } from 'react'
import { BookOpen, Calculator, Check, CreditCard, FileText, Plus, QrCode, Sheet } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { useT } from '@/i18n'
import {
  PROMPT_TEMPLATES,
  READY_APPS,
  fetchToolApps,
  type PromptTemplateId,
  type ToolAppCatalogEntry,
  type ToolAppId,
} from './readySkills'
import type { Skill } from './skillsApi'

const APP_ICON: Record<ToolAppId, typeof FileText> = {
  documents: FileText,
  qr: QrCode,
  payments: CreditCard,
  sheets: Sheet,
}

/**
 * "Ready-made skills" on /skills: one-click Add for the connected apps that do
 * real work without code (Documents PDF, QR codes, Payment links, Google Sheets
 * log) and for instruction templates. Add opens the skill wizard prefilled — a
 * first-time user fills in a few details and saves; nothing is created until
 * then. An app the library already has shows "Added".
 */
export function ReadySkillsGallery({
  skills,
  canManage,
  onAdd,
}: {
  skills: Skill[]
  canManage: boolean
  onAdd: (pick: { app: ToolAppId } | { template: PromptTemplateId }) => void
}) {
  const t = useT()
  const [apps, setApps] = useState<ToolAppCatalogEntry[] | null>(null)
  useEffect(() => {
    let alive = true
    fetchToolApps().then((list) => alive && setApps(list))
    return () => {
      alive = false
    }
  }, [])

  const hasApp = (app: ToolAppId) =>
    skills.some((s) => s.kind === 'app' && (s.config as { app?: string } | null | undefined)?.app === app)
  const hasTemplate = (id: PromptTemplateId) => {
    const name = t(PROMPT_TEMPLATES.find((p) => p.id === id)!.name)
    return skills.some((s) => s.kind === 'prompt' && s.name === name)
  }

  return (
    <section className="flex flex-col gap-3" data-tour="skills-ready">
      <div>
        <h2 className="text-[14px] font-semibold text-text-heading">{t('readySkills.gallery.title')}</h2>
        <p className="mt-0.5 max-w-[760px] text-[12.5px] leading-relaxed text-text-mid">{t('readySkills.gallery.intro')}</p>
      </div>
      <div className="grid grid-cols-1 gap-2.5 sm:grid-cols-2 xl:grid-cols-4">
        {READY_APPS.map((a) => {
          const Icon = APP_ICON[a.app]
          const entry = apps?.find((x) => x.app === a.app)
          const added = hasApp(a.app)
          return (
            <div key={a.app} className="flex flex-col gap-2 rounded-xl border border-line bg-card p-3">
              <div className="flex items-center gap-2">
                <span className="grid h-8 w-8 shrink-0 place-items-center rounded-lg border border-line bg-fill-subtle text-text-high" aria-hidden>
                  <Icon size={15} />
                </span>
                <span className="min-w-0 flex-1 text-[13px] font-semibold text-text-heading">{t(a.name)}</span>
              </div>
              <p className="flex-1 text-[12px] leading-snug text-text-mid">{t(a.blurb)}</p>
              {a.provider && entry && (
                <span className={`text-[11px] ${entry.connected ? 'text-[color:var(--green-ink)]' : 'text-[color:var(--amber-ink)]'}`}>
                  {entry.connected
                    ? t('readySkills.gallery.connected', { provider: a.provider })
                    : t('readySkills.gallery.needsConnection', { provider: a.provider })}
                </span>
              )}
              {canManage && (
                <Button
                  variant={added ? 'ghost' : 'secondary'}
                  size="sm"
                  onClick={() => onAdd({ app: a.app })}
                  className="self-start"
                >
                  {added ? <Check size={13} aria-hidden /> : <Plus size={13} aria-hidden />}{' '}
                  {added ? t('readySkills.gallery.added') : t('readySkills.gallery.add')}
                </Button>
              )}
            </div>
          )
        })}
      </div>
      <p className="flex items-start gap-1.5 text-[12px] leading-snug text-text-muted">
        <Calculator size={13} className="mt-0.5 shrink-0" aria-hidden /> {t('readySkills.gallery.builtinNote')}
      </p>

      <div className="mt-1">
        <h3 className="text-[13px] font-semibold text-text-heading">{t('readySkills.gallery.templatesTitle')}</h3>
        <p className="mt-0.5 text-[12px] text-text-mid">{t('readySkills.gallery.templatesIntro')}</p>
      </div>
      <div className="flex flex-wrap gap-2">
        {PROMPT_TEMPLATES.map((p) => {
          const added = hasTemplate(p.id)
          return (
            <button
              key={p.id}
              type="button"
              disabled={!canManage}
              onClick={() => onAdd({ template: p.id })}
              title={t(p.useWhen)}
              className="inline-flex items-center gap-1.5 rounded-full border border-line px-3 py-1.5 text-[12px] text-text-mid transition-colors hover:border-line-strong hover:bg-fill-hover hover:text-text-high disabled:opacity-60"
            >
              {added ? <Check size={12} aria-hidden /> : <BookOpen size={12} aria-hidden />} {t(p.name)}
            </button>
          )
        })}
      </div>
    </section>
  )
}
