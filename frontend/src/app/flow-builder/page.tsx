'use client'

import { ReactFlowProvider } from '@xyflow/react'
import FlowBuilder from '@/components/flow/FlowBuilder'
import './flow-builder.css'

/**
 * Full-page screen (design.md): Flow Builder breaks the shell so it gets the
 * whole viewport and a "commit or discard before you leave" moment. The Shell
 * hides its topbar/sidebar/footer for /flow-builder; FlowBuilder's own toolbar
 * carries the Back button, workflow name, sync badge, and Import/Reset/Export.
 *
 * React Flow is browser-only, so the page is a client component;
 * ReactFlowProvider lets the toolbar/sidebar use React Flow hooks.
 */
export default function FlowBuilderPage() {
  return (
    <div className="flex h-full flex-col bg-app">
      <ReactFlowProvider>
        <FlowBuilder />
      </ReactFlowProvider>
    </div>
  )
}
