using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Sidecars;
using ChatBranch.Domain;

namespace ChatBranch.Tests;

/// <summary>
/// The LiveKit control plane (token minting, the Twirp calls, the unconfigured
/// path) and the voice agent's file contract: the dispatch metadata it reads and
/// the call records it leaves behind.
/// </summary>
public class LiveKitTests : IClassFixture<ApiFactory>
{
    private const string Key = "APIkey123";
    private const string Secret = "super-secret-signing-key-0123456789";
    private readonly ApiFactory _factory;
    public LiveKitTests(ApiFactory factory) => _factory = factory;

    // ---- helpers ------------------------------------------------------------

    /// <summary>Records every request and answers from a script (default: 200 {}).</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public readonly List<(string Url, string Auth, JsonElement Body)> Requests = new();
        public Func<string, HttpResponseMessage> Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}"),
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken ct)
        {
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
            var url = request.RequestUri!.ToString();
            Requests.Add((url, request.Headers.Authorization?.ToString() ?? "", body));
            return Respond(url);
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static LiveKitClient Client(StubHandler handler, string url = "wss://lk.example.com",
        string trunk = "ST_trunk") =>
        new(new StubFactory(handler), () => new LiveKitSettings(url, Key, Secret, "chatbranch-agent", trunk));

    private static byte[] FromB64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    /// <summary>Check the HS256 signature against the secret and return the decoded claims.</summary>
    private static JsonElement VerifiedClaims(string jwt, string secret = Secret)
    {
        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);
        var header = JsonDocument.Parse(FromB64Url(parts[0])).RootElement;
        Assert.Equal("HS256", header.GetProperty("alg").GetString());
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}"));
        Assert.Equal(expected, FromB64Url(parts[2]));
        return JsonDocument.Parse(FromB64Url(parts[1])).RootElement;
    }

    private static string BearerToken(string auth)
    {
        Assert.StartsWith("Bearer ", auth);
        return auth["Bearer ".Length..];
    }

    // ---- tokens -------------------------------------------------------------

    [Fact]
    public void Minted_token_is_hs256_signed_with_the_secret_and_carries_the_claims()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var jwt = LiveKitClient.MintToken(Key, Secret, TimeSpan.FromMinutes(10),
            new JsonObject { ["video"] = new JsonObject { ["roomAdmin"] = true, ["room"] = "call-x" } },
            identity: "phone_contact", name: "Aisyah", now: now);

        var claims = VerifiedClaims(jwt);
        Assert.Equal(Key, claims.GetProperty("iss").GetString());
        Assert.Equal("phone_contact", claims.GetProperty("sub").GetString());
        Assert.Equal("Aisyah", claims.GetProperty("name").GetString());
        Assert.Equal(1_800_000_000, claims.GetProperty("nbf").GetInt64());
        Assert.Equal(1_800_000_600, claims.GetProperty("exp").GetInt64());
        Assert.True(claims.GetProperty("video").GetProperty("roomAdmin").GetBoolean());
        Assert.Equal("call-x", claims.GetProperty("video").GetProperty("room").GetString());

        // a token signed with any other secret must not verify
        Assert.ThrowsAny<Exception>(() => VerifiedClaims(jwt, "wrong-secret"));
    }

    [Fact]
    public void Participant_token_lets_the_whatsapp_bridge_join_and_publish_call_status()
    {
        var jwt = Client(new StubHandler()).ParticipantToken("call-wa", LiveKitClient.WhatsAppIdentity, "Sam",
            TimeSpan.FromHours(2));
        var claims = VerifiedClaims(jwt);
        Assert.Equal("whatsapp_borrower", claims.GetProperty("sub").GetString());
        var video = claims.GetProperty("video");
        Assert.Equal("call-wa", video.GetProperty("room").GetString());
        foreach (var grant in new[] { "roomJoin", "canPublish", "canSubscribe", "canUpdateOwnMetadata" })
            Assert.True(video.GetProperty(grant).GetBoolean(), grant);
    }

    // ---- server API ---------------------------------------------------------

    [Fact]
    public async Task Unconfigured_client_explains_what_to_set_and_never_calls_out()
    {
        var handler = new StubHandler();
        var client = new LiveKitClient(new StubFactory(handler),
            () => new LiveKitSettings("", "", "", "chatbranch-agent", ""));

        var placed = await client.PlaceSipCallAsync("call-1", "+60123456789", "Sam", "{}");
        Assert.False(placed.Ok);
        Assert.Equal(503, placed.Status);
        Assert.Contains("LIVEKIT_URL", placed.Detail);
        Assert.Equal(503, (await client.DeleteRoomAsync("call-1")).Status);
        Assert.Empty(handler.Requests);

        // server configured but no trunk: phone calls name the missing trunk id
        var noTrunk = Client(handler, trunk: "");
        var reason = (await noTrunk.PlaceSipCallAsync("call-1", "+60123456789", "Sam", "{}")).Detail;
        Assert.Contains("SIP_OUTBOUND_TRUNK_ID", reason);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Placing_a_call_dispatches_the_agent_then_dials_over_twirp()
    {
        var handler = new StubHandler();
        var metadata = JsonSerializer.Serialize(new { contact_name = "Sam", language = "ms" });
        var placed = await Client(handler).PlaceSipCallAsync("call-abc", "+60123456789", "Sam", metadata);

        Assert.True(placed.Ok, placed.Detail);
        Assert.Equal(2, handler.Requests.Count);

        var (dispatchUrl, dispatchAuth, dispatch) = handler.Requests[0];
        Assert.Equal("https://lk.example.com/twirp/livekit.AgentDispatchService/CreateDispatch", dispatchUrl);
        Assert.Equal("call-abc", dispatch.GetProperty("room").GetString());
        Assert.Equal("chatbranch-agent", dispatch.GetProperty("agentName").GetString());
        Assert.Equal(metadata, dispatch.GetProperty("metadata").GetString());
        var dispatchGrant = VerifiedClaims(BearerToken(dispatchAuth)).GetProperty("video");
        Assert.True(dispatchGrant.GetProperty("roomAdmin").GetBoolean());
        Assert.Equal("call-abc", dispatchGrant.GetProperty("room").GetString());

        var (sipUrl, sipAuth, sip) = handler.Requests[1];
        Assert.Equal("https://lk.example.com/twirp/livekit.SIPService/CreateSIPParticipant", sipUrl);
        Assert.Equal("ST_trunk", sip.GetProperty("sipTrunkId").GetString());
        Assert.Equal("+60123456789", sip.GetProperty("sipCallTo").GetString());
        Assert.Equal("call-abc", sip.GetProperty("roomName").GetString());
        Assert.Equal("phone_contact", sip.GetProperty("participantIdentity").GetString());
        Assert.True(VerifiedClaims(BearerToken(sipAuth)).GetProperty("sip").GetProperty("call").GetBoolean());
    }

    [Fact]
    public async Task A_failed_dial_deletes_the_room_and_reports_livekits_reason()
    {
        var handler = new StubHandler
        {
            Respond = url => url.Contains("CreateSIPParticipant")
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("""{"code":"not_found","msg":"trunk not found"}"""),
                }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") },
        };
        var placed = await Client(handler, url: "ws://127.0.0.1:7880").PlaceSipCallAsync(
            "call-bad", "+60123456789", "Sam", "{}");

        Assert.False(placed.Ok);
        Assert.Equal(404, placed.Status);
        Assert.Contains("trunk not found", placed.Detail);
        var (deleteUrl, deleteAuth, delete) = handler.Requests[^1];
        Assert.Equal("http://127.0.0.1:7880/twirp/livekit.RoomService/DeleteRoom", deleteUrl);
        Assert.Equal("call-bad", delete.GetProperty("room").GetString());
        Assert.True(VerifiedClaims(BearerToken(deleteAuth)).GetProperty("video").GetProperty("roomCreate").GetBoolean());
    }

    [Fact]
    public async Task Dial_and_hangup_answer_503_with_a_reason_when_livekit_is_unconfigured()
    {
        // Placing a real (billed) call needs a signed-in operator.
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.PostAsJsonAsync("/api/calls/dial", new { phone = "+60123456789" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/api/calls/call-abc/hangup", null)).StatusCode);

        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "agent", password = "agent123" });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var dial = await client.PostAsJsonAsync("/api/calls/dial", new { phone = "+60123456789", name = "Sam" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, dial.StatusCode);
        var body = await dial.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, body.GetProperty("room").ValueKind);
        Assert.Contains("LIVEKIT_URL", body.GetProperty("detail").GetString());

        // validation still answers first
        var bad = await client.PostAsJsonAsync("/api/calls/dial", new { phone = "60123" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var hangup = await client.PostAsync("/api/calls/call-abc/hangup", null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, hangup.StatusCode);
        Assert.Contains("LIVEKIT_URL", (await hangup.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("detail").GetString());
    }

    // ---- the agent's file contract ------------------------------------------

    [Fact]
    public async Task Dispatch_metadata_carries_contact_record_knowledge_and_skills()
    {
        using var t = new TestDb();
        var now = DateTime.UtcNow;
        var contact = new Contact
        {
            Name = "Aisyah", Phone = "+60123456789", Language = "ms", CompanyName = "Kedai Maju",
            AiProfileSummary = "Prefers WhatsApp.", PreferredContactTime = "evenings",
            CreatedAt = now, UpdatedAt = now,
        };
        t.Db.Contacts.Add(contact);
        t.Db.SaveChanges();
        t.Db.Records.Add(new ChatBranch.Domain.Record
        {
            ContactId = contact.Id, Type = "shipment", ExternalId = "SHP-1", Title = "Parcel", Status = "open",
            Amount = 120.5m, Currency = "MYR", DueDate = new DateOnly(2026, 10, 9), CreatedAt = now, UpdatedAt = now,
        });
        t.Db.KnowledgeItems.Add(new KnowledgeItem { Kind = "fact", Title = "Hours", Content = "Open 9-6.", CreatedAt = now, UpdatedAt = now });
        t.Db.AgentSkills.Add(new AgentSkill { SkillId = "s1", Name = "Reschedule", UseWhen = "they can't make it", Instructions = "Offer two slots." });
        t.Db.SaveChanges();

        var scoping = new ScopingService(t.Db);
        var knowledge = new KnowledgeService(t.Db, scoping);
        var assistant = new AssistantService(t.Db, scoping, new PersonaService(scoping), knowledge, new SandboxLlmClient());
        var md = await VoiceDispatch.BuildAsync(t.Db, knowledge, assistant, scoping, Path.GetTempPath(), contact,
            new VoiceCallOptions("+60123456789", VoiceProvider: "ElevenLabs", Voice: "v-1", LlmProvider: "ilmu",
                Script: "  Hello.  "));
        var json = JsonSerializer.SerializeToElement(md);

        Assert.Equal("+60123456789", json.GetProperty("phone").GetString());
        Assert.Equal(contact.Id, json.GetProperty("contact_id").GetInt32());
        Assert.Equal("Aisyah", json.GetProperty("contact_name").GetString());
        Assert.Equal("Kedai Maju", json.GetProperty("company_name").GetString());
        Assert.Equal("ms", json.GetProperty("language").GetString());
        Assert.Equal("shipment", json.GetProperty("record_type").GetString());
        Assert.Equal("SHP-1", json.GetProperty("record_id").GetString());
        Assert.Equal("2026-10-09", json.GetProperty("record_due_date").GetString());
        Assert.Contains("120.50", json.GetProperty("record_amount").GetString());
        Assert.Contains("Open 9-6.", json.GetProperty("knowledge").GetString());
        Assert.Contains("Reschedule", json.GetProperty("skills").GetString());
        Assert.Contains("Prefers WhatsApp.", json.GetProperty("memory").GetString());
        Assert.Equal("elevenlabs", json.GetProperty("voice_provider").GetString());
        Assert.Equal("v-1", json.GetProperty("voice").GetString());
        Assert.Equal("ilmu", json.GetProperty("llm_provider").GetString());
        Assert.Equal("Hello.", json.GetProperty("script").GetString());
        Assert.False(json.TryGetProperty("stt_provider", out _)); // unset keys are omitted, not ""
    }

    [Fact]
    public async Task A_call_without_a_voice_uses_the_organizations_saved_voice()
    {
        using var t = new TestDb();
        var scoping = new ScopingService(t.Db);
        var knowledge = new KnowledgeService(t.Db, scoping);
        var assistant = new AssistantService(t.Db, scoping, new PersonaService(scoping), knowledge, new SandboxLlmClient());
        scoping.SetSetting("default_voice", "cedar");
        scoping.SetSetting("default_voice_provider", "openai");

        var md = JsonSerializer.SerializeToElement(await VoiceDispatch.BuildAsync(t.Db, knowledge, assistant, scoping,
            Path.GetTempPath(), null, new VoiceCallOptions("+60123456789")));
        Assert.Equal("cedar", md.GetProperty("voice").GetString());
        Assert.Equal("openai", md.GetProperty("voice_provider").GetString());

        // a voice picked for the call still wins
        var picked = JsonSerializer.SerializeToElement(await VoiceDispatch.BuildAsync(t.Db, knowledge, assistant, scoping,
            Path.GetTempPath(), null, new VoiceCallOptions("+60123456789", VoiceProvider: "openai", Voice: "alloy")));
        Assert.Equal("alloy", picked.GetProperty("voice").GetString());
    }

    [Fact]
    public async Task Finished_calls_fold_into_call_records_and_the_usage_ledger_once()
    {
        using var t = new TestDb();
        var dir = Path.Combine(Path.GetTempPath(), $"cb-calls-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "live"));
            File.WriteAllText(Path.Combine(dir, "calls.json"), JsonSerializer.Serialize(new[]
            {
                new
                {
                    call_id = "call-1", started_at = "2026-10-08T10:00:00+00:00", ended_at = "2026-10-08T10:02:00+00:00",
                    contact = new { contact_name = "Sam", phone = "+60123456789", contact_id = 999 },
                    transcript = "assistant: Hi\nuser: Friday works", summary = "Agreed Friday.",
                    outcome = "scheduled", followup_date = "2026-10-10", needs_human = true,
                },
            }));
            File.WriteAllText(Path.Combine(dir, "live", "call-1.json"), JsonSerializer.Serialize(new
            {
                turns = Array.Empty<object>(), done = true, ended_by = "agent",
                llm_usage = new
                {
                    prompt = 100, completion = 20, audio_prompt = 300, audio_completion = 400,
                    model = "gpt-realtime", provider = "openai",
                    summary = new { model = "gpt-4o-mini", prompt = 50, completion = 10 },
                },
            }));
            var bridge = new VoiceAgentBridge(dir);

            Assert.Equal(1, await VoiceCallSync.SyncAsync(t.Db, bridge));
            Assert.Equal(0, await VoiceCallSync.SyncAsync(t.Db, bridge)); // idempotent

            var record = Assert.Single(t.Fresh().CallRecords.ToList());
            Assert.Equal("call-1", record.CallId);
            Assert.Null(record.ContactId); // contact 999 doesn't exist — the FK must hold
            Assert.Equal("Sam", record.Name);
            Assert.Equal("scheduled", record.Outcome);
            Assert.Equal("2026-10-10", record.FollowupDate);
            Assert.True(record.NeedsHuman);

            var usage = t.Fresh().LlmUsage.OrderBy(u => u.Id).ToList();
            Assert.Equal(2, usage.Count);
            Assert.Equal(("voice_call", "gpt-realtime", 400), (usage[0].Feature, usage[0].Model, usage[0].AudioCompletionTokens));
            Assert.Equal(("summary", 50), (usage[1].Feature, usage[1].PromptTokens));
            Assert.All(usage, u => Assert.Equal("call-1", u.Ref));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Bridge_files_use_the_agents_names_and_mirror_the_lexicon()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"cb-bridge-{Guid.NewGuid():N}");
        try
        {
            var bridge = new VoiceAgentBridge(dir);
            // the agent strips anything outside [A-Za-z0-9_-] from a room's file name
            bridge.Guide("call-a.b", "Hi");
            Assert.True(File.Exists(Path.Combine(dir, "inbox", "call-ab.json")));

            bridge.WritePronunciation(new[] { ("Duit Gini", "Doo-it Ghee-nee") });
            var lexicon = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "pronunciation.json"))).RootElement;
            var term = Assert.Single(lexicon.GetProperty("terms").EnumerateArray());
            Assert.Equal("Duit Gini", term.GetProperty("term").GetString());
            Assert.Equal("Doo-it Ghee-nee", term.GetProperty("alias").GetString());
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
