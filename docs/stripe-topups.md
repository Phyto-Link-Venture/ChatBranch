# AI-credit top-ups with Stripe — operator setup

Companies buy prepaid AI credit on **/billing → Top up AI credit**. They pay on
Stripe's own Checkout page; the credit lands when Stripe's webhook confirms the
payment. This uses the **platform's** Stripe account — not a company's own Stripe
integration (Settings → Integrations), which the AI payment-link skill uses.

Code: `backend-csharp/src/ChatBranch.Data/Ai/AiCreditTopup.cs` (create + webhook),
`ChatBranch.Api/Endpoints/AiCreditTopupEndpoints.cs` (routes), tables in
`docs/DATABASE_SCHEMA.md` (`crm_aicreditpayment`, `crm_aicreditoffer`,
`crm_stripeevent`).

## 1. Configuration

| Setting | Env var | Notes |
|---|---|---|
| `stripe_secret_key` | `STRIPE_SECRET_KEY` | `sk_test_…` / `sk_live_…` (a restricted `rk_…` key with *Checkout Sessions: write* also works). **The env var wins** over anything stored in the database, so a key pasted in the dashboard can never override the deployed one. Not shown in any settings page. |
| `stripe_webhook_secret` | `STRIPE_WEBHOOK_SECRET` | `whsec_…` of the webhook endpoint below (Settings → App & provider keys → Stripe also accepts it). |
| `stripe_mode` | `STRIPE_MODE` | `test` (default) or `live`. |

All three come from `IntegrationSettings` (stored JSON → Setting row → env; env
first for the secret key). In the compose deploy they reach the `api` container
through `docker-compose.yml` (`STRIPE_SECRET_KEY`, `STRIPE_WEBHOOK_SECRET`,
`STRIPE_MODE`) — supply them through the deploy script's `COMPOSE_ENV`, never by
committing values.

Top-ups stay switched off (the card says why, `POST …/credit/topup` answers 503)
until **all** of these hold:

- a secret key is configured, **and its prefix matches the mode** (`sk_test_` /
  `rk_test_` with `STRIPE_MODE=test`; `sk_live_` / `rk_live_` with `live`);
- a webhook signing secret is configured (otherwise a payment could never be
  credited);
- the billing currency (Settings → AI billing) is one Stripe charges in minor
  units (MYR, USD, SGD, … — three-decimal currencies such as KWD are refused).

**Set the billing currency first.** Credit is denominated in the billing
currency, and a later currency change does not convert existing balances: a
payment that completes after the currency changed is held for review, not
credited.

## 2. The webhook endpoint (Stripe dashboard → Developers → Webhooks)

Endpoint URL: `https://<host>/api/integrations/webhooks/stripe`
(production: `https://chatbranch.phytolink-venture.com/api/integrations/webhooks/stripe`).

Events to send:

| Event | Effect |
|---|---|
| `checkout.session.completed` | **credits** the top-up (only when `payment_status = paid`) — required |
| `charge.refunded` | writes a negative `refund` ledger line — required |
| `charge.dispute.created` | writes a negative `chargeback` ledger line — required |
| `checkout.session.expired` | closes an abandoned checkout (sessions expire after 1 hour) and gives back a limited offer's redemption — **required as soon as any offer has a `max_redemptions`**, or every abandoned checkout burns one redemption for good |
| `checkout.session.async_payment_succeeded` / `checkout.session.async_payment_failed` | only needed if a delayed-notification payment method is enabled; credits / closes such a payment |

Do **not** rely on `payment_intent.succeeded`: it is received and deliberately
ignored (crediting it as well would double-credit every top-up). Copy the
endpoint's signing secret into `STRIPE_WEBHOOK_SECRET`. Test and live endpoints
have different secrets; an event whose `livemode` disagrees with `STRIPE_MODE` is
refused with 400 (and an activity-log row `billing.credit.stripe.refused`).

## 3. Payment methods

Checkout uses the payment methods enabled on the Stripe account (no code
change). For Malaysian buyers **enable FPX** (and, if wanted, GrabPay) under
Settings → Payment methods in the Stripe dashboard; FPX only appears for MYR
charges. Cards work out of the box.

## 4. Going live

Roll the secret in the same deploy: `STRIPE_SECRET_KEY=sk_live_…`, the **live**
endpoint's `STRIPE_WEBHOOK_SECRET`, and `STRIPE_MODE=live`. Rows record the mode
they were opened in, so a test top-up can never be completed by a live event or
the reverse. Test the whole flow on the demo environment first (its own
Postgres, its own key file) with Stripe's test cards and the FPX test bank.

## 5. What to watch

- **`review` payments** (status on /billing, audit `billing.credit.topup.review`,
  a notification to superadmins): money was received but NOT credited — an
  amount/currency/mode mismatch or a billing-currency change. Refund it in Stripe
  or grant the credit by hand (Settings → AI billing → Grant AI credit).
- **Refunds and disputes are automatic and can drive a balance negative**, which
  pauses that company's AI (when credit enforcement is on). **A refund issued
  after the credit was already spent is an unrecoverable loss** — the AI work was
  done and paid to the model provider; the ledger shows it as a negative balance
  that only a later top-up or a manual correction clears. Consider refusing
  refunds for spent credit as a business rule. A dispute that is later **won**
  is not re-credited automatically — grant it back by hand.
- Maintenance windows answer 503 to Stripe; Stripe retries for up to three days,
  so credit is delayed, not lost.
- The webhook is idempotent: re-sending any event from the Stripe dashboard is
  safe.
