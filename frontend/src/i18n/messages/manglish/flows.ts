// Manglish overlay for the `flows` namespace (see ./index.ts): only the strings it
// rewrites — every other key falls back to English.
import type { Messages } from '../en'
import type { Overlay } from '../../types'

export const flows: Overlay<Messages['flows']> = {
  // ---- hints under the trigger / decision / step choices (the labels stay English) ----
  trigger: {
    contactCreatedHint: 'New contacts from anywhere, once the journey is live',
    tagAddedHint: 'As soon as a contact gets the tag you choose',
    messageReceivedHint: 'A customer messages in — can limit it to a keyword if you want',
    scheduledHint: 'Every day at a fixed time — or only on some weekdays',
  },
  decision: {
    repliedHint: 'A reply came in during the last wait',
    fieldEqualsHint: 'A custom field matches what you type',
  },
  stepChoice: {
    aiHint: 'The AI writes the message for you, following a skill',
    followupHint: 'Chase them again, up to 5 times, until they answer',
    handoverHint: 'Pass the chat over to a team',
    endHint: 'Close it off with an outcome',
  },

  // ---- the builder shell (FlowBuilder) ----
  builder: {
    viewOnly: 'View only — you can see this journey and its runs, but to change journeys you need the “Build & edit flows” permission.',
    outsideYourEntities: 'This journey runs for {name}, not under your own entities — can look, cannot change. Use ··· → Duplicate to make your own copy.',
    runsForHint: 'Each entity brings its branches along. A live journey starts counting fresh for its new audience — so nothing back-fires one.',
    listSubtitle: 'All your journeys in one place — open one to change the steps, go live or see how it’s running.',
    liveTitle: 'Live — this journey is running now. Click to pause.',
    pausedTitle: 'Paused — still a draft. Click to go live.',
    importNotAFlow: 'Eh, that file isn’t a journey exported from here.',
    importFailed: 'Alamak, import failed',
    loadingFlows: 'Loading journeys, wait ah…',
    gallerySubtitle: 'Pick a starter and tweak the words — or start from a blank one.',
    deleteStepTitle: 'Delete this step ah?',
    deleteStepBodyYes_one:
      'This step got a second path. Delete it and the YES path stays, but the {count} step that only the other path reaches will go too.',
    deleteStepBodyYes_other:
      'This step got a second path. Delete it and the YES path stays, but the {count} steps that only the other path reaches will go too.',
    deleteStepBodyReplied_one:
      'This step got a second path. Delete it and the Replied path stays, but the {count} step that only the other path reaches will go too.',
    deleteStepBodyReplied_other:
      'This step got a second path. Delete it and the Replied path stays, but the {count} steps that only the other path reaches will go too.',
    deleteFlowTitle: 'Confirm delete “{name}”?',
    deleteFlowBody: 'The journey will stop running and disappear for everyone. Don’t worry, its past runs stay in the records.',
    emptyBody:
      'A journey starts when something happens — a contact gets tagged, a message comes in, a date comes round — then goes through the steps you choose: send a message (or let the AI write it), wait for a reply, branch on the answer, chase them when nobody answers, and pass it to your team when a real person is needed. Every step shows how many people reached it, so you can see for yourself it’s working.',
  },
  gallery: {
    blankHint: 'Start with only a trigger and build it up step by step.',
  },
  addStep: {
    subtitle: 'It goes exactly where you clicked the +, no need to move it.',
    endBlocked: 'End can only go where the path already stops lah.',
  },
  stepsView: {
    noStartBody: 'Every journey starts with a “When…” step. This one got none, so it will never run.',
    notConnectedBody: 'No path reaches these steps, so they’ll never run. Delete them, or connect them up in the Advanced canvas.',
  },
  canvas: {
    autoLayoutTitle: 'Line up every step nicely, top to bottom',
    fitTitle: 'Show the whole journey on one screen',
  },
  edge: {
    decimalsHint: 'Decimals also can: 0.5 = 12 hours.',
    preferWait: 'For new journeys, better use a {wait} step — does the same thing, but easier to read in the Steps view.',
    movingDashesHint: 'Don’t overdo it — e.g. just to highlight the main path.',
  },

  // ---- the step editor (ConfigPanel) ----
  config: {
    legacySection: 'This is a legacy section from an older journey. It doesn’t do anything already — runs just pass straight through. Can delete it; the steps inside will stay.',
    nameHelp: 'Give this step your own label — it shows under the summary.',
    legacyTriggerNotice:
      'This starting point is from an older version. It still runs, but we don’t offer it anymore — pick one of the new ones to bring the journey up to date.',
    tagHelp: 'Each tagged contact enters once only, as soon as the tag goes on.',
    channelOnHelp: 'Which channel the message must come in on.',
    containsHelp: 'Separate the words with commas — any one of them also can match. Leave empty to catch every message.',
    dateFieldHelp: 'A customer custom field that keeps a date (YYYY-MM-DD).',
    bookingLeadHelp: 'How long before the booking starts — follows the booking’s own timezone.',
    bookingNotice: 'Each booking gets its own run. If a booking is moved, the reminder follows the new time; if it’s cancelled, the journey stops. A booking made with less time than this before it starts won’t get one.',
    bookingCalendarHelp: 'Only bookings made with this calendar skill. Leave it on Any calendar to cover every booking.',
    everyYearHelp: 'Only the day and month must match — good for birthdays, anniversaries and renewals.',
    timeHelp: 'Follows the organization’s time. Contacts enter once a day at this time.',
    onTheseDaysHelp: 'Leave it empty to run every day.',
    scheduleEveryone: 'Careful ah — a schedule with no audience filter runs for every contact, every day. Narrow it down below.',
    pickAtLeastOne: 'Pick at least one — if the list is empty, it means everyone.',
    noDateFields: 'No customer date fields yet leh — add one under Settings → Custom fields.',
    manualHelp: 'The journey must be saved and live first. Integrations use {endpoint}.',
    startedSkipped_one: 'Started for {started} · {count} was already inside.',
    startedSkipped_other: 'Started for {started} · {count} were already inside.',
    sendOnHelp: 'Pick all the channels this message should go out on.',
    sendOnHelpAi: 'Leave it empty to reply on whichever channel they last chatted on.',
    sendToPrimaryHelp: 'Just the phone number / email on the contact.',
    sendToAllHelp: 'Every WhatsApp number and email address linked to the contact — duplicates taken out.',
    humanizeHelp: 'Pauses a bit like it’s typing, and breaks long text into a few shorter messages.',
    mixedNotice: 'This older step mixes a few kinds of channel. It still runs as saved — just edit the channels here.',
    messageTemplateHelp: 'For the other channels — WhatsApp will send the template.',
    skillHelp: 'Which one of your AI skills writes this message.',
    aiBriefHelp: 'A short brief for the AI. It writes the real words itself, in the contact’s language, using what it knows about them.',
    scriptHelp: 'You can manage scripts under Templates → Call scripts.',
    actionHelp:
      'Uses the connection under Integrations. If nothing is connected, the step just logs a skip and the journey carries on.',
    eventTitleHelp: 'If empty, it takes the text below.',
    eventStartHelp: 'An ISO date-time (2026-08-01T10:00). Leave empty = tomorrow, same time.',
    calendarIdHelp: 'Leave empty = the connected account’s default calendar.',
    fileNameHelp: 'The text below goes inside the file.',
    spreadsheetIdHelp: 'Leave empty = use the one set on the integration.',
    newStatusHelp: 'The contact’s primary record will move to this status.',
    giveUpAfterHelp: 'Leave empty to wait however long it takes.',
    deliveryHint: 'Goes out on {channels}. Want to change the channel, who gets it or the typing style? Do it here.',
    giveUpHint: 'Stops waiting after {duration}.',
    giveUpNeverHint: 'Waits however long it takes.',
    waitMovesOn: 'Reply or not, the journey still moves on. Add an {step} step next if you want to do something different when no reply comes.',
    followUpBriefHelp: 'The AI writes a fresh nudge every time, and knows how many it has sent already.',
    followUpMessageHelp: 'Same message every time.',
    theyReplyHelp: 'Always on — any reply stops the follow-ups.',
    quietHoursNotice: 'Follow-ups never go out during quiet hours — they’ll wait until the window opens.',
    anyOfWordsHelp: 'Separate with commas — capital or small letters also can.',
    equalsHelp: 'Capital or small letters, same thing. Leave empty for “is filled in”.',
    valueHelp: 'Can use {{variables}} also, e.g. {{first_name}}.',
    looksAtReply: 'Checks the reply that came in during the last Wait (or the message that started the journey).',
    yesNoPaths: 'YES and NO each get their own path — build them side by side in the Steps view. A path with no steps just ends the journey there.',
    chatbotOffHint: 'Your team takes over from here',
    chatbotOnHint: 'The AI handles their chats again',
    handOverToHelp: 'You manage teams under Teams.',
    handoverNotice:
      'Their latest conversation goes to the team, the AI stops replying there, and a private note explains why. The team gets notified; for High and Urgent, the admins get alerted too.',
    handoverNoticeKeepsAi:
      'Their latest conversation goes to the team and a private note explains why — meanwhile the AI still replies there. The team gets notified; for High and Urgent, the admins get alerted too.',
    aiKeepsAnswering: 'AI still replies while the team gets notified',
    aiKeepsAnsweringHelp: 'Off: the AI stops replying in that chat until somebody lets it continue.',
    chatbotNotice:
      'It flips the same AI switch you see in the chat header. But the AI master switch, a contact set to “never”, never-AI tags and opt-outs still get the final say.',
    outcomeHelp: 'A short code shown on every finished run, like BOOKED or NO_REPLY.',
    rememberOutcomeHelp: 'Adds a line to the contact’s AI memory, so later chats and calls know how this one ended.',
  },
  contactPicker: {
    searching: 'Searching, wait ah…',
    unavailable: 'Cannot search contacts now — {error}',
    showingOf: 'Showing {shown} of {count} — type more to narrow it down',
  },
  skillPicker: {
    loading: 'Loading skills, wait ah…',
    savedByName: 'An older journey saved this by name (“{name}”) — pick it again so its id gets stored.',
    noSkills: 'No skills yet leh.',
    shapes: 'The skill’s instructions decide how the AI writes.',
  },
  waTemplate: {
    helpText:
      'Free text only goes through inside Meta’s 24-hour window (after the customer last wrote). Scheduled messages usually need a template lah.',
    helpTemplate: 'An approved template can go out any time, no problem.',
    helpAuto: 'Sends the text while the 24-hour window is still open, and this template once it’s closed.',
    loading: 'Loading templates, wait ah…',
    notInList: '{name} — not in the current template list leh',
    onlyApproved: 'Only APPROVED templates can go out — this one is still {status}.',
    none: 'No templates yet — go create one under Templates.',
    sandbox: 'Sandbox — no WhatsApp account linked yet, so these are just the local demo templates.',
  },
  runs: {
    aiWaiting: 'AI not sure — waiting for the next reply to ask again ({n} of {max})',
    aiTimedOut: 'No new reply came in time → {then}',
    aiEnded: 'AI not sure — so the journey ended here',
    noNumbers: 'No numbers yet leh. Once the journey is saved and live, every step will count the people who reach it, day by day.',
    dropExplainer:
      'Drop = of the people who finished the step before, the share that never made it here (on a branch: the share that went the other way instead). People still waiting don’t count as dropped.',
    noOne: 'Nobody matches in this period.',
    runUnavailable: 'This run isn’t available leh.',
    noEvents: 'No step events recorded for this run — it’s from before tracking started.',
  },

  // ---- Automations (/automations, AutomationsSettings) ----
  automations: {
    testIntro: 'Pick a real conversation and see if this rule would fire on it — don’t worry, nothing gets sent or changed.',
    testInactive: 'This rule is turned off, so it won’t really run until you turn it on ah.',
    testNoConditions: 'No conditions, so it fires on every event of this kind.',
    orderHint: 'Rules run from top to bottom. To change the order, drag a rule or use the arrows.',
    subtitle: 'Something happens to a conversation? Then do that.',
    intro:
      '{when} something happens to a conversation, {if} the conditions hold, {do} these actions. Every rule that matches will run, in order — not just the first one. For contact sequences that go over days (reminders, follow-ups), better use {flowBuilder}.',
    unavailable: 'Cannot load automations — is the backend API running or not?',
    unavailableError: 'Cannot load automations — {error}',
    empty: 'No rules yet — try something like “when a message contains {word}, label it and assign the billing team”.',
    confirmDelete: 'Delete rule “{name}” ah? Conversations it already touched won’t be affected.',
    ruleCreated: 'Rule {name} created — all set.',
    ruleUpdated: 'Rule {name} updated liao.',
    ruleDeleted: 'Rule {name} deleted, done.',
    ifHint: '(all must hold — got none = always)',
    noConditions: 'No conditions, so this rule fires every time {event}.',
    noActions: 'A rule with no actions won’t do anything — add at least one lah.',
    pickAssignee: 'Before saving, pick the team or agent for each “Assign to …” action first.',
    activeHint: '— off means it won’t run at all',
  },

  // ---- the decision step editor (DecisionEditor) ----
  decisionEditor: {
    legacyNotice: 'This step uses an older check: {label}. It still works like before — pick a way to decide above to shift it to the new editor.',
    convertedNotice: 'Saved with the older “{label}” check — shown here as the same condition. Once you change it, it’ll be stored the new way.',
    conditionsPaths: 'YES when the conditions hold, NO if not — each one gets its own path. Conditions are checked straight away; nothing waits.',
    questionHelp: 'Just ask in plain words. The AI answers YES or NO from the conversation — or tells you it’s not sure.',
    questionHelpPick: 'Tell the AI what to decide. It’ll pick the path that fits best, or “None of these”.',
    optionsHelp: 'Two to six. Each one gets its own path — a short description helps the AI choose better.',
    optionDescriptionsHelp: 'Optional hints so the AI can tell the paths apart.',
    historyMessagesHelp: 'From 5 to 100. Private notes never go in.',
    sinceEnteredHelp: 'Older messages won’t count.',
    thresholdHelp: 'If the AI is less sure than this, the answer counts as “unsure”.',
    whenUnsureHelp: 'Same thing happens when the AI is switched off for this outlet or cannot be reached.',
    waitHoursHelp: '0 = no limit. If no reply comes in time, it takes the NO / “None of these” path.',
    aiNotice: 'The AI only decides — it never messages the customer. If no AI model is set up yet, it makes a simple keyword guess (sandbox), and the Runs panel marks it as one.',
    aiPickNotice: 'Each path gets its own column. The AI only decides — it won’t message the customer. If no AI model is set up yet, it just makes a simple keyword guess (sandbox).',
  },

  // ---- unfinished-step notes (lib/flow/validate) ----
  issues: {
    noTrigger: 'This journey got no starting point yet (When…).',
    chooseTriggerTag: 'Choose the tag that kicks off the journey.',
    chooseDateField: 'Choose the date field first.',
    bookingLead: 'Set how long before the booking — whole number only, at least 1.',
    typeAudienceTag: 'Type the tag that lets contacts in — if empty, everyone gets in.',
    pickContacts: 'Pick at least one contact lah — an empty list means everyone.',
    nothingAfter: 'After this, nothing happens already — add a step.',
    pickChannel: 'Pick at least one channel.',
    writeMessage: 'Message still empty — write something.',
    chooseWaTemplate: 'Choose which WhatsApp template to send.',
    chooseScript: 'Choose which script to read.',
    pickFollowUpChannel: 'Pick which channel the follow-ups go out on.',
    writeFollowUp: 'Follow-up message still empty — write one.',
    listWords: 'List down the words to look for.',
    chooseTag: 'Choose the tag first.',
    chooseField: 'Choose the field first.',
    chooseOnOff: 'On or off? Choose one.',
    pickChatbotChannel: 'Pick at least one channel, or just apply it to all chats.',
    addCondition: 'Need at least one condition here.',
    finishCondition: 'Some conditions still need a value — finish them up.',
    writeQuestion: 'Write down the question for the AI.',
    addOptions: 'Need at least two paths with names.',
  },
}
