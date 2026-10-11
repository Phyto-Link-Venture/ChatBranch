/** How-to guides — admin group. Keys: see lib/guides/types.ts. */
export const admin = {
  inviteTeammate: {
    title: 'Add a teammate and choose their role',
    summary:
      'Create a login for a new colleague, pick what they are allowed to do, and decide which branches they can see. Takes about two minutes.',
    before:
      'You need to be an admin or a super admin. An admin can add agents in their own organization; only a super admin can add other admins. Decide on a username and a temporary password (at least 10 characters) to give the new person.',
    steps: {
      s1: {
        title: 'Open Users & roles',
        body: 'In the sidebar, open Admin → “Users & roles”. You see a table of everyone who can sign in, with their role, the entities they can see and when they last logged in.',
      },
      s2: {
        title: 'Know the three built-in roles',
        body: 'At the top of the page three cards explain the roles. “Super admin” can do everything, everywhere. “Admin” runs their own organization: channels, routing, journeys, templates, tags and agent logins. “Agent” works chats, calls and contacts in the entities assigned to them, with no settings.',
      },
      s3: {
        title: 'Click “Add user”',
        body: 'Click the “Add user” button above the table. A window called “Add user” opens.',
      },
      s4: {
        title: 'Type the login details',
        body: 'Fill in “Username” (for example aina — it must be unique), an optional “Display name” (for example Aina Rahman) and a “Password” of at least 10 characters that is not a common one. Type the username yourself — text your browser fills in automatically is not counted.',
      },
      s5: {
        title: 'Pick the role',
        body: 'Open the “Role” list and choose Agent, Admin or one of your custom roles (they are listed under “Custom roles”). If unsure, choose Agent — you can change it later.',
      },
      s6: {
        title: 'Choose which entities an agent sees',
        body: 'When the role is Agent or a custom role, an “Entity access” grid appears. Tick the branches or companies this person works for — ticking a main entity includes everything beneath it. Leave everything unticked to let them see the whole organization.',
      },
      s7: {
        title: 'Save the new user',
        body: 'Click “Add user” at the bottom of the window. A message “User … created.” appears and the person shows up in the table. Give them the username and password in person or by a private message.',
      },
      s8: {
        title: 'Reset a password or change a role later',
        body: 'On the person’s row, change the “Role” list to switch their role at once. Click the key icon (“Reset password”), type a new password and click “Set password” — this signs them out everywhere. The last icon deactivates or reactivates the login.',
      },
    },
    done: 'The new teammate appears in the table with the right role, and can sign in with the username and password you gave them.',
    tips: {
      t1: 'A login can also belong to a customer: pick a contact in the “Signs in as contact” column and that person signs in as that customer.',
      t2: 'Change what an agent can see later with the building icon (“Entity access”) on their row.',
      t3: 'Ask the new person to change nothing until they have opened the app tour from the ? button in the top bar.',
    },
    trouble:
      'If saving fails with “Username is required”, type the username again by hand. If the password is refused, make it at least 10 characters and less predictable (not the username, not a well-known password). If you cannot pick Admin, only a super admin can create admins. If the button is missing, your role does not hold “Create & manage users” — ask a super admin.',
  },
  rolesPermissions: {
    title: 'Set what each role can do',
    summary:
      'Use the permission matrix to fine-tune the built-in roles or create your own role, such as a team lead, with exactly the actions you tick.',
    before:
      'Only a super admin can open Permissions. Know which actions you want the role to have, for example “View chats” and “Send messages” but not “Manage channel accounts”.',
    steps: {
      s1: {
        title: 'Open Permissions',
        body: 'In the sidebar, open Admin → “Permissions”. You see one card per role, with how many users hold it and how many areas it can reach.',
      },
      s2: {
        title: 'Add a custom role (optional)',
        body: 'To make a new role, click “Add role”. Type a “Role name”, for example Team Lead, and click “Create role”. A new role starts with no access at all.',
      },
      s3: {
        title: 'Open the role you want to change',
        body: 'Click “Manage access” on the role’s card. A grid of areas appears — Contacts, Chats, Flows, Channels, AI, Settings and more — each showing No access, Partial or Full access.',
      },
      s4: {
        title: 'Grant a whole area at once',
        body: 'Tick the checkbox on an area card to give the role every action in that area. Untick it to take them all away. Use “Search resources…” to find an area quickly.',
      },
      s5: {
        title: 'Pick single actions',
        body: 'Click “Manage” on an area card to see each action, such as “View chats”, “Send messages” or “Manage chats”. Switch each one on or off, then click “Apply”.',
      },
      s6: {
        title: 'Save your changes',
        body: 'Nothing is stored until you click “Save changes”. A message “Permissions saved.” confirms it, and people with that role get the new access the next time a page loads.',
      },
      s7: {
        title: 'Give the role to people',
        body: 'Go to Admin → “Users & roles” and choose the new role in the “Role” list on a person’s row, or when you add a user.',
      },
      s8: {
        title: 'Rename or delete a custom role',
        body: 'Back on the role list, use the pencil icon to rename a custom role and the bin icon to delete it. A role still given to someone cannot be deleted — move those users to another role first.',
      },
    },
    done: '“Permissions saved.” appears, and the role card shows the new number of areas it can reach.',
    tips: {
      t1: 'Four actions are super-admin-only by default: editing permissions, app & provider keys, AI keys & models, and AI billing terms. Grant them to another role only if you fully trust it.',
      t2: 'A super admin always keeps every permission, so you can never lock yourself out.',
      t3: 'Access is on top of entity scope: an agent with “View chats” still sees only the chats of their own entities.',
    },
    trouble:
      'If the page says the matrix is managed by super admins, you are not signed in as a super admin. If a change seems not to work, check that you clicked “Save changes”, then ask the person to reload the page. If deleting a role is refused, it is still assigned to someone.',
  },
  entitiesBranches: {
    title: 'Set up your company and its branches',
    summary:
      'Entities describe how your business is organized: a company at the top and branches, outlets or units under it. They decide which conversations, contacts and settings belong where.',
    before:
      'You need to be an admin. Sketch your structure first, for example: Northwind Trading (company) → KL branch, Penang branch.',
    steps: {
      s1: {
        title: 'Open Entities',
        body: 'In the sidebar, open Admin → “Entities”. The list shows your entities as a tree; companies at the top carry a MAIN badge.',
      },
      s2: {
        title: 'Add the company',
        body: 'Click “Add entity”. Type the “Entity name”, for example Northwind Trading. Leave “Slug” empty — it is filled in for you.',
      },
      s3: {
        title: 'Make it a main entity',
        body: 'In “Sits under”, choose “Nothing — make it a main entity”. Click the save button. The company appears with the MAIN badge.',
      },
      s4: {
        title: 'Add a branch under it',
        body: 'Click “Add entity” again, type the branch name (for example KL branch) and in “Sits under” pick the company. Save. The branch appears indented under its company.',
      },
      s5: {
        title: 'Know what branches inherit',
        body: 'A branch automatically uses what is set higher up: the AI persona and house rules, knowledge, AI skills, templates, tags, teams, channel accounts, AI lessons and settings such as quiet hours. A branch can add its own or override a setting; the nearest level wins.',
      },
      s6: {
        title: 'Know what stays per branch',
        body: 'Contacts, conversations, AI drafts and dashboard figures belong to exactly one branch. The company sees all of its branches’ conversations; a branch never sees the company’s or a sibling’s. Passwords and provider keys are always organization-wide.',
      },
      s7: {
        title: 'Connect numbers to a branch',
        body: 'A conversation belongs to the branch whose number or inbox received it. Set this per inbox in Channels, or in bulk on Admin → “Bindings” with “Channel account ↔ entity”.',
      },
      s8: {
        title: 'Move or delete an entity',
        body: 'Click the pencil icon on a row to edit it; change “Sits under” to move it to another company, and switch “Active” off to take it out of routing without deleting it. The bin icon deletes it — an entity that still has branches under it cannot be deleted.',
      },
    },
    done: 'Your company shows a MAIN badge and its branches are listed beneath it in the tree.',
    tips: {
      t1: 'Give agents access to their branch under Admin → “Users & roles” so they see only its chats.',
      t2: 'Use the entity switcher at the top of the dashboard to look at one branch at a time.',
      t3: 'Add entity tags (for example Inner company / Outside company) under Library → Tags to describe what kind of unit each one is.',
    },
    trouble:
      'If deleting is refused, delete or move its branches first. If a branch cannot be placed under another company, entities must stay in the same organization and cannot sit under themselves. If conversations land in the wrong branch, check which entity the receiving inbox belongs to.',
  },
  teams: {
    title: 'Create a team and share out new chats',
    summary:
      'A team owns a group of conversations, such as Front desk or Sales. New chats can be handed to its members in turn, so no message waits without an owner.',
    before:
      'You need to be an admin. Create the users first under Admin → “Users & roles” — a team needs members.',
    steps: {
      s1: {
        title: 'Open Teams',
        body: 'In the sidebar, open Admin → “Teams”. The table shows each team, its members and how many open conversations it owns.',
      },
      s2: {
        title: 'Click “Add team”',
        body: 'Click “Add team”. A window opens with the team’s details and a list of users.',
      },
      s3: {
        title: 'Name the team',
        body: 'Type a “Team name”, for example Front desk, and optionally a “Description” such as Walk-in and booking questions.',
      },
      s4: {
        title: 'Choose where it belongs',
        body: 'In “Belongs to”, pick “Organization-wide” or one entity (a company or branch). A team of a branch is offered for that branch’s inboxes.',
      },
      s5: {
        title: 'Turn on round-robin',
        body: 'Switch on “Round-robin new conversations”. Each new conversation then goes to the next member in turn, so the work is shared evenly.',
      },
      s6: {
        title: 'Add the members',
        body: 'Under “Members”, tick the people who are in this team. The counter at the bottom shows how many you picked. Click “Add team” to save.',
      },
      s7: {
        title: 'Send an inbox’s new chats to the team',
        body: 'In the sidebar, open Channels, click “Settings” on an inbox card and go to the “Who answers it” step. Pick your team in “Team” and click “Save & continue”. New conversations in that inbox now go to this team first.',
      },
      s8: {
        title: 'Change or delete a team later',
        body: 'Use the pencil icon on a team’s row to change its members or switch round-robin off. The bin icon deletes it — its open conversations go back to unassigned, none are lost.',
      },
    },
    done: 'The team appears in the table with its members and a Round-robin badge, and new chats on the chosen inbox show the team as owner.',
    tips: {
      t1: 'A conversation belongs to a team first and to one agent second, so it is still owned even before anyone picks it up.',
      t2: 'Automations and journeys can also hand a conversation to a team.',
      t3: 'Change many memberships at once on Admin → “Bindings” with “Team ↔ members”.',
    },
    trouble:
      'If new chats are not assigned, check that round-robin is on and the team has members. If the member list is empty, create users first. If an inbox does not offer your team, check that the team belongs to that inbox’s entity or is organization-wide.',
  },
  tags: {
    title: 'Create tags and tag your contacts',
    summary:
      'Tags say what something is — a VIP customer, a driver, an outside company, a chat that needs follow-up. Journeys, automations, templates and the AI can all read them.',
    before:
      'Creating tags needs the “Create & edit tags” permission (admins have it). Think of a few tags you need, for example VIP and Driver.',
    steps: {
      s1: {
        title: 'Open Tags',
        body: 'In the sidebar, open Library → “Tags”. Use “Show” to switch between “Contacts”, “Entities” and “Conversations” tags.',
      },
      s2: {
        title: 'Click “New tag”',
        body: 'Click “New tag”. A window opens with a live preview of the tag chip at the top.',
      },
      s3: {
        title: 'Name it and choose what it applies to',
        body: 'Type a name, for example VIP. Under “Applies to”, choose “Contacts” (people), “Entities” (companies and branches) or “Conversations” (labels on chats in the inbox).',
      },
      s4: {
        title: 'Pick a colour',
        body: 'Click one of the “Colour” swatches. The preview chip changes so you see how it will look.',
      },
      s5: {
        title: 'Make tags exclusive with a group (optional)',
        body: 'Type a “Group”, for example Membership, and give the same group to Gold, Silver and Bronze. Something can then hold only one tag of that group — picking a new one replaces the old.',
      },
      s6: {
        title: 'Add fields (optional)',
        body: 'Click “Add field” when each tagged contact needs its own details, for example a Licence no. for Driver. Give it a “Label” and a type, and tick Required if it must be filled.',
      },
      s7: {
        title: 'Create the tag',
        body: 'Leave “Belongs to” on “Organization-wide” unless the tag is for one entity only, then click “Create tag”. It appears in the list.',
      },
      s8: {
        title: 'Tag one contact',
        body: 'Open the contact in Work → Contacts. In the “Tags” card, click “Add a tag…”, pick the tag, fill in its details if asked and click “Save tags”.',
      },
      s9: {
        title: 'Tag many contacts at once',
        body: 'Back on Library → Tags, click the count button on the tag’s row (for example “0 contacts”). Switch on every contact who should carry it, then click “Save changes”.',
      },
    },
    done: 'The tag chip shows on the contact, and the tag’s row in Library → Tags shows how many contacts carry it.',
    tips: {
      t1: 'Conversation tags are put on chats from the inbox, not from this page.',
      t2: 'A tag’s fields can be used in message templates, so a reminder can mention the driver’s licence number.',
      t3: 'You can mark a tag as never-AI in the AI controls, so contacts with it are always answered by a person.',
    },
    trouble:
      'If you cannot change “Applies to”, the tag is already in use — remove it from everything first. If a contact loses a tag when you add another, both tags are in the same group. If “New tag” is missing, ask an admin for the “Create & edit tags” permission.',
  },
  customFields: {
    title: 'Add your own customer fields',
    summary:
      'Custom fields add the details your business needs to every customer — such as Grade, Preferred day or Needs transport — and let you use them in messages.',
    before:
      'You need the “Manage custom fields” permission (admins have it). Decide the field name and type, for example Preferred day as a dropdown.',
    steps: {
      s1: {
        title: 'Open Settings → Custom fields',
        body: 'Click “Settings” at the bottom of the sidebar. Under “Organization”, choose “Custom fields”. The “Customers” card lists the fields you already have.',
      },
      s2: {
        title: 'Click “Add field”',
        body: 'Click “Add field” on the “Customers” card. A new empty row appears.',
      },
      s3: {
        title: 'Type the label',
        body: 'In “Label”, type what staff will see on the customer form, for example Preferred day.',
      },
      s4: {
        title: 'Choose the type',
        body: 'Open “Type” and pick Text, Number, Yes / No, “Dropdown (pick one)” or “Multi-select (pick many)”. For a dropdown, type each option under “Options” and press Enter after each one.',
      },
      s5: {
        title: 'Check the preview and save',
        body: 'Tick “Required” if the field must always be filled. Look at the preview at the bottom of the card to see how the form will look, then click “Save”. A message “Customer fields saved.” appears.',
      },
      s6: {
        title: 'Fill it in on a contact',
        body: 'Open Work → Contacts and click “Add contact”, or the pencil icon on a contact. Your new field appears in the form — fill it in and save. It then shows in the “Custom fields” panel of the contact’s page.',
      },
      s7: {
        title: 'Use it in a message template',
        body: 'Open Library → “Templates”, tab “Messages”, and click “New quick reply”. Under the message box, the chips under “Your custom fields” insert the field with one click.',
      },
      s8: {
        title: 'Know how the placeholder looks',
        body: 'The inserted placeholder is cf_ followed by the field key, inside double curly brackets. The key is the label in small letters with underscores, so Preferred day becomes cf_preferred_day. When the message is sent it is replaced by the customer’s value.',
      },
    },
    done: '“Customer fields saved.” appears, and the new field shows on the contact form and as a chip in the template editor.',
    tips: {
      t1: 'The AI can read custom field values, so a customer’s Grade can shape its replies.',
      t2: 'Details that apply to only some contacts, such as a driver’s licence number, fit better as a tag field.',
      t3: 'Journeys can check custom fields in a decision step, and a date field can trigger a journey.',
    },
    trouble:
      'If the “Save” button stays grey, nothing has changed yet. If the placeholder stays empty in a sent message, that customer has no value for the field. If you cannot edit the page, ask an admin for the “Manage custom fields” permission.',
  },
  bindings: {
    title: 'Link people, teams, tags and branches in bulk',
    summary:
      'The Bindings page sets many links in one go: which entities a user sees, who is in which team, which contacts carry a tag, and which branch owns each number.',
    before:
      'You need to be an admin. Create the users, teams, tags and entities first — Bindings only links things that already exist.',
    steps: {
      s1: {
        title: 'Open Bindings',
        body: 'In the sidebar, open Admin → “Bindings”. The page has three numbered steps.',
      },
      s2: {
        title: 'Choose what to bind',
        body: 'Under “1 · What to bind”, click a card: “User ↔ entities”, “Team ↔ members”, “Tag ↔ contacts”, “Contact ↔ entities”, “Tag ↔ entities” or “Channel account ↔ entity”. Each card explains what it does.',
      },
      s3: {
        title: 'Pick the one thing on the left',
        body: 'Under “2 · Pick …”, open the list and choose the user, team, tag or account you want to change, for example the user aina.',
      },
      s4: {
        title: 'Switch whole groups on or off',
        body: 'Step 3 shows cards of everything you can link. Tick a card’s checkbox to switch on all of its members, untick it to switch them all off. The pill shows None, “k of N” or “All N”.',
      },
      s5: {
        title: 'Pick one by one',
        body: 'Click “Manage” on a card to switch members on or off one at a time, then click “Apply”. Nothing is saved yet.',
      },
      s6: {
        title: 'Save with the Save bar',
        body: 'An “Unsaved changes” bar appears at the bottom. Click “Save changes” to store everything at once, or “Discard” to undo. A message says how many links were added and removed.',
      },
      s7: {
        title: 'Repeat for the next item',
        body: 'Pick another user, team or tag in step 2 and repeat. If you switch with unsaved changes, you are asked whether to discard them.',
      },
    },
    done: 'The message “Saved — … bound, … unbound.” appears, and the counts on the cards match what you chose.',
    tips: {
      t1: 'For “User ↔ entities”, leaving everything off means the user sees the whole organization.',
      t2: 'A channel account belongs to one entity at a time — binding it to a new one moves it.',
      t3: 'Ticking a main entity includes all the branches beneath it.',
    },
    trouble:
      'If a list says there is nothing to bind, create those items first (users, teams, tags or entities). If a side is marked read-only, your role lacks the permission for that kind. If tagging a contact removed another tag, both tags share a group.',
  },
  apiKeysMcp: {
    title: 'Create an API key and connect an AI app',
    summary:
      'An API key lets a script or an AI app such as Claude or Cursor use ChatBranch as you. Through MCP the app can search contacts, read and answer conversations and more, within your permissions.',
    before:
      'You need the “Manage API keys & MCP” permission (admins have it). Have the AI app installed on your computer. Keep the key secret — anyone with it can act as you.',
    steps: {
      s1: {
        title: 'Open API keys & MCP',
        body: 'In the sidebar, open Admin → “API keys & MCP”. The “API keys” card lists your keys.',
      },
      s2: {
        title: 'Click “Create key”',
        body: 'Click “Create key”. A window “Create an API key” opens. In “Name”, type where you will use it, for example Claude Desktop on my laptop.',
      },
      s3: {
        title: 'Choose the access',
        body: 'Under “Access”, choose “Everything I can do”, or “Only what I pick” and tick only the permissions it needs. A key without any “manage” or “send” permission can only read.',
      },
      s4: {
        title: 'Choose an expiry and create',
        body: 'Pick an “Expiry”, for example “90 days”, then click the create button.',
      },
      s5: {
        title: 'Copy the key now',
        body: 'The window changes to “Copy your new key”. Click “Copy” and keep it somewhere safe, such as a password manager. This is the only time the key is shown.',
      },
      s6: {
        title: 'Pick your AI app',
        body: 'Click “Done” and scroll to “Connect an AI app”. Choose Claude Code, Claude Desktop, Cursor or “Other”. The “MCP endpoint” shown is your server’s address ending in /api/mcp.',
      },
      s7: {
        title: 'Copy the ready-made setup',
        body: 'Your new key is already filled into the example for this visit. Copy the command or config and follow the short hint under it, for example pasting it into the app’s MCP settings and restarting the app.',
      },
      s8: {
        title: 'Test the connection',
        body: 'Paste the key into “Paste an API key (cbk_…)” and click “Test connection”. A line “Connected to … tools available with this key” shows that it works.',
      },
      s9: {
        title: 'Revoke a key you no longer use',
        body: 'On the key’s row, click “Revoke” and confirm. Anything using it stops working at once.',
      },
    },
    done: 'The key shows as Active in the list, and the test says “Connected to …” with the number of tools you can use.',
    tips: {
      t1: 'Make one key per app or computer, so you can revoke one without breaking the others.',
      t2: 'Everything a key does is recorded in Review → Activity with the source API or MCP.',
      t3: 'Over MCP, actions that delete things ask for confirmation first.',
    },
    trouble:
      'If you lost the key, revoke it and create a new one — it cannot be shown again. If the test fails, check that you pasted the whole key starting with cbk_ and that it is not expired or revoked. If the app sees fewer tools than expected, the key or your role lacks those permissions.',
  },
  aiBill: {
    title: 'Read and set up the AI bill',
    summary:
      'The AI bill shows what each company owes for its AI usage each month. Super admins set the currency, markup and monthly fee once in Settings → AI billing.',
    before:
      'Viewing the bill needs “View the AI usage bill” (admins have it). Setting the terms and seeing your real cost needs “Set AI billing markup & see costs” (super admins by default). A company is a main entity; its branches are billed to it.',
    steps: {
      s1: {
        title: 'Open the AI bill',
        body: 'In the sidebar, open Review → “AI bill”. A company admin lands straight on their own company’s statement.',
      },
      s2: {
        title: 'Choose the month',
        body: 'Use the arrows “Previous month” and “Next month” at the top to move between months. The statement shows the period it covers.',
      },
      s3: {
        title: 'Choose the company',
        body: 'If you see several companies, pick one in the “Company” list, or “All companies” for a summary table of every company.',
      },
      s4: {
        title: 'Read the statement',
        body: 'Each line is a kind of AI work, such as Chat replies, Follow-ups or Voice calls, with its requests, tokens and amount. Below come the “Monthly platform fee” and the “Total”. A chart shows daily usage.',
      },
      s5: {
        title: 'Print or save as PDF',
        body: 'Click “Print”. In your browser’s print window choose “Save as PDF” to keep a file, or a printer. The menus are hidden so only the statement prints.',
      },
      s6: {
        title: 'Super admin: open the billing terms',
        body: 'Click “Billing terms” on the bill, or open Settings → Organization → “AI billing”.',
      },
      s7: {
        title: 'Set currency, exchange rate and markup',
        body: 'Type the “Currency (3 letters)”, for example MYR, the exchange rate in “1 USD = how many MYR”, for example 4.70, the “Markup (%)”, for example 30, and the monthly fee. The example line shows what USD 10 of usage becomes.',
      },
      s8: {
        title: 'Set terms for one company (optional)',
        body: 'Under “Per-company terms”, type a different markup or monthly fee for a company. Leave a box empty to use the default. Click “Save billing terms”.',
      },
      s9: {
        title: 'Check cost against what you bill',
        body: 'Back on the bill, billing managers see an extra line with your cost, the markup and your margin. Usage that belongs to no company — skill drafting, knowledge indexing and the operator assistant — is listed separately and never billed.',
      },
    },
    done: 'The statement shows the month’s usage in your currency, with the monthly fee and the total, and prints cleanly.',
    tips: {
      t1: 'Companies only ever see the billed amounts — never your cost or margin.',
      t2: 'Costs come from the model price table, so update prices when your AI provider changes them.',
      t3: 'Print each company’s statement at the start of the month for its invoice.',
    },
    trouble:
      'If a month says “No AI usage this month.”, no AI work was linked to that company. If the company list is empty, create a main entity first. If you cannot see “Billing terms”, you lack the billing manager permission.',
  },
  activityLog: {
    title: 'See who did what in the Activity log',
    summary:
      'The Activity log records every change made by people, API keys, AI apps, the assistant, automations, journeys and webhooks — so you can always check who did what and when.',
    before:
      'You need the “View activity log” permission. Agents and admins have it by default.',
    steps: {
      s1: {
        title: 'Open Activity',
        body: 'In the sidebar, open Review → “Activity”. The newest entries are at the top, with “When”, “Who”, “Action” and “Target”.',
      },
      s2: {
        title: 'Choose a time range',
        body: 'Under “Time range”, click 24 hours, “7 days”, 30 days, 90 days or All time. Choose “Custom” to pick a start and end date.',
      },
      s3: {
        title: 'Search',
        body: 'Type in the search box (“Search target, detail, path, IP, request id…”), for example a contact’s name or a username.',
      },
      s4: {
        title: 'Filter by person, action or source',
        body: 'Click “More filters” to choose an “Actor” (who did it), an “Action” and a “Source” — for example User for a person, MCP for an AI app or Automation for a rule. Use the entity filter to look at one branch.',
      },
      s5: {
        title: 'Open one entry',
        body: 'Click a row to see the details: how it was done, the full detail text, and the request information. Use “More by …” to see everything that person did.',
      },
      s6: {
        title: 'Page through older entries',
        body: 'At the bottom, use “Older” and “Newer” to move between pages, and change how many rows each page shows.',
      },
      s7: {
        title: 'Export to a spreadsheet',
        body: 'Click “Export CSV”. A file with the entries that match your filters downloads, ready to open in Excel or Google Sheets.',
      },
    },
    done: 'The list shows only the entries you filtered for, and the CSV file opens with the same rows.',
    tips: {
      t1: 'Check the Activity log first when a setting changed and nobody remembers why.',
      t2: 'Passwords, message bodies and secret keys are never written to the log.',
      t3: 'Use “Everything in this request” on an entry to see all changes made by one click.',
    },
    trouble:
      'If the page says you need a permission, ask an admin for “View activity log”. If nothing matches, widen the time range or clear the filters. If the export fails, try a shorter time range.',
  },
  orgSettings: {
    title: 'Set organization-wide settings',
    summary:
      'A few settings apply to everyone at once: which kinds of notifications reach people, and the keys that connect ChatBranch to Meta, Google, Shopify and Stripe.',
    before:
      'Notification defaults need “Manage organization notification defaults” (admins have it). App & provider keys are for super admins only. Have the values from Meta, Google, Shopify or Stripe ready before you start.',
    steps: {
      s1: {
        title: 'Open Settings',
        body: 'Click “Settings” at the bottom of the sidebar. The “Organization” section lists the pages that apply to everyone; a lock means your role cannot open that page.',
      },
      s2: {
        title: 'Open Notification defaults',
        body: 'Click “Notification defaults”. You see one switch per kind of alert: Customer messages, Channel health, Flows & automations, Admin & configuration and System.',
      },
      s3: {
        title: 'Mute or unmute a kind of alert',
        body: 'Switch a category off to make it “Muted for everyone” — nobody gets that alert, whatever their own choice. Switch it on for “On for everyone”. Each change is saved straight away.',
      },
      s4: {
        title: 'Open App & provider keys (super admin)',
        body: 'Click “App & provider keys”. The top part holds the “Webhook verify token” shared by all WhatsApp Business numbers.',
      },
      s5: {
        title: 'Set the Meta webhook token',
        body: 'Type a long secret word in “Webhook verify token” and click “Save app keys”. Type exactly the same word in the webhook form in your Meta developer account.',
      },
      s6: {
        title: 'Add Google, Shopify and Stripe keys',
        body: 'Under “Provider keys”, paste the Google client id and secret from your Google Cloud console, and copy the redirect address shown back into Google. Paste the Shopify and Stripe webhook secrets if you use them. Click “Save provider keys”.',
      },
      s7: {
        title: 'Know where the other settings live',
        body: 'Custom fields and AI billing are separate pages in the same section. AI provider keys and models are under the “AI” section in “AI keys & models”.',
      },
    },
    done: 'Each category shows “On for everyone” or “Muted for everyone” as you chose, and saved keys show a “Saved” message and then appear masked.',
    tips: {
      t1: 'Each person can still turn categories off for themselves under Settings → “My notifications”.',
      t2: 'Saved keys show masked; leave a masked box alone to keep its value.',
      t3: 'Once the Google client is saved, everyone can connect their own Google account from Settings → “My connections”.',
    },
    trouble:
      'If a page shows a lock, your role lacks its permission — ask a super admin. If Meta says the webhook cannot be verified, the token on both sides must match exactly. If Google sign-in fails, check that the redirect address in Google is copied exactly.',
  },
  testLab: {
    title: 'Check everything works with the Test lab',
    summary:
      'The Test lab runs about forty ready-made scenarios — AI replies, handovers, routing, opt-outs, bookings and more — through the real system, and tells you which ones pass.',
    before:
      'Only a super admin can open the Test lab. It works inside a fenced “Test lab” entity and never touches your own customers. Use it on a test or demo server, never on a live server — there it should be switched off (TEST_LAB=0).',
    steps: {
      s1: {
        title: 'Open the Test lab',
        body: 'In the sidebar, open Review → “Test lab”. You see the scenarios grouped by category, with a status line saying whether a real AI key or the test brain will be used.',
      },
      s2: {
        title: 'Run every scenario',
        body: 'Click “Run all”. A progress line shows which scenario is running. Click “Stop after this one” to stop early.',
      },
      s3: {
        title: 'Or run only some',
        body: 'Tick the scenarios you want, or “Select all in …” for a whole category, then click “Run selected”. Click “Run” on one scenario to run just that one.',
      },
      s4: {
        title: 'Read the results',
        body: 'Each scenario shows Passed, Failed or Skipped and how many checks passed. Click “Details” to see the steps, what was expected, each check and any error.',
      },
      s5: {
        title: 'Open what a scenario made',
        body: 'In the details, use the links under “Open what it made” to look at the test conversation, contact or draft in the normal dashboard.',
      },
      s6: {
        title: 'Load demo data',
        body: 'Click “Load demo data” to fill the Test lab entity with sample contacts, conversations and bookings you can click through — handy for training new staff.',
      },
      s7: {
        title: 'Reset the test data',
        body: 'Click “Reset test data”, then “Delete test data” to confirm. Everything inside the Test lab entity is removed; nothing outside it is touched and the run history is kept.',
      },
    },
    done: 'The finish line reads “Finished: … passed, … failed, … skipped.”, and the run appears under “Run history”.',
    tips: {
      t1: 'Run all scenarios after changing settings or upgrading, to catch problems early.',
      t2: 'Without an AI key the lab uses a predictable test brain, so results do not depend on a model.',
      t3: 'Developers can run the same scenarios from a terminal with npm run test:scenarios.',
    },
    trouble:
      'If the page says the Test lab is switched off, the server runs with TEST_LAB=0 — that is correct for a live server. If a scenario fails, open “Details” to see which check failed and why. If “Load demo data” says it is already loaded, reset the test data first.',
  },
  requireTwoStep: {
    title: 'Require two-step sign-in',
    summary: 'Make everyone — or just admins — sign in with a code from an authenticator app. Anyone who hasn’t set it up is taken to the setup screen before they can do anything else.',
    before: 'You need to be a super admin (or hold “Manage sign-in security”). Set up two-step sign-in for yourself first (Settings → My account) — the rule covers you too.',
    steps: {
      s1: {
        title: 'Open Settings → Security',
        body: 'Click “Settings” at the bottom of the sidebar, then “Security” under “Organization”.',
      },
      s2: {
        title: 'Choose who must use it',
        body: 'Pick “Admins and super admins” or “Everyone”. The card shows how many people already use it.',
      },
      s3: {
        title: 'Click “Save”',
        body: 'From now on, anyone the rule covers who hasn’t set it up is taken to “Set up two-step sign-in” the next time they use ChatBranch.',
      },
      s4: {
        title: 'Help anyone who lost their phone',
        body: 'Open Admin → “Users & roles” and click the shield button on their row (“Reset two-step sign-in”). They sign in with just their password and set it up again.',
      },
    },
    done: 'Settings → Security shows your choice and how many people use two-step sign-in.',
    tips: {
      t1: 'API keys and the MCP server are not affected — they never sign in with a password.',
      t2: 'To go back, choose “Nobody has to”; people who turned it on themselves keep it.',
    },
    trouble: 'No “Security” in Settings: only super admins (or a role given “Manage sign-in security”) see it. Asked to set it up right after saving: the rule applies to you too — scan the code and carry on.',
  },
}
