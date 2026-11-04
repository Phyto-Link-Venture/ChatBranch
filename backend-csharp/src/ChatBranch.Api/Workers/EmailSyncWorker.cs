using ChatBranch.Data.Channels;

namespace ChatBranch.Api.Workers;

/// <summary>
/// Runs the two-way IMAP sync (<see cref="EmailSyncService.SyncAsync"/>) on the
/// server every <see cref="Interval"/>, so inbound mail and mail sent from other
/// apps (the Sent folder) arrive whether or not anyone has the inbox open.
/// Before this worker, only the dashboard's /chats page nudged the sync (every
/// 60 s while open), so API keys, MCP tools and scheduled jobs read a mailbox
/// that stopped moving whenever nobody was looking at it.
///
/// Uses the service's own 30 s throttle (never <c>force</c>), so it shares one
/// pass with any dashboards that are polling. When a pass comes back full (a
/// folder handed over <see cref="EmailSyncService.MaxFetchPerMailbox"/> mails)
/// the next pass runs as soon as the throttle allows, so a backlog drains in
/// minutes instead of one page-load at a time.
///
/// Env <c>EMAIL_SYNC_WORKER=0</c> disables it (the test host does, and drives
/// the sync through <c>POST /api/messaging/email/sync</c>);
/// <c>EMAIL_SYNC_INTERVAL</c> = seconds between passes (default 60, minimum 30).
/// </summary>
public class EmailSyncWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<EmailSyncWorker> _log;

    public EmailSyncWorker(IServiceScopeFactory scopes, ILogger<EmailSyncWorker> log)
    {
        _scopes = scopes;
        _log = log;
    }

    public static bool Enabled =>
        Environment.GetEnvironmentVariable("EMAIL_SYNC_WORKER") is not ("0" or "false" or "off");

    public static TimeSpan Interval
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("EMAIL_SYNC_INTERVAL");
            var secs = int.TryParse(raw, out var s) ? s : 60;
            return TimeSpan.FromSeconds(Math.Max(secs, (int)EmailSyncService.MinInterval.TotalSeconds));
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled) return;
        try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
        catch (TaskCanceledException) { return; }

        var interval = Interval;
        while (!stoppingToken.IsCancellationRequested)
        {
            var backlog = false;
            try
            {
                using var scope = _scopes.CreateScope();
                var report = await scope.ServiceProvider.GetRequiredService<EmailSyncService>()
                    .SyncAsync(force: false, stoppingToken);
                if (report.Errors.Count > 0)
                    _log.LogWarning("Email sync: {Errors}", string.Join(" | ", report.Errors));
                backlog = report.Fetched + report.Skipped + report.Duplicates + report.SentImported
                          + report.SentDuplicates + report.SentSkipped
                          >= EmailSyncService.MaxFetchPerMailbox;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Email sync pass failed");
            }
            // a full pass means more is waiting: go again as soon as the throttle allows
            try { await Task.Delay(backlog ? EmailSyncService.MinInterval : interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
