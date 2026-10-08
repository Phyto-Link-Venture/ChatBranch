using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ChatBranch.Api.Workers;
using ChatBranch.Data.Channels;
using ChatBranch.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace ChatBranch.Tests;

/// <summary>SMTP that never leaves the process: records what would have been sent.</summary>
public sealed class FakeEmailTransport : IEmailTransport
{
    public ConcurrentQueue<(EmailServerSettings Server, MimeMessage Message)> Sent { get; } = new();
    public Func<EmailServerSettings, EmailResult> SendResult { get; set; } = _ => EmailResult.Success;
    public Func<EmailServerSettings, EmailResult> ProbeResult { get; set; } = _ => EmailResult.Success;
    private int _probes;
    public int Probes => _probes;

    public Task<EmailResult> SendAsync(EmailServerSettings server, MimeMessage message, CancellationToken ct = default)
    {
        var r = SendResult(server);
        if (r.Ok) Sent.Enqueue((server, message));
        return Task.FromResult(r);
    }

    public Task<EmailResult> ProbeAsync(EmailServerSettings server, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _probes);
        return Task.FromResult(ProbeResult(server));
    }

    public void Reset()
    {
        Sent.Clear();
        SendResult = _ => EmailResult.Success;
        ProbeResult = _ => EmailResult.Success;
    }
}

/// <summary>An IMAP mailbox whose UNSEEN mail the test decides (handed out once, like \Seen).</summary>
public sealed class FakeMailboxReader : IMailboxReader
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<MimeMessage>> _unseen = new();
    public ConcurrentQueue<EmailServerSettings> Calls { get; } = new();
    public Func<EmailServerSettings, Exception?> Fail { get; set; } = _ => null;

    public void Deliver(string mailboxUser, MimeMessage msg) =>
        _unseen.GetOrAdd(mailboxUser.ToLowerInvariant(), _ => new()).Enqueue(msg);

    public Task<IReadOnlyList<MimeMessage>> FetchUnseenAsync(EmailServerSettings mailbox, DateTime sinceUtc,
        int max, CancellationToken ct = default)
    {
        Calls.Enqueue(mailbox);
        if (Fail(mailbox) is { } ex) throw ex;
        var outv = new List<MimeMessage>();
        if (_unseen.TryGetValue(mailbox.ImapUsername.ToLowerInvariant(), out var q))
            while (outv.Count < max && q.TryDequeue(out var m)) outv.Add(m);
        return Task.FromResult<IReadOnlyList<MimeMessage>>(outv);
    }
}

/// <summary>
/// api.telegram.org stand-in at the HTTP level, so the REAL <see cref="TelegramClient"/>
/// (URLs, JSON, multipart, error parsing, token scrubbing) is what runs.
/// </summary>
public sealed class FakeTelegramApi : HttpMessageHandler, IHttpClientFactory
{
    public sealed record Call(string Token, string Method, string Body, string? ContentType);

    public ConcurrentQueue<Call> Calls { get; } = new();
    /// <summary>(token, method, body) → response; null = the default OK answer for that method.</summary>
    public Func<string, string, string, HttpResponseMessage?> Respond { get; set; } = (_, _, _) => null;
    public Dictionary<string, byte[]> Files { get; } = new();
    private long _nextMessageId = 1000;

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath; // /bot<token>/<method> or /file/bot<token>/<path>
        if (path.StartsWith("/file/bot"))
        {
            var rest = path["/file/bot".Length..];
            var filePath = rest[(rest.IndexOf('/') + 1)..];
            return Files.TryGetValue(filePath, out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
        var parts = path.Trim('/').Split('/');
        var token = parts[0].StartsWith("bot") ? parts[0][3..] : "";
        var method = parts.Length > 1 ? parts[1] : "";
        var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
        Calls.Enqueue(new Call(token, method, body, request.Content?.Headers.ContentType?.MediaType));
        return Respond(token, method, body) ?? Default(method, body);
    }

    private HttpResponseMessage Default(string method, string body) => method switch
    {
        "getMe" => Ok(new { id = 777, is_bot = true, first_name = "Test Bot", username = "test_bot" }),
        "getWebhookInfo" => Ok(new { url = "https://example.test/hook", pending_update_count = 0 }),
        "setWebhook" => Ok(true),
        "getFile" => Ok(new { file_id = "x", file_path = "photos/file_1.jpg", file_size = 4 }),
        _ => Ok(new { message_id = Interlocked.Increment(ref _nextMessageId), date = 0, chat = new { id = 1 } }),
    };

    public static HttpResponseMessage Ok(object result) => Json(HttpStatusCode.OK, new { ok = true, result });

    public static HttpResponseMessage Error(HttpStatusCode status, string description) =>
        Json(status, new { ok = false, error_code = (int)status, description });

    private static HttpResponseMessage Json(HttpStatusCode status, object payload) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") };

    public void Reset()
    {
        Calls.Clear();
        Respond = (_, _, _) => null;
    }
}

/// <summary>The Meta Graph liveness ping, decided by the test.</summary>
public sealed class FakeGraphProbe : IGraphHealthProbe
{
    public Func<ChannelAccount, GraphPing> Answer { get; set; } = _ => new GraphPing(true, 200, null, "");
    public Task<GraphPing> PingAsync(ChannelAccount account, CancellationToken ct = default) =>
        Task.FromResult(Answer(account));
}

/// <summary>
/// The API host with every email / Telegram / Graph client faked — and the
/// post-save health re-check switched ON (it only ever reaches the fakes).
/// </summary>
public sealed class TransportFactory : ApiFactory
{
    public FakeEmailTransport Email { get; } = new();
    public FakeMailboxReader Mailbox { get; } = new();
    public FakeTelegramApi TelegramApi { get; } = new();
    public FakeGraphProbe Graph { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s =>
        {
            s.AddSingleton<IEmailTransport>(Email);
            s.AddSingleton<IMailboxReader>(Mailbox);
            s.AddSingleton<ITelegramClient>(new TelegramClient(TelegramApi, "https://tg.test"));
            s.AddSingleton<IGraphHealthProbe>(Graph);
            s.AddSingleton(sp => new HealthRecheck(sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<ILogger<HealthRecheck>>(), enabled: true));
        });
    }
}

/// <summary>
/// A tiny loopback SMTP server (plain text, AUTH PLAIN/LOGIN) — enough for the
/// real MailKit transport to connect, log in and hand over one message.
/// </summary>
public sealed class FakeSmtpServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Task _loop;
    public int Port { get; }
    public bool RejectAuth { get; init; }
    public bool RejectRecipient { get; init; }
    public ConcurrentQueue<string> Commands { get; } = new();
    public string Data { get; private set; } = "";
    public string AuthPayload { get; private set; } = "";

    public FakeSmtpServer(bool rejectAuth = false, bool rejectRecipient = false)
    {
        RejectAuth = rejectAuth;
        RejectRecipient = rejectRecipient;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(ServeAsync);
    }

    private async Task ServeAsync()
    {
        try
        {
            while (true)
            {
                using var client = await _listener.AcceptTcpClientAsync();
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII);
                await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
                await writer.WriteLineAsync("220 fake.smtp ESMTP ready");
                while (await reader.ReadLineAsync() is { } line)
                {
                    Commands.Enqueue(line);
                    var cmd = line.ToUpperInvariant();
                    if (cmd.StartsWith("EHLO"))
                        await writer.WriteAsync("250-fake.smtp\r\n250-AUTH PLAIN LOGIN\r\n250 8BITMIME\r\n");
                    else if (cmd.StartsWith("AUTH PLAIN"))
                    {
                        var payload = line.Length > "AUTH PLAIN ".Length ? line["AUTH PLAIN ".Length..] : "";
                        if (payload.Length == 0)
                        {
                            await writer.WriteLineAsync("334 ");
                            payload = await reader.ReadLineAsync() ?? "";
                        }
                        AuthPayload = payload;
                        await writer.WriteLineAsync(RejectAuth ? "535 5.7.8 Authentication credentials invalid"
                            : "235 2.7.0 Authentication successful");
                    }
                    else if (cmd.StartsWith("AUTH LOGIN"))
                    {
                        await writer.WriteLineAsync("334 VXNlcm5hbWU6");
                        var user = await reader.ReadLineAsync() ?? "";
                        await writer.WriteLineAsync("334 UGFzc3dvcmQ6");
                        var pass = await reader.ReadLineAsync() ?? "";
                        AuthPayload = $"{user}:{pass}";
                        await writer.WriteLineAsync(RejectAuth ? "535 5.7.8 Authentication credentials invalid"
                            : "235 2.7.0 Authentication successful");
                    }
                    else if (cmd.StartsWith("RCPT"))
                        await writer.WriteLineAsync(RejectRecipient ? "550 5.1.1 No such user" : "250 2.1.5 OK");
                    else if (cmd.StartsWith("DATA"))
                    {
                        await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                        var sb = new StringBuilder();
                        while (await reader.ReadLineAsync() is { } dataLine && dataLine != ".")
                            sb.Append(dataLine).Append("\r\n");
                        Data = sb.ToString();
                        await writer.WriteLineAsync("250 2.0.0 queued");
                    }
                    else if (cmd.StartsWith("QUIT"))
                    {
                        await writer.WriteLineAsync("221 2.0.0 bye");
                        break;
                    }
                    else await writer.WriteLineAsync("250 OK");
                }
            }
        }
        catch { /* listener stopped */ }
    }

    public void Dispose()
    {
        try { _listener.Stop(); } catch { }
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { }
    }
}
