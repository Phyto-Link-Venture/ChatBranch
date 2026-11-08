using System.Security.Claims;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Audit;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Sidecars;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using ChannelIds = ChatBranch.Domain.Channels;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// <c>POST /api/messaging/send-file</c> — send a file WITHOUT spelling it out.
///
/// The old way for an AI (or any API client) to attach a file was to put the whole
/// thing in the request as base64, which means the model writing every character of
/// it: on 8 Nov 2026 a phone photo had to be shrunk to 270×480 (about 15 KB) before it
/// could be sent at all, it still became 20,272 characters, and sending it to WhatsApp
/// and email — writing that string out twice — took about thirteen minutes. A normal
/// 2–5 MB photo or scanned PDF was simply out of reach, and base64 adds a third to the
/// size on top.
///
/// So this route takes a SHORT reference and lets the server do the carrying:
///
/// <list type="bullet">
/// <item><c>media_url</c> — any https link, on EVERY channel (before, only Messenger,
/// Instagram and Telegram took one). The server fetches it
/// (<see cref="IRemoteMediaFetcher"/>: https only, 3 hops, 30 s, the channel's size
/// limit, no private or local address, Google Drive share links understood including
/// private files through a connected account).</item>
/// <item><c>source_message_id</c> — a file ChatBranch ALREADY stores, resent to another
/// conversation, across channels: a customer's invoice from an email thread into a
/// WhatsApp chat. (<c>conversations_forward_message</c> cannot do this — a WhatsApp
/// forward is same-channel by nature.)</item>
/// <item><c>file_id</c> — a file somebody uploaded to the MEDIA LIBRARY once
/// (<see cref="MediaLibraryEndpoints"/>), which is the answer for a photo or a scan
/// that exists only on somebody's phone and is nowhere online at all.</item>
/// <item><c>file_base64</c> — still there, unchanged, for a file that is nowhere
/// online and not in the library either.</item>
/// </list>
///
/// And <c>targets</c> carries one file to several conversations in ONE call: it is
/// fetched or decoded ONCE, then each target goes through its own guards and gets its
/// own result. Nothing is all-or-nothing — a refusal on one target never costs the
/// others, which is why the answer is a list and the HTTP status is 200 whenever the
/// file itself was readable.
/// </summary>
public static class MessagingSendFileEndpoints
{
    /// <summary>Most conversations one call may carry a file to.</summary>
    public const int MaxTargets = 20;

    /// <summary>WhatsApp's own limit; every other channel allows more.</summary>
    public const long WhatsAppMaxBytes = 16L * 1024 * 1024;
    public const long OtherMaxBytes = 20L * 1024 * 1024;

    /// <summary>One conversation to deliver to.</summary>
    private sealed record Target(string Channel, string Key, int? ThreadId, string? Company, string? Subject);

    public static void MapMessagingSendFileEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/messaging/send-file", async (JsonElement body, HttpContext ctx, AppDbContext db,
            ThreadStore store, ScopingService sc, OutboundSender sender, IWhatsAppCloudClient wa,
            MediaStore media, VoiceAgentBridge bridge, IRemoteMediaFetcher fetcher, ClaimsPrincipal me) =>
        {
            // ---- the conversations ------------------------------------------
            var targets = ReadTargets(body);
            if (targets.Count == 0)
                return Problem(422, "no_target",
                    "Name a conversation (channel + key) or a targets list.");
            if (targets.Count > MaxTargets)
                return Problem(422, "too_many_targets",
                    $"At most {MaxTargets} conversations per call — send the rest in another call.");
            foreach (var t in targets)
                if (!MessagingNotifyEndpoints.NotifyChannels.Contains(t.Channel))
                    return Problem(422, "bad_channel",
                        $"channel must be one of {string.Join(", ", MessagingNotifyEndpoints.NotifyChannels)}.");

            // the smallest limit among the targets, so one file suits them all
            var maxBytes = targets.Any(t => t.Channel == ChannelIds.WhatsApp) ? WhatsAppMaxBytes : OtherMaxBytes;
            var force = Flag(body, "force");
            var caption = (Str(body, "caption") ?? "").Trim();

            // ---- the file, ONCE ---------------------------------------------
            var file = await ReadFileAsync(body, db, sc, me, media, bridge, fetcher, maxBytes, ctx.RequestAborted);
            if (file.Error is { } problem) return problem;
            var found = file.File!;

            // ---- each target, with its own guards ---------------------------
            var results = new List<object>();
            var sent = 0;
            foreach (var target in targets)
            {
                var outcome = await DeliverAsync(db, store, sc, sender, wa, media, bridge, me,
                    target, found, caption, force);
                if (outcome.Ok) sent++;
                results.Add(outcome.Payload);
            }

            AuditTrail.Add(db, "message.send_file", string.Join(" ", targets.Select(t => $"{t.Channel}:{t.Key}")),
                $"{found.Kind} {found.FileName} ({found.Content.Length} bytes) from {file.From} → {sent}/{targets.Count} sent");
            await db.SaveChangesAsync();

            return Results.Ok(new
            {
                file = new
                {
                    name = found.FileName,
                    mime = found.Mime,
                    kind = found.Kind,
                    size = found.Content.Length,
                    source = file.From,
                },
                sent,
                failed = targets.Count - sent,
                results,
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("chats.send");
    }

    // =========================================================================
    // the file
    // =========================================================================

    /// <summary>The file, where it came from (url | message | base64), or the refusal.</summary>
    private sealed record Loaded(RemoteFile? File, string From, IResult? Error);

    /// <summary>
    /// The file to send, from whichever reference the request gave. Exactly one source
    /// is read, in the order a caller is most likely to mean it.
    /// </summary>
    private static async Task<Loaded> ReadFileAsync(JsonElement body, AppDbContext db, ScopingService sc,
        ClaimsPrincipal me, MediaStore media, VoiceAgentBridge bridge, IRemoteMediaFetcher fetcher,
        long maxBytes, CancellationToken ct)
    {
        var given = (Str(body, "file_name") ?? "").Trim();

        if ((Str(body, "media_url") ?? "").Trim() is { Length: > 0 } url)
        {
            var fetched = await fetcher.FetchAsync(url, maxBytes, null, ct);
            if (!fetched.Ok)
                return new Loaded(null, "url", Problem(StatusFor(fetched.Error), CodeFor(fetched.Error),
                    fetched.Detail));
            var file = fetched.File!;
            return new Loaded(
                given.Length > 0 ? file with { FileName = MessagingMedia.SafeName(given) } : file, "url", null);
        }

        if (IntOrNull(body, "source_message_id") is int messageId)
        {
            var reused = await FromMessageAsync(db, sc, me, media, bridge, messageId, maxBytes);
            return reused;
        }

        if ((Str(body, "file_id") ?? "").Trim() is { Length: > 0 } fileId)
        {
            var item = await MediaLibraryEndpoints.ForSendAsync(db, sc, me, fileId);
            if (item == null)
                return new Loaded(null, "library", Problem(404, "no_such_file",
                    "No file with that id in the media library (it may have expired, or belong to "
                    + "another outlet)."));
            var path = media.ResolveLibrary(item.Path);
            if (path == null)
                return new Loaded(null, "library", Problem(410, "file_gone",
                    "That library file is no longer on this server."));
            if (new FileInfo(path).Length > maxBytes)
                return new Loaded(null, "library", Problem(413, "too_large",
                    $"That file is larger than this channel allows ({maxBytes / (1024 * 1024)} MB)."));
            await MediaLibraryEndpoints.MarkSentAsync(db, item.FileId);
            return new Loaded(
                new RemoteFile(await File.ReadAllBytesAsync(path, ct),
                    given.Length > 0 ? MessagingMedia.SafeName(given) : item.Name, item.Mime, item.Kind),
                "library", null);
        }

        if ((Str(body, "file_base64") ?? "").Trim() is { Length: > 0 } base64)
        {
            if (given.Length == 0)
                return new Loaded(null, "base64", Problem(422, "file_name_required",
                    "file_name is required with file_base64 — its extension decides the file type."));
            byte[] bytes;
            try { bytes = Convert.FromBase64String(base64); }
            catch
            {
                return new Loaded(null, "base64", Problem(422, "bad_base64", "file_base64 is not valid base64."));
            }
            if (bytes.Length == 0)
                return new Loaded(null, "base64", Problem(422, "nothing_to_send", "file_base64 is empty."));
            if (bytes.Length > maxBytes)
                return new Loaded(null, "base64", Problem(413, "too_large",
                    $"That file is larger than this channel allows ({maxBytes / (1024 * 1024)} MB)."));
            var name = MessagingMedia.SafeName(given);
            if (!Path.HasExtension(name))
                return new Loaded(null, "base64", Problem(422, "file_name_required",
                    "file_name needs an extension (for example photo.jpg) — it decides the file type."));
            return new Loaded(
                new RemoteFile(bytes, name, MessagingMedia.ContentTypeFor(name), MessagingMedia.TypeFor(name)),
                "base64", null);
        }

        return new Loaded(null, "", Problem(422, "nothing_to_send",
            "Give media_url, file_id (from the media library), source_message_id, or file_base64 "
            + "with file_name."));
    }

    /// <summary>
    /// A file ChatBranch already stores, read back off the message that carries it. The
    /// caller must be able to SEE that conversation — a message id is not a key to
    /// another outlet's attachments.
    /// </summary>
    private static async Task<Loaded> FromMessageAsync(AppDbContext db, ScopingService sc, ClaimsPrincipal me,
        MediaStore media, VoiceAgentBridge bridge, int messageId, long maxBytes)
    {
        var row = await db.Messages.AsNoTracking().Include(m => m.Thread)
            .FirstOrDefaultAsync(m => m.Id == messageId);
        if (row?.Thread == null)
            return new Loaded(null, "message", Problem(404, "no_such_message", "No message with that id."));
        if (!ThreadAccessFilter.InScope(db, sc, me, row.Thread.EntityId))
            return new Loaded(null, "message", Problem(403, "out_of_scope",
                "That message is in a conversation outside your scope."));
        if (row.RecalledAt != null)
            return new Loaded(null, "message", Problem(409, "recalled",
                "That message was deleted on the channel, so its file is not ours to resend."));
        if (row.MediaPath.Length == 0)
            return new Loaded(null, "message", Problem(422, "no_file", "That message carries no file."));

        // the file lives under the SOURCE conversation's own channel root
        var path = media.Resolve(row.Thread.Channel, row.MediaPath)
                   ?? MessagingMedia.Resolve(bridge.DataDir, row.MediaPath);
        if (path == null || !File.Exists(path))
            return new Loaded(null, "message", Problem(410, "file_gone",
                "That message's file is no longer on this server."));
        var info = new FileInfo(path);
        if (info.Length > maxBytes)
            return new Loaded(null, "message", Problem(413, "too_large",
                $"That file is larger than this channel allows ({maxBytes / (1024 * 1024)} MB)."));

        var bytes = await File.ReadAllBytesAsync(path);
        var name = row.MediaName.Length > 0
            ? MessagingMedia.SafeName(row.MediaName)
            : MessagingMedia.SafeName(Path.GetFileName(path));
        var mime = row.MediaMime.Length > 0 ? row.MediaMime : MessagingMedia.ContentTypeFor(name);
        var kind = row.MediaType.Length > 0 ? row.MediaType : MessagingMedia.TypeFor(name);
        return new Loaded(new RemoteFile(bytes, name, mime, kind), "message", null);
    }

    // =========================================================================
    // one target
    // =========================================================================

    private sealed record Outcome(bool Ok, object Payload);

    /// <summary>
    /// Deliver the file to one conversation, through exactly the guards that
    /// conversation's own send route applies: scope, opt-out, the cold-file guard, the
    /// WhatsApp 24h window, Messenger's / Instagram's messaging window. A refusal is
    /// this target's result, never the call's.
    /// </summary>
    private static async Task<Outcome> DeliverAsync(AppDbContext db, ThreadStore store, ScopingService sc,
        OutboundSender sender, IWhatsAppCloudClient wa, MediaStore media, VoiceAgentBridge bridge,
        ClaimsPrincipal me, Target target, RemoteFile file, string caption, bool force)
    {
        object Refused(string code, string detail) => new
        {
            channel = target.Channel, key = target.Key, ok = false, code, detail,
        };

        var opened = await ThreadOpen.FindOrOpenAsync(db, store, sc, me, target.Channel,
            target.Channel is ChannelIds.WhatsApp or ChannelIds.Sms ? PhoneMatch.Digits(target.Key) : target.Key,
            target.ThreadId, target.Company);
        if (!opened.Ok)
            return new Outcome(false, Refused(
                opened.Pick == ThreadResolver.Outcome.Ambiguous ? "company_required" : "out_of_scope",
                opened.Pick == ThreadResolver.Outcome.Ambiguous
                    ? "This customer has a conversation in more than one of your companies — name one with thread_id or company."
                    : "That conversation is outside your scope."));
        var thread = opened.Thread!;
        if (!ThreadAccessFilter.InScope(db, sc, me, thread.EntityId))
            return new Outcome(false, Refused("out_of_scope", "That conversation is outside your scope."));

        if (thread.OptedOut && !force)
            return new Outcome(false, Refused("opted_out",
                $"This contact sent STOP on {thread.Channel}. A manual exception needs force."));

        if (file.Content.Length > (thread.Channel == ChannelIds.WhatsApp ? WhatsAppMaxBytes : OtherMaxBytes))
            return new Outcome(false, Refused("too_large", $"{thread.Channel} does not allow a file this size."));

        var human = MessagingTransportEndpoints.IsHumanSender(me);
        ChannelAccount? account = null;
        if (thread.Channel == ChannelIds.WhatsApp)
        {
            var routing = new Routing(db, sc);
            var route = routing.ForThread(thread, RouteNeed.Conversation, apply: true);
            if (!route.Ok) return new Outcome(false, Refused("no_account", route.Error));
            account = route.Account;
            var live = account != null && wa.IsConfigured(account);

            // a file into a conversation that has never written to us, on a personal
            // number, is the same ban risk as cold free text
            if (account != null && ColdBlocked(db, sc, thread, account) && !force)
                return new Outcome(false, Refused("cold_file",
                    "This number has never received a message from this contact. Cold outbound from a personal "
                    + "number carries ban risk — have the contact message first, or send an approved template."));
            if (live && !WindowOpen(db, thread, account))
                return new Outcome(false, Refused("window_closed",
                    "WhatsApp's 24-hour window is closed for this conversation — only a template can reopen it."));
        }
        else if (sender.SocialWindowBlock(thread, thread.AccountId, human) is { } windowClosed)
        {
            return new Outcome(false, Refused("window_closed", windowClosed));
        }

        Message msg;
        try
        {
            msg = thread.Channel == ChannelIds.WhatsApp
                ? await SendWhatsAppAsync(sender, media, thread, file, caption, account?.Id)
                : SendMessaging(sender, bridge, thread, file, caption, target.Subject, human);
        }
        catch (Exception e)
        {
            return new Outcome(false, Refused("send_failed", e.Message));
        }

        // a person attaching a file answers an open handover; an API key or an MCP
        // client is automation and must not
        if (human) AiControlsEndpoints.HumanReplied(db, thread, msg, me);

        return new Outcome(msg.Status != "failed", new
        {
            channel = thread.Channel,
            key = thread.Key,
            thread_id = thread.Id,
            ok = msg.Status != "failed",
            // the truth, as everywhere: "sandbox" means nothing left the building
            status = msg.Status,
            message_id = msg.Id,
            external_id = msg.ExternalId,
            error = msg.Error,
            url = $"/chats?thread={thread.Channel}:{thread.Key}",
        });
    }

    private static async Task<Message> SendWhatsAppAsync(OutboundSender sender, MediaStore media, Thread thread,
        RemoteFile file, string caption, int? accountId)
    {
        var ext = MediaStore.ExtFor(file.Mime, file.FileName);
        var rel = await media.SaveAsync(ChannelIds.WhatsApp, thread.Key, file.Content, ext);
        return sender.SendMedia(thread, rel, file.Kind, file.Mime, caption, file.FileName,
            MessageSource.Agent, accountId);
    }

    private static Message SendMessaging(OutboundSender sender, VoiceAgentBridge bridge, Thread thread,
        RemoteFile file, string caption, string? subject, bool human)
    {
        var rel = MessagingMedia.Save(bridge.DataDir, thread.Channel, thread.Key, file.FileName, file.Content);
        // Messenger / Instagram upload the stored file through the window-aware path;
        // email, Telegram and the newer channels attach it to their own send
        if (SocialWindow.Applies(thread.Channel))
            return sender.SendMedia(thread, rel, file.Kind, file.Mime,
                caption.Length > 0 ? caption : null, file.FileName, MessageSource.Agent, null, humanAgent: human);
        var outbound = new OutboundMedia(MessagingMedia.Resolve(bridge.DataDir, rel)!, rel, file.Kind, file.FileName);
        return sender.Send(thread, caption.Length > 0 ? caption : $"[{file.Kind}]", MessageSource.Agent, null,
            subject: string.IsNullOrWhiteSpace(subject) ? null : subject, media: outbound);
    }

    // =========================================================================
    // helpers
    // =========================================================================

    private static List<Target> ReadTargets(JsonElement body)
    {
        var list = new List<Target>();
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("targets", out var arr)
            && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var channel = (Str(item, "channel") ?? "").Trim().ToLowerInvariant();
                var key = (Str(item, "key") ?? "").Trim();
                if (channel.Length == 0 || key.Length == 0) continue;
                list.Add(new Target(channel, key, IntOrNull(item, "thread_id"), Str(item, "company"),
                    Str(item, "subject")));
            }
            return list;
        }
        // the single-conversation shape, the same keys the thread routes use
        var one = (Str(body, "channel") ?? "").Trim().ToLowerInvariant();
        var oneKey = (Str(body, "key") ?? "").Trim();
        if (one.Length > 0 && oneKey.Length > 0)
            list.Add(new Target(one, oneKey, IntOrNull(body, "thread_id"), Str(body, "company"),
                Str(body, "subject")));
        return list;
    }

    private static bool ColdBlocked(AppDbContext db, ScopingService sc, Thread thread, ChannelAccount account)
    {
        var role = account.Config.TryGetValue("role", out var raw) && (raw?.ToString() ?? "") is { Length: > 0 } r
            ? r
            : account.Provider == Providers.Baileys ? "conversation" : "reminder";
        if (role != "conversation") return false;
        var guard = (sc.SettingValue("cold_text_guard", thread.EntityId) ?? "").Trim().ToLowerInvariant();
        if (guard.Length == 0) guard = "block";
        return guard == "block"
               && !db.Messages.Any(m => m.ThreadId == thread.Id && m.Direction == MessageDirection.In);
    }

    private static bool WindowOpen(AppDbContext db, Thread thread, ChannelAccount? account)
    {
        if (account == null || account.Provider == Providers.Baileys) return true;
        var lastIn = db.Messages.AsNoTracking()
            .Where(m => m.ThreadId == thread.Id && m.Direction == MessageDirection.In)
            .Max(m => (DateTime?)m.CreatedAt);
        return lastIn is DateTime ts && DateTime.UtcNow < ts.AddHours(24);
    }

    private static int StatusFor(RemoteMediaError error) => error switch
    {
        RemoteMediaError.NotFound => 404,
        RemoteMediaError.NoAccess => 403,
        RemoteMediaError.TooLarge => 413,
        RemoteMediaError.Timeout => 504,
        _ => 422,
    };

    private static string CodeFor(RemoteMediaError error) => error switch
    {
        RemoteMediaError.NotHttps => "bad_media_url",
        RemoteMediaError.Blocked => "blocked_media_url",
        RemoteMediaError.NotFound => "media_not_found",
        RemoteMediaError.NoAccess => "media_no_access",
        RemoteMediaError.TooLarge => "too_large",
        RemoteMediaError.Timeout => "media_timeout",
        RemoteMediaError.Unreadable => "media_unreadable",
        _ => "media_failed",
    };

    private static IResult Problem(int status, string code, string detail) =>
        Results.Json(new { detail, code }, statusCode: status);

    private static string? Str(JsonElement body, string key) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(key, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString()
                : v.ValueKind == JsonValueKind.Null ? null : v.ToString()
            : null;

    private static int? IntOrNull(JsonElement body, string key) =>
        int.TryParse(Str(body, key), out var n) && n > 0 ? n : null;

    private static bool Flag(JsonElement body, string key) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(key, out var v)
        && (v.ValueKind == JsonValueKind.True
            || (v.ValueKind == JsonValueKind.String && v.GetString()?.ToLowerInvariant() is "true" or "1" or "yes"));
}
