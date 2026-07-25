# Linking a real WhatsApp account (Meta Cloud API)

The `whatsapp` app runs in **sandbox mode** until an account is linked — everything
works locally but nothing reaches Meta.

> **Preferred path: the dashboard.** Settings → **Channels** → *WhatsApp* → *Add
> account* — any admin can link any number of accounts (name, phone number ID,
> access token), and they go live instantly with no restart. App-level keys
> (OpenAI/OpenRouter for AI replies, webhook verify token) live in Settings →
> **App Keys** (superadmin only). The steps below explain where those Meta values
> come from; the `.env` keys remain as a fallback that appears as a read-only
> "From backend/.env" account.

## 1. Create the Meta app

1. Go to https://developers.facebook.com → **My Apps → Create App** → type **Business**.
2. In the app dashboard, **Add Product → WhatsApp → Set up**. This creates/links a
   **WhatsApp Business Account (WABA)** and gives you a **free test number**.

## 2. Grab the two credentials

Both are on **WhatsApp → API Setup** in the app dashboard:

| .env key | Where it is |
|---|---|
| `WHATSAPP_PHONE_NUMBER_ID` | "Phone number ID" under the From-number dropdown (a long numeric ID — NOT the phone number) |
| `WHATSAPP_ACCESS_TOKEN` | "Temporary access token" (expires in 24h — fine for testing) |

**Permanent token (production):** Meta **Business Settings → Users → System Users**
→ create an Admin system user → **Add Assets** (your app + the WABA) → **Generate
Token** with `whatsapp_business_messaging` + `whatsapp_business_management`
permissions. Use that as `WHATSAPP_ACCESS_TOKEN` instead.

Put them in `backend/.env`:

```env
WHATSAPP_ACCESS_TOKEN=EAAG...
WHATSAPP_PHONE_NUMBER_ID=123456789012345
WHATSAPP_VERIFY_TOKEN=                      # optional: leave blank to auto-generate (step 4)
WHATSAPP_APP_SECRET=EAB...                  # REQUIRED for live accounts: App Settings → Basic → App Secret
WHATSAPP_WABA_ID=                           # WhatsApp Business Account ID — needed for TEMPLATES (see below)
```

> **WABA ID (templates):** the **WhatsApp Business Account ID** is shown on
> **WhatsApp → API Setup** right next to the Phone number ID. Without it the
> app can still send/receive, but `GET /api/whatsapp/templates` can't list
> your approved templates (template *sending* by name still works). Dashboard
> accounts have a *WABA ID* field in Settings → Channels → WhatsApp.

> **App secret is required once an account is live.** The webhook verifies
> Meta's `X-Hub-Signature-256` header against the app secret. In sandbox mode
> (no configured account) unsigned posts are accepted so local demos work, but
> as soon as ANY account is configured the webhook **rejects every unsigned or
> wrongly-signed POST (403)** — including all inbound messages, if you forgot
> to set the app secret on the account (dashboard accounts have an *App
> secret* field; `.env` accounts use `WHATSAPP_APP_SECRET`).

Restart the backend. `GET /api/whatsapp/status` should now return
`"configured": true` and the Chats page sandbox banner disappears. **Sending**
works immediately — try it from `/chats`.

## 3. Expose the webhook (needed to RECEIVE messages)

Meta pushes inbound messages/statuses to a **public HTTPS** URL. For local dev,
tunnel port 8000:

```powershell
ngrok http 8000     # or: cloudflared tunnel --url http://localhost:8000
```

Then add the tunnel host to `backend/.env` (Django rejects unknown Host headers):

```env
ALLOWED_HOSTS=localhost,127.0.0.1,a1b2c3.ngrok-free.app
```

and restart the backend.

## 4. Register the webhook with Meta

App dashboard → **WhatsApp → Configuration → Webhook → Edit**:

- **Callback URL:** `https://<your-tunnel>/api/whatsapp/webhook`
- **Verify token:** the value shown in **Settings → App Keys** in the dashboard
  (or `GET /api/whatsapp/settings`). If you set `WHATSAPP_VERIFY_TOKEN` in
  `.env` that value is used; if you leave it at/blank the shipped default
  (`chatbranch-verify` is public in this repo, so it is never accepted), the
  app **auto-generates a random token once** and persists it as a Setting row —
  always copy the live value from the dashboard, not from this doc.

Click **Verify and save** — Meta calls the URL with a challenge; our webhook view
echoes it back. Then under **Webhook fields**, **Subscribe** to:

- `messages` (inbound texts + delivery/read statuses) — required
- `calls` + `call_permissions` — only if you enable WhatsApp calling later

## 5. Test the loop

1. On **API Setup**, add your own phone to the recipient allow-list (test numbers
   may message up to 5 listed recipients) and send the `hello_world` template to
   yourself.
2. Reply from your phone → the message arrives via the webhook and shows up in
   the dashboard **Chats** page under your number.
3. If a thread has **AI auto-reply** on and `OPENAI_API_KEY` (or
   `OPENROUTER_API_KEY`) is set in `.env`, the AI answers automatically.

## Templates, media, compliance

- **Templates (implemented):** `GET /api/whatsapp/templates` lists the WABA's
  approved templates (needs the WABA ID, see above);
  `POST /api/whatsapp/threads/<wa_id>/send-template {name, language,
  variables[]}` sends one. In **sandbox** (no linked account / no WABA ID) the
  app serves three built-in demo templates (`order_confirmation`,
  `delivery_update`, `appointment_reminder`, three positional slots each) and
  renders them locally, so the whole flow is demoable offline. Real templates
  must be created + approved in Meta's WhatsApp Manager first (utility category
  for transactional notifications).
- **Media (implemented):** inbound images/documents/audio/video are downloaded
  to `backend/data/whatsapp/media/{wa_id}/` (gitignored PII) and served,
  auth-gated, from `GET /api/whatsapp/media/<message_pk>`. Outbound:
  `POST /api/whatsapp/threads/<wa_id>/send-media` (multipart `file` +
  optional `caption`; jpg/png/webp/pdf/mp4/ogg/mp3, max 16 MB). Sandbox keeps
  the file locally and marks the message `sandbox`.
- **Opt-out (automatic):** an inbound STOP / UNSUBSCRIBE / BERHENTI /
  "jangan hubungi" / "do not contact" flags the thread `opted_out`: AI
  auto-replies and template sends are blocked (409), and manual free-text
  needs `{"force": true}`. A system note lands in the thread + audit log.
- **Quiet hours:** set `quiet_start` / `quiet_end` ("HH:MM", 24h) in
  Settings → **AI Configuration**, or `WHATSAPP_QUIET_START` /
  `WHATSAPP_QUIET_END` in `.env`. Times are **server-local** — there is no
  per-contact timezone handling, so run the server in the timezone your
  contacts live in. During quiet hours AI auto-replies are skipped and template
  sends return 409; manual operator sends remain allowed. Empty = disabled.

## Gotchas that will bite

- **24-hour window:** free-form text (what `client.send_text` sends) is only
  deliverable within 24h of the customer's last message. Outside that window
  Meta rejects it (error 131047) — the API now returns **409 "24h window
  closed — use a template"** for free-text/media on a live thread whose
  window has lapsed, and thread payloads expose `window_open` /
  `window_expires_at`. Send a pre-approved **template** to re-open the
  conversation; the customer texting you first always opens the window.
  Sandbox threads are always treated as open.
- **Test number limits:** unverified apps can only message the ≤5 allow-listed
  recipients. Messaging real customers needs a real business number registered
  on the WABA + Meta **business verification**.
- **Token expiry:** the dashboard token dies after 24h — switch to a system-user
  token before any real use.
- **Tunnel URL changes:** every ngrok restart mints a new hostname → update both
  `ALLOWED_HOSTS` and the Meta webhook URL.
- **Calling:** implemented (media bridge in `whatsapp/bridge.py` — aiortc ↔
  LiveKit voice agent), but it needs (1) `LIVEKIT_*` keys in `.env`, (2) the
  webhook subscribed to the `calls` field, and (3) a number enrolled in Meta's
  **Business Calling** beta — without that enrollment Meta rejects
  `action=connect`. Not yet validated against a live calling-enabled number.
