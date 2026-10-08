'use client'

import { ReactFlowProvider } from '@xyflow/react'
import FlowBuilder from '@/components/flow/FlowBuilder'
import './flow-builder.css'

/**
 * Flow builder — renders full-bleed inside the app shell. React Flow is
 * browser-only, so the page is a client component; ReactFlowProvider lets the
 * canvas and its panels share one React Flow store.
 */
export default function FlowBuilderPage() {
  return (
    <div className="flex h-full min-h-0 flex-col bg-app">
      <ReactFlowProvider>
        <FlowBuilder />
      </ReactFlowProvider>
    </div>
  )
}
