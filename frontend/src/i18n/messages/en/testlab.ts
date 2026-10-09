// English source for the `testlab` namespace (owner: test lab). Other locales mirror it key for key.
// Scenario titles, steps and check names come from the server in English.
export const testlab = {
  title: 'Test lab',
  eyebrow: 'Review',
  subtitle:
    'Scripted use cases that run end to end through the real pipelines — inside a fenced “Test lab” entity that never touches your own data.',
  off: {
    title: 'The Test lab is switched off',
    body: 'This server runs with TEST_LAB=0, so the Test lab is not available here.',
  },
  status: {
    none: 'No test data yet — the “Test lab” entity is created the first time a scenario runs.',
    data: '{contacts} contacts and {conversations} conversations in “{entity}”.',
    demo: 'Demo data loaded',
    aiReal: 'An AI key is configured: scenarios marked “AI key” also check the real model’s answer.',
    aiSandbox:
      'No AI key: the lab’s deterministic test brain writes the AI’s words; scenarios marked “AI key” run their deterministic variant.',
    cli: 'From a terminal: npm run test:scenarios',
  },
  actions: {
    runSelected: 'Run selected ({count})',
    runAll: 'Run all',
    stop: 'Stop after this one',
    loadDemo: 'Load demo data',
    reset: 'Reset test data',
    selectCategory: 'Select all in {category}',
    details: 'Details',
    hideDetails: 'Hide details',
    runOne: 'Run',
  },
  progress: {
    running: 'Running {done} of {total} — {title}',
    stopping: 'Stopping after the current scenario…',
    done: 'Finished: {passed} passed, {failed} failed, {skipped} skipped.',
  },
  result: {
    passed: 'Passed',
    failed: 'Failed',
    skipped: 'Skipped',
    running: 'Running…',
    queued: 'Queued',
    notRun: 'Not run',
    seconds: '{seconds} s',
    brainLab: 'Test brain',
    brainReal: 'Real AI model',
    checks: '{ok} of {total} checks',
  },
  badge: {
    aiKey: 'AI key',
  },
  detail: {
    steps: 'Steps',
    expect: 'Expected',
    checks: 'Checks',
    links: 'Open what it made',
    error: 'Error',
    skipped: 'Skipped: {reason}',
  },
  history: {
    title: 'Run history',
    subtitle: 'The last 50 runs, from this page, the CLI and the test suite.',
    empty: 'No runs yet.',
    summary: '{passed} passed · {failed} failed · {skipped} skipped',
    by: 'by {user}',
    show: 'Show results',
    showing: 'Showing run #{id} from {when}.',
    sources: {
      page: 'Page',
      cli: 'CLI',
      api: 'API',
      test: 'Test suite',
    },
  },
  demo: {
    loaded: 'Demo data loaded: {contacts} contacts, {conversations} conversations, {bookings} bookings.',
    already: 'Demo data is already loaded — reset the test data to load it again.',
  },
  reset: {
    title: 'Reset test data?',
    body: 'This deletes everything inside the “Test lab” entity: its conversations, contacts, AI drafts, bookings, queued messages, flow runs, inboxes, teams, settings and the testlab_* users. Nothing outside it is touched, and the run history is kept.',
    confirm: 'Delete test data',
    done: 'Test data deleted ({count} rows).',
  },
  errors: {
    load: 'Couldn’t load the Test lab — {error}',
    action: 'That didn’t work — {error}',
  },
} as const
