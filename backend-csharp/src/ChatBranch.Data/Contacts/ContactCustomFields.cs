using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ChatBranch.Data.Flows;

namespace ChatBranch.Data.Contacts;

/// <summary>One contact custom-field DEFINITION (the <c>custom_fields</c> Setting row).</summary>
/// <remarks>
/// The write side is <c>CustomFieldsEndpoints</c>; everything that only needs the
/// shape (journeys, import, the AI prompt) reads it through
/// <see cref="ContactCustomFields"/> so there is one definition of a kind.
/// </remarks>
public sealed class ContactFieldDef
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "text";
    [JsonPropertyName("options")] public List<string> Options { get; set; } = new();
    [JsonPropertyName("required")] public bool Required { get; set; }
    [JsonPropertyName("position")] public int Position { get; set; }

    /// <summary>Opt this field in to the AI's prompts (default OFF) — the
    /// customer-facing CONTACT block, the journey "Let AI decide" prompt, and
    /// <c>ctx.contact.fields</c> in a code skill. Internal fields — credit risk, a
    /// churn score — stay out until someone says so; a skill that needs one reads
    /// <c>ctx.contact.internalFields</c> (<see cref="ContactCustomFields.SkillFields"/>).</summary>
    [JsonPropertyName("show_to_ai")] public bool ShowToAi { get; set; }

    /// <summary>The entity this field belongs to, like a registry tag's: null =
    /// organization-wide (every company), else only that entity's COMPANY sees it —
    /// on every AI-facing path (<see cref="ContactCustomFields.VisibleToCompany"/>).</summary>
    [JsonPropertyName("entity_id")] public int? EntityId { get; set; }
}

/// <summary>
/// Contact custom fields — the kinds, how a VALUE is stored, and what of it the
/// customer-facing AI may see.
///
/// <para><b>Kinds.</b> text · select · multiselect · number · boolean · date · json.
/// Values live in <c>Contact.Attributes</c> (a free-form JSON dictionary), so no kind
/// needs a schema change.</para>
///
/// <para><b>date</b> is stored in ONE canonical form: <c>YYYY-MM-DD</c>, a plain
/// calendar date with no time and no zone — what "service date" means to a person. A
/// write is canonicalised (<see cref="TryCanonicalDate"/>): <c>31/12/2026</c> is read
/// day-first (Malaysian/UK order, matching import and the dashboard) and a full
/// timestamp is reduced to its date — if it carries an offset or <c>Z</c> it is first
/// moved into the ORG time zone (Asia/Kuala_Lumpur here), so an instant late in the UTC
/// day doesn't land on the day before. Journey conditions (before · after ·
/// within_last_days · within_next_days) and the "Date field" trigger parse exactly this
/// form, and the dashboard renders it in the operator's locale.</para>
///
/// <para><b>json</b> is arbitrary structured data for API-driven syncs ("an outside
/// system pushes the service history / whatever else it holds"). The top level must be
/// an OBJECT: everything that consumes a field value — templates (<c>{{cf_key}}</c>),
/// journey conditions, the prompt — wants <c>name: value</c> pairs, a bare array has no
/// names to show and a bare scalar is just text/number. It is capped
/// (<see cref="JsonMaxChars"/> compact chars, <see cref="JsonMaxDepth"/> deep) because
/// it rides in a row other code reads on every message. It never reaches a message as
/// raw JSON: <see cref="Display"/> flattens it to <c>key: value; key: value</c>, so a
/// template renders something sensible (or nothing) and never a broken brace.</para>
///
/// <para><b>The AI.</b> Three paths can carry a field towards a model, and all three
/// come through here:
/// <list type="bullet">
/// <item>the customer-facing CONTACT block and the journey "Let AI decide" prompt —
/// <see cref="ContactFieldDef.ShowToAi"/> ON and the acting company's scope
/// (<see cref="VisibleToAi"/>), then as capped, guarded lines
/// (<see cref="PromptLines"/> · <see cref="PromptFields"/>);</item>
/// <item>a code skill's sandbox (<c>ctx.contact</c>) — <see cref="SkillFields"/>:
/// <c>fields</c> is the opted-in set, <c>internalFields</c> everything else this
/// company may see. Operator-authored code keeps its reach, but only inside its own
/// company.</item>
/// </list>
/// <see cref="ContactFieldDef.EntityId"/> is enforced on every one of them
/// (<see cref="VisibleToCompany"/>): this installation holds unrelated client
/// companies as top-level entities, so scoping a field is a privacy boundary.</para>
/// </summary>
public static class ContactCustomFields
{
    public const string SettingKey = "custom_fields";

    /// <summary>Every kind a definition may use (the API validates against this).</summary>
    public static readonly string[] Kinds =
        { "text", "select", "multiselect", "number", "boolean", "date", "json" };

    /// <summary>Kinds that need at least one option.</summary>
    public static readonly string[] OptionKinds = { "select", "multiselect" };

    public const string DateFormat = "yyyy-MM-dd";

    /// <summary>A json value's cap, in characters of compact JSON.</summary>
    public const int JsonMaxChars = 8000;
    /// <summary>A json value's nesting cap (a sync that nests deeper is a bug, not data).</summary>
    public const int JsonMaxDepth = 16;

    // ---- the prompt's budget ------------------------------------------------------
    /// <summary>Longest one field's value may be in the prompt (PromptGuard cap).</summary>
    public const int AiValueMax = 160;
    /// <summary>Longest a field LABEL may be in the prompt.</summary>
    public const int AiLabelMax = 40;
    /// <summary>Most fields the prompt carries, however many are opted in.</summary>
    public const int AiMaxFields = 10;
    /// <summary>Most characters the custom-field lines may add to the CONTACT block —
    /// it sits next to knowledge and memory (Trim(…, 1500)) and must never crowd them out.</summary>
    public const int AiBlockMax = 600;
    /// <summary>Entries of a json field the prompt shows.</summary>
    public const int AiJsonEntries = 8;

    private static readonly JsonDocumentOptions ReadOptions = new() { MaxDepth = JsonMaxDepth };
    private static readonly string[] DateFormats =
    {
        "yyyy-MM-dd", "yyyy/MM/dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "dd.MM.yyyy", "d.M.yyyy",
    };
    private static readonly Regex HasZone = new(@"(Z|[+\-]\d{2}:?\d{2})$", RegexOptions.Compiled);

    // ---- the definitions ----------------------------------------------------------

    /// <summary>The customer custom-field definitions, in order; an unreadable
    /// setting answers an empty list (custom fields never break a caller).</summary>
    public static List<ContactFieldDef> Customer(ScopingService scoping)
    {
        try { return Parse(scoping.SettingValue(SettingKey)); }
        catch { return new List<ContactFieldDef>(); }
    }

    /// <summary>The definitions in one stored <c>custom_fields</c> JSON document.</summary>
    public static List<ContactFieldDef> Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new List<ContactFieldDef>();
        var list = new List<ContactFieldDef>();
        using var doc = JsonDocument.Parse(raw, ReadOptions);
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("customer", out var arr)
            || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var f in arr.EnumerateArray())
        {
            if (f.ValueKind != JsonValueKind.Object) continue;
            var def = f.Deserialize<ContactFieldDef>();
            if (def == null || string.IsNullOrWhiteSpace(def.Key)) continue;
            if (def.Label.Length == 0) def.Label = def.Key;
            def.Kind = (def.Kind ?? "text").Trim().ToLowerInvariant();
            def.Options ??= new List<string>();
            list.Add(def);
        }
        return list.OrderBy(f => f.Position).ToList();
    }

    // ---- values: validate + canonicalise on write ---------------------------------

    /// <summary>
    /// Canonicalise the typed values of ONE contact write in place (and only the keys
    /// the write carries — <c>Attributes</c> is merged, not replaced). Returns the
    /// operator-facing reason to refuse (400), or null when every value is good.
    /// </summary>
    public static string? Apply(IReadOnlyList<ContactFieldDef> defs, TimeZoneInfo zone,
        Dictionary<string, object?>? values)
    {
        if (values == null || values.Count == 0 || defs.Count == 0) return null;
        foreach (var def in defs)
        {
            if (!values.TryGetValue(def.Key, out var raw)) continue;
            var label = def.Label.Length > 0 ? def.Label : def.Key;
            if (IsBlank(raw))
            {
                // clearing a field is always allowed — the same empty the forms send
                if (raw is JsonElement) values[def.Key] = null;
                continue;
            }
            switch (def.Kind)
            {
                case "date":
                    if (!TryCanonicalDate(raw, zone, out var iso))
                        return $"'{label}' is a date — use YYYY-MM-DD (got \"{Short(raw)}\").";
                    values[def.Key] = iso;
                    break;
                case "json":
                    if (TryCanonicalJson(raw, out var json, out var why)) values[def.Key] = json;
                    else return $"'{label}' is a JSON field — {why}";
                    break;
            }
        }
        return null;
    }

    /// <summary>The same, reading the definitions and the org time zone itself.</summary>
    public static string? Apply(ScopingService scoping, Dictionary<string, object?>? values)
    {
        List<ContactFieldDef> defs;
        try { defs = Customer(scoping); }
        catch { return null; } // a malformed schema can't refuse a write
        return Apply(defs, OrgClock.Zone(scoping), values);
    }

    /// <summary>A value nobody set: null, "" or JSON null.</summary>
    public static bool IsBlank(object? raw) => raw switch
    {
        null => true,
        string s => s.Trim().Length == 0,
        JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => true,
        JsonElement { ValueKind: JsonValueKind.String } el => (el.GetString() ?? "").Trim().Length == 0,
        _ => false,
    };

    /// <summary>
    /// One canonical <c>YYYY-MM-DD</c> out of whatever a form, a CSV or an API sync
    /// sent. A timestamp with an offset is moved into <paramref name="zone"/> (the org's)
    /// first; day-first is assumed for slashed/dashed dates. A number is NOT a date (a
    /// spreadsheet serial means nothing without its epoch).
    /// </summary>
    public static bool TryCanonicalDate(object? raw, TimeZoneInfo zone, out string iso)
    {
        iso = "";
        var s = (raw is JsonElement el
            ? el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : el.ToString()
            : raw?.ToString() ?? "").Trim();
        if (raw is DateTime dtv) { iso = DateOnly.FromDateTime(dtv).ToString(DateFormat, CultureInfo.InvariantCulture); return true; }
        if (raw is DateOnly dov) { iso = dov.ToString(DateFormat, CultureInfo.InvariantCulture); return true; }
        // a bare number is NOT a date: a spreadsheet serial (45000) means nothing
        // without its epoch, and guessing one would quietly store the wrong day
        if (s.Length == 0 || raw is JsonElement { ValueKind: JsonValueKind.Number } or int or long or double or decimal
            || s.All(char.IsDigit))
            return false;
        if (DateOnly.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            iso = d.ToString(DateFormat, CultureInfo.InvariantCulture);
            return true;
        }
        // a full timestamp: with a zone it is an instant (shown on the org's calendar),
        // without one it is already a local wall time — take its date either way
        if (HasZone.IsMatch(s) && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var dto))
        {
            iso = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(dto, zone).DateTime)
                .ToString(DateFormat, CultureInfo.InvariantCulture);
            return true;
        }
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            iso = DateOnly.FromDateTime(dt).ToString(DateFormat, CultureInfo.InvariantCulture);
            return true;
        }
        return false;
    }

    /// <summary>
    /// A json field's value as a stored JSON OBJECT: an object as sent, or a string
    /// holding one (a textarea, a CSV cell). <paramref name="why"/> is the
    /// operator-facing reason when it is refused.
    /// </summary>
    public static bool TryCanonicalJson(object? raw, out JsonElement value, out string why)
    {
        value = default;
        why = "";
        string text;
        switch (raw)
        {
            case JsonElement { ValueKind: JsonValueKind.Object } el:
                text = el.GetRawText();
                break;
            case JsonElement { ValueKind: JsonValueKind.String } s:
                text = (s.GetString() ?? "").Trim();
                break;
            case JsonElement other:
                why = $"the top level must be a JSON object {{…}}, not {Word(other.ValueKind)}.";
                return false;
            case string str:
                text = str.Trim();
                break;
            case IDictionary<string, object?> dict:
                try { text = JsonSerializer.Serialize(dict); }
                catch { why = "it could not be read as JSON."; return false; }
                break;
            default:
                why = "the top level must be a JSON object {…}.";
                return false;
        }
        if (text.Length > JsonMaxChars)
        {
            why = $"it is {text.Length} characters — the limit is {JsonMaxChars}.";
            return false;
        }
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text, ReadOptions); }
        catch (JsonException e)
        {
            why = $"it is not valid JSON ({e.Message.Split(" Path:")[0].Trim()}).";
            return false;
        }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                why = $"the top level must be a JSON object {{…}}, not {Word(doc.RootElement.ValueKind)}.";
                return false;
            }
            // compact form, so the cap means the same for pretty and minified input
            var compact = JsonSerializer.Serialize(doc.RootElement);
            if (compact.Length > JsonMaxChars)
            {
                why = $"it is {compact.Length} characters — the limit is {JsonMaxChars}.";
                return false;
            }
            value = doc.RootElement.Clone();
            return true;
        }
    }

    private static string Word(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Array => "a list",
        JsonValueKind.String => "text",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a yes/no",
        _ => "that",
    };

    private static string Short(object? raw)
    {
        var s = (raw is JsonElement el
            ? el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : el.ToString()
            : raw?.ToString() ?? "").Trim();
        return s.Length <= 40 ? s : s[..40] + "…";
    }

    // ---- values: how one reads ----------------------------------------------------

    /// <summary>
    /// One stored attribute as text, the way the dashboard shows it: booleans Yes/No,
    /// multi-select a comma list, a date its canonical ISO day (unambiguous — the
    /// operator's locale is a UI concern), a json object FLATTENED to
    /// <c>key: value; key: value</c> and never raw JSON.
    /// </summary>
    public static string Display(Dictionary<string, object?> attributes, string key, string kind,
        int jsonEntries = 12, int jsonValue = 120)
    {
        if (!attributes.TryGetValue(key, out var raw) || raw == null) return "";
        if (kind == "json") return FlattenJson(raw, jsonEntries, jsonValue);
        if (raw is JsonElement el)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Null or JsonValueKind.Undefined: return "";
                case JsonValueKind.True: return "Yes";
                case JsonValueKind.False: return "No";
                case JsonValueKind.Array:
                    return string.Join(", ", el.EnumerateArray()
                        .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString())
                        .Where(x => x.Length > 0));
                case JsonValueKind.String: return el.GetString() ?? "";
                case JsonValueKind.Object: return FlattenJson(el, jsonEntries, jsonValue);
                default: return el.ToString();
            }
        }
        if (raw is bool b) return b ? "Yes" : "No";
        if (raw is System.Collections.IEnumerable seq and not string)
            return string.Join(", ", seq.Cast<object?>().Select(x => x?.ToString() ?? "").Where(x => x.Length > 0));
        if (kind == "boolean")
            return string.Equals(raw.ToString(), "true", StringComparison.OrdinalIgnoreCase) ? "Yes" : "No";
        return raw.ToString() ?? "";
    }

    /// <summary>
    /// A json value as bounded <c>key: value</c> pairs — flattened (<c>plan.tier</c>),
    /// at most <paramref name="maxEntries"/> of them, each value capped at
    /// <paramref name="maxValue"/>, "…" when there was more. Never braces, never a
    /// newline: it goes into messages and into one prompt line.
    /// </summary>
    public static string FlattenJson(object? raw, int maxEntries = 12, int maxValue = 120)
    {
        JsonElement root;
        if (raw is JsonElement el) root = el;
        else if (raw is string s && s.Trim().StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(s, ReadOptions);
                root = doc.RootElement.Clone();
            }
            catch { return ""; }
        }
        else if (raw is IDictionary<string, object?> dict)
        {
            try
            {
                using var doc = JsonDocument.Parse(JsonSerializer.Serialize(dict), ReadOptions);
                root = doc.RootElement.Clone();
            }
            catch { return ""; }
        }
        else return "";
        if (root.ValueKind != JsonValueKind.Object) return "";
        var parts = new List<string>();
        var more = Walk(root, "", parts, maxEntries, maxValue, 0);
        if (parts.Count == 0) return "";
        return string.Join("; ", parts) + (more ? "; …" : "");
    }

    /// <summary>Flatten one level; true when something was left out.</summary>
    private static bool Walk(JsonElement el, string prefix, List<string> parts, int maxEntries, int maxValue, int depth)
    {
        foreach (var p in el.EnumerateObject())
        {
            if (parts.Count >= maxEntries) return true;
            var name = prefix.Length == 0 ? p.Name : prefix + "." + p.Name;
            switch (p.Value.ValueKind)
            {
                case JsonValueKind.Object:
                    if (depth >= 2) { parts.Add($"{name}: {Cap(Scalars(p.Value), maxValue)}"); break; }
                    if (Walk(p.Value, name, parts, maxEntries, maxValue, depth + 1)) return true;
                    break;
                case JsonValueKind.Array:
                    parts.Add($"{name}: {Cap(Scalars(p.Value), maxValue)}");
                    break;
                case JsonValueKind.Null or JsonValueKind.Undefined:
                    break;
                default:
                    parts.Add($"{name}: {Cap(Text(p.Value), maxValue)}");
                    break;
            }
        }
        return false;
    }

    /// <summary>A nested object / list as one short value ("a, b" · "2026-01-01, …").</summary>
    private static string Scalars(JsonElement el)
    {
        var items = el.ValueKind == JsonValueKind.Array
            ? el.EnumerateArray().Select(Text)
            : el.EnumerateObject().Select(p => $"{p.Name}: {Text(p.Value)}");
        return string.Join(", ", items.Where(x => x.Length > 0).Take(8));
    }

    private static string Text(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString() ?? "",
        JsonValueKind.True => "Yes",
        JsonValueKind.False => "No",
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        JsonValueKind.Object or JsonValueKind.Array => Scalars(el),
        _ => el.ToString(),
    };

    private static string Cap(string s, int max)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Replace("{", "(").Replace("}", ")").Trim();
        return s.Length <= max ? s : s[..max].TrimEnd() + "…";
    }

    // ---- what the AI may see ------------------------------------------------------

    /// <summary>
    /// The fields the ACTING company may see at all, in order: an organization-wide
    /// field (no entity) always — like an organization-wide tag — and a field that
    /// belongs to an entity only when that entity's COMPANY is the one acting. This
    /// install hosts unrelated client companies in one organization, so
    /// <c>entity_id</c> is a PRIVACY boundary, not a tidiness label: it is the one
    /// place that rule lives, and every AI-facing path goes through it (the
    /// customer-facing prompt, the "Let AI decide" prompt and a code skill's
    /// sandbox). A field with no entity is shared by design.
    /// </summary>
    public static List<ContactFieldDef> VisibleToCompany(ScopingService scoping,
        IEnumerable<ContactFieldDef> defs, int? company) =>
        defs.Where(f => f.EntityId == null || (company != null && scoping.CompanyOf(f.EntityId) == company))
            .OrderBy(f => f.Position).ToList();

    /// <summary>
    /// The opted-in fields the ANSWERING company may see: <see cref="VisibleToCompany"/>
    /// plus the <c>show_to_ai</c> gate (default OFF), so an internal field never
    /// reaches a model's prompt by surprise. Used by the customer-facing CONTACT
    /// block and by the "Let AI decide" routing prompt — both are model prompts.
    /// </summary>
    public static List<ContactFieldDef> VisibleToAi(ScopingService scoping,
        IEnumerable<ContactFieldDef> defs, int? company) =>
        VisibleToCompany(scoping, defs.Where(f => f.ShowToAi), company);

    /// <summary>
    /// The label → value pairs a MODEL PROMPT may carry (the "Let AI decide" step's
    /// CONTACT lines), rendered the way the dashboard shows them
    /// (<see cref="Display"/>) and skipping a field with no value. Same two gates as
    /// the customer-facing block — <c>show_to_ai</c> ON and the acting company's
    /// scope — read through <see cref="VisibleToAi"/>. The caller owns the clipping
    /// and fencing of each value. Exception-safe: a malformed schema answers an
    /// empty map, so a flow decision never fails over a field definition.
    /// </summary>
    public static Dictionary<string, string> PromptFields(ScopingService scoping,
        Dictionary<string, object?>? attributes, int? company)
    {
        var fields = new Dictionary<string, string>();
        if (attributes == null || attributes.Count == 0) return fields;
        try
        {
            foreach (var f in VisibleToAi(scoping, Customer(scoping), company))
            {
                var value = Display(attributes, f.Key, f.Kind).Trim();
                if (value.Length == 0) continue;
                fields[f.Label.Length > 0 ? f.Label : f.Key] = value;
            }
        }
        catch { return new Dictionary<string, string>(); }
        return fields;
    }

    /// <summary>
    /// A contact's stored attributes as a CODE SKILL's sandbox may see them, split in
    /// two. Both halves are company-scoped (<see cref="VisibleToCompany"/>): a field
    /// that belongs to ANOTHER company's entity is in neither, because
    /// <c>entity_id</c> is a privacy boundary and operator-authored code is no
    /// exception.
    /// <list type="bullet">
    /// <item><c>Shown</c> (<c>ctx.contact.fields</c>) — only the fields whose
    /// <c>show_to_ai</c> is ON, so the common "dump what we know about this contact
    /// into a tool result" skill cannot push an internal field at the model;</item>
    /// <item><c>Internal</c> (<c>ctx.contact.internalFields</c>) — everything this
    /// company may see: those same values, the fields that are NOT opted in, and
    /// attributes with no definition at all (<c>preferred_channel</c> and the like).
    /// A skill that legitimately reads an internal field keeps doing so under a key
    /// that says what it is.</item>
    /// </list>
    /// Values are the RAW stored ones, exactly as before — skills compare them, so
    /// nothing is rendered or capped here. Exception-safe AND fail-closed: an
    /// unreadable schema answers two EMPTY maps (never the raw attributes — it cannot
    /// be known which of them another company owns) rather than failing the skill run.
    /// No definitions at all is a different thing: then nothing is scoped and nothing is
    /// opted in, so <c>Internal</c> carries the attributes and <c>Shown</c> is empty.
    /// </summary>
    public static (Dictionary<string, object?> Shown, Dictionary<string, object?> Internal) SkillFields(
        ScopingService scoping, Dictionary<string, object?>? attributes, int? company)
    {
        var shown = new Dictionary<string, object?>();
        var internals = new Dictionary<string, object?>();
        if (attributes == null || attributes.Count == 0) return (shown, internals);
        try
        {
            // Parse, not Customer: an UNREADABLE schema must throw into the catch below
            // and cost both maps. Customer() answers an empty list, which here would read
            // as "nothing is scoped" and hand another company's field to internalFields.
            var defs = Parse(scoping.SettingValue(SettingKey));
            // keys that belong to another company: every scoped key, less the ones
            // this company may see (a key defined twice — scoped and shared — is shared)
            var blocked = defs.Where(f => f.EntityId != null).Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var f in VisibleToCompany(scoping, defs, company)) blocked.Remove(f.Key);
            var opted = VisibleToAi(scoping, defs, company).Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var (key, value) in attributes)
            {
                if (blocked.Contains(key)) continue;
                internals[key] = value;
                if (opted.Contains(key)) shown[key] = value;
            }
        }
        catch { return (new Dictionary<string, object?>(), new Dictionary<string, object?>()); }
        return (shown, internals);
    }

    /// <summary>
    /// The CONTACT block's custom-field lines ("Service date: 2026-04-02"), bounded
    /// three ways — <see cref="AiMaxFields"/> fields, <see cref="AiValueMax"/> per
    /// value, <see cref="AiBlockMax"/> for the lot — and each value guarded
    /// (<paramref name="guard"/> = <c>PromptGuard.Inline</c>; the caller owns it, this
    /// assembly's AI namespace is not a dependency of Contacts). A field with no value
    /// is left out entirely, so the prompt never carries empty labels.
    /// </summary>
    public static List<string> PromptLines(IReadOnlyList<ContactFieldDef> visible,
        Dictionary<string, object?> attributes, Func<string, int, string> guard)
    {
        var lines = new List<string>();
        var budget = AiBlockMax;
        foreach (var f in visible)
        {
            if (lines.Count >= AiMaxFields) break;
            var text = Display(attributes, f.Key, f.Kind, AiJsonEntries, AiValueMax);
            if (text.Trim().Length == 0) continue;
            var line = guard(f.Label.Length > 0 ? f.Label : f.Key, AiLabelMax)
                       + ": " + guard(text, AiValueMax);
            if (line.Length > budget) break; // whole lines only — never half a value
            budget -= line.Length;
            lines.Add(line);
        }
        return lines;
    }

    // ---- import -------------------------------------------------------------------

    /// <summary>
    /// One imported CELL as the stored value of a field of this kind, or null with a
    /// <paramref name="problem"/> to warn about (import warns and skips a bad cell —
    /// it never fails the row, the way an unparseable number is kept as text today).
    /// </summary>
    public static object? FromCell(string raw, string kind, TimeZoneInfo zone, out string? problem)
    {
        problem = null;
        switch (kind)
        {
            case "date":
                if (TryCanonicalDate(raw, zone, out var iso)) return iso;
                problem = "date";
                return null;
            case "json":
                if (TryCanonicalJson(raw, out var json, out var why)) return json;
                problem = why;
                return null;
            default:
                return raw;
        }
    }

    /// <summary>The one-line hint the settings UI and the MCP tool share.</summary>
    public static string KindList => string.Join(" | ", Kinds);
}
