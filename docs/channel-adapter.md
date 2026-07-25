# Channels: how they work, and how to add one

A reference for the six text channels ChatBranch ships with — what each one can
actually do, where its code lives, and what it takes to add a seventh.

> ## ⚠ Open security gap: the Messenger / Instagram webhook is not signed
>
> `POST /api/messaging/webhook` (`backend/messaging/views.py:webhook`) accepts
> **any** POST. It does not verify Meta's `X-Hub-Signature-256` header. The
> WhatsApp Cloud webhook does (`whatsapp/views.py:_signature_ok`, required once
> any account is configured); the Messenger/IG one never gained the check.
>
> **Consequence:** anyone who learns your webhook URL can inject fake inbound
> messages — which create threads, notify operators, trigger AI auto-replies and
> resume parked flow runs.
>
> **Do not expose `/api/messaging/webhook` publicly until this is fixed.** The
> fix is small: read `request.body` before parsing, HMAC-SHA256 it with the Meta
> app secret, `hmac.compare_digest` against the header, and reject on mismatch —
> `whatsapp/views.py:_signature_ok` is the template. It needs an app-secret field
> on facebook/instagram `ChannelAccount` rows (the model already has
> `app_secret`) and a decision about the sandbox case (WhatsApp's rule — accept
> unsigned only while *no* account is configured — is the precedent worth
> copying). This is documented and unfixed on purpose rather than silently
> shipped.
>
> Verified still true against the current tree.

---

## 1. What each channel can do

"—" means the capability does not exist for that channel in this codebase.

| | WhatsApp Cloud | WhatsApp Baileys | Telegram | Messenger / IG | Email |
|---|---|---|---|---|---|
| Send text | ✅ | ✅ | ✅ | ✅ | ✅ (SMTP) |
| Send template | ✅ (Meta-approved) | rendered as free text | — | — | — |
| Send media | ✅ | ✅ | — | — | attachments in, not out |
| Receive | webhook | gateway push | webhook | webhook | **IMAP poll** |
| Delivery receipts | ✅ `statuses` | ✅ gateway push | — | — | — |
| 24h messaging window | ✅ enforced | none | none | applies at Meta, **not modelled** | — |
| Opt-out (STOP/…) | ✅ | ✅ | — | — | — |
| Health probe | Graph token ping | gateway state | `getMe` + `getWebhookInfo` | page profile fetch | SMTP connect |
| Multi-account | ✅ | ✅ | ✅ | ⚠ first account with a token | ✅ |
| Voice calls | ✅ (gated beta, untested live) | ✅ (ToS-violating) | — | — | — |
| Webhook signature | ✅ required | shared secret | token in path | ❌ **none** | n/a |

Where the code is:

| Channel | Transport | Store | Config |
|---|---|---|---|
| WhatsApp (both providers) | `whatsapp/client.py` | `whatsapp/store.py` | `whatsapp/wa_config.py` |
| Baileys gateway | `backend/baileys_service/` (Node) | — | `BAILEYS_*` env |
| Telegram | `messaging/telegram_client.py` | `messaging/store.py` | `messaging/msg_config.py` |
| Messenger / Instagram | `messaging/meta_client.py` | `messaging/store.py` | `messaging/msg_config.py` |
| Email | `messaging/email_client.py` (out) + `imap_client.py` (in) | `messaging/store.py` | `messaging/msg_config.py` + per-account `ChannelAccount.config` |

All of them write to the **same** `crm.Thread` / `crm.Message` tables, so the
`/chats` inbox, the SSE stream, the AI brain, the flow engine and the outbound
queue are already channel-blind.

---

## 2. Per-channel notes that will bite you

### WhatsApp Cloud API
- **The 24-hour window is derived at read time** from the newest inbound
  message (`store._window_state`, `WINDOW_HOURS = 24`), not stored as a flag you
  can set. Free-text and media sends into a closed window return **409 — use a
  template**.
- **Inbound rows store Meta's own `messages[].timestamp`**, not arrival time.
  Meta's retry backlog can deliver a webhook hours late; window maths off
  arrival time would offer sends that Meta then rejects with `131047`. A `131047`
  status auto-runs `store.close_window` so the composer flips to template mode.
- **The webhook signature is required once any account is configured.** In pure
  sandbox (no configured account) unsigned posts are accepted so local demos
  work. Live accounts must carry an `app_secret` or every inbound message is
  rejected with 403.
- **The verify token auto-generates once** into a `Setting` row. The value
  shipped in `.env.example` is public and is never accepted — copy the real one
  from Settings.

### WhatsApp over Baileys
- Unofficial WhatsApp Web protocol. **Violates WhatsApp's ToS**; dev/demo only.
  See `backend/baileys_service/README.md` and `RESEARCH.md`.
- No templates, no 24h window (`store._is_baileys_thread` forces
  `window_open = True`), no Meta approval.
- **Inbound media is decrypted by the gateway** — only it holds the Signal keys
  — and pushed multipart to `POST /api/whatsapp/baileys/media`.
- Delivery receipts *are* wired: the gateway pushes to
  `POST /api/whatsapp/baileys/message-status`, which advances the tick exactly
  like the Cloud webhook's `statuses` events.
- One call at a time per gateway; outbound-only.

### Telegram
- The bot token in the webhook path **is** the authentication
  (`/api/messaging/webhook/telegram/<token>`, resolved by
  `msg_config.find_telegram_account_by_token`).
- `access_token` holds the BotFather token; `external_id` holds the numeric bot
  id, backfilled via `getMe` on account creation.
- No phone number, so CRM linking is manual (the `/chats` Contact picker, or
  `POST /api/messaging/threads/telegram/<key>/link`). Auto-replies only fire on
  threads linked to a `crm.Contact`, so an unlinked Telegram chat gets nothing.
- Health is the richest of any channel: `getMe` (revoked token → disconnected)
  plus `getWebhookInfo` (no webhook set, a failing webhook, or >20 stuck pending
  updates → degraded).
- Bot API hard limits: 4096 chars per message, ~30 messages/second bot-wide,
  ~1/second per chat.

### Messenger / Instagram
- Beyond the signature gap above: the send-side account lookup is
  `msg_config.find_account_for_channel` = **the first account with a token**.
  There is no multi-account routing, and the webhook does not route by
  `entry.id` (the helper `find_account_by_external_id` exists but is unused).
- Meta's 24h standard messaging window applies in reality but is **not
  modelled** — a send outside it fails at the provider and surfaces as a 502.
- Inbound attachments download asynchronously off the ack, from the CDN url.

### Email
- **Inbound is a poll, not a webhook.** `POST /api/messaging/email/sync` runs an
  IMAP fetch; the `/chats` page nudges it every 60s and the server throttles real
  IMAP hits to one per 30s. The fetch marks mail `\Seen`, so each message is
  considered exactly once.
- **Inbound mail is scope-filtered** (`views._known_sender`): only senders
  matching a CRM contact's email, or a thread we have already corresponded on,
  are ingested. Newsletters and platform notifications are dropped permanently.
- `Thread.ai_enabled` defaults to **False** for email
  (`store.get_or_create_thread`) — email is not a chat channel and instant AI
  replies to it read badly.
- Per-mailbox SMTP credentials live in `ChannelAccount.config`; the password is
  never returned unmasked.

---

## 3. The cross-channel machinery

These subsystems are already channel-blind. A new channel gets them for free if
it writes through the stores.

**Account health** — `whatsapp/health.py` is the **only** writer of
`ChannelAccount.health_*`. Send paths, webhooks, gateway pushes and a periodic
checker thread all funnel through `set_status`, which fans out one Notification
and one AuditLog per real transition. `healthy`/`degraded` are routable;
`disconnected`/`banned` are skipped and trigger failover.

Error classification matters here and is easy to get wrong. Three classes:

| Class | Meaning | Queue behaviour | Health effect |
|---|---|---|---|
| **transient** | network, 5xx, 429 | retry with backoff | `fail_streak += 1` → degraded past a threshold |
| **recipient** | bad number, user blocked the bot, window closed, opted out | fail the job, never retry | **none** — a dead recipient says nothing about the account |
| **account fatal** | token revoked (Meta 190), number banned, Baileys `logged_out` | fail + re-route via failover | → disconnected/banned, notify once |

Meta's per-recipient codes (`131047` window closed, `131026` undeliverable) must
**never** damage account health. The full classification lives in
`whatsapp/health.py`'s module docstring.

**Routing** — `whatsapp/routing.py account_for_contact(contact, channel)`:
sticky `crm.AccountAssignment` → least-loaded eligible auto-assign → failover
when the assigned account is banned or disconnected. It is role-aware:
conversational sends ask for `role="conversation"`, template sends for
`provider="cloud"` preferring `role="reminder"`. **Templates never fail over to
Baileys.**

**The outbound queue** — `flows/queue.py` drains `crm.OutboundJob` with jitter,
per-account gaps, daily caps and an active-conversation cap. The account is
resolved **at send time**, not at enqueue time.

**The inbox stream** — `GET /api/whatsapp/stream` (SSE) covers *all* channels
via `crm.Thread`. Its change token watches `Thread.updated_at` + message count,
so **any write path using a queryset `.update()` must bump the thread's
`updated_at`** (see `store.update_message_status`) or the dashboard silently
stops refreshing.

**The AI** — `aicore/assistant.py` is channel-agnostic; `whatsapp/ai.py` is a
thin delegate. Auto-replies fire only for threads linked to a `crm.Contact`.

---

## 4. Adding a channel

There is deliberately **no plugin system and no adapter registry**. A channel is
a code review, not config. A design for a `backend/channels/` adapter package
was drafted and never built; if you want one, build it — but the six existing
channels do not need it, and a half-built abstraction is worse than none.

The concrete work for a seventh channel, in order:

1. **Add the channel value** to `crm.ChannelAccount.CHANNEL_CHOICES` and
   `crm.Thread.CHANNEL_CHOICES` (+ a migration).
2. **Write a client module** in `backend/messaging/` — `send_message(account,
   recipient, text)` plus whatever probe the provider offers for health. Keep it
   ORM-free and take the serialized account **dict**, not the model.
3. **Wire the send seam.** `messaging/views.py:_send_via_provider` is a two-line
   dispatch (`telegram` → telegram_client, else meta_client) — it is the natural
   place, and the honest starting point for a registry if you grow past three.
4. **Add an inbound view.** Auth first (signature, shared secret, or token in
   path — pick one and actually implement it), then parse to
   `store.get_or_create_thread` + `store.append_message`, then off the ack path:
   `rbac.notify`, `rbac.audit`, `_ai_reply_async`, `flows_engine.on_inbound_async`.
   Copy `telegram_webhook` — it is the simplest complete example.
5. **Teach the health checker** about it in `whatsapp/health.py check_account`.
6. **Add it to the Settings → Channels UI** (`ChannelsSettings.tsx`) and to
   `routing.eligible_accounts`.
7. **Decide the sandbox behaviour.** Unconfigured must store locally with
   `status="sandbox"` and never raise. This is a hard rule of the template.

Rules that are load-bearing, not stylistic:

- **Write through the stores, never `Message.objects.create`.** The stores bump
  `Thread.updated_at`, stamp `Message.account`, and are what the SSE token
  watches.
- **Keep the store dict shapes.** Views, the AI, the bridge and the timeline all
  consume them. Change the store layer, not the callers.
- **Webhook views never 500 a batch.** Ack fast, do side effects on a daemon
  thread. A provider that gets a 500 will retry the whole batch and duplicate it.
- **Provider message ids must land in `Message.external_id`.** The partial unique
  constraint `(thread, external_id)` is the only thing standing between you and
  duplicated inbound on webhook redelivery.

## 5. Deliberately out of scope

- **Voice calls stay channel-specific.** SIP (`calls/lk.py` → `voice_agent/`),
  WhatsApp Cloud (`whatsapp/bridge.py`, aiortc) and Baileys
  (`baileys_service/calls.js`) each terminate a different media stack into a
  LiveKit room. There is no unified call abstraction and there should not be one
  until a third transport genuinely needs it.
- **No store unification.** `whatsapp/store.py` and `messaging/store.py` keep
  separate signatures and dict shapes on purpose.
- **SMS has no provider.** The flow engine's SMS branch logs and skips. Adding
  Twilio would follow §4 exactly.
