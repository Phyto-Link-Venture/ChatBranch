using System.Text.Json;

namespace ChatBranch.Data.Sidecars;

/// <summary>
/// The Django-free voice agent talks over FILES ON PURPOSE (see CLAUDE.md). This
/// is the C# side of that contract — identical paths and JSON shapes to
/// voice_agent/storage.py, so the existing Python agent process needs no change:
///   * read  data/live/{room}.json   — the live transcript
///   * write data/inbox/{room}.json  — operator instructions (consume-once)
/// No LiveKit SDK is needed for these paths (same reason wa_calls imports
/// storage directly rather than calls.lk).
/// </summary>
public class VoiceAgentBridge
{
    private readonly string _dataDir;

    public VoiceAgentBridge(string dataDir) => _dataDir = dataDir;

    private string LivePath(string room) => Path.Combine(_dataDir, "live", $"{room}.json");
    private string InboxPath(string room) => Path.Combine(_dataDir, "inbox", $"{room}.json");

    /// <summary>The live transcript for a room, or an empty envelope if none yet.</summary>
    public JsonElement LiveTranscript(string room)
    {
        try
        {
            var path = LivePath(room);
            if (File.Exists(path))
                return JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path));
        }
        catch { }
        using var doc = JsonDocument.Parse("""{"room":"","turns":[],"status":"idle"}""");
        return doc.RootElement.Clone();
    }

    /// <summary>Push an operator instruction into the consume-once inbox.</summary>
    public void PushInstruction(string room, string kind, string text)
    {
        var dir = Path.Combine(_dataDir, "inbox");
        Directory.CreateDirectory(dir);
        var payload = new { kind, text, ts = DateTime.UtcNow.ToString("o") };
        File.WriteAllText(InboxPath(room), JsonSerializer.Serialize(payload));
    }

    /// <summary>Steer the agent to generate a reply from a hint ("guide").</summary>
    public void Guide(string room, string text) => PushInstruction(room, "guide", text);

    /// <summary>Make the agent say something verbatim ("takeover").</summary>
    public void Say(string room, string text) => PushInstruction(room, "takeover", text);
}
