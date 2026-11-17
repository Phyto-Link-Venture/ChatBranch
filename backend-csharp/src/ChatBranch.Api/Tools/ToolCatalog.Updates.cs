namespace ChatBranch.Api.Tools;

public static partial class ToolCatalog
{
    /// <summary>Versions and each company's update choice (UpdatesEndpoints, Domain/Releases.cs).</summary>
    private static IEnumerable<IChatBranchTool> Updates()
    {
        const string st = "settings";
        yield return T(st, "updates_get_status", "Versions and update choice",
            "The released ChatBranch versions (newest first, with dates), the version the caller's company runs "
            + "(mine), and — for someone who may manage updates — each company they manage with its choice: "
            + "pinned_major (the major it stays on until moved), minor_mode (auto = new features arrive "
            + "automatically; hold = only the approved version, plus its fixes), held_version and the newest "
            + "version available in its major. Patches (fixes) always apply.",
            "GET", "/api/updates", C(), link: (_, _) => Page("Open What's new", "/whats-new"));
        yield return T(st, "updates_set_policy", "Change a company's update choice",
            "Hold or resume automatic feature updates for one company, approve a version while holding, or move "
            + "the company to another major version. Send only what changes. Every change is recorded in the "
            + "activity log (updates.policy).",
            "PUT", "/api/updates", C("updates.manage"), new[]
            {
                A.Str("company", "Company (top-level entity) id or name.", req: true),
                A.Str("minor_mode", "auto | hold.", oneOf: new[] { "auto", "hold" }),
                A.Str("approve", "While holding: the version to run, e.g. \"1.4.0\" (its later fixes still apply)."),
                A.Int("pinned_major", "Move the company to this major version (e.g. 2)."),
            }, idempotent: true, link: (_, _) => Page("Open update settings", "/settings/updates"));
        yield return T(st, "updates_get_overview", "Every company's version",
            "For the developers: every company with the version it runs, its update choice and who changed it "
            + "last, plus the 50 most recent changes.",
            "GET", "/api/updates/overview", C("updates.overview"),
            link: (_, _) => Page("Open update settings", "/settings/updates"));
    }
}
