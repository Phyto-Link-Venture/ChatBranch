using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Ai.Voice;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Flows;
using ChatBranch.Data.Sidecars;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Tests;

/// <summary>ElevenLabs (text-to-speech + scribe) and OpenAI transcriptions at the HTTP level.</summary>
internal sealed class FakeSpeech : HttpMessageHandler, IHttpClientFactory
{
    public sealed record Call(string Path, string Query, string Body, string Key, string Origin = "");
    public ConcurrentQueue<Call> Calls { get; } = new();
    public bool FailTts { get; set; }
    public bool NotOgg { get; set; }
    public string Transcript { get; set; } = "Can I book a table for two tonight?";
    public static readonly byte[] Ogg = Encoding.ASCII.GetBytes("OggS\0fake-opus-voice-note");
    public static readonly byte[] Mp3 = Encoding.ASCII.GetBytes("ID3fake-mp3");

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

    public List<Call> Tts => Calls.Where(c => c.Path.Contains("/text-to-speech/")).ToList();
    public List<Call> Stt => Calls.Where(c => c.Path.Contains("transcriptions") || c.Path.Contains("speech-to-text")).ToList();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
        var key = request.Headers.TryGetValues("xi-api-key", out var k) ? k.First()
            : request.Headers.Authorization?.Parameter ?? "";
        var path = request.RequestUri!.AbsolutePath;
        Calls.Enqueue(new Call(path, request.RequestUri.Query, body, key, request.RequestUri.GetLeftPart(UriPartial.Authority)));
        if (path.Contains("/text-to-speech/"))
        {
            if (FailTts) return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"detail\":\"invalid_api_key\"}") };
            var ogg = request.RequestUri.Query.Contains("opus") && !NotOgg;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ogg ? Ogg : Mp3) };
        }
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { text = Transcript }), Encoding.UTF8, "application/json"),
        };
    }
}

/// <summary>A Baileys gateway that records voice notes (and texts).</summary>
internal sealed class VoiceBaileys : IBaileysGateway
{
    public List<(string Account, string To, byte[] Audio)> Voice { get; } = new();
    public List<string> Texts { get; } = new();
    public bool FailVoice { get; set; }
    public bool IsConfigured => true;
    public string BaseUrl => "http://gateway.test";

    public Task<BaileysResult> SendTextAsync(string a, string to, string text, CancellationToken ct = default)
    {
        Texts.Add(text);
        return Task.FromResult(new BaileysResult(true, "sent", $"3EB0TEXT{Texts.Count}"));
    }

    public Task<BaileysResult> SendVoiceNoteAsync(string a, string to, byte[] oggOpus, CancellationToken ct = default)
    {
        if (FailVoice) return Task.FromResult(new BaileysResult(false, "gateway refused the voice note"));
        Voice.Add((a, to, oggOpus));
        return Task.FromResult(new BaileysResult(true, "sent", $"3EB0VOICE{Voice.Count}"));
    }

    public Task<BaileysResult> StartCallAsync(string a, string to, Dictionary<string, object?> d,
        CancellationToken ct = default) => Task.FromResult(new BaileysResult(false, "no calls here"));
    public Task<BaileysResult> EndCallAsync(string a, CancellationToken ct = default) =>
        Task.FromResult(new BaileysResult(true, "ended"));
    private static readonly BaileysStatus Connected = new("connected", null, "me", null);
    public Task<BaileysStatus> StatusAsync(string a, CancellationToken ct = default) => Task.FromResult(Connected);
    public Task<BaileysStatus> StartAsync(string a, CancellationToken ct = default) => Task.FromResult(Connected);
    public Task<BaileysStatus> LogoutAsync(string a, CancellationToken ct = default) => Task.FromResult(Connected);
}

/// <summary>A Cloud number that uploads media and sends by id, logging each call.</summary>
internal sealed class VoiceCloud : IWhatsAppCloudClient
{
    public List<string> Events { get; } = new();
    private int _n;
    public bool IsConfigured(ChannelAccount account) => account.AccessToken.Length > 0 && account.ExternalId.Length > 0;
    public Task<SendOutcome> SendTextAsync(ChannelAccount a, string to, string text, CancellationToken ct = default)
    {
        Events.Add($"text:{text}");
        return Task.FromResult(new SendOutcome(true, $"wamid.out.{++_n}", ""));
    }
    public Task<SendOutcome> UploadMediaAsync(ChannelAccount a, byte[] bytes, string mime, string filename,
        CancellationToken ct = default)
    {
        Events.Add($"upload:{mime}:{filename}");
        return Task.FromResult(new SendOutcome(true, "MEDIA1", ""));
    }
    public Task<SendOutcome> SendMediaAsync(ChannelAccount a, string to, string type, string mediaId, string? caption,
        string? filename, CancellationToken ct = default)
    {
        Events.Add($"media:{type}:{mediaId}:{caption ?? ""}");
        return Task.FromResult(new SendOutcome(true, $"wamid.out.{++_n}", ""));
    }
}

/// <summary>The AI-ops stack with voice replies wired: media store, ElevenLabs fake, Baileys + Telegram + Cloud fakes.</summary>
internal sealed class VoiceRig : IDisposable
{
    public TestDb T { get; } = new();
    public AppDbContext Db => T.Db;
    public ManualClock Clock { get; } = new(DateTimeOffset.UtcNow);
    public ToolScriptLlm Llm { get; } = new((_, _) => ToolScriptLlm.Says("Sure, see you at 8pm!"));
    public FakeSpeech Speech { get; } = new();
    public VoiceBaileys Baileys { get; } = new();
    public VoiceCloud Cloud { get; } = new();
    public FakeTelegramApi Telegram { get; } = new();
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), $"cb-voice-{Guid.NewGuid():N}");
    public MediaStore Media { get; }
    public ScopingService Sc { get; }
    public ThreadStore Threads { get; }
    public AiGate Gate { get; }
    public AiComposer Composer { get; }
    public InboundProcessor Inbound { get; }

    public VoiceRig()
    {
        Sc = new ScopingService(Db);
        Threads = new ThreadStore(Db, Sc);
        Media = new MediaStore(Dir);
        var tg = new TelegramChannel(Db, Sc, Threads, new TelegramClient(Telegram, "https://tg.test"));
        var sender = new OutboundSender(Db, Threads, Cloud, Baileys, media: Media, telegram: tg);
        var assistant = new AssistantService(Db, Sc, new PersonaService(Sc), new KnowledgeService(Db, Sc), Llm);
        var speech = new SpeechClient(Sc, Speech);
        Composer = new AiComposer(Db, assistant, Llm, new VoiceTranscriber(Db, Sc, Media, speech));
        Gate = new AiGate(Db, Sc, Threads, sender, Clock, voice: new VoiceReplyService(Db, Sc, Media, speech));
        var responder = new AiResponder(Db, Sc, Composer, Gate, Clock);
        var queue = new QueueService(Db, Sc, Threads, assistant, sender, responder, Gate, Clock);
        Inbound = new InboundProcessor(Db, Threads, assistant, sender, new FlowEngine(Db, Sc, queue), queue, Sc,
            new AutomationService(Db, Sc), responder);
        foreach (var (k, v) in new[]
                 {
                     ("ai_review_mode", "auto"), ("ai_review_first_n", "0"), ("ai_review_notify", "false"),
                     ("ai_wait_seconds", "0"), ("ai_typing", "false"), ("ai_split", "off"), ("ai_ack_skip", "false"),
                     ("ai_followup", "false"), ("ai_reply_mode", "instant"), ("ai_reactions", "false"),
                     ("ai_quote_replies", "off"), ("ai_voice_replies", "mirror"), ("elevenlabs_api_key", "el-key"),
                 })
            Sc.SetSetting(k, v);
    }

    public ChannelAccount Account(string channel, string provider, string externalId, string token = "")
    {
        var a = new ChannelAccount
        {
            Channel = channel, Provider = provider, Name = $"{channel}-{provider}", ExternalId = externalId,
            AccessToken = token, HealthStatus = Health.Healthy, CreatedAt = DateTime.UtcNow,
            Config = new Dictionary<string, object?> { ["role"] = AccountRoles.Conversation },
        };
        Db.ChannelAccounts.Add(a);
        Db.SaveChanges();
        return a;
    }

    public Contact Contact(string name, string phone = "")
    {
        var c = new Contact { Name = name, Phone = phone, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        Db.Contacts.Add(c);
        Db.SaveChanges();
        return c;
    }

    public List<Message> Out(string channel, string key)
    {
        var db = T.Fresh();
        var t = db.Threads.First(x => x.Channel == channel && x.Key == key);
        return db.Messages.Where(m => m.ThreadId == t.Id && m.Direction == MessageDirection.Out && !m.Private)
            .OrderBy(m => m.Id).ToList();
    }

    public AiDraft LastDraft() => T.Fresh().AiDrafts.OrderByDescending(d => d.Id).First();

    public void Dispose()
    {
        T.Dispose();
        try { Directory.Delete(Dir, recursive: true); } catch { }
    }
}

/// <summary>
/// AI voice replies (Ai/Voice/): <c>ai_voice_replies</c> off | mirror | always — one voice note
/// carrying the whole reply on WhatsApp (Baileys ptt, Cloud audio) and Telegram (sendVoice), the
/// words kept on the message, text whenever it can't be (other channels, TTS failure, no key, the
/// AI quota, a link), held drafts never synthesizing, and customer voice notes transcribed for the AI.
/// </summary>
public class VoiceReplyTests
{
    [Fact]
    public void Mirror_answers_a_baileys_voice_note_with_one_voice_note_and_keeps_the_words()
    {
        using var r = new VoiceRig();
        var acct = r.Account("whatsapp", Providers.Baileys, "gw-1");
        r.Contact("Aisyah", "60123000001");

        r.Inbound.ProcessInbound("whatsapp", "60123000001", "[audio]", accountId: acct.Id, wamid: "wa.v1");

        var voice = Assert.Single(r.Baileys.Voice);
        Assert.Equal("gw-1", voice.Account);
        Assert.True(SpeechClient.IsOgg(voice.Audio));
        Assert.Empty(r.Baileys.Texts);
        var tts = Assert.Single(r.Speech.Tts);
        Assert.Contains("output_format=opus_48000_64", tts.Query);
        Assert.Contains(SpeechClient.DefaultVoiceId, tts.Path);
        Assert.Equal("el-key", tts.Key);
        Assert.Contains("Sure, see you at 8pm!", tts.Body);

        var sent = Assert.Single(r.Out("whatsapp", "60123000001"));
        Assert.Equal("Sure, see you at 8pm!", sent.Text);
        Assert.Equal("audio", sent.MediaType);
        Assert.EndsWith(".ogg", sent.MediaPath);
        Assert.Equal("sent", sent.Status);
        Assert.NotNull(r.Media.Resolve("whatsapp", sent.MediaPath));
        Assert.Equal(AiGate.VoiceSent, AiGate.Str(r.LastDraft().Context, "voice", ""));

        var db = r.T.Fresh();
        var usage = db.LlmUsage.Single(u => u.Feature == VoiceReplyService.Feature);
        Assert.Equal("elevenlabs", usage.Provider);
        Assert.Equal("Sure, see you at 8pm!".Length, usage.PromptTokens);
        Assert.True(db.AuditLogs.Any(a => a.Action == "ai.voice_reply"));

        // the customer TYPES next: mirror answers in text
        r.Inbound.ProcessInbound("whatsapp", "60123000001", "and parking?", accountId: acct.Id, wamid: "wa.v2");
        Assert.Single(r.Baileys.Voice);
        Assert.Single(r.Baileys.Texts);
        Assert.Equal("", r.Out("whatsapp", "60123000001").Last().MediaType);
    }

    [Fact]
    public void Always_mode_speaks_every_reply_and_off_never_does()
    {
        using var r = new VoiceRig();
        var acct = r.Account("whatsapp", Providers.Baileys, "gw-1");
        r.Contact("Ben", "60123000002");

        r.Sc.SetSetting("ai_voice_replies", "always");
        r.Inbound.ProcessInbound("whatsapp", "60123000002", "hi, open today?", accountId: acct.Id, wamid: "wa.a1");
        Assert.Single(r.Baileys.Voice);

        r.Sc.SetSetting("ai_voice_replies", "off");
        r.Inbound.ProcessInbound("whatsapp", "60123000002", "[audio]", accountId: acct.Id, wamid: "wa.a2");
        Assert.Single(r.Baileys.Voice);
        Assert.Single(r.Baileys.Texts);
        Assert.Single(r.Speech.Tts);

        // an inbox override wins over the organization setting
        acct.Config["ai_voice_replies"] = "always";
        r.Db.ChannelAccounts.Update(acct);
        r.Db.SaveChanges();
        r.Inbound.ProcessInbound("whatsapp", "60123000002", "thanks", accountId: acct.Id, wamid: "wa.a3");
        Assert.Equal(2, r.Baileys.Voice.Count);
    }

    [Fact]
    public void Telegram_sends_the_voice_note_with_sendVoice()
    {
        using var r = new VoiceRig();
        var bot = r.Account("telegram", "", "777", token: "777:token");
        var c = r.Contact("Tg");
        r.Threads.GetOrCreateThread("telegram", "4401", "Tg", bot.Id, c.Id);

        r.Inbound.ProcessInbound("telegram", "4401", "[audio]", accountId: bot.Id, wamid: "tg.1");

        var call = Assert.Single(r.Telegram.Calls, x => x.Method is "sendVoice" or "sendMessage" or "sendAudio");
        Assert.Equal("sendVoice", call.Method);
        Assert.Contains("name=voice", call.Body);
        Assert.DoesNotContain("name=caption", call.Body);
        var sent = Assert.Single(r.Out("telegram", "4401"));
        Assert.Equal("Sure, see you at 8pm!", sent.Text);
        Assert.Equal("audio", sent.MediaType);
    }

    [Fact]
    public void Cloud_uploads_ogg_and_sends_an_audio_message()
    {
        using var r = new VoiceRig();
        var acct = r.Account("whatsapp", Providers.Cloud, "PN-1", token: "tok");
        r.Contact("Cloudy", "60123000003");
        r.Inbound.ProcessInbound("whatsapp", "60123000003", "[audio]", accountId: acct.Id, wamid: "wa.c1");
        Assert.Equal(new[] { "upload:audio/ogg:voice.ogg", "media:audio:MEDIA1:" }, r.Cloud.Events);
        Assert.Equal("audio", Assert.Single(r.Out("whatsapp", "60123000003")).MediaType);
    }

    [Fact]
    public void Unsupported_channels_and_twilio_numbers_get_text()
    {
        Assert.False(VoiceReplyService.Supports("email", null));
        Assert.False(VoiceReplyService.Supports("sms", null));
        Assert.False(VoiceReplyService.Supports("line", null));
        Assert.False(VoiceReplyService.Supports("website", null));
        Assert.False(VoiceReplyService.Supports("whatsapp", new ChannelAccount { Provider = Providers.Twilio }));
        Assert.True(VoiceReplyService.Supports("whatsapp", new ChannelAccount { Provider = Providers.YCloud }));
        Assert.True(VoiceReplyService.Supports("instagram", null));

        using var r = new VoiceRig();
        r.Sc.SetSetting("ai_voice_replies", "always");
        var c = r.Contact("Web");
        var widget = r.Account("website", "", "wtok");
        r.Threads.GetOrCreateThread("website", "visitor-1", "Web", widget.Id, c.Id);
        r.Inbound.ProcessInbound("website", "visitor-1", "hello", accountId: widget.Id);
        Assert.Empty(r.Speech.Tts);
        Assert.Equal("", Assert.Single(r.Out("website", "visitor-1")).MediaType);
    }

    [Fact]
    public void Tts_failure_no_key_and_links_fall_back_to_text()
    {
        using var r = new VoiceRig();
        var acct = r.Account("whatsapp", Providers.Baileys, "gw-1");
        r.Contact("Fail", "60123000004");

        r.Speech.FailTts = true;
        r.Inbound.ProcessInbound("whatsapp", "60123000004", "[audio]", accountId: acct.Id, wamid: "wa.f1");
        Assert.Empty(r.Baileys.Voice);
        Assert.Single(r.Baileys.Texts);
        var d = r.LastDraft();
        Assert.Equal(AiGate.VoiceAsText, AiGate.Str(d.Context, "voice", ""));
        Assert.Contains("401", AiGate.Str(d.Context, "voice_error", ""));
        Assert.Empty(r.T.Fresh().LlmUsage.Where(u => u.Feature == VoiceReplyService.Feature));

        // a non-Ogg answer can't be a voice note
        r.Speech.FailTts = false;
        r.Speech.NotOgg = true;
        r.Inbound.ProcessInbound("whatsapp", "60123000004", "[audio]", accountId: acct.Id, wamid: "wa.f2");
        Assert.Empty(r.Baileys.Voice);
        Assert.Equal(2, r.Baileys.Texts.Count);
        r.Speech.NotOgg = false;

        // no ElevenLabs key: text, and ElevenLabs is never called
        r.Sc.SetSetting("elevenlabs_api_key", "");
        var before = r.Speech.Tts.Count;
        r.Inbound.ProcessInbound("whatsapp", "60123000004", "[audio]", accountId: acct.Id, wamid: "wa.f3");
        Assert.Equal(before, r.Speech.Tts.Count);
        Assert.Equal(3, r.Baileys.Texts.Count);

        // the gateway refusing the voice note: the words still go, no failed row left
        r.Sc.SetSetting("elevenlabs_api_key", "el-key");
        r.Baileys.FailVoice = true;
        r.Inbound.ProcessInbound("whatsapp", "60123000004", "[audio]", accountId: acct.Id, wamid: "wa.f4");
        Assert.Equal(4, r.Baileys.Texts.Count);
        Assert.DoesNotContain(r.Out("whatsapp", "60123000004"), m => m.Status == "failed");

        // a reply with a link stays text
        Assert.Null(VoiceReplyService.Speakable("Pay here: https://pay.example/x"));
        Assert.Equal("Hello there", VoiceReplyService.Speakable("**Hello** there 😊"));
        Assert.Null(VoiceReplyService.Speakable("👍"));
    }

    [Fact]
    public void The_ai_quota_at_cap_means_text()
    {
        using var r = new VoiceRig();
        var tenant = new Tenant { Name = "T", Slug = "t" };
        r.Db.Tenants.Add(tenant);
        r.Db.SaveChanges();
        var e = new Entity { TenantId = tenant.Id, Name = "Capped", Slug = "capped", CreatedAt = DateTime.UtcNow };
        r.Db.Entities.Add(e);
        r.Db.SaveChanges();
        var thread = r.Threads.GetOrCreateThread("whatsapp", "60123000005", "Q");
        thread.EntityId = e.Id;
        r.Db.SaveChanges();
        AiQuota.SaveConfig(r.Sc, new AiQuotaConfig { Entities = new() { [e.Id.ToString()] = 1 } });
        r.Db.LlmUsage.Add(new LlmUsage { Feature = "chat_reply", Model = "gpt-4o", EntityId = e.Id, CostUsd = 500,
            CreatedAt = DateTime.UtcNow });
        r.Db.SaveChanges();
        AiQuota.Invalidate();
        try
        {
            var svc = new VoiceReplyService(r.Db, r.Sc, r.Media, new SpeechClient(r.Sc, r.Speech));
            var policy = AiBehaviourSettings.Resolve(r.Sc, e.Id);
            var (note, why) = svc.Synthesize(thread, "Hello there", policy);
            Assert.Null(note);
            Assert.Contains("quota", why, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(r.Speech.Tts);
        }
        finally { AiQuota.Invalidate(); }
    }

    [Fact]
    public void A_held_draft_never_synthesizes_until_a_person_approves_it()
    {
        using var r = new VoiceRig();
        r.Sc.SetSetting("ai_review_mode", "review_all");
        var acct = r.Account("whatsapp", Providers.Baileys, "gw-1");
        r.Contact("Held", "60123000006");

        r.Inbound.ProcessInbound("whatsapp", "60123000006", "[audio]", accountId: acct.Id, wamid: "wa.h1");
        var d = r.LastDraft();
        Assert.Equal(AiDraftStatus.Pending, d.Status);
        Assert.Equal(AiGate.VoicePlanned, AiGate.Str(d.Context, "voice", ""));
        Assert.Empty(r.Speech.Tts);
        Assert.Empty(r.Baileys.Voice);

        var outcome = r.Gate.Approve(d.Id, "admin");
        Assert.Equal(AiGateStatus.Sent, outcome.Status);
        Assert.Single(r.Baileys.Voice);
        Assert.Single(r.Speech.Tts);
    }

    [Fact]
    public void A_queued_voice_reply_is_made_at_send_time_as_one_message()
    {
        using var r = new VoiceRig();
        r.Sc.SetSetting("ai_typing", "true");
        r.Sc.SetSetting("ai_split", "always");
        r.Llm.Script = (_, _) => ToolScriptLlm.Says("First part.\n\nSecond part.");
        var acct = r.Account("whatsapp", Providers.Baileys, "gw-1");
        r.Contact("Queue", "60123000007");

        r.Inbound.ProcessInbound("whatsapp", "60123000007", "[audio]", accountId: acct.Id, wamid: "wa.q1");
        Assert.Empty(r.Speech.Tts);
        var job = r.T.Fresh().OutboundJobs.Single(j => j.Status == JobStatus.Queued);
        Assert.True(AiGate.Bool(job.Payload, "voice"));
        Assert.Empty(AiGate.StrList(job.Payload, "rest"));

        r.Clock.Advance(TimeSpan.FromMinutes(2));
        var assistant = new AssistantService(r.Db, r.Sc, new PersonaService(r.Sc), new KnowledgeService(r.Db, r.Sc), r.Llm);
        var sender = new OutboundSender(r.Db, r.Threads, r.Cloud, r.Baileys, media: r.Media);
        var queue = new QueueService(r.Db, r.Sc, r.Threads, assistant, sender,
            new AiResponder(r.Db, r.Sc, r.Composer, r.Gate, r.Clock), r.Gate, r.Clock);
        queue.Drain(10);
        var voice = Assert.Single(r.Baileys.Voice);
        Assert.Empty(r.Baileys.Texts);
        var sent = Assert.Single(r.Out("whatsapp", "60123000007"));
        Assert.Equal("First part.\n\nSecond part.", sent.Text);
        Assert.Equal("audio", sent.MediaType);
        Assert.NotNull(r.LastDraft().SentAt);
    }

    [Fact]
    public void A_customer_voice_note_is_transcribed_once_and_read_by_the_ai()
    {
        using var r = new VoiceRig();
        r.Sc.SetSetting("openai_api_key", "oa-key");
        var acct = r.Account("whatsapp", Providers.Baileys, "gw-1");
        var c = r.Contact("Talk", "60123000008");
        var thread = r.Threads.GetOrCreateThread("whatsapp", "60123000008", "Talk", acct.Id, c.Id);
        var msg = r.Threads.AppendMessage(thread, MessageDirection.In, MessageSource.Customer, "[audio]",
            externalId: "wa.t1", accountId: acct.Id);
        var rel = Task.Run(() => r.Media.SaveAsync("whatsapp", thread.Key, FakeSpeech.Ogg, ".ogg")).GetAwaiter().GetResult();
        msg.MediaType = "audio";
        msg.MediaPath = rel;
        r.Db.SaveChanges();

        var composed = Task.Run(() => r.Composer.ComposeReplyAsync(thread)).GetAwaiter().GetResult();
        Assert.False(string.IsNullOrEmpty(composed.Text));
        var stt = Assert.Single(r.Speech.Stt);
        Assert.Contains("transcriptions", stt.Path);
        Assert.Equal("oa-key", stt.Key);
        Assert.Contains("gpt-4o-mini-transcribe", stt.Body);
        Assert.Contains(r.Llm.Seen.Last().Messages,
            m => m.Role == "user" && (m.Content ?? "").Contains("[voice note] Can I book a table for two tonight?"));

        var db = r.T.Fresh();
        Assert.Equal("Can I book a table for two tonight?", db.Messages.Single(m => m.Id == msg.Id).Transcript);
        Assert.Single(db.LlmUsage.Where(u => u.Feature == VoiceTranscriber.Feature && u.Provider == "openai"));

        // stored once: the next reply doesn't transcribe again
        Task.Run(() => r.Composer.ComposeReplyAsync(thread)).GetAwaiter().GetResult();
        Assert.Single(r.Speech.Stt);
        // the summary text reads the words too
        Assert.Equal("[voice note] Can I book a table for two tonight?",
            VoiceTranscriber.Display(db.Messages.Single(m => m.Id == msg.Id)));
    }

    [Fact]
    public void Elevenlabs_scribe_transcribes_when_there_is_no_openai_key_and_nothing_without_either()
    {
        using var r = new VoiceRig();
        var speech = new SpeechClient(r.Sc, r.Speech);
        Assert.Equal("elevenlabs", speech.SttProvider);
        var res = Task.Run(() => speech.TranscribeAsync(FakeSpeech.Ogg, "v.ogg", "audio/ogg")).GetAwaiter().GetResult();
        Assert.True(res.Ok);
        Assert.Contains("/v1/speech-to-text", r.Speech.Stt.Single().Path);
        Assert.Contains("scribe_v1", r.Speech.Stt.Single().Body);

        r.Sc.SetSetting("elevenlabs_api_key", "");
        Assert.Equal("", speech.SttProvider);
    }

    [Fact]
    public void Local_whisper_is_its_own_switch_needs_no_openai_key_and_off_turns_transcription_off()
    {
        using var r = new VoiceRig();
        r.Sc.SetSetting("elevenlabs_api_key", "");
        var speech = new SpeechClient(r.Sc, r.Speech);
        Assert.Equal("", speech.SttProvider);

        // Local Whisper: the compose service on the internal network, no key at all
        r.Sc.SetSetting("transcription_provider", "local");
        Assert.Equal("local", speech.SttProvider);
        var res = Task.Run(() => speech.TranscribeAsync(FakeSpeech.Ogg, "v.ogg", "audio/ogg")).GetAwaiter().GetResult();
        Assert.True(res.Ok);
        var call = r.Speech.Stt.Single();
        Assert.Equal("http://whisper:8000", call.Origin);
        Assert.Equal("/v1/audio/transcriptions", call.Path);
        Assert.Contains(SpeechClient.LocalSttModelDefault, call.Body);
        Assert.Equal("", call.Key);
        // switching Whisper on never makes OpenAI a chat provider
        Assert.DoesNotContain(new OpenAiLlmClient(new SingleFactory(), r.Sc).Configured(), p => p.Name == "openai");

        // its own address, model and optional key
        r.Sc.SetSetting("transcription_base_url", "http://10.0.0.5:9000/v1/");
        r.Sc.SetSetting("transcription_model", "Systran/faster-whisper-medium");
        r.Sc.SetSetting("transcription_api_key", "local-secret");
        Task.Run(() => speech.TranscribeAsync(FakeSpeech.Ogg, "v.ogg", "audio/ogg")).GetAwaiter().GetResult();
        var second = r.Speech.Stt.Last();
        Assert.Equal("http://10.0.0.5:9000", second.Origin);
        Assert.Contains("faster-whisper-medium", second.Body);
        Assert.Equal("local-secret", second.Key);

        // a pin wins over keys; off turns it off even with an OpenAI key
        r.Sc.SetSetting("openai_api_key", "sk-real-openai");
        Assert.Equal("local", speech.SttProvider);
        r.Sc.SetSetting("transcription_provider", "off");
        Assert.Equal("", speech.SttProvider);
        r.Sc.SetSetting("transcription_provider", "");
        Assert.Equal("openai", speech.SttProvider);
    }

    private sealed class SingleFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    [Fact]
    public async Task Http_gateway_sends_a_voice_note_as_ptt_ogg()
    {
        using var t = new TestDb();
        var sc = new ScopingService(t.Db);
        sc.SetSetting("baileys_url", "http://gw.test/");
        sc.SetSetting("baileys_secret", "s3cret");
        var services = new ServiceCollection();
        services.AddScoped(_ => new ScopingService(t.Db));
        await using var sp = services.BuildServiceProvider();
        var stub = new GatewayStub();
        var gw = new HttpBaileysGateway(new FakeHttpFactory(stub), sp.GetRequiredService<IServiceScopeFactory>());

        var r = await gw.SendVoiceNoteAsync("gw1", "60123", FakeSpeech.Ogg);
        Assert.True(r.Ok);
        var seen = Assert.Single(stub.Requests);
        Assert.EndsWith("/accounts/gw1/send-media", seen.Url);
        using var body = JsonDocument.Parse(seen.Body);
        Assert.True(body.RootElement.GetProperty("ptt").GetBoolean());
        Assert.Equal("audio", body.RootElement.GetProperty("media_type").GetString());
        Assert.Equal("audio/ogg", body.RootElement.GetProperty("mime").GetString());
    }

    [Fact]
    public void Voice_settings_are_in_the_human_group_and_entity_scoped()
    {
        var f = AiBehaviourSettings.ByKey["ai_voice_replies"];
        Assert.Equal("human", f.Group);
        Assert.Equal("off", f.Default);
        Assert.Equal(new[] { "off", "mirror", "always" }, f.Options);
        Assert.Equal("human", AiBehaviourSettings.ByKey["ai_voice_id"].Group);
        Assert.True(ScopingService.IsEntityScoped("ai_voice_replies"));
        Assert.True(ScopingService.IsEntityScoped("ai_voice_id"));
        Assert.NotNull(AiBehaviourSettings.Normalize("ai_voice_replies", "sometimes", out _));
    }
}
