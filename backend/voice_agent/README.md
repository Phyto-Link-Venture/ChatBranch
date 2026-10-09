# Voice agent worker

The LiveKit Agents worker that runs every AI voice call — SIP (PSTN via a
Twilio trunk) and WhatsApp (the Baileys gateway joins the same LiveKit room).
Node/TypeScript on the official [`@livekit/agents`](https://docs.livekit.io/agents/)
SDK, pinned to **one** version across every `@livekit/*` package (skew between
them breaks imports — bump them together).

It is a **separate long-lived process**: the C# API dispatches it by name
(`AGENT_NAME`) and never imports it. The two share state through files only.

## Run

```bash
npm install                 # or, from the repo root: npm run setup:agent
npm run dev                 # development worker (tsx, pretty logs)
npm start                   # production: builds to dist/ then `node dist/agent.js start`
```

From the repo root, `npm run dev:agent` (or `npm run dev:all`) does the same.
Without `LIVEKIT_URL` / `LIVEKIT_API_KEY` / `LIVEKIT_API_SECRET` and the
realtime provider's key the worker exits at once with the missing names —
everything else in ChatBranch keeps running.

| Script | What it does |
|---|---|
| `npm run selftest` | Offline checks: metadata, prompts, call-ending cues, file IPC, WAV writer, WebRTC VAD, provider wiring. No network, no keys. |
| `npm run typecheck` | `tsc --noEmit` (strict). |
| `npm run setup-trunk` | One-time: create the LiveKit outbound SIP trunk, print `SIP_OUTBOUND_TRUNK_ID`. |
| `npm run dial -- +60123456789 "Aisyah" ms` | Manual test call over the trunk (`--script "text"` for a script call). |
| `npm run download-files` | Pre-fetch plugin model files (Docker layer caching). |

## Configuration

Env comes from `backend/.env` (shell-exported variables win). The annotated
list lives in `backend/.env.example`; the essentials:

- `LIVEKIT_URL`, `LIVEKIT_API_KEY`, `LIVEKIT_API_SECRET` — the LiveKit project.
- `REALTIME_PROVIDER` = `openai` (default, needs `OPENAI_API_KEY`; the Realtime
  API is not on OpenRouter) or `gemini` (needs `GOOGLE_API_KEY`).
- `ELEVENLABS_API_KEY` / `ILMU_API_KEY` — the cascade voices.
- `AGENT_NAME` — must match the API's (dispatch is by name).
- `CHATBRANCH_DATA_DIR` — the directory shared with the API. Default
  `backend/data`; set it to the same absolute path for both processes when
  either runs elsewhere (Docker: `/data`).
- Tuning: `VAD_SILENCE_MS` (900), `ANSWER_SETTLE_MS` (1500),
  `SILENCE_NUDGE_SECONDS` (12), `SILENCE_HANGUP_SECONDS` (120),
  `MAX_CALL_SECONDS` (300), `VOICE_GATE_MODE` (`vad`).

### SIP trunk (once)

1. In Twilio: an Elastic SIP Trunk with a Termination URI and a Credential List.
2. Fill `CALLER_ID`, `TWILIO_TERMINATION_URI`, `TWILIO_SIP_USERNAME`,
   `TWILIO_SIP_PASSWORD` in `backend/.env`.
3. `npm run setup-trunk`, then copy the printed `SIP_OUTBOUND_TRUNK_ID` into
   `backend/.env` (the API reads it too — it places the calls).

## How a call runs

1. The API (`POST /api/calls/dial`, or a WhatsApp call start) creates an agent
   dispatch carrying the **dispatch metadata** (`src/metadata.ts` documents every
   key: contact, record, objective, memory/knowledge/skills blocks,
   `voice_provider`/`voice`, `llm_provider`/`stt_provider`, `script`), then adds
   the phone participant.
2. `src/agent.ts` waits for the participant and for `sip.callStatus` /
   `wa.callStatus` to read `active` (greeting a ringing line plays into dead
   air), then waits `ANSWER_SETTLE_MS` and greets.
3. The session is a realtime model (`openai` / `gemini`) or a cascade
   (`elevenlabs` / `ilmu` voice, with an OpenAI / ILMU / OpenRouter / Gemini
   brain) — `src/session.ts`. Script calls read `script` verbatim with audio
   input disabled, then hang up.
4. Endings: the `end_call` tool; a spoken `{end_call}` marker (scrubbed from the
   transcript); an agent goodbye after a real exchange; the silence watchdog;
   the `MAX_CALL_SECONDS` failsafe; the caller hanging up. Each records who
   ended the call (`ended_by`).
5. On shutdown: a JSON-mode summary, the record appended to `calls.json`, the
   final live write (`done`, `ended_by`, `llm_usage`), and the contact's memory file.

## Files shared with the API (`CHATBRANCH_DATA_DIR`)

| Path | Writer → reader | Shape |
|---|---|---|
| `live/{room}.json` | agent → API (polled) | `{turns:[{role,text,ts}], done, ended_by?, llm_usage?}` |
| `inbox/{room}.json` | API → agent (consume-once) | `[{mode:"guide"\|"takeover", text, ts}]` |
| `live/{room}.operator-end` | API → agent | marker written just before an operator's hang-up deletes the room, so `ended_by` reads `operator` |
| `calls.json` | agent → API | array of call records |
| `memory/{contact_id}.md` | both | frontmatter + `## Notes` bullets |
| `pronunciation.json` | API → agent | `{terms:[{term,alias}], dictionary_id?, version_id?}` |
| `recordings/{room}.wav` | agent → API | stereo 16 kHz, L = contact, R = agent |

Writes are atomic (temp file + rename).
