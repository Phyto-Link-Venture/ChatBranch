'use client'

import { useState } from 'react'
import { Code2 } from 'lucide-react'
import { AreaPage } from '@/components/shell/AreaPage'
import { ApiKeysCard } from '@/components/developers/ApiKeysCard'
import { ConnectAiCard } from '@/components/developers/ConnectAiCard'
import { McpServersCard } from '@/components/developers/McpServersCard'

/**
 * Developers — everything for building on ChatBranch: personal API keys (REST +
 * MCP), connecting AI apps to ChatBranch's MCP server, and the external MCP
 * servers whose tools the in-app assistant may use. A key created here is kept
 * in memory for this visit only, so the connect snippets come out ready to
 * paste; it is never written to storage.
 */
export default function DevelopersPage() {
  const [freshToken, setFreshToken] = useState<string | null>(null)
  return (
    <AreaPage
      title="Developers"
      eyebrow="Admin"
      Icon={Code2}
      subtitle="API keys, AI apps over MCP, and the external tools the assistant may use."
    >
      <div className="flex flex-col gap-5 pb-6">
        <ApiKeysCard onCreated={setFreshToken} />
        <ConnectAiCard freshToken={freshToken} />
        <McpServersCard />
      </div>
    </AreaPage>
  )
}
