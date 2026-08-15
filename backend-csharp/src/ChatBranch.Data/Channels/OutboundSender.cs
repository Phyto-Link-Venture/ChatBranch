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
    private readonly ChatBranch.Data.Sidecars.IBaileysGateway _baileys;

    public OutboundSender(AppDbContext db, ThreadStore threads, IWhatsAppCloudClient whatsapp,
        ChatBranch.Data.Sidecars.IBaileysGateway baileys)
    {
        _db = db;
        _threads = threads;
        _whatsapp = whatsapp;
        _baileys = baileys;
    }

    /// <summary>
    /// True when this thread would send for REAL (a configured Cloud or Baileys
    /// account carries it) rather than storing a sandbox message. Pacing and the
    /// queue only make sense for real sends — a sandbox demo must stay instant.
    /// </summary>
    public bool WouldSendLive(Thread thread, int? accountId = null)
    {
        var acctId = accountId ?? thread.AccountId;
        var account = acctId is int id ? _db.ChannelAccounts.Find(id) : null;
        if (account == null) return false;
        if (thread.Channel == ChatBranch.Domain.Channels.WhatsApp
            && account.Provider == "baileys")
            return _baileys.IsConfigured && !string.IsNullOrEmpty(account.ExternalId);
        if (thread.Channel == ChatBranch.Domain.Channels.WhatsApp)
            return _whatsapp.IsConfigured(account);
        return false;
    }

    public Message Send(Thread thread, string text, string source, int? accountId = null)
    {
        var acctId = accountId ?? thread.AccountId;
        var account = acctId is int id ? _db.ChannelAccounts.Find(id) : null;

        // Baileys (personal number paired by QR): send through the Node gateway.
        // The account's ExternalId is the gateway's account id.
        if (thread.Channel == ChatBranch.Domain.Channels.WhatsApp && account != null
            && account.Provider == "baileys" && _baileys.IsConfigured
            && !string.IsNullOrEmpty(account.ExternalId))
        {
            var r = _baileys.SendTextAsync(account.ExternalId, thread.Key, text).GetAwaiter().GetResult();
            if (r.Ok)
                return _threads.AppendMessage(thread, MessageDirection.Out, source, text,
                    status: "sent", externalId: r.ExternalId, accountId: account.Id);
            return _threads.AppendMessage(thread, MessageDirection.Out, MessageSource.System,
                $"[send failed: {r.Detail}] {text}", status: "failed", accountId: account.Id);
        }

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
