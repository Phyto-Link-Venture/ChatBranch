# Baileys gateway (unofficial WhatsApp Web sidecar)

A small Node sidecar that gives ChatBranch a second WhatsApp transport:
a **normal WhatsApp account paired by QR** (via
[@whiskeysockets/baileys](https://github.com/WhiskeySockets/Baileys)) instead
of a Meta Cloud API business number. The C# API is its only client
(`ChatBranch.Data/Sidecars/IBaileysGateway.cs`, shared `X-Baileys-Secret`
header); the gateway pushes back to the API's `/api/whatsapp/baileys/*` routes.

> ⚠ **ToS warning.** Baileys speaks WhatsApp's unofficial Web protocol.
> Automated messaging — and *especially* automated calling — violates
> WhatsApp's Terms of Service and gets numbers banned quickly (community
> evidence puts it at <2%/yr for inbound-first bots and 15–30%/yr once there is
> a cold-outbound component; bans are permanent with no appeal). **This
> transport is for development and demos only — do not run production outreach
> on it.** It exists because Meta's Business Calling beta is gated. Use a
> dedicated spare number and keep volumes low. Evidence, the DisconnectReason
> table and the pacing numbers the outbound queue's defaults come from:
> [RESEARCH.md](RESEARCH.md).

## Processes & files

- `history.js` — the pure half of importing a number's existing chats: which
  replayed messages are importable, and the window — plus the whole decision
  surface of the **on-demand history fetch** below (cursors, caps, pacing, the
  stop conditions, the run's own bookkeeping), all self-tested offline
  (`node history.js --selftest`)
- `pairing.js` — the pure half of pairing: cleaning the number for
  "Link with phone number", formatting the code (`--selftest`)
- `index.js` — pairing, text + media send/receive, HTTP control API (port
  `BAILEYS_PORT`, default 3020). One WhatsApp Web session per
  `crm.ChannelAccount` row with `provider="baileys"`; auth state persists in
  `backend/data/whatsapp/baileys/<account_id>/`.
- `presence.js` — **keep phone notifications**: WhatsApp silences the phone while a
  linked device shows as online, so a number with the setting stays `unavailable`
  and comes online only around a send / typing, quiet again `BAILEYS_PRESENCE_IDLE_MS`
  (default 30 000) later. Pure + self-tested (`node presence.js --selftest`, also
  run by `calls.js --selftest`).
- **Messages sent from the phone** (`index.js` `pushFromPhone`, rule in `messages.js`
  `phoneSent`): a live message from the number itself that this gateway did not send
  (`ownSends` holds its own ids), 1:1, under 15 minutes old, not a reaction → pushed
  to the API's `/api/whatsapp/baileys/from-phone`, which records it as the person's
  reply (a file follows on `/baileys/media` with `from_me=1`).
- **"Delete for everyone"** (`index.js` `pushRecall`, rule in `messages.js` `recallOf`):
  a live REVOKE `protocolMessage` in a 1:1 chat — the customer's, or one made on the
  number's own phone (`from_me`) — is pushed to `/api/whatsapp/baileys/recall` with the
  id of the message taken back; it is never a bubble or a "sent from the phone" reply.
- **Edits** (`index.js` `pushEdit`, rule in `messages.js` `editOf`): a live
  `MESSAGE_EDIT` `protocolMessage` in a 1:1 chat — the customer re-wording their own
  message, or one re-worded on the number's own phone (`from_me`) — is pushed to
  `/api/whatsapp/baileys/edited` with the id and the NEW wording, which updates that
  row rather than adding a bubble. An edit whose new wording cannot be read is
  dropped rather than blanking the message.
- `calls.js` — **voice calls**. Composes the internals of
  [baileys-caller](https://github.com/SheIITear/baileys-caller) (MIT,
  WhatsApp Web's own VoIP WASM stack) around the *existing* account socket —
  its top-level `VoipClient` is deliberately not used, because that would
  open a second WhatsApp connection with the same creds and the server kicks
  one (stream conflict). Loaded lazily on the first call.

## Voice call flow

```
operator (/chats)  →  C# API POST /api/whatsapp/threads/{key}/call/start
or a queued flow call  (QueueCallPlacer, transport "baileys")
                          1. dispatch the voice agent into a fresh LiveKit room
                             (VoiceDispatch metadata + LiveKitClient)
                          2. POST /accounts/:id/call  {to, room, livekit_url, token}
gateway calls.js       3. resolveLid → discover devices → signal sessions → startCall
                       4. joins the room as "whatsapp_contact":
                            contact audio  = WASM playback → LiveKit track
                            agent audio     = LiveKit track → paced sendAudioData
                       5. pushes ringing/connected/ended to
                          POST /api/whatsapp/baileys/call-event  (X-Baileys-Secret)
C# API                 6. mirrors events onto the thread → /chats updates via SSE
```

No per-call bridge process, no ffmpeg, no signal files — the audio loop runs
in-process at 16 kHz mono. One call at a time per gateway (bandwidth + WASM
sanity). Known library limits: outbound 1:1 audio only — no inbound calls, no
groups, no video.

## Group chats

A message in a group the number belongs to is pushed to `/api/whatsapp/baileys/inbound`
keyed by the group itself: `wa_id` = the full `…@g.us` jid (never digits), plus
`group: {jid, subject}`, `sender: {id, name, phone}` (the participant jid, push name,
and phone digits when WhatsApp reveals them) and `mentioned` (an @mention of our
number / lid, or a reply quoting our message — `messages.js mentionsMe`). Group files
add `sender_id` / `sender_name` to the `/media` form; reactions and delivery ticks
use the group jid as `wa_id`. Group metadata is cached per number for 1 h (and handed
to Baileys as `cachedGroupMetadata`), dropped on a membership change, renamed in place
on `groups.update`. Status updates, broadcast lists and channels are never pushed
(`chatKind`). History import, "sent from the phone", recalls and edits stay 1:1-only.

## HTTP API (all requests need `X-Baileys-Secret`)

| Route | What |
| --- | --- |
| `GET  /accounts/:id/status` | `{state, qr?, pairing_code?, pairing_phone?, me?, error?}` — pairing state (`state` `qr` = a QR is waiting, `pairing_code` = a code is waiting) |
| `POST /accounts/:id/start` | begin/refresh pairing. `{}` emits a QR (data URL); `{phone: "+60123456789"}` asks WhatsApp for an 8-character code to type on that phone instead (**400** for a number that cannot be one — no pairing attempt is spent, WhatsApp rate-limits them per number). A session that is already registered just reconnects, code or no code |
| `POST /accounts/:id/send` | `{to, text, quoted_id?, quoted_from_me?, quoted_text?, quoted_participant?}` → `{id}` (`quoted_*` = a reply quoting that message — the real one from the gateway's recent-message map, else rebuilt from `quoted_text`; `quoted_participant` = in a group, the author jid of someone else's quoted message). `to` may be a group jid (`…@g.us`) — sent as is, no number lookup |
| `POST /accounts/:id/react` | `{to, msg_id, from_me?, emoji, participant?}` → `{id}` (an emoji reaction; `""` removes ours; `participant` = in a group, the author jid of the message reacted to) |
| `POST /accounts/:id/revoke` | `{to, msg_id, from_me?}` → `{id}` (“delete for everyone” on a message we sent; WhatsApp allows about two days) |
| `POST /accounts/:id/edit` | `{to, msg_id, from_me?, text}` → `{id}` (re-word a message we sent; WhatsApp allows about 15 minutes) |
| `POST /accounts/:id/forward` | `{to, msg_id}` → `{id}` (a true WhatsApp forward of a message the gateway still holds in its recent-message map — **409** once it doesn't, rather than re-sending the content unmarked) |
| `POST /accounts/:id/send-location` | `{to, lat, lng, name?, address?}` → `{id}` |
| `POST /accounts/:id/send-contact` | `{to, name, phones[], organization?}` → `{id}` (a vCard; the first phone is the one WhatsApp shows) |
| `POST /accounts/:id/send-poll` | `{to, question, options[], selectable_count?}` → `{id}` (2–12 distinct options; `selectableCount` is read too, and is what the C# client sends). Votes are NOT pushed back: WhatsApp encrypts each `pollUpdateMessage` against the poll's creation message, which this gateway only holds until it restarts |
| `POST /accounts/:id/send-media` | `{to, media_type: image\|video\|audio\|document\|sticker, file_b64, mime?, filename?, caption?}` → `{id}` (≤16 MB, base64 in the body; paths/URLs refused). `media_type: sticker` is its own kind, not an image: `image/webp` only, ≤500 KB, checked by RIFF magic, and it carries neither a caption nor a file name because WhatsApp shows neither. The API shapes the picture (512×512, EXIF pack) before it gets here — this end re-checks it anyway, because the gateway must not depend on its caller being right |
| `POST /accounts/:id/check` | `{to}` → `{exists, jid}` |
| `POST /accounts/:id/logout` | unpair + wipe auth state (ends any live call) |
| `POST /accounts/:id/call` | `{to, room, livekit_url, token, max_seconds}` → call snapshot |
| `POST /accounts/:id/call-end` | hang up the active call |
| `POST /accounts/:id/presence` | typing/online presence before an AI reply (best-effort) |
| `GET  /accounts/:id/history-fetch` | progress of the on-demand history run (below): requests made + the cap, messages WhatsApp served, messages the inbox took, per-chat rows and why it stopped. Reads the gateway's own bookkeeping only — it asks WhatsApp for nothing |
| `POST /accounts/:id/history-fetch` | `{depth?, wa_id?, chats?: [{wa_id, msg_id, from_me, ts}]}` — start ONE run (below). `chats` are seed anchors from the API's own rows; `wa_id` narrows it to one chat. **409** when the number is not connected, a run is already going, history import is off for it, or no chat has an anchor; **429** inside the cooldown or once the day's allowance is spent |
| `POST /accounts/:id/history-fetch-stop` | stop the run in progress (idempotent). What landed stays, and so do the cursors |
| `GET/POST /accounts/:id/settings` | `{keep_phone_notifications?: bool, import_history_days?: 0\|30\|90\|-1}` — stay offline between sends so the phone keeps WhatsApp's notifications (`presence.js`), and how far back to import the number's existing chats when it links (`history.js`; read at socket creation, so the API hands it over before each pairing attempt). Stored in the session dir's `settings.json`; the presence setting is applied to a live session at once |
| `GET  /accounts/:id/call-status` | `{status, call_id, room?}` — the API's busy/stale-state probe |

## On-demand history (fetching OLDER messages)

WhatsApp's replay when a number is linked is **shallow**: it sends the chat LIST
and only the most recent messages. One measured number imported 287
conversations but 386 messages — about 1.3 per chat. The chat list arrived; the
depth did not, and `messaging-history.set` does not fire again.

`POST /accounts/:id/history-fetch` asks for the rest, chat by chat, with
Baileys' `sock.fetchMessageHistory(count, oldestMsgKey, oldestMsgTimestamp)`
(this version's own README passes `msg.messageTimestamp`, i.e. **seconds**, even
though the proto field is named `oldestMsgTimestampMs`; a run that gets nothing
at all for its first request spends ONE request trying milliseconds, then keeps
whichever unit answered next to the session) —
a `HISTORY_SYNC_ON_DEMAND` peer request whose answer comes back as another
`messaging-history.set`, with `syncType` `ON_DEMAND` (6). Rows are pushed
through the **same** `pushHistory` path as the first import, so the filtering
(1:1 chats, text and the `[image]`-style placeholders, the inbox's window),
the batching and the dedupe all stay in one place. **Historical media is still
never downloaded** — the files are usually long gone from WhatsApp's servers.

> ⚠ Every one of these requests is traffic no human generates, on a transport
> that already violates WhatsApp's Terms. A banned number is gone permanently.
> So the feature is built around restraint, not throughput:

| Guard | Value | Why |
| --- | --- | --- |
| Trigger | an operator only | never on pair, never on a timer, never from the AI's own schedule |
| In flight | 1 request | the next one is only chosen after this one's answer (or its timeout) |
| Pacing | jittered **20–45 s** between requests | slower than the outbound queue's own 10–30 s (`queue_min_gap_s` / `queue_max_gap_s`), because a peer request looks nothing like a person typing |
| Per request | 50 messages | the ceiling WhatsApp Web itself asks for (and Baileys' documented max) |
| Per chat, per run | 6 requests (≤ 300 messages) | the `depth` an operator asks for is clamped to this |
| Per run | 20 requests (≤ 1 000 messages) | one press of the button is 7–15 minutes of paced traffic |
| Per number, rolling 24 h | 60 requests (≤ 3 000 messages) | bounds the DAY, not just one run — at most three full runs |
| Between runs | 15 min cooldown | nothing can turn "one run" into continuous polling |
| Answer window | 90 s | no answer = WhatsApp served nothing (the phone may be offline) |

Overrides exist only in **this process's environment**, never from the API or
the dashboard, and every one is clamped (`fetchCaps`): `BAILEYS_HISTORY_PER_REQUEST`,
`_PER_CHAT`, `_PER_RUN`, `_PER_DAY`, `_MIN_GAP_S`, `_MAX_GAP_S`, `_EMPTY_STREAK`,
`_TIMEOUT_MS`, `_COOLDOWN_S`. The gap can never go below 5 s.

**A run stops** — and says which it was — when: every chat is as deep as
WhatsApp will serve, a chat's `endOfHistoryTransferType` says the phone holds
nothing older, two answers in a row bring nothing new, the cursor stops moving,
the chat is already back past the inbox's own `import_history_days` window
(asking further would only produce rows we then discard), the per-chat or
per-run cap is reached, the day's allowance is spent, the number goes offline,
or the operator presses Stop.

**One chat, one key.** WhatsApp hosts some conversations on a `<lid>@lid`
identity rather than the customer's number, and the API only ever knows the
number — so cursors, the run's chats and the report are all keyed by the
NUMBER, with the identity the anchor arrived under riding inside the cursor
(that is what the request names). PN↔LID pairs are learned free from each
sync's own `lidPnMappings` and from the mapping Baileys stored locally at
pairing; the answer is matched by number, so a request made on the phone jid
and answered under the lid still counts as the real answer it is.

**Resumable.** Each chat's oldest known message is kept in
`data/whatsapp/baileys/<id>/history-cursors.json` (next to the session, under
`CHATBRANCH_DATA_DIR` — never in the API's database), recorded from *every*
`messaging-history.set`, including the first import's. A cursor only ever walks
backwards, so a second run continues instead of re-asking, and the file also
holds the rolling 24 h request ledger and the timestamp unit WhatsApp answered
for. For a number paired before this feature existed the API seeds the cursors
from the oldest message it already holds per chat (`chats[]` in the request
body); a seed can only move a cursor further back.

**The ceiling is WhatsApp's, not ours.** It serves what the paired phone still
holds — and **that phone must be online**, or nothing comes back at all. The
progress payload is deliberately honest about it: requests *made*, messages
*served*, messages the inbox actually *took* (the API's own dedupe count), and
the reason it stopped. It is not a full archive and the UI never calls it one.

## Setup / run

```powershell
npm install            # from backend/baileys_service (or `npm run setup` at repo root)
npm start              # or `npm run dev:baileys` / `npm run dev:all` at repo root
node calls.js --selftest   # offline: proves the WASM engine + LiveKit bindings load
                           # (+ media.js and messages.js — quotes, reactions, inbound parsing)
node messages.js --selftest  # just the quote / reaction / inbound-parsing half (no deps)
node history.js --selftest   # the history import + the on-demand fetch's whole
                             # decision surface (cursors, caps, stop conditions)
```

Pair a number from **Channels → Add a channel → WhatsApp (QR)** (the setup
drawer shows the QR). Calls need `LIVEKIT_URL` /
`LIVEKIT_API_KEY` / `LIVEKIT_API_SECRET` in `backend/.env` and the voice
agent worker running — same requirements as every other call path.

Env (loaded from `backend/.env`; shell variables win): `BAILEYS_PORT` (3020),
`BAILEYS_HOST` (bind address, `127.0.0.1`; docker compose sets `0.0.0.0` so the
API container can reach it on the compose network — the port is not published),
`BAILEYS_SECRET` (shared with the API — Setting `baileys_secret` or env on the
API side; change the default), `API_URL` (push target; legacy `DJANGO_URL` still
accepted), `BAILEYS_LOG_LEVEL`, `BAILEYS_RECORD_CALLS`. Call length is capped by
`max_seconds` on the `/call` request (default 300).

## Debugging

- `data/whatsapp/baileys/events.log` — append-only trail: connections,
  message receipts, and every `call` lifecycle event.
- A call that never leaves `connecting` usually means the callee's device
  rejected the offer silently (ancient client, or the account tripped
  WhatsApp's spam scoring). `remote_end` right after `ringing` = declined.
- `calls.js --selftest` failing = native module problem
  (`@roamhq/wrtc` / `@livekit/rtc-node` prebuilds), not a WhatsApp problem.
