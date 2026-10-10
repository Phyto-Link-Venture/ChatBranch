/** How-to guides — start group. Keys: see lib/guides/types.ts. */
export const start = {
  firstSignIn: {
    title: 'Sign in for the first time',
    summary: 'Open ChatBranch, sign in with the username and password you were given, and find your way around the home page. Takes about two minutes.',
    before:
      'You need the ChatBranch web address (for example https://chat.yourshop.com) and a username and password. Accounts are created by your administrator — there is no “Sign up” button. A computer or phone with an up-to-date browser (Chrome, Edge, Safari or Firefox) is enough.',
    steps: {
      s1: {
        title: 'Open the sign-in page',
        body: 'Type the ChatBranch address into your browser and press Enter. You will see the ChatBranch logo and a card headed “Welcome back”.',
      },
      s2: {
        title: 'Pick your language (optional)',
        body: 'Under the card, click “English”, “Bahasa Melayu” or “简体中文”. The page switches at once. This choice is remembered on this browser only until you sign in.',
      },
      s3: {
        title: 'Type your username and password',
        body: 'Click the “Username” box and type your username (for example siti). Then type your password in the “Password” box. Click the eye icon to check what you typed.',
      },
      s4: {
        title: 'Click “Sign in”',
        body: 'The button turns active once both boxes are filled. A short spinner shows while ChatBranch checks your details.',
      },
      s5: {
        title: 'You land on the Dashboard',
        body: 'The Dashboard is the home page: a summary of conversations, calls and the AI for the last few days. If your role can’t see the Dashboard, you land on the first page you are allowed to use instead (often Chats).',
      },
      s6: {
        title: 'Follow (or skip) the page guide',
        body: 'The first time you open a page, a small guide pops up and highlights its parts one by one. Click “Next” to move on, “Back” to go back, or “Skip” to close it. It will not pop up again on that page.',
      },
      s7: {
        title: 'Replay a guide any time',
        body: 'Click the ? button in the top bar (its name is “Guide”). Choose “Show this page’s guide” to replay it, “Tour of ChatBranch” for a tour of the whole screen, or “Open the user manual” for this manual.',
      },
      s8: {
        title: 'Stop guides from popping up (optional)',
        body: 'In the same ? menu, switch off “Show guides on first visit”. You can still replay any guide from the menu.',
      },
      s9: {
        title: 'Sign out when you are done',
        body: 'At the bottom of the sidebar, next to your name and role, click the door-with-arrow icon (“Sign out”). On a phone, tap “More” first to open the menu. Always sign out on a shared computer.',
      },
    },
    done: 'You see your name and role at the bottom-left of the sidebar and the Dashboard page in the middle of the screen.',
    tips: {
      t1: 'Once signed in, change your language for good in Settings → Appearance — it then follows you to any device.',
      t2: 'Bookmark the address, or on a phone use “Install app” at the bottom of the menu to get a ChatBranch icon on your home screen.',
      t3: 'You can be signed in on your computer and your phone at the same time.',
    },
    trouble:
      'The page shows an error under the password: check the spelling and Caps Lock, then try again; only your administrator can reset a forgotten password. “Your session expired — please sign in again”: you were signed out (for example after a long break) — just sign in again. The page doesn’t load at all: check your internet connection and the address with your administrator.',
  },
  findYourWay: {
    title: 'Find your way around',
    summary: 'Learn the parts of the screen you will use every day: the sidebar, the search box, the Live chip, the bell and, on a phone, the tab bar at the bottom.',
    steps: {
      s1: {
        title: 'Look at the sidebar',
        body: 'On the left is the sidebar. At the top are “Dashboard” and “Notifications”, then groups: “Work” (Chats, Contacts, Bookings…), “Automate”, “Library”, “Channels”, “Admin” and “Review”. You only see the pages your role allows.',
      },
      s2: {
        title: 'Fold a group you don’t use',
        body: 'Click a group name, such as “Library”, to fold it away; click it again to open it. The small arrow next to the name shows whether it is open.',
      },
      s3: {
        title: 'Make the sidebar narrow',
        body: 'Click the panel icon at the top of the sidebar, next to the logo (“Collapse sidebar”). Only icons remain — hover one to see its name. Click the icon again (“Expand sidebar”) to bring the names back.',
      },
      s4: {
        title: 'Find Settings and your name',
        body: '“Settings” is at the very bottom of the sidebar. Below it are your name, your role, a sun/moon button for light or dark mode, and “Sign out”.',
      },
      s5: {
        title: 'Open the search (command palette)',
        body: 'Click the search box at the top (“Search contacts, chats, flows, templates…”) or press Ctrl+K (⌘K on a Mac) from anywhere. A search window opens over the page.',
      },
      s6: {
        title: 'Search and jump',
        body: 'Type a name, phone number or page name, for example “siti” or “templates”. Matching pages, contacts and conversations appear in groups. Use the arrow keys and press Enter, or click a result to open it. Esc closes the window.',
      },
      s7: {
        title: 'Check the Live chip',
        body: 'In the top bar, the “Live” chip shows how many calls are running and how many chats are waiting for a reply. The dot is “Live — connected” when new messages arrive instantly. Click it to see the waiting chats and active calls, then “Open chats”.',
      },
      s8: {
        title: 'Check the bell',
        body: 'The bell at the top right shows a number when you have unread notifications. Click it to see the latest ones, “Mark all read”, or “View all” to open the Notifications page.',
      },
      s9: {
        title: 'On a phone: use the bottom tabs',
        body: 'On a small screen the sidebar hides. Use the tab bar at the bottom — “Home”, “Chats”, “Contacts” and “More”. “More” opens the full menu, including the entity switcher and “Sign out”.',
      },
    },
    done: 'You can open any page from the sidebar or from Ctrl+K, and you know where waiting chats and new notifications show up.',
    tips: {
      t1: 'Ctrl+K is the fastest way to get anywhere — type a few letters of a page or a customer’s name and press Enter.',
      t2: 'Your folded groups and the narrow sidebar are remembered on this browser.',
      t3: 'Ctrl+J (⌘J on a Mac) opens Ask ChatBranch, the assistant — see “Ask ChatBranch for help”.',
    },
    trouble:
      'A page you need is missing from the sidebar, or you see “You don’t have access to …”: your role doesn’t include it — ask an admin to grant it under Users & roles. The Live chip says “Offline — updates paused”: your connection dropped; it reconnects by itself when the network is back.',
  },
  pickEntity: {
    title: 'Choose which outlet you are looking at',
    summary: 'If your business has several companies, outlets or branches (ChatBranch calls them entities), use the switcher in the top bar to choose whose chats, contacts and numbers every page shows.',
    steps: {
      s1: {
        title: 'Find the switcher',
        body: 'In the top bar, to the left of the sparkle button, is a box with a name and a small arrow — that is the entity you are looking at now. On a phone, tap “More” at the bottom; the switcher is at the top of the menu.',
      },
      s2: {
        title: 'Open the list',
        body: 'Click the box. A list opens with “All entities” at the top and every entity you may see below it. Main entities carry a “Main” badge; their branches are indented under them.',
      },
      s3: {
        title: 'Show only one entity',
        body: 'Click an entity’s NAME. The list closes and every page now shows only that entity (and its branches). A tick appears next to its name the next time you open the list.',
      },
      s4: {
        title: 'See several together',
        body: 'Click the small CHECKBOX in front of a name instead. It is added to the view and the list stays open, so you can tick more. The box in the top bar then says, for example, “2 entities”. Click a ticked checkbox to remove that entity again.',
      },
      s5: {
        title: 'Branches come along automatically',
        body: 'Ticking a main entity includes all its branches — their checkboxes appear ticked and greyed out (“Included with …”). You don’t need to tick them one by one.',
      },
      s6: {
        title: 'See everything',
        body: 'Click “All entities” at the top of the list to see every entity you are allowed to see at once.',
      },
      s7: {
        title: 'Search a long list (optional)',
        body: 'When there are more than six entities, a “Find an entity” box appears at the top of the list. Type part of a name to filter it.',
      },
      s8: {
        title: 'Your choice is remembered',
        body: 'ChatBranch remembers your pick on this browser. Next time you sign in here, you start on the same entity or entities.',
      },
    },
    done: 'The box in the top bar shows the entity name (or “2 entities”, or “All entities”) and the page reloads with only that data.',
    tips: {
      t1: 'Head office sees its branches’ chats and contacts; a branch never sees head office’s or a sibling branch’s.',
      t2: 'To change an entity’s own settings (for example its AI persona), pick ONE entity — with several ticked, settings pages show the organization level.',
      t3: 'If a list looks empty, check the switcher first — you may be looking at a different outlet.',
    },
    trouble:
      'The box has no arrow and can’t be clicked: you can see only one entity, so there is nothing to switch. An entity is missing from the list: you are not assigned to it — ask an admin (Admin → Bindings or Users & roles). A notice says several entities are ticked: pick a single entity in the switcher to see or edit its own settings.',
  },
  languageAndLook: {
    title: 'Change the language and the look',
    summary: 'Choose the dashboard language, a theme, light or dark mode, an accent colour and a comfortable text size. Your choices are saved to your profile, so they follow you to any device.',
    steps: {
      s1: {
        title: 'Open Settings → Appearance',
        body: 'Click “Settings” at the bottom of the sidebar. Under “Personal”, click “Appearance”.',
      },
      s2: {
        title: 'Choose your language',
        body: 'In the “Language” card at the top, click “English”, “Bahasa Melayu” or “简体中文”. The whole dashboard switches at once and you see “Language saved.”.',
      },
      s3: {
        title: 'Pick a theme',
        body: 'Under “Theme”, click one of the ten cards — for example “Paper & Ink” (warm paper, the default), “Ocean”, “Midnight” (great in dark mode) or “High contrast” (the easiest to read). Each card shows a small preview. The app changes straight away — there is no Save button.',
      },
      s4: {
        title: 'Choose light or dark',
        body: 'Under “Mode”, click “Light”, “Dark” or “System”. “System” follows your computer or phone setting, switching to dark at night if your device does.',
      },
      s5: {
        title: 'Make text bigger or smaller',
        body: 'Under “Text size”, click S, M, L or XL, or drag the slider to any size between 85 % and 130 %. Everything on screen grows or shrinks — try L if the text feels small.',
      },
      s6: {
        title: 'Pick an accent colour (optional)',
        body: 'Click “Advanced settings” to open it. Under “Accent colour”, click one of the colour dots, or “Theme default” to go back. ChatBranch adjusts it slightly so text stays easy to read.',
      },
      s7: {
        title: 'Fonts, spacing and layout (optional)',
        body: 'Also in “Advanced settings”: “Typography” (body font, heading font, line height), “Spacing” (density, page width, page margins, card padding), “Layout” (sidebar on the left or right, or a top bar — computers only) and “Details” (corners, card style, motion). The preview card at the top shows how it looks.',
      },
      s8: {
        title: 'Switch light/dark quickly',
        body: 'Anywhere in the app, click the sun/moon button at the bottom of the sidebar, next to your name. It flips between light and dark and keeps the rest of your look.',
      },
      s9: {
        title: 'Start again if you don’t like it',
        body: 'At the bottom of the Appearance page, click “Reset to organization default” (or “Reset to default” when your organization has none) to go back to the organization’s look (or Paper & Ink, light).',
      },
    },
    done: 'The dashboard shows your chosen language and colours, and they are still there after you sign in on another device.',
    tips: {
      t1: 'Admins can click “Use as organization default” (and “Make this the organization default” for the language) so new users start with this look.',
      t2: 'Changing your language doesn’t change what customers see — the AI replies in the customer’s language.',
      t3: 'Dark mode is easier on the eyes for evening shifts.',
    },
    trouble:
      '“Applied on this device — could not save it to your profile.”: the look works here, but the server wasn’t reached; try again later so it follows you to other devices. The language goes back after signing in: you chose it on the sign-in page, which only remembers it on that browser — choose it again in Settings → Appearance.',
  },
  myNotifications: {
    title: 'Manage your notifications',
    summary: 'Read and tidy your alerts — new customer messages, channel problems, handovers — and choose which kinds reach you.',
    steps: {
      s1: {
        title: 'Open the bell',
        body: 'Click the bell at the top right. A red number means unread alerts. A short list of the latest ones opens.',
      },
      s2: {
        title: 'Open an alert',
        body: 'Click an alert to go straight to what it is about — for example the conversation with the customer who wrote. Click “Mark all read” to clear the number.',
      },
      s3: {
        title: 'Open the Notifications page',
        body: 'Click “View all” at the bottom of the bell, or “Notifications” near the top of the sidebar. You see every alert, newest first.',
      },
      s4: {
        title: 'Filter the list',
        body: 'Use “Category” (for example “Customer messages” or “Channel health”), the “Unread” / “Read” choice, the dates, or the search box to find an alert. “Clear filters” shows everything again.',
      },
      s5: {
        title: 'Tidy up',
        body: 'Use the small buttons on the right of an alert to mark it read or unread, or to delete it. “Mark all read” clears everything; “Delete read” removes the alerts you have already read (the conversations themselves are not touched).',
      },
      s6: {
        title: 'Open your notification settings',
        body: 'Click “Notification settings” on the Notifications page, or go to Settings → Personal → “My notifications”.',
      },
      s7: {
        title: 'Switch off what you don’t need',
        body: 'In “Alerts I receive”, use the switch next to each kind: “Customer messages”, “Channel health”, “Flows & automations”, “Admin & configuration” and “System”. Each change saves at once and affects only you.',
      },
      s8: {
        title: 'Only your own conversations (optional)',
        body: 'Under “Customer messages scope”, switch on “Only conversations assigned to me or my teams” if you are an admin and don’t want alerts for unassigned chats.',
      },
      s9: {
        title: 'Check it works',
        body: 'When the next alert of a kind you left on arrives, the bell shows a number again.',
      },
    },
    done: 'The bell number is gone, and the switches under “Alerts I receive” show exactly the kinds you want.',
    tips: {
      t1: 'New-message alerts go to the chat’s assigned agent first, then its team, then the admins — so assigning chats cuts the noise for everyone.',
      t2: 'Several messages in the same conversation collapse into one unread alert.',
      t3: 'Keep “Channel health” on if you look after the WhatsApp numbers — it tells you when one disconnects.',
    },
    trouble:
      'A switch is greyed out with “Muted for the whole organization by an admin.”: that kind is off for everyone — ask an admin (Settings → Notification defaults). You never get message alerts: check that “Customer messages” is on and that chats are assigned to you or your team.',
  },
  askChatBranch: {
    title: 'Ask ChatBranch for help',
    summary: 'Ask the built-in assistant how to do something, look things up, or have it prepare a change for you. It acts as you, and nothing changes until you click “Confirm”.',
    steps: {
      s1: {
        title: 'Open the assistant',
        body: 'Click the sparkle button in the top bar (“Ask ChatBranch”), or press Ctrl+J (⌘J on a Mac). On a computer you can also hover the thin tab on the right edge of the screen and click it. A panel opens on the right.',
      },
      s2: {
        title: 'Try a suggestion',
        body: 'Under “How can I help?” you see example buttons such as “How many chats are waiting for a reply?” or “How do I connect WhatsApp?”. Click one to ask it.',
      },
      s3: {
        title: 'Ask your own question',
        body: 'Type in the box at the bottom (“Ask anything about ChatBranch…”), for example “Find Siti Aminah’s phone number”, and press Enter. Shift+Enter starts a new line.',
      },
      s4: {
        title: 'Ask it to do something',
        body: 'Type a task, for example “Turn the AI off for this chat” or “Tag Ahmad as VIP”. The assistant prepares it but does not do it yet.',
      },
      s5: {
        title: 'Read the confirm card',
        body: 'A card marked “Needs your confirmation” shows exactly what will change. Click “Details” to see more.',
      },
      s6: {
        title: 'Confirm or cancel',
        body: 'Click “Confirm” to do it — the card then says “Done” — or “Cancel” to drop it. A card you leave for about 15 minutes expires; just ask again.',
      },
      s7: {
        title: 'Find an earlier chat',
        body: 'Click the clock icon at the top of the panel (“History”) to see your past chats — only you can see them. Click one to continue it. The speech-bubble-plus icon (“New chat”) starts a fresh one.',
      },
      s8: {
        title: 'Move it out of the way (optional)',
        body: 'On a computer, click “Float — move it anywhere” in the panel header and drag the window by its top bar. Click “Dock to the side” to put it back.',
      },
      s9: {
        title: 'Close it',
        body: 'Press Esc or click the X. Your chat is kept, and opens again where you left off.',
      },
    },
    done: 'The assistant answered your question, or a card says “Done” after you clicked “Confirm”.',
    tips: {
      t1: 'It can only do what your own role allows, and every change it makes is recorded in the activity log under your name.',
      t2: 'Be specific: name the contact, channel or page (“on WhatsApp”, “in the Bangsar outlet”).',
      t3: 'Ask “how do I …” questions any time — it answers from this manual and the knowledge base.',
    },
    trouble:
      'A “Sandbox” badge shows at the top: no AI model is connected, so it understands only simple requests and how-to questions — an admin adds a key in Settings → AI keys & models. A card says “Expired”: ask again. “I couldn’t reach the assistant”: check your connection and try again.',
  },
  myAccount: {
    title: 'Change your name or password',
    summary: 'Set the name colleagues see, add your email, change your password and sign out devices you no longer use — all on Settings → My account.',
    steps: {
      s1: {
        title: 'Open Settings → My account',
        body: 'Click your name at the bottom of the sidebar, or click “Settings” and then “My account” under “Personal”.',
      },
      s2: {
        title: 'Change your name',
        body: 'In the “Profile” card, type your first and last name. Add your email if you like, then click “Save profile”. Your name in the sidebar updates at once.',
      },
      s3: {
        title: 'Type your current password',
        body: 'In the “Password” card, type the password you use today in “Current password”.',
      },
      s4: {
        title: 'Choose a new password',
        body: 'Type the new password in “New password” and again in “Confirm new password”. It needs at least 8 characters; the strength hint under it says how strong it is. Click the eye icon to check what you typed.',
      },
      s5: {
        title: 'Click “Change password”',
        body: 'You see “Password changed — N other devices signed out”. This browser stays signed in; everywhere else you sign in again with the new password.',
      },
      s6: {
        title: 'Sign out other devices (optional)',
        body: 'In the “Devices” card, click “Sign out other devices” to end every other session without changing your password — for example after using a shared computer.',
      },
    },
    done: 'The sidebar shows your new name, and signing in elsewhere needs the new password.',
    tips: {
      t1: 'Your username never changes — it is what you sign in with and what the activity log records.',
      t2: 'Forgot your password? Only an administrator can set a new one for you on the Users page.',
    },
    trouble: '“Your current password is wrong”: check Caps Lock and try again. “Too many wrong passwords”: wait about 15 minutes before trying again. “Another user already has that email address”: use a different email or ask an administrator.',
  },
  myConnections: {
    title: 'Connect your own Google Calendar',
    summary: 'Link your Google Calendar to ChatBranch so the AI can check when you are free and book customer appointments straight into your calendar for chats assigned to you.',
    before:
      'You need a Google account with Google Calendar. Anyone can connect their own — no special permission is needed — but a super admin must first set up Google sign-in once (Settings → App & provider keys). If that is not done, the page says so.',
    steps: {
      s1: {
        title: 'Open Settings → My connections',
        body: 'Click “Settings” at the bottom of the sidebar, then “My connections” under “Personal”. The card is called “My Google account”, marked “Only you”.',
      },
      s2: {
        title: 'Find Google Calendar',
        body: 'Each Google product has a row with a short line saying what it is used for. Find the “Google Calendar” row.',
      },
      s3: {
        title: 'Click “Connect”',
        body: 'Click “Connect” on the Google Calendar row. Your browser goes to Google’s sign-in page.',
      },
      s4: {
        title: 'Choose your Google account',
        body: 'Pick the Google account whose calendar you use for work (sign in if asked).',
      },
      s5: {
        title: 'Allow access',
        body: 'Google lists what ChatBranch may do with your calendar. Click “Continue” or “Allow”. If you click Cancel, nothing is connected.',
      },
      s6: {
        title: 'Check it is connected',
        body: 'You come back to My connections with the message “Connected your Google Calendar.” and a “Connected” label on the row.',
      },
      s7: {
        title: 'Disconnect or switch account later',
        body: 'Click “Disconnect” on the row to unlink it at any time, or “Reconnect” to sign in again (for example with a different Google account).',
      },
    },
    done: 'The Google Calendar row shows “Connected”. Booking skills set to the conversation’s assigned agent now check your free time and book into this calendar.',
    tips: {
      t1: 'Your connection is yours alone — other people keep using their own, or the shared one an admin set up.',
      t2: 'Ask an admin to set your booking skill’s calendar to “Conversation’s assigned agent” (Library → AI skills), so bookings land in the calendar of whoever handles the chat.',
      t3: 'Bookings the AI makes appear on the Bookings page (Work → Bookings) and as events in your Google Calendar.',
    },
    trouble:
      '“Google sign-in isn’t set up for this workspace yet”: a super admin must register the Google client in Settings → App & provider keys. “Google sign-in was cancelled — nothing was connected.”: click “Connect” again and allow access. The row says “Needs reconnecting”: Google withdrew access (for example after a password change) — click “Reconnect”.',
  },
}
