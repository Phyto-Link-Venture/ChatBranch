using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatBranch.Api.Auth;
using ChatBranch.Data;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// Custom Fields — the per-vertical label schema for Customers (crm.Contact).
/// A tutoring deployment defines "Grade"; a clinic defines something else.
/// The definitions live in the global Setting <c>custom_fields</c>; the VALUES
/// are saved into each contact's existing free-form <c>attributes</c> JSON
/// (which the AI already reads), so no schema migration is needed and the data
/// model stays generic. (Per-relationship fields — a driver's licence number,
/// an outside company's contract end — ride on registry TAGS instead.)
///
/// Field kinds: text · select (one option) · multiselect · number · boolean.
/// Reads are open to any authed role (forms render them); writes are admin+.
/// </summary>
public static class CustomFieldsEndpoints
{
    private const string Key = "custom_fields";
    private static readonly string[] Targets = { "customer" };
    private static readonly string[] Kinds = { "text", "select", "multiselect", "number", "boolean" };

    public class FieldDef
    {
        [JsonPropertyName("key")] public string Key { get; set; } = "";
        [JsonPropertyName("label")] public string Label { get; set; } = "";
        [JsonPropertyName("kind")] public string Kind { get; set; } = "text";
        [JsonPropertyName("options")] public List<string> Options { get; set; } = new();
        [JsonPropertyName("required")] public bool Required { get; set; }
        [JsonPropertyName("position")] public int Position { get; set; }
    }

    public class Schema
    {
        [JsonPropertyName("customer")] public List<FieldDef> Customer { get; set; } = new();
    }

    private static Schema Load(ScopingService sc)
    {
        var raw = sc.SettingValue(Key);
        if (string.IsNullOrWhiteSpace(raw)) return new Schema();
        try { return JsonSerializer.Deserialize<Schema>(raw) ?? new Schema(); }
        catch { return new Schema(); }
    }

    private static object Payload(Schema s) => new
    {
        customer = s.Customer.OrderBy(f => f.Position).ToList(),
        kinds = Kinds,
        targets = Targets,
    };

    /// <summary>Slug a label into a stable attribute key ("Preferred Language" → preferred_language).</summary>
    private static string Slug(string label)
    {
        var chars = label.Trim().ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray();
        var s = new string(chars);
        while (s.Contains("__")) s = s.Replace("__", "_");
        return s.Trim('_');
    }

    private static string? Validate(List<FieldDef> fields, string target)
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < fields.Count; i++)
        {
            var f = fields[i];
            f.Label = (f.Label ?? "").Trim();
            if (f.Label.Length == 0) return $"{target}: field #{i + 1} needs a label.";
            f.Key = string.IsNullOrWhiteSpace(f.Key) ? Slug(f.Label) : Slug(f.Key);
            if (f.Key.Length == 0) return $"{target}: '{f.Label}' produces an empty key.";
            if (!seen.Add(f.Key)) return $"{target}: duplicate field key '{f.Key}'.";
            f.Kind = (f.Kind ?? "text").Trim().ToLowerInvariant();
            if (!Kinds.Contains(f.Kind)) return $"{target}: '{f.Label}' has unknown kind '{f.Kind}'.";
            f.Options = (f.Options ?? new()).Select(o => o.Trim()).Where(o => o.Length > 0).Distinct().ToList();
            if (f.Kind is "select" or "multiselect" && f.Options.Count == 0)
                return $"{target}: '{f.Label}' is a {f.Kind} and needs at least one option.";
            f.Position = i;
        }
        return null;
    }

    public static void MapCustomFieldsEndpoints(this IEndpointRouteBuilder app)
    {
        // GET the whole schema — forms + the settings tab read this.
        app.MapGet("/api/settings/custom-fields", (ScopingService sc) =>
            Results.Ok(Payload(Load(sc))))
            .RequireAuthorization(AuthPolicies.Authenticated);

        // PUT replaces the schema. Body: {customer:[...]} — an omitted key leaves
        // that target untouched (a legacy "provider" key is ignored).
        // Gate: the settings.custom_fields.manage capability (superadmin always).
        app.MapPut("/api/settings/custom-fields", async (HttpContext ctx, ScopingService sc,
            AppDbContext db, ClaimsPrincipal me) =>
        {
            if (await SettingsEndpoints.RequireCapability(db, me, "settings.custom_fields.manage") is { } denied)
                return denied;
            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(); }
            catch { return Results.Json(new { detail = "invalid body" }, statusCode: 400); }
            if (body.ValueKind != JsonValueKind.Object)
                return Results.Json(new { detail = "expected an object" }, statusCode: 400);

            var schema = Load(sc);
            foreach (var target in Targets)
            {
                if (!body.TryGetProperty(target, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
                List<FieldDef> fields;
                try { fields = JsonSerializer.Deserialize<List<FieldDef>>(arr.GetRawText()) ?? new(); }
                catch { return Results.Json(new { detail = $"{target}: malformed field list" }, statusCode: 400); }
                var err = Validate(fields, target);
                if (err != null) return Results.Json(new { detail = err }, statusCode: 400);
                schema.Customer = fields;
            }
            sc.SetSetting(Key, JsonSerializer.Serialize(schema));
            db.AuditLogs.Add(new ChatBranch.Domain.AuditLog
            {
                Actor = me.Identity?.Name ?? "system", Action = "settings.custom_fields",
                Target = Key, Detail = $"{schema.Customer.Count} customer field(s)", CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
            return Results.Ok(Payload(schema));
        }).RequireAuthorization(AuthPolicies.Authenticated);
    }
}
