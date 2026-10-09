using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// Messenger / Instagram "typing…": Send API sender actions (mark_seen, then
/// typing_on) from the page that carries the conversation, only inside Meta's
/// standard 24h window, never throwing into the send path.
/// </summary>
public class MetaTypingTests : IDisposable
{
    private readonly TestDb _t = new();
    private readonly FakeGraph _graph = new();
    private readonly ScopingService _sc;
    private readonly ThreadStore _threads;
    private readonly ManualClock _clock = new(DateTimeOffset.UtcNow);

    public MetaTypingTests()
    {
        _sc = new ScopingService(_t.Db);
        _threads = new ThreadStore(_t.Db, _sc);
        _graph.Json("POST", "/me/messages", new { recipient_id = "psid-1" });
    }

    public void Dispose() => _t.Dispose();

    private ChannelAccount Page(string channel = "facebook", string token = "page-token")
    {
        var a = new ChannelAccount
        {
            Channel = channel, Name = "Page", ExternalId = "PAGE-1", AccessToken = token, CreatedAt = DateTime.UtcNow,
        };
        _t.Db.ChannelAccounts.Add(a);
        _t.Db.SaveChanges();
        return a;
    }

    private MetaTypingIndicator Indicator() =>
        new(new MetaMessagingClient(new FakeHttpFactory(_graph)), _t.Db, _clock);

    [Fact]
    public async Task Inside_the_window_marks_seen_then_types_from_the_page()
    {
        var page = Page();
        var thread = _threads.GetOrCreateThread("facebook", "psid-1", accountId: page.Id);
        _threads.AppendMessage(thread, MessageDirection.In, MessageSource.Customer, "hi", status: "received");

        Assert.True(await Indicator().StartAsync(thread, page));
        var actions = _graph.Requests.Where(r => r.Url.Contains("/me/messages"))
            .Select(r => JsonSerializer.Deserialize<JsonElement>(r.Body)).ToList();
        Assert.Equal(new[] { "mark_seen", "typing_on" },
            actions.Select(a => a.GetProperty("sender_action").GetString()));
        Assert.All(actions, a =>
        {
            Assert.Equal("psid-1", a.GetProperty("recipient").GetProperty("id").GetString());
            Assert.False(a.TryGetProperty("messaging_type", out _)); // a sender action carries no message envelope
        });
        Assert.All(_graph.Requests, r => Assert.Contains("page-token", r.Auth));
    }

    [Fact]
    public async Task Outside_the_window_or_without_a_token_nothing_is_sent()
    {
        var page = Page("instagram");
        var thread = _threads.GetOrCreateThread("instagram", "igsid-1", accountId: page.Id);
        // never wrote: closed
        Assert.False(await Indicator().StartAsync(thread, page));
        _threads.AppendMessage(thread, MessageDirection.In, MessageSource.Customer, "hello", status: "received");
        // 25 h later: only a human-agent reply may go out, and no sender action
        _clock.Advance(TimeSpan.FromHours(25));
        Assert.False(await Indicator().StartAsync(thread, page));
        // a sandbox page (no token)
        var sandbox = Page("instagram", token: "");
        Assert.False(await Indicator().StartAsync(thread, sandbox));
        Assert.Empty(_graph.Requests);
    }

    [Fact]
    public async Task A_refused_action_returns_false_and_the_dispatcher_routes_social_threads()
    {
        var failing = new FakeGraph();
        failing.Json("POST", "/me/messages", new { error = new { code = 190, message = "token expired" } },
            System.Net.HttpStatusCode.BadRequest);
        var page = Page();
        var thread = _threads.GetOrCreateThread("facebook", "psid-2", accountId: page.Id);
        _threads.AppendMessage(thread, MessageDirection.In, MessageSource.Customer, "hi", status: "received");
        var meta = new MetaTypingIndicator(new MetaMessagingClient(new FakeHttpFactory(failing)), _t.Db, _clock);
        Assert.False(await meta.StartAsync(thread, page));
        Assert.Equal("unknown", _t.Db.ChannelAccounts.Find(page.Id)!.HealthStatus); // presence never moves health

        // the dispatcher: Messenger/Instagram → the Meta indicator, WhatsApp unchanged
        var whatsapp = new RecordingTyping();
        var recordingMeta = new RecordingTyping();
        var dispatcher = new ChannelTypingIndicator(whatsapp, new NoTypingIndicator(), recordingMeta);
        dispatcher.Start(thread, page);
        dispatcher.Start(_threads.GetOrCreateThread("whatsapp", "60111"), page);
        Assert.Equal((1, 1), (recordingMeta.Count, whatsapp.Count));
    }
}
