'use client'

import { useState } from 'react'
import { Code2 } from 'lucide-react'
import { AreaPage } from '@/components/shell/AreaPage'
import { ApiKeysCard } from '@/components/developers/ApiKeysCard'
import { ConnectAiCard } from '@/components/developers/ConnectAiCard'
import { McpServersCard } from '@/components/developers/McpServersCard'
import { useT } from '@/i18n'

/**
 * Developers — everything for building on ChatBranch: personal API keys (REST +
 * MCP), connecting AI apps to ChatBranch's MCP server, and the external MCP
 * servers whose tools the in-app assistant may use. A key created here is kept
 * in memory for this visit only, so the connect snippets come out ready to
 * paste; it is never written to storage.
 */
export default function DevelopersPage() {
  const t = useT()
  const [freshToken, setFreshToken] = useState<string | null>(null)
  return (
    <AreaPage
      title={t('shell.nav.developers')}
      eyebrow={t('shell.groups.admin')}
      Icon={Code2}
      subtitle={t('admin.developers.page.subtitle')}
    >
      <div className="flex flex-col gap-5 pb-6">
        <ApiKeysCard onCreated={setFreshToken} />
        <ConnectAiCard freshToken={freshToken} />
        <McpServersCard />
      </div>
    </AreaPage>
  )
}
