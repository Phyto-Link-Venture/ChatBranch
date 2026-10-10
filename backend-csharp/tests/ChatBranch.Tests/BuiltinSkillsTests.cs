using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Ai.Skills;
using ChatBranch.Domain;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>A clock tests can pin.</summary>
internal sealed class FixedClock : TimeProvider
{
    private readonly DateTimeOffset _now;
    public FixedClock(DateTimeOffset now) => _now = now;
    public override DateTimeOffset GetUtcNow() => _now;
}

/// <summary>The C# built-ins the AI always has as tools — against a fixed clock.</summary>
public class BuiltinSkillsTests
{
    // Thursday 2026-10-08 14:30 in Kuala Lumpur
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 6, 30, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Kl = BuiltinSkills.ResolveTimeZone("Asia/Kuala_Lumpur")!;

    private static JsonElement Run(string name, string args) =>
        JsonDocument.Parse(BuiltinSkills.Execute(name, args, Now, Kl)).RootElement;

    [Fact]
    public void Current_time_in_the_business_timezone()
    {
        var j = Run("current_time", "{}");
        Assert.Equal("2026-10-08", j.GetProperty("date").GetString());
        Assert.Equal("14:30", j.GetProperty("time").GetString());
        Assert.Equal("Thursday", j.GetProperty("weekday").GetString());
        Assert.Equal("+08:00", j.GetProperty("utc_offset").GetString());

        var london = Run("current_time", "{\"timezone\":\"Europe/London\"}");
        Assert.Equal("07:30", london.GetProperty("time").GetString());
    }

    [Fact]
    public void Current_week_is_iso()
    {
        var j = Run("current_week", "{}");
        Assert.Equal(41, j.GetProperty("iso_week").GetInt32());
        Assert.Equal("2026-10-05", j.GetProperty("week_start").GetString());
        Assert.Equal("2026-10-11", j.GetProperty("week_end").GetString());
        Assert.Equal(4, j.GetProperty("day_of_week_number").GetInt32());

        var newYear = Run("current_week", "{\"date\":\"2027-01-01\"}");
        Assert.Equal(53, newYear.GetProperty("iso_week").GetInt32());
        Assert.Equal(2026, newYear.GetProperty("iso_year").GetInt32());
    }

    [Fact]
    public void Date_add_and_days_between()
    {
        Assert.Equal("2026-10-13", Run("date_add", "{\"amount\":3,\"unit\":\"business_days\"}").GetProperty("result").GetString());
        Assert.Equal("2026-10-22", Run("date_add", "{\"amount\":2,\"unit\":\"weeks\"}").GetProperty("result").GetString());
        Assert.Equal("2026-09-28", Run("date_add", "{\"date\":\"2026-10-08\",\"amount\":-10}").GetProperty("result").GetString());
        Assert.Equal("2026-10-02", Run("date_add", "{\"amount\":-4,\"unit\":\"business_days\"}").GetProperty("result").GetString());

        var between = Run("days_between", "{\"from\":\"2026-10-01\"}");
        Assert.Equal(7, between.GetProperty("days").GetInt32());
        Assert.Equal(5, between.GetProperty("business_days").GetInt32());
        var back = Run("days_between", "{\"from\":\"2026-10-08\",\"to\":\"2026-09-08\"}");
        Assert.Equal(-30, back.GetProperty("days").GetInt32());
        Assert.Equal(-22, back.GetProperty("business_days").GetInt32());
    }

    [Fact]
    public void Convert_timezone()
    {
        var j = Run("convert_timezone", "{\"time\":\"09:00\",\"to\":\"Europe/London\"}");
        Assert.Equal("02:00", j.GetProperty("result_time").GetString()); // BST, UTC+1
        var iso = Run("convert_timezone", "{\"time\":\"2026-12-01T18:00:00Z\",\"to\":\"Asia/Kuala_Lumpur\"}");
        Assert.Equal("2026-12-02", iso.GetProperty("result_date").GetString());
        Assert.Equal("02:00", iso.GetProperty("result_time").GetString());
    }

    [Fact]
    public void Bad_arguments_throw_for_the_tool_loop_to_report()
    {
        Assert.Throws<ArgumentException>(() => BuiltinSkills.Execute("date_add", "{}", Now, Kl));
        Assert.Throws<ArgumentException>(() => BuiltinSkills.Execute("convert_timezone", "{\"to\":\"Mars/Base\"}", Now, Kl));
        Assert.Throws<ArgumentException>(() => BuiltinSkills.Execute("nope", "{}", Now, Kl));
    }

    [Fact]
    public void Org_timezone_comes_from_the_setting_and_skill_tools_use_the_injected_clock()
    {
        using var t = new TestDb();
        var scoping = new ScopingService(t.Db);
        scoping.SetSetting("org_timezone", "Asia/Kuala_Lumpur");
        var skills = new SkillService(t.Db, scoping, new FixedClock(Now));
        Assert.Equal("Asia/Kuala_Lumpur", skills.OrgTimeZone().Id);

        var set = skills.BuildTools(Array.Empty<AgentSkill>());
        Assert.Equal(7, set.Tools.Count); // the five date tools + calculate + request_handover
        var (result, ok) = skills.ExecuteTool(set, new LlmToolCall("c1", "current_time", "{}"),
            skills.Context(null, "whatsapp", ""));
        Assert.True(ok);
        Assert.Equal("14:30", JsonDocument.Parse(result).RootElement.GetProperty("time").GetString());

        var (err, ok2) = skills.ExecuteTool(set, new LlmToolCall("c2", "date_add", "{}"), skills.Context(null, "", ""));
        Assert.False(ok2);
        Assert.StartsWith("error:", err);
    }
}
