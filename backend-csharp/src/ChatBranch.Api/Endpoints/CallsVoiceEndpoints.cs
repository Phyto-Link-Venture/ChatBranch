using System.Text.Json;
using System.Text.Json.Nodes;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Sidecars;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// Outbound AI voice calls and the voice-tooling endpoints — the port of
/// <c>backend/calls/views.py</c> (call_logs, dial, live, summary, say, hangup,
/// escalate, voice_preview, voice_clone, voice_import, voice_detail).
///
/// Wire format is snake_case, key for key with the Django views, because the
/// frontend parses those exact names (app/call-logs/page.tsx, lib/AppContext.tsx,
/// lib/voice.ts). Responses are built inline rather than through DTOs so the
/// shape is readable next to the view it mirrors.
///
/// HOW A CALL IS PLACED
/// ---------------------
/// The voice agent is a separate process (backend/voice_agent, Node) that talks
/// to this API over FILES (data/live/{room}.json in, data/inbox/{room}.json out,
/// data/calls.json at the end — see <see cref="VoiceAgentBridge"/>). Placing and
/// dropping a call is the LiveKit control plane, driven through
/// <see cref="LiveKitClient"/>: dispatch the agent with its metadata
/// (<see cref="VoiceDispatch"/>), add the SIP participant, delete the room to
/// hang up. With no LIVEKIT_* / SIP_OUTBOUND_TRUNK_ID configured those answer a
/// 503 whose `detail` tells the operator what to set — never a fake call. The
/// ElevenLabs/OpenAI TTS paths have no provider client here, so
/// preview/clone/import answer 501 with a `detail` the UI shows to the operator.
///
/// Two rules hold everywhere: never a 500 (every failure is a shaped JSON body),
/// and every `{room}` is validated before it reaches the file bridge — the room
/// id is a path segment, and Path.Combine would happily follow "../" out of the
/// data directory.
/// </summary>
public static class CallsVoiceEndpoints
{
    // calls/views.VALID_PERSONAS, verbatim.
    private static readonly string[] ValidPersonas = { "advisor", "negotiator", "counsellor", "officer" };

    // The client-facing voice fields, in Django's order (calls/views.voice_*).
    private static readonly string[] VoiceKeys =
        { "id", "name", "gender", "accent", "age", "description", "language", "persona", "provider", "status", "custom" };

    public static void MapCallsVoiceEndpoints(this IEndpointRouteBuilder app)
    {
        MapCallLogs(app);
        MapCallControl(app);
        MapVoiceTooling(app);
    }

    // =========================================================================
    // helpers
    // =========================================================================

    /// <summary>A shaped, never-throwing "this build can't do that" body.</summary>
    private static IResult Unavailable(string detail, int status = 501) =>
        Results.Json(new { ok = false, detail }, statusCode: status);

    private static IResult Bad(string detail) =>
        Results.Json(new { detail }, statusCode: 400);

    private static string Env(string name) =>
        (Environment.GetEnvironmentVariable(name) ?? "").Trim();

    /// <summary>
    /// Room ids are LiveKit room names (call-…); they reach the file bridge as a
    /// filename, so anything that isn't a plain token is rejected rather than
    /// path-joined. Without this a room of "../../appsettings" would read — and
    /// /say would WRITE — outside data/.
    /// </summary>
    private static bool RoomOk(string? room) =>
        !string.IsNullOrWhiteSpace(room) && room.Length <= 128 &&
        room.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') &&
        !room.Contains("..");

    /// <summary>
    /// calls/views._parse_call_transcript — the agent stores the SIP transcript
    /// as newline-joined "role: text" lines; split it back into chat turns.
    /// </summary>
    private static List<Dictionary<string, object?>> ParseTranscript(string? text)
    {
        var turns = new List<Dictionary<string, object?>>();
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var i = line.IndexOf(':');
            turns.Add(i >= 0
                ? new() { ["role"] = line[..i].Trim().ToLowerInvariant(), ["text"] = line[(i + 1)..].Trim() }
                : new() { ["role"] = "user", ["text"] = line });
        }
        return turns;
    }

    /// <summary>Display name for a call row: the CRM name, else the dialled number.</summary>
    private static string CallerLabel(CallRecord r)
    {
        var name = r.Contact?.Name;
        if (string.IsNullOrWhiteSpace(name)) name = r.Name;
        if (string.IsNullOrWhiteSpace(name)) name = r.Phone;
        return string.IsNullOrWhiteSpace(name) ? "Unknown" : name;
    }

    private static string CallTs(CallRecord r) =>
        !string.IsNullOrWhiteSpace(r.EndedAt) ? r.EndedAt : r.StartedAt ?? "";

    // =========================================================================
    // 1. Call history — GET /api/call-logs
    // =========================================================================

    private static void MapCallLogs(IEndpointRouteBuilder app)
    {
        // Port of calls/views.call_logs. Django merges two file stores (the SIP
        // agent's data/calls.json and the WhatsApp recordings dir); the C# build
        // folds the agent's calls.json into the CallRecords table
        // (VoiceCallSync) and serves that. The WhatsApp-call half is best-effort
        // in Django too and has no C# store yet, so this returns the ai-call
        // rows only — the page's "WhatsApp" tab simply counts 0.
        // Auth-gated like Django (the page sends authHeaders()).
        app.MapGet("/api/call-logs", async (AppDbContext db, VoiceAgentBridge bridge,
            ChatBranch.Data.Channels.CallRecordings recordings, ScopingService sc,
            System.Security.Claims.ClaimsPrincipal me) =>
        {
            await VoiceCallSync.SyncAsync(db, bridge); // fold in calls the agent finished since
            // only the calls placed for the caller's own entities (CallScope) — never another company's
            var rows = await CallScope.VisibleAsync(db, sc, me, await db.CallRecords.AsNoTracking()
                .Include(r => r.Contact)
                .OrderByDescending(r => r.Id)
                .Take(500)
                .ToListAsync());

            var logs = rows
                .Select(r => new Dictionary<string, object?>
                {
                    ["id"] = $"sip-{r.CallId}",
                    ["type"] = "ai-call",
                    ["call_id"] = r.CallId,
                    ["contact"] = CallerLabel(r),
                    ["phone"] = r.Phone ?? "",
                    ["ts"] = CallTs(r),
                    ["outcome"] = r.Outcome ?? "",
                    ["summary"] = r.Summary ?? "",
                    // The agent's WAV (data/recordings/{call_id}.wav) when it exists —
                    // streamed by GET /api/calls/recording/{call_id}; else "no audio".
                    ["recording"] = recordings.ResolveAgentCall(r.CallId) != null
                        ? $"/api/calls/recording/{Uri.EscapeDataString(r.CallId)}" : null,
                    ["transcript"] = ParseTranscript(r.Transcript),
                    ["contact_id"] = r.ContactId,
                })
                .OrderByDescending(x => (string?)x["ts"] ?? "", StringComparer.Ordinal)
                .Take(200)
                .ToList();

            return Results.Ok(new { logs });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("call_logs.view");
    }

    // =========================================================================
    // 2. Call control — dial / summary / live / say / hangup / escalate
    // =========================================================================

    public record DialWrite(int? contact_id, string? phone, string? name, string? language,
        string? voiceId, string? voiceProvider, string? script);

    public record SayWrite(string? text, string? mode);

    private static void MapCallControl(IEndpointRouteBuilder app)
    {
        // These six are open like their Django counterparts (calls/views.dial,
        // live, summary, say, hangup, escalate carry no role gate) — and they
        // have to be: AppContext.tsx fetches every one of them with a bare
        // fetch(), no auth headers, so gating them would 401 the live-call UI.
        // They expose no stored data beyond the call the operator is watching.
        // NOTE: with LiveKit + a trunk configured, /dial places real (billed)
        // phone calls for any caller that can reach the API — gate it once the
        // dashboard sends its bearer token on these requests.

        // ---- POST /api/calls/dial ------------------------------------------
        // Validate (contact lookup, do-not-call, E.164, voice pre-flight), then
        // dispatch the agent with the call's metadata and dial over the SIP
        // trunk. AppContext.tsx:322 does `data = await res.json()` BEFORE
        // checking res.ok and shows `data.detail` on the call tile, so every
        // answer is JSON; `room` is null on failure because the tile re-keys to
        // data.room only on a 2xx.
        app.MapPost("/api/calls/dial", async (DialWrite? body, AppDbContext db, ScopingService sc,
            KnowledgeService knowledge, AssistantService assistant, LiveKitClient livekit,
            VoiceAgentBridge bridge, System.Security.Claims.ClaimsPrincipal me, CancellationToken ct) =>
        {
            body ??= new DialWrite(null, null, null, null, null, null, null);

            Contact? contact = null;
            string? phone;
            if (body.contact_id is int contactId)
            {
                // only a contact the caller may see: the dispatch carries its memory and records
                contact = await ContactsEndpoints.ScopeContacts(db.Contacts.AsNoTracking(), me, db, sc)
                    .FirstOrDefaultAsync(c => c.Id == contactId, ct);
                if (contact == null)
                    return Results.Json(new { detail = "Contact not found." }, statusCode: 404);
                if (contact.DoNotCall)
                    return Results.Json(new { detail = "Contact is marked do-not-call." }, statusCode: 409);
                phone = contact.Phone;
            }
            else
            {
                phone = (body.phone ?? "").Trim();
            }

            if (string.IsNullOrEmpty(phone) || !phone.StartsWith('+'))
                return Bad("phone must be E.164 (start with +).");

            // AI usage quotas: an AI voice call is AI spend — the caller's own cap,
            // then the contact's outlet (or any level above it)
            if (QuotaGuard.Refusal(db, sc, me) is { } userQuota) return userQuota;
            // the outlet the call is placed FOR: the contact's entity inside the caller's own
            // scope (a person bound to entities calls for their company, never another's)
            var links = contact == null ? new List<int>()
                : await db.ContactEntities.AsNoTracking().Where(l => l.ContactId == contact.Id)
                    .OrderBy(l => l.Id).Select(l => l.EntityId).ToListAsync(ct);
            var boundTo = ShellScope.BoundEntityIds(db, sc, me);
            int? callEntity = boundTo == null
                ? (links.Count > 0 ? links[0] : null)
                : links.FirstOrDefault(boundTo.Contains) is var mineLink && mineLink != 0
                    ? mineLink : ShellScope.ScopeRoots(db, me).FirstOrDefault();
            if (ChatBranch.Data.Ai.AiQuota.EntityBlock(db, sc, callEntity) is { } entityQuota)
                return Results.Json(new { ok = false, room = (string?)null, status = "quota",
                    detail = ChatBranch.Data.Ai.AiQuota.Reason(entityQuota), quota = true }, statusCode: 429);

            // Voice pre-flight, in Django's order: an unusable provider is a 501
            // BEFORE anything is dialled, so the operator sees the missing key
            // rather than a call that silently has no voice.
            var provider = (body.voiceProvider ?? "").Trim().ToLowerInvariant();
            var voiceId = (body.voiceId ?? "").Trim();
            if (provider == "elevenlabs" && Env("ELEVENLABS_API_KEY").Length == 0)
                return Unavailable("Voice calling not configured (ELEVENLABS_API_KEY unset).");
            if (provider == "ilmu")
            {
                if (Env("ILMU_API_KEY").Length == 0)
                    return Unavailable("ILMU voice not configured (ILMU_API_KEY unset).");
                var resolved = voiceId.Length > 0 ? voiceId : "voice_1";
                if (resolved is not ("voice_1" or "voice_2" or "voice_3"))
                    return Bad("Unknown ILMU voice (voice_1/2/3).");
            }

            if (livekit.Settings.MissingForSip() is string reason)
                return Results.Json(new { ok = false, room = (string?)null, status = "unavailable", detail = reason },
                    statusCode: 503);

            // The call brain and STT engine are the organization-wide settings
            // here (no channel account carries a SIP call).
            var metadata = await VoiceDispatch.BuildAsync(db, knowledge, assistant, sc, bridge.DataDir, contact,
                new VoiceCallOptions(phone, body.name, body.language, provider, voiceId,
                    AccountDelivery.Resolve(null, AccountDelivery.LlmProvider, sc),
                    AccountDelivery.Resolve(null, AccountDelivery.SttProvider, sc), body.script,
                    EntityId: callEntity), ct);
            var room = LiveKitClient.NewRoomName();
            var placed = await livekit.PlaceSipCallAsync(room, phone,
                metadata.GetValueOrDefault("contact_name") as string ?? "contact",
                JsonSerializer.Serialize(metadata), ct);
            if (!placed.Ok)
                return Results.Json(new { ok = false, room = (string?)null, status = "failed", detail = placed.Detail },
                    statusCode: placed.Status == 503 ? 503 : 502);
            return Results.Ok(new { ok = true, room, status = "dialing" });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- GET /api/calls/{room} — the finished call's summary ------------
        // The agent appends the record to calls.json; VoiceCallSync folds it
        // into CallRecords. The file record also fills the fields the entity
        // does not carry (followup_action, objection, sentiment_score); keys
        // match AppContext.tsx's CallSummary type.
        app.MapGet("/api/calls/{room}", async (string room, AppDbContext db, VoiceAgentBridge bridge,
            ScopingService sc, System.Security.Claims.ClaimsPrincipal me) =>
        {
            if (!RoomOk(room)) return Bad("Invalid room id.");
            await VoiceCallSync.SyncAsync(db, bridge);
            var r = await db.CallRecords.AsNoTracking()
                .Include(x => x.Contact)
                .FirstOrDefaultAsync(x => x.CallId == room);
            // another company's call reads as not there (CallScope)
            if (r == null || (await CallScope.VisibleAsync(db, sc, me, new List<CallRecord> { r })).Count == 0)
                return Results.Json(new { detail = "Summary not ready." }, statusCode: 404);
            var file = bridge.CallRecords().LastOrDefault(c =>
                c.TryGetProperty("call_id", out var id) && id.ValueKind == JsonValueKind.String
                && id.GetString() == room);
            string? FromFile(string key) =>
                file.ValueKind == JsonValueKind.Object && file.TryGetProperty(key, out var v)
                && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            return Results.Ok(new Dictionary<string, object?>
            {
                ["room"] = room,
                ["call_id"] = r.CallId,
                ["contact_id"] = r.ContactId,
                ["contact"] = CallerLabel(r),
                ["phone"] = r.Phone ?? "",
                ["started_at"] = r.StartedAt ?? "",
                ["ended_at"] = r.EndedAt ?? "",
                ["summary"] = r.Summary ?? "",
                ["outcome"] = string.IsNullOrWhiteSpace(r.Outcome) ? "unclear" : r.Outcome,
                ["followup_date"] = r.FollowupDate,
                ["needs_human"] = r.NeedsHuman,
                ["followup_action"] = FromFile("followup_action"),
                ["objection"] = FromFile("objection"),
                ["sentiment_score"] = FromFile("sentiment_score"),
                ["transcript"] = ParseTranscript(r.Transcript),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireAnyCapability("chats.view", "call_logs.view");

        // ---- GET /api/calls/{room}/live — the live transcript ---------------
        // The file half of the agent protocol. Mirrors calls/lk.live_transcript,
        // including its belt-and-suspenders rule — a persisted call record means
        // the call ENDED even if the agent never wrote its final done-flag, so
        // AppContext's 1.5s poll can't stick on "Ringing…" forever.
        app.MapGet("/api/calls/{room}/live", async (string room, VoiceAgentBridge bridge, AppDbContext db) =>
        {
            if (!RoomOk(room)) return Bad("Invalid room id.");

            var live = bridge.LiveTranscript(room);
            var outp = new Dictionary<string, object?>();
            if (live.ValueKind == JsonValueKind.Object)
                foreach (var p in live.EnumerateObject())
                    outp[p.Name] = p.Value;

            var doneInFile = outp.TryGetValue("done", out var d)
                             && d is JsonElement de && de.ValueKind == JsonValueKind.True;
            var hasTurns = outp.TryGetValue("turns", out var t)
                           && t is JsonElement te && te.ValueKind == JsonValueKind.Array
                           && te.GetArrayLength() > 0;

            if (doneInFile) await VoiceCallSync.SyncAsync(db, bridge);
            var record = await db.CallRecords.AsNoTracking()
                .FirstOrDefaultAsync(x => x.CallId == room);

            outp["room"] = room;
            outp["done"] = doneInFile || record != null;
            // No live file yet but a finished record exists (or the agent wrote
            // no turns before crashing): serve the persisted transcript so the
            // ended call still shows what was said.
            if (!hasTurns && record != null)
                outp["turns"] = ParseTranscript(record.Transcript);
            else if (!outp.ContainsKey("turns"))
                outp["turns"] = Array.Empty<object>();
            if (!outp.ContainsKey("status") || record != null)
                outp["status"] = record != null ? "ended" : (hasTurns ? "live" : "idle");

            return Results.Ok(outp);
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireAnyCapability("chats.view", "call_logs.view");

        // ---- POST /api/calls/{room}/say — guide / take over -----------------
        // calls/views.say: push the instruction into the agent's consume-once
        // inbox (VoiceAgentBridge.PushInstruction). Nothing consumes it when no
        // agent is running, but the write genuinely happens, so ok:true is
        // honest — `queued` says what was actually done.
        app.MapPost("/api/calls/{room}/say", (string room, SayWrite? body, VoiceAgentBridge bridge) =>
        {
            if (!RoomOk(room)) return Bad("Invalid room id.");
            var text = (body?.text ?? "").Trim();
            if (text.Length == 0) return Bad("text is required.");
            var mode = (body?.mode ?? "").Trim().ToLowerInvariant();
            if (mode is not ("guide" or "takeover")) mode = "guide";

            bridge.PushInstruction(room, mode, text);
            return Results.Ok(new { ok = true, room, mode, queued = true });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- POST /api/calls/{room}/hangup ----------------------------------
        // Deleting the room drops the phone leg and ends the agent's job (which
        // then writes its summary). Without the LiveKit control plane the best
        // this can do is queue a wrap-up instruction a running agent acts on —
        // answered as a 503 so nobody reads it as a confirmed disconnect.
        app.MapPost("/api/calls/{room}/hangup", async (string room, VoiceAgentBridge bridge,
            LiveKitClient livekit, CancellationToken ct) =>
        {
            if (!RoomOk(room)) return Bad("Invalid room id.");
            if (livekit.Settings.MissingForServer() is string reason)
            {
                try
                {
                    bridge.Guide(room, "Politely wrap up and end the call now.");
                }
                catch { /* unwritable data dir — still answer, never 500 */ }
                return Results.Json(new { ok = false, room, queued = "guide", detail = reason }, statusCode: 503);
            }

            try { bridge.MarkOperatorEnd(room); }
            catch { /* provenance only — never block the hang-up on it */ }
            var dropped = await livekit.DeleteRoomAsync(room, ct);
            // 404: the room is already gone — the call has ended either way.
            if (!dropped.Ok && dropped.Status != 404)
                return Results.Json(new { ok = false, room, detail = dropped.Detail }, statusCode: 502);
            return Results.Ok(new { ok = true, room });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- POST /api/calls/{room}/escalate --------------------------------
        // REAL and complete: calls/views.escalate is itself just a guide
        // instruction, so the file bridge covers it fully. Wording kept verbatim
        // from Django — it is what the caller actually hears.
        app.MapPost("/api/calls/{room}/escalate", (string room, VoiceAgentBridge bridge) =>
        {
            if (!RoomOk(room)) return Bad("Invalid room id.");
            bridge.Guide(room,
                "Politely tell the caller that a human agent will follow up with them shortly, then wrap up.");
            return Results.Ok(new { ok = true, room, queued = true });
        }).RequireAuthorization(AuthPolicies.Authenticated);
    }

    // =========================================================================
    // 3. Voice tooling — preview / clone / import / edit / delete
    // =========================================================================

    public record ImportWrite(string? voiceId, string? name, string? gender, string? persona);

    private static string DataDir(IConfiguration config) =>
        config["DataDir"] ?? Path.Combine(AppContext.BaseDirectory, "data");

    private static string VoicesPath(IConfiguration config) =>
        Path.Combine(DataDir(config), "voices.json");

    /// <summary>
    /// The local custom-voice store (data/voices.json) — the same file
    /// VoiceMessagingEndpoints merges into GET /api/voices. Presets live in
    /// code and are deliberately absent here: Django's voice_detail only ever
    /// edits or deletes CUSTOM voices.
    /// </summary>
    private static JsonObject LoadVoicesDoc(IConfiguration config)
    {
        try
        {
            var path = VoicesPath(config);
            if (File.Exists(path) &&
                JsonNode.Parse(File.ReadAllText(path)) is JsonObject doc &&
                doc["voices"] is JsonArray)
                return doc;
        }
        catch { /* a corrupt local file degrades to "no custom voices" */ }
        return new JsonObject { ["voices"] = new JsonArray() };
    }

    private static void SaveVoicesDoc(IConfiguration config, JsonObject doc)
    {
        var path = VoicesPath(config);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static JsonObject? FindCustomVoice(JsonObject doc, string voiceId) =>
        (doc["voices"] as JsonArray)?
            .OfType<JsonObject>()
            .FirstOrDefault(v => (string?)v["id"] == voiceId);

    /// <summary>Django's client_view: the public voice fields that are present.</summary>
    private static Dictionary<string, object?> ClientView(JsonObject v)
    {
        var outv = new Dictionary<string, object?>();
        foreach (var key in VoiceKeys)
        {
            if (v[key] is not JsonValue val) continue;
            // bool first: `custom` is the one non-string field, and a JSON true
            // read as a string would reach the UI as "True".
            if (val.TryGetValue<bool>(out var b)) outv[key] = b;
            else if (val.TryGetValue<string>(out var s)) outv[key] = s;
            else outv[key] = val.ToJsonString().Trim('"');
        }
        return outv;
    }

    private static void MapVoiceTooling(IEndpointRouteBuilder app)
    {
        // lib/voice.ts and AppContext.tsx call these with a bare fetch(); the
        // provider ids stay server-side (voices.json), never in a payload.

        // ---- POST /api/voice/preview ----------------------------------------
        // audio/mpeg on success; {detail} on failure, which lib/voice.ts reads
        // out of the error body and shows verbatim. The voice's own provider
        // speaks it: a clone through ElevenLabs or VoiceStudio, the presets
        // marin / cedar / alloy through OpenAI, the VoiceStudio entry through
        // VoiceStudio. Puck (Gemini realtime) has no standalone speech call.
        app.MapPost("/api/voice/preview", async (Dictionary<string, JsonElement>? body, IConfiguration config,
            ChatBranch.Data.Ai.Voice.SpeechClient speech, CancellationToken ct) =>
        {
            var voiceId = (body.Str("voiceId") ?? "").Trim();
            var text = (body.Str("text") ?? "").Trim();
            if (voiceId.Length == 0 || text.Length == 0)
                return Bad("voiceId and text are required.");
            if (text.Length > 400)  // previews are short persona lines
                return Bad("text too long for preview (max 400 chars).");

            string provider, remote;
            if (voiceId == VoiceDispatch.VoiceStudioDefaultId)
                (provider, remote) = ("voicestudio", ChatBranch.Data.Ai.Voice.SpeechClient.VoiceStudioDefaultVoice);
            else if (voiceId is "marin" or "cedar" or "alloy")
                (provider, remote) = ("openai", voiceId);
            else if (voiceId == "puck")
                return Unavailable("Puck is a Gemini live-call voice — it has no preview.", 400);
            else if (VoiceDispatch.Lookup(DataDir(config), voiceId) is { } hit && hit.RemoteId.Length > 0)
                (provider, remote) = (hit.Provider, hit.RemoteId);
            else
                return Results.Json(new { detail = "Voice not found." }, statusCode: 404);

            if (!speech.TtsReady(provider))
                return Unavailable(provider switch
                {
                    "openai" => "Previewing this voice needs an OpenAI API key (Settings → AI keys & models).",
                    "voicestudio" => "VoiceStudio is not set up (Settings → AI keys & models → VoiceStudio address).",
                    _ => "Previewing this voice needs an ElevenLabs API key (Settings → AI keys & models).",
                }, 503);
            var tts = await speech.SynthesizeWithAsync(provider, text, remote,
                ChatBranch.Data.Ai.Voice.SpeechClient.FileFormat, ct);
            return tts.Ok
                ? Results.File(tts.Audio, "audio/mpeg")
                : Results.Json(new { detail = $"Preview failed: {tts.Detail}" }, statusCode: 502);
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- POST /api/voice/clone ------------------------------------------
        // multipart/form-data (name, language, persona, gender, audio, provider).
        // provider = elevenlabs (instant voice cloning, uses a voice slot) |
        // voicestudio (a profile on the self-hosted VoiceStudio); empty = the
        // first one set up. The clone lands in data/voices.json with the
        // provider's id kept server-side (never in the payload).
        app.MapPost("/api/voice/clone", async (HttpContext ctx, IConfiguration config,
            ChatBranch.Data.Ai.Voice.SpeechClient speech) =>
        {
            if (!ctx.Request.HasFormContentType)
                return Bad("name, audio, and language (en/ms/zh) are required.");

            IFormCollection form;
            try { form = await ctx.Request.ReadFormAsync(); }
            catch { return Bad("Malformed multipart upload."); }

            var name = form["name"].ToString().Trim();
            var language = form["language"].ToString().Trim();
            var audio = form.Files["audio"];
            if (name.Length == 0 || audio == null || language is not ("en" or "ms" or "zh"))
                return Bad("name, audio, and language (en/ms/zh) are required.");
            if (audio.Length > 10 * 1024 * 1024)
                return Bad("audio too large (max 10 MB).");
            if (!(audio.ContentType ?? "").StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
                && !(audio.ContentType ?? "").StartsWith("video/webm", StringComparison.OrdinalIgnoreCase))
                return Bad("audio must be an audio file.");
            var persona = form["persona"].ToString().Trim();
            if (!ValidPersonas.Contains(persona)) persona = ValidPersonas[0];
            var gender = form["gender"].ToString().Trim();

            var ready = speech.CloneProviders();
            var provider = form["provider"].ToString().Trim().ToLowerInvariant();
            if (provider.Length == 0) provider = ready.FirstOrDefault() ?? "";
            if (provider.Length == 0)
                return Unavailable("Voice cloning needs ElevenLabs (an API key) or VoiceStudio (its address) — "
                    + "set one in Settings → AI keys & models.", 503);
            if (provider is not ("elevenlabs" or "voicestudio"))
                return Bad("provider must be elevenlabs or voicestudio.");
            if (!ready.Contains(provider))
                return Unavailable(provider == "voicestudio"
                    ? "VoiceStudio is not set up (Settings → AI keys & models → VoiceStudio address)."
                    : "No ElevenLabs API key (Settings → AI keys & models).", 503);

            byte[] bytes;
            using (var ms = new MemoryStream())
            {
                await audio.CopyToAsync(ms, ctx.RequestAborted);
                bytes = ms.ToArray();
            }
            var made = await speech.CloneVoiceAsync(provider, name, bytes, audio.FileName, audio.ContentType ?? "",
                language, $"ChatBranch voice clone ({persona})", ctx.RequestAborted);
            if (!made.Ok)
                return Results.Json(new { detail = $"Clone failed: {made.Detail}" }, statusCode: 502);

            var record = new JsonObject
            {
                ["id"] = $"custom-{Guid.NewGuid():N}"[..19],
                ["name"] = name,
                ["language"] = language,
                ["persona"] = persona,
                ["provider"] = provider,
                ["status"] = "ready",
                ["custom"] = true,
                ["tag"] = "Cloned",
                ["gender"] = "",
                ["accent"] = provider == "voicestudio" ? "VoiceStudio clone" : "ElevenLabs clone",
                [provider == "voicestudio" ? "remoteVoiceId" : "elevenVoiceId"] = made.RemoteId,
                ["createdAt"] = DateTime.UtcNow.ToString("o"),
            };
            if (gender.Length > 0) record["gender"] = gender;   // else the "" above
            var doc = LoadVoicesDoc(config);
            ((JsonArray)doc["voices"]!).Add(record);
            try { SaveVoicesDoc(config, doc); }
            catch (Exception e)
            {
                // don't leave a paid-for clone nobody can see or delete
                await speech.DeleteRemoteVoiceAsync(provider, made.RemoteId);
                return Unavailable($"Could not write the voice registry: {e.Message}", 503);
            }
            return Results.Ok(new { voice = ClientView(record) });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("settings.ai.manage")
          .OrgWideAction();   // the organization's voice library (Voice & calls)

        // ---- POST /api/voice/import -----------------------------------------
        // Import copies an existing ElevenLabs voice into the local registry.
        // AppContext.tsx reads {voice} on success and throws the status otherwise.
        app.MapPost("/api/voice/import", async (ImportWrite? body, IConfiguration config,
            ChatBranch.Data.Ai.Voice.SpeechClient speech, CancellationToken ct) =>
        {
            var elId = (body?.voiceId ?? "").Trim();
            if (elId.Length == 0) return Bad("voiceId is required.");

            // Already imported? The local record, without touching ElevenLabs.
            var doc = LoadVoicesDoc(config);
            var existing = (doc["voices"] as JsonArray)?
                .OfType<JsonObject>()
                .FirstOrDefault(v => (string?)v["elevenVoiceId"] == elId);
            if (existing != null)
                return Results.Ok(new { voice = ClientView(existing) });

            var found = await speech.ElevenVoiceAsync(elId, ct);
            if (!found.Ok)
                return Results.Json(new { detail = $"Import failed: {found.Detail}" },
                    statusCode: speech.ElevenLabsKey.Length == 0 ? 503 : 502);
            var persona = (body?.persona ?? "").Trim();
            var record = new JsonObject
            {
                ["id"] = $"custom-{Guid.NewGuid():N}"[..19],
                ["name"] = (body?.name ?? "").Trim() is { Length: > 0 } n ? n : (found.Name.Length > 0 ? found.Name : elId),
                ["language"] = "unknown",
                ["persona"] = ValidPersonas.Contains(persona) ? persona : ValidPersonas[0],
                ["provider"] = "elevenlabs",
                ["status"] = "ready",
                ["custom"] = true,
                ["tag"] = "Imported",
                ["gender"] = "",
                ["accent"] = "ElevenLabs",
                ["elevenVoiceId"] = elId,
                ["imported"] = true,
                ["createdAt"] = DateTime.UtcNow.ToString("o"),
            };
            if ((body?.gender ?? "").Trim() is { Length: > 0 } g) record["gender"] = g;
            ((JsonArray)doc["voices"]!).Add(record);
            try { SaveVoicesDoc(config, doc); }
            catch (Exception e) { return Unavailable($"Could not write the voice registry: {e.Message}", 503); }
            return Results.Ok(new { voice = ClientView(record) });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("settings.ai.manage")
          .OrgWideAction();   // the organization's voice library (Voice & calls)

        // ---- PATCH /api/voice/{voiceId} --------------------------------------
        // REAL for locally-stored custom voices: renaming, re-labelling and
        // re-personaing are pure local-registry edits, no provider call. Presets
        // are not in the store, so editing one 404s exactly as in Django.
        // AppContext.tsx:259 replaces its voice with the returned {voice}.
        app.MapMethods("/api/voice/{voiceId}", new[] { "PATCH" },
            (string voiceId, Dictionary<string, JsonElement>? body, IConfiguration config) =>
        {
            var doc = LoadVoicesDoc(config);
            var voice = FindCustomVoice(doc, voiceId);
            if (voice == null)
                return Results.Json(new { detail = "Voice not found (presets can't be edited or deleted)." },
                    statusCode: 404);

            if (body != null && body.ContainsKey("name"))
            {
                var name = (body.Str("name") ?? "").Trim();
                if (name.Length == 0) return Bad("name cannot be empty.");
                voice["name"] = name;
            }
            if (body != null && body.ContainsKey("language"))
            {
                var language = (body.Str("language") ?? "").Trim();
                if (language is not ("en" or "ms" or "zh" or "unknown"))
                    return Bad("language must be one of en/ms/zh/unknown.");
                voice["language"] = language;
            }
            if (body != null && body.ContainsKey("gender"))
            {
                var gender = (body.Str("gender") ?? "").Trim();
                if (gender.Length > 0) voice["gender"] = gender;   // blank keeps the stored value
            }
            if (body != null && body.ContainsKey("accent"))
            {
                var accent = (body.Str("accent") ?? "").Trim();
                if (accent.Length > 0) voice["accent"] = accent;
            }
            if (body != null && body.ContainsKey("age"))
                voice["age"] = (body.Str("age") ?? "").Trim();
            if (body != null && body.ContainsKey("description"))
                voice["description"] = (body.Str("description") ?? "").Trim();
            if (body != null && body.ContainsKey("persona"))
            {
                var persona = (body.Str("persona") ?? "").Trim();
                if (!ValidPersonas.Contains(persona))
                    return Bad($"persona must be one of {string.Join(", ", ValidPersonas)}.");
                voice["persona"] = persona;
            }

            try { SaveVoicesDoc(config, doc); }
            catch (Exception e) { return Unavailable($"Could not write the voice registry: {e.Message}", 503); }

            return Results.Ok(new { voice = ClientView(voice) });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("settings.ai.manage")
          .OrgWideAction();   // the organization's voice library (Voice & calls)

        // ---- DELETE /api/voice/{voiceId} -------------------------------------
        // Deleting a cloned voice deletes it at its provider too (ElevenLabs /
        // VoiceStudio), or the account keeps an orphaned clone; when that fails
        // the local record is kept. A local-only custom voice is just removed. AppContext.tsx:226 drops
        // the voice from its list on any 2xx.
        app.MapDelete("/api/voice/{voiceId}", async (string voiceId, IConfiguration config,
            ChatBranch.Data.Ai.Voice.SpeechClient speech) =>
        {
            var doc = LoadVoicesDoc(config);
            var voice = FindCustomVoice(doc, voiceId);
            if (voice == null)
                return Results.Json(new { detail = "Voice not found (presets can't be edited or deleted)." },
                    statusCode: 404);

            // Delete the provider's copy first, or the account keeps an orphaned clone (an
            // ElevenLabs voice slot). An IMPORTED voice was never ours to delete upstream.
            var provider = (string?)voice["provider"] ?? "";
            var remoteId = provider == "voicestudio" ? (string?)voice["remoteVoiceId"] : (string?)voice["elevenVoiceId"];
            if (provider is "elevenlabs" or "voicestudio" && !string.IsNullOrWhiteSpace(remoteId)
                && (bool?)voice["imported"] != true)
            {
                var (ok, why) = await speech.DeleteRemoteVoiceAsync(provider, remoteId!);
                if (!ok)
                    return Unavailable($"The voice was kept: {why}.", 502);
            }

            (doc["voices"] as JsonArray)?.Remove(voice);
            try { SaveVoicesDoc(config, doc); }
            catch (Exception e) { return Unavailable($"Could not write the voice registry: {e.Message}", 503); }

            return Results.Ok(new { deleted = voiceId });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("settings.ai.manage")
          .OrgWideAction();   // the organization's voice library (Voice & calls)
    }

    /// <summary>Loose string read off a raw JSON body (numbers/bools included).</summary>
    private static string? Str(this Dictionary<string, JsonElement>? body, string key)
    {
        if (body == null || !body.TryGetValue(key, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString() ?? "",
            JsonValueKind.Number => el.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }
}
