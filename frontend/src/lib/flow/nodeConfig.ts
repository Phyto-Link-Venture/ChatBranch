import type { ConfigField, FlowConfig, NodeKind } from './types'

/**
 * n8n-style per-node configuration.
 *
 * Each node kind gets a list of fields; clicking a node opens the config
 * panel which renders these fields, and the values live in `node.data.config`
 * (so they travel with Export/Import JSON automatically).
 *
 * To add a field: add one entry here — the panel and the in-node summary
 * pick it up with no other code changes.
 */
/**
 * Third-party action steps. These labels are a CONTRACT with the backend:
 * `integrations/actions.py ACTIONS` maps exactly these strings to handlers,
 * and `flows/engine.py _do_action` routes any channel found in that map to
 * `_do_integration`. Change one side and you must change the other.
 */
export const INTEGRATION_ACTIONS = [
  'Create calendar event',
  'Find free slot',
  'Upload file to Drive',
  'Append row to Sheet',
  'Send via Gmail',
  'Update order status',
  'Create Stripe payment link',
] as const

export const CONFIG_SCHEMAS: Record<NodeKind, ConfigField[]> = {
  trigger: [
    { key: 'when', label: 'When does it fire?', type: 'select', options: ['On the due date', 'Days before due date', 'Days after due date', 'Days after due date (exactly)', 'Record status is', 'Contact has tag'] },
    { key: 'days', label: 'How many days?', type: 'number', help: 'Plain number: 7 = "7 days before due" or "day 7 after due" depending on the choice above. Ignored for "On the due date", "Record status is" and "Contact has tag". Values stay client-tunable (day-config).' },
    { key: 'status', label: 'Record status', type: 'text', help: 'Only used when "Record status is" is selected — the exact status value to match (e.g. "shipped").' },
    { key: 'tag', label: 'Contact tag', type: 'text', help: 'Only used when "Contact has tag" is selected — the tag to match (e.g. "vip").' },
    { key: 'window', label: 'Call window', type: 'select', options: ['9:00–12:00', '12:00–15:00', '15:00–18:00', 'Any allowed hours'] },
    { key: 'repeat', label: 'Repeat', type: 'select', options: ['Once', 'Daily until answered', 'Every 2 days'] },
    { key: 'contacts', label: 'Apply to contacts', type: 'contactselect', help: 'Leave empty to enrol EVERY contact whose trigger condition matches (the default). Pick names to scope this trigger to just them — useful for piloting a flow on a few contacts before rolling it out.' },
  ],
  stage: [
    { key: 'stage', label: 'Stage', type: 'select', options: ['Pre-due', 'Normal', 'Serious', 'Critical', 'Ghost'] },
    { key: 'rangeType', label: 'Range is measured…', type: 'select', options: ['Days after due date', 'Days before due date'] },
    { key: 'fromDay', label: 'From day', type: 'number' },
    { key: 'toDay', label: 'To day', type: 'number', help: 'Leave empty for open-ended (e.g. day 31+). Values stay client-tunable via day-config.' },
  ],
  action: [
    { key: 'skill', label: 'Agent skill for this step', type: 'skillselect', help: 'The instruction module the AI loads to run this step — manage them in the sidebar under "Agent skills".' },
    { key: 'channels', label: 'Channels', type: 'multiselect', options: ['AI voice call', 'Script call', 'WhatsApp', 'Telegram', 'SMS', 'Email', 'In-app push', 'Internal log', ...INTEGRATION_ACTIONS], help: 'Pick every channel this step may use — e.g. WhatsApp + SMS + in-app push for a reminder. Compliance caps (max 1 call + 2 messages/day) still apply across all of them. The last group are third-party actions (Settings → Integrations); with nothing connected they log a clear skip instead of failing the step.' },
    { key: 'callVia', label: 'Call via', type: 'select', options: ['Phone (SIP)', 'WhatsApp (Baileys)'], help: 'Used by both call channels ("AI voice call" and "Script call"): dial the phone line over SIP, or place a WhatsApp voice call through a linked personal number — no trunk needed.' },
    { key: 'scriptTemplateId', label: 'Script template', type: 'scripttemplateselect', help: 'Only used when "Script call" is selected: the contact is dialed, the agent reads this script verbatim, then hangs up — no conversation, no listening. Works over SIP or WhatsApp — see "Call via" above. Manage the library in Settings → Script Templates.' },
    { key: 'voice', label: 'Agent voice', type: 'voiceselect', help: 'Voice the agent uses for this step’s call. Leave on the native default, or pick a cloned ElevenLabs voice. Applies to "Script call" and "WhatsApp (Baileys)" AI voice calls.' },
    { key: 'model', label: 'AI model', type: 'select', options: ['Haiku 4.5', 'Sonnet 5', 'Opus 4.8', 'Fable 5'], help: 'Cheapest model that can do the job (diagram 03).' },
    { key: 'script', label: 'Script / instruction', type: 'textarea', variables: true, help: 'What the caller says or does. Click a variable below to insert it — values are filled in from the account at call time.' },
    { key: 'maxAttempts', label: 'Max attempts', type: 'number' },
    // ---- third-party action settings (only read by the matching channel) ----
    { key: 'eventTitle', label: 'Calendar event title', type: 'text', help: 'Used by "Create calendar event". Falls back to the script text.' },
    { key: 'eventStart', label: 'Event start', type: 'text', help: 'Used by "Create calendar event" / "Find free slot": an ISO datetime (2026-08-01T10:00). Empty = tomorrow, same time.' },
    { key: 'eventMinutes', label: 'Event length (minutes)', type: 'number', help: 'Used by "Create calendar event" / "Find free slot". Default 30.' },
    { key: 'calendarId', label: 'Calendar id', type: 'text', help: 'Overrides the calendar chosen on the integration account. Empty = that account’s default.' },
    { key: 'fileName', label: 'Drive file name', type: 'text', help: 'Used by "Upload file to Drive". The script text becomes the file body.' },
    { key: 'spreadsheetId', label: 'Spreadsheet id', type: 'text', help: 'Used by "Append row to Sheet". Empty = the id set on the integration account.' },
    { key: 'sheetRange', label: 'Sheet range', type: 'text', help: 'Used by "Append row to Sheet", e.g. Sheet1!A:Z.' },
    { key: 'subject', label: 'Email subject', type: 'text', help: 'Used by "Send via Gmail".' },
    { key: 'orderStatus', label: 'New record status', type: 'text', help: 'Used by "Update order status": the status the contact’s primary record moves to (e.g. shipped). Connected Shopify accounts also get the note pushed back.' },
    { key: 'successUrl', label: 'Payment success URL', type: 'text', help: 'Used by "Create Stripe payment link" — where Stripe sends the customer after paying.' },
  ],
  decision: [
    { key: 'basedOn', label: 'Decided from', type: 'select', options: ["Contact's answer", 'Record status', 'Contact tag'] },
    { key: 'status', label: 'Record status', type: 'text', help: 'Only used when "Record status" is selected — the exact status value to match.' },
    { key: 'tag', label: 'Contact tag', type: 'text', help: 'Only used when "Contact tag" is selected — the tag to match.' },
  ],
  handover: [
    { key: 'team', label: 'Hand over to', type: 'select', options: ['Senior agent', 'Compliance', 'Legal', 'Manager'] },
    { key: 'priority', label: 'Priority', type: 'select', options: ['Normal', 'High', 'Urgent'] },
    { key: 'reason', label: 'Reason / context for the human', type: 'textarea', variables: true },
  ],
  end: [
    { key: 'logCode', label: 'Log code', type: 'text', help: 'e.g. RESOLVED, FOLLOW-UP-SET, NO-ANSWER, ESCALATED' },
    { key: 'updateMemory', label: 'Update MEMORY.md', type: 'select', options: ['Yes', 'No'] },
  ],
}

/** Values a freshly dropped node starts with. */
export const DEFAULT_CONFIG: Record<NodeKind, FlowConfig> = {
  trigger: { when: 'Days after due date', days: 1, status: '', tag: '', window: 'Any allowed hours', repeat: 'Once', contacts: [] },
  stage: { stage: 'Normal', rangeType: 'Days after due date', fromDay: 1, toDay: 7 },
  action: {
    skill: '', channels: ['AI voice call'], callVia: 'Phone (SIP)', scriptTemplateId: '', voice: '',
    model: 'Sonnet 5', script: '', maxAttempts: 3,
    eventTitle: '', eventStart: '', eventMinutes: 30, calendarId: '', fileName: '',
    spreadsheetId: '', sheetRange: '', subject: '', orderStatus: '', successUrl: '',
  },
  decision: { basedOn: "Contact's answer", status: '', tag: '' },
  handover: { team: 'Senior agent', priority: 'Normal', reason: '' },
  end: { logCode: '', updateMemory: 'Yes' },
}
