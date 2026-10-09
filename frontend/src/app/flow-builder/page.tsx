'use client'

import { LegacyRedirect } from '@/components/shell/LegacyRedirect'

// The flow builder is called Journeys now — old links and bookmarks (query
// and hash included) land on /journeys.
export default function FlowBuilderRedirect() {
  return <LegacyRedirect to="/journeys" />
}
