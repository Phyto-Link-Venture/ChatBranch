using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using ChatBranch.Api.Tools;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Ai.Skills;
using ChatBranch.Data.Bookings;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Flows;
using ChatBranch.Data.Stores;
using ChatBranch.Data.Tags;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Api.TestLab;

/// <summary>
/// What one scenario run gets: the fenced fixture, a unique tag for its keys,
/// the real services (one DI scope, one DbContext shared with the endpoints it
/// calls in-process), the lab's deterministic brain, and helpers that drive
/// the SAME seams production uses — an inbound message goes through
/// <see cref="InboundProcessor.ProcessInbound"/> exactly like a webhook; an
/// operator action is the dashboard's own endpoint run in-process as that
/// user (<see cref="InternalApi"/>, so its authorization, capability and scope
/// guards apply); time moves through the manual seams (queue drain, flow tick,
/// AI-monitor tick), never by sleeping. Checks are recorded, not thrown: a
/// scenario reports everything it saw.
/// </summary>
public sealed class LabContext
{
    private readonly HttpContext _outer;
    private readonly List<Action> _cleanup = new();
    private HashSet<string>? _tails;
    private InboundProcessor? _inbound;
    private AiGate? _gate;
    private AssistantService? _assistant;

    public LabContext(IServiceProvider services, HttpContext outer, ClaimsPrincipal super, LabState lab)
    {
        Services = services;
        _outer = outer;
        Super = super;
        Lab = lab;
        Db = services.GetRequiredService<AppDbContext>();
        Sc = services.GetRequiredService<ScopingService>();
        Threads = services.GetRequiredService<ThreadStore>();
        Tag = Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();
        var configured = services.GetService<ILlmClient>();
        RealModel = configured is { IsConfigured: true } ? configured : null;
    }

    public IServiceProvider Services { get; }
    public AppDbContext Db { get; }
    public ScopingService Sc { get; }
    public ThreadStore Threads { get; }
    public LabState Lab { get; }
    /// <summary>Unique per scenario run — part of every key, name and email it makes.</summary>
    public string Tag { get; }
    public ClaimsPrincipal Super { get; }
    public LabBrain Brain { get; } = new();
    /// <summary>The organization's configured language model, or null (sandbox).</summary>
    public ILlmClient? RealModel { get; }
    public bool HasRealModel => RealModel != null;
    /// <summary>Set by a needs-AI-key scenario before its first message to use the real model.</summary>
    public bool UseRealModel { get; set; }
    public bool AiUsed { get; private set; }

    public List<LabCheck> Checks { get; } = new();
    public List<LabLink> Links { get; } = new();

    // ---- checks + links ------------------------------------------------------------

    /// <summary>Record a check (never throws). Returns <paramref name="ok"/>.</summary>
    public bool Check(string name, bool ok, string detail = "")
    {
        Checks.Add(new LabCheck(name, ok, detail));
        return ok;
    }

    /// <summary>Stop here: the environment can't run this scenario (reported as skipped, not failed).</summary>
    public void Skip(string reason) => throw new LabSkipException(reason);

    public void Link(string label, string url)
    {
        if (!Links.Any(l => l.Url == url)) Links.Add(new LabLink(label, url));
    }

    public void LinkChat(string channel, string key, string label = "Open conversation") =>
        Link(label, ChatBranch.Data.Stores.ThreadCompanies.ChatHref(channel, key, ThreadOf(channel, key)?.Id));

    public void LinkContact(int id, string label = "Open contact") => Link(label, $"/contacts/{id}");

    /// <summary>Run after the scenario, pass or fail (restore a setting, pause a flow…).</summary>
    public void Defer(Action undo) => _cleanup.Add(undo);

    internal void RunCleanup()
    {
        for (var i = _cleanup.Count - 1; i >= 0; i--)
            try { _cleanup[i](); } catch { /* best effort — the next Ensure re-applies the fixture */ }
        _cleanup.Clear();
        try { LabSafety.NeutraliseLookups(Db); } catch { }
    }

    // ---- the AI pipeline (real code, the lab's brain) ---------------------------------

    private ILlmClient Model => UseRealModel && RealModel != null ? RealModel : Brain;

    /// <summary>The assistant behind lab replies — the real prompt, skills, knowledge, checks.</summary>
    public AssistantService Assistant => _assistant ??= Build().Assistant;

    /// <summary>The AI gate for lab replies (review policy, routing, send-time guards, bookings).</summary>
    public AiGate Gate => _gate ??= Build().Gate;

    /// <summary>The webhook path, wired to the lab's brain: store → link → opt-out → flows →
    /// teams → automations → notifications → unknown customers → the AI step.</summary>
    public InboundProcessor Inbound => _inbound ??= Build().Inbound;

    private (AssistantService Assistant, AiGate Gate, InboundProcessor Inbound) Build()
    {
        AiUsed = true;
        var clock = Services.GetRequiredService<TimeProvider>();
        var sender = Services.GetRequiredService<OutboundSender>();
        var bookings = Services.GetRequiredService<BookingService>();
        var typing = Services.GetRequiredService<ITypingIndicator>();
        var skills = new SkillService(Db, Sc, clock, bookings);
        var assistant = new AssistantService(Db, Sc, new PersonaService(Sc),
            Services.GetRequiredService<KnowledgeService>(), Model, skills);
        var gate = new AiGate(Db, Sc, Threads, sender, clock, typing, bookings);
        var responder = new AiResponder(Db, Sc, new AiComposer(Db, assistant), gate, clock);
        var queue = new QueueService(Db, Sc, Threads, assistant, sender, responder, gate, clock,
            Services.GetRequiredService<QueueCallPlacer>());
        var flows = new FlowEngine(Db, Sc, queue,
            Services.GetService<ChatBranch.Data.Integrations.IntegrationActions>(), assistant);
        var inbound = new InboundProcessor(Db, Threads, assistant, sender, flows, queue, Sc,
            new AutomationService(Db, Sc), responder);
        _assistant = assistant;
        _gate = gate;
        _inbound = inbound;
        return (assistant, gate, inbound);
    }

    public string BrainName => !AiUsed ? "" : UseRealModel && RealModel != null ? "configured" : "lab";

    // ---- people ------------------------------------------------------------------------

    /// <summary>A signed-in principal for a lab user, shaped like the bearer-token handler's.</summary>
    public ClaimsPrincipal As(AppUser user)
    {
        var profile = Db.Profiles.AsNoTracking().First(p => p.UserId == user.Id);
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, profile.Role),
            new("profile_id", profile.Id.ToString()),
        };
        if (profile.TenantId is int tid) claims.Add(new Claim("tenant_id", tid.ToString()));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    /// <summary>The dashboard's own endpoint, in-process, as <paramref name="who"/> (every guard applies).</summary>
    public Task<InternalApi.Response> Api(ClaimsPrincipal who, string method, string routeTemplate,
        object? body = null, Dictionary<string, string>? route = null, Dictionary<string, string?>? query = null)
    {
        var outer = new DefaultHttpContext { RequestServices = Services, User = who };
        outer.Request.Scheme = _outer.Request.Scheme;
        outer.Request.Host = _outer.Request.Host.HasValue ? _outer.Request.Host : new HostString("localhost");
        outer.Request.Headers.UserAgent = "ChatBranch-TestLab";
        outer.Connection.RemoteIpAddress = _outer.Connection.RemoteIpAddress;
        outer.TraceIdentifier = _outer.TraceIdentifier;
        return InternalApi.InvokeAsync(outer, who, method, routeTemplate, route, body, query);
    }

    // ---- data -----------------------------------------------------------------------------

    /// <summary>A phone number (digits) whose last 8 digits match NO contact and no chat — a lab
    /// conversation can never link to one of your real customers.</summary>
    public string NewPhone()
    {
        _tails ??= Db.Contacts.AsNoTracking().Where(c => c.Phone != "").Select(c => c.Phone).AsEnumerable()
            .Concat(Db.Threads.AsNoTracking().Where(t => t.Channel == ChatBranch.Domain.Channels.WhatsApp)
                .Select(t => t.Key).AsEnumerable())
            .Select(PhoneMatch.Tail).Where(t => t != null).Select(t => t!).ToHashSet();
        for (var i = 0; i < 50; i++)
        {
            var digits = "601" + RandomNumberGenerator.GetInt32(10_000_000, 99_999_999).ToString();
            if (PhoneMatch.Tail(digits) is string tail && _tails.Add(tail)) return digits;
        }
        throw new InvalidOperationException("Could not find an unused phone number.");
    }

    /// <summary>A Telegram chat id no conversation uses yet.</summary>
    public string NewChatId() => "8" + RandomNumberGenerator.GetInt32(100_000_000, 999_999_999);

    public string NewEmail(string who) =>
        $"{new string(who.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray())}.{Tag}@{LabFixture.EmailDomain}";

    /// <summary>A lab contact: in a lab entity, tagged <c>test-lab</c>, phone-unique.</summary>
    public Contact NewContact(string name, int? entityId = null, string? phone = null, string? email = null,
        string aiMode = "", string language = "en", Dictionary<string, object?>? attributes = null)
    {
        var now = DateTime.UtcNow;
        var c = new Contact
        {
            Name = name, Phone = phone ?? "+" + NewPhone(), Email = email ?? "", Language = language,
            Status = "active", TenantId = Lab.TenantId, AiMode = aiMode, Method = "testlab",
            Attributes = attributes ?? new(), CreatedAt = now, UpdatedAt = now,
        };
        Db.Contacts.Add(c);
        Db.SaveChanges();
        Db.ContactEntities.Add(new ContactEntity { ContactId = c.Id, EntityId = entityId ?? Lab.Hq.Id, CreatedAt = now });
        Db.SaveChanges();
        TagService.Assign(Db, Db.Tags.Find(Lab.LabTag.Id)!, TagService.ContactTarget, c.Id, new(), "testlab");
        return c;
    }

    /// <summary>The digits of a contact's phone — its WhatsApp chat key.</summary>
    public static string WaKey(Contact c) => PhoneMatch.Digits(c.Phone);

    /// <summary>A customer message arriving on a lab inbox, through the real webhook path.</summary>
    public Message Say(ChannelAccount inbox, string key, string text, string? name = null) =>
        Inbound.ProcessInbound(inbox.Channel, key, text, name, inbox.Id, wamid: $"lab-{Guid.NewGuid():N}");

    /// <summary>A lab inbox of its own for one scenario (get-or-create by name, reset to a sandbox).</summary>
    public ChannelAccount Inbox(string name, Dictionary<string, object?> overrides, string channel = "whatsapp",
        string provider = Providers.Baileys, string role = AccountRoles.Conversation, int? entityId = null,
        int? teamId = null)
    {
        var cfg = LabFixture.BaseConfig(teamId ?? Lab.FrontDesk.Id,
            channel == ChatBranch.Domain.Channels.WhatsApp ? role : "");
        foreach (var (k, v) in overrides) cfg[k] = v;
        return LabFixture.EnsureAccount(Db, LabFixture.AccountPrefix + name, channel, provider,
            entityId ?? Lab.Hq.Id, cfg);
    }

    /// <summary>Set an entity-scoped Setting on a lab entity for this scenario only.</summary>
    public void SetEntitySetting(int entityId, string key, string value)
    {
        var before = Db.Settings.AsNoTracking().FirstOrDefault(s => s.Key == key && s.EntityId == entityId)?.Value;
        Sc.SetSetting(key, value, entityId);
        Defer(() =>
        {
            if (before == null) Sc.ClearSetting(key, entityId);
            else Sc.SetSetting(key, before, entityId);
        });
    }

    // ---- fresh reads (straight from the database) ------------------------------------------

    /// <summary>The LAB's conversation with (channel, key) — a customer has one per company, so the
    /// lab company's (or an unattributed one) is preferred over anybody else's.</summary>
    public Thread? ThreadOf(string channel, string key) =>
        Db.Threads.AsNoTracking().Include(t => t.Contact).Include(t => t.Account)
            .Where(t => t.Channel == channel && t.Key == key).ToList()
            .OrderByDescending(t => t.CompanyEntityId == Lab.Hq.Id)
            .ThenByDescending(t => t.CompanyEntityId == null)
            .ThenByDescending(t => t.Id)
            .FirstOrDefault();

    public List<Message> MessagesOf(int threadId) =>
        Db.Messages.AsNoTracking().Where(m => m.ThreadId == threadId).OrderBy(m => m.Id).ToList();

    public List<Message> Replies(int threadId, string source = MessageSource.Ai) =>
        MessagesOf(threadId).Where(m => m.Direction == MessageDirection.Out && !m.Private && m.Source == source).ToList();

    public List<AiDraft> DraftsOf(int threadId) =>
        Db.AiDrafts.AsNoTracking().Where(d => d.ThreadId == threadId).OrderBy(d => d.Id).ToList();

    public Contact? ContactOf(int id) => Db.Contacts.AsNoTracking().FirstOrDefault(c => c.Id == id);

    public AiVerdict Verdict(Thread t) => AiEligibility.Decide(Db, Sc,
        Db.Threads.AsNoTracking().Include(x => x.Account).Include(x => x.Contact).First(x => x.Id == t.Id));

    // ---- time seams ---------------------------------------------------------------------------

    /// <summary>Drain the outbound queue (the worker's / POST /api/flows/tick's pass) until
    /// <paramref name="done"/> holds or nothing moves. Returns how many jobs went out.</summary>
    public int Drain(Func<bool>? done = null, int passes = 8)
    {
        var queue = Services.GetRequiredService<QueueService>();
        var sent = 0;
        for (var i = 0; i < passes; i++)
        {
            if (done?.Invoke() == true) break;
            var n = queue.Drain(25);
            sent += n;
            if (n == 0 && done == null) break;
        }
        return sent;
    }

    /// <summary>One flow-engine pass (the worker's / POST /api/flows/tick's).</summary>
    public void FlowTick() => Services.GetRequiredService<FlowEngine>().Tick();

    public OutboundJob? Job(int id) => Db.OutboundJobs.AsNoTracking().FirstOrDefault(j => j.Id == id);

    // ---- small JSON readers for API answers --------------------------------------------------

    public static string Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()
            : "";

    public static int? Int(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt32(out var n) ? n : null;
}
