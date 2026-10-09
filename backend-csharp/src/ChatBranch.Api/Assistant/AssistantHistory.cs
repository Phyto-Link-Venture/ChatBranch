using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ChatBranch.Api.Tools;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Assistant;

/// <summary>
/// "Ask ChatBranch" history: the per-user chats (<see cref="AssistantConversation"/>)
/// and their turns (<see cref="AssistantMessage"/>). Every reader here takes the
/// CALLER's user id — a conversation is only ever visible to the login that had
/// it. The model's context is rebuilt from the stored turns, so a client can no
/// longer put words in the assistant's mouth by editing its local history.
/// </summary>
public static class AssistantHistory
{
    /// <summary>Kept per user; the least recently used ones go when a new one starts.</summary>
    public const int MaxConversationsPerUser = 100;
    /// <summary>Stored turns the model sees before the new question.</summary>
    public const int ContextTurns = 12;
    public const int MaxTitle = 120;
    private const int AutoTitle = 80;
    private const int MaxStoredText = 8000;
    private const int SnippetChars = 140;

    /// <summary>States an action card can still change from.</summary>
    private static readonly string[] OpenStates = { "pending", "running" };

    /// <summary>The caller's own conversation, or null (missing, or another login's).</summary>
    public static Task<AssistantConversation?> OwnAsync(AppDbContext db, int userId, int id,
        CancellationToken ct = default) =>
        db.AssistantConversations.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId, ct);

    /// <summary>The first question, on one line and shortened — the default title.</summary>
    public static string TitleFrom(string question)
    {
        var t = Regex.Replace(question ?? "", @"\s+", " ").Trim();
        if (t.Length == 0) return "New chat";
        return t.Length <= AutoTitle ? t : t[..(AutoTitle - 1)].TrimEnd() + "…";
    }

    /// <summary>Start a conversation for the user and prune past the cap.</summary>
    public static async Task<AssistantConversation> StartAsync(AppDbContext db, int userId, string question,
        int? entityId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var row = new AssistantConversation
        {
            UserId = userId,
            Title = TitleFrom(question),
            EntityId = entityId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.AssistantConversations.Add(row);
        await db.SaveChangesAsync(ct);
        await PruneAsync(db, userId, ct);
        return row;
    }

    /// <summary>Drop the user's least recently used conversations beyond the cap.</summary>
    public static async Task<int> PruneAsync(AppDbContext db, int userId, CancellationToken ct = default)
    {
        var stale = await db.AssistantConversations.AsNoTracking()
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.UpdatedAt).ThenByDescending(c => c.Id)
            .Skip(MaxConversationsPerUser)
            .Select(c => c.Id)
            .ToListAsync(ct);
        return stale.Count == 0 ? 0 : await DeleteAsync(db, userId, stale, ct);
    }

    /// <summary>
    /// Delete the user's conversations (all of them when <paramref name="ids"/> is
    /// null) with their turns. A write still waiting for confirmation in a deleted
    /// chat is cancelled — nothing the person can no longer see stays runnable.
    /// </summary>
    public static async Task<int> DeleteAsync(AppDbContext db, int userId, IReadOnlyCollection<int>? ids,
        CancellationToken ct = default)
    {
        var q = db.AssistantConversations.Where(c => c.UserId == userId);
        if (ids != null) q = q.Where(c => ids.Contains(c.Id));
        var convIds = await q.Select(c => c.Id).ToListAsync(ct);
        if (convIds.Count == 0) return 0;

        var payloads = await db.AssistantMessages.AsNoTracking()
            .Where(m => convIds.Contains(m.ConversationId) && m.Role == "assistant")
            .Select(m => m.Payload).ToListAsync(ct);
        var actionIds = payloads.SelectMany(p => Cards(Parse(p)))
            .Select(c => (string?)c["id"] ?? "").Where(id => id.Length > 0).Distinct().ToList();
        if (actionIds.Count > 0)
        {
            var now = DateTime.UtcNow;
            await db.AssistantActions
                .Where(a => a.UserId == userId && actionIds.Contains(a.Id) && a.Status == "pending")
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, "cancelled")
                    .SetProperty(a => a.DecidedAt, (DateTime?)now), ct);
        }

        await db.AssistantMessages.Where(m => convIds.Contains(m.ConversationId)).ExecuteDeleteAsync(ct);
        return await db.AssistantConversations.Where(c => convIds.Contains(c.Id)).ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// The model's context: the conversation's last <see cref="ContextTurns"/>
    /// stored turns, oldest first, starting on a user turn. An assistant turn
    /// carries the outcome of the changes it proposed, so the model knows what was
    /// confirmed, cancelled or left to expire.
    /// </summary>
    public static async Task<List<OperatorAssistant.ChatTurn>> ContextAsync(AppDbContext db, int conversationId,
        CancellationToken ct = default)
    {
        var rows = await db.AssistantMessages.AsNoTracking()
            .Where(m => m.ConversationId == conversationId)
            .OrderByDescending(m => m.Id).Take(ContextTurns)
            .ToListAsync(ct);
        rows.Reverse();
        var turns = rows
            .SkipWhile(m => m.Role != "user")
            .Select(m => new OperatorAssistant.ChatTurn(m.Role,
                m.Role == "assistant" ? m.Text + Outcomes(Parse(m.Payload)) : m.Text))
            .ToList();
        return turns;
    }

    private static string Outcomes(JsonObject? payload)
    {
        var notes = new List<string>();
        foreach (var card in Cards(payload))
        {
            var summary = (string?)card["summary"] ?? "";
            var message = (string?)card["message"] ?? "";
            var note = ((string?)card["state"] ?? "pending") switch
            {
                "done" => $"(The user confirmed it and it was done: {summary}.)",
                "failed" => $"(The user confirmed it but it failed: {(message.Length > 0 ? message : summary)})",
                "cancelled" => $"(The user cancelled it: {summary}.)",
                "expired" => $"(It expired without being confirmed: {summary}.)",
                _ => "",
            };
            if (note.Length > 0) notes.Add(note);
        }
        return notes.Count == 0 ? "" : "\n" + string.Join("\n", notes);
    }

    /// <summary>Store one turn and bump the conversation (the history's order).</summary>
    public static async Task<AssistantMessage> AppendAsync(AppDbContext db, AssistantConversation convo, string role,
        string text, JsonNode? payload, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var row = new AssistantMessage
        {
            ConversationId = convo.Id,
            Role = role,
            Text = text.Length <= MaxStoredText ? text : text[..MaxStoredText],
            Payload = payload?.ToJsonString() ?? "{}",
            CreatedAt = now,
        };
        db.AssistantMessages.Add(row);
        convo.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return row;
    }

    /// <summary>What the dock needs to re-render an assistant turn (also the chat's response fields).</summary>
    public static JsonObject ReplyPayload(OperatorAssistant.Reply reply) =>
        (JsonObject)JsonSerializer.SerializeToNode(new
        {
            steps = reply.Steps.Select(s => new { tool = s.Tool, title = s.Title, ok = s.Ok, summary = s.Summary }),
            pending_actions = reply.PendingActions.Select(p => new
            {
                id = p.Id, tool = p.Tool, title = p.Title, args = p.Args, summary = p.Summary,
                expires_at = p.ExpiresAt, state = "pending",
            }),
            navigation = reply.Navigation.Select(l => new { label = l.Label, href = l.Href }),
            mode = reply.Mode,
        })!;

    /// <summary>
    /// Record a confirm/cancel/expiry on the stored action card (the user's own
    /// conversations only), adding any navigation the run produced. Never throws:
    /// the history must not fail the action it describes.
    /// </summary>
    public static async Task MarkActionAsync(AppDbContext db, int userId, string actionId, string state,
        string? message = null, IEnumerable<ToolLink>? navigation = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(actionId)) return;
        try
        {
            var rows = await db.AssistantMessages
                .Include(m => m.Conversation)
                .Where(m => m.Role == "assistant" && m.Conversation!.UserId == userId
                            && m.Payload.Contains(actionId))
                .ToListAsync(ct);
            var links = navigation?.ToList() ?? new List<ToolLink>();
            var now = DateTime.UtcNow;
            foreach (var m in rows)
            {
                var payload = Parse(m.Payload);
                var card = Cards(payload).FirstOrDefault(c => (string?)c["id"] == actionId);
                if (payload == null || card == null) continue;
                card["state"] = state;
                if (!string.IsNullOrEmpty(message)) card["message"] = message;
                if (links.Count > 0)
                {
                    if (payload["navigation"] is not JsonArray nav)
                        payload["navigation"] = nav = new JsonArray();
                    var have = nav.OfType<JsonObject>().Select(n => (string?)n["href"]).ToHashSet();
                    foreach (var l in links.Where(l => have.Add(l.Href)))
                        nav.Add(new JsonObject { ["label"] = l.Label, ["href"] = l.Href });
                }
                m.Payload = payload.ToJsonString();
                m.Conversation!.UpdatedAt = now;
            }
            if (rows.Count > 0) await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { /* best effort */ }
    }

    /// <summary>
    /// The turns as the dock renders them. A card stored as still pending reads
    /// its live status from the action queue (decided elsewhere, or expired).
    /// </summary>
    public static async Task<List<object>> RenderAsync(AppDbContext db, int userId, int conversationId,
        CancellationToken ct = default)
    {
        var rows = await db.AssistantMessages.AsNoTracking()
            .Where(m => m.ConversationId == conversationId)
            .OrderBy(m => m.Id)
            .ToListAsync(ct);
        var parsed = rows.Select(m => (Row: m, Payload: Parse(m.Payload) ?? new JsonObject())).ToList();

        var open = parsed.SelectMany(p => Cards(p.Payload))
            .Where(c => OpenStates.Contains((string?)c["state"] ?? "pending"))
            .ToList();
        if (open.Count > 0)
        {
            var ids = open.Select(c => (string?)c["id"] ?? "").Distinct().ToList();
            var live = await db.AssistantActions.AsNoTracking()
                .Where(a => a.UserId == userId && ids.Contains(a.Id))
                .Select(a => new { a.Id, a.Status, a.ExpiresAt })
                .ToDictionaryAsync(a => a.Id, ct);
            var now = DateTime.UtcNow;
            foreach (var card in open)
            {
                if (!live.TryGetValue((string?)card["id"] ?? "", out var a))
                {
                    card["state"] = "expired"; // the queue no longer knows it
                    continue;
                }
                card["state"] = a.Status == "pending" && a.ExpiresAt <= now ? "expired" : a.Status;
            }
        }

        return parsed.Select(p => (object)new
        {
            id = p.Row.Id,
            role = p.Row.Role,
            text = p.Row.Text,
            payload = p.Payload,
            created_at = Iso(p.Row.CreatedAt),
        }).ToList();
    }

    /// <summary>A one-line preview of a turn for the history list.</summary>
    public static string Snippet(string? text)
    {
        var t = Regex.Replace(text ?? "", @"\s+", " ").Trim();
        return t.Length <= SnippetChars ? t : t[..(SnippetChars - 1)].TrimEnd() + "…";
    }

    public static string Iso(DateTime dt) =>
        DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");

    // ---- payload helpers ------------------------------------------------------

    private static JsonObject? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonNode.Parse(json) as JsonObject; }
        catch { return null; }
    }

    private static IEnumerable<JsonObject> Cards(JsonObject? payload) =>
        payload?["pending_actions"] is JsonArray arr ? arr.OfType<JsonObject>() : Enumerable.Empty<JsonObject>();
}
