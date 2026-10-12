// Manglish overlay for the `bookings` namespace (see ./index.ts): only the strings it
// rewrites — every other key falls back to English.
import type { Messages } from '../en'
import type { Overlay } from '../../types'

export const bookings: Overlay<Messages['bookings']> = {
  statusHint: {
    pending: 'Held for an AI reply that’s not sent yet — once it goes out, it’s booked.',
    booked: 'Already on the calendar.',
    cancelling: 'Will be cancelled once the AI reply waiting for review goes out.',
    cancelled: 'Cancelled liao — the time is free again.',
    released: 'The AI reply never went out, so nothing got booked.',
    failed: 'The calendar rejected the booking when the reply was supposed to go out.',
  },
  card: {
    loadFailed: 'Aiyo, cannot load the bookings right now.',
    none: 'No upcoming bookings yet.',
    noneHint: 'Once the AI books an appointment with this contact, it’ll show up here.',
    cancelled: 'Cancelled {label} liao.',
  },
  cancel: {
    title: 'Confirm cancel this booking?',
    failed: 'Cannot cancel the booking leh.',
    googleNote:
      'The event will be removed from Google Calendar and a note added to the conversation. The customer won’t get any message automatically — tell them in the chat yourself.',
    localNote:
      'The time gets freed up and a note added to the conversation. The customer won’t get any message automatically — remember to tell them in the chat.',
  },
  page: {
    subtitle: 'All the appointments booked by the AI and your team — on Google Calendar or kept in ChatBranch.',
    noAccess: 'Sorry ah, your account cannot see bookings. Ask an admin for the “View chats” permission.',
    notAvailable: 'Bookings not available on this server yet leh.',
    loadFailed: 'Cannot load the bookings — {error}',
    loading: 'Loading bookings, wait ah…',
    emptyFiltered: 'No bookings match these filters leh.',
    emptyUpcoming: 'No upcoming bookings yet.',
    emptyPast: 'Nothing in past bookings yet.',
    emptyHint: 'Give the AI a calendar on {link} (a Connected app), then whatever it books will land here.',
    cancelledWith: 'Done, cancelled {label} with {name}.',
  },
  calendar: {
    loading: 'Getting your calendars, hold on ah…',
    loadFailed: 'Cannot load the calendar — {error}',
    noneTitle: 'No Google Calendar to show yet',
    none: 'Connect your own Google Calendar at {mine}, then its events will show up here.',
    noneAdmin:
      'Connect your own Google Calendar at {mine}, or a shared one for an outlet or the whole organization at {integrations}, then its events will show up here.',
    reconnect: 'This calendar’s Google connection stopped working liao — reconnect it at {link}.',
    googleFailed: 'Cannot read Google Calendar — {error}',
    truncated: 'This week got too many events to show — only the first 500 are listed.',
    readOnly:
      'This is the calendar exactly as it is in Google — read-only. To change events, go to Google Calendar; bookings made in ChatBranch can be cancelled in the Upcoming tab.',
  },
}
