using System.Text.Json;
using System.Text.Json.Nodes;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
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
/// WHAT IS REAL AND WHAT IS A STUB
/// -------------------------------
/// The voice agent is a separate, Django-free process that talks over FILES
/// (data/live/{room}.json in, data/inbox/{room}.json out). <see cref="VoiceAgentBridge"/>
/// already speaks that protocol, so everything file-backed is implemented for
/// real here: the live transcript, guide/take-over instructions and escalation.
/// Everything that needs the LiveKit CONTROL PLANE (placing a call, dropping a
/// call) has no C# implementation — those return an honest, JSON, non-throwing
/// "not available in this build" instead of pretending a call was placed. Same
/// for the ElevenLabs/OpenAI TTS paths: no provider client exists here, so
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
        // keeps finished calls in the CallRecords table instead, so that is the
        // source here. The WhatsApp-call half is best-effort in Django too (it
        // sits inside a bare try/except) and has no C# store yet, so this returns
        // the ai-call rows only — the page's "WhatsApp" tab simply counts 0.
        // Auth-gated like Django (the page sends authHeaders()).
        app.MapGet("/api/call-logs", async (AppDbContext db) =>
        {
            var rows = await db.CallRecords.AsNoTracking()
                .Include(r => r.Contact)
                .OrderByDescending(r => r.Id)
                .Take(500)
                .ToListAsync();

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
                    // Recordings are written by the Python recorder into
                    // data/recordings/; nothing in this build produces or serves
                    // them, so the row reports "no audio" rather than a dead link.
                    ["recording"] = null,
                    ["transcript"] = ParseTranscript(r.Transcript),
                    ["contact_id"] = r.ContactId,
                })
                .OrderByDescending(x => (string?)x["ts"] ?? "", StringComparer.Ordinal)
                .Take(200)
                .ToList();

            return Results.Ok(new { logs });
        }).RequireAuthorization(AuthPolicies.Authenticated);
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

        // ---- POST /api/calls/dial ------------------------------------------
        // Django validates the request, then hands off to LiveKit (calls/lk.dial
        // → voice_agent/caller.place_call) for the SIP INVITE. There is no
        // LiveKit client in this build, so every validation Django does still
        // runs here — contact lookup, E.164 check, voice pre-flight — and only
        // the hand-off is stubbed. AppContext.tsx:322 does
        // `data = await res.json()` BEFORE checking res.ok and shows
        // `data.detail` on the call tile, so the body is always JSON.
        app.MapPost("/api/calls/dial", async (DialWrite? body, AppDbContext db) =>
        {
            body ??= new DialWrite(null, null, null, null, null, null, null);

            string? phone;
            if (body.contact_id is int contactId)
            {
                var contact = await db.Contacts.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Id == contactId);
                if (contact == null)
                    return Results.Json(new { detail = "Contact not found." }, statusCode: 404);
                phone = contact.Phone;
            }
            else
            {
                phone = (body.phone ?? "").Trim();
            }

            if (string.IsNullOrEmpty(phone) || !phone.StartsWith('+'))
                return Bad("phone must be E.164 (start with +).");

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

            // The stub. `room` is explicitly null: the frontend re-keys its call
            // tile to data.room only on a 2xx, so a fabricated room id would
            // leave a tile polling a call that will never exist.
            return Results.Json(new
            {
                ok = false,
                room = (string?)null,
                status = "unavailable",
                detail = "Calling is not available in this build: placing a call needs the LiveKit "
                       + "control plane and a SIP trunk (LIVEKIT_URL / LIVEKIT_API_KEY), which the "
                       + "C# API does not embed.",
            }, statusCode: 501);
        }).AllowAnonymous();

        // ---- GET /api/calls/{room} — the finished call's summary ------------
        // Django reads the agent's file store (calls/lk.call_record); the C#
        // build keeps the same record in CallRecords, keyed by call id = room.
        // Keys match AppContext.tsx's CallSummary type; the three the C# entity
        // does not carry are returned as null rather than omitted, so the UI
        // renders "—" instead of `undefined`.
        app.MapGet("/api/calls/{room}", async (string room, AppDbContext db) =>
        {
            if (!RoomOk(room)) return Bad("Invalid room id.");
            var r = await db.CallRecords.AsNoTracking()
                .Include(x => x.Contact)
                .FirstOrDefaultAsync(x => x.CallId == room);
            if (r == null)
                return Results.Json(new { detail = "Summary not ready." }, statusCode: 404);

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
                // Not modelled on CallRecord — null keeps the CallSummary shape
                // whole instead of leaving the field undefined in the UI.
                ["followup_action"] = null,
                ["objection"] = null,
                ["sentiment_score"] = null,
                ["transcript"] = ParseTranscript(r.Transcript),
            });
        }).AllowAnonymous();

        // ---- GET /api/calls/{room}/live — the live transcript ---------------
        // REAL, not a stub: this is the file half of the agent protocol, which
        // VoiceAgentBridge already speaks. Mirrors calls/lk.live_transcript,
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
        }).AllowAnonymous();

        // ---- POST /api/calls/{room}/say — guide / take over -----------------
        // REAL: calls/views.say pushes the instruction into the agent's
        // consume-once inbox (storage.push_instruction), which is exactly what
        // VoiceAgentBridge.PushInstruction writes. Nothing consumes it when no
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
        }).AllowAnonymous();

        // ---- POST /api/calls/{room}/hangup ----------------------------------
        // Dropping a call is a LiveKit control-plane operation (calls/lk.hangup
        // → caller.hangup_room); the file bridge has no equivalent. Best effort:
        // queue a wrap-up hint, which a running agent WILL act on, and report
        // ok:false so nobody reads this as a confirmed disconnect. 200 on
        // purpose — AppContext.tsx fires this and ignores the response, and the
        // tile has already moved to "Hanging up…".
        app.MapPost("/api/calls/{room}/hangup", (string room, VoiceAgentBridge bridge) =>
        {
            if (!RoomOk(room)) return Bad("Invalid room id.");
            try
            {
                bridge.Guide(room, "Politely wrap up and end the call now.");
            }
            catch { /* no agent / unwritable data dir — still answer, never 500 */ }

            return Results.Ok(new
            {
                ok = false,
                room,
                queued = "guide",
                detail = "Dropping the SIP leg needs the LiveKit control plane, which this build does "
                       + "not include — a wrap-up instruction was queued for the agent instead.",
            });
        }).AllowAnonymous();

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
        }).AllowAnonymous();
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
        // Open like the Django voice views — lib/voice.ts and AppContext.tsx
        // call all five with a bare fetch(). PATCH/DELETE touch only the local
        // custom-voice file and expose no secrets.

        // ---- POST /api/voice/preview ----------------------------------------
        // On success Django streams audio/mpeg; on failure it returns
        // {detail}, which lib/voice.ts:52 reads out of the error body and
        // surfaces verbatim. No TTS client exists in this build (no OpenAI /
        // ElevenLabs speech call), so this is always the failure branch — but
        // the request-shape validation is real, so a caller still learns when
        // the fault is theirs.
        app.MapPost("/api/voice/preview", (Dictionary<string, JsonElement>? body) =>
        {
            var voiceId = (body.Str("voiceId") ?? "").Trim();
            var text = (body.Str("text") ?? "").Trim();
            if (voiceId.Length == 0 || text.Length == 0)
                return Bad("voiceId and text are required.");
            if (text.Length > 400)  // previews are short persona lines
                return Bad("text too long for preview (max 400 chars).");

            return Unavailable(
                "Voice preview is not available in this build: no TTS provider client is wired up "
                + "(OpenAI / ElevenLabs speech synthesis lives in the Python service).");
        }).AllowAnonymous();

        // ---- POST /api/voice/clone ------------------------------------------
        // multipart/form-data (name, language, persona, gender, audio). Cloning
        // is an ElevenLabs round-trip; with no client here every well-formed
        // request ends 501. AppContext.tsx:233 throws on !ok, so the operator
        // gets the detail — and the 400s below still catch a malformed upload.
        app.MapPost("/api/voice/clone", async (HttpContext ctx) =>
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
            if (!(audio.ContentType ?? "").StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
                return Bad("audio must be an audio file.");

            return Unavailable(Env("ELEVENLABS_API_KEY").Length == 0
                ? "Voice cloning not configured (ELEVENLABS_API_KEY unset)."
                : "Voice cloning is not available in this build (no ElevenLabs client in the C# API).");
        }).AllowAnonymous();

        // ---- POST /api/voice/import -----------------------------------------
        // Import copies an existing ElevenLabs voice into the local registry.
        // Django gates on the API key first, and so does this: without a key
        // there is nothing to look the id up against. AppContext.tsx:245 reads
        // {voice} on success and throws the status otherwise.
        app.MapPost("/api/voice/import", (ImportWrite? body, IConfiguration config) =>
        {
            var elId = (body?.voiceId ?? "").Trim();
            if (elId.Length == 0) return Bad("voiceId is required.");

            // Already imported? Django's fast path returns the local record
            // without touching ElevenLabs — the one import that can succeed here.
            var doc = LoadVoicesDoc(config);
            var existing = (doc["voices"] as JsonArray)?
                .OfType<JsonObject>()
                .FirstOrDefault(v => (string?)v["elevenVoiceId"] == elId);
            if (existing != null)
                return Results.Ok(new { voice = ClientView(existing) });

            return Unavailable(Env("ELEVENLABS_API_KEY").Length == 0
                ? "Voice import not configured (ELEVENLABS_API_KEY unset)."
                : "Voice import is not available in this build (no ElevenLabs client in the C# API).");
        }).AllowAnonymous();

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
        }).AllowAnonymous();

        // ---- DELETE /api/voice/{voiceId} -------------------------------------
        // Deleting an ElevenLabs-backed voice has to delete it UPSTREAM too, or
        // the account keeps paying for an orphaned clone — Django refuses when
        // it can't do that, and so does this build, which can never do it. A
        // local-only custom voice is genuinely removed. AppContext.tsx:226 drops
        // the voice from its list on any 2xx.
        app.MapDelete("/api/voice/{voiceId}", (string voiceId, IConfiguration config) =>
        {
            var doc = LoadVoicesDoc(config);
            var voice = FindCustomVoice(doc, voiceId);
            if (voice == null)
                return Results.Json(new { detail = "Voice not found (presets can't be edited or deleted)." },
                    statusCode: 404);

            var remoteId = (string?)voice["elevenVoiceId"];
            if ((string?)voice["provider"] == "elevenlabs" && !string.IsNullOrWhiteSpace(remoteId))
                return Unavailable(Env("ELEVENLABS_API_KEY").Length == 0
                    ? "Voice delete not configured (ELEVENLABS_API_KEY unset)."
                    : "Deleting the upstream ElevenLabs voice is not available in this build, so the "
                      + "local record was kept to avoid orphaning the clone.");

            (doc["voices"] as JsonArray)?.Remove(voice);
            try { SaveVoicesDoc(config, doc); }
            catch (Exception e) { return Unavailable($"Could not write the voice registry: {e.Message}", 503); }

            return Results.Ok(new { deleted = voiceId });
        }).AllowAnonymous();
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
