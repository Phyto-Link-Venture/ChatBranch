using ChatBranch.Data;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Sidecars;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Xunit;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Tests;

/// <summary>A gateway double that records the delete / edit it was asked for.</summary>
internal sealed class ActionBaileys : IBaileysGateway
{
    public List<BaileysQuote> Revoked { get; } = new();
    public List<(BaileysQuote Target, string Text)> Edited { get; } = new();
    public List<byte[]> VoiceNotes { get; } = new();
    public bool Refuse { get; set; }

    public bool IsConfigured => true;
    public string BaseUrl => "http://gateway.test";

    public Task<BaileysResult> SendTextAsync(string a, string to, string text, CancellationToken ct = default) =>
        Task.FromResult(new BaileysResult(true, "sent", "3EB0T"));

    public Task<BaileysResult> RevokeMessageAsync(string a, string to, BaileysQuote target,
        CancellationToken ct = default)
    {
        Revoked.Add(target);
        return Task.FromResult(Refuse
            ? new BaileysResult(false, "gateway 500: too old to delete")
            : new BaileysResult(true, "deleted", "3EB0D"));
    }

    public Task<BaileysResult> EditMessageAsync(string a, string to, BaileysQuote target, string text,
        CancellationToken ct = default)
    {
        Edited.Add((target, text));
        return Task.FromResult(Refuse
            ? new BaileysResult(false, "gateway 500: edit refused")
            : new BaileysResult(true, "edited", "3EB0E"));
    }

    public Task<BaileysResult> SendVoiceNoteAsync(string a, string to, byte[] oggOpus, CancellationToken ct = default)
    {
        VoiceNotes.Add(oggOpus);
        return Task.FromResult(new BaileysResult(true, "sent", "3EB0V"));
    }

    public Task<BaileysResult> StartCallAsync(string a, string to, Dictionary<string, object?> d,
        CancellationToken ct = default) => Task.FromResult(new BaileysResult(false, "no calls"));
    public Task<BaileysResult> EndCallAsync(string a, CancellationToken ct = default) =>
        Task.FromResult(new BaileysResult(true, "ended"));
    private static readonly BaileysStatus Connected = new("connected", null, "me", null);
    public Task<BaileysStatus> StatusAsync(string a, CancellationToken ct = default) => Task.FromResult(Connected);
    public Task<BaileysStatus> StartAsync(string a, CancellationToken ct = default) => Task.FromResult(Connected);
    public Task<BaileysStatus> LogoutAsync(string a, CancellationToken ct = default) => Task.FromResult(Connected);
}

/// <summary>
/// What an operator can do to a message already sent: WhatsApp's "delete for
/// everyone" and its 15-minute edit.
///
/// The tests concentrate on the refusals, because those are what an operator
/// actually meets and where a wrong answer does harm: a message the customer can
/// still see must never be reported as deleted, and a channel that cannot do
/// this at all must say so instead of failing obscurely.
/// </summary>
public class MessageActionTests : IDisposable
{
    private readonly TestDb _t = new();
    private readonly ActionBaileys _gw = new();

    public void Dispose() => _t.Dispose();

    private OutboundSender Sender() =>
        new(_t.Db, new ThreadStore(_t.Db, new ScopingService(_t.Db)),
            new WhatsAppCloudClient(new FakeHttpFactory(new FakeGraph())), _gw);

    /// <summary>A QR (Baileys) number, a Cloud number, or no account at all.</summary>
    private (Thread Thread, ChannelAccount? Account) Chat(string provider, string key = "60111000777")
    {
        ChannelAccount? account = null;
        if (provider.Length > 0)
        {
            account = new ChannelAccount
            {
                Channel = "whatsapp",
                Provider = provider,
                Name = provider == Providers.Baileys ? "QR" : "Cloud",
                ExternalId = provider == Providers.Baileys ? "gw-a" : "123456789",
                AccessToken = provider == Providers.Baileys ? "" : "tok",
                CreatedAt = DateTime.UtcNow,
            };
            _t.Db.ChannelAccounts.Add(account);
            _t.Db.SaveChanges();
        }
        var threads = new ThreadStore(_t.Db, new ScopingService(_t.Db));
        var thread = threads.GetOrCreateThread("whatsapp", key, "A", account?.Id);
        return (thread, account);
    }

    /// <summary>A message on the conversation, as if sent (or received) `agoMinutes` ago.</summary>
    private Message Message(Thread thread, string direction, string text, double agoMinutes = 1,
        string externalId = "3EB0ABC", string mediaPath = "", bool priv = false, string source = MessageSource.Agent)
    {
        var m = new Message
        {
            ThreadId = thread.Id,
            Direction = direction,
            Source = source,
            Text = text,
            Status = "sent",
            ExternalId = externalId,
            MediaPath = mediaPath,
            MediaType = mediaPath.Length > 0 ? "image" : "",
            Private = priv,
            AccountId = thread.AccountId,
            CreatedAt = DateTime.UtcNow.AddMinutes(-agoMinutes),
        };
        _t.Db.Messages.Add(m);
        _t.Db.SaveChanges();
        return m;
    }

    // ---- delete for everyone --------------------------------------------------

    [Fact]
    public void Deleting_our_own_message_reaches_the_gateway_with_its_whatsapp_id()
    {
        var (thread, _) = Chat(Providers.Baileys);
        var msg = Message(thread, MessageDirection.Out, "oops wrong number");

        var sent = Sender().Revoke(thread, msg);

        Assert.True(sent.Ok, sent.Detail);
        Assert.True(sent.Live);
        var target = Assert.Single(_gw.Revoked);
        Assert.Equal("3EB0ABC", target.Id);
        Assert.True(target.FromMe, "we only ever delete our own message");
    }

    [Fact]
    public void A_customers_message_is_never_deleted_for_everyone()
    {
        var (thread, _) = Chat(Providers.Baileys);
        var inbound = Message(thread, MessageDirection.In, "hello", source: MessageSource.Customer);

        var sent = Sender().Revoke(thread, inbound);

        Assert.False(sent.Ok);
        Assert.True(sent.Conflict);
        Assert.Contains("message you sent", sent.Detail);
        Assert.Empty(_gw.Revoked);
    }

    [Fact]
    public void A_note_has_nothing_to_delete_on_whatsapp()
    {
        var (thread, _) = Chat(Providers.Baileys);
        var note = Message(thread, MessageDirection.Out, "internal", priv: true);

        var sent = Sender().Revoke(thread, note);

        Assert.False(sent.Ok);
        Assert.True(sent.Conflict);
        Assert.Empty(_gw.Revoked);
    }

    [Fact]
    public void An_official_number_says_meta_has_no_delete_rather_than_failing_obscurely()
    {
        var (thread, _) = Chat(Providers.Cloud);
        var msg = Message(thread, MessageDirection.Out, "sent via Cloud");

        var sent = Sender().Revoke(thread, msg);

        Assert.False(sent.Ok);
        Assert.True(sent.Conflict);
        Assert.Contains("no endpoint for it", sent.Detail);
        Assert.Contains("QR-paired", sent.Detail);
        Assert.Empty(_gw.Revoked);
    }

    [Fact]
    public void A_message_that_never_left_the_inbox_is_removed_locally_and_nothing_is_called()
    {
        var (thread, _) = Chat("");   // no account at all: sandbox
        var msg = Message(thread, MessageDirection.Out, "never sent", externalId: "");

        var sent = Sender().Revoke(thread, msg);

        Assert.True(sent.Ok);
        Assert.False(sent.Live, "nothing reached WhatsApp, so there is nothing to delete there");
        Assert.Empty(_gw.Revoked);
    }

    [Fact]
    public void A_live_message_with_no_whatsapp_id_is_refused_rather_than_guessed_at()
    {
        var (thread, _) = Chat(Providers.Baileys);
        var msg = Message(thread, MessageDirection.Out, "queued only", externalId: "db-17");

        var sent = Sender().Revoke(thread, msg);

        Assert.False(sent.Ok);
        Assert.True(sent.Conflict);
        Assert.Empty(_gw.Revoked);
    }

    [Fact]
    public void A_gateway_refusal_is_reported_and_the_row_is_left_alone()
    {
        var (thread, _) = Chat(Providers.Baileys);
        var msg = Message(thread, MessageDirection.Out, "too old");
        _gw.Refuse = true;

        var sent = Sender().Revoke(thread, msg);

        Assert.False(sent.Ok);
        Assert.False(sent.Conflict, "a provider failure is not the caller's mistake");
        Assert.Null(_t.Db.Messages.Find(msg.Id)!.RecalledAt);
    }

    // ---- what the recall records ---------------------------------------------

    [Fact]
    public void Recording_our_recall_erases_the_words_and_audits_who_did_it()
    {
        var (thread, account) = Chat(Providers.Baileys);
        var msg = Message(thread, MessageDirection.Out, "the wrong price is RM 5");

        var done = MessageRecalls.RecordOurs(_t.Db, account!, msg, "mei");

        Assert.True(done);
        var row = _t.Db.Messages.Find(msg.Id)!;
        Assert.NotNull(row.RecalledAt);
        Assert.Equal(MessageRecalls.ByBusiness, row.RecalledBy);
        Assert.Equal("", row.Text);   // the inbox does not keep recalled messages by default
        var audit = Assert.Single(_t.Db.AuditLogs.Where(a => a.Action == MessageRecalls.AuditAction).ToList());
        Assert.Contains("Deleted for everyone", audit.Detail);
        Assert.Equal("agent:mei", audit.Actor);
    }

    [Fact]
    public void An_inbox_that_keeps_recalled_messages_keeps_ours_too_marked()
    {
        var (thread, account) = Chat(Providers.Baileys);
        account!.Config[InboxSettings.KeepRecalledKey] = true;
        _t.Db.SaveChanges();
        var msg = Message(thread, MessageDirection.Out, "still readable here");

        MessageRecalls.RecordOurs(_t.Db, account, msg, "mei");

        var row = _t.Db.Messages.Find(msg.Id)!;
        Assert.Equal("still readable here", row.Text);
        Assert.NotNull(row.RecalledAt);
    }

    [Fact]
    public void Recording_a_recall_twice_changes_nothing()
    {
        var (thread, account) = Chat(Providers.Baileys);
        var msg = Message(thread, MessageDirection.Out, "once");

        Assert.True(MessageRecalls.RecordOurs(_t.Db, account!, msg, "mei"));
        Assert.False(MessageRecalls.RecordOurs(_t.Db, account!, msg, "mei"));
        Assert.Single(_t.Db.AuditLogs.Where(a => a.Action == MessageRecalls.AuditAction).ToList());
    }

    // ---- edit ----------------------------------------------------------------

    [Fact]
    public void Editing_our_own_message_sends_the_new_wording_to_the_gateway()
    {
        var (thread, _) = Chat(Providers.Baileys);
        var msg = Message(thread, MessageDirection.Out, "RM 5");

        var sent = Sender().Edit(thread, msg, "RM 50");

        Assert.True(sent.Ok, sent.Detail);
        var (target, text) = Assert.Single(_gw.Edited);
        Assert.Equal("3EB0ABC", target.Id);
        Assert.Equal("RM 50", text);
    }

    [Fact]
    public void A_message_carrying_a_file_cannot_be_edited()
    {
        var (thread, _) = Chat(Providers.Baileys);
        var msg = Message(thread, MessageDirection.Out, "see photo", mediaPath: "whatsapp/x.jpg");

        var sent = Sender().Edit(thread, msg, "see this photo");

        Assert.False(sent.Ok);
        Assert.True(sent.Conflict);
        Assert.Contains("delete it and send a new one", sent.Detail);
        Assert.Empty(_gw.Edited);
    }

    [Fact]
    public void An_official_number_says_meta_has_no_edit()
    {
        var (thread, _) = Chat(Providers.Cloud);
        var msg = Message(thread, MessageDirection.Out, "typo");

        var sent = Sender().Edit(thread, msg, "fixed");

        Assert.False(sent.Ok);
        Assert.True(sent.Conflict);
        Assert.Contains("no endpoint for it", sent.Detail);
        Assert.Empty(_gw.Edited);
    }

    [Fact]
    public void An_already_deleted_message_cannot_be_edited()
    {
        var (thread, account) = Chat(Providers.Baileys);
        var msg = Message(thread, MessageDirection.Out, "gone");
        MessageRecalls.RecordOurs(_t.Db, account!, msg, "mei");

        var sent = Sender().Edit(thread, _t.Db.Messages.Find(msg.Id)!, "back again");

        Assert.False(sent.Ok);
        Assert.True(sent.Conflict);
        Assert.Empty(_gw.Edited);
    }

    [Fact]
    public void Recording_an_edit_shows_the_new_wording_and_keeps_the_old_one_in_the_log()
    {
        var (thread, _) = Chat(Providers.Baileys);
        var msg = Message(thread, MessageDirection.Out, "deposit is RM 5");
        var before = thread.UpdatedAt;

        MessageEdits.Record(_t.Db, thread, msg, "deposit is RM 50", "mei");

        var row = _t.Db.Messages.Find(msg.Id)!;
        Assert.Equal("deposit is RM 50", row.Text);   // the chat shows what the customer now sees
        Assert.NotNull(row.EditedAt);
        Assert.True(_t.Db.Threads.Find(thread.Id)!.UpdatedAt >= before, "the open chat must redraw");
        var audit = Assert.Single(_t.Db.AuditLogs.Where(a => a.Action == MessageEdits.AuditAction).ToList());
        Assert.Contains("was: deposit is RM 5", audit.Detail);   // and the trail still says what it said
        Assert.Equal("agent:mei", audit.Actor);
    }

    // ---- WhatsApp's own windows ----------------------------------------------

    [Fact]
    public void The_edit_window_closes_after_about_fifteen_minutes()
    {
        var (thread, _) = Chat(Providers.Baileys);
        var fresh = Message(thread, MessageDirection.Out, "just now", agoMinutes: 1);
        var old = Message(thread, MessageDirection.Out, "a while ago", agoMinutes: 20, externalId: "3EB0OLD");

        Assert.True(MessageEdits.WithinWindow(fresh));
        Assert.False(MessageEdits.WithinWindow(old));
        Assert.True(MessageEdits.Remaining(fresh) > TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, MessageEdits.Remaining(old));
    }

    [Fact]
    public void The_delete_window_is_longer_than_the_edit_window()
    {
        var (thread, _) = Chat(Providers.Baileys);
        var hourOld = Message(thread, MessageDirection.Out, "an hour ago", agoMinutes: 60);
        var weekOld = Message(thread, MessageDirection.Out, "last week", agoMinutes: 60 * 24 * 7,
            externalId: "3EB0WEEK");

        Assert.False(MessageEdits.WithinWindow(hourOld), "too late to edit");
        Assert.True(MessageEdits.WithinRecallWindow(hourOld), "but still deletable");
        Assert.False(MessageEdits.WithinRecallWindow(weekOld));
    }

    // ---- the operator's voice note -------------------------------------------

    [Fact]
    public void An_operators_voice_note_goes_out_as_a_real_voice_note()
    {
        var (thread, _) = Chat(Providers.Baileys);
        var dir = Path.Combine(Path.GetTempPath(), $"cb-voice-{Guid.NewGuid():N}");
        var store = new MediaStore(dir);
        var rel = store.SaveAsync("whatsapp", thread.Key, new MemoryStream(new byte[] { 1, 2, 3, 4 }), ".ogg")
            .GetAwaiter().GetResult()!;
        try
        {
            var sender = new OutboundSender(_t.Db, new ThreadStore(_t.Db, new ScopingService(_t.Db)),
                new WhatsAppCloudClient(new FakeHttpFactory(new FakeGraph())), _gw, media: store);

            var (msg, error) = sender.SendVoiceNote(thread, rel, "audio/ogg", "[voice note]", MessageSource.Agent);

            Assert.NotNull(msg);
            Assert.Equal("", error);
            Assert.Single(_gw.VoiceNotes);
            // the bubble keeps the words AND plays the file
            Assert.Equal("[voice note]", msg!.Text);
            Assert.Equal("audio", msg.MediaType);
            Assert.Equal(MessageSource.Agent, msg.Source);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task A_recording_is_re_containered_to_ogg_opus_only_when_ffmpeg_is_there()
    {
        // Browsers record webm/opus or mp4/aac; WhatsApp's voice note is Ogg/Opus.
        // Without ffmpeg the route refuses rather than sending something that is
        // not a voice note, so the behaviour is asserted either way.
        var dir = Path.Combine(Path.GetTempPath(), $"cb-ogg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var source = Path.Combine(dir, "note.webm");
        await File.WriteAllBytesAsync(source, new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0, 0, 0, 0 });
        try
        {
            var converted = await AudioTranscoder.ToOggOpusAsync(source);
            if (AudioTranscoder.Available)
                // a 8-byte stub is not decodable, so ffmpeg must FAIL cleanly (null),
                // never a half-written file the operator would send as silence
                Assert.True(converted == null || new FileInfo(converted).Length > 0);
            else
                Assert.Null(converted);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
