using System.Text;
using System.Text.Json;

namespace ChatBranch.Data.Sidecars;

/// <summary>
/// The voice agent worker (backend/voice_agent, Node) talks to the API over
/// FILES ON PURPOSE (see CLAUDE.md). This is the C# side of that contract —
/// identical paths and JSON shapes to the agent's src/storage.ts:
///   * read  live/{room}.json      — the live transcript (+ done, ended_by, llm_usage)
///   * write inbox/{room}.json     — operator instructions, a list the agent consumes once
///   * read  calls.json            — finished call records
///   * write pronunciation.json    — the TTS lexicon (Settings → Pronunciation)
/// Both processes resolve the directory through CHATBRANCH_DATA_DIR.
/// </summary>
public class VoiceAgentBridge
{
    public VoiceAgentBridge(string dataDir) => DataDir = dataDir;

    /// <summary>The shared data directory (also where media and voices.json live).</summary>
    public string DataDir { get; }

    /// <summary>
    /// backend/ of a source checkout — found by walking up from the binary to the
    /// folder holding both backend/ and backend-csharp/ — or null when the API
    /// runs from a publish directory or container.
    /// </summary>
    public static string? BackendDir { get; } = FindBackendDir();

    private static string? FindBackendDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "backend"))
                && Directory.Exists(Path.Combine(dir.FullName, "backend-csharp")))
                return Path.Combine(dir.FullName, "backend");
        }
        return null;
    }

    /// <summary>
    /// The ONE data directory the API and the voice agent share:
    /// CHATBRANCH_DATA_DIR → the legacy <c>DataDir</c> setting → backend/data of
    /// a source checkout (where the agent and the Baileys session live in dev)
    /// → data/ next to the binary.
    /// </summary>
    public static string ResolveDataDir(string? configured)
    {
        var env = Environment.GetEnvironmentVariable("CHATBRANCH_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env.Trim());
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured.Trim());
        return BackendDir != null
            ? Path.Combine(BackendDir, "data")
            : Path.Combine(AppContext.BaseDirectory, "data");
    }

    /// <summary>The agent's filename rule (storage.safeName): keep [A-Za-z0-9_-] only.</summary>
    public static string SafeName(string room) =>
        new(room.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray());

    private string LivePath(string room) => Path.Combine(DataDir, "live", $"{SafeName(room)}.json");
    private string InboxPath(string room) => Path.Combine(DataDir, "inbox", $"{SafeName(room)}.json");
    private string CallsPath => Path.Combine(DataDir, "calls.json");

    /// <summary>Write via a sibling temp file + rename, so the agent never reads half a file.</summary>
    private static void WriteAtomic(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tmp, contents, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

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

    /// <summary>
    /// Queue an operator instruction ("guide" steers the next reply, "takeover"
    /// is said verbatim). Appended to the pending list, so two instructions sent
    /// within one agent poll both land.
    /// </summary>
    public void PushInstruction(string room, string mode, string text)
    {
        var path = InboxPath(room);
        var items = new List<JsonElement>();
        try
        {
            if (File.Exists(path))
            {
                var existing = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path));
                if (existing.ValueKind == JsonValueKind.Array) items.AddRange(existing.EnumerateArray());
                else if (existing.ValueKind == JsonValueKind.Object) items.Add(existing);
            }
        }
        catch { /* a corrupt inbox is replaced, not fatal */ }
        items.Add(JsonSerializer.SerializeToElement(new { mode, text, ts = DateTime.UtcNow.ToString("o") }));
        WriteAtomic(path, JsonSerializer.Serialize(items));
    }

    /// <summary>Steer the agent to generate a reply from a hint ("guide").</summary>
    public void Guide(string room, string text) => PushInstruction(room, "guide", text);

    /// <summary>Make the agent say something verbatim ("takeover").</summary>
    public void Say(string room, string text) => PushInstruction(room, "takeover", text);

    /// <summary>Every finished call record the agent has appended to calls.json.</summary>
    public List<JsonElement> CallRecords()
    {
        try
        {
            if (File.Exists(CallsPath))
            {
                var doc = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(CallsPath));
                if (doc.ValueKind == JsonValueKind.Array)
                    return doc.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.Object).ToList();
            }
        }
        catch { /* corrupt or mid-rewrite file → no records this time */ }
        return new List<JsonElement>();
    }

    /// <summary>
    /// Mirror the pronunciation lexicon for the agent, which applies every
    /// term → alias respelling to the text its cascade TTS speaks.
    /// </summary>
    public void WritePronunciation(IEnumerable<(string Term, string Alias)> terms) =>
        WriteAtomic(Path.Combine(DataDir, "pronunciation.json"), JsonSerializer.Serialize(new
        {
            terms = terms.Select(t => new { term = t.Term, alias = t.Alias }),
        }));
}
