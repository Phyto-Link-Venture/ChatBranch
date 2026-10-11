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

/// <summary>
/// IMAP mailboxes, one per login: an INBOX and (unless taken away) a Sent folder, each
/// holding mails with UIDs, a <c>\Seen</c> flag and a UIDVALIDITY. Read the way the real
/// reader reads them — <see cref="MailboxCursorPlan"/>: the window (SENTSINCE, seen or
/// not) on a first run, <c>UID last+1:*</c> after it, IMAP's quirk of answering
/// <c>n:*</c> with the highest UID included — and, like EXAMINE, reading never changes a
/// flag. Appends to Sent (<see cref="IMailboxWriter"/>) land in the Sent folder and are
/// recorded.
/// </summary>
public sealed class FakeMailboxReader : IMailboxReader, IMailboxWriter
{
    public sealed class Item
    {
        public uint Uid { get; init; }
        public MimeMessage Message { get; init; } = null!;
        public bool Seen { get; set; }
        /// <summary>The server will not hand this one over (a broken message).</summary>
        public bool Broken { get; set; }
    }

    public sealed class Folder
    {
        public string Name { get; set; }
        public uint UidValidity { get; set; } = 1;
        public uint UidNext { get; set; } = 1;
        public List<Item> Items { get; } = new();
        public Folder(string name) => Name = name;
    }

    private sealed class Box
    {
        public Folder Inbox { get; } = new("INBOX");
        public Folder? Sent { get; set; } = new("Sent");
    }

    private readonly ConcurrentDictionary<string, Box> _boxes = new();
    public ConcurrentQueue<EmailServerSettings> Calls { get; } = new();
    public Func<EmailServerSettings, Exception?> Fail { get; set; } = _ => null;
    /// <summary>Every pass over every folder: which UIDs were handed out, and whether it was a first run.</summary>
    public ConcurrentQueue<(string User, string Folder, uint[] Uids, bool FirstRun)> Reads { get; } = new();
    /// <summary>Copies appended to a Sent folder (after an SMTP send).</summary>
    public ConcurrentQueue<(EmailServerSettings Server, MimeMessage Message)> Appended { get; } = new();
    public Func<EmailServerSettings, Exception?> AppendFail { get; set; } = _ => null;

    private Box For(string user) => _boxes.GetOrAdd(user.ToLowerInvariant(), _ => new Box());

    /// <summary>New mail in the INBOX (already read in another mail app when <paramref name="seen"/>).</summary>
    public uint Deliver(string mailboxUser, MimeMessage msg, bool seen = false)
    {
        var box = For(mailboxUser);
        lock (box) return Add(box.Inbox, msg, seen);
    }

    /// <summary>A mail a person sent from their own mail app (filed \Seen, as mail apps do).</summary>
    public uint DeliverSent(string mailboxUser, MimeMessage msg)
    {
        var box = For(mailboxUser);
        lock (box) return Add(box.Sent ?? throw new InvalidOperationException("no Sent folder"), msg, true);
    }

    /// <summary>Rename the Sent folder, or (null) take it away.</summary>
    public void SentFolder(string mailboxUser, string? name)
    {
        var box = For(mailboxUser);
        lock (box)
        {
            if (name == null) box.Sent = null;
            else if (box.Sent == null) box.Sent = new Folder(name);
            else box.Sent.Name = name;
        }
    }

    /// <summary>The server rebuilt the folder: a new UIDVALIDITY, every mail renumbered from 1.</summary>
    public void Rebuild(string mailboxUser, string folder)
    {
        var box = For(mailboxUser);
        lock (box)
        {
            var f = Find(box, folder);
            var items = f.Items.ToList();
            f.Items.Clear();
            f.UidValidity += 100;
            f.UidNext = 1;
            foreach (var i in items) Add(f, i.Message, i.Seen);
        }
    }

    public void Break(string mailboxUser, string folder, uint uid)
    {
        var box = For(mailboxUser);
        lock (box) Find(box, folder).Items.Single(i => i.Uid == uid).Broken = true;
    }

    public IReadOnlyList<Item> Items(string mailboxUser, string folder)
    {
        var box = For(mailboxUser);
        lock (box) return Find(box, folder).Items.ToList();
    }

    private static Folder Find(Box box, string folder) =>
        folder == "INBOX" ? box.Inbox
        : box.Sent is { } s && s.Name == folder ? s
        : throw new InvalidOperationException($"no folder {folder}");

    private static uint Add(Folder f, MimeMessage msg, bool seen)
    {
        var uid = f.UidNext++;
        f.Items.Add(new Item { Uid = uid, Message = msg, Seen = seen });
        return uid;
    }

    public Task<MailboxFetch> FetchAsync(EmailServerSettings mailbox, IReadOnlyDictionary<string, FolderCursor> cursors,
        DateTime sinceUtc, int max, CancellationToken ct = default)
    {
        Calls.Enqueue(mailbox);
        if (Fail(mailbox) is { } ex) throw ex;
        var user = mailbox.ImapUsername.ToLowerInvariant();
        var box = For(user);
        lock (box)
        {
            var folders = new List<FolderFetch> { Read(box.Inbox, MailFolderRole.Inbox) };
            if (box.Sent != null) folders.Add(Read(box.Sent, MailFolderRole.Sent));
            return Task.FromResult(new MailboxFetch(folders) { NoSentFolder = box.Sent == null });
        }

        FolderFetch Read(Folder f, MailFolderRole role)
        {
            cursors.TryGetValue(f.Name, out var stored);
            List<uint> found;
            if (MailboxCursorPlan.IsFirstRun(stored, f.UidValidity))
                found = f.Items.Where(i => i.Message.Date.Date >= sinceUtc.Date).Select(i => i.Uid).ToList();
            else
            {
                found = f.Items.Where(i => i.Uid > stored!.LastUid).Select(i => i.Uid).ToList();
                if (found.Count == 0 && f.Items.Count > 0) found.Add(f.Items.Max(i => i.Uid)); // "n:*"
            }
            var plan = MailboxCursorPlan.Plan(stored, f.UidValidity, found, f.UidNext, max);
            var mails = plan.Fetch.Select(uid => f.Items.Single(i => i.Uid == uid))
                .Select(i => new FetchedMail(i.Uid, i.Broken ? null : i.Message)).ToList();
            Reads.Enqueue((user, f.Name, plan.Fetch.ToArray(), plan.FirstRun));
            return new FolderFetch(role, f.Name, f.UidValidity, plan.FirstRun, mails, plan.AdvanceTo);
        }
    }

    public Task<SentCopyResult> AppendToSentAsync(EmailServerSettings mailbox, MimeMessage message,
        CancellationToken ct = default)
    {
        if (AppendFail(mailbox) is { } ex) throw ex;
        var box = For(mailbox.ImapUsername);
        lock (box)
        {
            if (box.Sent == null) return Task.FromResult(SentCopyResult.NoSentFolder);
            if (box.Sent.Items.Any(i => i.Message.MessageId == message.MessageId))
                return Task.FromResult(SentCopyResult.AlreadyThere);
            Add(box.Sent, message, seen: true);
            Appended.Enqueue((mailbox, message));
            return Task.FromResult(SentCopyResult.Appended);
        }
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
            s.AddSingleton<IMailboxWriter>(Mailbox);
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
