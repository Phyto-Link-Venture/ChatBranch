'use client'

import { UserRoundCog } from 'lucide-react'
import { AreaPage } from '@/components/shell/AreaPage'
import { PersonaRules } from '@/components/persona/PersonaRules'
import { useT } from '@/i18n'

/** Persona & house rules — how the AI introduces itself and the rules every reply follows, per outlet. */
export default function AiPersonaPage() {
  const t = useT()
  return (
    <AreaPage
      title={t('shell.nav.aiPersona')}
      eyebrow={t('shell.groups.library')}
      subtitle={t('ai.pages.personaSubtitle')}
      Icon={UserRoundCog}
    >
      <PersonaRules />
    </AreaPage>
  )
}
