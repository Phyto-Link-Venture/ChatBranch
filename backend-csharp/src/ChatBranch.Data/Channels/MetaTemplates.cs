using System.Collections;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChatBranch.Domain;

namespace ChatBranch.Data.Channels;

public sealed record WaTemplateButton(string Type, string Text, string Url = "", string PhoneNumber = "");

/// <summary>
/// One WhatsApp message template, wherever it came from: <c>demo</c> (the three
/// sandbox stand-ins), <c>custom</c> (operator-made, Setting
/// <c>whatsapp_custom_templates</c>) or <c>live</c> (the WABA's registry at Meta).
/// </summary>
public sealed record WaTemplateDef(
    string Name, string Language, string Category, string Status, string Body,
    List<WaTemplateButton> Buttons, string Source,
    string Id = "", string HeaderFormat = "", string HeaderText = "", string Footer = "");

/// <summary>
/// The values a template send fills in. <see cref="Body"/> is positional for the
/// body's <c>{{1}}…{{n}}</c> (or, for a NAMED template, its distinct
/// <c>{{name}}</c>s in order of appearance). A TEXT header and dynamic URL
/// buttons take their own lists when given, else fall back to the body values
/// with the same placeholder number. A media header needs <see cref="HeaderMediaUrl"/>.
/// </summary>
public sealed record TemplateArgs(List<string> Body, List<string>? Header = null, List<string>? Buttons = null,
    string? HeaderMediaUrl = null)
{
    public static TemplateArgs Of(IEnumerable<string>? body) => new((body ?? Array.Empty<string>()).ToList());

    /// <summary>
    /// A queued template job's payload. Flows write <c>parameters</c> (a list, plus a
    /// joined <c>variables</c> string for display); older writers put a list in
    /// <c>variables</c>; recovery/follow-up jobs carry none.
    /// </summary>
    public static TemplateArgs FromPayload(IDictionary<string, object?> payload)
    {
        var body = List(payload, "parameters");
        if (body == null)
        {
            body = List(payload, "variables");
            if (body == null && payload.TryGetValue("variables", out var raw) && Scalar(raw) is { Length: > 0 } s)
                body = s.Split(", ").ToList();
        }
        return new TemplateArgs(body ?? new List<string>(),
            List(payload, "header_parameters"), List(payload, "button_parameters"),
            payload.TryGetValue("header_media_url", out var hm) ? Scalar(hm) : null);
    }

    private static string? Scalar(object? v) => v switch
    {
        null => null,
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
        JsonElement { ValueKind: JsonValueKind.Number } e => e.ToString(),
        JsonElement => null,
        _ => v.ToString(),
    };

    private static List<string>? List(IDictionary<string, object?> payload, string key)
    {
        if (!payload.TryGetValue(key, out var v) || v == null) return null;
        if (v is JsonElement e)
            return e.ValueKind == JsonValueKind.Array
                ? e.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString()).ToList()
                : null;
        if (v is string) return null;
        if (v is IEnumerable seq) return seq.Cast<object?>().Select(x => x?.ToString() ?? "").ToList();
        return null;
    }
}

/// <summary>
/// The template registry every send path reads (port of <c>wa_config</c>'s
/// template helpers + <c>client.send_template</c>'s component building):
///
///   * sandbox / Baileys / no WABA → the 3 demo templates + operator custom ones;
///   * a configured Cloud account with a <c>waba_id</c> → Meta's live registry
///     (GET /{waba_id}/message_templates, paged; cached 5 minutes per account,
///     invalidated by every create/edit/delete) merged with the custom ones.
///
/// <see cref="BuildMessage"/> turns a definition + values into the Graph
/// <c>template</c> object (header / body / dynamic-URL-button parameters) and
/// <see cref="Render"/> into the text the inbox stores for the bubble.
/// </summary>
public class WhatsAppTemplateCatalog
{
    public const string CustomTemplatesKey = "whatsapp_custom_templates";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private static readonly Regex Placeholder = new(@"\{\{\s*([A-Za-z0-9_]+)\s*\}\}", RegexOptions.Compiled);

    /// <summary>Sandbox template set — neutral stand-ins so the whole template flow works with zero Meta creds.</summary>
    public static readonly IReadOnlyList<WaTemplateDef> Demo = new[]
    {
        new WaTemplateDef("order_confirmation", "en", "UTILITY", "APPROVED",
            "Hi {{1}}, thanks for your order {{2}}. We'll have it ready by {{3}}. " +
            "Reply here if you have any questions.", new(), "demo"),
        new WaTemplateDef("delivery_update", "en", "UTILITY", "APPROVED",
            "Hi {{1}}, an update on {{2}}: it's on the way and should arrive on {{3}}. " +
            "Reply here if anything needs to change.", new(), "demo"),
        new WaTemplateDef("appointment_reminder", "en", "UTILITY", "APPROVED",
            "Hi {{1}}, a reminder about {{2}} on {{3}}. Reply here to reschedule.", new(), "demo"),
    };

    /// <summary>Keyed by account id + WABA + a token fingerprint, so a re-pointed or
    /// re-tokened account never reads another registry's list.</summary>
    private static readonly ConcurrentDictionary<string, (DateTime At, List<WaTemplateDef> Items)> Cache = new();

    private static string CacheKey(ChannelAccount a) =>
        $"{a.Id}|{a.WabaId}|{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(a.AccessToken ?? "")))[..16]}";

    private readonly ScopingService _sc;
    private readonly IWhatsAppCloudClient _wa;

    public WhatsAppTemplateCatalog(ScopingService sc, IWhatsAppCloudClient wa)
    {
        _sc = sc;
        _wa = wa;
    }

    /// <summary>Forget an account's cached registry (after a create/edit/delete).</summary>
    public static void Invalidate(int accountId)
    {
        foreach (var key in Cache.Keys.Where(k => k.StartsWith($"{accountId}|", StringComparison.Ordinal)))
            Cache.TryRemove(key, out _);
    }

    /// <summary>True when this account talks to Meta's template registry.</summary>
    public bool IsLive(ChannelAccount? account) =>
        account != null && account.Channel == ChatBranch.Domain.Channels.WhatsApp
        && account.Provider != Providers.Baileys && _wa.IsConfigured(account) && account.WabaId.Length > 0;

    /// <summary>The operator-made templates (one Setting row, so the flow works offline).</summary>
    public List<WaTemplateDef> Custom()
    {
        var outv = new List<WaTemplateDef>();
        var raw = _sc.SettingValue(CustomTemplatesKey);
        if (string.IsNullOrWhiteSpace(raw)) return outv;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return outv;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var buttons = new List<WaTemplateButton>();
                if (el.TryGetProperty("buttons", out var b) && b.ValueKind == JsonValueKind.Array)
                    foreach (var one in b.EnumerateArray())
                        buttons.Add(new WaTemplateButton(MetaGraph.Str(one, "type"), MetaGraph.Str(one, "text"),
                            MetaGraph.Str(one, "url"), MetaGraph.Str(one, "phone_number")));
                outv.Add(new WaTemplateDef(MetaGraph.Str(el, "name"),
                    Or(MetaGraph.Str(el, "language"), "en"), Or(MetaGraph.Str(el, "category"), "UTILITY"),
                    Or(MetaGraph.Str(el, "status"), "APPROVED"), MetaGraph.Str(el, "body"), buttons, "custom"));
            }
        }
        catch { /* a malformed row degrades to "no custom templates" */ }
        return outv;
    }

    /// <summary>Parse Meta's message_templates <c>data</c> array.</summary>
    public static List<WaTemplateDef> ParseLive(JsonElement data)
    {
        var outv = new List<WaTemplateDef>();
        if (data.ValueKind != JsonValueKind.Array) return outv;
        foreach (var t in data.EnumerateArray())
        {
            if (t.ValueKind != JsonValueKind.Object) continue;
            string body = "", headerFormat = "", headerText = "", footer = "";
            var buttons = new List<WaTemplateButton>();
            if (t.TryGetProperty("components", out var comps) && comps.ValueKind == JsonValueKind.Array)
                foreach (var c in comps.EnumerateArray())
                {
                    switch (MetaGraph.Str(c, "type").ToUpperInvariant())
                    {
                        case "BODY": body = MetaGraph.Str(c, "text"); break;
                        case "FOOTER": footer = MetaGraph.Str(c, "text"); break;
                        case "HEADER":
                            headerFormat = MetaGraph.Str(c, "format").ToUpperInvariant();
                            headerText = MetaGraph.Str(c, "text");
                            break;
                        case "BUTTONS":
                            if (c.TryGetProperty("buttons", out var bs) && bs.ValueKind == JsonValueKind.Array)
                                foreach (var b in bs.EnumerateArray())
                                    buttons.Add(new WaTemplateButton(MetaGraph.Str(b, "type").ToUpperInvariant(),
                                        MetaGraph.Str(b, "text"), MetaGraph.Str(b, "url"), MetaGraph.Str(b, "phone_number")));
                            break;
                    }
                }
            var name = MetaGraph.Str(t, "name");
            if (name.Length == 0) continue;
            outv.Add(new WaTemplateDef(name, Or(MetaGraph.Str(t, "language"), "en"),
                MetaGraph.Str(t, "category").ToUpperInvariant(), MetaGraph.Str(t, "status").ToUpperInvariant(),
                body, buttons, "live", MetaGraph.Str(t, "id"), headerFormat, headerText, footer));
        }
        return outv;
    }

    /// <summary>The WABA's live registry (cached). Error = why the listing failed.</summary>
    public async Task<(List<WaTemplateDef> Templates, string? Error)> LiveAsync(ChannelAccount account,
        bool fresh = false, CancellationToken ct = default)
    {
        if (!fresh && Cache.TryGetValue(CacheKey(account), out var hit) && DateTime.UtcNow - hit.At < CacheTtl)
            return (hit.Items, null);
        var r = await _wa.ListTemplatesAsync(account, ct);
        if (!r.Ok) return (new List<WaTemplateDef>(), r.Detail.Length > 0 ? r.Detail : "Meta template listing failed");
        var items = ParseLive(r.Body);
        Cache[CacheKey(account)] = (DateTime.UtcNow, items);
        return (items, null);
    }

    /// <summary>
    /// Every template the picker offers for this account: live (+ custom) when the
    /// account has a WABA, demo + custom otherwise. Live names win a collision.
    /// </summary>
    public async Task<(List<WaTemplateDef> Templates, bool Live, string? Error)> ListAsync(ChannelAccount? account,
        bool fresh = false, CancellationToken ct = default)
    {
        var custom = Custom();
        if (!IsLive(account))
            return (Demo.Concat(custom.Where(c => Demo.All(d => d.Name != c.Name))).ToList(), false, null);
        var (live, error) = await LiveAsync(account!, fresh, ct);
        var merged = live.ToList();
        merged.AddRange(custom.Where(c => live.All(l => l.Name != c.Name)));
        return (merged, true, error);
    }

    /// <summary>Resolve one template by name (+ language when several exist).</summary>
    public async Task<WaTemplateDef?> FindAsync(ChannelAccount? account, string name, string? language,
        CancellationToken ct = default)
    {
        var (all, _, _) = await ListAsync(account, ct: ct);
        var named = all.Where(t => t.Name == name).ToList();
        if (named.Count == 0) return null;
        return named.FirstOrDefault(t => string.Equals(t.Language, language, StringComparison.OrdinalIgnoreCase))
               ?? named[0];
    }

    // ---- placeholders ---------------------------------------------------------

    /// <summary>The distinct placeholders in a text, in order: numbers ("1","2") or names.</summary>
    public static List<string> Placeholders(string? text) =>
        Placeholder.Matches(text ?? "").Select(m => m.Groups[1].Value).Distinct().ToList();

    private static bool Numeric(string p) => p.All(char.IsAsciiDigit);

    /// <summary>How many values a body takes: the highest {{n}}, or the number of distinct names.</summary>
    public static int VariableCount(string? body)
    {
        var ph = Placeholders(body);
        if (ph.Count == 0) return 0;
        return ph.All(Numeric) ? ph.Select(int.Parse).Max() : ph.Count;
    }

    /// <summary>Value for a placeholder: {{n}} → values[n-1]; a name → its position among the names.</summary>
    private static string? ValueFor(string placeholder, List<string> names, IList<string> values)
    {
        int i = Numeric(placeholder) ? int.Parse(placeholder) - 1 : names.IndexOf(placeholder);
        return i >= 0 && i < values.Count ? values[i] : null;
    }

    /// <summary>Fill placeholders; a missing value leaves the placeholder visible.</summary>
    public static string Fill(string text, IList<string> values)
    {
        var names = Placeholders(text).Where(p => !Numeric(p)).ToList();
        return Placeholder.Replace(text ?? "", m => ValueFor(m.Groups[1].Value, names, values) ?? m.Value);
    }

    /// <summary>The text the inbox stores for a template bubble (header line + body).</summary>
    public static string Render(WaTemplateDef? def, string name, TemplateArgs args)
    {
        if (def == null || def.Body.Length == 0)
            return $"[template: {name}]" + (args.Body.Count > 0 ? $" — {string.Join(", ", args.Body)}" : "");
        var body = Fill(def.Body, args.Body);
        if (def.HeaderFormat == "TEXT" && def.HeaderText.Length > 0)
            return $"{Fill(def.HeaderText, args.Header ?? args.Body)}\n{body}";
        return body;
    }

    /// <summary>
    /// The Graph <c>template</c> object: name, language and the components that
    /// carry parameters. An unknown definition (registry unreachable) sends every
    /// value as a body parameter and lets Meta validate.
    /// </summary>
    public static Dictionary<string, object?> BuildMessage(WaTemplateDef? def, string name, string? language,
        TemplateArgs args)
    {
        var components = new List<object>();
        var lang = !string.IsNullOrWhiteSpace(language) ? language! : def?.Language is { Length: > 0 } l ? l : "en";

        if (def == null)
        {
            if (args.Body.Count > 0)
                components.Add(new { type = "body", parameters = args.Body.Select(Text).ToList() });
        }
        else
        {
            // header: TEXT with a placeholder, or a media header fed by a link
            if (def.HeaderFormat == "TEXT" && Placeholders(def.HeaderText).Count > 0)
            {
                var hv = args.Header ?? args.Body;
                var ph = Placeholders(def.HeaderText);
                var names = ph.Where(p => !Numeric(p)).ToList();
                components.Add(new
                {
                    type = "header",
                    parameters = ph.Select(p => Param(p, ValueFor(p, names, hv) ?? "")).ToList(),
                });
            }
            else if (def.HeaderFormat is "IMAGE" or "VIDEO" or "DOCUMENT"
                     && !string.IsNullOrWhiteSpace(args.HeaderMediaUrl))
            {
                var kind = def.HeaderFormat.ToLowerInvariant();
                components.Add(new
                {
                    type = "header",
                    parameters = new[]
                    {
                        new Dictionary<string, object?> { ["type"] = kind, [kind] = new { link = args.HeaderMediaUrl } },
                    },
                });
            }

            var bodyPh = Placeholders(def.Body);
            if (bodyPh.Count > 0)
            {
                List<object> parameters;
                if (bodyPh.All(Numeric))
                {
                    var count = bodyPh.Select(int.Parse).Max();
                    parameters = Enumerable.Range(0, count)
                        .Select(i => Text(i < args.Body.Count ? args.Body[i] : "")).ToList();
                }
                else
                {
                    parameters = bodyPh.Select((p, i) => Param(p, i < args.Body.Count ? args.Body[i] : "")).ToList();
                }
                components.Add(new { type = "body", parameters });
            }

            // dynamic URL buttons ({{1}} suffix): their own list, else the body value of that number
            var urlIndex = 0;
            for (var i = 0; i < def.Buttons.Count; i++)
            {
                var b = def.Buttons[i];
                if (b.Type != "URL" || Placeholders(b.Url).Count == 0) continue;
                var p = Placeholders(b.Url)[0];
                var value = args.Buttons != null && urlIndex < args.Buttons.Count
                    ? args.Buttons[urlIndex]
                    : ValueFor(p, new List<string>(), args.Body) ?? "";
                urlIndex++;
                components.Add(new
                {
                    type = "button",
                    sub_type = "url",
                    index = i.ToString(),
                    parameters = new[] { Text(value) },
                });
            }
        }

        var template = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["language"] = new { code = lang },
        };
        if (components.Count > 0) template["components"] = components;
        return template;
    }

    private static object Text(string value) => new { type = "text", text = value };

    private static object Param(string placeholder, string value) => Numeric(placeholder)
        ? Text(value)
        : new { type = "text", parameter_name = placeholder, text = value };

    private static string Or(string v, string fallback) => v.Length > 0 ? v : fallback;
}
