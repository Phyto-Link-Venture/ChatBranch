// Manglish overlay for the `analytics` namespace (see ./index.ts): only the strings it
// rewrites — every other key falls back to English.
import type { Messages } from '../en'
import type { Overlay } from '../../types'

export const analytics: Overlay<Messages['analytics']> = {
  page: {
    refreshFailed: 'cannot refresh, showing the previous data',
    unavailable: 'Dashboard not available leh',
    unavailableBody: 'Cannot reach the API — is the backend running?',
    loading: 'Loading the dashboard, wait ah…',
    byEntitySub: 'All outlets side by side — pick one in the top bar to go deeper',
  },
  kpi: {
    noSends: 'nothing sent yet',
    noReplies: 'no replies to count yet',
    noChange: 'no change from before',
  },
  daily: {
    empty: 'No messages in this period yet leh.',
  },
  health: {
    unavailable: 'Cannot get channel health now — is the API running?',
    checkingChannels: 'Checking the channels, sekejap ah…',
    noneManage: 'No channel accounts yet — go {link}.',
    noneAdmin: 'No channel accounts yet — an administrator will connect them in Channels.',
  },
  waiting: {
    unavailable: 'Live data not available right now.',
    nobody: 'Nobody waiting — every conversation got an answer liao.',
  },
  flows: {
    tooltip: '{name}: {reached} of {entered} made it to the end today',
    unavailable: 'Flow numbers not available right now.',
    none: 'No flow live yet. Flows can send follow-ups for you — {link}.',
  },
  reports: {
    subtitle_one: 'Who settled what — conversations touched in the last {count} day',
    subtitle_other: 'Who settled what — conversations touched in the last {count} days',
    empty: 'No conversations in this period.',
    emptyLabels: 'Nothing got labelled yet.',
  },
  funnel: {
    empty: 'No outbound messages in this period yet — the funnel will fill up once you start sending.',
  },
  records: {
    empty: 'No records linked to contacts in this period yet.',
  },
  accounts: {
    empty: 'No messages in this period yet — send something from the inbox first, then can see per-account volume.',
  },
  outcomes: {
    empty: 'No completed calls in this period yet.',
    flagged_one: '{count} call flagged for a person to follow up',
    flagged_other: '{count} calls flagged for a person to follow up',
  },
  healthEvents: {
    empty: 'No health changes in this period — accounts all steady.',
  },
  usage: {
    subtitle:
      'Every LLM call metered (replies, summaries, voice calls). Costs are only estimates from the price table — check against your provider’s billing before you invoice anybody.',
    empty: 'Nothing recorded in this period yet.',
  },
}
