using System.Text.Json;

namespace ChatBranch.Data.Ai;

/// <summary>
/// Makes a failing AI provider VISIBLE. When every provider of a call fails the
/// reply path still degrades to the sandbox answer (a chat must never break), but
/// the failure is kept in Setting <c>llm_last_error</c> ({at, error, count}) — shown
/// as a red banner on Settings → AI keys &amp; models — and the organization's admins
/// get ONE collapsing notification (RefKey <c>llm_failure</c>). The next successful
/// call clears it. Writes are throttled (≤ one per 60 s) and never throw.
/// </summary>
public static class LlmHealth
{
    public const string Key = "llm_last_error";
    // per database (tests run many side by side): failing? + when we last wrote
    private static readonly Dictionary<string, (bool Failing, DateTime LastWrite)> State = new();
    private static readonly object Gate = new();

    private static string DbKey(ScopingService sc)
    {
        try { return Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.GetConnectionString(sc.Db.Database) ?? ""; }
        catch { return ""; }
    }

    public static void Failed(ScopingService sc, string error)
    {
        try
        {
            var now = DateTime.UtcNow;
            var key = DbKey(sc);
            lock (Gate)
            {
                if (State.TryGetValue(key, out var st) && st.Failing && (now - st.LastWrite).TotalSeconds < 60) return;
                State[key] = (true, now);
            }
            var detail = string.IsNullOrWhiteSpace(error) ? "the AI provider did not answer" : error;
            var prev = Read(sc);
            var count = (prev?.Count ?? 0) + 1;
            sc.SetSetting(Key, JsonSerializer.Serialize(new Entry(now, detail, count)));
            NotificationService.Notify(sc.Db, NotificationCategories.System, "llm_failure",
                "AI replies are failing",
                $"The AI provider refused the request, so customers got the built-in fallback reply. {detail}",
                "/settings/ai-providers", NotificationAudience.Admins(),
                new NotifyOptions { RefKey = "llm_failure" });
        }
        catch { /* visibility must never break the reply path */ }
    }

    public static void Succeeded(ScopingService sc)
    {
        var key = DbKey(sc);
        lock (Gate)
        {
            if (State.TryGetValue(key, out var st) && !st.Failing) return;
            State[key] = (false, DateTime.MinValue);
        }
        try
        {
            if (Read(sc) != null) sc.SetSetting(Key, "");
        }
        catch { /* best effort */ }
    }

    public sealed record Entry(DateTime At, string Error, int Count);

    public static Entry? Read(ScopingService sc)
    {
        try
        {
            var raw = sc.SettingValue(Key, null);
            if (string.IsNullOrWhiteSpace(raw)) return null;
            return JsonSerializer.Deserialize<Entry>(raw);
        }
        catch { return null; }
    }

    /// <summary>Tests: forget the in-process state.</summary>
    public static void Reset() { lock (Gate) State.Clear(); }
}
