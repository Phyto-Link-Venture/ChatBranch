'use client'

import { Suspense } from 'react'
import { TemplatesModule } from '@/components/templates/TemplatesModule'

// One Templates module: Messages (quick replies), Call scripts and WhatsApp
// (Meta-approved) — the tab is ?tab=messages|scripts|whatsapp.
export default function TemplatesPage() {
  return (
    <Suspense fallback={null}>
      <TemplatesModule />
    </Suspense>
  )
}
