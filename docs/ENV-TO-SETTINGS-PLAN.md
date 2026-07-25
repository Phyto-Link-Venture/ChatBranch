# ChatBranch — Move .env vars into frontend-editable Settings

Goal: make runtime configuration (LLM keys, provider selection, tunables)
editable from **Settings** instead of hardcoded in `.env`, so a deployment can
be re-configured without touching files or redeploying. Some vars must remain in
env — see the boundary below.

## The pattern already exists

`crm.Setting` rows read through accessor functions, **Setting-over-env with env
fallback**, masked on the way to the frontend (`••••1234`), admin/superadmin
gated, audit-logged. Already used for: Twilio REST voice keys
(`voice_twilio_settings`), integration OAuth client + webhook secrets
(`integrations_settings`), WhatsApp app keys / verify token, chat provider pin +
model, quiet hours, routing/queue knobs, persona. We extend the SAME pattern —
no new mechanism.

## The one constraint that decides everything

A variable can move to Settings only if it is **read live, per-request, by
Django**. The accessor hits the `Setting` table each time, so an edit takes
effect immediately.

A variable **cannot** move cleanly if it is read **once at process boot**, or by
a **Django-free process** that has no access to the DB:

- **Django boot / security**: `SECRET_KEY`, `DEBUG`, `ALLOWED_HOSTS`,
  `DATABASE_URL`, `CORS_*`, `CSRF_*`, `FRONTEND_URL`, `SEED_ADMIN_PASSWORD` —
  needed before the ORM/Setting table is even available.
- **Voice agent worker** (`backend/voice_agent/`, a separate LiveKit process,
  Django-free): reads `LIVEKIT_*`, SIP/trunk creds, `PIPELINE_LLM_PROVIDER`,
  `ILMU_API_KEY`, `REALTIME_*`, VAD/silence tunables, `ELEVENLABS_API_KEY` (for
  TTS) from its OWN env at boot/per-call. It cannot read a `Setting` row.
- **Baileys Node gateway** (`backend/baileys_service/`): reads `BAILEYS_SECRET`
  (trust handshake with Django), `BAILEYS_PORT`, `BAILEYS_URL`, log/gate config
  at Node boot.

For the Django-free processes, the existing escape hatch is **dispatch
metadata**: when Django starts a call it already passes non-secret selectors
(`voice_provider`, `voice`, `llm_provider`) into the agent via LiveKit dispatch
metadata, and the agent prefers those over its env. So provider/model/voice
*selections* can be made dashboard-editable and flow through metadata. But
**secret API keys should NOT be shipped through dispatch metadata** — that puts
credentials in a call-routing payload. So agent-side secret keys stay in env.

## The buckets

### A — MOVE to Settings (Django-runtime; safe; the bulk of the value)

Stored in Setting rows, masked, env fallback, one accessor each. Surfaced on a
new **Settings → AI & Providers** tab (or extend AI Configuration / App Keys).

| Var | Consumed by | Notes |
|---|---|---|
| `OPENAI_API_KEY` | chat brain, summaries, integrations | secret, masked |
| `OPENROUTER_API_KEY` | chat brain | secret, masked |
| `ILMU_API_KEY` (chat use) | chat brain when provider=ilmu | secret, masked — see split below |
| `PIPELINE_LLM_PROVIDER` | selector | non-secret dropdown; also passed to agent via metadata |
| `WHATSAPP_CHAT_MODEL` | chat brain | model name (some already editable) |
| `SUMMARY_MODEL` | summaries | model name |
| `ELEVENLABS_API_KEY` (Django use) | voice listing/cloning | secret, masked — see split |
| `EMAIL_DEFAULT_SUBJECT` | email send | trivial |
| `AICORE_SUMMARY_INTERVAL` / `_IDLE_MIN` | summary ticker | tunable (ticker re-reads each pass) |
| `FLOWS_TICK_SECONDS`, `WHATSAPP_HEALTH_INTERVAL` | tickers | tunable |

### B — Already dashboard-editable (verify only)

Twilio REST voice keys, Google/Shopify/Stripe integration keys + webhook
secrets, WhatsApp app keys/verify token, quiet hours, routing/queue gaps,
persona, chat provider pin. No new work; confirm they're surfaced.

### C — MUST stay in env (document, don't move)

`SECRET_KEY`, `DEBUG`, `ALLOWED_HOSTS`, `DATABASE_URL`, `CORS_ALLOWED_ORIGINS`,
`CSRF_TRUSTED_ORIGINS`, `FRONTEND_URL`, `SEED_ADMIN_PASSWORD`, `LIVEKIT_URL/
API_KEY/API_SECRET`, `CALLER_ID`, `SIP_OUTBOUND_TRUNK_ID`, `TWILIO_TERMINATION_
URI`, `TWILIO_SIP_USERNAME/PASSWORD`, `BAILEYS_SECRET/PORT/URL/LOG_LEVEL/GATE_*`,
and the agent-side use of `ILMU_API_KEY` / `ELEVENLABS_API_KEY` / `REALTIME_*` /
VAD/silence tunables.

### The ILMU / ElevenLabs split (the subtle part)

Both keys are **dual-use**: the chat brain (Django, runtime) AND the voice agent
(Django-free, boot) use them.

- **Chat/pipeline side** → make editable in Settings (Setting-over-env). Works
  immediately for chat replies, summaries, voice listing/cloning.
- **Voice-call side** → the agent keeps reading env at call time. Document this:
  "the AI chat uses the dashboard key; live voice calls read the key from the
  agent's `.env`." Provider/voice *selection* still flows to the agent via
  metadata; only the secret stays in env.

This honestly satisfies "make the ilmu key / pipeline provider editable" for
everything except the live-voice secret, which the architecture won't allow to
move without shipping secrets through call metadata.

## Owner decisions (2026-07-25)

- **Secrets in DB: YES.** Secret API keys move to masked Setting rows (env
  fallback), same as the existing Twilio/OAuth keys. README must state the DB
  now holds credentials.
- **Scope: everything Django-runtime movable** — the LLM/provider block PLUS
  `EMAIL_DEFAULT_SUBJECT`, `AICORE_SUMMARY_INTERVAL`/`_IDLE_MIN`,
  `FLOWS_TICK_SECONDS`, `WHATSAPP_HEALTH_INTERVAL`, and any other var read live
  by Django. Bucket C stays env.

## Security note (needs owner sign-off) — signed off above

Moving secret API keys from env into the DB means secrets live in
`db.sqlite3` (Setting rows) rather than the `.env` file. The existing pattern
already does this for Twilio/OAuth secrets and mitigates leakage with the
masked round-trip (keys never return unmasked over the API). Acceptable for this
POC/template; worth stating in the README so an adopter knows the DB now holds
credentials and should be protected accordingly.

## Delivery

- Backend: one accessor module (e.g. `core/app_settings.py` or extend
  `whatsapp/wa_config.py`) exposing `ai_settings()` / typed getters with
  Setting-over-env fallback; `GET/PUT /api/settings/ai-providers` (superadmin
  PUT, masked round-trip, audit `settings.update`).
- Refactor the movable getters' call sites to read the accessor instead of
  `os.getenv` directly.
- Frontend: **Settings → AI & Providers** tab (new `SETTINGS_TABS` entry + route
  + component), modelled on the Integrations keys panel — masked inputs, provider
  dropdown, save via `SettingsDirtyContext`.
- `.env.example`: annotate each var with `[dashboard-editable]` /
  `[env-only, boot]` so the boundary is documented at the source.
- Tests: accessor precedence (Setting beats env, env fallback when unset), mask
  round-trip (a `••`-containing value is treated as unchanged), superadmin gate.

## Sequencing

**Do NOT start while the blue-accent / sidebar redesign agent is editing
frontend `settings/*` files** — collision risk. This lands after that agent
finishes. The backend accessor work could begin earlier (no frontend overlap),
but keep it one pass for coherence.
