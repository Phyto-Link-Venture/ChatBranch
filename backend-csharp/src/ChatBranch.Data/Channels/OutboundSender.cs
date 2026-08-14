using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Data.Channels;

/// <summary>
/// The one place an outbound message becomes real or sandbox. If the resolved
/// account is a configured WhatsApp Cloud number, it hits graph.facebook.com and
/// the stored message carries the real wamid + status; otherwise it stores
/// locally with status "sandbox" (the zero-credential default). The queue and
/// the manual send endpoint both go through here so behaviour is identical.
/// </summary>
public class OutboundSender
{
    private readonly AppDbContext _db;
    private readonly ThreadStore _threads;
    private readonly IWhatsAppCloudClient _whatsapp;

    public OutboundSender(AppDbContext db, ThreadStore threads, IWhatsAppCloudClient whatsapp)
    {
        _db = db;
        _threads = threads;
        _whatsapp = whatsapp;
    }

    public Message Send(Thread thread, string text, string source, int? accountId = null)
    {
        var acctId = accountId ?? thread.AccountId;
        var account = acctId is int id ? _db.ChannelAccounts.Find(id) : null;

        if (thread.Channel == ChatBranch.Domain.Channels.WhatsApp && account != null
            && _whatsapp.IsConfigured(account))
        {
            var outcome = _whatsapp.SendTextAsync(account, thread.Key, text).GetAwaiter().GetResult();
            if (outcome.Ok)
                return _threads.AppendMessage(thread, MessageDirection.Out, source, text,
                    status: "sent", externalId: outcome.ExternalId, accountId: account.Id);
            // real send failed → record it honestly rather than pretend success
            return _threads.AppendMessage(thread, MessageDirection.Out, MessageSource.System,
                $"[send failed: {outcome.Detail}] {text}", status: "failed", accountId: account.Id);
        }

        // sandbox (no configured account)
        return _threads.AppendMessage(thread, MessageDirection.Out, source, text,
            status: "sandbox", accountId: acctId);
    }
}
