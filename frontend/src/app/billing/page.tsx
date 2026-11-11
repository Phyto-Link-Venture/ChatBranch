'use client'

import { Receipt } from 'lucide-react'
import { AreaPage } from '@/components/shell/AreaPage'
import { AiBill } from '@/components/billing/AiBill'
import { AiQuotas } from '@/components/billing/AiQuotas'
import { AiCredit } from '@/components/billing/AiCredit'
import { useT } from '@/i18n'

/** The AI usage bill — per company, per month (billing.view). */
export default function BillingPage() {
  const t = useT()
  return (
    <AreaPage title={t('shell.nav.aiBill')} eyebrow={t('shell.groups.review')} subtitle={t('billing.subtitle')} Icon={Receipt}>
      <div className="flex flex-col gap-4">
        <AiBill />
        {/* prepaid credit and the quotas are not part of the printed bill */}
        <div className="print:hidden">
          <AiCredit />
        </div>
        <div className="print:hidden">
          <AiQuotas />
        </div>
      </div>
    </AreaPage>
  )
}
