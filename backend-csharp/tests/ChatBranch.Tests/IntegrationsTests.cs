using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatBranch.Data.Integrations;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>Phase 6: integrations sandbox, webhook HMAC, api/v1 + record sync.</summary>
public class IntegrationsTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public IntegrationsTests(ApiFactory factory) => _factory = factory;

    private async Task<HttpClient> Authed(string user, string pass)
    {
        var c = _factory.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { username = user, password = pass });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    [Fact]
    public async Task Integrations_list_and_sandbox_connect_refuses_without_creds()
    {
        var su = await Authed("superadmin", "super123");
        var list = await su.GetFromJsonAsync<JsonElement>("/api/integrations");
        Assert.True(list.GetProperty("integrations").GetArrayLength() >= 6);

        // no creds → 400 with an operator reason (no socket opened)
        var connect = await su.PostAsJsonAsync("/api/integrations/shopify/connect", new { });
        Assert.Equal(HttpStatusCode.BadRequest, connect.StatusCode);
    }

    [Fact]
    public async Task Stripe_webhook_returns_503_without_secret()
    {
        var c = _factory.CreateClient();
        var res = await c.PostAsync("/api/integrations/webhooks/stripe",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal((HttpStatusCode)503, res.StatusCode);
    }

    /// <summary>
    /// Meta's X-Hub-Signature-256 (the WhatsApp Cloud and Messenger/Instagram
    /// webhooks' only authentication — <c>WebhooksEndpoints</c> /
    /// <c>MetaWebhookEndpoints</c>): hex HMAC-SHA256 of the RAW body under the app
    /// secret. A tampered body, a wrong secret, a missing header and a header
    /// carrying something other than that digest are all rejected.
    /// </summary>
    [Fact]
    public void Meta_hmac_verifies_and_rejects()
    {
        var secret = "app-secret-1";
        var body = Encoding.UTF8.GetBytes("""{"entry":[{"id":"WABA-1"}]}""");
        static string Hex(string secret, byte[] body)
        {
            using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            return Convert.ToHexString(h.ComputeHash(body)).ToLowerInvariant();
        }
        var good = "sha256=" + Hex(secret, body);

        Assert.True(WebhookVerify.Meta(secret, body, good));
        // the digest's hex case is irrelevant; the "sha256=" label is matched literally
        Assert.True(WebhookVerify.Meta(secret, body, "sha256=" + Hex(secret, body).ToUpperInvariant()));
        Assert.False(WebhookVerify.Meta(secret, body, "SHA256=" + Hex(secret, body)));
        // tampered body: the same signature over different bytes
        Assert.False(WebhookVerify.Meta(secret, Encoding.UTF8.GetBytes("""{"entry":[{"id":"WABA-2"}]}"""), good));
        Assert.False(WebhookVerify.Meta("another-app-secret", body, good));     // wrong secret
        Assert.False(WebhookVerify.Meta("", body, good));                       // no secret ⇒ false
        Assert.False(WebhookVerify.Meta(secret, body, ""));                     // missing header
        Assert.False(WebhookVerify.Meta(secret, body, "   "));                  // blank-ish header
        Assert.False(WebhookVerify.Meta(secret, body, "sha256="));              // prefix, no digest
        Assert.False(WebhookVerify.Meta(secret, body, "sha1=" + Hex(secret, body)));  // wrong algorithm label
        Assert.False(WebhookVerify.Meta(secret, body, "not-a-signature"));      // malformed
        // DOCUMENTED leniency, pinned here: the "sha256=" prefix is optional, so a
        // bare correct digest verifies. That is not a hole (it still takes the app
        // secret to produce it) and Meta always sends the prefix — but a change of
        // mind about it must break this line rather than a live install.
        Assert.True(WebhookVerify.Meta(secret, body, Hex(secret, body)));
        // …while a bare digest of the WRONG body or secret is still refused
        Assert.False(WebhookVerify.Meta(secret, body, Hex("another-app-secret", body)));
    }

    [Fact]
    public void Shopify_hmac_verifies_and_rejects()
    {
        var secret = "shhh";
        var body = Encoding.UTF8.GetBytes("""{"id":123}""");
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var good = Convert.ToBase64String(h.ComputeHash(body));
        Assert.True(WebhookVerify.Shopify(secret, body, good));
        Assert.False(WebhookVerify.Shopify(secret, body, "wrong"));
        Assert.False(WebhookVerify.Shopify("", body, good)); // no secret ⇒ false
    }

    [Fact]
    public void Stripe_hmac_honours_replay_window()
    {
        var secret = "whsec_test";
        var body = Encoding.UTF8.GetBytes("""{"type":"payment_intent.succeeded"}""");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string Sign(long ts)
        {
            var signed = Encoding.UTF8.GetBytes($"{ts}.").Concat(body).ToArray();
            using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            return $"t={ts},v1={Convert.ToHexString(h.ComputeHash(signed)).ToLowerInvariant()}";
        }
        Assert.True(WebhookVerify.Stripe(secret, body, Sign(now)));
        Assert.False(WebhookVerify.Stripe(secret, body, Sign(now - 10_000))); // outside window
    }

    [Fact]
    public async Task Record_sync_upserts_records_and_contacts()
    {
        var admin = await Authed("admin", "admin123");
        var payload = new
        {
            records = new[]
            {
                new { externalId = "ORD-1", type = "order", title = "Order 1", status = "open",
                      amount = 99.5m, contact = new { phone = "60123999888", name = "Bob", email = (string?)null } },
            },
        };
        var first = await admin.PostAsJsonAsync("/api/v1/sync/records", payload);
        first.EnsureSuccessStatusCode();
        Assert.Equal(1, (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("created").GetInt32());

        // idempotent — same external_id updates, not duplicates
        var second = await admin.PostAsJsonAsync("/api/v1/sync/records", payload);
        Assert.Equal(1, (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("updated").GetInt32());
    }
}
