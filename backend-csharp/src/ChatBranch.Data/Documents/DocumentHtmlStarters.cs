using System.Reflection;
using System.Text.RegularExpressions;

namespace ChatBranch.Data.Documents;

/// <summary>
/// The custom layouts shipped with the product (embedded <c>Documents/HtmlStarters/*.html</c>)
/// — the editor's "Start from" and the no-model fallback of "Upload my design":
/// <list type="bullet">
/// <item><b>malaysian-classic</b> — the formal Malaysian invoice (centred letterhead,
/// underlined title, Ref No / Date, Attn + Tel + Fax, "Dear Sir / Madam,", PRO / REF, a
/// grey-headed grid table with "1)" numbering and a bold first line, the amount in words,
/// "Total Payment (RM) :", a numbered NOTE list ending with the bank account, the
/// signature block); quotations and receipts adapt, letters drop the table.</item>
/// <item><b>clean-modern</b> — an airy layout in the skill's accent colour.</item>
/// <item><b>letter</b> — a formal letter on a serif letterhead.</item>
/// </list>
/// Each works in English and Malay (the <c>label</c> / <c>words</c> / <c>date</c> helpers).
/// </summary>
public static class DocumentHtmlStarters
{
    public sealed record Starter(string Id, string Name, string Description, List<DocField> Fields)
    {
        public string Template => Load(Id);

        /// <summary>The starter as a complete <c>html</c> config (page defaults, its fields, no words yet).</summary>
        public DocumentHtmlConfig Config() => new() { Template = Template, Fields = Fields.ToList() };

        public Dictionary<string, object?> ToDictionary() => new()
        {
            ["id"] = Id,
            ["name"] = Name,
            ["description"] = Description,
            ["html"] = Config().ToDictionary(),
        };
    }

    public static readonly Starter[] All =
    {
        new("malaysian-classic", "Malaysian classic",
            "Formal Malaysian invoice: centred letterhead, Ref No / Date, Attn, PRO / REF, grid table, amount in words, NOTE list and signature block.",
            new()
            {
                new("project", "Project (PRO)", "text", false, ""),
                new("attn_tel", "Attn tel", "text", false, ""),
                new("attn_fax", "Attn fax", "text", false, ""),
                new("signatory_name", "Signatory name", "text", false, ""),
                new("signatory_title", "Signatory title", "text", false, "DIRECTOR"),
            }),
        new("clean-modern", "Clean modern",
            "An airy layout in your accent colour: logo and business on the left, the title on the right, zebra rows and a totals card.",
            new()),
        new("letter", "Letter",
            "A formal business letter on a serif letterhead; invoices and quotes print a compact table under it.",
            new()
            {
                new("signatory_name", "Signatory name", "text", false, ""),
                new("signatory_title", "Signatory title", "text", false, ""),
            }),
    };

    public static Starter? Get(string? id) => All.FirstOrDefault(s => s.Id == (id ?? "").Trim().ToLowerInvariant());

    public static List<Dictionary<string, object?>> Catalog() => All.Select(s => s.ToDictionary()).ToList();

    private static readonly Dictionary<string, string> Cache = new();

    private static string Load(string id)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(id, out var hit)) return hit;
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith($".HtmlStarters.{id}.html", StringComparison.Ordinal))
                       ?? throw new InvalidOperationException($"starter template {id} is not embedded");
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd().Replace("\r\n", "\n");
            return Cache[id] = text;
        }
    }

    /// <summary>
    /// The starter closest to an uploaded design's text (the fallback when no model can draft
    /// one): formal Malaysian wording → malaysian-classic; a letter (no table words) → letter;
    /// else clean-modern.
    /// </summary>
    public static Starter Closest(DocumentTemplateAi.PdfLook? look)
    {
        var text = look == null ? "" : string.Join("\n", look.Lines.Select(l => l.Text));
        if (Regex.IsMatch(text, @"\b(ringgit malaysia|ref\.?\s*no|attn|dear sir|note\s*:|your'?s faithfully|chop|sdn\.?\s*bhd|tel\s*:)", RegexOptions.IgnoreCase))
            return All[0];
        var tableWords = Regex.Matches(text, @"\b(qty|quantity|unit price|amount|total|description|rate)\b", RegexOptions.IgnoreCase).Count;
        if (look != null && look.Lines.Count > 0 && tableWords < 2) return All[2];
        return All[1];
    }
}
