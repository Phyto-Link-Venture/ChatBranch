# Baileys production-reliability research

Practical research report on running the `backend/baileys_service/` gateway
(unofficial WhatsApp Web transport) reliably, and the ban/session realities that
cap how far it can be pushed. Written against the **actually installed** package,
not the latest docs — behaviour was read out of
`node_modules/@whiskeysockets/baileys` and cross-checked with the project code in
`index.js` / `calls.js`.

- **Installed Baileys:** `@whiskeysockets/baileys@7.0.0-rc13`
  (`backend/baileys_service/package.json` pins `^7.0.0-rc13`;
  `node_modules/@whiskeysockets/baileys/package.json` → `7.0.0-rc13`).
- **Auth backend in use:** `useMultiFileAuthState` (folder-per-account under
  `backend/data/whatsapp/baileys/<account_id>/`).
- Sources are cited inline. Package-internal facts cite the file path under
  `node_modules/@whiskeysockets/baileys/lib/…`.

---

## TL;DR

- **This transport cannot be made "production reliable" in the sense that
  matters.** Reconnection and session handling can be hardened (and should be —
  see below), but the dominant failure mode is **WhatsApp banning the number**,
  which no amount of gateway engineering prevents. Keep the project posture:
  **dev/demo only, official Cloud API for production.**
- **Session lifetime** is effectively indefinite *while the paired phone checks
  in*, but a linked device is force-logged-out if the **primary phone is offline
  for ~14 days** (multi-device security rule — WhatsApp Help Center; some
  unofficial providers quote up to 30 days). The auth state itself does not
  expire on a timer.
- **Our auth persistence is correct** — `creds.update → saveCreds` writes
  `creds.json` + per-key files per account, and boot resumes every folder that
  has a `creds.json`. Losing that folder = a forced QR re-pair, nothing worse.
- **Our reconnection logic is too naive.** It reconnects on *everything except
  401* with a **fixed 3s delay, no cap, no jitter, no attempt ceiling**. That
  loops forever on a ban (`403`), fights a takeover (`440`), and hammers WhatsApp
  every 3s during an outage — each of which is itself a ban-risk signal. A
  reason-aware backoff table is the single highest-value change.
- **The installed v7 already exposes native ban/throttle telemetry we don't
  use**: `connection.update.reachoutTimeLock` ("WhatsApp prevents outgoing
  messages and calls" — `lib/Types/State.d.ts:39`) and the
  `message-capping.update` event (`NewChatMessageCapInfo` with
  `capping_status: NONE|FIRST_WARNING|SECOND_WARNING|CAPPED`). These are the
  *ideal* health signals to push to Django — better than inferring health from
  disconnects.
- **Inbound-first is the single biggest risk reducer.** Community/vendor data
  puts reactive (customer-initiated) bots at **<2% ban over 12 months** vs
  **15–30%** for proactive outreach to fresh contacts
  ([wapisimo.dev](https://wapisimo.dev/blog/en/whatsapp-unofficial-api-ban-risk)).
  Our SOP ("customer contacts us first") lands us in the low-risk bucket — the
  outbound queue must be built to *preserve* that, not erode it.
- **Automatic sender-number switching is a ban-evasion pattern** and should not
  be automated as failover. Documented safe practice below (warm-up, contact
  saved, inbound-only on fresh numbers).

---

## 1. Session lifetime (multi-device auth state)

**What "the session" is.** After a QR pair, WhatsApp issues the linked device a
Signal-protocol identity. There is **no fixed session TTL** — the credentials
stay valid as long as the account remains linked and the primary phone keeps
checking in.

**What invalidates it:**

| Cause | Effect | Notes |
| --- | --- | --- |
| **Primary phone offline ~14 days** | All linked devices auto-logged-out | Multi-device security rule ([WhatsApp Help Center](https://faq.whatsapp.com/378279804439436/)); resets by opening WhatsApp on the phone with internet. Unofficial providers quote 14d, sometimes 30d for MD ([green-api](https://green-api.com/en/docs/faq/why-does-the-crash-happen/)). |
| **Manual unlink** | Immediate `loggedOut` (401) | User taps "Log out" on the linked-devices screen, or WhatsApp does it for "security reasons" ([TechRadar](https://www.techradar.com/news/whatsapp-may-kick-you-out-of-linked-devices-for-security-reasons)). |
| **WhatsApp app reinstall / phone migration** | Unlinks all devices | ([green-api](https://green-api.com/en/docs/faq/why-does-the-crash-happen/)) |
| **Encryption-key rotation by WhatsApp** | Session invalidated | Same source. |
| **Ban / policy enforcement** | `loggedOut` (401) or `forbidden` (403) *without* any user action | The dangerous one — indistinguishable at first glance from a normal logout. |
| **`badSession` (500)** | Local key state corrupt | Reconnect may loop; usually needs auth wipe + re-pair. |

**What the auth-state files contain** (`useMultiFileAuthState`, written with
`BufferJSON` so binary keys survive JSON round-trips —
`lib/Utils/use-multi-file-auth-state.js`):

- `creds.json` — the crown jewels: `noiseKey`, `signedIdentityKey`,
  `signedPreKey`, `registrationId`, `advSecretKey`, routing info, and after
  pairing the `me` identity (phone JID + LID). Shape from `initAuthCreds()`
  (`lib/Utils/auth-utils.js:279`).
- `app-state-sync-key-*.json`, `pre-key-*.json`, `session-*.json`,
  `sender-key-*.json` — per-contact Signal sessions and app-state sync keys,
  written lazily by the `keys.set` path as chats happen.

Losing `creds.json` = the device can never re-authenticate that identity → a
fresh QR pair is required (a *new* linked-device slot, not the old one).

---

## 2. QR-login persistence — does our gateway store it correctly?

**Yes, correctly and per-account.** `index.js`:

- `startAccount()` calls `useMultiFileAuthState(dir)` where
  `dir = data/whatsapp/baileys/<account_id>/` (`index.js:149-151`).
- `sock.ev.on('creds.update', saveCreds)` (`index.js:175`) persists on every
  credential mutation — the required pattern.
- On boot, the HTTP server lists `AUTH_ROOT` and resumes any folder that has a
  `creds.json` (`index.js:413-419`).

**Risks / caveats:**

- **`useMultiFileAuthState` is explicitly "not for production"** — the library's
  own source header says *"I wouldn't endorse this for any production level use
  other than perhaps a bot… recommend writing an auth state for use with a proper
  SQL or No-SQL DB"* (`lib/Utils/use-multi-file-auth-state.js:25-26`), and the
  docs repeat *"DONT EVER USE THE `useMultiFileAuthState` IN PROD"* — the concern
  is **I/O volume and write races**, not correctness. It fsyncs many small files
  under a per-file async mutex; on a busy account this is a lot of disk churn.
- **`creds.json` is a bearer credential.** Anyone who copies that folder can
  impersonate the number from another host. It sits under `backend/data/` — treat
  it as a secret (the repo already gitignores PII data). A second process opening
  the same creds triggers a `440 connectionReplaced` stream conflict (this is
  exactly why `calls.js` composes the existing socket instead of opening its own
  — see `calls.js:5-9`).
- **No atomic write / backup.** A crash mid-write of `creds.json` (power loss,
  disk full) can corrupt it → forced re-pair. A DB-backed or atomic-rename auth
  store removes this class of failure.

---

## 3. Session recovery (restart behaviour)

On gateway restart, for each account folder with a `creds.json`, `startAccount()`
re-runs and the socket **re-logs-in silently using the saved keys** — no QR. This
is the normal, expected path and it works.

**Resumes silently (no human action):**

- Ordinary process restart / crash-restart while the link is still valid.
- Transient network drops (`428`/`408`), server-requested restarts (`515`),
  service blips (`503`).
- On reconnect the socket replays offline traffic: `messaging-history.set` and
  the `receivedPendingNotifications` flag mark when the buffered backlog has
  drained ([Baileys connection docs](https://whiskeysockets-baileys-94.mintlify.app/concepts/connection)).

**Needs a fresh QR pair (human action):**

- `loggedOut` (401) — link revoked (manual, 14-day rule, or ban). Our code wipes
  the auth dir here (`index.js:199-204`), which is correct: the keys are dead.
- `badSession` (500) — corrupt local key state; only a wipe + re-pair reliably
  clears it. **We do not handle this today** (we just reconnect-loop).
- `forbidden` (403) — treat as terminal (ban/enforcement); re-pairing usually
  fails or re-bans. **We do not handle this today.**

Note the pairing handshake itself ends in a disconnect on purpose: right after a
successful scan WhatsApp drops the socket with `515 restartRequired`; the client
must reconnect to come up authenticated
([Baileys connecting docs](https://baileys.wiki/docs/socket/connecting/)). Our
blanket "reconnect on non-401" happens to cover this, but only by luck of the
default — it is not explicitly modelled.

---

## 4. Disconnect scenarios — DisconnectReason reference

Exact numeric values from the **installed** enum
(`lib/Types/index.js:13-25`, mirrored in `lib/Types/index.d.ts:25-36`). Note two
members **share code 408** (`connectionLost` and `timedOut`), so a numeric switch
cannot tell them apart — you only get the distinction from the Boom message.

| Reason | Code | Origin in source | Operational meaning | Action |
| --- | --- | --- | --- | --- |
| `connectionClosed` | **428** | normal WS close | Benign close (idle, server cycling). Very commonly seen ~24h in ([issue #1625](https://github.com/WhiskeySockets/Baileys/issues/1625)). | Reconnect (short backoff). |
| `connectionLost` | **408** | keep-alive/query timeout | Network blip / missed heartbeat. | Reconnect (short backoff). |
| `timedOut` | **408** | `getCodeFromWSError` on `E*`/"timed out" (`lib/Utils/generics.js:339`) | DNS/TCP failure to reach WA. | Reconnect (short backoff). |
| `connectionReplaced` | **440** | `CB:stream:error` `conflict` → `CODE_MAP` (`lib/Utils/generics.js:282`) | **Another session took over the same creds** (WhatsApp Web opened elsewhere, or a 2nd gateway/`VoipClient`). Immediate reconnect ping-pongs with the other session. | **Do NOT auto-reconnect blindly** — back off hard or stop; investigate duplicate consumer. |
| `loggedOut` | **401** | `CB:failure reason=401` / stream error | Link revoked: manual unlink, 14-day rule, **or ban**. Keys are dead. | **Do NOT reconnect.** Wipe auth, surface "needs re-pair". (We do this.) |
| `badSession` | **500** | default stream-error code (`lib/Utils/generics.js:292`) | Corrupt local Signal state. | Reconnect once; if it recurs, **wipe + re-pair**. |
| `restartRequired` | **515** | stream error, relabelled "restart required" (`lib/Utils/generics.js:293`) | Server wants a fresh socket — **normal immediately after QR pair**. | Reconnect immediately (new socket). |
| `multideviceMismatch` | **411** | `CB:ib,,downgrade_webclient` (`lib/Socket/socket.js:800`) | Protocol/version mismatch. | Update `fetchLatestBaileysVersion` / library; reconnect won't fix alone. |
| `forbidden` | **403** | `CB:failure reason=403` | **Access denied — strongest single ban/enforcement signal.** | **Do NOT reconnect-loop.** Mark account banned/unavailable, alert operator. |
| `unavailableService` | **503** | `CB:failure`/stream | WA service temporarily unavailable. | Reconnect with long backoff. |

Where these come from at the wire level (`lib/Socket/socket.js`):
`ws.on('CB:failure')` reads `reason = +(node.attrs.reason || 500)`
(`socket.js:795-797`); `ws.on('CB:stream:error')` runs
`getErrorCodeFromStreamError` (`socket.js:788-792`), which maps the `conflict`
tag → `440` and otherwise defaults to `500`.

**"Banned" vs "just reconnect":**

- **Ban / terminal:** `403 forbidden` (clearest), and `401 loggedOut` **when no
  human unlinked it**. A `401` arriving seconds after a healthy `open`, or right
  after a burst of sends, reads as enforcement rather than a normal logout.
- **Just reconnect:** `428`, `408`, `515`, `503`, `500` (once).
- **Ambiguous / needs judgement:** `440 connectionReplaced` (usually
  operational — duplicate session — not a ban) and `411 multideviceMismatch`
  (library staleness).

---

## 5. Reconnection strategy — current vs recommended

**What `index.js` does today** (`index.js:192-210`): on `connection === 'close'`,
if code `=== 401` wipe + stop; **otherwise unconditionally reconnect after a
fixed 3000 ms**, forever, with no attempt counter, no cap, and no jitter.

Problems:
- Loops every 3s on `403` (ban) and `500` (bad session) — pointless, and the
  reconnect storm is itself a spam signal.
- On `440` it immediately re-grabs the socket, ping-ponging with whatever else
  holds the creds.
- During a real WA outage it hammers `wss://web.whatsapp.com` every 3s
  indefinitely.
- Fixed timing across all accounts = synchronised reconnect thundering herd if
  several drop together.

**Recommended reason-aware backoff** (drop-in replacement for the single 3s
timer):

| Reason (code) | Reconnect? | Backoff |
| --- | --- | --- |
| `restartRequired` (515) | Yes, immediately | 0–1 s (expected post-pair) |
| `connectionClosed` (428) | Yes | exp backoff 1s→60s cap, ±20% jitter |
| `connectionLost`/`timedOut` (408) | Yes | exp backoff 2s→60s cap, ±20% jitter |
| `unavailableService` (503) | Yes | exp backoff 5s→5min cap |
| `badSession` (500) | Once | 5s; on 2nd `500` in a row → wipe auth, set `needs_pair`, stop |
| `connectionReplaced` (440) | Guarded | wait 30–60s; if it recurs, **stop** and alert (duplicate consumer) |
| `multideviceMismatch` (411) | No (auto) | stop, log "update Baileys" |
| `forbidden` (403) | **No** | stop, mark `banned`, alert |
| `loggedOut` (401) | **No** | wipe auth, mark `logged_out` (already done) |

Add: a per-account reconnect-attempt counter reset on `open`, a max-attempt
ceiling (e.g. 10 consecutive failures → `unhealthy`, stop and alert), and full
jitter so accounts don't reconnect in lockstep. The generic
"reconnect-unless-401" pattern the community recommends
([connecting docs](https://baileys.wiki/docs/socket/connecting/)) is the
*floor*; the table above is what a messaging gateway holding real contact
conversations should do.

---

## 6. Detecting unavailable / banned accounts

**Reliable ban signals, fastest → slowest:**

1. **`403 forbidden` on `connection.update` close** — the single most direct
   "WhatsApp is blocking you" signal (+40 in the community risk model —
   [baileys-antiban](https://github.com/kobie3717/baileys-antiban)). Detectable in
   seconds.
2. **`401 loggedOut` with no operator unlink** — treat as ban until proven
   otherwise, especially if it follows outbound sends.
3. **`reachoutTimeLock.isActive === true`** (native v7 — see §7) — WhatsApp has
   put the account in a state where *"WhatsApp prevents outgoing messages and
   calls"* (`lib/Types/State.d.ts:39`). This is a **throttle/enforcement** signal
   that arrives *before* a hard ban.
4. **`message-capping.update` → `capping_status` = `FIRST_WARNING` /
   `SECOND_WARNING` / `CAPPED`** (native v7) — WhatsApp is explicitly warning
   that new-chat volume is over the line. `CAPPED` = new-chat sends will be
   refused. This is the earliest, most actionable pre-ban signal available.
5. **Send-failure pattern** — messages never advancing past `SERVER_ACK` (status
   2) to `DELIVERY_ACK` (3) across *multiple distinct recipients* (§9). A single
   stuck send = recipient offline; a *pattern* = the account's sends are being
   silently dropped.
6. **Repeated `connecting`→`close` with no `open`** — a churn loop that never
   stabilises often precedes/accompanies enforcement.

**Speed:** hard bans surface within seconds (the `403`/`401` close fires
immediately). Soft throttles (`reachoutTimeLock`, capping warnings) can precede a
ban by hours to days — which is the whole point of watching them. Bans are
**permanent with no appeal** on unofficial APIs
([Medium: why WhatsApp bans](https://medium.com/@shubhodey/why-whatsapp-bans-accounts-and-how-to-make-sure-it-never-happens-to-you-5790a5626a0d)).

---

## 7. Account-health monitoring — the ideal signal set

A status-event push to Django is being built in parallel. Recommended signal set,
in priority order. **The top three are native to the installed v7 and currently
unused by the gateway** — wiring them is the highest-leverage health work.

**A. Native WhatsApp enforcement telemetry (v7, not yet used):**

- **`reachoutTimeLock`** on `connection.update`
  (`ConnectionState.reachoutTimeLock: ReachoutTimelockState` —
  `lib/Types/State.d.ts:33-46`). Fields: `isActive`, `timeEnforcementEnds`
  (Date), `enforcementType`. `enforcementType === "DEFAULT"` means *no*
  restriction; `WEB_COMPANION_ONLY`, `BIZ_QUALITY`, and the many
  `BIZ_COMMERCE_VIOLATION_*` values mean an active restriction. Also pollable via
  `sock.fetchAccountReachoutTimelock()` (`lib/Socket/socket.js:903`), whose
  docstring is literally *"Fetches your account's standing when it comes to
  restrictions."*
- **`message-capping.update`** event → `NewChatMessageCapInfo`
  (`lib/Types/State.d.ts:87-96`): `total_quota`, `used_quota`, cycle
  start/end timestamps, and `capping_status`
  (`NONE|FIRST_WARNING|SECOND_WARNING|CAPPED`). Pollable via
  `sock.fetchNewChatMessageCap()` (`socket.js:919`). This is WhatsApp's *own*
  new-chat (outbound-to-new-contact) allowance — exactly the metric an outbound
  queue should throttle against.

**B. Connection state machine (partly logged today):**

- `connection.update` → `connection` (`open`/`connecting`/`close`) and the
  `lastDisconnect` reason code — push the mapped state
  (`connected|reconnecting|banned|logged_out|needs_pair|unhealthy`) plus the raw
  code so Django can distinguish transient from terminal.
- `receivedPendingNotifications` / history-sync completion — "fully caught up"
  flag; useful to know a resumed session is actually live, not mid-sync.
- Reconnect-attempt count and time-in-state — a rising count is an early
  degradation signal.

**C. Delivery health (raw material already logged as `receipt`/`receipt2`):**

- Rolling ratio of sends reaching `DELIVERY_ACK` (3) vs stuck at `SERVER_ACK`
  (2), and count of `ERROR` (0). A dropping delivery ratio across recipients is a
  soft-ban signal.

**Recommended push payload (per account):**
`{ state, disconnect_code, reachout_locked, reachout_type, reachout_ends,
capping_status, new_chat_quota_used, new_chat_quota_total, delivery_ratio_1h,
reconnect_attempts, me, last_open_at }`. `banned` should be a first-class state
(distinct from `disconnected`) so the outbound queue halts on it and the operator
is alerted, not silently retried.

---

## 8. Automatic account switching — feasibility & risk

**Feasible to build, dangerous to automate.** Rotating the sender number the
moment one is throttled/banned is a **textbook ban-evasion pattern**, and
WhatsApp's anti-abuse explicitly targets exactly this: Meta has pursued legal
action against operators who spin up/rotate numbers for automated messaging
([SecurityWeek](https://www.securityweek.com/whatsapp-will-take-legal-action-against-automated-or-bulk-messaging/)),
and correlated behaviour across numbers on the same host/IP/pattern gets the
*replacement* numbers burned faster than the first.

**If failover is implemented at all, constrain it to safe practice:**

- **Never migrate an in-flight conversation** to a new number mid-thread —
  reads as evasion and confuses the contact. Use a fresh number only for **new**
  conversations.
- **Warm up every new number before it carries load** (§10) — a cold number put
  straight into rotation is the fastest way to lose it too.
- **Contact-first / inbound-first** — a fresh number should ideally only *reply*
  to contacts who message it; don't have it cold-open outreach.
- **Keep numbers isolated** — distinct devices/sessions, avoid one host
  fingerprint fanning out across many numbers, don't reuse the same message
  templates verbatim across numbers.
- **Manual gate** — surface "account X is throttled/banned, promote standby Y?"
  to an operator rather than auto-swapping. Automated rotation on ban is the
  behaviour most likely to convert one banned number into a banned *fleet*.
- **Cap the fleet** — a handful of well-warmed numbers beats a churn of fresh
  ones; the churn *is* the signal.

Verdict: treat multiple numbers as **capacity with independent health**, not as
**automatic ban insurance**. Auto-switching as ban-evasion increases total risk.

---

## 9. Message-failure handling (ack levels & silent failures)

WhatsApp message status ladder (from `proto.WebMessageInfo.Status`, confirmed in
`WAProto/index.js` and `STATUS_MAP` in `lib/Utils/generics.js:265`):

| Status | Value | Meaning |
| --- | --- | --- |
| `ERROR` | 0 | Send failed |
| `PENDING` | 1 | Queued locally, not yet acked by server |
| `SERVER_ACK` | 2 | **WhatsApp server accepted** the message |
| `DELIVERY_ACK` | 3 | **Delivered to recipient device** |
| `READ` | 4 | Read by recipient |
| `PLAYED` | 5 | Voice/video note played |

The gateway already logs these via `messages.update` and
`message-receipt.update` (`index.js:216-229`), and its own comment nails the key
insight: *"A send stuck at 2 forever = WhatsApp accepted it but delivered
nowhere"* (`index.js:214-215`).

**Detecting silent failures:**

- `sock.sendMessage()` resolving only guarantees the message was *submitted* — it
  returns a message key, not delivery. Real success is the later transition
  `2 → 3`.
- **Track per-message: does it reach `DELIVERY_ACK` within N seconds?** A single
  stuck-at-2 = recipient offline (will deliver later) or not on WhatsApp. **Many
  distinct recipients stuck at 2** = the account's outbound is being silently
  dropped — a soft-ban indicator that should feed the health signal (§7C).
- `ERROR` (0) or a `messages.update` carrying a failure = hard send failure;
  retry once with backoff, then surface to the operator. Baileys already retries
  transient send failures internally up to `maxMsgRetryCount = 5` with
  `retryRequestDelayMs = 250` (`lib/Defaults/index.js:58-59`) — so an application
  visible failure has already exhausted the cheap retries.
- Today the gateway only *logs* receipts; it does not correlate them back to the
  Django message record. Feeding `SERVER_ACK`/`DELIVERY_ACK` back to Django would
  turn "sent" into "delivered", and expose the stuck-at-2 pattern.

---

## 10. Ban-risk factors & mitigation (inbound-first focus)

**Why inbound-first changes everything.** WhatsApp's anti-spam weighs: are you
messaging people who *expect* to hear from you, is your pace human, are
recipients *replying* (vs ignoring/blocking/reporting), and — added in 2026 — an
**unanswered-message count** (too many no-reply sends → flagged)
([wapisimo.dev](https://wapisimo.dev/blog/en/whatsapp-unofficial-api-ban-risk),
[achiya-automation](https://achiya-automation.com/en/blog/whatsapp-spam-detection-2026/)).
A customer-initiated thread satisfies every one of these: the recipient opted in,
replies are guaranteed (they started it), and reports are unlikely.

**The evidence for the split:**

- Reactive/inbound-only bots: **<2% ban over 12 months.**
- Proactive outreach to fresh contacts: **15–30% ban over 12 months.**
  ([wapisimo.dev](https://wapisimo.dev/blog/en/whatsapp-unofficial-api-ban-risk))
- Broad "unofficial API" figure: **~1 in 5 (20%) banned within a year**, ban is
  permanent, no appeal
  ([Medium](https://medium.com/@shubhodey/why-whatsapp-bans-accounts-and-how-to-make-sure-it-never-happens-to-you-5790a5626a0d)).

**How our SOP maps to this:** because the SOP requires the customer to contact us
first, replies inside an open, customer-initiated thread are the **safest class
of traffic**. The risk creeps in the moment the outbound queue sends the *first*
message to someone who hasn't written in this cycle — that is a "new chat," which
is exactly what `NewChatMessageCapInfo` (§7) meters. So: **maximise replies inside
existing threads; minimise and meter cold new-chat opens.**

**Concrete pacing (community/vendor evidence — treat as ceilings, not targets):**

The `baileys-antiban` middleware ships these defaults after live stress-testing
1000 messages with zero bans at ~12 msg/min and ~4.7s average human-like spacing
([discussion #2357](https://github.com/WhiskeySockets/Baileys/discussions/2357),
[kobie3717/baileys-antiban](https://github.com/kobie3717/baileys-antiban)):

| Knob | Value |
| --- | --- |
| Max per minute | 8 |
| Max per hour | 200 |
| Max per day | 1,500 |
| Inter-message delay | 1,500–5,000 ms, gaussian jitter |
| New-chat penalty | +3,000 ms extra before first message to an unknown recipient |
| Stranger delay multiplier | 2.5× (vs 1.0× for saved contacts) |

**Warm-up schedule for a fresh number (7-day ramp, ~1.8× daily growth):**

| Day | Cap (msgs) |
| --- | --- |
| 1 | 20 |
| 2 | 36 |
| 3 | 65 |
| 4 | 117 |
| 5 | 210 |
| 6 | 378 |
| 7 | 680 |
| 8+ | full capacity |

Re-enters warm-up after ~72h inactivity. Source: same middleware. Other community
guidance is more conservative still (start 10→20→30/day and ramp slowly).

**Recommended pacing for *this* platform (deliberately conservative):**

- **Fresh number:** follow the 7-day ramp above but treat the numbers as
  **new-chat opens per day**, and keep new-chat opens well under the
  `NewChatMessageCap` WhatsApp reports for the account. Realistically **≤20–30
  new conversations/day** in week one, replies unlimited within reason.
- **Warmed number:** cap **outbound new-chat opens at ~1 per 3–5 min and a few
  hundred/day max**, never the raw 200/hr ceiling. Blocks and "report spam" taps
  weigh far heavier than raw volume, so any use case where recipients are likely
  to be annoyed should sit well below these numbers.
- **Always:** randomised 2–6s spacing with jitter (the gateway already does
  900–2700ms "composing" presence before a send — `index.js:280-285` — keep and
  extend it), business-hours + quiet-hours respect (the WhatsApp app already has
  quiet-hours settings — reuse them), and **stop immediately on `reachoutTimeLock`
  or `capping_status >= FIRST_WARNING`.**
- **Get the number saved as a contact** by your contacts where possible (via the
  official onboarding/opt-in) — saved-contact traffic is treated as lower risk
  (1.0× vs 2.5× stranger multiplier above).
- **Watch the reply ratio** — if outbound new-chats routinely go unanswered,
  throttle harder; the 2026 unanswered-message counter penalises exactly this.

---

## 11. Production verdict

**Dev/demo only. Do not run production outreach on Baileys in
production.** Evidence-based assessment:

**Realistic ban exposure.** Even in the favourable inbound-first regime, reactive
bots still see **<2%/yr** bans, and any outbound component pushes toward the
**15–30%/yr** band; the blended unofficial-API figure is **~20%/yr**, permanent,
no appeal
([wapisimo.dev](https://wapisimo.dev/blog/en/whatsapp-unofficial-api-ban-risk),
[Medium](https://medium.com/@shubhodey/why-whatsapp-bans-accounts-and-how-to-make-sure-it-never-happens-to-you-5790a5626a0d)).
Any use case that structurally attracts blocks and "report spam" from unhappy
recipients — chasing money, cold outreach, anything the recipient did not ask for
— should assume the **worse end** of that range. A banned number means the
messaging channel for those contacts goes dark mid-workflow with no recovery.

**Financial / legal exposure.** (1) Numbers are disposable but the *conversation
history and trust* attached to them are not — a ban mid-campaign is a real
operational loss. (2) Meta's ToS prohibits automated/bulk messaging and it
**actively pursues legal action** against operators who do it, including evidence
gathered off-platform and against number-rotation schemes
([SecurityWeek](https://www.securityweek.com/whatsapp-will-take-legal-action-against-automated-or-bulk-messaging/)).
(3) If your industry is itself regulated (financial services, healthcare, debt
recovery…), conducting it over a channel the platform owner has declared
unauthorised compounds the compliance risk rather than being a separate one.
(4) `useMultiFileAuthState` on disk is a credential-custody liability.

**Recommended posture:**

- **Baileys = development / demo / internal testing only**, on a dedicated spare
  number, low volume — exactly as `README.md` already
  state. Voice-call support in `calls.js` is *higher* risk still (automated
  calling is called out as the fastest way to get banned).
- **Production = WhatsApp Cloud API** (official Business Platform), which the
  codebase already implements (`backend/whatsapp/` Cloud path). It gives
  template-governed outbound, no ban-by-automation risk, delivery receipts, and
  legal standing. The one gap — WhatsApp *voice calls* — is why the in-house
  bridge exists; that stays experimental until Meta's Business Calling beta is
  generally available.

**Migration path (Baileys → Cloud API):**

1. Keep the transport abstraction the codebase already has (`provider="baileys"`
   vs Cloud accounts routed by the same store layer) — messaging code is
   provider-agnostic, so migration is mostly account-config + capability flags.
2. Move outbound to **template messages** (Cloud API requires a template to open
   a conversation outside the 24h window — the codebase already has template
   support on the Cloud path). This *forces* the inbound-first discipline that
   also minimises Baileys risk, so building it now pays off either way.
3. Port health/delivery signals to the Cloud webhook's `statuses` (sent /
   delivered / read / failed) — same delivery-ladder semantics as §9, so the
   Django-side consumer built for the gateway's health push largely carries over.
4. Retire Baileys to a feature-flagged dev transport; keep the voice bridge as an
   explicitly-experimental capability pending official calling.

---

## Recommendations for this codebase (concrete)

Changes later work can implement in `backend/baileys_service/index.js` (and the
Django side that consumes the status push). None are applied here — this is a
research doc.

1. **Replace the single fixed-3s reconnect with the reason-aware backoff table
   in §5.** Specifically: stop reconnecting on `403 forbidden`; wipe+re-pair on a
   second consecutive `500 badSession`; back off hard (or stop + alert) on `440
   connectionReplaced`; stop + "update Baileys" on `411`; exponential backoff
   with ±20% jitter and a 60s–5min cap for `428`/`408`/`503`; keep the existing
   `401` wipe. Add a per-account reconnect-attempt counter (reset on `open`) and
   an `unhealthy` stop-state after ~10 consecutive failures.

2. **Add a `banned` account state** distinct from `disconnected`, set on `403`
   and on `401`-without-operator-action, and surface it in `GET
   /accounts/:id/status`. The outbound queue must halt on `banned`, not retry.

3. **Wire the native v7 health telemetry** (currently unused):
   - Handle `reachoutTimeLock` on `connection.update` and poll
     `sock.fetchAccountReachoutTimelock()` periodically → push `reachout_locked`
     / `reachout_type` / `reachout_ends`.
   - Subscribe to the `message-capping.update` event and/or poll
     `sock.fetchNewChatMessageCap()` → push `capping_status` +
     `used_quota`/`total_quota`.
   - **Gate the outbound queue on these**: pause new-chat opens on
     `capping_status >= FIRST_WARNING` or `reachoutTimeLock.isActive`.

4. **Correlate receipts back to Django.** The gateway already logs
   `messages.update` status transitions (`index.js:216-229`) — push `2→3`
   (delivered) and `0`/stuck-at-2 patterns to Django so "sent" becomes
   "delivered" and silent-drop bans are detectable (§9).

5. **Harden auth storage.** Atomic-rename `creds.json` writes (write temp +
   rename) to survive a crash mid-write; optionally migrate to a DB-backed
   `AuthenticationState` (the library's own recommendation) given
   `useMultiFileAuthState` is flagged not-for-prod. Back up `creds.json` so a
   corrupt file doesn't force a re-pair.

6. **Pacing defaults for the outbound conversation queue** (new module, not the
   gateway): human jitter 2,000–6,000 ms between sends; **new-chat opens capped
   per the warm-up ramp in §10** and never exceeding the WhatsApp-reported
   new-chat quota; warmed-number ceiling ~1 new-chat / 3–5 min and a few
   hundred/day; respect quiet-hours; **hard-stop on any throttle signal.** Keep
   and extend the existing pre-send `composing` presence (`index.js:280-285`).

7. **Do not auto-switch sender numbers as ban failover** (§8). If a standby-number
   feature is built, make promotion operator-gated, warm up standbys before use,
   and only route *new* conversations to them.

8. **Surface the 14-day rule to operators.** If an account's paired phone hasn't
   been seen (no successful `open`/heartbeat) in ~10 days, warn that the link will
   expire — a cheap way to avoid surprise mass re-pairs.

---

### Source index

- Installed package source: `node_modules/@whiskeysockets/baileys@7.0.0-rc13`
  — `lib/Types/index.js` (DisconnectReason), `lib/Types/State.d.ts` +
  `State.js` (reachoutTimeLock / NewChatMessageCapInfo), `lib/Socket/socket.js`
  (CB:failure / CB:stream:error / fetchAccountReachoutTimelock /
  fetchNewChatMessageCap / QR timing), `lib/Utils/generics.js`
  (getErrorCodeFromStreamError / getCodeFromWSError / STATUS_MAP),
  `lib/Utils/use-multi-file-auth-state.js`, `lib/Utils/auth-utils.js`
  (initAuthCreds), `lib/Defaults/index.js` (timeouts).
- Gateway code: `backend/baileys_service/index.js`, `calls.js`, `README.md`.
- Baileys docs: <https://baileys.wiki/docs/socket/connecting/>,
  <https://baileys.wiki/docs/api/enumerations/DisconnectReason/>,
  <https://whiskeysockets-baileys-94.mintlify.app/concepts/connection>.
- Baileys GitHub: [#1869 high bans](https://github.com/WhiskeySockets/Baileys/issues/1869),
  [#2260 getting banned](https://github.com/WhiskeySockets/Baileys/issues/2260),
  [#1625 428 after ~24h](https://github.com/WhiskeySockets/Baileys/issues/1625),
  [discussion #2357 anti-ban stress test](https://github.com/WhiskeySockets/Baileys/discussions/2357),
  [kobie3717/baileys-antiban](https://github.com/kobie3717/baileys-antiban).
- Ban/session evidence: [wapisimo.dev](https://wapisimo.dev/blog/en/whatsapp-unofficial-api-ban-risk),
  [Medium — why WhatsApp bans](https://medium.com/@shubhodey/why-whatsapp-bans-accounts-and-how-to-make-sure-it-never-happens-to-you-5790a5626a0d),
  [achiya-automation 2026 spam detection](https://achiya-automation.com/en/blog/whatsapp-spam-detection-2026/),
  [green-api crash FAQ](https://green-api.com/en/docs/faq/why-does-the-crash-happen/),
  [WhatsApp Help — linked devices](https://faq.whatsapp.com/378279804439436/),
  [TechRadar — linked-device logout](https://www.techradar.com/news/whatsapp-may-kick-you-out-of-linked-devices-for-security-reasons),
  [SecurityWeek — WhatsApp legal action on bulk messaging](https://www.securityweek.com/whatsapp-will-take-legal-action-against-automated-or-bulk-messaging/).
</content>
</invoke>
