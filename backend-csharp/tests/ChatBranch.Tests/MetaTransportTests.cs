using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChatBranch.Data;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Flows;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Xunit;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Tests;

/// <summary>
/// A scripted graph.facebook.com (and Meta CDN): routes by method + URL regex,
/// records every request (auth header and body included). The LAST matching
/// route wins, so a test can override a default. Unrouted → a Graph error 100.
/// </summary>
internal sealed class FakeGraph : HttpMessageHandler
{
    public sealed record Seen(string Method, string Url, string Auth, string Body, string ContentType);

    private readonly object _gate = new();
    private readonly List<(string Method, Regex Url, Func<HttpRequestMessage, HttpResponseMessage> Respond)> _routes = new();
    private readonly List<Seen> _requests = new();

    public List<Seen> Requests { get { lock (_gate) return _requests.ToList(); } }

    public void On(string method, string urlPattern, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        lock (_gate) _routes.Add((method, new Regex(urlPattern), respond));
    }

    public void Json(string method, string urlPattern, object body, HttpStatusCode code = HttpStatusCode.OK) =>
        On(method, urlPattern, _ => new HttpResponseMessage(code)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        });

    public void Error(string method, string urlPattern, int code, string message, HttpStatusCode status = HttpStatusCode.BadRequest) =>
        Json(method, urlPattern, new { error = new { message, type = "OAuthException", code } }, status);

    public void Bytes(string urlPattern, byte[] bytes, string mime) =>
        On("GET", urlPattern, _ =>
        {
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mime);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
        var url = request.RequestUri!.ToString();
        Func<HttpRequestMessage, HttpResponseMessage>? respond = null;
        lock (_gate)
        {
            _requests.Add(new Seen(request.Method.Method, url, request.Headers.Authorization?.ToString() ?? "", body,
                request.Content?.Headers.ContentType?.MediaType ?? ""));
            for (var i = _routes.Count - 1; i >= 0; i--)
                if (_routes[i].Method == request.Method.Method && _routes[i].Url.IsMatch(url))
                {
                    respond = _routes[i].Respond;
                    break;
                }
        }
        return respond?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":{"message":"no fake route","code":100}}""", Encoding.UTF8,
                "application/json"),
        };
    }
}

internal sealed class FakeHttpFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

/// <summary>
/// The WhatsApp Cloud + Messenger/Instagram transports against a fake Graph: real
/// template messages (header / body / URL-button parameters, rendered text in the
/// inbox), the paged template registry, media upload → send and inbound download,
/// delivery statuses (forward-only, 131047 closes the window, account-level codes
/// move health and per-recipient codes never do), the Send API, the media store's
/// traversal guard and the call-recordings store.
/// </summary>
public class MetaTransportTests : IDisposable
{
    private readonly TestDb _t = new();
    private readonly FakeGraph _graph = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"cb-meta-{Guid.NewGuid():N}");
    private readonly ScopingService _sc;
    private readonly ThreadStore _threads;
    private readonly WhatsAppCloudClient _wa;
    private readonly MetaMessagingClient _meta;
    private readonly MediaStore _media;
    private readonly OutboundSender _sender;

    public MetaTransportTests()
    {
        _sc = new ScopingService(_t.Db);
        _threads = new ThreadStore(_t.Db, _sc);
        _wa = new WhatsAppCloudClient(new FakeHttpFactory(_graph));
        _meta = new MetaMessagingClient(new FakeHttpFactory(_graph));
        _media = new MediaStore(_dir);
        _sender = new OutboundSender(_t.Db, _threads, _wa, new ChatBranch.Data.Sidecars.SandboxBaileysGateway(),
            _meta, _media);
    }

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private ChannelAccount Cloud(string pnid, string waba = "")
    {
        var a = new ChannelAccount
        {
            Channel = "whatsapp", Provider = "cloud", Name = $"Biz {pnid}", ExternalId = pnid,
            AccessToken = $"tok-{pnid}", WabaId = waba, CreatedAt = DateTime.UtcNow,
        };
        _t.Db.ChannelAccounts.Add(a);
        _t.Db.SaveChanges();
        return a;
    }

    private Thread MakeThread(string channel, string key, ChannelAccount? account)
    {
        var th = _threads.GetOrCreateThread(channel, key, accountId: account?.Id);
        return th;
    }

    private static JsonElement BodyOf(FakeGraph.Seen s) => JsonSerializer.Deserialize<JsonElement>(s.Body);

    // ---- templates -----------------------------------------------------------------

    [Fact]
    public void Live_template_send_posts_a_real_template_message_and_stores_the_rendered_text()
    {
        var acct = Cloud("PN100", "WABA100");
        _graph.Json("GET", @"/WABA100/message_templates", new
        {
            data = new object[]
            {
                new
                {
                    name = "order_ready", language = "en_US", status = "APPROVED", category = "UTILITY", id = "T-9",
                    components = new object[]
                    {
                        new { type = "HEADER", format = "TEXT", text = "Order {{1}}" },
                        new { type = "BODY", text = "Hi {{1}}, order {{2}} is ready." },
                        new { type = "FOOTER", text = "Reply STOP to opt out" },
                        new
                        {
                            type = "BUTTONS",
                            buttons = new object[]
                            {
                                new { type = "QUICK_REPLY", text = "Thanks" },
                                new { type = "URL", text = "Track", url = "https://shop.test/t/{{1}}" },
                            },
                        },
                    },
                },
            },
        });
        _graph.Json("POST", @"/PN100/messages$", new { messages = new[] { new { id = "wamid.T1" } } });
        var thread = MakeThread("whatsapp", "60120000001", acct);

        var msg = _sender.SendTemplate(thread, "order_ready", "en_US",
            new TemplateArgs(new List<string> { "Aisha", "A-77" }, Buttons: new List<string> { "A-77-trk" }),
            MessageSource.Agent);

        Assert.Equal("sent", msg.Status);
        Assert.Equal("wamid.T1", msg.ExternalId);
        Assert.Equal("Order Aisha\nHi Aisha, order A-77 is ready.", msg.Text); // header + body, rendered
        var post = _graph.Requests.Single(r => r.Method == "POST" && r.Url.Contains("/PN100/messages"));
        Assert.Equal("Bearer tok-PN100", post.Auth);
        var body = BodyOf(post);
        Assert.Equal("template", body.GetProperty("type").GetString());
        Assert.Equal("60120000001", body.GetProperty("to").GetString());
        var tpl = body.GetProperty("template");
        Assert.Equal("order_ready", tpl.GetProperty("name").GetString());
        Assert.Equal("en_US", tpl.GetProperty("language").GetProperty("code").GetString());
        var comps = tpl.GetProperty("components").EnumerateArray().ToList();
        var header = comps.Single(c => c.GetProperty("type").GetString() == "header");
        Assert.Equal("Aisha", header.GetProperty("parameters")[0].GetProperty("text").GetString());
        var bodyParams = comps.Single(c => c.GetProperty("type").GetString() == "body").GetProperty("parameters");
        Assert.Equal(new[] { "Aisha", "A-77" }, bodyParams.EnumerateArray().Select(p => p.GetProperty("text").GetString()));
        var button = comps.Single(c => c.GetProperty("type").GetString() == "button");
        Assert.Equal("url", button.GetProperty("sub_type").GetString());
        Assert.Equal("1", button.GetProperty("index").GetString()); // the URL button is the 2nd button
        Assert.Equal("A-77-trk", button.GetProperty("parameters")[0].GetProperty("text").GetString());
        // the account just proved itself
        Assert.Equal(Health.Healthy, _t.Fresh().ChannelAccounts.Find(acct.Id)!.HealthStatus);
    }

    [Fact]
    public void A_queued_flow_template_job_goes_out_as_a_template_message()
    {
        var acct = Cloud("PN101"); // no WABA: the definition is unknown, so values ride as body params
        _graph.Json("POST", @"/PN101/messages$", new { messages = new[] { new { id = "wamid.Q1" } } });
        var thread = MakeThread("whatsapp", "60120000002", acct);
        var queue = new QueueService(_t.Db, _sc, _threads, new ChatBranch.Data.Ai.AssistantService(_t.Db, _sc,
            new ChatBranch.Data.Ai.PersonaService(_sc), new ChatBranch.Data.Ai.KnowledgeService(_t.Db, _sc),
            new ChatBranch.Data.Ai.SandboxLlmClient()), _sender);
        queue.Enqueue("whatsapp", JobKind.Template, thread.Key, new Dictionary<string, object?>
        {
            ["name"] = "payment_reminder", ["language"] = "ms",
            ["variables"] = "Ali, RM50", ["parameters"] = new List<string> { "Ali", "RM50" },
        }, accountId: acct.Id);

        Assert.Equal(1, queue.Drain());
        var tpl = BodyOf(_graph.Requests.Single(r => r.Url.Contains("/PN101/messages"))).GetProperty("template");
        Assert.Equal("payment_reminder", tpl.GetProperty("name").GetString());
        Assert.Equal("ms", tpl.GetProperty("language").GetProperty("code").GetString());
        Assert.Equal(new[] { "Ali", "RM50" }, tpl.GetProperty("components")[0].GetProperty("parameters")
            .EnumerateArray().Select(p => p.GetProperty("text").GetString()));
        var job = _t.Fresh().OutboundJobs.Single();
        Assert.Equal(JobStatus.Sent, job.Status);
    }

    [Fact]
    public void Template_registry_is_paged_and_parsed()
    {
        var acct = Cloud("PN102", "WABA102");
        _graph.Json("GET", @"/WABA102/message_templates\?fields", new
        {
            data = new[] { new { name = "first", language = "en", status = "APPROVED", category = "UTILITY", id = "1",
                components = new object[] { new { type = "BODY", text = "One {{1}}" } } } },
            paging = new { next = "https://graph.facebook.com/v23.0/WABA102/message_templates?after=CUR2" },
        });
        _graph.Json("GET", @"after=CUR2", new
        {
            data = new[] { new { name = "second", language = "ms", status = "PENDING", category = "MARKETING", id = "2",
                components = new object[] { new { type = "BODY", text = "Dua {{name}} {{code}}" } } } },
        });
        var catalog = new WhatsAppTemplateCatalog(_sc, _wa);
        var (live, error) = catalog.LiveAsync(acct, fresh: true).GetAwaiter().GetResult();
        Assert.Null(error);
        Assert.Equal(new[] { "first", "second" }, live.Select(t => t.Name));
        Assert.Equal("PENDING", live[1].Status);
        Assert.Equal(2, WhatsAppTemplateCatalog.VariableCount(live[1].Body)); // named placeholders
        Assert.Equal("Dua Siti X1", WhatsAppTemplateCatalog.Fill(live[1].Body, new List<string> { "Siti", "X1" }));
        // named templates send parameter_name
        var msg = WhatsAppTemplateCatalog.BuildMessage(live[1], "second", null, TemplateArgs.Of(new[] { "Siti", "X1" }));
        var json = JsonSerializer.SerializeToElement(msg);
        Assert.Equal("name", json.GetProperty("components")[0].GetProperty("parameters")[0]
            .GetProperty("parameter_name").GetString());
        Assert.Equal("ms", json.GetProperty("language").GetProperty("code").GetString());
    }

    [Fact]
    public void Template_args_read_every_payload_shape()
    {
        Assert.Equal(new[] { "a", "b" }, TemplateArgs.FromPayload(new Dictionary<string, object?>
            { ["parameters"] = new List<string> { "a", "b" }, ["variables"] = "a, b" }).Body);
        Assert.Equal(new[] { "x", "y" }, TemplateArgs.FromPayload(new Dictionary<string, object?>
            { ["variables"] = JsonSerializer.SerializeToElement(new[] { "x", "y" }) }).Body);
        Assert.Equal(new[] { "p", "q" }, TemplateArgs.FromPayload(new Dictionary<string, object?>
            { ["variables"] = "p, q" }).Body);
        Assert.Empty(TemplateArgs.FromPayload(new Dictionary<string, object?>
            { ["variables"] = new Dictionary<string, string>() }).Body); // recovery jobs
        Assert.Empty(TemplateArgs.FromPayload(new Dictionary<string, object?> { ["variables"] = "" }).Body);
    }

    [Fact]
    public void Sandbox_and_baileys_templates_render_as_text()
    {
        var thread = MakeThread("whatsapp", "60120000003", null);
        var msg = _sender.SendTemplate(thread, "order_confirmation", "en",
            TemplateArgs.Of(new[] { "Bob", "#5", "Friday" }), MessageSource.Agent);
        Assert.Equal("sandbox", msg.Status);
        Assert.StartsWith("Hi Bob, thanks for your order #5. We'll have it ready by Friday.", msg.Text);
        Assert.Empty(_graph.Requests);
    }

    // ---- failures, the window and health ----------------------------------------------

    [Fact]
    public void Per_recipient_error_never_touches_health_but_131047_closes_the_window()
    {
        var acct = Cloud("PN103");
        var thread = MakeThread("whatsapp", "60120000004", acct);
        _threads.AppendMessage(thread, MessageDirection.In, MessageSource.Customer, "hello",
            status: "received", accountId: acct.Id, providerTs: DateTime.UtcNow.AddHours(-2));
        _graph.Json("POST", @"/PN103/messages$", new
        {
            error = new { message = "Re-engagement message", code = 131047,
                error_data = new { details = "More than 24 hours have passed" } },
        }, HttpStatusCode.BadRequest);

        var msg = _sender.Send(thread, "are you there?", MessageSource.Agent);

        Assert.Equal("failed", msg.Status);
        Assert.StartsWith("131047:", msg.Error);
        var db = _t.Fresh();
        Assert.Equal(Health.Unknown, db.ChannelAccounts.Find(acct.Id)!.HealthStatus); // per-recipient
        var lastIn = db.Messages.Where(m => m.ThreadId == thread.Id && m.Direction == "in").Max(m => m.CreatedAt);
        Assert.True(lastIn < DateTime.UtcNow.AddHours(-24)); // window reads closed → template mode
    }

    [Fact]
    public void Account_level_error_moves_health_once_through_the_writer()
    {
        var acct = Cloud("PN104");
        var thread = MakeThread("whatsapp", "60120000005", acct);
        _graph.Error("POST", @"/PN104/messages$", 190, "Error validating access token", HttpStatusCode.Unauthorized);

        _sender.Send(thread, "one", MessageSource.Agent);
        _sender.Send(thread, "two", MessageSource.Agent);

        var db = _t.Fresh();
        Assert.Equal(Health.Disconnected, db.ChannelAccounts.Find(acct.Id)!.HealthStatus);
        var rows = db.AuditLogs.Where(a => a.Action == AccountHealth.AuditAction && a.Target == acct.Name).ToList();
        Assert.Single(rows); // one transition, one row
        Assert.Contains("unknown → disconnected", rows[0].Detail);
    }

    [Fact]
    public void Error_classification_table()
    {
        Assert.Equal(MetaErrorScope.Recipient, MetaErrors.Classify(131047).Scope);
        Assert.Null(MetaErrors.Classify(131026).Health);
        Assert.Equal(MetaErrorScope.Template, MetaErrors.Classify(132001).Scope);
        Assert.Equal(Health.Disconnected, MetaErrors.Classify(190).Health);
        Assert.Equal(Health.Banned, MetaErrors.Classify(131031).Health);
        Assert.Equal(Health.Banned, MetaErrors.Classify(368).Health);
        Assert.Equal(Health.Degraded, MetaErrors.Classify(130429).Health);
        Assert.Null(MetaErrors.Classify(131000).Health);           // Meta's own hiccup
        Assert.Null(MetaErrors.Classify(999999).Health);           // unknown never takes a number out
        Assert.Null(MetaErrors.Classify(10, 2018278).Health);      // Messenger window subcode
        Assert.Equal(Health.Disconnected, MetaErrors.Classify(10).Health);
    }

    // ---- delivery statuses -----------------------------------------------------------------

    private static JsonElement StatusPayload(string pnid, string wamid, string status, int? code = null)
    {
        object st = code is int c
            ? new { id = wamid, status, timestamp = "1700000100", recipient_id = "601",
                errors = new[] { new { code = c, title = "err title" } } }
            : new { id = wamid, status, timestamp = "1700000100", recipient_id = "601" };
        return JsonSerializer.SerializeToElement(new
        {
            entry = new[] { new { changes = new[] { new { value = new
            {
                metadata = new { phone_number_id = pnid },
                statuses = new[] { st },
            } } } } },
        });
    }

    [Fact]
    public void Statuses_only_move_forward_and_bump_the_thread()
    {
        var acct = Cloud("PN105");
        var thread = MakeThread("whatsapp", "60120000006", acct);
        var sent = _threads.AppendMessage(thread, "out", "agent", "hi", status: "sent", externalId: "wamid.S1",
            accountId: acct.Id);
        var before = _t.Fresh().Threads.Find(thread.Id)!.UpdatedAt;

        foreach (var s in MetaWebhook.CloudStatuses(StatusPayload("PN105", "wamid.S1", "read")))
            Assert.True(MetaWebhook.ApplyStatus(_t.Db, s));
        foreach (var s in MetaWebhook.CloudStatuses(StatusPayload("PN105", "wamid.S1", "delivered")))
            Assert.False(MetaWebhook.ApplyStatus(_t.Db, s)); // late "delivered" never regresses "read"

        var db = _t.Fresh();
        Assert.Equal("read", db.Messages.Find(sent.Id)!.Status);
        Assert.True(db.Threads.Find(thread.Id)!.UpdatedAt > before);
    }

    [Fact]
    public void Failed_status_records_the_error_and_classifies_it()
    {
        var acct = Cloud("PN106");
        var thread = MakeThread("whatsapp", "60120000007", acct);
        _threads.AppendMessage(thread, "in", "customer", "yo", status: "received", accountId: acct.Id,
            providerTs: DateTime.UtcNow.AddMinutes(-30));
        var a = _threads.AppendMessage(thread, "out", "agent", "x", status: "sent", externalId: "wamid.F1", accountId: acct.Id);
        var b = _threads.AppendMessage(thread, "out", "agent", "y", status: "sent", externalId: "wamid.F2", accountId: acct.Id);

        foreach (var s in MetaWebhook.CloudStatuses(StatusPayload("PN106", "wamid.F1", "failed", 131047)))
            MetaWebhook.ApplyStatus(_t.Db, s);
        var db = _t.Fresh();
        Assert.Equal("failed", db.Messages.Find(a.Id)!.Status);
        Assert.Equal("131047: err title", db.Messages.Find(a.Id)!.Error);
        Assert.Equal(Health.Unknown, db.ChannelAccounts.Find(acct.Id)!.HealthStatus);
        Assert.True(db.Messages.Where(m => m.ThreadId == thread.Id && m.Direction == "in")
            .Max(m => m.CreatedAt) < DateTime.UtcNow.AddHours(-24));

        foreach (var s in MetaWebhook.CloudStatuses(StatusPayload("PN106", "wamid.F2", "failed", 131031)))
            MetaWebhook.ApplyStatus(_t.Db, s);
        Assert.Equal(Health.Banned, _t.Fresh().ChannelAccounts.Find(acct.Id)!.HealthStatus);
    }

    // ---- inbound parsing -------------------------------------------------------------------

    [Fact]
    public void Cloud_inbound_parses_media_interactive_location_and_skips_reactions()
    {
        var root = JsonSerializer.SerializeToElement(new
        {
            entry = new[] { new { changes = new[] { new { value = new
            {
                metadata = new { phone_number_id = "PN9" },
                contacts = new[] { new { profile = new { name = "Mia" } } },
                messages = new object[]
                {
                    new { from = "601", id = "w1", timestamp = "1700000000", type = "image",
                          image = new { id = "MEDIA1", mime_type = "image/jpeg", caption = "receipt" } },
                    new { from = "601", id = "w2", timestamp = "1700000001", type = "document",
                          document = new { id = "MEDIA2", mime_type = "application/pdf", filename = "inv.pdf" } },
                    new { from = "601", id = "w3", timestamp = "1700000002", type = "interactive",
                          interactive = new { type = "button_reply", button_reply = new { id = "b1", title = "Yes please" } } },
                    new { from = "601", id = "w4", timestamp = "1700000003", type = "location",
                          location = new { latitude = 3.1, longitude = 101.6, name = "KLCC" } },
                    new { from = "601", id = "w5", timestamp = "1700000004", type = "reaction",
                          reaction = new { message_id = "w1", emoji = "👍" } },
                },
            } } } } },
        });
        var msgs = MetaWebhook.CloudMessages(root);
        Assert.Equal(4, msgs.Count);
        Assert.Equal(("receipt", "MEDIA1", "image/jpeg"), (msgs[0].Text, msgs[0].MediaId, msgs[0].Mime));
        Assert.Equal(("[document]", "inv.pdf"), (msgs[1].Text, msgs[1].Filename));
        Assert.Equal("Yes please", msgs[2].Text);
        Assert.StartsWith("[location] 3.1,101.6 KLCC", msgs[3].Text);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000).UtcDateTime, msgs[0].Timestamp);
        Assert.Equal("Mia", msgs[0].Name);
    }

    [Fact]
    public void Messenger_events_skip_echoes_and_carry_attachments_and_receipts()
    {
        var root = JsonSerializer.SerializeToElement(new
        {
            @object = "page",
            entry = new[] { new { id = "PAGE1", time = 1, messaging = new object[]
            {
                new { sender = new { id = "PSID1" }, recipient = new { id = "PAGE1" }, timestamp = 1700000000000,
                      message = new { mid = "m1", text = "hi", attachments = new[]
                      { new { type = "image", payload = new { url = "https://cdn.test/a.jpg" } } } } },
                new { sender = new { id = "PAGE1" }, recipient = new { id = "PSID1" }, timestamp = 1700000000001,
                      message = new { mid = "m2", text = "our reply", is_echo = true } },
                new { sender = new { id = "PSID1" }, recipient = new { id = "PAGE1" }, timestamp = 1700000000002,
                      postback = new { title = "Get started", payload = "START", mid = "m3" } },
                new { sender = new { id = "PSID1" }, recipient = new { id = "PAGE1" }, timestamp = 1700000000003,
                      delivery = new { mids = new[] { "out-1" }, watermark = 1700000000003 } },
            } } },
        });
        var evs = MetaWebhook.MessagingEvents(root);
        Assert.Equal(3, evs.Count);
        Assert.Equal(("facebook", "PAGE1", "PSID1", "hi"), (evs[0].Channel, evs[0].PageId, evs[0].SenderId, evs[0].Text));
        Assert.Equal("https://cdn.test/a.jpg", evs[0].Attachments.Single().Url);
        Assert.Equal(("message", "Get started"), (evs[1].Kind, evs[1].Text));
        Assert.Equal("delivery", evs[2].Kind);
        Assert.Equal(new[] { "out-1" }, evs[2].Mids);
    }

    // ---- media ------------------------------------------------------------------------------

    [Fact]
    public void Live_media_send_uploads_then_sends_by_id()
    {
        var acct = Cloud("PN107");
        var thread = MakeThread("whatsapp", "60120000008", acct);
        _graph.Json("POST", @"/PN107/media$", new { id = "MID-77" });
        _graph.Json("POST", @"/PN107/messages$", new { messages = new[] { new { id = "wamid.M1" } } });
        var rel = _media.SaveAsync("whatsapp", thread.Key, Encoding.UTF8.GetBytes("%PDF-1.4 fake"), ".pdf")
            .GetAwaiter().GetResult();

        var msg = _sender.SendMedia(thread, rel, "document", "application/pdf", "your invoice", "inv.pdf",
            MessageSource.Agent);

        Assert.Equal(("sent", "wamid.M1", "document"), (msg.Status, msg.ExternalId, msg.MediaType));
        Assert.Equal(rel, msg.MediaPath);
        var upload = _graph.Requests.Single(r => r.Url.EndsWith("/PN107/media"));
        Assert.Equal("multipart/form-data", upload.ContentType);
        Assert.Contains("messaging_product", upload.Body);
        Assert.Contains("%PDF-1.4 fake", upload.Body);
        var send = BodyOf(_graph.Requests.Single(r => r.Url.EndsWith("/PN107/messages")));
        Assert.Equal("document", send.GetProperty("type").GetString());
        Assert.Equal("MID-77", send.GetProperty("document").GetProperty("id").GetString());
        Assert.Equal("inv.pdf", send.GetProperty("document").GetProperty("filename").GetString());
        Assert.Equal("your invoice", send.GetProperty("document").GetProperty("caption").GetString());
    }

    [Fact]
    public void Inbound_media_download_follows_the_url_with_the_bearer_token()
    {
        var acct = Cloud("PN108");
        _graph.Json("GET", @"/v23\.0/MEDIA-9$", new
        {
            url = "https://lookaside.fbsbx.com/whatsapp_business/attachments/?mid=MEDIA-9",
            mime_type = "image/png", file_size = 4, id = "MEDIA-9",
        });
        _graph.Bytes(@"lookaside\.fbsbx\.com", new byte[] { 1, 2, 3, 4 }, "image/png");

        var r = _wa.DownloadMediaAsync(acct, "MEDIA-9", MediaStore.MaxBytes).GetAwaiter().GetResult();

        Assert.True(r.Ok);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, r.Bytes);
        Assert.All(_graph.Requests, req => Assert.Equal("Bearer tok-PN108", req.Auth));
        // the declared size is checked before the download
        _graph.Json("GET", @"/v23\.0/MEDIA-BIG$", new { url = "https://lookaside.fbsbx.com/big", file_size = 50_000_000 });
        Assert.False(_wa.DownloadMediaAsync(acct, "MEDIA-BIG", MediaStore.MaxBytes).GetAwaiter().GetResult().Ok);
        Assert.DoesNotContain(_graph.Requests, q => q.Url.EndsWith("/big"));
    }

    [Fact]
    public void Media_store_refuses_paths_outside_its_root()
    {
        var rel = _media.SaveAsync("telegram", "-100/../../x", new byte[] { 9 }, "../../evil").GetAwaiter().GetResult();
        Assert.StartsWith("telegram/", rel);
        Assert.EndsWith(".bin", rel);   // a hostile extension collapses to .bin
        Assert.NotNull(_media.Resolve("telegram", rel));
        Assert.Null(_media.Resolve("telegram", "../../../etc/passwd"));
        Assert.Null(_media.Resolve("whatsapp", "../messaging/media/" + rel));
        Assert.False(MediaStore.IsInlineSafe("text/html"));
        Assert.Equal("document", MediaStore.TypeForMime("application/zip"));
    }

    // ---- Messenger / Instagram send -------------------------------------------------------------

    [Fact]
    public void Messenger_send_uses_the_page_token_and_records_the_mid()
    {
        var page = new ChannelAccount
        {
            Channel = "facebook", Name = "Shop page", ExternalId = "PAGE7", AccessToken = "page-token",
            CreatedAt = DateTime.UtcNow,
        };
        _t.Db.ChannelAccounts.Add(page);
        _t.Db.SaveChanges();
        var thread = MakeThread("facebook", "PSID-7", page);
        // the customer wrote first — Meta's messaging window is open
        _threads.AppendMessage(thread, MessageDirection.In, MessageSource.Customer, "hi", externalId: "mid.in.7",
            accountId: page.Id);
        _graph.Json("POST", @"/me/messages$", new { recipient_id = "PSID-7", message_id = "m_out_1" });

        Assert.True(_sender.WouldSendLive(thread));
        var msg = _sender.Send(thread, "Hello from the page", MessageSource.Agent);

        Assert.Equal(("sent", "m_out_1"), (msg.Status, msg.ExternalId));
        var post = _graph.Requests.Single();
        Assert.Equal("Bearer page-token", post.Auth);
        var body = BodyOf(post);
        Assert.Equal("PSID-7", body.GetProperty("recipient").GetProperty("id").GetString());
        Assert.Equal("RESPONSE", body.GetProperty("messaging_type").GetString());
        Assert.Equal("Hello from the page", body.GetProperty("message").GetProperty("text").GetString());

        // an unconfigured page stays sandbox
        var bare = MakeThread("instagram", "IGSID-1", null);
        Assert.Equal("sandbox", _sender.Send(bare, "hi", MessageSource.Agent).Status);
    }

    // ---- call recordings ------------------------------------------------------------------------

    [Fact]
    public void Recordings_pair_gateway_wavs_with_transcripts_and_fall_back_to_the_agent_recording()
    {
        var recs = new CallRecordings(_dir);
        var bridge = new ChatBranch.Data.Sidecars.VoiceAgentBridge(_dir);
        Directory.CreateDirectory(recs.GatewayDir);
        Directory.CreateDirectory(recs.AgentDir);
        Directory.CreateDirectory(Path.Combine(_dir, "live"));
        File.WriteAllBytes(Path.Combine(recs.GatewayDir, "60123-1700000000000.wav"), new byte[] { 1, 2 });
        File.WriteAllBytes(Path.Combine(recs.AgentDir, "room-b.wav"), new byte[] { 3, 4, 5 });
        File.WriteAllText(Path.Combine(_dir, "live", "room-a.json"),
            """{"turns":[{"role":"assistant","text":"Hello!","ts":1700000001}],"done":true,"ended_by":"contact"}""");

        recs.SaveEnded("60123", "60123-1700000000000.wav", "room-a", 1700000000500, "hangup", "admin", "Biz", bridge);
        recs.SaveEnded("60123", null, "room-b", null, "hangup", "flow", "Biz", bridge); // gateway did not record

        var list = recs.ListFor("60123", bridge);
        Assert.Equal(2, list.Count);
        var first = list.Single(e => (string)e["name"]! == "60123-1700000000000.wav");
        Assert.Equal("/api/whatsapp/calls/recording/60123-1700000000000.wav", first["url"]);
        var turns = (JsonElement)first["transcript"]!;
        Assert.Equal("Hello!", turns[0].GetProperty("text").GetString());
        var meta = (JsonElement)first["meta"]!;
        Assert.Equal("contact", meta.GetProperty("ended_by").GetString());
        Assert.Equal("admin", meta.GetProperty("initiated_by").GetString());
        Assert.Contains(list, e => (string)e["name"]! == "room-b.wav"); // the agent's audio
        Assert.Empty(recs.ListFor("99999", bridge));

        Assert.NotNull(recs.Resolve("room-b.wav"));
        Assert.Null(recs.Resolve("../calls.json"));
        Assert.Null(recs.Resolve("..%2Fx.wav"));
        Assert.Equal("60123", CallRecordings.WaIdOf("60123-1700000000000.wav"));
        Assert.Null(CallRecordings.WaIdOf("room-b.wav"));
    }
}
