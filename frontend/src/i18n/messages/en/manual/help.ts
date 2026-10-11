/** Troubleshooting (faq.<id>.q/.a) and glossary (glossary.<id>.term/.def). */
export const faq = {
  aiNotReplying: {
    q: 'The AI isn’t replying to a customer',
    a: 'First, open “AI monitor” and check the “AI master switch” is ON (and, if it runs on a schedule, that now is inside the schedule). Then open the chat and make sure its AI switch shows “AI on”. Next, look at the contact: “AI for this contact” must not say never, the contact must not carry a never-AI tag, and the chat must be linked to a contact at all. Then check the customer hasn’t sent STOP, and look under “Waiting for review” in case the reply is being held for a person to approve. Finally, if no AI key is set under Settings → “AI keys & models”, the AI only gives practice (sandbox) answers — ask an admin to add one.',
  },
  aiWrongAnswer: {
    q: 'The AI gave a wrong or odd answer',
    a: 'First, add the correct facts to the “Knowledge base” so the AI can find them. Then, if it is a rule it must always follow, add a house rule under “Persona & rules”. Next, press 👎 on the bad reply in the chat and type the right answer as a correction, so the AI learns from it. Finally, ask the same question in the “AI playground” to check the answer is now right.',
  },
  messageFailed: {
    q: 'My message has a red mark and didn’t send',
    a: 'First, hover the red mark to read the reason. If it says the 24-hour window is closed, the customer last wrote more than a day ago on WhatsApp — send an approved template instead of free text. Then check the customer hasn’t opted out (sent STOP). Next, see if the number that sends the message is disconnected on the “Channels” page. Finally, if it is your first message to someone who never wrote to you, the cold-message guard may have stopped it on purpose.',
  },
  window24h: {
    q: 'What is the 24-hour window and why can’t I type freely?',
    a: 'First, know that WhatsApp (and some other apps) only lets a business send normal messages for 24 hours after the customer’s last message. After that, you can only send a pre-approved template from “Templates”. Then, as soon as the customer replies, the window opens again and you can type freely. The chat list can filter conversations by whether the window is open.',
  },
  customerStoppedMessages: {
    q: 'A customer stopped getting messages',
    a: 'First, check the chat: if the customer sent STOP or UNSUBSCRIBE (or BERHENTI), they are opted out and nothing more is sent to them, by law and by design. Then, if they want messages again, they simply send START (or MULA) from their phone and they are opted back in. You can still send one message by hand with force, but only do this when the customer clearly asked for it.',
  },
  numberDisconnected: {
    q: 'A WhatsApp number shows Disconnected or Banned',
    a: 'First, open “Channels” and look at the health badge on the number’s card; press “Check now” to test it again. Then, for a QR-paired personal number, open its card and scan the new QR code with WhatsApp on that phone to pair it again. Next, for a WhatsApp Business (Cloud) number, ask an admin to check the Meta access token has not expired and paste a new one. While a number is down, conversations move to another healthy number where possible.',
  },
  emailNotArriving: {
    q: 'Customer emails aren’t showing up in Chats',
    a: 'First, check the mailbox on the “Channels” page has incoming mail (IMAP) details filled in, not only sending (SMTP). Then, for Gmail or Outlook, use an app password instead of your normal password. Next, look under “Not in contacts” at the top of the chat list — mail from someone who isn’t a contact yet lands there (automatic out-of-office replies are left out). Finally, mail is fetched every short while — wait a minute, then reopen Chats.',
  },
  widgetNotShowing: {
    q: 'The chat bubble doesn’t appear on my website',
    a: 'First, open your widget on the “Channels” page and copy the embed code again. Then make sure the whole code is pasted into your website, just before the closing body tag, and the page is published. Next, clear your browser cache or open the site in a private window. Finally, check the widget is switched on and that your website can reach the ChatBranch address.',
  },
  callFails: {
    q: 'A voice call won’t start',
    a: 'First, read the message on screen: if it says voice calling is not configured, ask an admin to set up the calling service — ChatBranch never fakes a call. Then check the phone number starts with the country code, for example +60123456789. Next, check the contact is not marked do-not-call and hasn’t opted out. Finally, look at “Call logs” to see how earlier calls ended.',
  },
  importSkipped: {
    q: 'Some contacts were skipped when importing',
    a: 'First, on the last step of the import, download “Errors (.csv)” to see each skipped row and why. Then fix phone numbers so they include the country code (for example +60…), or set the default country under your settings. Next, remember the duplicates choice you made: “skip” leaves existing contacts untouched, so those rows are counted as skipped. Finally, import the fixed file again.',
  },
  cantSeePage: {
    q: 'I can’t see a page, or it says I don’t have access',
    a: 'First, know that what you see depends on your role and its permissions — this is normal, not a fault. Then ask an admin to give your role the permission you need under “Users & roles”. Finally, sign out and back in after the change so the menu updates.',
  },
  wrongEntityData: {
    q: 'I see the wrong branch’s contacts or chats',
    a: 'First, look at the entity switcher at the top of the screen — it decides which business or branch you are looking at. Then pick the right one, or “All entities” to see everything you are allowed to see. If a branch you need is missing, ask an admin to assign you to it.',
  },
  billLooksLow: {
    q: 'The AI bill looks too low (or shows zero)',
    a: 'First, know that AI messages often cost less than one cent each, so small totals are normal. Then check the unattributed usage — work like drafting skills or the assistant isn’t billed to any company. Finally, check the markup: the bill is the real AI cost plus the markup and monthly fee set under Settings → “AI billing” — with no markup set, companies see the cost price.',
  },
  guideNotShowing: {
    q: 'The page guide doesn’t pop up',
    a: 'First, press the “?” button at the top of the screen and choose “Show this page’s guide”. Then, if you want guides to start by themselves, tick “Show guides on first visit” in that same menu. Guides only start by themselves the first time you open a page.',
  },
  forgotPassword: {
    q: 'I forgot my password',
    a: 'First, ask an admin in your company to reset it for you under “Users & roles”. Then sign in with the new password and change it to one only you know.',
  },
  slowFirstLoad: {
    q: 'A page is slow the first time I open it',
    a: 'First, give it a few seconds — some pages take longer the very first time they load. Then, if it stays slow every time, refresh the page and check your internet connection. If it is still slow, tell your admin which page it is.',
  },
}

export const glossary = {
  entity: {
    term: 'Entity',
    def: 'A business, brand or location inside ChatBranch. Each one has its own contacts, chats and settings.',
  },
  branch: {
    term: 'Branch (outlet)',
    def: 'An entity that sits under another one, like a shop under its head office. It uses its head office’s settings unless it sets its own.',
  },
  inbox: {
    term: 'Inbox (channel account)',
    def: 'One connected place customers can reach you: a WhatsApp number, an email mailbox, a Telegram bot, a Facebook page or a website chat. Set them up on the “Channels” page.',
  },
  conversation: {
    term: 'Conversation (chat)',
    def: 'All the messages between you and one customer on one channel. You read and answer them in “Chats”.',
  },
  contact: {
    term: 'Contact',
    def: 'A saved customer with a name, phone, email and notes. One contact can have chats on many channels.',
  },
  team: {
    term: 'Team',
    def: 'A group of agents who share the work. New chats can be handed to a team and spread between its members.',
  },
  tag: {
    term: 'Tag',
    def: 'A coloured label you stick on a contact, a conversation or an entity, like VIP or New lead. Tags help you sort, filter and automate.',
  },
  handover: {
    term: 'Handover',
    def: 'When a chat is passed from the AI to a person. It appears under the “Needs human” tab until someone takes it.',
  },
  aiMonitor: {
    term: 'AI monitor',
    def: 'The page that shows every reply the AI wrote. Here you can approve, edit or reject replies waiting for review.',
  },
  draft: {
    term: 'Draft',
    def: 'A reply the AI has written but not yet sent. Depending on your settings, a person may need to approve it first.',
  },
  persona: {
    term: 'Persona',
    def: 'The AI’s personality and way of speaking, such as friendly and short. You set it on “Persona & rules”.',
  },
  houseRules: {
    term: 'House rules',
    def: 'Short rules the AI must always follow, one per line, like “Never promise a refund”. A branch can add rules but cannot remove its head office’s.',
  },
  knowledge: {
    term: 'Knowledge base',
    def: 'The facts, FAQs and policies the AI reads before it answers. Keep it up to date so the AI answers correctly.',
  },
  lesson: {
    term: 'Lesson',
    def: 'Something the AI learned from feedback, such as a correction to a wrong answer. Lessons can wait for a person to approve them before the AI uses them.',
  },
  skill: {
    term: 'AI skill',
    def: 'An extra ability or instruction for the AI, like booking an appointment or checking a reply before it is sent. Manage them on “AI skills”.',
  },
  journey: {
    term: 'Journey',
    def: 'A step-by-step plan that runs by itself for each contact, like a welcome message, a wait of two days, then a reminder. Build them on “Journeys”.',
  },
  automation: {
    term: 'Automation',
    def: 'A simple “when this happens, do that” rule for conversations, like tagging every new chat that mentions price.',
  },
  template: {
    term: 'Template',
    def: 'A saved message you can reuse. On WhatsApp, an approved template is the only kind of message you can send outside the 24-hour window.',
  },
  window24h: {
    term: '24-hour window',
    def: 'The time after a customer’s last message when you may send them normal messages. When it closes, you need a template until they write again.',
  },
  optOut: {
    term: 'Opt-out',
    def: 'When a customer asks to stop receiving messages, for example by sending STOP. They can opt back in by sending START.',
  },
  sandbox: {
    term: 'Sandbox',
    def: 'Practice mode: things look real but nothing leaves ChatBranch. It is used when a channel or the AI has no keys set up yet.',
  },
  apiKey: {
    term: 'API key',
    def: 'A secret code that lets another program use ChatBranch on your behalf, with your permissions. Keep it private like a password.',
  },
  mcp: {
    term: 'MCP',
    def: 'A standard way for AI assistants, like Claude, to connect to ChatBranch and use its tools. You set it up with an API key on “API keys & MCP”.',
  },
}
