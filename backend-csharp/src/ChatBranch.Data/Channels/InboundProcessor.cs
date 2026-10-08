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
    private readonly QueueService _queue;
    private readonly ScopingService _scoping;
    private readonly AutomationService _automation;

    public InboundProcessor(AppDbContext db, ThreadStore threads, AssistantService assistant,
        OutboundSender sender, FlowEngine engine, QueueService queue, ScopingService scoping,
        AutomationService automation)
    {
        _db = db;
        _threads = threads;
        _assistant = assistant;
        _sender = sender;
        _engine = engine;
        _queue = queue;
        _scoping = scoping;
        _automation = automation;
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
        // a brand-new conversation takes its inbox's AI reply default
        InboxSettings.ApplyToNewThread(_db, thread);

        var msg = _threads.AppendMessage(thread, MessageDirection.In, MessageSource.Customer,
            text, status: "received", externalId: wamid, accountId: accountId, providerTs: providerTs);

        // event axis: resume flow runs parked on a reply
        try { _engine.OnInboundMessage(channel, key, text); } catch { }

        // route a brand-new conversation to a team (round-robin within it)
        try { AutoAssignTeam(thread); } catch { }

        // automation rules — conversation_created fires once, on the first
        // customer message; message_created fires on every one
        try
        {
            var isFirst = _db.Messages.Count(m => m.ThreadId == thread.Id
                                                  && m.Direction == MessageDirection.In) <= 1;
            if (isFirst) _automation.Run("conversation_created", thread, text, isFirstMessage: true);
            _automation.Run("message_created", thread, text, isFirst);
        }
        catch { }

        // page the people who own this conversation (assignee → team → admins)
        NotificationService.NotifyInboundMessage(_db, thread, text);

        // coverage gate: AI auto-reply only for threads linked to a contact + AI on
        if (thread.ContactId != null && thread.AiEnabled && !thread.OptedOut)
            TryAiReply(thread);

        return msg;
    }

    /// <summary>
    /// Auto-reply, honouring the `ai_reply_mode` routing rule:
    ///
    /// * <b>queued</b> (the default) — enqueue an ai_reply job instead of
    ///   answering inline, so the account's Send Pacing gap and quiet hours
    ///   actually apply and several messages in a burst collapse into ONE
    ///   answer (the queue generates the reply at send time). Without this the
    ///   rule was stored but never read, and the pacing setting paced nothing.
    /// * <b>instant</b> — reply immediately, as before.
    ///
    /// A thread never stacks queued replies: one pending job at a time.
    /// </summary>
    /// <summary>
    /// Run the auto-reply for a thread whose message was stored by another path
    /// (the website widget). Same coverage gate as the webhook: a linked contact
    /// is not required for a widget visitor, but AI must be on and the contact
    /// not opted out.
    /// </summary>
    public void MaybeAutoReply(Thread thread)
    {
        if (!thread.AiEnabled || thread.OptedOut) return;
        TryAiReply(thread);
    }

    private void TryAiReply(Thread thread)
    {
        try
        {
            var mode = _scoping.SettingValue("ai_reply_mode", thread.EntityId);
            // Sandbox stays instant (flows/queue.py's rule): with no configured
            // account there is nothing to pace, and a demo that answers 30s later
            // just looks broken.
            if (mode != "instant" && _sender.WouldSendLive(thread))
            {
                var pending = _db.OutboundJobs.Any(j => j.Kind == JobKind.AiReply
                    && j.Status == JobStatus.Queued
                    && j.Channel == thread.Channel && j.ToAddress == thread.Key);
                if (!pending)
                    _queue.Enqueue(thread.Channel, JobKind.AiReply, thread.Key,
                        new Dictionary<string, object?>(), thread.ContactId, thread.AccountId);
                return;
            }
            // a held reply (checks failed → handover/block) comes back with no text
            var reply = _assistant.GenerateReplyAsync(thread).GetAwaiter().GetResult();
            if (reply.ShouldSend)
            {
                _sender.Send(thread, reply.Text, MessageSource.Ai, thread.AccountId);
                ChatBranch.Data.Audit.AuditTrail.Write(_db, "ai.reply", $"{thread.Channel}:{thread.Key}",
                    reply.Text, actor: "ai", source: AuditSources.System, entityId: thread.EntityId);
            }
        }
        catch { /* AI failure never breaks the webhook path */ }
    }

    /// <summary>
    /// Hand a conversation with no team to the entity's auto-assign team. Lives
    /// behind a hook so ChatBranch.Data does not depend on the Api assembly:
    /// Program.cs sets it at startup.
    /// </summary>
    public static Action<AppDbContext, Thread>? TeamRouter { get; set; }

    private void AutoAssignTeam(Thread thread)
    {
        if (thread.TeamId != null) return;
        TeamRouter?.Invoke(_db, thread);
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
