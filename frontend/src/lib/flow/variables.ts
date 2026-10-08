/**
 * The {{variables}} a message, brief or reason can use. The engine fills these
 * from the contact, their primary record and the business settings at send
 * time (FlowEngine.VariablesFor — keep the two in step). Custom fields are
 * appended live by the picker as {{cf_<key>}}.
 */
export interface FlowVariable {
  key: string
  label: string
  example: string
}

export interface VariableGroup {
  group: string
  items: FlowVariable[]
}

export const VARIABLE_GROUPS: VariableGroup[] = [
  {
    group: 'Contact',
    items: [
      { key: 'first_name', label: 'First name', example: 'Aisha' },
      { key: 'name', label: 'Full name', example: 'Aisha Rahman' },
      { key: 'phone', label: 'Phone number', example: '+60 12-345 6789' },
      { key: 'email', label: 'Email', example: 'aisha@example.com' },
      { key: 'company', label: 'Company', example: 'Rahman Trading' },
      { key: 'language', label: 'Preferred language', example: 'ms' },
    ],
  },
  {
    group: 'Business',
    items: [
      { key: 'business_name', label: 'Business name', example: 'ChatBranch' },
      { key: 'agent_name', label: 'AI agent name', example: 'Mia' },
      { key: 'support_number', label: 'Support phone', example: '03-1234 5678' },
      { key: 'support_email', label: 'Support email', example: 'help@example.com' },
      { key: 'website', label: 'Website', example: 'https://example.com' },
    ],
  },
  {
    group: 'Record (order, booking, ticket…)',
    items: [
      { key: 'record_title', label: 'Title', example: 'Order #1042' },
      { key: 'record_status', label: 'Status', example: 'shipped' },
      { key: 'record_id', label: 'Reference', example: '1042' },
      { key: 'amount', label: 'Amount', example: 'MYR 320.00' },
      { key: 'due_date', label: 'Date', example: '28 Jun 2026' },
      { key: 'link', label: 'Link', example: 'https://app.example.com/orders/1042' },
      { key: 'tracking_url', label: 'Tracking link', example: 'https://track.example.com/…' },
    ],
  },
  {
    group: 'From memory',
    items: [
      { key: 'best_contact_time', label: 'Best time to reach', example: 'weekdays after 6pm' },
      { key: 'preferred_channel', label: 'Preferred channel', example: 'WhatsApp' },
      { key: 'last_contact_date', label: 'Last contact', example: '2 Jul 2026' },
    ],
  },
]

/** Total count, for the collapsed-state hint ("Insert a variable (21)"). */
export const VARIABLE_COUNT = VARIABLE_GROUPS.reduce((n, g) => n + g.items.length, 0)
