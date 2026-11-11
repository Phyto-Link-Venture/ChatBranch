'use client'

import { Suspense } from 'react'
import { AiCreditTopupReturn } from '@/components/billing/AiCreditTopupReturn'

/** Stripe sends the buyer back here after a top-up (useSearchParams needs a Suspense boundary). */
export default function TopupReturnPage() {
  return (
    <Suspense fallback={null}>
      <AiCreditTopupReturn kind="success" />
    </Suspense>
  )
}
