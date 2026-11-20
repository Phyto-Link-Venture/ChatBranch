namespace ChatBranch.Domain;

// ---- product versions and each company's update choice ------------------------
//
// ChatBranch is versioned MAJOR.MINOR.PATCH. A PATCH (1.4.0 → 1.4.1) only fixes
// things and always reaches everyone. A MINOR (1.4 → 1.5) adds features: a
// company's admin either takes them automatically (the default) or holds them
// until they approve. A MAJOR (1 → 2) is never automatic: a company stays on its
// major until its admin moves it. The release notes (what changed in each
// version) live in the dashboard, frontend/src/content/releases.ts, and must list
// exactly the versions below (a test checks both lists match).
//
// Today one build serves every company, so "holding" a minor hides that minor's
// new features (the dashboard's useFeature gate) rather than serving older code;
// a second major will add a second build, routed by the company's pinned major.

/// <summary>A MAJOR.MINOR.PATCH version.</summary>
public readonly record struct SemVer(int Major, int Minor, int Patch) : IComparable<SemVer>
{
    public static bool TryParse(string? s, out SemVer v)
    {
        v = default;
        var parts = (s ?? "").Trim().TrimStart('v', 'V').Split('.');
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], out var a) || !int.TryParse(parts[1], out var b) || !int.TryParse(parts[2], out var c)) return false;
        if (a < 0 || b < 0 || c < 0) return false;
        v = new SemVer(a, b, c);
        return true;
    }

    public static SemVer Parse(string s) =>
        TryParse(s, out var v) ? v : throw new FormatException($"Not a MAJOR.MINOR.PATCH version: '{s}'.");

    public int CompareTo(SemVer o) =>
        Major != o.Major ? Major.CompareTo(o.Major) : Minor != o.Minor ? Minor.CompareTo(o.Minor) : Patch.CompareTo(o.Patch);

    public static bool operator <(SemVer a, SemVer b) => a.CompareTo(b) < 0;
    public static bool operator >(SemVer a, SemVer b) => a.CompareTo(b) > 0;
    public static bool operator <=(SemVer a, SemVer b) => a.CompareTo(b) <= 0;
    public static bool operator >=(SemVer a, SemVer b) => a.CompareTo(b) >= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

/// <summary>Every released version, oldest first. Add the new version here (and its
/// notes in the dashboard's releases.ts) in the same commit that ships it.</summary>
public static class ProductReleases
{
    public static readonly IReadOnlyList<(SemVer Version, DateOnly Date)> All = new[]
    {
        (new SemVer(1, 0, 0), new DateOnly(2026, 10, 9)),
        (new SemVer(1, 1, 0), new DateOnly(2026, 10, 30)),
        (new SemVer(1, 2, 0), new DateOnly(2026, 11, 10)),
        (new SemVer(1, 3, 0), new DateOnly(2026, 11, 17)),
        (new SemVer(1, 4, 0), new DateOnly(2026, 11, 17)),
        (new SemVer(1, 5, 0), new DateOnly(2026, 11, 20)),
    };

    /// <summary>The major a company is on until its admin moves it. Companies with no
    /// saved choice stay here when a later major ships.</summary>
    public const int BaselineMajor = 1;

    public static SemVer Current => All[^1].Version;

    public static bool Exists(SemVer v) => All.Any(r => r.Version == v);

    public static IReadOnlyList<int> Majors => All.Select(r => r.Version.Major).Distinct().ToList();

    /// <summary>The newest version within a major (patches and minors included).</summary>
    public static SemVer LatestIn(int major) =>
        All.Where(r => r.Version.Major == major).Select(r => r.Version).DefaultIfEmpty(Current).Max();

    /// <summary>The newest patch of a held MAJOR.MINOR (patches always apply).</summary>
    public static SemVer LatestPatchOf(SemVer held) =>
        All.Where(r => r.Version.Major == held.Major && r.Version.Minor == held.Minor)
            .Select(r => r.Version).DefaultIfEmpty(held).Max();

    /// <summary>The version a company actually runs under its choice (none saved =
    /// automatic minors on the baseline major).</summary>
    public static SemVer Effective(UpdatePolicy? p)
    {
        var major = p?.PinnedMajor is int m && m > 0 ? m : BaselineMajor;
        if (p is { MinorMode: UpdateModes.Hold } && SemVer.TryParse(p.HeldVersion, out var held) && held.Major == major)
            return LatestPatchOf(held);
        return LatestIn(major);
    }
}

public static class UpdateModes
{
    public const string Auto = "auto";
    public const string Hold = "hold";
}

/// <summary>
/// One company's update choice (<c>crm_updatepolicy</c>), keyed by its top-level
/// entity; branches follow their company. No row = the defaults (automatic minors,
/// <see cref="ProductReleases.BaselineMajor"/>).
/// </summary>
public class UpdatePolicy
{
    public int Id { get; set; }
    /// <summary>The company (a top-level entity).</summary>
    public int EntityId { get; set; }
    /// <summary>The major the company stays on until its admin moves it.</summary>
    public int PinnedMajor { get; set; } = ProductReleases.BaselineMajor;
    /// <summary><see cref="UpdateModes.Auto"/> or <see cref="UpdateModes.Hold"/>.</summary>
    public string MinorMode { get; set; } = UpdateModes.Auto;
    /// <summary>While holding: the approved MAJOR.MINOR.PATCH (its later patches still apply). "" when automatic.</summary>
    public string HeldVersion { get; set; } = "";
    public string UpdatedBy { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
}
