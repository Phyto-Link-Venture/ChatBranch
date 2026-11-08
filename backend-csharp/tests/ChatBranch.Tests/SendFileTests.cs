using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ChatBranch.Data;
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
/// A fetcher that answers from a map instead of the internet, so the endpoint's own
/// behaviour (one fetch for many targets, the per-target guards, the refusals) is
/// testable offline. The real one's rules are checked separately, as pure functions.
/// </summary>
public sealed class FakeMediaFetcher : IRemoteMediaFetcher
{
    public Dictionary<string, RemoteFile> Files { get; } = new();
    public Dictionary<string, RemoteMediaResult> Failures { get; } = new();
    public List<string> Fetched { get; } = new();

    public Task<RemoteMediaResult> FetchAsync(string url, long maxBytes, int? entityId, CancellationToken ct = default)
    {
        lock (Fetched) Fetched.Add(url);
        if (Failures.TryGetValue(url, out var failure)) return Task.FromResult(failure);
        if (!Files.TryGetValue(url, out var file))
            return Task.FromResult(RemoteMediaResult.Fail(RemoteMediaError.NotFound, "no such test file"));
        return Task.FromResult(file.Content.Length > maxBytes
            ? RemoteMediaResult.Fail(RemoteMediaError.TooLarge, "too large for this channel")
            : new RemoteMediaResult(file));
    }
}

public class SendFileFactory : ApiFactory
{
    public FakeMediaFetcher Fetcher { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s => s.AddScoped<IRemoteMediaFetcher>(_ => Fetcher));
    }
}

/// <summary>
/// POST /api/messaging/send-file — sending a file by a SHORT reference instead of
/// spelling it out in base64, to one conversation or to several at once.
/// </summary>
public class SendFileTests : IClassFixture<SendFileFactory>
{
    private readonly SendFileFactory _f;
    public SendFileTests(SendFileFactory f) => _f = f;

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

    private Task<HttpResponseMessage> Send(HttpClient c, object body) =>
        c.PostAsJsonAsync("/api/messaging/send-file", body);

    private static string Number() => "6018" + Random.Shared.Next(1000000, 9999999);

    private RemoteFile APhoto(string url, int kb = 400)
    {
        var file = new RemoteFile(new byte[kb * 1024], "photo.jpg", "image/jpeg", "image");
        _f.Fetcher.Files[url] = file;
        return file;
    }

    private List<Message> Messages(string channel, string key) => Db(db =>
    {
        var t = db.Threads.Single(x => x.Channel == channel && x.Key == key);
        return db.Messages.Where(m => m.ThreadId == t.Id).OrderBy(m => m.Id).ToList();
    });

    // ---- a link, on channels that never took one -------------------------------

    [Fact]
    public async Task A_link_sends_on_whatsapp___which_used_to_need_the_whole_file()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var url = $"https://files.example.com/{Guid.NewGuid():N}/photo.jpg";
        APhoto(url, kb: 2048);   // 2 MB: hopeless as base64, fine as a link
        var to = Number();

        var answer = await Ok(await Send(admin, new { channel = "whatsapp", key = to, media_url = url, caption = "Invoice" }));

        Assert.Equal("photo.jpg", answer.GetProperty("file").GetProperty("name").GetString());
        Assert.Equal("image", answer.GetProperty("file").GetProperty("kind").GetString());
        Assert.Equal(2048 * 1024, answer.GetProperty("file").GetProperty("size").GetInt32());
        Assert.Equal("url", answer.GetProperty("file").GetProperty("source").GetString());
        Assert.Equal(1, answer.GetProperty("sent").GetInt32());

        var msg = Assert.Single(Messages("whatsapp", to));
        Assert.Equal("image", msg.MediaType);
        Assert.Equal("Invoice", msg.Text);
        Assert.NotEqual("", msg.MediaPath);
    }

    [Fact]
    public async Task An_email_takes_a_link_too___with_its_own_subject()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var url = $"https://files.example.com/{Guid.NewGuid():N}/invoice.pdf";
        _f.Fetcher.Files[url] = new RemoteFile(Encoding.UTF8.GetBytes("%PDF-1.4 fake"), "invoice.pdf",
            "application/pdf", "document");
        var to = $"buyer-{Guid.NewGuid():N}"[..18] + "@example.com";

        var answer = await Ok(await Send(admin, new
        {
            channel = "email", key = to, media_url = url, caption = "Your invoice", subject = "Invoice INV-0012",
        }));

        Assert.Equal(1, answer.GetProperty("sent").GetInt32());
        Assert.Equal("document", answer.GetProperty("file").GetProperty("kind").GetString());
        Assert.Single(Messages("email", to));
    }

    // ---- one file, several conversations ---------------------------------------

    [Fact]
    public async Task One_call_fetches_once_and_delivers_to_every_target()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var url = $"https://files.example.com/{Guid.NewGuid():N}/photo.jpg";
        APhoto(url);
        _f.Fetcher.Fetched.Clear();
        var wa = Number();
        var mail = $"both-{Guid.NewGuid():N}"[..14] + "@example.com";

        var answer = await Ok(await Send(admin, new
        {
            targets = new object[]
            {
                new { channel = "whatsapp", key = wa },
                new { channel = "email", key = mail, subject = "Photo" },
            },
            media_url = url, caption = "Here you go",
        }));

        // the whole point: the file travels once, not once per recipient
        Assert.Single(_f.Fetcher.Fetched);
        Assert.Equal(2, answer.GetProperty("sent").GetInt32());
        Assert.Equal(0, answer.GetProperty("failed").GetInt32());
        var results = answer.GetProperty("results").EnumerateArray().ToList();
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.GetProperty("ok").GetBoolean()));
        Assert.Contains(results, r => r.GetProperty("channel").GetString() == "whatsapp");
        Assert.Contains(results, r => r.GetProperty("channel").GetString() == "email");
        Assert.Single(Messages("whatsapp", wa));
        Assert.Single(Messages("email", mail));
    }

    [Fact]
    public async Task One_refused_target_never_costs_the_others()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var url = $"https://files.example.com/{Guid.NewGuid():N}/photo.jpg";
        APhoto(url);
        var good = Number();
        var optedOut = Number();
        // give the opted-out conversation a history, then STOP
        await Ok(await Send(admin, new { channel = "whatsapp", key = optedOut, media_url = url }));
        Db(db =>
        {
            db.Threads.Single(t => t.Key == optedOut).OptedOut = true;
            return db.SaveChanges();
        });
        var before = Messages("whatsapp", optedOut).Count;

        var answer = await Ok(await Send(admin, new
        {
            targets = new object[]
            {
                new { channel = "whatsapp", key = good },
                new { channel = "whatsapp", key = optedOut },
            },
            media_url = url,
        }));

        Assert.Equal(1, answer.GetProperty("sent").GetInt32());
        Assert.Equal(1, answer.GetProperty("failed").GetInt32());
        var refused = Assert.Single(answer.GetProperty("results").EnumerateArray()
            .Where(r => !r.GetProperty("ok").GetBoolean()));
        Assert.Equal("opted_out", refused.GetProperty("code").GetString());
        Assert.Equal(before, Messages("whatsapp", optedOut).Count);   // nothing added there
        Assert.Single(Messages("whatsapp", good));                     // and the other went
    }

    [Fact]
    public async Task More_than_twenty_targets_is_refused_outright()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var url = $"https://files.example.com/{Guid.NewGuid():N}/photo.jpg";
        APhoto(url);
        var many = Enumerable.Range(0, 21).Select(_ => new { channel = "whatsapp", key = Number() }).ToArray();

        var res = await Send(admin, new { targets = many, media_url = url });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Contains("too_many_targets", await res.Content.ReadAsStringAsync());
    }

    // ---- a file ChatBranch already holds ---------------------------------------

    [Fact]
    public async Task A_stored_file_is_resent_to_another_channel_by_message_id()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var url = $"https://files.example.com/{Guid.NewGuid():N}/invoice.pdf";
        _f.Fetcher.Files[url] = new RemoteFile(Encoding.UTF8.GetBytes("%PDF-1.4 the customer's invoice"),
            "invoice.pdf", "application/pdf", "document");
        var mail = $"src-{Guid.NewGuid():N}"[..14] + "@example.com";
        await Ok(await Send(admin, new { channel = "email", key = mail, media_url = url }));
        var stored = Assert.Single(Messages("email", mail));
        var wa = Number();
        _f.Fetcher.Fetched.Clear();

        // the same file, from an email thread into a WhatsApp chat — which a WhatsApp
        // forward cannot do, because a forward is same-channel by nature
        var answer = await Ok(await Send(admin, new
        {
            channel = "whatsapp", key = wa, source_message_id = stored.Id, caption = "Your invoice",
        }));

        Assert.Empty(_f.Fetcher.Fetched);   // read off disk, not fetched again
        Assert.Equal("message", answer.GetProperty("file").GetProperty("source").GetString());
        Assert.Equal("invoice.pdf", answer.GetProperty("file").GetProperty("name").GetString());
        Assert.Equal(1, answer.GetProperty("sent").GetInt32());
        var resent = Assert.Single(Messages("whatsapp", wa));
        Assert.Equal("document", resent.MediaType);
        Assert.NotEqual(stored.MediaPath, resent.MediaPath);   // its own copy under its own channel
    }

    [Fact]
    public async Task A_message_with_no_file___or_outside_the_scope___is_refused()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var to = Number();
        var plain = Db(db =>
        {
            var t = new Thread { Channel = "whatsapp", Key = to, Address = to, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.Threads.Add(t);
            db.SaveChanges();
            var m = new Message
            {
                ThreadId = t.Id, Direction = MessageDirection.In, Source = MessageSource.Customer,
                Text = "no attachment here", CreatedAt = DateTime.UtcNow,
            };
            db.Messages.Add(m);
            db.SaveChanges();
            return m.Id;
        });

        var res = await Send(admin, new { channel = "whatsapp", key = Number(), source_message_id = plain });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Contains("no_file", await res.Content.ReadAsStringAsync());

        var missing = await Send(admin, new { channel = "whatsapp", key = Number(), source_message_id = 99_000_000 });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    // ---- base64 still works exactly as before ----------------------------------

    [Fact]
    public async Task Base64_still_works___and_needs_a_file_name()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var to = Number();
        var bytes = Convert.ToBase64String(Encoding.UTF8.GetBytes("%PDF-1.4 small"));

        var answer = await Ok(await Send(admin, new
        {
            channel = "whatsapp", key = to, file_base64 = bytes, file_name = "note.pdf",
        }));
        Assert.Equal("base64", answer.GetProperty("file").GetProperty("source").GetString());
        Assert.Equal(1, answer.GetProperty("sent").GetInt32());

        var noName = await Send(admin, new { channel = "whatsapp", key = Number(), file_base64 = bytes });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noName.StatusCode);
        Assert.Contains("file_name", await noName.Content.ReadAsStringAsync());

        var notBase64 = await Send(admin, new
        {
            channel = "whatsapp", key = Number(), file_base64 = "!!! not base64 !!!", file_name = "x.pdf",
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, notBase64.StatusCode);
    }

    // ---- refusals from the fetch itself ----------------------------------------

    [Theory]
    [InlineData(RemoteMediaError.Blocked, HttpStatusCode.UnprocessableEntity, "blocked_media_url")]
    [InlineData(RemoteMediaError.NoAccess, HttpStatusCode.Forbidden, "media_no_access")]
    [InlineData(RemoteMediaError.NotFound, HttpStatusCode.NotFound, "media_not_found")]
    [InlineData(RemoteMediaError.TooLarge, HttpStatusCode.RequestEntityTooLarge, "too_large")]
    [InlineData(RemoteMediaError.Timeout, HttpStatusCode.GatewayTimeout, "media_timeout")]
    [InlineData(RemoteMediaError.Unreadable, HttpStatusCode.UnprocessableEntity, "media_unreadable")]
    public async Task A_link_that_cannot_be_fetched_says_which_way_it_failed(RemoteMediaError error,
        HttpStatusCode status, string code)
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var url = $"https://files.example.com/{Guid.NewGuid():N}/bad";
        _f.Fetcher.Failures[url] = RemoteMediaResult.Fail(error, "the reason, in words");

        var res = await Send(admin, new { channel = "whatsapp", key = Number(), media_url = url });

        Assert.Equal(status, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains(code, body);
        Assert.Contains("the reason, in words", body);
    }

    [Fact]
    public async Task A_file_over_whatsapps_limit_is_refused_before_any_send()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var url = $"https://files.example.com/{Guid.NewGuid():N}/huge.jpg";
        _f.Fetcher.Files[url] = new RemoteFile(new byte[17 * 1024 * 1024], "huge.jpg", "image/jpeg", "image");

        var res = await Send(admin, new { channel = "whatsapp", key = Number(), media_url = url });

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, res.StatusCode);
    }

    [Fact]
    public async Task Nothing_to_send___and_no_target___are_both_refused()
    {
        var admin = await Platform.As(_f, "admin", "admin123");

        var noSource = await Send(admin, new { channel = "whatsapp", key = Number() });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noSource.StatusCode);
        Assert.Contains("nothing_to_send", await noSource.Content.ReadAsStringAsync());

        var noTarget = await Send(admin, new { media_url = "https://files.example.com/x.jpg" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noTarget.StatusCode);
        Assert.Contains("no_target", await noTarget.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_read_only_api_key_cannot_send_a_file()
    {
        var token = Platform.MintKey(_f, "admin", "contacts.view");
        var client = _f.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", token);

        var res = await Send(client, new
        {
            channel = "whatsapp", key = Number(), media_url = "https://files.example.com/x.jpg",
        });

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }
}
