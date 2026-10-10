using ChatBranch.Data.Ai.Tasks;

namespace ChatBranch.Api.Workers;

/// <summary>
/// Runs the background AI tasks' due runs (<see cref="AiTaskService.RunDueAsync"/>)
/// every 10 s. Off the webhook path by design: the inbound path only queues.
/// Env <c>AI_TASK_WORKER=0</c> disables it (the test host does, and drives the
/// service directly or through <c>POST /api/ai/tasks/tick</c>).
/// </summary>
public class AiTaskWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
    private const int MaxPerPass = 20;

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<AiTaskWorker> _log;

    public AiTaskWorker(IServiceScopeFactory scopes, ILogger<AiTaskWorker> log)
    {
        _scopes = scopes;
        _log = log;
    }

    public static bool Enabled =>
        Environment.GetEnvironmentVariable("AI_TASK_WORKER") is not ("0" or "false" or "off");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled) return;
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
        catch (TaskCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            var ran = 0;
            try
            {
                using var scope = _scopes.CreateScope();
                ran = await scope.ServiceProvider.GetRequiredService<AiTaskService>()
                    .RunDueAsync(MaxPerPass, ct: stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _log.LogError(ex, "AI task pass failed");
            }
            // a full pass means more is waiting: go again soon
            try { await Task.Delay(ran >= MaxPerPass ? TimeSpan.FromSeconds(1) : Interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
