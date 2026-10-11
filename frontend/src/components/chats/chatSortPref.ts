/**
 * The chat list's order switch — "Newest message" / "Newest from customer" — is
 * each person's own. The account keeps it (Profile.Preferences `chats.sort`, via
 * /api/auth/preferences — like the language and the page guides), so it follows
 * them to another browser or the installed app; this browser keeps a copy
 * (localStorage `chats-sort:<username>`) that answers at once on the next visit.
 * Storage may be blocked (private mode): every read and write is best effort.
 */
import { getAuth } from '@/lib/auth'
import { api } from './inboxTypes'
import { isHeaderSort, type HeaderSort } from './inboxFilters'

const storageKey = (user: string) => `chats-sort:${user}`

/** This browser's copy for the signed-in person (null = none yet). Call after mount. */
export function localChatSort(): HeaderSort | null {
  try {
    const user = getAuth()?.username
    if (!user) return null
    const v = localStorage.getItem(storageKey(user))
    return isHeaderSort(v) ? v : null
  } catch {
    return null
  }
}

/** The account's copy (null = unreachable, or an older API without it). */
export async function serverChatSort(): Promise<HeaderSort | null> {
  try {
    const d = await api<{ chats?: { sort?: string } }>('auth/preferences')
    const s = d.chats?.sort
    return isHeaderSort(s) ? s : null
  } catch {
    return null
  }
}

/** Keep this browser's copy (e.g. of the account's answer). */
export function storeLocalChatSort(sort: HeaderSort) {
  try {
    const user = getAuth()?.username
    if (user) localStorage.setItem(storageKey(user), sort)
  } catch {
    /* storage blocked — the account copy still holds */
  }
}

/** Remember the person's choice here and on their account (fire and forget). */
export function saveChatSort(sort: HeaderSort) {
  storeLocalChatSort(sort)
  api('auth/preferences', { method: 'PUT', body: JSON.stringify({ chats: { sort } }) }).catch(() => {
    /* offline / older API — this browser's copy still holds */
  })
}
