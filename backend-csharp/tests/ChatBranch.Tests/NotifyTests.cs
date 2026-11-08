using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Tests;

/// <summary>
/// POST /api/messaging/notify — one call for a message an APP originated (a login code,
/// an order update). It composes the pieces the inbox already uses, so what is tested
/// here is the composition: idempotency, the purpose split on the cold-text guard, the
/// template fallback on a shut window, opt-out with no override, and the rule that this
/// route never answers 404 (a client reads 404 as "no such endpoint" and falls back).
/// </summary>
public class NotifyTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public NotifyTests(ApiFactory f) => _f = f;

    private T Db<T>(Func<AppDbContext, T> fn)
    {
        using var scope = _f.Services.CreateScope();
        return fn(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private static async Task<JsonElement> Ok(HttpResponseMessage res)
    {
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, text);
        return JsonDocument.Parse(text).RootElement;
    }

    private static async Task<(HttpStatusCode Status, string Code, string Detail)> Refusal(HttpResponseMessage res)
    {
        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        var code = body.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
        var detail = body.TryGetProperty("detail", out var d) ? d.GetString() ?? "" : "";
        return (res.StatusCode, code, detail);
    }

    private Task<HttpResponseMessage> Notify(HttpClient client, object body) =>
        client.PostAsJsonAsync("/api/messaging/notify", body);

    /// <summary>A unique customer number per test, so the tests cannot share a conversation.</summary>
    private static string Number() => "6019" + Random.Shared.Next(1000000, 9999999);

    /// <summary>A QR (Baileys) number: a conversation-role account, so the cold guard applies.</summary>
    private int QrNumber(string name) => Db(db =>
    {
        var a = new ChannelAccount
        {
            Channel = "whatsapp", Provider = Providers.Baileys, Name = name,
            ExternalId = $"notify-{Guid.NewGuid():N}"[..20], HealthStatus = Health.Healthy,
            CreatedAt = DateTime.UtcNow,
        };
        db.ChannelAccounts.Add(a);
        db.SaveChanges();
        return a.Id;
    });

    private Thread ThreadOf(string channel, string key) =>
        Db(db => db.Threads.Include(t => t.Account).Single(t => t.Channel == channel && t.Key == key));

    private List<Message> Messages(int threadId) =>
        Db(db => db.Messages.Where(m => m.ThreadId == threadId).OrderBy(m => m.Id).ToList());

    private void SetGuard(string value) => Db(db =>
    {
        var sc = new ScopingService(db);
        sc.SetSetting("cold_text_guard", value);
        return 0;
    });

    // ---- the happy path ----------------------------------------------------

    [Fact]
    public async Task A_first_send_opens_the_conversation_and_a_second_reuses_it()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var to = Number();

        var first = await Ok(await Notify(admin, new
        {
            channel = "whatsapp", to, text = "123456 is your code.", purpose = "otp",
            name = "Aisha",
        }));
        var threadId = first.GetProperty("thread").GetProperty("thread_id").GetInt32();
        Assert.Equal("whatsapp", first.GetProperty("thread").GetProperty("channel").GetString());
        Assert.Equal(to, first.GetProperty("thread").GetProperty("key").GetString());
        Assert.Equal($"/chats?thread=whatsapp:{to}", first.GetProperty("thread").GetProperty("url").GetString());
        Assert.False(first.GetProperty("idempotent").GetBoolean());
        Assert.Equal("text", first.GetProperty("sent_as").GetString());
        // nobody typed this: it is a system message, so it cannot read as a person's reply
        Assert.Equal("system", first.GetProperty("message").GetProperty("source").GetString());

        var second = await Ok(await Notify(admin, new
        {
            channel = "whatsapp", to, text = "Your order is on the way.",
        }));
        Assert.Equal(threadId, second.GetProperty("thread").GetProperty("thread_id").GetInt32());
        Assert.Equal(2, Messages(threadId).Count);
    }

    [Fact]
    public async Task With_no_live_account_the_status_is_sandbox___never_sent()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var answer = await Ok(await Notify(admin, new
        {
            channel = "whatsapp", to = Number(), text = "123456 is your code.", purpose = "otp",
        }));
        // the message IS filed in the inbox, so this is a 200 — but it must never claim
        // to have been sent: an OTP that looks delivered and is not breaks a login
        Assert.Equal("sandbox", answer.GetProperty("message").GetProperty("status").GetString());
    }

    // ---- idempotency -------------------------------------------------------

    [Fact]
    public async Task The_same_key_twice_sends_once()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var to = Number();
        var key = $"onel-otp-{to}-{Guid.NewGuid():N}";

        var first = await Ok(await Notify(admin, new
        {
            channel = "whatsapp", to, text = "123456 is your code.", purpose = "otp",
            idempotency_key = key,
        }));
        var second = await Ok(await Notify(admin, new
        {
            channel = "whatsapp", to, text = "123456 is your code.", purpose = "otp",
            idempotency_key = key,
        }));

        Assert.False(first.GetProperty("idempotent").GetBoolean());
        Assert.True(second.GetProperty("idempotent").GetBoolean());
        Assert.Equal(first.GetProperty("message").GetProperty("id").GetInt32(),
            second.GetProperty("message").GetProperty("id").GetInt32());
        Assert.Single(Messages(ThreadOf("whatsapp", to).Id));
        Assert.Equal(key, Db(db => db.Messages.Single(m => m.IdempotencyKey == key).IdempotencyKey));
    }

    [Fact]
    public async Task Two_concurrent_identical_keys_send_once()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var to = Number();
        var key = $"race-{Guid.NewGuid():N}";
        var body = new { channel = "whatsapp", to, text = "123456 is your code.", purpose = "otp", idempotency_key = key };

        // fired together: whichever loses the unique index must read the winner's
        // message back rather than storing a second one
        var results = await Task.WhenAll(Notify(admin, body), Notify(admin, body));

        foreach (var res in results)
            Assert.True(res.IsSuccessStatusCode || res.StatusCode == HttpStatusCode.Conflict,
                $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        Assert.Equal(1, Db(db => db.Messages.Count(m => m.IdempotencyKey == key)));
        Assert.Single(Messages(ThreadOf("whatsapp", to).Id));
    }

    // ---- compliance --------------------------------------------------------

    [Fact]
    public async Task An_opted_out_conversation_is_refused_with_no_way_round_it()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var to = Number();
        await Ok(await Notify(admin, new { channel = "whatsapp", to, text = "Hello", purpose = "otp" }));
        var threadId = ThreadOf("whatsapp", to).Id;
        Db(db =>
        {
            db.Threads.Single(t => t.Id == threadId).OptedOut = true;
            return db.SaveChanges();
        });
        var before = Messages(threadId).Count;

        // even an OTP, and even with force — there is no force on this route
        var res = await Notify(admin, new
        {
            channel = "whatsapp", to, text = "123456 is your code.", purpose = "otp", force = true,
        });

        var (status, code, detail) = await Refusal(res);
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("opted_out", code);
        Assert.Contains("START", detail);
        Assert.Equal(before, Messages(threadId).Count);   // nothing stored
    }

    [Fact]
    public async Task Purpose_decides_the_cold_text_guard()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var accountId = QrNumber("Cold guard number");
        SetGuard("block");
        try
        {
            // a code the customer just asked for goes out: blocking it breaks a login
            var otp = await Ok(await Notify(admin, new
            {
                channel = "whatsapp", to = Number(), text = "123456 is your code.", purpose = "otp",
                account_id = accountId,
            }));
            Assert.Equal("text", otp.GetProperty("sent_as").GetString());

            // the same conversation shape, sent as a notification, is the ban-risk case
            var res = await Notify(admin, new
            {
                channel = "whatsapp", to = Number(), text = "Your order is ready.",
                purpose = "notification", account_id = accountId,
            });
            var (status, code, detail) = await Refusal(res);
            Assert.Equal(HttpStatusCode.Conflict, status);
            Assert.Equal("cold_text", code);
            Assert.Contains("template", detail);
        }
        finally
        {
            SetGuard("allow");
        }
    }

    [Fact]
    public async Task A_warning_guard_sends_and_says_so_in_the_chat()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var accountId = QrNumber("Warn guard number");
        var to = Number();
        SetGuard("warn");
        try
        {
            await Ok(await Notify(admin, new
            {
                channel = "whatsapp", to, text = "Your order is ready.", purpose = "notification",
                account_id = accountId,
            }));
            var msgs = Messages(ThreadOf("whatsapp", to).Id);
            Assert.Equal(2, msgs.Count);
            Assert.Contains("Cold outbound", msgs[1].Text);
        }
        finally
        {
            SetGuard("allow");
        }
    }

    [Fact]
    public async Task It_never_closes_an_open_handover()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var to = Number();
        await Ok(await Notify(admin, new { channel = "whatsapp", to, text = "Hi", purpose = "otp" }));
        var threadId = ThreadOf("whatsapp", to).Id;
        Db(db => Handover.Mark(db, db.Threads.Single(t => t.Id == threadId), HandoverKinds.Manual,
            "Customer wants a human", "agent:admin"));

        await Ok(await Notify(admin, new
        {
            channel = "whatsapp", to, text = "123456 is your code.", purpose = "otp",
        }));

        // an order update is not a reply: the person waiting still has to answer
        var thread = Db(db => db.Threads.Single(t => t.Id == threadId));
        Assert.Null(thread.HandoverResolvedAt);
        Assert.Equal(HandoverKinds.Manual, thread.HandoverKind);
        // and it writes its own audit row
        Assert.True(Db(db => db.AuditLogs.Any(a => a.Action == "message.notify"
                                                   && a.Target == $"whatsapp:{to}")));
    }

    [Fact]
    public async Task An_idempotent_replay_writes_no_second_audit_row()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var to = Number();
        var key = $"audit-{Guid.NewGuid():N}";
        var body = new { channel = "whatsapp", to, text = "123456 is your code.", purpose = "otp", idempotency_key = key };
        await Ok(await Notify(admin, body));
        var after = Db(db => db.AuditLogs.Count(a => a.Action == "message.notify" && a.Target == $"whatsapp:{to}"));

        await Ok(await Notify(admin, body));

        Assert.Equal(after, Db(db => db.AuditLogs.Count(a => a.Action == "message.notify"
                                                             && a.Target == $"whatsapp:{to}")));
    }

    // ---- the new conversation's chatbot tick -------------------------------

    [Fact]
    public async Task Ai_off_opens_the_conversation_with_the_chatbot_off()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var to = Number();
        await Ok(await Notify(admin, new
        {
            channel = "whatsapp", to, text = "123456 is your code.", purpose = "otp", ai = "off",
        }));
        var thread = ThreadOf("whatsapp", to);
        Assert.False(AiEligibility.Effective(thread, thread.Account, reminderDefaultOn: false));
    }

    // ---- validation, and never 404 -----------------------------------------

    [Theory]
    [InlineData("pigeon", "", "", "bad_channel")]
    [InlineData("whatsapp", "", "hello", "bad_recipient")]
    [InlineData("email", "not-an-address", "hello", "bad_recipient")]
    [InlineData("whatsapp", "60123456789", "", "nothing_to_send")]
    public async Task A_bad_request_is_422_with_a_code(string channel, string to, string text, string expected)
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var res = await Notify(admin, new { channel, to, text });
        var (status, code, _) = await Refusal(res);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal(expected, code);
    }

    [Fact]
    public async Task A_bad_purpose_or_a_template_off_whatsapp_is_422()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var (_, badPurpose, _) = await Refusal(await Notify(admin, new
        {
            channel = "whatsapp", to = Number(), text = "hi", purpose = "marketing",
        }));
        Assert.Equal("bad_purpose", badPurpose);

        var (_, noTemplates, _) = await Refusal(await Notify(admin, new
        {
            channel = "telegram", to = "12345", text = "hi",
            template = new { name = "order_update" },
        }));
        Assert.Equal("template_unsupported", noTemplates);
    }

    [Fact]
    public async Task An_unknown_entity_is_422___never_404()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var res = await Notify(admin, new
        {
            channel = "whatsapp", to = Number(), text = "hi", purpose = "otp",
            entity = $"no-such-entity-{Guid.NewGuid():N}"[..20],
        });
        var (status, code, _) = await Refusal(res);
        // 404 is reserved for "this ChatBranch has no /notify", which clients fall back on
        Assert.NotEqual(HttpStatusCode.NotFound, status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("unknown_entity", code);
    }

    [Fact]
    public async Task An_entity_outside_the_callers_scope_is_403___never_404()
    {
        // an agent bound to one entity, asked to send for another
        var sfx = Guid.NewGuid().ToString("N")[..8];
        var (bound, otherSlug) = Db(db =>
        {
            var tenant = db.Entities.AsNoTracking().First();
            var now = DateTime.UtcNow;
            var mine = new Entity
            {
                Name = $"Notify mine {sfx}", Slug = $"notify-mine-{sfx}", TenantId = tenant.TenantId,
                CreatedAt = now, UpdatedAt = now,
            };
            var theirs = new Entity
            {
                Name = $"Notify theirs {sfx}", Slug = $"notify-theirs-{sfx}", TenantId = tenant.TenantId,
                CreatedAt = now, UpdatedAt = now,
            };
            db.Entities.AddRange(mine, theirs);
            db.SaveChanges();
            var username = $"notifyagent{sfx}";
            var user = new AppUser
            {
                Username = username, Password = DjangoPasswordHasher.Hash("agent123"), IsActive = true,
            };
            db.Users.Add(user);
            db.SaveChanges();
            var profile = new Profile { UserId = user.Id, Role = Roles.Agent, TenantId = tenant.TenantId };
            profile.Entities.Add(mine);
            db.Profiles.Add(profile);
            db.SaveChanges();
            return (username, theirs.Slug);
        });

        var client = await Platform.As(_f, bound, "agent123");
        var res = await Notify(client, new
        {
            channel = "whatsapp", to = Number(), text = "hi", purpose = "otp", entity = otherSlug,
        });

        var (status, code, _) = await Refusal(res);
        Assert.NotEqual(HttpStatusCode.NotFound, status);
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("out_of_scope", code);
    }

    [Fact]
    public async Task A_read_only_api_key_is_refused_before_the_handler()
    {
        var token = Platform.MintKey(_f, "admin", "contacts.view");
        var client = _f.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", token);

        var res = await Notify(client, new
        {
            channel = "whatsapp", to = Number(), text = "hi", purpose = "otp",
        });

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task An_api_key_with_chats_send_may_notify()
    {
        var token = Platform.MintKey(_f, "admin", "chats.send");
        var client = _f.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", token);

        var answer = await Ok(await Notify(client, new
        {
            channel = "whatsapp", to = Number(), text = "123456 is your code.", purpose = "otp",
        }));

        Assert.Equal("system", answer.GetProperty("message").GetProperty("source").GetString());
    }
}
