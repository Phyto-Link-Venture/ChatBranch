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

- `index.js` — pairing, text send/receive, HTTP control API (port
  `BAILEYS_PORT`, default 3020). One WhatsApp Web session per
  `crm.ChannelAccount` row with `provider="baileys"`; auth state persists in
  `backend/data/whatsapp/baileys/<account_id>/`.
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
| `GET  /accounts/:id/status` | `{state, qr?, me?, error?}` — pairing state |
| `POST /accounts/:id/start` | begin/refresh pairing (QR as data URL) |
| `POST /accounts/:id/send` | `{to, text}` → `{id}` |
| `POST /accounts/:id/check` | `{to}` → `{exists, jid}` |
| `POST /accounts/:id/logout` | unpair + wipe auth state (ends any live call) |
| `POST /accounts/:id/call` | `{to, room, livekit_url, token, max_seconds}` → call snapshot |
| `POST /accounts/:id/call-end` | hang up the active call |
| `POST /accounts/:id/presence` | typing/online presence before an AI reply (best-effort) |
| `GET  /accounts/:id/call-status` | `{status, call_id, room?}` — the API's busy/stale-state probe |

## Setup / run

```powershell
npm install            # from backend/baileys_service (or `npm run setup` at repo root)
npm start              # or `npm run dev:baileys` / `npm run dev:all` at repo root
node calls.js --selftest   # offline: proves the WASM engine + LiveKit bindings load
```

Pair a number from **Channels → Add a channel → WhatsApp (QR)** (the setup
drawer shows the QR). Calls need `LIVEKIT_URL` /
`LIVEKIT_API_KEY` / `LIVEKIT_API_SECRET` in `backend/.env` and the voice
agent worker running — same requirements as every other call path.

Env (loaded from `backend/.env`; shell variables win): `BAILEYS_PORT` (3020),
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
