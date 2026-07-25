'use client'

import { useEffect, useState } from 'react'
import { getRole, type Role } from './auth'

/**
 * Hydration-safe role read.
 *
 * `getRole()` reads localStorage, which doesn't exist during SSR — calling it
 * straight from render makes the server paint the 'agent' fallback while
 * the client paints the real role. React then aborts hydration ("Expected
 * server HTML to contain a matching <svg> in <button>") and re-renders the
 * whole root on the client. Resolving the role in a post-mount effect keeps
 * both passes identical.
 *
 * `role` is 'agent' (least privilege, matching getRole's own fallback)
 * until `ready` flips true. Gate admin-only UI on `role`; use `ready` where
 * you'd rather render nothing for a frame than flash a "not allowed" notice.
 *
 * Components that only need a boolean may keep the existing
 * `useState(false)` + `useEffect(() => setCanManage(getRole() !== 'agent'))`
 * pattern — it is hydration-safe for the same reason.
 */
export function useRole(): { role: Role; ready: boolean } {
  const [state, setState] = useState<{ role: Role; ready: boolean }>({
    role: 'agent',
    ready: false,
  })

  useEffect(() => setState({ role: getRole(), ready: true }), [])

  return state
}
