'use client'

import { Sparkles } from 'lucide-react'
import { AreaPage } from '@/components/shell/AreaPage'
import { WhatsNew } from '@/components/updates/WhatsNew'
import { useT } from '@/i18n'

/** What's new — release notes for signed-in users (reached from the Guide menu). */
export default function WhatsNewPage() {
  const t = useT()
  return (
    <AreaPage title={t('updates.page.title')} eyebrow={t('updates.page.eyebrow')} Icon={Sparkles}>
      <WhatsNew />
    </AreaPage>
  )
}
