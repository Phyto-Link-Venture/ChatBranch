using ChatBranch.Data.Channels;

namespace ChatBranch.Api.Workers;

/// <summary>
/// Drives <see cref="MediaOffload"/>: every <c>R2_SYNC_INTERVAL</c> seconds (default
/// 300) it uploads new attachments to object storage and evicts local copies older
/// than <c>R2_KEEP_LOCAL_DAYS</c>. Does nothing unless <c>R2_BUCKET</c> is set.
/// </summary>
public class MediaOffloadWorker : BackgroundService
{
    private readonly ILogger<MediaOffloadWorker> _log;

    public MediaOffloadWorker(ILogger<MediaOffloadWorker> log) => _log = log;

    public static TimeSpan Interval =>
        TimeSpan.FromSeconds(Math.Max(30,
            int.TryParse(Environment.GetEnvironmentVariable("R2_SYNC_INTERVAL"), out var s) ? s : 300));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (MediaOffload.Current is not { } offload) return;
        _log.LogInformation("Media offload on: prefix {Prefix}, keeping {Days} days on disk",
            offload.Prefix, offload.KeepLocal.TotalDays);
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var r = await offload.SyncOnceAsync(stoppingToken);
                if (r.Uploaded + r.Evicted + r.Failed > 0)
                    _log.LogInformation(
                        "Media offload: uploaded {Up} ({UpMb:F1} MB), evicted {Ev} ({EvMb:F1} MB), failed {Failed}",
                        r.Uploaded, r.UploadedBytes / 1048576.0, r.Evicted, r.EvictedBytes / 1048576.0, r.Failed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                _log.LogWarning(e, "Media offload pass failed; retrying next interval");
            }
            try { await Task.Delay(Interval, stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }
}
