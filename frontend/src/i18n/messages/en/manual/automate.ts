/** How-to guides — automate group. Keys: see lib/guides/types.ts. */
export const automate = {
  journeyFromTemplate: {
    title: 'Create your first journey from a template',
    summary:
      'A journey sends the right messages at the right time by itself — a welcome, a reminder, a follow-up. Starting from a ready-made template takes about five minutes.',
    before:
      'You need an admin login (journeys need the “manage flows” permission) and at least one connected channel, such as a WhatsApp number or an email mailbox, so the messages have somewhere to go.',
    steps: {
      s1: {
        title: 'Open Journeys',
        body: 'In the sidebar, open Automate → Journeys. If you have no journeys yet, you see “Let follow-ups run themselves” with the template cards underneath.',
      },
      s2: {
        title: 'Open the template gallery',
        body: 'If you already have journeys, click “New journey” at the top right. A window called “Start a new journey” opens with the same cards.',
      },
      s3: {
        title: 'Pick a template',
        body: 'Each card says what it does and shows its steps in one line. For a first try, click “Welcome series” — it greets every new contact and sends a getting-started email two days later.',
      },
      s4: {
        title: 'Read the steps in plain words',
        body: 'The journey opens in the “Steps” view. Each card is one step, read from top to bottom: the orange card at the top says when it starts (“When…”), the cards below say what happens next.',
      },
      s5: {
        title: 'Change the words to sound like you',
        body: 'Click a message card. A “Step settings” panel opens on the right; edit the text in “Message”. “Changes save automatically.” — there is no save button.',
      },
      s6: {
        title: 'Fix anything marked in amber',
        body: 'An amber line under the header (for example “2 things to finish before this runs properly”) and an amber note on a card show what is missing, such as a template or a tag. Click that card and fill in the missing field.',
      },
      s7: {
        title: 'Rename the journey (optional)',
        body: 'Click the pencil next to the journey name at the top, type a name such as “Welcome — new customers” and click “Save”.',
      },
      s8: {
        title: 'Go live',
        body: 'Every template starts paused, so nothing is sent while you edit. When you are happy, click “Go live” at the top right. If steps are still unfinished you are asked “Go live with unfinished steps?” — choose “Finish first” to fix them.',
      },
      s9: {
        title: 'Check it is running',
        body: 'The button turns green and reads “Live” with a pulsing dot, and the journey shows as “Live” in the list of journeys. Click “Live” again at any time to pause it.',
      },
    },
    done: 'The green “Live” button is on, and after the first contact enters, the step cards start showing “1 reached”.',
    tips: {
      t1: '“Contact created” journeys only greet contacts created after you go live, so turning one on never messages your whole contact book.',
      t2: 'Use “More actions” (the … button) → “Duplicate” to try a change on a copy while the original keeps running.',
      t3: 'Next, read “Add, move and delete steps” to change a template to fit your business.',
    },
    trouble:
      'Nothing is sent: check the journey is “Live”, that the channel in the message step is connected under Channels, and that the WhatsApp message type suits the situation (free text only reaches people who wrote to you in the last 24 hours — use a template otherwise). Can’t see Journeys in the sidebar: your role does not have the journeys permission; ask an admin.',
  },
  journeyEdit: {
    title: 'Add, move and delete steps in a journey',
    summary:
      'Shape a journey to fit your business: send a message, wait, follow up when nobody answers, let the AI write, update the contact, hand over to your team and finish — all from the simple Steps view.',
    steps: {
      s1: {
        title: 'Open the journey',
        body: 'In the sidebar, open Automate → Journeys. The list of all journeys opens; click “Open” next to the journey (or click its row). Make sure “Steps” (not “Advanced canvas”) is selected.',
      },
      s2: {
        title: 'Click a + to add a step',
        body: 'Between the cards there are small + buttons (“Add a step here”). Click the one where the new step should go. A window “Add a step” opens — the step goes exactly where you clicked.',
      },
      s3: {
        title: 'Send a message',
        body: 'Choose “Send message”. In the panel on the right, pick the channels under “Send on” (for example WhatsApp), then type your text in “Message”. Use “Insert a variable” to drop in the customer’s first name.',
      },
      s4: {
        title: 'Choose the WhatsApp message type',
        body: 'For WhatsApp, “WhatsApp message type” offers “Free text” (only within 24 hours of their last message), “Template” (works any time) or “Text, else template”. For scheduled or reminder messages, pick a template.',
      },
      s5: {
        title: 'Wait',
        body: 'Add a “Wait” step. Under “Wait for”, choose “A set time” (for example 2 days) or “A reply”; with “A reply” you can set “Give up after” so the journey does not wait forever.',
      },
      s6: {
        title: 'Follow up if no reply',
        body: 'Add “Follow up if no reply”. Set “Wait before each follow-up” and “Follow up at most” (up to 5 times), then write the “Follow-up message” or choose “AI writes it”. The step splits into two paths: “Replied” and “No reply”.',
      },
      s7: {
        title: 'Let the AI write a message',
        body: 'Add “AI message”. Optionally pick a “Skill”, then describe the goal in “What should the message do?” — for example “Thank them for signing up and ask what brought them to us.” The AI writes the words in the contact’s language.',
      },
      s8: {
        title: 'Update the contact or hand over',
        body: 'Add “Update contact” and pick “Add tag”, “Remove tag” or “Set field” (for example add the tag “qualified”). Or add “Hand over”, choose the team under “Hand over to” and write “Why — a note for the team”.',
      },
      s9: {
        title: 'Finish with End',
        body: 'At the bottom of a path (where it says “Path ends”), click + and choose “End”. Type a short “Outcome” code such as BOOKED or NO_REPLY so you can see how each run finished.',
      },
      s10: {
        title: 'Move or delete a step',
        body: 'Hover over a card to show its small buttons at the top right: the arrows (“Move up” / “Move down”) and the bin (“Delete step”). Deleting a step with two paths asks first and keeps the YES (or “Replied”) path.',
      },
      s11: {
        title: 'Use the Advanced canvas (optional)',
        body: 'Click “Advanced canvas” to see the same journey as boxes and lines. You can drag in steps from “Drag in a step”, connect boxes by dragging between their dots, and tidy up with “Auto-layout”. Switch back to “Steps” at any time.',
      },
    },
    done: 'The new cards appear in the right order in the Steps view, and no card shows an amber “needs” note.',
    tips: {
      t1: 'Every change saves by itself — the header shows whether it is saved to the server or only in this browser.',
      t2: 'Turn on “Send like a person” on a message step for a short typing pause and long text split into a few messages.',
      t3: 'Pause the journey (click “Live”) before big changes, so nobody gets a half-finished version.',
      t4: 'A “Set chatbot on/off” step turns the AI on or off for that contact’s chats — handy after a hand-over.',
    },
    trouble:
      '“End” is greyed out: End can only go where a path already stops — click the + under “Path ends”. Some cards sit under “Not connected”: no path reaches them, so they never run; delete them or connect them in the Advanced canvas. Changes disappeared: if the header says “Offline — saved in this browser”, the server could not be reached; reload once the connection is back.',
  },
  journeyTriggers: {
    title: 'Choose when a journey starts',
    summary:
      'The first card of every journey (“When…”) decides who enters and when: a new contact, a tag, an incoming message, a date, a schedule, or you starting it by hand.',
    steps: {
      s1: {
        title: 'Open the starting card',
        body: 'In Automate → Journeys, open your journey and click the top card (the orange one). The “Step settings” panel opens on the right.',
      },
      s2: {
        title: 'Pick what starts it',
        body: 'Click the list under “Start this journey when”. The choices are “A contact is created”, “A contact is tagged”, “A message arrives”, “A date comes round”, “On a schedule” and “Started by hand or by the API”.',
      },
      s3: {
        title: 'A contact is created',
        body: 'Choose this for welcome messages. Only contacts created after the journey goes live enter, from any source — typed in, imported or a new chat.',
      },
      s4: {
        title: 'A contact is tagged',
        body: 'Type the tag under “Tag”, for example “New customer”. Each contact enters as soon as the tag is on them.',
      },
      s5: {
        title: 'A message arrives',
        body: 'Pick the channel under “On” (or “Any channel”). To react only to certain words, fill in “Only when it contains (optional)”, for example price, quote, booking — any one of the words matches.',
      },
      s6: {
        title: 'A date comes round',
        body: 'Pick a date custom field under “Date field” (for example a birthday or renewal date), then the number of days and “days before the date”, “on the date” or “days after the date”. Tick “Every year” for birthdays and anniversaries.',
      },
      s7: {
        title: 'On a schedule',
        body: 'Set the “Time” (your organization’s time zone) and, under “On these days”, the weekdays — leave it empty for every day. A schedule with “Everyone” runs for every contact, so narrow it in the next step.',
      },
      s8: {
        title: 'Decide who can enter',
        body: 'Under “Who can enter”, choose “Everyone”, “Tagged” (type a tag, only those contacts enter) or “Chosen” (search and pick contacts by name or phone).',
      },
      s9: {
        title: 'Set the repeat rule',
        body: 'Where shown, “If it happens again” decides whether a contact can enter more than once: “Once per contact”, “Every time it happens” or “At most once a day”.',
      },
      s10: {
        title: 'Started by hand or by the API',
        body: 'Choose this to start the journey yourself. Make sure the journey is live, then under “Start it now for…” search and pick contacts and click “Start”. Your developers can start it from other systems through the API address shown there.',
      },
    },
    done: 'The top card now describes the start in plain words, for example “A message arrives on WhatsApp containing “price””, with no amber note.',
    tips: {
      t1: '“A message arrives” starts every time by default — choose “At most once a day” to avoid answering the same person twice in a busy chat.',
      t2: 'No date fields to pick? Add one first under Settings → Custom fields (type Date).',
      t3: 'Want a second way in? Add another “When…” card in the Advanced canvas — each one is a separate starting point.',
    },
    trouble:
      'Contacts who already carry the tag also enter when a “A contact is tagged” journey goes live — use “Chosen” or a fresh tag if you only want new ones. A schedule sent to everyone: switch “Who can enter” from “Everyone” to “Tagged” or “Chosen”. “Start” gives an error: the journey must be saved and live first.',
  },
  journeyDecisions: {
    title: 'Branch a journey: check conditions or let the AI decide',
    summary:
      'Send different people down different paths — for example, customers who said yes get a thank-you and the rest reach your team. You can set the rules yourself, or let the AI read the chat and decide.',
    steps: {
      s1: {
        title: 'Add a decision step',
        body: 'In the Steps view, click + where the split should happen. Choose “Check conditions” to set rules yourself, or “Let AI decide” to let the AI read the conversation.',
      },
      s2: {
        title: 'Choose how to decide',
        body: 'In the panel on the right, “How to decide” has three choices: “Check conditions” (rules you set), “AI yes / no” (a question, YES / NO) and “AI picks a path” (one of your paths). You can switch at any time.',
      },
      s3: {
        title: 'Check conditions: add a rule',
        body: 'Click “Add a condition” and pick what to check — a tag, a custom field, the contact, the conversation, their reply or bookings. For example “Their reply” → “They said yes”, or “Tag” → “Has tag” → vip.',
      },
      s4: {
        title: 'All or any',
        body: 'With more than one condition, “Go YES when” decides: “All of these” (every condition must hold) or “Any of these” (one is enough). Everyone else goes NO. Conditions are checked at once — nothing waits.',
      },
      s5: {
        title: 'AI yes / no: write the question',
        body: 'Under “Question for the AI”, ask in plain words, for example “Is the customer ready to book a viewing?”. The AI answers YES or NO from the conversation, or says it is unsure.',
      },
      s6: {
        title: 'AI picks a path: name the paths',
        body: 'Under “Paths”, give two to six names, such as “Wants to buy”, “Has a question”, “Has a complaint”, each with a short description of when to pick it. Click “Add a path” for more, and optionally “+ “None of these”” for when nothing fits.',
      },
      s7: {
        title: 'Choose what the AI reads (optional)',
        body: 'Under “What the AI reads”, set how many “Last messages” it looks at (5 to 100) and from where. Tick “Only since they entered this journey” to ignore older chats. Private notes are never included.',
      },
      s8: {
        title: 'Decide what happens when the AI is unsure',
        body: 'Under “When the AI is unsure”, pick “Go NO”, “Go YES”, “Take “None of these””, “Wait for their next reply and ask again” or “End the journey”. The same choice is used if the AI is switched off or cannot be reached.',
      },
      s9: {
        title: 'Build each path',
        body: 'The Steps view now shows the paths side by side (YES and NO, or one column per AI path). Click the + under each path and add its steps. A path with no steps simply ends the journey.',
      },
      s10: {
        title: 'Check the decisions later',
        body: 'Once the journey is live, open “Runs” → “Each contact” and click a person: the decision shows what was decided, for example “AI decided YES (0.82)”.',
      },
    },
    done: 'The decision card reads in plain words (for example “AI asks: Is the customer ready to book a viewing?”) and each path below it has its own steps.',
    tips: {
      t1: 'The AI only decides — it never sends anything to the customer in this step.',
      t2: 'Short, clear path descriptions (“asks about price, availability or ordering”) make the AI choose more reliably.',
      t3: 'Raise “Minimum confidence” if the AI guesses too often; less confident answers then count as unsure.',
    },
    trouble:
      'The AI seems to guess with simple keywords: no AI model is set up, so it makes a “sandbox guess” (marked in the Runs panel) — add AI keys under Settings → AI keys & models. Everyone goes NO: check “All of these” vs “Any of these”, and that every condition has a value (amber notes show the missing ones).',
  },
  journeyRuns: {
    title: 'See who went through a journey and where they stopped',
    summary:
      'The Runs panel answers “Is it working?”: how many people entered, how far they got, what failed, and the exact path of any one contact. You can also start the journey for chosen contacts.',
    steps: {
      s1: {
        title: 'Open the Runs panel',
        body: 'In Automate → Journeys, open the journey and click “Runs” at the top (the number next to it is how many runs started). The panel “Is it working?” opens on the right.',
      },
      s2: {
        title: 'Choose the period',
        body: 'Use the “7 days” / “30 days” switch in the header to choose how far back the numbers go.',
      },
      s3: {
        title: 'Read the totals',
        body: 'On the “Overview” tab, the top shows how many “Started”, how many are “Waiting now”, and a chart of “People who entered, per day”.',
      },
      s4: {
        title: 'Find where people drop off',
        body: 'Under “Each step”, every row shows “Reached”, “Drop” and “Now”. A high “Drop” means many people did not get from the step before to this one (on a branch: they went the other way). People still waiting are not counted as dropped.',
      },
      s5: {
        title: 'See what went wrong',
        body: 'Under “What went wrong”, failures are listed with their reason and step — for example a missing template or an opted-out contact. Fix the step, and the next people go through cleanly.',
      },
      s6: {
        title: 'See who reached a step',
        body: 'Click the number in a row’s “Reached” column (“See who reached this step”). The “Each contact” tab opens, filtered to that step. Clicking the step name opens that step’s settings instead.',
      },
      s7: {
        title: 'Follow one contact’s path',
        body: 'On “Each contact”, type a name in “Find a contact…” or filter “Everyone” / “In progress”, then click a person. You see every step they passed, the time, their last reply and, if it failed, “Why it failed”.',
      },
      s8: {
        title: 'Start the journey for chosen contacts',
        body: 'For a journey that starts “Started by hand or by the API”, click the top card, search contacts under “Start it now for…”, pick them and click “Start”. You see “Started for 3 contacts.” (anyone already in it is skipped).',
      },
      s9: {
        title: 'Watch the numbers on the steps',
        body: 'Close the panel with the X. Each card in the Steps view also shows “reached”, “waiting” and “failed” counts for the period.',
      },
    },
    done: 'The Runs panel shows numbers per step, and clicking a contact shows their path step by step.',
    tips: {
      t1: 'No numbers yet? Runs only count once the journey is saved and live.',
      t2: 'Check “What went wrong” the day after you go live — most problems show up in the first few runs.',
      t3: 'The “Follow-ups” line shows how many nudges were sent and how many people replied after one.',
    },
    trouble:
      '“Start” does nothing or shows an error: the journey must be live, and the trigger must be “Started by hand or by the API”. A contact is missing from the list: you only see contacts in your own outlets (entities). Many runs failed on a send step: open that step and check its channel is connected and its WhatsApp template is approved.',
  },
  automationRule: {
    title: 'Create an automation rule',
    summary:
      'An automation rule reacts instantly to a conversation: when something happens, if conditions hold, then do something — for example send VIP chats to a team, hand over when someone writes “refund”, or ask unknown customers who they are.',
    before:
      'You need an admin login with the “manage flows” permission. To assign chats to a team, create the team first (sidebar → Admin → Teams). Rules are for instant reactions to chats; for messages spread over days, use a journey instead.',
    steps: {
      s1: {
        title: 'Open Automations',
        body: 'In the sidebar, open Automate → Automations. You see your rules, each with what it reacts to, its conditions and actions, and how many times it has fired.',
      },
      s2: {
        title: 'Start a new rule',
        body: 'Click “Add rule”. A window “New automation rule” opens.',
      },
      s3: {
        title: 'Name it',
        body: 'Under “Rule name”, type what it does, for example “VIP chats to the VIP team”. A “Description” is optional.',
      },
      s4: {
        title: 'Choose When',
        body: 'Under “When”, pick the event: “A conversation starts”, “A customer message arrives”, “A conversation’s status changes” or “A conversation is assigned”. For most rules, “A customer message arrives” is right.',
      },
      s5: {
        title: 'Choose where it applies (optional)',
        body: 'Under “Available to”, keep “Everyone (global)” or choose “Selected entities” and tick one or more outlets so the rule only acts on their conversations (and their branches’).',
      },
      s6: {
        title: 'Add the If conditions',
        body: 'Click “+ Add condition” and pick a field, an operator and a value — for example “Contact tags” “contains” vip, or “Message text” “contains” refund. All conditions must hold; with none, the rule fires every time.',
      },
      s7: {
        title: 'Add the Then actions',
        body: 'A new rule starts with one “Add label” action. Change it, or click “+ Add action” for more: for example “Assign to team” → pick the team, or “Hand over to a human” → type “Why (shown to the team)”.',
      },
      s8: {
        title: 'Example: ask unknown customers who they are',
        body: 'With “When” = “A conversation starts” and the condition “Contact is linked (yes/no)” “is” “No” (pick it from the list), add the action “Ask who they are (unknown customer)” and choose “Ask — leave a note if no contact matches” or “Ask and create the contact”.',
      },
      s9: {
        title: 'Save the rule',
        body: 'Leave “Active” ticked and click “Create rule”. A message “Rule … created.” appears and the rule shows at the bottom of the list with an “On” switch.',
      },
      s10: {
        title: 'Put the rules in order',
        body: 'Rules run from top to bottom, and every rule whose conditions hold runs — not just the first one. The number on each rule shows its place, and a new rule goes to the bottom. To move a rule, drag it, or use the up and down arrows beside it. If two rules assign a team, the lower one has the last word.',
      },
      s11: {
        title: 'Test it',
        body: 'Click “Test” on the rule. In “Conversation to test against”, search a name, number or message and pick a real chat; optionally type a “Message to test with (optional)”, for example I want a refund. Click “Run the test”: you see “This rule would fire” or “This rule would not fire”, each condition ticked or crossed, and “This conversation: …” with the real value. Nothing is sent or changed.',
      },
    },
    done: 'The test says “This rule would fire” for a chat that should match, and once real messages arrive the rule’s card shows “fired 1×” (or more).',
    tips: {
      t1: 'Click the “On” / “Off” button on a rule to pause it without deleting it.',
      t2: 'Words are matched without caring about capitals — “Refund” and “refund” both match “contains refund”.',
      t3: 'Combine actions: “Add label” urgent + “Notify the team” + “Turn the chatbot off” makes sure a person sees it.',
      t4: 'Click the pencil on a rule to edit it; your changes apply to the next message.',
      t5: 'To see it for real, send a matching message from your own phone to your connected number or website chat, then open the chat in Chats to check the label, team or note.',
      t6: 'You can test a rule that is turned off — the result reminds you it won’t actually run until you turn it on.',
    },
    trouble:
      'The rule never fires: click “Test” with a chat that should match — the crossed condition shows what doesn’t hold. Also check it is “On”, that “Available to” covers the outlet the chat belongs to, and that each condition has a value (an unknown operator matches nothing). It fires too often: add a condition such as “Is the first message” = yes. Two rules fight each other: all matching rules run, top to bottom — drag one above the other, turn one off or tighten its conditions.',
  },
  accountAssignment: {
    title: 'Choose which number each contact talks to',
    summary:
      'When you have several WhatsApp numbers or mailboxes, each contact sticks to one of them so the conversation stays on the same number. Here you see who is on which number, move or pin contacts, spread them evenly, and set the routing rules in plain words.',
    before:
      'You need an admin login with the “account assignment” permission and at least two connected accounts on the same channel (for example two WhatsApp numbers) — with one, everyone is on it anyway.',
    steps: {
      s1: {
        title: 'Open Account assignment',
        body: 'In the sidebar, open Work → Account assignment. At the top, choose the “Channel” (WhatsApp, email…).',
      },
      s2: {
        title: 'Read the account cards',
        body: 'Each sending account has a card showing how many contacts are routed through it and how many are pinned. On WhatsApp a chip shows its role: a business (reminder) number or a conversation (personal) number.',
      },
      s3: {
        title: 'Find one contact',
        body: 'Type a name or phone in “Find a contact by name or phone…”. Cards with that contact show how many matches they hold.',
      },
      s4: {
        title: 'Move a contact to another number',
        body: 'Click “Manage contacts” on a card. Next to the contact, pick another account in the list (or “Automatic” to let ChatBranch choose). A dot marks the unsaved change.',
      },
      s5: {
        title: 'Pin a contact',
        body: 'In the same window, turn on the pin switch next to the contact (“Pinned — rebalancing never moves it”). Use this for VIPs who must always hear from the same number. Click “Save changes”.',
      },
      s6: {
        title: 'Spread contacts evenly',
        body: 'Click “Rebalance evenly”. The window “Rebalance evenly?” says how many unpinned contacts may move; click “Rebalance”. Pinned contacts stay put, and contacts on an account with room usually stay too.',
      },
      s7: {
        title: 'Set AI reply pacing',
        body: 'Scroll to “Routing rules”. Under “AI reply pacing”, keep “Queued” (recommended): AI replies go out one at a time per number with human-like pauses, which protects personal numbers. “Instant” replies straight away.',
      },
      s8: {
        title: 'Set the cold-text guard',
        body: 'Under “Cold-text guard”, keep “Block” (recommended): it stops first messages to people who never wrote to you — the top reason WhatsApp bans personal numbers. “Warn” asks for confirmation; “Allow” sends anyway at your own risk.',
      },
      s9: {
        title: 'Business-number chatbot and redirect',
        body: '“Business-number chatbot” decides whether the AI chats on your business (reminder) number by default. If it is off, turn on “Business-number redirect” so a customer replying there gets one message per 24 hours pointing them to “Conversation number”, “Customer support” or “Your template”. Fill in “Support contact” if you choose support.',
      },
      s10: {
        title: 'Plan for a banned number',
        body: 'Under “Ban recovery”, choose “One-click from notification” (recommended) or “Automatic”, and pick an approved “Recovery template” that tells customers your new number. If a number goes down, a banner shows how many contacts are affected with a “Send recovery templates” button.',
      },
      s11: {
        title: 'Save the rules',
        body: 'Click “Save routing rules”. You see “Routing rules saved — live immediately.”',
      },
    },
    done: 'The account cards show the contact counts you expect, and “Routing rules saved — live immediately.” appears after saving.',
    tips: {
      t1: 'If a number gets disconnected or banned, its unpinned contacts move automatically to a healthy number, with a note in the chat — you don’t have to do it by hand.',
      t2: 'Add your own opt-out words (for example BATAL) under “Opt-out keywords”; STOP, UNSUBSCRIBE and BERHENTI always work.',
      t3: 'To move one chat right now, an admin can also switch the number from inside the chat.',
    },
    trouble:
      '“No WhatsApp accounts yet”: connect a number first under Channels. Rebalance says “Already balanced — no contacts moved.”: contacts are already spread, or all of them are pinned. The recovery template list is empty: only approved WhatsApp templates on a business (Cloud) number can be picked — create one under Templates and wait for Meta’s approval.',
  },
}
