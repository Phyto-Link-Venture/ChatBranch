using System.Text.Json;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Sidecars;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Data.Flows;

/// <summary>What happened to one queued call.</summary>
public sealed record CallAttempt(string Outcome, string Detail, string? Room = null, int? AccountId = null)
{
    public const string Placed = "placed";
    /// <summary>Not now (number busy, breather, daily cap) — the job stays queued, no attempt burned.</summary>
    public const string Wait = "wait";
    /// <summary>Refused for good (opt-out, do-not-call, cold-call guard, not configured…).</summary>
    public const string Failed = "failed";
}

/// <summary>
/// Places the queue's <c>kind="call"</c> jobs (flow "AI voice call" / "Script
/// call" steps) — the queue rules from CLAUDE.md:
///  * <b>transport "sip"</b> → the same path as <c>POST /api/calls/dial</c>:
///    <see cref="VoiceDispatch"/> metadata (script, voice, brain, STT) and
///    <see cref="LiveKitClient.PlaceSipCallAsync"/>. No liveness endpoint, so SIP
///    paces by <c>queue_call_gap_s</c> + one dial per drain pass.
///  * <b>transport "baileys"/"whatsapp"</b> → the WhatsApp call start path:
///    dispatch the agent, then the gateway's <c>/call</c> with a room token.
///    A WhatsApp number carries ONE call: busy is checked at the GATEWAY
///    (call-status) with the thread rows only as a fallback, and a busy number
///    is a WAIT, never a failure. Stale busy rows are reconciled when the
///    gateway says idle.
///  * <c>queue_call_gap_s</c> (60) breather and <c>queue_call_daily_cap</c> (50)
///    per number; compliance refusals — do-not-call, opt-out, cold-call guard —
///    and an unconfigured LiveKit/gateway fail the job with a clear reason.
/// Never throws into the drain.
/// </summary>
public class QueueCallPlacer
{
    private static readonly string[] BusyStates = { "connecting", "ringing", "in_call", "calling", "active" };

    private readonly AppDbContext _db;
    private readonly ScopingService _scoping;
    private readonly ThreadStore _threads;
    private readonly LiveKitClient _livekit;
    private readonly IBaileysGateway _baileys;
    private readonly KnowledgeService _knowledge;
    private readonly AssistantService _assistant;
    private readonly string _dataDir;
    private readonly TimeProvider _clock;

    public QueueCallPlacer(AppDbContext db, ScopingService scoping, ThreadStore threads, LiveKitClient livekit,
        IBaileysGateway baileys, KnowledgeService knowledge, AssistantService assistant, VoiceAgentBridge voice,
        TimeProvider? clock = null)
    {
        _db = db;
        _scoping = scoping;
        _threads = threads;
        _livekit = livekit;
        _baileys = baileys;
        _knowledge = knowledge;
        _assistant = assistant;
        _dataDir = voice.DataDir;
        _clock = clock ?? TimeProvider.System;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public static bool IsWhatsApp(OutboundJob job)
    {
        var t = AiGate.Str(job.Payload, "transport", "").ToLowerInvariant();
        return t is "baileys" or "whatsapp" || (t.Length == 0 && job.Channel == ChatBranch.Domain.Channels.WhatsApp);
    }

    private int IntSetting(string key, int fallback, int? entityId = null) =>
        int.TryParse(_scoping.SettingValue(key, entityId), out var n) ? n : fallback;

    /// <summary>
    /// The pre-claim gate: why this call must wait (null = go). Busy number,
    /// post-call breather, daily cap. Cheap enough to run every drain pass.
    /// </summary>
    public string? WaitReason(OutboundJob job)
    {
        try
        {
            var now = Now;
            var whatsapp = IsWhatsApp(job);
            var account = whatsapp ? ResolveAccount(job) : null;
            var accountId = account?.Id;
            var sent = _db.OutboundJobs.AsNoTracking()
                .Where(j => j.Kind == JobKind.Call && j.Status == JobStatus.Sent && j.SentAt != null)
                .Where(j => whatsapp ? j.AccountId == accountId : j.Channel == "voice");

            var gap = IntSetting("queue_call_gap_s", 60);
            var last = sent.Max(j => (DateTime?)j.SentAt);
            if (last is DateTime l && (now - l).TotalSeconds < gap) return "post-call breather";

            var cap = IntSetting("queue_call_daily_cap", 50);
            var since = now.AddHours(-24);
            if (sent.Count(j => j.SentAt >= since) >= cap) return "daily call cap reached";

            if (account != null && Busy(account)) return $"{account.Name} is on a call";
        }
        catch { /* a broken check never blocks the queue for good */ }
        return null;
    }

    /// <summary>Gateway-authoritative busy check, thread rows as the fallback (reconciled when stale).</summary>
    private bool Busy(ChannelAccount account)
    {
        string? gateway = null;
        try
        {
            if (_baileys.IsConfigured && account.ExternalId.Length > 0)
                gateway = _baileys.CallStatusAsync(account.ExternalId).GetAwaiter().GetResult();
        }
        catch { gateway = null; }

        var rows = _db.Threads.Where(t => t.AccountId == account.Id && BusyStates.Contains(t.CallStatus)).ToList();
        if (gateway != null)
        {
            var busy = BusyStates.Contains(gateway.ToLowerInvariant());
            if (!busy && rows.Count > 0)
            {
                // the gateway knows the line is free: clear stale "in call" rows
                foreach (var t in rows)
                {
                    t.CallStatus = "idle";
                    t.UpdatedAt = DateTime.UtcNow;
                }
                _db.SaveChanges();
            }
            return busy;
        }
        // no gateway answer: trust a recent thread row, ignore one older than a call can last
        var maxAge = TimeSpan.FromSeconds(MaxCallSeconds() + 120);
        return rows.Any(t => DateTime.UtcNow - t.UpdatedAt < maxAge);
    }

    /// <summary>Place the call now (the job is already claimed).</summary>
    public CallAttempt Place(OutboundJob job)
    {
        try { return PlaceAsync(job).GetAwaiter().GetResult(); }
        catch (Exception ex) { return new CallAttempt(CallAttempt.Failed, $"call failed: {ex.Message}"); }
    }

    private async Task<CallAttempt> PlaceAsync(OutboundJob job)
    {
        var contact = job.ContactId is int cid
            ? await _db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid)
            : null;
        if (contact?.DoNotCall == true)
            return new CallAttempt(CallAttempt.Failed, "Contact is marked do-not-call.");

        var script = AiGate.Str(job.Payload, "script", "").Trim();
        var voice = AiGate.Str(job.Payload, "voice", "").Trim();
        if (voice.Length == 0) voice = _scoping.SettingValue("flow_call_voice");
        var voiceProvider = AiGate.Str(job.Payload, "voice_provider", "").Trim();
        if (voiceProvider.Length == 0 && voice.Length > 0) voiceProvider = "elevenlabs";

        return IsWhatsApp(job)
            ? await PlaceWhatsAppAsync(job, contact, script, voiceProvider, voice)
            : await PlaceSipAsync(job, contact, script, voiceProvider, voice);
    }

    private async Task<CallAttempt> PlaceSipAsync(OutboundJob job, Contact? contact, string script,
        string voiceProvider, string voice)
    {
        var phone = (job.ToAddress ?? "").Trim();
        if (!phone.StartsWith('+'))
            phone = contact?.Phone is { Length: > 0 } p && p.Trim().StartsWith('+') ? p.Trim() : phone;
        if (!phone.StartsWith('+'))
            return new CallAttempt(CallAttempt.Failed, "Phone must be E.164 (start with +) for a phone call.");
        if (_livekit.Settings.MissingForSip() is string reason)
            return new CallAttempt(CallAttempt.Failed, reason);

        var entityId = ContactEntity(contact);
        var metadata = await VoiceDispatch.BuildAsync(_db, _knowledge, _assistant, _scoping, _dataDir, contact,
            new VoiceCallOptions(phone, contact?.Name, contact?.Language, voiceProvider, voice,
                AccountDelivery.Resolve(null, AccountDelivery.LlmProvider, _scoping),
                AccountDelivery.Resolve(null, AccountDelivery.SttProvider, _scoping),
                script.Length > 0 ? script : null, EntityId: entityId));
        var room = LiveKitClient.NewRoomName();
        var placed = await _livekit.PlaceSipCallAsync(room, phone,
            metadata.GetValueOrDefault("contact_name") as string ?? "contact", JsonSerializer.Serialize(metadata));
        return placed.Ok
            ? new CallAttempt(CallAttempt.Placed, "dialling over SIP", room)
            : new CallAttempt(CallAttempt.Failed, placed.Detail);
    }

    private async Task<CallAttempt> PlaceWhatsAppAsync(OutboundJob job, Contact? contact, string script,
        string voiceProvider, string voice)
    {
        var waId = new string((job.ToAddress ?? "").Where(char.IsDigit).ToArray());
        if (waId.Length < 8) return new CallAttempt(CallAttempt.Failed, "No WhatsApp number to call.");
        var account = ResolveAccount(job);
        if (account == null)
            return new CallAttempt(CallAttempt.Failed,
                "No linked WhatsApp (Baileys) number can place the call — pair one in Channels → WhatsApp.");
        if (!_baileys.IsConfigured)
            return new CallAttempt(CallAttempt.Failed, "The Baileys gateway is not configured.", AccountId: account.Id);
        if (_livekit.Settings.MissingForServer() is string reason)
            return new CallAttempt(CallAttempt.Failed, reason, AccountId: account.Id);

        var thread = _db.Threads.FirstOrDefault(t => t.Channel == ChatBranch.Domain.Channels.WhatsApp && t.Key == waId);
        if (thread?.OptedOut == true)
            return new CallAttempt(CallAttempt.Failed, "Contact opted out — calls are blocked.", AccountId: account.Id);
        // cold-call guard: a WhatsApp call to someone who never wrote to us is the ban-risk case
        var guard = _scoping.SettingValue("cold_call_guard", thread?.EntityId ?? account.EntityId);
        if (guard.Length == 0) guard = "block";
        var inbound = thread != null && _db.Messages.Any(m => m.ThreadId == thread.Id && m.Direction == MessageDirection.In);
        if (!inbound && guard == "block")
            return new CallAttempt(CallAttempt.Failed,
                "Cold-call guard: this contact has never messaged us on WhatsApp, so the number won't call them "
                + "first (Routing rules → cold-call guard).", AccountId: account.Id);

        // the line may have become busy between the pre-check and now: wait, don't fail
        if (Busy(account)) return new CallAttempt(CallAttempt.Wait, $"{account.Name} is on a call", AccountId: account.Id);

        var room = LiveKitClient.NewRoomName();
        var metadata = await VoiceDispatch.BuildAsync(_db, _knowledge, _assistant, _scoping, _dataDir, contact,
            new VoiceCallOptions("+" + waId, contact?.Name, contact?.Language, voiceProvider, voice,
                AccountDelivery.Resolve(account, AccountDelivery.LlmProvider, _scoping),
                AccountDelivery.Resolve(account, AccountDelivery.SttProvider, _scoping),
                script.Length > 0 ? script : null, EntityId: thread?.EntityId ?? account.EntityId));
        var dispatched = await _livekit.CreateDispatchAsync(room, JsonSerializer.Serialize(metadata));
        if (!dispatched.Ok) return new CallAttempt(CallAttempt.Failed, dispatched.Detail, AccountId: account.Id);
        var started = await _baileys.StartCallAsync(account.ExternalId, waId, new Dictionary<string, object?>
        {
            ["room"] = room,
            ["livekit_url"] = _livekit.Settings.Url,
            ["token"] = _livekit.ParticipantToken(room, LiveKitClient.WhatsAppIdentity,
                contact?.Name ?? waId, TimeSpan.FromHours(2)),
            ["max_seconds"] = MaxCallSeconds(),
        });
        if (!started.Ok)
        {
            await _livekit.DeleteRoomAsync(room); // don't leave the agent waiting in an empty room
            return started.Detail.Contains("already has a live call", StringComparison.OrdinalIgnoreCase)
                ? new CallAttempt(CallAttempt.Wait, $"{account.Name} is on a call", AccountId: account.Id)
                : new CallAttempt(CallAttempt.Failed, started.Detail, AccountId: account.Id);
        }

        thread ??= _threads.GetOrCreateThread(ChatBranch.Domain.Channels.WhatsApp, waId, contact?.Name,
            accountId: account.Id, contactId: contact?.Id);
        thread.CallId = started.ExternalId.Length > 0 ? started.ExternalId : room;
        thread.CallStatus = "connecting";
        thread.CallRoom = room;
        thread.UpdatedAt = DateTime.UtcNow;
        _db.SaveChanges();
        var note = (!inbound && guard == "warn" ? "[cold-call guard: warn — first contact] " : "")
                   + "WhatsApp call started by a flow — voice agent dispatched.";
        _threads.AppendMessage(thread, MessageDirection.Out, MessageSource.System, note, status: "sent",
            accountId: account.Id);
        return new CallAttempt(CallAttempt.Placed, "ringing over WhatsApp", room, account.Id);
    }

    /// <summary>
    /// The Baileys number that carries the call: the job's own account, else the
    /// contact's sticky assignment, else the account its WhatsApp thread is on,
    /// else the healthy Baileys number with the fewest calls today.
    /// </summary>
    private ChannelAccount? ResolveAccount(OutboundJob job)
    {
        static bool Usable(ChannelAccount? a) => a != null && a.Channel == ChatBranch.Domain.Channels.WhatsApp
            && a.Provider == Providers.Baileys && a.ExternalId.Length > 0
            && a.HealthStatus is not (Health.Banned or Health.Disconnected);

        var accounts = _db.ChannelAccounts.AsNoTracking()
            .Where(a => a.Channel == ChatBranch.Domain.Channels.WhatsApp && a.Provider == Providers.Baileys)
            .ToList();
        ChannelAccount? Find(int? id) => id is int x ? accounts.FirstOrDefault(a => a.Id == x) : null;

        if (Usable(Find(job.AccountId))) return Find(job.AccountId);
        if (job.ContactId is int cid)
        {
            var pinned = _db.AccountAssignments.AsNoTracking()
                .Where(a => a.ContactId == cid && a.Channel == ChatBranch.Domain.Channels.WhatsApp)
                .Select(a => (int?)a.AccountId).FirstOrDefault();
            if (Usable(Find(pinned))) return Find(pinned);
        }
        var waId = new string((job.ToAddress ?? "").Where(char.IsDigit).ToArray());
        var threadAccount = _db.Threads.AsNoTracking()
            .Where(t => t.Channel == ChatBranch.Domain.Channels.WhatsApp && t.Key == waId)
            .Select(t => t.AccountId).FirstOrDefault();
        if (Usable(Find(threadAccount))) return Find(threadAccount);

        var since = Now.AddHours(-24);
        return accounts.Where(Usable)
            .OrderBy(a => _db.OutboundJobs.Count(j => j.Kind == JobKind.Call && j.AccountId == a.Id
                                                      && j.SentAt != null && j.SentAt >= since))
            .ThenBy(a => a.Id)
            .FirstOrDefault();
    }

    private int? ContactEntity(Contact? contact) => contact == null
        ? null
        : _db.ContactEntities.AsNoTracking().Where(l => l.ContactId == contact.Id)
            .OrderBy(l => l.Id).Select(l => (int?)l.EntityId).FirstOrDefault();

    private static int MaxCallSeconds() =>
        int.TryParse(Environment.GetEnvironmentVariable("MAX_CALL_SECONDS"), out var s) && s > 0 ? s : 300;

    /// <summary>
    /// A call that can't be placed must not leave its flow run parked on a reply
    /// only the call could prompt — a parked run also blocks re-enrolment. End
    /// the run (failed, with the reason in its log).
    /// </summary>
    public void UnparkRun(OutboundJob job, string reason)
    {
        try
        {
            if (job.FlowRunId is not int rid) return;
            var run = _db.FlowRuns.FirstOrDefault(r => r.Id == rid);
            // only a run parked on a reply is stuck because of the call
            if (run == null || run.Status != FlowRunStatus.Waiting || run.WaitFor != "reply") return;
            var log = run.Log;
            log.Add(new { t = DateTime.UtcNow.ToString("o"), node = job.FlowNode, evt = $"call not placed: {Cap(reason, 240)} — run ended" });
            run.Log = log.Count > 200 ? log.Skip(log.Count - 200).ToList() : log;
            run.Status = FlowRunStatus.Failed;
            run.WaitFor = "";
            run.WaitUntil = null;
            run.FinishedAt = DateTime.UtcNow;
            run.UpdatedAt = DateTime.UtcNow;
            _db.SaveChanges();
        }
        catch { /* the job's own failure is already recorded */ }
    }

    private static string Cap(string s, int n) => s.Length <= n ? s : s[..n];
}
