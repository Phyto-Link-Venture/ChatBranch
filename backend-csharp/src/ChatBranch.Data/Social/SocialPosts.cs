using System.Text.Json;
using ChatBranch.Domain;

namespace ChatBranch.Data.Social;

/// <summary>
/// A <see cref="SocialPost"/> kept in step with Zernio's post: the status vocabulary,
/// the per-target results, and the post lifecycle webhooks (<c>post.*</c>).
/// </summary>
public static class SocialPosts
{
    public const string Draft = "draft";
    public const string Scheduled = "scheduled";
    public const string Publishing = "publishing";
    public const string Published = "published";
    public const string Partial = "partial";
    public const string Failed = "failed";
    public const string Deleted = "deleted";
    public static readonly string[] Statuses = { Draft, Scheduled, Publishing, Published, Partial, Failed, Deleted };

    /// <summary>The metadata key ChatBranch puts on every Zernio post (echoed on each post event).</summary>
    public const string MetadataKey = "chatbranch_post_id";

    /// <summary>Zernio's post status → ours (pending_approval waits like a scheduled post; cancelled = failed).</summary>
    public static string MapStatus(string zernio) => zernio switch
    {
        "draft" => Draft,
        "pending_approval" or "scheduled" => Scheduled,
        "publishing" => Publishing,
        "published" => Published,
        "partial" => Partial,
        "failed" or "cancelled" => Failed,
        _ => "",
    };

    /// <summary>Apply a Zernio post object (REST <c>post</c> or a webhook's <c>post</c>) to our row.</summary>
    public static void Apply(SocialPost row, JsonElement post)
    {
        if (post.ValueKind != JsonValueKind.Object) return;
        var id = ZernioJson.Str(post, "_id");
        if (id.Length == 0) id = ZernioJson.Str(post, "id");
        if (id.Length > 0) row.ZernioPostId = id;
        var zstatus = ZernioJson.Str(post, "status");
        var mapped = MapStatus(zstatus);
        if (mapped.Length > 0 && row.Status != Deleted) row.Status = mapped;

        var targets = ZernioJson.Arr(post, "platforms").ToList();
        if (targets.Count > 0)
        {
            var results = new List<Dictionary<string, object?>>();
            var errors = new List<string>();
            foreach (var t in targets)
            {
                // REST: errorMessage / platformPostUrl; webhooks: error / publishedUrl
                var error = ZernioJson.Str(t, "errorMessage");
                if (error.Length == 0) error = ZernioJson.Str(t, "error");
                var url = ZernioJson.Str(t, "platformPostUrl");
                if (url.Length == 0) url = ZernioJson.Str(t, "publishedUrl");
                var platform = ZernioJson.Str(t, "platform");
                results.Add(new Dictionary<string, object?>
                {
                    ["account_id"] = ZernioJson.Id(ZernioJson.At(t, "accountId")),
                    // a Facebook Page target (one account, several Pages)
                    ["page_id"] = ZernioJson.Str(t, "platformSpecificData", "pageId") is { Length: > 0 } pg ? pg : null,
                    ["platform"] = platform,
                    ["status"] = ZernioJson.Str(t, "status"),
                    ["url"] = url,
                    ["error"] = error,
                });
                if (error.Length > 0) errors.Add($"{SocialChannels.PlatformLabel(platform)}: {error}");
            }
            row.Results = results;
            row.Error = string.Join("; ", errors);
            if (row.Error.Length > 500) row.Error = row.Error[..500];
        }
        if (zstatus == "cancelled" && row.Error.Length == 0)
            row.Error = "cancelled in Zernio (an account disconnected, or the post was unpublished)";
        row.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>A post event: find our row (by Zernio id, else our metadata) and apply it.</summary>
    public static ZernioOutcome ApplyWebhook(AppDbContext db, string eventName, JsonElement body)
    {
        var post = ZernioJson.At(body, "post");
        var zid = ZernioJson.Str(post, "id");
        var row = zid.Length > 0 ? db.SocialPosts.FirstOrDefault(p => p.ZernioPostId == zid) : null;
        if (row == null && int.TryParse(ZernioJson.Str(post, "metadata", MetadataKey), out var ours))
            row = db.SocialPosts.FirstOrDefault(p => p.Id == ours);
        if (row == null) return new ZernioOutcome("ignored", "not a post composed in ChatBranch");
        if (row.Status == Deleted) return new ZernioOutcome("ignored", "the post was deleted here");

        Apply(row, post);
        if (eventName == "post.platform.deleted")
        {
            var acct = ZernioJson.Str(body, "account", "accountId");
            foreach (var r in row.Results.Where(r => (r.GetValueOrDefault("account_id")?.ToString() ?? "") == acct))
                r["status"] = "deleted";
            row.Results = row.Results.ToList(); // a new list, so EF sees the change
        }
        db.SaveChanges();
        return new ZernioOutcome("ok", $"post {row.Id} → {row.Status}");
    }

    public static Dictionary<string, object?> Payload(SocialPost p) => new()
    {
        ["id"] = p.Id,
        ["entity_id"] = p.EntityId,
        ["zernio_post_id"] = p.ZernioPostId.Length > 0 ? p.ZernioPostId : null,
        ["content"] = p.Content,
        ["media"] = p.Media,
        ["account_ids"] = p.AccountIds,
        ["platforms"] = p.Platforms,
        ["scheduled_at"] = p.ScheduledAt is DateTime s ? DateTime.SpecifyKind(s, DateTimeKind.Utc).ToString("o") : null,
        ["timezone"] = p.Timezone,
        ["status"] = p.Status,
        ["error"] = p.Error,
        ["results"] = p.Results,
        ["created_by"] = p.CreatedBy,
        ["created_at"] = DateTime.SpecifyKind(p.CreatedAt, DateTimeKind.Utc).ToString("o"),
        ["updated_at"] = DateTime.SpecifyKind(p.UpdatedAt, DateTimeKind.Utc).ToString("o"),
    };
}
