import type { MessageKey } from '@/i18n'

/**
 * The {{variables}} a message, brief or reason can use. The engine fills these
 * from the contact, their primary record and the business settings at send
 * time (FlowEngine.VariablesFor — keep the two in step). Custom fields are
 * appended live by the picker as {{cf_<key>}}. The tokens never change; their
 * names and group titles are translation keys.
 */
export interface FlowVariable {
  key: string
  lbl: MessageKey
  /** a sample value — sample words are a key, sample data (names, numbers) as is */
  example: string
  exampleKey?: MessageKey
}

export interface VariableGroup {
  lbl: MessageKey
  items: FlowVariable[]
}

export const VARIABLE_GROUPS: VariableGroup[] = [
  {
    lbl: 'flows.variables.groupContact',
    items: [
      { key: 'first_name', lbl: 'flows.variables.firstName', example: 'Aisha' },
      { key: 'name', lbl: 'flows.variables.fullName', example: 'Aisha Rahman' },
      { key: 'phone', lbl: 'flows.variables.phone', example: '+60 12-345 6789' },
      { key: 'email', lbl: 'common.fields.email', example: 'aisha@example.com' },
      { key: 'company', lbl: 'common.fields.company', example: 'Rahman Trading' },
      { key: 'language', lbl: 'flows.variables.language', example: 'ms' },
    ],
  },
  {
    lbl: 'flows.variables.groupBusiness',
    items: [
      { key: 'business_name', lbl: 'flows.variables.businessName', example: 'ChatBranch' },
      { key: 'agent_name', lbl: 'flows.variables.agentName', example: 'Mia' },
      { key: 'support_number', lbl: 'flows.variables.supportPhone', example: '03-1234 5678' },
      { key: 'support_email', lbl: 'flows.variables.supportEmail', example: 'help@example.com' },
      { key: 'website', lbl: 'flows.variables.website', example: 'https://example.com' },
    ],
  },
  {
    lbl: 'flows.variables.groupRecord',
    items: [
      { key: 'record_title', lbl: 'common.fields.title', example: 'Order #1042', exampleKey: 'flows.variables.exampleOrder' },
      { key: 'record_status', lbl: 'common.fields.status', example: 'shipped' },
      { key: 'record_id', lbl: 'flows.variables.reference', example: '1042' },
      { key: 'amount', lbl: 'flows.variables.amount', example: 'MYR 320.00' },
      { key: 'due_date', lbl: 'flows.variables.date', example: '28 Jun 2026' },
      { key: 'link', lbl: 'flows.variables.link', example: 'https://app.example.com/orders/1042' },
      { key: 'tracking_url', lbl: 'flows.variables.trackingLink', example: 'https://track.example.com/…' },
    ],
  },
  {
    lbl: 'flows.variables.groupMemory',
    items: [
      { key: 'best_contact_time', lbl: 'flows.variables.bestTime', example: 'weekdays after 6pm', exampleKey: 'flows.variables.exampleBestTime' },
      { key: 'preferred_channel', lbl: 'flows.variables.preferredChannel', example: 'WhatsApp' },
      { key: 'last_contact_date', lbl: 'flows.variables.lastContact', example: '2 Jul 2026' },
    ],
  },
]

/** Total count, for the collapsed-state hint ("Insert a variable (21)"). */
export const VARIABLE_COUNT = VARIABLE_GROUPS.reduce((n, g) => n + g.items.length, 0)
