'use client'

import { Receipt } from 'lucide-react'
import { AreaPage } from '@/components/shell/AreaPage'
import { AiBill } from '@/components/billing/AiBill'
import { useT } from '@/i18n'

/** The AI usage bill — per company, per month (billing.view). */
export default function BillingPage() {
  const t = useT()
  return (
    <AreaPage title={t('shell.nav.aiBill')} eyebrow={t('shell.groups.review')} subtitle={t('billing.subtitle')} Icon={Receipt}>
      <AiBill />
    </AreaPage>
  )
}
