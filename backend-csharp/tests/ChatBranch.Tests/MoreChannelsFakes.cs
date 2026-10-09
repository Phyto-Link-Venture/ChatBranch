using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using ChatBranch.Data.Channels;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ChatBranch.Tests;

/// <summary>
/// Every provider host the new channels talk to (api.line.me, chatapi.viber.com,
/// api.twilio.com, api.weixin.qq.com, business-api.tiktok.com, open.douyin.com,
/// openapi.zalo.me…), answered in-process at the HTTP level — so the REAL
/// transports (URLs, headers, JSON, error parsing, token refresh) are what run.
/// </summary>
public sealed class FakeProviderHttp : HttpMessageHandler, IHttpClientFactory
{
    public sealed record Call(string Method, Uri Url, string Body, Dictionary<string, string> Headers)
    {
        public string Host => Url.Host;
        public string Path => Url.AbsolutePath;
        public JsonElement Json => JsonSerializer.Deserialize<JsonElement>(Body.Length > 0 ? Body : "{}");
        public string Header(string name) =>
            Headers.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value ?? "";
    }

    public ConcurrentQueue<Call> Calls { get; } = new();
    /// <summary>A test's own answer; null = the default answer for that endpoint.</summary>
    public Func<Call, HttpResponseMessage?> Respond { get; set; } = _ => null;
    private long _next = 5000;

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

    public void Reset()
    {
        Calls.Clear();
        Respond = _ => null;
    }

    public List<Call> To(string host, string pathContains) =>
        Calls.Where(c => c.Host == host && c.Path.Contains(pathContains)).ToList();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value));
        if (request.Content != null)
            foreach (var h in request.Content.Headers) headers[h.Key] = string.Join(",", h.Value);
        var call = new Call(request.Method.Method, request.RequestUri!, body, headers);
        Calls.Enqueue(call);
        return Respond(call) ?? Default(call);
    }

    private string Id(string prefix) => $"{prefix}{Interlocked.Increment(ref _next)}";

    public static HttpResponseMessage Json(object payload, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") };

    public static HttpResponseMessage File(byte[] bytes, string type = "image/jpeg") =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) { Headers = { { "Content-Type", type } } } };

    public static readonly byte[] Jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4 };

    private HttpResponseMessage Default(Call c) => (c.Host, c.Path) switch
    {
        ("api.line.me", var p) when p.EndsWith("/message/push") || p.EndsWith("/message/reply") =>
            Json(new { sentMessages = new[] { new { id = Id("L"), quoteToken = "q" } } }),
        ("api.line.me", "/v2/bot/info") => Json(new { userId = "Ubot0001", basicId = "@shop", displayName = "Shop Bot" }),
        ("api.line.me", "/v2/bot/channel/webhook/endpoint") => c.Method == "PUT"
            ? Json(new { })
            : Json(new { endpoint = "https://hooks.example.test/x", active = true }),
        ("api.line.me", var p) when p.StartsWith("/v2/bot/profile/") => Json(new { displayName = "Aiko", userId = "U" }),
        ("api-data.line.me", _) => File(Jpeg),

        ("chatapi.viber.com", "/pa/send_message") =>
            Json(new { status = 0, status_message = "ok", message_token = long.Parse(Id("")) }),
        ("chatapi.viber.com", "/pa/get_account_info") =>
            Json(new { status = 0, id = "pa:123", name = "Viber Shop", webhook = "https://hooks.example.test/v" }),
        ("chatapi.viber.com", "/pa/set_webhook") => Json(new { status = 0, status_message = "ok" }),

        ("api.twilio.com", var p) when p.EndsWith("/Messages.json") =>
            Json(new { sid = Id("SM"), status = "queued" }, HttpStatusCode.Created),
        ("api.twilio.com", var p) when p.Contains("/Media/") => File(Jpeg),
        ("api.twilio.com", _) => Json(new { status = "active", friendly_name = "Twilio Shop" }),

        ("api.weixin.qq.com", "/cgi-bin/token") => Json(new { access_token = "WXTOKEN-1", expires_in = 7200 }),
        ("api.weixin.qq.com", "/cgi-bin/message/custom/send") => Json(new { errcode = 0, errmsg = "ok" }),
        ("api.weixin.qq.com", "/cgi-bin/media/upload") => Json(new { type = "image", media_id = "MEDIA-1", created_at = 1 }),
        ("api.weixin.qq.com", "/cgi-bin/media/get") => File(Jpeg),

        ("business-api.tiktok.com", var p) when p.EndsWith("/business/message/send/") =>
            Json(new { code = 0, message = "OK", data = new { message_id = Id("T") } }),
        ("business-api.tiktok.com", _) => Json(new { code = 0, message = "OK", data = new { display_name = "TT Shop" } }),

        ("open.douyin.com", "/im/send/msg/") => Json(new { err_no = 0, err_msg = "", data = new { msg_id = Id("D") } }),
        ("open.douyin.com", "/oauth/client_token/") =>
            Json(new { data = new { access_token = "clt.1", error_code = 0, expires_in = 7200 }, message = "success" }),

        ("openapi.zalo.me", "/v3.0/oa/message/cs") =>
            Json(new { error = 0, message = "Success", data = new { message_id = Id("Z"), user_id = "u" } }),
        ("openapi.zalo.me", "/v2.0/oa/getoa") => Json(new { error = 0, data = new { oa_id = "OA-777", name = "Zalo Shop" } }),
        ("openapi.zalo.me", "/v2.0/oa/upload/image") => Json(new { error = 0, data = new { attachment_id = "ATT-1" } }),
        ("openapi.zalo.me", "/v3.0/oa/user/detail") => Json(new { error = 0, data = new { display_name = "Linh" } }),
        ("oauth.zaloapp.com", _) => Json(new { access_token = "ZALO-NEW", refresh_token = "REFRESH-NEW", expires_in = "90000" }),

        ("cdn.example.test", _) => File(Jpeg),
        _ => new HttpResponseMessage(HttpStatusCode.NotFound),
    };
}

/// <summary>The API host with the seven new transports talking to <see cref="FakeProviderHttp"/>.</summary>
public sealed class MoreChannelsFactory : ApiFactory
{
    public FakeProviderHttp Http { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s =>
        {
            s.AddSingleton(new LineTransport(Http));
            s.AddSingleton(new ViberTransport(Http));
            s.AddSingleton(new TwilioSmsTransport(Http));
            s.AddSingleton(new WeChatTransport(Http));
            s.AddSingleton(new TikTokTransport(Http));
            s.AddSingleton(new DouyinTransport(Http));
            s.AddSingleton(new ZaloTransport(Http));
        });
    }
}
