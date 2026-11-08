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

## HTTP API (all requests need `X-Baileys-Secret`)

| Route | What |
| --- | --- |
| `GET  /accounts/:id/status` | `{state, qr?, pairing_code?, pairing_phone?, me?, error?}` — pairing state (`state` `qr` = a QR is waiting, `pairing_code` = a code is waiting) |
| `POST /accounts/:id/start` | begin/refresh pairing. `{}` emits a QR (data URL); `{phone: "+60123456789"}` asks WhatsApp for an 8-character code to type on that phone instead (**400** for a number that cannot be one — no pairing attempt is spent, WhatsApp rate-limits them per number). A session that is already registered just reconnects, code or no code |
| `POST /accounts/:id/send` | `{to, text, quoted_id?, quoted_from_me?, quoted_text?}` → `{id}` (`quoted_*` = a reply quoting that message — the real one from the gateway's recent-message map, else rebuilt from `quoted_text`) |
| `POST /accounts/:id/react` | `{to, msg_id, from_me?, emoji}` → `{id}` (an emoji reaction; `""` removes ours) |
| `POST /accounts/:id/revoke` | `{to, msg_id, from_me?}` → `{id}` (“delete for everyone” on a message we sent; WhatsApp allows about two days) |
| `POST /accounts/:id/edit` | `{to, msg_id, from_me?, text}` → `{id}` (re-word a message we sent; WhatsApp allows about 15 minutes) |
| `POST /accounts/:id/forward` | `{to, msg_id}` → `{id}` (a true WhatsApp forward of a message the gateway still holds in its recent-message map — **409** once it doesn't, rather than re-sending the content unmarked) |
| `POST /accounts/:id/send-location` | `{to, lat, lng, name?, address?}` → `{id}` |
| `POST /accounts/:id/send-contact` | `{to, name, phones[], organization?}` → `{id}` (a vCard; the first phone is the one WhatsApp shows) |
| `POST /accounts/:id/send-poll` | `{to, question, options[], selectable_count?}` → `{id}` (2–12 distinct options; `selectableCount` is read too, and is what the C# client sends). Votes are NOT pushed back: WhatsApp encrypts each `pollUpdateMessage` against the poll's creation message, which this gateway only holds until it restarts |
| `POST /accounts/:id/send-media` | `{to, media_type: image\|video\|audio\|document, file_b64, mime?, filename?, caption?}` → `{id}` (≤16 MB, base64 in the body; paths/URLs refused) |
| `POST /accounts/:id/check` | `{to}` → `{exists, jid}` |
| `POST /accounts/:id/logout` | unpair + wipe auth state (ends any live call) |
| `POST /accounts/:id/call` | `{to, room, livekit_url, token, max_seconds}` → call snapshot |
| `POST /accounts/:id/call-end` | hang up the active call |
| `POST /accounts/:id/presence` | typing/online presence before an AI reply (best-effort) |
| `GET/POST /accounts/:id/settings` | `{keep_phone_notifications: bool}` — stay offline between sends so the phone keeps WhatsApp's notifications (`presence.js`; stored in the session dir's `settings.json`, applied to a live session at once) |
| `GET  /accounts/:id/call-status` | `{status, call_id, room?}` — the API's busy/stale-state probe |

## Setup / run

```powershell
npm install            # from backend/baileys_service (or `npm run setup` at repo root)
npm start              # or `npm run dev:baileys` / `npm run dev:all` at repo root
node calls.js --selftest   # offline: proves the WASM engine + LiveKit bindings load
                           # (+ media.js and messages.js — quotes, reactions, inbound parsing)
node messages.js --selftest  # just the quote / reaction / inbound-parsing half (no deps)
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
