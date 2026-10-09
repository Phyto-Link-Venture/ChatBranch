using System.Text.Json;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using ChannelIds = ChatBranch.Domain.Channels;

namespace ChatBranch.Data.Channels;

/// <summary>
/// Turns transport observations into health verdicts (health.py
/// record_send_ok / record_send_failure / the checker's per-channel rules).
/// Every transition still goes through <see cref="AccountHealth.SetStatus"/>;
/// this class only decides WHICH status an observation means. Per-recipient
/// failures (a refused address, a blocked bot, a missing chat) never touch the
/// account. Callers save.
/// </summary>
public static class HealthRecorder
{
    /// <summary>Consecutive transient failures before an account reads "degraded".</summary>
    public const int DegradeAfter = 3;

    /// <summary>Meta codes (health.py): a ban, an auth/token problem, an account-level restriction.</summary>
    private static readonly HashSet<int> BanCodes = new() { 368, 131031 };
    private static readonly HashSet<int> AuthCodes = new() { 0, 3, 10, 33, 100, 190, 133010 };
    private static readonly HashSet<int> DegradeCodes = new() { 131048, 131042 };

    /// <summary>Proof of life: a send went through, a probe answered, an inbound arrived.</summary>
    public static void Ok(AppDbContext db, ChannelAccount a, string detail = "")
    {
        a.LastOkAt = DateTime.UtcNow;
        a.FailStreak = 0;
        AccountHealth.SetStatus(db, a, Health.Healthy, detail);
    }

    /// <summary>A timeout / 5xx / rate limit: degraded only once it keeps happening.</summary>
    public static void Transient(AppDbContext db, ChannelAccount a, string detail)
    {
        a.FailStreak += 1;
        if (a.FailStreak >= DegradeAfter) AccountHealth.SetStatus(db, a, Health.Degraded, detail);
    }

    public static void EmailSendFailure(AppDbContext db, ChannelAccount a, EmailResult r)
    {
        switch (r.Failure)
        {
            case EmailFailure.Recipient: return;
            case EmailFailure.Auth: AccountHealth.SetStatus(db, a, Health.Disconnected, r.Detail); return;
            default: Transient(db, a, r.Detail); return;
        }
    }

    public static void TelegramSendFailure(AppDbContext db, ChannelAccount a, TelegramResult r)
    {
        if (r.TokenRejected)
            AccountHealth.SetStatus(db, a, Health.Disconnected, "bot token invalid or revoked");
        else if (r.HttpStatus is 400 or 403)
            return; // chat not found / bot blocked by this user — the recipient's problem
        else
            Transient(db, a, r.Description);
    }

    /// <summary>The verdict a failed Graph ping means (null = transient).</summary>
    public static string? GraphVerdict(GraphPing p)
    {
        if (p.Ok) return Health.Healthy;
        if (p.ErrorCode is int c && BanCodes.Contains(c)) return Health.Banned;
        if (p.ErrorCode is int a && (AuthCodes.Contains(a) || a is >= 200 and <= 299)) return Health.Disconnected;
        if (p.HttpStatus == 401) return Health.Disconnected;
        var text = p.Message.ToLowerInvariant();
        if (p.HttpStatus == 403 && (text.Contains("token") || text.Contains("oauth"))) return Health.Disconnected;
        if (text.Contains("access token") && (text.Contains("expired") || text.Contains("invalid")
                                              || text.Contains("revoked"))) return Health.Disconnected;
        if (p.ErrorCode is int d && DegradeCodes.Contains(d)) return Health.Degraded;
        return null;
    }
}

/// <summary>What one probe concluded. <c>Status == null</c> + <c>Transient</c> = count a failure.</summary>
public sealed record ProbeVerdict(string? Status, string Detail, bool Transient = false);

public sealed record HealthRunSummary(int Checked, int Skipped, string? GlobalEmail, DateTime CheckedAt,
    IReadOnlyDictionary<string, int> ByChannel);

/// <summary>
/// The periodic channel-health checker (port of whatsapp/health.py
/// <c>run_checks</c> / <c>check_account</c>). Probes every CONFIGURED account:
///
///  * WhatsApp Cloud — Graph token ping (<c>GET /{phone_number_id}</c>); Meta's
///    error code decides banned / disconnected / degraded / transient.
///  * WhatsApp Baileys — skipped: the gateway pushes its own lifecycle.
///  * Telegram — <c>getMe</c> (revoked token → disconnected) then
///    <c>getWebhookInfo</c> (no webhook, a recent delivery error or a stuck
///    backlog → degraded).
///  * Email — SMTP connect + login (refused login → disconnected; unreachable →
///    transient). The global SMTP fallback has no row: its verdict goes to the
///    <c>email_health</c> Setting the channel-health card reads.
///  * Messenger / Instagram — <c>GET /me</c> with the page token.
///  * Voice (SIP) — nothing to probe: health is config presence, computed on read.
///
/// Network probes run concurrently; verdicts are applied one by one on the
/// caller's DbContext, each transition through <see cref="AccountHealth.SetStatus"/>.
/// Unconfigured (sandbox) accounts are never probed and never notify.
/// </summary>
public sealed class ChannelHealthProber
{
    public const string EmailHealthKey = "email_health";
    public const string CheckedAtKey = "channel_health_checked_at";
    /// <summary>Webhook errors newer than this mark a bot degraded.</summary>
    public static readonly TimeSpan WebhookErrorWindow = TimeSpan.FromMinutes(30);
    public const int PendingBacklogLimit = 20;

    private readonly AppDbContext _db;
    private readonly ScopingService _scoping;
    private readonly ITelegramClient _telegram;
    private readonly IEmailTransport _email;
    private readonly IGraphHealthProbe _graph;

    public ChannelHealthProber(AppDbContext db, ScopingService scoping, ITelegramClient telegram,
        IEmailTransport email, IGraphHealthProbe graph)
    {
        _db = db;
        _scoping = scoping;
        _telegram = telegram;
        _email = email;
        _graph = graph;
    }

    /// <summary>Does this account have something to probe?</summary>
    public static bool Probeable(ChannelAccount a) => a.Channel switch
    {
        ChannelIds.WhatsApp => a.Provider != Providers.Baileys
                             && a.AccessToken.Length > 0 && a.ExternalId.Length > 0,
        ChannelIds.Telegram => TelegramBot.Configured(a),
        ChannelIds.Email => EmailConfig.AccountConfigured(a),
        ChannelIds.Facebook or ChannelIds.Instagram => a.AccessToken.Length > 0,
        _ => false,
    };

    /// <summary>The network half: never touches the DbContext, never throws.</summary>
    public async Task<ProbeVerdict?> ProbeAsync(ChannelAccount a, CancellationToken ct = default)
    {
        if (!Probeable(a)) return null;
        try
        {
            switch (a.Channel)
            {
                case ChannelIds.Telegram:
                    return await ProbeTelegramAsync(a, ct);
                case ChannelIds.Email:
                {
                    var r = await _email.ProbeAsync(EmailConfig.ForAccount(a, _scoping), ct);
                    return r.Ok ? new ProbeVerdict(Health.Healthy, "")
                        : r.Failure == EmailFailure.Connect ? new ProbeVerdict(null, r.Detail, Transient: true)
                        : new ProbeVerdict(Health.Disconnected, r.Detail);
                }
                default:
                {
                    var p = await _graph.PingAsync(a, ct);
                    var verdict = HealthRecorder.GraphVerdict(p);
                    var detail = p.Ok ? "" : $"Meta API {(p.HttpStatus > 0 ? p.HttpStatus.ToString() : "unreachable")}"
                                             + (p.ErrorCode is int c ? $" (code {c})" : "")
                                             + (p.Message.Length > 0 ? $": {p.Message}" : "");
                    return verdict == null ? new ProbeVerdict(null, detail, Transient: true)
                        : new ProbeVerdict(verdict, detail);
                }
            }
        }
        catch (Exception ex)
        {
            return new ProbeVerdict(null, $"health check failed: {ex.GetType().Name}", Transient: true);
        }
    }

    private async Task<ProbeVerdict> ProbeTelegramAsync(ChannelAccount a, CancellationToken ct)
    {
        var me = await _telegram.GetMeAsync(a, ct);
        if (me.TokenRejected) return new ProbeVerdict(Health.Disconnected, "bot token invalid or revoked");
        if (!me.Ok)
            return me.HttpStatus == 0
                ? new ProbeVerdict(Health.Degraded, $"Telegram API unreachable: {me.Description}")
                : new ProbeVerdict(Health.Disconnected, me.Description);

        var info = await _telegram.GetWebhookInfoAsync(a, ct);
        if (!info.Ok) return new ProbeVerdict(Health.Degraded, $"could not read the webhook state: {info.Description}");
        if (info.Str("url").Length == 0)
            return new ProbeVerdict(Health.Degraded,
                "bot alive, but no webhook set — inbound messages won't arrive (Channels → Register webhook)");
        var lastError = info.Long("last_error_date");
        if (lastError > 0 && DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(lastError) < WebhookErrorWindow)
        {
            var why = info.Str("last_error_message");
            return new ProbeVerdict(Health.Degraded,
                $"webhook failing: {(why.Length > 140 ? why[..140] : why.Length > 0 ? why : "unknown error")}");
        }
        var pending = info.Long("pending_update_count");
        if (pending > PendingBacklogLimit)
            return new ProbeVerdict(Health.Degraded, $"{pending} updates stuck pending — webhook not keeping up");
        return new ProbeVerdict(Health.Healthy, "");
    }

    /// <summary>The DB half: one verdict onto one tracked account (caller saves).</summary>
    public void Apply(ChannelAccount a, ProbeVerdict? v)
    {
        if (v == null) return;
        if (v.Transient) { HealthRecorder.Transient(_db, a, v.Detail); return; }
        if (v.Status == Health.Healthy) { HealthRecorder.Ok(_db, a, ""); return; }
        if (v.Status != null) AccountHealth.SetStatus(_db, a, v.Status, v.Detail);
    }

    /// <summary>Probe ONE account now (the reconnect re-check). True when a probe ran.</summary>
    public async Task<bool> CheckAccountAsync(int accountId, CancellationToken ct = default)
    {
        var a = await _db.ChannelAccounts.FirstOrDefaultAsync(x => x.Id == accountId, ct);
        if (a == null || !Probeable(a)) return false;
        Apply(a, await ProbeAsync(a, ct));
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// One checker pass over <paramref name="accountIds"/> (null = every account)
    /// plus, when asked, the global SMTP fallback. Stamps <c>channel_health_checked_at</c>.
    /// </summary>
    public async Task<HealthRunSummary> RunAsync(IReadOnlyCollection<int>? accountIds = null,
        bool includeGlobalEmail = true, CancellationToken ct = default)
    {
        var query = _db.ChannelAccounts.AsQueryable();
        if (accountIds != null) query = query.Where(a => accountIds.Contains(a.Id));
        var accounts = await query.OrderBy(a => a.Id).ToListAsync(ct);
        var targets = accounts.Where(Probeable).ToList();

        using var gate = new SemaphoreSlim(8);
        var verdicts = await Task.WhenAll(targets.Select(async a =>
        {
            await gate.WaitAsync(ct);
            try { return (Account: a, Verdict: await ProbeAsync(a, ct)); }
            finally { gate.Release(); }
        }));

        var byChannel = new Dictionary<string, int>();
        foreach (var (account, verdict) in verdicts)
        {
            try
            {
                Apply(account, verdict);
                byChannel[account.Channel] = byChannel.GetValueOrDefault(account.Channel) + 1;
            }
            catch { /* one account's bookkeeping must not stop the rest */ }
        }

        string? globalEmail = null;
        if (includeGlobalEmail)
        {
            try { globalEmail = await ProbeGlobalEmailAsync(ct); }
            catch { /* the chip stays at its last value */ }
        }

        var now = DateTime.UtcNow;
        try { _scoping.SetSetting(CheckedAtKey, DateTime.SpecifyKind(now, DateTimeKind.Utc).ToString("o")); }
        catch { /* read-only DB: the verdicts below still save */ }
        await _db.SaveChangesAsync(ct);
        return new HealthRunSummary(targets.Count, accounts.Count - targets.Count, globalEmail, now, byChannel);
    }

    /// <summary>The rowless global SMTP fallback: verdict → Setting <c>email_health</c>.</summary>
    private async Task<string?> ProbeGlobalEmailAsync(CancellationToken ct)
    {
        var global = EmailConfig.GlobalSettings(_scoping);
        if (!global.SmtpConfigured) return null;
        var r = await _email.ProbeAsync(global, ct);
        var health = r.Ok ? Health.Healthy
            : r.Failure == EmailFailure.Connect ? Health.Degraded
            : Health.Disconnected;
        _scoping.SetSetting(EmailHealthKey, JsonSerializer.Serialize(new
        {
            health,
            detail = r.Ok ? "" : r.Detail,
            checked_at = DateTime.UtcNow.ToString("o"),
        }));
        return health;
    }
}
