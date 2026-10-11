using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using ChatBranch.Data.Channels;
using MailKit.Security;
using MimeKit;

namespace ChatBranch.Tests;

/// <summary>
/// The REAL MailKit reader / writer against a tiny loopback IMAP server: folders are
/// EXAMINEd (never SELECTed), mail is fetched with BODY.PEEK[] and never STOREd, so a
/// read mail stays read and an unread one unread; the Sent folder is found by its
/// SPECIAL-USE flag or a usual name; a copy is APPENDed to it flagged \Seen (once);
/// a refused login is an auth failure with no secret in it.
/// </summary>
public class ImapLoopbackTests
{
    private static MimeMessage Mail(string from, string to, string subject, string id, DateTimeOffset? date = null)
    {
        var m = new MimeMessage();
        m.From.Add(MailboxAddress.Parse(from));
        m.To.Add(MailboxAddress.Parse(to));
        m.Subject = subject;
        m.MessageId = id;
        m.Date = date ?? DateTimeOffset.UtcNow.AddHours(-1);
        m.Body = new TextPart("plain") { Text = $"{subject} body" };
        return m;
    }

    private static EmailServerSettings Box(int port) => new()
    {
        ImapHost = "127.0.0.1", ImapPort = port, ImapUsername = "penny@shop.test", ImapPassword = "imap-secret-9",
        From = "penny@shop.test",
    };

    [Fact]
    public async Task The_real_reader_examines_read_only_never_marks_seen_and_reads_on_from_its_cursor()
    {
        using var server = new FakeImapServer();
        server.Folder("INBOX", validity: 5);
        server.Folder("Drafts");
        server.Folder("Sent Items", validity: 9);
        var read = server.Add("INBOX", Mail("a@cust.test", "penny@shop.test", "Read already", "i1@cust.test"), "\\Seen");
        var unread = server.Add("INBOX", Mail("b@cust.test", "penny@shop.test", "Not read", "i2@cust.test"));
        server.Add("INBOX", Mail("c@cust.test", "penny@shop.test", "Ancient", "i0@cust.test", DateTimeOffset.UtcNow.AddDays(-40)));
        server.Add("Sent Items", Mail("penny@shop.test", "a@cust.test", "Re: Read already", "s1@shop.test"), "\\Seen");
        var reader = new MailKitMailboxReader(5000, SecureSocketOptions.None);

        var first = await reader.FetchAsync(Box(server.Port), new Dictionary<string, FolderCursor>(),
            DateTime.UtcNow.AddDays(-7), 10);
        Assert.False(first.NoSentFolder);
        var inbox = first.Folders.Single(f => f.Role == MailFolderRole.Inbox);
        Assert.Equal("INBOX", inbox.Folder);
        Assert.True(inbox.FirstRun);
        Assert.Equal(5u, inbox.UidValidity);
        Assert.Equal(new[] { read, unread }, inbox.Mails.Select(m => m.Uid));      // the window, seen or not
        Assert.Equal("Not read", inbox.Mails[1].Message!.Subject);
        Assert.Equal(3u, inbox.AdvanceTo);                                          // past the old one, too
        var sent = first.Folders.Single(f => f.Role == MailFolderRole.Sent);
        Assert.Equal("Sent Items", sent.Folder);                                    // found by its usual name
        Assert.Equal("s1@shop.test", sent.Mails.Single().Message!.MessageId);

        // nothing was marked read — and nothing that could mark it was ever sent
        Assert.Contains("\\Seen", server.FlagsOf("INBOX", read));
        Assert.DoesNotContain("\\Seen", server.FlagsOf("INBOX", unread));
        Assert.Contains(server.Commands, c => c.Contains("EXAMINE INBOX"));
        Assert.DoesNotContain(server.Commands, c => Regex.IsMatch(c, @"\b(SELECT|STORE)\b", RegexOptions.IgnoreCase));
        Assert.All(server.Commands.Where(c => c.Contains(" FETCH ")), c => Assert.Contains("BODY.PEEK[]", c));

        // the next pass reads after the cursor; IMAP's "n:*" answer with the last UID is dropped
        var newer = server.Add("INBOX", Mail("d@cust.test", "penny@shop.test", "New", "i3@cust.test"));
        var cursors = new Dictionary<string, FolderCursor>
        {
            ["INBOX"] = new(5, inbox.AdvanceTo!.Value),
            ["Sent Items"] = new(9, sent.AdvanceTo!.Value),
        };
        var second = await reader.FetchAsync(Box(server.Port), cursors, DateTime.UtcNow.AddDays(-7), 10);
        Assert.Equal(new[] { newer }, second.Folders.Single(f => f.Role == MailFolderRole.Inbox).Mails.Select(m => m.Uid));
        Assert.False(second.Folders.Single(f => f.Role == MailFolderRole.Inbox).FirstRun);
        Assert.Empty(second.Folders.Single(f => f.Role == MailFolderRole.Sent).Mails);
        Assert.DoesNotContain("\\Seen", server.FlagsOf("INBOX", newer));
    }

    [Fact]
    public async Task The_real_writer_files_one_seen_copy_in_the_special_use_sent_folder()
    {
        using var server = new FakeImapServer(specialUse: true);
        server.Folder("INBOX");
        server.Folder("Sent");                                   // a decoy by name …
        server.Folder("Verzonden", validity: 3, attributes: "\\Sent"); // … the server's real \Sent folder
        var writer = new MailKitMailboxReader(5000, SecureSocketOptions.None);
        var settings = Box(server.Port) with { Host = "smtp.test", From = "penny@shop.test" };
        var mime = EmailComposer.Build(settings, "c@cust.test", null, "Re: Hi", "Thanks!", Array.Empty<string>(), null,
            null, out var messageId);

        Assert.Equal(SentCopyResult.Appended, await writer.AppendToSentAsync(settings, mime));
        var copy = Assert.Single(server.MailsOf("Verzonden"));
        Assert.Contains("\\Seen", copy.Flags);
        Assert.Contains(messageId.Trim('<', '>'), Encoding.UTF8.GetString(copy.Raw));
        Assert.Empty(server.MailsOf("Sent"));

        // a second copy of the same mail (the server may file SMTP mail itself) is not added
        Assert.Equal(SentCopyResult.AlreadyThere, await writer.AppendToSentAsync(settings, mime));
        Assert.Single(server.MailsOf("Verzonden"));

        // the reader finds the same folder by its flag
        var fetched = await writer.FetchAsync(settings, new Dictionary<string, FolderCursor>(), DateTime.UtcNow.AddDays(-7), 10);
        Assert.Equal("Verzonden", fetched.Folders.Single(f => f.Role == MailFolderRole.Sent).Folder);
    }

    [Fact]
    public async Task No_sent_folder_and_a_refused_login_are_reported_without_secrets()
    {
        using (var server = new FakeImapServer())
        {
            server.Folder("INBOX");
            server.Folder("Archive");
            var reader = new MailKitMailboxReader(5000, SecureSocketOptions.None);
            var fetched = await reader.FetchAsync(Box(server.Port), new Dictionary<string, FolderCursor>(),
                DateTime.UtcNow.AddDays(-7), 10);
            Assert.True(fetched.NoSentFolder);
            Assert.Single(fetched.Folders);
            Assert.Equal(SentCopyResult.NoSentFolder,
                await reader.AppendToSentAsync(Box(server.Port), Mail("penny@shop.test", "x@y.test", "S", "n1@shop.test")));
        }
        using (var server = new FakeImapServer(rejectLogin: true))
        {
            var reader = new MailKitMailboxReader(5000, SecureSocketOptions.None);
            var ex = await Assert.ThrowsAsync<MailboxException>(() => reader.FetchAsync(Box(server.Port),
                new Dictionary<string, FolderCursor>(), DateTime.UtcNow.AddDays(-7), 10));
            Assert.True(ex.AuthFailure);
            Assert.DoesNotContain("imap-secret-9", ex.Message);
        }
    }
}

/// <summary>
/// A loopback IMAP4rev1 server — just enough for MailKit: LOGIN, LIST (plain and
/// SPECIAL-USE), EXAMINE / SELECT, UID SEARCH (SENTSINCE, UID sets with IMAP's
/// <c>n:*</c> rule, HEADER), UID FETCH (BODY[] sets \Seen in a READ-WRITE folder, as
/// a real server does; BODY.PEEK[] never), STORE, APPEND (literal, flags), LOGOUT.
/// Every command line is recorded.
/// </summary>
public sealed class FakeImapServer : IDisposable
{
    public sealed class Mail
    {
        public uint Uid { get; init; }
        public byte[] Raw { get; init; } = Array.Empty<byte>();
        public HashSet<string> Flags { get; } = new(StringComparer.OrdinalIgnoreCase);
        public DateTimeOffset Date { get; init; }
    }

    public sealed class Box
    {
        public string Name { get; init; } = "";
        public string Attributes { get; init; } = "";
        public uint UidValidity { get; init; } = 1;
        public uint UidNext { get; set; } = 1;
        public List<Mail> Mails { get; } = new();
    }

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Task _loop;
    private readonly List<Box> _folders = new();
    private readonly bool _specialUse;
    private readonly bool _rejectLogin;
    public int Port { get; }
    public ConcurrentQueue<string> Commands { get; } = new();

    public FakeImapServer(bool specialUse = false, bool rejectLogin = false)
    {
        _specialUse = specialUse;
        _rejectLogin = rejectLogin;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(ServeAsync);
    }

    public void Folder(string name, uint validity = 1, string attributes = "")
    {
        lock (_folders) _folders.Add(new Box { Name = name, UidValidity = validity, Attributes = attributes });
    }

    public uint Add(string folder, MimeMessage msg, params string[] flags)
    {
        using var ms = new MemoryStream();
        msg.WriteTo(new FormatOptions { NewLineFormat = NewLineFormat.Dos }, ms);
        lock (_folders)
        {
            var box = Find(folder)!;
            var mail = new Mail { Uid = box.UidNext++, Raw = ms.ToArray(), Date = msg.Date };
            foreach (var f in flags) mail.Flags.Add(f);
            box.Mails.Add(mail);
            return mail.Uid;
        }
    }

    public IReadOnlyCollection<string> FlagsOf(string folder, uint uid)
    {
        lock (_folders) return Find(folder)!.Mails.Single(m => m.Uid == uid).Flags.ToList();
    }

    public IReadOnlyList<Mail> MailsOf(string folder)
    {
        lock (_folders) return Find(folder)!.Mails.ToList();
    }

    private Box? Find(string name) => _folders.FirstOrDefault(f =>
        f.Name == name || (f.Name == "INBOX" && name.Equals("INBOX", StringComparison.OrdinalIgnoreCase)));

    private string Caps => "IMAP4rev1" + (_specialUse ? " SPECIAL-USE" : "");

    private async Task ServeAsync()
    {
        try
        {
            while (true)
            {
                using var client = await _listener.AcceptTcpClientAsync();
                await using var stream = client.GetStream();
                try { await SessionAsync(stream); }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }
        }
        catch { /* listener stopped */ }
    }

    private async Task SessionAsync(Stream s)
    {
        await Send(s, $"* OK [CAPABILITY {Caps}] fake IMAP ready\r\n");
        Box? selected = null;
        var readOnly = true;
        while (await ReadLineAsync(s) is { } raw)
        {
            var line = raw;
            byte[]? literal = null;
            var lit = Regex.Match(line, @"\{(\d+)\}$");
            if (lit.Success)
            {
                await Send(s, "+ go ahead\r\n");
                literal = await ReadExactlyAsync(s, int.Parse(lit.Groups[1].Value));
                line = line[..lit.Index] + (await ReadLineAsync(s) ?? "");
            }
            Commands.Enqueue(line);
            var t = Tokenize(line);
            if (t.Count < 2) continue;
            var tag = t[0];
            var cmd = t[1].ToUpperInvariant();
            var args = t.Skip(2).ToList();
            var byUid = false;
            if (cmd == "UID" && args.Count > 0)
            {
                byUid = true;
                cmd = args[0].ToUpperInvariant();
                args = args.Skip(1).ToList();
            }
            var sb = new StringBuilder();
            lock (_folders)
            {
                switch (cmd)
                {
                    case "CAPABILITY":
                        sb.Append($"* CAPABILITY {Caps}\r\n{tag} OK done\r\n");
                        break;
                    case "LOGIN":
                        sb.Append(_rejectLogin
                            ? $"{tag} NO [AUTHENTICATIONFAILED] Invalid credentials\r\n"
                            : $"{tag} OK [CAPABILITY {Caps}] Logged in\r\n");
                        break;
                    case "LIST":
                        List(sb, tag, line, args);
                        break;
                    case "EXAMINE":
                    case "SELECT":
                        selected = Find(args.FirstOrDefault() ?? "");
                        readOnly = cmd == "EXAMINE";
                        if (selected == null) { sb.Append($"{tag} NO [NONEXISTENT] no such folder\r\n"); break; }
                        sb.Append("* FLAGS (\\Answered \\Flagged \\Deleted \\Seen \\Draft)\r\n")
                          .Append(readOnly ? "* OK [PERMANENTFLAGS ()] read-only\r\n" : "* OK [PERMANENTFLAGS (\\Seen \\Deleted)] ok\r\n")
                          .Append($"* {selected.Mails.Count} EXISTS\r\n* 0 RECENT\r\n")
                          .Append($"* OK [UIDVALIDITY {selected.UidValidity}] ok\r\n* OK [UIDNEXT {selected.UidNext}] ok\r\n")
                          .Append($"{tag} OK [{(readOnly ? "READ-ONLY" : "READ-WRITE")}] {cmd} done\r\n");
                        break;
                    case "SEARCH":
                        if (selected == null) { sb.Append($"{tag} BAD nothing selected\r\n"); break; }
                        var hits = Search(selected, args);
                        sb.Append("* SEARCH").Append(string.Concat(hits.Select(m =>
                            " " + (byUid ? m.Uid : (uint)selected.Mails.IndexOf(m) + 1)))).Append("\r\n")
                          .Append($"{tag} OK SEARCH done\r\n");
                        break;
                    case "FETCH":
                        if (selected == null) { sb.Append($"{tag} BAD nothing selected\r\n"); break; }
                        Fetch(sb, selected, readOnly, byUid, args);
                        sb.Append($"{tag} OK FETCH done\r\n");
                        break;
                    case "STORE":
                        if (selected != null && !readOnly && args.Count >= 3)
                            foreach (var m in Set(selected, args[0], byUid))
                                foreach (var f in args[2].Trim('(', ')').Split(' ', StringSplitOptions.RemoveEmptyEntries))
                                    if (args[1].StartsWith('-')) m.Flags.Remove(f); else m.Flags.Add(f);
                        sb.Append(readOnly ? $"{tag} NO read-only\r\n" : $"{tag} OK STORE done\r\n");
                        break;
                    case "APPEND":
                        var target = Find(args.FirstOrDefault() ?? "");
                        if (target == null || literal == null) { sb.Append($"{tag} NO [TRYCREATE] no such folder\r\n"); break; }
                        var parsed = MimeMessage.Load(new MemoryStream(literal));
                        var mail = new Mail { Uid = target.UidNext++, Raw = literal, Date = parsed.Date };
                        foreach (var f in args.Where(a => a.StartsWith('('))
                                     .SelectMany(a => a.Trim('(', ')').Split(' ', StringSplitOptions.RemoveEmptyEntries)))
                            mail.Flags.Add(f);
                        target.Mails.Add(mail);
                        sb.Append($"{tag} OK [APPENDUID {target.UidValidity} {mail.Uid}] APPEND done\r\n");
                        break;
                    case "CLOSE":
                    case "UNSELECT":
                        selected = null;
                        sb.Append($"{tag} OK done\r\n");
                        break;
                    case "NOOP":
                    case "CHECK":
                        sb.Append($"{tag} OK done\r\n");
                        break;
                    case "LOGOUT":
                        sb.Append($"* BYE see you\r\n{tag} OK LOGOUT done\r\n");
                        break;
                    default:
                        sb.Append($"{tag} BAD unknown command\r\n");
                        break;
                }
            }
            await SendRaw(s, sb);
            if (cmd == "LOGOUT") return;
        }
    }

    private void List(StringBuilder sb, string tag, string line, List<string> args)
    {
        var specialUseQuery = line.Contains("SPECIAL-USE", StringComparison.OrdinalIgnoreCase);
        var pattern = specialUseQuery ? "*" : args.Count >= 2 ? args[1] : "*";
        if (pattern == "")
            sb.Append("* LIST (\\Noselect) \"/\" \"\"\r\n");
        else
            foreach (var f in _folders.Where(f => pattern is "*" or "%" || Find(pattern) == f))
            {
                var attrs = "\\HasNoChildren" + (_specialUse && f.Attributes.Length > 0 ? " " + f.Attributes : "");
                sb.Append($"* LIST ({attrs}) \"/\" {Quote(f.Name)}\r\n");
            }
        sb.Append($"{tag} OK LIST done\r\n");
    }

    private static List<Mail> Search(Box box, List<string> args)
    {
        IEnumerable<Mail> hits = box.Mails;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i].ToUpperInvariant())
            {
                case "SENTSINCE":
                case "SINCE":
                    var day = DateTime.ParseExact(args[++i], new[] { "d-MMM-yyyy", "dd-MMM-yyyy" },
                        CultureInfo.InvariantCulture, DateTimeStyles.None);
                    hits = hits.Where(m => m.Date.Date >= day.Date);
                    break;
                case "UID":
                    var set = Set(box, args[++i], byUid: true).ToHashSet();
                    hits = hits.Where(set.Contains);
                    break;
                case "HEADER":
                    var field = args[++i];
                    var value = args[++i];
                    hits = hits.Where(m => MimeMessage.Load(new MemoryStream(m.Raw)).Headers[field]?
                        .Contains(value, StringComparison.OrdinalIgnoreCase) == true);
                    break;
            }
        }
        return hits.ToList();
    }

    /// <summary>A sequence set ("1,3:5", "7:*"); "*" is the highest number in use, and a:b = b:a.</summary>
    private static IEnumerable<Mail> Set(Box box, string set, bool byUid)
    {
        if (box.Mails.Count == 0) return Enumerable.Empty<Mail>();
        uint Num(Mail m) => byUid ? m.Uid : (uint)box.Mails.IndexOf(m) + 1;
        var top = box.Mails.Max(Num);
        uint Parse(string p) => p == "*" ? top : uint.Parse(p);
        var picked = new HashSet<Mail>();
        foreach (var part in set.Split(','))
        {
            var ends = part.Split(':');
            var a = Parse(ends[0]);
            var b = ends.Length > 1 ? Parse(ends[1]) : a;
            var (lo, hi) = a <= b ? (a, b) : (b, a);
            foreach (var m in box.Mails.Where(m => Num(m) >= lo && Num(m) <= hi)) picked.Add(m);
        }
        return box.Mails.Where(picked.Contains);
    }

    private static void Fetch(StringBuilder sb, Box box, bool readOnly, bool byUid, List<string> args)
    {
        var items = args.Count > 1 ? string.Join(" ", args.Skip(1)).ToUpperInvariant() : "";
        var body = items.Contains("BODY[]") || items.Contains("BODY.PEEK[]") || items.Contains("RFC822");
        // like a real server: a non-PEEK body fetch in a READ-WRITE folder marks the mail read
        var marks = !readOnly && (items.Contains("BODY[") || items.Contains("RFC822")) && !items.Contains("PEEK");
        foreach (var m in Set(box, args[0], byUid))
        {
            if (marks) m.Flags.Add("\\Seen");
            var seq = box.Mails.IndexOf(m) + 1;
            sb.Append($"* {seq} FETCH (UID {m.Uid} FLAGS ({string.Join(' ', m.Flags)})");
            if (body)
                sb.Append($" BODY[] {{{m.Raw.Length}}}\r\n").Append(Encoding.Latin1.GetString(m.Raw));
            sb.Append(")\r\n");
        }
    }

    private static string Quote(string s) =>
        Regex.IsMatch(s, @"^[A-Za-z0-9.\-_]+$") ? s : "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>Atoms, "quoted strings" (unquoted) and (parenthesised lists) as one token each.</summary>
    private static List<string> Tokenize(string line)
    {
        var outv = new List<string>();
        var i = 0;
        while (i < line.Length)
        {
            if (line[i] == ' ') { i++; continue; }
            if (line[i] == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < line.Length && line[i] != '"')
                {
                    if (line[i] == '\\' && i + 1 < line.Length) i++;
                    sb.Append(line[i++]);
                }
                i++;
                outv.Add(sb.ToString());
            }
            else if (line[i] == '(')
            {
                var depth = 0;
                var start = i;
                do
                {
                    if (line[i] == '(') depth++;
                    else if (line[i] == ')') depth--;
                    i++;
                } while (i < line.Length && depth > 0);
                outv.Add(line[start..i]);
            }
            else
            {
                var start = i;
                while (i < line.Length && line[i] != ' ')
                {
                    // BODY.PEEK[] and similar keep their brackets
                    if (line[i] == '[')
                        while (i < line.Length && line[i] != ']') i++;
                    i++;
                }
                outv.Add(line[start..Math.Min(i, line.Length)]);
            }
        }
        return outv;
    }

    private static async Task<string?> ReadLineAsync(Stream s)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            var n = await s.ReadAsync(one);
            if (n == 0) return bytes.Count == 0 ? null : Encoding.Latin1.GetString(bytes.ToArray());
            if (one[0] == '\n')
            {
                if (bytes.Count > 0 && bytes[^1] == '\r') bytes.RemoveAt(bytes.Count - 1);
                return Encoding.Latin1.GetString(bytes.ToArray());
            }
            bytes.Add(one[0]);
        }
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream s, int count)
    {
        var buf = new byte[count];
        await s.ReadExactlyAsync(buf);
        return buf;
    }

    private static Task Send(Stream s, string text) => s.WriteAsync(Encoding.Latin1.GetBytes(text)).AsTask();

    private static Task SendRaw(Stream s, StringBuilder sb) => Send(s, sb.ToString());

    public void Dispose()
    {
        try { _listener.Stop(); } catch { }
        try { _loop.Wait(2000); } catch { }
    }
}
