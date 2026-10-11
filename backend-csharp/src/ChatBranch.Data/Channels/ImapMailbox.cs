using System.Collections.Concurrent;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace ChatBranch.Data.Channels;

/// <summary>Which folder of a mailbox a batch came from.</summary>
public enum MailFolderRole { Inbox, Sent }

/// <summary>Where the sync stands in one folder: its UIDVALIDITY and the last UID considered.</summary>
public sealed record FolderCursor(uint UidValidity, uint LastUid);

/// <summary>One fetched mail. <see cref="Message"/> null = the server would not hand it over
/// (or it could not be parsed): counted as an error, and the cursor still moves past it.</summary>
public sealed record FetchedMail(uint Uid, MimeMessage? Message);

/// <summary>
/// What one folder yielded this pass, oldest UID first. <see cref="AdvanceTo"/> is
/// where the folder's cursor stands once every mail in <see cref="Mails"/> has been
/// handled (null = leave it: a first run that found nothing and could not learn the
/// folder's top keeps reading its window).
/// </summary>
public sealed record FolderFetch(MailFolderRole Role, string Folder, uint UidValidity, bool FirstRun,
    IReadOnlyList<FetchedMail> Mails, uint? AdvanceTo);

/// <summary>One pass over a mailbox: the Inbox, then the Sent folder when there is one.</summary>
public sealed record MailboxFetch(IReadOnlyList<FolderFetch> Folders)
{
    /// <summary>No folder flagged <c>\Sent</c> and none of <see cref="SentFolders.FallbackNames"/>.</summary>
    public bool NoSentFolder { get; init; }
    /// <summary>A folder that could not be read this pass (secret-free); the rest still counts.</summary>
    public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();
}

/// <summary>
/// The IMAP side of email. Folders are opened READ-ONLY (IMAP EXAMINE), so reading
/// a mail can never set <c>\Seen</c> — a mail a person already read in Outlook is
/// still imported, and ChatBranch never marks anything read there. Each folder is
/// read from its cursor (<see cref="MailboxCursorPlan"/>): the window
/// (<c>SENTSINCE</c>) on a first run or after a UIDVALIDITY change, else the UIDs
/// after the last one considered — at most <c>max</c> per folder, oldest first.
/// </summary>
public interface IMailboxReader
{
    /// <param name="cursors">the mailbox's stored cursors by folder full name</param>
    Task<MailboxFetch> FetchAsync(EmailServerSettings mailbox, IReadOnlyDictionary<string, FolderCursor> cursors,
        DateTime sinceUtc, int max, CancellationToken ct = default);
}

public enum SentCopyResult
{
    Appended,
    /// <summary>The server had already filed it (Gmail and many Exchange setups copy SMTP mail to Sent themselves).</summary>
    AlreadyThere,
    NoSentFolder,
}

/// <summary>Writes to a mailbox: a copy of a mail ChatBranch sent, into its Sent folder (flagged <c>\Seen</c>).</summary>
public interface IMailboxWriter
{
    /// <exception cref="MailboxException">the mailbox could not be reached or refused the copy</exception>
    Task<SentCopyResult> AppendToSentAsync(EmailServerSettings mailbox, MimeMessage message,
        CancellationToken ct = default);
}

/// <summary>An IMAP failure with a readable, secret-free message.</summary>
public sealed class MailboxException : Exception
{
    public bool AuthFailure { get; }
    public MailboxException(string message, bool authFailure) : base(message) => AuthFailure = authFailure;
}

/// <summary>The UID-cursor rule both the real reader and the tests' fake mailbox follow. Pure.</summary>
public sealed record FolderPlan(bool FirstRun, IReadOnlyList<uint> Fetch, uint? AdvanceTo);

public static class MailboxCursorPlan
{
    /// <summary>No cursor yet, or the folder was rebuilt (another UIDVALIDITY): read the window again.</summary>
    public static bool IsFirstRun(FolderCursor? stored, uint uidValidity) =>
        stored is null || stored.UidValidity != uidValidity;

    /// <summary>
    /// Which UIDs to fetch this pass and where the cursor stands afterwards.
    /// <paramref name="found"/> is what the server's search answered — the window on a
    /// first run, <c>UID last+1:*</c> after it. IMAP answers <c>n:*</c> with the
    /// folder's highest UID even when that is ≤ last, so everything ≤ the cursor is
    /// dropped here. Oldest first, at most <paramref name="max"/>; a longer backlog
    /// leaves the cursor on the last UID taken so the next pass carries on from
    /// there. When everything that qualified was taken, the cursor may move up to the
    /// folder's top (UIDNEXT − 1, read when the folder was opened — before the search,
    /// so nothing that arrives later is passed over): on a first run that is what
    /// marks the older mail outside the window as considered.
    /// </summary>
    public static FolderPlan Plan(FolderCursor? stored, uint uidValidity, IEnumerable<uint> found, uint? uidNext,
        int max)
    {
        var first = IsFirstRun(stored, uidValidity);
        var last = first ? 0u : stored!.LastUid;
        var pending = found.Where(u => u > last).Distinct().OrderBy(u => u).ToList();
        var fetch = pending.Take(Math.Max(1, max)).ToList();
        if (pending.Count > fetch.Count) return new FolderPlan(first, fetch, fetch[^1]);
        var top = Math.Max(last, fetch.Count > 0 ? fetch[^1] : 0u);
        if (uidNext is uint next && next > 0) return new FolderPlan(first, fetch, Math.Max(top, next - 1));
        return new FolderPlan(first, fetch, first && fetch.Count == 0 ? null : top);
    }
}

/// <summary>
/// Finding a mailbox's Sent folder: the server's SPECIAL-USE / XLIST <c>\Sent</c>
/// folder when it advertises one, else a folder flagged <c>\Sent</c> in the listing,
/// else the first of <see cref="FallbackNames"/> that exists — compared
/// case-insensitively, with '.' or '/' as the separator, with or without the
/// personal namespace's prefix. Pure, so it is unit-tested with folder names.
/// </summary>
public static class SentFolders
{
    public static readonly IReadOnlyList<string> FallbackNames = new[] { "Sent", "Sent Items", "Sent Messages", "INBOX.Sent" };

    public sealed record Listed(string FullName, char Separator, bool FlaggedSent);

    /// <summary>The full name of the Sent folder among <paramref name="folders"/>, else null.</summary>
    public static string? Pick(IEnumerable<Listed> folders, string namespacePath = "", char namespaceSeparator = '.')
    {
        var list = folders.Where(f => !string.IsNullOrWhiteSpace(f.FullName)).ToList();
        if (list.FirstOrDefault(f => f.FlaggedSent) is { } flagged) return flagged.FullName;
        var prefix = (namespacePath ?? "").TrimEnd(namespaceSeparator);
        foreach (var name in FallbackNames)
        {
            var hit = list.FirstOrDefault(f => Names(f, prefix)
                .Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)));
            if (hit != null) return hit.FullName;
        }
        return null;
    }

    /// <summary>The folder's name as written with '.' — whole, and without the namespace prefix.</summary>
    private static IEnumerable<string> Names(Listed f, string prefix)
    {
        var full = f.Separator is '\0' or '.' ? f.FullName : f.FullName.Replace(f.Separator, '.');
        yield return full;
        var p = prefix.Length == 0 ? "" : (f.Separator is '\0' or '.' ? prefix : prefix.Replace(f.Separator, '.')) + ".";
        if (p.Length > 0 && full.Length > p.Length && full.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            yield return full[p.Length..];
    }
}

/// <summary>The real IMAP client (MailKit) — the reader and the Sent-folder writer.</summary>
public sealed class MailKitMailboxReader : IMailboxReader, IMailboxWriter
{
    private readonly int _timeoutMs;
    private readonly SecureSocketOptions? _security;

    /// <param name="security">tests only: force the socket options (a loopback server speaks plain text)</param>
    public MailKitMailboxReader(int timeoutMs = 20_000, SecureSocketOptions? security = null)
    {
        _timeoutMs = timeoutMs;
        _security = security;
    }

    public Task<MailboxFetch> FetchAsync(EmailServerSettings mailbox, IReadOnlyDictionary<string, FolderCursor> cursors,
        DateTime sinceUtc, int max, CancellationToken ct = default) =>
        RunAsync(mailbox, "IMAP sync failed", async (client, token) =>
        {
            var folders = new List<FolderFetch>
            {
                await ReadFolderAsync(client, client.Inbox, MailFolderRole.Inbox, cursors, sinceUtc, max, token),
            };
            var noSent = false;
            var problems = new List<string>();
            try
            {
                var sent = await FindSentFolderAsync(client, token);
                if (sent == null) noSent = true;
                else folders.Add(await ReadFolderAsync(client, sent, MailFolderRole.Sent, cursors, sinceUtc, max, token));
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // the Inbox already read still counts this pass
                problems.Add($"the Sent folder could not be read: {MailKitEmailTransport.Scrub(ex.Message, mailbox)}");
            }
            return new MailboxFetch(folders) { NoSentFolder = noSent, Problems = problems };
        }, ct);

    public Task<SentCopyResult> AppendToSentAsync(EmailServerSettings mailbox, MimeMessage message,
        CancellationToken ct = default) =>
        RunAsync(mailbox, "IMAP copy to Sent failed", async (client, token) =>
        {
            var sent = await FindSentFolderAsync(client, token);
            if (sent == null) return SentCopyResult.NoSentFolder;
            var id = EmailComposer.Bare(message.MessageId ?? "");
            if (id.Length > 0)
            {
                await sent.OpenAsync(FolderAccess.ReadOnly, token);
                var already = await sent.SearchAsync(SearchQuery.HeaderContains("Message-ID", id), token);
                if (already.Count > 0) return SentCopyResult.AlreadyThere;
            }
            var date = message.Date == DateTimeOffset.MinValue ? DateTimeOffset.UtcNow : message.Date;
            await sent.AppendAsync(new AppendRequest(message, MessageFlags.Seen, date), token);
            return SentCopyResult.Appended;
        }, ct);

    private async Task<T> RunAsync<T>(EmailServerSettings mailbox, string what,
        Func<ImapClient, CancellationToken, Task<T>> work, CancellationToken ct)
    {
        using var client = new ImapClient { Timeout = _timeoutMs };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeoutMs * 3);
        try
        {
            // 143 speaks STARTTLS; everything else (993 by default) implicit TLS, as Django's IMAP4_SSL
            var security = _security
                           ?? (mailbox.ImapPort == 143 ? SecureSocketOptions.StartTls : SecureSocketOptions.SslOnConnect);
            await client.ConnectAsync(mailbox.ImapHost, mailbox.ImapPort, security, cts.Token);
            await client.AuthenticateAsync(mailbox.ImapUsername, mailbox.ImapPassword, cts.Token);
            var result = await work(client, cts.Token);
            try { await client.DisconnectAsync(true, cts.Token); } catch { /* the work is done */ }
            return result;
        }
        catch (AuthenticationException ex)
        {
            throw new MailboxException($"IMAP login refused: {MailKitEmailTransport.Scrub(ex.Message, mailbox)}",
                authFailure: true);
        }
        catch (Exception ex) when (ex is not MailboxException)
        {
            throw new MailboxException($"{what}: {MailKitEmailTransport.Scrub(ex.Message, mailbox)}", authFailure: false);
        }
    }

    private static async Task<FolderFetch> ReadFolderAsync(ImapClient client, IMailFolder folder, MailFolderRole role,
        IReadOnlyDictionary<string, FolderCursor> cursors, DateTime sinceUtc, int max, CancellationToken ct)
    {
        // EXAMINE, never SELECT: in a read-only folder nothing we fetch can gain \Seen
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);
        var validity = folder.UidValidity;
        cursors.TryGetValue(folder.FullName, out var stored);
        IList<UniqueId> found;
        if (MailboxCursorPlan.IsFirstRun(stored, validity))
            found = await folder.SearchAsync(SearchQuery.SentSince(sinceUtc.Date), ct); // seen or not
        else if (stored!.LastUid == uint.MaxValue)
            found = Array.Empty<UniqueId>();
        else
            found = await folder.SearchAsync(
                SearchQuery.Uids(new UniqueIdRange(validity, stored.LastUid + 1, uint.MaxValue)), ct);

        var plan = MailboxCursorPlan.Plan(stored, validity, found.Select(u => u.Id), folder.UidNext?.Id, max);
        var mails = new List<FetchedMail>();
        foreach (var uid in plan.Fetch)
        {
            MimeMessage? message = null;
            // a broken (or meanwhile deleted) message must not stall the rest; a dropped
            // connection must not be mistaken for one — that fails the pass, cursor untouched
            try { message = await folder.GetMessageAsync(new UniqueId(validity, uid), ct); }
            catch (Exception) when (!ct.IsCancellationRequested && client.IsConnected) { }
            mails.Add(new FetchedMail(uid, message));
        }
        return new FolderFetch(role, folder.FullName, validity, plan.FirstRun, mails, plan.AdvanceTo);
    }

    /// <summary>The mailbox's Sent folder (see <see cref="SentFolders"/>), else null.</summary>
    private static async Task<IMailFolder?> FindSentFolderAsync(ImapClient client, CancellationToken ct)
    {
        if ((client.Capabilities & (ImapCapabilities.SpecialUse | ImapCapabilities.XList)) != 0)
        {
            try
            {
                if (client.GetFolder(SpecialFolder.Sent) is { } special) return special;
            }
            catch (NotSupportedException) { /* fall back to the usual names */ }
        }
        if (client.PersonalNamespaces.Count == 0) return null;
        var ns = client.PersonalNamespaces[0];
        var all = await client.GetFoldersAsync(ns, StatusItems.None, false, ct);
        var pick = SentFolders.Pick(
            all.Select(f => new SentFolders.Listed(f.FullName, f.DirectorySeparator,
                f.Attributes.HasFlag(FolderAttributes.Sent))),
            ns.Path, ns.DirectorySeparator);
        return pick == null ? null : all.First(f => f.FullName == pick);
    }
}

/// <summary>
/// Saves a copy of each mail ChatBranch sends into the mailbox's Sent folder, so the
/// person's own mail app (Outlook…) shows it too. Runs OFF the request on the thread
/// pool with its own timeout: the send has already succeeded and is never delayed,
/// failed or retried because of it. Failures (and a mailbox with no Sent folder) are
/// logged without secrets. A mailbox without IMAP settings is skipped.
/// </summary>
public sealed class SentFolderCopier
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    private readonly IMailboxWriter _writer;
    private readonly ILogger<SentFolderCopier> _log;
    private readonly ConcurrentDictionary<Guid, Task> _running = new();

    public SentFolderCopier(IMailboxWriter writer, ILogger<SentFolderCopier> log)
    {
        _writer = writer;
        _log = log;
    }

    public void Enqueue(EmailServerSettings server, MimeMessage message)
    {
        if (!server.ImapConfigured) return;
        var id = Guid.NewGuid();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _running[id] = done.Task;
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(Timeout);
                var result = await _writer.AppendToSentAsync(server, message, cts.Token);
                if (result == SentCopyResult.NoSentFolder)
                    _log.LogInformation("Mailbox {Mailbox} has no Sent folder; the copy of {MessageId} was not saved.",
                        server.Label, message.MessageId);
            }
            catch (Exception ex)
            {
                var detail = ex is MailboxException ? ex.Message : MailKitEmailTransport.Scrub(ex.Message, server);
                _log.LogWarning("Could not save a copy of sent mail {MessageId} to the Sent folder of {Mailbox} " +
                                "(the mail itself was sent): {Detail}", message.MessageId, server.Label, detail);
            }
            finally
            {
                _running.TryRemove(id, out _);
                done.TrySetResult();
            }
        });
    }

    /// <summary>Wait until every queued copy has finished (or the timeout passes) — tests.</summary>
    public async Task WhenIdleAsync(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (!_running.IsEmpty && DateTime.UtcNow < until)
            await Task.WhenAny(Task.WhenAll(_running.Values), Task.Delay(50));
    }
}
