using System.Security.Claims;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// Pronunciation dictionary — port of <c>calls/views.py:pronunciation_settings</c>,
/// rendered by Settings → Pronunciation
/// (<c>components/settings/PronunciationSettings.tsx</c>).
///
/// term → respelling ("alias") overrides applied to ElevenLabs TTS. NOT real
/// IPA: eleven_multilingual_v2 (the model this app needs for ms/zh) only
/// supports alias rules, not phoneme/IPA rules — so an entry is a plain
/// phonetic respelling ("Doo-it Ghee-nee"), never phonetic symbols. OpenAI
/// voices have no pronunciation-control API, which is why the payload carries
/// <c>elevenlabs_only: true</c> for the banner the settings tab shows.
///
/// Storage differs from Django ON PURPOSE: Django keeps the list in
/// <c>data/pronunciation.json</c> next to voices.json; this port is
/// database-first, so the terms live in the org-wide Setting row
/// <c>pronunciation_terms</c> (a JSON array). The key is deliberately NOT in
/// ScopingService.EntityScopedSettings — a voice's pronunciation of a brand
/// name is organization-wide, like the credentials it rides on.
///
/// WIRE FORMAT IS snake_case, built inline (no camelCase Dtos mappers).
/// </summary>
public static class PronunciationEndpoints
{
    private const string TermsKey = "pronunciation_terms";

    public record TermItem(string term, string alias);

    public static void MapPronunciationEndpoints(this IEndpointRouteBuilder app)
    {
        // Open like the Django view (and like the settings tab's fetch, which
        // sends no bearer token): the terms are display strings, not secrets.
        app.MapGet("/api/pronunciation", (AppDbContext db) => Results.Ok(new
        {
            terms = LoadTerms(db),
            elevenlabs_only = true,
        })).AllowAnonymous();

        app.MapPost("/api/pronunciation", async (JsonElement body, AppDbContext db,
            ScopingService sc, ClaimsPrincipal me, HttpContext ctx) =>
        {
            if (body.ValueKind != JsonValueKind.Object ||
                !body.TryGetProperty("terms", out var raw) || raw.ValueKind != JsonValueKind.Array)
                return Results.Json(new { detail = "terms must be a list of {term, alias}." },
                    statusCode: 400);

            var terms = new List<TermItem>();
            foreach (var el in raw.EnumerateArray())
            {
                var term = Str(el, "term").Trim();
                var alias = Str(el, "alias").Trim();
                if (term.Length == 0 || alias.Length == 0)
                    return Results.Json(
                        new { detail = "each term needs a non-empty term and alias." },
                        statusCode: 400);
                terms.Add(new TermItem(term, alias));
            }

            sc.SetSetting(TermsKey, JsonSerializer.Serialize(terms));
            Audit(db, me, ctx, "settings.update", "pronunciation", $"terms={terms.Count}");

            // Django syncs the list into an ElevenLabs pronunciation dictionary
            // and 501s when ELEVENLABS_API_KEY is unset. This port has no
            // ElevenLabs client yet, so the terms are stored either way and
            // `synced` reports the truth rather than failing the save — an
            // unsynced term is a voice that reads it as spelled, not data loss.
            var configured = ElevenLabsConfigured(sc);
            await Task.CompletedTask;
            return Results.Ok(new
            {
                terms,
                elevenlabs_only = true,
                synced = false,
                detail = configured
                    ? "Saved. ElevenLabs dictionary sync is not implemented in this API yet."
                    : "Saved. Pronunciation sync not configured (ELEVENLABS_API_KEY unset).",
            });
        }).RequireAuthorization(AuthPolicies.AdminPlus);
    }

    /// <summary>
    /// pronunciation.load_terms — the stored list, or empty. Never raises: a
    /// malformed row degrades to "no overrides", never a 500 on a settings tab.
    /// </summary>
    private static List<TermItem> LoadTerms(AppDbContext db)
    {
        try
        {
            var row = db.Settings.AsNoTracking()
                .FirstOrDefault(s => s.Key == TermsKey && s.EntityId == null);
            if (row == null || string.IsNullOrWhiteSpace(row.Value)) return new List<TermItem>();
            var parsed = JsonSerializer.Deserialize<List<TermItem>>(row.Value);
            return parsed?.Where(t => !string.IsNullOrWhiteSpace(t.term) &&
                                      !string.IsNullOrWhiteSpace(t.alias)).ToList()
                   ?? new List<TermItem>();
        }
        catch { return new List<TermItem>(); }
    }

    /// <summary>Setting row → .env, the same order the AI-provider keys resolve in.</summary>
    private static bool ElevenLabsConfigured(ScopingService sc)
    {
        if (sc.SettingValue("elevenlabs_api_key").Length > 0) return true;
        return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ELEVENLABS_API_KEY"));
    }

    private static string Str(JsonElement el, string key)
    {
        if (el.ValueKind != JsonValueKind.Object) return "";
        if (!el.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.String) return "";
        return v.GetString() ?? "";
    }

    private static void Audit(AppDbContext db, ClaimsPrincipal me, HttpContext ctx,
        string action, string target, string detail)
    {
        try
        {
            db.AuditLogs.Add(new AuditLog
            {
                Actor = me.Identity?.Name ?? "system",
                Action = action,
                Target = target,
                Detail = detail,
                Ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "",
                CreatedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
        }
        catch { /* an audit failure must never fail the operation itself */ }
    }
}
