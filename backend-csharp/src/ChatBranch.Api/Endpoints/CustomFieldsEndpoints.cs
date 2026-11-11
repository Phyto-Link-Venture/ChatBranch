using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Contacts;
using FieldDef = ChatBranch.Data.Contacts.ContactFieldDef;

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
/// Field kinds: text · select (one option) · multiselect · number · boolean ·
/// date (canonical YYYY-MM-DD) · json (an object, for API-driven syncs) — see
/// <see cref="ContactCustomFields"/>, which owns the kinds, the value rules and
/// what the AI may see. Each field also carries <c>show_to_ai</c> (off by default:
/// an internal field never reaches a customer-facing prompt) and an optional
/// <c>entity_id</c> (null = organization-wide, else only that entity's company
/// sees it — like a registry tag's scope).
/// Reads are open to any authed role (forms render them); writes are admin+.
/// </summary>
public static class CustomFieldsEndpoints
{
    private const string Key = "custom_fields";
    private static readonly string[] Targets = { "customer" };
    private static readonly string[] Kinds = ContactCustomFields.Kinds;

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

    /// <summary>The contact custom-field definitions, in order (the AI playground's made-up profile form).</summary>
    internal static List<FieldDef> CustomerFields(ScopingService sc) =>
        Load(sc).Customer.OrderBy(f => f.Position).ToList();

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

    private static string? Validate(List<FieldDef> fields, string target, Func<int, bool> entityExists)
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
            if (ContactCustomFields.OptionKinds.Contains(f.Kind) && f.Options.Count == 0)
                return $"{target}: '{f.Label}' is a {f.Kind} and needs at least one option.";
            if (!ContactCustomFields.OptionKinds.Contains(f.Kind)) f.Options = new();
            // a field may belong to one entity (only its company's AI sees it) — but a real one
            if (f.EntityId is int eid && !entityExists(eid))
                return $"{target}: '{f.Label}' points at entity {eid}, which doesn't exist.";
            f.Position = i;
        }
        return null;
    }

    public static void MapCustomFieldsEndpoints(this IEndpointRouteBuilder app)
    {
        // GET the whole schema — forms + the settings tab read this: the contact
        // pages, the template/flow variable pickers and the settings page.
        app.MapGet("/api/settings/custom-fields", (ScopingService sc) =>
            Results.Ok(Payload(Load(sc))))
            .RequireAuthorization(AuthPolicies.Authenticated)
            .RequireAnyCapability("contacts.view", "flows.manage", "templates.manage",
                "script_templates.manage", "settings.custom_fields.manage");

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
                var err = Validate(fields, target, id => db.Entities.Any(e => e.Id == id));
                if (err != null) return Results.Json(new { detail = err }, statusCode: 400);
                schema.Customer = fields;
            }
            sc.SetSetting(Key, JsonSerializer.Serialize(schema));
            db.AuditLogs.Add(new ChatBranch.Domain.AuditLog
            {
                Actor = me.Identity?.Name ?? "system", Action = "settings.custom_fields",
                Target = Key, Detail = $"{schema.Customer.Count} customer field(s), "
                          + $"{schema.Customer.Count(f => f.ShowToAi)} shown to the AI", CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
            return Results.Ok(Payload(schema));
        }).RequireAuthorization(AuthPolicies.Authenticated);
    }
}
