using ChatBranch.Data;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Workers;

/// <summary>
/// Carries the notifications people are already given to their devices — a browser
/// through Web Push, the phone app through Firebase. Before this, ChatBranch could
/// only tell someone something while they had it open: the bell and the SSE stream
/// are both in-page, so a customer writing in at 9pm reached nobody.
///
/// It reads, it never decides. <see cref="NotificationService"/> stays the one writer
/// of what a person is told, so the organization's mutes, each person's own switches
/// and "only conversations assigned to me" have all been applied before a row exists.
/// The worker only moves fresh UNREAD rows to that person's devices
/// (<see cref="PushDelivery"/>).
///
/// The cursor is a timestamp, not an id, because the bell COLLAPSES: a second message
/// in the same conversation updates the open row and bumps its <c>CreatedAt</c>
/// instead of adding another (<c>RefKey</c>), and a person waiting on a reply should
/// still feel their phone. Rows older than <see cref="MaxAge"/> are never pushed, so
/// a server that was down for an hour does not come back and buzz everyone with
/// yesterday's news.
///
/// Env <c>PUSH_WORKER=0</c> disables it (the test host does, and drives delivery
/// through <c>POST /api/push/tick</c>); <c>PUSH_INTERVAL</c> = seconds between
/// passes (default 10).
/// </summary>
public class PushWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<PushWorker> _log;

    public PushWorker(IServiceScopeFactory scopes, ILogger<PushWorker> log)
    {
        _scopes = scopes;
        _log = log;
    }

    /// <summary>The Setting holding how far the worker has read.</summary>
    public const string CursorKey = "push_last_notified_at";

    /// <summary>A notification older than this is history: it is marked seen, never sent.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    /// <summary>Most rows one pass will carry, so a burst cannot hold the loop.</summary>
    public const int MaxPerPass = 200;

    public static bool Enabled =>
        Environment.GetEnvironmentVariable("PUSH_WORKER") is not ("0" or "false" or "off");

    public static TimeSpan Interval =>
        TimeSpan.FromSeconds(Math.Max(5,
            int.TryParse(Environment.GetEnvironmentVariable("PUSH_INTERVAL"), out var s) ? s : 10));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled) return;
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                await RunAsync(scope.ServiceProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Push pass failed");
            }
            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// One pass: every unread notification written since the cursor, to its
    /// recipient's devices. Returns how many pushes were delivered. Also the manual
    /// seam behind <c>POST /api/push/tick</c>, so tests and a dashboard can drive it.
    /// </summary>
    public static async Task<(int Notifications, int Delivered)> RunAsync(IServiceProvider services,
        CancellationToken ct = default)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var scoping = services.GetRequiredService<ScopingService>();
        var sender = services.GetRequiredService<IPushSender>();

        // nothing to do at all until someone has asked to be alerted
        if (!await db.PushDevices.AnyAsync(d => d.Failures < Domain.PushDevice.MaxFailures, ct)) return (0, 0);

        var now = DateTime.UtcNow;
        var cursor = Cursor(scoping, now);
        var fresh = now - MaxAge;

        var rows = await db.Notifications
            .Where(n => n.CreatedAt > cursor && !n.Read)
            .OrderBy(n => n.CreatedAt).ThenBy(n => n.Id)
            .Take(MaxPerPass)
            .ToListAsync(ct);
        // the cursor moves past everything READ in this pass, sent or not — a row
        // held back for age must not be reconsidered forever
        var highWater = rows.Count > 0 ? rows[^1].CreatedAt : now;

        var delivered = 0;
        var pushed = 0;
        foreach (var row in rows)
        {
            if (row.CreatedAt < fresh) continue;   // too old to be news
            pushed++;
            delivered += await PushDelivery.SendAsync(db, sender, row, PushDelivery.From(row), ct);
        }

        scoping.SetSetting(CursorKey, highWater.ToString("O"));
        return (pushed, delivered);
    }

    /// <summary>
    /// How far the worker has read. A server with no cursor yet starts from NOW, so
    /// switching push on does not replay everything already in the bell.
    /// </summary>
    private static DateTime Cursor(ScopingService scoping, DateTime now)
    {
        var raw = (scoping.SettingValue(CursorKey) ?? "").Trim();
        return DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.AdjustToUniversal
                                              | System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
            ? DateTime.SpecifyKind(at, DateTimeKind.Unspecified)
            : now.AddSeconds(-30);
    }
}
