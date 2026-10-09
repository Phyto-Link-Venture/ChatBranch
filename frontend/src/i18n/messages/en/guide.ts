export const guide = {
  ui: {
    button: 'Guide',
    thisPage: 'Show this page’s guide',
    appTour: 'Tour of ChatBranch',
    manual: 'Open the user manual',
    autoStart: 'Show guides on first visit',
    stepOf: 'Step {n} of {count}',
    next: 'Next',
    back: 'Back',
    skip: 'Skip',
    done: 'Done',
    close: 'Close guide',
  },
  manual: {
    title: 'ChatBranch user manual',
    subtitle: 'A practical guide to every part of ChatBranch — read it here, download it, or put it on your own website.',
    contents: 'Contents',
    showMe: 'Show me in the app',
    downloadPdf: 'Download PDF',
    downloadHtml: 'Download HTML',
    embed: 'Put it on your website',
    embedHelp: 'Paste this where you want the manual to appear. It stays up to date as ChatBranch changes.',
    copy: 'Copy',
    copied: 'Copied',
    standaloneHelp: 'Prefer your own copy? “Download HTML” saves a single file you can host anywhere.',
    groups: {
      start: 'Getting started',
      work: 'Everyday work',
      ai: 'The AI',
      setup: 'Setup & billing',
    },
    footer: 'Every chapter is also a guided tour inside ChatBranch: open the page and press the ? button in the top bar to replay it.',
    generated: 'Generated {date}',
  },
  tours: {
    shell: {
      title: 'Getting around',
      intro: 'The parts of ChatBranch you will use on every page.',
      steps: {
        s1: {
          title: 'The sidebar',
          body: 'Every area of ChatBranch lives here, grouped as Work, Automate, Library, Channels, Admin and Review. You only see what your role allows.',
        },
        s2: {
          title: 'Search anything',
          body: 'Press Ctrl+K (⌘K on a Mac) to find contacts, conversations, templates, journeys and pages from anywhere.',
        },
        s3: {
          title: 'Pick an outlet',
          body: 'If your business has several outlets or companies, choose one here — every page then shows only that outlet’s data.',
        },
        s4: {
          title: 'Live right now',
          body: 'How many chats are waiting and how many calls are live. The dot shows whether the inbox is connected in real time.',
        },
        s5: {
          title: 'Ask ChatBranch',
          body: 'Your assistant: ask a question or tell it what to do (“find Siti’s last order”, “turn the AI off for this chat”). It asks before changing anything.',
        },
        s6: {
          title: 'Guides',
          body: 'Replay the guide for the page you are on, take this tour again, or open the full manual.',
        },
      },
    },
    analytics: {
      title: 'Analytics — your home page',
      intro: 'How conversations, calls and the AI are doing.',
      steps: {
        s1: {
          title: 'Your home page',
          body: 'Analytics is the first thing you see after signing in: a summary of the period you choose, for the outlet you picked.',
        },
        s2: {
          title: 'Choose the period',
          body: 'Switch between 7, 30 and 90 days. Every card below follows.',
        },
        s3: {
          title: 'Key numbers',
          body: 'Messages sent and delivered, how many customers replied, calls and their outcomes — with the change against the previous period.',
        },
        s4: {
          title: 'Go deeper',
          body: 'Reports break the same numbers down by agent, team, label and channel. AI costs and company bills live under Review → AI bill.',
        },
      },
    },
    chats: {
      title: 'Chats — the unified inbox',
      intro: 'Every channel in one inbox: answer, hand over, and let the AI help.',
      steps: {
        s1: {
          title: 'Find a conversation',
          body: 'Search by name, number or anything said in the chat. Filter opens every filter: channel, status, assignee, AI on or off, waiting for a reply and more.',
        },
        s2: {
          title: 'Views',
          body: 'All, Mine, Unassigned and Needs human are ready-made views. Save your own filters as a new tab with the ••• menu.',
        },
        s3: {
          title: 'The conversation list',
          body: 'Newest activity first. An orange time means the customer is waiting for a reply; the chip shows whether the AI answers this chat.',
        },
        s4: {
          title: 'AI on or off for this chat',
          body: 'Switch the AI off to answer yourself, or back on to let it reply. If a rule higher up decides, the switch is locked and says why.',
        },
        s5: {
          title: 'Reply or add a note',
          body: 'Write a reply, or switch to Private note to leave a message only your team sees. Type / for quick replies; attach files with the clip.',
        },
        s6: {
          title: 'Details',
          body: 'Who the customer is, which team and person own the chat, labels, the number it is sent from, and the case summary.',
        },
      },
    },
    contacts: {
      title: 'Contacts',
      intro: 'Your customer book — import, find and open a contact.',
      steps: {
        s1: {
          title: 'All your contacts',
          body: 'Everyone you talk to, for the outlet you picked. Click a contact to open their full profile.',
        },
        s2: {
          title: 'Import or add',
          body: 'Import a spreadsheet (.csv or .xlsx), a phone export (.vcf) or your Google Contacts. Duplicates are matched by phone and email, never doubled.',
        },
        s3: {
          title: 'Search and sort',
          body: 'Type to filter by name, phone, tag or entity; click a column heading to sort.',
        },
      },
    },
    contact: {
      title: 'A contact’s profile',
      intro: 'Everything about one customer on one page.',
      steps: {
        s1: {
          title: 'Who they are',
          body: 'Name, phone, email and tags. “Open in Chats” jumps to their conversation.',
        },
        s2: {
          title: 'AI summary',
          body: 'A short brief of every call, chat and email with this person — generate it before you call them back.',
        },
        s3: {
          title: 'Communications',
          body: 'Every message and call across every channel, newest first.',
        },
        s4: {
          title: 'AI for this contact',
          body: 'Follow the chat’s own switch, or decide for this person: the AI never answers them, or always does.',
        },
        s5: {
          title: 'Channels',
          body: 'Where you can reach them — WhatsApp, email, voice and more — with each channel’s health. Invite them to another app with a link or QR code.',
        },
      },
    },
    journeys: {
      title: 'Journeys — automated follow-ups',
      intro: 'Messages, calls and checks that run by themselves.',
      steps: {
        s1: {
          title: 'What a journey is',
          body: 'A journey starts on a trigger (a new contact, a tag, a message, a date) and then sends messages, waits, makes decisions and hands over to people — on its own.',
        },
        s2: {
          title: 'Your journeys',
          body: 'Pick a journey to edit it, or start a new one from a template such as a welcome series or appointment reminder.',
        },
        s3: {
          title: 'Build the steps',
          body: 'Add steps in order: send a message, wait, check a condition or let the AI decide, call, update a tag. The Advanced canvas shows the same journey as a diagram.',
        },
        s4: {
          title: 'Go live and watch',
          body: 'New journeys start paused. Switch one live when it is ready, and open Runs to see who is where and why anyone dropped out.',
        },
      },
    },
    aiMonitor: {
      title: 'AI monitor',
      intro: 'See, check and approve what the AI says.',
      steps: {
        s1: {
          title: 'The master switch',
          body: 'Turn the AI on or off for everyone, or give it a schedule (for example evenings and weekends only).',
        },
        s2: {
          title: 'Sections',
          body: 'Waiting for review holds replies a person should check first. AI activity is everything the AI wrote. Learning and Stats show how it improves.',
        },
        s3: {
          title: 'Review modes',
          body: 'In Settings, choose: send straight away, hold only risky replies (prices, refunds, promises), hold everything, or shadow mode (the AI drafts, people send).',
        },
        s4: {
          title: 'Teach it',
          body: 'Approve, edit or reject a held reply — each decision becomes a lesson the AI uses next time.',
        },
      },
    },
    playground: {
      title: 'AI playground',
      intro: 'Talk to the AI as a customer would — nothing is sent.',
      steps: {
        s1: {
          title: 'Set the scene',
          body: 'Choose the outlet, the channel and the customer — a made-up one, a real contact, or a copy of a real conversation.',
        },
        s2: {
          title: 'Chat',
          body: 'Type as the customer. The AI answers exactly as it would in production, but nothing is sent, saved or booked.',
        },
        s3: {
          title: 'Inspector',
          body: 'What the reply was based on (knowledge, lessons, tools) and what would happen in production: send, hold for review, or hand over.',
        },
        s4: {
          title: 'Try changes, keep the good ones',
          body: 'Change the model or edit the persona and compare two set-ups side by side. “Save as persona” makes a good draft the real one; “Teach” turns a fix into a lesson.',
        },
      },
    },
    persona: {
      title: 'Persona & house rules',
      intro: 'How the AI introduces itself, and the rules it always follows.',
      steps: {
        s1: {
          title: 'Persona',
          body: 'Who the AI is and how it sounds. One persona applies: an outlet’s own, else its head office’s, else the organization’s.',
        },
        s2: {
          title: 'House rules',
          body: 'Rules every reply must follow, such as “Always be polite”. They add up — a branch keeps every rule above it and can add its own.',
        },
        s3: {
          title: 'What the AI receives',
          body: 'The exact opening of every prompt: safety rules, then house rules, then the persona. Try it in the playground before going live.',
        },
      },
    },
    knowledge: {
      title: 'Knowledge base',
      intro: 'The facts the AI answers from.',
      steps: {
        s1: {
          title: 'What the AI knows',
          body: 'Facts, policies, FAQs, events and short notices. The AI only states what is here — it never makes up prices or dates.',
        },
        s2: {
          title: 'Add or upload',
          body: 'Add an item by hand or upload a PDF, text or CSV file. Notices and events can start and end on a date, so they expire by themselves.',
        },
        s3: {
          title: 'Test retrieval',
          body: 'Type a customer question to see exactly which items the AI would read for it. Add synonyms to tags (in every language your customers use) when the right item is missed.',
        },
      },
    },
    skills: {
      title: 'AI skills',
      intro: 'What the AI can do, and the checks every reply must pass.',
      steps: {
        s1: {
          title: 'Four kinds of skill',
          body: 'Instructions (how to handle a situation), tools (look something up), checks (a rule every reply must pass) and connected apps (book appointments in a calendar).',
        },
        s2: {
          title: 'Create with the wizard',
          body: 'Describe what you want in plain words; the wizard asks a few questions and drafts the skill for you to test.',
        },
        s3: {
          title: 'Built-in tools',
          body: 'Date and time tools and the hand-over tool are always on — the AI uses them to get dates right and to call a person in when it should.',
        },
      },
    },
    channels: {
      title: 'Channels',
      intro: 'Every number, mailbox and app the business talks on.',
      steps: {
        s1: {
          title: 'Your inboxes',
          body: 'Each WhatsApp number, email mailbox, bot, page and website widget is a card with its health.',
        },
        s2: {
          title: 'Add a channel',
          body: 'Pick an app, connect it step by step, then test it. TikTok and Douyin need approval from the platform first.',
        },
        s3: {
          title: 'Who answers it',
          body: 'For each inbox, choose the team that gets new chats, whether the AI answers by default, and how unknown customers are identified.',
        },
      },
    },
    billing: {
      title: 'AI bill',
      intro: 'What each company owes for its AI usage.',
      steps: {
        s1: {
          title: 'Month and company',
          body: 'Step through the months and pick a company. Branches are billed to their company.',
        },
        s2: {
          title: 'The statement',
          body: 'Usage by feature, the monthly fee and the total, in your billing currency.',
        },
        s3: {
          title: 'Print or set terms',
          body: 'Print saves the statement as a PDF. Billing managers set the markup, fee and currency under Billing terms.',
        },
      },
    },
    settings: {
      title: 'Settings',
      intro: 'Your own preferences and the organization’s configuration.',
      steps: {
        s1: {
          title: 'Sections',
          body: 'Personal pages are just for you (appearance, language, notifications, your own Google connections). Organization and AI pages need a permission — a lock shows which.',
        },
        s2: {
          title: 'Where everything else went',
          body: 'Channels, templates, knowledge, skills, users and entities live in the sidebar, not here.',
        },
      },
    },
  },
} as const
