using ChatBranch.Data.Ai;
using ChatBranch.Data.Contacts;
using ChatBranch.Data.Flows;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Data.Channels;

/// <summary>
/// The shared inbound path for every channel: store the message (wamid dedup),
/// link a CRM contact by phone/email, move the conversation onto the number
/// that received it, apply STOP/START (<see cref="OptOut"/>), resume flow runs
/// parked on a reply, and — behind the coverage gate (linked contact, the
/// EFFECTIVE chatbot tick (<see cref="AiEligibility"/>) on, not opted out) —
/// generate and send an AI auto-reply; a business (reminder) number with the
/// chatbot off gets the <see cref="BusinessRedirect"/> instead. Called by the
/// live webhooks; runs off the ack path.
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
    private readonly AiResponder _responder;

    public InboundProcessor(AppDbContext db, ThreadStore threads, AssistantService assistant,
        OutboundSender sender, FlowEngine engine, QueueService queue, ScopingService scoping,
        AutomationService automation, AiResponder? responder = null)
    {
        _db = db;
        _threads = threads;
        _assistant = assistant;
        _sender = sender;
        _engine = engine;
        _queue = queue;
        _scoping = scoping;
        _automation = automation;
        _responder = responder ?? new AiResponder(db, scoping, new AiComposer(db, assistant),
            new AiGate(db, scoping, threads, sender));
    }

    /// <param name="sender">A GROUP message's author (<see cref="GroupChats"/>); ignored on a 1:1 chat.</param>
    /// <param name="addressedToUs">A group message that mentions us, replies to us or is a bot
    /// command — the only kind the AI may answer in a group, and the only kind that pages anyone.</param>
    public Message ProcessInbound(string channel, string key, string text, string? name = null,
        int? accountId = null, string wamid = "", DateTime? providerTs = null, string replyToExternalId = "",
        InboundSender? sender = null, bool addressedToUs = false)
    {
        // a group conversation (a WhatsApp group, a Telegram group, a LINE group / room):
        // stored and shown, but it is no customer — no contact, no flows, no automations,
        // no STOP, no "who are you?", and the AI only when switched on AND addressed
        var group = GroupChats.IsGroup(channel, key);
        if (group) return ProcessGroupInbound(channel, key, text, name, accountId, wamid, providerTs,
            replyToExternalId, sender, addressedToUs);

        // the conversation of the RECEIVING number's company: a customer writing to a
        // second company's number gets that company's own conversation (never another's)
        var thread = _threads.GetOrCreateThread(channel, key, name, accountId);
        // invite links ("[ref:CODE]", "(ref:CODE)", "/start CODE"…) bind the chat to
        // the invited contact BEFORE phone/email matching; the marker is stripped
        var invite = Invites.TryBind(thread, text);
        if (invite != null) text = invite.Stripped;
        LinkContact(thread, accountId);
        // the number that RECEIVES a message decides attribution: a conversation
        // sitting on another of the SAME company's numbers moves to the one the
        // customer wrote to
        try { Reattribute(thread, accountId); } catch { }
        // a brand-new conversation takes its inbox's AI reply default
        InboxSettings.ApplyToNewThread(_db, thread);

        var msg = _threads.AppendMessage(thread, MessageDirection.In, MessageSource.Customer,
            text, status: "received", externalId: wamid, accountId: accountId, providerTs: providerTs,
            replyToExternalId: replyToExternalId);
        Invites.Complete(invite, thread, msg);

        // background AI tasks (silent workers that read the message and act through
        // tools, never answering the customer) are QUEUED here and run by their worker
        Ai.Tasks.AiTaskService.Enqueue(_db, _scoping, thread, msg);

        // STOP / UNSUBSCRIBE / BERHENTI (+ configured words) opt the conversation
        // out of everything automated; START / UNSTOP / MULA opts it back in
        ApplyOptOut(thread, text);

        // event axis: resume flow runs parked on a reply
        try { _engine.OnInboundMessage(thread, text); } catch { }

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

        // unknown customer: ask who they are (per-inbox identify_unknown / the
        // ask_identity automation) — a reply that identifies them links the chat
        // here, so the AI step below answers them
        Identity.OnInbound(thread, msg);

        // a reply still waiting in the AI monitor is stale now — expire it so the
        // next reply covers everything the customer said
        _responder.OnCustomerMessage(thread);

        AutoAnswer(thread, accountId);
        return msg;
    }

    /// <summary>
    /// The inbound path of a GROUP conversation (<see cref="GroupChats"/>): the thread
    /// (named after the group, never after whoever wrote first), same-company
    /// re-attribution, the inbox's AI default, the message with its author, team
    /// routing, a notification only when we are addressed (or the group is new), and
    /// the AI only when an operator switched it on for this group AND the message is
    /// addressed to us. Nothing customer-shaped runs: no contact link, invite binder,
    /// background AI tasks, STOP/START, flows, automations or identity ask.
    /// </summary>
    private Message ProcessGroupInbound(string channel, string key, string text, string? name,
        int? accountId, string wamid, DateTime? providerTs, string replyToExternalId,
        InboundSender? sender, bool addressedToUs)
    {
        var groupName = GroupChats.CleanName(name);
        var thread = _threads.GetOrCreateThread(channel, key,
            groupName.Length > 0 ? groupName : GroupChats.DefaultName(channel), accountId);
        try { Reattribute(thread, accountId); } catch { }
        InboxSettings.ApplyToNewThread(_db, thread);

        var msg = _threads.AppendMessage(thread, MessageDirection.In, MessageSource.Customer,
            text, status: "received", externalId: wamid, accountId: accountId, providerTs: providerTs,
            replyToExternalId: replyToExternalId, sender: sender);

        try { AutoAssignTeam(thread); } catch { }

        // a busy group must not page the team on every message: only when it talks to
        // us, or the very first time it appears
        try
        {
            var isFirst = _db.Messages.Count(m => m.ThreadId == thread.Id
                                                  && m.Direction == MessageDirection.In) <= 1;
            if (addressedToUs || isFirst) NotificationService.NotifyInboundMessage(_db, thread, text);
        }
        catch { }

        _responder.OnCustomerMessage(thread);

        try
        {
            if (addressedToUs && !thread.OptedOut && AiEligibility.Decide(_db, _scoping, thread).Enabled)
                TryAiReply(thread);
        }
        catch { /* never breaks the webhook */ }
        return msg;
    }

    /// <summary>Apply a customer message to the conversation's STOP/START state (never throws).</summary>
    public string? ApplyOptOut(Thread thread, string? text) =>
        OptOut.Apply(_db, _scoping, _threads, thread, text);

    /// <summary>
    /// The one auto-answer decision for an inbound: nothing for an opted-out
    /// conversation; the AI reply behind the coverage gate (linked contact AND
    /// the effective chatbot tick — the same rule the dashboard shows); else, on
    /// a business (reminder) number, the queued redirect.
    /// </summary>
    private void AutoAnswer(Thread thread, int? receivingAccountId)
    {
        try
        {
            // a group is answered only when addressed (ProcessGroupInbound), never here
            if (thread.OptedOut || GroupChats.IsGroup(thread)) return;
            if (thread.ContactId != null)
            {
                var verdict = AiEligibility.Decide(_db, _scoping, thread);
                if (verdict.Enabled)
                {
                    TryAiReply(thread);
                    return;
                }
                if (QuotaHandover(thread, verdict)) return;
            }
            BusinessRedirect.Maybe(_db, _scoping, thread, receivingAccountId);
        }
        catch { /* never breaks the webhook */ }
    }

    /// <summary>
    /// Inbound re-attribution: a WhatsApp conversation moving between the
    /// business and personal numbers OF ONE COMPANY follows the number the
    /// customer last wrote to, so the reply (and that number's own chatbot tick)
    /// comes from there. Never across companies: another company's number has its
    /// own conversation (<see cref="ThreadStore.GetOrCreateThread"/>), so a
    /// mismatch here is refused, audited and left where it is.
    /// </summary>
    private void Reattribute(Thread thread, int? accountId)
    {
        if (accountId is not int aid || thread.AccountId is null || thread.AccountId == aid) return;
        var account = _db.ChannelAccounts.Find(aid);
        if (account == null || account.Channel != thread.Channel) return;
        var from = _scoping.ThreadCompany(thread);
        var to = _scoping.AccountCompany(account);
        if (from != to)
        {
            try
            {
                Audit.AuditTrail.Add(_db, "thread.reattribute_refused", $"{thread.Channel}:{thread.Key}",
                    $"thread #{thread.Id} (company {from?.ToString() ?? "none"}) stays on account #{thread.AccountId}; "
                    + $"account #{aid} belongs to company {to?.ToString() ?? "none"}",
                    actor: "webhook", source: AuditSources.Webhook, entityId: thread.EntityId);
                _db.SaveChanges();
            }
            catch { /* the refusal stands; the audit row is best-effort */ }
            return;
        }
        thread.AccountId = aid;
        thread.Account = account;
        _scoping.SyncThreadEntity(thread, save: false);
        thread.UpdatedAt = DateTime.UtcNow;
        _db.SaveChanges();
    }

    /// <summary>
    /// Link the conversation to the customer's contact by phone / email — only a
    /// contact the conversation's company may see (<see cref="ContactCompanies.Pick"/>
    /// never returns another company's). Never throws.
    /// </summary>
    private void LinkContact(Thread thread, int? accountId)
    {
        if (thread.ContactId != null || GroupChats.IsGroup(thread)) return;
        try
        {
            var contact = MatchContact(thread.Channel, thread.Key, ReceivingEntity(thread, accountId));
            if (contact == null || !ContactCompanies.MayLink(_db, _scoping, contact.Id, _scoping.ThreadCompany(thread)))
                return;
            thread.ContactId = contact.Id;
            _db.SaveChanges();
            _scoping.SyncThreadEntity(thread);
            _db.SaveChanges();
        }
        catch { }
    }

    /// <summary>
    /// The get-or-create + contact link + same-company re-attribution of an inbound,
    /// for a path that stores the message itself (the sandbox <c>/simulate</c>), so a
    /// simulated message to a number lands exactly where a real one would.
    /// </summary>
    public Thread ReceiveThread(string channel, string key, string? name, int? accountId)
    {
        var thread = _threads.GetOrCreateThread(channel, key, name, accountId);
        LinkContact(thread, accountId);
        try { Reattribute(thread, accountId); } catch { }
        return thread;
    }

    /// <summary>
    /// The sandbox <c>/simulate</c> path: the same post-inbound AI step as a real
    /// webhook (stale drafts expire, coverage-gated auto-reply) — a sandbox that
    /// behaves differently from production hides bugs.
    /// </summary>
    public void AfterSimulatedInbound(Thread thread)
    {
        IdentityStepForStored(thread);
        _responder.OnCustomerMessage(thread);
        AutoAnswer(thread, thread.AccountId);
    }

    /// <summary>
    /// Run the auto-reply for a thread whose message was stored by another path
    /// (the website widget). Same coverage gate as the webhook: a linked contact
    /// is not required for a widget visitor, but AI must be on and the contact
    /// not opted out. Pass the visitor's <paramref name="text"/> so STOP/START
    /// applies here too.
    /// </summary>
    public void MaybeAutoReply(Thread thread, string? text = null)
    {
        if (text != null) ApplyOptOut(thread, text);
        // while the visitor is being asked who they are, the AI waits
        if (IdentityStepForStored(thread) is { Linked: false, State: IdentityStates.Asking }) return;
        _responder.OnCustomerMessage(thread);
        if (thread.OptedOut) return;
        var verdict = AiEligibility.Decide(_db, _scoping, thread);
        if (!verdict.Enabled)
        {
            QuotaHandover(thread, verdict);
            return;
        }
        TryAiReply(thread);
    }

    /// <summary>
    /// The AI would have answered this customer but its usage quota — or its
    /// company's prepaid AI credit — is used up: hand the chat to a person (kind
    /// <c>quota</c> / <c>credit</c>, the chatbot tick left as it was so the AI
    /// resumes by itself next month, once the cap is raised or once credit is
    /// added) — once per open handover. The customer is never told about billing.
    /// Then warn the admins if a quota crossed a threshold (credit warnings come
    /// from the credit sweep). Returns true when the chat is (already) with a
    /// person for this reason.
    /// </summary>
    private bool QuotaHandover(Thread thread, AiVerdict verdict)
    {
        try
        {
            if (verdict.Rule != AiRules.Quota && verdict.Rule != AiRules.Credit) return false;
            if (!AiEligibility.Decide(_db, _scoping, thread, null, ignoreQuota: true).Enabled) return false;
            if (!Handover.IsOpen(thread))
                Handover.Mark(_db, thread, verdict.Rule == AiRules.Credit ? HandoverKinds.Credit : HandoverKinds.Quota,
                    verdict.Reason.StartsWith("Off: ") ? verdict.Reason[5..] : verdict.Reason, "ai", standDown: false);
            if (verdict.Rule == AiRules.Quota) AiQuota.Sweep(_db, _scoping);
            return true;
        }
        catch { return false; }
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
    private void TryAiReply(Thread thread)
    {
        try
        {
            var mode = _scoping.SettingValue("ai_reply_mode", thread.EntityId);
            var behaviour = AiBehaviourSettings.Resolve(_scoping, thread.EntityId, thread.Account);
            // "Wait for the customer to finish" (any channel, when enabled) and the
            // queued routing rule on live accounts share ONE debounced ai_reply job
            // per thread: each inbound pushes its not_before out, and the reply is
            // written at send time so a burst of messages gets one answer. With no
            // wait configured a sandbox still answers instantly (flows/queue.py's
            // rule: nothing to pace, and a demo that answers late looks broken).
            if (behaviour.WaitSeconds > 0 || (mode != "instant" && _sender.WouldSendLive(thread)))
            {
                _responder.ScheduleReply(thread, behaviour.WaitSeconds, behaviour.WaitMaxSeconds);
                return;
            }
            // instant: through the AI monitor (review policy, acknowledgement skip,
            // human-like delivery) like every other AI message — the gate, not the
            // assistant, decides what a failed check or a handover request means
            _responder.RespondAsync(thread).GetAwaiter().GetResult();
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
    /// address; WhatsApp (the only phone-keyed channel) → last-8-digit phone
    /// match; anything else → no automatic link. Messenger PSIDs, Instagram
    /// IGSIDs, Telegram chat ids and widget session ids are opaque — an 8-digit
    /// tail match would link a stranger to a random contact; those are linked
    /// by hand (or by email from the widget's pre-chat form). Public so the
    /// sandbox <c>/simulate</c> links threads exactly like a real webhook does.
    /// </summary>
    public Contact? MatchContact(string channel, string key, int? entityId = null)
    {
        // a group's id is no customer's phone — its digits would tail-match a stranger
        if (GroupChats.IsGroup(channel, key)) return null;
        // Several contacts may share a phone / email (one per company): the one
        // in the receiving number's entity wins, never another company's
        // (ContactCompanies.Pick). No entity = the oldest match, as before.
        if (channel == "email")
            return ContactCompanies.MatchEmail(_db, _scoping, key, entityId);
        // SMS is phone-keyed too (MoreChannels.PhoneKeyed)
        if (channel != ChatBranch.Domain.Channels.WhatsApp && !MoreChannels.PhoneKeyed(channel)) return null;
        if (PhoneMatch.Tail(key) == null) return null;
        return ContactCompanies.MatchPhone(_db, _scoping, key, entityId);
    }

    /// <summary>The entity a conversation's contact should belong to: the
    /// receiving account's, else the thread's own.</summary>
    private int? ReceivingEntity(Thread thread, int? accountId) =>
        accountId is int a
            ? _db.ChannelAccounts.AsNoTracking().Where(x => x.Id == a).Select(x => x.EntityId).FirstOrDefault()
              ?? thread.EntityId
            : thread.EntityId;

    // ---- contacts: invite binder + "who are you?" ------------------------------

    private ContactInviteService? _invites;
    private ContactInviteService Invites => _invites ??= new ContactInviteService(_db, _scoping);
    private IdentityRequestService? _identity;
    private IdentityRequestService Identity => _identity ??= new IdentityRequestService(_db, _scoping, _threads, _sender);

    /// <summary>
    /// The binder + identity step for a message ANOTHER path already stored (the
    /// website widget, the sandbox simulate): an invite marker is stripped from
    /// the stored row, then the unknown-customer step runs. Never throws.
    /// </summary>
    private IdentityOutcome? IdentityStepForStored(Thread thread)
    {
        try
        {
            var last = _db.Messages.Where(m => m.ThreadId == thread.Id && m.Direction == MessageDirection.In)
                .OrderByDescending(m => m.Id).FirstOrDefault();
            if (last == null) return null;
            var invite = Invites.TryBind(thread, last.Text);
            if (invite != null)
            {
                last.Text = invite.Stripped;
                _db.SaveChanges();
                Invites.Complete(invite, thread, last);
            }
            return Identity.OnInbound(thread, last);
        }
        catch { return null; }
    }
}
