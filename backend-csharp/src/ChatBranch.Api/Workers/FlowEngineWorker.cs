using ChatBranch.Data.Flows;

namespace ChatBranch.Api.Workers;

/// <summary>
/// Time axis of the flow engine — resumes due runs and evaluates triggers on a
/// timer (the C# replacement for engine.py's ticker). Draining the outbound
/// queue is left to <see cref="OutboundQueueWorker"/> so the two never
/// double-send. Set env <c>FLOWS_WORKER=0</c> to disable (e.g. under tests).
/// </summary>
public class FlowEngineWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<FlowEngineWorker> _log;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    public FlowEngineWorker(IServiceScopeFactory scopes, ILogger<FlowEngineWorker> log)
    {
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Environment.GetEnvironmentVariable("FLOWS_WORKER") == "0") return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var engine = scope.ServiceProvider.GetRequiredService<FlowEngine>();
                engine.ResumeDueRuns();
                engine.EvaluateTriggers();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "flow engine tick failed");
            }
            await Task.Delay(Interval, stoppingToken);
        }
    }
}
