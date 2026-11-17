using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>Versions and each company's update choice (UpdatesEndpoints, Domain/Releases.cs).</summary>
public class UpdatesTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public UpdatesTests(ApiFactory f) => _f = f;

    [Fact]
    public void Releases_are_ascending_and_match_the_dashboard_release_notes()
    {
        var versions = ProductReleases.All.Select(r => r.Version).ToList();
        Assert.Equal(versions.OrderBy(v => v).ToList(), versions);
        Assert.Equal(versions.Count, versions.Distinct().Count());

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "frontend"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var notes = File.ReadAllText(Path.Combine(dir!.FullName, "frontend", "src", "content", "releases.ts"));
        var noted = Regex.Matches(notes, @"version:\s*'(\d+\.\d+\.\d+)'").Select(m => SemVer.Parse(m.Groups[1].Value))
            .OrderBy(v => v).ToList();
        Assert.Equal(versions, noted);
    }

    [Fact]
    public void Effective_version_follows_the_choice()
    {
        Assert.Equal(ProductReleases.LatestIn(1), ProductReleases.Effective(null));
        var held = new UpdatePolicy { MinorMode = UpdateModes.Hold, HeldVersion = "1.2.0" };
        Assert.Equal(ProductReleases.LatestPatchOf(new SemVer(1, 2, 0)), ProductReleases.Effective(held));
        var junk = new UpdatePolicy { MinorMode = UpdateModes.Hold, HeldVersion = "nonsense" };
        Assert.Equal(ProductReleases.LatestIn(1), ProductReleases.Effective(junk));
        Assert.True(SemVer.Parse("1.10.0") > SemVer.Parse("1.9.3"));
    }

    [Fact]
    public async Task Admin_holds_approves_and_resumes_and_every_change_is_logged()
    {
        var (a, _) = Companies();
        var admin = await SettingsTestKit.Client(_f, "admin", "admin123");

        var status = await (await admin.GetAsync("/api/updates")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ProductReleases.Current.ToString(), status.GetProperty("current").GetString());
        Assert.True(status.GetProperty("can_manage").GetBoolean());
        Assert.False(status.GetProperty("can_overview").GetBoolean());
        Assert.Contains(status.GetProperty("companies").EnumerateArray(), c => c.GetProperty("id").GetInt32() == a);

        var hold = await admin.PutAsJsonAsync("/api/updates", new { company = a.ToString(), minor_mode = "hold" });
        hold.EnsureSuccessStatusCode();
        var h = await hold.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("hold", h.GetProperty("minor_mode").GetString());
        Assert.Equal(ProductReleases.Current.ToString(), h.GetProperty("held_version").GetString()); // nothing changes today

        var approve = await admin.PutAsJsonAsync("/api/updates", new { company = a.ToString(), approve = "1.2.0" });
        approve.EnsureSuccessStatusCode();
        Assert.Equal(ProductReleases.LatestPatchOf(new SemVer(1, 2, 0)).ToString(),
            (await approve.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetString());

        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PutAsJsonAsync("/api/updates", new { company = a.ToString(), approve = "9.9.9" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PutAsJsonAsync("/api/updates", new { company = a.ToString(), pinned_major = 9 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PutAsJsonAsync("/api/updates", new { company = a.ToString(), minor_mode = "sometimes" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PutAsJsonAsync("/api/updates", new { minor_mode = "auto" })).StatusCode);

        var resume = await admin.PutAsJsonAsync("/api/updates", new { company = a.ToString(), minor_mode = "auto" });
        resume.EnsureSuccessStatusCode();
        var r = await resume.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("", r.GetProperty("held_version").GetString());
        Assert.Equal(ProductReleases.LatestIn(1).ToString(), r.GetProperty("version").GetString());

        var logged = SettingsTestKit.WithDb(_f, db => db.AuditLogs.AsNoTracking()
            .Count(l => l.Action == "updates.policy" && l.Target.StartsWith($"company:{a} ")));
        Assert.Equal(3, logged); // hold, approve, resume — the refused calls wrote nothing
    }

    [Fact]
    public async Task Agents_read_but_cannot_change_and_company_admins_stay_in_their_company()
    {
        var (a, b) = Companies();
        var agent = await SettingsTestKit.Client(_f, "agent", "agent123");
        var mine = await (await agent.GetAsync("/api/updates")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(mine.GetProperty("can_manage").GetBoolean());
        Assert.Empty(mine.GetProperty("companies").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden,
            (await agent.PutAsJsonAsync("/api/updates", new { company = a.ToString(), minor_mode = "hold" })).StatusCode);

        SettingsTestKit.CreateUser(_f, "upd-coadmin", "pw-upd-1", Roles.Admin);
        SettingsTestKit.WithDb(_f, db =>
        {
            db.Profiles.Include(p => p.Entities).First(p => p.User!.Username == "upd-coadmin")
                .Entities.Add(db.Entities.First(e => e.Id == b));
            db.SaveChanges();
        });
        var co = await SettingsTestKit.Client(_f, "upd-coadmin", "pw-upd-1");
        var coStatus = await (await co.GetAsync("/api/updates")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { b }, coStatus.GetProperty("companies").EnumerateArray().Select(c => c.GetProperty("id").GetInt32()));
        Assert.Equal("Updates Co B", coStatus.GetProperty("mine_company").GetString());
        Assert.Equal(HttpStatusCode.Forbidden,
            (await co.PutAsJsonAsync("/api/updates", new { company = a.ToString(), minor_mode = "hold" })).StatusCode);
        (await co.PutAsJsonAsync("/api/updates", new { company = b.ToString(), minor_mode = "hold" })).EnsureSuccessStatusCode();
        (await co.PutAsJsonAsync("/api/updates", new { company = b.ToString(), minor_mode = "auto" })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Only_the_super_admin_sees_every_company()
    {
        Companies();
        var admin = await SettingsTestKit.Client(_f, "admin", "admin123");
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/updates/overview")).StatusCode);

        var sa = await SettingsTestKit.Client(_f, "superadmin", "super123");
        var o = await (await sa.GetAsync("/api/updates/overview")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(o.GetProperty("companies").GetArrayLength() >= 2);
        Assert.Equal(ProductReleases.All.Count, o.GetProperty("releases").GetArrayLength());
        Assert.Equal(JsonValueKind.Array, o.GetProperty("changes").ValueKind);
    }

    /// <summary>Two companies (top-level entities) for these tests, created once.</summary>
    private (int A, int B) Companies() => SettingsTestKit.WithDb(_f, db =>
    {
        int Ensure(string name)
        {
            var e = db.Entities.FirstOrDefault(x => x.Name == name && x.ParentId == null);
            if (e != null) return e.Id;
            var tenant = db.Tenants.First(t => t.Slug == "demo");
            e = new Entity { Name = name, Slug = name.ToLowerInvariant().Replace(' ', '-'), TenantId = tenant.Id, CreatedAt = DateTime.UtcNow };
            db.Entities.Add(e);
            db.SaveChanges();
            return e.Id;
        }
        return (Ensure("Updates Co A"), Ensure("Updates Co B"));
    });
}
