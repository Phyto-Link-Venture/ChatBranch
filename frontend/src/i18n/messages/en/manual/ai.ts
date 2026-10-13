/** How-to guides — ai group. Keys: see lib/guides/types.ts. */
export const ai = {
  aiPersona: {
    title: 'Write the AI’s persona and house rules',
    summary: 'Tell the AI who it is, how it should sound and which rules every reply must follow — for the whole organization or for one outlet.',
    before: 'You need an admin login (the “Persona & rules” page needs the AI settings permission). Have a few lines ready about your business: its name, what you sell, and the tone you want.',
    steps: {
      s1: {
        title: 'Open Persona & rules',
        body: 'In the sidebar, open Library → “Persona & rules”. The line at the top says which level you are editing, for example “Editing for the organization.”',
      },
      s2: {
        title: 'Choose the level you are editing',
        body: 'Use the entity switcher at the top of the screen. “All entities” edits the whole organization. Click one outlet’s name to give that outlet its own settings.',
      },
      s3: {
        title: 'Write the persona',
        body: 'In the “Persona” box, describe who the AI is and how it talks. For example: “You are Aina, the friendly assistant of Kedai Bunga Melur, a florist in Shah Alam. Reply warmly and briefly, in the customer’s language.”',
      },
      s4: {
        title: 'Save the persona',
        body: 'Click “Save persona”. A green “Saved” appears next to the button, and the “In use:” label at the top of the card names the level whose persona is used.',
      },
      s5: {
        title: 'Give one outlet its own persona (optional)',
        body: 'With an outlet picked, turn on the switch “… has its own persona”. The box fills with the inherited text — change it and click “Save persona”. To go back, turn the switch off and click “Use the inherited persona”.',
      },
      s6: {
        title: 'Type your house rules',
        body: 'Under “House rules”, type one rule per line in “Rules for every outlet” (or “Extra rules for …” on an outlet). For example: “Never quote a delivery date.” and “Always offer to call back for complaints.”',
      },
      s7: {
        title: 'Save the rules',
        body: 'Click “Save house rules”. On an outlet, the rules it inherits are listed above the box under “Inherited — always on here”. Rules add up: an outlet keeps every rule above it and can only add more.',
      },
      s8: {
        title: 'Check what the AI receives',
        body: 'Scroll to “What the AI receives”. It shows the start of every prompt in order: the built-in safety rules, then your house rules, then the persona.',
      },
      s9: {
        title: 'Try it out',
        body: 'Click “Try it in the playground” at the top right of that card and chat with the AI as a customer to hear how it sounds.',
      },
    },
    done: 'The “What the AI receives” box shows your rules and persona, and the “In use:” label names the level you saved.',
    tips: {
      t1: 'Keep the persona to a short paragraph. Put facts like opening hours in the Knowledge base, not in the persona.',
      t2: 'Write rules as clear do’s and don’ts. One rule per line makes them easy to read and change.',
      t3: 'At the organization level, “Reset to the built-in default” brings back the original persona.',
      t4: 'The safety rules always come first and cannot be edited — your persona and rules can never override them.',
    },
    trouble: 'The save button is grey: nothing has changed yet, or your role can only read this page (“You can read these but not change them.”). The persona box is grey on an outlet: turn on “… has its own persona” first. The page shows the organization although you picked outlets: several are ticked in the switcher — pick just one.',
  },

  aiKnowledge: {
    title: 'Teach the AI about your business',
    summary: 'Add facts, policies, FAQs and notices the AI uses to answer customers, upload a document, and test what the AI finds for a question.',
    before: 'You need an admin login. Gather the basics: opening hours, address, prices, return policy and common questions. A text PDF of your price list or policy works too.',
    steps: {
      s1: {
        title: 'Open the Knowledge base',
        body: 'In the sidebar, open Library → “Knowledge base”. You see your entries, with filters by kind at the top.',
      },
      s2: {
        title: 'Start a new entry',
        body: 'Click “New entry”. The “New knowledge entry” form opens.',
      },
      s3: {
        title: 'Pick the type',
        body: 'In “Type”, choose: “Business fact” (always true, like your address), “Policy / T&C”, “FAQ” (adds a “Question” field), “Event / promotion” or “Sudden notice” (like “closed today for repairs”).',
      },
      s4: {
        title: 'Choose who it applies to',
        body: 'Under “Available to”, leave “Everyone (global)” so every outlet uses it, or choose “Selected entities” and tick one or more companies or outlets — each ticked one and its branches use it, no one else does.',
      },
      s5: {
        title: 'Write the title and content',
        body: 'Type a “Title” (for example “Opening hours”) and the “Content” (for example “We’re open 9am to 6pm, Monday to Saturday.”). For an FAQ, also type the question the way a customer would ask it.',
      },
      s6: {
        title: 'Add dates for events and notices (optional)',
        body: 'Fill “Starts (optional)” and “Ends (optional — it expires itself)”. The AI starts and stops mentioning it on its own — handy for a promotion or a holiday closure.',
      },
      s7: {
        title: 'Active and Published are different',
        body: 'Keep “Active” ticked so the AI may use the entry. Tick “Publish to the Help Center” only if customers should also read it on your public /help page.',
      },
      s8: {
        title: 'Save the entry',
        body: 'Click “Add entry”. You see “Knowledge added — the AI uses it on the next message.” and the entry shows a “Live” badge (or “Scheduled” if it starts later).',
      },
      s9: {
        title: 'Upload a document (optional)',
        body: 'Click “Upload document”, then drag a PDF, text, Markdown or CSV file (up to 10 MB) into the box. Pick the “Type” and who it is “Available to”, then click “Upload”. The AI reads the text; the file itself is not kept.',
      },
      s10: {
        title: 'Test what the AI finds',
        body: 'Scroll down to “Test retrieval”, type a customer question such as “how long does a refund take?” and click “Test”. “What it found” lists the matching entries, and “Exactly what the AI is handed” shows the text it answers from.',
      },
    },
    done: 'Your entry is in the list with a green “Live” badge, and “Test retrieval” finds it for a matching question.',
    tips: {
      t1: 'One topic per entry works best — “Delivery fees” and “Return policy” as two entries, not one long page.',
      t2: 'Use “Tags” as matching hints, such as “refund” or “bayaran balik”, so different wordings still find the entry.',
      t3: 'To stop the AI using an entry for a while, click “Turn off” on it instead of deleting it.',
      t4: 'Add an embeddings provider in Settings → AI keys & models so the AI matches questions by meaning, not just by words.',
    },
    trouble: '“Add entry” is grey: the title and content are both required. An upload is refused: scanned PDFs are pictures with no text — export a text PDF or paste the text into a new entry. “Test retrieval” finds nothing: try the customer’s own words, add tags, or write the entry as an FAQ.',
  },

  aiPlayground: {
    title: 'Test the AI safely as a pretend customer',
    summary: 'Chat with the AI the way a customer would, see why it answered, and correct it — nothing is sent to anyone, stored in a chat or booked.',
    before: 'Anyone who can see the AI monitor can use the playground. Trying another model or a draft persona needs the “Change AI behaviour” permission (usually admins).',
    steps: {
      s1: {
        title: 'Open the AI playground',
        body: 'In the sidebar, open Automate → “AI playground”. The badge “Test only — nothing reaches a customer” reminds you it is safe. On a phone, switch between “Setup”, “Chat” and “Inspector” with the tabs.',
      },
      s2: {
        title: 'Pick the outlet and channel',
        body: 'In “Setup”, choose the “Outlet” (its knowledge, skills, persona and review rule apply) and the “Channel”, such as WhatsApp.',
      },
      s3: {
        title: 'Make up a customer',
        body: 'Under “Customer”, keep “Made-up” and type a “Name” (for example Mei Ling), pick the “Language” and add any tags. Or choose “Existing contact” and search for a real one — it is only read, never changed.',
      },
      s4: {
        title: 'Or copy a real conversation (optional)',
        body: 'Click “Start from a real conversation”, search for the chat, choose “Messages to copy” and click “Copy into the playground”. The real conversation does not change.',
      },
      s5: {
        title: 'Write as the customer',
        body: 'Type in the “Write as the customer…” box, for example “Do you deliver to Penang?”, and click “Send”. The AI’s reply appears after a moment.',
      },
      s6: {
        title: 'Read what would happen in production',
        body: 'Under each reply a chip tells you what the real inbox would do: “Would send”, “Held for review”, “Hand over”, “Blocked” or “AI is off here”, with the reason.',
      },
      s7: {
        title: 'See why it answered',
        body: 'Click “Inspect” under a reply. The “Inspector” shows “What it was based on” (knowledge and lessons), the tools it called and the reply checks it passed.',
      },
      s8: {
        title: 'Teach a correction',
        body: 'If the answer was wrong, click “This should have said…”, type the better answer in “What it should have said” and click “Save the lesson”. For a great answer, click “Good answer” to keep it as an example.',
      },
      s9: {
        title: 'Try again or start over',
        body: 'Click “Regenerate” to get a new reply to the same message, or “Reset chat” to start a fresh conversation (your setup stays).',
      },
    },
    done: 'After saving a lesson you see “Saved — waiting for review in AI monitor → Learning” or “Saved — the AI uses it from its next reply.”',
    tips: {
      t1: 'Test the questions your customers ask most, in every language you serve.',
      t2: 'Admins can open “Try changes” to test another model or an unsaved persona, and “Compare with another set-up” side by side.',
      t3: 'Use “Download Markdown” to share a test conversation with your team.',
    },
    trouble: 'A “Sandbox” badge means no AI model is connected, so replies are only an echo — a superadmin adds a key in Settings → AI keys & models. “At most … messages”: click “Reset chat”. Too many tries in a minute: wait a minute and send again.',
  },

  aiTurnOn: {
    title: 'Turn the AI on or off',
    summary: 'Switch the AI on for the whole organization, one outlet, a schedule, one chat or one contact — and find out why it is off for a conversation.',
    before: 'Changing the master switch, schedule and never-AI tags needs the “Edit AI behaviour settings” permission (usually admins). The per-chat switch needs permission to reply to chats.',
    steps: {
      s1: {
        title: 'Find the master switch',
        body: 'In the sidebar, open Automate → “AI monitor”. The top card, “AI master switch”, says “AI is ON” or “AI is OFF” for the current scope.',
      },
      s2: {
        title: 'Choose where it applies',
        body: 'In the picker beside the switch, choose “Whole organization” or one outlet.',
      },
      s3: {
        title: 'Flip the switch',
        body: 'Click the switch. You see “AI switched on for …” (or off). Off means no AI replies, follow-ups or AI journey messages anywhere in that scope.',
      },
      s4: {
        title: 'Run the AI on a schedule (optional)',
        body: 'Click “Schedule & exceptions”. Under “AI in …”, choose “On a schedule”, add hours for each day with “Hours”, then choose “AI during these hours” or “AI outside these hours” (for example, the AI covers nights while people work office hours). Click “Save”.',
      },
      s5: {
        title: 'Keep the AI away from some contacts',
        body: 'In the same panel, use “Never use AI for contacts tagged” to pick tags such as VIP or Complaint, then click “Save”.',
      },
      s6: {
        title: 'Set an outlet differently (optional)',
        body: 'Pick an outlet in the picker and choose its own mode. “Use the parent’s setting” makes it follow the level above again. An “Off” higher up always wins.',
      },
      s7: {
        title: 'Switch it for one chat',
        body: 'In Work → “Chats”, open a conversation. Use the small “AI” switch at the top right of the chat header. Hover over it to read why the AI is on or off.',
      },
      s8: {
        title: 'Switch it for one contact',
        body: 'In Work → “Contacts”, open the contact. In the “AI for this contact” card, choose “Follow chat” (normal), “Never” or “Always”.',
      },
      s9: {
        title: 'Find out why the AI is off',
        body: 'If the chat’s “AI” switch is grey and cannot be clicked, a rule above it decided — the master switch or schedule, a never-AI tag, the contact set to “Never”, or the customer opted out. The tooltip names it; change it at that place.',
      },
    },
    done: 'The master switch card says “AI is ON”, and the “AI” switch in the chat header is on.',
    tips: {
      t1: 'The Live chip in the top bar has a quick on/off toggle for the master switch.',
      t2: 'When you set up a channel, “Who answers it” lets that inbox start new chats with the AI on or off.',
      t3: 'A reply a person approves still goes out even while the AI is switched off.',
      t4: 'Email chats have no AI switch — the AI does not auto-reply to email.',
    },
    trouble: 'The AI is on but never replies: the AI only answers conversations linked to a contact — link or create the contact from the chat’s info panel. Replies wait in the AI monitor: your review rule holds them (see “Review AI replies before they go out”). Replies look like an echo: no AI key is set yet (ask your superadmin).',
  },

  aiReview: {
    title: 'Review AI replies before they go out',
    summary: 'Decide which AI replies a person checks first, then approve, edit or reject them from the AI monitor or right inside the chat.',
    before: 'Anyone with the “See & approve AI drafts” permission can review (agents have it). Changing the review rule needs the “Edit AI behaviour settings” permission.',
    steps: {
      s1: {
        title: 'Open the AI monitor',
        body: 'In the sidebar, open Automate → “AI monitor”. The line under the title says the current rule, for example “Right now only risky replies are held.”',
      },
      s2: {
        title: 'Open the review rule',
        body: 'Click the “Settings” tab. In “Settings for”, keep “Organization default (every entity)” or pick one outlet.',
      },
      s3: {
        title: 'Choose who checks the AI',
        body: 'In “Review before sending” → “Who checks the AI before it sends”, choose: “Send straight away” (no checks), “Review risky replies” (only replies with money, dates or your risky words wait), “Review every reply”, or “Shadow mode” (the AI writes drafts but never sends — good for a trial).',
      },
      s4: {
        title: 'Set a deadline (optional)',
        body: '“Send held replies automatically after (minutes)”: 0 means a held reply waits for a person; 10 means it goes out by itself after 10 minutes if nobody acts. You can also add “Words that make a reply risky”, such as refund, discount.',
      },
      s5: {
        title: 'Save the rule',
        body: 'Click “Save changes” in the bar at the bottom. The “How it plays out” example on the right shows what the rule will do.',
      },
      s6: {
        title: 'Open the review queue',
        body: 'Click the “Waiting for review” tab — the number on it is how many replies wait. Each card shows the conversation and what the “AI proposes”.',
      },
      s7: {
        title: 'Approve a good reply',
        body: 'Click “Approve & send” (or press A). It goes to the customer through the normal channel.',
      },
      s8: {
        title: 'Edit a reply',
        body: 'Click “Edit” (or press E), change the text, then click “Send edited reply”. “Undo edit” brings back the AI’s words.',
      },
      s9: {
        title: 'Reject a reply',
        body: 'Click “Reject” (or press R), pick a reason or type one in “Why reject it? (optional)”, and confirm with “Reject”. Nothing is sent, and the reason helps the AI improve.',
      },
      s10: {
        title: 'Review from inside a chat',
        body: 'When you open a chat with a held reply, a banner “AI reply waiting for review” shows it with “Approve & send”, “Reject” and “Edit in monitor”.',
      },
    },
    done: 'The queue shows “All caught up”, and approved replies appear in the chat as sent.',
    tips: {
      t1: 'Start a new AI in “Shadow mode” or “Review every reply”, then relax to “Review risky replies” once you trust it.',
      t2: 'Press J and K to move between cards; tick several cards to approve or reject them together.',
      t3: 'The “AI activity” tab lists everything the AI wrote — sent, held, rejected or stopped.',
      t4: 'Turn on “Notify reviewers when a reply is waiting” so reviewers get a bell alert.',
    },
    trouble: '“A teammate already replied in this chat after the AI wrote this”: click “Send anyway” only if it still fits, otherwise reject it. “Not sent”: a safety guard stopped it — the customer opted out or the WhatsApp 24-hour window closed. The Settings tab is read-only: ask an admin for the permission.',
  },

  aiTeach: {
    title: 'Teach the AI with thumbs up and down',
    summary: 'Rate AI replies, say what they should have said, and turn that feedback into lessons the AI uses in later replies.',
    before: 'Anyone with the “See & approve AI drafts” permission can rate replies. Approving lessons and choosing the learning mode needs the “Edit AI behaviour settings” permission.',
    steps: {
      s1: {
        title: 'Find the thumbs on an AI reply',
        body: 'In Work → “Chats”, open a conversation and hover over a reply written by the AI. A small 👍 / 👎 pill appears beside it. On a phone, tap the “AI assistant” label above the reply.',
      },
      s2: {
        title: 'Thumbs up a good reply',
        body: 'Click 👍 (“Good reply”). One click is enough — the AI can use it as a good example.',
      },
      s3: {
        title: 'Thumbs down a bad reply',
        body: 'Click 👎. The box “What was wrong with this reply?” opens. Tick one or more reasons, such as “Wrong information” or “Too long”.',
      },
      s4: {
        title: 'Write the better answer',
        body: 'In “What should it have said?”, type the reply you would have sent. Add a “Note for your team” if useful. The customer never sees any of this.',
      },
      s5: {
        title: 'Send the feedback',
        body: 'Click “Send feedback”. You see “Thanks — a lesson is waiting for review.” (or that the AI uses it from now on).',
      },
      s6: {
        title: 'Choose the learning mode',
        body: 'Open Automate → “AI monitor” → “Learning” tab. Under “How the AI learns”, choose “Review first” (a person approves each lesson), “Learn from admins” (admins’ feedback goes live at once) or “Off”.',
      },
      s7: {
        title: 'Approve suggested lessons',
        body: 'Click the “Suggested” tab. Each card shows the question and the better answer. Click “Approve” to make it live, or “Dismiss” if it is wrong.',
      },
      s8: {
        title: 'Manage active lessons',
        body: 'The “Active” tab lists what the AI uses now. Click “Archive” to stop using one, or “Turn into knowledge” to make it a knowledge base FAQ.',
      },
      s9: {
        title: 'Add a rule by hand (optional)',
        body: 'Click “Add a rule”, type it — for example “Never promise same-day delivery” — and click “Add rule”. Rules are always in the AI’s instructions.',
      },
    },
    done: 'The lesson appears under the “Active” tab and you see “Lesson #… is live — the AI uses it from the next reply.”',
    tips: {
      t1: 'A thumbs-down with a written correction teaches the most. Reasons alone are only counted.',
      t2: 'Approving, editing and rejecting replies in the AI monitor also count as feedback.',
      t3: 'Lessons stay with their outlet and its branches — they never spread to a sister outlet.',
    },
    trouble: 'No suggested lessons appear: the learning mode may be “Off”, or nobody wrote a correction yet. A lesson does not seem to be used: check that it is under “Active”, not “Suggested”, and that it belongs to the right outlet.',
  },

  aiReviewer: {
    title: 'Let a stronger AI review the AI’s replies',
    summary: 'Have a stronger model check a sample of recent AI replies, see what went wrong, and turn its suggestions into lessons, knowledge or house rules.',
    before: 'You need the “Edit AI behaviour settings” permission to start a review (anyone who can see the AI monitor can read the results). For a real review, an AI key must be set in Settings → “AI keys & models” — OpenRouter for the default reviewer model.',
    steps: {
      s1: {
        title: 'Open the AI review tab',
        body: 'In the sidebar, open Automate → “AI monitor” and click the “AI review” tab.',
      },
      s2: {
        title: 'Pick the period',
        body: 'Click “Last 7 days” or “Last 30 days”. For exact dates or a different number of replies, open “Custom period and size”.',
      },
      s3: {
        title: 'Check the reviewer',
        body: 'Under the button you see “Reviewer: …” — the model that will check the replies — and how many replies it checks at most.',
      },
      s4: {
        title: 'Click Review now',
        body: 'Click “Review now”. The button shows “Reviewing…”; a larger review can take a minute or two, and you can leave the page meanwhile.',
      },
      s5: {
        title: 'Read the summary',
        body: 'Under “Latest review”, read “What the reviewer found”, the recurring problems and the “Suggestions for your setup”.',
      },
      s6: {
        title: 'Go through the issues',
        body: 'Under “Replies checked”, each card shows how serious it is, what kind of problem, the customer’s message, the AI’s reply, why it is a problem and a suggested fix. Click “Open chat” to see the whole conversation.',
      },
      s7: {
        title: 'Apply a suggestion',
        body: 'Click “Add as lesson”, “Add to knowledge” or “Add house rule” on a card. Lessons follow your learning mode; knowledge is saved as an inactive draft for you to check and switch on.',
      },
    },
    done: 'The card shows “Added as a lesson”, “Added to knowledge (inactive draft)” or “Added as a house rule”, and the review appears under “Past reviews”.',
    tips: {
      t1: 'Run a review after changing your knowledge or house rules to see whether the replies got better.',
      t2: 'Pick one outlet in the entity switcher first to review only its conversations — a house rule is then added to that outlet.',
      t3: 'Each review costs AI tokens; it shows on the AI bill as “AI review”.',
    },
    trouble: '“Automatic checks only”: no AI model is set up, so only simple checks ran — add a key in Settings → “AI keys & models”. “A review is already running”: wait for it to finish. The reviewer named is not the one you expected: OpenRouter is not configured, so the normal chat model reviewed; pick another model in “Reviewer model” next to “Review now” (models of a provider with no key are greyed out), or change the default with “Make this the default” there or “AI reviewer model” in Settings → “AI keys & models” → Advanced settings.',
  },

  aiSkills: {
    title: 'Add an AI skill with the wizard',
    summary: 'Describe in your own words something the AI should be able to do, answer a few questions, check and save it.',
    before: 'You need an admin login. Think of one thing at a time, for example “Tell customers whether we are open right now and when we open next”.',
    steps: {
      s1: {
        title: 'Open AI skills',
        body: 'In the sidebar, open Library → “AI skills”. The page is split into “Instructions”, “Tools”, “Reply checks” and “Connected apps”, plus the always-on “Built-in tools”.',
      },
      s2: {
        title: 'Start a new skill',
        body: 'Click “New skill”. The wizard opens at the “Describe” step.',
      },
      s3: {
        title: 'Describe what the AI should do',
        body: 'In “What should the AI be able to do?”, write it in plain words. You can also click one of the examples under “Try:”.',
      },
      s4: {
        title: 'Pick the kind (or let the AI pick)',
        body: 'Under “Kind of skill”, keep “Let the AI decide”. “Instructions” are plain-language guidance with no code; a “Tool” or “Reply check” is code for answers that must be exact.',
      },
      s5: {
        title: 'Continue and answer the questions',
        body: 'Click “Continue”. If something is unclear, the wizard asks up to four questions — answer them and click “Continue”, or click “Skip — draft now”.',
      },
      s6: {
        title: 'Check the draft',
        body: 'Read the “Name”, “Use when” (when the AI should use it) and the “Instructions”. Change any wording you like — everything is editable.',
      },
      s7: {
        title: 'Test tools and checks',
        body: 'For a tool or reply check, click “Run tests”. When every test passes, “Save & switch on” becomes clickable.',
      },
      s8: {
        title: 'Save it',
        body: 'Click “Save” (instructions) or “Save & switch on” (code). The skill appears in its section with an on/off switch.',
      },
      s9: {
        title: 'Try it in the playground',
        body: 'Open Automate → “AI playground” and ask a question that should use the skill. “Inspect” shows the tools it called.',
      },
    },
    done: 'You see “… saved — the AI uses it from the next message.” and the skill is listed with its switch on.',
    tips: {
      t1: 'Instructions are like a note to a new staff member. A connected app lets the AI use another system — a calendar, PDF documents, QR codes, payment links or a Google Sheet (see “Ready-made skills”). Code skills run in a sandbox with no internet and no files, so they can never make a PDF or send a request — use a ready-made app for that.',
      t2: 'New here? Click “Load starter instructions” in the empty Instructions section for ready-made examples.',
      t3: 'The built-in tools already answer time and date questions exactly — no skill needed for those.',
      t4: 'To pause a skill, flip its switch off instead of deleting it.',
    },
    trouble: '“Continue” is grey: write a slightly longer description. “Save & switch on” is grey: a test fails — go back, fix the code or the test, and run again, or click “Save anyway as disabled”. Without an AI key, the wizard drafts from a template library instead.',
  },

  aiCalendar: {
    title: 'Let the AI book appointments',
    summary: 'Add a calendar skill so the AI can offer free times, book, and cancel appointments — in ChatBranch’s own calendar or on Google Calendar.',
    before: 'You need an admin login. For ChatBranch’s own calendar you need nothing else. For Google Calendar, connect it first in Channels → Integrations (shared), or each agent connects their own in Settings → My connections.',
    steps: {
      s1: {
        title: 'Open AI skills',
        body: 'In the sidebar, open Library → “AI skills” and click “New skill” (or “Set up calendar booking” in the empty Connected apps section).',
      },
      s2: {
        title: 'Choose the connected app',
        body: 'In the wizard, click the “Connected app” row under “Kind of skill” → “Set up”. No description is needed.',
      },
      s3: {
        title: 'Pick the calendar',
        body: 'Under “Calendar”, choose “ChatBranch bookings” (works without Google — bookings live on the Bookings page) or “Google Calendar” (it shows “Connected” or “Not connected”).',
      },
      s4: {
        title: 'For Google: whose calendar',
        body: 'In “Whose calendar”, choose “Conversation’s assigned agent”, “This outlet’s shared calendar” or one specific connection. Leave “Calendar ID” as primary for the account’s main calendar.',
      },
      s5: {
        title: 'Choose what the AI may do',
        body: 'Under “What the AI may do”, tick “Check availability”, “Book appointments” and, if you like, “Cancel / reschedule”.',
      },
      s6: {
        title: 'Set your working hours',
        body: 'Under “Working hours”, switch each day open or closed and type the times in 24-hour format, for example 09:00 to 18:00. Use “Add break” for a lunch break.',
      },
      s7: {
        title: 'Set the appointment rules',
        body: 'Under “Appointments”, set the “Appointment length” (for example 30 minutes), “Buffer around busy times”, “Book up to” how many days ahead, the “Minimum notice” and “Active bookings per customer”.',
      },
      s8: {
        title: 'Name the calendar event',
        body: 'Under “Calendar event”, type the “Event title”. Use the Insert buttons to add the customer’s name or phone.',
      },
      s9: {
        title: 'Preview the free times',
        body: 'Click “Preview availability”. You see the times the AI would offer right now — a dry run, nothing is booked.',
      },
      s10: {
        title: 'Save the skill',
        body: 'Click “Save”. The skill appears under “Connected apps” with its switch on.',
      },
      s11: {
        title: 'See the bookings',
        body: 'Open Work → “Bookings” to see every appointment. They also show on the contact’s page and in the chat’s info panel. A booking is “Reserved” while the reply is being sent and “Booked” once it is out.',
      },
    },
    done: 'When a customer books in a chat, the appointment shows as “Booked” on the Bookings page and a 📅 note appears in the conversation.',
    tips: {
      t1: 'Test it in the AI playground first — calendar tools only preview there, nothing is booked.',
      t2: 'If replies are held for review, the booking is only made when someone approves the reply.',
      t3: 'You can cancel a booking from the Bookings page — the customer is not messaged automatically, so tell them in the chat.',
      t4: 'For online appointments, set “Video meeting” to Zoom (connect Zoom in Channels → Integrations first), Google Meet (Google Calendar only) or “My own meeting link”. The link is sent to the customer in its own message right after the AI confirms the booking.',
    },
    trouble: 'Google shows “Not connected”: connect it in Integrations — until then the AI says the calendar is unavailable and offers a teammate; it never books into ChatBranch instead. “No free times”: widen the working hours, shorten the minimum notice or allow more days ahead. A booking failed when the reply was sent: the reply is held and the chat is handed to a person.',
  },

  aiReadySkills: {
    title: 'Add a ready-made skill (PDF invoices, QR codes, payment links, Sheets)',
    summary: 'Switch on real capabilities without code: the AI creates PDF invoices, quotations, receipts and letters, QR codes, Stripe payment links, or logs rows to a Google Sheet.',
    before: 'You need an admin login. Payment links need Stripe connected at Channels → Integrations; the Sheets log needs Google Sheets connected there. Documents and QR codes need nothing else.',
    steps: {
      s1: {
        title: 'Open AI skills',
        body: 'In the sidebar, open Library → “AI skills” and scroll to “Ready-made skills”.',
      },
      s2: {
        title: 'Pick one and click Add',
        body: '“Documents (PDF)” makes invoices, quotations, receipts and letters; “QR codes” turns a link into a QR picture; “Payment links” creates a Stripe link; “Google Sheets log” writes a row. Click “Add” on the one you want.',
      },
      s3: {
        title: 'Fill in your letterhead (Documents)',
        body: 'Type your “Business name”, “Address”, “Phone” and “Email”. Under “Money”, set the “Currency”, the “Tax name” (for example SST) and the “Tax rate (%)” — 0 means no tax line.',
      },
      s4: {
        title: 'Choose what the AI may create',
        body: 'Under “What the AI may create”, switch on Invoices, Quotations, Receipts and/or Letters. Add your bank details in “Payment instructions” — they are printed on invoices and quotations.',
      },
      s5: {
        title: 'Optional: the look, logo and numbering',
        body: 'Under “Look”, click “Design template”: start from Classic, Modern, Minimal or Bold, click “Upload my design” to copy an invoice or letterhead you already use (PDF, PNG or JPEG — switch on “Also use my PDF as the page background” to print it under every page), or type a request in “Ask AI” such as “make it minimal with a navy header” (“Undo” takes it back). Each panel on the left changes one block; the preview on the right updates as you go, and “Editing” can change one document type only. Click “Apply”. “Advanced settings” on the form holds the logo, accent colour, registration and tax numbers, the printed language, and each type’s prefix and first number (for example INV- starting at 1001).',
      },
      s6: {
        title: 'Preview the PDF',
        body: 'Click “Preview PDF”. A sample opens in a new tab with your settings — nothing is sent and no number is used. For payment links and Sheets, “Check connection” tells you whether the integration is there.',
      },
      s7: {
        title: 'Save the skill',
        body: 'Click “Save”. The skill appears under “Connected apps” with its switch on.',
      },
      s8: {
        title: 'Try it in the playground',
        body: 'Open Automate → “AI playground” and ask “Can you send me an invoice for 2 haircuts?”. The inspector shows “Would send Invoice …” with “Open preview” — the real PDF, but nothing is kept.',
      },
      s9: {
        title: 'See what was sent',
        body: 'When a customer asks in a chat, the AI replies with the document number and total, then the PDF follows as a file. A “📄 Invoice … sent” note appears in the conversation. In the AI monitor, a held reply lists its files with “Open”.',
      },
    },
    done: 'The customer receives the reply and then the PDF (or QR picture). On LINE, Viber, SMS and the website chat — which can’t carry files — they get a secure download link instead.',
    tips: {
      t1: 'The server does every sum: the AI only sends quantities and prices, so totals and tax are always exact. The AI also has a built-in calculator for any other sums.',
      t2: 'If replies are held for review, the document is only sent when someone approves the reply. Rejecting it voids the document; its number stays used so there are no silent gaps.',
      t3: 'Give the AI the prices in your knowledge base or the skill’s instructions — it never invents a price.',
      t4: 'Chinese characters print only when the server has a Chinese font installed (DOCUMENT_CJK_FONT); otherwise the AI writes the document in English or Malay.',
      t5: 'Need it to look exactly like the invoice you already use? In “Design template”, switch to “Custom layout (HTML)”: start from “Malaysian classic” or click “Upload my design”, add the extra fields your layout prints (for example PRO or Attn tel — the AI asks the customer for them), and check “PDF preview”. A teammate can also make one from a chat: the info panel’s “Documents” → “New document”.',
    },
    trouble: 'The AI says it can’t create the document: check the skill is switched on and allows that type, and that the items have prices. Payment links say Stripe is not connected: connect it at Integrations. No file arrived on WhatsApp: the chat shows a download link instead and the note says why the file was refused.',
  },

  aiHumanLike: {
    title: 'Make AI replies feel human',
    summary: 'Let the AI wait for the customer to finish, show typing, split long answers into short messages, use emoji, reactions and quotes like a person and follow up when the customer goes quiet.',
    before: 'You need the “Edit AI behaviour settings” permission (usually admins).',
    steps: {
      s1: {
        title: 'Open the AI settings',
        body: 'In the sidebar, open Automate → “AI monitor” and click the “Settings” tab.',
      },
      s2: {
        title: 'Choose the level',
        body: 'In “Settings for”, keep “Organization default (every entity)” or pick one outlet to change it there only.',
      },
      s3: {
        title: 'Wait for the customer to finish',
        body: 'Under “Wait for the customer to finish”, set “Wait for the customer to finish (seconds)” — for example 20 — so several short messages get one answer. Set “…but never wait longer than (seconds)”, for example 90.',
      },
      s4: {
        title: 'Don’t reply to “ok, thanks”',
        body: 'Turn on “Acknowledgements don’t need a reply”. Optionally type a short acknowledgement such as “Noted, thanks!” to send instead of staying silent.',
      },
      s5: {
        title: 'Type like a person',
        body: 'Under “Type like a person”, turn on “Show typing and pause before sending”. “Typing speed (characters per second)” of 8 reads like a quick typist.',
      },
      s6: {
        title: 'Split long replies',
        body: 'Under “Split long replies”, choose “Split replies longer than the limit” or “Always split at paragraph breaks”. Never more than 3 messages, never mid-sentence.',
      },
      s7: {
        title: 'Emoji, reactions and quotes',
        body: 'Under “Human-like”, pick “Emoji in replies” (Off, Light or Expressive), turn on “React to the customer’s messages” so the AI can answer “ok thanks” with 🙏 instead of words, and keep “Quote the message being answered” on Auto so a reply to several messages quotes the one it answers.',
      },
      s8: {
        title: 'Follow up when the customer goes quiet',
        body: 'Turn on “Follow up when the customer goes quiet”. Set “First follow-up after (hours)”, for example 4, and “Most follow-ups per silence”, for example 2. For WhatsApp, pick a “Re-engagement template for closed WhatsApp windows”.',
      },
      s9: {
        title: 'Watch the example',
        body: 'The “How it plays out” panel on the right replays a sample conversation with your settings and updates as you change them.',
      },
      s10: {
        title: 'Save',
        body: 'Click “Save changes” in the bar at the bottom. On an outlet, “Use default” next to a field puts it back to the organization’s value.',
      },
    },
    done: 'The “How it plays out” example shows the wait, typing and split messages you chose, and the bar confirms the save.',
    tips: {
      t1: 'Typing shows only where the channel supports it (WhatsApp, Telegram, Messenger and Instagram); elsewhere the AI just pauses.',
      t2: 'Follow-ups only start after the AI asked something, and stop as soon as the customer replies, opts out, the chat is resolved or a person takes over.',
      t3: 'Add “Guidance for follow-up messages”, such as “Keep it to one friendly line”, to set their tone.',
      t4: 'Reactions and quotes work on WhatsApp and Telegram; email never gets emoji. A held reply reacts only when someone approves it, and the AI monitor shows the planned reaction on the draft.',
    },
    trouble: 'A note says a follow-up was skipped: the WhatsApp 24-hour window had closed and no re-engagement template is set — pick one. Replies feel slow: lower the wait seconds. The settings are read-only: ask an admin for the permission.',
  },

  aiKeys: {
    title: 'Connect an AI model (AI keys & models)',
    summary: 'Connect one or more AI providers so the AI writes real replies, choose the model, and turn on meaning-based search.',
    before: 'Only a superadmin can do this (the AI keys permission). Create an API key first on the provider’s website — for example OpenRouter, OpenAI, Anthropic, Google AI Studio (Gemini), Qwen or DeepSeek — and keep it ready to paste.',
    steps: {
      s1: {
        title: 'Open AI keys & models',
        body: 'Click “Settings” at the bottom of the sidebar. In the AI section, open “AI keys & models”.',
      },
      s2: {
        title: 'Choose your provider',
        body: 'Under “AI providers”, click the tile of your AI company — for example “OpenRouter”, “Anthropic (Claude)”, “Google Gemini” or “Qwen (Alibaba Cloud)”. Type in “Search providers” or use the “Connected” filter to find one quickly.',
      },
      s3: {
        title: 'Paste the key and test it',
        body: 'In the panel, paste your key into “API key” and click “Test connection”. “It works” means the key is good. Click “Save” — the tile now says “Connected”. One provider is enough.',
      },
      s4: {
        title: 'Pick the model (optional)',
        body: 'In the same panel, keep “Default model” on its default, choose another model from the list, or choose “Custom…” and type any model id. For Qwen, Kimi and Zhipu, also pick the “Region”.',
      },
      s5: {
        title: 'Choose who answers',
        body: 'In “Who answers”, keep “Automatic (priority order)” to try your connected providers in turn, or pick one provider to use only that one. If you change it, click “Save changes” at the top of the page.',
      },
      s6: {
        title: 'Know what “sandbox” means',
        body: 'With no key, the AI runs in sandbox mode: simple built-in answers or an echo, never a real model. The AI playground shows a “Sandbox” badge until a key works.',
      },
      s7: {
        title: 'Turn on meaning-based search (optional)',
        body: 'Open “Advanced settings”. In “Embeddings (semantic search)”, keep the “Provider” on “Automatic” (it uses your OpenAI or Gemini key) so “can I get my money back?” still finds your refund policy. “Off” matches by words only (keyword search).',
      },
      s8: {
        title: 'Test it',
        body: 'Open Automate → “AI playground” and send a message. A real reply with no “Sandbox” badge means the model is connected.',
      },
    },
    done: 'The page says “AI replies ready”, your provider’s tile says “Connected”, and the AI playground answers without a “Sandbox” badge.',
    tips: {
      t1: 'Saved keys are masked and never shown again — keep a copy in your password manager.',
      t2: 'OpenRouter gives you many models with one key; Gemini has a free tier for testing; Qwen and DeepSeek are low-cost options in Asia.',
      t3: 'What the AI costs shows on the AI bill page (Review → AI bill).',
      t4: 'Live voice calls read their keys from the voice agent’s own settings file, not from this page.',
    },
    trouble: 'Still in sandbox after saving: open the provider’s tile and click “Test connection” — the message says why (a wrong key, no credit, a model the account cannot use). A pinned provider never falls back to another; set “Who answers” to Automatic to let the next provider step in.',
  },

  aiVoice: {
    title: 'Choose the AI’s voice for calls',
    summary: 'Browse and preview voices for AI phone and WhatsApp calls, add your own voice, and teach the voice how to say tricky names.',
    before: 'You need an admin login. Adding, cloning and previewing voices, and syncing pronunciation, need an ElevenLabs key set up by your technical team.',
    steps: {
      s1: {
        title: 'Open Voice & calls',
        body: 'In the sidebar, open Automate → “Voice & calls”. Four cards at the top describe the call personalities, such as “Friendly Advisor”.',
      },
      s2: {
        title: 'Filter the voice library',
        body: 'In “Voice Library”, use the filters (gender, language, accent, source, persona) to narrow the list. “Clear filters” shows them all again.',
      },
      s3: {
        title: 'Listen to a voice',
        body: 'Click the play button on a voice card (“Play preview”). It speaks the sample line in its own language — open “Sample preview line” to read it.',
      },
      s4: {
        title: 'Select a voice',
        body: 'Click a voice marked “Ready”. As an admin, this saves it as the voice for every AI call — you see “Saved — every AI call now uses this voice.” and it stays picked after you reload. A call campaign can still pick a different voice for that campaign.',
      },
      s5: {
        title: 'Add your own voice (optional)',
        body: 'Click “+ Add voice”. Under “Add your own voice”, click “Upload file” (MP3 or WAV, 30–60 seconds of clear speech) or “Record” and read the script shown. Give it a name when asked.',
      },
      s6: {
        title: 'Or import an ElevenLabs voice (optional)',
        body: 'In the same window, under “or import an existing ElevenLabs voice”, paste the “ElevenLabs voice ID” and click “Import”.',
      },
      s7: {
        title: 'Fix how a word is said',
        body: 'Scroll to “Pronunciation” and click “Add term”. Type the word in “Term” (for example Northwind) and how it should sound in “Respelling”, using normal letters (for example Nort-wind).',
      },
      s8: {
        title: 'Check and save',
        body: 'Use the speaker buttons to hear your browser read the term and the respelling, then click “Save & sync”.',
      },
    },
    done: 'You see “Saved — every AI call now uses this voice.” after picking a voice, “Saved and synced to ElevenLabs.” after saving pronunciation, and any voice you added appears in the library marked “Ready”.',
    tips: {
      t1: 'Pronunciation fixes only work for ElevenLabs voices — OpenAI voices always read words as spelled.',
      t2: 'Only clone a voice with the speaker’s consent.',
      t3: 'Click the pencil on a voice card to rename it or change its language or persona.',
    },
    trouble: '“Clone failed” or “Import failed”: the ElevenLabs key is missing or your voice slots are full — ask your technical team. “Microphone access was unavailable”: allow the microphone in your browser and try again. “Saved. Not synced to ElevenLabs yet”: calls already use the new respellings; sync again later. “Picked for this browser only”: you are not an admin, so the voice was not saved for everyone — ask an admin to pick it.',
  },
}
