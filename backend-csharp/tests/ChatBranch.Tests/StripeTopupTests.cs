using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Integrations;
using ChatBranch.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// A Stripe API that never leaves the process: it records every Checkout Session
/// request and answers a fake session (or a refusal, when told to).
/// </summary>
public sealed class FakeStripe : IStripeGateway
{
    public sealed record Call(string SecretKey, Dictionary<string, string> Form, string IdempotencyKey);
    public readonly ConcurrentQueue<Call> Calls = new();
    public volatile bool Refuse;
    private int _n;

    public Task<StripeResponse> CreateCheckoutSessionAsync(string secretKey, IReadOnlyDictionary<string, string> form,
        string idempotencyKey, CancellationToken ct = default)
    {
        Calls.Enqueue(new Call(secretKey, new Dictionary<string, string>(form), idempotencyKey));
        if (Refuse) return Task.FromResult(new StripeResponse(false, "HTTP 400 Invalid currency", 400));
        var id = $"cs_test_{Interlocked.Increment(ref _n)}_{Guid.NewGuid():N}"[..30];
        return Task.FromResult(new StripeResponse(true,
            JsonSerializer.Serialize(new { id, url = $"https://checkout.stripe.test/c/pay/{id}", @object = "checkout.session" }),
            200));
    }
}

/// <summary>The normal test host with <see cref="FakeStripe"/> as the Stripe API.</summary>
public sealed class StripeFactory : ApiFactory
{
    public readonly FakeStripe Stripe = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s => s.AddSingleton<IStripeGateway>(Stripe));
    }
}

/// <summary>
/// AI credit phases 3 + 4: Stripe Checkout top-ups and offers. Signed FAKE webhook
/// events only — nothing here reaches Stripe. A paid session credits exactly once
/// (a literally replayed request too), payment_intent.succeeded alone credits
/// nothing, a refund or dispute writes a new negative line, a livemode or currency
/// mismatch never credits, an offer's bonus is its own promo_bonus line and
/// max_redemptions holds under concurrency.
/// </summary>
public class StripeTopupTests : IClassFixture<StripeFactory>
{
    private const string WebhookSecret = "whsec_test_fixture_only";
    private const string Webhook = "/api/integrations/webhooks/stripe";
    private readonly StripeFactory _f;

    public StripeTopupTests(StripeFactory f)
    {
        _f = f;
        _ = _f.CreateClient(); // boot + seed
        With(db =>
        {
            var sc = new ScopingService(db);
            // configuration as the operator sets it — Setting rows, never the process env
            sc.SetSetting(StripeConfig.SecretKeyKey, "sk_test_fixture_only");
            sc.SetSetting(StripeConfig.WebhookSecretKey, WebhookSecret);
            sc.SetSetting(StripeConfig.ModeKey, "test");
            return 0;
        });
    }

    private T With<T>(Func<AppDbContext, T> act) => Platform.WithDb(_f, act);

    private static int _n;
    private static string Sfx() => $"{Interlocked.Increment(ref _n)}{Guid.NewGuid():N}"[..8];

    private async Task<HttpClient> Su() => await Platform.As(_f, "superadmin", "super123");

    private static async Task SetCurrency(HttpClient su, string currency) =>
        (await su.PutAsJsonAsync("/api/billing/ai/settings", new { currency, usd_rate = currency == "USD" ? 1 : 4.5 }))
        .EnsureSuccessStatusCode();

    private async Task<(int Id, string Slug)> Company(HttpClient su)
    {
        var slug = $"topup-co-{Sfx()}";
        Assert.Equal(HttpStatusCode.Created, (await su.PostAsJsonAsync("/api/v1/entities",
            new { name = $"Topup Co {slug}", slug })).StatusCode);
        return (With(db => db.Entities.Single(e => e.Slug == slug).Id), slug);
    }

    private async Task<(AiCreditPayment Row, JsonElement Res)> Topup(HttpClient c, string company, double amount,
        string? offer = null, string? key = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/billing/ai/credit/topup")
        {
            Content = JsonContent.Create(new
            {
                company, amount, offer,
                success_url = "http://localhost/billing/topup/success",
                cancel_url = "http://localhost/billing/topup/cancel",
            }),
        };
        if (key != null) req.Headers.Add("Idempotency-Key", key);
        var res = await c.SendAsync(req);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(res.IsSuccessStatusCode, body.GetRawText());
        var id = body.GetProperty("payment").GetProperty("id").GetInt32();
        return (With(db => db.AiCreditPayments.AsNoTracking().Single(p => p.Id == id)), body);
    }

    // ---- signed fake events ------------------------------------------------------------

    private static string Sign(string body, string secret = WebhookSecret, long? ts = null)
    {
        var t = (ts ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString(CultureInfo.InvariantCulture);
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var sig = Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes($"{t}.{body}"))).ToLowerInvariant();
        return $"t={t},v1={sig}";
    }

    private static string Event(string type, object obj, bool livemode = false, string? id = null) =>
        JsonSerializer.Serialize(new
        {
            id = id ?? "evt_" + Guid.NewGuid().ToString("N"),
            @object = "event",
            type,
            livemode,
            data = new { @object = obj },
        });

    private static string Completed(AiCreditPayment p, string pi, string? id = null, bool livemode = false,
        long? amount = null, string? currency = null, string paymentStatus = "paid",
        string type = "checkout.session.completed") =>
        Event(type, new
        {
            id = p.SessionId, @object = "checkout.session", client_reference_id = p.Uuid,
            amount_total = amount ?? p.AmountMinor, currency = currency ?? p.Currency.ToLowerInvariant(),
            payment_status = paymentStatus, payment_intent = pi, livemode,
        }, livemode, id);

    private async Task<(HttpStatusCode Status, JsonElement Body)> Deliver(string body, string? signature = null)
    {
        var c = _f.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, Webhook)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("Stripe-Signature", signature ?? Sign(body));
        var res = await c.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        return (res.StatusCode, text.Length > 0 ? JsonDocument.Parse(text).RootElement.Clone() : default);
    }

    private double Balance(int company) => With(db =>
        db.AiCreditAccounts.AsNoTracking().Where(a => a.EntityId == company).Select(a => (double?)a.Balance)
            .FirstOrDefault() ?? 0);

    private List<AiCreditEntry> Lines(int company) => With(db =>
        db.AiCreditEntries.AsNoTracking().Where(e => e.EntityId == company).OrderBy(e => e.Id).ToList());

    private AiCreditPayment Row(int id) => With(db => db.AiCreditPayments.AsNoTracking().Single(p => p.Id == id));

    // ---- the happy path, exactly once ---------------------------------------------------

    [Fact]
    public async Task A_completed_session_credits_exactly_once_even_when_the_request_is_replayed()
    {
        var su = await Su();
        await SetCurrency(su, "MYR");
        var (company, slug) = await Company(su);

        var (row, res) = await Topup(su, slug, 100);
        Assert.StartsWith("https://checkout.stripe.test/", res.GetProperty("url").GetString());
        Assert.Equal("created", row.Status);
        Assert.Equal(10000, row.AmountMinor);           // minor units (sen)
        Assert.Equal("MYR", row.Currency);
        Assert.Equal(100.0, row.CreditAmount, 6);       // decided at creation
        Assert.Equal("test", row.Mode);
        // what went to Stripe: the uuid (not the row id), myr minor units, an Idempotency-Key
        var call = _f.Stripe.Calls.Last(c => c.Form["client_reference_id"] == row.Uuid);
        Assert.Equal("sk_test_fixture_only", call.SecretKey);
        Assert.Equal("10000", call.Form["line_items[0][price_data][unit_amount]"]);
        Assert.Equal("myr", call.Form["line_items[0][price_data][currency]"]);
        Assert.Equal("payment", call.Form["mode"]);
        Assert.Equal("chatbranch-topup-" + row.Uuid, call.IdempotencyKey);
        Assert.Contains($"payment={row.Id}", call.Form["success_url"]);
        Assert.StartsWith("http://localhost/billing/topup/success", call.Form["success_url"]);

        var body = Completed(row, "pi_" + Sfx());
        var signature = Sign(body);
        var (status, answer) = await Deliver(body, signature);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(100.0, answer.GetProperty("credited").GetDouble(), 6);
        Assert.Equal(100.0, Balance(company), 6);

        // the LITERAL same request — body and signature — credits nothing more
        var (again, againBody) = await Deliver(body, signature);
        Assert.Equal(HttpStatusCode.OK, again);
        Assert.True(againBody.GetProperty("duplicate").GetBoolean());
        Assert.Equal(100.0, Balance(company), 6);

        // a DIFFERENT event for the same session (async_payment_succeeded, a resend with a
        // new id) cannot credit either: the created → paid flip happens once
        var (_, other) = await Deliver(Completed(row, "pi_other", type: "checkout.session.async_payment_succeeded"));
        Assert.True(other.GetProperty("duplicate").GetBoolean());
        Assert.Equal(100.0, Balance(company), 6);

        var lines = Lines(company);
        var line = Assert.Single(lines);
        Assert.Equal(AiCreditKinds.Topup, line.Kind);
        Assert.Equal(row.Id, line.PaymentId);
        Assert.StartsWith("stripe:evt_", line.IdempotencyKey);
        Assert.Equal("stripe", line.Actor);
        Assert.Equal(AuditSources.Webhook, line.Source);
        var paid = Row(row.Id);
        Assert.Equal("paid", paid.Status);
        Assert.NotNull(paid.CompletedAt);
        Assert.Equal(line.IdempotencyKey, "stripe:" + paid.LastEventId);
        Assert.Equal(1, With(db => db.StripeEvents.Count(e => e.PaymentId == row.Id && e.Status == "processed"
                                                                && e.Type == "checkout.session.completed")));

        // the buyer's status read, and the activity log
        var read = await su.GetFromJsonAsync<JsonElement>($"/api/billing/ai/credit/topup?payment={row.Id}");
        Assert.Equal("paid", read.GetProperty("payment").GetProperty("status").GetString());
        Assert.Contains(await Platform.Audit(su, "action=billing.credit.topup.paid"),
            a => a.GetProperty("target").GetString() == $"payment:{row.Id}");
        Assert.Empty(With(db => AiCredit.ReconcileAsync(db).GetAwaiter().GetResult()));
    }

    [Fact]
    public async Task Payment_intent_succeeded_alone_credits_nothing()
    {
        var su = await Su();
        await SetCurrency(su, "MYR");
        var (company, slug) = await Company(su);
        var (row, _) = await Topup(su, slug, 50);

        var (status, body) = await Deliver(Event("payment_intent.succeeded", new
        {
            id = "pi_" + Sfx(), @object = "payment_intent", amount = row.AmountMinor, currency = "myr",
            status = "succeeded", metadata = new { payment_uuid = row.Uuid },
        }));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.TryGetProperty("ignored", out _));
        Assert.Empty(Lines(company));
        Assert.Equal("created", Row(row.Id).Status);

        // an unpaid completion (a delayed method still settling) credits nothing either
        var (_, unpaid) = await Deliver(Completed(row, "pi_x", paymentStatus: "unpaid"));
        Assert.True(unpaid.TryGetProperty("ignored", out _));
        Assert.Empty(Lines(company));

        // a session that is not a top-up (the AI payment-link skill shares the account) is ignored
        var (_, foreign) = await Deliver(Event("checkout.session.completed", new
        {
            id = "cs_test_foreign_" + Sfx(), client_reference_id = "", amount_total = 999, currency = "myr",
            payment_status = "paid", payment_intent = "pi_foreign",
        }));
        Assert.True(foreign.TryGetProperty("ignored", out _));
    }

    [Fact]
    public async Task A_crash_between_the_claim_and_the_credit_is_finished_by_stripes_retry()
    {
        var su = await Su();
        await SetCurrency(su, "MYR");
        var (company, slug) = await Company(su);
        var (row, _) = await Topup(su, slug, 60);
        var evt = "evt_crash_" + Guid.NewGuid().ToString("N");
        var pi = "pi_" + Sfx();
        // the claim committed (event claimed + row flipped to paid), then the process died
        // before any ledger line was written
        With(db =>
        {
            db.StripeEvents.Add(new StripeEvent
            {
                EventId = evt, Type = "checkout.session.completed", Status = "claimed", PaymentId = row.Id,
                ReceivedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
            return db.AiCreditPayments.Where(p => p.Id == row.Id).ExecuteUpdate(u => u
                .SetProperty(p => p.Status, "paid").SetProperty(p => p.LastEventId, evt)
                .SetProperty(p => p.PaymentIntentId, pi));
        });
        Assert.Empty(Lines(company));

        // Stripe retries the same event: the credit is written, once
        var body = Completed(Row(row.Id), pi, id: evt);
        Assert.Equal(HttpStatusCode.OK, (await Deliver(body)).Status);
        Assert.Equal(60.0, Balance(company), 6);
        Assert.Equal("processed", With(db => db.StripeEvents.Single(e => e.EventId == evt).Status));
        Assert.True((await Deliver(body)).Body.GetProperty("duplicate").GetBoolean());
        Assert.Single(Lines(company));

        // the same for a refund whose claim (15.00 of it) committed but whose debit did not
        var revt = "evt_crash_refund_" + Guid.NewGuid().ToString("N");
        With(db =>
        {
            db.StripeEvents.Add(new StripeEvent
            {
                EventId = revt, Type = "charge.refunded", Status = "claimed", PaymentId = row.Id, AmountMinor = 1500,
                ReceivedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
            return db.AiCreditPayments.Where(p => p.Id == row.Id).ExecuteUpdate(u => u
                .SetProperty(p => p.RefundedMinor, 1500).SetProperty(p => p.LastEventId, revt));
        });
        var refund = Event("charge.refunded", new { payment_intent = pi, amount = 6000, amount_refunded = 1500 }, id: revt);
        Assert.Equal(HttpStatusCode.OK, (await Deliver(refund)).Status);
        Assert.Equal(45.0, Balance(company), 6);
        Assert.Equal(HttpStatusCode.OK, (await Deliver(refund)).Status);
        Assert.Equal(45.0, Balance(company), 6);
        Assert.Equal(2, Lines(company).Count);
        Assert.Empty(With(db => AiCredit.ReconcileAsync(db).GetAwaiter().GetResult()));
    }

    // ---- refunds and disputes ------------------------------------------------------------

    [Fact]
    public async Task A_refund_writes_one_negative_entry_and_never_edits_the_top_up()
    {
        var su = await Su();
        await SetCurrency(su, "MYR");
        var (company, slug) = await Company(su);
        var (row, _) = await Topup(su, slug, 100);
        var pi = "pi_" + Sfx();
        Assert.Equal(HttpStatusCode.OK, (await Deliver(Completed(row, pi))).Status);
        var topup = Assert.Single(Lines(company));

        // a partial refund: RM 40 of RM 100 (Stripe's amount_refunded is cumulative)
        var refund = Event("charge.refunded", new
        {
            id = "ch_" + Sfx(), @object = "charge", payment_intent = pi, amount = 10000, amount_refunded = 4000,
            refunded = false,
        });
        var sig = Sign(refund);
        Assert.Equal(HttpStatusCode.OK, (await Deliver(refund, sig)).Status);
        Assert.Equal(60.0, Balance(company), 6);
        // replayed: debited once
        Assert.Equal(HttpStatusCode.OK, (await Deliver(refund, sig)).Status);
        Assert.Equal(60.0, Balance(company), 6);
        var lines = Lines(company);
        Assert.Equal(2, lines.Count);
        Assert.Equal(AiCreditKinds.Refund, lines[1].Kind);
        Assert.Equal(-40.0, lines[1].Amount, 6);
        Assert.Equal(row.Id, lines[1].PaymentId);
        Assert.Equal("paid", Row(row.Id).Status);   // partial: still paid

        // the rest: cumulative 100 → only the new RM 60 is taken back
        Assert.Equal(HttpStatusCode.OK, (await Deliver(Event("charge.refunded", new
        {
            id = "ch_x", payment_intent = pi, amount = 10000, amount_refunded = 10000, refunded = true,
        }))).Status);
        Assert.Equal(0.0, Balance(company), 6);
        Assert.Equal(-60.0, Lines(company)[2].Amount, 6);
        var after = Row(row.Id);
        Assert.Equal("refunded", after.Status);
        Assert.Equal(10000, after.RefundedMinor);

        // the original top-up line is untouched
        var original = With(db => db.AiCreditEntries.AsNoTracking().Single(e => e.Id == topup.Id));
        Assert.Equal(100.0, original.Amount, 6);
        Assert.Equal(topup.IdempotencyKey, original.IdempotencyKey);

        // a dispute after a full refund has nothing left to take
        Assert.Equal(HttpStatusCode.OK, (await Deliver(Event("charge.dispute.created", new
        {
            id = "dp_" + Sfx(), @object = "dispute", payment_intent = pi, amount = 10000, charge = "ch_x",
        }))).Status);
        Assert.Equal(3, Lines(company).Count);
        Assert.Contains(await Platform.Audit(su, "action=billing.credit.refund"),
            a => a.GetProperty("target").GetString() == $"payment:{row.Id}");
        Assert.Empty(With(db => AiCredit.ReconcileAsync(db).GetAwaiter().GetResult()));
    }

    [Fact]
    public async Task A_dispute_writes_a_chargeback_line_and_may_take_the_balance_negative()
    {
        var su = await Su();
        await SetCurrency(su, "MYR");
        var (company, slug) = await Company(su);
        var (row, _) = await Topup(su, slug, 80);
        var pi = "pi_" + Sfx();
        await Deliver(Completed(row, pi));
        // the credit was spent before the dispute landed
        (await su.PostAsJsonAsync("/api/billing/ai/credit/adjust", new { entity = slug, amount = -70, note = "spent" }))
            .EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.OK, (await Deliver(Event("charge.dispute.created", new
        {
            id = "dp_" + Sfx(), @object = "dispute", payment_intent = pi, amount = 8000,
        }))).Status);
        var last = Lines(company).Last();
        Assert.Equal(AiCreditKinds.Chargeback, last.Kind);
        Assert.Equal(-80.0, last.Amount, 6);
        Assert.Equal(-70.0, Balance(company), 6);   // signed, never clamped: the unrecoverable loss is visible
        Assert.Equal("disputed", Row(row.Id).Status);
    }

    // ---- refusals ------------------------------------------------------------------------

    [Fact]
    public async Task A_livemode_mismatch_is_refused_and_credits_nothing()
    {
        var su = await Su();
        await SetCurrency(su, "MYR");
        var (company, slug) = await Company(su);
        var (row, _) = await Topup(su, slug, 30);

        var (status, _) = await Deliver(Completed(row, "pi_live", livemode: true));
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Empty(Lines(company));
        Assert.Equal("created", Row(row.Id).Status);
        Assert.Contains(await Platform.Audit(su, "action=billing.credit.stripe.refused"), _ => true);

        // a refund in the other mode is refused the same way
        var (refund, _) = await Deliver(Event("charge.refunded", new { payment_intent = "pi_live", amount_refunded = 1 },
            livemode: true));
        Assert.Equal(HttpStatusCode.BadRequest, refund);
    }

    [Fact]
    public async Task A_currency_mismatch_is_refused_and_held_for_review()
    {
        var su = await Su();
        await SetCurrency(su, "MYR");
        var (company, slug) = await Company(su);

        // (a) Stripe reports a different currency than the top-up was opened in
        var (row, _) = await Topup(su, slug, 40);
        var (status, body) = await Deliver(Completed(row, "pi_" + Sfx(), currency: "usd"));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.TryGetProperty("review", out _));
        Assert.Empty(Lines(company));
        Assert.Equal("review", Row(row.Id).Status);

        // (b) the billing currency changed between checkout and payment
        var (row2, _) = await Topup(su, slug, 40);
        await SetCurrency(su, "USD");
        try
        {
            var (s2, b2) = await Deliver(Completed(row2, "pi_" + Sfx()));
            Assert.Equal(HttpStatusCode.OK, s2);
            Assert.Contains("billing currency", b2.GetProperty("review").GetString());
            Assert.Empty(Lines(company));
            Assert.Equal("review", Row(row2.Id).Status);
            Assert.Contains(await Platform.Audit(su, "action=billing.credit.topup.review"),
                a => a.GetProperty("target").GetString() == $"payment:{row2.Id}");
            // and a new top-up for a company whose credit is held in MYR is refused outright
            (await su.PostAsJsonAsync("/api/billing/ai/credit/grant", new { entity = slug, amount = 1 }))
                .EnsureSuccessStatusCode();   // opens the account… in USD now
        }
        finally
        {
            await SetCurrency(su, "MYR");
        }
        // the account is USD, billing is MYR again → top-ups refused, no session opened
        var before = _f.Stripe.Calls.Count;
        var refused = await su.PostAsJsonAsync("/api/billing/ai/credit/topup", new { company = slug, amount = 20 });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(before, _f.Stripe.Calls.Count);
    }

    [Fact]
    public async Task Unsigned_or_badly_signed_events_are_401_and_no_secret_is_503()
    {
        var body = Event("checkout.session.completed", new { id = "cs_x", payment_status = "paid" });
        var c = _f.CreateClient();
        var unsigned = await c.PostAsync(Webhook, new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Deliver(body, Sign(body, "whsec_wrong"))).Status);
        // a stale timestamp (outside the 300 s window) is a replay
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Deliver(body, Sign(body, ts: DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 900))).Status);
        // a tampered body under a good signature
        Assert.Equal(HttpStatusCode.Unauthorized, (await Deliver(body.Replace("cs_x", "cs_y"), Sign(body))).Status);

        With(db => { new ScopingService(db).SetSetting(StripeConfig.WebhookSecretKey, ""); return 0; });
        try
        {
            Assert.Equal((HttpStatusCode)503, (await Deliver(body)).Status);
            // and with no webhook secret, a top-up is refused before any money can move
            var su = await Su();
            var (_, slug) = await Company(su);
            var res = await su.PostAsJsonAsync("/api/billing/ai/credit/topup", new { company = slug, amount = 20 });
            Assert.Equal((HttpStatusCode)503, res.StatusCode);
        }
        finally
        {
            With(db => { new ScopingService(db).SetSetting(StripeConfig.WebhookSecretKey, WebhookSecret); return 0; });
        }
    }

    [Fact]
    public async Task A_live_key_in_test_mode_or_no_key_refuses_top_ups()
    {
        var su = await Su();
        var (_, slug) = await Company(su);
        With(db => { new ScopingService(db).SetSetting(StripeConfig.SecretKeyKey, "sk_live_fixture_only"); return 0; });
        try
        {
            var res = await su.PostAsJsonAsync("/api/billing/ai/credit/topup", new { company = slug, amount = 20 });
            Assert.Equal((HttpStatusCode)503, res.StatusCode);
            var read = await su.GetFromJsonAsync<JsonElement>("/api/billing/ai/credit/topup");
            Assert.False(read.GetProperty("ready").GetBoolean());
            Assert.Contains("test-mode key", read.GetProperty("reason").GetString());
        }
        finally
        {
            With(db => { new ScopingService(db).SetSetting(StripeConfig.SecretKeyKey, "sk_test_fixture_only"); return 0; });
        }
    }

    // ---- idempotent creation, permissions ------------------------------------------------

    [Fact]
    public async Task A_double_clicked_pay_button_opens_one_session()
    {
        var su = await Su();
        await SetCurrency(su, "MYR");
        var (_, slug) = await Company(su);
        var key = Guid.NewGuid().ToString("N");
        var before = _f.Stripe.Calls.Count;
        var (first, r1) = await Topup(su, slug, 25, key: key);
        var (second, r2) = await Topup(su, slug, 25, key: key);
        Assert.Equal(first.Id, second.Id);
        Assert.True(r2.GetProperty("replayed").GetBoolean());
        Assert.Equal(r1.GetProperty("url").GetString(), r2.GetProperty("url").GetString());
        Assert.Equal(before + 1, _f.Stripe.Calls.Count);

        // Stripe refusing the request: the row is failed, nothing to pay
        _f.Stripe.Refuse = true;
        try
        {
            var res = await su.PostAsJsonAsync("/api/billing/ai/credit/topup", new { company = slug, amount = 25 });
            Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);
            Assert.Equal("failed", With(db => db.AiCreditPayments.AsNoTracking().OrderByDescending(p => p.Id).First().Status));
        }
        finally { _f.Stripe.Refuse = false; }
    }

    [Fact]
    public async Task Buying_needs_billing_topup_and_offers_need_billing_manage()
    {
        var su = await Su();
        var admin = await Platform.As(_f, "admin", "admin123");
        var agent = await Platform.As(_f, "agent", "agent123");
        var (_, slug) = await Company(su);

        // an admin holds billing.topup by default; an agent does not
        (await admin.GetAsync("/api/billing/ai/credit/topup")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync("/api/billing/ai/credit/topup")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsJsonAsync("/api/billing/ai/credit/topup",
            new { company = slug, amount = 20 })).StatusCode);
        // offers are a billing manager's
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/billing/ai/credit/offers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync("/api/billing/ai/credit/offers",
            new { label = "nope", kind = "bonus_pct", bonus_pct = 5 })).StatusCode);
        // input guards
        Assert.Equal(HttpStatusCode.BadRequest, (await su.PostAsJsonAsync("/api/billing/ai/credit/topup",
            new { company = slug, amount = 1 })).StatusCode);      // below the minimum
        Assert.Equal(HttpStatusCode.BadRequest, (await su.PostAsJsonAsync("/api/billing/ai/credit/topup",
            new { amount = 20 })).StatusCode);                     // which company?
    }

    // ---- offers --------------------------------------------------------------------------

    private async Task<string> Offer(HttpClient su, object body)
    {
        var res = await su.PostAsJsonAsync("/api/billing/ai/credit/offers", body);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(res.IsSuccessStatusCode, json.GetRawText());
        return json.GetProperty("offer").GetProperty("slug").GetString()!;
    }

    [Fact]
    public async Task An_offers_bonus_is_a_separate_promo_bonus_entry()
    {
        var su = await Su();
        await SetCurrency(su, "MYR");
        var (company, slug) = await Company(su);
        var offer = await Offer(su, new { label = $"Plus ten {Sfx()}", kind = "bonus_pct", bonus_pct = 10, pay_amount = 100 });

        // below the offer's minimum: refused
        Assert.Equal(HttpStatusCode.BadRequest, (await su.PostAsJsonAsync("/api/billing/ai/credit/topup",
            new { company = slug, amount = 50, offer })).StatusCode);
        // the buyer's read lists it
        var options = await su.GetFromJsonAsync<JsonElement>($"/api/billing/ai/credit/topup?company={slug}");
        Assert.Contains(options.GetProperty("offers").EnumerateArray(), o => o.GetProperty("slug").GetString() == offer);

        var (row, _) = await Topup(su, slug, 200, offer);
        Assert.Equal(200.0, row.CreditAmount, 6);
        Assert.Equal(20.0, row.BonusAmount, 6);
        Assert.Equal(20000, row.AmountMinor);          // the bonus is not charged
        var offerId = With(db => db.AiCreditOffers.Single(o => o.Slug == offer).Id);
        Assert.Equal(offerId, row.OfferId);
        Assert.Equal(1, With(db => db.AiCreditOffers.Single(o => o.Id == offerId).Redemptions));

        var pi = "pi_" + Sfx();
        await Deliver(Completed(row, pi));
        var lines = Lines(company);
        Assert.Equal(2, lines.Count);
        Assert.Equal(AiCreditKinds.Topup, lines[0].Kind);
        Assert.Equal(200.0, lines[0].Amount, 6);
        Assert.Equal(AiCreditKinds.PromoBonus, lines[1].Kind);
        Assert.Equal(20.0, lines[1].Amount, 6);
        Assert.Equal(offerId, lines[1].OfferId);
        Assert.Equal(row.Id, lines[1].PaymentId);
        Assert.Equal(lines[0].IdempotencyKey + ":bonus", lines[1].IdempotencyKey);
        Assert.Equal(220.0, Balance(company), 6);

        // a half refund takes back half the paid credit AND half its bonus, as new lines
        await Deliver(Event("charge.refunded", new { payment_intent = pi, amount = 20000, amount_refunded = 10000 }));
        var refunds = Lines(company).Where(l => l.Kind == AiCreditKinds.Refund).ToList();
        Assert.Equal(2, refunds.Count);
        Assert.Equal(-100.0, refunds[0].Amount, 6);
        Assert.Equal(-10.0, refunds[1].Amount, 6);
        Assert.Equal(110.0, Balance(company), 6);

        // a manual grant of a fixed bonus by a billing manager
        var fixedOffer = await Offer(su, new { label = $"Gift {Sfx()}", kind = "bonus_fixed", bonus_fixed = 15 });
        var g = await su.PostAsJsonAsync($"/api/billing/ai/credit/offers/{fixedOffer}/grant", new { entity = slug, note = "goodwill" });
        g.EnsureSuccessStatusCode();
        Assert.Equal("promo_bonus", Lines(company).Last().Kind);
        Assert.Equal(125.0, Balance(company), 6);
        // a percentage offer granted by hand needs its base amount
        Assert.Equal(HttpStatusCode.BadRequest, (await su.PostAsJsonAsync(
            $"/api/billing/ai/credit/offers/{offer}/grant", new { entity = slug })).StatusCode);
        // a used offer cannot be deleted, only deactivated
        Assert.Equal(HttpStatusCode.Conflict, (await su.DeleteAsync($"/api/billing/ai/credit/offers/{offer}")).StatusCode);
        Assert.Contains(await Platform.Audit(su, "action=billing.credit.offer.save"),
            a => a.GetProperty("target").GetString() == $"offer:{offer}");
        Assert.Empty(With(db => AiCredit.ReconcileAsync(db).GetAwaiter().GetResult()));
    }

    [Fact]
    public async Task Max_redemptions_holds_under_concurrent_redemption()
    {
        var su = await Su();
        await SetCurrency(su, "MYR");
        var (company, slug) = await Company(su);

        // manual grants, ten at once, against an offer that allows three
        var grantOffer = await Offer(su, new { label = $"Three only {Sfx()}", kind = "bonus_fixed", bonus_fixed = 5, max_redemptions = 3 });
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(async i =>
        {
            var c = await Su();
            return (await c.PostAsJsonAsync($"/api/billing/ai/credit/offers/{grantOffer}/grant",
                new { entity = slug, idempotency_key = $"race-{grantOffer}-{i}" })).StatusCode;
        }));
        Assert.Equal(3, results.Count(s => s == HttpStatusCode.OK));
        Assert.Equal(7, results.Count(s => s == HttpStatusCode.Conflict));
        Assert.Equal(3, With(db => db.AiCreditOffers.Single(o => o.Slug == grantOffer).Redemptions));
        Assert.Equal(3, Lines(company).Count(l => l.Kind == AiCreditKinds.PromoBonus));
        Assert.Equal(15.0, Balance(company), 6);

        // top-ups, six at once, against an offer that allows two
        var buyOffer = await Offer(su, new { label = $"Two only {Sfx()}", kind = "bonus_pct", bonus_pct = 5, max_redemptions = 2 });
        var buys = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            var c = await Su();
            return (await c.PostAsJsonAsync("/api/billing/ai/credit/topup",
                new { company = slug, amount = 20, offer = buyOffer })).StatusCode;
        }));
        Assert.Equal(2, buys.Count(s => s == HttpStatusCode.OK));
        Assert.Equal(2, With(db => db.AiCreditOffers.Single(o => o.Slug == buyOffer).Redemptions));

        // an abandoned checkout expires: its redemption is given back
        var expiring = With(db => db.AiCreditPayments.AsNoTracking()
            .Where(p => p.OfferId == db.AiCreditOffers.Single(o => o.Slug == buyOffer).Id).OrderBy(p => p.Id).First());
        var (st, _) = await Deliver(Event("checkout.session.expired", new
        {
            id = expiring.SessionId, client_reference_id = expiring.Uuid, payment_status = "unpaid",
        }));
        Assert.Equal(HttpStatusCode.OK, st);
        Assert.Equal("expired", Row(expiring.Id).Status);
        Assert.Equal(1, With(db => db.AiCreditOffers.Single(o => o.Slug == buyOffer).Redemptions));
        Assert.Empty(With(db => AiCredit.ReconcileAsync(db).GetAwaiter().GetResult()));
    }

    // ---- MCP: the safe reads are tools, the purchase is not ------------------------------

    [Fact]
    public async Task The_offer_tools_work_over_mcp()
    {
        var su = await Su();
        await SetCurrency(su, "MYR");
        var key = Platform.Client(_f, Platform.MintKey(_f, "superadmin"));
        var saved = await Platform.CallTool(key, "billing_save_ai_credit_offer",
            new { label = $"Mcp offer {Sfx()}", kind = "bonus_fixed", bonus_fixed = 3 });
        Assert.False(saved.GetProperty("isError").GetBoolean(), saved.GetProperty("content")[0].GetProperty("text").GetString());
        var list = await Platform.CallTool(key, "billing_get_ai_credit_offers", new { });
        Assert.False(list.GetProperty("isError").GetBoolean());
        var options = await Platform.CallTool(key, "billing_get_ai_credit_topup", new { });
        Assert.False(options.GetProperty("isError").GetBoolean());
        Assert.True(options.GetProperty("structuredContent").GetProperty("ready").GetBoolean());
    }

    // ---- the schema upgrade on an existing SQLite database --------------------------------

    private static List<string> Objects(AppDbContext db, string type, string table)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT name FROM sqlite_master WHERE type = '{type}' AND tbl_name = '{table}'";
        using var r = cmd.ExecuteReader();
        var names = new List<string>();
        while (r.Read()) names.Add(r.GetString(0));
        return names;
    }

    [Fact]
    public void Upgrading_an_existing_sqlite_database_adds_the_topup_tables()
    {
        var file = Path.Combine(Path.GetTempPath(), $"cb-topup-upgrade-{Guid.NewGuid():N}.sqlite3");
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={file}").Options;
        try
        {
            using (var db = new AppDbContext(options))
            {
                db.Database.EnsureCreated();
                db.Database.ExecuteSqlRaw("DROP TABLE \"crm_aicreditpayment\"");
                db.Database.ExecuteSqlRaw("DROP TABLE \"crm_aicreditoffer\"");
                db.Database.ExecuteSqlRaw("DROP TABLE \"crm_stripeevent\"");
                Assert.Empty(Objects(db, "table", "crm_aicreditpayment"));
            }

            using (var db = new AppDbContext(options)) Seed.Run(db);

            using (var db = new AppDbContext(options))
            {
                Assert.NotEmpty(Objects(db, "table", "crm_aicreditpayment"));
                Assert.NotEmpty(Objects(db, "table", "crm_aicreditoffer"));
                Assert.NotEmpty(Objects(db, "table", "crm_stripeevent"));
                var payIdx = Objects(db, "index", "crm_aicreditpayment");
                foreach (var ix in new[] { "uniq_aicreditpayment_session", "uniq_aicreditpayment_uuid",
                             "uniq_aicreditpayment_idem", "ix_aicreditpayment_entity_status", "ix_aicreditpayment_intent" })
                    Assert.Contains(ix, payIdx);
                Assert.Contains("uniq_aicreditoffer_slug", Objects(db, "index", "crm_aicreditoffer"));
                Assert.Contains("uniq_stripeevent_event", Objects(db, "index", "crm_stripeevent"));
                Seed.Run(db);   // a second boot is a no-op
            }

            using (var db = new AppDbContext(options))
            {
                // EF's model matches the DDL: every column round-trips
                var now = DateTime.UtcNow;
                db.AiCreditOffers.Add(new AiCreditOffer
                {
                    Slug = "upgraded", Label = "Upgraded", Kind = AiCreditOfferKinds.Bundle, PayAmount = 10,
                    CreditAmount = 12, Currency = "MYR", StartsAt = now, EndsAt = now.AddDays(1), MaxRedemptions = 1,
                    EntityId = 1, CreatedAt = now, UpdatedAt = now,
                });
                db.AiCreditPayments.Add(new AiCreditPayment
                {
                    Uuid = "u1", EntityId = 1, OfferId = 1, SessionId = "cs_1", PaymentIntentId = "pi_1",
                    AmountMinor = 1000, Currency = "MYR", CreditAmount = 10, BonusAmount = 2, CreatedByUserId = 1,
                    CreatedAt = now, UpdatedAt = now, CompletedAt = now,
                });
                db.StripeEvents.Add(new StripeEvent { EventId = "evt_1", Type = "x", PaymentId = 1, ReceivedAt = now, ProcessedAt = now });
                db.SaveChanges();
                // the partial unique indexes bite
                db.AiCreditPayments.Add(new AiCreditPayment { Uuid = "u2", SessionId = "cs_1", Currency = "MYR", CreatedAt = now, UpdatedAt = now });
                Assert.Throws<DbUpdateException>(() => db.SaveChanges());
                db.ChangeTracker.Clear();
                db.StripeEvents.Add(new StripeEvent { EventId = "evt_1", Type = "x", ReceivedAt = now });
                Assert.Throws<DbUpdateException>(() => db.SaveChanges());
                db.ChangeTracker.Clear();
                // blank keys do not collide
                db.AiCreditPayments.Add(new AiCreditPayment { Uuid = "u3", Currency = "MYR", CreatedAt = now, UpdatedAt = now });
                db.AiCreditPayments.Add(new AiCreditPayment { Uuid = "u4", Currency = "MYR", CreatedAt = now, UpdatedAt = now });
                db.SaveChanges();
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(file); } catch { /* best effort */ }
        }
    }
}
