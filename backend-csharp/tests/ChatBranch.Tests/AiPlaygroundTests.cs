using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Bookings;
using ChatBranch.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Tests;

/// <summary>
/// An OpenAI-compatible <c>/chat/completions</c> stand-in at the HTTP level, so
/// the REAL <see cref="OpenAiLlmClient"/> (provider choice, pinning, the wire
/// body) is what runs. Records every request; <see cref="Script"/> answers.
/// </summary>
public sealed class FakeChatApi : HttpMessageHandler
{
    public sealed record Call(string Host, string Model, string System, List<JsonElement> Messages, List<string> Tools);

    public List<Call> Calls { get; } = new();
    /// <summary>The round's answer: text, or tool calls (name, JSON args). Null = a friendly default.</summary>
    public Func<Call, (string Text, (string Name, string Args)[] ToolCalls)>? Script { get; set; }

    public Call Last { get { lock (Calls) return Calls[^1]; } }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
        var msgs = body.GetProperty("messages").EnumerateArray().ToList();
        var tools = body.TryGetProperty("tools", out var t)
            ? t.EnumerateArray().Select(x => x.GetProperty("function").GetProperty("name").GetString()!).ToList()
            : new List<string>();
        var call = new Call(request.RequestUri!.Host, body.GetProperty("model").GetString() ?? "",
            msgs[0].GetProperty("content").GetString() ?? "", msgs.Skip(1).ToList(), tools);
        lock (Calls) Calls.Add(call);
        var lastUser = call.Messages.LastOrDefault(m => m.GetProperty("role").GetString() == "user");
        var (text, toolCalls) = Script?.Invoke(call)
            ?? ($"Model reply to: {(lastUser.ValueKind == JsonValueKind.Object ? lastUser.GetProperty("content").GetString() : "")}",
                Array.Empty<(string, string)>());
        var message = new Dictionary<string, object?> { ["role"] = "assistant", ["content"] = text };
        if (toolCalls.Length > 0)
            message["tool_calls"] = toolCalls.Select((c, i) => new
            {
                id = $"call_{Guid.NewGuid():N}_{i}", type = "function",
                function = new { name = c.Name, arguments = c.Args },
            }).ToList();
        var payload = new
        {
            model = call.Model,
            choices = new[] { new { message } },
            usage = new { prompt_tokens = 1000, completion_tokens = 200 },
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
    }

    // the HttpClientFactory must never dispose the one shared fake
    protected override void Dispose(bool disposing) { }
}

/// <summary>A test host whose chat-completions traffic goes to <see cref="FakeChatApi"/>.</summary>
public class PlaygroundFactory : ApiFactory
{
    public FakeChatApi Chat { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureLogging(l => l.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning));
        builder.ConfigureTestServices(s => s.AddHttpClient("llm")
            .ConfigurePrimaryHttpMessageHandler(() => Chat)
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan));
    }
}

/// <summary>
/// The AI playground: the real generator fed from a transcript, its toggles and
/// overrides, the production verdict, the calendar preview — and, above all,
/// that nothing it does reaches a customer or touches real data.
/// </summary>
public class AiPlaygroundTests : IClassFixture<PlaygroundFactory>
{
    private readonly PlaygroundFactory _f;
    private static readonly object Arranged = new();

    public AiPlaygroundTests(PlaygroundFactory f)
    {
        _f = f;
        _ = _f.CreateClient(); // boot + seed
        lock (Arranged)
            With(db =>
            {
                var sc = new ScopingService(db);
                // keyword retrieval only — a configured OpenAI key must not reach for embeddings
                sc.SetSetting("embedding_provider", "none");
            });
    }

    // ---- helpers -------------------------------------------------------------------------

    private void With(Action<AppDbContext> act) => SettingsTestKit.WithDb(_f, act);
    private T With<T>(Func<AppDbContext, T> act) => SettingsTestKit.WithDb(_f, act);
    private void Set(string key, string value) => With(db => new ScopingService(db).SetSetting(key, value));

    private void Models(params string[] providers)
    {
        foreach (var p in new[] { "openai", "openrouter", "google", "ilmu" })
            Set($"{p}_api_key", providers.Contains(p) ? $"sk-test-{p}-secret" : "");
    }

    private Task<HttpClient> Admin() => Platform.As(_f, "admin", "admin123");
    private Task<HttpClient> Agent() => Platform.As(_f, "agent", "agent123");

    private static object Customer(string text) => new { role = "customer", text };
    private static object Ai(string text) => new { role = "ai", text };

    private static object Body(object[] messages, object? options = null, object? profile = null, string? entity = null,
        int? contactId = null, string channel = "whatsapp") => new Dictionary<string, object?>
    {
        ["entity"] = entity,
        ["channel"] = channel,
        ["contact_id"] = contactId,
        ["profile"] = contactId == null ? profile ?? new { name = "Test customer", language = "en" } : null,
        ["messages"] = messages,
        ["options"] = options,
    };

    private static async Task<JsonElement> Ok(HttpResponseMessage res)
    {
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"HTTP {(int)res.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private sealed record Counts(int Threads, int Messages, int Drafts, int Bookings, int Notifications, int Audit,
        int Jobs, int Contacts, int Handovers, int LessonUses, int Usage, int PlaygroundUsage);

    private Counts Snap() => With(db => new Counts(
        db.Threads.Count(), db.Messages.Count(), db.AiDrafts.Count(), db.Bookings.Count(), db.Notifications.Count(),
        db.AuditLogs.Count(), db.OutboundJobs.Count(), db.Contacts.Count(), db.Threads.Count(t => t.HandoverAt != null),
        db.AiLessons.Sum(l => l.Uses), db.LlmUsage.Count(), db.LlmUsage.Count(u => u.Feature == "playground")));

    private static void Untouched(Counts before, Counts after)
    {
        Assert.Equal(before.Threads, after.Threads);
        Assert.Equal(before.Messages, after.Messages);
        Assert.Equal(before.Drafts, after.Drafts);
        Assert.Equal(before.Bookings, after.Bookings);
        Assert.Equal(before.Notifications, after.Notifications);
        Assert.Equal(before.Audit, after.Audit);
        Assert.Equal(before.Jobs, after.Jobs);
        Assert.Equal(before.Contacts, after.Contacts);
        Assert.Equal(before.Handovers, after.Handovers);
        Assert.Equal(before.LessonUses, after.LessonUses);
    }

    // ---- nothing persisted -----------------------------------------------------------------

    [Fact]
    public async Task A_sandbox_reply_works_with_no_model_and_nothing_is_persisted_but_usage()
    {
        Models();
        var admin = await Admin();
        var opts = await Ok(await admin.GetAsync("/api/ai/playground/options"));
        Assert.True(opts.GetProperty("sandbox").GetBoolean());
        Assert.True(opts.GetProperty("can_override").GetBoolean());

        var before = Snap();
        var r = await Ok(await admin.PostAsJsonAsync("/api/ai/playground/reply",
            Body(new[] { Customer("Hi, are you open on Sunday?") })));
        var after = Snap();

        Assert.Equal("sandbox", r.GetProperty("provider").GetString());
        Assert.True(r.GetProperty("sandbox").GetBoolean());
        Assert.Contains("sandbox reply", r.GetProperty("reply").GetString());
        Assert.Equal("send", r.GetProperty("production").GetProperty("verdict").GetString());
        Assert.Equal(1, r.GetProperty("bubbles").GetArrayLength());
        Assert.True(r.GetProperty("contact").GetProperty("made_up").GetBoolean());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("prompt").ValueKind);

        Untouched(before, after);
        Assert.Equal(before.Usage + 1, after.Usage);
        Assert.Equal(before.PlaygroundUsage + 1, after.PlaygroundUsage);
        var row = With(db => db.LlmUsage.OrderByDescending(u => u.Id).First());
        Assert.Equal("playground", row.Feature);
        Assert.Null(row.ContactId); // a made-up customer is never a stored contact
    }

    [Fact]
    public async Task Compare_is_just_two_calls_with_no_server_state()
    {
        Models("openai");
        try
        {
            var admin = await Admin();
            var transcript = new[] { Customer("What time do you close?"), Ai("We close at 6pm."), Customer("And on Saturday?") };
            var before = Snap();
            var a = await Ok(await admin.PostAsJsonAsync("/api/ai/playground/reply", Body(transcript)));
            var b = await Ok(await admin.PostAsJsonAsync("/api/ai/playground/reply",
                Body(transcript, new { persona = "You are terse.", knowledge = false })));
            var after = Snap();

            Assert.Equal("openai", a.GetProperty("provider").GetString());
            Assert.Equal("openai", b.GetProperty("provider").GetString());
            Assert.False(a.GetProperty("options").GetProperty("persona").GetBoolean());
            Assert.True(b.GetProperty("options").GetProperty("persona").GetBoolean());
            // the whole transcript went to the model each time — nothing was remembered between calls
            var last = _f.Chat.Last;
            Assert.Equal(3, last.Messages.Count);
            Assert.Equal("And on Saturday?", last.Messages[2].GetProperty("content").GetString());
            Untouched(before, after);
            Assert.Equal(before.PlaygroundUsage + 2, after.PlaygroundUsage);
            Assert.True(a.GetProperty("cost_estimate").GetProperty("priced").GetBoolean()); // gpt-4o-mini
            Assert.Equal(1000, a.GetProperty("tokens").GetProperty("prompt").GetInt32());
        }
        finally { Models(); }
    }

    // ---- the prompt: who the customer is ------------------------------------------------------

    [Fact]
    public async Task A_made_up_profile_reaches_the_contact_block()
    {
        Models("openai");
        try
        {
            var admin = await Admin();
            var r = await Ok(await admin.PostAsJsonAsync("/api/ai/playground/reply", Body(
                new[] { Customer("Ada kopi susu?") },
                new { show_prompt = true },
                new { name = "Mei Ling", language = "ms", tags = new[] { "VIP", "Regular" }, fields = new { member_tier = "Gold" } })));

            var system = r.GetProperty("prompt").GetProperty("system").GetString()!;
            Assert.Contains("CONTACT:\n- Name: Mei Ling", system);
            Assert.Contains("- Language: ms", system);
            Assert.Contains("- Tags: VIP, Regular", system);
            // the prompt shown is exactly the one the model received
            Assert.Equal(system, _f.Chat.Last.System);
            Assert.StartsWith(PersonaService.HardRules, system);
            Assert.Equal("Ada kopi susu?", r.GetProperty("prompt").GetProperty("messages")[0].GetProperty("content").GetString());
            Assert.Equal("Mei Ling", r.GetProperty("contact").GetProperty("name").GetString());
        }
        finally { Models(); }
    }

    [Fact]
    public async Task A_real_contacts_memory_reaches_contact_memory_only_when_memory_is_on()
    {
        Models("openai");
        try
        {
            var contactId = With(db =>
            {
                var tenant = db.Tenants.First(t => t.Slug == "demo");
                var c = new Contact
                {
                    Name = "Memory Mabel", Phone = "+60111222333", TenantId = tenant.Id, CommunicationStyle = "Short and direct",
                    AiProfileSummary = "Orders oat milk.", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                };
                db.Contacts.Add(c);
                db.SaveChanges();
                db.ConversationSummaries.Add(new ConversationSummary
                {
                    ContactId = c.Id, Source = "chat", Summary = "Asked about the loyalty card.", CreatedAt = DateTime.UtcNow,
                });
                db.SaveChanges();
                return c.Id;
            });
            var admin = await Admin();
            var before = Snap();
            var on = await Ok(await admin.PostAsJsonAsync("/api/ai/playground/reply",
                Body(new[] { Customer("Hello again") }, new { show_prompt = true }, contactId: contactId)));
            var off = await Ok(await admin.PostAsJsonAsync("/api/ai/playground/reply",
                Body(new[] { Customer("Hello again") }, new { show_prompt = true, memory = false }, contactId: contactId)));
            var after = Snap();

            var withMemory = on.GetProperty("prompt").GetProperty("system").GetString()!;
            Assert.Contains("- Name: Memory Mabel", withMemory);
            Assert.Contains("CONTACT MEMORY:", withMemory);
            Assert.Contains("Asked about the loyalty card.", withMemory);
            Assert.Contains("Style: Short and direct", withMemory);
            var without = off.GetProperty("prompt").GetProperty("system").GetString()!;
            Assert.Contains("- Name: Memory Mabel", without);
            Assert.DoesNotContain("CONTACT MEMORY", without);
            Assert.DoesNotContain("Orders oat milk.", without);
            Assert.False(on.GetProperty("contact").GetProperty("made_up").GetBoolean());
            Untouched(before, after);
            // a real contact's usage is metered against them, as a reply to them would be
            Assert.Equal(contactId, With(db => db.LlmUsage.OrderByDescending(u => u.Id).First().ContactId));
        }
        finally { Models(); }
    }

    // ---- toggles -------------------------------------------------------------------------------

    [Fact]
    public async Task Toggles_drop_knowledge_lessons_skills_and_the_persona_override_keeps_the_hard_rules()
    {
        Models("openai");
        var ids = With(db =>
        {
            var now = DateTime.UtcNow;
            var k = new KnowledgeItem { Kind = "fact", Title = "Opening hours", Content = "We open 8am to 6pm daily.", Active = true, CreatedAt = now, UpdatedAt = now };
            var l = new AiLesson { Kind = AiLessonKind.Rule, Status = AiLessonStatus.Active, Answer = "Always sign off as Team Kopi.", CreatedAt = now, UpdatedAt = now };
            db.KnowledgeItems.Add(k);
            db.AiLessons.Add(l);
            db.AgentSkills.AddRange(
                new AgentSkill { SkillId = "pg-tool", Name = "Loyalty points", Kind = SkillKinds.Tool, Enabled = true,
                    Code = "function run(args, ctx) { return { points: 42 }; }" },
                new AgentSkill { SkillId = "pg-check", Name = "No guarantees", Kind = SkillKinds.Check, Enabled = true,
                    Code = "function check(reply, ctx) { return { ok: true }; }" },
                new AgentSkill { SkillId = "pg-prompt", Name = "Upsell pastries", Kind = SkillKinds.Prompt, Enabled = true,
                    Instructions = "Mention today's pastries." });
            db.SaveChanges();
            return (k.Id, l.Id);
        });
        try
        {
            var admin = await Admin();
            var msgs = new[] { Customer("What are your opening hours?") };
            async Task<(JsonElement R, FakeChatApi.Call Call)> Run(object options)
            {
                var r = await Ok(await admin.PostAsJsonAsync("/api/ai/playground/reply", Body(msgs, options)));
                return (r, _f.Chat.Last);
            }

            var (all, allCall) = await Run(new { show_prompt = true });
            var system = all.GetProperty("prompt").GetProperty("system").GetString()!;
            Assert.Contains("COMPANY KNOWLEDGE", system);
            Assert.Contains("We open 8am to 6pm daily.", system);
            Assert.Contains("LEARNED FROM PAST CONVERSATIONS", system);
            Assert.Contains("SKILLS:", system);
            Assert.Contains("loyalty_points", allCall.Tools);
            Assert.Contains("current_time", allCall.Tools);
            Assert.Contains(all.GetProperty("checks").EnumerateArray(), c => c.GetProperty("name").GetString() == "No guarantees");
            Assert.Contains(all.GetProperty("grounding").EnumerateArray(), g => g.GetProperty("type").GetString() == "knowledge");
            Assert.Contains(all.GetProperty("hits").EnumerateArray(), h => h.GetProperty("used").GetBoolean());

            var (noKnowledge, _) = await Run(new { show_prompt = true, knowledge = false });
            var nk = noKnowledge.GetProperty("prompt").GetProperty("system").GetString()!;
            Assert.DoesNotContain("COMPANY KNOWLEDGE", nk);
            Assert.Contains("LEARNED FROM PAST CONVERSATIONS", nk);

            var (noLessons, _) = await Run(new { show_prompt = true, lessons = false });
            var nl = noLessons.GetProperty("prompt").GetProperty("system").GetString()!;
            Assert.DoesNotContain("LEARNED FROM PAST CONVERSATIONS", nl);
            Assert.Contains("COMPANY KNOWLEDGE", nl);

            var (noSkills, noSkillsCall) = await Run(new { show_prompt = true, skills = false });
            Assert.Empty(noSkillsCall.Tools); // no tools offered at all
            Assert.Equal(0, noSkills.GetProperty("checks").GetArrayLength()); // no checks run
            Assert.Equal(0, noSkills.GetProperty("tools_offered").GetArrayLength());
            Assert.DoesNotContain("SKILLS:", noSkills.GetProperty("prompt").GetProperty("system").GetString());

            var (persona, _) = await Run(new { show_prompt = true, persona = "You are Kopi Bot, a cheerful barista." });
            var ps = persona.GetProperty("prompt").GetProperty("system").GetString()!;
            Assert.StartsWith(PersonaService.HardRules + "\n\nYou are Kopi Bot, a cheerful barista.", ps);
            Assert.DoesNotContain(PersonaService.DefaultPersona, ps);
            // the stored persona is untouched
            Assert.True(string.IsNullOrEmpty(With(db => new ScopingService(db).SettingValue("aicore_persona"))));
            // lessons a playground prompt carried are not counted as used
            Assert.Equal(0, With(db => db.AiLessons.First(x => x.Id == ids.Item2).Uses));
        }
        finally
        {
            Models();
            With(db =>
            {
                db.AgentSkills.RemoveRange(db.AgentSkills.Where(s => s.SkillId.StartsWith("pg-")));
                db.KnowledgeItems.RemoveRange(db.KnowledgeItems.Where(k => k.Id == ids.Item1));
                db.AiLessons.RemoveRange(db.AiLessons.Where(l => l.Id == ids.Item2));
                db.SaveChanges();
            });
        }
    }

    // ---- the model -----------------------------------------------------------------------------

    [Fact]
    public async Task A_provider_pin_uses_only_that_provider_and_an_unconfigured_one_is_refused()
    {
        Models("openai", "openrouter");
        try
        {
            var admin = await Admin();
            var opts = await Ok(await admin.GetAsync("/api/ai/playground/options"));
            var providers = opts.GetProperty("providers").EnumerateArray().Select(p => p.GetProperty("id").GetString()).ToList();
            Assert.Equal(new[] { "openrouter", "openai" }, providers);
            Assert.DoesNotContain("sk-test", opts.GetRawText()); // keys never leave the server
            Assert.Equal("openrouter", opts.GetProperty("default_route").GetProperty("provider").GetString());

            var start = _f.Chat.Calls.Count;
            var auto = await Ok(await admin.PostAsJsonAsync("/api/ai/playground/reply", Body(new[] { Customer("Hi") })));
            Assert.Equal("openrouter", auto.GetProperty("provider").GetString());
            Assert.Equal("openrouter.ai", _f.Chat.Calls[start].Host);

            start = _f.Chat.Calls.Count;
            var pinned = await Ok(await admin.PostAsJsonAsync("/api/ai/playground/reply",
                Body(new[] { Customer("Hi") }, new { provider = "openai", model = "gpt-4.1-mini" })));
            Assert.Equal("openai", pinned.GetProperty("provider").GetString());
            Assert.Equal("gpt-4.1-mini", pinned.GetProperty("model").GetString());
            var calls = _f.Chat.Calls.Skip(start).ToList();
            Assert.NotEmpty(calls);
            Assert.All(calls, c => Assert.Equal("api.openai.com", c.Host));
            Assert.All(calls, c => Assert.Equal("gpt-4.1-mini", c.Model));

            var refused = await admin.PostAsJsonAsync("/api/ai/playground/reply",
                Body(new[] { Customer("Hi") }, new { provider = "google" }));
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            var nonsense = await admin.PostAsJsonAsync("/api/ai/playground/reply",
                Body(new[] { Customer("Hi") }, new { provider = "nope" }));
            Assert.Equal(HttpStatusCode.BadRequest, nonsense.StatusCode);
        }
        finally { Models(); }
    }

    [Fact]
    public void Pinned_client_speaks_to_one_provider_with_its_own_model_and_never_falls_back()
    {
        using var t = new TestDb();
        var sc = new ScopingService(t.Db);
        sc.SetSetting("openai_api_key", "sk-a");
        sc.SetSetting("openrouter_api_key", "sk-b");
        var client = new OpenAiLlmClient(new SingleClientFactory(new FakeChatApi()), sc);
        Assert.Equal(new[] { "openrouter", "openai" }, client.Configured().Select(p => p.Name));
        var pinned = client.Pinned("openai", "gpt-4o")!;
        Assert.Equal("openai", Assert.Single(pinned.Candidates()).Name);
        Assert.Equal("gpt-4o", pinned.Candidates()[0].Model);
        Assert.Null(client.Pinned("google"));
        Assert.Null(client.Pinned("ilmu", "x"));
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _h;
        public SingleClientFactory(HttpMessageHandler h) => _h = h;
        public HttpClient CreateClient(string name) => new(_h, disposeHandler: false);
    }

    // ---- what would happen in production ---------------------------------------------------------

    [Fact]
    public async Task The_production_verdict_follows_the_review_policy_and_the_ai_switch()
    {
        var admin = await Admin();
        async Task<JsonElement> Verdict() => (await Ok(await admin.PostAsJsonAsync("/api/ai/playground/reply",
            Body(new[] { Customer("Hello there") })))).GetProperty("production");
        try
        {
            Set("ai_review_mode", "review_all");
            var hold = await Verdict();
            Assert.Equal("hold", hold.GetProperty("verdict").GetString());
            Assert.Contains("Every AI reply is reviewed", hold.GetProperty("why").GetString());
            Assert.Equal("review_all", hold.GetProperty("review_mode").GetString());

            Set("ai_review_mode", "shadow");
            var shadow = await Verdict();
            Assert.Equal("hold", shadow.GetProperty("verdict").GetString());
            Assert.True(shadow.GetProperty("shadow").GetBoolean());

            Set("ai_review_mode", "auto");
            Set("ai_enabled", "off");
            var off = await Verdict();
            Assert.Equal("ai_off", off.GetProperty("verdict").GetString());
            Assert.Equal(AiRules.MasterOff, off.GetProperty("rule").GetString());
            Assert.False(off.GetProperty("ai").GetProperty("enabled").GetBoolean());

            // …and the playground still answers — it is a test
            Set("ai_enabled", "on");
            var on = await Verdict();
            Assert.Equal("send", on.GetProperty("verdict").GetString());
        }
        finally
        {
            Set("ai_review_mode", "auto");
            Set("ai_enabled", "");
        }
    }

    [Fact]
    public void The_verdict_names_handovers_and_blocks_like_the_gate()
    {
        using var t = new TestDb();
        var policy = AiBehaviourSettings.Resolve(new ScopingService(t.Db), null) with { ReviewMode = "auto" };
        var on = new AiVerdict(true, AiRules.Thread, "On", false);
        var handover = AiPlayground.Decide(policy, on, new AiReplyResult
        {
            Action = AiActions.Handover, Draft = "Let me check.", Reason = "Refund dispute",
        }, 0);
        Assert.Equal("handover", handover.Verdict);
        Assert.Equal(HandoverKinds.CheckFailed, handover.Kind);
        var blocked = AiPlayground.Decide(policy, on, new AiReplyResult
        {
            Action = AiActions.Block, Draft = "We guarantee it.", Reason = "No guarantees: promises",
        }, 0);
        Assert.Equal("blocked", blocked.Verdict);
        var person = AiPlayground.Decide(policy, on, new AiReplyResult
        {
            Action = AiActions.Send, Text = "A member of our team will get back to you shortly.",
        }, 0);
        Assert.Equal("handover", person.Verdict); // sent — and flagged for a person, like the gate does
        Assert.Equal(HandoverKinds.AskedForHuman, person.Kind);
        var empty = AiPlayground.Decide(policy, on, AiReplyResult.Failed("playground", "boom"), 0);
        Assert.Equal("blocked", empty.Verdict);
        var flagged = policy with { ReviewMode = "review_flagged", FirstN = 2 };
        var first = AiPlayground.Decide(flagged, on, new AiReplyResult { Action = AiActions.Send, Text = "Hello!" }, 0);
        Assert.Equal("hold", first.Verdict); // one of the first 2 replies of a conversation
        var third = AiPlayground.Decide(flagged, on, new AiReplyResult { Action = AiActions.Send, Text = "Hello!" }, 2);
        Assert.Equal("send", third.Verdict);
    }

    // ---- calendar --------------------------------------------------------------------------------

    [Fact]
    public async Task A_calendar_booking_is_previewed_and_nothing_is_reserved_or_booked()
    {
        Models("openai");
        var allDay = AiSchedule.Days.ToDictionary(d => d, _ => new[] { new[] { "00:00", "23:59" } });
        var cfg = CalendarAppConfig.From(JsonSerializer.SerializeToElement(new
        {
            source = "chatbranch", hours = allDay, duration_minutes = 30, min_notice_minutes = 60, max_days_ahead = 14,
        }), out var cfgError);
        Assert.Null(cfgError);
        With(db =>
        {
            db.AgentSkills.Add(new AgentSkill
            {
                SkillId = "pg-cal", Name = "Book a table", Kind = SkillKinds.App, Enabled = true,
                Instructions = "Offer times, book the one picked.", Config = cfg.ToDictionary(),
            });
            db.SaveChanges();
        });
        _f.Chat.Script = call =>
        {
            var last = call.Messages.LastOrDefault();
            if (last.ValueKind != JsonValueKind.Object || last.GetProperty("role").GetString() != "tool")
                return ("", new[] { ("calendar_availability", "{}") });
            var json = JsonDocument.Parse(last.GetProperty("content").GetString()!).RootElement;
            if (json.TryGetProperty("slots", out var slots) && slots.GetArrayLength() > 0)
                return ("", new[] { ("calendar_book", $"{{\"start\":\"{slots[0].GetProperty("start").GetString()}\"}}") });
            if (json.TryGetProperty("status", out var st) && st.GetString() == "reserved")
                return ($"You're booked for {json.GetProperty("label").GetString()}.", Array.Empty<(string, string)>());
            return ("Sorry, no times.", Array.Empty<(string, string)>());
        };
        try
        {
            var admin = await Admin();
            var before = Snap();
            var r = await Ok(await admin.PostAsJsonAsync("/api/ai/playground/reply",
                Body(new[] { Customer("Can I book a table for two?") })));
            var after = Snap();

            Assert.StartsWith("You're booked for", r.GetProperty("reply").GetString());
            var preview = Assert.Single(r.GetProperty("bookings_previewed").EnumerateArray());
            Assert.Equal("book", preview.GetProperty("type").GetString());
            Assert.False(string.IsNullOrEmpty(preview.GetProperty("label").GetString()));
            var book = r.GetProperty("tools").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "calendar_book");
            Assert.True(book.GetProperty("dry_run").GetBoolean());
            Assert.True(book.GetProperty("ok").GetBoolean());
            Untouched(before, after);
            // nothing reserved, nothing booked: not even a released row
            Assert.Equal(0, With(db => db.Bookings.Count()));
        }
        finally
        {
            _f.Chat.Script = null;
            Models();
            With(db =>
            {
                db.AgentSkills.RemoveRange(db.AgentSkills.Where(s => s.SkillId == "pg-cal"));
                db.SaveChanges();
            });
        }
    }

    // ---- RBAC + scope ------------------------------------------------------------------------------

    [Fact]
    public async Task An_agent_can_use_it_but_its_overrides_are_refused()
    {
        var agent = await Agent();
        var opts = await Ok(await agent.GetAsync("/api/ai/playground/options"));
        Assert.False(opts.GetProperty("can_override").GetBoolean());
        Assert.Equal(JsonValueKind.Null, opts.GetProperty("persona").ValueKind);
        Assert.Equal(0, opts.GetProperty("providers").GetArrayLength());

        var r = await Ok(await agent.PostAsJsonAsync("/api/ai/playground/reply", Body(new[] { Customer("Hello") })));
        Assert.False(string.IsNullOrEmpty(r.GetProperty("reply").GetString()));

        foreach (var options in new object[]
                 {
                     new { provider = "openai" }, new { persona = "Be rude." }, new { knowledge = false },
                     new { lessons = false }, new { skills = false }, new { memory = false }, new { show_prompt = true },
                 })
        {
            var res = await agent.PostAsJsonAsync("/api/ai/playground/reply", Body(new[] { Customer("Hello") }, options));
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        }
        // a role with neither ai.monitor nor ai.behaviour.manage can't open it at all
        var none = await SettingsTestKit.CustomRoleClient(_f, "Playground none", "chats.view");
        Assert.Equal(HttpStatusCode.Forbidden, (await none.GetAsync("/api/ai/playground/options")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await none.PostAsJsonAsync("/api/ai/playground/reply", Body(new[] { Customer("Hi") }))).StatusCode);
    }

    [Fact]
    public async Task Out_of_scope_entities_contacts_and_conversations_are_refused()
    {
        var (east, west, westContact, westThread, eastThread) = With(db =>
        {
            var now = DateTime.UtcNow;
            var tenant = db.Tenants.First(t => t.Slug == "demo");
            var e = db.Entities.FirstOrDefault(x => x.Slug == "pg-east")
                    ?? db.Entities.Add(new Entity { TenantId = tenant.Id, Name = "PG East", Slug = "pg-east", CreatedAt = now, UpdatedAt = now }).Entity;
            var w = db.Entities.FirstOrDefault(x => x.Slug == "pg-west")
                    ?? db.Entities.Add(new Entity { TenantId = tenant.Id, Name = "PG West", Slug = "pg-west", CreatedAt = now, UpdatedAt = now }).Entity;
            db.SaveChanges();
            var c = new Contact { Name = "Westie", Phone = "+60199000111", TenantId = tenant.Id, CreatedAt = now, UpdatedAt = now };
            db.Contacts.Add(c);
            db.SaveChanges();
            db.ContactEntities.Add(new ContactEntity { ContactId = c.Id, EntityId = w.Id, CreatedAt = now });
            var wt = new Thread { Channel = "whatsapp", Key = "60199000111", Name = "Westie", EntityId = w.Id, ContactId = c.Id, UpdatedAt = now, CreatedAt = now };
            var et = new Thread { Channel = "telegram", Key = "pg-east-1", Name = "Easton", EntityId = e.Id, UpdatedAt = now, CreatedAt = now };
            db.Threads.AddRange(wt, et);
            db.SaveChanges();
            db.Messages.AddRange(
                new Message { ThreadId = et.Id, Direction = MessageDirection.In, Text = "Do you deliver?", CreatedAt = now },
                new Message { ThreadId = et.Id, Direction = MessageDirection.Out, Source = MessageSource.Ai, Text = "Yes, within 5 km.", CreatedAt = now },
                new Message { ThreadId = et.Id, Direction = MessageDirection.Out, Private = true, Text = "internal note", CreatedAt = now },
                new Message { ThreadId = et.Id, Direction = MessageDirection.In, Text = "How much?", CreatedAt = now });
            db.SaveChanges();
            return (e.Id, w.Id, c.Id, wt, et);
        });
        SettingsTestKit.CreateUser(_f, "pg-east-agent", "pass12345", Roles.Agent);
        With(db =>
        {
            db.Profiles.Include(p => p.Entities).First(p => p.User!.Username == "pg-east-agent")
                .Entities.Add(db.Entities.First(x => x.Id == east));
            db.SaveChanges();
        });
        var agent = await SettingsTestKit.Client(_f, "pg-east-agent", "pass12345");

        // own outlet: fine; another outlet: 403; org-wide without picking one: 403; unknown: 404
        await Ok(await agent.PostAsJsonAsync("/api/ai/playground/reply", Body(new[] { Customer("Hi") }, entity: "pg-east")));
        Assert.Equal(HttpStatusCode.Forbidden,
            (await agent.PostAsJsonAsync("/api/ai/playground/reply", Body(new[] { Customer("Hi") }, entity: "pg-west"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await agent.PostAsJsonAsync("/api/ai/playground/reply", Body(new[] { Customer("Hi") }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await agent.PostAsJsonAsync("/api/ai/playground/reply", Body(new[] { Customer("Hi") }, entity: "pg-nowhere"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync("/api/ai/playground/options?entity=pg-west")).StatusCode);

        // a contact outside the agent's outlets: 404
        Assert.Equal(HttpStatusCode.NotFound, (await agent.PostAsJsonAsync("/api/ai/playground/reply",
            Body(new[] { Customer("Hi") }, entity: "pg-east", contactId: westContact))).StatusCode);

        // importing: the agent's own outlet's conversation works, the other outlet's is a 404
        var before = Snap();
        var imported = await Ok(await agent.PostAsJsonAsync("/api/ai/playground/import",
            new { channel = "telegram", key = "pg-east-1" }));
        Assert.Equal(HttpStatusCode.NotFound, (await agent.PostAsJsonAsync("/api/ai/playground/import",
            new { channel = westThread.Channel, key = westThread.Key })).StatusCode);
        Untouched(before, Snap());
        var turns = imported.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(3, turns.Count); // the private note stays out
        Assert.Equal(new[] { "customer", "ai", "customer" }, turns.Select(m => m.GetProperty("role").GetString()));
        Assert.Equal("pg-east", imported.GetProperty("entity").GetProperty("slug").GetString());
        Assert.Equal("telegram:pg-east-1", imported.GetProperty("thread").GetString());
        Assert.Equal("telegram", imported.GetProperty("channel").GetString());

        // an admin imports the west conversation with its contact, and the playground can carry its thread
        var admin = await Admin();
        var west_ = await Ok(await admin.PostAsJsonAsync("/api/ai/playground/import",
            new { channel = westThread.Channel, key = westThread.Key, take = 5 }));
        Assert.Equal(westContact, west_.GetProperty("contact").GetProperty("id").GetInt32());
        _ = west;
        _ = eastThread;
    }

    [Fact]
    public async Task The_reply_validates_its_transcript()
    {
        var admin = await Admin();
        async Task<HttpStatusCode> Status(object body) =>
            (await admin.PostAsJsonAsync("/api/ai/playground/reply", body)).StatusCode;
        Assert.Equal(HttpStatusCode.BadRequest, await Status(Body(Array.Empty<object>())));
        Assert.Equal(HttpStatusCode.BadRequest, await Status(Body(new[] { Customer("Hi"), Ai("Hello!") })));
        Assert.Equal(HttpStatusCode.BadRequest, await Status(Body(new[] { new { role = "system", text = "x" } })));
        Assert.Equal(HttpStatusCode.BadRequest, await Status(Body(new[] { Customer(new string('a', 4001)) })));
        Assert.Equal(HttpStatusCode.BadRequest,
            await Status(Body(Enumerable.Range(0, 61).Select(i => i % 2 == 0 ? Customer($"m{i}") : Ai($"a{i}")).Append(Customer("end")).ToArray())));
        Assert.Equal(HttpStatusCode.BadRequest, await Status(Body(new[] { Customer("Hi") }, channel: "fax")));
        Assert.Equal(HttpStatusCode.BadRequest,
            await Status(Body(new[] { Customer("Hi") }, profile: new { name = "X", language = "fr" })));
        Assert.Equal(HttpStatusCode.NotFound, await Status(Body(new[] { Customer("Hi") }, contactId: 987654)));
    }

    [Fact]
    public async Task The_rate_limit_answers_429_with_a_clear_detail()
    {
        var c = await SettingsTestKit.CustomRoleClient(_f, "Playground rate", "ai.monitor");
        for (var i = 0; i < Endpoints_Limit; i++)
            await Ok(await c.PostAsJsonAsync("/api/ai/playground/reply", Body(new[] { Customer($"Ping {i}") })));
        var res = await c.PostAsJsonAsync("/api/ai/playground/reply", Body(new[] { Customer("One too many") }));
        Assert.Equal((HttpStatusCode)429, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("a minute", body.GetProperty("detail").GetString());
        Assert.True(body.GetProperty("retry_after").GetInt32() >= 1);
        // …per user: someone else is not slowed down
        var admin = await Admin();
        await Ok(await admin.PostAsJsonAsync("/api/ai/playground/reply", Body(new[] { Customer("Still fine") })));
    }

    private const int Endpoints_Limit = ChatBranch.Api.Endpoints.PlaygroundRateLimiter.Limit;
}
