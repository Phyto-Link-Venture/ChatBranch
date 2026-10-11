using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChatBranch.Api.Tools;

/// <summary>
/// Turns an endpoint's JSON answer into a compact tool result: long strings are
/// clipped, long lists cut (with a <c>…_truncated</c> count beside them), every
/// conversation gains its deep link (<c>href: /chats?thread=channel:key</c>),
/// and anything that looks like a credential is blanked — the endpoints already
/// mask their secrets; this is the second lock on the same door.
/// </summary>
public static class ToolResults
{
    /// <summary>Keys whose STRING values never leave through a tool.</summary>
    private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "passwd", "secret", "token", "access_token", "refresh_token", "id_token", "api_key",
        "apikey", "app_secret", "client_secret", "webhook_secret", "auth_token", "auth_header",
        "authorization", "private_key", "verify_token", "sip_password", "smtp_password", "smtp_pass",
        "imap_password", "signing_secret", "channel_secret", "encoding_aes_key", "session_secret",
        "baileys_secret", "oauth_state_secret", "qr", "qr_code", "pairing_code",
    };

    private static readonly string[] SecretSuffixes =
        { "_secret", "_password", "_api_key", "_token", "_apikey", "_private_key" };

    public static bool IsSecretKey(string key)
    {
        if (SecretKeys.Contains(key)) return true;
        var k = key.ToLowerInvariant();
        return SecretSuffixes.Any(k.EndsWith);
    }

    /// <summary>Already masked by the endpoint ("EAA…1234", "••••") — safe to show.</summary>
    private static bool LooksMasked(string v) =>
        v.Length == 0 || v.Contains('•') || v.Contains('*') || v.Contains('…') || v == "[redacted]";

    /// <summary>How tool results are written for a model: readable (no \uXXXX for
    /// "+", accents, 中文 or emoji) — the text goes to an AI, never into HTML.</summary>
    public static readonly JsonSerializerOptions Wire = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public sealed record Options(int MaxItems = 50, int MaxString = 1500, int MaxChars = 40_000,
        IReadOnlySet<string>? KeepKeys = null);

    public static readonly Options Default = new();

    /// <summary>Compact + redact + link; shrinks further until it fits <see cref="Options.MaxChars"/>.</summary>
    public static JsonNode? Compact(JsonElement body, Options? options = null)
    {
        var o = options ?? Default;
        var steps = new[] { (o.MaxItems, o.MaxString), (20, 400), (8, 200), (3, 120) };
        JsonNode? node = null;
        foreach (var (items, chars) in steps)
        {
            node = Walk(JsonNode.Parse(body.GetRawText()), items, chars, o.KeepKeys);
            if ((node?.ToJsonString().Length ?? 0) <= o.MaxChars) return node;
        }
        return node;
    }

    private static JsonNode? Walk(JsonNode? node, int maxItems, int maxString, IReadOnlySet<string>? keep)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var notes = new List<(string, int)>();
                foreach (var (k, v) in obj.ToList())
                {
                    if (v is JsonValue sv && sv.TryGetValue<string>(out var s) && IsSecretKey(k)
                        && keep?.Contains(k) != true && !LooksMasked(s))
                    {
                        obj[k] = "[redacted]";
                        continue;
                    }
                    if (v is JsonArray arr && arr.Count > maxItems)
                    {
                        notes.Add(($"{k}_truncated", arr.Count));
                        while (arr.Count > maxItems) arr.RemoveAt(arr.Count - 1);
                    }
                    var walked = Walk(v, maxItems, maxString, keep);
                    if (!ReferenceEquals(walked, v)) obj[k] = walked;
                }
                foreach (var (k, total) in notes)
                    if (!obj.ContainsKey(k)) obj[k] = $"showing {maxItems} of {total}";
                if (!obj.ContainsKey("href")
                    && obj["channel"] is JsonValue ch && ch.TryGetValue<string>(out var channel)
                    && obj["key"] is JsonValue kv && kv.TryGetValue<string>(out var key)
                    && channel.Length > 0 && key.Length > 0 && !key.Contains('/'))
                    obj["href"] = ChatLink(channel, key,
                        obj["thread_id"] is JsonValue tv && tv.TryGetValue<int>(out var tid) ? tid : null);
                return obj;
            }
            case JsonArray list:
            {
                while (list.Count > maxItems) list.RemoveAt(list.Count - 1);
                for (var i = 0; i < list.Count; i++)
                {
                    var walked = Walk(list[i], maxItems, maxString, keep);
                    if (!ReferenceEquals(walked, list[i])) list[i] = walked;
                }
                return list;
            }
            case JsonValue value when value.TryGetValue<string>(out var str) && str.Length > maxString:
                return JsonValue.Create(str[..maxString] + "…");
            default:
                return node;
        }
    }

    /// <summary>The dashboard deep link to a conversation.</summary>
    public static string ChatLink(string channel, string key, int? threadId = null) =>
        ChatBranch.Data.Stores.ThreadCompanies.ChatHref(channel, key, threadId);
}
