using System.Text.Json;
using ChatBranch.Data.Stores;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Data.Contacts;

/// <summary>
/// Messenger / Instagram referrals for invite links: a customer who opens
/// <c>m.me/&lt;page&gt;?ref=CODE</c> arrives as a <c>messaging_referrals</c> event
/// (an existing conversation), a <c>postback.referral</c> (the "Get started"
/// button of a new one) or a <c>message.referral</c>. The code binds the
/// conversation to its contact before the webhook's message events run.
/// Signature verification has already happened; never throws.
/// </summary>
public static class InviteReferrals
{
    public record Referral(string Channel, string PageId, string SenderId, string Ref);

    public static List<Referral> Parse(JsonElement root)
    {
        var outv = new List<Referral>();
        try
        {
            if (root.ValueKind != JsonValueKind.Object) return outv;
            var channel = Str(root, "object") switch
            {
                "page" => ChatBranch.Domain.Channels.Facebook,
                "instagram" => ChatBranch.Domain.Channels.Instagram,
                _ => "",
            };
            if (channel.Length == 0 || !root.TryGetProperty("entry", out var entries)
                || entries.ValueKind != JsonValueKind.Array) return outv;
            foreach (var entry in entries.EnumerateArray())
            {
                var pageId = Str(entry, "id");
                if (!entry.TryGetProperty("messaging", out var events) || events.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var ev in events.EnumerateArray())
                {
                    var sender = ev.TryGetProperty("sender", out var s) ? Str(s, "id") : "";
                    if (sender.Length == 0 || sender == pageId) continue;
                    var code = RefOf(ev);
                    if (code.Length == 0 && ev.TryGetProperty("postback", out var pb)) code = RefOf(pb);
                    if (code.Length == 0 && ev.TryGetProperty("message", out var m)) code = RefOf(m);
                    if (code.Length > 0) outv.Add(new Referral(channel, pageId, sender, code));
                }
            }
        }
        catch { }
        return outv;
    }

    public static int Bind(JsonElement root, AppDbContext db, ThreadStore store, ContactInviteService invites)
    {
        var bound = 0;
        foreach (var r in Parse(root))
        {
            try
            {
                if (!ContactInviteService.LooksLikeCode(r.Ref)) continue;
                var account = db.ChannelAccounts.AsNoTracking()
                    .FirstOrDefault(a => a.Channel == r.Channel && a.ExternalId == r.PageId);
                var thread = store.GetOrCreateThread(r.Channel, r.SenderId, null, account?.Id);
                var m = invites.TryBindReferral(thread, r.Ref);
                if (m == null) continue;
                invites.Complete(m, thread, null);
                if (m.Linked) bound++;
            }
            catch { /* one bad referral never fails the batch */ }
        }
        return bound;
    }

    private static string RefOf(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty("referral", out var r) && r.ValueKind == JsonValueKind.Object
            ? Str(r, "ref") : "";

    private static string Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? "").Trim() : "";
}
