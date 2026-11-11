using System.Collections.Concurrent;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace ChatBranch.Data.Channels;

/// <summary>
/// Keeps message attachments in object storage (Cloudflare R2, any S3 API) so the
/// server's disk only holds the recent ones. The local media roots stay the working
/// copy every caller already uses — ffmpeg, speech-to-text, AI vision, range serving
/// and the channel uploads all want a path — and this class sits underneath them:
///
///   * <see cref="SyncOnceAsync"/> uploads every local file the bucket does not have
///     yet, then deletes local copies untouched for <see cref="KeepLocal"/> once the
///     bucket holds the same bytes. Files younger than <see cref="SettleTime"/> are
///     left alone so a write in progress is never uploaded half-done.
///   * <see cref="Hydrate"/> brings an evicted file back on the next
///     <c>Resolve</c>, so callers never see the difference.
///   * <see cref="Delete"/> removes the bucket copy too — a recalled message must be
///     gone, not merely evicted (it would otherwise come back on the next read).
///
/// Object keys mirror the data directory under <see cref="Prefix"/>:
/// <c>{prefix}/whatsapp/media/{wa_id}/{file}</c>, <c>{prefix}/messaging/media/…</c>,
/// <c>{prefix}/library/…</c>. Only those three roots are offloaded; keys, the
/// database, backups and the voice agent's files never leave the volume.
///
/// Off unless <c>R2_BUCKET</c> is set (see <see cref="FromEnvironment"/>); while off,
/// <see cref="Current"/> is null and every hook is a no-op.
/// </summary>
public sealed class MediaOffload
{
    /// <summary>The data-dir subfolders that hold user media.</summary>
    public static readonly string[] Roots = ["whatsapp/media", "messaging/media", "library"];

    /// <summary>The configured instance, or null when offloading is off. Static because
    /// <see cref="MessagingMedia"/> is static and some callers build their own <see cref="MediaStore"/>.</summary>
    public static MediaOffload? Current { get; set; }

    private readonly IMediaObjectStore _store;
    private readonly string _dataDir;
    private readonly ConcurrentDictionary<string, long> _remote = new(StringComparer.Ordinal);
    private volatile bool _indexed;

    public MediaOffload(IMediaObjectStore store, string dataDir, string prefix, TimeSpan keepLocal)
    {
        _store = store;
        _dataDir = Path.GetFullPath(dataDir);
        Prefix = prefix.Trim('/');
        KeepLocal = keepLocal;
    }

    public string Prefix { get; }

    /// <summary>How long a file stays on disk after it was last written or hydrated.</summary>
    public TimeSpan KeepLocal { get; }

    /// <summary>A file this fresh may still be being written: it waits for the next pass.</summary>
    public TimeSpan SettleTime { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The instance env asks for: <c>R2_BUCKET</c>, <c>R2_ENDPOINT</c>, <c>R2_ACCESS_KEY_ID</c>,
    /// <c>R2_SECRET_ACCESS_KEY</c>, <c>R2_PREFIX</c> (default "chatbranch") and
    /// <c>R2_KEEP_LOCAL_DAYS</c> (default 7). null when <c>R2_BUCKET</c> is unset.
    /// </summary>
    public static MediaOffload? FromEnvironment(string dataDir)
    {
        static string? Env(string k) => Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : null;
        if (Env("R2_BUCKET") is not { } bucket) return null;
        var store = new S3MediaObjectStore(
            Env("R2_ENDPOINT") ?? throw new InvalidOperationException("R2_ENDPOINT is required with R2_BUCKET"),
            Env("R2_ACCESS_KEY_ID") ?? throw new InvalidOperationException("R2_ACCESS_KEY_ID is required with R2_BUCKET"),
            Env("R2_SECRET_ACCESS_KEY") ?? throw new InvalidOperationException("R2_SECRET_ACCESS_KEY is required with R2_BUCKET"),
            bucket);
        var days = double.TryParse(Env("R2_KEEP_LOCAL_DAYS"), out var d) && d >= 0 ? d : 7;
        return new MediaOffload(store, dataDir, Env("R2_PREFIX") ?? "chatbranch", TimeSpan.FromDays(days));
    }

    /// <summary>The object key for a file under one of the <see cref="Roots"/>, else null.</summary>
    public string? KeyFor(string fullPath)
    {
        var full = Path.GetFullPath(fullPath);
        if (!full.StartsWith(_dataDir + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return null;
        var rel = full[(_dataDir.Length + 1)..].Replace('\\', '/');
        foreach (var root in Roots)
            if (rel.StartsWith(root + "/", StringComparison.Ordinal) && rel.Length > root.Length + 1)
                return $"{Prefix}/{rel}";
        return null;
    }

    /// <summary>
    /// Make sure an offloaded file is on disk: when it is missing locally, download it
    /// from the bucket. True when the file exists afterwards. Never throws.
    /// </summary>
    public bool Hydrate(string fullPath)
    {
        if (File.Exists(fullPath)) return true;
        if (KeyFor(fullPath) is not { } key) return false;
        // once the bucket is indexed, a key it never had needs no round trip
        if (_indexed && !_remote.ContainsKey(key)) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            var tmp = $"{fullPath}.{Guid.NewGuid():N}.part";
            try
            {
                if (!_store.DownloadAsync(key, tmp, CancellationToken.None).GetAwaiter().GetResult())
                    return false;
                File.Move(tmp, fullPath, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
            return true;
        }
        catch
        {
            return File.Exists(fullPath);
        }
    }

    /// <summary>
    /// Delete the bucket copy of a file and of its transcoder renderings
    /// (<c>.m4a</c>, <c>.ogg</c>). The caller deletes the local file. Never throws.
    /// </summary>
    public void Delete(string fullPath)
    {
        if (KeyFor(fullPath) is not { } key) return;
        foreach (var k in new[] { key, key + ".m4a", key + ".ogg" })
        {
            try
            {
                _store.DeleteAsync(k, CancellationToken.None).GetAwaiter().GetResult();
                _remote.TryRemove(k, out _);
            }
            catch { /* the row going is what matters; a stray object is not served */ }
        }
    }

    /// <summary>What one <see cref="SyncOnceAsync"/> pass did.</summary>
    public sealed record SyncResult(int Uploaded, long UploadedBytes, int Evicted, long EvictedBytes, int Failed);

    /// <summary>Upload what the bucket lacks, then evict what is old and safely stored.</summary>
    public async Task<SyncResult> SyncOnceAsync(CancellationToken ct)
    {
        if (!_indexed)
        {
            await foreach (var (key, size) in _store.ListAsync(Prefix + "/", ct).ConfigureAwait(false))
                _remote[key] = size;
            _indexed = true;
        }

        int uploaded = 0, evicted = 0, failed = 0;
        long upBytes = 0, evBytes = 0;
        var now = DateTime.UtcNow;
        foreach (var root in Roots)
        {
            var dir = Path.Combine(_dataDir, root);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                if (file.EndsWith(".part", StringComparison.Ordinal) || file.EndsWith(".tmp", StringComparison.Ordinal))
                    continue;
                if (KeyFor(file) is not { } key) continue;
                try
                {
                    var info = new FileInfo(file);
                    if (!info.Exists || now - info.LastWriteTimeUtc < SettleTime) continue;
                    if (!_remote.TryGetValue(key, out var remoteSize) || remoteSize != info.Length)
                    {
                        await _store.UploadAsync(key, file, MediaStore.MimeFor(file), ct).ConfigureAwait(false);
                        _remote[key] = info.Length;
                        uploaded++;
                        upBytes += info.Length;
                        continue;   // evict on a later pass, never in the same breath as the upload
                    }
                    if (now - info.LastWriteTimeUtc >= KeepLocal)
                    {
                        File.Delete(file);
                        evicted++;
                        evBytes += info.Length;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { failed++; }
            }
        }
        return new SyncResult(uploaded, upBytes, evicted, evBytes, failed);
    }
}

/// <summary>The four object-store calls <see cref="MediaOffload"/> needs.</summary>
public interface IMediaObjectStore
{
    Task UploadAsync(string key, string filePath, string contentType, CancellationToken ct);

    /// <summary>Write the object to <paramref name="filePath"/>; false when there is no such key.</summary>
    Task<bool> DownloadAsync(string key, string filePath, CancellationToken ct);

    Task DeleteAsync(string key, CancellationToken ct);

    IAsyncEnumerable<(string Key, long Size)> ListAsync(string prefix, CancellationToken ct);
}

/// <summary><see cref="IMediaObjectStore"/> over the S3 API (Cloudflare R2: region "auto").</summary>
public sealed class S3MediaObjectStore : IMediaObjectStore
{
    private readonly AmazonS3Client _s3;
    private readonly string _bucket;

    public S3MediaObjectStore(string endpoint, string accessKey, string secretKey, string bucket)
    {
        _bucket = bucket;
        _s3 = new AmazonS3Client(new BasicAWSCredentials(accessKey, secretKey), new AmazonS3Config
        {
            ServiceURL = endpoint,
            AuthenticationRegion = "auto",
            ForcePathStyle = true,
            // R2 rejects some of the SDK's default integrity checksums on PUT
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        });
    }

    public async Task UploadAsync(string key, string filePath, string contentType, CancellationToken ct) =>
        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            FilePath = filePath,
            ContentType = contentType,
            DisablePayloadSigning = true,
        }, ct).ConfigureAwait(false);

    public async Task<bool> DownloadAsync(string key, string filePath, CancellationToken ct)
    {
        try
        {
            using var res = await _s3.GetObjectAsync(_bucket, key, ct).ConfigureAwait(false);
            var dest = File.Create(filePath);
            await using (dest.ConfigureAwait(false))
                await res.ResponseStream.CopyToAsync(dest, ct).ConfigureAwait(false);
            return true;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task DeleteAsync(string key, CancellationToken ct) =>
        await _s3.DeleteObjectAsync(_bucket, key, ct).ConfigureAwait(false);

    public async IAsyncEnumerable<(string Key, long Size)> ListAsync(string prefix,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var req = new ListObjectsV2Request { BucketName = _bucket, Prefix = prefix };
        while (true)
        {
            var res = await _s3.ListObjectsV2Async(req, ct).ConfigureAwait(false);
            foreach (var o in res.S3Objects ?? []) yield return (o.Key, o.Size ?? 0);
            if (res.IsTruncated != true) yield break;
            req.ContinuationToken = res.NextContinuationToken;
        }
    }
}
