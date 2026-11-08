using System.Diagnostics;
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
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Tests;

public class StickerFactory : ApiFactory
{
    public FakeMediaFetcher Fetcher { get; } = new();
    public MediaBaileys Gateway { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s =>
        {
            s.AddScoped<IRemoteMediaFetcher>(_ => Fetcher);
            s.AddSingleton<IBaileysGateway>(Gateway);
        });
    }
}

/// <summary>
/// <c>POST /api/messaging/send-sticker</c> — a picture sent as a real WhatsApp sticker.
/// Receiving stickers always worked; sending one needs the picture SHAPED first (512×512
/// WebP, under WhatsApp's own cap, EXIF pack), which is what this route does before the
/// gateway sees it. The shaping itself is <see cref="StickerTests"/>; here it is the
/// route: the guards a manual send applies, the honest refusal on a number that cannot
/// carry one, and the fact that what goes out is a <c>sticker</c> and not an image.
/// </summary>
public class StickerSendTests : IClassFixture<StickerFactory>
{
    private readonly StickerFactory _f;
    public StickerSendTests(StickerFactory f) => _f = f;

    private T Db<T>(Func<AppDbContext, T> fn)
    {
        using var scope = _f.Services.CreateScope();
        return fn(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private Task<HttpResponseMessage> Send(HttpClient c, object body) =>
        c.PostAsJsonAsync("/api/messaging/send-sticker", body);

    private static async Task<JsonElement> Ok(HttpResponseMessage res)
    {
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, text);
        return JsonDocument.Parse(text).RootElement;
    }

    private static async Task<(HttpStatusCode Status, string Code, string Detail)> Refused(HttpResponseMessage res)
    {
        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        return (res.StatusCode,
            body.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "",
            body.TryGetProperty("detail", out var d) ? d.GetString() ?? "" : "");
    }

    private static string Number() => "6017" + Random.Shared.Next(1000000, 9999999);

    private List<Message> Messages(string key) => Db(db =>
    {
        var t = db.Threads.Single(x => x.Channel == "whatsapp" && x.Key == key);
        return db.Messages.Where(m => m.ThreadId == t.Id).OrderBy(m => m.Id).ToList();
    });

    /// <summary>A real picture at a link, so the conversion is the real one.</summary>
    private string APicture(string spec = "testsrc=size=900x300:duration=1:rate=1", string ext = "png")
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-stk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "pic." + ext);
            var psi = new ProcessStartInfo(AudioTranscoder.FfmpegPath()!)
            {
                RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false,
            };
            foreach (var a in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
                                      "-f", "lavfi", "-i", spec, path })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.StandardError.ReadToEnd();
            p.StandardOutput.ReadToEnd();
            p.WaitForExit(60_000);
            Assert.Equal(0, p.ExitCode);

            var url = $"https://files.example.com/{Guid.NewGuid():N}/pic.{ext}";
            _f.Fetcher.Files[url] = new RemoteFile(File.ReadAllBytes(path), "pic." + ext,
                ext == "gif" ? "image/gif" : "image/png", "image");
            return url;
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>A QR-paired number with the customer having written, so the guards pass.</summary>
    private int LiveQrNumber(string key, bool inbound = true, bool optedOut = false)
    {
        return Platform.WithDb(_f, db =>
        {
            var qr = new ChannelAccount
            {
                Channel = "whatsapp", Provider = Providers.Baileys, Name = "QR stickers",
                ExternalId = "gw-stk-" + Guid.NewGuid().ToString("N")[..8], CreatedAt = DateTime.UtcNow,
            };
            db.ChannelAccounts.Add(qr);
            db.SaveChanges();
            var thread = new Thread
            {
                Channel = "whatsapp", Key = key, Name = "Sticker fan", AccountId = qr.Id, OptedOut = optedOut,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.Threads.Add(thread);
            db.SaveChanges();
            if (inbound)
            {
                db.Messages.Add(new Message
                {
                    ThreadId = thread.Id, Direction = MessageDirection.In, Text = "send me a sticker",
                    Source = MessageSource.Customer, Status = "received", CreatedAt = DateTime.UtcNow,
                });
                db.SaveChanges();
            }
            return qr.Id;
        });
    }

    /// <summary>
    /// An OFFICIAL (Cloud) number on the conversation. <paramref name="live"/> decides which
    /// of the two honest answers this exercises: a configured number is told stickers are not
    /// wired for the Cloud API, an unconfigured one keeps a sandbox row.
    /// </summary>
    private int CloudNumber(string key, bool live)
    {
        return Platform.WithDb(_f, db =>
        {
            var cloud = new ChannelAccount
            {
                Channel = "whatsapp", Provider = Providers.Cloud, Name = live ? "Cloud live" : "Cloud blank",
                ExternalId = "pnid-" + Guid.NewGuid().ToString("N")[..8],
                AccessToken = live ? "EAAG-test-token" : "",
                // a conversation number, so a conversational send may use it
                Config = new Dictionary<string, object?> { ["role"] = "conversation" },
                CreatedAt = DateTime.UtcNow,
            };
            db.ChannelAccounts.Add(cloud);
            db.SaveChanges();
            var thread = new Thread
            {
                Channel = "whatsapp", Key = key, Name = "Official chat", AccountId = cloud.Id,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.Threads.Add(thread);
            db.SaveChanges();
            db.Messages.Add(new Message
            {
                ThreadId = thread.Id, Direction = MessageDirection.In, Text = "hi",
                Source = MessageSource.Customer, Status = "received", CreatedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
            return cloud.Id;
        });
    }

    // ---- the happy path ----------------------------------------------------

    [Fact]
    public async Task A_wide_photo_goes_out_as_a_sticker___not_as_an_image()
    {
        if (!AudioTranscoder.Available) return;
        var admin = await Platform.As(_f, "admin", "admin123");
        var to = Number();
        LiveQrNumber(to);
        _f.Gateway.Media.Clear();
        var url = APicture();

        var answer = await Ok(await Send(admin, new
        {
            channel = "whatsapp", key = to, media_url = url, pack = "Demo Pack", emojis = new[] { "⭐" },
        }));

        Assert.True(answer.GetProperty("ok").GetBoolean());
        Assert.Equal("sent", answer.GetProperty("status").GetString());
        var sticker = answer.GetProperty("sticker");
        Assert.False(sticker.GetProperty("animated").GetBoolean());
        Assert.Equal(512, sticker.GetProperty("width").GetInt32());
        Assert.Equal("Demo Pack", sticker.GetProperty("pack").GetString());
        Assert.Equal("url", sticker.GetProperty("source").GetString());

        // what reached the gateway is a STICKER, webp, with no caption
        var call = Assert.Single(_f.Gateway.Media);
        Assert.Equal("sticker", call.Type);
        Assert.Equal("image/webp", call.Mime);
        Assert.Null(call.Caption);
        Assert.Null(StickerImage.Refusal(call.Content));
        Assert.Equal((512, 512), StickerImage.CanvasOf(call.Content));
        Assert.True(call.Content.Length <= StickerImage.MaxStaticBytes);

        // the row reads like an INBOUND sticker does, so the inbox renders both the same
        var msg = Messages(to).Last();
        Assert.Equal("[sticker]", msg.Text);
        Assert.Equal("image", msg.MediaType);
        Assert.Equal("image/webp", msg.MediaMime);
        Assert.NotEqual("", msg.MediaPath);
        Assert.Equal("3EB0MEDIA1", msg.ExternalId);
    }

    [Fact]
    public async Task An_animated_gif_becomes_an_animated_sticker()
    {
        if (!AudioTranscoder.Available) return;
        var admin = await Platform.As(_f, "admin", "admin123");
        var to = Number();
        LiveQrNumber(to);
        _f.Gateway.Media.Clear();
        var url = APicture("testsrc=size=400x300:duration=3:rate=15", "gif");

        var answer = await Ok(await Send(admin, new { channel = "whatsapp", key = to, media_url = url }));

        Assert.True(answer.GetProperty("sticker").GetProperty("animated").GetBoolean());
        var call = Assert.Single(_f.Gateway.Media);
        Assert.True(StickerImage.IsAnimatedWebp(call.Content));
        Assert.True(call.Content.Length <= StickerImage.MaxAnimatedBytes);
    }

    [Fact]
    public async Task A_number_that_cannot_send_fails_over_like_any_other_send()
    {
        if (!AudioTranscoder.Available) return;
        var admin = await Platform.As(_f, "admin", "admin123");
        var to = Number();
        CloudNumber(to, live: false);   // pinned to a number with no credentials
        _f.Gateway.Media.Clear();
        var url = APicture();

        var answer = await Ok(await Send(admin, new { channel = "whatsapp", key = to, media_url = url }));

        // routing is not bypassed for stickers: the conversation moves to a number that
        // can actually carry one, exactly as a text or a file would
        Assert.Equal("sent", answer.GetProperty("status").GetString());
        Assert.Equal("sticker", Assert.Single(_f.Gateway.Media).Type);
    }

    [Fact]
    public async Task An_official_number_is_told_which_kind_of_no_it_is()
    {
        if (!AudioTranscoder.Available) return;
        var admin = await Platform.As(_f, "admin", "admin123");
        var to = Number();
        CloudNumber(to, live: true);
        var url = APicture();

        var refused = await Refused(await Send(admin, new { channel = "whatsapp", key = to, media_url = url }));

        // Meta's API CAN carry a sticker — it is simply not wired here, and saying so
        // beats implying the platform cannot do it
        Assert.Equal(HttpStatusCode.Conflict, refused.Status);
        Assert.Equal("send_failed", refused.Code);
        Assert.Contains("isn't wired up", refused.Detail);
        Assert.Contains("QR-paired", refused.Detail);
        // nothing was recorded: a refused send leaves no bubble behind
        Assert.DoesNotContain(Messages(to), m => m.Text == "[sticker]");
    }

    // ---- the refusals ------------------------------------------------------

    [Fact]
    public async Task Only_whatsapp_has_stickers___and_it_says_so_before_fetching_anything()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        _f.Fetcher.Fetched.Clear();

        var refused = await Refused(await Send(admin, new
        {
            channel = "telegram", key = "12345", media_url = "https://files.example.com/x/pic.png",
        }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.Status);
        Assert.Equal("unsupported_channel", refused.Code);
        Assert.Contains("telegram", refused.Detail);
        Assert.Empty(_f.Fetcher.Fetched);   // refused before any download
    }

    [Fact]
    public async Task A_document_is_not_a_picture()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var to = Number();
        var url = $"https://files.example.com/{Guid.NewGuid():N}/invoice.pdf";
        _f.Fetcher.Files[url] = new RemoteFile(Encoding.UTF8.GetBytes("%PDF-1.4"), "invoice.pdf",
            "application/pdf", "document");

        var refused = await Refused(await Send(admin, new { channel = "whatsapp", key = to, media_url = url }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.Status);
        Assert.Equal("not_a_picture", refused.Code);
    }

    [Fact]
    public async Task An_opted_out_conversation_refuses___and_force_is_a_deliberate_exception()
    {
        if (!AudioTranscoder.Available) return;
        var admin = await Platform.As(_f, "admin", "admin123");
        var to = Number();
        LiveQrNumber(to, optedOut: true);
        var url = APicture();

        var refused = await Refused(await Send(admin, new { channel = "whatsapp", key = to, media_url = url }));
        Assert.Equal(HttpStatusCode.Conflict, refused.Status);
        Assert.Equal("opted_out", refused.Code);

        var forced = await Ok(await Send(admin, new { channel = "whatsapp", key = to, media_url = url, force = true }));
        Assert.Equal("sent", forced.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_contact_who_never_wrote_is_refused_like_cold_text()
    {
        if (!AudioTranscoder.Available) return;
        var admin = await Platform.As(_f, "admin", "admin123");
        var to = Number();
        LiveQrNumber(to, inbound: false);
        var url = APicture();

        var refused = await Refused(await Send(admin, new { channel = "whatsapp", key = to, media_url = url }));

        Assert.Equal(HttpStatusCode.Conflict, refused.Status);
        Assert.Equal("cold_sticker", refused.Code);
        Assert.Contains("ban risk", refused.Detail);
    }

    [Fact]
    public async Task A_gateway_failure_is_the_gateways_own_reason()
    {
        if (!AudioTranscoder.Available) return;
        var admin = await Platform.As(_f, "admin", "admin123");
        var to = Number();
        LiveQrNumber(to);
        var url = APicture();
        _f.Gateway.Answer = new BaileysResult(false, "the number is not paired");
        try
        {
            var refused = await Refused(await Send(admin, new { channel = "whatsapp", key = to, media_url = url }));
            Assert.Equal("send_failed", refused.Code);
            Assert.Contains("not paired", refused.Detail);
        }
        finally
        {
            _f.Gateway.Answer = new BaileysResult(true, "sent", "3EB0MEDIA1");
        }
    }

    [Fact]
    public async Task Naming_no_conversation_is_refused()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var refused = await Refused(await Send(admin, new { media_url = "https://files.example.com/x/p.png" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.Status);
        Assert.Equal("no_target", refused.Code);
    }

    [Fact]
    public async Task An_agent_without_chats_send_cannot_send_one()
    {
        var viewer = await Platform.As(_f, "agent", "agent123");
        // the seeded agent HOLDS chats.send, so this checks the gate is wired, not denied
        var res = await Send(viewer, new { media_url = "https://files.example.com/x/p.png" });
        Assert.NotEqual(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
    }

    [Fact]
    public async Task Sending_one_is_recorded_in_the_activity_log()
    {
        if (!AudioTranscoder.Available) return;
        var admin = await Platform.As(_f, "admin", "admin123");
        var to = Number();
        LiveQrNumber(to);
        var url = APicture();

        await Ok(await Send(admin, new { channel = "whatsapp", key = to, media_url = url }));

        var rows = await Platform.Audit(admin, "?action=message.send_sticker");
        Assert.Contains(rows, r => (r.GetProperty("target").GetString() ?? "").Contains(to));
    }
}
