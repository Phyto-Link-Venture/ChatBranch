using System.Text.Json;
using System.Text.Json.Nodes;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Audit;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using ChannelIds = ChatBranch.Domain.Channels;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Data.Channels;

/// <summary>
/// A message RECALLED on the channel — WhatsApp's "delete for everyone" (QR numbers:
/// the gateway's REVOKE, <c>/api/whatsapp/baileys/recall</c>), LINE's <c>unsend</c>
/// event, Instagram / Messenger's <c>is_deleted</c>. Before this ChatBranch never
/// heard of it: the message simply stayed, with nothing saying it was taken back.
///
/// What happens follows the inbox's switch (<see cref="InboxSettings.KeepRecalledKey"/>):
///   * off (the default) — like on the customer's phone: the words, the transcript
///     and the file are ERASED (the file deleted from disk, the reactions removed, the
///     unread notification and held AI drafts no longer quote it); the bubble reads
///     "This message was deleted".
///   * on — KEPT for the team: the bubble still shows it, marked "Deleted by the customer".
/// Either way the row is stamped (<see cref="Message.RecalledAt"/> / <see cref="Message.RecalledBy"/>)
/// and the AI never answers or reads it (a held reply that answers only recalled
/// messages expires — <see cref="AiGate.ExpireForRecall"/>). One audit row
/// <c>message.recalled</c>. Never throws: a webhook must still get its 200.
/// </summary>
public static class MessageRecalls
{
    public const string ByCustomer = "customer";
    /// <summary>Recalled from our own number — the phone or another device of a QR number.</summary>
    public const string ByBusiness = "business";
    public const string AuditAction = "message.recalled";

    /// <summary>The channels that tell us about a recall (the only ones offering the switch).</summary>
    public static bool Supports(ChannelAccount? account) => account != null && (account.Channel switch
    {
        ChannelIds.WhatsApp => account.Provider == Providers.Baileys,
        ChannelIds.Line or ChannelIds.Facebook or ChannelIds.Instagram => true,
        _ => false,
    });

    /// <summary>
    /// Recall the message(s) the provider id names on this account's conversation with
    /// <paramref name="key"/> (Instagram's extra attachments ride as <c>{mid}#2</c>, <c>#3</c>…
    /// and go with it). Returns the rows recalled now (none for an unknown id or a repeat).
    /// </summary>
    public static List<Message> Record(AppDbContext db, ThreadStore threads, ChannelAccount account, string key,
        string externalId, string by, AiGate? gate = null, string? dataDir = null, DateTime? nowUtc = null)
    {
        try
        {
            var id = (externalId ?? "").Trim();
            if (id.Length == 0) return new();
            var prefix = id + "#";
            // the account that carried it first (its own rows, whatever conversation key they sit under)
            var rows = db.Messages.Where(m => m.AccountId == account.Id
                    && (m.ExternalId == id || m.ExternalId.StartsWith(prefix))).ToList();
            if (rows.Count == 0 && threads.FindForAccount(account.Channel, key, account.Id) is { } t)
                rows = db.Messages.Where(m => m.ThreadId == t.Id
                    && (m.ExternalId == id || m.ExternalId.StartsWith(prefix))).ToList();
            rows = rows.Where(m => m.RecalledAt == null && !m.Private).ToList();
            if (rows.Count == 0) return rows;

            var who = by == ByBusiness ? ByBusiness : ByCustomer;
            Apply(db, rows, account, who,
                actor: who == ByBusiness ? "agent:phone" : "contact", source: "webhook",
                reason: who == ByBusiness ? "Recalled from our number" : "The customer recalled",
                gate: gate, dataDir: dataDir, nowUtc: nowUtc);
            return rows;
        }
        catch
        {
            return new();
        }
    }

    /// <summary>
    /// Delete-for-everyone that WE asked for, from the inbox: the gateway has
    /// already told WhatsApp, so this records the same outcome a customer's recall
    /// records — the inbox's keep-or-erase switch, the audit row, the redraw —
    /// under the person who did it. Returns false when the row was already
    /// recalled or is a note (nothing to do).
    /// </summary>
    public static bool RecordOurs(AppDbContext db, ChannelAccount account, Message message, string username,
        AiGate? gate = null, string? dataDir = null, DateTime? nowUtc = null)
    {
        if (message.RecalledAt != null || message.Private) return false;
        Apply(db, new List<Message> { message }, account, ByBusiness,
            actor: $"agent:{username}", source: "user", reason: "Deleted for everyone",
            gate: gate, dataDir: dataDir, nowUtc: nowUtc);
        return true;
    }

    /// <summary>
    /// Mark rows recalled: erase or keep them per the inbox's switch, scrub the
    /// copies the words reached, bump the conversation so the open chat redraws,
    /// audit it, and expire an AI draft that was answering one. ONE path, so a
    /// recall the customer made and a recall we made cannot drift apart.
    /// </summary>
    private static void Apply(AppDbContext db, List<Message> rows, ChannelAccount account, string who,
        string actor, string source, string reason, AiGate? gate, string? dataDir, DateTime? nowUtc)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var keep = InboxSettings.KeepRecalled(account);
        var words = rows.ToDictionary(m => m.Id, m => m.Text.Trim());   // before erasing: where else they were copied
        foreach (var m in rows)
        {
            m.RecalledAt = now;
            m.RecalledBy = who;
            if (!keep) Erase(db, m, dataDir);
        }
        foreach (var group in rows.GroupBy(m => m.ThreadId))
        {
            var thread = db.Threads.FirstOrDefault(x => x.Id == group.Key);
            if (thread == null) continue;
            thread.UpdatedAt = now;   // the open chat redraws (the SSE change token)
            var ids = group.Select(m => m.Id).ToList();
            if (!keep) ScrubCopies(db, thread, group.Select(m => (m.Id, words[m.Id])).ToList());
            AuditTrail.Add(db, AuditAction, $"{thread.Channel}:{thread.Key}",
                $"{reason} {(ids.Count == 1 ? "a message" : $"{ids.Count} messages")} "
                + $"(#{string.Join(", #", ids)}) — "
                + (keep ? "kept (the inbox keeps recalled messages)" : "erased"),
                actor: actor, source: source, entityId: thread.EntityId);
        }
        db.SaveChanges();
        if (gate != null)
            foreach (var group in rows.Where(m => m.Direction == MessageDirection.In).GroupBy(m => m.ThreadId))
                if (db.Threads.FirstOrDefault(x => x.Id == group.Key) is { } thread)
                    gate.ExpireForRecall(thread, group.Select(m => m.Id).ToList());
    }

    /// <summary>The words, the transcript, the file (from disk too) and the reactions.</summary>
    private static void Erase(AppDbContext db, Message m, string? dataDir)
    {
        if (m.MediaPath.Length > 0)
        {
            try
            {
                var channel = m.Thread?.Channel ?? db.Threads.Where(t => t.Id == m.ThreadId).Select(t => t.Channel).FirstOrDefault() ?? "";
                var store = new MediaStore(dataDir ?? Sidecars.VoiceAgentBridge.ResolveDataDir(null));
                if (store.Resolve(channel, m.MediaPath) is { } file)
                {
                    File.Delete(file);
                    var m4a = file + ".m4a";   // the AAC rendering of a voice note
                    if (File.Exists(m4a)) File.Delete(m4a);
                }
            }
            catch { /* the row is erased even if the file can't be */ }
        }
        m.Text = "";
        m.Transcript = "";
        m.MediaPath = "";
        m.MediaName = "";
        m.MediaMime = "";
        m.MediaSize = 0;
        db.MessageReactions.RemoveRange(db.MessageReactions.Where(r => r.MessageId == m.Id));
    }

    /// <summary>
    /// Where the words were copied: the unread "New message" notification and the
    /// held / past AI drafts' "answering" snapshot.
    /// </summary>
    private static void ScrubCopies(AppDbContext db, Thread thread, List<(int Id, string Text)> erased)
    {
        var ids = erased.Select(m => m.Id).ToHashSet();
        var texts = erased.Select(m => m.Text).Where(t => t.Length > 0).ToList();
        try
        {
            // the notification's preview of THAT message (capped at 400 characters when written)
            var refKey = $"thread:{thread.Id}";
            foreach (var n in db.Notifications.Where(n => n.RefKey == refKey).ToList())
                if (texts.Any(t => n.Body == t || (n.Body.Length >= 20 && t.StartsWith(n.Body.TrimEnd('…'), StringComparison.Ordinal))))
                    n.Body = "This message was deleted by the customer.";
        }
        catch { /* best effort */ }
        try
        {
            foreach (var d in db.AiDrafts.Where(d => d.ThreadId == thread.Id).ToList())
            {
                if (JsonSerializer.SerializeToNode(d.Context) is not JsonObject ctx
                    || ctx["answering"] is not JsonArray answering) continue;
                var changed = false;
                foreach (var item in answering.OfType<JsonObject>())
                    if (item["id"] is JsonValue v && v.TryGetValue<int>(out var mid) && ids.Contains(mid))
                    {
                        item["text"] = "";
                        item["recalled"] = true;
                        changed = true;
                    }
                if (changed)
                    d.Context = JsonSerializer.Deserialize<Dictionary<string, object?>>(ctx.ToJsonString()) ?? d.Context;
            }
        }
        catch { /* best effort */ }
    }
}
