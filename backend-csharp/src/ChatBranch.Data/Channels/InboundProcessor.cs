using ChatBranch.Data.Ai;
using ChatBranch.Data.Flows;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Data.Channels;

/// <summary>
/// The shared inbound path for every channel: store the message (wamid dedup),
/// link a CRM contact by phone/email, resume flow runs parked on a reply, and —
/// behind the coverage gate (thread has a linked contact AND AI is on) — generate
/// and send an AI auto-reply. Called by the live webhooks; runs off the ack path.
/// </summary>
public class InboundProcessor
{
    private readonly AppDbContext _db;
    private readonly ThreadStore _threads;
    private readonly AssistantService _assistant;
    private readonly OutboundSender _sender;
    private readonly FlowEngine _engine;

    public InboundProcessor(AppDbContext db, ThreadStore threads, AssistantService assistant,
        OutboundSender sender, FlowEngine engine)
    {
        _db = db;
        _threads = threads;
        _assistant = assistant;
        _sender = sender;
        _engine = engine;
    }

    public Message ProcessInbound(string channel, string key, string text, string? name = null,
        int? accountId = null, string wamid = "", DateTime? providerTs = null)
    {
        var thread = _threads.GetOrCreateThread(channel, key, name, accountId);
        if (thread.ContactId == null)
        {
            var contact = MatchContact(channel, key);
            if (contact != null)
            {
                thread.ContactId = contact.Id;
                _db.SaveChanges();
            }
        }

        var msg = _threads.AppendMessage(thread, MessageDirection.In, MessageSource.Customer,
            text, status: "received", externalId: wamid, accountId: accountId, providerTs: providerTs);

        // event axis: resume flow runs parked on a reply
        try { _engine.OnInboundMessage(channel, key, text); } catch { }

        // coverage gate: AI auto-reply only for threads linked to a contact + AI on
        if (thread.ContactId != null && thread.AiEnabled && !thread.OptedOut)
            TryAiReply(thread);

        return msg;
    }

    private void TryAiReply(Thread thread)
    {
        try
        {
            var reply = _assistant.GenerateReplyAsync(thread).GetAwaiter().GetResult();
            if (!string.IsNullOrWhiteSpace(reply))
                _sender.Send(thread, reply, MessageSource.Ai, thread.AccountId);
        }
        catch { /* AI failure never breaks the webhook path */ }
    }

    /// <summary>
    /// The one contact-matching rule for inbound identities: email → exact
    /// address; everything else → last-8-digit phone match. Public so the
    /// sandbox <c>/simulate</c> links threads exactly like a real webhook does.
    /// </summary>
    public Contact? MatchContact(string channel, string key)
    {
        if (channel == "email")
            return _db.Contacts.FirstOrDefault(c => c.Email.ToLower() == key.ToLower());
        var digits = new string(key.Where(char.IsDigit).ToArray());
        if (digits.Length < 8) return null;
        var tail = digits[^8..];
        foreach (var c in _db.Contacts.AsEnumerable())
        {
            var p = new string((c.Phone ?? "").Where(char.IsDigit).ToArray());
            if (p.Length >= 8 && p[^8..] == tail) return c;
        }
        return null;
    }
}
