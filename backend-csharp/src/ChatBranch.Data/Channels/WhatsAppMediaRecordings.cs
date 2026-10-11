using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChatBranch.Data.Sidecars;

namespace ChatBranch.Data.Channels;

/// <summary>
/// Call recordings on disk (port of <c>wa_calls.save_call_transcript</c> +
/// the recordings views). Two writers, one reader:
///
///   * the Baileys gateway records each WhatsApp call to
///     <c>{data}/whatsapp/calls/recordings/{wa_id}-{ms}.wav</c> (stereo, L=contact
///     R=agent) and names it in its <c>ended</c> call-event; the API then writes
///     the paired <c>{wa_id}-{ms}.json</c> (turns + who started / ended the call),
///     backfilled from the agent's live file until the agent marks it done;
///   * the voice agent records every call it runs to <c>{data}/recordings/{room}.wav</c>
///     — the fallback audio for a WhatsApp call the gateway did not record, and
///     the audio of SIP calls (<c>/api/calls/recording/{call_id}</c>).
///
/// Every name that reaches the file system is validated against
/// <c>[A-Za-z0-9_-]+.wav</c> and resolved inside one of the two folders, so a
/// request can never walk out of them.
/// </summary>
public sealed class CallRecordings
{
    private static readonly Regex NameRe = new(@"^[A-Za-z0-9_-]{1,160}\.wav$", RegexOptions.Compiled);
    private static readonly Regex GatewayStemRe = new(@"^(\d{5,20})-(\d{10,16})$", RegexOptions.Compiled);

    public CallRecordings(string dataDir) => DataDir = dataDir;

    public string DataDir { get; }
    public string GatewayDir => Path.Combine(DataDir, "whatsapp", "calls", "recordings");
    public string AgentDir => Path.Combine(DataDir, "recordings");

    /// <summary>The wa_id a gateway recording belongs to ({wa_id}-{ms}.wav), else null.</summary>
    public static string? WaIdOf(string name)
    {
        var m = GatewayStemRe.Match(Path.GetFileNameWithoutExtension(name ?? ""));
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string? Inside(string dir, string name)
    {
        if (!NameRe.IsMatch(name ?? "")) return null;
        var root = Path.GetFullPath(dir);
        var full = Path.GetFullPath(Path.Combine(root, name!));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return null;
        return File.Exists(full) ? full : null;
    }

    /// <summary>A recording by file name: the gateway's folder first, then the agent's.</summary>
    public string? Resolve(string name) => Inside(GatewayDir, name) ?? Inside(AgentDir, name);

    /// <summary>The voice agent's recording of one call (its room = the call id).</summary>
    public string? ResolveAgentCall(string callId)
    {
        var safe = (callId ?? "").Replace('.', '_');
        safe = new string(safe.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        return safe.Length == 0 ? null : Inside(AgentDir, $"{safe}.wav");
    }

    /// <summary>The room a paired transcript names for this recording, if any.</summary>
    public string? RoomOf(string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name ?? "");
        var json = ReadJson(Path.Combine(GatewayDir, $"{stem}.json"));
        return json != null && json.TryGetValue("room", out var r) && r is JsonElement { ValueKind: JsonValueKind.String } s
            ? s.GetString() : null;
    }

    /// <summary>
    /// Persist the paired transcript for a call that just ended. <paramref name="recording"/>
    /// is the gateway's WAV name (null when it did not record — the agent's
    /// <c>{room}.wav</c> is then the audio). Best-effort; never throws.
    /// </summary>
    public string? SaveEnded(string waId, string? recording, string? room, long? recStartedAt, string reason,
        string initiatedBy, string account, VoiceAgentBridge? bridge, int? threadId = null, int? accountId = null)
    {
        try
        {
            string stem;
            if (!string.IsNullOrEmpty(recording) && NameRe.IsMatch(recording))
                stem = Path.GetFileNameWithoutExtension(recording);
            else
                stem = $"{new string((waId ?? "").Where(char.IsAsciiDigit).ToArray())}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            if (!GatewayStemRe.IsMatch(stem)) return null;

            var doc = new Dictionary<string, object?>
            {
                ["wa_id"] = waId,
                ["room"] = room,
                ["recording"] = string.IsNullOrEmpty(recording) ? null : recording,
                ["reason"] = reason,
                ["ended_at"] = DateTime.UtcNow.ToString("o"),
                ["turns"] = new List<object>(),
                ["final"] = false,
                ["meta"] = new Dictionary<string, object?>
                {
                    ["initiated_by"] = initiatedBy,
                    ["account"] = account,
                    ["rec_started_at"] = recStartedAt,
                },
                // the conversation the call belongs to: one customer has one PER COMPANY,
                // so the wa_id in the file name alone does not say whose call it was
                ["thread_id"] = threadId,
                ["account_id"] = accountId,
            };
            Backfill(doc, bridge);
            var path = Path.Combine(GatewayDir, $"{stem}.json");
            WriteAtomic(path, JsonSerializer.Serialize(doc));
            return path;
        }
        catch { return null; }
    }

    /// <summary>
    /// The /chats "Calls" section for one WhatsApp conversation: one entry per call
    /// — audio url + transcript + who started / ended it — newest first. Entries
    /// whose transcript is not final yet are backfilled from the agent's live file
    /// (it writes <c>ended_by</c> only after its summary) and re-saved.
    /// </summary>
    /// <param name="threadId">The conversation asking: only ITS calls are listed (the paired
    /// transcript's <c>thread_id</c>; an older pair without one belongs to
    /// <paramref name="legacyOwnerId"/>, the wa_id's original conversation).</param>
    public List<Dictionary<string, object?>> ListFor(string waId, VoiceAgentBridge? bridge, int? threadId = null,
        int? legacyOwnerId = null)
    {
        var outv = new List<Dictionary<string, object?>>();
        var digits = new string((waId ?? "").Where(char.IsAsciiDigit).ToArray());
        if (digits.Length == 0 || !Directory.Exists(GatewayDir)) return outv;
        var stems = Directory.EnumerateFiles(GatewayDir, $"{digits}-*.*")
            .Select(Path.GetFileName)
            .Where(f => f != null && (f.EndsWith(".wav") || f.EndsWith(".json")))
            .Select(f => Path.GetFileNameWithoutExtension(f!))
            .Where(s => GatewayStemRe.Match(s) is { Success: true } m && m.Groups[1].Value == digits)
            .Distinct();
        foreach (var stem in stems)
        {
            var jsonPath = Path.Combine(GatewayDir, $"{stem}.json");
            var doc = ReadJson(jsonPath);
            if (threadId is int askedFor && OwnerOf(doc, legacyOwnerId) != askedFor) continue;
            if (doc != null && !(doc.TryGetValue("final", out var f) && f is JsonElement { ValueKind: JsonValueKind.True }))
            {
                var mutable = doc.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
                if (Backfill(mutable, bridge))
                {
                    try { WriteAtomic(jsonPath, JsonSerializer.Serialize(mutable)); } catch { }
                    doc = ReadJson(jsonPath) ?? doc;
                }
            }

            // audio: the gateway's own WAV, else the agent's recording of the same room
            string? name = null, full = null;
            var gw = Inside(GatewayDir, $"{stem}.wav");
            if (gw != null) { name = $"{stem}.wav"; full = gw; }
            else if (doc != null && doc.TryGetValue("room", out var r) && r is JsonElement { ValueKind: JsonValueKind.String } rs
                     && VoiceAgentBridge.SafeName(rs.GetString() ?? "") is { Length: > 0 } room
                     && Inside(AgentDir, $"{room}.wav") is string agentWav)
            { name = $"{room}.wav"; full = agentWav; }
            if (name == null || full == null) continue; // no audio anywhere: nothing to play

            var ms = long.Parse(GatewayStemRe.Match(stem).Groups[2].Value);
            var created = ms > 1_000_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
                : File.GetLastWriteTimeUtc(full);
            var entry = new Dictionary<string, object?>
            {
                ["name"] = name,
                ["url"] = $"/api/whatsapp/calls/recording/{name}",
                ["created"] = DateTime.SpecifyKind(created, DateTimeKind.Utc).ToString("o"),
                ["size"] = new FileInfo(full).Length,
            };
            if (doc != null)
            {
                if (doc.TryGetValue("turns", out var turns) && turns.ValueKind == JsonValueKind.Array)
                    entry["transcript"] = turns;
                if (doc.TryGetValue("meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
                    entry["meta"] = meta;
            }
            outv.Add(entry);
        }
        return outv.OrderByDescending(e => (string?)e["created"], StringComparer.Ordinal).ToList();
    }

    /// <summary>The conversation a gateway recording belongs to: its pair's <c>thread_id</c>, else
    /// (a pair written before calls were filed per conversation, or none) the wa_id's original one.</summary>
    public int? ThreadOf(string name, int? legacyOwnerId)
    {
        var stem = Path.GetFileNameWithoutExtension(name ?? "");
        return OwnerOf(ReadJson(Path.Combine(GatewayDir, $"{stem}.json")), legacyOwnerId);
    }

    private static int? OwnerOf(Dictionary<string, JsonElement>? doc, int? legacyOwnerId) =>
        doc != null && doc.TryGetValue("thread_id", out var t) && t.ValueKind == JsonValueKind.Number
        && t.TryGetInt32(out var id) ? id : legacyOwnerId;

    /// <summary>Pull turns + ended_by from the agent's live/{room}.json. True when something changed.</summary>
    private static bool Backfill(Dictionary<string, object?> doc, VoiceAgentBridge? bridge)
    {
        if (bridge == null) return false;
        var room = doc.TryGetValue("room", out var r) ? r switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
            _ => null,
        } : null;
        if (string.IsNullOrEmpty(room)) return false;
        var live = bridge.LiveTranscript(room);
        if (live.ValueKind != JsonValueKind.Object) return false;
        var changed = false;
        if (live.TryGetProperty("turns", out var turns) && turns.ValueKind == JsonValueKind.Array
            && turns.GetArrayLength() > 0)
        {
            doc["turns"] = turns.Clone();
            changed = true;
        }
        var meta = doc.TryGetValue("meta", out var m) ? m switch
        {
            Dictionary<string, object?> d => d,
            JsonElement { ValueKind: JsonValueKind.Object } e =>
                e.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone()),
            _ => new Dictionary<string, object?>(),
        } : new Dictionary<string, object?>();
        if (MetaGraph.Str(live, "ended_by") is { Length: > 0 } endedBy)
        {
            meta["ended_by"] = endedBy;
            doc["meta"] = meta;
            changed = true;
        }
        if (live.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True)
        {
            doc["final"] = true;
            changed = true;
        }
        return changed;
    }

    private static Dictionary<string, JsonElement>? ReadJson(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var doc = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path));
            return doc.ValueKind == JsonValueKind.Object
                ? doc.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
                : null;
        }
        catch { return null; }
    }

    private static void WriteAtomic(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tmp, contents, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}
