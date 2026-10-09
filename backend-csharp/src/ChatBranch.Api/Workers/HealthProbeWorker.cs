using ChatBranch.Data;
using ChatBranch.Data.Channels;

namespace ChatBranch.Api.Workers;

/// <summary>
/// The periodic channel-health checker (whatsapp/health.py <c>start_checker</c>):
/// every <c>WHATSAPP_HEALTH_INTERVAL</c> seconds (Setting
/// <c>whatsapp_health_interval</c> → env → 300, read live each pass, floor 30)
/// it probes every configured account through <see cref="ChannelHealthProber"/>.
/// The first pass runs shortly after boot so a restarted server does not show
/// every account "unknown" for a whole interval. <c>HEALTH_WORKER=0</c> disables
/// it (the test host does) — and with it the post-save re-checks.
/// </summary>
public class HealthProbeWorker : BackgroundService
{
    public const int DefaultIntervalSeconds = 300;
    private static readonly TimeSpan FirstPassDelay = TimeSpan.FromSeconds(15);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<HealthProbeWorker> _log;

    public HealthProbeWorker(IServiceScopeFactory scopes, ILogger<HealthProbeWorker> log)
    {
        _scopes = scopes;
        _log = log;
    }

    public static bool Enabled => Environment.GetEnvironmentVariable("HEALTH_WORKER") != "0";

    /// <summary>Seconds between passes: Setting row → env → default, never below 30.</summary>
    public static int IntervalSeconds(ScopingService? sc)
    {
        var raw = "";
        try { raw = sc?.SettingValue("whatsapp_health_interval") ?? ""; } catch { }
        if (string.IsNullOrWhiteSpace(raw)) raw = Environment.GetEnvironmentVariable("WHATSAPP_HEALTH_INTERVAL") ?? "";
        return int.TryParse(raw.Trim(), out var s) && s > 0 ? Math.Max(30, s) : DefaultIntervalSeconds;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled) return;
        var delay = FirstPassDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { return; }
            try
            {
                using var scope = _scopes.CreateScope();
                var prober = scope.ServiceProvider.GetRequiredService<ChannelHealthProber>();
                var summary = await prober.RunAsync(ct: stoppingToken);
                _log.LogInformation("channel health: {Checked} account(s) probed", summary.Checked);
                delay = TimeSpan.FromSeconds(IntervalSeconds(scope.ServiceProvider.GetRequiredService<ScopingService>()));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // never the account's secrets: the exception type and message only
                _log.LogError("channel health pass failed: {Type}", ex.GetType().Name);
                delay = TimeSpan.FromSeconds(DefaultIntervalSeconds);
            }
        }
    }
}

/// <summary>
/// The reconnect re-check (health.reset_on_reconfigure's daemon thread): after
/// an operator saves new credentials the account is reset to "unknown"
/// silently and probed again in the background, so a just-fixed account is
/// routable in seconds instead of at the next checker pass. Fire-and-forget,
/// own DI scope, never throws. Off when <see cref="Enabled"/> is false (the test
/// host, unless a test opts in with fake transports).
/// </summary>
public sealed class HealthRecheck
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<HealthRecheck> _log;

    public HealthRecheck(IServiceScopeFactory scopes, ILogger<HealthRecheck> log, bool enabled)
    {
        _scopes = scopes;
        _log = log;
        Enabled = enabled;
    }

    public bool Enabled { get; }

    /// <summary>Probe this account soon, off the request. Returns the task (tests await it).</summary>
    public Task Schedule(int accountId)
    {
        if (!Enabled) return Task.CompletedTask;
        return Task.Run(async () =>
        {
            try
            {
                using var scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ChannelHealthProber>().CheckAccountAsync(accountId);
            }
            catch (Exception ex)
            {
                _log.LogWarning("health re-check of account {Id} failed: {Type}", accountId, ex.GetType().Name);
            }
        });
    }
}
