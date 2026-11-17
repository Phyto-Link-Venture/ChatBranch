using System.Collections.Concurrent;
using ChatBranch.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace ChatBranch.Data.Channels;

/// <summary>
/// Inbound attachments are fetched OFF the webhook ack (Django's two-phase
/// intake): the webhook stores the message with its text placeholder
/// ("[image]" / the caption) and returns 200 at once; this runs the download in
/// its own DI scope and attaches the file to that message afterwards, bumping
/// the thread so the inbox re-renders. Best-effort: a failed download leaves the
/// placeholder. <see cref="WhenIdleAsync"/> lets tests wait for the work.
/// </summary>
public sealed class MediaFetcher
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ConcurrentDictionary<Guid, Task> _running = new();

    public MediaFetcher(IServiceScopeFactory scopes) => _scopes = scopes;

    public void Enqueue(Func<IServiceProvider, CancellationToken, Task> work)
    {
        var id = Guid.NewGuid();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _running[id] = done.Task;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopes.CreateScope();
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                await work(scope.ServiceProvider, cts.Token);
            }
            catch { /* never surfaces: the placeholder stays */ }
            finally
            {
                _running.TryRemove(id, out _);
                done.TrySetResult();
            }
        });
    }

    /// <summary>Wait until every queued fetch has finished (or the timeout passes).</summary>
    public async Task WhenIdleAsync(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (!_running.IsEmpty && DateTime.UtcNow < until)
            await Task.WhenAny(Task.WhenAll(_running.Values), Task.Delay(50));
    }

    // ---- the jobs -----------------------------------------------------------------

    /// <summary>
    /// WhatsApp Cloud: media id → Graph download (bearer) → data/whatsapp/media →
    /// attached to message <paramref name="messageId"/>. <paramref name="account"/>
    /// is a detached snapshot (it may be the env-default pseudo-account).
    /// </summary>
    public void FetchCloudMedia(int messageId, ChannelAccount account, string mediaId, string mime,
        string filename, string type) =>
        Enqueue(async (sp, ct) =>
        {
            var wa = sp.GetRequiredService<IWhatsAppCloudClient>();
            var r = await wa.DownloadMediaAsync(account, mediaId, MediaStore.MaxBytes, ct);
            if (!r.Ok) return;
            await AttachAsync(sp, messageId, r.Bytes, r.Mime.Length > 0 ? r.Mime : mime, filename,
                type == "sticker" ? "image" : null, ct);
        });

    /// <summary>Messenger / Instagram: CDN url → data/messaging/media → attached.</summary>
    public void FetchUrl(int messageId, string url, string type) =>
        Enqueue(async (sp, ct) =>
        {
            var meta = sp.GetRequiredService<IMetaMessagingClient>();
            var r = await meta.DownloadAsync(url, MediaStore.MaxBytes, ct);
            if (!r.Ok) return;
            var kind = MetaWebhook.AttachmentKind(type);
            await AttachAsync(sp, messageId, r.Bytes, r.Mime, "",
                kind is "image" or "video" or "audio" or "document" ? kind : null, ct);
        });

    /// <summary>
    /// A native-channel attachment that arrived through Zernio: WhatsApp's url is Zernio's
    /// authenticated media endpoint (the key rides only to Zernio's host), the others a
    /// platform CDN link → stored and attached to message <paramref name="messageId"/>.
    /// </summary>
    public void FetchZernio(int messageId, string url, string type) =>
        Enqueue(async (sp, ct) =>
        {
            var zernio = sp.GetRequiredService<ChatBranch.Data.Zernio.IZernioClient>();
            var (bytes, mime, _) = await zernio.DownloadAsync(url, MediaStore.MaxBytes, ct);
            if (bytes == null) return;
            await AttachAsync(sp, messageId, bytes, mime, "", type is "image" or "video" or "audio" or "document" ? type : null, ct);
        });

    /// <summary>A new Messenger / Instagram conversation is keyed by an opaque id —
    /// learn the person's display name once.</summary>
    public void FetchProfileName(int threadId, int accountId) =>
        Enqueue(async (sp, ct) =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var thread = await db.Threads.FindAsync(new object[] { threadId }, ct);
            var account = await db.ChannelAccounts.FindAsync(new object[] { accountId }, ct);
            if (thread == null || account == null || thread.Name != thread.Key) return;
            var name = await sp.GetRequiredService<IMetaMessagingClient>().GetProfileNameAsync(account, thread.Key, ct);
            if (name.Length == 0) return;
            thread.Name = name.Length > 120 ? name[..120] : name;
            thread.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        });

    private static async Task AttachAsync(IServiceProvider sp, int messageId, byte[] bytes, string mime,
        string filename, string? typeOverride, CancellationToken ct)
    {
        var db = sp.GetRequiredService<AppDbContext>();
        var store = sp.GetRequiredService<MediaStore>();
        var msg = await db.Messages.FindAsync(new object[] { messageId }, ct);
        if (msg == null || msg.MediaPath.Length > 0) return; // gone, or already attached
        var thread = await db.Threads.FindAsync(new object[] { msg.ThreadId }, ct);
        if (thread == null) return;
        var rel = await store.SaveAsync(thread.Channel, thread.Key, bytes, MediaStore.ExtFor(mime, filename), ct);
        msg.MediaType = typeOverride ?? MediaStore.TypeForMime(mime);
        msg.MediaPath = rel;
        thread.UpdatedAt = DateTime.UtcNow; // the inbox SSE re-fetches and renders the attachment
        await db.SaveChangesAsync(ct);
    }
}
