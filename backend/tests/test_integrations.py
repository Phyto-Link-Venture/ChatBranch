"""Phase 6 — the integrations app.

Covers what actually protects a deployment:

* auth/role gating on every endpoint (webhooks + OAuth callback are the only
  open routes, by design);
* secrets never come back unmasked, and a masked round-trip never clobbers a
  stored secret;
* OAuth: PKCE challenge derivation, signed `state` (tamper + expiry +
  wrong-row rejection), token exchange, lazy refresh, and the rule that a
  FAILED refresh transitions status instead of raising;
* webhook HMAC: Shopify base64 + Stripe v1 hex, both rejecting bad
  signatures, missing headers, replayed timestamps and a missing secret;
* the Shopify/Stripe -> crm.Record + crm.RecordEvent mapping (incl. contact
  matching, creation, and webhook-redelivery idempotency);
* the sandbox contract: with ZERO credentials configured every endpoint
  answers and every flow action skips gracefully without opening a socket.

Fully offline — no network, no Google/Shopify/Stripe.
"""
import json
import time
from datetime import timedelta
from decimal import Decimal

import pytest
from django.contrib.auth.models import User
from django.core import signing
from django.utils import timezone

from crm.models import AuditLog, Contact, Notification, Profile, Record, RecordEvent, Setting
from integrations import actions, catalog, health, mapping, oauth, webhooks
from integrations.models import Integration

TOKEN_SUPER = "int-super-token"
TOKEN_ADMIN = "int-admin-token"
TOKEN_AGENT = "int-agent-token"
SUPER = {"HTTP_AUTHORIZATION": f"Bearer {TOKEN_SUPER}"}
ADMIN = {"HTTP_AUTHORIZATION": f"Bearer {TOKEN_ADMIN}"}
AGENT = {"HTTP_AUTHORIZATION": f"Bearer {TOKEN_AGENT}"}


@pytest.fixture
def superadmin(db):
    u = User.objects.create_user(username="int_super", password="x")
    Profile.objects.create(user=u, role="superadmin", token=TOKEN_SUPER)
    return u


@pytest.fixture
def admin(db):
    u = User.objects.create_user(username="int_admin", password="x")
    Profile.objects.create(user=u, role="admin", token=TOKEN_ADMIN)
    return u


@pytest.fixture
def agent(db):
    u = User.objects.create_user(username="int_agent", password="x")
    Profile.objects.create(user=u, role="agent", token=TOKEN_AGENT)
    return u


@pytest.fixture
def google_client(db):
    Setting.objects.update_or_create(key=catalog.SETTINGS_KEY, defaults={"value": json.dumps({
        "google_client_id": "client-id-123",
        "google_client_secret": "client-secret-456",
        "google_redirect_uri": "http://testserver/api/integrations/oauth/google/callback",
    })})


def _mk(provider="google_calendar", **kw):
    return Integration.objects.create(provider=provider,
                                      name=kw.pop("name", "Ops"), **kw)


# ---- sandbox: zero credentials, everything still answers -------------------------

@pytest.mark.django_db
class TestSandbox:
    def test_providers_lists_catalog_with_no_rows(self, client, admin):
        r = client.get("/api/integrations/providers", **ADMIN)
        assert r.status_code == 200
        body = r.json()
        assert {p["id"] for p in body["providers"]} == set(catalog.PROVIDER_IDS)
        assert body["sandbox"] is True
        assert body["google_oauth_configured"] is False
        google = next(p for p in body["providers"] if p["id"] == "gmail")
        assert google["oauth_configured"] is False
        assert google["connected"] == 0

    def test_accounts_empty(self, client, admin):
        r = client.get("/api/integrations/accounts", **ADMIN)
        assert r.status_code == 200 and r.json()["accounts"] == []

    def test_settings_reports_nothing_configured(self, client, admin):
        r = client.get("/api/integrations/settings", **ADMIN)
        assert r.status_code == 200
        body = r.json()
        assert body["google_oauth_configured"] is False
        assert body["google_client_secret_masked"] == ""
        assert body["webhook_paths"]["shopify"] == "/api/integrations/webhook/shopify"

    def test_oauth_start_without_client_signals_needs_setup(self, client, admin):
        # UPGRADE-SPEC §5: never 500; a clean {configured:false, reason} so the
        # card shows its one-time "Set up (admin)" state.
        r = client.get("/api/integrations/oauth/gmail/start", **ADMIN)
        assert r.status_code == 200
        body = r.json()
        assert body["configured"] is False
        assert "not configured" in body["reason"].lower()
        # no orphan Integration row is created in the sandbox case
        assert Integration.objects.count() == 0

    def test_every_flow_action_skips_without_credentials(self, db, monkeypatch):
        def boom(*a, **kw):
            raise AssertionError("no network call may happen in sandbox")

        monkeypatch.setattr(actions, "_request", boom)
        contact = Contact.objects.create(name="Sandbox", phone="+60111000111")
        for label in actions.ACTIONS:
            result = actions.run_action(label, contact=contact, config={}, text="hi")
            assert result["ok"] is False, label
            assert result["skipped"] is True, label
            assert result["detail"], label

    def test_unknown_action_is_a_skip_not_a_crash(self, db):
        result = actions.run_action("Teleport the parcel")
        assert result["skipped"] is True and result["ok"] is False


# ---- auth gating ------------------------------------------------------------------

@pytest.mark.django_db
class TestAuth:
    @pytest.mark.parametrize("path", [
        "/api/integrations/providers",
        "/api/integrations/accounts",
        "/api/integrations/settings",
        "/api/integrations/oauth/gmail/start",
    ])
    def test_endpoints_401_without_a_role(self, client, admin, path):
        assert client.get(path).status_code == 401

    def test_create_requires_admin(self, client, agent):
        r = client.post("/api/integrations/accounts",
                        data=json.dumps({"provider": "stripe", "name": "Live"}),
                        content_type="application/json", **AGENT)
        assert r.status_code == 403

    def test_settings_put_requires_superadmin(self, client, admin):
        r = client.put("/api/integrations/settings",
                       data=json.dumps({"google_client_id": "x"}),
                       content_type="application/json", **ADMIN)
        assert r.status_code == 403

    def test_settings_put_superadmin_saves_and_audits(self, client, superadmin):
        r = client.put("/api/integrations/settings",
                       data=json.dumps({"google_client_id": "cid",
                                        "google_client_secret": "csecret-1234"}),
                       content_type="application/json", **SUPER)
        assert r.status_code == 200
        assert r.json()["google_client_id"] == "cid"
        assert r.json()["google_client_secret_masked"] == "••••1234"
        assert catalog.settings()["google_client_secret"] == "csecret-1234"
        assert AuditLog.objects.filter(action="settings.update",
                                       target="integrations").exists()

    def test_masked_settings_round_trip_keeps_secret(self, client, superadmin):
        catalog.save_settings({"google_client_secret": "csecret-1234"})
        r = client.put("/api/integrations/settings",
                       data=json.dumps({"google_client_secret": "••••1234",
                                        "google_client_id": "cid-2"}),
                       content_type="application/json", **SUPER)
        assert r.status_code == 200
        assert catalog.settings()["google_client_secret"] == "csecret-1234"
        assert catalog.settings()["google_client_id"] == "cid-2"

    def test_unknown_provider_rejected(self, client, admin):
        r = client.post("/api/integrations/accounts",
                        data=json.dumps({"provider": "sap", "name": "x"}),
                        content_type="application/json", **ADMIN)
        assert r.status_code == 400


# ---- account CRUD + secret masking -------------------------------------------------

@pytest.mark.django_db
class TestAccounts:
    def test_create_masks_secrets_and_notifies(self, client, admin):
        r = client.post("/api/integrations/accounts", data=json.dumps({
            "provider": "shopify", "name": "Main store",
            "config": {"shop_domain": "demo.myshopify.com"},
            "api_key": "shpat_abcdefgh1234",
            "webhook_secret": "whsecret_9999",
        }), content_type="application/json", **ADMIN)
        assert r.status_code == 200
        body = r.json()
        assert body["api_key_masked"] == "••••1234"
        assert body["webhook_secret_masked"] == "••••9999"
        assert "api_key" not in json.dumps(body) or body.get("api_key") is None
        assert "shpat_abcdefgh1234" not in json.dumps(body)
        row = Integration.objects.get(pk=body["id"])
        assert row.credentials["api_key"] == "shpat_abcdefgh1234"
        assert row.connected is True
        assert Notification.objects.filter(title__icontains="Integration added").exists()

    def test_list_never_leaks_a_token(self, client, admin):
        _mk("stripe", name="Live", credentials={"api_key": "sk_test_secret_value"})
        r = client.get("/api/integrations/accounts", **ADMIN)
        assert "sk_test_secret_value" not in r.content.decode()

    def test_patch_masked_secret_is_ignored(self, client, admin):
        row = _mk("stripe", name="Live", credentials={"api_key": "sk_live_abcd1234"})
        r = client.patch(f"/api/integrations/accounts/{row.pk}",
                         data=json.dumps({"api_key": "••••1234", "name": "Renamed"}),
                         content_type="application/json", **ADMIN)
        assert r.status_code == 200
        row.refresh_from_db()
        assert row.credentials["api_key"] == "sk_live_abcd1234"
        assert row.name == "Renamed"

    def test_patch_real_secret_resets_status(self, client, admin):
        row = _mk("stripe", name="Live", credentials={"api_key": "sk_old"},
                  status="disconnected")
        client.patch(f"/api/integrations/accounts/{row.pk}",
                     data=json.dumps({"api_key": "sk_new_value_1"}),
                     content_type="application/json", **ADMIN)
        row.refresh_from_db()
        assert row.credentials["api_key"] == "sk_new_value_1"
        assert row.status == "unknown"

    def test_delete_is_admin_only_and_audited(self, client, admin, agent):
        row = _mk("gmail", name="Ops mailbox")
        assert client.delete(f"/api/integrations/accounts/{row.pk}",
                             **AGENT).status_code == 403
        assert client.delete(f"/api/integrations/accounts/{row.pk}",
                             **ADMIN).status_code == 200
        assert not Integration.objects.filter(pk=row.pk).exists()
        assert AuditLog.objects.filter(action="integration.delete").exists()

    def test_test_endpoint_on_unconnected_row(self, client, admin):
        row = _mk("google_drive", name="Docs")
        r = client.post(f"/api/integrations/accounts/{row.pk}/test", **ADMIN)
        assert r.status_code == 400
        assert r.json()["skipped"] is True

    def test_disconnect_clears_credentials(self, client, admin):
        row = _mk("gmail", name="Ops", credentials={"access_token": "tok",
                                                    "refresh_token": "r"},
                  scopes=["a"], status="healthy")
        r = client.post(f"/api/integrations/accounts/{row.pk}/disconnect", **ADMIN)
        assert r.status_code == 200 and r.json()["connected"] is False
        row.refresh_from_db()
        assert row.credentials == {} and row.scopes == [] and row.status == "revoked"


# ---- status transitions (single writer) ---------------------------------------------

@pytest.mark.django_db
class TestStatus:
    def test_transition_notifies_and_audits_once(self, admin):
        row = _mk("stripe", credentials={"api_key": "sk_x"})
        health.set_status(row, health.DEGRADED, "rate limited")
        health.set_status(row, health.DEGRADED, "rate limited")  # no change
        assert AuditLog.objects.filter(action="integration.status").count() == 1
        assert Notification.objects.filter(title__icontains="Integration degraded").count() == 1
        row.refresh_from_db()
        assert row.status == "degraded" and row.status_changed_at is not None

    def test_unconnected_rows_transition_silently(self, admin):
        row = _mk("shopify")
        health.set_status(row, health.DISCONNECTED, "nope")
        row.refresh_from_db()
        assert row.status == "disconnected"
        assert AuditLog.objects.filter(action="integration.status").count() == 0
        assert Notification.objects.count() == 0

    def test_bad_status_ignored(self, db):
        row = _mk("stripe")
        health.set_status(row, "on fire")
        row.refresh_from_db()
        assert row.status == "unknown"

    def test_set_status_never_raises(self):
        health.set_status(None, health.HEALTHY)  # no row — must not raise


# ---- OAuth ---------------------------------------------------------------------------

@pytest.mark.django_db
class TestOAuthState:
    def test_challenge_is_s256_of_the_verifier(self):
        import base64
        import hashlib

        verifier = oauth.make_verifier()
        assert 43 <= len(verifier) <= 128
        expected = base64.urlsafe_b64encode(
            hashlib.sha256(verifier.encode()).digest()).decode().rstrip("=")
        assert oauth.challenge_for(verifier) == expected
        assert "=" not in oauth.challenge_for(verifier)

    def test_state_round_trips(self):
        state = oauth.sign_state(7, "gmail")
        data = oauth.verify_state(state)
        assert data["i"] == 7 and data["p"] == "gmail"

    def test_tampered_state_rejected(self):
        state = oauth.sign_state(7, "gmail")
        with pytest.raises(oauth.OAuthError):
            oauth.verify_state(state[:-3] + "aaa")
        with pytest.raises(oauth.OAuthError):
            oauth.verify_state("")
        with pytest.raises(oauth.OAuthError):
            # correctly-signed but for a DIFFERENT salt — i.e. not our flow
            oauth.verify_state(signing.dumps({"i": 7, "p": "gmail"}, salt="other"))

    def test_expired_state_rejected(self, monkeypatch):
        state = oauth.sign_state(7, "gmail")
        monkeypatch.setattr(oauth, "STATE_MAX_AGE", -1)
        with pytest.raises(oauth.OAuthError) as exc:
            oauth.verify_state(state)
        assert "expired" in str(exc.value).lower()


@pytest.mark.django_db
class TestOAuthFlow:
    def test_start_builds_url_with_pkce_and_state(self, client, admin, google_client):
        r = client.get("/api/integrations/oauth/google_calendar/start", **ADMIN)
        assert r.status_code == 200
        body = r.json()
        url = body["authorize_url"]
        assert url.startswith(catalog.GOOGLE_AUTH_URL)
        assert "code_challenge_method=S256" in url
        assert "client_id=client-id-123" in url
        assert "access_type=offline" in url
        # the SECRET is never in a URL the browser sees
        assert "client-secret-456" not in url
        row = Integration.objects.get(pk=body["integration_id"])
        assert row.credentials["code_verifier"]
        assert oauth.challenge_for(row.credentials["code_verifier"]) in url

    def test_start_reports_configured_true_on_success(self, client, admin, google_client):
        r = client.get("/api/integrations/oauth/gmail/start", **ADMIN)
        assert r.status_code == 200
        assert r.json()["configured"] is True
        assert r.json()["authorize_url"].startswith(catalog.GOOGLE_AUTH_URL)

    def test_start_scopes_are_per_product(self, client, admin, google_client):
        cal = client.get("/api/integrations/oauth/google_calendar/start",
                         **ADMIN).json()["authorize_url"]
        mail = client.get("/api/integrations/oauth/gmail/start",
                          **ADMIN).json()["authorize_url"]
        assert "calendar.events" in cal and "gmail.send" not in cal
        assert "gmail.send" in mail and "calendar.events" not in mail

    def test_start_requires_admin(self, client, agent, google_client):
        assert client.get("/api/integrations/oauth/gmail/start",
                          **AGENT).status_code == 403

    def test_start_unknown_provider_404(self, client, admin, google_client):
        assert client.get("/api/integrations/oauth/sap/start",
                          **ADMIN).status_code == 404

    def test_callback_exchanges_and_stores(self, client, admin, google_client, monkeypatch):
        start = client.get("/api/integrations/oauth/gmail/start", **ADMIN).json()
        row = Integration.objects.get(pk=start["integration_id"])
        verifier = row.credentials["code_verifier"]
        state = oauth.sign_state(row.pk, "gmail")
        sent = {}

        def fake_post(payload):
            sent.update(payload)
            return {"access_token": "ya29.new", "refresh_token": "1//refresh",
                    "expires_in": 3600, "scope": "https://www.googleapis.com/auth/gmail.send",
                    "token_type": "Bearer"}

        monkeypatch.setattr(oauth, "_post_token", fake_post)
        r = client.get(f"/api/integrations/oauth/gmail/callback"
                       f"?code=abc&state={state}&format=json")
        assert r.status_code == 200 and r.json()["ok"] is True
        assert sent["code_verifier"] == verifier       # PKCE proof travelled once
        assert sent["grant_type"] == "authorization_code"
        assert sent["client_secret"] == "client-secret-456"
        row.refresh_from_db()
        assert row.credentials["access_token"] == "ya29.new"
        assert row.credentials["refresh_token"] == "1//refresh"
        assert "code_verifier" not in row.credentials      # single use
        assert row.scopes == ["https://www.googleapis.com/auth/gmail.send"]
        assert row.status == "healthy"
        assert AuditLog.objects.filter(action="integration.connect").exists()

    def test_callback_needs_no_bearer_token(self, client, admin, google_client, monkeypatch):
        """The browser arrives from Google with no Authorization header — the
        signed state is the whole CSRF control."""
        start = client.get("/api/integrations/oauth/gmail/start", **ADMIN).json()
        state = oauth.sign_state(start["integration_id"], "gmail")
        monkeypatch.setattr(oauth, "_post_token",
                            lambda payload: {"access_token": "t", "expires_in": 60})
        r = client.get(f"/api/integrations/oauth/gmail/callback?code=x&state={state}")
        assert r.status_code == 302   # bounced back to the dashboard
        assert "connected=" in r["Location"]

    def test_callback_rejects_forged_state(self, client, admin, google_client):
        row = _mk("gmail")
        r = client.get(f"/api/integrations/oauth/gmail/callback"
                       f"?code=abc&state=not-a-real-state&format=json")
        assert r.status_code == 400
        assert "state" in r.json()["detail"].lower()
        row.refresh_from_db()
        assert row.credentials == {}

    def test_callback_without_a_started_flow_has_no_verifier(self, client, admin,
                                                             google_client, monkeypatch):
        row = _mk("gmail")
        monkeypatch.setattr(oauth, "_post_token",
                            lambda payload: pytest.fail("must not exchange"))
        state = oauth.sign_state(row.pk, "gmail")
        r = client.get(f"/api/integrations/oauth/gmail/callback?code=abc&state={state}&format=json")
        assert r.status_code == 400
        row.refresh_from_db()
        assert row.status == "disconnected"

    def test_callback_surfaces_provider_error(self, client, google_client):
        r = client.get("/api/integrations/oauth/gmail/callback"
                       "?error=access_denied&format=json")
        assert r.status_code == 400 and "access_denied" in r.json()["detail"]


@pytest.mark.django_db
class TestTokenRefresh:
    def _row(self, expires_in_seconds):
        return _mk("google_sheets", credentials={
            "access_token": "old-token", "refresh_token": "r-1",
            "expires_at": (timezone.now()
                           + timedelta(seconds=expires_in_seconds)).isoformat()})

    def test_valid_token_is_returned_without_refreshing(self, google_client, monkeypatch):
        monkeypatch.setattr(oauth, "_post_token",
                            lambda p: pytest.fail("must not refresh a live token"))
        assert oauth.access_token(self._row(3600)) == "old-token"

    def test_expired_token_refreshes_lazily(self, google_client, monkeypatch):
        row = self._row(-10)
        monkeypatch.setattr(oauth, "_post_token", lambda p: {
            "access_token": "fresh-token", "expires_in": 3600})
        assert oauth.access_token(row) == "fresh-token"
        row.refresh_from_db()
        assert row.credentials["refresh_token"] == "r-1"  # kept when not re-issued

    def test_failed_refresh_disconnects_instead_of_raising(self, google_client, monkeypatch):
        row = self._row(-10)

        def boom(payload):
            raise oauth.OAuthError("Google rejected the token request (HTTP 400, invalid_grant).")

        monkeypatch.setattr(oauth, "_post_token", boom)
        assert oauth.access_token(row) == ""      # no exception into the caller
        row.refresh_from_db()
        assert row.status == "disconnected"
        assert "invalid_grant" in row.status_detail

    def test_refresh_without_a_refresh_token_disconnects(self, google_client):
        row = _mk("gmail", credentials={"access_token": "a", "expires_at":
                                        (timezone.now() - timedelta(seconds=1)).isoformat()})
        assert oauth.access_token(row) == ""
        row.refresh_from_db()
        assert row.status == "disconnected"

    def test_no_credentials_returns_empty(self, db):
        assert oauth.access_token(_mk("gmail")) == ""
        assert oauth.access_token(None) == ""


# ---- webhook signature verification ---------------------------------------------------

SHOPIFY_ORDER = {
    "id": 450789469,
    "order_number": 1001,
    "name": "#1001",
    "email": "bob@example.com",
    "phone": "+60123456789",
    "total_price": "598.94",
    "currency": "MYR",
    "financial_status": "pending",
    "fulfillment_status": None,
    "customer": {"first_name": "Bob", "last_name": "Norman",
                 "email": "bob@example.com", "phone": "+60123456789"},
    "line_items": [{"title": "Sun Hat", "quantity": 2, "price": "99.00"}],
    "order_status_url": "https://demo.myshopify.com/orders/abc",
}


def _shopify_post(client, payload, secret="shhh-secret", topic="orders/create",
                  header=None, extra=None):
    body = json.dumps(payload).encode()
    hmac_header = header if header is not None else webhooks.shopify_signature_header(secret, body)
    return client.post("/api/integrations/webhook/shopify", data=body,
                       content_type="application/json",
                       HTTP_X_SHOPIFY_HMAC_SHA256=hmac_header,
                       HTTP_X_SHOPIFY_TOPIC=topic,
                       HTTP_X_SHOPIFY_WEBHOOK_ID=(extra or {}).get("id", "wh-1"),
                       HTTP_X_SHOPIFY_SHOP_DOMAIN="demo.myshopify.com")


@pytest.mark.django_db
class TestShopifyWebhook:
    @pytest.fixture
    def secret(self, db):
        catalog.save_settings({"shopify_webhook_secret": "shhh-secret"})
        return "shhh-secret"

    def test_valid_signature_maps_order(self, client, secret):
        r = _shopify_post(client, SHOPIFY_ORDER)
        assert r.status_code == 200, r.content
        body = r.json()
        assert body["ok"] is True and body["record_created"] is True
        contact = Contact.objects.get(pk=body["contact_id"])
        assert contact.phone == "+60123456789" and contact.email == "bob@example.com"
        record = Record.objects.get(pk=body["record_id"])
        assert record.type == "order" and record.external_id == "450789469"
        assert record.title == "#1001" and record.status == "pending"
        assert record.amount == Decimal("598.94") and record.currency == "MYR"
        assert record.data["line_items"][0]["title"] == "Sun Hat"
        assert record.data["link"].endswith("/orders/abc")
        assert RecordEvent.objects.filter(record=record, kind="status").count() == 1

    def test_bad_signature_rejected(self, client, secret):
        r = _shopify_post(client, SHOPIFY_ORDER, header="Zm9yZ2Vk")
        assert r.status_code == 401
        assert Record.objects.count() == 0 and Contact.objects.count() == 0

    def test_missing_signature_rejected(self, client, secret):
        body = json.dumps(SHOPIFY_ORDER).encode()
        r = client.post("/api/integrations/webhook/shopify", data=body,
                        content_type="application/json", HTTP_X_SHOPIFY_TOPIC="orders/create")
        assert r.status_code == 401

    def test_tampered_body_rejected(self, client, secret):
        body = json.dumps(SHOPIFY_ORDER).encode()
        header = webhooks.shopify_signature_header("shhh-secret", body)
        tampered = json.dumps({**SHOPIFY_ORDER, "total_price": "1.00"}).encode()
        r = client.post("/api/integrations/webhook/shopify", data=tampered,
                        content_type="application/json",
                        HTTP_X_SHOPIFY_HMAC_SHA256=header,
                        HTTP_X_SHOPIFY_TOPIC="orders/create")
        assert r.status_code == 401

    def test_no_secret_configured_declines_rather_than_accepting(self, client, db):
        r = _shopify_post(client, SHOPIFY_ORDER)
        assert r.status_code == 503
        assert "not configured" in r.json()["detail"].lower()
        assert Record.objects.count() == 0

    def test_per_integration_secret_is_accepted(self, client, db):
        _mk("shopify", name="Store", config={"shop_domain": "demo.myshopify.com"},
            credentials={"api_key": "shpat_x", "webhook_secret": "per-store-secret"})
        r = _shopify_post(client, SHOPIFY_ORDER, secret="per-store-secret")
        assert r.status_code == 200 and r.json()["ok"] is True

    def test_non_order_topic_ignored(self, client, secret):
        r = _shopify_post(client, {"id": 1}, topic="customers/create")
        assert r.status_code == 200 and r.json()["ignored"] == "customers/create"
        assert Record.objects.count() == 0

    def test_update_moves_status_and_books_one_event(self, client, secret):
        _shopify_post(client, SHOPIFY_ORDER)
        record = Record.objects.get()
        updated = {**SHOPIFY_ORDER, "fulfillment_status": "fulfilled",
                   "financial_status": "paid"}
        r = _shopify_post(client, updated, topic="orders/updated",
                          extra={"id": "wh-2"})
        assert r.status_code == 200 and r.json()["record_created"] is False
        record.refresh_from_db()
        assert record.status == "fulfilled"
        assert RecordEvent.objects.filter(record=record, kind="status").count() == 2
        assert RecordEvent.objects.filter(record=record, kind="payment").count() == 1

    def test_paid_event_is_idempotent_across_redeliveries(self, client, secret):
        paid = {**SHOPIFY_ORDER, "financial_status": "paid"}
        _shopify_post(client, paid, extra={"id": "wh-9"})
        _shopify_post(client, paid, topic="orders/updated", extra={"id": "wh-9"})
        assert RecordEvent.objects.filter(kind="payment").count() == 1

    def test_matches_an_existing_contact_by_phone(self, client, secret):
        existing = Contact.objects.create(name="Bob N", phone="+60 12-345 6789")
        r = _shopify_post(client, SHOPIFY_ORDER)
        assert r.json()["contact_id"] == existing.pk
        assert r.json()["contact_created"] is False
        existing.refresh_from_db()
        assert existing.email == "bob@example.com"   # identity backfilled

    def test_anonymous_order_is_skipped(self, client, secret):
        r = _shopify_post(client, {"id": 9, "name": "#9"})
        assert r.status_code == 200 and r.json()["ok"] is False
        assert Contact.objects.count() == 0

    def test_get_not_allowed(self, client, secret):
        assert client.get("/api/integrations/webhook/shopify").status_code == 405


STRIPE_EVENT = {
    "id": "evt_1",
    "type": "payment_intent.succeeded",
    "data": {"object": {
        "id": "pi_123", "amount": 59894, "amount_received": 59894, "currency": "myr",
        "receipt_email": "bob@example.com",
        "metadata": {"phone": "+60123456789"},
        "charges": {"data": [{"billing_details": {"name": "Bob Norman",
                                                  "email": "bob@example.com",
                                                  "phone": "+60123456789"}}]},
    }},
}


def _stripe_post(client, event, secret="whsec_test", header=None, timestamp=None):
    body = json.dumps(event).encode()
    sig = header if header is not None else webhooks.stripe_signature_header(
        secret, body, timestamp)
    return client.post("/api/integrations/webhook/stripe", data=body,
                       content_type="application/json", HTTP_STRIPE_SIGNATURE=sig)


@pytest.mark.django_db
class TestStripeWebhook:
    @pytest.fixture
    def secret(self, db):
        catalog.save_settings({"stripe_webhook_secret": "whsec_test"})
        return "whsec_test"

    def test_valid_signature_books_a_payment(self, client, secret):
        r = _stripe_post(client, STRIPE_EVENT)
        assert r.status_code == 200, r.content
        body = r.json()
        assert body["ok"] is True
        record = Record.objects.get(pk=body["record_id"])
        assert record.type == "invoice" and record.external_id == "pi_123"
        assert record.status == "paid"
        event = RecordEvent.objects.get(record=record, kind="payment")
        assert event.amount == Decimal("598.94")
        assert event.data["currency"] == "MYR"

    def test_metadata_attaches_to_an_existing_order(self, client, secret):
        contact = Contact.objects.create(name="Bob", phone="+60123456789")
        order = Record.objects.create(contact=contact, type="order",
                                      external_id="450789469", status="pending",
                                      amount=Decimal("598.94"), currency="MYR")
        event = json.loads(json.dumps(STRIPE_EVENT))
        event["data"]["object"]["metadata"] = {"record_external_id": "450789469",
                                               "record_type": "order",
                                               "phone": "+60123456789"}
        r = _stripe_post(client, event)
        assert r.json()["record_id"] == order.pk
        order.refresh_from_db()
        assert order.status == "paid"
        assert order.amount == Decimal("598.94")      # the order keeps its own total
        assert RecordEvent.objects.filter(record=order, kind="payment").count() == 1

    def test_bad_signature_rejected(self, client, secret):
        r = _stripe_post(client, STRIPE_EVENT, secret="whsec_wrong")
        assert r.status_code == 401
        assert RecordEvent.objects.count() == 0

    def test_missing_header_rejected(self, client, secret):
        r = client.post("/api/integrations/webhook/stripe",
                        data=json.dumps(STRIPE_EVENT), content_type="application/json")
        assert r.status_code == 401

    def test_malformed_header_rejected(self, client, secret):
        r = _stripe_post(client, STRIPE_EVENT, header="v1=deadbeef")
        assert r.status_code == 401

    def test_replayed_timestamp_rejected(self, client, secret):
        old = int(time.time()) - (webhooks.STRIPE_TOLERANCE + 60)
        r = _stripe_post(client, STRIPE_EVENT, timestamp=old)
        assert r.status_code == 401
        assert "tolerance" in r.json()["detail"].lower()

    def test_no_secret_configured_declines(self, client, db):
        r = _stripe_post(client, STRIPE_EVENT)
        assert r.status_code == 503
        assert RecordEvent.objects.count() == 0

    def test_other_event_types_ignored(self, client, secret):
        r = _stripe_post(client, {"id": "evt_2", "type": "charge.refunded",
                                  "data": {"object": {}}})
        assert r.status_code == 200 and r.json()["ignored"] == "charge.refunded"
        assert RecordEvent.objects.count() == 0

    def test_redelivery_is_idempotent(self, client, secret):
        _stripe_post(client, STRIPE_EVENT)
        r = _stripe_post(client, STRIPE_EVENT)
        assert r.json()["duplicate"] is True
        assert RecordEvent.objects.filter(kind="payment").count() == 1


# ---- mapping helpers -------------------------------------------------------------------

@pytest.mark.django_db
class TestMapping:
    def test_find_contact_prefers_phone_then_email(self):
        by_phone = Contact.objects.create(name="P", phone="+60123456789")
        by_email = Contact.objects.create(name="E", phone="+60199999999",
                                          email="e@example.com")
        assert mapping.find_contact(phone="0123456789") == by_phone
        assert mapping.find_contact(email="E@Example.com") == by_email
        assert mapping.find_contact() is None

    def test_get_or_create_needs_an_identity(self):
        contact, created = mapping.get_or_create_contact(name="Nobody")
        assert contact is None and created is False

    def test_upsert_record_updates_in_place(self):
        contact = Contact.objects.create(name="C", phone="+60111111111")
        r1, created1, _ = mapping.upsert_record(contact, record_type="order",
                                                external_id="A1", status="open")
        r2, created2, old = mapping.upsert_record(contact, record_type="order",
                                                  external_id="A1", status="shipped")
        assert created1 is True and created2 is False and old == "open"
        assert r1.pk == r2.pk and r2.status == "shipped"
        assert Record.objects.count() == 1

    def test_book_event_dedupes(self):
        contact = Contact.objects.create(name="C", phone="+60111111112")
        record = Record.objects.create(contact=contact, type="order", external_id="B1")
        assert mapping.book_event(record, "payment", dedupe_key="k1") is not None
        assert mapping.book_event(record, "payment", dedupe_key="k1") is None
        assert RecordEvent.objects.filter(record=record).count() == 1


# ---- flow-engine wiring ------------------------------------------------------------------

@pytest.mark.django_db
class TestFlowIntegrationAction:
    def test_engine_dispatches_integration_channels(self, monkeypatch):
        from crm.models import FlowRun, Workflow
        from flows import engine

        seen = {}

        def fake_run_action(name, contact=None, config=None, text=""):
            seen.update({"name": name, "contact": contact, "text": text})
            return {"ok": True, "skipped": False, "detail": "event created",
                    "data": {"event_id": "e1"}}

        monkeypatch.setattr(actions, "run_action", fake_run_action)
        contact = Contact.objects.create(name="Zara", phone="+60122223333")
        wf = Workflow.objects.create(flow_id="wf-int", name="Integration flow", nodes=[
            {"id": "a1", "type": "action", "data": {"label": "Book",
                                                    "config": {"channels": ["Create calendar event"],
                                                               "script": "Hi {{name}}"}}},
        ], edges=[])
        run = FlowRun.objects.create(workflow=wf, contact=contact, current_node="a1")
        engine._do_action(run, wf.nodes[0])
        assert seen["name"] == "Create calendar event"
        assert seen["contact"] == contact
        assert seen["text"] == "Hi Zara"
        assert run.context["integrations"][0]["ok"] is True
        assert run.context["integration_data"]["Create calendar event"]["event_id"] == "e1"

    def test_unconfigured_integration_step_does_not_fail_the_run(self, db):
        from crm.models import FlowRun, Workflow
        from flows import engine

        contact = Contact.objects.create(name="Zara", phone="+60122223334")
        wf = Workflow.objects.create(flow_id="wf-int2", name="Integration flow", nodes=[
            {"id": "a1", "type": "action", "data": {"config": {
                "channels": ["Append row to Sheet"], "script": "note"}}},
        ], edges=[])
        run = FlowRun.objects.create(workflow=wf, contact=contact, current_node="a1")
        assert engine._do_action(run, wf.nodes[0]) is True
        assert run.context["integrations"][0]["skipped"] is True


# ---- local Shopify action (works with and without a connection) -----------------------

@pytest.mark.django_db
class TestUpdateOrderStatusAction:
    def test_updates_the_record_without_a_connection(self, db):
        contact = Contact.objects.create(name="Ann", phone="+60177778888")
        record = Record.objects.create(contact=contact, type="order",
                                       external_id="S-1", status="open")
        result = actions.run_action("Update order status", contact=contact,
                                    config={"orderStatus": "shipped"})
        assert result["ok"] is True and result["data"]["remote"] is False
        record.refresh_from_db()
        assert record.status == "shipped"
        assert RecordEvent.objects.filter(record=record, kind="status").count() == 1

    def test_no_record_is_a_skip(self, db):
        contact = Contact.objects.create(name="Ann", phone="+60177778889")
        result = actions.run_action("Update order status", contact=contact,
                                    config={"orderStatus": "shipped"})
        assert result["skipped"] is True
