// Manglish overlay for the `review` namespace (see ./index.ts): only the strings it
// rewrites — every other key falls back to English.
import type { Messages } from '../en'
import type { Overlay } from '../../types'

export const review: Overlay<Messages['review']> = {
  sources: {
    userHint: 'Someone signed in to the dashboard',
    mcpHint: 'An AI app (Claude, Cursor…) hooked up through MCP',
    assistantHint: 'The in-app assistant, doing things for the signed-in user',
    automationHint: 'An automation rule kicking in on a conversation event',
    webhookHint: 'A provider or gateway calling in (nobody signed in)',
    systemHint: 'Background work: the send queue, AI replies, health checks and so on',
  },
  activity: {
    subtitle: 'Everything people, API keys, AI apps, the assistant, automations, flows and webhooks did — all in one place.',
    exportFailed: 'Aiyo, export failed — {error}',
    needsPermission: 'Sorry ah, the activity log needs the “View activity log” permission — ask an admin.',
    unavailable: 'The activity log cannot open now — {error}.',
    emptyTitle: 'Nothing here leh',
    noMatch: 'No activity matches these filters — try loosening them sikit.',
    emptyBody: 'Actions will show up here once they happen.',
    loading: 'Loading activity, wait ah…',
  },
  callLogs: {
    loading: 'Loading call history, hang on…',
    empty: 'No calls yet — jom place one from Chats.',
    noTranscript: 'This call got no transcript leh.',
  },
  callDetail: {
    none: 'No call selected yet — pick one first.',
    notFound: 'Cannot find this contact record.',
  },
}
