/** How-to guides — work group. Keys: see lib/guides/types.ts. */
export const work = {
  replyToChat: {
    title: 'Reply to a customer’s message',
    summary: 'Open a conversation, write an answer, add a file if you need to, and mark the chat as done when the customer is helped.',
    steps: {
      s1: {
        title: 'Open the inbox',
        body: 'In the sidebar, under Work, click “Chats”. The screen has three parts: the list of conversations on the left, the open conversation in the middle and its details on the right (on a wide screen). On a phone or tablet, open the details with the “Conversation details” (ⓘ) button at the top of the chat.',
      },
      s2: {
        title: 'Pick a conversation',
        body: 'Click a row in the list. A small coloured dot on a row means “Waiting for our reply”. The messages appear in the middle, the newest at the bottom.',
      },
      s3: {
        title: 'Make sure “Reply” is selected',
        body: 'Under the messages are two tabs: “Reply” and “Private note”. Keep “Reply” selected — a private note is only seen by your team, never by the customer.',
      },
      s4: {
        title: 'Type your answer',
        body: 'Click the box that says “Type a message (/ for quick replies)” and write your answer, for example: “Hi Aisyah, your order is ready for pickup today.” The box grows as you type; press Shift+Enter to start a new line.',
      },
      s5: {
        title: 'Add an emoji or a ready-made answer',
        body: 'Click the smiley button, “Emoji”, left of the message box: pick a category, search (“thanks”, “terima kasih” or “谢谢” all work) or choose from “Recently used” — the emoji goes in where your cursor is. To use a saved answer, type “/” and pick one from the “Quick replies” list.',
      },
      s6: {
        title: 'Attach a file (optional)',
        body: 'Click the paperclip, “Attach a file”, and choose a photo, PDF, video or audio file (jpg, png, webp, pdf, mp4, ogg or mp3, up to 16 MB). The file name appears above the box; you can type a caption to go with it.',
      },
      s7: {
        title: 'Send it',
        body: 'Press Enter (Shift+Enter only adds a new line) or click the dark arrow button on the right. Your message appears on the right side of the conversation, with small ticks for “Sent”, “Delivered” and “Read” where the app reports them.',
      },
      s8: {
        title: 'Mark the chat as done',
        body: 'When the customer is helped, click “Resolve” at the top right of the conversation. The button changes to “Reopen”; if the customer writes again, the chat opens again by itself.',
      },
    },
    done: 'Your message shows on the right of the conversation with a tick, and after you click “Resolve” the button reads “Reopen”.',
    tips: {
      t1: 'Use the “Private note” tab to leave a message for a teammate inside the chat — the customer never sees it.',
      t2: 'A tick that says “Sandbox — not sent” means this channel is not connected for real yet, so nothing left ChatBranch. Ask an admin to finish the channel set-up.',
      t3: 'If the customer replied STOP, the chat is marked “Opted out” and ChatBranch asks you to confirm before sending anything — only send if they asked you something.',
      t4: 'Writing a longer answer with a list or several paragraphs? Use Shift+Enter between lines, then press Enter once to send it all as one message.',
    },
    trouble:
      'The message box is greyed out and says “24h window closed”: on a WhatsApp Business number you must first send an approved template — see “Send a WhatsApp template when the 24-hour window is closed”. The paperclip is missing: this channel cannot receive files from ChatBranch (for example LINE, Viber or SMS) — send a text instead. A red “Failed” tick: the message did not arrive — ask an admin to check that channel on the Channels page.',
  },
  quoteAndReact: {
    title: 'Quote a message or react with an emoji',
    summary: 'On WhatsApp and Telegram, answer one particular message so the customer sees which one you mean, or react to it with an emoji instead of writing back.',
    before: 'Quotes and reactions work in WhatsApp and Telegram conversations. You need to be allowed to send messages in the chat.',
    steps: {
      s1: {
        title: 'Open the conversation',
        body: 'In the sidebar, under Work, click “Chats” and pick a WhatsApp or Telegram conversation.',
      },
      s2: {
        title: 'Show a message’s actions',
        body: 'On a computer, move the mouse over the message. On a phone or tablet, press and hold it for a moment. A small bar appears above it with six emoji, a “+” (“More emoji”), “Reply” and “Copy text”.',
      },
      s3: {
        title: 'React with an emoji',
        body: 'Click one of 👍 ❤️ 😂 😮 😢 🙏, or “+” to choose any other emoji. Your reaction appears in a small chip under the message and is sent to the customer. Click the same emoji again (or your chip) to take it back; picking another one replaces it.',
      },
      s4: {
        title: 'Reply to that message',
        body: 'Click “Reply”. A “Replying to …” strip with the message appears above the message box — click its × if you change your mind.',
      },
      s5: {
        title: 'Write and send',
        body: 'Type your answer and press Enter. Your message shows the quoted message on top, and the customer’s app shows it as a reply to that message.',
      },
      s6: {
        title: 'Find the original',
        body: 'When a customer quotes an earlier message, the quote shows at the top of their bubble. Click it to jump to the original message, which flashes for a moment.',
      },
    },
    done: 'Your reaction shows as a chip under the message, or your reply shows the quoted message on top.',
    tips: {
      t1: 'Hover over a reaction chip to see who reacted — the customer or which teammate.',
      t2: 'The business has one reaction per message: if a teammate already reacted, yours replaces theirs.',
      t3: 'Customers’ reactions never wake the AI or reopen a finished chat — they are just shown on the message.',
    },
    trouble:
      '“WhatsApp 24h window closed — a reaction can only be sent within 24 hours…”: on a WhatsApp Business number a reaction counts as a message, so it only works within 24 hours of the customer’s last message. “REACTION_INVALID” on Telegram: Telegram only allows its own set of reaction emoji — try 👍, ❤️, 🔥 or 🙏. No “Reply” on a message: notes, system lines and other channels (email, Messenger, SMS…) don’t support quotes.',
  },
  findChats: {
    title: 'Find the right chats quickly',
    summary: 'Search, use the ready-made tabs, filter by status, channel or person, and save the filters you use every day as your own tab.',
    steps: {
      s1: {
        title: 'Open the inbox',
        body: 'In the sidebar, under Work, click “Chats”.',
      },
      s2: {
        title: 'Search by name, number or words',
        body: 'Click the search box “Name, number or message” above the list and type, for example, “0123” or “refund”. The list shrinks to the matches; click the small × to clear it.',
      },
      s3: {
        title: 'Use the tabs',
        body: 'Under the search box are tabs: “All”, “Mine” (chats assigned to you), “Unassigned”, “Needs human”, then any views you saved. Click a tab to switch the list.',
      },
      s4: {
        title: 'Open the filter pop-up',
        body: 'Click “Filter” next to the search box. The “Filter conversations” window opens and counts the matching chats at the top as you change things.',
      },
      s5: {
        title: 'Choose status and channel',
        body: 'Under “Status”, pick for example “Open” and “Pending”. Under “Channel”, pick the apps you want, such as WhatsApp or Email.',
      },
      s6: {
        title: 'Choose who it belongs to',
        body: 'Under “Assignee”, pick “Me” from the “Quick picks” (or “Unassigned”, or a teammate). Under “Team”, pick a team if you use teams.',
      },
      s7: {
        title: 'Choose the state of the chat',
        body: 'Under “Conversation state”, tick “Waiting for our reply” to see only chats where the customer wrote last. You can also set “AI auto-reply” to On or Off, and under “Handed to a human” set “Handover” to “Needs a human”.',
      },
      s8: {
        title: 'Show the chats',
        body: 'Click the dark button at the bottom (“Show 12 chats”, for example). Each active filter appears as a small chip above the list — click its × to remove it, or “Clear all” to start over.',
      },
      s9: {
        title: 'Save it as your own tab',
        body: 'Open “Filter” again. Next to “Save as view”, type a name such as “My WhatsApp waiting” and click “Save”. A message says it is now a tab above the list.',
      },
      s10: {
        title: 'Check the “Needs human” tab',
        body: 'Click “Needs human” to see every chat the AI (or a rule) passed to a person. The amber number on the tab shows how many are waiting.',
      },
    },
    done: 'The list shows only the chats you wanted, and your saved view appears as a tab next to “Needs human”.',
    tips: {
      t1: 'Click the “…” button at the end of the tabs (“Manage saved views”) to rename, reorder or delete your views. Your views are private — nobody else sees them.',
      t2: 'If you change the filters while a saved view is open, a dot appears on its tab and an “Update view” button lets you save the change.',
      t3: 'In the filter pop-up, set “Sort” to “Oldest waiting” to answer the customers who have waited longest first.',
      t4: 'Press Ctrl+K (⌘K on a Mac) anywhere to search contacts, conversations and pages at once.',
    },
  },
  takeOverFromAi: {
    title: 'Take over a chat from the AI',
    summary: 'When the AI cannot help, it passes the chat to a person. Here is how to spot those chats, take them, answer yourself and hand back to the AI.',
    before: 'The AI must be answering customers already. You need permission to send messages in chats (agents and admins have it).',
    steps: {
      s1: {
        title: 'Spot the chats that need you',
        body: 'In “Chats”, a handed-over chat shows an amber chip in the list, such as “AI handed over”. The amber number on the “Needs human” tab counts them.',
      },
      s2: {
        title: 'Open the chat',
        body: 'Click the “Needs human” tab, then click the chat.',
      },
      s3: {
        title: 'Read the amber banner',
        body: 'Above the message box, an amber banner says “The AI handed this over”, with the reason (for example “The AI asked for a person”) and how long ago. While it is open, the AI usually stops answering this chat.',
      },
      s4: {
        title: 'Click “Take it”',
        body: 'Click “Take it” in the banner. The chat is assigned to you and the banner adds “it’s yours now; your reply closes it.”',
      },
      s5: {
        title: 'Answer the customer yourself',
        body: 'Type your answer in the message box and press Enter. Your reply closes the handover — the amber banner and chip disappear. The AI stays off for this chat until someone turns it back on.',
      },
      s6: {
        title: 'Or let the AI carry on',
        body: 'If no person is needed after all, click “Let AI continue”. This closes the handover and turns the chatbot back on for this chat.',
      },
      s7: {
        title: 'Turn the AI off or on for this chat only',
        body: 'At the top of the conversation is a small pill marked “AI” with a switch. Click it to turn the AI off (or on) for this one chat; other chats are not affected.',
      },
      s8: {
        title: 'If the AI switch is greyed out',
        body: 'A rule above the chat decides — for example the AI is switched off for the whole business, it is outside the AI’s hours, the contact is set to “Never”, or the customer opted out. Point at the switch to read why; an admin changes those rules on the AI monitor page or in “AI for this contact”.',
      },
    },
    done: 'The amber banner is gone, the chat has left the “Needs human” tab, and the AI switch shows the state you chose.',
    tips: {
      t1: 'If you see “AI reply waiting for review” instead, the AI wrote an answer that needs approval: click “Approve & send”, “Reject”, or “Edit in monitor” to change it first.',
      t2: 'Leave a “Private note” saying why you took over, so a teammate who opens the chat later understands.',
      t3: 'When you have finished, turn the AI back on with the header switch so it can handle the customer’s next questions.',
    },
    trouble:
      'There is no “Take it” button: the chat is already assigned to you (only “Let AI continue” shows), or your role cannot send messages — ask an admin. The AI keeps replying after you switched it off: make sure you switched the right chat, and check “AI for this contact” on the contact page is not set to “Always”.',
  },
  assignChat: {
    title: 'Assign a chat to a teammate or a team',
    summary: 'Give a conversation an owner so everyone knows who is answering it — or take the owner off again.',
    before: 'Your teammates need their own login (Admin → Users & roles). Teams are created by an admin under Admin → Teams. On a laptop or desktop the details panel sits on the right of the chat; on a phone or tablet it opens as a drawer from the “Conversation details” (ⓘ) button in the chat header.',
    steps: {
      s1: {
        title: 'Open the chat',
        body: 'In the sidebar, under Work, click “Chats”, then click the conversation.',
      },
      s2: {
        title: 'Find the “Conversation” card',
        body: 'Look at the details panel on the right and find the “Conversation” card. If the panel is hidden, click the round “i” button (“Conversation details” (ⓘ)) at the top right of the chat. On a phone or tablet, tap the “Conversation details” (ⓘ) button in the chat header — the same panel slides in as a drawer.',
      },
      s3: {
        title: 'Pick a teammate',
        body: 'Click the “Assignee” box and choose a name (their role is shown in brackets). It saves straight away, and the chat appears in that person’s “Mine” tab.',
      },
      s4: {
        title: 'Or pick a team',
        body: 'Click the “Team” box (it only shows when your business has teams) and choose a team. If nobody is assigned yet, ChatBranch hands the chat to the next member of that team in turn.',
      },
      s5: {
        title: 'Remove the owner',
        body: 'To unassign, set “Assignee” to “Unassigned”. To take the team off, set “Team” to “No team”.',
      },
      s6: {
        title: 'Check your own chats',
        body: 'Click the “Mine” tab above the list to see every chat assigned to you.',
      },
    },
    done: 'The “Assignee” box shows the teammate’s name, and the chat appears in their “Mine” tab.',
    tips: {
      t1: 'Round-robin in one line: each time a team gets a chat with no owner, the next person on the team’s list gets it, so work is shared evenly.',
      t2: 'To assign many chats at once, use Select mode — see “Act on many chats at once”.',
      t3: 'Use the “Unassigned” tab to find chats nobody has claimed yet.',
    },
    trouble:
      'You can’t see the “Assignee” box: open the details panel — click the round “i” button on a wide screen, or tap “Conversation details” (ⓘ) in the chat header on a phone or tablet. The “Team” box is missing: no teams exist yet — ask an admin to create one under Admin → Teams.',
  },
  quickReplies: {
    title: 'Use and create quick replies',
    summary: 'Quick replies are saved answers you insert by typing “/” in the message box. Save time on the questions you answer every day.',
    before: 'Anyone can use quick replies. Creating them needs the “Manage message templates” permission (admins have it by default).',
    steps: {
      s1: {
        title: 'Open a chat and click the message box',
        body: 'In “Chats”, open a conversation and click the box “Type a message (/ for quick replies)”.',
      },
      s2: {
        title: 'Type “/” and a few letters',
        body: 'Type a slash followed by the start of the short code, for example “/deliv”. A “Quick replies” list pops up above the box with up to six matches.',
      },
      s3: {
        title: 'Pick one and send',
        body: 'Click the reply you want — or move with the ↑ and ↓ keys and press Enter or Tab (Esc closes the list). Its text fills the box, with details like the customer’s name already filled in. If a {{placeholder}} is left (a chat with no linked contact), a warning shows under the box: replace it, then press Enter.',
      },
      s4: {
        title: 'Or use the shortcut buttons',
        body: 'When the box is empty, up to eight quick replies show as small round buttons just above it. One click puts that reply in the box.',
      },
      s5: {
        title: 'Go to Templates → Messages',
        body: 'To make a new one, in the sidebar under Library click “Templates”, then the “Messages” tab.',
      },
      s6: {
        title: 'Click “New quick reply”',
        body: 'Click “New quick reply” at the top right of the list. A form opens.',
      },
      s7: {
        title: 'Fill in the form',
        body: '“Short code”: what agents will type after “/”, for example “delivery-update” (lowercase letters, digits and dashes). Leave “Available to” on “Everyone (global)” unless the reply only fits some companies or branches — then choose “Selected entities” and tick them (each with its branches). Under “Advanced settings”, leave “Channels” on “All channels” unless it only fits one app. Write the text under “Message” — the variable buttons insert things like the customer’s name.',
      },
      s8: {
        title: 'Save it',
        body: 'Click “Save quick reply”. A message confirms it was saved and tells you to type it after “/” in the message box.',
      },
    },
    done: 'Typing “/” and your short code in any chat shows your new reply in the “Quick replies” list.',
    tips: {
      t1: 'Keep short codes short and easy to guess, such as “hours”, “price-list” or “thanks”.',
      t2: 'Limit a reply to the channels it suits — for example a long answer with a signature only for Email.',
      t3: 'Edit or delete a reply with the pencil or bin icon in the Messages list; the change reaches everyone straight away.',
    },
  },
  sendTemplate: {
    title: 'Send a WhatsApp template when the 24-hour window is closed',
    summary: 'WhatsApp only lets businesses send free text within 24 hours of the customer’s last message. After that, you send an approved template to restart the conversation.',
    before: 'You need a WhatsApp Business number connected through Meta, and at least one approved template. Admins create templates in Library → Templates → “WhatsApp (Meta-approved)”; Meta reviews each one before it can be used.',
    steps: {
      s1: {
        title: 'Understand the 24-hour rule',
        body: 'For 24 hours after the customer’s last message you can write anything. After that, WhatsApp only allows pre-approved messages called templates. As soon as the customer answers, you get another 24 hours.',
      },
      s2: {
        title: 'Open the WhatsApp chat',
        body: 'In “Chats”, open the conversation. If the window has closed, the message box is greyed out and says “24h window closed — send a template to re-open”.',
      },
      s3: {
        title: 'Click the template button',
        body: 'Click the first icon on the left of the message box, “Send a message template”. When the window is closed it is shown dark so it is easy to find.',
      },
      s4: {
        title: 'Choose a template',
        body: 'The “Send a template” window lists your templates with their language and type. Click one marked “Approved”.',
      },
      s5: {
        title: 'Fill in the blanks',
        body: 'Each blank in the template has its own box. Boxes marked “Auto-filled” come from the contact’s record; type the others, for example a date like “12 Oct, 3pm”.',
      },
      s6: {
        title: 'Check the preview',
        body: 'Read the “Preview” at the bottom — it is exactly what the customer will receive.',
      },
      s7: {
        title: 'Send it',
        body: 'Click “Send template”. The message appears in the conversation.',
      },
      s8: {
        title: 'Wait for the customer to answer',
        body: 'When the customer replies, the message box unlocks and you can write freely again for 24 hours.',
      },
    },
    done: 'The template appears in the chat, and once the customer answers the message box is no longer greyed out.',
    tips: {
      t1: 'The 24 hours count from the customer’s last message, not yours — sending more messages does not extend it.',
      t2: 'Numbers paired with a QR code (WhatsApp Web) don’t have this rule; a template sent there simply goes out as normal text.',
      t3: 'Ask an admin to prepare templates for your usual situations (appointment reminder, order update, “are you still interested?”) in advance — Meta’s review can take a while.',
    },
    trouble:
      '“No templates on this account”: an admin needs to create one in Templates → “WhatsApp (Meta-approved)”, and live ones must be approved by Meta first. The template button is missing: it only appears in WhatsApp chats. Sending is refused: templates go out from a WhatsApp Business number — ask an admin to check that one is connected and healthy on the Channels page.',
  },
  addContact: {
    title: 'Add a contact by hand',
    summary: 'Save a customer’s name, phone, email, branch and tags so their chats link up and the AI knows who it is talking to.',
    before: 'You need the “Create & edit contacts” permission (admins have it by default; agents can view contacts only).',
    steps: {
      s1: {
        title: 'Open Contacts',
        body: 'In the sidebar, under Work, click “Contacts”.',
      },
      s2: {
        title: 'Click “Add contact”',
        body: 'Click the dark “Add contact” button at the top right. The “New contact” window opens.',
      },
      s3: {
        title: 'Type the name',
        body: 'In “Name”, type the person’s full name, for example “Ahmad Fauzi”. The name is required.',
      },
      s4: {
        title: 'Enter the phone number',
        body: 'In “Phone”, first pick the country code on the left (for example +60 for Malaysia), then type the rest of the number without the first 0 — 012-345 6789 becomes 12-345 6789. The phone is required.',
      },
      s5: {
        title: 'Choose the language',
        body: 'Under “Language”, choose the language the customer prefers. The AI and voice calls use it.',
      },
      s6: {
        title: 'Add email and company (optional)',
        body: 'Fill in “Email (optional)” and “Company (optional)” if you have them. An email lets their email conversations link up automatically.',
      },
      s7: {
        title: 'Add tags (optional)',
        body: 'In “Tags (comma-separated)”, type labels separated by commas, for example “vip, wholesale”. New tag names are created for you.',
      },
      s8: {
        title: 'Tick the entities',
        body: 'If your business has branches, tick under “Entities” the ones this contact belongs to (or click “All”). Fill in any “Custom fields” your business added — they are optional.',
      },
      s9: {
        title: 'Save',
        body: 'Click “Add contact” at the bottom. The window closes and the person appears in the contacts list.',
      },
    },
    done: 'The new contact appears in the Contacts list; click “View” on its row to open the contact page.',
    tips: {
      t1: 'Search first with “Search name, phone, email or tag…” to make sure the person isn’t already saved — it avoids duplicates.',
      t2: 'With the country code included, WhatsApp and SMS chats from this number link to the contact by themselves.',
      t3: 'To change details later, click the pencil icon in the “Actions” column of the list.',
    },
  },
  requestContactAccess: {
    title: 'Ask for a customer who belongs to another branch',
    summary: 'When a phone or email is already a customer of another branch of your company, ask an admin to share them with your branch instead of adding a duplicate.',
    before: 'You add contacts from your branch (the “Create & edit contacts” permission). Approving needs an admin who manages both branches.',
    steps: {
      s1: {
        title: 'Add the contact as usual',
        body: 'In “Contacts”, click “Add contact”, fill in the name and the phone or email, and click “Add contact”.',
      },
      s2: {
        title: 'Read the message',
        body: 'If the number belongs to a customer of another branch you can’t see, the window says “This customer already exists in another branch of …”. Their name and details stay hidden.',
      },
      s3: {
        title: 'Send the request',
        body: 'Type an optional note for the admin (for example why the customer is now with you) and click “Request access”.',
      },
      s4: {
        title: 'Wait for the admin',
        body: 'An admin of your company gets a notification. Your request shows under “Access requests” at the top of Contacts, where you can also cancel it.',
      },
      s5: {
        title: 'Admins: review',
        body: 'Admins click the notification or “Access requests” on Contacts. Each request shows who asked, for which branch, the customer’s name, the last 4 digits of the phone, the branches they are in now and the note.',
      },
      s6: {
        title: 'Admins: approve or deny',
        body: 'Click “Approve” to add the customer to the requested branch (they stay in their other branches), or “Deny” and give an optional reason. The person who asked is notified either way.',
      },
    },
    done: 'Once approved, the customer appears in your Contacts list with all their history.',
    tips: {
      t1: 'Asking twice for the same customer and branch just shows the request you already sent.',
      t2: 'A customer who writes in on your branch’s number links to the right contact by themselves — no request needed.',
    },
    trouble: 'If “Request access” says the link expired, click “Add contact” again: the request link lasts 30 minutes.',
  },
  importContacts: {
    title: 'Import contacts from a file or Google',
    summary: 'Bring in a contact book you already have — up to 20,000 people at once — in four short steps.',
    before:
      'You need the “Create & edit contacts” permission (admins). Have a file ready: Excel (.xlsx), CSV, or a vCard (.vcf) exported from an iPhone or Android phone, up to 10 MB. To import from Google instead, first connect Google Contacts under Settings → My connections.',
    steps: {
      s1: {
        title: 'Open the import wizard',
        body: 'In “Contacts”, click “Import” at the top right. The “Import contacts” window opens with four steps along the top: “Source”, “Map columns”, “Duplicates” and “Import”.',
      },
      s2: {
        title: 'Choose where the contacts come from',
        body: 'Click “Upload a file” and choose your file — or click “Google Contacts” to read your own Google address book. ChatBranch reads the file and moves to the next step.',
      },
      s3: {
        title: 'Check what each column holds',
        body: 'Each column of your file is listed with a few example values and a drop-down saying what it holds — ChatBranch guessed from the headings. Correct any wrong guess, and choose “Don’t import” for columns you don’t need.',
      },
      s4: {
        title: 'Make sure there is a phone or email',
        body: 'At least one column must be a phone or an email — a contact needs a way to be reached. Then click “Next”.',
      },
      s5: {
        title: 'Decide what happens to duplicates',
        body: 'You see how many are “New”, “Already here”, “Merged in file” and “Can’t import”. Under “When a contact already exists”, choose “Skip them”, “Fill empty fields” or “Overwrite”.',
      },
      s6: {
        title: 'Choose a branch (optional)',
        body: 'Under “Add every contact to”, pick the entity (branch) they belong to, or leave “No entity”.',
      },
      s7: {
        title: 'Tag them and find their channels (optional)',
        body: 'In “Tag every contact”, type a tag such as “trade-fair-2026”. Tick “Find their channels afterwards” to register WhatsApp, voice and email for each one.',
      },
      s8: {
        title: 'Import',
        body: 'Click “Import … contacts” (the button shows the number). When it finishes you see “Import complete” with how many were created, updated and skipped.',
      },
      s9: {
        title: 'Download the rows that were left out',
        body: 'If some rows couldn’t be imported, click “Errors (.csv)” (and “Warnings (.csv)”) to download them with the row number and the reason. Fix them in your spreadsheet and import that file again. Click “Close” when done.',
      },
    },
    done: '“Import complete” shows how many contacts were created, and the new people appear in the Contacts list.',
    tips: {
      t1: 'Try a small file of five rows first to check the columns come out right.',
      t2: 'Phone numbers without a country code get your organization’s default code (usually +60).',
      t3: 'Give every import its own tag — you can then find that batch later with the tag filter on the Contacts page.',
    },
    trouble:
      'It says to connect Google first: open Settings → My connections, connect Google Contacts, then come back and click “Google Contacts” again. “Next” stays greyed out: no column is set to a phone or an email — map one. The file is larger than 10 MB: split it into smaller files and import them one by one.',
  },
  linkUnknownChat: {
    title: 'Link a chat from an unknown number to a contact',
    summary: 'When someone you don’t have on file writes in, connect the chat to an existing contact or create a new one, so the AI and your team know who it is.',
    before: 'Linking to an existing contact: anyone who can send messages. Creating a new contact needs the “Create & edit contacts” permission (admins).',
    steps: {
      s1: {
        title: 'Spot an unknown chat',
        body: 'Open the chat in “Chats”. An unknown customer shows a number or app name instead of a person’s name, and the “Contact” card on the right shows a “Link to a contact…” box. On a phone or tablet, tap “Conversation details” (ⓘ) in the chat header to open that panel.',
      },
      s2: {
        title: 'Read the strip under the header',
        body: 'If your inbox asks unknown customers who they are, a strip under the header shows how it went, for example “Unknown customer — no matching contact”, with the name, phone or email they gave.',
      },
      s3: {
        title: 'Search for the existing contact',
        body: 'In the “Contact” card on the right, click “Link to a contact…”, type part of their name or number and pick the right person.',
      },
      s4: {
        title: 'Link it',
        body: 'Click the small chain button, “Link contact”, next to the box. The chat now joins that person’s other conversations.',
      },
      s5: {
        title: 'Or create a new contact',
        body: 'If they are new, click “Create contact” in the strip (or “Create from this chat” under the link box). The “Create contact from this chat” window opens, already filled with what the customer said.',
      },
      s6: {
        title: 'Check the details and save',
        body: 'Check “Name” (required), “Phone” and “Email”; leave “Entity” as “The inbox’s entity” unless you know better. Click “Create & link”.',
      },
      s7: {
        title: 'Confirm it worked',
        body: 'The “Contact” card now shows the person’s name with an “Unlink” button, and the unknown-customer strip is gone.',
      },
    },
    done: 'The “Contact” card shows the person’s name with an “Unlink” button.',
    tips: {
      t1: 'WhatsApp and SMS chats link by themselves when the phone number matches; email chats link by address. Messenger, Instagram, Telegram and website chats usually need linking by hand.',
      t2: 'Linked the wrong person? Click “Unlink” and link the right one — nothing is deleted.',
      t3: 'On the contact page, “Invite to another app” sends a link so their next Messenger or Telegram chat links itself.',
    },
  },
  contactProfile: {
    title: 'Read a contact’s page',
    summary: 'Everything about one customer in one place: an AI summary, their details, channels, tags, branches, bookings, how the AI treats them and every past conversation.',
    steps: {
      s1: {
        title: 'Open the contact',
        body: 'In the sidebar, under Work, click “Contacts”. Search for the person, then click “View” on their row.',
      },
      s2: {
        title: 'Read the header',
        body: 'The top shows the name, phone, email, company, status and tags. “Open in Chats” jumps straight to their conversations.',
      },
      s3: {
        title: 'Get the AI summary',
        body: 'Under “AI summary”, click “Generate summary” (or “Regenerate” for a fresh one). It condenses every call, chat and email into a short brief. “basic (no AI key)” means a simpler summary because no AI service is set up.',
      },
      s4: {
        title: 'Check details and custom fields',
        body: 'On the right, “Details” shows the contact ID and language (you can change the language here). “Custom fields” lists your business’s extra fields, and “Records” lists linked orders, bookings or tickets.',
      },
      s5: {
        title: 'See how to reach them',
        body: 'The “Channels” card lists every way to reach the person — WhatsApp, email, voice and more — with message counts, “On WhatsApp” or “Unverified”, and whether they opted out. Use “Find channels”, “Add channel” or “Invite to another app” to add more.',
      },
      s6: {
        title: 'Decide how the AI treats them',
        body: 'In “AI for this contact”, choose “Follow chat” (the usual), “Never” (the AI never answers this person — good for VIPs or complaints) or “Always”.',
      },
      s7: {
        title: 'Set their tags',
        body: 'In the “Tags” card, add or remove tags, then click “Save tags”. “Manage tags” opens the Tags page to create new ones.',
      },
      s8: {
        title: 'Check their entities',
        body: 'The “Entities” card shows which branches the contact belongs to. Click “Edit” to change them.',
      },
      s9: {
        title: 'See their bookings',
        body: 'The bookings card lists upcoming appointments. “All bookings” opens the Bookings page for this contact.',
      },
      s10: {
        title: 'Read the full history',
        body: 'The “Communications” timeline shows every call, chat and email, newest first. Click “Load full log” to see older ones.',
      },
    },
    done: 'From this one page you can tell who the customer is, what happened last time and how the AI should treat them.',
    tips: {
      t1: 'Generate the AI summary before calling a customer — it takes seconds and saves reading the whole history.',
      t2: 'To change the name or phone, go back to the Contacts list and click the pencil icon on their row.',
      t3: 'Setting “Never” on an unhappy customer makes sure a person always answers them.',
    },
  },
  voiceCall: {
    title: 'Place an AI voice call',
    summary: 'Let the AI phone a customer from their chat, follow the call live, steer it or put words in its mouth, and hang up when you want.',
    before:
      'Phone calls need a one-time voice set-up (a LiveKit server and a SIP phone line), done by whoever runs your ChatBranch server — usually the owner (superadmin). The chat must be linked to a contact whose phone number includes the country code.',
    steps: {
      s1: {
        title: 'Open the customer’s chat',
        body: 'In “Chats”, open a conversation that is linked to a contact (from a contact page, “Open in Chats” takes you there).',
      },
      s2: {
        title: 'Click the phone icon',
        body: 'At the top right of the conversation, click the phone icon, “AI phone call (SIP)”. It calls the contact’s real phone number. (The WhatsApp logo next to it starts a WhatsApp call instead, on a QR-paired number.)',
      },
      s3: {
        title: 'Watch it connect',
        body: 'A strip at the top of Chats shows the call: “Dialling…”, then “Ringing — answer the phone”, then live. A “Live transcript — AI phone call” panel above the messages shows what the AI and the caller say.',
      },
      s4: {
        title: 'Guide the AI',
        body: 'Type an instruction in the panel’s box, for example “Offer them the 3pm slot tomorrow”, and click “Guide”. The AI works it into what it says next.',
      },
      s5: {
        title: 'Say your exact words',
        body: 'To have your words spoken exactly as typed, type them and click “Takeover”. The AI voice reads them out word for word.',
      },
      s6: {
        title: 'Escalate if needed',
        body: 'Click “Escalate” and the AI politely tells the caller a person will follow up, then wraps up the call.',
      },
      s7: {
        title: 'Hang up',
        body: 'Click the red “End” button in the panel (or the red phone icon in the header). The call ends and is recorded as ended by an operator.',
      },
      s8: {
        title: 'Review the call later',
        body: 'The call appears in the chat as an “AI voice call” line you can open to read the transcript. In the sidebar, Review → “Call logs” lists every call with its transcript and AI summary.',
      },
    },
    done: 'The live panel shows the conversation as it happens, and after the call it appears in the chat and in Call logs.',
    tips: {
      t1: 'Set the contact’s language on their contact page — the AI opens and holds the call in that language.',
      t2: 'Keep Guide instructions short and specific; the AI follows one clear sentence best.',
      t3: 'Several calls can run at once (usually up to three); each has its own panel in its own chat.',
    },
    trouble:
      'The call strip shows a red ❌ with a message naming settings to set (LIVEKIT or a trunk): voice calls are not set up on this server — ask the owner (superadmin) or whoever runs the server. There is no phone icon: the chat isn’t linked to a contact — link it first (see “Link a chat from an unknown number to a contact”). The call never rings: check the contact’s phone number has the right country code.',
  },
  bookingsDaily: {
    title: 'Check and cancel bookings',
    summary: 'See every appointment the AI and your team booked, find one quickly, and cancel it when a customer can’t make it.',
    before: 'Bookings appear once an admin gives the AI a calendar (a Connected app in Library → AI skills). You need the “View chats” permission to see them.',
    steps: {
      s1: {
        title: 'Open Bookings',
        body: 'In the sidebar, under Work, click “Bookings”.',
      },
      s2: {
        title: 'Choose Upcoming or Past',
        body: 'The “Upcoming” tab is open by default. Click “Past” to see appointments that have already happened.',
      },
      s3: {
        title: 'Read the table',
        body: '“When” shows the date, time and time zone; then “Contact”, “Calendar”, “Status” and “Booked by”. “Booked” means it is on the calendar; “Reserved” means it is held for an AI reply that hasn’t been sent yet.',
      },
      s4: {
        title: 'Find a booking',
        body: 'Type a name or title in “Search contact or title…”. Use the status box (“Booked or held” or “All statuses”) and “All calendars” to narrow the list.',
      },
      s5: {
        title: 'Open the chat or the event',
        body: 'In “Actions”, the chat icon opens the conversation with that customer; the calendar icon opens the Google Calendar event (for Google calendars).',
      },
      s6: {
        title: 'Click “Cancel”',
        body: 'On a booked, upcoming row, click “Cancel” in the “Actions” column. A window asks “Cancel this booking?” and explains what will happen.',
      },
      s7: {
        title: 'Confirm, then tell the customer',
        body: 'Click “Cancel booking” (or “Keep it” to stop). The time is freed and a note is added to the conversation — but the customer is not told automatically, so send them a message in the chat.',
      },
    },
    done: 'The booking shows “Cancelled” and a note about it appears in the customer’s conversation.',
    tips: {
      t1: 'Opened Bookings from a contact? Only that person’s bookings show — click × on the contact chip (“Show every contact’s bookings”) to see everyone again.',
      t2: '“Reserved” turns into “Booked” when the AI’s reply is actually sent, for example after someone approves it on the AI monitor.',
      t3: 'Always message the customer after cancelling — ChatBranch does not do it for you.',
    },
  },
  bulkActions: {
    title: 'Act on many chats at once',
    summary: 'Pick several conversations and turn the AI on or off, resolve them or assign them in one go.',
    before: 'You need the “Manage chats” permission (admins by default). Without it, the “Select” button does not appear.',
    steps: {
      s1: {
        title: 'Narrow the list first',
        body: 'In “Chats”, use a tab, the search box or “Filter” so the list shows the chats you want to change, for example all “Waiting for our reply” chats.',
      },
      s2: {
        title: 'Click “Select”',
        body: 'Click “Select” next to the Chats title. Each row gets a tick box, and the button changes to “Done”.',
      },
      s3: {
        title: 'Tick the chats',
        body: 'Click the rows you want. A bar appears at the bottom showing how many are selected.',
      },
      s4: {
        title: 'Or select the whole filter',
        body: 'Click “Select all … in this filter” in the bottom bar to pick every matching chat (up to 1000). “Only the ones I picked” switches back.',
      },
      s5: {
        title: 'Turn the AI on or off, or resolve',
        body: 'Click “AI on”, “AI off” or “Resolve” in the bottom bar. It applies to every selected chat.',
      },
      s6: {
        title: 'Or assign them',
        body: 'Open the “Assign to…” box, pick a teammate (or “Nobody (unassign)”) and click “Assign”.',
      },
      s7: {
        title: 'Read the result and finish',
        body: 'A line confirms what happened, for example “Resolved · 12 conversations”, and says if any were skipped. Click “Done” at the top to leave Select mode.',
      },
    },
    done: 'The confirmation line shows the number of conversations changed, and the list updates.',
    tips: {
      t1: 'Filter first, then “Select all” — for example every open chat on one WhatsApp number — instead of ticking rows one by one.',
      t2: 'Click the × in the bottom bar (“Clear selection”) to start your selection again.',
      t3: 'Every bulk change is recorded once in the Activity log, so admins can see who did what.',
    },
  },
}
