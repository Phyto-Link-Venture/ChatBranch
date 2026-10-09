using ChatBranch.Data.Ai.Learning;

namespace ChatBranch.Api.Workers;

/// <summary>
/// Semantic-retrieval indexer: keeps <c>crm_embedding</c> in step with the
/// knowledge base and the lessons (<see cref="EmbeddingIndexer.SyncAsync"/>) —
/// every 30 s, or at once after a knowledge/lesson write or a re-index
/// (<see cref="Kick"/>). Embedding never happens inline on a webhook or a reply.
/// Does nothing while no embedding provider is configured. Env
/// <c>EMBEDDING_WORKER=0</c> disables it (the test host does, and drives the
/// indexer directly).
/// </summary>
public class EmbeddingWorker : BackgroundService
{
    private static readonly SemaphoreSlim Signal = new(0, 1);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private const int MaxPerPass = 256;

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<EmbeddingWorker> _log;

    public EmbeddingWorker(IServiceScopeFactory scopes, ILogger<EmbeddingWorker> log)
    {
        _scopes = scopes;
        _log = log;
    }

    public static bool Enabled =>
        Environment.GetEnvironmentVariable("EMBEDDING_WORKER") is not ("0" or "false" or "off");

    /// <summary>Ask for a pass now (a knowledge item or lesson changed). Never blocks.</summary>
    public static void Kick()
    {
        try
        {
            if (Signal.CurrentCount == 0) Signal.Release();
        }
        catch (SemaphoreFullException) { /* already signalled */ }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled) return;
        try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
        catch (TaskCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            var embedded = 0;
            try
            {
                using var scope = _scopes.CreateScope();
                embedded = await scope.ServiceProvider.GetRequiredService<EmbeddingIndexer>()
                    .SyncAsync(MaxPerPass, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _log.LogError(ex, "embedding pass failed");
            }
            // a full pass means more is waiting: go again soon instead of in 30 s
            var wait = embedded >= MaxPerPass ? TimeSpan.FromSeconds(1) : Interval;
            try { await Signal.WaitAsync(wait, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
