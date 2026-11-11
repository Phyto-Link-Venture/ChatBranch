using System.Collections.Concurrent;
using ChatBranch.Data.Channels;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>An object store in memory: key → bytes.</summary>
public sealed class MemoryObjectStore : IMediaObjectStore
{
    public ConcurrentDictionary<string, byte[]> Objects { get; } = new();
    public int Downloads { get; private set; }

    public async Task UploadAsync(string key, string filePath, string contentType, CancellationToken ct) =>
        Objects[key] = await File.ReadAllBytesAsync(filePath, ct);

    public async Task<bool> DownloadAsync(string key, string filePath, CancellationToken ct)
    {
        Downloads++;
        if (!Objects.TryGetValue(key, out var bytes)) return false;
        await File.WriteAllBytesAsync(filePath, bytes, ct);
        return true;
    }

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        Objects.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<(string Key, long Size)> ListAsync(string prefix,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        foreach (var (k, v) in Objects)
            if (k.StartsWith(prefix, StringComparison.Ordinal)) yield return (k, v.Length);
    }
}

public class MediaOffloadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cb-offload-" + Guid.NewGuid().ToString("N"));
    private readonly MemoryObjectStore _store = new();

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private MediaOffload Offload(TimeSpan keep) =>
        new(_store, _dir, "cb", keep) { SettleTime = TimeSpan.Zero };

    private string Put(string rel, string content, DateTime? written = null)
    {
        var full = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        if (written is { } w) File.SetLastWriteTimeUtc(full, w);
        return full;
    }

    [Fact]
    public void Only_media_roots_map_to_keys()
    {
        var o = Offload(TimeSpan.FromDays(7));
        Assert.Equal("cb/whatsapp/media/6012/a.jpg", o.KeyFor(Path.Combine(_dir, "whatsapp/media/6012/a.jpg")));
        Assert.Equal("cb/messaging/media/email/7/b.pdf", o.KeyFor(Path.Combine(_dir, "messaging/media/email/7/b.pdf")));
        Assert.Equal("cb/library/2026-11/c.png", o.KeyFor(Path.Combine(_dir, "library/2026-11/c.png")));
        Assert.Null(o.KeyFor(Path.Combine(_dir, "chatbranch.sqlite3")));
        Assert.Null(o.KeyFor(Path.Combine(_dir, "keys/key-1.xml")));
        Assert.Null(o.KeyFor(Path.Combine(_dir, "whatsapp/auth/creds.json")));
        Assert.Null(o.KeyFor(Path.Combine(_dir, "..", "elsewhere", "whatsapp/media/x.jpg")));
    }

    [Fact]
    public async Task Sync_uploads_then_evicts_only_old_files_already_stored()
    {
        var old = Put("whatsapp/media/6012/old.jpg", "old", DateTime.UtcNow.AddDays(-10));
        var fresh = Put("messaging/media/email/7/new.pdf", "new");
        var db = Put("chatbranch.sqlite3", "db", DateTime.UtcNow.AddDays(-10));
        var o = Offload(TimeSpan.FromDays(7));

        var first = await o.SyncOnceAsync(default);
        Assert.Equal(2, first.Uploaded);
        Assert.Equal(0, first.Evicted);   // never evicted in the pass that uploaded it
        Assert.True(File.Exists(old));
        Assert.False(_store.Objects.ContainsKey("cb/chatbranch.sqlite3"));

        var second = await o.SyncOnceAsync(default);
        Assert.Equal(0, second.Uploaded);
        Assert.Equal(1, second.Evicted);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(db));
    }

    [Fact]
    public async Task A_file_too_fresh_to_settle_waits()
    {
        Put("whatsapp/media/6012/writing.jpg", "partial");
        var o = new MediaOffload(_store, _dir, "cb", TimeSpan.Zero) { SettleTime = TimeSpan.FromMinutes(2) };
        var r = await o.SyncOnceAsync(default);
        Assert.Equal(0, r.Uploaded);
        Assert.Empty(_store.Objects);
    }

    [Fact]
    public async Task Hydrate_brings_an_evicted_file_back()
    {
        var full = Put("whatsapp/media/6012/v.ogg", "voice", DateTime.UtcNow.AddDays(-10));
        var o = Offload(TimeSpan.FromDays(7));
        await o.SyncOnceAsync(default);
        await o.SyncOnceAsync(default);
        Assert.False(File.Exists(full));

        Assert.True(o.Hydrate(full));
        Assert.Equal("voice", File.ReadAllText(full));
        // freshly hydrated = recently used: the next pass keeps it
        Assert.Equal(0, (await o.SyncOnceAsync(default)).Evicted);
    }

    [Fact]
    public async Task Hydrate_skips_the_round_trip_for_a_key_the_bucket_never_had()
    {
        var o = Offload(TimeSpan.FromDays(7));
        await o.SyncOnceAsync(default);   // indexes the (empty) bucket
        Assert.False(o.Hydrate(Path.Combine(_dir, "whatsapp/media/6012/missing.jpg")));
        Assert.Equal(0, _store.Downloads);
    }

    [Fact]
    public async Task Delete_removes_the_object_and_its_renderings()
    {
        var full = Put("whatsapp/media/6012/v.weba", "voice", DateTime.UtcNow.AddDays(-1));
        Put("whatsapp/media/6012/v.weba.ogg", "ogg", DateTime.UtcNow.AddDays(-1));
        var o = Offload(TimeSpan.FromDays(7));
        await o.SyncOnceAsync(default);
        Assert.Equal(2, _store.Objects.Count);

        o.Delete(full);
        Assert.Empty(_store.Objects);
        File.Delete(full);
        Assert.False(o.Hydrate(full));   // a recalled file must not come back
    }

    [Fact]
    public async Task MediaStore_resolve_hydrates_through_the_current_offload()
    {
        var o = Offload(TimeSpan.Zero);
        var media = new MediaStore(_dir);
        var rel = await media.SaveAsync(ChatBranch.Domain.Channels.WhatsApp, "6012", "hi"u8.ToArray(), ".txt");
        await o.SyncOnceAsync(default);   // upload
        await o.SyncOnceAsync(default);   // evict (keep = 0)
        var full = Path.Combine(media.Root(ChatBranch.Domain.Channels.WhatsApp), rel);
        Assert.False(File.Exists(full));

        Assert.Null(media.Resolve(ChatBranch.Domain.Channels.WhatsApp, rel));   // offload off: just missing
        MediaOffload.Current = o;
        try
        {
            var resolved = media.Resolve(ChatBranch.Domain.Channels.WhatsApp, rel);
            Assert.NotNull(resolved);
            Assert.Equal("hi", File.ReadAllText(resolved!));
        }
        finally { MediaOffload.Current = null; }
    }
}
