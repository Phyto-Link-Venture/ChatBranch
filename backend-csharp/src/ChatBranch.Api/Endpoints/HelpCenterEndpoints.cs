using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// The public Help Center: knowledge items an operator has PUBLISHED, readable
/// by anyone.
///
/// It is the same <see cref="KnowledgeItem"/> table the AI reads, with one
/// extra flag — which is the point. An operator writes the refund policy once;
/// the assistant quotes it in a conversation and the customer can also read it
/// on the site. Two copies would drift.
///
/// <b>Published is not Active.</b> Active governs whether the AI may use an
/// item; Published governs whether the public may read it. An internal pricing
/// note stays active and unpublished; a public FAQ is both. Anything not
/// explicitly published is invisible here, including expired or inactive rows.
/// </summary>
public static class HelpCenterEndpoints
{
    public static void MapHelpCenterEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- article list, grouped by category -------------------------------
        app.MapGet("/api/help/articles", async (string? q, string? entity, AppDbContext db) =>
        {
            var entityId = await InboxEndpoints.EntityId(db, entity);
            var rows = await PublishedAsync(db, entityId);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var needle = q.Trim();
                rows = rows.Where(a =>
                    a.Title.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                    a.Question.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                    a.Content.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                    a.Tags.Any(t => t.Contains(needle, StringComparison.OrdinalIgnoreCase))).ToList();
            }
            return Results.Ok(new
            {
                categories = rows
                    .GroupBy(a => a.Category.Length > 0 ? a.Category : "General")
                    .OrderBy(g => g.Key)
                    .Select(g => new
                    {
                        name = g.Key,
                        articles = g.OrderByDescending(a => a.Priority).ThenBy(a => a.Title)
                            .Select(Summary).ToList(),
                    }).ToList(),
                total = rows.Count,
            });
        }).AllowAnonymous().RequireCors("widget");

        // ---- one article ------------------------------------------------------
        app.MapGet("/api/help/articles/{slug}", async (string slug, string? entity, AppDbContext db) =>
        {
            var entityId = await InboxEndpoints.EntityId(db, entity);
            var rows = await PublishedAsync(db, entityId);
            var article = rows.FirstOrDefault(a => SlugFor(a) == slug.Trim().ToLowerInvariant());
            if (article == null) return Results.NotFound(new { detail = "No such article." });
            return Results.Ok(new
            {
                slug = SlugFor(article),
                title = article.Title,
                question = article.Question,
                content = article.Content,
                category = article.Category.Length > 0 ? article.Category : "General",
                kind = article.Kind,
                tags = article.Tags,
                updated_at = article.UpdatedAt,
                // Neighbours in the same category — a dead-end article is a
                // dead-end visit.
                related = rows.Where(a => a.Id != article.Id
                                          && a.Category == article.Category)
                    .OrderByDescending(a => a.Priority).Take(5).Select(Summary).ToList(),
            });
        }).AllowAnonymous().RequireCors("widget");
    }

    /// <summary>
    /// Published, live articles for this entity. Date-windowed items (an event
    /// or a notice) disappear from the portal exactly when they stop applying,
    /// the same rule the AI follows.
    /// </summary>
    private static async Task<List<KnowledgeItem>> PublishedAsync(AppDbContext db, int? entityId)
    {
        var now = DateTime.UtcNow;
        var rows = await db.KnowledgeItems.AsNoTracking()
            .Where(k => k.Published && k.Active)
            .Where(k => entityId == null || k.EntityId == null || k.EntityId == entityId)
            .ToListAsync();
        return rows
            .Where(k => k.StartsAt == null || k.StartsAt <= now)
            .Where(k => k.EndsAt == null || k.EndsAt >= now)
            .ToList();
    }

    private static object Summary(KnowledgeItem a) => new
    {
        slug = SlugFor(a),
        title = a.Title,
        question = a.Question,
        // Enough to decide whether to open it, never the whole article.
        excerpt = Excerpt(a.Content, 160),
        category = a.Category.Length > 0 ? a.Category : "General",
        kind = a.Kind,
        updated_at = a.UpdatedAt,
    };

    /// <summary>
    /// The stored slug, else one derived from the title. Deriving keeps every
    /// existing article addressable the moment it is published, with no
    /// migration and no empty-slug 404s.
    /// </summary>
    public static string SlugFor(KnowledgeItem a) =>
        a.Slug.Length > 0 ? a.Slug.Trim().ToLowerInvariant() : Slugify(a.Title);

    public static string Slugify(string raw)
    {
        var chars = (raw ?? "").Trim().ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray();
        var s = new string(chars);
        while (s.Contains("--")) s = s.Replace("--", "-");
        s = s.Trim('-');
        return s.Length > 80 ? s[..80].Trim('-') : s;
    }

    private static string Excerpt(string body, int max)
    {
        var flat = (body ?? "").Replace("\n", " ").Trim();
        return flat.Length <= max ? flat : flat[..max].TrimEnd() + "…";
    }
}
