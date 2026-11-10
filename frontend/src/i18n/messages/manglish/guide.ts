// Manglish overlay for the `guide` namespace (see ./index.ts): only the strings it
// rewrites — every other key falls back to English.
import type { Messages } from '../en'
import type { Overlay } from '../../types'

export const guide: Overlay<Messages['guide']> = {
  ui: {
    replayHint: 'Want to see it again? Press the ? button at the top of the page — any time also can.',
  },
  manual: {
    subtitle: 'A practical guide to every corner of ChatBranch — read it here, download it, or put it on your own website, up to you.',
    embedHelp: 'Paste this wherever you want the manual to show. When ChatBranch changes, it updates by itself — nothing for you to do.',
    standaloneHelp: 'Want your own copy? “Download HTML” saves one single file you can host anywhere you like.',
    footer: 'Every chapter is also a guided tour inside ChatBranch: just open the page and press the ? button in the top bar to play it again.',
  },
  tours: {
    shell: {
      intro: 'The parts of ChatBranch you’ll be touching on every single page.',
      steps: {
        s1: {
          body: 'Every area of ChatBranch stays here, grouped as Work, Automate, Library, Channels, Admin and Review. You only see what your role allows — so if something is missing, that’s why lah.',
        },
        s2: {
          body: 'Press Ctrl+K (⌘K on a Mac) and you can find contacts, conversations, templates, journeys and pages from anywhere. Very fast one.',
        },
        s3: {
          body: 'Got a few outlets or companies? Choose one here — then every page shows only that outlet’s data.',
        },
        s4: {
          body: 'See how many chats are waiting and how many calls are live right now. The dot tells you whether the inbox is connected in real time or not.',
        },
        s5: {
          body: 'Your own assistant: ask it anything or tell it what to do (“find Siti’s last order”, “turn the AI off for this chat”). Don’t worry, it asks you first before changing anything.',
        },
        s6: {
          body: 'Play the guide for this page again, redo this tour, or open the full manual — all from here.',
        },
      },
    },
    analytics: {
      intro: 'See how your conversations, calls and the AI are doing, all in one glance.',
      steps: {
        s1: {
          body: 'The Dashboard is the first thing you see after you sign in: a quick summary of the period you choose, for the outlet you picked.',
        },
        s2: {
          body: 'Switch between 7, 30 and 90 days — every card below follows along.',
        },
        s3: {
          body: 'Messages sent and delivered, how many customers replied, calls and how they ended — plus how much it went up or down from the period before.',
        },
        s4: {
          body: 'Reports break the same numbers down by agent, team, label and channel. Looking for AI costs and company bills? They’re under Review → AI bill.',
        },
      },
    },
    notifications: {
      intro: 'Everything that needs your attention, all in one list — no need to hunt around.',
      steps: {
        s1: {
          body: 'New customer messages, channel problems, journey handovers and admin news all land here. The number beside Notifications in the sidebar tells you how many you haven’t read yet.',
        },
        s2: {
          body: 'Click a category — Customer messages, Channel health and so on — to see only that kind. All brings everything back.',
        },
        s3: {
          body: 'Type some words to search, show only the unread or read ones, or pick a from–to date range. Up to you.',
        },
        s4: {
          body: 'Grouped by day, newest on top. Click a notification to go straight to what it’s about. Tick the boxes to mark or delete a few in one go.',
        },
        s5: {
          title: 'Clean up a bit',
          body: 'Mark all read clears the unread count. Delete read throws away the ones you already saw — don’t worry, it asks you first.',
        },
        s6: {
          body: 'The gear opens your own notification settings — switch each kind on or off, whatever you like.',
        },
      },
    },
    appearance: {
      intro: 'Make ChatBranch look the way you like lah.',
      steps: {
        s1: {
          body: 'Everything here changes only your own screen — other people keep theirs, so play all you want.',
        },
        s2: {
          body: 'Pick English, Bahasa Melayu or 简体中文 — the whole app switches on the spot.',
        },
        s3: {
          body: 'A live sample of headings, buttons and links. Pick something below and it changes right away.',
        },
        s4: {
          body: 'Ten looks to choose from — Paper & Ink, Graphite Pro, Soft Blocks, Ocean, Forest, Midnight, Rose, Sand, Nord and High contrast. Each card is a small preview; click one to try — don’t like, just switch back.',
        },
        s5: {
          body: 'Light, Dark, or System to just follow whatever your phone or computer is set to.',
        },
        s6: {
          body: 'Click S, M, L or XL, or drag the slider for any size between 85 % and 130 %. Everything on screen grows or shrinks together — easier on the eyes.',
        },
        s7: {
          body: 'Open this one for the accent colour, the body and heading fonts, line height, spacing (density, page width and margins, card padding), the layout (sidebar left or right, or a top bar on a computer), corners, card style and motion. Got a lot to play with.',
        },
        s8: {
          body: 'Reset to default undoes your changes. Admins can click Use as organization default, then everyone who hasn’t picked their own gets this look also.',
        },
      },
    },
    chats: {
      intro: 'Every channel in one inbox: answer, hand over, and let the AI help you out.',
      steps: {
        s1: {
          body: 'Type a name, a phone number or any words from the chat in this box — the list below will show only the chats that match.',
        },
        s2: {
          body: 'Click Filter to narrow down the list — by channel, status, who it’s assigned to, AI on or off, waiting for a reply and more. The small number tells you how many filters are on.',
        },
        s3: {
          body: 'All, Mine, Unassigned and Needs human are ready-made views. Set your filters, then use the ••• menu to save them as your own tab — next time, one click only.',
        },
        s4: {
          body: 'Newest activity on top — switch to “Newest from customer” above the list to sort by when the customer last wrote, and use All / Contacts / Not in contacts to see chats from your contacts or from new people. An orange time means the customer is waiting for you ah. Click a chat to open it — or press Select at the top to tick a few chats and turn the AI on or off, resolve or assign them all in one shot.',
        },
        s5: {
          body: 'Switch the AI off to answer yourself, or on to let it reply. If a rule higher up already decided, the switch is locked and tells you why. Resolve, beside it, closes the chat once you’re done.',
        },
        s6: {
          title: 'When need a real person',
          body: 'This yellow bar pops up when the AI asked for help or a rule handed the chat over. Click Take it to answer yourself, or Let AI continue to pass the chat back to the AI.',
        },
        s7: {
          body: 'Write a reply, or switch to Private note to leave a message only your team can see. Enter sends, Shift+Enter adds a new line. Type / for quick replies, and the clip is for attaching files.',
        },
        s8: {
          body: 'Who the customer is, which team and person take care of the chat, labels, the number it’s sent from, and the case summary. On a phone or tablet, open it with the Conversation details (ⓘ) button in the chat header.',
        },
      },
    },
    contacts: {
      intro: 'Your customer book — import, find and open any contact.',
      steps: {
        s1: {
          body: 'Everyone you talk to, for the outlet you picked at the top. The line under the title tells you how many got.',
        },
        s2: {
          body: 'Click Import to bring in a spreadsheet (.csv or .xlsx), a phone export (.vcf) or your Google Contacts. A short wizard matches the columns for you; duplicates are caught by phone and email, so nobody gets doubled.',
        },
        s3: {
          body: 'Click Add contact to key in one person by hand — name, phone, email and tags.',
        },
        s4: {
          body: 'Show only one entity, one tag or one record type. Click Clear to see everybody back.',
        },
        s5: {
          body: 'Type in the search box to find someone by name, phone, tag or entity; click a column heading to sort. Click a row and that contact opens.',
        },
        s6: {
          body: 'View opens the full profile. The pencil edits the contact and the bin deletes it — but it always asks you first, don’t worry.',
        },
        s7: {
          body: 'Somebody asked to share a customer who already sits under another branch? Requests waiting for you (or your own pending ones) are counted here. Open it to approve, deny or cancel.',
        },
      },
    },
    contact: {
      intro: 'Everything about one customer, all on one page.',
      steps: {
        s1: {
          body: 'Name, phone, email and tags. “Open in Chats” brings you straight to their conversation.',
        },
        s2: {
          body: 'A short brief of every call, chat and email with this person — generate it before you call them back, so you’re not blur.',
        },
        s3: {
          body: 'Every message and call from every channel, newest on top.',
        },
        s4: {
          body: 'Follow the chat’s own switch, or decide for this one person: the AI never answers them, or always does.',
        },
        s5: {
          body: 'Where you can reach them — WhatsApp, email, voice and more — with each channel’s health. Want them on another app? Invite them with a link or QR code.',
        },
      },
    },
    bookings: {
      intro: 'Every appointment the AI and your team booked, all here.',
      steps: {
        s1: {
          body: 'Appointments booked in chats — on Google Calendar or kept inside ChatBranch itself — for the outlet you picked.',
        },
        s2: {
          body: 'Bookings opens on the calendar straight away. Upcoming and Past show the same appointments as a list — can search and filter there.',
        },
        s3: {
          body: 'Every booking sits on its own day and time: solid = booked, dashed = held for an AI reply that hasn’t gone out yet. Click one to see who it’s with, open the chat or cancel it.',
        },
        s4: {
          body: 'Switch the view here lah. The arrows go back and forward, “Today” brings you back to now, and click a date to open that day.',
        },
        s5: {
          body: 'Pick a connected Google Calendar and its other events show in grey on top — meetings, leave, public holidays — so you can see when you’re really free. Read-only here ah.',
        },
        s6: {
          body: 'An appointment’s card got “Open the conversation” and, for a booked one still coming, “Cancel booking” — we ask you first, and the customer won’t get any message automatically.',
        },
      },
    },
    assignments: {
      intro: 'Which of your numbers or mailboxes talks to which customer.',
      steps: {
        s1: {
          body: 'Each customer is linked to one sending number per channel, so they always hear from the same number — less confusing for them. This page shows those links and lets you change them.',
        },
        s2: {
          body: 'Choose the channel first (WhatsApp, email and so on), then search for a contact by name or phone.',
        },
        s3: {
          body: 'How many contacts each number is taking care of, with its health. Click Manage contacts to move people around or pin them to one number.',
        },
        s4: {
          body: 'Shares out the unpinned contacts evenly across your healthy numbers. Pinned contacts stay put, won’t move one.',
        },
        s5: {
          body: 'How your numbers work as a team: how fast the AI replies, what happens if a number goes down, cold-message guards, and extra STOP / START words.',
        },
        s6: {
          body: 'Changes to the routing rules only count after you click Save routing rules — don’t forget ah.',
        },
      },
    },
    journeys: {
      intro: 'Messages, calls and checks that run by themselves — set up once, then relax.',
      steps: {
        s1: {
          body: 'A journey starts from a trigger (a new contact, a tag, a message, a date) and then sends messages, waits, makes decisions and hands over to people — all by itself.',
        },
        s2: {
          body: 'Click New journey and pick a ready-made template — welcome series, appointment reminder, re-engage and more — or start from a blank page if you prefer.',
        },
        s3: {
          body: 'Journeys opens on this list of every journey: what starts it, whether it’s live and how many people went in. Click Open (or the row) to edit one. Inside a journey, All journeys brings you back here; the pencil renames it and the ••• menu duplicates, exports or deletes it.',
        },
        s4: {
          body: 'Steps shows the journey as a simple list you read from top to bottom. Advanced canvas shows the same journey as a diagram — whichever one you find easier, use that lah.',
        },
        s5: {
          body: 'Add steps one by one: send a message, wait, check a condition or let the AI decide, call, update a tag. Click any step to change it.',
        },
        s6: {
          body: 'New journeys start paused, so nothing goes out while you’re still building. Click Go live when it’s ready; click Live again to pause it.',
        },
        s7: {
          body: 'Runs shows how many people started, where each person is now, and why anyone dropped out halfway.',
        },
      },
    },
    automations: {
      intro: 'Simple “when this happens, do that” rules for conversations — no coding needed.',
      steps: {
        s1: {
          body: 'A rule that works by itself on conversations — like “when a new chat contains the word refund, assign it to the Billing team”.',
        },
        s2: {
          body: 'Every rule got three parts: When (the event), If (optional conditions) and do (the actions). This line explains the whole idea in one sentence.',
        },
        s3: {
          body: 'Click Add rule, give it a name, pick the event, add conditions if you want, then choose what should happen.',
        },
        s4: {
          body: 'Each rule shows its number, its event, conditions and actions as small chips, and how many times it ran already. Every matching rule runs, top to bottom — drag a rule or use the arrows to change the order.',
        },
        s5: {
          body: 'Test checks a rule against a real conversation — nothing gets sent or changed, so test all you want. On / Off pauses a rule without deleting it. The pencil edits it; the bin deletes it.',
        },
        s6: {
          body: 'Use automations for quick reactions inside a chat. For longer, timed series of messages and calls to a contact, Journeys is the one.',
        },
      },
    },
    templates: {
      intro: 'Ready-made messages and scripts — write once, use everywhere.',
      steps: {
        s1: {
          body: 'Save the answers you keep giving again and again, then reuse them in chats, journeys and calls — no need to type every time.',
        },
        s2: {
          body: 'Messages are quick replies for chats. Call scripts are read out word for word on automated calls. WhatsApp (Meta-approved) are the templates Meta must give the green light to before you can message someone first.',
        },
        s3: {
          body: 'Click New quick reply (or New call script) to write one. Give it a short code so agents can find it fast in the reply box.',
        },
        s4: {
          body: 'Search, filter by channel, and click any template to edit it. Variables, like the customer’s first name, get filled in by themselves when the message goes out.',
        },
        s5: {
          body: 'In a chat, type / then the short code, and the quick reply drops straight into the reply box — saves so much typing.',
        },
      },
    },
    tags: {
      intro: 'Labels for your contacts, entities and conversations.',
      steps: {
        s1: {
          body: 'Tags group things together so you can find, filter and automate them — like “VIP”, “Wholesale” or “Needs follow-up”.',
        },
        s2: {
          body: 'A tag belongs to either Contacts, Entities or Conversations. Click one to see only those tags.',
        },
        s3: {
          body: 'Click New tag, choose a name, a colour and what it’s for. You can also add extra fields (like a membership number) and put tags in a group where only one can apply at a time.',
        },
        s4: {
          body: 'All your tags, grouped nicely, with any extra fields and the entity each one belongs to.',
        },
        s5: {
          body: 'The count shows how many contacts or entities got this tag — click it to see who. The pencil edits a tag; the bin deletes it.',
        },
      },
    },
    callLogs: {
      intro: 'Every finished call, with its summary and transcript.',
      steps: {
        s1: {
          body: 'All calls, Phone (SIP) or WhatsApp — click one to show only that kind. The number tells you how many got.',
        },
        s2: {
          body: 'Newest on top, with the contact, number, time and how it ended.',
        },
        s3: {
          body: 'Click a call to see the AI summary and the full transcript — who said what, all there. Click it again to close.',
        },
        s4: {
          body: 'Calls are started from a chat (the phone button) or by a journey. Once a call ends, it shows up here.',
        },
      },
    },
    aiMonitor: {
      intro: 'Keep an eye on what the AI says — see it, check it, approve it.',
      steps: {
        s1: {
          body: 'Turn the AI on or off for everyone, or give it a schedule (like evenings and weekends only).',
        },
        s2: {
          body: 'Replies the AI wrote that a person should check first. Approve sends it, Edit lets you fix the words, Reject throws it away — and every choice also teaches the AI something.',
        },
        s3: {
          body: 'Everything the AI wrote, sent or not, newest on top. Open one to see what the reply was based on.',
        },
        s4: {
          body: 'Lessons the AI picked up from thumbs up, thumbs down and edits. Depending on the learning mode, a person may need to approve each lesson before the AI uses it.',
        },
        s5: {
          body: 'Let a stronger AI check a sample of recent replies against your knowledge, house rules and persona. It spots mistakes and suggests what to add — a lesson, a knowledge item or a house rule — in one click. It only runs when you click Review now, not by itself.',
        },
        s6: {
          body: 'How many replies were sent, held or rejected, how often people liked them, and how many times chats got handed to a person.',
        },
        s7: {
          body: 'Choose how careful the AI should be: send straight away, hold only the risky replies (prices, refunds, promises), hold everything, or shadow mode (the AI drafts, people send).',
        },
        s8: {
          title: 'Try first, no risk',
          body: 'Opens the AI playground, where you chat with the AI like a customer would — nothing goes out to anybody, so try all you want.',
        },
      },
    },
    playground: {
      intro: 'Talk to the AI like a customer would — nothing gets sent, promise.',
      steps: {
        s1: {
          body: 'Choose the outlet, the channel and the customer — a made-up one, a real contact, or a copy of a real conversation, up to you.',
        },
        s2: {
          body: 'Type as if you’re the customer. The AI answers exactly like it would for real, but nothing is sent, saved or booked.',
        },
        s3: {
          body: 'What the reply was based on (knowledge, lessons, tools) and what would happen for real: send, hold for review, or hand over.',
        },
        s4: {
          body: 'Change the model or edit the persona, then compare two set-ups side by side. Like the result? “Save as persona” makes a good draft the real one; “Teach” turns a fix into a lesson.',
        },
      },
    },
    aiTasks: {
      intro: 'Silent AI workers that read what customers send and file it into your other apps — they never reply one.',
      steps: {
        s1: {
          title: 'A worker that never replies one',
          body: 'A background task reads new customer messages — an invoice, a receipt, a payment slip — and settles the paperwork with your apps. The customer won’t get any message from it.',
        },
        s2: {
          body: 'This line explains it in one sentence. It works whether the chatbot is on or off for a chat, but it stops when the AI master switch is off or the AI quota is finished.',
        },
        s3: {
          body: 'Pick “Save invoices” or “Log receipts”, or start blank. Tell it in your own words what to look for and what to do, tick the tools it may use, and test it on a sample first before switching it on.',
        },
        s4: {
          body: 'Each card shows when it runs, which tools it can use, who it’s available to and how its last few runs went.',
        },
        s5: {
          body: 'Switch a task on or off, open Runs to see what it pulled out and which tools it called for each message, or just edit it.',
        },
        s6: {
          body: 'Every run that did something leaves a private 🤖 note in the conversation, so your team knows what got filed.',
        },
      },
    },
    persona: {
      intro: 'How the AI introduces itself, and the rules it must always follow.',
      steps: {
        s1: {
          body: 'Who the AI is and how it sounds. Only one persona applies: the outlet’s own; if none, its head office’s; if none, the organization’s.',
        },
        s2: {
          body: 'Rules every reply must follow, like “Always be polite”. They stack up — a branch keeps every rule above it and can add its own on top.',
        },
        s3: {
          body: 'The exact opening of every prompt: safety rules, then house rules, then the persona. Try it out in the playground first before going live.',
        },
      },
    },
    knowledge: {
      intro: 'The facts the AI uses to answer your customers.',
      steps: {
        s1: {
          body: 'Facts, policies, FAQs, events and short notices. The AI only says what’s written here — it won’t anyhow make up prices or dates.',
        },
        s2: {
          body: 'New entry lets you type in a fact, policy, FAQ, event or notice. Upload document reads a PDF, text or CSV file for you. Events and notices can start and end on a date, so they expire by themselves — no need to remember to remove them.',
        },
        s3: {
          body: 'Click a kind to show only facts, policies, FAQs, events or notices. All brings everything back.',
        },
        s4: {
          body: 'Click an entry to edit it. The on/off switch keeps an entry saved but stops the AI from using it.',
        },
        s5: {
          body: 'Type a customer question to see exactly which entries the AI would read for it. If the right one didn’t come out, add the words your customers use (in every language) to its tags.',
        },
      },
    },
    skills: {
      intro: 'What the AI can do, and the checks every reply must pass first.',
      steps: {
        s1: {
          body: 'Instructions (how to handle a situation), tools (look something up), checks (a rule every reply must pass) and connected apps (like booking appointments in a calendar).',
        },
        s2: {
          body: 'Click New skill and describe what you want in plain words — no need to be technical. The wizard asks a few questions, writes the skill and lets you test it before you save.',
        },
        s3: {
          body: 'Date and time tools and the hand-over tool are always on — the AI uses them to get dates right and to call in a person when it should.',
        },
        s4: {
          body: 'Plain-language advice for one situation — like “when someone asks for a refund, ask for the order number first”.',
        },
        s5: {
          body: 'Give the AI a calendar so it can offer free times and book appointments by itself. Everything it books shows up on the Bookings page.',
        },
        s6: {
          body: 'Small programs the AI can run to look something up or work something out. Don’t know how to code? The wizard can write them for you.',
        },
        s7: {
          body: 'Rules every reply must pass before it goes out, like “never promise a discount”. A reply that fails gets rewritten, held or handed to a person.',
        },
        s8: {
          body: 'The switch turns a skill on or off — no need to delete it. The pencil edits it; the bin deletes it.',
        },
      },
    },
    voice: {
      intro: 'The voices the AI uses when it talks on phone and WhatsApp calls.',
      steps: {
        s1: {
          body: 'Choose and manage the voices the AI uses when it calls customers. This one is for calls only ah — the chat persona is under Persona & rules.',
        },
        s2: {
          body: 'Each voice comes with one of these four personalities, which shapes how the AI talks on a call.',
        },
        s3: {
          body: 'Click + Add voice to record or upload a short sample and clone your own voice, or bring one in from your voice provider.',
        },
        s4: {
          body: 'Filter by gender, language, accent, source or personality. The count on the right tells you how many voices match.',
        },
        s5: {
          body: 'Press Play preview to have a listen, then click its card. An admin’s pick is saved as the voice for every AI call; a campaign can still choose a different one.',
        },
        s6: {
          body: 'Teach the AI how to say tricky words — brand names, place names, people’s names — so the calls sound proper.',
        },
      },
    },
    aiProviders: {
      intro: 'Which AI service writes the replies, and the key it needs to work.',
      steps: {
        s1: {
          body: 'Tell ChatBranch which AI company to use and paste its key here. No key yet? Then the AI only answers in a simple test mode.',
        },
        s2: {
          body: 'Green means AI replies are ready to go. Grey means no key is set yet, so the AI cannot write real replies.',
        },
        s3: {
          body: 'Each tile is one AI company — OpenRouter, OpenAI, Anthropic, Gemini, Qwen, DeepSeek and more. Click one, paste its key and press Test connection. Connect as many as you like lah.',
        },
        s4: {
          body: 'Automatic tries your connected providers one by one, in order; pick one to use only that one. The chip tells you whether the choice is saved here (“from Settings”), set on the server (“from .env”) or “not set”.',
        },
        s5: {
          body: 'The AI reviewer model, ElevenLabs voices, embeddings (smarter knowledge search) and timings. The defaults are fine for most businesses, no need to touch.',
        },
        s6: {
          body: 'A provider’s panel saves by itself. Changes to “Who answers” or Advanced settings need Save changes at the top of the page; Discard undoes them.',
        },
      },
    },
    channels: {
      intro: 'Every number, mailbox and app your business talks on.',
      steps: {
        s1: {
          body: 'Every WhatsApp number, mailbox, bot, page and website chat your team answers from — all of them stay on this page.',
        },
        s2: {
          body: 'Click Add inbox to connect a new one. A step-by-step panel walks you through it, no need to panic: pick the channel → pick how to connect it → set it up.',
        },
        s3: {
          body: 'Each card is one inbox, with a coloured dot for its health — green means OK, amber or red means got something to look at.',
        },
        s4: {
          body: 'Click a channel to see only those inboxes; All brings everything back.',
        },
        s5: {
          body: 'Settings changes the inbox and who answers it (team, AI on or off, unknown customers). Test checks the connection on the spot. Pair shows the QR code for a personal WhatsApp number.',
        },
        s6: {
          body: 'One tile per channel, grouped by type. Click one to start. TikTok and Douyin need the platform’s approval first ah.',
        },
        s7: {
          body: 'The Meta webhook address and the default mail settings. You seldom need this one — maybe your developer will ask for it once.',
        },
      },
    },
    integrations: {
      intro: 'Your business tools — Google, Shopify, Stripe, MCP servers and all that. Customer-facing accounts stay on Channels.',
      steps: {
        s1: {
          body: 'Connections the whole organization, or one entity, shares — like a company Google Calendar the AI books appointments into.',
        },
        s2: {
          body: 'When something needs an app, ChatBranch tries your own connection first, then your entity’s, then its parent’s, then the organization’s. This box explains it all.',
        },
        s3: {
          body: 'One card per app, showing whether it’s connected already or not yet.',
        },
        s4: {
          body: 'Click Connect to sign in to the app and choose who the connection is for. If you see “Set up (admin, one-time)”, an admin must key in the app keys first.',
        },
        s5: {
          body: 'Every connection made so far, who’s using it and its status, plus buttons to manage it.',
        },
      },
    },
    account: {
      intro: 'Your own name, email, password and devices — all about you.',
      steps: {
        s1: {
          body: 'Everything on this page is about you only. Admins manage other people on the Users page.',
        },
        s2: {
          body: 'Set the name your colleagues see and your email, then click Save profile. Your username cannot change ah.',
        },
        s3: {
          body: 'Type your current password, then the new one two times. The strength hint helps you pick a good one; once it changes, every other device gets signed out.',
        },
        s4: {
          body: 'Turn on a code from an authenticator app at every sign-in, so even if someone steals your password, they still cannot get in. Scan the QR code, key in the code, and keep your recovery codes somewhere safe.',
        },
        s5: {
          body: 'See how many devices are signed in and sign the others out — useful if you used a shared computer or lost one.',
        },
      },
    },
    connections: {
      intro: 'Connect your own Google account here.',
      steps: {
        s1: {
          body: 'Link your own Google products. Only you use them — like the AI booking into your own calendar for chats assigned to you.',
        },
        s2: {
          body: 'Each Google product you can connect, and what it’s used for.',
        },
        s3: {
          body: 'A green Connected tag means all good. If it says it needs reconnecting, just sign in again.',
        },
        s4: {
          body: 'Click Connect and sign in with Google. Reconnect refreshes it; Disconnect takes back your permission any time you want.',
        },
        s5: {
          body: 'ChatBranch uses your own connection first, then a shared one from your entity, its parents and last of all the whole organization.',
        },
      },
    },
    users: {
      intro: 'Who can sign in, and what each person can do.',
      steps: {
        s1: {
          body: 'Add staff, change their role, and switch off a login when someone leaves.',
        },
        s2: {
          body: 'A quick reminder of the three built-in roles. Want custom roles? Make them under Permissions.',
        },
        s3: {
          body: 'Click Add user, then key in a username, their email and a role. With the welcome email ticked, they get a link to choose their own password — no need to pass them any password.',
        },
        s4: {
          body: 'Search by name, role or entity. A superadmin can change someone’s role directly in the Role column.',
        },
        s5: {
          body: 'Entity access chooses which outlets an agent can see. Resend invite emails a fresh set-password link to someone who hasn’t chosen one yet. Reset password emails a reset link or sets a new password. Deactivate blocks the login but keeps all the history — nothing lost.',
        },
      },
    },
    teams: {
      intro: 'Groups of people who share the work together.',
      steps: {
        s1: {
          body: 'A group of agents — like Sales or Support. New chats can go to a team first.',
        },
        s2: {
          body: 'A chat belongs to a team and, if you want, one person in it. With round-robin on, new chats are shared out one by one — fair for everybody.',
        },
        s3: {
          body: 'Click Add team, give it a name, pick its entity, tick its members, and choose whether new conversations are shared round-robin.',
        },
        s4: {
          body: 'Each team’s members, open chats and entity, all in one look.',
        },
        s5: {
          body: 'The pencil changes the team; the bin deletes it. Simple only.',
        },
      },
    },
    entities: {
      intro: 'Your organization, companies, branches and outlets, laid out as a tree.',
      steps: {
        s1: {
          body: 'Each chat, contact and many settings belong to one entity, so every outlet sees its own work only.',
        },
        s2: {
          body: 'A main entity sits at the top; branches and outlets sit under it. The top can see everything below — but never the other way round.',
        },
        s3: {
          body: 'How many main entities and sub-entities you got.',
        },
        s4: {
          body: 'Click Add entity, give it a name and choose its Parent. Leave Parent empty if you want a new main entity.',
        },
        s5: {
          body: 'The tree in order, with each entity’s parent and tags. The pencil edits; the bin deletes (only when nothing is under it any more).',
        },
      },
    },
    bindings: {
      intro: 'Link people, teams, contacts, tags and entities, all in one place.',
      steps: {
        s1: {
          body: 'One page for every link — which users see which entities, who’s in which team, which contacts got which tag, and so on.',
        },
        s2: {
          body: 'First choose the kind of link you want to change, like users and entities.',
        },
        s3: {
          body: 'Then pick the one item to work on — say, one entity.',
        },
        s4: {
          body: 'Switch members on or off. Each group card shows None, some or All; Manage opens up the full list.',
        },
        s5: {
          body: 'Nothing changes until you click Save changes in the bar at the bottom ah. Discard throws your edits away.',
        },
      },
    },
    permissions: {
      intro: 'Exactly what each role can see and do.',
      steps: {
        s1: {
          body: 'Each person got a role, and the role decides what they can see and change. Only a superadmin can edit roles.',
        },
        s2: {
          body: 'One card per role, with how many users have it and how many areas it can use. System roles are fixed — cannot rename or delete them.',
        },
        s3: {
          body: 'Click Add role to make your own custom role, like “Team lead”.',
        },
        s4: {
          body: 'Opens the role’s permissions as a grid of switches — one switch for each area of ChatBranch.',
        },
        s5: {
          body: 'Turn each permission on or off, or use Check all. Done already? Click Save changes.',
        },
        s6: {
          title: 'Good to know ah',
          body: 'The server always checks permissions too — hiding a menu item is not the only protection, so no worries.',
        },
      },
    },
    developers: {
      intro: 'Let other software and AI apps work together with ChatBranch.',
      steps: {
        s1: {
          body: 'Not so technical? Usually can leave this page to your developer lah.',
        },
        s2: {
          body: 'A key lets another program act as you. Each key can be limited to only the areas it needs — safer that way.',
        },
        s3: {
          body: 'Click Create key, name it and choose its access. The key shows only ONCE — copy it somewhere safe straight away, don’t say we never warn you.',
        },
        s4: {
          body: 'Step-by-step instructions to plug ChatBranch into an AI app like Claude, so it can read and act with your permissions.',
        },
        s5: {
          body: 'Give the Ask ChatBranch assistant tools from other systems. Don’t worry, it always asks first before changing anything.',
        },
      },
    },
    billing: {
      intro: 'How much each company owes for its AI usage.',
      steps: {
        s1: {
          body: 'Go through the months and pick a company. Branches are billed under their company.',
        },
        s2: {
          body: 'Usage by feature, the monthly fee and the total, all in your billing currency.',
        },
        s3: {
          body: 'Download PDF saves the bill as a PDF file (on an iPhone it opens in a new tab — share it from there); Print sends it to the printer. Billing managers set the markup, fee and currency under Billing terms.',
        },
        s4: {
          body: 'Monthly AI spending caps for companies, branches and people. Turns amber from 80%; at 100% the AI takes a break and chats go to a person until next month or until the cap is raised.',
        },
      },
    },
    billingSettings: {
      intro: 'The terms for billing each company for the AI it uses.',
      steps: {
        s1: {
          body: 'Set how AI usage turns into a bill for each company: currency, exchange rate, markup and monthly fee.',
        },
        s2: {
          body: 'The default currency, the exchange rate from US dollars, the markup percentage and the monthly fee. Open the bill brings you straight to the AI bill page.',
        },
        s3: {
          body: 'A worked example using your own numbers, so you can check whether the result makes sense or not.',
        },
        s4: {
          body: 'Give one company its own markup or fee. Leave a box empty and it just uses the default.',
        },
        s5: {
          body: 'Click Save billing terms to apply your changes — then all settled.',
        },
        s6: {
          title: 'Earlier months locked liao',
          body: 'A change applies to this month’s bill onwards. Earlier months keep the terms they had last time — the list shows which terms billed which months.',
        },
      },
    },
    activity: {
      intro: 'A record of who did what, and when — nothing escapes.',
      steps: {
        s1: {
          body: 'Every change made by people, API keys, AI apps, the assistant, automations, journeys and webhooks gets written down here.',
        },
        s2: {
          body: 'Pick a ready-made period, or choose your own dates also can.',
        },
        s3: {
          body: 'Search by words, or choose a person, an action, an entity or a source to narrow down the list.',
        },
        s4: {
          body: 'Newest on top. Click any row to see the full detail of that change.',
        },
        s5: {
          body: 'Download whatever you’re looking at as a spreadsheet file.',
        },
      },
    },
    testLab: {
      intro: 'Check that ChatBranch works properly — safely, with made-up data.',
      steps: {
        s1: {
          body: 'Scripted checks that run the real system with test data kept far away from your customers. Only a superadmin can see this page.',
        },
        s2: {
          body: 'Run all starts every check; Run selected runs only the ones you ticked. The line above tells you whether there’s test data and whether a real AI key is used.',
        },
        s3: {
          body: 'Checks grouped by area. Tick the ones you want, run just one with Run, or open one to see every single thing it checked.',
        },
        s4: {
          body: 'Your latest few runs. Click one to look at its results again.',
        },
        s5: {
          body: 'Fills the test area with sample contacts and chats you can click around and play with.',
        },
        s6: {
          body: 'Deletes everything the test lab made — and nothing else, don’t worry.',
        },
      },
    },
    settings: {
      intro: 'Your own preferences, plus the organization’s configuration.',
      steps: {
        s1: {
          body: 'Personal pages are just for you (appearance, language, notifications, your own Google connections). Organization and AI pages need a permission — a lock shows which ones.',
        },
        s2: {
          body: 'The name of the page you’re on, and one line on what it’s for.',
        },
        s3: {
          body: 'Change whatever you need here. Cannot open a page? Ask an admin for the permission it needs.',
        },
        s4: {
          body: 'When a page got unsaved changes, Save changes and Discard pop up here. Nothing is stored until you click Save changes ah.',
        },
        s5: {
          body: 'Looking for channels, templates, knowledge, skills, users or entities? They’re in the sidebar, not here.',
        },
      },
    },
  },
}
