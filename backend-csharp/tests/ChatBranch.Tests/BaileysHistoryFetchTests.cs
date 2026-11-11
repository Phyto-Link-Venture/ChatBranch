using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Sidecars;
using ChatBranch.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Tests;

/// <summary>
/// A gateway that records every history-fetch call and behaves like the real one in
/// the ways that matter here: one run at a time (a second attempt is a 409-shaped
/// refusal), a run that reports progress, and a stop that ends it.
/// </summary>
public sealed class HistoryGateway : IBaileysGateway
{
    public List<string> Calls { get; } = new();
    public List<BaileysHistorySeed> LastSeeds { get; private set; } = new();
    public int Fetches { get; private set; }
    public bool Running { get; set; }
    /// <summary>Set to make the next call fail like an unreachable gateway.</summary>
    public string FailWith { get; set; } = "";

    public bool IsConfigured => true;
    public string BaseUrl => "http://gateway.test";

    public void Reset()
    {
        lock (Calls) Calls.Clear();
        LastSeeds = new List<BaileysHistorySeed>();
        Fetches = 0;
        Running = false;
        FailWith = "";
    }

    private BaileysHistoryRun Snapshot(string stop = "", bool aborted = false) => new()
    {
        Running = Running,
        Aborted = aborted,
        Depth = 200,
        Requests = Fetches,
        RequestsCap = 20,
        Received = Fetches * 50,
        Imported = Fetches * 40,
        ChatsTotal = LastSeeds.Count,
        ChatsTouched = Fetches,
        ChatsKnown = LastSeeds.Count,
        StopReason = stop,
        MinGapS = 20,
        MaxGapS = 45,
        CooldownS = 900,
        DayUsed = Fetches,
        DayLeft = 60 - Fetches,
        DayCap = 60,
        Chats = LastSeeds.Take(1).Select(s => new BaileysHistoryChat(s.WaId, 1, 50, 40, false, "")).ToList(),
    };

    public Task<BaileysHistoryRun> HistoryRunAsync(string a, CancellationToken ct = default)
    {
        lock (Calls) Calls.Add($"progress {a}");
        return Task.FromResult(FailWith.Length > 0
            ? new BaileysHistoryRun { Error = FailWith }
            : Snapshot());
    }

    public Task<BaileysHistoryRun> FetchHistoryAsync(string a, int depth, string? waId,
        IReadOnlyList<BaileysHistorySeed> seeds, CancellationToken ct = default)
    {
        lock (Calls) Calls.Add($"fetch {a} depth={depth} wa_id={waId ?? "-"} seeds={seeds.Count}");
        if (FailWith.Length > 0) return Task.FromResult(new BaileysHistoryRun { Error = FailWith });
        if (Running)
            return Task.FromResult(new BaileysHistoryRun
            {
                Error = "gateway 409: a history fetch is already running for this number", Conflict = true,
            });
        LastSeeds = seeds.ToList();
        Fetches++;
        Running = true;
        return Task.FromResult(Snapshot());
    }

    public Task<BaileysHistoryRun> StopHistoryAsync(string a, CancellationToken ct = default)
    {
        lock (Calls) Calls.Add($"stop {a}");
        Running = false;
        return Task.FromResult(Snapshot("stopped by the operator", aborted: true));
    }

    // ---- the rest of the transport: enough to be a gateway, nothing more ----
    public Task<bool> SetSettingsAsync(string a, bool keep, int days, CancellationToken ct = default) =>
        Task.FromResult(true);
    public Task<BaileysResult> SendTextAsync(string a, string to, string text, CancellationToken ct = default) =>
        Task.FromResult(new BaileysResult(true, "sent", "wamid-1"));
    public Task<BaileysResult> StartCallAsync(string a, string to, Dictionary<string, object?> d,
        CancellationToken ct = default) => Task.FromResult(new BaileysResult(false, "no calls in tests"));
    public Task<BaileysResult> EndCallAsync(string a, CancellationToken ct = default) =>
        Task.FromResult(new BaileysResult(true, "ended"));
    public Task<BaileysStatus> StatusAsync(string a, CancellationToken ct = default) =>
        Task.FromResult(new BaileysStatus("connected", null, "60111111111", null));
    public Task<BaileysStatus> StartAsync(string a, CancellationToken ct = default) =>
        Task.FromResult(new BaileysStatus("qr", "data:image/png;base64,QR", null, null));
    public Task<BaileysStatus> LogoutAsync(string a, CancellationToken ct = default) =>
        Task.FromResult(new BaileysStatus("logged_out", null, null, null));
}

public class HistoryFetchFactory : ApiFactory
{
    public HistoryGateway Gateway { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s => s.AddSingleton<IBaileysGateway>(Gateway));
    }
}

/// <summary>
/// ON-DEMAND history for a QR (Baileys) number: the operator asking WhatsApp for the
/// older messages its shallow replay on pair did not bring.
///
/// The run itself lives in the gateway, where the pacing and the caps are
/// (backend/baileys_service/history.js, self-tested offline). What is tested here is
/// the API's half: the capability gate, the anchors it digs out of our own rows, the
/// bounds on "how much", the refusals that must stay refusals (a second run, an inbox
/// that imports nothing, nothing imported to anchor on), progress, and the stop.
/// </summary>
public class BaileysHistoryFetchTests : IClassFixture<HistoryFetchFactory>
{
    private readonly HistoryFetchFactory _f;
    public BaileysHistoryFetchTests(HistoryFetchFactory f) => _f = f;

    private static async Task<JsonElement> Json(HttpResponseMessage res)
    {
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, text);
        return JsonDocument.Parse(text).RootElement;
    }

    /// <summary>The gateway's own client: the secret the sandbox default expects.</summary>
    private HttpClient AsGateway()
    {
        var c = _f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Baileys-Secret", "baileys-local-dev");
        return c;
    }

    private static long Ago(int days) => DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeSeconds();

    /// <summary>
    /// A linked QR number whose inbox already holds one chat — written the way the
    /// first import writes it, through POST /api/whatsapp/baileys/history, so the
    /// anchors are real rows and not a test's idea of them. Every test uses its own
    /// <paramref name="waId"/>: a conversation is unique per (channel, number).
    /// </summary>
    /// <summary>The timestamp of the oldest message pushed for each test number.</summary>
    private readonly Dictionary<string, long> _oldest = new();

    private async Task<(HttpClient Admin, string Id, string Key)> ANumber(string name, string waId,
        int historyDays = 30, bool withMessages = true)
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var created = await Json(await admin.PostAsJsonAsync("/api/whatsapp/accounts",
            new { provider = "baileys", name, import_history_days = historyDays }));
        var id = created.GetProperty("id").GetString()!;
        // the first start mints the gateway-side key the run is addressed by
        await Json(await admin.PostAsJsonAsync($"/api/whatsapp/accounts/{id}/baileys/start", new { }));
        var key = Platform.WithDb(_f, db => db.ChannelAccounts.Single(a => a.Id == int.Parse(id)).ExternalId);
        if (withMessages)
        {
            var oldest = Ago(10);
            _oldest[waId] = oldest;
            // deliberately newest-first in the body: the OLDEST is the anchor, whatever
            // order the rows arrived in
            var res = await AsGateway().PostAsJsonAsync("/api/whatsapp/baileys/history", new
            {
                account_id = key,
                messages = new[]
                {
                    new { wa_id = waId, name = "Ali", msg_id = $"wamid-newer-{waId}", from_me = false,
                          ts = Ago(9), text = "later" },
                    new { wa_id = waId, name = "Ali", msg_id = $"wamid-oldest-{waId}", from_me = true,
                          ts = oldest, text = "first" },
                },
            });
            res.EnsureSuccessStatusCode();
        }
        _f.Gateway.Reset();
        return (admin, id, key);
    }

    [Fact]
    public async Task A_run_asks_the_gateway_from_the_oldest_message_we_hold()
    {
        var waId = "60180900001";
        var (admin, id, key) = await ANumber("Deep phone", waId);

        var run = await Json(await admin.PostAsJsonAsync($"/api/whatsapp/accounts/{id}/baileys/history-fetch",
            new { depth = 150 }));

        // the run is checked for first (one at a time), then started with our anchors
        Assert.Equal(new[] { $"progress {key}", $"fetch {key} depth=150 wa_id=- seeds=1" }, _f.Gateway.Calls);
        var seed = Assert.Single(_f.Gateway.LastSeeds);
        Assert.Equal(waId, seed.WaId);
        Assert.Equal($"wamid-oldest-{waId}", seed.MsgId);   // the oldest, not the newest
        Assert.True(seed.FromMe);                           // and whose message it was, which the key needs
        Assert.Equal(_oldest[waId], seed.Seconds);          // WhatsApp's own timestamp, in seconds
        Assert.True(run.GetProperty("running").GetBoolean());
        Assert.Equal(20, run.GetProperty("requests_cap").GetInt32());
        Assert.Equal(45, run.GetProperty("max_gap_s").GetInt32());
    }

    [Fact]
    public async Task One_chat_can_be_deepened_on_its_own()
    {
        var (admin, id, key) = await ANumber("One chat", "60777000111");

        await Json(await admin.PostAsJsonAsync($"/api/whatsapp/accounts/{id}/baileys/history-fetch",
            new { wa_id = "+60 777 000 111" }));

        // however the operator typed it, the gateway is handed the key the thread uses
        Assert.Contains($"fetch {key} depth=200 wa_id=60777000111 seeds=1", _f.Gateway.Calls);
        Assert.Equal("60777000111", Assert.Single(_f.Gateway.LastSeeds).WaId);
    }

    [Fact]
    public async Task It_cannot_be_started_twice_for_one_inbox()
    {
        var (admin, id, _) = await ANumber("Busy phone", "60180900002");
        await Json(await admin.PostAsJsonAsync($"/api/whatsapp/accounts/{id}/baileys/history-fetch", new { }));
        Assert.Equal(1, _f.Gateway.Fetches);

        var res = await admin.PostAsJsonAsync($"/api/whatsapp/accounts/{id}/baileys/history-fetch", new { });

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Contains("already running", await res.Content.ReadAsStringAsync());
        // refused HERE: the gateway was never asked to start a second loop
        Assert.Equal(1, _f.Gateway.Fetches);
    }

    [Fact]
    public async Task Progress_reports_what_was_asked_and_what_landed()
    {
        var (admin, id, key) = await ANumber("Watched phone", "60180900003");
        await Json(await admin.PostAsJsonAsync($"/api/whatsapp/accounts/{id}/baileys/history-fetch", new { }));

        var run = await Json(await admin.GetAsync($"/api/whatsapp/accounts/{id}/baileys/history-fetch"));

        Assert.True(run.GetProperty("running").GetBoolean());
        Assert.Equal(1, run.GetProperty("requests").GetInt32());
        Assert.Equal(50, run.GetProperty("received").GetInt32());    // what WhatsApp served
        Assert.Equal(40, run.GetProperty("imported").GetInt32());    // what the inbox took
        Assert.Equal(900, run.GetProperty("cooldown_s").GetInt32());
        // the rolling day is surfaced too: the bound that holds across runs
        Assert.Equal(59, run.GetProperty("day_left").GetInt32());
        Assert.Equal(60, run.GetProperty("day_cap").GetInt32());
        var chat = Assert.Single(run.GetProperty("chats").EnumerateArray());
        Assert.Equal("60180900003", chat.GetProperty("wa_id").GetString());
        Assert.Contains($"progress {key}", _f.Gateway.Calls);
    }

    [Fact]
    public async Task An_operator_can_stop_a_run()
    {
        var (admin, id, key) = await ANumber("Stoppable", "60180900004");
        await Json(await admin.PostAsJsonAsync($"/api/whatsapp/accounts/{id}/baileys/history-fetch", new { }));

        var run = await Json(await admin.PostAsync($"/api/whatsapp/accounts/{id}/baileys/history-fetch/stop", null));

        Assert.False(run.GetProperty("running").GetBoolean());
        Assert.True(run.GetProperty("aborted").GetBoolean());
        Assert.Equal("stopped by the operator", run.GetProperty("stop_reason").GetString());
        Assert.Contains($"stop {key}", _f.Gateway.Calls);
        // and a new run may start again afterwards
        var again = await admin.PostAsJsonAsync($"/api/whatsapp/accounts/{id}/baileys/history-fetch", new { });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [Fact]
    public async Task A_depth_beyond_what_one_run_may_ask_is_refused()
    {
        var (admin, id, _) = await ANumber("Greedy", "60180900005");

        var res = await admin.PostAsJsonAsync($"/api/whatsapp/accounts/{id}/baileys/history-fetch",
            new { depth = 5000 });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("300", await res.Content.ReadAsStringAsync());
        Assert.Empty(_f.Gateway.Calls);   // nothing was asked of WhatsApp
    }

    [Fact]
    public async Task An_inbox_that_imports_no_history_is_refused()
    {
        var (admin, id, _) = await ANumber("No history", "60180900006", historyDays: 0);

        var res = await admin.PostAsJsonAsync($"/api/whatsapp/accounts/{id}/baileys/history-fetch", new { });

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Contains("switched off", await res.Content.ReadAsStringAsync());
        Assert.Empty(_f.Gateway.Calls);
    }

    [Fact]
    public async Task A_number_with_nothing_imported_has_no_anchor_to_ask_from()
    {
        var (admin, id, _) = await ANumber("Fresh", "60180900007", withMessages: false);

        var res = await admin.PostAsJsonAsync($"/api/whatsapp/accounts/{id}/baileys/history-fetch", new { });

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Contains("no imported WhatsApp message", await res.Content.ReadAsStringAsync());
        Assert.Empty(_f.Gateway.Calls);
    }

    [Fact]
    public async Task An_unreachable_gateway_is_a_clean_failure_not_a_500()
    {
        var (admin, id, _) = await ANumber("Gatewayless", "60180900008");
        _f.Gateway.FailWith = "Baileys gateway unreachable at http://gateway.test";

        var res = await admin.PostAsJsonAsync($"/api/whatsapp/accounts/{id}/baileys/history-fetch", new { });

        Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);
        Assert.Contains("unreachable", await res.Content.ReadAsStringAsync());
        _f.Gateway.FailWith = "";
    }

    [Fact]
    public async Task Only_a_baileys_number_has_older_history_to_fetch()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var cloud = await Json(await admin.PostAsJsonAsync("/api/whatsapp/accounts",
            new { provider = "cloud", name = "Cloud number", phone_number_id = "123", access_token = "t" }));
        var id = cloud.GetProperty("id").GetString()!;

        var res = await admin.PostAsJsonAsync($"/api/whatsapp/accounts/{id}/baileys/history-fetch", new { });

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task An_agent_may_not_ask_whatsapp_for_history()
    {
        var (_, id, _) = await ANumber("Agents keep out", "60180900009");
        var agent = await Platform.As(_f, "agent", "agent123");

        var start = await agent.PostAsJsonAsync($"/api/whatsapp/accounts/{id}/baileys/history-fetch", new { });
        var stop = await agent.PostAsync($"/api/whatsapp/accounts/{id}/baileys/history-fetch/stop", null);

        Assert.Equal(HttpStatusCode.Forbidden, start.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, stop.StatusCode);
        Assert.Empty(_f.Gateway.Calls);
    }

    [Fact]
    public async Task The_run_is_written_to_the_activity_log()
    {
        var (admin, id, _) = await ANumber("Logged", "60180900010");
        await Json(await admin.PostAsJsonAsync($"/api/whatsapp/accounts/{id}/baileys/history-fetch",
            new { depth = 120 }));
        await admin.PostAsync($"/api/whatsapp/accounts/{id}/baileys/history-fetch/stop", null);

        var rows = Platform.WithDb(_f, db => db.AuditLogs
            .Where(a => a.Target == $"whatsapp:{id}")
            .Select(a => a.Action + " | " + a.Detail).ToList());
        Assert.Contains(rows, r => r.StartsWith("account.baileys_history_fetch") && r.Contains("depth 120"));
        Assert.Contains(rows, r => r.StartsWith("account.baileys_history_stop"));
    }

    // ---- the seed/bounds logic on its own (no HTTP) --------------------------

    [Theory]
    [InlineData(null, 200, true)]
    [InlineData(150, 150, true)]
    [InlineData(300, 300, true)]
    [InlineData(301, 200, false)]
    [InlineData(0, 200, false)]
    [InlineData(-5, 200, false)]
    public void The_depth_asked_for_is_bounded(int? asked, int expected, bool ok)
    {
        Assert.Equal(ok, WhatsAppHistoryFetch.TryDepth(asked, out var depth, out var error));
        Assert.Equal(expected, depth);
        Assert.Equal(ok, error.Length == 0);
    }

    [Fact]
    public async Task An_older_message_another_number_carried_is_not_the_anchor()
    {
        var waId = "60180900013";
        var (admin, id, _) = await ANumber("Switched to", waId);
        var (_, _, otherKey) = await ANumber("Switched from", "60180900014");
        // the same customer, but a message carried by the OTHER number — and older than ours.
        // Its id is a message THIS phone never had, so it must never become the anchor.
        var res = await AsGateway().PostAsJsonAsync("/api/whatsapp/baileys/history", new
        {
            account_id = otherKey,
            messages = new[]
            {
                new { wa_id = waId, name = "Ali", msg_id = "wamid-other-number", from_me = false,
                      ts = Ago(20), text = "sent through the other number" },
            },
        });
        res.EnsureSuccessStatusCode();
        _f.Gateway.Reset();

        await Json(await admin.PostAsJsonAsync($"/api/whatsapp/accounts/{id}/baileys/history-fetch", new { }));

        var seed = _f.Gateway.LastSeeds.Single(x => x.WaId == waId);
        Assert.Equal($"wamid-oldest-{waId}", seed.MsgId);
        Assert.DoesNotContain(_f.Gateway.LastSeeds, x => x.MsgId == "wamid-other-number");
    }

    [Fact]
    public async Task A_group_thread_is_never_asked_about()
    {
        var (admin, id, key) = await ANumber("With a group", "60180900011");
        // a group conversation of the same number, written the way a group message writes it
        var jid = "60180900011-1700000000@g.us";
        var inbound = await AsGateway().PostAsJsonAsync("/api/whatsapp/baileys/inbound", new
        {
            account_id = key, wa_id = jid, name = "Team", text = "hi all", msg_id = "wamid-group",
            group = new { jid, subject = "Team" },
            sender = new { id = "60180900012@s.whatsapp.net", name = "Siti", phone = "60180900012" },
            mentioned = false,
        });
        inbound.EnsureSuccessStatusCode();
        // it belongs to THIS number, so only the "no group history" rule can skip it
        Assert.Equal(int.Parse(id), Platform.WithDb(_f, db =>
            db.Threads.Single(t => t.Key == jid).AccountId) ?? 0);
        Assert.True(Platform.WithDb(_f, db => db.Messages.Any(m => m.ExternalId == "wamid-group")),
            "the group message was stored, so only the group rule can keep it out of the seeds");

        await Json(await admin.PostAsJsonAsync($"/api/whatsapp/accounts/{id}/baileys/history-fetch", new { }));

        // group history carries no authors and is never imported — so it is never asked for
        Assert.DoesNotContain(_f.Gateway.LastSeeds, x => x.WaId.Contains("g.us") || x.MsgId == "wamid-group");
        Assert.Single(_f.Gateway.LastSeeds);
    }
}
