// Manglish overlay for the `aiTasks` namespace (see ./index.ts): only the strings it
// rewrites — every other key falls back to English.
import type { Messages } from '../en'
import type { Overlay } from '../../types'

export const aiTasks: Overlay<Messages['aiTasks']> = {
  subtitle: 'Silent AI workers: they read what customers send — invoice, receipt, payment slip — and file it into your other apps. They never reply to the customer one.',
  intro: 'A background task reads every new customer message that fits its conditions, takes out what you asked for and settles the work with the tools you allow — a Google Sheet row, the file in Drive, another app. It never writes to the customer, and it runs whether the chatbot is on or off for that chat.',
  empty: 'No background tasks yet. Jom start from a template, e.g. “Save invoices”.',
  loadError: 'Aiyo, background tasks cannot load: {error}',
  readOnly: 'You can see these tasks and what they did, but only people who manage AI skills can change them.',
  confirmDelete: 'Confirm delete the task “{name}” and its run history ah? Notes it already wrote in chats will stay.',
  saved: '“{name}” saved liao.',
  deleted: '“{name}” deleted already.',
  tools: {
    add_private_note: { hint: 'A note in the chat for your team only — the customer won’t see it.' },
    add_conversation_label: { hint: 'Put a label on the conversation, e.g. invoice or payment-received.' },
    set_contact_field: { hint: 'Fill in one of your custom fields on the contact.' },
    hand_over_to_person: { hint: 'Flag the chat for a person to look at, with the reason.' },
    sheets_append_row: { hint: 'Time, name and phone go in front, then whatever values it found.' },
    drive_upload_attachment: { hint: 'Saves the customer’s attachment into your Drive folder.' },
    calendar_create_event: { hint: 'E.g. a payment due date. The customer won’t get invited.' },
    webhook_post: { hint: 'POSTs whatever it found as signed JSON to your https address.' },
  },
  editor: {
    subtitle: 'It reads customer messages and acts through the tools you allow — never replies to the customer.',
    channelsHint: 'Leave empty and it reads every channel.',
    keywordsHint: 'Any one of these words (capital or small letters also can). Leave empty for every message.',
    toolsHint: 'The AI only gets the tools in this list, nothing else. And there’s no tool that messages the customer.',
    connectHint: 'Connect it on the Apps page first lah.',
    noMcp: 'No external MCP servers ready yet. Add and test one at Developers → MCP servers, then its tools can show up here.',
    settings: {
      spreadsheetHint: 'Take it from the sheet’s URL: docs.google.com/spreadsheets/d/<ID>/…',
      folderHint: 'Optional — take it from the folder’s URL. Leave empty = the Drive’s top level.',
      webhookSecretHint: 'Must be at least 16 characters. Each POST comes with X-ChatBranch-Signature: t=<time>,v1=<HMAC-SHA256 of “t.body”>.',
    },
    needsSpreadsheet: 'Add the spreadsheet ID for Google Sheets first.',
    needsWebhook: 'The webhook needs an https URL and a signing secret — at least 16 characters ah.',
    saveFirst: 'Save the task first, then only can test.',
  },
  test: {
    hint: 'A dry run only: the AI reads your sample and shows what it would take out and do. Tools that change something are just previewed, don’t worry.',
    running: 'Testing, wait ah…',
    nothing: 'Nothing extracted leh.',
    noCalls: 'It wouldn’t call any tools.',
    notRelevant: 'Not related to this task — so it would do nothing.',
    conditionsNo: 'Note ah: this sample doesn’t meet the task’s conditions, so the task won’t run on it.',
  },
  runsModal: {
    empty: 'No runs yet. They’ll show up here a few seconds after a matching message comes in.',
  },
  templates: {
    invoices: {
      description: 'Reads invoices (PDF or photo), adds a row to a Google Sheet and keeps the file in Drive.',
    },
    receipts: {
      description: 'Reads payment slips and bank-transfer receipts, logs them in a sheet and labels the chat.',
    },
    blank: {
      description: 'Write your own instructions and choose the tools yourself.',
    },
  },
}
