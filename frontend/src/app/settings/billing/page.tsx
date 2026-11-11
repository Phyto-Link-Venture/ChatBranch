import { BillingSettings } from '@/components/billing/BillingSettings'
import { PriceBooks } from '@/components/billing/PriceBooks'
import { AiCreditGrants } from '@/components/billing/AiCreditGrants'

export default function BillingSettingsPage() {
  return (
    <div className="flex max-w-[1100px] flex-col gap-4">
      <BillingSettings />
      <PriceBooks />
      <AiCreditGrants />
    </div>
  )
}
