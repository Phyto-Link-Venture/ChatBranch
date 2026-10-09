using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Flows;
using ChatBranch.Data.Sidecars;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Tests;

/// <summary>A Baileys gateway that supports media: records each send-media call, answers as scripted.</summary>
internal sealed class MediaBaileys : IBaileysGateway
{
    public sealed record MediaCall(string Account, string To, string Type, byte[] Content, string Mime, string FileName,
        string? Caption);

    public List<MediaCall> Media { get; } = new();
    public BaileysResult Answer { get; set; } = new(true, "sent", "3EB0MEDIA1");
    public bool IsConfigured => true;
    public string BaseUrl => "http://gateway.test";

    public Task<BaileysResult> SendTextAsync(string a, string to, string text, CancellationToken ct = default) =>
        Task.FromResult(new BaileysResult(true, "sent", "3EB0TEXT"));

    public Task<BaileysResult> SendMediaAsync(string a, string to, string type, byte[] content, string mime,
        string fileName, string? caption, CancellationToken ct = default)
    {
        Media.Add(new MediaCall(a, to, type, content, mime, fileName, caption));
        return Task.FromResult(Answer);
    }

    public Task<BaileysResult> StartCallAsync(string a, string to, Dictionary<string, object?> d,
        CancellationToken ct = default) => Task.FromResult(new BaileysResult(false, "no calls here"));
    public Task<BaileysResult> EndCallAsync(string a, CancellationToken ct = default) =>
        Task.FromResult(new BaileysResult(true, "ended"));
    private static readonly BaileysStatus Connected = new("connected", null, "me", null);
    public Task<BaileysStatus> StatusAsync(string a, CancellationToken ct = default) => Task.FromResult(Connected);
    public Task<BaileysStatus> StartAsync(string a, CancellationToken ct = default) => Task.FromResult(Connected);
    public Task<BaileysStatus> LogoutAsync(string a, CancellationToken ct = default) => Task.FromResult(Connected);
}

/// <summary>The Node gateway at the HTTP level: records requests, answers as scripted.</summary>
internal sealed class GatewayStub : HttpMessageHandler
{
    public sealed record Seen(string Method, string Url, string Secret, string Body);
    public List<Seen> Requests { get; } = new();
    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ =>
        new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"id":"3EB0GW"}""") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
        Requests.Add(new Seen(request.Method.Method, request.RequestUri!.AbsoluteUri,
            request.Headers.TryGetValues("X-Baileys-Secret", out var v) ? v.First() : "", body));
        return Respond(request);
    }
}

/// <summary>One part of a captured multipart request.</summary>
internal sealed record FormPart(string? FileName, string? ContentType, byte[] Bytes)
{
    public string Text => Encoding.UTF8.GetString(Bytes);
}

/// <summary>
/// The transport gaps closed on <c>feat/gaps-media</c>: Baileys outbound media
/// through the gateway, Messenger / Instagram file uploads (multipart Send API),
/// Meta's Messenger / Instagram messaging window (24h free · 7 days for a
/// person with HUMAN_AGENT · nothing after) across the send seam, the AI gate
/// and the queue, and the Telegram typing indicator.
/// </summary>
public class GapsMediaTests : IDisposable
{
    private readonly TestDb _t = new();
    private readonly FakeGraph _graph = new();
    private readonly MediaBaileys _baileys = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"cb-gaps-{Guid.NewGuid():N}");
    private readonly ScopingService _sc;
    private readonly ThreadStore _threads;
    private readonly MediaStore _media;
    private readonly OutboundSender _sender;
    private readonly List<Dictionary<string, FormPart>> _uploads = new();
    private int _mid;

    public GapsMediaTests()
    {
        _sc = new ScopingService(_t.Db);
        _threads = new ThreadStore(_t.Db, _sc);
        _media = new MediaStore(_dir);
        var http = new FakeHttpFactory(_graph);
        _sender = new OutboundSender(_t.Db, _threads, new WhatsAppCloudClient(http), _baileys,
            new MetaMessagingClient(http), _media);
        // the Send API accepts everything with a fresh mid (the store dedups on it);
        // multipart bodies are captured part by part
        _graph.On("POST", @"/me/messages$", req =>
        {
            if (req.Content is MultipartFormDataContent form) _uploads.Add(Parts(form));
            return JsonResponse(new { recipient_id = "x", message_id = $"mid.out.{Interlocked.Increment(ref _mid)}" });
        });
    }

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static HttpResponseMessage JsonResponse(object body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    private static Dictionary<string, FormPart> Parts(MultipartFormDataContent form)
    {
        var parts = new Dictionary<string, FormPart>();
        foreach (var part in form)
        {
            var cd = part.Headers.ContentDisposition!;
            parts[(cd.Name ?? "").Trim('"')] = new FormPart(cd.FileName?.Trim('"'),
                part.Headers.ContentType?.MediaType, part.ReadAsByteArrayAsync().GetAwaiter().GetResult());
        }
        return parts;
    }

    private ChannelAccount Add(ChannelAccount a)
    {
        a.CreatedAt = DateTime.UtcNow;
        _t.Db.ChannelAccounts.Add(a);
        _t.Db.SaveChanges();
        return a;
    }

    private ChannelAccount Page(string channel = "facebook", string id = "PAGE1") =>
        Add(new ChannelAccount { Channel = channel, Name = $"{channel} {id}", ExternalId = id, AccessToken = $"tok-{id}" });

    /// <summary>A social conversation whose customer last wrote <paramref name="ago"/> ago (null = never).</summary>
    private Thread Social(ChannelAccount? page, string key, TimeSpan? ago, string channel = "facebook")
    {
        var t = _threads.GetOrCreateThread(channel, key, accountId: page?.Id);
        if (ago is TimeSpan since)
            _threads.AppendMessage(t, MessageDirection.In, MessageSource.Customer, "hello?",
                externalId: $"mid.in.{key}", accountId: page?.Id, providerTs: DateTime.UtcNow - since);
        return t;
    }

    private List<FakeGraph.Seen> Sends => _graph.Requests.Where(r => r.Url.EndsWith("/me/messages")).ToList();

    private static JsonElement Json(FakeGraph.Seen s) => JsonSerializer.Deserialize<JsonElement>(s.Body);

    // ---- Baileys outbound media -----------------------------------------------------------

    [Fact]
    public async Task Baileys_media_goes_out_through_the_gateway_and_records_the_message_id()
    {
        var qr = Add(new ChannelAccount { Channel = "whatsapp", Provider = Providers.Baileys, Name = "QR", ExternalId = "gw-1" });
        var thread = _threads.GetOrCreateThread("whatsapp", "60123456789", accountId: qr.Id);
        var bytes = Encoding.ASCII.GetBytes("JPEG-BYTES");
        var rel = await _media.SaveAsync("whatsapp", thread.Key, bytes, ".jpg");

        var msg = _sender.SendMedia(thread, rel, "image", "image/jpeg", "Our menu", "menu.jpg", MessageSource.Agent);

        Assert.Equal(("sent", "3EB0MEDIA1", "Our menu"), (msg.Status, msg.ExternalId, msg.Text));
        Assert.Equal(("image", rel, qr.Id), (msg.MediaType, msg.MediaPath, msg.AccountId));
        var call = Assert.Single(_baileys.Media);
        Assert.Equal(("gw-1", "60123456789", "image", "image/jpeg", "menu.jpg", "Our menu"),
            (call.Account, call.To, call.Type, call.Mime, call.FileName, call.Caption));
        Assert.Equal(bytes, call.Content);

        // a gateway refusal is a failed row carrying the reason, like a text send
        _baileys.Answer = new BaileysResult(false, "gateway 502: account gw-1 is not connected");
        var failed = _sender.SendMedia(thread, rel, "image", "image/jpeg", null, "menu.jpg", MessageSource.Agent);
        Assert.Equal("failed", failed.Status);
        Assert.Contains("not connected", failed.Text);
        Assert.Equal(rel, failed.MediaPath); // the file stays viewable in the inbox

        // no gateway attached (sandbox) → stored locally, never sent
        var sandboxSender = new OutboundSender(_t.Db, _threads, new WhatsAppCloudClient(new FakeHttpFactory(_graph)),
            new SandboxBaileysGateway(), null, _media);
        Assert.Equal("sandbox",
            sandboxSender.SendMedia(thread, rel, "image", "image/jpeg", null, null, MessageSource.Agent).Status);
        Assert.Equal(2, _baileys.Media.Count);
    }

    [Fact]
    public async Task Http_gateway_posts_the_file_as_base64_with_the_secret_and_reports_refusals()
    {
        _sc.SetSetting("baileys_url", "http://gw.test/");
        _sc.SetSetting("baileys_secret", "s3cret");
        var services = new ServiceCollection();
        services.AddScoped(_ => new ScopingService(_t.Db));
        await using var sp = services.BuildServiceProvider();
        var stub = new GatewayStub();
        var gw = new HttpBaileysGateway(new FakeHttpFactory(stub), sp.GetRequiredService<IServiceScopeFactory>());
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.4 fake");

        var ok = await gw.SendMediaAsync("gw 1", "60123", "document", pdf, "application/pdf; q=1", "Invoice.pdf", "Your invoice");

        Assert.Equal((true, "3EB0GW"), (ok.Ok, ok.ExternalId));
        var req = Assert.Single(stub.Requests);
        Assert.Equal(("POST", "http://gw.test/accounts/gw%201/send-media", "s3cret"), (req.Method, req.Url, req.Secret));
        var body = JsonSerializer.Deserialize<JsonElement>(req.Body);
        Assert.Equal("60123", body.GetProperty("to").GetString());
        Assert.Equal("document", body.GetProperty("media_type").GetString());
        Assert.Equal(pdf, Convert.FromBase64String(body.GetProperty("file_b64").GetString()!));
        Assert.Equal("application/pdf", body.GetProperty("mime").GetString());
        Assert.Equal("Invoice.pdf", body.GetProperty("filename").GetString());
        Assert.Equal("Your invoice", body.GetProperty("caption").GetString());
        Assert.False(body.TryGetProperty("path", out _)); // never a server path

        // the gateway's {detail} comes back as the reason
        stub.Respond = _ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("""{"detail":"account gw 1 is not connected (state: stopped)"}"""),
        };
        var refused = await gw.SendMediaAsync("gw 1", "60123", "image", pdf, "image/png", "a.png", null);
        Assert.False(refused.Ok);
        Assert.Contains("not connected", refused.Detail);

        // over WhatsApp's 16 MB, or not a WhatsApp media type: refused before any request
        var before = stub.Requests.Count;
        Assert.False((await gw.SendMediaAsync("g", "1", "video", new byte[BaileysMedia.MaxBytes + 1], "video/mp4", "v.mp4", null)).Ok);
        Assert.False((await gw.SendMediaAsync("g", "1", "sticker", pdf, "image/webp", "s.webp", null)).Ok);
        Assert.Equal(before, stub.Requests.Count);

        // audio carries no caption on WhatsApp
        Assert.False(BaileysMedia.Payload("1", "audio", pdf, "audio/ogg", "a.ogg", "hi").ContainsKey("caption"));
    }

    // ---- Messenger / Instagram file upload ------------------------------------------------

    [Fact]
    public void Messenger_sends_a_stored_file_as_a_multipart_upload_and_the_caption_follows_as_text()
    {
        var page = Page();
        var thread = Social(page, "PSID-9", TimeSpan.FromHours(1));
        var bytes = Encoding.ASCII.GetBytes("PNG-BYTES-123");
        var rel = MessagingMedia.Save(_dir, "facebook", thread.Key, "photo.png", bytes);

        var msg = _sender.SendMedia(thread, rel, "image", "image/png", null, "photo.png", MessageSource.Agent,
            humanAgent: true);

        Assert.Equal(("sent", "mid.out.1", "[image]", "image", rel), (msg.Status, msg.ExternalId, msg.Text,
            msg.MediaType, msg.MediaPath));
        var post = Assert.Single(Sends);
        Assert.Equal("Bearer tok-PAGE1", post.Auth);
        Assert.Equal("multipart/form-data", post.ContentType);
        var form = Assert.Single(_uploads);
        Assert.Equal("PSID-9", JsonSerializer.Deserialize<JsonElement>(form["recipient"].Text).GetProperty("id").GetString());
        Assert.Equal("RESPONSE", form["messaging_type"].Text);
        Assert.False(form.ContainsKey("tag"));
        var attachment = JsonSerializer.Deserialize<JsonElement>(form["message"].Text).GetProperty("attachment");
        Assert.Equal("image", attachment.GetProperty("type").GetString());
        Assert.False(attachment.GetProperty("payload").GetProperty("is_reusable").GetBoolean());
        Assert.Equal(("photo.png", "image/png"), (form["filedata"].FileName, form["filedata"].ContentType));
        Assert.Equal(bytes, form["filedata"].Bytes);

        // Messenger has no captions: a document goes up as "file", its caption as the next message
        var docRel = MessagingMedia.Save(_dir, "facebook", thread.Key, "terms.pdf", Encoding.ASCII.GetBytes("%PDF"));
        var caption = _sender.SendMedia(thread, docRel, "document", "application/pdf", "Our terms", "terms.pdf",
            MessageSource.Agent);
        Assert.Equal(("sent", "Our terms"), (caption.Status, caption.Text));
        Assert.Equal("file", JsonSerializer.Deserialize<JsonElement>(_uploads[1]["message"].Text)
            .GetProperty("attachment").GetProperty("type").GetString());
        var docRow = _t.Db.Messages.Single(m => m.MediaPath == docRel);
        Assert.Equal(("[document]", "sent"), (docRow.Text, docRow.Status));
        Assert.Equal("Our terms", Json(Sends.Last()).GetProperty("message").GetProperty("text").GetString());
        Assert.Equal(3, Sends.Count);

        // an unconfigured Instagram account keeps the file locally (sandbox)
        var bare = Social(null, "IGSID-1", TimeSpan.FromHours(1), "instagram");
        var igRel = MessagingMedia.Save(_dir, "instagram", bare.Key, "x.png", bytes);
        Assert.Equal("sandbox", _sender.SendMedia(bare, igRel, "image", "image/png", null, "x.png",
            MessageSource.Agent).Status);
        Assert.Equal(3, Sends.Count);
    }

    // ---- Messenger / Instagram messaging window -------------------------------------------

    [Fact]
    public void Inside_24h_anything_goes_out_as_a_RESPONSE()
    {
        var page = Page();
        var thread = Social(page, "PSID-A", TimeSpan.FromHours(23));

        Assert.Null(_sender.SocialWindowBlock(thread, null, humanAgent: false));
        var ai = _sender.Send(thread, "Here's our menu", MessageSource.Ai);
        Assert.Equal("sent", ai.Status);
        var body = Json(Sends.Single());
        Assert.Equal("RESPONSE", body.GetProperty("messaging_type").GetString());
        Assert.False(body.TryGetProperty("tag", out _));
    }

    [Fact]
    public void Between_24h_and_7_days_only_a_person_may_reply_with_the_HUMAN_AGENT_tag()
    {
        var page = Page();
        var thread = Social(page, "PSID-B", TimeSpan.FromHours(30));

        // automated (AI, flows, the queue) — refused, Meta never called
        var block = _sender.SocialWindowBlock(thread, null, humanAgent: false);
        Assert.Contains("24-hour window", block);
        var auto = _sender.Send(thread, "Following up!", MessageSource.Ai);
        Assert.Equal("failed", auto.Status);
        Assert.StartsWith("window_closed:", auto.Error);
        Assert.Empty(Sends);

        // a person: tagged HUMAN_AGENT — text, a URL attachment and an uploaded file alike
        Assert.Null(_sender.SocialWindowBlock(thread, null, humanAgent: true));
        var human = _sender.SendSocialText(thread, "Sorry for the wait — yes, it's in stock.", MessageSource.Agent,
            null, humanAgent: true);
        Assert.Equal("sent", human.Status);
        var text = Json(Sends.Single());
        Assert.Equal(("MESSAGE_TAG", "HUMAN_AGENT"),
            (text.GetProperty("messaging_type").GetString(), text.GetProperty("tag").GetString()));

        _sender.SendMediaUrl(thread, "image", "https://cdn.example.test/p.jpg", MessageSource.Agent, null,
            humanAgent: true);
        Assert.Equal("HUMAN_AGENT", Json(Sends.Last()).GetProperty("tag").GetString());

        var rel = MessagingMedia.Save(_dir, "facebook", thread.Key, "p.jpg", new byte[] { 1, 2, 3 });
        Assert.Equal("sent", _sender.SendMedia(thread, rel, "image", "image/jpeg", null, "p.jpg",
            MessageSource.Agent, humanAgent: true).Status);
        Assert.Equal(("MESSAGE_TAG", "HUMAN_AGENT"),
            (_uploads.Single()["messaging_type"].Text, _uploads.Single()["tag"].Text));

        // the same file without a person behind it is refused
        Assert.Equal("failed", _sender.SendMedia(thread, rel, "image", "image/jpeg", null, "p.jpg",
            MessageSource.System).Status);
        Assert.Equal(3, Sends.Count);
    }

    [Fact]
    public void Beyond_7_days_or_before_the_customer_writes_nothing_goes_out()
    {
        var ig = Page("instagram", "IG-1");
        var stale = Social(ig, "IGSID-old", TimeSpan.FromDays(8), "instagram");
        var never = Social(ig, "IGSID-new", null, "instagram");

        Assert.Contains("7-day", _sender.SocialWindowBlock(stale, null, humanAgent: true));
        Assert.Contains("Instagram", _sender.SocialWindowBlock(stale, null, humanAgent: true));
        Assert.Contains("after the customer has written", _sender.SocialWindowBlock(never, null, humanAgent: true));
        Assert.Equal("failed", _sender.SendSocialText(stale, "hi", MessageSource.Agent, null, humanAgent: true).Status);
        Assert.Equal("failed", _sender.SendSocialText(never, "hi", MessageSource.Agent, null, humanAgent: true).Status);
        Assert.Empty(Sends);

        // a sandbox page has no window to respect — it never dead-ends
        var sandbox = Social(null, "PSID-sandbox", TimeSpan.FromDays(30));
        Assert.Null(_sender.SocialWindowBlock(sandbox, null, humanAgent: false));
        Assert.Equal("sandbox", _sender.Send(sandbox, "hi", MessageSource.Ai).Status);
        // …and WhatsApp / email are not this rule's business
        Assert.Null(_sender.SocialWindowBlock(_threads.GetOrCreateThread("email", "a@b.test"), null, false));
    }

    [Fact]
    public void Window_fields_read_the_same_rule_for_payloads_and_lists()
    {
        var now = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
        DateTime Ago(double hours) => now.AddHours(-hours);

        Assert.Equal(SocialWindowState.Open, SocialWindow.StateAt(Ago(23.9), now));
        Assert.Equal(SocialWindowState.HumanAgentOnly, SocialWindow.StateAt(Ago(24), now));
        Assert.Equal(SocialWindowState.HumanAgentOnly, SocialWindow.StateAt(Ago(7 * 24 - 0.1), now));
        Assert.Equal(SocialWindowState.Closed, SocialWindow.StateAt(Ago(7 * 24), now));
        Assert.Equal(SocialWindowState.Closed, SocialWindow.StateAt(null, now));
        Assert.Null(SocialWindow.Tag(SocialWindowState.Open, humanAgent: true));
        Assert.Null(SocialWindow.Tag(SocialWindowState.HumanAgentOnly, humanAgent: false));
        Assert.Equal("HUMAN_AGENT", SocialWindow.Tag(SocialWindowState.HumanAgentOnly, humanAgent: true));

        var mid = SocialWindow.Fields(live: true, Ago(30), now);
        Assert.Equal((false, true), ((bool)mid["window_open"]!, (bool)mid["human_window_open"]!));
        Assert.Equal(Ago(6).ToString("o"), mid["window_expires_at"]);
        Assert.Equal(Ago(30).AddDays(7).ToString("o"), mid["human_window_expires_at"]);
        var sandbox = SocialWindow.Fields(live: false, null, now);
        Assert.Equal((true, true), ((bool)sandbox["window_open"]!, (bool)sandbox["human_window_open"]!));
        Assert.Equal((false, (string?)null), SocialWindow.ListWindow(true, null, now));
        Assert.True(SocialWindow.ListWindow(true, Ago(1), now).Open);
        Assert.True(SocialWindow.ListWindow(false, Ago(100), now).Open);
    }

    [Fact]
    public void Queued_and_AI_sends_outside_24h_fail_with_the_reason_and_inside_it_go_out()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var llm = new ScriptedReplyLlm();
        var assistant = new AssistantService(_t.Db, _sc, new PersonaService(_sc), new KnowledgeService(_t.Db, _sc), llm);
        var gate = new AiGate(_t.Db, _sc, _threads, _sender, clock);
        var queue = new QueueService(_t.Db, _sc, _threads, assistant, _sender, gate: gate, clock: clock);
        foreach (var (k, v) in new[] { ("ai_review_mode", "auto"), ("ai_review_first_n", "0"),
                     ("ai_review_notify", "false"), ("ai_typing", "false"), ("ai_split", "off") })
            _sc.SetSetting(k, v);
        var contact = new Contact { Name = "Nora", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _t.Db.Contacts.Add(contact);
        _t.Db.SaveChanges();

        // a flow's text step, 30h after the customer's last message
        var page = Page("facebook", "PAGE-Q");
        var stale = Social(page, "PSID-Q", TimeSpan.FromHours(30));
        var job = queue.Enqueue("facebook", JobKind.Text, stale.Key, new() { ["text"] = "Still interested?" },
            accountId: page.Id);
        queue.Drain();
        var failed = _t.Fresh().OutboundJobs.Single(j => j.Id == job.Id);
        Assert.Equal(JobStatus.Failed, failed.Status);
        Assert.Contains("24-hour window", failed.LastError);

        // the AI's reply on that conversation is blocked by the gate (draft kept, marked failed)
        stale.ContactId = contact.Id;
        AiEligibility.Set(stale, true);
        _t.Db.SaveChanges();
        var outcome = gate.Submit(new AiSubmission(stale, AiDraftKind.Reply, "Yes — still available!"));
        Assert.Equal(AiGateStatus.Blocked, outcome.Status);
        Assert.Contains("24-hour window", outcome.Detail);
        Assert.Equal(AiDraftStatus.Failed, _t.Fresh().AiDrafts.Single(d => d.Id == outcome.DraftId).Status);
        Assert.Empty(Sends);

        // inside the window the same job goes out as a plain RESPONSE
        var fresh = Social(Page("facebook", "PAGE-R"), "PSID-R", TimeSpan.FromHours(2));
        var ok = queue.Enqueue("facebook", JobKind.Text, fresh.Key, new() { ["text"] = "Your order shipped" },
            accountId: fresh.AccountId);
        queue.Drain();
        Assert.Equal(JobStatus.Sent, _t.Fresh().OutboundJobs.Single(j => j.Id == ok.Id).Status);
        Assert.Equal("RESPONSE", Json(Sends.Single()).GetProperty("messaging_type").GetString());
    }

    // ---- Telegram typing ------------------------------------------------------------------

    [Fact]
    public async Task Telegram_typing_is_sendChatAction_from_the_bot_and_never_throws()
    {
        var api = new FakeTelegramApi();
        var client = new TelegramClient(api, "https://tg.test");
        var bot = Add(new ChannelAccount { Channel = "telegram", Name = "bot", AccessToken = "123:typing-token" });
        var chat = _threads.GetOrCreateThread("telegram", "555001", accountId: bot.Id);
        var typing = new TelegramTypingIndicator(client);

        Assert.True(await typing.StartAsync(chat, bot));
        var call = Assert.Single(api.Calls);
        Assert.Equal(("123:typing-token", "sendChatAction"), (call.Token, call.Method));
        var body = JsonSerializer.Deserialize<JsonElement>(call.Body);
        Assert.Equal(("555001", "typing"), (body.GetProperty("chat_id").GetString(), body.GetProperty("action").GetString()));

        // a blocked bot / a dead network: false, never an exception into the send path
        api.Respond = (_, _, _) => FakeTelegramApi.Error(HttpStatusCode.Forbidden, "bot was blocked by the user");
        Assert.False(await typing.StartAsync(chat, bot));
        // no token, or not a Telegram chat: nothing is called
        api.Reset();
        Assert.False(await typing.StartAsync(chat, new ChannelAccount { Channel = "telegram", Name = "sandbox" }));
        Assert.False(await typing.StartAsync(_threads.GetOrCreateThread("whatsapp", "60111"), bot));
        Assert.Empty(api.Calls);

        // the registered dispatcher: Telegram → sendChatAction, WhatsApp → its own indicator, unchanged
        var whatsapp = new RecordingTyping();
        var dispatcher = new ChannelTypingIndicator(whatsapp, typing);
        dispatcher.Start(chat, bot);
        dispatcher.Start(_threads.GetOrCreateThread("whatsapp", "60111"), bot);
        Assert.Equal(1, whatsapp.Count);
        await Eventually(() => api.Calls.Any(c => c.Method == "sendChatAction"));

        // end to end: the AI's human-like send shows "typing…" on a live Telegram bot
        api.Reset();
        _sc.SetSetting("ai_typing", "true");
        var tgSender = new OutboundSender(_t.Db, _threads, new WhatsAppCloudClient(new FakeHttpFactory(_graph)),
            new SandboxBaileysGateway(), telegram: new TelegramChannel(_t.Db, _sc, _threads, client));
        var gate = new AiGate(_t.Db, _sc, _threads, tgSender, new ManualClock(DateTimeOffset.UtcNow), dispatcher);
        var policy = AiBehaviourSettings.Resolve(_sc, null, bot);
        Assert.True(policy.TypingEnabled);
        var start = gate.StartHumanSend(chat, "One moment, checking that for you.", MessageSource.Ai, bot, policy,
            humanize: true, new Dictionary<string, object?>());
        Assert.Null(start.MessageId); // scheduled after the typing pause
        await Eventually(() => api.Calls.Any(c => c.Method == "sendChatAction" && c.Body.Contains("555001")));
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition());
    }
}

/// <summary>
/// The same gaps over HTTP: the inbox send / send-media routes enforce the
/// Messenger window (409 + the reason; a person gets HUMAN_AGENT, an API key
/// does not), the thread payload and inbox list carry it, Messenger accepts a
/// file upload, and the host registers the per-channel typing dispatcher.
/// </summary>
public class GapsMediaApiTests : IClassFixture<MetaApiFactory>
{
    private readonly MetaApiFactory _f;
    private FakeGraph Graph => _f.Graph;
    private int _mid;

    public GapsMediaApiTests(MetaApiFactory f)
    {
        _f = f;
        Graph.On("POST", @"/me/messages$", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                message_id = $"mid.api.{Guid.NewGuid():N}{Interlocked.Increment(ref _mid)}",
            }), Encoding.UTF8, "application/json"),
        });
    }

    /// <summary>A live page + a conversation whose customer wrote <paramref name="ago"/> ago.</summary>
    private void Seed(string pageId, string psid, TimeSpan ago)
    {
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<ThreadStore>();
        var page = new ChannelAccount
        {
            Channel = "facebook", Name = $"Page {pageId}", ExternalId = pageId, AccessToken = $"tok-{pageId}",
            CreatedAt = DateTime.UtcNow,
        };
        db.ChannelAccounts.Add(page);
        db.SaveChanges();
        var t = store.GetOrCreateThread("facebook", psid, "Customer", accountId: page.Id);
        store.AppendMessage(t, MessageDirection.In, MessageSource.Customer, "Is this available?",
            externalId: $"mid.in.{psid}", accountId: page.Id, providerTs: DateTime.UtcNow - ago);
    }

    private List<FakeGraph.Seen> SendsBy(string pageId) =>
        Graph.Requests.Where(r => r.Url.EndsWith("/me/messages") && r.Auth == $"Bearer tok-{pageId}").ToList();

    [Fact]
    public async Task Inbox_send_respects_the_Messenger_window_and_tags_a_persons_late_reply()
    {
        Seed("PG-LATE", "PSID-LATE", TimeSpan.FromHours(30));
        Seed("PG-OLD", "PSID-OLD", TimeSpan.FromDays(9));
        var admin = await Platform.As(_f, "admin", "admin123");

        // a person, 30h later: sent with HUMAN_AGENT
        var res = await admin.PostAsJsonAsync("/api/messaging/threads/facebook/PSID-LATE/send",
            new { text = "So sorry for the slow reply — yes!" });
        res.EnsureSuccessStatusCode();
        Assert.Equal("sent", (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message")
            .GetProperty("status").GetString());
        var sent = JsonSerializer.Deserialize<JsonElement>(Assert.Single(SendsBy("PG-LATE")).Body);
        Assert.Equal(("MESSAGE_TAG", "HUMAN_AGENT"),
            (sent.GetProperty("messaging_type").GetString(), sent.GetProperty("tag").GetString()));

        // an API key is automation: no tag, so 409 with the reason
        var key = _f.CreateClient();
        key.DefaultRequestHeaders.Add("X-Api-Key", Platform.MintKey(_f, "admin"));
        var refused = await key.PostAsJsonAsync("/api/messaging/threads/facebook/PSID-LATE/send", new { text = "bot" });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("24-hour window", (await refused.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("detail").GetString());

        // 9 days: nobody
        var old = await admin.PostAsJsonAsync("/api/messaging/threads/facebook/PSID-OLD/send", new { text = "hi" });
        Assert.Equal(HttpStatusCode.Conflict, old.StatusCode);
        Assert.Contains("7-day", (await old.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
        Assert.Single(SendsBy("PG-LATE"));
        Assert.Empty(SendsBy("PG-OLD"));

        // the thread payload and the inbox list carry the window
        var thread = await admin.GetFromJsonAsync<JsonElement>("/api/messaging/threads/facebook/PSID-LATE");
        Assert.False(thread.GetProperty("window_open").GetBoolean());
        Assert.True(thread.GetProperty("human_window_open").GetBoolean());
        Assert.False((await admin.GetFromJsonAsync<JsonElement>("/api/messaging/threads/facebook/PSID-OLD"))
            .GetProperty("human_window_open").GetBoolean());
        var closed = await admin.GetFromJsonAsync<JsonElement>("/api/inbox/threads?channel=facebook&window=closed");
        var keys = closed.GetProperty("threads").EnumerateArray().Select(r => r.GetProperty("key").GetString()).ToList();
        Assert.Contains("PSID-LATE", keys);
        Assert.Contains("PSID-OLD", keys);
    }

    [Fact]
    public async Task Messenger_send_media_uploads_the_file_and_honours_opt_out()
    {
        Seed("PG-FILE", "PSID-FILE", TimeSpan.FromMinutes(10));
        var admin = await Platform.As(_f, "admin", "admin123");

        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.ASCII.GetBytes("JPEGDATA"));
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "file", "shoe.jpg");
        var res = await admin.PostAsync("/api/messaging/threads/facebook/PSID-FILE/send-media", form);
        res.EnsureSuccessStatusCode();
        var msg = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message");
        Assert.Equal("sent", msg.GetProperty("status").GetString());
        Assert.Equal("image", msg.GetProperty("media").GetProperty("type").GetString());
        var upload = Assert.Single(SendsBy("PG-FILE"));
        Assert.Equal("multipart/form-data", upload.ContentType);
        Assert.Contains("JPEGDATA", upload.Body);
        Assert.Contains("filedata", upload.Body);

        // the file is served back from the inbox's media route
        var id = Platform.WithDb(_f, db => db.Messages.Single(m => m.ExternalId == msg.GetProperty("id").GetString()).Id);
        Assert.Equal("JPEGDATA", await admin.GetStringAsync($"/api/messaging/media/{id}"));

        // STOP is honoured here too: a manual exception must say force
        Platform.WithDb(_f, db =>
        {
            db.Threads.Single(t => t.Channel == "facebook" && t.Key == "PSID-FILE").OptedOut = true;
            return db.SaveChanges();
        });
        var again = new MultipartFormDataContent { { new ByteArrayContent(new byte[] { 1 }), "file", "a.jpg" } };
        Assert.Equal(HttpStatusCode.Conflict,
            (await admin.PostAsync("/api/messaging/threads/facebook/PSID-FILE/send-media", again)).StatusCode);
    }

    [Fact]
    public async Task Baileys_media_to_a_contact_who_never_wrote_is_refused_like_cold_text()
    {
        Platform.WithDb(_f, db =>
        {
            var qr = new ChannelAccount
            {
                Channel = "whatsapp", Provider = Providers.Baileys, Name = "QR cold", ExternalId = "gw-cold",
                CreatedAt = DateTime.UtcNow,
            };
            db.ChannelAccounts.Add(qr);
            db.SaveChanges();
            db.Threads.Add(new Thread
            {
                Channel = "whatsapp", Key = "60199900001", Name = "Cold", AccountId = qr.Id,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            return db.SaveChanges();
        });
        var admin = await Platform.As(_f, "admin", "admin123");
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "flyer.png");

        var res = await admin.PostAsync("/api/whatsapp/threads/60199900001/send-media", form);

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Contains("never received a message", (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("detail").GetString());
        Assert.Equal(0, Platform.WithDb(_f, db => db.Messages.Count(m => m.Thread!.Key == "60199900001")));
    }

    [Fact]
    public void The_host_registers_the_per_channel_typing_dispatcher()
    {
        using var scope = _f.Services.CreateScope();
        Assert.IsType<ChannelTypingIndicator>(scope.ServiceProvider.GetRequiredService<ITypingIndicator>());
    }
}
