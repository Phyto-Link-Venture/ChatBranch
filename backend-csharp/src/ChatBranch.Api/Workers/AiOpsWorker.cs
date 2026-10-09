using ChatBranch.Data.Ai;

namespace ChatBranch.Api.Workers;

/// <summary>
/// The AI-ops ticker: sends held AI drafts whose auto-send deadline passed,
/// expires drafts a teammate already answered, and runs the "follow up when the
/// customer goes quiet" pass. Each pass gets its own DI scope. Env
/// <c>AI_FOLLOWUP_WORKER=0</c> disables it (the test host does, and drives
/// <see cref="AiGate.ProcessDue"/> / <see cref="AiFollowUpService.Tick"/>
/// explicitly — or POST /api/ai/monitor/tick).
/// </summary>
public class AiOpsWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<AiOpsWorker> _log;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    public AiOpsWorker(IServiceScopeFactory scopes, ILogger<AiOpsWorker> log)
    {
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Environment.GetEnvironmentVariable("AI_FOLLOWUP_WORKER") is "0" or "false") return;
        // let the host finish booting (seed, migrations) before the first pass
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
        catch (TaskCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                scope.ServiceProvider.GetRequiredService<AiGate>().ProcessDue();
                scope.ServiceProvider.GetRequiredService<AiFollowUpService>().Tick();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "ai-ops pass failed");
            }
            try { await Task.Delay(Interval, stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }
}
