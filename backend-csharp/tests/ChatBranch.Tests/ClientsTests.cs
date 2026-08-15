using ChatBranch.Data;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Xunit;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Tests;

/// <summary>Real-client wiring: configured-account detection and the send-path decision.</summary>
public class ClientsTests
{
    [Fact]
    public void WhatsApp_client_configured_detection()
    {
        var client = new WhatsAppCloudClient(new StubHttpFactory());
        Assert.False(client.IsConfigured(new ChannelAccount { Provider = "cloud" }));
        Assert.False(client.IsConfigured(new ChannelAccount
            { Provider = "cloud", AccessToken = "tok" })); // missing external_id
        Assert.True(client.IsConfigured(new ChannelAccount
            { Provider = "cloud", AccessToken = "tok", ExternalId = "12345" }));
        Assert.False(client.IsConfigured(new ChannelAccount
            { Provider = "baileys", AccessToken = "tok", ExternalId = "12345" })); // not cloud
    }

    [Fact]
    public void Outbound_sender_falls_back_to_sandbox_without_a_configured_account()
    {
        using var t = new TestDb();
        var scoping = new ScopingService(t.Db);
        var threads = new ThreadStore(t.Db, scoping);
        var sender = new OutboundSender(t.Db, threads, new StubWhatsApp(), new ChatBranch.Data.Sidecars.SandboxBaileysGateway());

        var thread = threads.GetOrCreateThread("whatsapp", "60123000000");
        var msg = sender.Send(thread, "hi", MessageSource.Agent);
        Assert.Equal("sandbox", msg.Status);
        Assert.Equal("out", msg.Direction);
    }
}

internal class StubHttpFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}
