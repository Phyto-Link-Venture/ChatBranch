// Manglish overlay for the `testlab` namespace (see ./index.ts): only the strings it
// rewrites — every other key falls back to English.
import type { Messages } from '../en'
import type { Overlay } from '../../types'

export const testlab: Overlay<Messages['testlab']> = {
  subtitle:
    'Scripted use cases that run end to end through the real pipelines — all inside a fenced “Test lab” entity, so your own data won’t kena touched.',
  off: {
    title: 'The Test lab is off on this server',
    body: 'This server runs with TEST_LAB=0, so cannot use the Test lab here.',
  },
  status: {
    none: 'No test data yet — the “Test lab” entity only gets created the first time a scenario runs.',
    data: 'Got {contacts} contacts and {conversations} conversations in “{entity}”.',
    aiReal: 'AI key is set up: scenarios marked “AI key” will also check the real model’s answer.',
    aiSandbox:
      'No AI key yet: the lab’s deterministic test brain writes the AI’s words for now; scenarios marked “AI key” run their deterministic version.',
    cli: 'Prefer the terminal? npm run test:scenarios',
  },
  progress: {
    running: 'Running {done} of {total} now — {title}',
    stopping: 'Will stop after this scenario, wait ah…',
    done: 'All done: {passed} passed, {failed} failed, {skipped} skipped.',
  },
  history: {
    subtitle: 'Last 50 runs, whether from this page, the CLI or the test suite.',
    empty: 'No runs yet leh.',
    showing: 'Now showing run #{id} from {when}.',
  },
  demo: {
    loaded: 'Demo data loaded liao: {contacts} contacts, {conversations} conversations, {bookings} bookings.',
    already: 'Demo data already loaded — reset the test data first if you want to load it again.',
  },
  reset: {
    title: 'Reset the test data ah?',
    body: 'This deletes everything inside the “Test lab” entity: its conversations, contacts, AI drafts, bookings, queued messages, flow runs, inboxes, teams, settings and the testlab_* users. Nothing outside it kena touched, and the run history stays.',
    done: 'Test data cleared ({count} rows) — clean slate.',
  },
  errors: {
    load: 'Aiyo, cannot load the Test lab — {error}',
    action: 'Alamak, that one didn’t work — {error}',
  },
}
