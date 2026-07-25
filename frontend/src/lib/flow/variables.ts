/**
 * The full catalog of {{variables}} a script or instruction can use.
 * The orchestrator fills these from the contact record, the linked record
 * (order/shipment/invoice/booking/ticket), and the contact's memory at call
 * time (mirrors backend `flows/engine.py variables_for()`).
 *
 * Shown as a click-to-insert list under every script field, so the client
 * never has to guess what's available. To add a variable: add one entry
 * here — the picker renders it automatically.
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
      { key: 'name', label: 'Contact full name', example: 'Ahmad Faiz' },
      { key: 'first_name', label: 'Contact first name', example: 'Ahmad' },
      { key: 'phone', label: 'Contact phone number', example: '+60 12-345 6789' },
      { key: 'email', label: 'Contact email', example: 'ahmad@example.com' },
      { key: 'language', label: 'Preferred language', example: 'ms' },
      { key: 'company', label: 'Contact company', example: 'Faiz Trading' },
    ],
  },
  {
    group: 'Record',
    items: [
      { key: 'record_id', label: 'Record ID', example: '1042' },
      { key: 'record_type', label: 'Record type', example: 'order' },
      { key: 'record_status', label: 'Record status', example: 'processing' },
      { key: 'record_title', label: 'Record title', example: 'Order #1042' },
      { key: 'amount', label: 'Record amount', example: 'RM 320.00' },
      { key: 'currency', label: 'Currency', example: 'MYR' },
      { key: 'due_date', label: 'Due date', example: '28 Jun 2026' },
      { key: 'days_until_due', label: 'Days until due', example: '3' },
      { key: 'days_overdue', label: 'Days past due', example: '4' },
      { key: 'tracking_url', label: 'Tracking URL', example: 'https://track.example.com/…' },
      { key: 'link', label: 'Record link', example: 'https://app.example.com/orders/1042' },
    ],
  },
  {
    group: 'Business',
    items: [
      { key: 'business_name', label: 'Business name', example: 'ChatWood' },
      { key: 'agent_name', label: 'AI agent display name', example: 'Aisyah from ChatWood' },
      { key: 'support_number', label: 'Support phone number', example: '03-1234 5678' },
      { key: 'support_email', label: 'Support email', example: 'support@example.com' },
      { key: 'website', label: 'Website', example: 'https://example.com' },
    ],
  },
  {
    group: 'Memory',
    items: [
      { key: 'best_contact_time', label: 'Best time to reach, from memory', example: 'weekdays 12:30–14:00' },
      { key: 'preferred_channel', label: 'Channel that works for this contact', example: 'WhatsApp' },
      { key: 'last_contact_date', label: 'Last successful contact', example: '2 Jul 2026' },
    ],
  },
  {
    group: 'Other',
    items: [
      { key: 'situation', label: 'Known situation notes', example: 'prefers evening calls' },
      { key: 'account_number', label: 'Assigned sending number (filled by the outbound queue)', example: '+60 3-1234 5678' },
    ],
  },
]

/** Total count, for the collapsed-state hint ("Insert a variable (25)"). */
export const VARIABLE_COUNT = VARIABLE_GROUPS.reduce((n, g) => n + g.items.length, 0)
