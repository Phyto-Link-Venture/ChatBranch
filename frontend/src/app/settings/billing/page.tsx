'use client'

import Link from 'next/link'
import { BillingSettings } from '@/components/billing/BillingSettings'
import { PriceBooks } from '@/components/billing/PriceBooks'
import { AiCreditGrants } from '@/components/billing/AiCreditGrants'
import { AiCreditOffers } from '@/components/billing/AiCreditOffers'
import { AiQuotas } from '@/components/billing/AiQuotas'
import { useT } from '@/i18n'

/**
 * Settings → AI pricing & credit (billing.manage): everything the OPERATOR sets —
 * currency and defaults, price books, credit grants, offers and every spending cap.
 * Clients see their usage, balance and their own caps on /billing instead.
 */
export default function BillingSettingsPage() {
  const t = useT()
  return (
    <div className="flex max-w-[1100px] flex-col gap-4">
      <p className="rounded-lg bg-fill-subtle px-3 py-2 text-[12.5px] text-text-mid">
        {t('billing.settings.audienceNote')}{' '}
        <Link href="/billing" className="font-semibold text-brand-600 underline-offset-2 hover:underline">
          {t('shell.nav.aiBill')}
        </Link>
      </p>
      <BillingSettings />
      <PriceBooks />
      <AiCreditGrants />
      <AiCreditOffers />
      <AiQuotas variant="operator" />
    </div>
  )
}
