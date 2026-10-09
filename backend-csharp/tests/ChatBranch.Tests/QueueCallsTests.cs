using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Flows;
using ChatBranch.Data.Sidecars;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>A Baileys gateway that records calls and reports a scripted line state.</summary>
internal sealed class FakeBaileys : IBaileysGateway
{
    public string? CallStatus { get; set; } = "idle";
    public List<(string Account, string To, Dictionary<string, object?> Dispatch)> Calls { get; } = new();
    public string? FailStartWith { get; set; }
    public bool IsConfigured => true;
    public string BaseUrl => "http://gateway.test";

    public Task<BaileysResult> SendTextAsync(string a, string to, string text, CancellationToken ct = default) =>
        Task.FromResult(new BaileysResult(true, "sent", "msg-1"));

    public Task<BaileysResult> StartCallAsync(string a, string to, Dictionary<string, object?> dispatch,
        CancellationToken ct = default)
    {
        if (FailStartWith != null) return Task.FromResult(new BaileysResult(false, FailStartWith));
        Calls.Add((a, to, dispatch));
        CallStatus = "connecting";
        return Task.FromResult(new BaileysResult(true, "dialled", $"call-{Calls.Count}"));
    }

    public Task<BaileysResult> EndCallAsync(string a, CancellationToken ct = default) =>
        Task.FromResult(new BaileysResult(true, "ended"));

    public Task<string?> CallStatusAsync(string a, CancellationToken ct = default) => Task.FromResult(CallStatus);

    private static readonly BaileysStatus Connected = new("connected", null, "me", null);
    public Task<BaileysStatus> StatusAsync(string a, CancellationToken ct = default) => Task.FromResult(Connected);
    public Task<BaileysStatus> StartAsync(string a, CancellationToken ct = default) => Task.FromResult(Connected);
    public Task<BaileysStatus> LogoutAsync(string a, CancellationToken ct = default) => Task.FromResult(Connected);
}

/// <summary>Records LiveKit Twirp requests and answers 200 {}.</summary>
internal sealed class TwirpStub : HttpMessageHandler
{
    public List<(string Url, JsonElement Body)> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
        Requests.Add((request.RequestUri!.ToString(), body));
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
    }
}

internal sealed class TwirpFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

/// <summary>
/// Queued calls (flow "AI voice call" / "Script call" steps): SIP through the
/// LiveKit control plane, WhatsApp through the Baileys gateway, one call per
/// number (a busy line WAITS), the breather + daily cap, compliance refusals,
/// and the unconfigured path — which fails the job clearly and un-parks the run.
/// </summary>
public class QueueCallsTests : IDisposable
{
    private readonly TestDb _t = new();
    private readonly ManualClock _clock = new(DateTimeOffset.UtcNow);
    private readonly TwirpStub _twirp = new();
    private readonly FakeBaileys _baileys = new();
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"cb-calls-{Guid.NewGuid():N}");
    private LiveKitSettings _lk = new("wss://lk.test", "APIkey", "secret-0123456789", "chatbranch-agent", "ST_trunk");

    private QueueService Queue()
    {
        var sc = new ScopingService(_t.Db);
        var threads = new ThreadStore(_t.Db, sc);
        var sender = new OutboundSender(_t.Db, threads, new StubWhatsApp(), _baileys);
        var knowledge = new KnowledgeService(_t.Db, sc);
        var assistant = new AssistantService(_t.Db, sc, new PersonaService(sc), knowledge, new SandboxLlmClient());
        var livekit = new LiveKitClient(new TwirpFactory(_twirp), () => _lk);
        var calls = new QueueCallPlacer(_t.Db, sc, threads, livekit, _baileys, knowledge, assistant,
            new VoiceAgentBridge(_dataDir), _clock);
        return new QueueService(_t.Db, sc, threads, assistant, sender, clock: _clock, calls: calls);
    }

    private Contact Contact(string phone, bool doNotCall = false)
    {
        var c = new Contact { Name = "Callee", Phone = phone, DoNotCall = doNotCall,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _t.Db.Contacts.Add(c);
        _t.Db.SaveChanges();
        return c;
    }

    private FlowRun ParkedRun(int contactId)
    {
        var wf = new Workflow { FlowId = $"wf-{Guid.NewGuid():N}", Name = "Call flow", Active = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _t.Db.Workflows.Add(wf);
        _t.Db.SaveChanges();
        var run = new FlowRun { WorkflowId = wf.Id, ContactId = contactId, Status = FlowRunStatus.Waiting,
            WaitFor = "reply", CurrentNode = "n2", StartedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _t.Db.FlowRuns.Add(run);
        _t.Db.SaveChanges();
        return run;
    }

    private ChannelAccount BaileysAccount()
    {
        var a = new ChannelAccount { Channel = "whatsapp", Provider = Providers.Baileys, Name = "Sales phone",
            ExternalId = "acct-1", HealthStatus = Health.Healthy, CreatedAt = DateTime.UtcNow };
        _t.Db.ChannelAccounts.Add(a);
        _t.Db.SaveChanges();
        return a;
    }

    /// <summary>A WhatsApp conversation the contact started (so the cold-call guard allows a call).</summary>
    private void Conversation(string waId, int contactId, int accountId, bool optedOut = false)
    {
        var th = new ChatBranch.Domain.Thread { Channel = "whatsapp", Key = waId, Address = waId, Name = "Callee",
            ContactId = contactId, AccountId = accountId, OptedOut = optedOut,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _t.Db.Threads.Add(th);
        _t.Db.SaveChanges();
        _t.Db.Messages.Add(new Message { ThreadId = th.Id, Direction = "in", Source = "customer", Text = "hi",
            CreatedAt = DateTime.UtcNow });
        _t.Db.SaveChanges();
    }

    private OutboundJob Job(int id) => _t.Fresh().OutboundJobs.Single(j => j.Id == id);

    [Fact]
    public void Unconfigured_livekit_fails_the_job_with_a_reason_and_unparks_the_run()
    {
        _lk = new LiveKitSettings("", "", "", "chatbranch-agent", "");
        var q = Queue();
        var c = Contact("+60123000001");
        var run = ParkedRun(c.Id);
        var job = q.Enqueue("voice", JobKind.Call, c.Phone,
            new() { ["transport"] = "sip", ["script"] = "Hello" }, c.Id, flowRunId: run.Id, flowNode: "n1");

        q.Drain();

        var failed = Job(job.Id);
        Assert.Equal(JobStatus.Failed, failed.Status);
        Assert.Contains("AI calling is not configured", failed.LastError);
        Assert.Empty(_twirp.Requests);
        var after = _t.Fresh().FlowRuns.Single(r => r.Id == run.Id);
        Assert.Equal(FlowRunStatus.Failed, after.Status);
        Assert.Equal("", after.WaitFor);
        Assert.Contains("call not placed", JsonSerializer.Serialize(after.Log));
    }

    [Fact]
    public void Sip_call_dials_through_livekit_and_paces_the_next_one()
    {
        var q = Queue();
        new ScopingService(_t.Db).SetSetting("queue_call_gap_s", "60");
        var c = Contact("+60123000002");
        var first = q.Enqueue("voice", JobKind.Call, c.Phone,
            new() { ["transport"] = "sip", ["script"] = "Your parcel arrives tomorrow." }, c.Id);
        var second = q.Enqueue("voice", JobKind.Call, c.Phone, new() { ["transport"] = "sip" }, c.Id);

        Assert.Equal(1, q.Drain());

        var placed = Job(first.Id);
        Assert.Equal(JobStatus.Sent, placed.Status);
        Assert.StartsWith("call-", AiGate.Str(placed.Payload, "room", ""));
        Assert.Contains(_twirp.Requests, r => r.Url.EndsWith("AgentDispatchService/CreateDispatch")
            && r.Body.GetProperty("metadata").GetString()!.Contains("Your parcel arrives tomorrow."));
        Assert.Contains(_twirp.Requests, r => r.Url.EndsWith("SIPService/CreateSIPParticipant")
            && r.Body.GetProperty("sipCallTo").GetString() == "+60123000002");
        Assert.Equal(JobStatus.Queued, Job(second.Id).Status);   // breather + one SIP dial per pass

        _clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(1, q.Drain());
        Assert.Equal(JobStatus.Sent, Job(second.Id).Status);
    }

    [Fact]
    public void A_busy_WhatsApp_number_waits_instead_of_failing()
    {
        var q = Queue();
        var acct = BaileysAccount();
        var c = Contact("+60123000003");
        Conversation("60123000003", c.Id, acct.Id);
        var job = q.Enqueue("whatsapp", JobKind.Call, c.Phone,
            new() { ["transport"] = "baileys", ["script"] = "" }, c.Id);

        _baileys.CallStatus = "in_call";   // the gateway says the line is taken
        Assert.Equal(0, q.Drain());
        var waiting = Job(job.Id);
        Assert.Equal(JobStatus.Queued, waiting.Status);
        Assert.Equal(0, waiting.Attempts);
        Assert.Empty(_baileys.Calls);

        _baileys.CallStatus = "idle";
        Assert.Equal(1, q.Drain());
        Assert.Equal(JobStatus.Sent, Job(job.Id).Status);
        var call = Assert.Single(_baileys.Calls);
        Assert.Equal("acct-1", call.Account);
        Assert.Equal("60123000003", call.To);
        Assert.False(string.IsNullOrEmpty(call.Dispatch["token"] as string));
        var thread = _t.Fresh().Threads.Single(t => t.Key == "60123000003");
        Assert.Equal("connecting", thread.CallStatus);
        Assert.Equal(acct.Id, Job(job.Id).AccountId);

        // the gateway refusing a second dial is also a wait, not a failure
        var again = q.Enqueue("whatsapp", JobKind.Call, c.Phone, new() { ["transport"] = "baileys" }, c.Id);
        _clock.Advance(TimeSpan.FromMinutes(5));
        _baileys.CallStatus = "idle";
        _baileys.FailStartWith = "gateway 502: account acct-1 already has a live call (+60123000003)";
        q.Drain();
        Assert.Equal(JobStatus.Queued, Job(again.Id).Status);
    }

    [Fact]
    public void Compliance_refusals_fail_the_call_for_good()
    {
        var q = Queue();
        var acct = BaileysAccount();
        var dnc = Contact("+60123000011", doNotCall: true);
        var stop = Contact("+60123000012");
        Conversation("60123000012", stop.Id, acct.Id, optedOut: true);
        var cold = Contact("+60123000013");   // never wrote to us
        var a = q.Enqueue("whatsapp", JobKind.Call, dnc.Phone, new() { ["transport"] = "baileys" }, dnc.Id);
        var b = q.Enqueue("whatsapp", JobKind.Call, stop.Phone, new() { ["transport"] = "baileys" }, stop.Id);
        var d = q.Enqueue("whatsapp", JobKind.Call, cold.Phone, new() { ["transport"] = "baileys" }, cold.Id);

        q.Drain(10);

        Assert.Contains("do-not-call", Job(a.Id).LastError);
        Assert.Contains("opted out", Job(b.Id).LastError);
        Assert.Contains("Cold-call guard", Job(d.Id).LastError);
        Assert.All(new[] { a.Id, b.Id, d.Id }, id => Assert.Equal(JobStatus.Failed, Job(id).Status));
        Assert.Empty(_baileys.Calls);
    }

    public void Dispose()
    {
        _t.Dispose();
        try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, true); } catch { }
    }
}
