using System.Text.Json;
using System.Text.RegularExpressions;
using ChatBranch.Data.Ai;

namespace ChatBranch.Api.TestLab;

/// <summary>
/// The Test lab's deterministic language model. Everything around it is the
/// real AI pipeline — the composer, the assistant's prompt (persona, skills,
/// knowledge, contact), the tool loop, reply checks and the AI gate — only the
/// words come from here, so a scenario's outcome never depends on a vendor.
///
/// A scenario queues the reply it needs (<see cref="Say"/>); with nothing
/// queued it answers like the sandbox ("…you said: …"). <see cref="BookingScript"/>
/// plays a customer-service model that uses the calendar tools of the lab's
/// booking skill. <see cref="ILlmClient.IsConfigured"/> is false, so follow-ups
/// use the assistant's fixed sandbox nudges.
/// </summary>
public sealed class LabBrain : ILlmClient
{
    private readonly Queue<string> _replies = new();

    public bool IsConfigured => false;

    /// <summary>Every system prompt the brain saw (the scenario can assert on it).</summary>
    public List<string> Prompts { get; } = new();

    /// <summary>Set by the booking scenarios: the calendar skill's name to drive.</summary>
    public string? BookingSkillName { get; set; }

    /// <summary>The next reply(ies) the "model" writes, in order.</summary>
    public void Say(params string[] replies)
    {
        foreach (var r in replies) _replies.Enqueue(r);
    }

    public Task<LlmResult> CompleteAsync(string system, IReadOnlyList<(string role, string content)> messages,
        CancellationToken ct = default)
    {
        Prompts.Add(system);
        var last = messages.LastOrDefault(m => m.role == "user").content ?? "";
        return Task.FromResult(new LlmResult(Next(last), 0, 0, "testlab", "lab-brain"));
    }

    public Task<LlmChatResult> ChatAsync(string system, IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<LlmTool>? tools = null, string? model = null, double temperature = 0.5,
        CancellationToken ct = default)
    {
        Prompts.Add(system);
        if (BookingSkillName != null && tools != null)
        {
            var scripted = BookingScript(system, messages, tools);
            if (scripted != null) return Task.FromResult(scripted);
        }
        var lastUser = messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";
        return Task.FromResult(new LlmChatResult(Next(lastUser), Array.Empty<LlmToolCall>(), 0, 0,
            "testlab", "lab-brain"));
    }

    private string Next(string lastUser)
    {
        if (_replies.Count > 0) return _replies.Dequeue();
        var said = lastUser.Length <= 120 ? lastUser : lastUser[..120] + "…";
        return string.IsNullOrWhiteSpace(said)
            ? "Hello! How can I help you today?"
            : $"Thanks for your message. (test lab reply) You said: \"{said}\".";
    }

    // ---- the booking journey ------------------------------------------------------

    /// <summary>
    /// A model that books: when the customer asks for an appointment it calls
    /// the skill's availability tool and offers the first two times; when they
    /// pick ("the first one") it checks again, books the first slot and
    /// confirms with the label the tool returned. The tool names come from the
    /// SKILLS block of the real prompt (a second calendar skill in scope would
    /// rename them), so this drives exactly the lab's own skill.
    /// </summary>
    private LlmChatResult? BookingScript(string system, IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<LlmTool> tools)
    {
        var b = BaseName(system, BookingSkillName!, tools);
        if (b == null) return null;
        var lastUser = (messages.LastOrDefault(m => m.Role == "user")?.Content ?? "").ToLowerInvariant();
        var last = messages.LastOrDefault();
        var picking = lastUser.Contains("first one") || lastUser.Contains("book it");

        if (last?.Role != "tool")
            return Calls((b + "_availability", "{}"));

        using var doc = JsonDocument.Parse(last.Content ?? "{}");
        var root = doc.RootElement;
        if (root.TryGetProperty("slots", out var slots) && slots.ValueKind == JsonValueKind.Array)
        {
            var list = slots.EnumerateArray().ToList();
            if (list.Count == 0) return Says("Sorry — there are no free times right now. A teammate will help you.");
            if (!picking)
            {
                var a = Label(list[0]);
                var second = list.Count > 1 ? $" or {Label(list[1])}" : "";
                return Says($"I can book you on {a}{second}. Which one suits you?");
            }
            var start = list[0].TryGetProperty("start", out var s) ? s.GetString() ?? "" : "";
            return Calls((b + "_book", JsonSerializer.Serialize(new { start, note = "Test lab appointment" })));
        }
        if (root.TryGetProperty("status", out var st) && st.GetString() == "reserved")
        {
            var label = root.TryGetProperty("label", out var l) ? l.GetString() : "that time";
            var tz = root.TryGetProperty("timezone", out var z) ? z.GetString() : "";
            return Says($"You're booked for {label}{(string.IsNullOrEmpty(tz) ? "" : $" ({tz})")}. See you then!");
        }
        return Says("Sorry — I couldn't book that. A teammate will confirm a time with you.");
    }

    private static string Label(JsonElement slot) =>
        slot.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() ?? ""
        : slot.TryGetProperty("start", out var s) ? s.GetString() ?? "" : "";

    /// <summary>The tool base name ("calendar", "calendar_2"…) of the named skill, read off the prompt.</summary>
    private static string? BaseName(string system, string skillName, IReadOnlyList<LlmTool> tools)
    {
        var at = system.IndexOf("- " + skillName, StringComparison.Ordinal);
        if (at >= 0)
        {
            var m = Regex.Match(system[at..], @"call (\w+)_availability");
            if (m.Success && tools.Any(t => t.Name == m.Groups[1].Value + "_availability")) return m.Groups[1].Value;
        }
        return tools.Any(t => t.Name == "calendar_availability") ? "calendar" : null;
    }

    private static LlmChatResult Calls(params (string Name, string Args)[] calls) =>
        new("", calls.Select((c, i) => new LlmToolCall($"lab-{Guid.NewGuid():N}-{i}", c.Name, c.Args)).ToList(),
            0, 0, "testlab", "lab-brain");

    private static LlmChatResult Says(string text) =>
        new(text, Array.Empty<LlmToolCall>(), 0, 0, "testlab", "lab-brain");
}
