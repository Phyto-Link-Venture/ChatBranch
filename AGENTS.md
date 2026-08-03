# AGENTS.md

Working rules for coding agents in this repo. **`CLAUDE.md` is the long form** —
read it before any non-trivial change. This file is the short version.

## What this is

**ChatBranch** — an industry-agnostic multi-channel conversation platform, shipped
as a template. WhatsApp (two transports), email, Telegram, Messenger, Instagram
and AI voice calls, behind one operator dashboard and one AI brain.
`README.md` is the user-facing entry point; `docs/vertical-guide.md` explains
how it gets configured for a business.

## Four processes

| # | Process | Path | Port |
|---|---|---|---|
| 1 | Django API | `backend/` | 8000 |
| 2 | Voice agent worker (LiveKit, **Django-free**) | `backend/voice_agent/` | — |
| 3 | Next.js dashboard | `frontend/` | 3000 |
| 4 | Baileys WhatsApp gateway (Node, optional) | `backend/baileys_service/` | 3020 |

```bash
npm run dev:all          # all four — cross-platform (Windows/macOS/Linux)
npm run dev:api | dev:agent | dev:web | dev:baileys
```

venv is at `backend/venv`. `scripts/setup-api.js` / `scripts/run-py.js`
resolve the right Python + venv path per OS — don't hardcode a
platform-specific path back into `package.json`.

## Verify before you claim done

```bash
cd backend  && venv/bin/python manage.py check              # Windows: venv\Scripts\python
cd backend  && venv/bin/python manage.py makemigrations --check --dry-run
cd backend  && venv/bin/python -m pytest tests/ calls/tests -q    # 863, keep green
cd frontend && npx tsc --noEmit
```

`manage.py check` emits one benign `staticfiles.W004`. There are no other known
failures — `tsc` is clean and there is no `frontend/__tests__/` directory.

## The rules that actually bite

- **Change the store layer, not the callers.** `whatsapp/store.py` and
  `messaging/store.py` are ORM-backed but keep old function signatures and dict
  shapes. Views, the AI, the bridges and the timeline all consume those dicts.
- **Never write `Message.objects.create` directly.** Go through the stores: they
  bump `Thread.updated_at` (which the `/chats` SSE change token watches), stamp
  `Message.account`, and honour the dedupe constraint. A queryset `.update()`
  that skips `updated_at` silently freezes the dashboard.
- **`calls.lk` is imported LAZILY**, inside the views that dial or poll. It
  pulls the whole LiveKit SDK (seconds). Same rule for `openai` / `httpx` in
  `whatsapp/ai.py`. Do not "tidy" these to the top of the module.
- **`whatsapp/health.py` is the only writer of `ChannelAccount.health_*`**, and
  `integrations/health.py` the only writer of `Integration.status`. Both fan out
  exactly one Notification + AuditLog per real transition.
- **`GET/PUT /api/aicore/skills` is the only writer of `crm.AgentSkill`.** The
  flow-builder library PUT deliberately ignores the `agentSkills` it receives —
  it used to prune them, so a stale browser tab deleted an operator's work.
- **Read config through accessors** (`wa_config.*`, `catalog.settings()`,
  `health.voice_settings()`, `persona.persona_text()`). Never re-add
  module-level constants: dashboard edits must apply without a restart.
- **Sandbox must never raise.** With no credentials, sends store locally with
  `status="sandbox"` and integration actions return
  `{ok: false, skipped: true, …}`. This is a hard product rule of the template.
- **Webhook views ack fast and never 500 a batch.** Side effects go on a daemon
  thread. A 500 makes the provider retry and duplicate the whole batch.
- **Never call `getRole()` / `getAuth()` during render** (frontend). They read
  `localStorage`, which does not exist during SSR, and break hydration. Use
  `useRole()` from `src/lib/useRole.ts`.
- **Use the semantic Tailwind tokens** (`app / surface / card / text-* / line /
  fill*`), not raw `white/α` or hardcoded slate hex — raw white-alpha does not
  flip and vanishes in light mode. Never put `var(--…)` in a lucide `color=`.

## Naming that looks wrong but is not

- `Message.source` has the **value** `"customer"` — that is who wrote the
  message (a speaker role), not a pointer at a model. Everything that points at
  a `crm.Contact` is called `contact` / `contact_id`.
- `frontend/src/lib/auth.ts` maps a cached `'collector'` session to `'agent'`.
  That is a deliberate back-compat shim for sessions predating the rename.

## Maturity — do not oversell in code comments or docs

- Baileys is an **unofficial, ToS-violating** WhatsApp transport: dev/demo only.
- WhatsApp Cloud voice calling is a **gated beta, never tested against a live
  Meta number**.
- Shopify/Stripe webhook mappings and the Google action HTTP shapes are
  **fixture-tested only** — no live provider has ever exercised them.
- `/api/messaging/webhook` (Messenger/Instagram) has **no signature
  verification**. Known, open gap — see `docs/channel-adapter.md`.
- SQLite, a dev `SECRET_KEY` and demo passwords are the shipped defaults.

## Docs map

`README.md` · `docs/vertical-guide.md` · `docs/DATABASE_SCHEMA.md` ·
`docs/API_CONVENTIONS.md` · `docs/RBAC_AND_BRANDS.md` · `docs/record-sync.md` ·
`docs/channel-adapter.md` · `backend/whatsapp/SETUP.md` ·
`backend/baileys_service/README.md` + `RESEARCH.md` · **`CLAUDE.md`** for
everything else.
