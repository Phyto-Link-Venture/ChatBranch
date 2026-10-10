using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChatBranch.Data.Stores;
using ChatBranch.Data.Tags;
using ChatBranch.Domain;
using ExcelDataReader;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Data.Contacts;

/// <summary>A parsed contact book: column headers + one dictionary per row.</summary>
public sealed class ImportTable
{
    public string Source { get; set; } = "csv";
    public List<string> Headers { get; } = new();
    public List<Dictionary<string, string>> Rows { get; } = new();
    /// <summary>The row number each row had in the source (header = row 1; a vCard's number).</summary>
    public List<int> RowNumbers { get; } = new();
    /// <summary>Rows beyond the cap that were dropped.</summary>
    public int Truncated { get; set; }

    public void Add(Dictionary<string, string> row, int number)
    {
        if (Rows.Count >= ContactImporter.MaxRows) { Truncated++; return; }
        foreach (var k in row.Keys) if (!Headers.Contains(k)) Headers.Add(k);
        Rows.Add(row);
        RowNumbers.Add(number);
    }
}

/// <summary>Turns uploads into an <see cref="ImportTable"/>: CSV, .xlsx (first sheet), .vcf, Google people.</summary>
public static class ImportReaders
{
    // the column names a vCard / Google contact book is presented under
    public const string ColName = "Name", ColFirst = "First name", ColLast = "Last name", ColPhone = "Phone",
        ColOtherPhones = "Other phones", ColEmail = "Email", ColOtherEmails = "Other emails",
        ColCompany = "Company", ColNotes = "Notes", ColBirthday = "Birthday";

    /// <summary>RFC 4180 CSV — quoted cells, doubled quotes, CRLF; ',' ';' or tab, whichever the header uses.</summary>
    public static ImportTable Csv(string text)
    {
        var t = new ImportTable { Source = "csv" };
        text = (text ?? "").TrimStart('﻿');
        var firstLine = text.Split('\n').FirstOrDefault() ?? "";
        var delim = new[] { ',', ';', '\t' }.OrderByDescending(d => firstLine.Count(c => c == d)).First();
        var table = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else quoted = false;
                }
                else cell.Append(ch);
                continue;
            }
            if (ch == '"') quoted = true;
            else if (ch == delim) { row.Add(cell.ToString()); cell.Clear(); }
            else if (ch == '\r') { }
            else if (ch == '\n') { row.Add(cell.ToString()); cell.Clear(); table.Add(row); row = new List<string>(); }
            else cell.Append(ch);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); table.Add(row); }
        Fill(t, table);
        return t;
    }

    /// <summary>The first worksheet of an .xlsx; the first non-empty row is the header.</summary>
    public static ImportTable Xlsx(Stream stream)
    {
        var t = new ImportTable { Source = "xlsx" };
        using var reader = ExcelReaderFactory.CreateOpenXmlReader(stream);
        var table = new List<List<string>>();
        var guard = 0;
        while (reader.Read() && guard++ < ContactImporter.MaxRows + 50)
        {
            var cells = new List<string>(reader.FieldCount);
            for (var i = 0; i < reader.FieldCount; i++) cells.Add(CellText(reader.GetValue(i)));
            table.Add(cells);
        }
        Fill(t, table);
        return t;
    }

    /// <summary>A spreadsheet cell as text: phone numbers stored as numbers keep every digit.</summary>
    public static string CellText(object? v) => v switch
    {
        null => "",
        string s => s,
        double d when Math.Abs(d) < 1e15 && d == Math.Floor(d) => d.ToString("0", CultureInfo.InvariantCulture),
        double d => d.ToString(CultureInfo.InvariantCulture),
        DateTime dt => dt.TimeOfDay == TimeSpan.Zero ? dt.ToString("yyyy-MM-dd") : dt.ToString("yyyy-MM-dd HH:mm"),
        bool b => b ? "true" : "false",
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };

    private static void Fill(ImportTable t, List<List<string>> table)
    {
        var start = table.FindIndex(r => r.Any(c => !string.IsNullOrWhiteSpace(c)));
        if (start < 0) return;
        var headers = table[start].Select(h => (h ?? "").Trim()).ToList();
        // repeated / blank headers stay addressable: "Phone", "Phone (2)", "Column 3"
        for (var i = 0; i < headers.Count; i++)
        {
            if (headers[i].Length == 0) headers[i] = $"Column {i + 1}";
            var dup = 2;
            var baseName = headers[i];
            while (headers.Take(i).Contains(headers[i])) headers[i] = $"{baseName} ({dup++})";
        }
        foreach (var h in headers) if (!t.Headers.Contains(h)) t.Headers.Add(h);
        for (var r = start + 1; r < table.Count; r++)
        {
            var cells = table[r].Select(c => (c ?? "").Trim()).ToList();
            if (!cells.Any(c => c.Length > 0)) continue;
            var dict = new Dictionary<string, string>();
            for (var i = 0; i < headers.Count; i++) dict[headers[i]] = i < cells.Count ? cells[i] : "";
            t.Add(dict, r + 1);
        }
    }

    public static ImportTable Vcf(byte[] bytes)
    {
        var t = new ImportTable { Source = "vcf" };
        var cards = VCardParser.Parse(VCardParser.Decode(bytes));
        AddHeaders(t);
        var n = 0;
        foreach (var c in cards)
            t.Add(Row(c.DisplayName, c.GivenName, c.FamilyName, c.Phones, c.Emails, c.Organization, c.Note,
                c.Birthday), ++n);
        return t;
    }

    public static ImportTable Google(IEnumerable<GooglePerson> people)
    {
        var t = new ImportTable { Source = "google" };
        AddHeaders(t);
        var n = 0;
        foreach (var p in people)
            t.Add(Row(p.Name, p.GivenName, p.FamilyName, p.Phones, p.Emails, p.Organization, p.Note, p.Birthday), ++n);
        return t;
    }

    private static void AddHeaders(ImportTable t)
    {
        foreach (var h in new[] { ColName, ColFirst, ColLast, ColPhone, ColOtherPhones, ColEmail, ColOtherEmails,
                     ColCompany, ColNotes, ColBirthday })
            t.Headers.Add(h);
    }

    private static Dictionary<string, string> Row(string name, string first, string last, List<string> phones,
        List<string> emails, string org, string note, string bday) => new()
    {
        [ColName] = name, [ColFirst] = first, [ColLast] = last,
        [ColPhone] = phones.FirstOrDefault() ?? "",
        [ColOtherPhones] = string.Join(", ", phones.Skip(1)),
        [ColEmail] = emails.FirstOrDefault() ?? "",
        [ColOtherEmails] = string.Join(", ", emails.Skip(1)),
        [ColCompany] = org, [ColNotes] = note, [ColBirthday] = bday,
    };

    /// <summary>The JSON contract: {rows: [{header: value}]}.</summary>
    public static ImportTable Json(JsonElement rows, string source = "json")
    {
        var t = new ImportTable { Source = source };
        if (rows.ValueKind != JsonValueKind.Array) return t;
        var n = 1;
        foreach (var el in rows.EnumerateArray())
        {
            n++;
            if (el.ValueKind != JsonValueKind.Object) continue;
            var row = new Dictionary<string, string>();
            foreach (var p in el.EnumerateObject())
                row[p.Name] = (p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString()).Trim();
            if (row.Values.Any(v => v.Length > 0)) t.Add(row, n);
        }
        return t;
    }
}

/// <summary>A mapping target a column can feed.</summary>
public record ImportTarget(string Value, string Label, string Group, bool Multi = false);

public sealed class ImportOptions
{
    /// <summary>skip | update_empty | overwrite — what happens to a row that matches an existing contact.</summary>
    public string Mode { get; set; } = ContactImporter.ModeUpdateEmpty;
    public bool Preview { get; set; } = true;
    /// <summary>Entity every imported contact is linked to (already scope-checked).</summary>
    public int? EntityId { get; set; }
    /// <summary>Entities a per-row "entity" column may name (the importer's scope); null = any.</summary>
    public HashSet<int>? AllowedEntityIds { get; set; }
    public List<string> Tags { get; set; } = new();
    /// <summary>Register identities (+ queue WhatsApp lookups) for every imported contact.</summary>
    public bool Discover { get; set; } = true;
    public int? TenantId { get; set; }
    public string Actor { get; set; } = "import";
}

public sealed class ImportReport
{
    public int Total { get; set; }
    public int Created { get; set; }
    public int Updated { get; set; }
    public int Unchanged { get; set; }
    public int Skipped { get; set; }
    public int Merged { get; set; }
    public int Duplicates { get; set; }
    public List<Dictionary<string, object?>> Errors { get; } = new();
    public List<Dictionary<string, object?>> Warnings { get; } = new();
    public int Queued { get; set; }
}

/// <summary>
/// The contact-book importer behind <c>POST /api/contacts/import</c> (preview
/// and commit share every step, so the preview's counts are the commit's):
/// column mapping (contact fields, custom fields <c>cf:key</c>, tags, entity),
/// E.164 phones with the default country code, rows merged WITHIN the file
/// (same phone last-8 or email), matched against the tenant's existing contacts
/// (external id → phone last-8 → email) and handled by the chosen duplicate
/// mode: <c>skip</c>, <c>update_empty</c> (fill blanks only) or
/// <c>overwrite</c>. Writes in batches; never more than 20 000 rows.
/// </summary>
public class ContactImporter
{
    public const int MaxRows = 20_000;
    public const long MaxBytes = 10L * 1024 * 1024;
    public const int BatchSize = 500;
    public const string ModeSkip = "skip", ModeUpdateEmpty = "update_empty", ModeOverwrite = "overwrite";
    public static readonly string[] Modes = { ModeSkip, ModeUpdateEmpty, ModeOverwrite };

    private static readonly Regex EmailRx = new(@"^[^@\s,;]+@[^@\s,;]+\.[^@\s,;]{2,}$", RegexOptions.Compiled);

    public static readonly ImportTarget[] FieldTargets =
    {
        new("name", "Name", "Contact"),
        new("first_name", "First name", "Contact"),
        new("last_name", "Last name", "Contact"),
        new("phone", "Phone", "Contact"),
        new("other_phones", "Other phones", "Contact", Multi: true),
        new("email", "Email", "Contact"),
        new("other_emails", "Other emails", "Contact", Multi: true),
        new("company_name", "Company", "Contact"),
        new("language", "Language", "Contact"),
        new("external_id", "External ID", "Contact"),
        new("notes", "Notes", "Contact", Multi: true),
        new("birthday", "Birthday", "Contact"),
        new("tags", "Tags", "Organize", Multi: true),
        new("entity", "Entity (slug or name)", "Organize"),
    };

    /// <summary>header (normalised) → target.</summary>
    private static readonly (string Target, string[] Aliases)[] Aliases =
    {
        ("name", new[] { "name", "full name", "fullname", "contact name", "display name", "customer name",
                         "student name", "client name", "nama", "nama penuh", "fn" }),
        ("first_name", new[] { "first name", "firstname", "given name", "nama pertama" }),
        ("last_name", new[] { "last name", "lastname", "surname", "family name", "nama akhir" }),
        ("phone", new[] { "phone", "mobile", "phone number", "mobile number", "mobile phone", "contact number",
                          "whatsapp", "whatsapp number", "hp", "no hp", "no telefon", "telefon", "nombor telefon",
                          "cell", "cellphone", "tel", "telephone", "primary phone", "phone 1 value", "phone 1" }),
        ("other_phones", new[] { "other phones", "other phone", "phone 2", "phone 2 value", "phone 3 value",
                                 "home phone", "work phone", "business phone" }),
        ("email", new[] { "email", "e mail", "email address", "e mail address", "emel", "email 1 value",
                          "e mail 1 value", "primary email" }),
        ("other_emails", new[] { "other emails", "other email", "email 2", "email 2 value", "e mail 2 value" }),
        ("company_name", new[] { "company", "company name", "organization", "organisation", "organization name",
                                 "organization 1 name", "syarikat", "org", "business" }),
        ("language", new[] { "language", "lang", "bahasa" }),
        ("external_id", new[] { "external id", "id", "member id", "student id", "customer id", "client id",
                                "ref", "reference", "account number" }),
        ("notes", new[] { "notes", "note", "remarks", "comment", "comments", "catatan" }),
        ("birthday", new[] { "birthday", "birth date", "date of birth", "dob", "tarikh lahir" }),
        ("tags", new[] { "tags", "tag", "labels", "label", "group membership", "groups" }),
        ("entity", new[] { "entity", "branch", "outlet", "cawangan", "store" }),
    };

    private readonly AppDbContext _db;
    private readonly ScopingService _sc;

    public ContactImporter(AppDbContext db, ScopingService sc)
    {
        _db = db;
        _sc = sc;
    }

    // ---- mapping ----------------------------------------------------------------

    /// <summary>The custom-field schema (key, label, kind) from the `custom_fields` Setting.</summary>
    public List<(string Key, string Label, string Kind)> CustomFields()
    {
        var outv = new List<(string, string, string)>();
        try
        {
            var raw = _sc.SettingValue("custom_fields");
            if (string.IsNullOrWhiteSpace(raw)) return outv;
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("customer", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return outv;
            foreach (var f in arr.EnumerateArray())
            {
                var key = f.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "";
                if (key.Length == 0) continue;
                var label = f.TryGetProperty("label", out var l) ? l.GetString() ?? key : key;
                var kind = f.TryGetProperty("kind", out var kd) ? kd.GetString() ?? "text" : "text";
                outv.Add((key, label, kind));
            }
        }
        catch { }
        return outv;
    }

    public List<ImportTarget> Targets()
    {
        var list = FieldTargets.ToList();
        list.AddRange(CustomFields().Select(f => new ImportTarget($"cf:{f.Key}", f.Label, "Custom fields")));
        return list;
    }

    private static string Norm(string h) =>
        Regex.Replace(Regex.Replace((h ?? "").ToLowerInvariant(), @"[^a-z0-9]+", " "), @"\s+", " ").Trim();

    /// <summary>Suggested column → target ("" = ignore) for these headers.</summary>
    public Dictionary<string, string> Suggest(IReadOnlyList<string> headers)
    {
        var map = headers.ToDictionary(h => h, _ => "");
        var used = new HashSet<string>();
        var custom = CustomFields();
        foreach (var h in headers)
        {
            var n = Norm(h);
            if (n.Length == 0) continue;
            // a custom field by its key or label wins over the generic aliases
            var cf = custom.FirstOrDefault(f => Norm(f.Key) == n || Norm(f.Label) == n);
            if (cf.Key != null && !used.Contains($"cf:{cf.Key}"))
            {
                map[h] = $"cf:{cf.Key}";
                used.Add(map[h]);
                continue;
            }
            foreach (var (target, aliases) in Aliases)
            {
                if (!aliases.Contains(n)) continue;
                var multi = FieldTargets.First(t => t.Value == target).Multi;
                if (used.Contains(target) && !multi)
                {
                    // a second phone / email column becomes "other"
                    if (target == "phone") { map[h] = "other_phones"; break; }
                    if (target == "email") { map[h] = "other_emails"; break; }
                    continue;
                }
                map[h] = target;
                used.Add(target);
                break;
            }
            // Google CSV: "Phone 2 - Value", "E-mail 3 - Value"
            if (map[h].Length == 0 && Regex.IsMatch(n, @"^phone \d+ value$")) map[h] = used.Add("phone") ? "phone" : "other_phones";
            if (map[h].Length == 0 && Regex.IsMatch(n, @"^e ?mail \d+ value$")) map[h] = used.Add("email") ? "email" : "other_emails";
        }
        return map;
    }

    /// <summary>Normalise a requested mapping: unknown targets drop to "" (ignored).</summary>
    public Dictionary<string, string> Clean(IReadOnlyList<string> headers, Dictionary<string, string>? requested)
    {
        var valid = Targets().Select(t => t.Value).ToHashSet();
        var map = headers.ToDictionary(h => h, _ => "");
        if (requested == null) return map;
        foreach (var (h, target) in requested)
            if (map.ContainsKey(h)) map[h] = valid.Contains(target ?? "") ? target! : "";
        return map;
    }

    /// <summary>The legacy {field: header} mapping → {header: field}.</summary>
    public static Dictionary<string, string> FromLegacy(Dictionary<string, string> fieldToHeader) =>
        fieldToHeader.Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key);

    // ---- records ----------------------------------------------------------------

    private sealed class Rec
    {
        public int Row;
        public string Name = "", Phone = "", Email = "", Company = "", Language = "", ExternalId = "",
            Notes = "", Birthday = "", EntityRaw = "";
        public List<string> OtherPhones = new(), OtherEmails = new(), Tags = new();
        public Dictionary<string, object?> Custom = new();
        public int? EntityId;
        public string Tail => PhoneMatch.Tail(Phone) ?? "";
    }

    private static List<string> SplitList(string raw) =>
        raw.Replace(";", ",").Replace("|", ",").Replace(":::", ",").Split(',', '\n')
            .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

    private Rec? Build(Dictionary<string, string> row, int number, Dictionary<string, string> map, string cc,
        List<(string Key, string Label, string Kind)> custom, ImportReport report, ImportOptions opt,
        Dictionary<string, int> entityLookup)
    {
        var r = new Rec { Row = number };
        string first = "", last = "";
        var rawPhones = new List<string>();
        var rawEmails = new List<string>();
        foreach (var (header, target) in map)
        {
            if (target.Length == 0 || !row.TryGetValue(header, out var v) || string.IsNullOrWhiteSpace(v)) continue;
            v = v.Trim();
            switch (target)
            {
                case "name": r.Name = v; break;
                case "first_name": first = v; break;
                case "last_name": last = v; break;
                case "phone": rawPhones.Insert(0, v); break;
                case "other_phones": rawPhones.AddRange(SplitList(v)); break;
                case "email": rawEmails.Insert(0, v); break;
                case "other_emails": rawEmails.AddRange(SplitList(v)); break;
                case "company_name": r.Company = v; break;
                case "language": r.Language = Language(v); break;
                case "external_id": r.ExternalId = v; break;
                case "notes": r.Notes = r.Notes.Length > 0 ? r.Notes + "\n" + v : v; break;
                case "birthday": r.Birthday = v; break;
                case "tags": r.Tags.AddRange(SplitList(v)); break;
                case "entity": r.EntityRaw = v; break;
                default:
                    if (target.StartsWith("cf:"))
                    {
                        var key = target[3..];
                        var kind = custom.FirstOrDefault(f => f.Key == key).Kind ?? "text";
                        r.Custom[key] = CustomValue(v, kind);
                    }
                    break;
            }
        }
        if (r.Name.Length == 0) r.Name = string.Join(' ', new[] { first, last }.Where(x => x.Length > 0));

        // phones → E.164; the first usable one is THE phone, the rest "other phones"
        var badPhones = new List<string>();
        foreach (var p in rawPhones)
        {
            var e = PhoneNormalizer.ToE164(p, cc);
            if (e.Length == 0) { badPhones.Add(p); continue; }
            if (r.Phone.Length == 0) r.Phone = e;
            else if (e != r.Phone && !r.OtherPhones.Contains(e)) r.OtherPhones.Add(e);
        }
        var badEmails = new List<string>();
        foreach (var e in rawEmails)
        {
            var low = e.Trim().ToLowerInvariant();
            if (!EmailRx.IsMatch(low)) { badEmails.Add(e); continue; }
            if (r.Email.Length == 0) r.Email = low;
            else if (low != r.Email && !r.OtherEmails.Contains(low)) r.OtherEmails.Add(low);
        }

        if (r.Phone.Length == 0 && r.Email.Length == 0)
        {
            report.Errors.Add(Error(number, badPhones.Count > 0 ? $"invalid phone number \"{badPhones[0]}\""
                : badEmails.Count > 0 ? $"invalid email \"{badEmails[0]}\"" : "no phone number or email", r.Name,
                rawPhones.FirstOrDefault() ?? "", rawEmails.FirstOrDefault() ?? ""));
            return null;
        }
        foreach (var p in badPhones)
            report.Warnings.Add(Error(number, $"ignored invalid phone \"{p}\"", r.Name, p, r.Email));
        foreach (var e in badEmails)
            report.Warnings.Add(Error(number, $"ignored invalid email \"{e}\"", r.Name, r.Phone, e));
        if (r.Name.Length == 0) r.Name = r.Phone.Length > 0 ? r.Phone : r.Email;

        if (r.EntityRaw.Length > 0)
        {
            var key = r.EntityRaw.Trim().ToLowerInvariant();
            if (entityLookup.TryGetValue(key, out var eid)) r.EntityId = eid;
            else report.Warnings.Add(Error(number,
                $"entity \"{r.EntityRaw}\" not found or outside your scope — imported without it", r.Name, r.Phone, r.Email));
        }
        r.Tags = r.Tags.Concat(opt.Tags).Select(t => t.Trim()).Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return r;
    }

    private static string Language(string v)
    {
        var s = v.Trim().ToLowerInvariant();
        return s switch
        {
            "english" or "inggeris" or "bi" => "en",
            "malay" or "melayu" or "bahasa melayu" or "bm" or "bahasa" => "ms",
            "chinese" or "mandarin" or "cina" => "zh",
            _ => s.Length is >= 2 and <= 5 ? s : "",
        };
    }

    private static object? CustomValue(string v, string kind) => kind switch
    {
        "boolean" => v.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "y" or "ya",
        "number" => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : v,
        "multiselect" => SplitList(v),
        _ => v,
    };

    private static Dictionary<string, object?> Error(int row, string reason, string name, string phone, string email) =>
        new() { ["row"] = row, ["reason"] = reason, ["name"] = name, ["phone"] = phone, ["email"] = email };

    /// <summary>Fold rows that are the same person (phone last-8 or email) into the first one.</summary>
    private static Rec Merge(Rec into, Rec from)
    {
        if (into.Name.Length == 0 || into.Name == into.Phone || into.Name == into.Email) into.Name = from.Name;
        if (into.Phone.Length == 0) into.Phone = from.Phone;
        else if (from.Phone.Length > 0 && from.Phone != into.Phone && !into.OtherPhones.Contains(from.Phone))
            into.OtherPhones.Add(from.Phone);
        if (into.Email.Length == 0) into.Email = from.Email;
        else if (from.Email.Length > 0 && from.Email != into.Email && !into.OtherEmails.Contains(from.Email))
            into.OtherEmails.Add(from.Email);
        foreach (var p in from.OtherPhones) if (p != into.Phone && !into.OtherPhones.Contains(p)) into.OtherPhones.Add(p);
        foreach (var e in from.OtherEmails) if (e != into.Email && !into.OtherEmails.Contains(e)) into.OtherEmails.Add(e);
        if (into.Company.Length == 0) into.Company = from.Company;
        if (into.Language.Length == 0) into.Language = from.Language;
        if (into.ExternalId.Length == 0) into.ExternalId = from.ExternalId;
        if (from.Notes.Length > 0 && !into.Notes.Contains(from.Notes))
            into.Notes = into.Notes.Length > 0 ? into.Notes + "\n" + from.Notes : from.Notes;
        if (into.Birthday.Length == 0) into.Birthday = from.Birthday;
        into.EntityId ??= from.EntityId;
        foreach (var t in from.Tags) if (!into.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)) into.Tags.Add(t);
        foreach (var (k, v) in from.Custom) into.Custom.TryAdd(k, v);
        return into;
    }

    // ---- run ----------------------------------------------------------------------

    public ImportReport Run(ImportTable table, Dictionary<string, string> mapping, ImportOptions opt)
    {
        var report = new ImportReport { Total = table.Rows.Count };
        if (!Modes.Contains(opt.Mode)) opt.Mode = ModeUpdateEmpty;
        var cc = PhoneNormalizer.CountryCode(_sc);
        var custom = CustomFields();
        var entityLookup = EntityLookup(opt);

        // 1. rows → records (errors for unusable rows)
        var recs = new List<Rec>();
        for (var i = 0; i < table.Rows.Count; i++)
        {
            var r = Build(table.Rows[i], table.RowNumbers.ElementAtOrDefault(i), mapping, cc, custom, report, opt,
                entityLookup);
            if (r != null) recs.Add(r);
        }

        // 2. merge rows that are the same person within the file
        var byTail = new Dictionary<string, Rec>();
        var byEmail = new Dictionary<string, Rec>();
        var unique = new List<Rec>();
        foreach (var r in recs)
        {
            Rec? same = null;
            if (r.Tail.Length > 0) byTail.TryGetValue(r.Tail, out same);
            if (same == null && r.Email.Length > 0) byEmail.TryGetValue(r.Email, out same);
            if (same != null)
            {
                Merge(same, r);
                report.Merged++;
                if (same.Tail.Length > 0) byTail.TryAdd(same.Tail, same);
                if (same.Email.Length > 0) byEmail.TryAdd(same.Email, same);
                continue;
            }
            unique.Add(r);
            if (r.Tail.Length > 0) byTail[r.Tail] = r;
            if (r.Email.Length > 0) byEmail[r.Email] = r;
        }

        // 3. match against the tenant's existing contacts (loaded once)
        IQueryable<Contact> scope = opt.TenantId is int tid
            ? _db.Contacts.Where(c => c.TenantId == tid)
            : _db.Contacts.Where(c => c.TenantId == null);
        var existing = scope.AsNoTracking()
            .Select(c => new { c.Id, c.Phone, c.Email, c.ExternalId }).ToList();
        var exTail = existing.Where(c => PhoneMatch.Tail(c.Phone) != null)
            .GroupBy(c => PhoneMatch.Tail(c.Phone)!).ToDictionary(g => g.Key, g => g.Select(c => c.Id).OrderBy(i => i).ToList());
        var exEmail = existing.Where(c => c.Email.Length > 0)
            .GroupBy(c => c.Email.Trim().ToLowerInvariant()).ToDictionary(g => g.Key, g => g.Select(c => c.Id).OrderBy(i => i).ToList());
        var exExt = existing.Where(c => c.ExternalId.Length > 0)
            .GroupBy(c => c.ExternalId).ToDictionary(g => g.Key, g => g.Select(c => c.Id).OrderBy(i => i).ToList());

        // "The same person" is per COMPANY (ContactCompanies): a row only matches
        // an existing contact of one of the companies it is imported into (or,
        // imported with no entity, an organization-wide contact) — a contact of
        // another company is a different record, so the row is created.
        var exLinks = new Dictionary<int, List<int>>();
        foreach (var chunk in existing.Select(c => c.Id).Chunk(BatchSize))
            foreach (var l in _db.ContactEntities.AsNoTracking().Where(l => chunk.Contains(l.ContactId))
                         .Select(l => new { l.ContactId, l.EntityId }))
                (exLinks.TryGetValue(l.ContactId, out var list) ? list : exLinks[l.ContactId] = new List<int>()).Add(l.EntityId);
        var companyCache = new Dictionary<int, int>();
        int Company(int e) => companyCache.TryGetValue(e, out var co) ? co : companyCache[e] = ContactCompanies.CompanyOf(_sc, e);
        HashSet<int> CompaniesOfContact(int id) =>
            exLinks.TryGetValue(id, out var ls) ? ls.Select(Company).ToHashSet() : new HashSet<int>();
        // …and an UNASSIGNED contact (no entity at all) is adopted: the import
        // links it to the entity instead of creating a twin
        int? SameCompany(List<int>? ids, HashSet<int> mine) =>
            ids?.Cast<int?>().FirstOrDefault(id =>
            {
                var theirs = CompaniesOfContact(id!.Value);
                return mine.Count == 0 ? theirs.Count == 0 : theirs.Overlaps(mine);
            })
            ?? ids?.Cast<int?>().FirstOrDefault(id => CompaniesOfContact(id!.Value).Count == 0);

        var plan = new List<(Rec Rec, int? ExistingId)>();
        foreach (var r in unique)
        {
            var mine = new[] { opt.EntityId, r.EntityId }.OfType<int>().Select(Company).ToHashSet();
            int? id = null;
            if (r.ExternalId.Length > 0) id = SameCompany(exExt.GetValueOrDefault(r.ExternalId), mine);
            if (id == null && r.Tail.Length > 0) id = SameCompany(exTail.GetValueOrDefault(r.Tail), mine);
            if (id == null && r.Email.Length > 0) id = SameCompany(exEmail.GetValueOrDefault(r.Email), mine);
            plan.Add((r, id));
        }
        report.Duplicates = plan.Count(p => p.ExistingId != null);

        if (opt.Preview)
        {
            report.Created = plan.Count(p => p.ExistingId == null);
            if (opt.Mode == ModeSkip) { report.Skipped = report.Duplicates; return report; }
            // the same Apply the commit runs, on detached copies — so "would update"
            // excludes duplicates that already hold everything the file has
            var matchIds = plan.Where(p => p.ExistingId != null).Select(p => p.ExistingId!.Value).Distinct().ToList();
            var copies = new Dictionary<int, Contact>();
            foreach (var chunk in matchIds.Chunk(BatchSize))
                foreach (var c in _db.Contacts.AsNoTracking().Where(c => chunk.Contains(c.Id)))
                    copies[c.Id] = c;
            // an entity link the import would add is a change too
            var links = new HashSet<(int, int)>();
            foreach (var chunk in matchIds.Chunk(BatchSize))
                foreach (var l in _db.ContactEntities.AsNoTracking().Where(l => chunk.Contains(l.ContactId))
                             .Select(l => new { l.ContactId, l.EntityId }))
                    links.Add((l.ContactId, l.EntityId));
            bool NeedsLink(int cid, int? eid) => eid is int e && !links.Contains((cid, e));
            foreach (var (r, id) in plan.Where(p => p.ExistingId != null))
            {
                if (!copies.TryGetValue(id!.Value, out var c)) continue;
                var changed = Apply(c, r, overwrite: opt.Mode == ModeOverwrite)
                              | r.Tags.Any(t => !c.Tags.Contains(t, StringComparer.OrdinalIgnoreCase))
                              | NeedsLink(c.Id, opt.EntityId) | NeedsLink(c.Id, r.EntityId);
                if (changed) report.Updated++; else report.Unchanged++;
            }
            return report;
        }

        // 4. write in batches
        var touched = new List<int>();
        var tagWork = new List<(int ContactId, List<string> Names, bool Fresh)>();
        var now = DateTime.UtcNow;
        foreach (var chunk in plan.Chunk(BatchSize))
        {
            var ids = chunk.Where(p => p.ExistingId != null).Select(p => p.ExistingId!.Value).ToList();
            var tracked = _db.Contacts.Where(c => ids.Contains(c.Id)).ToDictionary(c => c.Id);
            var created = new List<(Contact Contact, Rec Rec)>();
            foreach (var (r, existingId) in chunk)
            {
                if (existingId is int eid && tracked.TryGetValue(eid, out var c))
                {
                    if (opt.Mode == ModeSkip) { report.Skipped++; continue; }
                    var changed = Apply(c, r, overwrite: opt.Mode == ModeOverwrite);
                    var tagsChanged = r.Tags.Any(t => !c.Tags.Contains(t, StringComparer.OrdinalIgnoreCase));
                    var linked = (r.EntityId is int re && Link(c.Id, re)) | (opt.EntityId is int oe && Link(c.Id, oe));
                    if (changed || tagsChanged || linked)
                    {
                        c.UpdatedAt = now;
                        report.Updated++;
                    }
                    else report.Unchanged++;
                    if (tagsChanged) tagWork.Add((c.Id, c.Tags.Concat(r.Tags).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), false));
                    touched.Add(c.Id);
                }
                else
                {
                    var n = new Contact
                    {
                        TenantId = opt.TenantId, Status = "active", Language = r.Language.Length > 0 ? r.Language : "en",
                        CreatedAt = now, UpdatedAt = now,
                    };
                    Apply(n, r, overwrite: true);
                    _db.Contacts.Add(n);
                    created.Add((n, r));
                    report.Created++;
                }
            }
            try
            {
                _db.SaveChanges();
            }
            catch (Exception e)
            {
                // a batch that cannot be written is reported row by row, never half-applied silently
                foreach (var (c, r) in created)
                {
                    _db.Entry(c).State = EntityState.Detached;
                    report.Errors.Add(Error(r.Row, $"could not be saved ({e.GetType().Name})", r.Name, r.Phone, r.Email));
                }
                report.Created -= created.Count;
                foreach (var entry in _db.ChangeTracker.Entries().Where(x => x.State != EntityState.Unchanged).ToList())
                    entry.State = EntityState.Detached;
                continue;
            }
            foreach (var (c, r) in created)
            {
                if (r.EntityId is int re) Link(c.Id, re);
                if (opt.EntityId is int oe) Link(c.Id, oe);
                if (r.Tags.Count > 0) tagWork.Add((c.Id, r.Tags, true));
                touched.Add(c.Id);
            }
            _db.SaveChanges();
            _linked.Clear();
        }

        // 5. tags through the registry (one ApplyContactNames per distinct set; the
        //    rest copy its resolved tags), then the contacts' tag caches
        ApplyTags(tagWork, opt.Actor);

        // 6. identities (WhatsApp / voice / SMS / email) + queued WhatsApp lookups
        if (opt.Discover && touched.Count > 0)
        {
            var discovery = new ContactIdentityService(_db, _sc);
            foreach (var chunk in touched.Distinct().Chunk(BatchSize))
            {
                var contacts = _db.Contacts.Where(c => chunk.Contains(c.Id)).ToList();
                foreach (var c in contacts)
                    report.Queued += discovery.Discover(c, "import", save: false).Queued;
                _db.SaveChanges();
            }
        }

        Audit.AuditTrail.Write(_db, "contacts.import", $"{table.Source}:{table.Rows.Count} rows",
            $"created {report.Created}, updated {report.Updated}, skipped {report.Skipped}, merged {report.Merged}, "
            + $"errors {report.Errors.Count} (mode {opt.Mode})", entityId: opt.EntityId);
        return report;
    }

    private readonly HashSet<(int, int)> _linked = new();

    /// <summary>Link the contact to the entity unless it already is. True when a link was added.</summary>
    private bool Link(int contactId, int entityId)
    {
        if (!_linked.Add((contactId, entityId))) return false;
        if (_db.ContactEntities.Any(l => l.ContactId == contactId && l.EntityId == entityId)) return false;
        _db.ContactEntities.Add(new ContactEntity { ContactId = contactId, EntityId = entityId, CreatedAt = DateTime.UtcNow });
        return true;
    }

    /// <summary>Copy a record onto a contact. overwrite=false fills blanks only. True when something changed.</summary>
    private static bool Apply(Contact c, Rec r, bool overwrite)
    {
        var changed = false;
        void Set(Func<string> get, Action<string> set, string value, bool emptyIsDefault = false)
        {
            if (value.Length == 0) return;
            var cur = get();
            if (cur == value) return;
            if (!overwrite && cur.Length > 0 && !emptyIsDefault) return;
            set(value);
            changed = true;
        }
        Set(() => c.Name, v => c.Name = v, r.Name);
        Set(() => c.Phone, v => c.Phone = v, r.Phone);
        Set(() => c.Email, v => c.Email = v, r.Email);
        Set(() => c.CompanyName, v => c.CompanyName = v, r.Company);
        if (r.Language.Length > 0 && c.Language != r.Language && (overwrite || c.Language is "" or "en"))
        {
            c.Language = r.Language;
            changed = true;
        }
        Set(() => c.ExternalId, v => c.ExternalId = v, r.ExternalId);
        Set(() => c.Notes, v => c.Notes = v, r.Notes);

        var attrs = new Dictionary<string, object?>(c.Attributes);
        void Attr(string key, object? value)
        {
            if (value == null || value is string { Length: 0 }) return;
            var has = attrs.TryGetValue(key, out var cur) && cur != null && cur.ToString()?.Length > 0;
            if (has && !overwrite) return;
            if (has && JsonSerializer.Serialize(cur) == JsonSerializer.Serialize(value)) return;
            attrs[key] = value;
            changed = true;
        }
        // other phones / emails accumulate (never lose a number the file mentions)
        var others = MergeList(attrs, "other_phones", r.OtherPhones.Where(p => p != c.Phone));
        if (others) changed = true;
        if (MergeList(attrs, "other_emails", r.OtherEmails.Where(e => e != c.Email))) changed = true;
        Attr("birthday", r.Birthday);
        foreach (var (k, v) in r.Custom) Attr(k, v);
        if (changed) c.Attributes = attrs;
        return changed;
    }

    private static bool MergeList(Dictionary<string, object?> attrs, string key, IEnumerable<string> add)
    {
        var items = add.ToList();
        if (items.Count == 0) return false;
        var cur = attrs.TryGetValue(key, out var raw) && raw != null
            ? SplitList(raw is JsonElement el && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : raw.ToString() ?? "")
            : new List<string>();
        var next = cur.Concat(items).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (next.Count == cur.Count) return false;
        attrs[key] = string.Join(", ", next);
        return true;
    }

    private void ApplyTags(List<(int ContactId, List<string> Names, bool Fresh)> work, string actor)
    {
        if (work.Count == 0) return;
        // fresh contacts sharing one tag set: resolve it once, then copy the assignments
        var resolved = new Dictionary<string, List<int>>();
        var refresh = new List<int>();
        foreach (var (contactId, names, fresh) in work)
        {
            try
            {
                var key = string.Join("\u0001", names.Select(n => n.ToLowerInvariant()).OrderBy(n => n));
                if (!fresh || !resolved.TryGetValue(key, out var tagIds))
                {
                    TagService.ApplyContactNames(_db, contactId, names, actor);
                    if (fresh)
                        resolved[key] = _db.TagAssignments.AsNoTracking()
                            .Where(a => a.TargetType == TagService.ContactTarget && a.TargetId == contactId)
                            .Select(a => a.TagId).ToList();
                    continue;
                }
                foreach (var tagId in tagIds)
                    _db.TagAssignments.Add(new TagAssignment
                    {
                        TagId = tagId, TargetType = TagService.ContactTarget, TargetId = contactId,
                        CreatedBy = actor, CreatedAt = DateTime.UtcNow,
                    });
                refresh.Add(contactId);
                if (refresh.Count % BatchSize == 0)
                {
                    _db.SaveChanges();
                    TagService.RefreshContactCache(_db, refresh.Skip(refresh.Count - BatchSize));
                }
            }
            catch { /* tagging never fails an imported row */ }
        }
        try
        {
            _db.SaveChanges();
            var rest = refresh.Count % BatchSize;
            if (rest > 0) TagService.RefreshContactCache(_db, refresh.Skip(refresh.Count - rest));
        }
        catch { }
    }

    /// <summary>slug / lower-cased name → entity id, limited to the importer's scope.</summary>
    private Dictionary<string, int> EntityLookup(ImportOptions opt)
    {
        var rows = _db.Entities.AsNoTracking().Select(e => new { e.Id, e.Slug, e.Name, e.TenantId }).ToList()
            .Where(e => opt.AllowedEntityIds == null || opt.AllowedEntityIds.Contains(e.Id))
            .Where(e => opt.TenantId == null || e.TenantId == opt.TenantId);
        var map = new Dictionary<string, int>();
        foreach (var e in rows)
        {
            map.TryAdd(e.Slug.ToLowerInvariant(), e.Id);
            map.TryAdd(e.Name.Trim().ToLowerInvariant(), e.Id);
        }
        return map;
    }

    /// <summary>Up to <paramref name="n"/> non-empty sample values per column.</summary>
    public static Dictionary<string, List<string>> Samples(ImportTable t, int n = 3) =>
        t.Headers.ToDictionary(h => h, h => t.Rows.Select(r => r.TryGetValue(h, out var v) ? v : "")
            .Where(v => v.Length > 0).Take(n).Select(v => v.Length > 60 ? v[..60] + "…" : v).ToList());
}
