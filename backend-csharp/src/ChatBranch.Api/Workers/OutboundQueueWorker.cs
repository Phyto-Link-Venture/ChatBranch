using ChatBranch.Data.Flows;

namespace ChatBranch.Api.Workers;

/// <summary>
/// Drains the outbound queue on a timer — the C# replacement for the Django
/// in-process ticker. Each pass runs in its own DI scope (the DbContext and
/// services are scoped). The atomic claim inside QueueService.Drain means
/// running this alongside a manual POST /api/flows/tick can never double-send.
/// Set env <c>QUEUE_WORKER=0</c> to disable (e.g. under tests).
/// </summary>
public class OutboundQueueWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<OutboundQueueWorker> _log;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    public OutboundQueueWorker(IServiceScopeFactory scopes, ILogger<OutboundQueueWorker> log)
    {
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Environment.GetEnvironmentVariable("QUEUE_WORKER") == "0") return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var queue = scope.ServiceProvider.GetRequiredService<QueueService>();
                queue.Drain();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "queue drain failed");
            }
            await Task.Delay(Interval, stoppingToken);
        }
    }
}
