using ChatBranch.Data.Contacts;

namespace ChatBranch.Api.Workers;

/// <summary>
/// Works the WhatsApp number-lookup queue (<see cref="WhatsAppNumberChecker"/>):
/// a pass every 2 s, at most one lookup per pass through a connected QR number
/// (the limiter enforces ≤ 1 per 2 s per gateway even if a manual run overlaps).
/// <c>IDENTITY_CHECK_WORKER=0</c> disables it; <c>POST
/// /api/contacts/identity-checks/run</c> is the manual seam.
/// </summary>
public class IdentityCheckWorker : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(2);
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<IdentityCheckWorker> _log;

    public IdentityCheckWorker(IServiceScopeFactory scopes, ILogger<IdentityCheckWorker> log)
    {
        _scopes = scopes;
        _log = log;
    }

    public static bool Enabled => Environment.GetEnvironmentVariable("IDENTITY_CHECK_WORKER") != "0";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(Tick, stoppingToken); }
            catch (OperationCanceledException) { return; }
            try
            {
                using var scope = _scopes.CreateScope();
                var checker = scope.ServiceProvider.GetRequiredService<WhatsAppNumberChecker>();
                await checker.RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _log.LogError("whatsapp lookup pass failed: {Type}", ex.GetType().Name);
            }
        }
    }
}
