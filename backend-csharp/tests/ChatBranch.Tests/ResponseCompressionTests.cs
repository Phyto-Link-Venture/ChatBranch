using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// Brotli / gzip on the API's JSON (Program.cs, AddResponseCompression): a client
/// that asks gets a compressed body that decodes to the same JSON, one that doesn't
/// ask gets it plain, and the SSE change stream is never compressed (it must flush
/// event by event through the proxy).
/// </summary>
public class ResponseCompressionTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public ResponseCompressionTests(ApiFactory factory) => _factory = factory;

    private async Task<HttpClient> Authed()
    {
        var c = _factory.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "admin123" });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    [Theory]
    [InlineData("br")]
    [InlineData("gzip")]
    public async Task Json_is_compressed_when_asked_and_decodes_to_the_same_body(string encoding)
    {
        var c = await Authed();
        var plain = await c.GetAsync("/api/inbox/threads?limit=50");
        plain.EnsureSuccessStatusCode();
        Assert.Empty(plain.Content.Headers.ContentEncoding);
        var plainJson = JsonDocument.Parse(await plain.Content.ReadAsStringAsync()).RootElement;

        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/inbox/threads?limit=50");
        req.Headers.AcceptEncoding.ParseAdd(encoding);
        var packed = await c.SendAsync(req);
        packed.EnsureSuccessStatusCode();
        Assert.Equal(encoding, Assert.Single(packed.Content.Headers.ContentEncoding));
        Assert.Contains("Accept-Encoding", packed.Headers.Vary);

        await using var raw = await packed.Content.ReadAsStreamAsync();
        await using Stream unpacked = encoding == "br"
            ? new BrotliStream(raw, CompressionMode.Decompress)
            : new GZipStream(raw, CompressionMode.Decompress);
        using var reader = new StreamReader(unpacked);
        var json = JsonDocument.Parse(await reader.ReadToEndAsync()).RootElement;
        Assert.Equal(plainJson.GetProperty("count").GetInt32(), json.GetProperty("count").GetInt32());
        Assert.Equal(plainJson.GetProperty("threads").GetArrayLength(), json.GetProperty("threads").GetArrayLength());
    }

    [Fact]
    public async Task The_sse_change_stream_is_never_compressed()
    {
        var c = await Authed();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/whatsapp/stream");
        req.Headers.AcceptEncoding.ParseAdd("br, gzip");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var res = await c.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        res.EnsureSuccessStatusCode();
        Assert.Equal("text/event-stream", res.Content.Headers.ContentType?.MediaType);
        Assert.Empty(res.Content.Headers.ContentEncoding);
        // the first event arrives as plain text, readable as it is
        await using var body = await res.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(body);
        var first = await reader.ReadLineAsync(cts.Token);
        Assert.StartsWith("event: change", first);
    }
}
