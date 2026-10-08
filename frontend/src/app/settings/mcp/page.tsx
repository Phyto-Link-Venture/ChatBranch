import { LegacyRedirect } from '@/components/shell/LegacyRedirect'

/** MCP servers moved to Developers (alongside API keys and ChatBranch's own MCP server). */
export default function McpServersSettingsPage() {
  return <LegacyRedirect to="/developers" />
}
