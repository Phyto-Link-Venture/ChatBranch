// Manglish overlay for the `ai` namespace (see ./index.ts): only the strings it
// rewrites — every other key falls back to English.
import type { Messages } from '../en'
import type { Overlay } from '../../types'

export const ai: Overlay<Messages['ai']> = {
  common: {
    savedFor: 'Saved for {name} liao.',
    needsBehaviourPermission: 'To change this, you need the “Edit AI behaviour settings” permission.',
  },
  pages: {
    personaSubtitle: 'How the AI introduces itself, plus the rules every reply must follow — set per outlet',
    skillsSubtitle: 'What the AI follows, the tools it calls when the answer must be exact, and the checks every reply kena pass',
    knowledgeSubtitle: 'Everything the AI knows about your business — facts, policies, FAQs and notices, all in one place',
  },

  // ---- persona & house rules (/ai-persona) ----------------------------------
  persona: {
    editingFor: 'Now editing for {level}.',
    switcherHint: 'Want another outlet? Pick it in the switcher at the top — or All entities for the whole organization.',
    readOnly: 'You can see these, but cannot change them ah.',
    personaHelp: 'Who the AI is and how it talks: your business, its tone, what it helps with. Only one persona applies — the nearest level that has one wins.',
    ownOn: 'This outlet uses its own persona — the one above won’t apply here.',
    ownOff: 'Now following the persona from {source}.',
    inheritedFrom: 'Coming from {source} — switch on “own persona” if you want to change it here.',
    rulesHelp: 'Rules every reply must follow, like “Always be polite” or “Never quote a delivery date”. They stack up: an outlet keeps all the rules above it and can add its own also.',
    onePerLine: 'One rule per line ah.',
    safetyHelp: 'These always come first in every prompt, for every outlet. Personas and house rules sit below them and cannot remove them. Keep the honesty, privacy and hand-off rules — they stop the AI from simply making up prices or leaking data.',
    safetyLocked: 'Sorry, only a superadmin (or a role given “Edit the AI’s safety rules”) can change these.',
    safetyWarn: 'Careful ah — this changes every AI reply in the whole organization.',
    previewHelp: 'The start of every prompt at this level: safety rules first, then house rules, then the persona. Knowledge, skills and lessons come after.',
  },

  // ---- AI monitor ------------------------------------------------------------
  monitor: {
    subtitle: 'Check what the AI says before it reaches your customers.',
    rightNow: 'At the moment, {mode}.',
    rightNowEntity: 'At the moment, {mode} ({entity}).',
    noAccess:
      'Sorry ah, the AI monitor is not open for your account. Ask an admin for the “See & approve AI drafts” permission.',
    mode: {
      auto: 'the AI sends straight away, no waiting',
      reviewFlagged: 'only the risky replies get held',
      reviewAll: 'every AI reply waits for a person to check',
      shadow: 'shadow mode — the AI only drafts, never sends',
      withAutosend: '{mode}, and if nobody acts in {minutes} min, it sends by itself',
    },
  },
  draft: {
    touch: {
      reactionOnly: 'Just a reaction — no text will go out.',
      voiceText: 'Went out as text — couldn’t make the voice note',
    },
    blockedTitle: 'This reply kena blocked by the assistant',
    blockedReasonTitle: 'This reply kena blocked by the assistant: {reason}',
    handoverAskTitle: 'The assistant wants a person to step in',
    handoverAskReasonTitle: 'The assistant wants a person to step in: {reason}',
    retriesTitle: 'A check didn’t pass, so the AI rewrote its answer',
    handoverTitle: 'The AI asked for a person to take over from here',
    templateTitle: 'The 24-hour window already closed, so it went out as an approved WhatsApp template',
    ackTitle: 'The customer only acknowledged — so this is just the short acknowledgement text',
    approvingCancels: '— approve and it gets cancelled',
    approvingBooks: '— approve and it gets booked',
    countdownTitle: 'Nobody touched it yet — once the deadline hits, it sends by itself',
    sending: 'Sending, wait ah…',
    sendsIn: 'Sends by itself in {time}',
  },
  review: {
    rejectedNotice: 'Okay, the reply to {name} is rejected.',
    approvedScheduled: 'Reply to {name} approved — {detail}',
    sentNotice: 'Sent to {name} liao.',
    notSentNotice: 'Couldn’t send to {name}: {detail}',
    bulkApproved: '{ok} of {total} approved liao.',
    bulkRejected: '{ok} of {total} rejected.',
    bulkNeedLook_one: '{count} still needs you to check: {detail}',
    bulkNeedLook_other: '{count} still need you to check: {detail}',
    loadError: 'Aiyo, cannot load the review queue: {error}',
    nothingWaiting: 'Nothing waiting now.',
    waiting_one: '{count} reply waiting for you, oldest on top.',
    waiting_other: '{count} replies waiting for you, oldest on top.',
    emptyTitle: 'All settled liao',
    emptyBody: 'When the AI wants a person to check a reply, it shows up here first — before the customer sees it.',
    currentRule: 'Now the rule is: {mode}.',
    superseded: 'Eh, a teammate already replied in this chat after the AI wrote this.',
    reasonPlaceholder: 'Write in your own words — helps the AI get better',
  },
  activity: {
    empty:
      'No AI activity matches leh. Everything the AI writes — sent, held, rejected or stopped by a guard — shows up here.',
    followUpQuiet: 'Nothing — the customer just went quiet.',
    noCustomerMessage: 'No customer message got recorded.',
    signals: 'Spotted these, but didn’t hold it: {flags}',
  },
  stats: {
    noneYet: 'nothing yet',
    noDecisions: 'nobody decided yet',
    nothingHeld: 'Nothing got held in this period.',
    noRejections: 'No rejections in this period. When reviewers reject with a reason, it shows up here as feedback.',
    shadowNote_one: '{count} shadow draft written but not sent in this period.',
    shadowNote_other: '{count} shadow drafts written but not sent in this period.',
  },

  // ---- AI behaviour settings ---------------------------------------------------
  behaviour: {
    depends: {
      held: 'Only matters when replies are held for review.',
      risky: 'Only matters in “Review risky replies”.',
      wait: 'Only matters when the wait is on.',
      ack: 'Only matters when acknowledgements are skipped.',
      typing: 'Only matters when typing is on.',
      split: 'Only matters when splitting is on.',
      followUps: 'Only matters when follow-ups are on.',
      voice: 'Only matters when voice replies are on.',
    },
    savedOrg: 'Organization defaults saved liao.',
    backToDefault: 'Okay, back to the organization default.',
    overridesRemoved: 'Done — all overrides for this entity removed.',
    discardConfirm: 'Throw away your unsaved changes ah?',
    overridesHere_one: '{count} setting here is different from the organization default. The rest all follow the default.',
    overridesHere_other: '{count} settings here are different from the organization default. The rest all follow the default.',
    inheritsAll: 'This entity follows every organization default. Change a value to override it for this entity only.',
    appliesEverywhere: 'These apply everywhere, unless an entity sets its own.',
    loadError: 'Alamak, couldn’t load the settings: {error}',
    readOnly: 'You can see these rules, but to change them you need the “Edit AI behaviour settings” permission.',
    groups: {
      human: {
        description: 'Emoji, emoji reactions and quoted replies — those little touches, like a real person texting on the phone.',
      },
      review: {
        description: 'Choose which AI messages a person must check first, before they reach the customer.',
      },
      wait: {
        description: 'Customer sends many short messages in a row? Answer them once — and just let a simple acknowledgement be.',
      },
      typing: {
        description: 'Show typing where the channel can, and pause longer for longer replies.',
      },
      split: {
        description: 'Long answers go out as a few shorter messages, like how a person would send them.',
      },
      followup: {
        description: 'A gentle nudge when the AI asked something but got nothing back.',
      },
      learning: {
        description: 'Thumbs up/down, approvals and edits become lessons the AI uses in later replies.',
      },
    },
    fields: {
      emojiStyle: {
        help:
          'Off: no emoji at all. Light: once in a while, where a person would use one — max one per message, never in a complaint or an apology, and less if the customer doesn’t use any. Expressive: warmer, a bit more often. Email never gets emoji, and if the persona says otherwise, the persona wins.',
      },
      reactions: {
        help:
          'On WhatsApp and Telegram the AI can react to the customer’s latest message — 👍 on “ok”, 🙏 on “thanks”, ❤️ on a photo — max one reaction per reply. For a simple acknowledgement, a reaction alone can answer instead of keeping silent. It is added when the reply goes out, never while it is held.',
      },
      quoteReplies: {
        help:
          'Auto: if the customer sent a few messages at once, or the answer is about an older message, the reply quotes the message it’s answering (WhatsApp and Telegram). Off: never quote.',
      },
      voiceReplies: {
        help:
          'Needs an ElevenLabs API key or a VoiceStudio server first (Settings → AI keys & models). Mirror: if the customer’s latest message was a voice note, the AI answers with one too. Always: every reply is a voice note. The words still stay on the message for the team. Works on WhatsApp (QR numbers, Cloud API, YCloud), Telegram, Messenger and Instagram; anywhere else — or when a reply has a link, is very long, or the voice cannot be made — it goes out as text.',
      },
      voiceId: {
        help:
          'Optional. Put a voice-library id (like your clone — ElevenLabs or VoiceStudio) or a raw ElevenLabs voice ID. Leave empty and it uses the organization’s call voice if that one is ElevenLabs or VoiceStudio, else the default voice of whichever one is set up.',
      },
      reviewMode: {
        help:
          'Send straight away, hold only the replies that look risky, hold everything for a person, or shadow mode (the AI writes drafts but never sends — good for trying it out safely).',
      },
      autosendMinutes: {
        help:
          '0 means a held reply waits for a person. Put any other number and it sends by itself once that many minutes pass and nobody acts.',
      },
      firstN: {
        help:
          'In “review risky replies” mode, the first N AI replies of every conversation are held, so a person can see how the AI opens.',
      },
      keywords: {
        help:
          'Separate them with commas. If a reply mentions one of these — or a money amount or a date — it gets held in “review risky replies” mode.',
      },
      notify: {
        help:
          'A bell notification goes to the conversation’s assignee and the admins — one unread alert per person, refreshed by newer held replies, so it won’t spam anybody.',
      },
      waitSeconds: {
        help:
          'Wait this long after the customer’s last message before writing, so a few short messages get one answer. Every new message restarts the wait. 0 replies at once.',
      },
      waitMaxSeconds: {
        help: 'No matter how many messages keep coming in, the reply is written by this time at the latest.',
      },
      ackSkip: {
        help:
          'When the customer only says “ok”, “thanks” or sends a thumbs-up after an answer, the AI just keeps quiet instead of replying.',
      },
      ackText: {
        help:
          'If you set this, it is sent instead of a full reply to an acknowledgement (for example “Noted, thanks!”). Leave blank to keep silent — or to react with an emoji if reactions are on.',
      },
      typing: {
        help:
          'Shows “typing…” where the channel can (WhatsApp) and waits longer for longer replies.',
      },
      typingCps: {
        help: 'Higher means faster. 8 feels like someone who types quite fast.',
      },
      split: {
        help:
          'Off sends one message. Auto splits only the replies longer than the limit. Always splits at every paragraph break. Never more than 3 messages, and never cut mid-sentence.',
      },
      followUp: {
        help:
          'Only after the AI’s last message asked something or left the conversation open. Stops right away once the customer replies, opts out, the conversation is resolved or a person takes over.',
      },
      followUpTemplate: {
        help:
          'WhatsApp only allows free text within 24 hours of the customer’s last message. After that, a follow-up uses this approved template — or if none is set, it’s skipped (and noted in the chat).',
      },
      followUpGuidance: {
        help:
          'Optional — tell the AI how follow-ups should sound, e.g. “Keep it to one friendly line and offer to call”.',
      },
      learningMode: {
        help:
          'Thumbs up/down, approvals and edits suggest lessons the AI uses in later replies. Review: every lesson waits for a person to approve it. Auto: lessons from admins’ feedback go live straight away (everyone else’s still wait). Off: feedback is only counted, nothing is learned.',
      },
    },
  },
  example: {
    subtitle: 'A sample chat using the settings on screen — it changes as you change them.',
    oneReply_one: 'Looks like the customer is done — the AI writes ONE reply for {count} message',
    oneReply_other: 'Looks like the customer is done — the AI writes ONE reply for {count} messages',
    startsWriting: 'Now the AI starts writing',
    everyReviewed: 'every reply gets reviewed',
    shadow: 'Shadow mode: the draft is only kept in the AI monitor, never sent',
    autoAfter: 'Nobody did anything for {minutes} min, so it goes out by itself',
    waitsApproval: 'Waits for a person to approve it (here we show it as if approved straight away)',
    ackNoReply: 'Just an acknowledgement — no need to reply',
    ackReaction: 'The AI just reacts {emoji}, no reply',
    repliesToAck: 'The AI replies to the acknowledgement also',
    ifNoAnswer: 'If the AI asks something and the customer keeps quiet',
    followUpsOff: 'Follow-ups are off — so the conversation just waits.',
    noFollowUps: 'No follow-ups (the limit is set to 0).',
    thenStops: 'Then it stops. Any reply, an opt-out, a teammate jumping in or quiet hours will change the plan.',
  },

  // ---- AI learning -------------------------------------------------------------
  learning: {
    modes: {
      reviewHelp: 'Ratings and edits turn into suggested lessons. A person approves each one first before the AI uses it.',
      autoHelp: 'Feedback from an admin becomes a live lesson straight away. Everyone else’s still needs review first.',
      offHelp: 'Ratings still count in the stats, but the AI won’t learn anything from them.',
    },
    savedOrgWide: 'Saved for the whole organization liao.',
    howLearnsBody:
      'Every thumbs-up, thumbs-down, edit and rejection is recorded. Lessons from them go into the AI’s instructions as examples from past conversations — but your hard rules still win.',
    modeUnavailable: 'This server doesn’t have the learning mode yet leh.',
    reindexing_one: 'Re-indexing {count} item behind the scenes.',
    reindexing_other: 'Re-indexing {count} items behind the scenes.',
    reindexingBg: 'Re-indexing behind the scenes.',
    checking: 'Checking, wait ah…',
    retrievalUnavailable: 'This server can’t show the retrieval status yet. For now the AI matches knowledge by keywords.',
    semanticBody: 'Knowledge and lessons are matched by meaning, so even if the question is asked differently, the AI still finds the right answer.',
    keywordBody:
      'Add an OpenAI, Gemini, OpenRouter or local embedding provider in Settings → AI keys & models, then it can match by meaning, not just words.',
    lastError: 'Last time, indexing got error: {error}',
    deleteConfirm: 'Confirm delete lesson #{id} for good? Cannot undo ah.',
    toKnowledge: 'Done — lesson #{id} is now a knowledge base FAQ.',
    toKnowledgeId: 'Done — lesson #{id} is now a knowledge base FAQ (#{kid}).',
    deleted: 'Lesson #{id} deleted liao.',
    live: 'Lesson #{id} is live — the AI will use it from the next reply onwards.',
    dismissed: 'Lesson #{id} dismissed.',
    archived: 'Lesson #{id} archived — the AI won’t use it anymore.',
    savedLive: 'Lesson #{id} saved and live liao.',
    saved: 'Lesson #{id} saved.',
    unavailableTitle: 'Learning not switched on here yet',
    unavailableBody:
      'This server doesn’t keep lessons yet. Once it does, ratings on AI replies and the edits reviewers make will turn into lessons and show up here.',
    loadError: 'Aiyo, cannot load the lessons: {error}',
    loadingLessons: 'Loading lessons, wait ah…',
    emptyNoMatch: 'No lessons match leh',
    emptySuggested: 'No suggestions waiting now',
    emptyActive: 'The AI still hasn’t learned anything',
    emptyArchived: 'Nothing archived yet',
    emptyDismissed: 'Nothing dismissed yet',
    tryAnother: 'Try another word or kind lah.',
    suggestedBody:
      'Rate AI replies with the thumbs in Chats, or here in the AI monitor — a thumbs-down with a correction, a reply a reviewer edited, or a thumbs-up on a good answer will become a suggested lesson.',
    activeBody:
      'Approve a suggestion, or add a rule yourself. Active lessons go into the AI’s instructions when a customer asks something similar.',
    archivedBody: 'Lessons you archive or dismiss stay here, so you can bring them back anytime.',
  },
  lesson: {
    fromRatingsTitle: 'How many ratings or edits led to this lesson',
    notUsed: 'Not used in any reply yet',
  },
  addLesson: {
    ruleAdded: 'Rule added liao — the AI follows it from the next reply.',
    exampleAdded: 'Example added — the AI will use it from the next reply.',
    subtitle: 'Rules always go into the AI’s instructions. Examples only come in when a customer asks something similar.',
    onlyThisEntity: 'Only this entity’s conversations will use it.',
    everyInherits: 'Every entity gets it too.',
  },
  addAnswer: {
    subtitle: 'It goes into the knowledge base as an FAQ. The AI uses it from the next message onwards.',
  },
  feedback: {
    goodReplyQ: 'This reply okay or not?',
    popTitle: 'What’s wrong with this reply ah?',
    correctionPlaceholder: 'Write the reply you would send',
    correctionHelp: 'Optional. Once approved, your correction becomes a lesson the AI can learn from.',
    customerNever: 'Don’t worry, the customer won’t see any of this.',
    savedActive: 'Thanks ah — the AI will use this from now on.',
    savedSuggested: 'Thanks — a lesson is now waiting for review.',
    savedPlain: 'Thanks for the feedback, appreciate it!',
    upRatedTitle: 'You said this reply was good',
    downRatedTitle: 'You said this reply got problem — click to edit',
    notSaved: 'Alamak, feedback didn’t save: {error}',
  },
  learningStats: {
    noDown: 'No thumbs-down in this period — nice!',
    loadError: 'Cannot load the feedback stats: {error}',
    nobodyRated: 'nobody rated any reply yet',
    gapsBody:
      'Questions the AI couldn’t answer well — it handed over, got blocked, or was rated down for wrong or missing info.',
    noGaps: 'No gaps found in this period — steady!',
    added: '“{title}” added to the knowledge base liao — the AI uses it from the next message.',
  },
  embeddings: {
    body:
      'Lets the AI find the right knowledge and lessons by meaning, so “can I get my money back?” still finds your refund policy. Without a provider, it matches by keywords only. Change the provider or model and everything gets re-indexed in the background.',
    modelHelp: 'Optional — leave empty to use the provider’s default.',
    checkingIndex: 'Checking the index, wait ah…',
    statusUnavailable: 'This server can’t show the indexing status yet.',
    keywordNoProvider: 'Keyword search — no embedding provider set up yet',
  },

  // ---- knowledge base ------------------------------------------------------------
  upload: {
    subtitle: 'The AI reads the text — price lists, policies, a product manual. The file itself we don’t keep.',
    added: '“{title}” added liao',
    charsSplit_one: '{chars} characters of text, cut into {count} piece for search. The AI can use it from the next message.',
    charsSplit_other:
      '{chars} characters of text, cut into {count} pieces for search. The AI can use it from the next message.',
    charsOnly: '{chars} characters of text. The AI can use it from the next message onwards.',
    uploading: 'Uploading, wait ah…',
    reading: 'Reading the document, hold on…',
    scanned:
      'Scanned PDFs (pictures of pages) got no text to read — export a text PDF, or paste the content into a new entry instead.',
    errors: {
      notSupported: '“{name}” is not a PDF, text, Markdown or CSV file leh.',
      tooBig: '“{name}” is {size} MB — too big, the limit is 10 MB.',
      empty: '“{name}” got nothing inside.',
      noUploads: 'This server cannot take document uploads yet.',
      tooLarge: 'The file too big lah — the limit is 10 MB.',
      noPermission: 'Sorry ah, your account cannot change the knowledge base.',
      failedHttp: 'Aiyo, upload failed (HTTP {status}).',
      unreachable: 'Cannot reach the server — check your connection and try again can?',
      cancelled: 'Okay, upload cancelled.',
    },
  },
  retrieval: {
    body:
      'Type a message a customer might send. You’ll see what the AI finds — the best-matching pieces of your knowledge and any lessons it learned — plus the exact text it gets before it answers.',
    runError: 'Aiyo, cannot run the test: {error}',
    nothingMatched: 'Nothing matched leh. Only standing notices, events and facts would go in.',
    nothingMatchedEmpty:
      'Nothing matched — type a question first to search. Only standing notices, events and facts would go in.',
    nothingHanded: '(nothing — for this message the AI answers without any company knowledge)',
    budget: '{used} / {budget} characters of the knowledge budget used up',
  },
  knowledge: {
    updated: 'Knowledge updated liao — the AI uses it from the next message.',
    added: 'Knowledge added — the AI will use it from the next message.',
    deleteConfirm: 'Confirm delete “{title}”? The AI will stop mentioning it straight away.',
    deleted: '“{title}” deleted liao.',
    loading: 'Loading the knowledge base, hold on ah…',
    loadError: 'Aiyo, cannot load the knowledge base — {error}',
    intro:
      'Everything the AI should know about your business, typed in by you — no code, no redeploy. Facts and policies are always there; {events} and {notices} come with a date range and {stop} once it ends. It reaches every channel and every voice call in one go.',
    readOnly: 'Read-only ah — only an admin can edit the knowledge base.',
    onlyCustomers: 'Only this entity’s customers will hear it.',
    everyInheritsThis: 'Every entity gets this too.',
    tagsHint: '(matching hints — type one, then press Enter to add)',
    publishHint: '— customers can read it at /help; the AI uses it anyway',
    emptyList: 'Nothing here yet leh — add your opening hours, your return policy, or a notice about a delay.',
    publishedTitle: 'Customers can read this at /help',
  },

  // ---- AI skills -------------------------------------------------------------------
  skills: {
    onFail: {
      retryHint: 'the AI rewrites up to 2×; still cannot → a human answers',
      handoverHint: 'no rewrite — the chat is marked pending for a human straight away',
      blockHint: 'the AI rewrites up to 2×; still cannot → nothing is sent',
    },
    toggledOn: '“{name}” is on liao.',
    toggledOff: '“{name}” is off — the AI won’t use it.',
    deleted: '“{name}” deleted.',
    startersAdded: 'Starter instructions added liao.',
    loading: 'Loading skills, one moment ah…',
    loadError: 'Aiyo, cannot load skills — {error}',
    intro:
      'Just describe what the assistant should be able to do. Plain guidance becomes an {instruction}; anything that must be exact — opening hours, dates, ID formats — becomes a {tool} it calls or a {check} every answer kena pass. Code skills are written for you, tested in a sandbox, and only switched on once the tests pass. A {app} lets the AI use your calendar — see free times and book.',
    readOnly: 'Read-only ah — only an admin can change skills.',
    deleteTitle: 'Confirm delete this skill?',
    deleteBody:
      '{name} will stop applying to every conversation straight away. Flow steps that use it will fall back to their own script.',
    deleteBodyEntity:
      '{name} will stop applying to every conversation in {entity} straight away. Flow steps that use it will fall back to their own script.',
    empty: {
      prompt: 'No instructions yet — the AI just runs on its persona.',
      tool: 'No tools yet. Don’t worry, the built-ins above still answer time and date questions exactly.',
      check: 'No reply checks yet — replies go out exactly as the AI writes them.',
      app: 'No connected apps yet — jom let the AI check free times and book appointments, on Google Calendar or in ChatBranch.',
    },
    noTrigger: 'No trigger set — the AI decides by itself when to use it.',
    builtinsBody: 'Exact date and time answers on every channel — the AI calls these instead of simply guessing.',
    builtinsUnavailable: 'Built-ins not available right now leh.',
  },
  wizard: {
    subtitles: {
      describe: 'Just say in your own words what the AI should be able to do. If anything is unclear, it will ask you.',
      clarify: 'A few questions only, so the code does exactly what you want.',
      reviewApp: 'Choose the calendar and when the AI can book, then preview the free times it would offer.',
      reviewCode: 'Check the code and its test cases — everything here you can edit.',
      reviewPrompt: 'Check the wording and when it should apply.',
      test: 'The tests run inside the same sandbox the AI uses for real replies.',
    },
    paramsObject: 'The parameters must be a JSON object (a JSON schema) ah.',
    paramsJson: 'The parameters are not valid JSON leh.',
    nameRequired: 'Give the skill a name first lah.',
    savedOn: '“{name}” saved liao — the AI uses it from the next message.',
    savedOffCode: '“{name}” saved, but switched off — turn it on once the tests pass.',
    savedOff: '“{name}” saved, but switched off for now.',
    mustPass: 'To switch the skill on, every test must pass (and you need at least one test).',
    connectedAppBlurb:
      'Let the AI use a connected app — e.g. check your calendar and book. Set it up directly, no need to describe anything.',
    useWhenHint: 'When the AI should use it.',
    descriptionHint: 'What it does, in one sentence — the AI reads this also.',
    appInstructionsHint: 'How the AI should use the calendar — the basics are filled in already; change as you like.',
    argsHint: 'What the AI passes when it calls the tool. No arguments? Just leave it empty.',
    running_one: 'Running {count} test in the sandbox, wait ah…',
    running_other: 'Running {count} tests in the sandbox, wait ah…',
    notRun: 'The code cannot run leh',
    noCases: 'No test cases yet',
    allPass_one: 'The test passed — steady!',
    allPass_other: 'All {count} tests passed — steady!',
    addOne: 'Add at least one test case first, then the skill can be switched on.',
    ready: 'All good — can switch on liao.',
    goBack: 'Go back to the code, fix it (or the test), and run again — or just save it switched off.',
  },
  tests: {
    empty: 'No test cases yet — add at least one first, so the skill can be switched on.',
    nowTitle: 'Fix the clock to one time, e.g. 2026-10-05T10:00 (business timezone)',
    errExpectJson: '“{label}”: the expectation is not proper JSON.',
    errArgsJson: '“{label}”: the arguments are not valid JSON leh.',
    errArgsObject: '“{label}”: arguments must be a JSON object ah, like {"days": 3}.',
    errExpectedJson: '“{label}”: the expected result is not proper JSON — check again.',
  },
  app: {
    actionHelp: {
      availability: 'See the free times within your working hours.',
      book: 'Hold the time the customer picked — only really booked when the reply is sent.',
      cancel: 'Only this customer’s own bookings made through ChatBranch. Want to reschedule? That’s cancel + book.',
    },
    problems: {
      hhmm: 'Use HH:MM times ah.',
      endAfterStart: 'Each window must end after it starts lah.',
      overlap: 'The windows overlap each other.',
      pickAction: 'Pick at least one thing the AI can do with the calendar first.',
      openDay: 'Must open at least one day in the working hours.',
      length: 'The appointment length can only be between 5 and 480 minutes.',
      step: 'Start times can only be 5 to 480 minutes apart.',
      buffer: 'The buffer can only be between 0 and 240 minutes.',
      notice: 'The minimum notice can only be between 0 minutes and 30 days.',
      ahead: 'Bookings can only be made 1 to 90 days ahead.',
      perCustomer: 'Active bookings per customer can only be 1 to 20.',
      unitMultiple: 'The appointment length must fit a whole number of {unit}-minute units.',
      longest: 'The appointment length ({length} min) is longer than the longest booking ({longest} min) leh.',
      eventTitle: 'Give the calendar event a title first.',
    },
    calendarHint: 'Where the AI checks free times and puts the bookings.',
    checkingConnection: 'Checking the connection, wait ah…',
    couldntCheck: 'Cannot check the connection right now leh.',
    notConnected: 'Not connected yet — {link}',
    chatbranchBody: 'No Google also can — bookings stay on the Bookings page.',
    assigneeNote:
      'Each agent connects their own calendar at {link}; conversations assigned to someone who hasn’t done so will use the shared calendar.',
    untilConnected:
      'Until Google Calendar is connected, the AI is told the calendar is not available and offers a teammate — it never secretly books into ChatBranch instead.',
    perReply:
      'Max one booking and one cancellation per reply, and only for the customer in the conversation. A booking reserved while the AI writes is only made when the reply is sent — a reply held for review books once approved.',
    workingHoursHint: 'The AI will only offer times inside these windows.',
    hours24: 'Use 24-hour times, in the timezone above.',
    startHint: 'Usually same as the length.',
    perCustomerHint: 'Anything more gets refused — the AI offers to reschedule instead.',
    lengthDefaultHint: 'Used if the customer doesn’t say how long.',
    lengthUnitHint: 'Longer bookings go up in steps of this.',
    longestHint: 'More than one unit, the AI asks how long and books ONE appointment for the whole time.',
    noticeHint: 'No booking for right this minute — this sets the earliest time offered.',
    eventNotesHint: 'Optional — the same {{variables}} also can use here.',
    previewBody: 'The free times the AI would offer right now with these settings — just a dry run, nothing gets booked.',
    settingsChanged: '· settings changed — check again ah',
    couldNotRead: 'Aiyo, cannot read the calendar.',
    noFree: 'No free times in the next few days with these settings leh.',
  },
  reviewer: {
    intro: 'Let a stronger AI check the recent replies and suggest what to improve.',
    introMore: 'It reads a sample of the replies the AI sent (or a person approved, edited or rejected), checks each one against your knowledge, house rules and persona, then tells you what to change. It only runs when you click Review now.',
    running: 'Reviewing, wait ah…',
    runningHint: 'This can take a minute or two. You can leave the page, no problem — the result will be kept.',
    willUseHeuristic: 'No AI model set up yet — so only simple automatic checks will run.',
    model: {
      someMissing: 'The greyed-out models need that provider’s API key first (Settings → AI keys & models).',
      pickedMissing: '{provider} not set up yet — add its API key in Settings → AI keys & models, or pick another model.',
      thisRunOnly: 'Only for this review.',
      savingDefault: 'Saving, wait ah…',
      savedDefault: 'Saved as the default reviewer liao.',
      defaultWhere: 'You can change it in Settings → AI keys & models.',
      optionsError: 'Alamak, couldn’t load the reviewer models: {error}',
    },
    noAccessRun: 'Only people who can change AI behaviour can start a review. But you can still read past reviews here.',
    heuristicNote: 'No AI model reviewed these replies — these are just simple automatic checks (long replies, unsupported numbers, unanswered questions, wrong language, missed hand-overs). Add an AI key in Settings → AI keys & models for a proper review.',
    noSuggestions: 'Nothing to change for now.',
    noIssues: 'No problems found in this review — all good!',
    noFindings: 'No AI replies to check in this period leh.',
    confirm: {
      subtitle: 'Check the wording and where it applies first, before adding.',
      levelHint: 'By default it goes to the conversation’s entity. That entity’s branches use it too; the organization level reaches everyone.',
      empty: 'Write the text first lah.',
    },
    noRuns: 'No reviews yet. Click Review now to run the first one.',
    failedRun: 'Aiyo, this review failed: {error}',
    stillRunning: 'Still reviewing, almost there…',
    loadError: 'Cannot load the reviews: {error}',
  },
}
